using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExpedition;

/// <summary>
/// Heading toward a listed item lying off screen, for AutolootSettings.HeadTowardListedLootOffScreen: along a route
/// round walls over the pathfinding grid (GroundRoutes), rolling and sprinting down a straight stretch at least a roll
/// long, steered by the cursor, and walking with the movement keys otherwise, until the item's label is on screen -
/// then the ordinary pickup takes it. For at most ChaseMostMs.
/// </summary>
internal static partial class Autoloot
{
    /// <summary>The listed item being headed toward, its id and name, and when the chase began.</summary>
    private static Entity _chaseTarget;

    private static uint _chaseId;
    private static string _chaseName = "";
    private static DateTime _chaseStartedAt;

    /// <summary>The route in hand, start first, and when it was worked out. See ChaseRerouteEvery.</summary>
    private static List<Vector2> _chaseRoute;

    private static DateTime _chaseRoutedAt;

    /// <summary>
    /// The longest a chase runs, and its key block: 3s, the most ExileInput2 blocks keys for (KeyWatch.MostBlockMs),
    /// since a chase from 169 grid away took 1703ms of the 2s it had (dump 2026-10-25 02:39:00). A pickup's own block
    /// stays KeyBlockMs.
    /// </summary>
    private const int ChaseMostMs = 3000;

    /// <summary>How often the route is worked out again as the character moves. Chosen.</summary>
    private static readonly TimeSpan ChaseRerouteEvery = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Items not headed toward again this area: a chase that ran out of time, or found no route or no input block. One
    /// ended by the label coming on screen is the pickup's to take; one whose item went has nothing left.
    /// </summary>
    private static readonly HashSet<uint> ChasedItemIds = new();

    /// <summary>The movement keys the chase holds to walk, and whether it holds the dodge roll key.</summary>
    private static readonly HashSet<Keys> ChaseMovementKeysHeld = new();

    private static bool _chaseDodgeKeyHeld;

    /// <summary>When the chase pressed the dodge roll key, for its stall. See StepOffScreenChase.</summary>
    private static DateTime? _chaseRollPressedAt;

    /// <summary>
    /// The chase in hand as it went, for the dump: each change of animation, action, movement signs and the keys it
    /// holds, timed from its start. Kept to see how its roll sets off. See NoteChaseTimeline.
    /// </summary>
    private static readonly List<string> ChaseTimeline = new();

    private static string _chaseLastSaid = "";

    /// <summary>The ground point the chase last steered for. See StepOffScreenChase.</summary>
    private static Vector2 _chaseLastSteer;

    /// <summary>
    /// When the chase last sent the cursor to aim a roll, null once a roll has gone with it. A roll waits for the game's
    /// cursor to show that aim for as long as a click has to show a walk (ClickFailsAfterMs). See StepOffScreenChase.
    /// </summary>
    private static DateTime? _chaseAimSentAt;

    private static void NoteChaseTimeline(GameController gc)
    {
        var said = Placement.MovementSignsSaid(gc) + (_chaseDodgeKeyHeld ? ", roll key held" : "") +
                   (ChaseMovementKeysHeld.Count == 0 ? "" : $", walking with {string.Join("+", ChaseMovementKeysHeld.Order())}");

        if (said == _chaseLastSaid)
            return;

        _chaseLastSaid = said;
        ChaseTimeline.Add($"{(DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds:0}ms {said}");
    }

    /// <summary>Each chase, how it went, newest last, for the dump.</summary>
    private static readonly List<string> ChasesSaid = new();

    /// <summary>How many ChasesSaid are kept. Chosen.</summary>
    private const int ChasesKept = 10;

    /// <summary>
    /// Starts heading toward the nearest listed item off screen, when the setting is on, nothing on screen is to be
    /// picked up and the player's input may be blocked: the nearest by straight distance among the listed items in the
    /// entity list beyond pickup reach whose label cannot be clicked (LabelClickable, the test the chase ends on), not
    /// tried, not headed toward before and not held for another player. True when a chase began. What each look found is
    /// in ChaseLookSaid.
    ///
    /// **The pickup's own test of clickable, not where the label or the item lies.** By the item's ground position, an
    /// orb whose label the look counted off screen was never headed toward (dump 2026-10-25 02:32:12). By the label's
    /// centre on screen, an orb whose label was there but nowhere clear to click was neither picked up nor headed toward
    /// for 10.6s, until the player walked near (dump 02:57:45).
    /// </summary>
    private static bool StartOffScreenChase(GameController gc, AutoExpeditionSettings settings)
    {
        var autoloot = settings.Automation.Autoloot;

        if (!autoloot.HeadTowardListedLootOffScreen.Value)
            return false;

        var ranks = RanksOf(autoloot.InstantLootItems);

        if (ranks.Count == 0)
            return false;

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (player == Vector2.Zero)
            return false;

        var covered = Panels.Covered(gc);

        var items = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.WorldItem, out var of) ? of.ToList() : null, null) ?? [];

        (Entity Item, uint Id, string Name, float Away)? nearest = null;
        int offScreen = 0, onScreen = 0, skipped = 0, behindPanel = 0;

        foreach (var item in items)
        {
            var id = Safe.Read(item, static e => e.Id, 0u);
            var (rank, name) = RankOfItem(gc, item, ranks);

            if (rank < 0)
                continue;

            if (id == 0u || TriedItemIds.Contains(id) || ChasedItemIds.Contains(id) || AllocatedToSomeoneElse(gc, item))
            {
                skipped++;
                continue;
            }

            var away = Vector2.Distance(player, Safe.Read(item, static e => e.GridPos, Vector2.Zero));

            // In reach, an item not clickable is under other labels or a panel, which a chase does not help. See
            // StepUnderLabels.
            if (away <= PickupReachGrid || LabelClickable(gc, covered, item, id))
            {
                onScreen++;
                continue;
            }

            // Behind a panel the player has open - the inventory, a stash - the character is not moved under them.
            if (LabelBehindPanel(gc, covered, item))
            {
                behindPanel++;
                continue;
            }

            offScreen++;

            if (nearest is not { } so || away < so.Away)
                nearest = (item, id, name, away);
        }

        ChaseLookSaid = $"{DateTime.Now:HH:mm:ss.fff} {items.Count} item(s) on the ground: {offScreen} listed with no label clear to " +
                        $"click and not yet tried or headed toward, {onScreen} clickable or in reach, {behindPanel} behind an open panel, " +
                        $"{skipped} tried, headed toward or another player's";

        if (nearest is not { } chosen)
            return false;

        var blockInput = autoloot.BlockInputForInstantLoot.Value && (!autoloot.BlockInputOnlyWithParty.Value || PartyInArea(gc));

        if (!blockInput)
        {
            ChasedItemIds.Add(chosen.Id);
            SayChase($"{chosen.Name}, {chosen.Away:0} grid away: not headed toward - input not blocked for instant loot");
            return false;
        }

        var grid = Safe.Read(gc, static g => g.IngameState.Data.RawPathfindingData, null);
        var to = Safe.Read(chosen.Item, static e => e.GridPos, Vector2.Zero);
        var route = GroundRoutes.Route(grid, player, to);

        if (route == null)
        {
            ChasedItemIds.Add(chosen.Id);
            SayChase($"{chosen.Name}, {chosen.Away:0} grid away: not headed toward - no route round the walls");
            return false;
        }

        if (!(_input.TakeWithPriority(InstantLootPriority) || _input.Take()))
            return false;

        _restoreHeldKeys = autoloot.RestoreHeldKeysAfterInstantLoot.Value;
        _cancelKey = autoloot.ActionKeyEndsInputBlock.Value ? Safe.Read(() => settings.ActionHotkey.Value.Key, Keys.None) : Keys.None;
        _movementKeys = (Safe.Read(() => autoloot.MoveUpKey.Value.Key, Keys.W), Safe.Read(() => autoloot.MoveLeftKey.Value.Key, Keys.A),
            Safe.Read(() => autoloot.MoveDownKey.Value.Key, Keys.S), Safe.Read(() => autoloot.MoveRightKey.Value.Key, Keys.D));
        NoteSteeringMoveTimes(autoloot);
        _sprintAfterRoll = autoloot.SprintAfterInstantLootDodgeRoll.Value;
        _dodgeRollKey = autoloot.DodgeRollToOutOfRangeLootWhenFaster.Value
            ? Safe.Read(() => autoloot.DodgeRollKey.Value.Key, Keys.None)
            : Keys.None;
        _mouseBlockMs = Math.Clamp(autoloot.MouseBlockMs.Value, 125, 1000);
        _inputBlocked = true;
        _input.IgnorePlayerMouse(_mouseBlockMs, true);
        _keysBlocked = _input.BlockPlayerKeys(ChaseMostMs, _restoreHeldKeys, _cancelKey);
        _input.SetShortestMove(autoloot.InstantLootShortestMoveMs.Value);
        _input.SetMoveTimePerRootPixel(autoloot.InstantLootMoveTimePerRootPixelMs.Value);

        _chaseTarget = chosen.Item;
        _chaseId = chosen.Id;
        _chaseName = chosen.Name;
        _itemName = chosen.Name;
        _chaseStartedAt = DateTime.UtcNow;
        _chaseRoute = route;
        _chaseRoutedAt = DateTime.UtcNow;
        _chaseRollPressedAt = null;
        _chaseAimSentAt = null;
        _phase = Phase.Chasing;

        ChaseTimeline.Clear();
        _chaseLastSaid = "";
        _chaseLastSteer = Vector2.Zero;
        var routeLength = 0f;

        for (var i = 1; i < route.Count; i++)
            routeLength += Vector2.Distance(route[i - 1], route[i]);

        SayChase($"{chosen.Name}, {chosen.Away:0} grid away from ({player.X:0},{player.Y:0}) to ({to.X:0},{to.Y:0}), the straight " +
                 $"line {(GroundRoutes.LineWalkable(grid, player, to) ? "clear" : "blocked")} on the pathfinding grid, a route " +
                 $"{routeLength:0} grid long in {route.Count} cells: headed toward");

        // **The instant loot sound as the chase takes over**, not when the label comes on screen: the player's input is
        // blocked from now. Once per item, as SoundForNewItems plays it. See AutolootSettings.PlayInstantLootSound.
        if (autoloot.PlayInstantLootSound && SoundedItemIds.Add(chosen.Id))
            PlaySound(autoloot.InstantLootSound.Value, autoloot.InstantLootSoundVolume.Value);

        return true;
    }

    /// <summary>
    /// One Tick of the chase: over once the item's label is on screen, the item gone, or ChaseMostMs passed; otherwise the
    /// route worked out again every ChaseRerouteEvery, and the character sent toward the farthest point of it in a
    /// straight line from where it stands - a roll held into a sprint, aimed with the cursor, when that stretch is at
    /// least a roll long and the dodge roll key is set, and the movement keys pointing at it otherwise. A sprint stalled
    /// since its roll ended has its key let go.
    /// </summary>
    private static void StepOffScreenChase(GameController gc)
    {
        NoteChaseTimeline(gc);

        // **Sprinting on to reach, once the label is on screen**: ended there, the sprint was let go short of the item
        // and the click walked the rest, slower (the player's account, 2026-10-25). Walking with the movement keys, the
        // pickup's own walk is no slower, so it takes over at once.
        if (LabelClickable(gc, Panels.Covered(gc), _chaseTarget, _chaseId) && (!_chaseDodgeKeyHeld || ChaseTargetInReach(gc)))
        {
            EndChase(_chaseDodgeKeyHeld ? "its label on screen and in reach, sprinted to" : "its label on screen");
            return;
        }

        // **Gone by its leaving the entity list, not by Entity.IsValid**, which read false for a Divine Orb 170 grid off
        // that stayed on the ground, ending the chase after 110ms (dump 2026-10-25 02:34:24). See Ground.ItemDeparted.
        if (Ground.DepartureOfItem(_chaseId) is { } departed && departed >= _chaseStartedAt)
        {
            EndChase("the item gone");
            return;
        }

        if ((DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds >= ChaseMostMs)
        {
            ChasedItemIds.Add(_chaseId);
            EndChase($"{ChaseMostMs}ms passed - not headed toward again this area");
            return;
        }

        var grid = Safe.Read(gc, static g => g.IngameState.Data.RawPathfindingData, null);
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var to = Safe.Read(_chaseTarget, static e => e.GridPos, Vector2.Zero);

        if (DateTime.UtcNow - _chaseRoutedAt >= ChaseRerouteEvery && player != Vector2.Zero)
        {
            _chaseRoute = GroundRoutes.Route(grid, player, to) ?? _chaseRoute;
            _chaseRoutedAt = DateTime.UtcNow;
        }

        var steer = FarthestStraightFrom(grid, player, _chaseRoute);

        // Where it steers, when that changes, beside where the character stands, so a dump shows the way it took.
        if (Vector2.Distance(steer, _chaseLastSteer) >= 1f)
        {
            _chaseLastSteer = steer;
            ChaseTimeline.Add($"{(DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds:0}ms at ({player.X:0},{player.Y:0}) steering for " +
                              $"({steer.X:0},{steer.Y:0})");
        }

        // The roll held into a sprint: let go once stalled since the roll ended, a wall or a monster in the way.
        if (_chaseDodgeKeyHeld && _chaseRollPressedAt is { } pressed && !Placement.DodgeRolling(gc) &&
            DateTime.UtcNow - pressed > TimeSpan.FromMilliseconds(CharacterMovementFromStats.Roll(gc).Ms) + Placement.StandingAfter &&
            !Placement.PositionChangedRecently())
        {
            _input.Let(_dodgeRollKey);
            _chaseDodgeKeyHeld = false;
        }

        var straight = Vector2.Distance(player, steer);
        var roll = CharacterMovementFromStats.Roll(gc);

        // A roll of its own under way: from its key until the roll is over, the cursor steers it, no movement key held.
        var rollUnderWay = _chaseRollPressedAt is { } rolledAt &&
                           (Placement.DodgeRolling(gc) || (DateTime.UtcNow - rolledAt).TotalMilliseconds < ClickFailsAfterMs(gc, true));

        // **A roll where the straight stretch is a roll long**: once, held into a sprint, with Sprint after instant loot's
        // dodge roll; otherwise tapped, and again after each where the next stretch is long enough and a roll covers
        // ground faster than walking does. See AutolootSettings.SprintAfterInstantLootDodgeRoll.
        var mayRoll = _sprintAfterRoll
            ? _chaseRollPressedAt == null
            : !rollUnderWay && roll.Ms > 0d && roll.Grid / roll.Ms * 1000d > CharacterMovementFromStats.WalkGridPerSecond(gc);

        if (!_chaseDodgeKeyHeld && _dodgeRollKey != Keys.None && !Placement.DodgeRolling(gc) && mayRoll &&
            straight >= roll.Grid)
        {
            LetGoOfChaseMovementKeys();

            if (AimCursorAtGround(gc, steer, player))
            {
                _chaseAimSentAt = DateTime.UtcNow;
                return;
            }

            // **The roll only once the game's own cursor points the way**: with no movement key held a roll goes toward
            // the cursor, and nothing checked that it was there. A chase's first roll went away from a Perfect Chaos Orb
            // 100 grid off, the player pressed S and D against it, and its second roll was a DodgeRollBack (dump
            // 2026-10-25 18:24:06); where the cursor was is not in that dump. Off by more than SteeringTurnDegrees, the
            // steering's own turn tolerance: aimed again, then waited for, then the chase ended without the roll.
            var off = CursorOffWayDegrees(gc, steer, player);

            if (off > SteeringTurnDegrees)
            {
                if (_chaseAimSentAt == null)
                {
                    _steeringSentWay = Vector2.Zero;
                    _chaseAimSentAt = DateTime.UtcNow;
                    ChaseTimeline.Add($"{(DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds:0}ms the cursor {off:0} degrees off the way " +
                                      "to roll: aimed again");
                    return;
                }

                if ((DateTime.UtcNow - _chaseAimSentAt.Value).TotalMilliseconds < ClickFailsAfterMs(gc, true))
                    return;

                ChasedItemIds.Add(_chaseId);
                EndChase($"the cursor still {off:0} degrees off the way to roll " +
                         $"{(DateTime.UtcNow - _chaseAimSentAt.Value).TotalMilliseconds:0}ms after it was aimed - not rolled, " +
                         "not headed toward again this area");
                return;
            }

            NoteSteeringWayTo(gc, steer);

            // From the later of the chase's start, when the block let go of the player's keys, and the chase letting go
            // of its own movement keys to roll.
            if (WalkingOnLetGoKeys(gc, _chaseMovementKeysLetGoAt > _chaseStartedAt ? _chaseMovementKeysLetGoAt : _chaseStartedAt))
                return;

            if (_sprintAfterRoll ? _input.Hold(_dodgeRollKey) : _input.Tap(_dodgeRollKey))
            {
                _chaseDodgeKeyHeld = _sprintAfterRoll;
                _chaseRollPressedAt = DateTime.UtcNow;
                _chaseAimSentAt = null;
                rollUnderWay = true;
                ChaseTimeline.Add($"{(DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds:0}ms roll pressed at ({player.X:0},{player.Y:0}) " +
                                  $"for ({steer.X:0},{steer.Y:0}), the cursor {(float.IsNaN(off) ? "unread" : $"{off:0} degrees")} off the way");
            }
        }

        if (_chaseDodgeKeyHeld || rollUnderWay)
            SteerCursorToward(gc, steer);
        else
            HoldChaseMovementKeysToward(gc, steer, player);

        _input.IgnorePlayerMouse(_mouseBlockMs, _inputBlocked);
    }

    /// <summary>
    /// Whether an item's label has a spot clear to click - on screen, off every panel and other label - by
    /// Placement.Exposed, the test the pickup clicks on. The test a chase starts and ends on.
    /// </summary>
    private static bool LabelClickable(GameController gc, List<RectangleF> covered, Entity item, uint id) =>
        Placement.Button(gc, item, []).Rect is { Width: > 0f } rect &&
        Placement.Exposed(gc, covered, rect, id).At != Vector2.Zero;

    /// <summary>Whether an item's label centre is on screen under a panel - the inventory, a stash. See StartOffScreenChase.</summary>
    private static bool LabelBehindPanel(GameController gc, List<RectangleF> covered, Entity item) =>
        Placement.Button(gc, item, []).Rect is { Width: > 0f } rect &&
        new Vector2(rect.Center.X, rect.Center.Y) is var centre && Placement.OnScreen(gc, centre) &&
        (Panels.Covers(covered, centre) || Panels.Blocked(gc, centre, true));

    /// <summary>Whether the player stands within PickupReachGrid of the chase's item.</summary>
    private static bool ChaseTargetInReach(GameController gc)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var item = Safe.Read(_chaseTarget, static e => e.GridPos, Vector2.Zero);

        return player != Vector2.Zero && item != Vector2.Zero && Vector2.Distance(player, item) <= PickupReachGrid;
    }

    /// <summary>What the last look found to head toward, for the dump. See StartOffScreenChase.</summary>
    private static string ChaseLookSaid = "no look yet";

    /// <summary>
    /// Where to steer along the route: the farthest point of it the straight line from here reaches keeping
    /// GroundRoutes.SteeringRoom from walls, unless that is nearer than SteeringRoomNearest, when the farthest it
    /// reaches over ground at all; the route's end with neither.
    /// </summary>
    private static Vector2 FarthestStraightFrom(int[][] grid, Vector2 from, List<Vector2> route)
    {
        if (route == null || route.Count == 0)
            return from;

        Vector2? Farthest(int room)
        {
            for (var i = route.Count - 1; i >= 0; i--)
            {
                if (GroundRoutes.LineWithRoom(grid, from, route[i], room))
                    return route[i];
            }

            return null;
        }

        if (Farthest(GroundRoutes.SteeringRoom) is { } roomy && Vector2.Distance(from, roomy) >= GroundRoutes.SteeringRoomNearest)
            return roomy;

        return Farthest(1) ?? route[^1];
    }

    /// <summary>
    /// Where a grid point shows on screen, brought in along the line from the player's own screen position to the last
    /// point on screen when it lies off it, for the cursor to point the character's way. Zero when either cannot be read.
    /// </summary>
    private static Vector2 ScreenTowardGround(GameController gc, Vector2 groundAt, Vector2 player)
    {
        var from = ScreenOfGrid(gc, player);
        var to = ScreenOfGrid(gc, groundAt);

        if (from == Vector2.Zero || to == Vector2.Zero)
            return Vector2.Zero;

        if (Placement.OnScreen(gc, to))
            return to;

        // Halving toward the last share of the line still on screen.
        float low = 0f, high = 1f;

        for (var step = 0; step < 12; step++)
        {
            var mid = (low + high) / 2f;

            if (Placement.OnScreen(gc, Vector2.Lerp(from, to, mid)))
                low = mid;
            else
                high = mid;
        }

        return Vector2.Lerp(from, to, low);
    }

    /// <summary>Where a grid point shows on screen, at the terrain's height there; zero when it cannot be read.</summary>
    private static Vector2 ScreenOfGrid(GameController gc, Vector2 grid)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(grid), Vector3.Zero);

        return camera == null || world == Vector3.Zero ? Vector2.Zero : Safe.Read(() => camera.WorldToScreen(world), Vector2.Zero);
    }

    /// <summary>
    /// How far, in degrees on screen, the game's cursor (IngameState.MousePosX and Y, what a roll goes toward) points from
    /// the character's way to a ground point; NaN when the character, the point or the cursor cannot be read, or the
    /// cursor sits on the character.
    /// </summary>
    private static float CursorOffWayDegrees(GameController gc, Vector2 groundAt, Vector2 player)
    {
        var from = ScreenOfGrid(gc, player);
        var to = ScreenOfGrid(gc, groundAt);
        var cursor = CursorOnScreen(gc);

        if (from == Vector2.Zero || to == Vector2.Zero || cursor == Vector2.Zero || (to - from).Length() < 1f || (cursor - from).Length() < 1f)
            return float.NaN;

        var cos = Vector2.Dot(Vector2.Normalize(to - from), Vector2.Normalize(cursor - from));

        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>
    /// The cursor the way to a ground point, at once: on the ground PickupReachGrid from the character that way, or at
    /// the point when nearer - as SteerCursorToward puts it, at instant loot's move times. Sent toward the point itself,
    /// a chase's first aim at a route point off screen put the cursor at the screen's edge (the player's account,
    /// 2026-10-25). True when a move was sent.
    /// </summary>
    private static bool AimCursorAtGround(GameController gc, Vector2 groundAt, Vector2 player)
    {
        var way = groundAt - player;

        if (player == Vector2.Zero || groundAt == Vector2.Zero || way.Length() < 1f)
            return false;

        // **Not again for the same way**: the point a reach out slides across the screen with the camera while the
        // character settles, and re-aiming at it each Tick held a chase's roll 69ms past the end of the walk it waited
        // on (dump 2026-10-25 02:52:14). Sent once, it is moved again only on a turn, as SteerCursorToward does.
        if (_steeringSentWay != Vector2.Zero &&
            Vector2.Dot(Vector2.Normalize(way), _steeringSentWay) >= MathF.Cos(SteeringTurnDegrees * MathF.PI / 180f))
            return false;

        var at = ScreenTowardGround(gc, player + Vector2.Normalize(way) * Math.Min(PickupReachGrid, way.Length()), player);

        if (at == Vector2.Zero)
            return false;

        var cursor = CursorOnScreen(gc);
        var spot = new RectangleF(at.X - 3f, at.Y - 3f, 6f, 6f);

        if (cursor.X >= spot.Left && cursor.X <= spot.Right && cursor.Y >= spot.Top && cursor.Y <= spot.Bottom)
            return false;

        if (!_input.MoveTo(spot))
            return false;

        _steeringSentWay = Vector2.Normalize(way);
        return true;
    }

    /// <summary>Holds the movement keys pointing from the character to a ground point on screen, letting go of the rest.</summary>
    private static void HoldChaseMovementKeysToward(GameController gc, Vector2 groundAt, Vector2 player)
    {
        var wanted = new HashSet<Keys>();
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var fromWorld = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(player), Vector3.Zero);
        var toWorld = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(groundAt), Vector3.Zero);

        if (camera != null && fromWorld != Vector3.Zero && toWorld != Vector3.Zero)
        {
            var way = Safe.Read(() => camera.WorldToScreen(toWorld), Vector2.Zero) - Safe.Read(() => camera.WorldToScreen(fromWorld), Vector2.Zero);

            if (way != Vector2.Zero)
            {
                if (Math.Abs(way.X) > EighthTurnSlope * Math.Abs(way.Y))
                    wanted.Add(way.X < 0f ? _movementKeys.Left : _movementKeys.Right);

                if (Math.Abs(way.Y) > EighthTurnSlope * Math.Abs(way.X))
                    wanted.Add(way.Y < 0f ? _movementKeys.Up : _movementKeys.Down);

                wanted.Remove(Keys.None);
            }
        }

        foreach (var key in ChaseMovementKeysHeld.Except(wanted).ToList())
        {
            _input.Let(key);
            ChaseMovementKeysHeld.Remove(key);
        }

        foreach (var key in wanted.Except(ChaseMovementKeysHeld).ToList())
        {
            if (_input.Hold(key))
                ChaseMovementKeysHeld.Add(key);
        }
    }

    /// <summary>Lets go of every key the chase holds. In Finish too, which the action key's cancel goes through.</summary>
    private static void LetGoOfChaseKeys()
    {
        LetGoOfChaseMovementKeys();

        if (_chaseDodgeKeyHeld)
            _input?.Let(_dodgeRollKey);

        _chaseDodgeKeyHeld = false;
    }

    private static void LetGoOfChaseMovementKeys()
    {
        if (ChaseMovementKeysHeld.Count > 0)
            _chaseMovementKeysLetGoAt = DateTime.UtcNow;

        foreach (var key in ChaseMovementKeysHeld)
            _input?.Let(key);

        ChaseMovementKeysHeld.Clear();
    }

    /// <summary>When the chase last let go of movement keys it held, for its roll's wait. See WalkingOnLetGoKeys.</summary>
    private static DateTime _chaseMovementKeysLetGoAt;

    /// <summary>
    /// Ends the chase: the keys it holds let go and the cursor given up, the key block kept for the pickup that follows -
    /// the next look, at once - to replace or let go. See LetGoWhenNoPickupStarts.
    /// </summary>
    private static void EndChase(string why)
    {
        LetGoOfChaseKeys();
        EndSteeringGlide();
        SayChase($"{_chaseName}: chase over after {(DateTime.UtcNow - _chaseStartedAt).TotalMilliseconds:0}ms - {why}; " +
                 string.Join("; ", ChaseTimeline));
        _chaseTarget = null;
        _scannedAt = DateTime.MinValue;
        Finish(keepKeysBlocked: true);
    }

    private static void SayChase(string said)
    {
        ChasesSaid.Add($"{DateTime.Now:HH:mm:ss.fff} {said}");

        if (ChasesSaid.Count > ChasesKept)
            ChasesSaid.RemoveAt(0);
    }
}
