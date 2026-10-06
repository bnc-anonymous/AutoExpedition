using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// Chains built from an order of the capturable remnants: every order of them is tried, each is laid as the fewest
/// links that reach one remnant after another, the explosives left over are spent greedily, and every result is scored
/// by the objective.
///
/// **The order of the remnants is the decision the search does not make.** It builds and repairs a chain a link at a
/// time, and the order falls out of those steps. Measured on Stagnant Basin (2026-10-03): four fresh presses caught the
/// same seven remnants as a route the player found by hand, or six of them, and scored 11-16% below it by catching
/// them in a different order. Each step towards the better order looked worse than going the other way when it was
/// taken, so no step took it.
///
/// **Built on the solver's own spots and reach.** The links are Planner.Candidates, a link stands when Planner.Says
/// answers yes, leftover explosives are spent inside the chain or after it, whichever scores more, and the score is Planner.Plainly - so a chain from
/// here is one the search could have built, and it loads, scores and places as any other.
///
/// **Between two remnants, the fewest links, and of those the richest.** A leg is a breadth-first walk over the spots
/// from where the chain stands to any spot catching the next remnant; among the walks of least length the one whose
/// spots catch most is kept, counting each spot's content alone, so content two spots share counts twice there. The
/// chain is scored properly afterwards.
/// </summary>
internal static class RemnantOrder
{
    /// <summary>One order tried, the chain built from it and what that scores. Order holds target indices.</summary>
    /// <param name="Ranked">
    /// What the order is ranked by: the objective's whole score, which holds the must-take requirement, so an order
    /// missing a must-take ranks below every order holding them all. Plain is what it is worth. See Planner.Score.
    /// </param>
    internal sealed record Tried(List<int> Order, List<Vector2> Chain, double Plain, int LinksToRemnants,
        List<(int From, List<int> Leg)> Legs = null, double Ranked = 0d);


    /// <summary>Where the marker taken last stands, or zero when there is none, for the order keys. See KeyOf.</summary>
    private static System.Numerics.Vector2 TakenLastGridOf(PlanEnvironment env) =>
        env.TakenLast >= 0 && env.TakenLast < env.Targets.Count ? env.Targets[env.TakenLast].Grid : default;

    /// <summary>
    /// A digest of everything Search reads, for Latest to tell one site state from the next: the detonator, reach, blast, explosives and those already down, and per
    /// target its place, kind, weight, carry, waves and what it sets off, with every reward a remnant offers - its value,
    /// carry, local worth and runes. The ground is not in it: the spots and their reach are asked of the router, which
    /// learns more of the site as it goes, so an order impossible once can stand later; more of the site loading changes
    /// the targets, which is in it.
    ///
    /// **Indifferent to the order of the targets**, as Openings.Key is: the scan does not promise one, and a digest that
    /// rolled the targets in order changed every solve although the site had not. Each target's digest is added.
    /// Not indifferent to the order of a set-off list or a reward's runes, which are the target's own.
    /// </summary>
    private static long KeyOf(PlanEnvironment env)
    {
        // **The marks too**: a must take is a stop of its own and the marker taken last ends every order, so orders
        // finished before a mark are not this site state's. They were taken as current: on an Exhumed Ruins site
        // (2026-10-06) the orders came from a search over 9 stops while the marks asked for a tenth.
        var site = HashCode.Combine(env.Origin, env.Reach, env.Blast, env.Explosives, env.Placed?.Count ?? 0,
            TakenLastGridOf(env));
        var targets = 0L;

        foreach (var target in env.Targets)
        {
            var hash = new HashCode();

            hash.Add(target.Grid);
            hash.Add(target.Kind);
            hash.Add(target.Must);
            hash.Add(target.Weight);
            hash.Add(target.Carries);
            hash.Add(target.Waves);

            foreach (var also in target.Sets ?? [])
                hash.Add(also);

            foreach (var choice in target.Choices ?? [])
            {
                hash.Add(choice.Reward);
                hash.Add(choice.Carries);
                hash.Add(choice.Local);

                foreach (var rune in choice.Runes ?? [])
                    hash.Add(rune);
            }

            targets = unchecked(targets + hash.ToHashCode());
        }

        return unchecked(targets * 31 + site * 4294967296L + env.Targets.Count);
    }

    /// <summary>
    /// The orders of the capturable remnants, best first, with the chains built from them. Every order of every subset
    /// that leaves out at most <paramref name="mayLeaveOut"/> remnants, as long as there are at most
    /// <paramref name="mostRemnants"/> to order - beyond that the richest are ordered and the rest are left to the
    /// leftover explosives.
    /// </summary>
    ///
    /// **The ground is kept between searches, the value is not.** Which spots there are, which reach which and what each
    /// blast takes depend on the ground and where the targets stand; what a spot is worth and how the chains score
    /// depend on the targets' weights and rewards. A reroll changes only the second, so the walker is kept while the
    /// ground key holds and only told the new values. See GroundKeyOf and Walker.Revalued.
    /// </summary>
    /// <param name="everyOrder">
    /// Every order of up to <paramref name="mostRemnants"/> remnants, leaving out at most <paramref name="mayLeaveOut"/>,
    /// instead of the orders the measured rules allow - for checking the rules against. See Walker.Grow.
    /// </param>
    /// <param name="budgetMs">How long the orders may be grown for; the best laid by then are kept. See Walker.Grow.</param>
    /// <param name="stop">Asked as the orders grow; true ends the growing early, as a newer site state does. See Feed.</param>
    /// <param name="relics">Whether relics multiplying monsters are stops of their own. See Multiplies.</param>
    /// <param name="progress">
    /// Given the best orders so far, finished as the answer is, each time the orders back from one last stop are done,
    /// so they can be handed out before the search ends. See Feed.
    /// </param>
    /// <param name="parts">
    /// How many parts the last stops are shared between, each grown on a thread of its own with a walker of its own.
    /// One grows them all on the calling thread. See SearchAcrossThreads.
    /// </param>
    /// <param name="runPart">Runs one part, on a thread the caller provides; needed when parts is more than one.</param>
    internal static List<Tried> Search(PlanEnvironment env, int mayLeaveOut = 1, int mostRemnants = 9, int keep = 20,
        bool everyOrder = false, long budgetMs = long.MaxValue, Func<bool> stop = null, bool relics = true,
        Action<List<Tried>> progress = null, int parts = 1, Func<Action, System.Threading.Tasks.Task> runPart = null)
    {
        lock (Gate)
            return SearchOneAtATime(env, mayLeaveOut, mostRemnants, keep, everyOrder, budgetMs, stop, relics, progress,
                runPart == null ? 1 : Math.Max(1, parts), runPart);
    }

    /// <summary>
    /// Builds the orders for the site as it stands on several threads at once, publishing them as each part ends and
    /// when all have, and answers them; for a solve with no orders for this ground to start from, whose workers opening
    /// on orders wait for them. See Planning.Start.
    ///
    /// **On threads the waiting workers leave, not on threads of its own.** The order workers wait before taking a
    /// slot of the solve's thread count, as the workers opening on the enumeration do, so the parts run in their
    /// slots and the solve never runs more threads than it is allowed. Grown on one thread at low priority beside the
    /// workers, the search took 19.4 s after a reset on a Frigid Bluffs site (2026-10-04), and the workers climbed on
    /// fallback openings until it was done.
    /// </summary>
    internal static IReadOnlyList<List<Vector2>> SearchAcrossThreads(PlanEnvironment env, int parts,
        Func<Action, System.Threading.Tasks.Task> runPart, Func<bool> stop)
    {
        var key = KeyOf(env);

        lock (FeedGate)
        {
            _wanted = (env, key);
            _building = key;
        }

        try
        {
            IReadOnlyList<List<Vector2>> Told()
            {
                lock (FeedGate)
                    return _publishedKey == key ? _published : null;
            }

            // **The slots taken once and held until the search is done.** Taken per piece of work, they could be given
            // back to the order workers waiting on the threads before the search had finished. See HeldParts.
            using var held = new HeldParts(parts, runPart);

            // **One pass, not a quick pass on shorter neighbour lists and then a full one.** The quick pass answered some
            // of the router's questions early, but the full pass asked them all again, the router answering the repeats
            // from its own lengths, and walked every leg again; in game the walking had become the cost, so on a Frigid
            // Bluffs site (2026-10-04) the two passes took about 18 s where the full one alone took 9.2 s, and nothing
            // used the quick pass's orders, the order workers waiting for the finished search.
            var found = Search(env, keep: Published, budgetMs: SearchMs, stop: stop,
                progress: x => PublishFor(env, key, x, false), parts: parts, runPart: held.Run);

            // A search cut short by a newer solve is not the last word on its site state; published as finished, the next
            // solve took its partial orders as final.
            PublishFor(env, key, found, !(stop?.Invoke() ?? false));

            return Told();
        }
        finally
        {
            lock (FeedGate)
                _building = 0;
        }
    }

    /// <summary>
    /// Forgets the orders published, for a reset: without this the next solve found orders for the same ground and
    /// handed them out at once, so a reset did not start the workers from nothing. See Caches.ForgetPlan and Clear.
    /// </summary>
    internal static void ForgetOrders()
    {
        lock (FeedGate)
        {
            _published = null;
            _publishedKey = 0;
            _publishedStops = 0;
            _publishedGround = 0;
            _publishedFinished = false;
        }
    }

    /// <summary>
    /// Forgets the orders and the ground - each spot's catches and neighbours, the latter the router's answers - for a
    /// reset that throws away what was worked out about the ground. The walkers on it are built again at the next
    /// search, so a search running on another thread is not waited for here. Without it, after a cold start on a
    /// Frigid Bluffs site (2026-10-04) the order search read its ground as kept and asked the router nothing. See
    /// SpotGround.Forget and Caches.Clear.
    /// </summary>
    internal static void ForgetGround()
    {
        ForgetOrders();
        SpotGround.Forget();
    }

    /// <summary>
    /// Threads that each take a slot once, through the caller's runner, and run the parts given to them until disposed,
    /// so a search keeps its slots until it is done. A part given when no holder is left, all of them having been
    /// refused a slot by a cancelled solve, counts as done at once rather than being waited for. See
    /// SearchAcrossThreads.
    /// </summary>
    private sealed class HeldParts : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Part, System.Threading.Tasks.TaskCompletionSource Done)> _parts = new();
        private int _holding;

        public HeldParts(int count, Func<Action, System.Threading.Tasks.Task> runPart)
        {
            _holding = count;

            for (var i = 0; i < count; i++)
            {
                runPart(() =>
                {
                    foreach (var (part, done) in _parts.GetConsumingEnumerable())
                    {
                        try
                        {
                            part();
                            done.TrySetResult();
                        }
                        catch (Exception e)
                        {
                            done.TrySetException(e);
                        }
                    }
                }).ContinueWith(_ =>
                {
                    // The last holder gone: whatever is left waiting will not be run.
                    if (Interlocked.Decrement(ref _holding) == 0)
                    {
                        while (_parts.TryTake(out var left))
                            left.Done.TrySetResult();
                    }
                });
            }
        }

        /// <summary>Gives a part to a holder, answering when it has run.</summary>
        public System.Threading.Tasks.Task Run(Action part)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            if (Volatile.Read(ref _holding) == 0 || _parts.IsAddingCompleted)
                done.TrySetResult();
            else
                _parts.Add((part, done));

            return done.Task;
        }

        public void Dispose() => _parts.CompleteAdding();
    }

    /// <summary>
    /// The record of one last stop shared between the units growing it on several threads, against which each unit
    /// counts its own patience. See SearchOneAtATime and UnitPatienceOrders.
    /// </summary>
    private sealed class SharedPatience
    {
        private readonly object _gate = new();
        private double _best = double.NegativeInfinity;
        private int _improvements;
        private int _unitsRunning;
        private int _unitsSpent;

        /// <summary>The best rank any unit of this last stop has laid.</summary>
        public double Best
        {
            get
            {
                lock (_gate)
                    return _best;
            }
        }

        /// <summary>How many times the best has improved, for a unit to tell whether it has since it last looked.</summary>
        public int Improvements
        {
            get
            {
                lock (_gate)
                    return _improvements;
            }
        }

        /// <summary>
        /// Whether the last stop is done with: a unit has run out of patience and none since has improved the best. Its
        /// remaining units are not started. See SearchOneAtATime.
        /// </summary>
        public bool Spent
        {
            get
            {
                lock (_gate)
                    return _unitsSpent > 0;
            }
        }

        public void Ended(double ranked)
        {
            lock (_gate)
            {
                if (ranked > _best)
                {
                    _best = ranked;
                    _improvements++;
                    _unitsSpent = 0;
                }
            }
        }

        /// <summary>Told that a unit has stopped for want of a better order. See Spent.</summary>
        public void UnitSpent()
        {
            lock (_gate)
                _unitsSpent++;
        }
    }

    /// <summary>
    /// The first <paramref name="keep"/> of the orders, best first, passing over any sharing more than MostSharedLinks of
    /// its links with one of a different order kept already; the orders passed over fill what is left after.
    ///
    /// **Orders that differ, for the workers to search from.** The best few were all one chain: on a Frigid Bluffs site
    /// (2026-10-04) the eight published all ended on the same spot, sharing 8 to 12 of their 15 links with the best, so
    /// the six workers opening on orders searched copies of one chain, and a worker done with its own had nothing new to
    /// move to. The two shapes of one order are not compared, since keeping both is deliberate. See Finished.
    /// </summary>
    private static List<Tried> Unalike(List<Tried> ranked, int keep)
    {
        var kept = new List<Tried>();

        bool Shares(Tried a, Tried b) =>
            !a.Order.SequenceEqual(b.Order) &&
            a.Chain.Count(p => b.Chain.Any(q => Vector2.DistanceSquared(p, q) < SameLinkGrid * SameLinkGrid)) >
            MostSharedLinks * a.Chain.Count;

        foreach (var tried in ranked)
        {
            if (kept.Count < keep && !kept.Any(x => Shares(tried, x)))
                kept.Add(tried);
        }

        foreach (var tried in ranked)
        {
            if (kept.Count < keep && !kept.Contains(tried))
                kept.Add(tried);
        }

        kept.Sort((a, b) => b.Ranked.CompareTo(a.Ranked));

        return kept;
    }

    /// <summary>The share of an order's links another may hold before the two count as one chain. Chosen. See Unalike.</summary>
    private const float MostSharedLinks = 2f / 3f;

    /// <summary>How near two links are, in grid cells, to count as the same link. Chosen. See Unalike.</summary>
    private const float SameLinkGrid = 8f;

    /// <summary>
    /// The processor time the calling thread has used, user and kernel, in milliseconds; nought where it cannot be read.
    /// Against the wall time a unit took, it says whether the unit was working or waiting for a processor: in game the
    /// search shares the cores with the game at low priority. See UnitsSaid.
    /// </summary>
    private static double ThreadCpuMs()
    {
        try
        {
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
                ? (kernel + user) / 10_000d
                : 0d;
        }
        catch (Exception)
        {
            return 0d;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>The site state SearchAcrossThreads is building orders for, so the background feed leaves it.</summary>
    private static long _building;

    /// <summary>
    /// Whether orders are being built now, across threads or in the background, for work that should wait until the
    /// search has stalled. See Repair.Search.
    /// </summary>
    internal static bool Searching => Interlocked.Read(ref _building) != 0 || Volatile.Read(ref _feeding) is { IsCompleted: false };

    /// <summary>
    /// Whether a search the order workers wait for is running: SearchAcrossThreads, not the background feed. See
    /// Repair's gates on the exhaustive spot search.
    /// </summary>
    internal static bool SearchingAcrossThreads => Interlocked.Read(ref _building) != 0;

    /// <summary>
    /// The orders finished for exactly this site state, or null: what a solve takes without searching. See
    /// Planning.Start.
    /// </summary>
    internal static IReadOnlyList<List<Vector2>> FinishedFor(PlanEnvironment env)
    {
        var key = KeyOf(env);

        lock (FeedGate)
            return _publishedKey == key && _publishedFinished && _published is { Count: > 0 } ? _published : null;
    }

    /// <summary>
    /// The orders published for this ground - the same markers in the same places, what they offer aside - or null:
    /// after a reroll, what a solve hands out at once while the new ones are built. See Planning.Start.
    /// </summary>
    internal static IReadOnlyList<List<Vector2>> PublishedForGround(PlanEnvironment env)
    {
        var ground = GroundKeyOf(env);

        lock (FeedGate)
            return _publishedGround == ground && _published is { Count: > 0 } ? _published : null;
    }

    /// <summary>
    /// The orders published for these stops - the same remnants and multiplying relics in the same places, whatever
    /// else has arrived - or null when there are none; unlike Latest it asks for nothing to be built. See StopsKeyOf.
    /// </summary>
    internal static IReadOnlyList<List<Vector2>> PublishedFor(PlanEnvironment env)
    {
        var stops = StopsKeyOf(env);

        lock (FeedGate)
            return _publishedStops == stops && _published is { Count: > 0 } ? _published : null;
    }

    /// <summary>
    /// Publishes orders found for a site state, unless a newer one is wanted, for Latest and PublishedSince to hand
    /// out. See Feed.
    /// </summary>
    private static void PublishFor(PlanEnvironment env, long key, List<Tried> found, bool finished)
    {
        var chains = found.Select(x => x.Chain).Where(x => Repair.Sound(env, x)).ToList();

        lock (FeedGate)
        {
            if (_wanted.Key != key)
                return;

            _published = chains;
            _publishedKey = key;
            _publishedStops = StopsKeyOf(env);
            _publishedGround = GroundKeyOf(env);
            _publishedFinished = finished;
            _publishedAt = DateTime.UtcNow;
            _publications++;
        }
    }

    /// <summary>
    /// The newest orders for this ground, for the workers with the role, at once - or null when none have been built on
    /// it. Asks for orders for the site as it stands, which the background builds and publishes as it goes. See Feed.
    ///
    /// **Never waited for.** A solve after a roll lasts about a second and the orders took longer than that to build,
    /// so the workers waiting for them never started: in one recording only the other four workers reported at all.
    /// Orders from before a roll stand on the same ground, so they are handed out until the new ones arrive.
    /// </summary>
    internal static IReadOnlyList<List<Vector2>> Latest(PlanEnvironment env)
    {
        var key = KeyOf(env);
        var stops = StopsKeyOf(env);

        lock (FeedGate)
        {
            _wanted = (env, key);

            if (_feeding == null || _feeding.IsCompleted)
                _feeding = BackgroundWork.StartAtLowPriority(() =>
                {
                    Feed();

                    return 0;
                });

            return _publishedStops == stops ? _published : null;
        }
    }

    /// <summary>
    /// Builds what Latest last asked for, again whenever it asks for something newer: one search, its best orders
    /// published each time a last stop is done and again at the end, given up as soon as a newer site state is wanted.
    ///
    /// **One search with the relics as stops, not a quick pass of remnants alone and then a deep one.** Grown back from
    /// the last remnant, the remnants alone took as long as the search with relics (1.7-8.5 s on six sites) and laid a
    /// worse best order on five of the six - 48,986 against 70,030 on a Frigid Bluffs site - and a quick pass held to a
    /// second laid nothing at all on the large sites. Publishing after each last stop is what gets orders to the workers
    /// early instead.
    /// </summary>
    private static void Feed()
    {
        while (true)
        {
            (PlanEnvironment Env, long Key) wanted;

            lock (FeedGate)
            {
                wanted = _wanted;

                if (wanted.Env == null || wanted.Key == _publishedKey && _publishedFinished || wanted.Key == _building)
                    return;
            }

            bool Superseded() => _wanted.Key != wanted.Key;

            var found = Search(wanted.Env, keep: Published, budgetMs: SearchMs, stop: Superseded,
                progress: x => PublishFor(wanted.Env, wanted.Key, x, false));

            PublishFor(wanted.Env, wanted.Key, found, true);
        }
    }

    /// <summary>
    /// What the background has published, for the dump: how many orders, whether the search had finished, when, and
    /// whether it is for the site state last asked about. See Latest.
    /// </summary>
    internal static string FeedSaid
    {
        get
        {
            lock (FeedGate)
            {
                if (_published == null)
                    return "nothing published yet";

                return $"{_published.Count} order(s) from {(_publishedFinished ? "the finished search" : "a search still running")}, " +
                       $"{(DateTime.UtcNow - _publishedAt).TotalSeconds:0}s ago, for " +
                       (_publishedKey == _wanted.Key ? "the site state last asked about" : "an earlier site state (a newer one is being built)");
            }
        }
    }

    private static DateTime _publishedAt;

    private static readonly object FeedGate = new();
    private static (PlanEnvironment Env, long Key) _wanted;
    private static System.Threading.Tasks.Task _feeding;
    private static IReadOnlyList<List<Vector2>> _published;
    private static long _publishedKey;
    private static long _publishedStops;

    /// <summary>The ground the published orders were built on. See PublishedForGround.</summary>
    private static long _publishedGround;
    private static bool _publishedFinished;
    private static int _publications;

    /// <summary>
    /// The orders published for this site state since the publication a worker last took, or null when there are
    /// none newer. For a worker opening on remnant orders to take them while its solve runs, since a solve begun before
    /// they were published had none to give it. See Repair.Search.
    /// </summary>
    /// <param name="taken">The publication last taken, nought for none; moved on to the one returned.</param>
    /// <summary>Whether orders newer than the publication taken have been published for this site state. See PublishedSince.</summary>
    internal static bool PublishedAfter(PlanEnvironment env, int taken)
    {
        lock (FeedGate)
            return _publications != taken && _published is { Count: > 0 } && _publishedStops == StopsKeyOf(env);
    }

    /// <param name="finished">Whether the orders returned are the search's last for this site state.</param>
    internal static IReadOnlyList<List<Vector2>> PublishedSince(PlanEnvironment env, ref int taken, out bool finished)
    {
        lock (FeedGate)
        {
            finished = false;

            if (_publications == taken || _published is not { Count: > 0 } || _publishedStops != StopsKeyOf(env))
                return null;

            taken = _publications;
            finished = _publishedFinished;

            return _published;
        }
    }

    /// <summary>
    /// The most a search grows orders for. Chosen; it runs in the background, publishes as it goes and mostly ends well
    /// before this, when each last stop stops finding better. See RootPatienceOrders.
    /// </summary>
    private const long SearchMs = 30000;

    /// <summary>
    /// How many orders each publication holds for the workers to share out: one each for the workers opening on them,
    /// and the rest for a worker done with its own to move to. See Latest and Repair.NextSpareOrder.
    /// </summary>
    private const int Published = 16;

    /// <summary>
    /// The best chain the order search's own growing lays for one order, grown back from its last stop along it with
    /// every group and end the search keeps, and no patience: what the search would make of the order if it reached
    /// it. Null when no chain is laid. See Walker.FollowedOrder.
    /// </summary>
    internal static (List<Vector2> Chain, double Plain, int Laid)? GrowOrder(PlanEnvironment env, List<Vector2> places,
        List<Vector2> witness = null)
    {
        lock (Gate)
        {
            var order = places
                .Select(at => Enumerable.Range(0, env.Targets.Count)
                    .Where(i => env.Targets[i].Kind == TargetKind.Remnant || Multiplies(env.Targets[i]))
                    .OrderBy(i => Vector2.Distance(env.Targets[i].Grid, at)).First())
                .ToList();
            var ground = SpotGround.Of(env);
            var walker = new Walker(env, ground, order)
            {
                FollowedOrder = Enumerable.Reverse(order).ToList(),
                Witness = witness?.Select(ground.IndexOf).ToList(),
            };
            List<Vector2> best = null;
            var (bestWorth, laid) = (double.NegativeInfinity, 0);

            walker.Grow(order, (_, chain, _, _) =>
            {
                laid++;

                var worth = Planner.Score(env, chain);

                if (worth > bestWorth)
                    (best, bestWorth) = (chain, worth);

                return worth;
            }, 60000, () => false, onlyLasts: new List<int> { order[^1] }, patienceOfLast: new SharedPatience());

            GrowOrderWitnessed = walker.Witness == null ? "" : $"witness: {walker.WitnessFollowed.Matched} of {walker.Witness.Count} link(s) kept; {walker.WitnessFollowed.Lost}";

            return best == null ? null : (best, Planner.Plainly(env, best), laid);
        }
    }

    /// <summary>How far the last GrowOrder kept its witness chain, and where it lost it. See Walker.Witness.</summary>
    internal static string GrowOrderWitnessed { get; private set; } = "";

    /// <summary>Why the last LayOrder returned null. See Walker.LayFailedAt.</summary>
    internal static string LayOrderFailure { get; private set; } = "";

    /// <summary>
    /// One order of remnants laid as Search lays it, with the chain and its score, or null - for the offline harness, to
    /// see how a given order comes out. The remnants are named by their places.
    /// </summary>
    internal static (List<Vector2> Chain, double Plain)? LayOrder(PlanEnvironment env, List<Vector2> remnantPlaces)
    {
        lock (Gate)
        {
            var order = remnantPlaces
                .Select(at => Enumerable.Range(0, env.Targets.Count)
                    .Where(i => env.Targets[i].Kind == TargetKind.Remnant || Multiplies(env.Targets[i]))
                    .OrderBy(i => Vector2.Distance(env.Targets[i].Grid, at)).First())
                .ToList();
            var walker = new Walker(env, SpotGround.Of(env), order);

            if (walker.Lay(order, out _, out _) is { } chain)
                return (chain, Planner.Plainly(env, chain));

            var (index, stop, links) = walker.LayFailedAt;

            LayOrderFailure = index < 0
                ? "the order could not be laid"
                : $"no way on to stop {index + 1} of {order.Count}, {env.Targets[stop].Kind} ({env.Targets[stop].Grid.X:0},{env.Targets[stop].Grid.Y:0}), with {links} of {env.Explosives} links laid - " +
                  string.Join("; ", walker.LayFailedChain.Select(p => $"{p.X:0},{p.Y:0}"));

            return null;
        }
    }

    /// <summary>Serialises Search, which shares its walker between the solve and the chain window.</summary>
    private static readonly object Gate = new();

    /// <summary>The walker for the ground last searched, and that ground's key. See Search.</summary>
    private static Walker _walker;

    /// <summary>The walkers for the parts past the first, on the same ground. See SearchOneAtATime.</summary>
    private static readonly List<Walker> _partWalkers = new();

    /// <summary>
    /// Where the last search spent its time, for the chain window: the ground (spots, and what each blast takes), the
    /// laying of the orders with the reach questions it asked the router, the scoring of the laid chains, and finishing
    /// the best of them. Written under Gate.
    /// </summary>
    internal static string LastTimings { get; private set; } = "not run";

    /// <summary>
    /// How far the order search running now has got, for the score area: seconds since it began, orders grown, the
    /// best order's worth so far, and the share of its units begun - the most it could run, since patience can retire
    /// a last stop's remaining units early, so the share can jump to the end. The share is -1 for a search on one
    /// thread, the background feed's, which has no units. Null when no search is running.
    ///
    /// **A search of eight to twelve seconds said nothing while it ran.** The solve waits on it once its window has
    /// run out, and the score area read "Presolve: 0.0s" for ten seconds and more before the score jumped, with no
    /// sign anything was happening (2026-10-04). See Overlay.
    /// </summary>
    internal static (double Seconds, int Orders, double Best, float Share)? Progress
    {
        get
        {
            if (!Searching || _progressStarted == DateTime.MinValue)
                return null;

            var units = Volatile.Read(ref _progressUnits);

            return ((DateTime.UtcNow - _progressStarted).TotalSeconds, Volatile.Read(ref _progressOrders), _progressBest,
                units <= 0 ? -1f : Math.Clamp(Volatile.Read(ref _progressUnitsBegun) / (float)units, 0f, 1f));
        }
    }

    private static DateTime _progressStarted = DateTime.MinValue;
    private static int _progressOrders;
    private static double _progressBest;
    private static int _progressUnits;
    private static int _progressUnitsBegun;

    /// <summary>
    /// How far inside the reach, in grid units, a clear straight line is taken as landing without routing it. Over every
    /// aim a game session recorded asking, at 4: 55% of a Craggy Peninsula site's pairs and 77% of a Frigid Bluffs
    /// site's are answered this way, and 0.095% and 0.025% of those answers are wrong - the wire bends round a coarse
    /// waypoint the straight line clears. A wrong answer can only cost an order, since every order is checked with the
    /// router before it is published. Cold, the order search ran 5.7 s -> 4.7 s on Craggy and 9.3 s -> 7.1 s on
    /// Frigid. Measured offline with tools/offline --estimate-check, 2026-10-06. See EstimatedReachOf.
    /// </summary>
    private const float EstimatedReachMargin = 4f;

    /// <summary>
    /// The environment the order search walks on: Lands answering yes from Wire.LandsByEstimate where it can and
    /// asking env's own Lands otherwise. Only yes is taken from the estimate: env's Lands can still land a spot the
    /// direct route refuses, through the clamp search, so a no is always asked.
    ///
    /// **The walking only.** PublishFor checks every order with env's own Lands before handing it out, and the
    /// exhaustive spot search, which shares the ground built here, checks every chain it re-lays. The estimate only
    /// ever says yes where the router might say no, so the ground's neighbour lists are a superset of the exact ones
    /// and nothing it misses is lost. Unchanged without a router. See EstimatedReachMargin.
    /// </summary>
    private static PlanEnvironment EstimatedReachOf(PlanEnvironment env)
    {
        if (env.Lands is not { } exact || Terrain.KeptRouter is not { } router)
            return env;

        var reach = env.Reach;
        var margin = EstimatedReachMargin;

        return env with
        {
            Lands = (from, to) =>
            {
                Interlocked.Increment(ref _reachQuestionsWalked);

                if (router.LandsByEstimate(from, to, reach, margin) == true)
                {
                    Interlocked.Increment(ref _reachEstimated);

                    return true;
                }

                return exact(from, to);
            },
        };
    }

    /// <summary>
    /// How many reach questions the walking environment was asked, and how many the estimate answered without routing,
    /// since the search began. Counted across the search's threads, for the timings. See EstimatedReachOf.
    /// </summary>
    private static long _reachQuestionsWalked;

    /// <summary>See _reachQuestionsWalked.</summary>
    private static long _reachEstimated;

    private static List<Tried> SearchOneAtATime(PlanEnvironment env, int mayLeaveOut, int mostRemnants, int keep,
        bool everyOrder, long budgetMs, Func<bool> stop, bool relics, Action<List<Tried>> progress, int parts,
        Func<Action, System.Threading.Tasks.Task> runPart)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Counted per search; searches run one at a time under Gate. See _reachQuestionsWalked.
        Interlocked.Exchange(ref _reachQuestionsWalked, 0);
        Interlocked.Exchange(ref _reachEstimated, 0);

        var remnants = Enumerable.Range(0, env.Targets.Count)
            .Where(i => env.Targets[i].Kind == TargetKind.Remnant && !env.Targets[i].Shunned)
            .OrderByDescending(i => Planner.WorthOfTarget(env.Targets[i]))
            .Take(everyOrder ? mostRemnants : MostRemnantsGrown)
            .ToList();

        // **And the relics that multiply what comes after them**, as stops of their own when the rules grow the orders.
        // On a Frigid Bluffs site (2026-10-03) the player's chain caught a relic duplicating elites, with +40% monster
        // rarity, at link 12 before its last three remnants, and scored 16,566; the same order of remnants laid past it
        // scored 4,130. Scorched Cay 15's Sulphite opening was the same lesson. See Multiplies.
        if (!everyOrder && relics)
            remnants.AddRange(Enumerable.Range(0, env.Targets.Count).Where(i => Multiplies(env.Targets[i])));

        // **And every marker the player marked must take**, whatever it is: the order is where it gets taken, not the
        // leftover explosives. See PlanTarget.Must.
        if (!everyOrder)
            remnants.AddRange(Enumerable.Range(0, env.Targets.Count).Where(i => env.Targets[i].Must && !remnants.Contains(i)));

        var ground = GroundKeyOf(env);
        // Reach for the walking, estimated where the straight line settles it. What is published is checked with
        // env's own, so an estimate that was wrong only loses an order; Search's own callers check what it returns.
        // See EstimatedReachOf.
        var walking = EstimatedReachOf(env);
        // The ground shared with every search on it; a walker on a ground since forgotten or changed is built again.
        var spotGround = SpotGround.Of(walking);
        var kept = _walker != null && ReferenceEquals(_walker.Ground, spotGround);

        if (kept)
            _walker.Revalued(walking, remnants);
        else
            _walker = new Walker(walking, spotGround, remnants);

        var walker = _walker;
        var spots = walker.Spots;

        // A walker for each part past the first, on the same spots, kept with the ground as the first is. A walker
        // remembers its legs and neighbours as it goes, so two threads cannot share one.
        var walkers = new List<Walker> { walker };

        for (var p = 1; p < parts; p++)
        {
            if (_partWalkers.Count >= p && !ReferenceEquals(_partWalkers[p - 1].Ground, spotGround))
                _partWalkers.RemoveRange(p - 1, _partWalkers.Count - (p - 1));

            if (_partWalkers.Count < p)
                _partWalkers.Add(new Walker(walking, spotGround, remnants));
            else
                _partWalkers[p - 1].Revalued(walking, remnants);

            walkers.Add(_partWalkers[p - 1]);
        }

        var groundMs = clock.ElapsedMilliseconds;
        var (reachBefore, reachTicksBefore) = (walker.ReachAsked, walker.ReachTicks);
        var (walksBefore, walksMsBefore) = (walker.WalksDone, walker.WalkTicks * 1000d / System.Diagnostics.Stopwatch.Frequency);
        var allWalkTicksBefore = walkers.Sum(x => x.WalkTicks);
        var (listsBefore, backBefore) = (spotGround.Lists, spotGround.BackChecks);
        var scoringTicks = 0L;
        var laid = new List<Tried>();
        var seen = new HashSet<string>();

        // When the best laid order was laid, and on which remnant it ends, for the timings: how much of a search
        // was needed for its answer. See LastTimings.
        var (bestLaid, bestLaidAtMs, bestLaidEnding) = (double.NegativeInfinity, 0L, -1);

        // The thread time spent publishing while growing and checking laid chains for repeats, and how many
        // publications there were, for the timings. See LaidBy and Laid.
        var (publishTicks, seenTicks, publications) = (0L, 0L, 0);

        // When the orders were last published while being grown, and how many orders have ended. See LaidBy.
        var publishedAtMs = long.MinValue / 2;
        var ordersEnded = 0;

        (_progressStarted, _progressOrders, _progressBest, _progressUnits, _progressUnitsBegun) =
            (DateTime.UtcNow, 0, 0d, 0, 0);

        // **Published as a better order is laid, at most once a ProgressEveryMs, not only when a part ends.** A part
        // keeps growing its last stops for 500 orders past its best, and published only then: after a cold start on a
        // Frigid Bluffs site (2026-10-04) the order workers had their first orders 6.8 s into the solve. Finished by the
        // walker that laid the order, on its own thread, since a walker is not shared between threads.
        double LaidBy(Walker by, List<int> order, List<Vector2> chain, int links, List<(int From, List<int> Leg)> legs)
        {
            Interlocked.Increment(ref ordersEnded);
            Interlocked.Increment(ref _progressOrders);

            var ranked = Laid(order, chain, links, legs);
            var due = false;

            lock (laid)
            {
                if (progress != null && ranked >= bestLaid && clock.ElapsedMilliseconds - publishedAtMs >= ProgressEveryMs)
                {
                    publishedAtMs = clock.ElapsedMilliseconds;
                    due = true;
                }
            }

            if (due)
            {
                var publishing = System.Diagnostics.Stopwatch.GetTimestamp();

                progress(Finished(by));
                Interlocked.Add(ref publishTicks, System.Diagnostics.Stopwatch.GetTimestamp() - publishing);
                Interlocked.Increment(ref publications);
            }

            return ranked;
        }

        double Laid(List<int> order, List<Vector2> chain, int links, List<(int From, List<int> Leg)> legs)
        {
            // Many orders lay the same links - a remnant caught on the way to another costs nothing.
            var checking = System.Diagnostics.Stopwatch.GetTimestamp();
            var said = string.Join(";", chain.Select(p => $"{p.X:0},{p.Y:0}"));
            bool fresh;

            lock (laid)
                fresh = seen.Add(said);

            Interlocked.Add(ref seenTicks, System.Diagnostics.Stopwatch.GetTimestamp() - checking);

            if (!fresh)
                return double.NegativeInfinity;

            var scoring = System.Diagnostics.Stopwatch.GetTimestamp();
            var tried = new Tried(new List<int>(order), chain, Planner.Plainly(env, chain), links, legs, Planner.Score(env, chain));

            lock (laid)
            {
                laid.Add(tried);
                scoringTicks += System.Diagnostics.Stopwatch.GetTimestamp() - scoring;

                if (tried.Ranked > bestLaid)
                {
                    (bestLaid, bestLaidAtMs, bestLaidEnding) = (tried.Ranked, clock.ElapsedMilliseconds, order[^1]);
                    _progressBest = tried.Plain;
                }
            }

            return tried.Ranked;
        }

        // The router's counts as the search began, so the line can say what routing the search itself caused. See
        // RoutingSaid.
        var (routeHitsBefore, routeMissesBefore) = Wire.Routing;
        var routeTicksBefore = Wire.RouteTicks;

        // Each unit of a search split across threads: its last stop, first step, when it began and ended and how many
        // orders it laid, for the timings. See UnitsSaid.
        var unitsRun = new List<(int Last, int Step, long BeganMs, long EndedMs, int Orders, double CpuMs)>();

        if (everyOrder)
        {
            foreach (var subset in SubsetsLeavingOut(remnants.Take(mostRemnants).ToList(), mayLeaveOut))
            {
                foreach (var order in Permutations(subset))
                {
                    if (walker.Lay(order, out var links, out var legs) is { } chain)
                        Laid(order, chain, links, legs);
                }
            }
        }
        else if (walkers.Count == 1)
            walker.Grow(remnants, (o, c, l, g) => LaidBy(walker, o, c, l, g), Math.Max(0L, budgetMs - clock.ElapsedMilliseconds),
                () => stop?.Invoke() ?? false, progress == null ? null : () => progress(Finished(walker)));
        else
        {
            // **Units of a last stop and a first step back from it, taken from a queue by every part.** One part per
            // last stop left the search waiting on the slowest of four: on a Frigid Bluffs site (2026-10-04) the best
            // orders came from one last stop, grown on one thread while the other three had finished, and the parts
            // could not use the six threads the order workers leave. Each part grows with its own walker and publishes
            // as it lays better orders. See LaidBy.
            //
            // **Every last stop's first unit first, then the next unit of whichever last stop has laid the best order so
            // far.** Queued first step by first step across the last stops, the best order on a Frigid Bluffs site
            // (2026-10-04) was laid at 10.2 s of an 11.1 s search, from the last stop whose first order ranked best. A
            // ranking pass ahead of the units, one first order per last stop, cost 6.8 s in game, laying the first legs
            // of four last stops on four threads while the rest waited; the first units lay those orders anyway. See
            // NextUnit.
            var lasts = walker.LastStopsOf(remnants);
            var steps = NearestNext + StrongDetours;
            var nextStep = lasts.ToDictionary(x => x, _ => 0);
            var unitGate = new object();

            // **One record for all the units of a last stop, and a patience for each unit counted against it.** A unit
            // stops once it has laid UnitPatienceOrders orders of its own since the last stop's best last improved,
            // whichever unit improved it. Each unit against its own best, the units grew 13,954 orders where one thread
            // grew 3,241; one count shared by the units let the quick ones spend it while a slow one was still walking
            // the deep branch that held the best order, and in game, where the threads run unevenly, that lost the
            // 70,046 order for 57,363 (2026-10-04). See SharedPatience.
            var patienceOfLasts = lasts.ToDictionary(x => x, _ => new SharedPatience());

            Volatile.Write(ref _progressUnits, lasts.Count * steps);

            bool NextUnit(out (int Last, int Step) unit)
            {
                lock (unitGate)
                {
                    var last = lasts.FirstOrDefault(x => nextStep[x] == 0, -1);

                    if (last < 0)
                        // The fewest units begun first among equals, so last stops none of which has laid an order yet
                        // take turns: picked in list order, one took four units at once on a Frigid Bluffs site
                        // (2026-10-04) while the last stop holding the best order waited 1.3 s for its second.
                        last = lasts.Where(x => nextStep[x] < steps && !patienceOfLasts[x].Spent)
                            .OrderByDescending(x => patienceOfLasts[x].Best).ThenBy(x => nextStep[x]).FirstOrDefault(-1);

                    unit = (last, last < 0 ? 0 : nextStep[last]++);

                    if (last >= 0)
                        Interlocked.Increment(ref _progressUnitsBegun);

                    return last >= 0;
                }
            }

            var tasks = new List<System.Threading.Tasks.Task>();

            foreach (var part in walkers)
            {
                tasks.Add(runPart(() =>
                {
                    while (!(stop?.Invoke() ?? false) && clock.ElapsedMilliseconds < budgetMs && NextUnit(out var unit))
                    {
                        var unitBegan = clock.ElapsedMilliseconds;
                        var cpuBegan = ThreadCpuMs();

                        part.Grow(remnants, (o, c, l, g) => LaidBy(part, o, c, l, g),
                            Math.Max(0L, budgetMs - clock.ElapsedMilliseconds), () => stop?.Invoke() ?? false, null,
                            new List<int> { unit.Last }, unit.Step, patienceOfLasts[unit.Last]);

                        lock (unitsRun)
                            unitsRun.Add((unit.Last, unit.Step, unitBegan, clock.ElapsedMilliseconds, part.Leaves, ThreadCpuMs() - cpuBegan));
                    }

                    if (progress != null)
                        progress(Finished(part));
                }));
            }

            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        }

        var layingMs = clock.ElapsedMilliseconds - groundMs;
        var found = Finished(walker);

        // **The slack spread for the best of them only**, ranked as laid with the fewest links, and the best kept. See
        // Walker.Slackened.
        List<Tried> Finished(Walker finishing)
        {
            var finished = new List<Tried>();
            List<Tried> best;

            // The best laid, and the best back from each last stop, which can rank below all of them and is the most
            // different chain there is. See Unalike.
            lock (laid)
                best = laid.OrderByDescending(x => x.Ranked).Take(Math.Max(keep, FilledIn))
                    .Concat(laid.GroupBy(x => x.Order[^1]).Select(g => g.MaxBy(x => x.Ranked)))
                    .Distinct().ToList();

            foreach (var tried in best)
            {
                // Two explosives too close are refused by the game and by the search's soundness test alike, so a chain
                // breaking the rule could not even be handed to the search. See Walker.Spaced.
                // **Both ways of spending the slack, each kept as its own chain.** Inside the chain is the shape wanted; on the
                // rerolled Scorched Cay of 2026-10-03 the best chain on file runs two links past its last remnant, and seeds
                // with the slack inside lost 9% there to seeds with it appended. Keeping only the better-scoring of the two
                // lost the same 9%: the shape that scores more as a seed is not the one the search does best from, so both go
                // on, for different workers to start from. See ThreadRoles.Opens.RemnantOrder.
                foreach (var shape in new[]
                         {
                             finishing.Slackened(tried.Legs),
                             tried.Chain.Count < env.Explosives ? Planner.Complete(env, tried.Chain, spots) : tried.Chain,
                         })
                {
                    if (shape == null || finishing.Spaced(shape) is not { } chain ||
                        finished.Any(x => x.Chain.SequenceEqual(chain)))
                        continue;

                    finished.Add(tried with { Chain = chain, Plain = Planner.Plainly(env, chain), Ranked = Planner.Score(env, chain) });
                }
            }

            finished.Sort((a, b) => b.Ranked.CompareTo(a.Ranked));

            return Unalike(finished, keep);
        }

        double Ms(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;

        var scoringMs = Ms(scoringTicks);

        // **Where the walking went: building neighbour lists, checking which reach back, and the walk itself.** Walking
        // was 0.74 ms a leg in game against 0.24 offline while scoring ran at the same pace in both, and the walk's
        // time held the router's work for the lists it built on the way - the reaches-back checks uncounted. All thread
        // time, summed over the threads. See SpotGround.Lists.
        // **When each unit ran, to see whether the search ends on one long unit with the other threads idle.** Units in
        // the order they began, as last stop/first step began-ended ms (orders), and the time every thread was busy.
        string UnitsSaid()
        {
            if (unitsRun.Count == 0)
                return "";

            var busyMs = unitsRun.Sum(x => x.EndedMs - x.BeganMs);
            var spanMs = unitsRun.Max(x => x.EndedMs) - unitsRun.Min(x => x.BeganMs);

            return $"units {busyMs:N0} ms busy over {spanMs:N0} ms on {walkers.Count} threads, on the processor for {unitsRun.Sum(x => x.CpuMs):N0} ms of it: " +
                   string.Join(", ", unitsRun.OrderBy(x => x.BeganMs).Select(x =>
                       $"({env.Targets[x.Last].Grid.X:0},{env.Targets[x.Last].Grid.Y:0})/{x.Step} {x.BeganMs:N0}-{x.EndedMs:N0} ({x.Orders})")) + "; ";
        }

        // **How much of the walking was the router routing pairs it had not seen.** Offline the same search walks three
        // times faster than in game while scoring runs at the same pace, so what differs is in the walks.
        string RoutingSaid()
        {
            var (hits, misses) = Wire.Routing;

            return $"router {misses - routeMissesBefore:N0} pair(s) routed afresh in {Ms(Wire.RouteTicks - routeTicksBefore):N0} ms of thread time, " +
                   $"{hits - routeHitsBefore:N0} found already routed; ";
        }

        string WalkingSaid()
        {
            var walkTicks = walkers.Sum(x => x.WalkTicks) - allWalkTicksBefore;
            var (lists, listTicks) = (spotGround.Lists.Built - listsBefore.Built, spotGround.Lists.Ticks - listsBefore.Ticks);
            var (checks, checkTicks) = (spotGround.BackChecks.Checks - backBefore.Checks, spotGround.BackChecks.Ticks - backBefore.Ticks);

            // **The lists and checks are not taken out of the walks.** They are counted on the ground by whichever thread
            // built them, which is not always a walker - another part's thread, or a worker's spot search on the same
            // ground - so subtracting them from the walkers' own time read as negative: "-858 ms" on a Grazed Prairie
            // site (2026-10-06).
            return $"walking {Ms(walkTicks):N0} ms of the walkers' thread time, neighbour lists and checks they waited for included; " +
                   $"on the ground meanwhile, by any thread: {lists:N0} neighbour list(s) built in {Ms(listTicks):N0} ms, " +
                   $"{checks:N0} reaches-back check(s) in {Ms(checkTicks):N0} ms; ";
        }

        var walksMs = walker.WalkTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
        // Counted over the search, not read off the walkers: a walker's counts are its last unit's.
        var leaves = everyOrder || walkers.Count == 1 ? walker.Leaves : ordersEnded;
        var lastsReached = walkers.Count == 1 ? walker.LastStopsReached.Reached : laid.Select(x => x.Order[^1]).Distinct().Count();
        var lastsOffered = walkers.Count == 1 ? walker.LastStopsReached.Offered : walker.LastStopsOf(remnants).Count;

        LastTimings = $"{clock.ElapsedMilliseconds:N0} ms over {remnants.Count} stop(s) " +
                      (walkers.Count > 1 ? $"on {walkers.Count} threads " : "") +
                      $"({(everyOrder ? "every order" : $"{leaves:N0} order(s) grown by the rules, back from {lastsReached} of {lastsOffered} last stops")}): ground {groundMs:N0} ms ({(kept ? "kept" : "worked out")}, {spots.Count:N0} spots); " +
                      // Split across threads, the scoring and the reach questions are summed over the threads, so they are
                      // reported as thread time beside the laying's own time rather than taken out of it.
                      $"laying {(walkers.Count > 1 ? layingMs : layingMs - scoringMs):N0} ms ({walker.WalksDone - walksBefore:N0} legs walked by the first, {walksMs - walksMsBefore:N0} ms) with {walker.ReachAsked - reachBefore:N0} reach question(s) taking " +
                      $"{Ms(walker.ReachTicks - reachTicksBefore):N0} ms{(walkers.Count > 1 ? " of thread time" : "")}; scoring {laid.Count:N0} laid chain(s) {scoringMs:N0} ms{(walkers.Count > 1 ? " of thread time" : "")}; " +
                      $"finishing {clock.ElapsedMilliseconds - groundMs - layingMs:N0} ms; " +
                      UnitsSaid() +
                      RoutingSaid() +
                      $"reach estimated without routing for {Interlocked.Read(ref _reachEstimated):N0} of the walking's " +
                      $"{Interlocked.Read(ref _reachQuestionsWalked):N0} reach question(s); " +
                      $"publishing while growing {publications} time(s) in {Ms(publishTicks):N0} ms of thread time; checking laid chains for repeats {Ms(seenTicks):N0} ms of thread time; " +
                      WalkingSaid() +
                      (bestLaidEnding < 0 ? "nothing laid" : $"the best laid at {bestLaidAtMs:N0} ms, ending on ({env.Targets[bestLaidEnding].Grid.X:0},{env.Targets[bestLaidEnding].Grid.Y:0}); " +
                       $"the best finished worth {(found.Count > 0 ? found[0].Plain : 0d):N0}");

        return found;
    }

    /// <summary>
    /// A digest of what the walker's ground depends on: the detonator or last explosive, reach, blast, explosives and
    /// those down, and per target its place and kind and the places of what it sets off - not weights, carries or
    /// rewards. Indifferent to the targets' order, as KeyOf is.
    /// </summary>
    /// <summary>
    /// What remnant orders are built from, and nothing else: the site, and each remnant and multiplying relic - its
    /// place, its marks and what it offers. Orders are looked up and taken by this, so they carry over while other
    /// markers arrive, the workers scoring each order on the site as it stands.
    ///
    /// **Not every marker.** Keyed on all of them, orders built while a site was scouted belonged to a site that had
    /// changed by the time they were ready, and every pass started the search over: on a Frigid Bluffs site
    /// (2026-10-04) the 8 s search never finished in 38 s of scouting. See Planning.OrderSearchFor.
    /// </summary>
    internal static long StopsKeyOf(PlanEnvironment env)
    {
        var site = HashCode.Combine(env.Origin, env.Reach, env.Blast, env.Explosives, env.Placed?.Count ?? 0,
            TakenLastGridOf(env));
        var stops = 0L;

        foreach (var target in env.Targets)
        {
            // A must take of any kind is a stop of the search, so it is one of the key's too.
            if (target.Kind != TargetKind.Remnant && !Multiplies(target) && !target.Must)
                continue;

            var hash = new HashCode();

            hash.Add(target.Grid);
            hash.Add(target.Kind);
            hash.Add(target.Shunned);
            hash.Add(target.Must);
            hash.Add(target.Weight);
            hash.Add(target.Carries);

            foreach (var choice in target.Choices ?? [])
            {
                hash.Add(choice.Reward);
                hash.Add(choice.Carries);
            }

            stops = unchecked(stops + hash.ToHashCode());
        }

        return unchecked(stops * 31 + site);
    }

    internal static long GroundKeyOf(PlanEnvironment env)
    {
        var site = HashCode.Combine(env.Origin, env.Reach, env.Blast, env.Explosives, env.Placed?.Count ?? 0);
        var targets = 0L;

        foreach (var target in env.Targets)
        {
            var hash = new HashCode();

            hash.Add(target.Grid);
            hash.Add(target.Kind);
            hash.Add(target.Shunned);

            foreach (var also in target.Sets ?? [])
            {
                if (also >= 0 && also < env.Targets.Count)
                    hash.Add(env.Targets[also].Grid);
            }

            targets = unchecked(targets + hash.ToHashCode());
        }

        return unchecked(targets * 31 + site * 4294967296L + env.Targets.Count);
    }

    /// <summary>
    /// Whether a target is a relic whose effects multiply the monsters after it - one that does not stack, such as elites
    /// duplicated, or a scoped effect on monsters, rares or magics - rather than one worth only its own weight or one
    /// lifting chests. Such a relic is ordered like a remnant, so the chain takes it before what it multiplies.
    ///
    /// **Monsters only.** Every scoped relic counted at first, and a site with eight of them branched every step eight
    /// more ways: 155 s and the order limit reached on one Frigid Bluffs site. See PlanTarget.Spread and NonStacking.
    /// </summary>
    internal static bool Multiplies(PlanTarget target) =>
        target.Kind == TargetKind.Relic && !target.Shunned &&
        (target.NonStacking is { Length: > 0 } ||
         (target.Spread ?? []).Any(x => x.Tag == Tags.Monsters || x.Tag == Tags.Rares || x.Tag == Tags.Magics));

    /// <summary>
    /// How many remnants the rules may order. Far more than any site holds; the cap is only against a scan gone wrong.
    /// </summary>
    private const int MostRemnantsGrown = 40;

    /// <summary>How many of the nearest uncaught remnants each step may go to next. See Walker.Grow.</summary>
    private const int NearestNext = 3;

    /// <summary>
    /// The carry a remnant's best reward must reach for any step to detour to it, however far: Rebirth carries about 4,
    /// Opulent 50, most remnants 1-2. See Walker.Grow.
    /// </summary>
    private const float StrongCarry = 3f;

    /// <summary>
    /// How many strong stops - propagators and relics multiplying monsters - beyond the nearest each step may detour to,
    /// nearest first. Chosen, not measured; every one of them was too many on a site with eight relics.
    /// </summary>
    private const int StrongDetours = 2;

    /// <summary>The most orders one search lays, against a site that branches without end. See Walker.Grow.</summary>
    private const int MostLeaves = 20000;

    /// <summary>
    /// How many remnants, the richest, the orders are grown back from as the last one the chain takes. Chosen, not
    /// measured. See Walker.Grow.
    /// </summary>
    private const int LastStops = 4;

    /// <summary>
    /// The least time between two publications of the orders while they are grown, each a better order than any
    /// before. Finishing the best orders for a publication takes 50-100 ms. Chosen. See SearchOneAtATime.
    /// </summary>
    private const long ProgressEveryMs = 1000;

    /// <summary>
    /// How many orders one last stop lays without a better one before the next last stop is begun, its unused time
    /// passing on. Measured on five sites (2026-10-04): each last stop's best came within 0.03-3.6 s of its start, and the
    /// rest of its share, most of a 15-20 s search on the large sites, found nothing better. Chosen from those, not tuned.
    /// See Walker.Grow.
    ///
    /// **Counted in orders, not milliseconds.** In game the search runs at low priority beside the solve's workers, and a
    /// second there laid about half the orders it lays offline - 2,095 in 6.9 s against 3,900 in 7.3 s on one Frigid
    /// Bluffs site - so a limit on the clock stopped each last stop after half the work, and the workers opened on
    /// orders worth 56,000 where the same search offline lays 70,030.
    /// </summary>
    private const int RootPatienceOrders = 500;

    /// <summary>
    /// How many orders a unit of a last stop lays of its own, since the last stop's best last improved, before it stops.
    /// See SharedPatience.
    /// </summary>
    private const int UnitPatienceOrders = 500;

    /// <summary>
    /// How many of the ends tying on the fewest links a partial order keeps, richest first, and how far apart in grid
    /// cells any two must be. Chosen, not measured. See Walker.Advance.
    /// </summary>
    private const int MostEnds = 16;

    private const float EndSpacing = 15f;

    /// <summary>How many of the best laid chains have their slack spread before the final ranking. See Search.</summary>
    private const int FilledIn = 40;


    /// <summary>
    /// Lays orders of remnants as links over the spots, remembering each leg it has walked: a leg depends only on where
    /// the chain stands and which remnant is next, and most orders share most of their legs.
    /// </summary>
    private sealed class Walker
    {
        private PlanEnvironment _env;
        private readonly List<Vector2> _spots;
        private double[] _worthOfSpot;
        private readonly Dictionary<int, HashSet<int>> _catching = new();
        private readonly Dictionary<(long Sources, int Stop, ulong Avoid, bool Backward), (int[] From, List<(int Source, List<int> Path, double Worth)> Legs, int FailedWithin)> _legs = new();
        private const int Detonator = SpotGround.Detonator;

        /// <summary>The spots, what each takes and each spot's neighbours, shared with every search on this ground.</summary>
        public SpotGround Ground { get; }

        /// <summary>The spots, for the caller. See Planner.Candidates.</summary>
        public List<Vector2> Spots => _spots;

        /// <summary>
        /// How many reach questions the ground's neighbours have asked the router, and the ticks they took, by every
        /// walker on it. See SpotGround.Next.
        /// </summary>
        public long ReachAsked => Ground.ReachAsked;

        public long ReachTicks => Ground.ReachTicks;

        /// <summary>
        /// A walker on a ground: the spots, what each takes and each spot's neighbours are the ground's, shared with the
        /// other walkers and with the exhaustive spot search; the legs, which depend on what is being searched, its own.
        ///
        /// **The neighbours shared, not worked out again by each.** They are what the router is asked for, and after a
        /// reload on a Frigid Bluffs site (2026-10-04) the four parts of one search each working out their own asked it
        /// 753,756 questions apiece, 6.7 s of an 11.9 s search. See SpotGround.
        /// </summary>
        public Walker(PlanEnvironment env, SpotGround ground, List<int> remnants)
        {
            _env = env;
            Ground = ground;
            _spots = ground.Spots;

            Revalued(env, remnants);
        }

        /// <summary>
        /// Takes the targets' values from this scan of the same ground: each spot's catches mapped onto its indices, each
        /// spot's worth and the spots catching each remnant worked out again, and the legs forgotten, since which walk is
        /// richest depends on the worths. The spots and their reach are kept. See Search.
        /// </summary>
        public void Revalued(PlanEnvironment env, List<int> remnants)
        {
            _env = env;
            _legs.Clear();
            _catching.Clear();
            _worthOfSpot = new double[_spots.Count];
            _caughtBy = new List<int>[_spots.Count];
            _remnantsOfSpot = new ulong[_spots.Count];
            _avoidedBy = new bool[_spots.Count];
            _bitOf.Clear();

            for (var i = 0; i < remnants.Count && i < 64; i++)
                _bitOf[remnants[i]] = i;

            _caughtBy = Ground.TargetsCaughtAt(env);

            for (var s = 0; s < _spots.Count; s++)
            {

                _avoidedBy[s] = _caughtBy[s].Any(t => env.Targets[t].Shunned);

                foreach (var t in _caughtBy[s])
                {
                    _worthOfSpot[s] += Planner.WorthOfTarget(env.Targets[t]);

                    if (remnants.Contains(t))
                    {
                        if (!_catching.TryGetValue(t, out var at))
                            _catching[t] = at = new HashSet<int>();

                        at.Add(s);
                    }

                    if (_bitOf.TryGetValue(t, out var b))
                        _remnantsOfSpot[s] |= 1UL << b;
                }
            }
        }

        /// <summary>The remnants orders are grown back from: the LastStops richest. See Grow.</summary>
        /// <summary>
        /// The remnants the orders are grown back from: the richest LastStops of them, or only the marker the player
        /// wants taken last when it is a remnant, so every order ends on it. See PlanEnvironment.TakenLast.
        /// </summary>
        public List<int> LastStopsOf(List<int> remnants) =>
            _env.TakenLast >= 0 && _env.Targets[_env.TakenLast].Kind == TargetKind.Remnant && remnants.Contains(_env.TakenLast)
                ? [_env.TakenLast]
                : remnants.Where(r => _env.Targets[r].Kind == TargetKind.Remnant)
                    .OrderByDescending(r => Planner.WorthOfTarget(_env.Targets[r])).Take(LastStops).ToList();

        /// <summary>How many orders the last Grow laid. See Grow.</summary>
        public int Leaves { get; private set; }


        /// <summary>
        /// How many of the last stops the last Grow laid at least one order back from, and how many it was given. See
        /// Grow.
        /// </summary>
        public (int Reached, int Offered) LastStopsReached { get; private set; }

        /// <summary>
        /// The orders the measured rules allow, grown back from the last remnant one stop at a time and handed to the
        /// caller as each one ends, closed to the detonator.
        ///
        /// **The rules, from the best chains on file and 44 recorded Grand runs (2026-10-03):** the next remnant was the
        /// nearest uncaught one 76% of the time and one of the three nearest 99%, and the real detours went to take a
        /// strong propagator early. The nearest-next rule reads the same either way along a chain, so each step back goes
        /// to one of the NearestNext nearest uncaught remnants, by distance from where the chain begins so far, or to any
        /// remnant whose best reward carries StrongCarry or more. A remnant a leg's blasts take on the way, set-offs
        /// included, is caught. A link is taken to reach the same both ways; the chains are tested for soundness after.
        ///
        /// **Grown as a tree, so an ending is laid once** for every order sharing it. A branch ends when no further leg
        /// leaves room to close back to the detonator. Of two branches with the same remnants caught standing on the same spot, one with more links
        /// is dropped; equal ones both go on, since their orders can be worth different amounts.
        /// </summary>
        /// <param name="ended">
        /// Given each order as it ends, first stop first, with its chain; answers what it ranks at, so a last stop's
        /// growing can end once it stops finding better. See RootPatienceOrders.
        /// </param>
        /// <param name="budgetMs">How long the orders may be grown for, shared between the last stops.</param>
        /// <param name="stop">Asked as the orders grow; true ends the growing at once.</param>
        /// <param name="lastStopDone">Called each time the orders back from one last stop are done. See Search.</param>
        /// <param name="onlyLasts">The last stops to grow back from, when a part of a search grows only some. See LastStopsOf.</param>
        /// <param name="firstStep">
        /// Which of the first steps back from the last stop to grow, by its place among the branches, when a unit of a
        /// search split across threads grows one. See SearchOneAtATime.
        /// </param>
        public void Grow(List<int> remnants, Func<List<int>, List<Vector2>, int, List<(int From, List<int> Leg)>, double> ended,
            long budgetMs, Func<bool> stop, Action lastStopDone = null, List<int> onlyLasts = null, int? firstStep = null,
            SharedPatience patienceOfLast = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (leafLimit, msLimit) = (MostLeaves, budgetMs);
            var (rootBest, rootBestAtLeaf) = (double.NegativeInfinity, 0);
            var reached = new HashSet<int>();

            Leaves = 0;
            FromDetonator();

            // A strong propagator, or a relic multiplying what follows: either is worth a detour from anywhere.
            var strong = remnants.Where(r => Multiplies(_env.Targets[r]) || _env.Targets[r].Must ||
                                             (_env.Targets[r].Choices ?? []).Any(c => c.Carries >= StrongCarry)).ToHashSet();
            var fewest = new Dictionary<(ulong Caught, long Ends), int>();

            // **Depth first, every branch the rules allow, until told to stop.** A beam keeping the best partial chains
            // was tried and dropped: the best routes look poor halfway - out to the far end first, or a relic before the
            // remnants it multiplies - and ranked by their score so far they were cut before they paid (14,614 against
            // 9,706 on one Frigid Bluffs site). Searching to the end is slow, so it runs in the background. See Feed.
            // A unit's own orders since its last stop's best last improved, by whichever unit. See SharedPatience.
            var (improvementsSeen, sinceImproved) = (patienceOfLast?.Improvements ?? 0, 0);

            bool Ending() =>
                Leaves >= leafLimit || clock.ElapsedMilliseconds > msLimit ||
                (patienceOfLast != null ? sinceImproved > UnitPatienceOrders : Leaves - rootBestAtLeaf > RootPatienceOrders) ||
                stop();

            void Step(Grown state)
            {
                if (Ending())
                    return;

                var went = false;
                var branches = FollowedOrder is { } followed
                    ? followed.Where(r => !_bitOf.TryGetValue(r, out var bit) || (state.Caught & (1UL << bit)) == 0).Take(1).ToList()
                    : Branches(state, remnants, strong);

                // One first step back only, for a unit of a search split across threads; the order with no step back at
                // all is the first unit's.
                if (firstStep is { } only && state.Order.Count == 1)
                {
                    if (only >= branches.Count)
                        return;

                    branches = new List<int> { branches[only] };
                    went = only > 0;
                }

                // **Asked again before each leg, not only on entry.** Asked only on entry, a branch past the limit still
                // walked its leg before its Step returned at once: the first order of each last stop, which ranks them,
                // walked every branch at every depth on the way back out of the recursion, and on a Frigid Bluffs site
                // (2026-10-04) took 6.8 s in game rather than the handful of legs it needs.
                foreach (var r in branches)
                {
                    if (Ending())
                        return;

                    var advanced = Closable(Advance(state.Ends, r, state.Caught, backward: true));

                    if (Witness != null)
                        Witnessed(state, r, advanced);

                    foreach (var (caught, ends) in advanced)
                    {
                        if (Ending())
                            return;

                        // Of two branches with the same stops taken, standing on the same spots, the first with as few
                        // links goes on. Letting equal ones both go on was exact and, with relics among the stops, without
                        // end.
                        var key = (caught, KeyOfSpots(ends.Select(x => x.At)));
                        var links = ends[0].Chain.Count;

                        if (fewest.TryGetValue(key, out var had) && had <= links)
                            continue;

                        fewest[key] = links;
                        went = true;

                        Step(new Grown(ends, new List<int>(state.Order) { r }, caught));
                    }
                }

                if (!went && Closed(state.Ends) is { } chain)
                {
                    Leaves++;
                    reached.Add(state.Order[0]);

                    var order = new List<int>(state.Order);

                    order.Reverse();

                    var ranked = ended(order, chain.Select(x => _spots[x]).ToList(), chain.Count,
                        new List<(int From, List<int> Leg)> { (Detonator, chain) });

                    if (patienceOfLast != null)
                    {
                        patienceOfLast.Ended(ranked);

                        if (patienceOfLast.Improvements is var now && now != improvementsSeen)
                            (improvementsSeen, sinceImproved) = (now, 0);
                        else if (++sinceImproved > UnitPatienceOrders)
                            patienceOfLast.UnitSpent();
                    }
                    else if (ranked > rootBest)
                        (rootBest, rootBestAtLeaf) = (ranked, Leaves);
                }
            }

            // **Back from the last remnant, not forward from the detonator.** The remnant a chain ends on takes every wave
            // carried down it, so which one that is decides the score more than how the chain begins; the two best chains
            // on one Frigid Bluffs site (2026-10-04, 72,530 and 73,960 by hand) both ended on its one 8-socket remnant.
            // Grown forward, the first stop's orders filled the search - 20,000 orders from 4 of 19 first stops there -
            // and an equal share for every first stop was too little for any to reach its good orders. There are only a
            // few remnants worth ending on, so each of the LastStops richest is given an equal share, what one leaves
            // unused passing to those after it, and the beginning is the last leg laid, back to the detonator.
            var lasts = onlyLasts ?? LastStopsOf(remnants);

            void Grown(int last)
            {
                if (Witness != null)
                {
                    var offered = EndsOn(last);
                    var kept = Closable(offered);
                    var final = Witness[^1];

                    WitnessFollowed = kept.Any(g => g.Ends.Any(e => Near(e.At, final)))
                        ? (1, "")
                        : (0, $"the last link ({_spots[final].X:0},{_spots[final].Y:0}) is not among the ends kept on {_env.Targets[last].Kind} " +
                              $"({_env.Targets[last].Grid.X:0},{_env.Targets[last].Grid.Y:0}): offered {offered.Sum(g => g.Ends.Count)} in {offered.Count} group(s), " +
                              $"{(_catching.TryGetValue(last, out var all) ? all.Count : 0)} spot(s) catch it, it {(all?.Contains(final) == true ? "is" : "is NOT")} one of them; kept " +
                              string.Join(" ", kept.SelectMany(g => g.Ends).Select(e => $"({_spots[e.At].X:0},{_spots[e.At].Y:0})")));
                }

                foreach (var (caught, ends) in Closable(EndsOn(last)))
                {
                    var key = (caught, KeyOfSpots(ends.Select(x => x.At)));

                    if (fewest.TryAdd(key, ends[0].Chain.Count))
                        Step(new Grown(ends, new List<int> { last }, caught));
                }
            }

            // **The last stop whose first order ranks best is grown first.** Its orders are handed to the workers as
            // soon as it is done, and ranked by the worth of their own remnants the last stops put the best last: on a
            // Frigid Bluffs site (2026-10-04) the 70,030 order came from the fourth, after two remnants no good chain
            // reaches, so the workers had nothing like it until the search ended. Each last stop's first order, the
            // nearest at every step, ranked it first there (43,504 against 35,532, 35,194 and 24,903), and costs a
            // handful of legs. Grown again in full afterwards, the orders already laid passed over. See Search.
            var firstOrders = new Dictionary<int, double>();

            foreach (var last in firstStep == null ? lasts : [])
            {
                if (stop())
                    break;

                (leafLimit, msLimit) = (Leaves + 1, budgetMs);
                (rootBest, rootBestAtLeaf) = (double.NegativeInfinity, Leaves);
                Grown(last);
                firstOrders[last] = rootBest;
                fewest.Clear();
            }

            lasts = lasts.OrderByDescending(x => firstOrders.GetValueOrDefault(x, double.NegativeInfinity)).ToList();


            for (var i = 0; i < lasts.Count && !stop(); i++)
            {
                var shares = lasts.Count - i;

                leafLimit = Leaves + (MostLeaves - Leaves) / shares;
                msLimit = clock.ElapsedMilliseconds + Math.Max(0L, budgetMs - clock.ElapsedMilliseconds) / shares;
                (rootBest, rootBestAtLeaf) = (double.NegativeInfinity, Leaves);
                Grown(lasts[i]);
                lastStopDone?.Invoke();
            }

            LastStopsReached = (reached.Count, lasts.Count);
        }

        /// <summary>
        /// The spots taking a stop, as the last link of a chain grown back from it, grouped by what they take and spread
        /// apart as Advance spreads its ends. See Grow.
        /// </summary>
        private List<(ulong Caught, List<Standing> Ends)> EndsOn(int stop)
        {
            var groups = new List<(ulong Caught, List<Standing> Ends)>();

            if (!_catching.TryGetValue(stop, out var spots))
                return groups;

            foreach (var spot in spots)
            {
                if (_avoidedBy[spot])
                    continue;

                var standing = new Standing(new List<int> { spot }, new List<(int From, List<int> Leg)>(), _worthOfSpot[spot]);
                var taken = _remnantsOfSpot[spot];
                var group = groups.FindIndex(g => g.Caught == taken);

                if (group < 0)
                    groups.Add((taken, new List<Standing> { standing }));
                else
                    groups[group].Ends.Add(standing);
            }

            return Spread(groups);
        }

        /// <summary>
        /// The groups keeping only the ends a chain grown back can still close from: its links so far and the fewest
        /// from the detonator to its front within the explosives. A group left with none is dropped. See FromDetonator.
        /// </summary>
        private List<(ulong Caught, List<Standing> Ends)> Closable(List<(ulong Caught, List<Standing> Ends)> groups)
        {
            var kept = new List<(ulong Caught, List<Standing> Ends)>();

            foreach (var (caught, ends) in groups)
            {
                var closable = ends.Where(x => _depthFromDetonator[x.At] > 0 &&
                                               x.Chain.Count + _depthFromDetonator[x.At] - 1 <= _env.Explosives).ToList();

                if (closable.Count > 0)
                    kept.Add((caught, closable));
            }

            return kept;
        }

        /// <summary>
        /// A chain grown back, closed to the detonator and turned the right way round: the richest walk of the fewest
        /// links from the detonator to the richest end it closes from without standing twice on a spot, or null.
        /// </summary>
        private List<int> Closed(List<Standing> ends)
        {
            foreach (var end in ends)
            {
                var opening = new List<int>();

                for (var at = _parentFromDetonator[end.At]; at != Detonator; at = _parentFromDetonator[at])
                    opening.Add(at);

                opening.Reverse();

                if (opening.Count + end.Chain.Count > _env.Explosives || opening.Any(end.Chain.Contains))
                    continue;

                var chain = new List<int>(end.Chain);

                chain.Reverse();
                opening.AddRange(chain);

                return opening;
            }

            return null;
        }

        /// <summary>
        /// The fewest links from the detonator to every spot, and the richest walk of those, for closing a chain grown
        /// back: nought for a spot out of reach within the explosives. Worked out once per search. See Closed.
        /// </summary>
        private void FromDetonator()
        {
            _depthFromDetonator = new int[_spots.Count];
            _parentFromDetonator = new int[_spots.Count];

            var worth = new double[_spots.Count];
            var layer = new List<int> { Detonator };

            for (var level = 1; level <= _env.Explosives && layer.Count > 0; level++)
            {
                var nextLayer = new List<int>();

                foreach (var at in layer)
                {
                    foreach (var to in Next(at))
                    {
                        if (_avoidedBy[to])
                            continue;

                        var richer = (at == Detonator ? 0d : worth[at]) + _worthOfSpot[to];

                        if (_depthFromDetonator[to] > 0)
                        {
                            if (_depthFromDetonator[to] == level && richer > worth[to])
                                (worth[to], _parentFromDetonator[to]) = (richer, at);

                            continue;
                        }

                        _depthFromDetonator[to] = level;
                        _parentFromDetonator[to] = at;
                        worth[to] = richer;
                        nextLayer.Add(to);
                    }
                }

                layer = nextLayer;
            }
        }

        private int[] _depthFromDetonator;
        private int[] _parentFromDetonator;

        /// <summary>
        /// One way a partial chain can stand: its spots, the legs they were laid as, and the summed worth of its spots,
        /// by which the ends of a partial order are ranked. See Advance.
        /// </summary>
        private sealed record Standing(List<int> Chain, List<(int From, List<int> Leg)> Legs, double Worth)
        {
            public int At => Chain.Count == 0 ? Detonator : Chain[^1];

            public static Standing Start => new(new List<int>(), new List<(int From, List<int> Leg)>(), 0d);
        }

        /// <summary>
        /// A partial order: the ways its chain can stand, richest first and all with the same number of links, its stops
        /// and what it has taken. See Grow and Advance.
        /// </summary>
        private sealed record Grown(List<Standing> Ends, List<int> Order, ulong Caught);

        /// <summary>
        /// Where a partial order may go next: the NearestNext nearest uncaught stops and the StrongDetours nearest strong
        /// stops besides, by distance from its richest end. See Grow.
        /// </summary>
        private List<int> Branches(Grown state, List<int> stops, HashSet<int> strong)
        {
            var at = state.Ends[0].At;
            var here = at == Detonator ? _env.Origin : _spots[at];
            var uncaught = stops.Where(r => !_bitOf.TryGetValue(r, out var b) || (state.Caught & (1UL << b)) == 0).ToList();
            var branches = uncaught.OrderBy(r => Vector2.Distance(here, _env.Targets[r].Grid))
                .Take(NearestNext).ToList();

            // A detour to the nearest few strong stops not already among the nearest, wherever they are.
            foreach (var r in uncaught.Where(strong.Contains).Where(r => !branches.Contains(r))
                         .OrderBy(r => Vector2.Distance(here, _env.Targets[r].Grid)).Take(StrongDetours))
                branches.Add(r);

            return branches;
        }

        /// <summary>
        /// One order to grow back along, last stop first, in place of the branches the rules allow: each step back goes to
        /// the next stop of it not yet caught. For telling whether the growing can lay a given order and what it makes
        /// of it. See GrowOrder.
        /// </summary>
        public List<int> FollowedOrder { get; set; }

        /// <summary>
        /// A chain to follow through the growing, as spot indices first link first: how much of its end the ends kept
        /// still match at each step, and where the last of it was dropped. See GrowOrder.
        /// </summary>
        public List<int> Witness { get; set; }

        /// <summary>Whether two spots are within WitnessNear grid of each other, so one stands for the other. See Witness.</summary>
        private bool Near(int a, int b) =>
            a >= 0 && b >= 0 && Vector2.DistanceSquared(_spots[a], _spots[b]) <= WitnessNear * WitnessNear;

        /// <summary>How near a kept spot must be to a witness's to stand for it, in grid cells. Chosen. See Witness.</summary>
        private const float WitnessNear = 20f;

        /// <summary>The most of Witness's end any kept end matched, and what was offered at the step that lost it.</summary>
        public (int Matched, string Lost) WitnessFollowed { get; private set; } = (0, "");

        private void Witnessed(Grown state, int stop, List<(ulong Caught, List<Standing> Ends)> advanced)
        {
            // Near enough, not equal: a spot a few grid from the witness's stands for it. See WitnessNear.
            bool Matches(Standing end) =>
                end.Chain.Count <= Witness.Count &&
                end.Chain.Select((x, i) => (x, i)).All(p => Near(p.x, Witness[Witness.Count - 1 - p.i]));

            var before = state.Ends.Where(Matches).Select(x => x.Chain.Count).DefaultIfEmpty(0).Max();

            if (before == 0 && state.Ends.All(x => x.Chain.Count > 0))
                return;

            var after = advanced.SelectMany(g => g.Ends).Where(Matches).Select(x => x.Chain.Count).DefaultIfEmpty(0).Max();

            if (after > WitnessFollowed.Matched)
                WitnessFollowed = (after, "");
            else if (after == 0 && before >= WitnessFollowed.Matched)
                WitnessFollowed = (before, $"growing to {_env.Targets[stop].Kind} ({_env.Targets[stop].Grid.X:0},{_env.Targets[stop].Grid.Y:0}) from an end matching {before} link(s), " +
                    $"the next of the witness would be ({_spots[Witness[Witness.Count - before - 1]].X:0},{_spots[Witness[Witness.Count - before - 1]].Y:0}); offered " +
                    string.Join(", ", advanced.Select(g => $"[{string.Join(" ", g.Ends.Take(6).Select(e => $"{string.Join("<", e.Chain.Skip(before).Take(3).Select(x => $"({_spots[x].X:0},{_spots[x].Y:0})"))}"))}]")));
        }

        /// <summary>
        /// Where the last Lay that returned null gave up: the place in the order, the stop, and how many links the chain
        /// had by then. See LayOrder.
        /// </summary>
        public (int Index, int Stop, int Links) LayFailedAt { get; private set; } = (-1, -1, 0);

        /// <summary>The richest chain the last failed Lay had laid when it gave up. See LayFailedAt.</summary>
        public List<Vector2> LayFailedChain { get; private set; } = new();

        /// <summary>The chain laying these remnants in this order, or null when the explosives run out first.</summary>
        public List<Vector2> Lay(List<int> order, out int links, out List<(int From, List<int> Leg)> legs)
        {
            LayFailedAt = (-1, -1, 0);

            var ends = new List<Standing> { Standing.Start };
            var caught = 0UL;

            links = 0;
            legs = new List<(int From, List<int> Leg)>();

            ulong BitOf(int stop) => _bitOf.TryGetValue(stop, out var b) ? 1UL << b : 0UL;

            for (var i = 0; i < order.Count; i++)
            {
                // Caught already by a spot on the way, so this remnant costs nothing more.
                if ((caught & BitOf(order[i])) != 0UL)
                    continue;

                var groups = Advance(ends, order[i], caught);

                if (groups.Count == 0)
                {
                    LayFailedAt = (i, order[i], ends[0].Chain.Count);
                    LayFailedChain = ends[0].Chain.Select(x => _spots[x]).ToList();

                    return null;
                }

                // **The group keeping to the order given**: the one taking the longest run of the next remnants in turn,
                // since one blast may take several, and the richest only when none does. A group taking a later remnant
                // out of turn has changed the order.
                int InTurn(ulong taken)
                {
                    var planned = caught;

                    for (var k = i; k < order.Count; k++)
                    {
                        planned |= BitOf(order[k]);

                        if (planned == taken)
                            return k - i + 1;
                    }

                    return 0;
                }

                (caught, ends) = groups.OrderByDescending(g => InTurn(g.Caught)).ThenByDescending(g => g.Ends[0].Worth).First();
            }

            var richest = ends[0];

            links = richest.Chain.Count;
            legs = richest.Legs;

            return richest.Chain.Select(s => _spots[s]).ToList();
        }

        /// <summary>
        /// Every way on from these ends to a spot catching the next stop in the fewest links, grouped by what the chain
        /// has then taken, each group richest first, at most MostEnds long and no two EndSpacing apart; none when the
        /// explosives run out first.
        ///
        /// **Every end that ties on links goes on, not only the richest.** Which spot a leg ends on decides how far the
        /// next leg has to go, and the richest end is often the wrong side of the stop: on a Frigid Bluffs site
        /// (2026-10-04) the relic first taken from (668,838), the richest spot, cost three links on to the next remnant
        /// where (708,821) cost two, and the order of the best chain found by hand ran out of explosives two remnants
        /// early. So each leg is walked from all the ends at once, and the fewest links are the fewest over the order
        /// so far rather than leg by leg.
        /// </summary>
        /// <param name="backward">
        /// The chain is grown back from its end, so a leg is walked towards its beginning: every ordered stop is passed
        /// by but the one gone to, those already in the order because they are caught later, the rest because they
        /// would be caught out of the order grown. See Grow.
        /// </param>
        private List<(ulong Caught, List<Standing> Ends)> Advance(List<Standing> ends, int stop, ulong caught, bool backward = false)
        {
            var groups = new List<(ulong Caught, List<Standing> Ends)>();
            var left = _env.Explosives - ends[0].Chain.Count;

            if (left <= 0)
                return groups;

            // **Not past another remnant before its turn.** A leg may not stand on a spot whose blast takes an ordered
            // remnant still uncaught, other than the one it is going to: the remnant would be caught early, before what
            // the order meant to propagate to it first. On a Frigid Bluffs site (2026-10-03) the player's order laid by a
            // walker that allowed it caught a Rage remnant at link 2 on the way out instead of at link 13, after a Sky
            // Rebirth, and scored 3,807 against the player's 16,566 on the same shape of chain.
            var bitOfTarget = _bitOf.TryGetValue(stop, out var b) ? 1UL << b : 0UL;
            var avoid = (backward ? ulong.MaxValue : ~caught) & ~bitOfTarget & (_bitOf.Count >= 64 ? ulong.MaxValue : (1UL << _bitOf.Count) - 1);
            var sources = ends.Select(x => x.At).Distinct().OrderBy(x => x).ToArray();

            // The walk passing anything is kept and reused while none of its legs happens to pass a remnant not yet due,
            // which is most of the time, since the same ends are then the fewest links away and the same walks the
            // richest; only one that would is walked again around them, and that is kept by what it avoided.
            var walked = Legs(sources, stop, 0UL, left, backward);

            if (walked != null && walked.Any(x => x.Path.Take(x.Path.Count - 1).Any(s => (_remnantsOfSpot[s] & avoid) != 0UL)))
                walked = Legs(sources, stop, avoid, left, backward);

            if (walked == null)
                return groups;

            foreach (var (source, path, worth) in walked)
            {
                foreach (var end in ends)
                {
                    // A leg back over a spot the chain holds is refused outright; one that only crowds the chain is laid
                    // as it is and moved apart afterwards. See Spaced.
                    if (end.At != source || path.Any(end.Chain.Contains))
                        continue;

                    var chain = new List<int>(end.Chain);

                    chain.AddRange(path);

                    var standing = new Standing(chain, new List<(int From, List<int> Leg)>(end.Legs) { (source, path) },
                        end.Worth + worth);
                    var taken = RemnantsTakenBy(path, caught);
                    var group = groups.FindIndex(g => g.Caught == taken);

                    if (group < 0)
                        groups.Add((taken, new List<Standing> { standing }));
                    else
                        groups[group].Ends.Add(standing);
                }
            }

            return Spread(groups);
        }

        /// <summary>The groups' ends, richest first, at most MostEnds and no two within EndSpacing. See Advance.</summary>
        private List<(ulong Caught, List<Standing> Ends)> Spread(List<(ulong Caught, List<Standing> Ends)> groups)
        {
            // **The richest ends spread apart, not the richest ends.** The richest are mostly in one place, where the
            // content is: on that Frigid Bluffs site the six richest spots taking the relic all lay in the cluster of
            // monsters to its west, and the next remnant was north-east. The richest in each of eight directions round
            // the stop was not enough either: (493,713) and (491,737) both take the next relic from the same side, and
            // only the second is a link from where the chain goes after.
            for (var g = 0; g < groups.Count; g++)
            {
                var kept = new List<Standing>();

                foreach (var end in groups[g].Ends.OrderByDescending(x => x.Worth))
                {
                    if (kept.Count < MostEnds && kept.All(x => Vector2.Distance(_spots[x.At], _spots[end.At]) >= EndSpacing))
                        kept.Add(end);
                }

                groups[g] = (groups[g].Caught, kept);
            }

            return groups;
        }

        /// <summary>
        /// The legs of the fewest links from any of these spots, or the detonator, to each spot catching the stop, the
        /// richest walk to each, or null when none reaches within the explosives left. Remembered per set of starting
        /// spots, stop and what was avoided, since most orders share most of their legs: a failure with the depth it
        /// failed at, a success for good, since a walk breadth first finds the fewest links whatever its limit.
        /// </summary>
        private List<(int Source, List<int> Path, double Worth)> Legs(int[] sources, int stop, ulong avoid, int left, bool backward)
        {
            var key = (KeyOfSpots(sources), stop, avoid, backward);

            if (_legs.TryGetValue(key, out var had) && had.From.AsSpan().SequenceEqual(sources) &&
                (had.Legs != null || had.FailedWithin >= left))
                return had.Legs != null && had.Legs[0].Path.Count > left ? null : had.Legs;

            var walked = Walk(sources, stop, avoid, left, backward);

            _legs[key] = (sources, walked, walked == null ? left : int.MaxValue);

            return walked;
        }

        /// <summary>A digest of a set of spots, whatever their order. See Legs and Grow.</summary>
        private static long KeyOfSpots(IEnumerable<int> spots)
        {
            var key = 17L;

            foreach (var spot in spots.OrderBy(x => x))
                key = unchecked(key * 1_000_003L + spot + 2);

            return key;
        }

        /// <summary>
        /// The chain with its slack spent inside it: the explosives left after the fewest links are inserted one at a
        /// time between two links already there, never after the last, each at the spot and place that catch most
        /// that nothing in the chain catches yet - where the link before reaches it, it reaches the link after, and it
        /// keeps its spacing. Null when nothing can be inserted.
        ///
        /// **Spent between the remnants, never after the last one.** Spending them by Planner.Complete appended them to
        /// the end of the chain, where the leftover explosives went on elite markers past the last remnant - the shape
        /// the live solver ends 8 of 44 recorded Grand chains in, and the best chains on file 1 of 9. A chain that should
        /// run on past its last remnant is for the search to find afterwards, not for this skeleton.
        ///
        /// **Inserted, not walked at a new length.** The richest walk of an exact length between two ends steps back and
        /// forth between rich spots, and a chain cannot stand twice on one, so that left most legs with nothing longer
        /// to offer.
        /// </summary>
        public List<Vector2> Slackened(List<(int From, List<int> Leg)> legs)
        {
            if (legs is not { Count: > 0 })
                return null;

            var chain = legs.SelectMany(x => x.Leg).ToList();
            var slack = _env.Explosives - chain.Count;

            if (slack <= 0)
                return null;

            var caught = new bool[_env.Targets.Count];

            foreach (var at in chain)
                Mark(at, caught);

            var inserted = 0;

            for (var e = 0; e < slack; e++)
            {
                var (bestGain, bestAt, bestSpot) = (0d, -1, -1);
                var laid = chain.Select(x => _spots[x]).ToList();

                for (var i = 0; i < chain.Count; i++)
                {
                    var before = i == 0 ? _env.Origin : _spots[chain[i - 1]];
                    var after = _spots[chain[i]];

                    for (var s = 0; s < _spots.Count; s++)
                    {
                        // The cheap tests first: within reach of both neighbours by distance, then worth, then spacing.
                        // Spacing walked the whole chain for every spot on the site before any of these, and made
                        // finishing the slowest part of a search.
                        if (_worthOfSpot[s] <= bestGain || _avoidedBy[s] ||
                            Planner.Span(before, _spots[s]) > _env.Reach || Planner.Span(_spots[s], after) > _env.Reach ||
                            chain.Contains(s) || !Planner.Spaced(_env, laid, _spots[s]))
                            continue;

                        var gain = Uncaught(s, caught);

                        if (gain <= bestGain || !Reaches(before, _spots[s]) || !Reaches(_spots[s], after))
                            continue;

                        (bestGain, bestAt, bestSpot) = (gain, i, s);
                    }
                }

                if (bestSpot < 0)
                    break;

                chain.Insert(bestAt, bestSpot);
                Mark(bestSpot, caught);
                inserted++;
            }

            return inserted == 0 ? null : chain.Select(x => _spots[x]).ToList();
        }

        /// <summary>Whether a link from one point to another stands, the router asked once per pair. See Slackened.</summary>
        private bool Reaches(Vector2 from, Vector2 to)
        {
            if (!_reachesBetween.TryGetValue((from, to), out var yes))
                _reachesBetween[(from, to)] = yes = Planner.Says(_env, from, to) == Certainty.Yes;

            return yes;
        }

        /// <summary>The router's answers for Reaches, kept with the ground. See Reaches.</summary>
        private readonly Dictionary<(Vector2 From, Vector2 To), bool> _reachesBetween = new();

        /// <summary>Marks every target a spot catches. See Slackened.</summary>
        private void Mark(int spot, bool[] caught)
        {
            foreach (var t in _caughtBy[spot])
                caught[t] = true;
        }

        /// <summary>What a blast at each spot takes, set-offs included, as this scan's indices. See SpotGround and Revalued.</summary>
        private List<int>[] _caughtBy;

        /// <summary>Each ordered remnant's bit, the first 64. See _remnantsOfSpot.</summary>
        private readonly Dictionary<int, int> _bitOf = new();

        /// <summary>Which ordered remnants a blast at each spot takes, as bits. See Walk.</summary>
        private ulong[] _remnantsOfSpot;

        /// <summary>
        /// Whether a blast at each spot takes a target the player marked must avoid, whose weight is then below nothing.
        /// No leg stands on one and no leftover link is put on one. See PlanTarget.Shunned.
        /// </summary>
        private bool[] _avoidedBy;

        /// <summary>The ordered remnants a set of spots takes, as bits, added to those given.</summary>
        private ulong RemnantsTakenBy(IEnumerable<int> spots, ulong taken)
        {
            foreach (var spot in spots)
                taken |= _remnantsOfSpot[spot];

            return taken;
        }

        /// <summary>What a spot catches that nothing marked yet does. See Slackened.</summary>
        private double Uncaught(int spot, bool[] caught)
        {
            var worth = 0d;

            foreach (var t in _caughtBy[spot])
            {
                if (!caught[t])
                    worth += Planner.WorthOfTarget(_env.Targets[t]);
            }

            return worth;
        }

        /// <summary>
        /// The chain with every spot that crowds another moved to the nearest spot that is spaced from the rest and still
        /// links from the spot before and to the spot after, or null when one cannot be. Spacing is left to here
        /// because walking every leg around the chain made laying the orders take minutes, not seconds.
        /// </summary>
        public List<Vector2> Spaced(List<Vector2> chain)
        {
            var moved = new List<Vector2>(chain);

            for (var i = 0; i < moved.Count; i++)
            {
                if (Planner.Spaced(_env, moved, moved[i], i))
                    continue;

                var before = i == 0 ? _env.Origin : moved[i - 1];
                var best = -1;
                var nearest = float.MaxValue;

                for (var s = 0; s < _spots.Count; s++)
                {
                    var away = Vector2.DistanceSquared(_spots[s], moved[i]);

                    if (away >= nearest || !Planner.Spaced(_env, moved, _spots[s], i) ||
                        Planner.Says(_env, before, _spots[s]) != Certainty.Yes ||
                        (i + 1 < moved.Count && Planner.Says(_env, _spots[s], moved[i + 1]) != Certainty.Yes))
                        continue;

                    nearest = away;
                    best = s;
                }

                if (best < 0)
                    return null;

                moved[i] = _spots[best];
            }

            return moved;
        }


        /// <summary>The breadth-first walk behind Legs. See Legs.</summary>
        private List<(int Source, List<int> Path, double Worth)> Walk(int[] sources, int stop, ulong avoid, int within, bool backward)
        {
            var clock = System.Diagnostics.Stopwatch.GetTimestamp();

            WalksDone++;

            try
            {
                return WalkArrays(sources, stop, avoid, within, backward);
            }
            finally
            {
                WalkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - clock;
            }
        }

        /// <summary>How many legs have been walked, and the ticks they took, for the timings. See Walk.</summary>
        public long WalksDone { get; private set; }

        public long WalkTicks { get; private set; }

        /// <summary>
        /// Walk's breadth-first search from every starting spot at once, on arrays the size of the spots reused from one
        /// walk to the next and told apart by a stamp per walk, rather than on dictionaries made each time. The detonator,
        /// which is no spot, is a start only, and stands at depth nought by itself. Every spot catching the stop at the
        /// first depth any does is an end, each with its richest walk back to the start it came from.
        ///
        /// **Arrays because the walks were most of a search.** On a Frigid Bluffs site with 19 stops, laying took 29.7 of
        /// a 30 s budget, nearly all of it in legs walked over 6,671 spots.
        /// </summary>
        /// <param name="backward">
        /// Walked towards the chain's beginning, so each step must reach from the spot stepped to back to the one stepped
        /// from, the way the explosives will be placed. Reach is not the same both ways where the wire bends round
        /// something, and chains grown back over the forward answers failed the soundness test. See Grow.
        /// </param>
        private List<(int Source, List<int> Path, double Worth)> WalkArrays(int[] sources, int stop, ulong avoid, int within, bool backward)
        {
            if (!_catching.TryGetValue(stop, out var goals))
                return null;

            if (_depth == null || _depth.Length != _spots.Count)
            {
                _depth = new int[_spots.Count];
                _worthSoFar = new double[_spots.Count];
                _parentOf = new int[_spots.Count];
                _stamp = new int[_spots.Count];
            }

            var stamp = ++_stampNow;

            foreach (var source in sources)
            {
                if (source == Detonator)
                    continue;

                _stamp[source] = stamp;
                _depth[source] = 0;
                _worthSoFar[source] = 0d;
            }

            double WorthAt(int at) => at == Detonator ? 0d : _worthSoFar[at];
            bool IsSource(int at) => at == Detonator || _stamp[at] == stamp && _depth[at] == 0;

            var layer = new List<int>(sources);
            var level = 0;

            while (layer.Count > 0 && level < Math.Min(within, _env.Explosives))
            {
                level++;
                var nextLayer = new List<int>();

                foreach (var at in layer)
                {
                    foreach (var to in backward ? Previous(at) : Next(at))
                    {
                        // A spot taking a remnant not yet due is passed by, unless it is where the leg ends, and one
                        // taking a must-avoid is never stood on. See Advance and _avoidedBy.
                        if (_avoidedBy[to] || ((_remnantsOfSpot[to] & avoid) != 0UL && !goals.Contains(to)))
                            continue;

                        // **Not onto a spot the game would refuse beside the one before.** Two explosives closer than
                        // Apart are refused, and two such spots take much the same, so a step between them added the same
                        // catches twice and read as the richest way on: grown back along the order of a 74,000 chain on a
                        // Frigid Bluffs site (2026-10-04), a leg spent a link moving 2 grid, from (706,641) to (706,643),
                        // and the order ran out of explosives before its first relic. See Spaced.
                        if (at != Detonator && Vector2.DistanceSquared(_spots[at], _spots[to]) < _env.Apart * _env.Apart)
                            continue;

                        var richer = WorthAt(at) + _worthOfSpot[to];

                        if (_stamp[to] == stamp)
                        {
                            if (_depth[to] == level && richer > _worthSoFar[to])
                            {
                                _worthSoFar[to] = richer;
                                _parentOf[to] = at;
                            }

                            continue;
                        }

                        _stamp[to] = stamp;
                        _depth[to] = level;
                        _worthSoFar[to] = richer;
                        _parentOf[to] = at;
                        nextLayer.Add(to);
                    }
                }

                List<(int Source, List<int> Path, double Worth)> found = null;

                foreach (var end in nextLayer)
                {
                    if (!goals.Contains(end))
                        continue;

                    var path = new List<int>(level);
                    var at = end;

                    while (!IsSource(at))
                    {
                        path.Add(at);
                        at = _parentOf[at];
                    }

                    path.Reverse();
                    (found ??= new List<(int, List<int>, double)>()).Add((at, path, _worthSoFar[end]));
                }

                if (found != null)
                {
                    found.Sort((x, y) => y.Worth.CompareTo(x.Worth));

                    return found;
                }

                layer = nextLayer;
            }

            return null;
        }

        private int[] _depth;
        private double[] _worthSoFar;
        private int[] _parentOf;
        private int[] _stamp;
        private int _stampNow;

        /// <summary>The ground's spots one link before a spot. See SpotGround.Previous.</summary>
        private List<int> Previous(int to) => Ground.Previous(_env, to);

        /// <summary>The ground's spots one link on from a spot, or from the detonator. See SpotGround.Next.</summary>
        private List<int> Next(int from) => Ground.Next(_env, from);
    }

    /// <summary>Every subset of the items that leaves out at most so many, largest first.</summary>
    private static IEnumerable<List<int>> SubsetsLeavingOut(List<int> items, int most)
    {
        yield return new List<int>(items);

        if (most >= 1)
        {
            for (var a = 0; a < items.Count; a++)
                yield return items.Where((_, i) => i != a).ToList();
        }

        if (most >= 2)
        {
            for (var a = 0; a < items.Count; a++)
            for (var b = a + 1; b < items.Count; b++)
                yield return items.Where((_, i) => i != a && i != b).ToList();
        }
    }

    /// <summary>Every order of the items, by Heap's algorithm.</summary>
    private static IEnumerable<List<int>> Permutations(List<int> items)
    {
        var a = new List<int>(items);
        var c = new int[a.Count];

        yield return new List<int>(a);

        var i = 0;

        while (i < a.Count)
        {
            if (c[i] < i)
            {
                var j = i % 2 == 0 ? 0 : c[i];

                (a[j], a[i]) = (a[i], a[j]);

                yield return new List<int>(a);

                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }
}
