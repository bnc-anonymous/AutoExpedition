using ExileCore2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Markers the player has said the chain must take, whatever the weights think.
///
/// **The weights are a policy and sometimes the answer is about this one thing.** A relic nobody has
/// priced yet, a chest behind the ridge that is worth the detour tonight, a remnant whose reward the
/// plugin cannot read - the objective has no term for any of that, and arguing with it through the
/// sliders changes every site rather than this one. Pointing at a marker and saying "that one" is
/// the shortest path from what the player can see to what the search optimises.
///
/// It reuses the machinery that already exists for a required remnant rather than adding a second
/// kind of insistence - see PlanTarget.Must, which both go through.
///
/// **A rule rather than a large weight, which is what it was.** Weighting the marker above the whole
/// site put together gets the same ordering and costs a scoring vocabulary: a second worth on the
/// target, a Worth() to undo the inflation, a Bonus to subtract it from every readout, and a number
/// recomputed per solve that drifted by a factor of three inside one dig site. Counting requirements
/// on the CHAIN instead says the same thing once.
///
/// Still not fatal on a marker nothing can reach: chains are ranked by how many requirements they
/// hold, so a site where none of them fits still yields the best chain there is - and says which it
/// dropped, which the weight could never do. See Verdict.Missed.
///
/// Kept per area and cleared on a zone change, like Refused and Missed. The cells are the marker's
/// grid position rounded, which is how every other per-site fact here is keyed - an entity address
/// does not survive the marker being streamed out and back in, and the ground does.
/// </summary>
internal sealed class Insisted
{
    /// <summary>The one the plugin uses. Static, because it is one table per session and every caller wants the same one.</summary>
    public static readonly Insisted Here = new();

    /// <summary>What has been said about a marker, in the order the key cycles through them.</summary>
    public enum Said
    {
        /// <summary>Nothing. The weights decide, as they do for everything else.</summary>
        Nothing,

        /// <summary>Take it, whatever that costs.</summary>
        Take,

        /// <summary>
        /// Leave it, whatever that costs.
        ///
        /// **The opposite insistence, and it needs one because the weights cannot say it either.** A
        /// rare that spawns something this character cannot fight, a chest behind a pack not worth
        /// waking, content in a corner you have no intention of walking back to - the objective has
        /// no term for "worth less than it looks" any more than it has one for "worth more".
        /// </summary>
        Avoid,
    }

    private readonly Dictionary<(int X, int Y), Said> _cells = new();

    /// <summary>
    /// Every cell the threshold has already had its say about, whatever came of it.
    ///
    /// **Recorded separately from the answer, because "marked" and "considered" are different
    /// facts.** Without this, un-marking an auto-marked remnant lasts until the next sweep: the
    /// threshold sees an unmarked remnant over the line and marks it again, and the key appears
    /// broken on exactly the remnants somebody most wants to argue about. Holding the cells it has
    /// visited makes the offer once and leaves the decision where it belongs.
    /// </summary>
    private readonly HashSet<(int X, int Y)> _offered = new();

    /// <summary>
    /// The cells the threshold has had its say about since their remnant was rolled.
    ///
    /// A roll replaces what a remnant offers, so an offer made on the rewards it had before the roll is
    /// an offer about a remnant that no longer exists. Without this second set a remnant priced at 80ex
    /// on arrival and rolled into 1,468ex was never looked at again, because its cell was already in
    /// _offered. A remnant can be rolled once, so one further offer is all it needs. See Automatic.
    /// </summary>
    private readonly HashSet<(int X, int Y)> _offeredAfterRoll = new();

    /// <summary>
    /// The cells marked must take because of what a reward is WORTH, as opposed to by hand.
    ///
    /// **A must take on reward value is a statement about one reward, and the chain has to honour that
    /// reward rather than merely the remnant.** Automatic reads Reroll.Worth, the price of the richest
    /// offer, so a remnant clears the threshold because of one specific combination. Nothing carried
    /// that fact, so the objective took the remnant and then chose freely among its offers - reported
    /// from the game: a 471ex Masterwork Rune made a remnant must take and a 12ex reward with better
    /// runes was taken instead, which honours the letter of the instruction and none of the point.
    ///
    /// Kept apart from _cells rather than folded into Said, because a must take set BY HAND says
    /// nothing about which reward to take: somebody marking a marker wants it in the chain, and the
    /// objective is still the right thing to pick its combination. Only the value-driven ones name a
    /// reward.
    /// </summary>
    private readonly HashSet<(int X, int Y)> _forReward = new();

    /// <summary>
    /// Whether this cell is must take BECAUSE of a reward's value, right now. See _forReward.
    ///
    /// **Both halves, so the pin cannot outlive the insistence that justified it.** _forReward records
    /// why a cell was marked and is never unmarked: Cycle takes a cell out of _cells when somebody
    /// turns the must take off, and the stale-cell sweep does the same, and neither knew about this
    /// set. Read on its own it therefore went on pinning the reward after the must take was gone -
    /// which locked a remnant to the rich offer whose runes are poor, cost the chain about six
    /// thousand, and then made rolling that remnant look like a gain of eight hundred. Reported as
    /// exactly that: the score fell from 30k to 24k and the roll advice would not go away.
    ///
    /// Derived rather than kept in step, because "remember to remove it in three places" is the shape
    /// of a fault that comes back. _cells is the one truth about whether a cell is insisted; this only
    /// says what the reason was.
    /// </summary>
    public bool MarkedForRewardValue(Vector2 grid)
    {
        var cell = Cell(grid);

        return _forReward.Contains(cell) &&
               _cells.TryGetValue(cell, out var said) &&
               said == Said.Take;
    }

    private uint _area;

    /// <summary>
    /// The marker the chain must take last, or null: the chain's final explosive is the one whose blast catches it, and
    /// nothing is placed after. One per area. It is also a must take - marking it sets Take - and Planner.Settle counts
    /// that must take held only when the final link catches it, so the ranking that puts every must take first also
    /// puts this ending first. See IsTakenLast.
    ///
    /// Asked for by players who want a remnant last for Gaining Traction, which the scoring already weighs: when they
    /// believe it weighs it wrongly for them, this lets them overrule it.
    /// </summary>
    private (int X, int Y)? _takenLast;

    /// <summary>Whether this marker is the one the chain must take last. See _takenLast.</summary>
    public bool IsTakenLast(Vector2 grid) => _takenLast is { } last && last == Cell(grid);

    /// <summary>
    /// Makes this marker the one the chain takes last, or clears that when it already is. Marking it also marks it must
    /// take and moves the mark off any other marker; clearing it leaves the must take standing. Answers whether it is
    /// taken last now.
    /// </summary>
    public bool ToggleTakenLast(Vector2 grid)
    {
        var cell = Cell(grid);

        if (_takenLast == cell)
        {
            _takenLast = null;

            return false;
        }

        _takenLast = cell;
        _cells[cell] = Said.Take;

        return true;
    }

    public int Count => _cells.Count;

    /// <summary>Whether the chain has been told to take this marker.</summary>
    public bool Wants(Vector2 grid) =>
        _cells.Count > 0 && _cells.TryGetValue(Cell(grid), out var said) && said == Said.Take;

    /// <summary>Whether the chain has been told to leave this marker alone.</summary>
    public bool Avoids(Vector2 grid) =>
        _cells.Count > 0 && _cells.TryGetValue(Cell(grid), out var said) && said == Said.Avoid;

    /// <summary>What has been said about this marker, if anything.</summary>
    public Said Of(Vector2 grid) =>
        _cells.TryGetValue(Cell(grid), out var said) ? said : Said.Nothing;

    /// <summary>
    /// Moves a marker on to the next thing that can be said about it.
    ///
    /// Nothing, take, avoid, and round to nothing again. One key rather than two because the three
    /// answers are one decision - what do I want done about that - and a second key would need
    /// remembering for the rarer half of it.
    /// </summary>
    public Said Cycle(Vector2 grid)
    {
        var cell = Cell(grid);
        var next = _cells.TryGetValue(cell, out var said)
            ? said == Said.Take ? Said.Avoid : Said.Nothing
            : Said.Take;

        if (next == Said.Nothing)
            _cells.Remove(cell);
        else
            _cells[cell] = next;

        // A mark changed by hand is the player's from then on, not the threshold's, so it no longer pins a reward. See
        // MarkedForRewardValue.
        _forReward.Remove(cell);

        // Avoiding it or dropping the must take ends it being taken last too. See _takenLast.
        if (next != Said.Take && _takenLast == cell)
            _takenLast = null;

        return next;
    }

    public void Clear()
    {
        _cells.Clear();
        _takenLast = null;

        // Cleared too, so a reset is a genuine fresh start: the threshold gets to make its offer
        // again on a site somebody has deliberately wiped.
        _offered.Clear();
        _offeredAfterRoll.Clear();
        _forReward.Clear();
    }

    /// <summary>
    /// Drops the mark on anything there is nothing left to decide about.
    ///
    /// **A shattered remnant is not a requirement, it is history.** The mark meant "the chain has to
    /// reach this"; once the blast has gone off and the encounter is spent, the chain either did or
    /// it did not, and either way the next solve cannot act on it. Left standing it counts against
    /// every plan afterwards - Verdict.Held ranks chains by how many marks they hold, so a mark
    /// nothing can hold any more makes every remaining chain look like it dropped one.
    ///
    /// Both kinds go, not only must takes. There is as little to avoid about a spent marker as
    /// there is to take, and a red ring over something already dealt with reads as a warning about
    /// a decision still ahead.
    ///
    /// **It does not come back.** The threshold offers each remnant once and remembers having done
    /// so - see _offered - so clearing here is final rather than a thing the next tick undoes.
    ///
    /// Spent is the target's own answer, which already knows that a remnant is done at activated
    /// six, a sentry at one, and an entrance once its blast has been through. See Target.Spent.
    /// </summary>
    public void Settled(Scan scan)
    {
        if (_cells.Count == 0 || scan?.Targets == null)
            return;

        List<(int X, int Y)> gone = null;

        foreach (var target in scan.Targets)
        {
            if (!Safe.Read(() => target.Spent, false))
                continue;

            var cell = Cell(target.Grid);

            if (!_cells.ContainsKey(cell))
                continue;

            gone ??= new List<(int, int)>(2);
            gone.Add(cell);
        }

        foreach (var cell in gone ?? [])
        {
            _cells.Remove(cell);

            if (_takenLast == cell)
                _takenLast = null;
        }
    }

    /// <summary>
    /// Marks the rich remnants must take, once each. See RewardSettings.MustTakeAbove.
    ///
    /// **The threshold and the key now write to the same place, which is the whole change.** The
    /// plan used to hold two sorts of requirement - the markers somebody pointed at, and a live
    /// re-reading of the threshold on every solve - that behaved identically and could not be told
    /// apart. The second one could not be argued with either: un-marking a remnant the threshold
    /// liked did nothing, because nothing was ever marked.
    ///
    /// Only remnants whose reward can actually be read, and only ones this has never looked at - with one
    /// exception. A remnant that has been rolled since it was looked at gets one more look, because the
    /// roll replaced its rewards. See _offeredAfterRoll.
    ///
    /// The second look does not overturn an avoid. An avoid is somebody's decision about this spot and a
    /// roll does not revoke it; a take set by hand is already what the look would set.
    /// </summary>
    public void Automatic(Scan scan, AutoExpeditionSettings settings, Valuation valuation)
    {
        var above = Safe.Read(() => settings.Rewards.MustTakeAbove.Value, 0f);

        if (above <= 0f || scan?.Targets == null)
            return;

        foreach (var target in scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            // Nothing more to say about a cell that has had its offer, and saying it costs a
            // read of the remnant's rewards on every tick for the rest of the site. A rolled
            // remnant is asked against its own set, so an offer made before the roll does not
            // count as one made after it.
            var rolled = Safe.Read(() => target.Rerolled, false);
            var considered = rolled ? _offeredAfterRoll : _offered;

            if (considered.Contains(Cell(target.Grid)))
                continue;

            // Worth is worked out BEFORE the cell is marked as considered, because a remnant read
            // before its prices arrive is worth nothing and would spend its one offer on a number
            // nobody had yet. No price, no offer, and it comes round again on the next sweep.
            //
            // On a rolled remnant this is what it ended on rather than the best it could have
            // offered - building the chain around a reward no longer available is the most
            // expensive way to be wrong here. See Reroll.Worth.
            var worth = Reroll.Worth(target, valuation);

            if (worth <= 0d)
                continue;

            var cell = Cell(target.Grid);

            // A rolled remnant also counts as offered in the ordinary set, so nothing reading that set
            // alone takes it for a cell nobody has looked at.
            if (rolled)
                _offered.Add(cell);

            if (considered.Add(cell) && worth >= above &&
                !(rolled && _cells.TryGetValue(cell, out var standing) && standing == Said.Avoid))
            {
                _cells[cell] = Said.Take;

                // Recorded so the reward that cleared the threshold is the one the chain takes. See
                // _forReward and Planning.Pinned.
                _forReward.Add(cell);
            }
        }
    }

    /// <summary>
    /// Offers an avoid on the boss that can brick a later site, while there is a later site to brick.
    ///
    /// **A workaround for the game, not for the planner.** Killing Tetzcatl can raise "Expedition
    /// Complete" for the whole map rather than for its own encounter, and an expedition nobody has
    /// started is then unable to take explosives at all. See BugsSettings.AvoidTetzcatl, and
    /// Detonator.Sites for how the rest of the map is enumerated.
    ///
    /// **Offered once and then left alone**, through the same _offered set the value threshold uses,
    /// so the avoid key takes it straight back off and it does not come back on the next sweep. The
    /// plugin gets to raise the question; the player gets to answer it. Which is also why it is not
    /// lifted automatically once the other sites have been started - something that un-marks itself
    /// cannot be argued with either.
    ///
    /// Marked rather than filtered for the same reason: an avoid is a thing the readout can state
    /// and the player can overrule, where content quietly going missing is neither.
    ///
    /// Runs BEFORE Automatic, because both write through _offered and the first one there wins. A
    /// boss worth more than the must-take threshold would otherwise be marked take and the
    /// workaround would never get a say.
    /// </summary>
    public void Bugged(GameController gc, Scan scan, AutoExpeditionSettings settings)
    {
        if (scan?.Targets == null || !Safe.Read(() => settings.Bugs.AvoidTetzcatl.Value, false))
            return;

        if (!Bricks(gc))
            return;

        foreach (var target in scan.Targets)
        {
            if (!string.Equals(target.Meta, Boss, StringComparison.OrdinalIgnoreCase))
                continue;

            var cell = Cell(target.Grid);

            if (_offered.Add(cell))
                _cells[cell] = Said.Avoid;
        }
    }

    /// <summary>
    /// Whether an expedition OTHER than the one being dug is still standing untouched.
    ///
    /// Not Detonator.ExpeditionsNeverStarted, which counts this one too: before the first explosive goes down the
    /// site in front of the player is itself untouched, so the plain count would say the danger had
    /// passed the moment the first bomb was placed - exactly backwards. The site being dug is the
    /// one whose detonator panel is open, so it is excluded by position.
    /// </summary>
    private static bool Bricks(GameController gc)
    {
        var here = Safe.Read(() => Detonator.DetonatorGridPosition(gc), Vector2.Zero);

        return Safe.Read(() => Detonator.Sites(gc), null)?.Any(
            x => x.Total > 0 && x.Placed == 0 && Vector2.Distance(x.At, here) > 1f) ?? false;
    }

    /// <summary>
    /// Tetzcatl, the Blazing Guardian - the boss behind the premature "Expedition Complete".
    ///
    /// **The metadata, matched whole, and the name appears nowhere in it.** This was written as a
    /// substring test for "Tetzcatl", which matches nothing at all: the client names the entity by
    /// its statue rather than by the boss, and the display name only ever reaches the plugin through
    /// the reference table row. Target.Meta is the path, so the path is what to compare - and whole,
    /// since a prefix test here would sweep up any variant a later patch hangs off the same folder.
    /// See Bugged.
    /// </summary>
    private const string Boss = "Metadata/Monsters/VaalBossStatue/VaalStatueBossSTANDALONEExpedition";

    /// <summary>
    /// What was said in each area recently left, by area hash, oldest first. A portal to town and back
    /// returns to the same instance under the same hash, and without this the marks would be wiped on the
    /// way out and the value threshold would make its offer again on the way back - re-marking a remnant
    /// somebody had unmarked by hand, and forgetting every must avoid.
    /// </summary>
    private readonly List<(uint Area, AreaMarks Marks)> _areasLeft = new();

    /// <summary>How many areas' marks are kept for a return. A town trip needs one; the rest is slack.</summary>
    private const int AreasRemembered = 8;

    /// <summary>One area's marks and threshold offers, held while the player is elsewhere. See _areasLeft.</summary>
    private sealed record AreaMarks(
        Dictionary<(int X, int Y), Said> Cells,
        HashSet<(int X, int Y)> Offered,
        HashSet<(int X, int Y)> OfferedAfterRoll,
        HashSet<(int X, int Y)> ForReward,
        (int X, int Y)? TakenLast);

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _areasLeft.RemoveAll(x => x.Area == _area);

            if (_cells.Count > 0 || _offered.Count > 0 || _offeredAfterRoll.Count > 0)
            {
                _areasLeft.Add((_area, new AreaMarks(new(_cells), new(_offered), new(_offeredAfterRoll),
                    new(_forReward), _takenLast)));

                if (_areasLeft.Count > AreasRemembered)
                    _areasLeft.RemoveAt(0);
            }

            _cells.Clear();
            _offered.Clear();
            _offeredAfterRoll.Clear();
            _forReward.Clear();
            _takenLast = null;

            var back = _areasLeft.FindIndex(x => x.Area == areaHash);

            if (back >= 0)
            {
                var marks = _areasLeft[back].Marks;

                foreach (var (cell, said) in marks.Cells)
                    _cells[cell] = said;

                _offered.UnionWith(marks.Offered);
                _offeredAfterRoll.UnionWith(marks.OfferedAfterRoll);
                _forReward.UnionWith(marks.ForReward);
                _takenLast = marks.TakenLast;
                _areasLeft.RemoveAt(back);
            }
        }

        _area = areaHash;
    }

    /// <summary>
    /// What has been insisted on, and whether the plan on screen actually takes it.
    ///
    /// **Marking a marker and re-solving gives almost no visible sign that anything happened.** The
    /// weight it adds is enormous and deliberately excluded from the score a person reads, so a
    /// chain that now reaches it and one that still cannot both show the same number. The only
    /// honest answer is to say, per marker, whether it is in the plan - and if it is not, that is
    /// the interesting case, because it means no legal chain could get there.
    /// </summary>
    public string Describe(Plan plan = null)
    {
        if (_cells.Count == 0)
            return "nothing marked as must take";

        var said = new List<string>();

        foreach (var (cell, what) in _cells)
        {
            said.Add($"({cell.X},{cell.Y}) {what.ToString().ToLowerInvariant()}" +
                     (_takenLast == cell ? " and TAKEN LAST" : "") + " " +
                     Taken(plan, cell.X, cell.Y));

            if (said.Count == 8)
                break;
        }

        return $"{_cells.Count}: {string.Join(", ", said)}" +
               (_cells.Count > said.Count ? ", ..." : "");
    }

    /// <summary>
    /// Whether the site could take every must-take at all, against whether the search found a way.
    ///
    /// **A chain that drops one is either refused by the geometry or missed by the search, and the
    /// two want opposite work.** One is a site the player has over-marked; the other is a search
    /// fault. The dump used to assert the first on every invalid chain without checking, and a
    /// Craggy Peninsula site with 428 grid of slack was read as impossible because of it.
    ///
    /// The bound is the cheapest straight line that visits them all, starting at the detonator -
    /// nearest-first, which is a lower bound on the real route rather than the best order, and a
    /// lower bound is what the argument needs. Against it sits the whole chain laid end to end,
    /// every link at full reach, plus one blast radius because a link catches from that far off.
    ///
    /// **Both sides are generous to the site on purpose.** The straight line ignores terrain and
    /// the budget ignores spacing, so a bound that still exceeds the budget is a real refusal and
    /// not a modelling artefact. Under it, nothing is proved either way - the route may bend past
    /// what the budget covers - so it says the search did not find one and stops there.
    /// </summary>
    public string Bound(PlanEnvironment env)
    {
        if (env == null)
            return "no environment to measure against";

        var wanted = new List<Vector2>();

        foreach (var (cell, _) in _cells)
            wanted.Add(new Vector2(cell.X, cell.Y));

        if (wanted.Count == 0)
            return "nothing marked, so nothing to reach";

        var at = env.Origin;
        var run = 0f;

        while (wanted.Count > 0)
        {
            var pick = 0;

            for (var i = 1; i < wanted.Count; i++)
                if (Vector2.Distance(at, wanted[i]) < Vector2.Distance(at, wanted[pick]))
                    pick = i;

            run += Vector2.Distance(at, wanted[pick]);
            at = wanted[pick];
            wanted.RemoveAt(pick);
        }

        var budget = env.Explosives * env.Reach + env.Blast;

        return run > budget
            ? $"the site cannot take them: {run:N0} grid to visit them all from the detonator, " +
              $"against {budget:N0} of chain ({env.Explosives} links x {env.Reach:0} reach + " +
              $"{env.Blast:0} blast) - and that is the straight line, so the real route is longer"
            : $"the site could take them: {run:N0} grid to visit them all from the detonator, " +
              $"against {budget:N0} of chain ({env.Explosives} links x {env.Reach:0} reach + " +
              $"{env.Blast:0} blast), {budget - run:N0} spare - so THE SEARCH DID NOT FIND IT " +
              "rather than the site refusing it";
    }

    /// <summary>
    /// Which blast of the plan catches this marker, counting from one.
    ///
    /// Zero for none, and MINUS ONE for "nobody has worked it out yet" - which is not the same thing
    /// and was being reported as though it were. A chain published while the search is still running
    /// carries no catch list, because what each blast catches is settled when the search commits;
    /// read as "no blast catches it", that drew a red NOT REACHED over a marker sitting plainly
    /// inside a circle on screen.
    /// </summary>
    public static int TakenBy(Plan plan, Vector2 grid)
    {
        if (plan is not { Points.Count: > 0 })
            return 0;

        if (plan.Catches == null)
            return -1;

        var cell = Cell(grid);

        for (var i = 0; i < plan.Points.Count; i++)
        {
            foreach (var caught in plan.CaughtBy(i))
            {
                if (Math.Abs((int)MathF.Round(caught.X) - cell.X) <= 1 &&
                    Math.Abs((int)MathF.Round(caught.Y) - cell.Y) <= 1)
                    return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Which blast of the plan catches this cell, in the words the circles use.</summary>
    private static string Taken(Plan plan, int x, int y)
    {
        if (plan is not { Points.Count: > 0 })
            return "- no plan";

        for (var i = 0; i < plan.Points.Count; i++)
        {
            foreach (var caught in plan.CaughtBy(i))
            {
                if (Math.Abs((int)MathF.Round(caught.X) - x) <= 1 &&
                    Math.Abs((int)MathF.Round(caught.Y) - y) <= 1)
                    return $"TAKEN by blast #{i + 1}";
            }
        }

        return "NOT TAKEN";
    }

    /// <summary>
    /// The marker nearest the cursor, or null when the cursor is not near one.
    ///
    /// Screen space rather than grid space, because the cursor is a screen thing and the map is
    /// drawn at an angle - two markers a long way apart on the ground can be a few pixels apart on
    /// screen, and it is the pixels the player is aiming with.
    /// </summary>
    /// <param name="within">How near, in pixels. Generous: the marker art is bigger than its cell.</param>
    public static Target Under(GameController gc, IReadOnlyList<Target> targets, float within = 90f)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null || targets == null)
            return null;

        var mouse = new Vector2(
            Safe.Read(gc, static g => g.IngameState.MousePosX, 0f),
            Safe.Read(gc, static g => g.IngameState.MousePosY, 0f));

        if (mouse == Vector2.Zero)
            return null;

        Target best = null;
        var closest = within * within;

        foreach (var target in targets)
        {
            var world = target.Where(gc);

            if (world == Vector3.Zero)
                continue;

            var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
                Vector2.Zero);

            if (at == Vector2.Zero)
                continue;

            var gap = Vector2.DistanceSquared(at, mouse);

            if (gap >= closest)
                continue;

            closest = gap;
            best = target;
        }

        return best;
    }

    private static (int X, int Y) Cell(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));
}
