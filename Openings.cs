using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// The first few links of a chain, enumerated and ranked by what a whole chain from them scores.
///
/// **A greedy opening is a trap most of the pool falls into, and it falls into the same one.** Measured on a
/// Grand site: four workers opened east and scored 6,858 to 9,510, four opened west and scored 6,488 to
/// 6,712 - and the three that opened at the same western spot stayed together for all fifteen links, one
/// identical chain found three times. Greedy scores a first link by what it catches, and what a first link is
/// for is taking ground towards a second, so every worker reasoning that way reaches the same wrong answer.
///
/// **Two failed designs are recorded here, because each failed for a reason worth keeping.**
///
/// *Dominance pruning* - drop an opening when another catches everything it catches and reaches everything it
/// reaches - is elegant and useless at this scale. On several hundred markers with overlapping blast discs
/// almost every spot catches something uniquely, so almost nothing is dominated: "pruning" returned hundreds
/// of survivors, including ones whose whole value was three normal monsters.
///
/// *Farthest-point selection*, added to cap those survivors without a coverage bias, made it worse. The most
/// distant parts of a dig site are the emptiest, so a filter built to avoid clustering preferred corners.
///
/// **What replaces both rests on an observation about the ground.** Two candidate spots within a blast radius
/// of each other catch nearly the same content, so one of them represents that area - which turns a few
/// hundred candidates into a few dozen areas, spread over the map by construction rather than by a selection
/// rule. Each representative is then judged by completing the chain greedily and scoring it: not a proxy for
/// the objective but the objective, measured on a chain that exists. Every static measure tried - coverage,
/// and coverage plus reachable frontier - ranked by local content density and pointed every opening into the
/// wrong half of the site.
///
/// Nothing consults this unless a worker's role asks for an enumerated opening. See SolverSettings.ThreadRoles.
/// </summary>
internal static class Openings
{
    /// <param name="Links">The prefix itself, from the first link onwards.</param>
    /// <param name="Caught">What the prefix catches, in weight. Shown because it is what greedy ranks by.</param>
    /// <param name="Completed">What a whole chain built greedily from this prefix scores. The ranking.</param>
    /// <param name="Branch">The heavy content the first link catches, as a key. See BranchOfOpening.</param>
    /// <param name="Variation">The heavy content the second link catches, as a key. See BranchOfOpening.</param>
    /// <param name="KeptFor">
    /// For an opening of one link, why the first bomb was kept: each next area it is the best placement towards, or
    /// "richest". Empty for longer openings. See BestTowardEachNextArea.
    /// </param>
    internal readonly record struct Opening(List<Vector2> Links, double Caught, double Completed,
        string Branch = "", string Variation = "", string KeptFor = "")
    {
        public double Worth => Completed;
    }

    /// <summary>What the last generation produced, best first, for the window.</summary>
    public static IReadOnlyList<Opening> Last => _last;

    /// <summary>
    /// How many openings one level of the enumeration made at each stage, for the chain panel.
    /// </summary>
    /// <param name="Level">The link this level places, from one.</param>
    /// <param name="Prefixes">How many openings from the level before were extended. One at level one.</param>
    /// <param name="Discarded">Placements the two prunes threw away as beaten on content and distance.</param>
    /// <param name="ForksLost">Of those, how many were the only way to heavy content. See ForkTally.</param>
    /// <param name="Offered">Placements that survived the prunes and were offered for rolling out.</param>
    /// <param name="RolledOut">How many of those were completed and scored.</param>
    /// <param name="NotRolledOut">Offered but never scored, because the level ran out of rollouts.</param>
    /// <param name="Kept">How many openings the ranking kept for the next level, or as the result.</param>
    /// <param name="Dominated">Openings dropped because another does everything they do. See WithoutDominated.</param>
    internal readonly record struct LevelCounts(int Level, int Prefixes, int Discarded, int ForksLost,
        int Offered, int RolledOut, int NotRolledOut, int Kept, int Dominated = 0);

    /// <summary>What each level of the last generation made. See LevelCounts.</summary>
    public static IReadOnlyList<LevelCounts> LastLevels => _lastLevels;

    private static List<LevelCounts> _lastLevels = new();

    /// <summary>The lost forks of the last generation, as placements, for the chain panel to draw.</summary>
    public static IReadOnlyList<ForkTally.LostFork> LastForks => _lastForks;

    private static List<ForkTally.LostFork> _lastForks = new();

    private static List<Opening> _last = new();

    /// <summary>What the last generation did, in words, for the window and the dump.</summary>
    public static string Said { get; private set; } = "not generated";

    /// <summary>What the last real enumeration said, repeated for the presses that reuse its answer.</summary>
    private static string _held = "not generated";

    /// <summary>
    /// How many chains may be completed per level, whatever the ground offers.
    ///
    /// A rollout is a greedy build over the whole candidate set, so it is the only expensive thing here. Raised
    /// from sixty four when the spots offered per anchor went from three to six: the ring is now covered and
    /// ranked by completion rather than reduced by a distance rule, and a budget that cuts the list before it is
    /// scored reinstates the bias the ring sampling exists to remove.
    /// </summary>
    private const int Rollouts = 256;

    /// <summary>
    /// Enumerates openings to <paramref name="levels"/> links, keeping the best <paramref name="want"/>.
    /// </summary>
    /// <summary>
    /// What the openings held in <see cref="Last"/> were worked out from, so a site is not enumerated twice.
    ///
    /// **Measured in game at 1,608ms, against 232ms for the same work offline.** Offline a terrain question is a
    /// dictionary lookup; in game it routes a wire, and this asks about fourteen thousand cells. That is a fifth
    /// of an eight second window, spent before any worker starts, out of the window they then share - and it was
    /// invisible, because the dump's "spent opening" figure is the worker's own construction and never included
    /// this.
    ///
    /// The openings depend on the ground and the content, not on the draw, so every press on a site was paying
    /// it again for an identical answer. The key is what they depend on: where the detonator is, how far and how
    /// wide the explosives reach, how many there are, and what the content is. A reroll changes a remnant's
    /// weight and so changes the key, which is the case that must not be missed.
    /// </summary>
    private static (int Targets, int Content, float Origin, float Reach, float Blast, int Explosives, int Levels,
        int Want, int Horizon) _from;

    /// <summary>What a set of openings would have to be worked out from again. See _from.</summary>
    private static (int, int, float, float, float, int, int, int, int) Key(PlanEnvironment env, int levels,
        int want, int horizon)
    {
        // A cheap digest of the content rather than the content itself: a rolled weight, a position and a
        // kind per marker, which is what any of these openings can depend on.
        //
        // **Order-independent, because the scan does not promise an order.** Combined with a rolling multiply
        // the digest changed every press although the site had not, so the key never matched and the work was
        // done again every time - a cache that costs its own lookup and saves nothing. Adding per-marker
        // hashes cannot tell two orderings apart, which is the property wanted here.
        var content = 0;

        foreach (var target in env.Targets)
        {
            var one = target.Grid.GetHashCode();

            one = one * 31 + target.Weight.GetHashCode();
            one = one * 31 + (int)target.Kind;
            one = one * 31 + target.Carries.GetHashCode();
            one = one * 31 + target.Waves.GetHashCode();

            content = unchecked(content + one);
        }

        return (env.Targets.Count, content, env.Origin.X * 7919f + env.Origin.Y, env.Reach, env.Blast,
            env.Explosives, levels, want, horizon);
    }

    public static void Generate(PlanEnvironment env, int levels, int want = 8, int horizon = 3)
    {
        // **One at a time.** The solve runs this on its own background task, so a presolve pass that starts
        // while the last one is still enumerating would run two of these over the same statics. The second
        // waits for the first and then, usually, finds its answer already in _last.
        lock (Gate)
        {
            _generating = true;

            try
            {
                GenerateOneAtATime(env, levels, want, horizon);
            }
            finally
            {
                _generating = false;
            }
        }
    }

    /// <summary>Whether an enumeration is running right now. See Spent.LongGaps.</summary>
    public static bool Generating => System.Threading.Volatile.Read(ref _generating);

    private static bool _generating;

    /// <summary>Serialises Generate. See Generate.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Stopwatch ticks the last enumeration spent in ground checks and in rollouts, for its Said line.
    ///
    /// **Because the same enumeration took 1.9s on the game's thread and 5.4 to 6.3s on the solve's**, asking the
    /// ground about as many times, and the total alone cannot say which part slowed or why. Written only under
    /// Gate. See PlaceableFrom and Finished.
    /// </summary>
    private static long GroundTicks;

    /// <summary>See GroundTicks.</summary>
    private static long RolloutTicks;

    /// <summary>Stopwatch ticks as milliseconds.</summary>
    private static double Ms(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Whether an explosive may stand at this cell and be reached from that one, timed. See GroundTicks.</summary>
    private static bool PlaceableFrom(PlanEnvironment env, Vector2 from, Vector2 at)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var (directBefore, snapBefore, searchesBefore, ruledOutBefore) =
            (Terrain.DirectAimTicks, Terrain.SnapSearchTicks, Terrain.SnapSearches, Terrain.ClampRuledOut);

        try
        {
            var placing = System.Diagnostics.Stopwatch.GetTimestamp();
            var allowed = env.CanPlace(at);

            PlacementTestTicks += System.Diagnostics.Stopwatch.GetTimestamp() - placing;

            return allowed && Planner.Says(env, from, at) == Certainty.Yes;
        }
        finally
        {
            GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
            DirectAimTicksHere += Terrain.DirectAimTicks - directBefore;
            SnapSearchTicksHere += Terrain.SnapSearchTicks - snapBefore;
            SnapSearchesHere += Terrain.SnapSearches - searchesBefore;
            ClampRuledOutHere += Terrain.ClampRuledOut - ruledOutBefore;
        }
    }

    /// <summary>
    /// Of GroundTicks, the ticks in the placement test, in routing a direct aim, and in the clamp search, with how many
    /// clamp searches ran - so a slow enumeration says which part of asking the ground was slow. Written under Gate.
    /// </summary>
    private static long PlacementTestTicks;

    /// <summary>See PlacementTestTicks.</summary>
    private static long DirectAimTicksHere;

    /// <summary>See PlacementTestTicks.</summary>
    private static long SnapSearchTicksHere;

    /// <summary>See PlacementTestTicks.</summary>
    private static int SnapSearchesHere;

    /// <summary>Clamp searches the route length made unnecessary. See Terrain.Aiming.</summary>
    private static int ClampRuledOutHere;

    private static void GenerateOneAtATime(PlanEnvironment env, int levels, int want, int horizon)
    {
        // **Once per site, not once per press.** See _from for what this costs and why the answer cannot differ
        // between two presses of the same ground.
        var key = Key(env, levels, want, horizon);

        if (_last.Count > 0 && key.Equals(_from))
        {
            Repair.ForgetOpeningTallies();

            // Says which press paid, so the cost is not read as being paid again.
            Said = $"{_held} - REUSED, this press worked nothing out";

            return;
        }

        _from = key;

        // The keep-opening tally is per solve and this runs once per solve, before any worker. See Repair.KeepOpeningSaid.
        Repair.ForgetOpeningTallies();

        // **What this costs, because nothing on the dump was saying.** It runs before the workers start, out of
        // the same window they then share, and every terrain question in it is a wire routing against the same
        // router the reach operator uses. Timed offline it is 145ms, and offline a terrain question is a
        // dictionary lookup - so the offline figure says nothing about the figure that matters.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Asked = 0;
        AskedForCatching = 0;
        AskedForApproach = 0;
        AskedOnRings = 0;

        _last = new List<Opening>();
        _lastLevels = new List<LevelCounts>();
        _lastForks = new List<ForkTally.LostFork>();
        Said = "nothing to enumerate";
        want = Math.Max(1, want);

        if (env?.Targets == null || env.Targets.Count == 0)
            return;

        GroundTicks = 0;
        RolloutTicks = 0;
        PlacementTestTicks = 0;
        DirectAimTicksHere = 0;
        SnapSearchTicksHere = 0;
        SnapSearchesHere = 0;
        ClampRuledOutHere = 0;
        _whyFirstBombKept.Clear();
        ForkTally.Forget();

        var candidating = System.Diagnostics.Stopwatch.GetTimestamp();
        var candidates = Planner.Candidates(env, out _, out _);
        var candidateTicks = System.Diagnostics.Stopwatch.GetTimestamp() - candidating;

        if (candidates is not { Count: > 0 })
            return;

        levels = Math.Max(1, Math.Min(levels, Math.Max(1, env.Explosives)));

        var caught = new Dictionary<Vector2, double>();
        var rolled = 0;
        var levelCounts = new List<LevelCounts>();

        // Level one: every area the detonator can reach, one representative apiece.
        var live = new List<(List<Vector2> Links, double Caught, double Completed)>();
        var (discardedBefore, lostBefore) = ForkTally.Counts();
        var offered = Anchored(env, candidates, env.Origin, [], caught);

        _dominatedByChoices = 0;

        if (levels > 1)
        {
            // **Carried forward whole, not ranked, when a second link follows.** A one link rollout kept one first bomb
            // per area, whichever finished best three links out - on Scorched Cay (1147,541) for the remnant at
            // (1132,574) - so the spot a player opened on, (1101,594), facing the next area, was offered and never
            // extended. What Anchored offers is already one placement per area per direction the chain can take, so
            // how many first bombs an area gets is decided by how many ways out it has, and the rollouts are spent on
            // the two link openings, where they decide something.
            foreach (var at in offered)
                live.Add((new List<Vector2> { at }, caught[at], caught[at]));
        }
        else
        {
            foreach (var at in offered)
            {
                if (rolled >= Rollouts)
                    break;

                rolled++;

                var links = new List<Vector2> { at };

                live.Add((links, caught[at], Finished(env, candidates, links, horizon)));
            }

            // **Every first bomb the two link enumeration would carry forward, not the best of each area.** A one link
            // run is how the first bombs are looked at, and Choices collapsed them to one per area - hiding exactly the
            // choice between them. They are already placed with the second bomb in mind, one per direction the chain
            // can take, and KeptFor says which. Ranked by the rollout, which decides nothing here.
            live.Sort((a, b) => b.Completed.CompareTo(a.Completed));
        }

        var (discardedAfter, lostAfter) = ForkTally.Counts();

        levelCounts.Add(new LevelCounts(1, 1, discardedAfter - discardedBefore, lostAfter - lostBefore,
            offered.Count, rolled, levels > 1 ? 0 : offered.Count - rolled, live.Count, _dominatedByChoices));

        // Each further level extends the survivors, one representative per area again, and re-ranks.
        for (var level = 1; level < levels; level++)
        {
            var next = new List<(List<Vector2> Links, double Caught, double Completed)>();
            var spent = 0;
            var offeredHere = 0;

            (discardedBefore, lostBefore) = ForkTally.Counts();

            var last = level == levels - 1;
            var grownAll = new List<(List<Vector2> Links, double Caught)>();

            foreach (var (links, was, _) in live)
            {
                var extensions = Anchored(env, candidates, links[^1], links, caught);

                offeredHere += extensions.Count;

                foreach (var at in extensions)
                    grownAll.Add((new List<Vector2>(links) { at }, was + caught[at]));
            }

            // **At the last link, one rollout per prefix and area it goes to.** Every extension was rolled out, and
            // with every prefix given its full set that was 285 on a 379 marker Grand site - past the 256 the limit
            // allows, so the last prefixes' extensions were never scored, and five seconds of rollouts in game. The
            // richest extension of each prefix towards each area is what is rolled out: 66 on that site, with the same
            // openings kept. One per pair of areas was tried first and lost them - richest by content over the two
            // links chose worse first bombs, the very choice between the closest and the richest spot that the prefix
            // stands for. Earlier links, where a longer enumeration has more than two, are still rolled out in full.
            var toRoll = last ? RichestPerPrefixAndArea(env, grownAll) : grownAll;

            foreach (var (grown, took) in toRoll)
            {
                if (spent >= Rollouts)
                    break;

                spent++;
                rolled++;

                next.Add((grown, took, Finished(env, candidates, grown, horizon)));
            }

            (discardedAfter, lostAfter) = ForkTally.Counts();

            var prefixes = live.Count;

            _dominatedByChoices = 0;

            if (next.Count > 0)
                live = Choices(env, next, want, caught);

            levelCounts.Add(new LevelCounts(level + 1, prefixes, discardedAfter - discardedBefore,
                lostAfter - lostBefore, offeredHere, spent, toRoll.Count - spent, next.Count > 0 ? live.Count : 0,
                _dominatedByChoices));

            if (next.Count == 0)
                break;
        }

        // Built aside and published whole, because the dump and the chain panel read Last from the game's
        // thread while this runs on the solve's.
        var made = new List<Opening>();
        var heavyForBranches = HeavyTargetsOfSite(env, 0);

        foreach (var (links, was, completed) in live)
        {
            var (branch, variation) = BranchOfOpening(env, heavyForBranches, links);

            made.Add(new Opening(links, was, completed, branch, variation,
                links.Count == 1 && _whyFirstBombKept.TryGetValue(links[0], out var why) ? string.Join("; ", why) : ""));
        }

        _last = made;
        _lastLevels = levelCounts;
        _lastForks = ForkTally.Lost();
        ForksSaid = ForkTally.Said();

        Said = _held =
            $"{_last.Count} distinct opening(s) of {levels} link(s), from {rolled:N0} chain(s) built to " +
            $"{horizon} link(s) in {clock.ElapsedMilliseconds:N0}ms, asking the ground {Asked:N0} time(s) " +
            $"[{AskedForCatching:N0} catching, {AskedForApproach:N0} approaching, {AskedOnRings:N0} on rings] " +
            $"(ground checks {Ms(GroundTicks):N0}ms [placement test {Ms(PlacementTestTicks):N0}ms, direct aim " +
            $"{Ms(DirectAimTicksHere):N0}ms, clamp search {Ms(SnapSearchTicksHere):N0}ms over {SnapSearchesHere:N0}, " +
            $"{ClampRuledOutHere:N0} ruled out by the route length], " +
            $"rollouts {Ms(RolloutTicks):N0}ms, candidates " +
            $"{Ms(candidateTicks):N0}ms; {Planning.RunningAtEnumeration} worker(s) of an earlier search still running " +
            "when it began) - " +
            $"anchored on heavy content, then grouped by where they lead. Worked out for " +
            $"[{_from.Targets} marker(s), content {_from.Content}, from {_from.Origin:0.###}, " +
            $"reach {_from.Reach:0.###}, blast {_from.Blast:0.###}, {_from.Explosives} explosive(s), " +
            $"{_from.Levels}/{_from.Want}/{_from.Horizon}]";
    }

    /// <summary>
    /// A candidate opening for each piece of heavy content in reach, placed to take it and as much else as
    /// possible.
    ///
    /// **This generates candidates; Choices decides how many openings a site actually has.** Heavy content is
    /// where the score is, so a first bomb that reaches none of it is a wasted bomb and the spots that reach it
    /// are the ones worth rolling out. On the Basin site that produced two - the northern remnant with three
    /// monster markers, the southern with seven - but two is what that ground happened to offer, and treating
    /// it as one opening per anchor is what an earlier version did wrong.
    ///
    /// This replaced a grid of blast-sized areas, which had the same fault one level down: a dig site has
    /// dozens of areas and most of them hold nothing, so the list filled with openings whose entire value was
    /// three normal monsters while the remnants went unmentioned.
    ///
    /// **An anchor is whatever is worth most, which is not the same as whatever is a remnant.** A relic
    /// carrying an increase across the rest of the chain can outweigh a remnant, and an empty remnant can be
    /// outweighed by a strongbox, so the ranking is by weight plus propagation and no kind is named. See
    /// HeavyTargetsOfSite.
    ///
    /// **A link that can reach no heavy content aims at it instead.** Whichever reachable spot closes the most
    /// ground towards an uncaught heavy target is offered as that target's corridor, provided it closes some -
    /// a spot no nearer than the chain already is would run the opening backwards. Without this the enumeration
    /// stopped at one link on a site where only one remnant is within a bomb's reach of the detonator, because
    /// the second link had nothing catchable in front of it and so no candidates at all.
    ///
    /// **The position for an anchor is decided in two steps, and the second is the one greedy has no answer
    /// for.** Among the spots that catch the anchor, take the most content; among those that take within a
    /// whisker of the most, take the one closest to the next heavy thing the chain has not got yet. That is
    /// "optimally pathing to the next heavy content" as a rule rather than an instinct, and it is why two spots
    /// catching identical content are not equal.
    /// </summary>
    private static List<Vector2> Anchored(PlanEnvironment env, List<Vector2> candidates, Vector2 from,
        IReadOnlyList<Vector2> already, Dictionary<Vector2, double> caught)
    {
        var heavy = HeavyTargetsOfSite(env, already.Count);

        if (heavy.Count == 0)
            return [];

        // Asks, of every placement the prunes below discard, whether it could have reached heavy content the
        // placement that beat it cannot. Measurement only; it changes nothing that is kept. See ForkTally.
        var forks = new ForkTally(env, heavy, candidates, already);

        // What the chain is heading towards, which is a higher bar than what it may open on. See
        // SubstantialTargetsOfSite.
        var ahead = SubstantialTargetsOfSite(env, already.Count);

        // The best spot for each anchor, and the runners-up close enough to it that the tie-break decides.
        // Every spot that catches each anchor, kept rather than reduced as it goes, because three different
        // questions are asked of the same list afterwards. A few dozen entries per anchor at most.
        var catching = new Dictionary<int, List<(Vector2 At, double Caught, double Toward)>>();

        // And for heavy content nothing in reach can catch, the spot that gets closest to it. Measured on the
        // Basin snapshot: from the detonator at (1037,567) with reach 108 and blast 34.8, exactly one remnant
        // is close enough for a bomb to catch - so without this, a site with ten remnants offered one opening
        // one link long, because the second link had nothing catchable in front of it and produced no
        // candidates at all. A link that reaches no heavy content is not a wasted link, it is the corridor.
        var approach = new Dictionary<int, (Vector2 At, double Left, double Caught)>();

        // **The ground is asked only about spots that catch something, and about approach spots in order of how
        // close they get.** It was asked about every candidate in reach that caught or merely approached anything
        // heavy - on a dense Grand site that is nearly every spot in reach, about 1,400 questions a prefix, at about
        // a fifth of a millisecond each - and all an approach is used for is the one nearest placeable spot per
        // target nothing in reach can catch. Approach candidates are gathered by arithmetic here and resolved below,
        // after the rings, asking the ground nearest first until one stands.
        var approachable = new Dictionary<int, List<(Vector2 At, double Left, double Worth)>>();
        var standing = new Dictionary<Vector2, bool>();

        bool PlaceableHere(Vector2 at)
        {
            if (standing.TryGetValue(at, out var known))
                return known;

            Asked++;

            var ok = PlaceableFrom(env, from, at);

            foreach (var had in already)
                ok &= Vector2.Distance(had, at) >= env.Apart;

            standing[at] = ok;

            return ok;
        }

        bool AskedAboutCatching(Vector2 at)
        {
            if (!standing.ContainsKey(at))
                AskedForCatching++;

            return PlaceableHere(at);
        }

        foreach (var at in candidates)
        {
            if (Planner.Span(from, at) > env.Reach)
                continue;

            var worth = -1d;
            var catchesAny = false;

            for (var i = 0; i < heavy.Count; i++)
            {
                if (Caught(already, heavy[i], env))
                    continue;

                var reach = env.Blast + heavy[i].Radius;

                if (Vector2.DistanceSquared(at, heavy[i].Grid) <= reach * reach)
                {
                    catchesAny = true;

                    continue;
                }

                // Out of reach of this one, so the question becomes how much of the distance it closes. Only a spot
                // that gets nearer than the chain already is, or the corridor would run backwards.
                var left = Vector2.Distance(at, heavy[i].Grid);

                if (left >= Vector2.Distance(from, heavy[i].Grid))
                    continue;

                if (worth < 0d)
                    worth = Catches(env, at);

                if (!approachable.TryGetValue(i, out var list))
                    approachable[i] = list = [];

                list.Add((at, left, worth));
            }

            if (!catchesAny)
                continue;

            // Not asked yet: a catching spot is only asked about when the selection below reaches it. See
            // BestTowardEachNextArea.
            if (worth < 0d)
                worth = Catches(env, at);

            var toward = Toward(env, ahead, already, at, from);

            for (var i = 0; i < heavy.Count; i++)
            {
                if (Caught(already, heavy[i], env))
                    continue;

                var reach = env.Blast + heavy[i].Radius;

                if (Vector2.DistanceSquared(at, heavy[i].Grid) > reach * reach)
                    continue;

                if (!catching.TryGetValue(i, out var spots))
                    catching[i] = spots = [];

                spots.Add((at, worth, toward));
            }
        }

        // **Every anchor's ring, not only the anchors the generator already reached.** Sampling the rings of
        // anchors that had a spot leaves out the case the sampling exists for: an anchor whose pair geometry put
        // nothing in reach has no entry here, so its ring is never walked and it drops out of the openings
        // altogether. On the Basin snapshot the chest at (1115,678) is that case, and (1145,699) - a second bomb
        // sitting exactly on its ring, taking the chest and the relic behind it - is reachable from the northern
        // opening and offered by nothing.
        for (var i = 0; i < heavy.Count; i++)
        {
            if (Caught(already, heavy[i], env))
                continue;

            // Beyond a bomb's reach of this link even at the ring's nearest point: no spot here can catch it.
            var radius = env.Blast + heavy[i].Radius;

            if (Planner.Span(from, heavy[i].Grid) > env.Reach + radius)
                continue;

            if (!catching.TryGetValue(i, out var spots))
                catching[i] = spots = [];

            RingSpotsOfAnchor(env, heavy[i], from, already, ahead, spots, askGround: false);

            // An anchor whose whole ring is unreachable or unplaceable leaves nothing, and an empty entry
            // would offer three copies of a spot that does not exist.
            if (spots.Count == 0)
                catching.Remove(i);
        }

        // The approaches, for heavy content nothing in reach catches: nearest first, asking the ground until one
        // stands, then any within a whisker of it that takes more content. See the gathering above.
        foreach (var (i, list) in approachable)
        {
            if (catching.ContainsKey(i))
                continue;

            list.Sort((a, b) => a.Left.CompareTo(b.Left));

            var found = -1;

            for (var k = 0; k < list.Count; k++)
            {
                if (found >= 0 && list[k].Left >= list[found].Left * Whisker)
                    break;

                AskedForApproach += standing.ContainsKey(list[k].At) ? 0 : 1;

                if (!PlaceableHere(list[k].At))
                    continue;

                if (found < 0 || list[k].Worth > list[found].Worth)
                    found = k;
            }

            if (found >= 0)
                approach[i] = (list[found].At, list[found].Left, list[found].Worth);
        }

        var openings = new List<Vector2>(catching.Count * 3);
        var offeredHere = new HashSet<Vector2>();

        // **Once per call, not once per enumeration.** The test was whether the spot had ever been offered, against a
        // dictionary shared by every prefix and every link - so a spot one prefix offered could never be offered to
        // another, and prefixes extended later found the spots they needed already taken. On Scorched Cay a three bomb
        // run kept (929,523) then (953,676) at the second link and gave it no third bomb at all, which is why one
        // opening starting at the remnant at (921,487) survived where two had at two bombs. What a spot catches does
        // not depend on the prefix, so the shared value is simply written again.
        void Offer(Vector2 at, double worth)
        {
            if (!offeredHere.Add(at))
                return;

            caught[at] = worth;
            openings.Add(at);
        }

        // **Every anchor's three points, then the ones beaten on both content and reach thrown away.**
        //
        // The two terms are comparable here and only here: every point in this list is a placement for the same
        // link, from the same spot, so the content it catches and the share of the reach it spends are measured
        // against the same thing. A point another beats on both is not a different plan - it takes less and ends
        // nearer. Measured on the Basin snapshot, second bombs from (1139,604): (1097,664) takes 97 and spends
        // 72.5, against (1149,691) taking 107 and spending 87; the relic's own cell (1121,691) takes 97 and
        // spends 88.3, against (1145,699) taking 104 and spending 94.7. Both were offered and neither was worth
        // reading, which is what filled the list with variations that looked alike.
        //
        // What survives is what no other point beats outright: the fullest, the furthest, and the trades in
        // between - including the ones that give up content the others keep.
        var offers = new List<(Vector2 At, double Caught, double Ahead, string Anchors)>();

        foreach (var spots in catching.Values)
        {
            foreach (var (at, worth) in CapturePointsOfAnchor(env, from, candidates, already, spots, forks,
                         at => AskedAboutCatching(at)))
                offers.Add((at, worth, Planner.Span(from, at), AnchorsAt(env, heavy, at)));
        }

        // **The corridors go through the same prune**, because they are placements for the same link from the
        // same spot and so are comparable with everything else. Offered after it instead, they were not pruned at
        // all: on the Basin snapshot (1097,664) and the relic's own cell (1121,691) are corridor spots towards
        // remnants further out, and both are beaten outright on content and reach by capture points in the list.
        //
        // **Only where no placement here catches a heavy area.** A corridor is what a link is when there is nothing
        // heavy in reach - the case these were written for - and offered beside real areas they filled the first
        // bombs on Scorched Cay with spots catching no remnant or relic at all.
        var areasHere = AreasOf(env);
        var catchesAnArea = offers.Any(o => areasHere.AreaCaughtAt(env, o.At) >= 0);

        foreach (var (i, (at, _, worth)) in approach)
        {
            if (!catching.ContainsKey(i) && !catchesAnArea)
            {
                offers.Add((at, worth, Planner.Span(from, at), AnchorsAt(env, heavy, at)));
            }
        }

        // **Only against placements competing for the same heavy content.** Two spots taking the same remnant are
        // two ways of doing one thing, and the one beaten is the worse way. Two spots taking DIFFERENT remnants are
        // different branches, and pruning across them deletes one: on the Basin snapshot the southern remnant takes
        // 241 with more in front of it than the northern one's 225, so a single comparison threw the whole northern
        // branch away - the same mistake a relative score floor made earlier in this file's history. Within a group
        // the test is the one the capture points face. See OnFrontTowardNextTargets.
        var nextAreas = NextAreasFrom(env, already, from);
        var areas = AreasOf(env);

        foreach (var group in offers.GroupBy(o => areas.AreaCaughtAt(env, o.At)))
        {
            var members = group.Select(o => (o.At, o.Caught)).ToList();
            var keep = BestTowardEachNextArea(env, from, members, nextAreas, forks,
                reasonFor: already.Count == 0 ? (i, why) => NoteWhyFirstBombKept(members[i].At, why) : null);

            for (var i = 0; i < members.Count; i++)
            {
                if (keep[i])
                    Offer(members[i].At, members[i].Caught);
            }
        }

        return openings;
    }

    /// <summary>
    /// Every whole cell on an anchor's catch ring that a bomb could sit on, added to the spots that catch it.
    ///
    /// **The best spot for an anchor is almost always on its ring, and the candidate generator does not offer
    /// the ring.** Planner.CandidatesInner offers a target's own cell, a ring around targets whose cell the
    /// ground refuses, and the two points where each PAIR of targets sits on the blast boundary - so the spots
    /// available around a placeable remnant are wherever its pair geometry happens to land. Measured on the
    /// Basin snapshot: five spots catch the northern remnant, the best of them leaves 80 grid to the next heavy
    /// content, and (1139,604) - on that remnant's ring, 107.9 of a 108 reach, catching the same 225 - leaves
    /// 78 and is offered by nothing. The cells just past it catch 245 and are out of reach, so the generator
    /// had no reason to produce the one in between.
    ///
    /// **Sampled here rather than in the generator, because the generator feeds every solve.** Its ring is
    /// twelve points, about nine grid apart at this radius, and the spots that matter differ by three - so
    /// fixing it there means thousands more candidates for every pass of every search. Here it is a few hundred
    /// cells around the handful of anchors an opening is built on, and nothing else pays for it.
    ///
    /// The inset is the half unit the generator uses for the same reason: a point exactly on the boundary
    /// rounds off the ring as often as onto it, and the marker the spot exists to catch is then missed.
    /// </summary>
    // Internal so the offline harness can show the ring and the three points it yields, rather than keeping
    // its own copy of a rule that could disagree. See tools/offline.
    /// <param name="askGround">
    /// False to gather the ring by arithmetic alone - in reach on the straight line and catching the anchor - and leave
    /// the ground to be asked only about the cells a selection reaches. Anchored does that; the offline harness asks
    /// about every cell, because it lists them all.
    /// </param>
    internal static void RingSpotsOfAnchor(PlanEnvironment env, PlanTarget anchor, Vector2 from,
        IReadOnlyList<Vector2> already, List<PlanTarget> heavy,
        List<(Vector2 At, double Caught, double Toward)> spots, bool askGround = true)
    {
        var radius = MathF.Max(1f, env.Blast + anchor.Radius - 0.5f);
        var steps = (int)MathF.Ceiling(MathF.Tau * radius);
        var seen = new HashSet<Vector2>();

        for (var step = 0; step < steps; step++)
        {
            var angle = step * MathF.Tau / steps;
            var on = anchor.Grid + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            var at = new Vector2(MathF.Round(on.X), MathF.Round(on.Y));

            if (!seen.Add(at))
                continue;

            if (askGround)
            {
                Asked++;
                AskedOnRings++;

                if (!PlaceableFrom(env, from, at))
                    continue;

                var apart = true;

                foreach (var had in already)
                    apart &= Vector2.Distance(had, at) >= env.Apart;

                if (!apart)
                    continue;
            }
            else if (Planner.Span(from, at) > env.Reach)
            {
                continue;
            }

            // The rounding can push a sample off the ring, so the catch is checked rather than assumed.
            var reach = env.Blast + anchor.Radius;

            if (Vector2.DistanceSquared(at, anchor.Grid) > reach * reach)
                continue;

            spots.Add((at, Catches(env, at), Toward(env, heavy, already, at, from)));
        }
    }

    /// <summary>
    /// Three spots for one piece of heavy content: the one taking the most, the one reaching furthest, and one
    /// balancing the two.
    ///
    /// **One spot per anchor answered one question and the ground asks three.** Among the spots that catch a
    /// given remnant, the richest takes every marker around it and leaves the chain where it started; the
    /// furthest gives up some of that content to end deep in the next corridor; and the useful one is usually
    /// neither. On the Basin snapshot a player picked (1145,699) as the second bomb - "skipping a few normal
    /// monster markers for a much stronger deeper position towards the next content" - and no single tie-break
    /// produces that, because it is not the best answer to either question on its own.
    ///
    /// So all three are offered and the rollout ranks them, which is the one comparison that can price content
    /// given up now against ground gained for later. The balance is the highest sum of the two shares - content
    /// as a fraction of the most any spot here takes, plus the closest approach as a fraction of this spot's -
    /// so it is scale-free and needs no weight chosen by hand.
    ///
    /// Duplicates are dropped by the caller, so an anchor whose richest spot is also its furthest yields one.
    /// </summary>
    /// <summary>
    /// Every placement on an anchor's ring that nothing else beats on both content taken and reach spent.
    ///
    /// **Two terms, and they are the only two that are comparable.** Every spot here is a placement for the same
    /// link from the same previous one, so what it catches and how much of the reach it spends are measured
    /// against the same thing. A spot another beats on both takes less and ends nearer, which is the same plan
    /// done worse. What is left is the trade-off curve: the fullest placement, the furthest, and every genuine
    /// exchange of content for depth in between.
    ///
    /// **This started as three picks - most content, most reach, and the balance of the two - which is how a
    /// player described what they wanted, and three was not enough.** On the Basin snapshot, second bombs from
    /// (1139,604): the fullest takes 114 and spends 59.8, the balance takes 104 and spends 94.7, and (1151,674)
    /// takes 111 and spends 70.4. Nothing beats that last one, a player picked it out as missing, and no choice of
    /// three points produces it - it is neither end of the curve nor the middle of it. The curve itself is the
    /// answer, and it is short: sparse ground round an anchor yields a handful of spots, dense ground yields more
    /// because there is genuinely more to decide.
    ///
    /// **Two earlier filters are gone and the history is worth keeping, because each looked reasonable.** Ranking
    /// the runners-up by distance to the nearest substantial content chose a spot completing to 480 over three
    /// links where the one it rejected reached 580, because the shallow spot never reaches the second remnant; and
    /// that distance is not a common scale anyway, since Toward leaves out whatever a spot itself catches, so a
    /// spot TAKING the relic reported 179 while one sitting 42 grid from the same relic untouched reported 42.
    /// A floor at seven tenths of the fullest spot's content then removed the genuine maximum-reach pick, which
    /// takes the relic alone - 4 against 111 - while spending 107.2 of a 108 reach.
    /// </summary>
    internal static List<(Vector2 At, double Caught)> CapturePointsOfAnchor(PlanEnvironment env, Vector2 from,
        List<Vector2> candidates, IReadOnlyList<Vector2> already,
        List<(Vector2 At, double Caught, double Toward)> spots, ForkTally forks = null,
        Func<Vector2, bool> placeable = null)
    {
        var points = new List<(Vector2 At, double Caught)>();
        var placements = new List<(Vector2 At, double Caught)>(spots.Count);

        foreach (var (at, worth, _) in spots)
            placements.Add((at, worth));

        var keep = BestTowardEachNextArea(env, from, placements, NextAreasFrom(env, already, from), forks, placeable);

        for (var i = 0; i < spots.Count; i++)
        {
            if (!keep[i])
                continue;

            var had = false;

            foreach (var (at, _) in points)
                had |= at == spots[i].At;

            if (!had)
                points.Add((spots[i].At, spots[i].Caught));
        }

        // Richest first, so a rollout budget that runs out spends what it had on the fullest plans.
        points.Sort((a, b) => b.Caught.CompareTo(a.Caught));

        return points;
    }


    /// <summary>
    /// The content worth opening on, which is whatever is worth most to the score rather than whatever is a
    /// remnant.
    ///
    /// **Kind is the wrong question and this asked it.** An earlier version took remnants and nothing else,
    /// because on the site it was written for the two remnants were the two decisions. A relic carrying an
    /// increase that applies to everything the rest of the chain digs up can be worth more than a remnant whose
    /// waves reach its own sockets, and a remnant with nothing in it can be worth less than a strongbox - so a
    /// list of kinds gets both of those wrong, and gets them wrong silently.
    ///
    /// **So the measure is the one greedy already uses:** a marker's own weight plus Rough, which is where a
    /// reward, a local rune and a carried increase are priced together against the explosives still to come.
    /// See Planner.Adds, which sums the same two terms per covered marker. It is an estimate - a carry scoped
    /// to chests is priced against the same downstream figure a flat one gets - and the rollout that ranks the
    /// openings afterwards is what decides whether the ground it opened was any good.
    ///
    /// **What counts as heavy is relative to the heaviest thing on the site, not a threshold in points.** A
    /// Grand site's remnants are hundreds against a monster marker's three, so the share cuts there by a wide
    /// margin; a site of nothing but monster markers has no such gap and keeps its best few, which is the
    /// behaviour wanted rather than a special case for it. The cap is on cost only: each anchor is a pass over
    /// the candidates, and a site where forty things tie is not offering forty decisions.
    /// </summary>
    // Internal rather than private so the offline harness can report the same anchors this uses, instead of
    // holding a second copy of the rule that could disagree with it. See tools/offline.
    internal static List<PlanTarget> HeavyTargetsOfSite(PlanEnvironment env, int already) =>
        TargetsWorthAtLeast(env, already, Ordinary);

    /// <summary>
    /// The content a chain is heading TOWARDS, which is a shorter list than the content worth opening on.
    ///
    /// **Two different questions, and one list answered both wrongly.** A chest worth ninety is worth putting a
    /// bomb on, so it belongs among the anchors. It is not what the chain is travelling towards, and counting it
    /// as such wrecks the tie-break: measured on the Basin snapshot, the spot (1150,668) sits 37 grid from a
    /// small chest and so looked ideally placed, while (1145,699) - which a player picked as the second bomb
    /// because it is deep in the corridor the chain actually continues down, and which the chain did continue
    /// down, to (1167,796) - measured 129 to anything and looked far worse.
    ///
    /// So the bar here is much higher than the anchor bar. What passes it is the content a route is planned
    /// around: remnants, and a relic carrying propagation. What does not is the ordinary furniture a bomb picks
    /// up on the way past.
    /// </summary>
    internal static List<PlanTarget> SubstantialTargetsOfSite(PlanEnvironment env, int already) =>
        TargetsWorthAtLeast(env, already, Considerable);

    /// <summary>
    /// How many times an ordinary marker a marker must be worth to count as something to head towards.
    ///
    /// Fifty, against the ten that makes a marker worth opening on. On the Basin snapshot the median marker is
    /// worth 3.2, so this floor is 160: the remnants (318 and up) and the relic carrying propagation (281) pass
    /// it, and the chests at 90 and 10 do not. Lower and the furniture decides the route again; much higher and a
    /// site whose best content is a few strongboxes has nothing to aim at.
    /// </summary>
    private const double Considerable = 50d;

    private static List<PlanTarget> TargetsWorthAtLeast(PlanEnvironment env, int already, double times)
    {
        // Measured in explosives still to come, the unit Rough expects, from the same expression greedy
        // passes it. One link is being decided, so that one does not count towards its own downstream.
        var downstream = Math.Max(0, env.Explosives - already - 1);
        var ranked = new List<(PlanTarget Target, double Worth)>();

        foreach (var target in env.Targets)
        {
            var worth = Planner.WorthOfTarget(target) + Math.Max(0f, target.Rough(downstream));

            if (worth > 0d)
                ranked.Add((target, worth));
        }

        if (ranked.Count == 0)
            return [];

        ranked.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        // **Measured against an ordinary marker, not against the best one.** A share of the best cut out a
        // remnant the player could see: on the Basin snapshot the richest marker is worth 1,503 with its
        // propagation and a remnant carrying nothing is worth 215, so a quarter of the best refused a remnant
        // 112 grid from the detonator and the enumeration offered one opening where there were two. What makes
        // a marker worth opening on is being far above the ordinary, and the median says what ordinary is.
        var middle = ranked[ranked.Count / 2].Worth;
        var floor = middle * times;
        var heavy = new List<PlanTarget>();

        foreach (var (target, worth) in ranked)
        {
            if (worth < floor)
                break;

            heavy.Add(target);
        }

        // A site with no such gap - nothing but monster markers - has no heavy content by that test and would
        // offer no openings at all. Its best few stand in, which is what the test is trying to find anyway.
        if (heavy.Count == 0)
        {
            for (var i = 0; i < ranked.Count && i < Fewest; i++)
                heavy.Add(ranked[i].Target);
        }

        return heavy;
    }

    /// <summary>
    /// How many times an ordinary marker a marker has to be worth before it is worth opening on.
    /// 
    /// Ten. The median marker on a dig site is a monster marker worth about three, and every container worth
    /// planning around is two orders of magnitude above that, so the cut lands in a wide empty band rather than
    /// among the things it is deciding between. Lower and an ordinary marker becomes an anchor, which is the
    /// candidate list again under another name.
    /// </summary>
    private const double Ordinary = 10d;

    /// <summary>
    /// How many stand in as heavy content on a site where nothing clears the floor.
    ///
    /// **Only for the site that has no heavy content at all**, such as one of nothing but monster markers,
    /// where the floor is above everything and an empty set would offer no openings. Twelve is arbitrary and
    /// only has to be more than the openings anybody would look at.
    ///
    /// **An earlier version capped the heavy set at twelve on every site, and that was a bug worth recording.**
    /// The cap was written as a cost ceiling and ended up deciding what counts as content worth heading towards,
    /// which is a different question. On the Basin snapshot the twelve highest-worth markers are all remnants,
    /// from 1,503 down to 318, so a relic at (1121,691) worth 281 - one weight of its own and the rest
    /// propagation - ranked thirteenth and was excluded from both the anchors and the tie-break, although the
    /// floor it had to clear was 32. A player looking at the site picked it out as the obvious second bomb. The
    /// floor is the rule; a cap on top of it is a second, silent rule.
    /// </summary>
    private const int Fewest = 12;

    /// <summary>
    /// How far this spot is from the next heavy thing ahead of it, which is the tie-break between spots that
    /// catch the same content.
    ///
    /// **Ahead, not merely nearest, and the difference decided a real case.** Measured on the Basin snapshot:
    /// five spots catch the northern remnant and all five catch the same 225, so the tie-break settles it. The
    /// nearest uncaught heavy thing from every one of them is the remnant at (948,499) - which is 196 grid the
    /// other way, is what the OTHER opening is built on, and is content a chain heading north will never take.
    /// Ranking on it pulled the northern opening backwards, to (1130,573) instead of (1137,601), and the spot it
    /// chose leaves the second bomb further from anything the chain can use.
    ///
    /// So a heavy target counts only if it lies in the direction the chain is already travelling: the step into
    /// this spot is one vector, the step onward to the target is another, and a target behind turns that product
    /// negative. Where nothing at all lies ahead the test is dropped rather than answered with infinity, since a
    /// spot at the edge of a site still has a nearest thing and a chain that has run out of forward content
    /// still has to be compared with something.
    ///
    /// Infinity only when there is no uncaught heavy content left anywhere, so a spot is never preferred for
    /// leading nowhere.
    /// </summary>
    internal static double Toward(PlanEnvironment env, List<PlanTarget> heavy, IReadOnlyList<Vector2> already,
        Vector2 at, Vector2 from)
    {
        var travelling = at - from;
        var ahead = double.MaxValue;
        var anywhere = double.MaxValue;

        foreach (var target in heavy)
        {
            if (Caught(already, target, env))
                continue;

            var reach = env.Blast + target.Radius;

            // Not the one this spot is itself taking - the question is what comes NEXT.
            if (Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                continue;

            var away = Vector2.Distance(at, target.Grid);

            anywhere = Math.Min(anywhere, away);

            if (Vector2.Dot(target.Grid - at, travelling) > 0f)
                ahead = Math.Min(ahead, away);
        }

        return ahead < double.MaxValue ? ahead : anywhere;
    }

    /// <summary>Whether the links laid so far already catch this target.</summary>
    private static bool Caught(IReadOnlyList<Vector2> links, PlanTarget target, PlanEnvironment env)
    {
        var reach = env.Blast + target.Radius;

        foreach (var at in links)
        {
            if (Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                return true;
        }

        return false;
    }

    /// <summary>
    /// How close two spots' content has to be before the tie-break decides between them.
    ///
    /// One per cent. Wider and a spot taking materially less content wins on position; narrower and two spots
    /// taking the same three monsters and one remnant are separated by a rounding difference.
    /// </summary>
    private const double Whisker = 1.01d;

    /// <summary>
    /// Collapses openings that lead to the same result, and drops the ones that lead nowhere.
    ///
    /// **This is what an opening IS, and the anchors above are only a way of finding candidates.** An earlier
    /// version took the rule literally from one site - two remnants in reach, so two openings - and that is a
    /// property of that ground rather than a principle: the same rule gives eight openings on a site with
    /// eight remnants in reach and none on a site with none, neither of which is the number of decisions
    /// actually available.
    ///
    /// Two openings are the same choice when they lead to the same place, so they are grouped by what a short
    /// chain from each one scores and one representative of each group is kept. On the site this was written
    /// for that leaves two - the northern remnant with three monster markers, the southern one with seven -
    /// because every spot taking a given remnant is a variation on one decision. That two is an outcome here,
    /// not a rule.
    ///
    /// **And an opening far behind the best is not a choice, it is a mistake.** A first bomb that takes a
    /// handful of monsters and no remnant scores well under one that takes a remnant, and offering it as an
    /// alternative is offering a wasted bomb. The floor is relative, so a site where everything opens equally
    /// well keeps everything.
    /// </summary>
    private static List<(List<Vector2> Links, double Caught, double Completed)> Choices(
        PlanEnvironment env, List<(List<Vector2> Links, double Caught, double Completed)> these, int want,
        Dictionary<Vector2, double> caughtBySpot)
    {
        _dominatedByChoices = 0;

        if (these.Count == 0)
            return these;

        var heavy = HeavyTargetsOfSite(env, 0);
        var ahead = SubstantialTargetsOfSite(env, 0);

        these.Sort((a, b) => b.Completed.CompareTo(a.Completed));

        // **An opening that reaches no heavy content is only a choice where none can be reached.** Measured on
        // the Basin snapshot: a spot taking seven monster markers scored 502 over three links and the northern
        // remnant scored inside two per cent of it, so the grouping merged them and the remnant opening
        // disappeared - two plans as different as a site offers, called one choice because a short rollout could
        // not tell them apart. Where nothing heavy is in reach these corridors are all there is and they stay.
        var reaching = these.FindAll(one =>
            HeavyContentCaughtByLinks(env, heavy, one.Links).Count > 0);

        if (reaching.Count > 0)
            these = reaching;

        var kept = new List<(List<Vector2> Links, double Caught, double Completed)>();
        var already = new List<List<int>>();
        var reaches = new List<double>();

        // The best score seen for each set of heavy content, because the floor compares like with like.
        var tops = new List<double>();

        // Which pair of areas each kept opening starts with. Both tests below compare only within one, which is what
        // "within a branch" means: three links starting in different areas can catch the same heavy content, and on
        // Scorched Cay a three bomb run dropped every opening starting at the remnant at (921,487) but one, as "behind"
        // openings that started elsewhere and caught the same remnants for more. See BranchOfOpening.
        var pairs = new List<(string, string)>();

        foreach (var one in these)
        {
            var caught = HeavyContentCaughtByLinks(env, heavy, one.Links);
            var reach = ReachOfOpening(env, ahead, one.Links);
            var pair = BranchOfOpening(env, heavy, one.Links);
            var behind = false;

            for (var i = 0; i < kept.Count; i++)
            {
                if (pairs[i] != pair)
                    continue;

                // **The floor applies within a branch, never across two.** Applied across the whole list it
                // dropped the northern opening on the Basin snapshot: over three links the southern branch
                // scores 806 and the northern one less than three quarters of that, so the northern branch was
                // pruned - and in full solves the previous session, the northern opening reached 9,510 against
                // the southern one's 6,712. A short rollout ranks branches; it is not good enough to delete one.
                // What the floor is for is a worse route to the SAME content, which is a comparison it can make.
                if (SameHeavyContent(already[i], caught) && SameReach(reaches[i], reach) &&
                    one.Completed < tops[i] * Adrift)
                    behind = true;
            }

            if (behind)
                continue;
            var same = false;

            for (var i = 0; i < kept.Count; i++)
            {
                if (pairs[i] != pair)
                    continue;

                // **Both, not either.** Alike scores alone merged a remnant with seven monster markers; the same
                // heavy content alone would merge two genuinely different routes through it.
                // **Three tests, because two openings can agree on content and score and still be different
                // plans.** The spots that catch a given remnant trade content against position: on the Basin
                // snapshot (1149,691) takes 107 and leaves 137 to the next heavy thing, (1145,699) takes 104 and
                // leaves 129. Same heavy content, scores within two per cent - so content and score alone called
                // them one choice and kept the richer, which is exactly the trade-off the three capture points
                // exist to offer. Where an opening leaves the chain is the third thing that makes it a choice.
                same |= Math.Abs(kept[i].Completed - one.Completed) <= Math.Abs(kept[i].Completed) * Alike &&
                        SameHeavyContent(already[i], caught) && SameReach(reaches[i], reach);
            }

            if (same)
                continue;

            kept.Add(one);
            pairs.Add(pair);
            already.Add(caught);
            reaches.Add(reach);
            tops.Add(one.Completed);
        }

        return AreasBeforeRank(env, WithoutDominated(env, kept, caughtBySpot), want);
    }

    /// <summary>
    /// How much further, in grid left to cover, one placement may be than another towards a direction and still count
    /// as serving it. Two grid is chosen, not measured: the neighbours it folds together stood one or two grid apart.
    /// </summary>
    private const double NearlyAsClose = 2d;

    /// <summary>Why each first bomb of the current generation was kept, by position. Written under Gate.</summary>
    private static readonly Dictionary<Vector2, List<string>> _whyFirstBombKept = new();

    private static void NoteWhyFirstBombKept(Vector2 at, string why)
    {
        if (!_whyFirstBombKept.TryGetValue(at, out var reasons))
            _whyFirstBombKept[at] = reasons = [];

        if (!reasons.Contains(why))
            reasons.Add(why);
    }

    /// <summary>
    /// The richest extension of each prefix towards each heavy area its last link catches, for rolling out. A last link
    /// that catches no heavy area is grouped as one for its prefix, so every corridor spot is not a choice of its own.
    /// Richest by what all its links catch; the first met wins a tie.
    /// </summary>
    private static List<(List<Vector2> Links, double Caught)> RichestPerPrefixAndArea(PlanEnvironment env,
        List<(List<Vector2> Links, double Caught)> grown)
    {
        var areas = AreasOf(env);
        var best = new Dictionary<(Vector2 Prefix, int Second), int>();

        for (var i = 0; i < grown.Count; i++)
        {
            var links = grown[i].Links;
            var key = (links[^2], areas.AreaCaughtAt(env, links[^1]));

            if (!best.TryGetValue(key, out var had) || grown[i].Caught > grown[had].Caught)
                best[key] = i;
        }

        return best.Values.OrderBy(i => i).Select(i => grown[i]).ToList();
    }

    /// <summary>How many openings the last call to Choices dropped as dominated. Written under Gate.</summary>
    private static int _dominatedByChoices;

    /// <summary>
    /// The heavy areas a bomb after this one could go for: none of it caught yet, and within two links of the previous
    /// one - this bomb's reach, then the next bomb's reach and its blast. See BestTowardEachNextArea and HeavyAreas.
    /// </summary>
    internal static List<int> NextAreasFrom(PlanEnvironment env, IReadOnlyList<Vector2> already, Vector2 from)
    {
        var areas = AreasOf(env);
        var next = new List<int>();

        for (var a = 0; a < areas.Count; a++)
        {
            if (areas.Touched(env, already, a))
                continue;

            if (areas.LeftTo(env, from, a) <= env.Reach * 2f)
                next.Add(a);
        }

        return next;
    }

    /// <summary>
    /// Which placements to keep: for each heavy area the chain could go to next, the placement with the least left to
    /// cover before a bomb could catch something there, and the richest placement. With no next area the measure is
    /// the reach spent from the previous link, which is what both prunes used before. How many survive is how many
    /// ways out the placements offer, not a number chosen here.
    ///
    /// **Distance from the previous bomb is not direction, and it was the second term.** It rewarded a spot for being
    /// far from where the chain came from, whichever way that was - so on Scorched Cay the only spot offered for the
    /// 4 socket remnant at (1132,574) was (1168,582): the most content, the furthest from the detonator, and 383 grid
    /// from the next heavy content, where the south edge of the same ring caught 13 less and stood 98 from the 6 socket
    /// remnant next along. A player opened on (1101,594), 70 grid from the detonator on the side facing the next
    /// remnant, and reached it and its relic with the second bomb. Measured per target, a spot is only thrown away
    /// when another does as well or better towards every place the chain could go next, which is also what keeps
    /// both sides of a fork.
    ///
    /// **Close is what is left to cover before a bomb could catch something in the area**: the distance to its
    /// nearest remnant or relic less the blast and that item's extent, nought when this placement already catches
    /// one. Straight lines, not routes; the rollouts that follow are what judge the survivors on the ground. **Per
    /// area, not per target**, because a player reads a site as areas: per target, the several items of one area each
    /// pulled out a spot a few grid from the others, and 55 first bombs were offered where 12 had been.
    /// </summary>
    /// <param name="placeable">
    /// Asked of a placement only when a selection reaches it, best first, until one says yes - so the ground answers a
    /// handful of questions per direction rather than one per spot on every ring. Null when every placement is
    /// already known to stand.
    /// </param>
    internal static bool[] BestTowardEachNextArea(PlanEnvironment env, Vector2 from,
        List<(Vector2 At, double Caught)> placements, List<int> nextAreas, ForkTally forks = null,
        Func<Vector2, bool> placeable = null, Action<int, string> reasonFor = null)
    {
        var keep = new bool[placements.Count];

        if (placements.Count == 0)
            return keep;

        var areas = AreasOf(env);

        // One column per next area, plus the old term when there are none.
        var columns = nextAreas.Count > 0 ? nextAreas.Count : 1;
        var left = new double[placements.Count, columns];

        for (var i = 0; i < placements.Count; i++)
        {
            if (nextAreas.Count == 0)
            {
                left[i, 0] = -Planner.Span(from, placements[i].At);

                continue;
            }

            for (var a = 0; a < nextAreas.Count; a++)
                left[i, a] = areas.LeftToHeart(env, placements[i].At, nextAreas[a]);
        }

        // **Towards the whole area where that can be had, towards any of it where it cannot.** Measured to the nearest
        // item, "towards" chose the spot closest to a remnant whether or not the next bomb could also take the relic
        // beside it - which is the difference between the second bomb a player places and one that takes half the
        // area. The heart is where one bomb catches the whole area (see HeavyAreas.LeftToHeart); where no placement
        // here brings it within one reach, the nearest item is what the direction is measured to.
        for (var a = 0; a < nextAreas.Count; a++)
        {
            var heartInReach = false;

            for (var i = 0; i < placements.Count && !heartInReach; i++)
                heartInReach = left[i, a] <= env.Reach;

            if (heartInReach)
                continue;

            for (var i = 0; i < placements.Count; i++)
                left[i, a] = areas.LeftTo(env, placements[i].At, nextAreas[a]);
        }

        // **The best for each next area, not every placement no other beats.** Every spot round a ring is closest to
        // some direction, so the full front kept most of the ring: on Scorched Cay 281 rollouts in place of 87.
        var bestFor = new int[columns];
        var order = new int[placements.Count];

        // The first placement in this order that stands, asking the ground only as far as that.
        int FirstStanding(Comparison<int> better)
        {
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, better);

            foreach (var i in order)
            {
                if (placeable == null || placeable(placements[i].At))
                    return i;
            }

            return -1;
        }

        // **Only areas a bomb from here could actually catch next.** Every area within two reaches of the previous link
        // was a direction, and each far one pulled out a spot a degree round the ring from the last - three first bombs
        // within a few grid on one remnant's ring on Scorched Cay. An area no placement here brings within one reach is
        // not a choice this bomb makes; it is left to the approach spots. Where none is in reach, all of them count.
        var catchable = new bool[columns];
        var anyCatchable = false;

        for (var t = 0; t < columns; t++)
        {
            for (var i = 0; i < placements.Count && !catchable[t]; i++)
                catchable[t] = nextAreas.Count == 0 || left[i, t] <= env.Reach;

            // An area every placement here already catches some of is where the chain is, not a way out of it - kept
            // as a direction it only repeated the richest. Tested on any of its items, not on its heart, or a group round
            // a remnant would count its own area as a direction wherever a spot misses the relic beside it.
            var everyCatches = nextAreas.Count > 0;

            for (var i = 0; i < placements.Count && everyCatches; i++)
                everyCatches = areas.LeftTo(env, placements[i].At, nextAreas[t]) <= 0d;

            if (everyCatches)
                catchable[t] = false;

            anyCatchable |= catchable[t];
        }

        for (var t = 0; t < columns; t++)
        {
            var column = t;

            bestFor[t] = -1;

            if (anyCatchable && !catchable[t])
                continue;

            bestFor[t] = FirstStanding((a, b) =>
            {
                var byLeft = left[a, column].CompareTo(left[b, column]);

                return byLeft != 0 ? byLeft : placements[b].Caught.CompareTo(placements[a].Caught);
            });

            if (bestFor[t] >= 0)
                keep[bestFor[t]] = true;
        }

        // **And for each direction, the richest placement that still reaches it.** The closest spot gives the next
        // bomb the most room; a richer spot a little further back gives up some of that room for content now, and
        // which pays is what the rollouts judge. On Scorched Cay, from the remnant at (921,487) towards the remnant
        // and relic at (978,649): (935,521) is closest, taking 225, and a player's own opener started there;
        // (941,517) takes 245 and still reaches two thirds of the ground where one bomb catches both. Both are kept.
        var richestFor = new int[columns];

        for (var t = 0; t < columns; t++)
        {
            var column = t;

            richestFor[t] = -1;

            if (bestFor[t] < 0 || nextAreas.Count == 0)
                continue;

            var reaching = FirstStanding((a, b) =>
            {
                var aIn = left[a, column] <= env.Reach;
                var bIn = left[b, column] <= env.Reach;

                if (aIn != bIn)
                    return aIn ? -1 : 1;

                var byCaught = placements[b].Caught.CompareTo(placements[a].Caught);

                return byCaught != 0 ? byCaught : left[a, column].CompareTo(left[b, column]);
            });

            if (reaching >= 0 && reaching != bestFor[t] && left[reaching, t] <= env.Reach &&
                placements[reaching].Caught > placements[bestFor[t]].Caught)
            {
                richestFor[t] = reaching;
                keep[reaching] = true;
            }
        }

        // And the richest, which may be best for no direction but is what the chain takes if content decides.
        //
        // **Among equally rich, the best placed.** Many spots round one remnant catch exactly the same, and the tie was
        // settled by list order: on Scorched Cay (944,459), (928,489) and (929,489) all caught 265 for the remnant at
        // (921,487), and the richest came out as (944,459) - against the wall, 298 from the next heavy content, and
        // out of reach of (815,580), which (929,489) reaches. So a tie goes to the spot that brings the most next areas
        // within one reach, then to the one with least left to cover to them in all.
        int WithinReach(int i)
        {
            var count = 0;

            for (var t = 0; t < columns; t++)
            {
                if (nextAreas.Count > 0 && left[i, t] <= env.Reach)
                    count++;
            }

            return count;
        }

        double LeftInAll(int i)
        {
            var sum = 0d;

            for (var t = 0; t < columns; t++)
            {
                if (nextAreas.Count > 0 && left[i, t] <= env.Reach)
                    sum += left[i, t];
            }

            return sum;
        }

        var richest = FirstStanding((a, b) =>
        {
            var byCaught = placements[b].Caught.CompareTo(placements[a].Caught);

            if (byCaught != 0)
                return byCaught;

            var byReach = WithinReach(b).CompareTo(WithinReach(a));

            return byReach != 0 ? byReach : LeftInAll(a).CompareTo(LeftInAll(b));
        });

        if (richest >= 0)
            keep[richest] = true;

        // **Two directions that point the same way are one choice.** The best spot towards a relic and the best towards
        // a remnant beyond it stood 1.4 grid apart on one ring on Scorched Cay. A kept placement is folded into another
        // that is within NearlyAsClose of it towards every direction it was kept for - and, if it was kept as the
        // richest, catches at least as much - and that one takes its reasons. Visited most reasons first, then
        // richest, so the placement serving the most directions absorbs the rest.
        var reasons = new Dictionary<int, List<int>>();

        void Reason(int placement, int code)
        {
            if (!reasons.TryGetValue(placement, out var list))
                reasons[placement] = list = [];

            list.Add(code);
        }

        for (var t = 0; t < columns; t++)
        {
            if (bestFor[t] >= 0)
                Reason(bestFor[t], t * 2);

            if (richestFor[t] >= 0)
                Reason(richestFor[t], t * 2 + 1);
        }

        if (richest >= 0)
        {
            if (!reasons.TryGetValue(richest, out var list))
                reasons[richest] = list = [];

            list.Add(-1);
        }

        var visiting = reasons.Keys
            .OrderByDescending(i => reasons[i].Count)
            .ThenByDescending(i => placements[i].Caught)
            .ToList();
        var accepted = new List<int>();

        foreach (var y in visiting)
        {
            var into = -1;

            foreach (var x in accepted)
            {
                var covers = true;

                foreach (var code in reasons[y])
                {
                    var t = code / 2;

                    covers &= code < 0
                        ? placements[x].Caught >= placements[y].Caught
                        : code % 2 == 0
                            ? left[x, t] <= left[y, t] + NearlyAsClose
                            : left[x, t] <= env.Reach && placements[x].Caught >= placements[y].Caught;
                }

                if (covers)
                {
                    into = x;

                    break;
                }
            }

            if (into < 0)
            {
                accepted.Add(y);

                continue;
            }

            keep[y] = false;
            reasons[into].AddRange(reasons[y]);
        }

        foreach (var x in accepted)
        {
            foreach (var code in reasons[x].Distinct())
            {
                reasonFor?.Invoke(x, code < 0
                    ? "richest"
                    : nextAreas.Count == 0
                        ? "furthest"
                        : (code % 2 == 0 ? "towards " : "richest towards ") + areas.Name(nextAreas[code / 2]));
            }
        }

        // Each placement dropped is credited to the one kept for the first direction, for the fork tally - only when
        // every placement is known to stand, since otherwise most of those dropped were never asked about.
        var credited = Array.FindIndex(bestFor, b => b >= 0);

        if (placeable == null && credited >= 0)
        {
            for (var i = 0; i < placements.Count; i++)
            {
                if (!keep[i])
                    forks?.Discarded(placements[i].At, placements[bestFor[credited]].At);
            }
        }

        return keep;
    }

    /// <summary>
    /// The openings with every one dropped that another does everything for.
    ///
    /// **Two first bombs are one choice when one can do all the other can.** Keeping the best opening of every
    /// distinct first link spread the pool over positions that differ only in where they stand: on Scorched Cay
    /// (920,471) then (952,623), and (909,495) then (943,637), both first catching target 10 and then target 5 - the
    /// second placed nearer, taking a little more, and within reach of the first's second bomb. An opening B is
    /// dropped when another opening A has the same heavy content at both links, a first bomb catching at least as
    /// much as B's, and a first bomb from which B's second bomb can be placed. Where the two first bombs catch the
    /// same, the lower ranked goes, so two openings cannot drop each other.
    ///
    /// Only openings of two links or more: a single bomb has no second placement to compare.
    /// </summary>
    private static List<(List<Vector2> Links, double Caught, double Completed)> WithoutDominated(
        PlanEnvironment env, List<(List<Vector2> Links, double Caught, double Completed)> ranked,
        Dictionary<Vector2, double> caughtBySpot)
    {
        if (ranked.Count < 2 || ranked[0].Links.Count < 2)
            return ranked;

        var heavy = HeavyTargetsOfSite(env, 0);
        var keys = new List<(string Branch, string Variation)>(ranked.Count);

        foreach (var one in ranked)
            keys.Add(BranchOfOpening(env, heavy, one.Links));

        var dropped = new bool[ranked.Count];

        for (var i = 0; i < ranked.Count; i++)
        {
            var loser = ranked[i];

            for (var j = 0; j < ranked.Count && !dropped[i]; j++)
            {
                if (j == i || dropped[j] || keys[j] != keys[i])
                    continue;

                var winner = ranked[j];

                if (winner.Links[0] == loser.Links[0])
                    continue;

                var winnerTakes = caughtBySpot.TryGetValue(winner.Links[0], out var w) ? w : 0d;
                var loserTakes = caughtBySpot.TryGetValue(loser.Links[0], out var l) ? l : 0d;

                // Ranked best first, so a lower j is the better ranked of an equal pair.
                if (winnerTakes < loserTakes || winnerTakes == loserTakes && j > i)
                    continue;

                if (Vector2.Distance(winner.Links[0], loser.Links[1]) < env.Apart ||
                    !PlaceableFrom(env, winner.Links[0], loser.Links[1]))
                    continue;

                dropped[i] = true;
            }
        }

        var kept = new List<(List<Vector2> Links, double Caught, double Completed)>(ranked.Count);

        for (var i = 0; i < ranked.Count; i++)
        {
            if (dropped[i])
                _dominatedByChoices++;
            else
                kept.Add(ranked[i]);
        }

        return kept;
    }

    /// <summary>
    /// The openings kept for the next level, chosen area first: the best of each branch, then the best of each
    /// variation within a branch, then the best of each remaining first link, then the rest by rank - up to the number
    /// wanted, returned best first.
    ///
    /// **Ranking alone kept one first link.** A level extends every survivor of the level before and keeps the best
    /// few by what a short rollout scores, and the extensions of whichever first link rolls out best fill the top of
    /// that list. Measured on Scorched Cay: the first link kept 8 distinct first bombs, the second extended them into
    /// 75 openings and kept 8 - all eight from (957,679), the other seven first bombs thrown away.
    ///
    /// **Then one opening per first link spent the places on positions.** The same site has four areas a first bomb
    /// can take, and one of them was offered from four positions a few grid apart, which took four of the eight places
    /// while the most central area kept one opening and one second bomb. A branch is an area, what the first bomb
    /// catches; a variation is what the second catches; a first link is a position. Sharing places out in that order
    /// is how a player reads the site, and the order OpeningForWorker hands the openings to the pool in.
    /// </summary>
    private static List<(List<Vector2> Links, double Caught, double Completed)> AreasBeforeRank(
        PlanEnvironment env, List<(List<Vector2> Links, double Caught, double Completed)> ranked, int want)
    {
        if (ranked.Count <= want)
            return ranked;

        var heavy = HeavyTargetsOfSite(env, 0);
        var keys = new List<(string Branch, string Variation)>(ranked.Count);

        foreach (var one in ranked)
            keys.Add(BranchOfOpening(env, heavy, one.Links));

        var chosen = new List<(List<Vector2> Links, double Caught, double Completed)>();
        var taken = new bool[ranked.Count];

        // One pass per tier, each taking the best not yet taken of every group it has not seen, in rank order.
        void Tier(Func<int, object> groupOf)
        {
            var seen = new HashSet<object>();

            for (var i = 0; i < ranked.Count && chosen.Count < want; i++)
            {
                if (taken[i] || !seen.Add(groupOf(i)))
                    continue;

                taken[i] = true;
                chosen.Add(ranked[i]);
            }
        }

        // **Every pair of areas keeps its best, however many there are.** A cap chose how many ways into the site
        // were kept rather than the site doing so; the number wanted only tops the list up by rank when the site has
        // fewer pairs than that.
        var seen = new HashSet<(string, string)>();

        for (var i = 0; i < ranked.Count; i++)
        {
            if (!seen.Add(keys[i]))
                continue;

            taken[i] = true;
            chosen.Add(ranked[i]);
        }

        Tier(i => i);

        chosen.Sort((a, b) => b.Completed.CompareTo(a.Completed));

        return chosen;
    }

    /// <summary>Which heavy markers these links catch, as indices into the heavy list, in order.</summary>
    private static List<int> HeavyContentCaughtByLinks(PlanEnvironment env, List<PlanTarget> heavy,
        List<Vector2> links)
    {
        var caught = new List<int>();

        for (var i = 0; i < heavy.Count; i++)
        {
            if (Caught(links, heavy[i], env))
                caught.Add(i);
        }

        return caught;
    }

    /// <summary>
    /// How far an opening leaves the chain from the next heavy content ahead of it.
    ///
    /// The same measure the capture points are chosen by, read off the finished opening: its last link is where
    /// the chain stands, the links before it are what has already been taken, and the one before that is the
    /// direction it arrived from.
    /// </summary>
    private static double ReachOfOpening(PlanEnvironment env, List<PlanTarget> heavy, List<Vector2> links)
    {
        if (links.Count == 0)
            return double.MaxValue;

        var at = links[^1];
        var from = links.Count > 1 ? links[^2] : env.Origin;
        var before = links.GetRange(0, links.Count - 1);

        return Toward(env, heavy, before, at, from);
    }

    /// <summary>Whether two openings leave the chain the same distance from what comes next.</summary>
    private static bool SameReach(double these, double those)
    {
        if (double.IsInfinity(these) || double.IsInfinity(those) ||
            these >= double.MaxValue || those >= double.MaxValue)
            return these >= double.MaxValue && those >= double.MaxValue;

        return Math.Abs(these - those) <= Math.Abs(these) * Alike;
    }

    /// <summary>Whether two openings take the same heavy content.</summary>
    private static bool SameHeavyContent(List<int> these, List<int> those)
    {
        if (these.Count != those.Count)
            return false;

        for (var i = 0; i < these.Count; i++)
        {
            if (these[i] != those[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// How close two openings' outcomes have to be to count as one choice.
    ///
    /// Two per cent. It is a tolerance on the same quantity for both, so it says "these lead to the same
    /// place" rather than "these are worth the same" - two genuinely different routes that happen to score
    /// alike would be merged, and that is the accepted cost of not offering eight spellings of one decision.
    /// </summary>
    private const double Alike = 0.02d;

    /// <summary>
    /// How far behind an opening may be, against others taking the same heavy content, and still be an
    /// alternative.
    ///
    /// Three quarters. Within one branch the comparison is fair: both openings reach the same content, so the
    /// one arriving with materially less is a worse route to it rather than a different plan. Across branches it
    /// is not fair, and this was applied across them - see Choices for the case where it deleted the branch that
    /// wins a full solve.
    /// </summary>
    private const double Adrift = 0.75d;

    /// <summary>
    /// The most a single further bomb could catch from here, over the markers this chain has not taken.
    ///
    /// **This is what "well placed" has to mean, and distance was the wrong proxy for it.** A placement is worth
    /// more than another not because it spent more of the reach getting there, but because there is more in front
    /// of it - "the reach to the next viable best bomb 3 candidates", in a player's words. The two often agree and
    /// the cases where they do not are the ones that matter: a spot at the far edge of the reach facing a wall is
    /// deep and worthless, and a shallower one facing a cluster is not.
    ///
    /// Measured in content, the same unit as what the spot itself catches, so the two can be compared without a
    /// weight chosen by hand. What is already taken is excluded, so a further bomb covering only markers this
    /// chain has is worth nothing.
    ///
    /// **The straight line, not the routed wire.** A routed test here would ask the game about every candidate
    /// within reach of every spot under consideration - tens of thousands of wire routings per link, where the
    /// straight line is arithmetic. It is optimistic: a spot whose ground is open scores the same as one whose
    /// wire has to go round something. This decides which placements are worth OFFERING, and the rollout that
    /// ranks them afterwards uses the routed test throughout.
    /// </summary>
    private static double AheadOfSpot(PlanEnvironment env, List<Vector2> candidates,
        IReadOnlyList<Vector2> already, Vector2 at)
    {
        var taken = new HashSet<int>();

        foreach (var had in already)
        {
            foreach (var i in Planner.CaughtIndicesAt(env, had))
                taken.Add(i);
        }

        foreach (var i in Planner.CaughtIndicesAt(env, at))
            taken.Add(i);

        var best = 0d;

        foreach (var to in candidates)
        {
            if (Planner.Span(at, to) > env.Reach || !env.CanPlace(to))
                continue;

            var adds = 0d;

            foreach (var i in Planner.CaughtIndicesAt(env, to))
            {
                if (!taken.Contains(i))
                    adds += env.Targets[i].Weight;
            }

            if (adds > best)
                best = adds;
        }

        return best;
    }

    /// <summary>
    /// Which heavy content a bomb here catches, as a key, so placements can be compared only against the ones
    /// competing for the same thing.
    /// </summary>
    private static string AnchorsAt(PlanEnvironment env, List<PlanTarget> heavy, Vector2 at)
    {
        var these = new List<int>();

        for (var i = 0; i < heavy.Count; i++)
        {
            var reach = env.Blast + heavy[i].Radius;

            if (Vector2.DistanceSquared(at, heavy[i].Grid) <= reach * reach)
                these.Add(i);
        }

        return string.Join(",", these);
    }

    /// <summary>
    /// Which opening a worker takes, spreading the pool across the branches a site offers before spreading it
    /// across variations of one branch.
    ///
    /// **A ranked list handed out round robin gives one branch most of the pool.** The list is ranked by what a
    /// short chain from each opening scores, and openings sharing a first link score alike, so the top of it is
    /// several variations of whichever branch rolls out best - on the measured site nine of fifteen two-bomb
    /// openings began at the same first link. Handing that out in order put three workers on one branch and one
    /// on the other, and the branch with three produced the winner at 10,407 while the branch with one reached
    /// 8,627. The two first links catch the same content and both reach the same second bomb, and substituting
    /// one for the other in the winning chain changes its score by nothing at four, eight or fifteen links - so
    /// that gap is the count of tickets each branch was given, not the quality of either opening.
    ///
    /// So the first link is what the pool is spread over. Workers go round the branches first and only then to a
    /// second variation within a branch, which on a site offering two branches and five seeded workers gives
    /// three and two rather than four and one. Variations still matter - a branch explored from two different
    /// second links is explored twice over - but they are what is left after every branch has somebody on it.
    ///
    /// <paramref name="slot"/> counts only the workers being seeded, so the workers held back to draw their own
    /// opening do not leave gaps in the rotation. See Repair, which works it out.
    /// </summary>
    internal static List<Vector2> OpeningForWorker(PlanEnvironment env, IReadOnlyList<List<Vector2>> openings,
        int slot)
    {
        if (openings is not { Count: > 0 })
            return null;

        var heavy = HeavyTargetsOfSite(env, 0);

        // Branches in the order they first appear, which is rank order, and within each branch its variations in
        // the order they first appear, each holding its openings in rank order.
        var branches = new List<List<List<List<Vector2>>>>();
        var branchIndex = new Dictionary<string, int>();
        var variationIndex = new List<Dictionary<string, int>>();

        foreach (var opening in openings)
        {
            if (opening is not { Count: > 0 })
                continue;

            var (branch, variation) = BranchOfOpening(env, heavy, opening);

            if (!branchIndex.TryGetValue(branch, out var at))
            {
                branchIndex[branch] = at = branches.Count;
                branches.Add([]);
                variationIndex.Add([]);
            }

            if (!variationIndex[at].TryGetValue(variation, out var within))
            {
                variationIndex[at][variation] = within = branches[at].Count;
                branches[at].Add([]);
            }

            branches[at][within].Add(opening);
        }

        if (branches.Count == 0)
            return null;

        // Within the branch, one opening from each variation before a second from any, for the same reason the
        // branches come before the variations.
        var variations = branches[Math.Abs(slot) % branches.Count];
        var total = 0;

        foreach (var variation in variations)
            total += variation.Count;

        var mine = new List<List<Vector2>>();

        for (var depth = 0; mine.Count < total; depth++)
        {
            foreach (var variation in variations)
            {
                if (depth < variation.Count)
                    mine.Add(variation[depth]);
            }
        }

        return mine[Math.Abs(slot) / branches.Count % mine.Count];
    }

    /// <summary>What the last generation's fork check found, for the dump. See ForkTally.</summary>
    public static string ForksSaid { get; private set; } = "not generated";

    /// <summary>
    /// Counts the placements the enumeration's prunes discard that could have reached heavy content the placement
    /// beating them cannot - the far side of a fork, lost.
    ///
    /// **Both prunes judge a placement on what it catches and how far it stands from the previous link, and
    /// neither looks at where the chain can go next.** Two spots catching one remnant, one at the edge nearer a
    /// second remnant and one at the edge nearer a third, are compared on distance from the last bomb alone, so
    /// the nearer of the two is discarded even when it is the only way to the remnant it faces. Whether that
    /// happens on real ground is what this answers before the prunes are changed.
    ///
    /// A heavy target counts as reachable from a spot when some candidate that catches it can be placed from that
    /// spot, the same question the enumeration asks of every link. Targets the winner already catches, or the
    /// chain already holds, are not counted as lost. Measurement only: nothing it finds changes what is kept.
    /// </summary>
    internal sealed class ForkTally
    {
        /// <summary>One lost fork: the placement discarded, the one that beat it, and what only the first reaches.</summary>
        internal readonly record struct LostFork(Vector2 Discarded, Vector2 BeatenBy, Vector2 OnlyItReaches);

        /// <summary>How many lost forks are kept to be drawn. The count covers all of them.</summary>
        private const int KeptForks = 40;

        /// <summary>
        /// How long the check may spend in one generation, after which discards are counted but not checked.
        ///
        /// **Unbounded, it took 19,946ms of one enumeration on Scorched Cay** - 3,171 discards, each asking the
        /// router about every heavy target in range, most of them misses that route from scratch - and it holds
        /// the enumeration's lock the whole time. Chosen, not measured: enough for a few hundred checks, which
        /// is a sample, and the readout says how many of the discards it covered.
        /// </summary>
        private static readonly long Budget = System.Diagnostics.Stopwatch.Frequency / 4;

        private static int _discarded;
        private static int _checked;
        private static int _lost;
        private static long _ticks;
        private static readonly List<string> _examples = [];
        private static readonly List<LostFork> _lostForks = [];

        private readonly PlanEnvironment _env;
        private readonly List<PlanTarget> _heavy;
        private readonly List<Vector2> _candidates;
        private readonly IReadOnlyList<Vector2> _already;
        private readonly Dictionary<int, List<Vector2>> _catchersOfTarget = new();
        private readonly Dictionary<Vector2, HashSet<int>> _reachableFromSpot = new();

        internal ForkTally(PlanEnvironment env, List<PlanTarget> heavy, List<Vector2> candidates,
            IReadOnlyList<Vector2> already)
        {
            _env = env;
            _heavy = heavy;
            _candidates = candidates;
            _already = already;
        }

        internal static void Forget()
        {
            _discarded = 0;
            _checked = 0;
            _lost = 0;
            _ticks = 0;
            _examples.Clear();
            _lostForks.Clear();
        }

        /// <summary>Placements discarded and forks lost so far in this generation.</summary>
        internal static (int Discarded, int Lost) Counts() => (_discarded, _lost);

        /// <summary>The lost forks kept to be drawn, copied so the panel does not read a list being written.</summary>
        internal static List<LostFork> Lost() => [.._lostForks];

        internal static string Said()
        {
            var said = $"{_discarded:N0} placement(s) discarded by the prunes, {_checked:N0} of them checked " +
                       $"within the time allowed, {_lost:N0} of those the only way to heavy content the placement " +
                       $"beating them cannot reach, checked in " +
                       $"{_ticks * 1000d / System.Diagnostics.Stopwatch.Frequency:N0}ms";

            return _examples.Count > 0 ? said + "; for example " + string.Join("; ", _examples) : said;
        }

        internal void Discarded(Vector2 loser, Vector2 winner)
        {
            _discarded++;

            if (_ticks >= Budget)
                return;

            var began = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                _checked++;

                var winnerReaches = ReachableFromSpot(winner);

                foreach (var target in ReachableFromSpot(loser))
                {
                    if (winnerReaches.Contains(target) || CatchesTarget(winner, _heavy[target]))
                        continue;

                    _lost++;

                    if (_lostForks.Count < KeptForks)
                        _lostForks.Add(new LostFork(loser, winner, _heavy[target].Grid));

                    if (_examples.Count < 3)
                    {
                        var at = _heavy[target].Grid;

                        _examples.Add(string.Create(CultureInfo.InvariantCulture,
                            $"({loser.X:0},{loser.Y:0}) beaten by ({winner.X:0},{winner.Y:0}) could reach " +
                            $"({at.X:0},{at.Y:0})"));
                    }

                    return;
                }
            }
            finally
            {
                _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
            }
        }

        private bool CatchesTarget(Vector2 at, PlanTarget target)
        {
            var reach = _env.Blast + target.Radius;

            return Vector2.DistanceSquared(at, target.Grid) <= reach * reach;
        }

        private HashSet<int> ReachableFromSpot(Vector2 spot)
        {
            if (_reachableFromSpot.TryGetValue(spot, out var known))
                return known;

            var reachable = new HashSet<int>();

            for (var t = 0; t < _heavy.Count; t++)
            {
                var target = _heavy[t];

                if (Caught(_already, target, _env) || CatchesTarget(spot, target))
                    continue;

                if (Planner.Span(spot, target.Grid) > _env.Reach + _env.Blast + target.Radius)
                    continue;

                foreach (var catcher in CatchersOfTarget(t))
                {
                    if (Vector2.Distance(catcher, spot) < _env.Apart)
                        continue;

                    if (_env.CanPlace(catcher) && Planner.Says(_env, spot, catcher) == Certainty.Yes)
                    {
                        reachable.Add(t);

                        break;
                    }
                }
            }

            _reachableFromSpot[spot] = reachable;

            return reachable;
        }

        private List<Vector2> CatchersOfTarget(int t)
        {
            if (_catchersOfTarget.TryGetValue(t, out var known))
                return known;

            var catchers = new List<Vector2>();

            foreach (var at in _candidates)
            {
                if (CatchesTarget(at, _heavy[t]))
                    catchers.Add(at);
            }

            _catchersOfTarget[t] = catchers;

            return catchers;
        }
    }

    /// <summary>
    /// The site's heavy areas: its remnants and relics, grouped where one bomb could catch two of them together.
    ///
    /// **An area is what a player means by one**: the remnant and the stalagmite beside it that pathing that way takes
    /// together, not each on its own. Two items join when they stand within two blasts and their extents of each other,
    /// so a bomb between them could catch both - with every other item of the area, not by a chain. Named by its
    /// heaviest item.
    /// </summary>
    internal sealed class HeavyAreas
    {
        private readonly List<PlanTarget> _items = new();
        private readonly List<int> _areaOfItem = new();
        private readonly List<PlanTarget> _named = new();

        internal HeavyAreas(PlanEnvironment env)
        {
            foreach (var target in env.Targets)
            {
                if (target.Kind is TargetKind.Remnant or TargetKind.Relic)
                    _items.Add(target);
            }

            // **Every pair, not a chain of pairs.** Joined transitively, a remnant, the relic beside it and a second
            // remnant beyond the relic became one area on Scorched Cay - (978,649), (931,684) and (931,754) - though the
            // two remnants stand 114 apart and no bomb takes both. An item joins an area only if one bomb could catch it
            // with every item already there; the closest pairs of groups are merged first.
            bool Together(int i, int j) =>
                Vector2.Distance(_items[i].Grid, _items[j].Grid) <=
                env.Blast * 2f + _items[i].Radius + _items[j].Radius;

            var groups = new List<List<int>>();

            for (var i = 0; i < _items.Count; i++)
                groups.Add([i]);

            while (true)
            {
                var (bestA, bestB, bestApart) = (-1, -1, double.MaxValue);

                for (var a = 0; a < groups.Count; a++)
                {
                    for (var b = a + 1; b < groups.Count; b++)
                    {
                        var every = true;
                        var closest = double.MaxValue;

                        foreach (var i in groups[a])
                        {
                            foreach (var j in groups[b])
                            {
                                every &= Together(i, j);
                                closest = Math.Min(closest, Vector2.Distance(_items[i].Grid, _items[j].Grid));
                            }
                        }

                        if (every && closest < bestApart)
                            (bestA, bestB, bestApart) = (a, b, closest);
                    }
                }

                if (bestA < 0)
                    break;

                groups[bestA].AddRange(groups[bestB]);
                groups.RemoveAt(bestB);
            }

            var areaOfItem = new int[_items.Count];

            for (var area = 0; area < groups.Count; area++)
            {
                var heaviest = groups[area][0];

                foreach (var i in groups[area])
                {
                    areaOfItem[i] = area;

                    if (Planner.WorthOfTarget(_items[i]) > Planner.WorthOfTarget(_items[heaviest]))
                        heaviest = i;
                }

                _named.Add(_items[heaviest]);
            }

            _areaOfItem.AddRange(areaOfItem);

            FindHearts(env, groups);
        }

        internal int Count => _named.Count;

        /// <summary>
        /// Each area's heart: the items one bomb catches together - all of them, or the heaviest set that can be caught
        /// at once - and, for more than one item, the cells on the edge of the ground where a bomb catches that set.
        /// </summary>
        private readonly List<(List<int> Items, List<Vector2> Edge)> _hearts = new();

        private void FindHearts(PlanEnvironment env, List<List<int>> groups)
        {
            foreach (var group in groups)
            {
                if (group.Count == 1)
                {
                    _hearts.Add((group, null));

                    continue;
                }

                // Every subset, heaviest first, and the first that one bomb can catch whole. Areas are a handful of
                // items, so this is at most a few dozen subsets once per site.
                var subsets = new List<List<int>>();

                for (var mask = 1; mask < 1 << Math.Min(group.Count, 8); mask++)
                {
                    var subset = new List<int>();

                    for (var k = 0; k < Math.Min(group.Count, 8); k++)
                    {
                        if ((mask & (1 << k)) != 0)
                            subset.Add(group[k]);
                    }

                    subsets.Add(subset);
                }

                subsets.Sort((a, b) =>
                    b.Sum(i => (double)Planner.WorthOfTarget(_items[i]))
                        .CompareTo(a.Sum(i => (double)Planner.WorthOfTarget(_items[i]))));

                var found = false;

                foreach (var subset in subsets)
                {
                    if (subset.Count == 1)
                    {
                        _hearts.Add((subset, null));
                        found = true;

                        break;
                    }

                    var edge = EdgeWhereAllCaught(env, subset);

                    if (edge.Count == 0)
                        continue;

                    _hearts.Add((subset, edge));
                    found = true;

                    break;
                }

                if (!found)
                    _hearts.Add(([group[0]], null));
            }
        }

        /// <summary>The cells where a bomb catches every one of these items and a neighbour does not.</summary>
        private List<Vector2> EdgeWhereAllCaught(PlanEnvironment env, List<int> subset)
        {
            bool Inside(int x, int y)
            {
                foreach (var i in subset)
                {
                    var reach = env.Blast + _items[i].Radius;

                    if (Vector2.DistanceSquared(new Vector2(x, y), _items[i].Grid) > reach * reach)
                        return false;
                }

                return true;
            }

            var first = _items[subset[0]];
            var span = (int)MathF.Ceiling(env.Blast + first.Radius) + 1;
            var edge = new List<Vector2>();

            for (var y = (int)first.Grid.Y - span; y <= (int)first.Grid.Y + span; y++)
            {
                for (var x = (int)first.Grid.X - span; x <= (int)first.Grid.X + span; x++)
                {
                    if (!Inside(x, y))
                        continue;

                    if (!Inside(x + 1, y) || !Inside(x - 1, y) || !Inside(x, y + 1) || !Inside(x, y - 1))
                        edge.Add(new Vector2(x, y));
                }
            }

            return edge;
        }

        /// <summary>
        /// What is left to cover from here before a bomb could catch the area's heart - every item one bomb can take
        /// together - measured to the edge of the ground where that bomb could stand; nought when a bomb here would.
        /// </summary>
        internal double LeftToHeart(PlanEnvironment env, Vector2 at, int area)
        {
            var (items, edge) = _hearts[area];

            if (edge == null)
            {
                var only = _items[items[0]];

                return Math.Max(0d, Vector2.Distance(at, only.Grid) - (env.Blast + only.Radius));
            }

            var inside = true;

            foreach (var i in items)
            {
                var reach = env.Blast + _items[i].Radius;

                inside &= Vector2.DistanceSquared(at, _items[i].Grid) <= reach * reach;
            }

            if (inside)
                return 0d;

            var least = double.MaxValue;

            foreach (var cell in edge)
                least = Math.Min(least, Vector2.Distance(at, cell));

            return least;
        }

        /// <summary>The area of the heaviest item a bomb here catches, or -1 when it catches none.</summary>
        internal int AreaCaughtAt(PlanEnvironment env, Vector2 at)
        {
            var best = -1;
            var heaviest = float.MinValue;

            for (var i = 0; i < _items.Count; i++)
            {
                var reach = env.Blast + _items[i].Radius;

                if (Vector2.DistanceSquared(at, _items[i].Grid) > reach * reach)
                    continue;

                var worth = Planner.WorthOfTarget(_items[i]);

                if (worth > heaviest)
                {
                    heaviest = worth;
                    best = _areaOfItem[i];
                }
            }

            return best;
        }

        /// <summary>Whether these links already catch anything in the area.</summary>
        internal bool Touched(PlanEnvironment env, IReadOnlyList<Vector2> links, int area)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_areaOfItem[i] == area && Caught(links, _items[i], env))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// What is left to cover from here before a bomb could catch something in the area: the distance to its nearest
        /// item less the blast and that item's extent, nought when a bomb here would already catch one.
        /// </summary>
        internal double LeftTo(PlanEnvironment env, Vector2 at, int area)
        {
            var least = double.MaxValue;

            for (var i = 0; i < _items.Count; i++)
            {
                if (_areaOfItem[i] != area)
                    continue;

                var left = Math.Max(0d, Vector2.Distance(at, _items[i].Grid) - (env.Blast + _items[i].Radius));

                if (left < least)
                    least = left;
            }

            return least;
        }

        /// <summary>The area's name, its heaviest item's kind and position, for the panel and the dump.</summary>
        internal string Name(int area) => area < 0 || area >= _named.Count
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"{_named[area].Kind} ({_named[area].Grid.X:0},{_named[area].Grid.Y:0})");
    }

    /// <summary>The heavy areas of an environment, worked out once for it. See HeavyAreas.</summary>
    internal static HeavyAreas AreasOf(PlanEnvironment env) => AreasByEnvironment.GetValue(env, e => new HeavyAreas(e));

    private static readonly ConditionalWeakTable<PlanEnvironment, HeavyAreas> AreasByEnvironment = new();

    /// <summary>
    /// Which branch an opening belongs to, and which variation of it: the heavy area its first link catches, and
    /// the heavy area its second link catches. See HeavyAreas.
    ///
    /// **By what a link catches, not by where it stands.** Grouped by exact position, three first links within
    /// eight grid of each other - (611,846), (614,852) and (611,854) on Craggy Peninsula - were three branches,
    /// so six of eight enumerated openings sat in one place while the pool was told it had four. The same holds
    /// one level down: (673,935), (685,934) and (690,928) were three variations. Two links catching the same
    /// heavy content are two ways of doing one thing, which is the rule the pruning of placements already uses.
    ///
    /// **By area, not by the exact set caught.** Keyed on the exact set, a remnant alone and the same remnant with its
    /// relic were two branches, and a relic on its own a third, so the list filled with small branches of one area.
    ///
    /// A link that catches no heavy area keys on its position, so it is never grouped with another.
    /// </summary>
    internal static (string Branch, string Variation) BranchOfOpening(PlanEnvironment env, List<PlanTarget> heavy,
        List<Vector2> links)
    {
        var areas = AreasOf(env);

        string KeyOfLink(Vector2 at)
        {
            var area = areas.AreaCaughtAt(env, at);

            return area >= 0
                ? areas.Name(area)
                : string.Create(CultureInfo.InvariantCulture, $"at {at.X:0},{at.Y:0}");
        }

        return (links.Count > 0 ? KeyOfLink(links[0]) : "", links.Count > 1 ? KeyOfLink(links[1]) : "");
    }

    /// <summary>
    /// The openings as the pool was given them, branch by branch, for reading against what each worker actually
    /// opened with.
    ///
    /// **Because a count cannot say whether the opening survived.** The dump reported how many openings were
    /// enumerated and what each worker ended on, and those two were not comparable - so a worker whose first
    /// link had been moved by a shake or a tear read exactly like a worker that had been seeded there. On the
    /// measured press three of the four first links the workers ended on were not enumerated openings at all,
    /// and nothing on the page said so.
    ///
    /// Grouped by the branch key, which is what the pool is spread over. See OpeningForWorker and BranchOfOpening.
    /// </summary>
    public static string Spelled()
    {
        var last = _last;

        if (last.Count == 0)
            return "    none enumerated";

        var branches = new List<(string Branch, Vector2 First, List<Opening> These)>();

        foreach (var opening in last)
        {
            if (opening.Links is not { Count: > 0 })
                continue;

            var at = branches.FindIndex(b => b.Branch == opening.Branch);

            if (at < 0)
            {
                branches.Add((opening.Branch, opening.Links[0], []));
                at = branches.Count - 1;
            }

            branches[at].These.Add(opening);
        }

        var b = new StringBuilder();

        foreach (var (branch, first, these) in branches)
        {
            var variations = new HashSet<string>();

            foreach (var opening in these)
                variations.Add(opening.Variation);

            b.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    branch ({first.X:0},{first.Y:0}), first link catching heavy content [{branch}]: " +
                $"{these.Count} opening(s) in {variations.Count} variation(s)"));

            foreach (var opening in these)
            {
                var links = new StringBuilder();

                foreach (var at in opening.Links)
                    links.Append(string.Create(CultureInfo.InvariantCulture, $"({at.X:0},{at.Y:0}) "));

                b.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"      {opening.Completed,8:N0}  {links}"));
            }
        }

        return b.ToString().TrimEnd();
    }

    /// <summary>
    /// How many times the last enumeration asked whether a wire lands, which is the expensive question.
    ///
    /// Counted because the ring sampling asks it for every whole cell on every anchor's ring, and in game each
    /// one routes a wire. See Generate, where the cost is reported.
    /// </summary>
    internal static long Asked { get; private set; }

    /// <summary>Of Asked, the questions about spots that catch heavy content, for the Said line.</summary>
    internal static int AskedForCatching { get; private set; }

    /// <summary>Of Asked, the questions about approach spots, asked nearest first. See Anchored.</summary>
    internal static int AskedForApproach { get; private set; }

    /// <summary>Of Asked, the questions about spots on the anchors' rings. See RingSpotsOfAnchor.</summary>
    internal static int AskedOnRings { get; private set; }

    /// <summary>What a bomb here catches, in weight.</summary>
    internal static double Catches(PlanEnvironment env, Vector2 at)
    {
        var total = 0d;

        foreach (var target in env.Targets)
        {
            var reach = env.Blast + target.Radius;

            if (Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                total += target.Weight;
        }

        return total;
    }

    /// <summary>
    /// What a chain of <paramref name="horizon"/> links built from this prefix scores, in what the site pays.
    ///
    /// **A few links, not the whole chain.** Completing all fifteen is the only expensive thing here and it
    /// answers more than the question: what an OPENING is worth is settled by what the first few links reach,
    /// and the eleven after them are the search's business rather than the opening's. Three links is a bomb of
    /// lookahead past a two-link opening, which is enough to tell a route that leads somewhere from one that
    /// leads into a corner - and it is five times cheaper than the full build.
    ///
    /// Bounded by handing Greedy a shorter site: it builds until the chain is as long as the environment says
    /// there are explosives, so an environment claiming three is a three-link build. The SCORE is taken
    /// against the real environment, so the weights and the propagation are the site's own.
    ///
    /// Greedy and deterministic, so two prefixes are compared on the same terms. It is a floor rather than a
    /// prediction - a worker improving from the same prefix will do better - and a floor measured the same way
    /// for every candidate is what a ranking needs.
    /// </summary>
    private static double Finished(PlanEnvironment env, List<Vector2> candidates, List<Vector2> prefix,
        int horizon)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            return FinishedUntimed(env, candidates, prefix, horizon);
        }
        finally
        {
            RolloutTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
        }
    }

    /// <summary>
    /// The candidates a rollout from this prefix could ever reach: within the reach of one explosive per link still
    /// to add, measured straight from the prefix's last spot, plus two grid for Planner.Span's half cell a side.
    ///
    /// **An exact cut, not a shortlist.** A rollout extends from the end of its prefix and every link it adds must be
    /// reachable from the one before, and a wire is never shorter than the straight line - so nothing outside this
    /// can be chosen, and leaving it out changes no answer. Measured on Craggy Peninsula: 51 rollouts over all 7,705
    /// candidates took 5,374ms of a 5,826ms enumeration. The workers' 600-spot shortlist was tried first and is
    /// wrong for this: on the Basin snapshot it pushed the verified best second link, (1149,691), from second to
    /// eighth, because its completion uses a spot the shortlist leaves out.
    /// </summary>
    private static List<Vector2> Within(PlanEnvironment env, List<Vector2> candidates, List<Vector2> prefix,
        int links)
    {
        if (prefix is not { Count: > 0 } || candidates == null)
            return candidates;

        var from = prefix[^1];
        var bound = env.Reach * Math.Max(0, links - prefix.Count) + 2f;
        var near = new List<Vector2>();

        foreach (var at in candidates)
        {
            if (Vector2.DistanceSquared(at, from) <= bound * bound)
                near.Add(at);
        }

        return near;
    }

    /// <summary>The body of Finished, which times it. See Finished.</summary>
    private static double FinishedUntimed(PlanEnvironment env, List<Vector2> candidates, List<Vector2> prefix,
        int horizon)
    {
        var links = Math.Max(prefix.Count, Math.Min(horizon, env.Explosives));
        var whole = Planner.Greedy(env with { Explosives = links }, Within(env, candidates, prefix, links), null, 1,
            null, prefix);

        return whole is { Count: > 0 } ? Planner.Plainly(env, whole) : 0d;
    }
}
