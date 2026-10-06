using System.Collections.Generic;
using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Linq;
using System.Numerics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The detonator, and the two numbers the planner cannot do without.
///
/// Everything here is in grid units, which is what the game reports and what the planner works in.
/// Only drawing converts to world.
/// </summary>
internal static class Detonator
{
    /// <summary>One grid cell in world units: 250 world per 23 grid.</summary>
    public const float GridToWorld = 250f / 23f;

    /// <summary>
    /// The smallest an entity can be, in grid units.
    ///
    /// **A floor rather than a measurement.** Nothing in a dig site is smaller than this, so an
    /// ordinary marker - a signpost, a chest, a remnant - is exactly this size whatever its model
    /// looks like. It is why Render.Bounds predicts nothing here: a runic henge the size of a house
    /// has this extent, and so does a monster marker.
    ///
    /// **It is one number used twice, and that is why it is named rather than typed twice.** A
    /// blast catches a thing when it touches the thing's EDGE, so the catch distance is the
    /// explosion radius plus the object's extent; and the circle the game paints is the explosion
    /// radius less this same floor, which is what the blast radius adjustment subtracts so the
    /// circle drawn here sits on the game's own. See Extents and Debug.CircleCorrection.
    /// </summary>
    public const float MinimumExtent = 2.25f;

    private const string IndicatorMetadata = "Metadata/MiscellaneousObjects/Expedition/ExpeditionPlacementIndicator";

    private static int _infoFrame = -1;
    private static ExpeditionDetonatorInfo _info;

    private static int _indicatorFrame = -1;
    private static Entity _indicatorEntity;

    /// <summary>
    /// The detonator's own account of the encounter, read once a frame.
    ///
    /// Reached about twenty times a frame between the overlay and the measurements - every count,
    /// every position, every state test goes through here - and the answer cannot change between
    /// two draws in the same frame.
    /// </summary>
    public static ExpeditionDetonatorInfo Info(GameController gc)
    {
        if (_infoFrame == Frame.Number)
            return _info;

        _infoFrame = Frame.Number;
        _info = Safe.Read(gc, static g => g?.IngameState?.IngameUi?.ExpeditionDetonatorElement?.Info, null);

        return _info;
    }

    public static bool Placing(GameController gc) =>
        Safe.Read(Info(gc), static i => i.IsExplosivePlacementActive, false);

    /// <summary>
    /// Every expedition in this map, as its detonator position and how much of it has been placed.
    ///
    /// **The detonator panel enumerates the whole map, not the site you are standing in.** Info
    /// carries an Encounters list with one entry per expedition - each with its own detonator
    /// position and its own placed-of-total - so "how many expeditions are here" and "which of them
    /// has been started" are both answerable from the moment the panel exists, without walking to
    /// either one. Corroborated by the map stat MapNumExtraExpeditions, which read 1 on the map
    /// where this list read two entries.
    ///
    /// This is what tells a legitimate "Expedition Complete" from the one that bricks a map. See
    /// Finished and Spawns.
    /// </summary>
    public static List<(Vector2 At, int Placed, int Total)> Sites(GameController gc)
    {
        var found = new List<(Vector2 At, int Placed, int Total)>();

        SwapSiteRecordsOnAreaChange(gc);

        var encounters = Safe.Read(Info(gc), static i => i.Encounters, null);

        if (encounters == null)
            return found;

        foreach (var one in encounters)
        {
            var at = Safe.Read(one, static e => e.DetonatorGridPosition, default);

            var placed = Safe.Read(one, static e => e.PlacedExplosiveCount, -1);

            found.Add((new Vector2(at.X, at.Y), placed,
                Safe.Read(one, static e => e.TotalExplosiveCount, -1)));

            // The high-water mark, taken here because this is the one place every encounter is
            // walked. See Worked.
            if (placed > 0)
            {
                var cell = (at.X, at.Y);

                if (!_worked.TryGetValue(cell, out var most) || placed > most)
                    _worked[cell] = placed;
            }
        }

        return found;
    }

    /// <summary>
    /// How many expeditions in this map have had nothing placed in them, ever.
    ///
    /// A banner claiming the whole thing is over while one of these is standing is the signature of
    /// the bug that bricks a map. Negative counts are what an unreadable entry reads as and are not
    /// evidence of anything, so only a positive total with nothing placed counts.
    ///
    /// **Ever, not now, because the count goes back to nought when a chain is detonated.** A site
    /// read 3/5 while it was being worked, and 0/5 once it had been blown and its markers cleared -
    /// so a finished expedition is indistinguishable from an untouched one through the live figure
    /// alone. That is what raised the bricked-map warning on the last site of a map where both
    /// expeditions had been done properly and both banners were owed.
    ///
    /// So it asks the high-water mark. See ExplosivesEverPlacedHere.
    /// </summary>
    public static int ExpeditionsNeverStarted(GameController gc)
    {
        var sites = Sites(gc);
        var idle = 0;

        foreach (var site in sites)
            if (site.Total > 0 && ExplosivesEverPlacedHere(site.At) == 0 && !SetOff(site.At))
                idle++;

        return idle;
    }

    /// <summary>
    /// The most explosives this expedition has ever been seen holding, in this area.
    ///
    /// Latched rather than read, because PlacedExplosiveCount is reset by detonation and the
    /// question "has anything ever been put in this site" has no other source. The detonator's own
    /// StateMachine says `activated`, which would answer it outright - but only for the one across
    /// the map when its entity happens to be loaded, and it is not. The panel is always readable.
    ///
    /// Keyed by the detonator's cell, since that is the only thing about an encounter that does not
    /// move, and cleared with the area.
    /// </summary>
    public static int ExplosivesEverPlacedHere(Vector2 at) =>
        _worked.TryGetValue(((int)MathF.Round(at.X), (int)MathF.Round(at.Y)), out var most) ? most : 0;

    private static readonly Dictionary<(int X, int Y), int> _worked = new();

    private static uint _workedFor;

    /// <summary>
    /// The set-off latch and the placed high-water mark of each area left recently, so coming back through a portal
    /// restores them. Neither can be read again from across the map: a detonated site's panel reads 0 of 0 on return
    /// and its detonator is only readable once loaded, so a finished Grand site whose records were dropped with the
    /// area read as never started, and the presolve planned and drew a chain on it. Keyed by area hash, which a map
    /// instance keeps across a portal and a new instance does not share. See Insisted.AreaChange, which keeps the
    /// must-take marks the same way.
    /// </summary>
    private static readonly Dictionary<uint, (Dictionary<(int X, int Y), int> Worked, HashSet<(int X, int Y)> SetOff)>
        _siteRecordsByArea = new();

    /// <summary>The areas in _siteRecordsByArea, oldest first, so the oldest goes once there are too many.</summary>
    private static readonly List<uint> _areasWithSiteRecords = new();

    /// <summary>How many areas' site records are kept. The same as the must-take marks keep.</summary>
    private const int AreasWithSiteRecordsKept = 8;

    /// <summary>
    /// Swaps the site records over when the area has changed since they were last touched. Called by everything that
    /// writes or reads them with the game in hand, so no one caller being skipped leaves another area's records in
    /// place. See _siteRecordsByArea.
    /// </summary>
    private static void SwapSiteRecordsOnAreaChange(GameController gc)
    {
        var area = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);

        if (area == _workedFor)
            return;

        SwapSiteRecordsForArea(_workedFor, area);
        _workedFor = area;
        SetOffEvidence.Clear();
    }

    /// <summary>
    /// Files the site records of the area being left and puts back those of the area entered, or empties them for an
    /// area not seen recently. See _siteRecordsByArea.
    /// </summary>
    private static void SwapSiteRecordsForArea(uint left, uint entered)
    {
        if (left != 0 && (_worked.Count > 0 || _setOff.Count > 0))
        {
            _siteRecordsByArea[left] = (new Dictionary<(int X, int Y), int>(_worked), new HashSet<(int X, int Y)>(_setOff));
            _areasWithSiteRecords.Remove(left);
            _areasWithSiteRecords.Add(left);

            while (_areasWithSiteRecords.Count > AreasWithSiteRecordsKept)
            {
                _siteRecordsByArea.Remove(_areasWithSiteRecords[0]);
                _areasWithSiteRecords.RemoveAt(0);
            }
        }

        _worked.Clear();
        _setOff.Clear();

        if (entered != 0 && _siteRecordsByArea.TryGetValue(entered, out var kept))
        {
            foreach (var (cell, most) in kept.Worked)
                _worked[cell] = most;

            _setOff.UnionWith(kept.SetOff);
        }
    }

    /// <summary>
    /// Whether the cursor is sitting on the game's own "toggle explosive placement" button.
    ///
    /// Used as a gesture rather than as a thing to click. The scan key means "place the next
    /// explosive" almost all the time, and the one moment it needs to mean something else is when
    /// the chain has moved on and the route wants working out again - so the button that already
    /// stands for "I am about to think about explosives" is what says which.
    ///
    /// Better than a second hotkey: it is where the hand already is when the question comes up, and
    /// there is nothing to remember.
    /// </summary>
    public static bool OverToggle(GameController gc)
    {
        var rect = ToggleRect(gc);

        if (rect.Width <= 0f || rect.Height <= 0f)
            return false;

        var at = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY),
            Vector2.Zero);

        return at != Vector2.Zero &&
               at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom;
    }

    /// <summary>
    /// Whether the green placement circle is actually up - the only state in which a click places.
    ///
    /// The indicator ENTITY rather than IsExplosivePlacementActive, because the entity is the thing
    /// that draws the circle: it is what the player means by "the green radius is visible", and it
    /// is the thing a click needs in order to put an explosive down. The flag is about the mode and
    /// has been seen to disagree; the circle cannot.
    /// </summary>
    public static bool Showing(GameController gc) => IndicatorEntity(gc) != null;

    /// <summary>The game's placement button on screen, or an empty rect when it is not there.</summary>
    /// <summary>
    /// Whether the game is offering the explosive placement button at all.
    ///
    /// **The rect is there whether the button is shown or not**, so its size cannot answer this -
    /// a dump taken with the detonator finished still reported a 142 by 127 rectangle sitting at
    /// 1737,1275 with visible False. Only IsVisible says it.
    ///
    /// It is the game's own answer to "are you at a dig site with something to place", which is a
    /// better question than any distance test: the client knows when it is offering the tool and
    /// nothing has to be inferred from where the detonators are.
    /// </summary>
    public static bool Placeable(GameController gc)
    {
        var button = Safe.Read(gc,
            static g => g?.IngameState?.IngameUi?.ExpeditionDetonatorElement?.ToggleExplosivePlacementButton,
            null);

        return button != null && Safe.Read(button, static b => b.IsVisible, false);
    }

    public static RectangleF ToggleRect(GameController gc)
    {
        var button = Safe.Read(gc,
            static g => g?.IngameState?.IngameUi?.ExpeditionDetonatorElement?.ToggleExplosivePlacementButton,
            null);

        return button == null ? default : Safe.Read(button, static b => b.GetClientRectCache, default);
    }

    /// <summary>
    /// Where the explosives already down are, in grid units.
    ///
    /// The game's own list, which is what makes undo free: shift+V takes the last one off the list
    /// and the plugin notices because it re-derives which planned spots are done from this rather
    /// than from a count it keeps itself. Nothing to keep in step, so nothing to get out of step.
    /// </summary>
    public static Vector2[] PlacedExplosiveGridPositions(GameController gc)
    {
        using var timing = Spent.On("Detonator.PlacedExplosiveGridPositions");

        var placed = Safe.Read(Info(gc), static i => i.PlacedExplosiveGridPositions, null);

        if (placed == null)
            return [];

        var found = new Vector2[placed.Length];

        for (var i = 0; i < placed.Length; i++)
            found[i] = new Vector2(placed[i].X, placed[i].Y);

        return found;
    }

    /// <summary>
    /// Where the explosive would actually land, in grid units.
    ///
    /// The indicator ENTITY's position, not the detonator element's
    /// PlacementIndicatorGridPosition. They agree while the cursor is somewhere an explosive can go
    /// and part company the moment it is not: the element's field keeps following the cursor out
    /// past the placement limit, while the entity - the thing that draws the ring - stops at the
    /// edge. Reading the element's field made the drawn circle sail off across the map while the
    /// game's sat still, and made the measured reach climb forever.
    ///
    /// Falls back to the element's field only when the entity cannot be found, which is the case
    /// where nothing better exists rather than a case where it is right.
    /// </summary>
    public static Vector2 PlacementIndicatorGridPosition(GameController gc)
    {
        var entity = IndicatorEntity(gc);

        // **Nothing, rather than the cursor, when there is no answer.**
        //
        // This used to fall back to Aim - a plausible value from a DIFFERENT quantity -
        // and that substitution caused five bugs in one day. Every one had the same shape: a
        // missing reading came back looking like a confirming one. A guard that compared the
        // landing against the request could not fail, because both sides were the request; a
        // clamp reading recorded "asked here, landed here" for a clamp that never happened; the
        // boundary sweep saw an indicator perfectly settled and perfectly agreeing.
        //
        // Zero is what the rest of this plugin already means by "no reading", and callers were
        // testing for it before this ever returned it.
        return entity == null ? Vector2.Zero : Safe.Read(() => entity.GridPos, Vector2.Zero);
    }

    /// <summary>
    /// Whether the game says an explosive can go where the indicator currently is.
    ///
    /// **The client states this outright and it was being inferred instead.** Reachability was read by
    /// comparing where the cursor asked for against where the indicator landed - apart means clamped -
    /// which only says anything when the cursor is sitting exactly on the cell being asked about. Slide
    /// it between cells and the reading is silent, so ground the player has swept over and proved
    /// perfectly placeable went unrecorded.
    ///
    /// The indicator's own animation carries the answer: the marker swaps to a "_fail" variant of the
    /// same .ao file when the spot will not take an explosive. That is a statement about wherever the
    /// indicator is standing, needs no cursor discipline, and is true on every frame.
    ///
    /// Null when it cannot be read at all - no indicator, no animation - which is not the same as a
    /// refusal and must not be recorded as one.
    /// </summary>
    public static bool? PlaceableNow(GameController gc) => PlaceableNow(gc, out _);

    /// <param name="at">
    /// The cell the verdict is ABOUT, read from the same entity that gave the verdict.
    ///
    /// **The position and the answer have to come from one place.** Indicator falls back to the
    /// detonator element's field when the entity cannot be read, and that field keeps following the
    /// cursor out past the placement limit while the entity stops at the edge - so pairing it with this
    /// animation would file a verdict about the edge against a cell out of range. One false entry in the
    /// confirmed set is worse than none of them, because that set is the evidence the model is being
    /// judged against.
    ///
    /// Zero when there is nothing to read, and then the caller records nothing.
    /// </param>
    public static bool? PlaceableNow(GameController gc, out Vector2 at)
    {
        at = Vector2.Zero;

        var entity = IndicatorEntity(gc);

        if (entity == null)
            return null;

        at = Safe.Read(() => entity.GridPos, Vector2.Zero);

        if (at == Vector2.Zero)
            return null;

        var file = Safe.Read(() => entity.GetComponent<Animated>()?.MiscAnimated?.AOFile, null);

        if (!string.IsNullOrWhiteSpace(file))
        {
            // Matched on the suffix rather than the whole name: the marker's path is a League asset
            // that may be renamed, and the variant convention is what carries the meaning.
            return !file.EndsWith("_fail.ao", StringComparison.OrdinalIgnoreCase);
        }

        // **The same fact by a second route, for the frames the first cannot answer.**
        //
        // The indicator carries a "blocked" state that moves exactly when the animation swaps - checked
        // in game, they agree - and one is readable on entities where the other is not: a dump caught
        // an indicator with a state and no animation at all. Two ways to ask one question is worth
        // having when the answer is being used to correct the model, and cheap when the second is only
        // consulted where the first came back empty.
        var states = Safe.Read(() => entity.GetComponent<StateMachine>()?.States, null);

        if (states == null)
            return null;

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i]?.Name == "blocked")
                return Safe.Read(states[i], static x => x.Value, -1L) == 0L;
        }

        return null;
    }

    /// <summary>
    /// The grid cell the placement is being ASKED for. Not where the explosive goes.
    ///
    /// Unclamped, and useful precisely because it is: the gap between this and
    /// IndicatorGridPosition is how far past the limit the aim is reaching.
    ///
    /// **The client's own name for this field is PlacementIndicatorGridPosition, and that name is
    /// the trap.** It is not where the placement indicator is - the indicator is an entity, it
    /// sits at the landing, and on a clamped aim the two are grid apart. The field is the request.
    ///
    /// **This was called RawIndicator and was read as the landing five times in one day** - a
    /// capture comparison that could never validate a clamp, a dump line asserting a marker was
    /// out of reach, the test that decides whether to click, the clamp readings, and the boundary
    /// sweep. The doc said "unclamped" the whole time; the name did not, and the doc is what
    /// nobody read. Both names now say what they hold and in what units.
    /// </summary>
    public static Vector2 RequestedGridPosition(GameController gc)
    {
        var at = Safe.Read(Info(gc), static i => i.PlacementIndicatorGridPosition, default);

        return new Vector2(at.X, at.Y);
    }

    /// <summary>
    /// The placement indicator entity, found once a frame.
    ///
    /// This one is worth caching more than the rest: finding it means scanning the whole
    /// MiscellaneousObjects bucket for a metadata match, and it was being done four separate times
    /// a frame - for the position, the blocked state, the art radius and the blast circle.
    /// </summary>
    public static Entity IndicatorEntity(GameController gc)
    {
        if (_indicatorFrame == Frame.Number)
            return _indicatorEntity;

        _indicatorFrame = Frame.Number;
        _indicatorEntity = Safe.Read(gc, static g =>
        {
            if (!g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.MiscellaneousObjects, out var of))
                return null;

            // By hand rather than with LINQ: this runs once a frame over a bucket that can hold a
            // few hundred entities, and FirstOrDefault allocates an enumerator to do the same walk.
            // **There can be more than one, and the first is not always the live one.** A dump caught
            // two: one at (1191,690) carrying the marker animation and a blocked state, and one at
            // (1182,738) with no animation at all. Taking whichever came first in the bucket meant the
            // answer depended on entity order, and PlaceableNow reads the animation - so the one that
            // has it is the one that can answer.
            Entity found = null;

            for (var i = 0; i < of.Count; i++)
            {
                if (of[i]?.Metadata != IndicatorMetadata)
                    continue;

                found ??= of[i];

                var file = Safe.Read(
                    () => of[i].GetComponent<Animated>()?.MiscAnimated?.AOFile, null);

                if (!string.IsNullOrWhiteSpace(file))
                    return of[i];
            }

            return found;
        }, null);

        return _indicatorEntity;
    }

    /// <summary>
    /// Whether the game is refusing this spot - out of range, or ground an explosive cannot go on.
    ///
    /// The indicator carries a "blocked" state, and a blocked spot is not a placement: it must not
    /// be measured from, and the planner must not put a link there.
    /// </summary>
    /// <summary>
    /// Whether an explosive already standing nearby is why the game is refusing this spot.
    ///
    /// **The placement circle goes red for two quite different reasons and they were treated as
    /// one.** Ground the game will never take is a fact about the site that is worth writing down
    /// and planning around. Ground it will not take BECAUSE an explosive is on it, or within the
    /// minimum separation of one, is a fact about this minute - take the explosive back off and the
    /// spot is fine again.
    ///
    /// Filing the second kind as the first is what poisoned whole chains: laying five explosives and
    /// letting the cursor pass over them marks all five spots refused for the life of the site, so
    /// undoing them left a route whose every link the plugin believed was unplaceable, scored at
    /// minus infinity. The spacing rule widens it further - every cell within the separation of a
    /// placed explosive reads red, so a chain poisons a circle around each of its own links.
    /// </summary>
    public static bool BlockedByOwnExplosive(GameController gc, AutoExpeditionSettings settings, Vector2 at)
    {
        if (at == Vector2.Zero)
            return false;

        var placed = PlacedExplosiveGridPositions(gc);

        if (placed == null || placed.Length == 0)
            return false;

        // The planner's own separation, so the two agree about what "too close" means. See
        // ApartAtLeast, which is measured from the game rather than guessed at.
        var apart = MathF.Max(1f, Safe.Read(() => settings.Debug.ApartAtLeast.Value, 20f));

        foreach (var was in placed)
        {
            // **At or inside the limit is too close, not just inside it.** The game wants squared
            // distance strictly past 400, so a cell exactly 20 away is refused - and on an integer
            // grid exactly 20 is reachable four ways, (0,20), (12,16), (16,12) and (20,0). This read
            // `<` and the planner reads `> apart`, so the two disagreed precisely on the boundary.
            // It never showed while the figure was 21, because no lattice point sits at 21 except
            // (0,21) and (21,0); at 20 it matters constantly.
            if (Vector2.DistanceSquared(was, at) <= apart * apart)
                return true;
        }

        return false;
    }

    public static bool PlacementIndicatorIsRed(GameController gc)
    {
        var entity = IndicatorEntity(gc);

        if (entity == null)
            return false;

        return Safe.Read(entity, static e =>
        {
            var states = e.GetComponent<StateMachine>()?.States;

            if (states == null)
                return 0L;

            for (var i = 0; i < states.Count; i++)
            {
                if (states[i]?.Name == "blocked")
                    return states[i].Value;
            }

            return 0L;
        }, 0L) != 0L;
    }

    /// <summary>
    /// Where the next explosive has to reach from: the last one placed, or the detonator if none.
    ///
    /// Never the player. The chain is anchored to itself - walking about changes nothing about how
    /// far the next explosive can go, and a reach measured from the player would shrink and grow as
    /// you moved while the real limit sat still.
    /// </summary>
    /// <summary>
    /// Which dig site is in play, as its detonator's position. Zero when there is none.
    ///
    /// A map can hold more than one encounter and they sit far apart, so a marker belongs to
    /// whichever detonator is nearest - and this is the one whose content should be counted and
    /// planned for.
    /// </summary>
    /// <summary>
    /// Whether the chain has been set off, from the detonator's own state.
    ///
    /// Minus one when the detonator cannot be found, so a caller can tell "not yet" from "no idea".
    ///
    /// The machine says it plainly, and says it at the moment the chain starts rather than part way
    /// through. With five explosives placed and nothing detonated it reads activated 0, light 1,
    /// explosives_placed 0 and targetable true; afterwards, activated 1, light 2, explosives_placed
    /// 1 and targetable false. Note that explosives_placed is NOT "explosives have been placed" -
    /// it was zero with all five down - so activated is the one to read.
    ///
    /// This replaced watching the remnants for their spent state, which worked but was late: a
    /// remnant only flips when a blast reaches it, so every explosive fired before the first one
    /// carrying a remnant had already unearthed its monsters. Measured, those blasts recorded zero
    /// monsters each while the later ones recorded twenty nine - not because they unearthed
    /// nothing, but because nobody was counting yet.
    /// </summary>
    /// <summary>
    /// What Detonated last answered, and when - because three callers ask it on every tick.
    ///
    /// **A full entity walk is not a per-frame question.** The lookup has to be the whole list
    /// rather than the IngameIcon bucket, which does not hold a spent detonator - but three callers
    /// asking sixty times a second turned that into a hundred and eighty walks of every entity in
    /// the map per second, and the overlay's frame rate went with it.
    ///
    /// A quarter second is far finer than the thing it measures: "has this site been set off" moves
    /// once per dig site, and every caller treats a late answer identically to an on-time one -
    /// Cleared starts one tick later, Spawns notices the transition one tick later.
    ///
    /// Keyed on the site as well as the clock, so walking up to a second detonator is answered at
    /// once rather than wearing the last one's answer for a quarter of a second.
    /// </summary>
    private static (Vector2 Site, int Said, DateTime When) _lit;

    public static int ExplosivesDetonated(GameController gc)
    {
        var asked = DetonatorGridPosition(gc);

        if (_lit.Site == asked && DateTime.UtcNow - _lit.When < TimeSpan.FromMilliseconds(250))
            return _lit.Said;

        var answer = Detonating(gc);

        _lit = (asked, answer, DateTime.UtcNow);

        return answer;
    }

    private static int Detonating(GameController gc)
    {
        SwapSiteRecordsOnAreaChange(gc);

        // **The whole list, because the type bucket does not hold it.**
        //
        // This asked ValidEntitiesByType for the IngameIcon bucket, which is the cheap way to find
        // something and the wrong way to find this. Seen in a live dump: a site with five of five
        // placed and the detonator sitting at the site's own grid position reading activated=1, and
        // the bucket empty of it - while the plugin's own entity listing, which walks Entities,
        // showed it plainly. Whatever "valid" means for a spent detonator, it is not this.
        //
        // The cost is a walk of the entity list, and it is paid on a tick that has already decided
        // it has something to do: Cleared stops asking once every link is fought out, and the
        // answer only matters between detonating and finishing. The metadata test below is what
        // actually identifies the thing, and it was always doing the work the bucket was credited
        // with.
        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);

        if (entities == null)
            return -1;

        var site = DetonatorGridPosition(gc);
        var here = -1;

        foreach (var entity in entities)
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("Expedition/ExpeditionDetonator", StringComparison.Ordinal) < 0)
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);
            var states = Safe.Read(entity, static e => e.GetComponent<StateMachine>()?.States, null);

            if (states == null)
                continue;

            var said = -1;

            for (var i = 0; i < states.Count; i++)
            {
                if (states[i]?.Name == "activated")
                {
                    said = (int)Safe.Read(states[i], static x => x.Value, -1L);

                    break;
                }
            }

            if (said < 0)
                continue;

            // **Every detonator this walk passes, not only this site's.** The walk is the whole
            // entity list either way, so recording the others costs nothing - and "has that
            // expedition been set off" is a question asked later, about a detonator that will not be
            // loaded by then. See SetOff.
            if (said >= 1 && _setOff.Add(((int)MathF.Round(at.X), (int)MathF.Round(at.Y))))
            {
                var placedNow = Safe.Read(() => Info(gc)?.PlacedExplosiveCount ?? -1, -1);

                SetOffEvidence.Add($"{DateTime.Now:HH:mm:ss} ({at.X:0},{at.Y:0}) {metadata} read activated {said} " +
                                   $"with {placedNow} explosive(s) placed on the panel");
            }

            // The one belonging to this dig site is the answer. A map holds two.
            if (site == Vector2.Zero || Vector2.Distance(at, site) <= 3f)
                here = said;
        }

        return here;
    }

    /// <summary>
    /// Whether this expedition has been set off, as the game said so at the time.
    ///
    /// **The detonator states it and the panel does not.** With explosives down and nothing fired
    /// the machine reads activated 0; afterwards activated 1, and it is the entity rather than
    /// ExpeditionDetonatorInfo that carries it - the panel exposes nothing about detonation at all,
    /// only the counts. See Detonating, which reads this while it is walking the list anyway.
    ///
    /// **Latched, because the entity goes away.** The detonator across the map has no StateMachine
    /// to read - it is an icon rather than a loaded object - so the state can only be taken while
    /// you are standing at the site. Taken there and kept for the area, it is still true when the
    /// question gets asked from the other end of the map.
    ///
    /// This is the direct answer where ExplosivesEverPlacedHere is a circumstantial one.
    /// ExpeditionsNeverStarted asks both: a site counts as never started only if nothing was ever
    /// put in it AND it was never set off, so either signal alone is enough to clear it.
    /// </summary>
    public static bool SetOff(Vector2 at) =>
        _setOff.Contains(((int)MathF.Round(at.X), (int)MathF.Round(at.Y)));

    /// <summary>
    /// Whether the dig site being stood in has been set off, by either signal.
    ///
    /// **Both, because each one is blind in a case the other covers.** ExplosivesDetonated walks the
    /// entity list for this site's detonator and returns -1 when it is not in it, so a spent site
    /// whose detonator has unloaded reads as a site that was never set off - and that is the normal
    /// state of affairs, because looting happens after the blasts and is spread across a dig site.
    /// SetOff is the same question answered and remembered while the walk had the entity in hand, so
    /// it survives walking away, and it knows nothing about a site first seen after the fact.
    ///
    /// The live read stays in front as the fast path for the site being stood on, and it overrules the latch whenever
    /// the detonator is loaded: a detonator read at 0 is not set off, whatever was latched. Seen 2026-10-02: a site
    /// with 0 of 19 explosives placed and its detonator reading activated 0 was latched as set off, so no link of
    /// its plan was drawn. What wrote the latch was not caught.
    /// </summary>
    public static bool SetOffHere(GameController gc)
    {
        SwapSiteRecordsOnAreaChange(gc);

        var live = ExplosivesDetonated(gc);

        if (live >= 1)
            return true;

        if (live == 0)
            return false;

        var site = DetonatorGridPosition(gc);

        return site != Vector2.Zero && SetOff(site);
    }

    private static readonly HashSet<(int X, int Y)> _setOff = new();

    /// <summary>
    /// Whether this is a Grand site and it has been set off, so there is nothing left to plan or draw on it. Grand
    /// only: a Grand map has one detonator, while an ordinary map can hold several expeditions, and the latch was not
    /// trusted to tell one of those from another when the site in front of the player changes. See SetOffHere.
    /// </summary>
    public static bool GrandSiteSetOff(GameController gc) => Grand(gc) && SetOffHere(gc);

    /// <summary>
    /// When each site was latched as set off and what was read, for the dump. Cleared with the latch. A latch written
    /// while nothing was placed is the case to look at; see SetOffHere.
    /// </summary>
    public static readonly List<string> SetOffEvidence = new();

    public static Vector2 DetonatorGridPosition(GameController gc)
    {
        var at = Safe.Read(Info(gc), static i => i.DetonatorGridPosition, default);

        return at.X != 0 || at.Y != 0 ? new Vector2(at.X, at.Y) : DetonatorGridPositionFromEntity(gc);
    }

    /// <summary>
    /// Where the detonator is, from the ENTITY, before its interface element exists.
    ///
    /// **Everything about a dig site hangs off ExpeditionDetonatorElement.Info, and that element
    /// arrives late.** It is the game's own detonator panel, so it is populated when the game
    /// decides you are dealing with a detonator - which is most of the way up to it. Until then
    /// Site reads nothing, and because Site reads nothing the preflood does not start, and because
    /// the preflood has not finished the presolve does not either. The whole approach, which is the
    /// window this plugin exists to use, was spent waiting for a panel.
    ///
    /// The entity streams in far earlier, with a grid position and nothing else needed. It is a
    /// worse source in one respect and better in every other: it cannot say how many explosives
    /// the site has - see Expected, which assumes - and it is the position of an object rather than
    /// the position the game calls the detonator's. Those agree, having been checked against a live
    /// site: the entity sits on the grid square Info reports.
    ///
    /// The nearest one, because a map holds two dig sites and the one being walked towards is the
    /// one this is about - at whatever distance, since the work worth doing early needs positions
    /// rather than proximity. Info takes over the moment it can, so this answers only until then.
    ///
    /// Cached, because it walks the whole entity list and three callers ask it per tick.
    /// </summary>
    public static Vector2 DetonatorGridPositionFromEntity(GameController gc)
    {
        if (DateTime.UtcNow - _seenAt < Lately)
            return _seen;

        _seenAt = DateTime.UtcNow;
        _seen = Vector2.Zero;

        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);

        if (entities == null)
            return _seen;

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var closest = float.MaxValue;

        foreach (var entity in entities)
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("Expedition/ExpeditionDetonator", StringComparison.Ordinal) < 0)
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

            if (at == Vector2.Zero)
                continue;

            var away = player == Vector2.Zero ? 0f : Vector2.Distance(player, at);

            // **The nearest, at whatever distance.**
            //
            // This used to refuse anything past six hundred grid, on the reasoning that a site you
            // have not turned towards is the wrong problem. The reasoning was wrong twice over.
            // Remnants are readable from right across the map - a live listing had every one of
            // fourteen loaded, the furthest 1,222 grid away, with its rewards, its sockets and its
            // chosen combination - so there is real work to do long before six hundred. And the
            // preflood in particular needs nothing but positions, which are the first thing there
            // is, and never wastes what it learns.
            //
            // What the distance was really guarding against - solving a site whose monsters have
            // not streamed, where propagation has almost nothing to pay out over - is a question
            // about what is KNOWN rather than about how far away it is, and it is answered by the
            // readout saying so and by the solve being redone as the content arrives.
            if (away >= closest)
                continue;

            closest = away;
            _seen = at;
        }

        return _seen;
    }

    private static Vector2 _seen;
    private static DateTime _seenAt = DateTime.MinValue;
    private static readonly TimeSpan Lately = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many explosives to plan for, assuming the ordinary five until the game says otherwise.
    ///
    /// **A plan for nought explosives is not a plan, and that is what Links returned on approach.**
    /// TotalExplosiveCount comes from the same late-arriving element as everything else, so a
    /// presolve started off the entity would have solved a site with one link. Five is what an
    /// ordinary Expedition gives, it is the commonest answer by a wide margin, and being wrong
    /// costs a presolve that is re-run the moment the real number arrives - which the rehearsal
    /// already does whenever the site changes under it.
    ///
    /// Deliberately not fifteen. Assuming a Grand site would make every approach solve the largest
    /// problem the plugin knows, and being wrong the other way is much cheaper: a five link answer
    /// on a fifteen link site is a usable prefix, while a fifteen link answer on a five link site
    /// is a chain that cannot be placed.
    /// </summary>
    public static int ExplosivesToPlanFor(GameController gc)
    {
        var area = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);
        var seat = DetonatorGridPosition(gc);
        var cell = ((int)MathF.Round(seat.X), (int)MathF.Round(seat.Y));

        // **The latch belongs to a SITE, not to an area, and that is what made it dangerous.**
        //
        // The panel keeps answering after you leave a map - see Blind - so the first frames of a new
        // area can still be reporting the last one's figures while the area hash has already moved
        // on. A latch that only ever rises then takes the stale number as this site's high-water
        // mark and holds it for the whole map: walk out of a Grand Expedition into an ordinary one
        // and the planner works to a reach of 108 where the game allows 90, promising links the game
        // clamps short of, until the plugin is reloaded and the static is thrown away.
        //
        // Keyed on the detonator as well as the area, so a count belonging to a different dig site
        // cannot be inherited by this one - and a map holding two expeditions gets one latch each
        // rather than the larger of the pair.
        if (area != _countedFor || cell != _countedAt)
        {
            _countedFor = area;
            _countedAt = cell;
            _counted = _countsOfSites.TryGetValue((area, cell), out var before) ? before : 0;
        }

        // **This site's own entry, not the panel's single figure.** Info.TotalExplosiveCount is one
        // number on a panel that enumerates every expedition in the map, where Encounters carries a
        // count per dig site - so the site being stood in can be asked about directly. See Sites.
        var known = 0;

        foreach (var (at, _, total) in Sites(gc))
        {
            if (total > known && (cell == default || Vector2.Distance(at, seat) < 3f))
                known = total;
        }

        // Nothing readable per site yet, which is the case the panel's own figure is for.
        if (known <= 0)
            known = Safe.Read(Info(gc), static i => i.TotalExplosiveCount, 0);

        // **The highest this site has ever admitted to, not whatever it says this frame.**
        //
        // The count decides whether the site is Grand, and that decides the base reach - 108 against
        // 90 - so a frame where it reads low is a frame where the planner is working to the wrong
        // budget. It reads low before the placement circle has been opened, which is exactly when a
        // site is first solved, so the reach could be wrong for a whole map and then quietly correct
        // itself the moment V was pressed.
        //
        // Latched rather than smoothed, because the count only ever goes UP as the client fills it
        // in: explosives already placed are still counted in the total. Taking the maximum cannot be
        // fooled by a late reading and cannot lag behind an early one - within one site, which is
        // the qualification the reset above exists to enforce.
        if (known > _counted)
        {
            _counted = known;

            if (_countsOfSites.Count >= CountsOfSitesKept && !_countsOfSites.ContainsKey((area, cell)))
                _countsOfSites.Clear();

            _countsOfSites[(area, cell)] = known;
        }

        return _counted > 0 ? _counted : Assumed;
    }

    /// <summary>The highest explosive count this SITE has reported. See Expected.</summary>
    private static int _counted;

    /// <summary>
    /// The highest count each site has reported, by area and detonator cell, so a site left and come back to keeps
    /// it. A detonated site's panel reads 0 of 0 on return, so without this a Grand site came back as the assumed
    /// five explosives and stopped reading as Grand. See GrandSiteSetOff.
    /// </summary>
    private static readonly Dictionary<(uint Area, (int X, int Y) Cell), int> _countsOfSites = new();

    /// <summary>How many sites' counts are kept before they are all dropped and gathered again.</summary>
    private const int CountsOfSitesKept = 32;

    /// <summary>Which area that was, so a new map cannot inherit it.</summary>
    private static uint _countedFor;

    /// <summary>And which detonator, so the other dig site in the map cannot inherit it either.</summary>
    private static (int X, int Y) _countedAt;

    /// <summary>
    /// What the latch holds and who it belongs to, so a poisoned one is visible rather than deduced.
    ///
    /// The count decides Grand, Grand decides the reach, and a reach that is wrong by eighteen grid
    /// looks exactly like a routing fault from the outside: links the overlay promises and the game
    /// clamps short of. The only symptom is the wrong number, and nothing printed it.
    /// </summary>
    public static string Counting =>
        $"{_counted} latched for area {_countedFor} at ({_countedAt.X},{_countedAt.Y})";

    /// <summary>What an ordinary Expedition gives you. See Expected.</summary>
    public const int Assumed = 5;

    /// <summary>
    /// Whether the detonator's own panel can still tell us nothing about this site.
    ///
    /// **Not a test of range, and it was briefly used as one.** The panel lists every encounter in
    /// the map with its grid position, from anywhere - a dump taken across the map named both dig
    /// sites and gave five explosives for the one being asked about - and once it has been read it
    /// keeps answering after you leave and come back. So "the panel has data" becomes true early
    /// and stays true, which makes it useless for deciding whether a site has been seen. What tells
    /// you that is which KINDS of content the scan holds; see Overlay.Status.
    ///
    /// What it does answer, and all it answers: whether the plan rests on the assumed five
    /// explosives and an entity-derived position rather than on the game's own figures.
    /// </summary>
    public static bool PanelUnreadable(GameController gc)
    {
        var info = Info(gc);

        if (info == null)
            return true;

        var at = Safe.Read(info, static i => i.DetonatorGridPosition, default);

        return (at.X == 0 && at.Y == 0) ||
               Safe.Read(info, static i => i.TotalExplosiveCount, 0) <= 0;
    }

    public static Vector2 LastExplosiveGridPosition(GameController gc)
    {
        var info = Info(gc);

        // **The same fallback Site has, and leaving it out cost the whole presolve.**
        //
        // This is where the chain starts, so it is what every link's travel is measured from and
        // what the first link has to be within reach of. With the detonator panel unread it
        // returned the grid origin - the corner of the map - and the presolve, which now begins on
        // the approach precisely because the panel is unread then, spent the walk in solving a
        // chain rooted a thousand grid from the dig site. The plan was poor, the score was low, and
        // pressing the key on arrival "fixed" it by solving the real problem for the first time.
        //
        // Nothing announced it, because a chain from the wrong place is still a chain.
        if (info == null)
            return DetonatorGridPositionFromEntity(gc);

        // Once a chain has started, the next explosive has to reach from the last one rather than
        // from the detonator.
        var placed = Safe.Read(() => info.PlacedExplosiveCount, 0);
        var at = placed > 0
            ? Safe.Read(() => info.LastExplosivePosition, default)
            : Safe.Read(() => info.DetonatorGridPosition, default);

        return at.X != 0 || at.Y != 0 ? new Vector2(at.X, at.Y) : DetonatorGridPositionFromEntity(gc);
    }

    /// <summary>
    /// How many explosives are still in hand, assuming a full set before the panel says otherwise.
    ///
    /// Zero used to mean both "all spent" and "not readable yet", and the second reading is what
    /// kept the presolve waiting on the approach.
    ///
    /// **The element existing is not the panel being filled in, and that was the remaining half of
    /// the same fault.** ExpeditionDetonatorElement is there and visible from well outside a dig
    /// site - a dump taken while walking up to one, with the detonator on screen, read the element
    /// fine and every count in it as nought. Testing Info for null therefore stopped being the
    /// question the moment the element turned out to arrive early: a panel reading 0 of 0 has not
    /// been populated for this site, where a spent site reads 0 of five.
    ///
    /// So the total is what says whether the panel knows anything. Below one it knows nothing and
    /// the assumed set stands; at one or more it is stating the site's size and the remaining count
    /// beside it means what it says. See Expected, which latches that same total.
    /// </summary>
    public static int ExplosivesInHand(GameController gc) =>
        PanelReady(gc) ? Safe.Read(Info(gc), static i => i.RemainingExplosiveCount, 0) : Assumed;

    /// <summary>
    /// Whether the detonator panel has been filled in for the site in front of you.
    ///
    /// **Not whether the element exists.** ExpeditionDetonatorElement is present and visible from
    /// well outside a dig site - a dump taken walking up to one, with the detonator on screen, read
    /// the element fine and every count in it as nought - so its existence says nothing about
    /// whether the client has started the encounter.
    ///
    /// The total is what says it: nought until the site is live, the site's real size afterwards.
    /// A spent site reads nought REMAINING of a positive total, which is how the two are told
    /// apart, and that distinction is the one thing standing between "no explosives left" and
    /// "nobody has told us yet".
    ///
    /// Three things turn on it: Remaining assumes a full set below it, Terrain.Trusted refuses to
    /// diagnose the offsets below it, and the rehearsal folds it into the site's fingerprint so a
    /// presolve made on the approach is re-opened once the site is live and the routing grid exists.
    /// </summary>
    public static bool PanelReady(GameController gc) =>
        Safe.Read(Info(gc), static i => i.TotalExplosiveCount, 0) > 0;

    /// <summary>
    /// How many explosives are down out of how many there were, as "5/5".
    ///
    /// What the HUD says once the chain is laid. It used to say "Spent", which is true and tells
    /// you nothing you did not just do - the interesting fact at that moment is the size of the
    /// chain you are about to set off, and a player deciding whether to detonate wants the number
    /// rather than a word confirming there is nothing left to place.
    ///
    /// Empty when the game has no count to give, so the caller can fall back rather than draw "0/0".
    /// </summary>
    public static string FormattedExplosivesPlacedOutOfTotal(GameController gc)
    {
        var info = Info(gc);
        var placed = Safe.Read(info, static i => i.PlacedExplosiveCount, 0);
        var total = Safe.Read(info, static i => i.TotalExplosiveCount, 0);

        return total > 0 ? $"{placed}/{total}" : "";
    }

    /// <summary>The tally, or the given word when the game has no count.</summary>
    public static string FormattedExplosivesPlacedOutOfTotal(GameController gc, string otherwise) =>
        FormattedExplosivesPlacedOutOfTotal(gc) is { Length: > 0 } tally ? tally : otherwise;

    /// <summary>
    /// The radius of the circle the game itself draws, in grid units. Exact, and from this map.
    ///
    /// While placement is active the game spawns an ExpeditionPlacementIndicator entity at the spot
    /// the explosive would go, and that entity is what draws the green circle. Its size is stated
    /// outright: MiscAnimated.BaseSize is the art's radius in world units and Positioned.Scale is
    /// what this map's modifiers did to it - the same two fields, multiplied the same way, that
    /// EffectZones uses to size every ground effect it draws.
    ///
    /// It is close but not exact: the art reads about 8% wider than the ring actually drawn, which
    /// is what a feathered edge looks like from the outside. So this is the fallback, good enough to
    /// draw with on arrival, and the measurement takes over the moment there is one. It also killed
    /// a cross-map constant-fitting scheme that had no business existing - worth remembering as a
    /// habit: look for the value before fitting a model of it.
    /// </summary>
    public static float? ArtRadius(GameController gc)
    {
        var indicator = IndicatorEntity(gc);

        if (indicator == null)
            return null;

        var baseSize = Safe.Read(indicator, static e => e.GetComponent<Animated>()?.MiscAnimated?.BaseSize ?? 0, 0);

        if (baseSize <= 0)
            return null;

        var scale = Safe.Read(indicator, static e => e.GetComponent<Positioned>()?.Scale ?? 1f, 1f);

        return baseSize * (scale <= 0f ? 1f : scale) / GridToWorld;
    }

    /// <summary>
    /// The explosion radius to draw and to plan with, in grid units. Constant while you stand here.
    ///
    /// **The computed radius, not the drawn one, and the same answer Blast.Radius gives.** It used
    /// to be the art size plus the correction, which made the overlay's circle and the planner's
    /// catch test two different numbers whenever the art had not been seen - and a planner working
    /// to a radius the player is not being shown is the fault that took a round of diagnosis on the
    /// reach. One source, and it exists before placement mode does. See ExplosionRadius.
    ///
    /// Nullable only because five callers were written against a nullable and it has never returned
    /// null since the radius became computable.
    /// </summary>
    public static float? BlastRadius(GameController gc, float correction) =>
        MathF.Max(1f, ExplosionRadius(gc) + correction);

    /// <summary>
    /// This map's explosion modifiers: area of effect, then radius.
    ///
    /// Area is a percentage of AREA and so enters as a square root - read as a radius percentage it
    /// overstates a +44% roll by a fifth. They are read separately and stored separately because
    /// only one of them has ever been seen set, and a model that silently folds two inputs into one
    /// number cannot be checked later.
    /// </summary>
    /// <summary>
    /// What this map's modifiers do to the blast radius, as a plain multiplier.
    ///
    /// Area of effect enters as a square root because it is a percentage of AREA - a +44% roll is
    /// a +20% radius, and reading it as a radius percentage would overstate the circle by a fifth.
    /// Radius applies directly.
    ///
    /// This is what makes the radius portable between maps: the art is the same everywhere and the
    /// modifiers are what differ, so dividing an observed radius by this gives a number that means
    /// something on the next map, and multiplying it back gives this map's answer without the
    /// placement indicator ever having been up.
    /// </summary>
    public static float ModifierScale(GameController gc)
    {
        var (area, radius) = Modifiers(gc);

        return MathF.Sqrt(MathF.Max(0.01f, 1f + area / 100f)) * MathF.Max(0.01f, 1f + radius / 100f);
    }

    public static (int AreaPct, int RadiusPct) Modifiers(GameController gc)
    {
        var stats = Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);

        if (stats == null)
            return (0, 0);

        var area = stats.TryGetValue(GameStat.MapExpeditionExplosionAreaOfEffectPct, out var a) ? a : 0;
        var radius = stats.TryGetValue(GameStat.MapExpeditionExplosionRadiusPct, out var r) ? r : 0;

        return (area, radius);
    }

    /// <summary>
    /// How much this map's modifiers add to the placement range, as a percentage.
    ///
    /// A distance percentage, so it applies directly - unlike the explosion's area of effect, which
    /// is a percentage of area and enters as a square root. Tablets carry it ("(15-30)% increased
    /// Expedition Explosive Placement Range"), so it is routinely non-zero and a fixed reach would
    /// be wrong whenever it is.
    ///
    /// The measured reach already includes it, because the game clamps the indicator to whatever
    /// the real limit is. This is here to be checked against, and to say why the reach on one map
    /// is not the reach on the next.
    /// </summary>
    /// <summary>
    /// How far a chain link reaches on an ordinary dig site, with no map modifier, in grid units.
    ///
    /// **Ninety, and it is a literal in the binary.** `0x141F5ABB8` reads
    /// `MapExpeditionMaximumPlacementDistancePct` (stat 0x3576) and computes
    /// `(stat + 100) * 0x5A / 100` in integer arithmetic - 0x5A is 90 - so at a map bonus of zero
    /// the reach is exactly 90. GrandReach is the same expression with 0x6C, which is 108, taken
    /// when the area flag at `[session+0x98]+0x1B4` reads 0x1C or 0x27, or the word at +0x2A reads
    /// 0x1C03. The same flag picks the blast radius base - see Blast.
    ///
    /// **This was 90.51 for a long time and that was wrong.** It came from a probe: cells at squared
    /// distance 8192 - exactly (+64,+64) - were accepted, and 8192 is 90.50967 squared. The probe
    /// could not have told the difference. Reach is a budget spent along the routed wire and the
    /// clamp does not refuse, it RELOCATES: aim 0.51 past the limit and the indicator lands 0.51
    /// short, which rounds to the same grid cell and reads as an acceptance. The test was blind to
    /// exactly the half-unit it was trying to measure.
    ///
    /// **Measured against the clamp instead.** A clamped indicator sits at exactly the reach along
    /// the wire, and unlike the cursor cell it is the game's own number. Over 28 distinct clamp
    /// landings from one explosive, the routed length to the landing has a median of 89.91 and a
    /// maximum of 90.44 - which is 90 plus the rounding of an integer indicator, and is not 90.51.
    ///
    /// Half a unit sounds harmless and is not. Near the boundary the wire doubles back, so it buys
    /// very little length per unit of ground covered: on the site this was measured on, ten grid of
    /// extra straight distance cost only 0.6 of wire. Half a unit of surplus budget therefore showed
    /// up as several grid of ground the overlay promised and the game clamped short of.
    /// </summary>
    public const float SmallReach = 90f;

    /// <summary>
    /// The same on a Grand Expedition, which is a different number and was being given the small
    /// one.
    ///
    /// **Ninety is the ordinary site's figure and it was applied to every site.** Measured by hand
    /// on a Grand site, the indicator runs to about 108 before the game refuses it - so the planner
    /// believed it had eighteen grid less to work with than it had, on every one of fifteen links.
    /// That is not a rounding error on a site this size: the chain in the last dump had six links
    /// within four grid of the limit it thought it had, which is a search pressed hard against a
    /// wall that was not there.
    ///
    /// A separate constant rather than a multiplier, because what the game is doing is unknown. The
    /// ratio is 1.2, which is suggestive and is not evidence, and nothing in MapStats moves with it
    /// - MapExpeditionMaximumPlacementDistancePct reads zero on the site this was measured on.
    ///
    /// **Hand measured, which is a step down from how the radius is known, and the search for a
    /// better source has been made.** The radius is read outright off the placement indicator's art
    /// (see ArtRadius); nothing on the detonator element or in GameStat states this one. The state
    /// "in_placing_range" looked like the answer - the game saying which markers are in range, which
    /// would bracket itself as you moved - and it is not: checked in game, it moves exactly as
    /// glow_epk does, lighting when a marker is inside the BLAST, not inside the placement range.
    /// Do not spend another hour on it.
    ///
    /// Settled instead by reading the routine that applies it - see PlacementRange, which is the
    /// game's own arithmetic rather than a figure fitted to observations of it.
    /// </summary>
    public const float GrandReach = 108f;

    /// <summary>
    /// The explosion radius on an ordinary dig site, before this map's modifiers, in grid units.
    ///
    /// **Thirty, and it is a literal in the binary.** `0x1417350B0` reads
    /// MapExpeditionExplosionAreaOfEffectPct and MapExpeditionExplosionRadiusPct, loads 0x1E or
    /// 0x25, and multiplies. See ExplosionRadius for the whole expression and GrandBlast for the
    /// other constant.
    /// </summary>
    public const float OrdinaryBlast = 30f;

    /// <summary>
    /// And on a Grand one. `0x1417350B0` takes 0x25 over 0x1E on the same area flag that makes
    /// Reach take 108 over 90, so the two constants are chosen together. See OrdinaryBlast.
    /// </summary>
    public const float GrandBlast = 37f;

    /// <summary>
    /// The explosion radius for this site, worked out the way the game works it out.
    ///
    /// `0x1417350B0`, in order: the area-of-effect percentage plus a hundred, times a hundred,
    /// through an integer square root - which is why AREA enters as a root and not directly - times
    /// the radius percentage plus a hundred over a hundred, times the base, truncated to an integer
    /// and clamped to 0..255. That product is exactly ModifierScale, and `MathF.Floor` is the
    /// truncation.
    ///
    /// **This is why nothing has to wait for the placement circle any more.** Every input is a map
    /// stat, readable from the moment the area loads, so the radius is known before the detonator
    /// panel exists rather than being learned from the drawn art once per install. See Blast.
    ///
    /// **One term of that routine is not modelled here**, and it is the only reason the art is still
    /// read: the base has `8 * (n + byte[this + 0x272])` added to it. Three of the four callers pass
    /// n = 0 and the fourth counts a loop under an area check of 0x5250, which is neither of the
    /// values that make a site Grand. Both radii ever read off the art came back at the base
    /// exactly, so the term is nought in ordinary play - and Blast says so out loud if a reading
    /// ever disagrees.
    /// </summary>
    public static float ExplosionRadius(GameController gc) =>
        MathF.Max(1f, MathF.Floor((Grand(gc) ? GrandBlast : OrdinaryBlast) * ModifierScale(gc)));

    /// <summary>
    /// How many explosives means a Grand Expedition.
    ///
    /// Fifteen is a Grand site and nothing else is. The same test decides the unknown-content sweep
    /// radius and whether the scouting layer draws.
    /// </summary>
    public const int GrandExplosives = 15;

    /// <summary>
    /// The base reach for whichever kind of site this is.
    ///
    /// Only a fallback either way. The live measurement takes over the moment the cursor has been
    /// pushed out to the edge, and it is the one to trust because it needs no assumption about
    /// which site this is or how a modifier applies - so pushing the cursor out once on a new kind
    /// of site is worth more than any number written down here.
    /// </summary>
    /// <summary>How many explosives this site has in total, which is what makes it Grand or not.</summary>
    public static int ExplosiveCount(GameController gc) => Math.Max(1, ExplosivesToPlanFor(gc));

    /// <summary>
    /// Whether this is a Grand Expedition, which is the one test several things turn on.
    ///
    /// Reach, the unknown-content radius, the scouting layer and the solve window all ask it, and
    /// they asked it four separate ways until they did not agree: the scouting layer MARKED ground
    /// only on a Grand site and DREW it everywhere, so an ordinary map had nothing marked, all of it
    /// unseen, and the whole minimap painted red.
    /// </summary>
    public static bool Grand(GameController gc) => ExplosiveCount(gc) >= GrandExplosives;

    public static float BaseReach(GameController gc) =>
        Grand(gc) ? GrandReach : SmallReach;

    /// <summary>
    /// How far from the detonator something still belongs to this dig site, in grid units.
    ///
    /// A map holds two expeditions, and several of the game's lists - the chests especially - are
    /// the whole map's rather than one site's. Measured, a SMALL dig site runs about two hundred and
    /// fifty grid from its detonator at the far end, so three hundred takes the site and nothing of
    /// the one across the map.
    ///
    /// **And that measurement was taken on a five explosive site and then applied to a fifteen.** A
    /// Grand Expedition is not the same dig with more bombs in it - the chain measured on one ran a
    /// thousand four hundred grid and put links six hundred and ninety from the detonator, which is
    /// more than twice this radius. Everything keyed on it quietly stopped at the halfway point: a
    /// reward chest at the far end of a Grand site was not offered a loot line, was not counted in
    /// the score card's total, and read as belonging to no site at all - while the automation walked
    /// to it and opened it perfectly happily, because the chore list measures from the PLAYER.
    ///
    /// **A Grand Expedition is the whole map's one dig, so there is nothing to tell it apart from.**
    /// The radius exists only because an ordinary map can hold two expeditions and the game's lists
    /// are the map's rather than a site's; where there is only ever one, any radius is a number that
    /// can be too small and can never be too large. So Grand sites are not measured at all, and the
    /// question that was being answered badly stops being asked.
    ///
    /// Small sites keep the measured three hundred, which is what it was measured on.
    /// </summary>
    public static float SiteReach(GameController gc) =>
        Grand(gc) ? float.PositiveInfinity : 300f;

    /// <summary>
    /// The reach for this site, worked out the way the game works it out.
    ///
    /// **Integer arithmetic, because the game's is.** `0x141F5ABE9` reads the percentage stat, adds
    /// a hundred, multiplies by the base and divides by a hundred - all in integers, so the result
    /// truncates. A float version is a fraction of a unit generous, and near the reach boundary a
    /// fraction of a unit of BUDGET is worth several grid of straight distance: the wire doubles
    /// back there, so it buys very little wire per unit of ground. That is what made the overlay
    /// promise placements the game then clamped short of.
    /// </summary>
    public static float PlacementRange(GameController gc) =>
        MathF.Floor(MathF.Max(1f, 100f + PlacementRangePct(gc)) * BaseReach(gc) / 100f);

    public static int PlacementRangePct(GameController gc)
    {
        var stats = Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);

        return stats != null && stats.TryGetValue(GameStat.MapExpeditionMaximumPlacementDistancePct, out var pct)
            ? pct
            : 0;
    }

}
