using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// Destroy and repair: tear a stretch out of the chain and rebuild it, over and over.
///
/// **The one move the orienteering literature considers essential, and the one this search has never
/// had.** Everything else here works a link at a time - relocate one, swap two, drop one and append
/// at the end - and a chain that needs three links to move together to reach anything better is
/// unreachable from all of them, because every intermediate step scores worse. Restarts answer that
/// by throwing the chain away and beginning somewhere else, which discards everything it had got
/// right along with the part that was wrong.
///
/// Large neighbourhood search (Shaw 1998) and its adaptive form (Ropke and Pisinger 2006) say: keep
/// most of the solution, destroy a piece of it, and rebuild that piece properly. The neighbourhood
/// is then every chain that agrees with this one outside the torn stretch - exponentially many, none
/// of them reachable by a single-link move - and it is searched by construction rather than by
/// enumeration.
///
/// Three things make it cheap enough to do hundreds of times in a window:
/// <list type="bullet">
/// <item>the torn stretch is contiguous, so the holes to fill sit between two known links and the
/// positions to try are a handful rather than all sixteen,</item>
/// <item>the candidates tried per hole are a shortlist rather than the whole site,</item>
/// <item>nothing is polished until something is accepted.</item>
/// </list>
///
/// **What it is NOT yet:** each trial insertion rescores the whole chain, where Planner's Push and
/// Pop price one added link against a running tally. That is the next order of magnitude and it is
/// deliberately not in the first version - a mode nobody has measured should be simple enough to be
/// obviously correct before it is fast.
/// </summary>
internal static class Repair
{
    /// <summary>The fallbacks, used only when a setting cannot be read. See Shortlist.</summary>
    private const int Rich = 400;

    /// <summary>
    /// How many more it keeps purely to cover ground.
    ///
    /// **A shortlist by worth alone is disconnected.** Candidates exist where content is, so the
    /// ground between two clusters offers nothing worth anything - and those are exactly the cells a
    /// chain needs to cross from one to the other. Ranking by value and cutting throws every one of
    /// them away, which leaves a repair that can rebuild a stretch inside a cluster and can never
    /// rebuild one that leaves it.
    /// </summary>
    private const int Spread = 200;

    /// <summary>
    /// How many of each kind the shortlist actually keeps, and how far apart the spread has to be.
    ///
    /// **Constants until the ceiling turned out to be flat.** Five cold presses of one site returned
    /// 4,083, 4,094, 4,097, 4,209 and 4,503 - a mean of 4,197 on a standard deviation of 178 - while
    /// the work per press doubled underneath them. A search that cannot be moved by twice the rounds
    /// is not short of time, and the next suspect is what it is allowed to look at: every tear
    /// rebuilds from this list and nothing outside it can ever enter a chain.
    ///
    /// Settings rather than a new constant, because the answer is a trade and the trade is per site:
    /// a longer list is more of the site reachable and a dearer repair, since every rebuild ranks it.
    ///
    /// **Watch the second number as much as the first.** The default asks for 400 rich and 200
    /// spread and gets 400 and 87 - the separation test runs against everything already kept, and
    /// the rich picks blanket the clusters, so the connective ground the spread exists for is the
    /// half being squeezed. Loosening Sparse buys spread picks that raising Spread alone will not.
    /// </summary>
    private static (int Rich, int Spread, float Sparse) Listing(PlanEnvironment env) =>
        (env.ShortlistRich > 0 ? env.ShortlistRich : Rich,
            env.ShortlistSpread >= 0 ? env.ShortlistSpread : Spread,
            env.ShortlistSparse > 0f ? env.ShortlistSparse : Sparse);

    /// <summary>How far apart the spread picks have to be, in grid units.</summary>
    private const float Sparse = 45f;

    /// <summary>
    /// How big a tear to take, given how long it has been since anything worked.
    ///
    /// **Variable neighbourhood search, which is the standard answer to exactly this.** Small tears
    /// are cheap and do the ordinary work; a chain that has stopped improving is one whose way
    /// forward needs several links to move at once, and no number of one-link tears will find it.
    /// So the CEILING climbs with consecutive failures and collapses the moment something is kept -
    /// the floor never moves, so the cheap moves stay available the whole way up.
    ///
    /// The climb is timed off the kick: the ceiling reaches its maximum just as the kick
    /// would fire, so the two are one escalation rather than two unrelated ones.
    /// </summary>
    /// <param name="stuck">How far towards the next kick the worker is, nought to one. See Repair.Search's Stuck.</param>
    private static int Torn(PlanEnvironment env, Random random, double stuck, int links)
    {
        var least = Math.Max(1, env.TearLeast);
        var most = Math.Max(least, env.TearMost);
        var steps = most - least + 1;
        var ceiling = steps > 1 ? Math.Min(most, least + (int)(Math.Min(1d, stuck) * steps)) : least;

        return Math.Min(random.Next(least, ceiling + 1), Math.Max(1, links - 1));
    }

    /// <summary>How long the band search gets to offer an opening. See Opening.</summary>
    private static readonly TimeSpan BandTime = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// How many randomised constructions the opening is drawn from.
    ///
    /// **Tearing at a bad chain is a bad chain with holes in it.** The first version opened at one
    /// deterministic greedy pass and measured 3,094.7 on a site where the whole-site search reached
    /// 12,582.6 - and it never once improved on its own opening across five thousand rounds, because
    /// a one-to-four link tear cannot make up the difference between a plain greedy chain and one
    /// built around the site's remnants. The perturbation was never the problem; the starting point
    /// was.
    /// </summary>
    private const int Starts = 4;

    /// <summary>
    /// Why a worker is kicked when it stops setting records. How many rounds that takes is
    /// PlanEnvironment.StagnationKickRounds, set by SolverSettings.StagnationKickRounds.
    ///
    /// **Five thousand rounds and nought new bests is not a search, it is a walk.** The acceptance
    /// rule lets the working chain drift within a couple of per cent of the record, which is what
    /// stops it sitting on one answer - and on a chain that is already locally optimal under a much
    /// stronger local search than any tear, it drifts for the rest of the window and never climbs
    /// back. Restarting on stagnation is what every iterated local search does about that, and the
    /// record is kept across restarts so nothing is lost by trying.
    /// </summary>
    private static int KickRounds(PlanEnvironment env) => Math.Max(0, env.StagnationKickRounds);

    /// <summary>
    /// How far either side of a hole an insertion may be tried.
    ///
    /// Nought would mean rebuilding the stretch exactly where it was. Two lets the repair shift the
    /// whole rebuilt run along the chain, which is what turns "replace these three links" into
    /// "replace these three links and re-time them".
    /// </summary>
    private const int Shift = 2;

    /// <summary>
    /// How near the best insertion another one may be and still be picked, as a fraction.
    ///
    /// **A deterministic repair undoes a deterministic destroy.** Worst-removal takes out the links
    /// worth least and the repair puts back whichever links are worth most - which, on a chain that
    /// was already locally optimal, is the ones just removed. Measured: six hundred and fourteen
    /// tries, nought accepted, while the two destroys that force genuine change kept one in five.
    /// The destroy was not the problem, the repair was: it could only ever give the same answer.
    ///
    /// Noise is the standard remedy in adaptive large neighbourhood search and it is one line -
    /// choose among the insertions that are nearly as good rather than always the single best. It
    /// costs a fraction of a per cent per link and it is what lets a destroy discover anything.
    /// </summary>
    private const double Nearly = 0.995d;

    /// <summary>
    /// How much worse than the best an accepted chain may be, as a fraction.
    ///
    /// Record-to-record travel: the simplest acceptance that is not hill climbing. A pure
    /// improvement rule gets stuck in exactly the way this mode exists to escape, and simulated
    /// annealing needs a temperature schedule nobody here has calibrated.
    /// </summary>
    private const double Slack = 0.02d;

    /// <summary>
    /// The accepted deviation in force, from the setting, falling back to Slack.
    ///
    /// **The narrowest thing in the search, measured against the distance it would have to cross.**
    /// Two per cent of a 4,092 chain is 82 points. The best press of the day was 411 points above
    /// that, and the all-time record about 950 at today's prices. Reaching either almost certainly
    /// means following chains well below the record for a while, and the rule refuses them.
    ///
    /// Not proven - a wider window may simply wander - but it is the only constraint left standing
    /// after twice the rounds and a candidate set swept from 291 spots to 1,063 both changed
    /// nothing. See SolverSettings.AcceptSlack.
    /// </summary>
    private static double Slacking(PlanEnvironment env) =>
        env.AcceptSlack > 0d ? env.AcceptSlack : Slack;

    /// <summary>
    /// The band search's answer for this environment, worked out once and handed to everyone.
    ///
    /// **It is deterministic, and it was being paid for four times over.** Every stagnation restart
    /// ran it again for the same answer, and with several workers each of those would run it again
    /// as well - nine hundred milliseconds apiece, out of the window the tearing was supposed to
    /// have. The environment is rebuilt per solve and never mutated during one, so reference
    /// equality is exactly the right key.
    /// </summary>
    /// <summary>
    /// The band seeding this worker asks for, which is not the same as its neighbour's.
    ///
    /// **Eight workers were exploring around one chain.** The band opening is deterministic, so it
    /// was computed once and shared - and every worker then started from the same structure, shaken
    /// by its own number of kicks. That is eight samples of one search rather than eight searches,
    /// and it shows: six measurements on one site, every one landing between 4,050 and 4,120, with
    /// runs routinely ending at exactly the figure they opened with.
    ///
    /// The bands are what decide that figure. They are drawn from families - how many spots per
    /// pair, per rare, how much worth may separate a cell from the best in its band, whether to lean
    /// on heavy content - and different answers to those give genuinely different chains rather than
    /// different points near one chain.
    ///
    /// Worker nought keeps the configured seeding exactly, so whatever the sliders say is always one
    /// of the eight and the settings still mean what they say. The rest vary around it.
    /// </summary>
    private static SeedFamilies Asking(PlanEnvironment env, int stream)
    {
        var seeding = env.Seeding;

        if (seeding == null || stream <= 0)
            return seeding;

        // Small, deliberate steps rather than random ones: a worker's opening should be repeatable,
        // and eight fixed variations spread further than eight draws from one distribution usually
        // manage. The counts and the slack move on different cycles, so the set covers "few spots,
        // tight" through "many spots, loose" rather than clustering in the middle.
        var step = Varied(stream);

        return seeding with
        {
            Pairs = Math.Max(1, seeding.Pairs + (step % 3) - 1),
            Rares = Math.Max(1, seeding.Rares + step / 3 * 2 - 1),
            Slack = Math.Max(0f, seeding.Slack * (step % 2 == 0 ? 0.5f : 2f)),
            Heavy = step % 4 < 2 ? seeding.Heavy : !seeding.Heavy,
        };
    }

    /// <returns>The band opening for this worker's own seeding, or null when there is none.</returns>
    internal static List<Vector2> Banded(PlanEnvironment env, CancellationToken token, int stream)
    {
        // Every worker takes worker nought's seeding - the configured one. A seeding per worker was a setting
        // and was removed: the opening shake undid the difference before anything read it, measured at 20,092
        // against 23,517 with half the rounds.
        var seeding = Asking(env, 0);

        if (seeding == null)
            return null;

        var key = (env, Varied(0));

        if (_bands.TryGetValue(key, out var had))
            return had;

        // **Claimed rather than locked, which is neither waiting nor duplicating.**
        //
        // Two workers asking the same question must not both answer it: with the variations off,
        // all eight ask the same one, and computing it eight times cost every worker 3,170ms of a
        // window it never recovered from - five of them managed under 500 rounds where the three
        // that got through managed ten thousand. Locking instead would serialise those eight, which
        // is the wait this was built to remove.
        //
        // So the first worker to arrive claims the key and the rest get on without the bands. They
        // still have their seed and their greedy starts to open from, and whoever arrives after the
        // answer is stored picks it up for nothing. Only one worker ever pays, and nobody queues.
        if (!_building.TryAdd(key, true))
            return null;

        var asked = env with { Seeding = seeding };

        // The band search is capped by wall clock too, and it decides the chain most workers open from. Let it
        // run to completion when measuring, for the same reason. See SolverSettings.RoundsPerWorker.
        var bandFor = env.RoundsPerWorker > 0 ? TimeSpan.FromHours(1) : BandTime;
        var plan = Edges.Search(asked, bandFor, TimeSpan.Zero, token, null);
        var found = plan.Points is { Count: > 0 } ? new List<Vector2>(plan.Points) : null;

        _bands[key] = found;
        _building.TryRemove(key, out _);

        return found;
    }

    /// <summary>The keys somebody is already working on. See Banded.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (PlanEnvironment Env, int Varied), bool> _building = new();

    /// <summary>Which of the seeding variations a worker uses. See Asking.</summary>
    private static int Varied(int stream) => stream <= 0 ? -1 : (stream - 1) % 6;

    /// <summary>
    /// The band opening per environment and per seeding variation.
    ///
    /// Keyed on both because the answer depends on both, and held across restarts because it is
    /// deterministic - a worker that kicks and opens again asks the same question and should not pay
    /// for it twice. The environment is rebuilt per solve and never mutated during one, so reference
    /// equality is exactly the right key for that half.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (PlanEnvironment Env, int Varied), List<Vector2>> _bands = new();



    /// <param name="stream">
    /// Which worker this is, when several run at once. It is the only thing separating them, so it
    /// has to reach the random seed - four workers drawing the same sequence are one worker.
    /// </param>
    /// <param name="seed">
    /// The chain this site gave up last time, or null.
    ///
    /// **Without it every press started over, and pressing again was a re-roll rather than a
    /// refinement.** A run that reached 16,644.9 was followed by one that settled at 14,794.6, which
    /// is the ordinary band six of eight workers land in - because nothing carried the good chain
    /// forward and the search had to rediscover it by luck. Taken as one of the openings, the worst
    /// a second press can do is keep what the first found.
    /// </param>
    public static Plan Search(PlanEnvironment env, TimeSpan budget, TimeSpan settle,
        CancellationToken token, Action<List<Vector2>> found, int stream = 0,
        List<Vector2> seed = null)
    {
        if (env.Explosives <= 0 || env.Targets.Count == 0)
            return Plan.Empty;

        // The engine actually in use, which the phase table could not say: it reported no line for
        // the planner's own Search at all, because the default strategy is this one. See
        // Planner.Phases.
        using var whole = new Planner.Phase(Planner.PhaseRepair);

        if (seed is { Count: > 0 })
            Solving.Carried(Planner.Score(env, seed));

        // The worker that continues from the carried plan publishes improvements on it, and those are not
        // what Behind should measure the others against. See Solving.PublishingFromCarried.
        Solving.PublishingFromCarried = seed is { Count: > 0 } && Role(env, stream) is { Opening: ThreadRoles.Opens.Continue };

        var candidates = Planner.Candidates(env, out var offered, out _);

        if (candidates.Count == 0)
        {
            return Plan.Empty with
            {
                Note = offered == 0
                    ? "no candidate positions could be generated from the content here"
                    : "every candidate position was refused as unplaceable",
            };
        }

        Planner.Positions = candidates.Count;

        var shortlist = Shortlist(env, candidates);
        var reach = new Near(env, shortlist);
        // The draw is per solve and the stream is per worker, so eight workers differ from each other
        // and two solves given different draws differ from each other. See PlanEnvironment.Draw.
        var random = new Random(20260916 + env.Targets.Count + stream * 7919 + env.Draw * 104729);

        var deadline = DateTime.UtcNow + budget;
        var improved = DateTime.UtcNow;

        // The seeded opening this worker must keep for a while, and when it stops having to. Null for a worker
        // that drew its own opening, and for every worker when the setting is nought.
        var keptOpening = (List<Vector2>)null;
        var keepOpeningUntil = DateTime.MinValue;

        // Whether a chain may be accepted, which is ordinarily yes and is no while it has moved an opening the
        // worker is still keeping. **Not a filter on what the operators try** - they explore freely and the
        // rejected chains simply do not become the incumbent - so the only cost of keeping it is the tries spent
        // on chains that could not be kept, and the only effect is that the opening survives long enough to be
        // judged on what follows it.
        /// <summary>
        /// How many leading links may not be reordered: the explosives already down, and the seeded opening
        /// while it is being kept.
        ///
        /// **The reordering is what was undoing the kept opening, not the shake.** Permuted puts a record into its
        /// best order and Slid shifts it along the route, and both take this count to know what to leave
        /// alone - ordinarily the bombs already placed. Every record therefore reordered the opening too, and
        /// the initial pass did it before the search started, so a worker seeded at (1139,604) was at
        /// (1130,573) by its first record. Measured on one press: neither worker seeded on that branch
        /// finished on it.
        /// </summary>
        int Frozen()
        {
            var placed = env.Placed?.Count ?? 0;

            return keptOpening != null && DateTime.UtcNow < keepOpeningUntil ? Math.Max(placed, keptOpening.Count) : placed;
        }

        bool Keeps(List<Vector2> chain)
        {
            if (keptOpening == null || DateTime.UtcNow >= keepOpeningUntil)
                return true;

            if (chain.Count < keptOpening.Count)
            {
                Interlocked.Increment(ref _refusedWhileKeepingOpening);

                return false;
            }

            for (var i = 0; i < keptOpening.Count; i++)
            {
                if (chain[i] != keptOpening[i])
                {
                    Interlocked.Increment(ref _refusedWhileKeepingOpening);

                    return false;
                }
            }

            return true;
        }

        // Stops keeping an opening the chain in hand no longer starts with.
        //
        // **The must-take tour replaces the chain after the opening is kept**, and a tour that starts at the
        // detonator does not begin with the kept links - so Keeps then refused every chain descended from it,
        // for the whole window, and the worker returned the tour's score untouched. Measured on Scorched Cay:
        // four seeded workers at 2,987 on every press, 35 samples over three batches. A worker whose chain has
        // lost its opening has nothing left to keep. See Planner.MustTakeTour's keepAtLeast.
        void ReleaseOpeningNotIn(List<Vector2> chain)
        {
            if (keptOpening == null)
                return;

            var intact = chain != null && chain.Count >= keptOpening.Count;

            for (var i = 0; intact && i < keptOpening.Count; i++)
                intact = chain[i] == keptOpening[i];

            if (intact)
                return;

            keptOpening = null;
            Interlocked.Increment(ref _releasedForMustTake);
        }

        // Whether the opening has produced a chain yet, which is what Waiting reads to know whether
        // the window has anything to run against. See Waiting.
        var constructed = false;

        // Declared above Waiting because Waiting reads it: with a round budget the count is what ends the
        // window rather than the clock. See SolverSettings.RoundsPerWorker.
        var rounds = 0;

        // **The improvement window measures improvement, so it does not start until there is
        // something to improve.**
        //
        // The opening publishes nothing until it has a complete chain and takes as long as it takes -
        // four seconds on a Grand site, measured - so the settle clock had already spent that much of
        // itself before the first score existed. On an ordinary site, where the window is 1.5s, it
        // expired during the opening every time.
        //
        // The deadline still bounds the solve, so this cannot run away.
        bool Waiting()
        {
            if (!constructed)
                improved = DateTime.UtcNow;

            // **A round budget replaces both clocks, so the work per press is fixed and a draw reproduces.**
            // The deadline and the settle window are what make a press unrepeatable: the seeds are common
            // random numbers and the amount of work is not, so two batches over the same draws build different
            // chains. Cancellation still stops it, because a cancelled solve is not a measurement.
            // See SolverSettings.RoundsPerWorker.
            if (env.RoundsPerWorker > 0)
                return rounds < env.RoundsPerWorker && !token.IsCancellationRequested;

            return DateTime.UtcNow < deadline && !token.IsCancellationRequested &&
                   (settle <= TimeSpan.Zero || DateTime.UtcNow - LastImprovement() < settle);
        }

        // **The pool's last record, not only this worker's.** The window was each worker's own, so a worker
        // stopped eight seconds after its own last record while another was still climbing, and its thread sat
        // idle for the rest of the press. Measured on Craggy Peninsula, twenty explosives: one worker ran 68s
        // and set 37 records, the other seven stopped between 15s and 27s. The later of the two is kept so a
        // worker still inside its own opening, which resets `improved` until it has a chain, is not stopped by
        // a pool that has not heard from it yet.
        DateTime LastImprovement()
        {
            var pool = Solving.PoolImprovedAt;

            return pool > improved ? pool : improved;
        }

        // **Where the window went, because inferring it has been wrong three times tonight.**
        // Rounds fell from 6,546 to 10 between two runs and the counters said nothing about which
        // part had eaten the time - the opening, the tears, the reach operator or the polish. One
        // stopwatch each and the next dump answers it by being read rather than reasoned about.
        var asked = 0;
        var arrived = 0;

        // **One counter called "opening" was hiding three different things.**
        //
        // It held the first construction, the kick's rebuild, and - through Banded's lock
        // - however long this worker sat waiting for another one to finish the band search. Read as
        // a single number it says "setup is expensive", which is true and useless: the setup happens
        // once, the kick happens every time the search gets stuck, and the wait is pure idling that
        // no amount of tuning the opening would remove. Measured at 4,257ms on a fifteen link site,
        // against 1,126ms of actual tearing, with no way to tell which third to attack.
        var bandMs = 0d;
        var openingMs = 0d;
        var kickMs = 0d;

        // The part of kickMs spent on restarts from a fresh construction, which cost about two seconds each on a
        // twenty explosive site against under one for an ordinary kick.
        var restartMs = 0d;
        var reachMs = 0d;
        var tearMs = 0d;
        var polishMs = 0d;

        // What the polish on each record added over the chain the tear produced: how many polishes ran, how many
        // raised the score, and by how much in all. Without it the polish time could not be priced against the
        // tearing it displaces.
        var polishes = 0;
        var polishesThatHelped = 0;
        var polishGain = 0d;

        // **The phases the window was losing without a line in the dump.** On a twenty explosive site with two
        // must-takes, workers ran nought to 250 rounds of an eight second window and the counters above
        // accounted for as little as two seconds of it. These are the rest: the ordering before the loop, the
        // must-take and touring constructions with their polish, and the final ordering, which runs after the
        // window has closed and so lengthens the press rather than shortening the search.
        var mustTakeMs = 0d;
        var touringMs = 0d;

        // The part of mustTakeMs spent polishing the chosen route rather than building the candidates.
        var mustTakePolishMs = 0d;
        var finishMs = 0d;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var watch = new System.Diagnostics.Stopwatch();
        var band = new System.Diagnostics.Stopwatch();

        // Something worth tearing at, and not the same thing every worker is tearing at. See Starts.
        watch.Restart();

        // **The opening's own clock, on top of the window's.** It otherwise runs until it finishes,
        // which on a Grand site is four to eight seconds of an eight second window. Nought leaves
        // that alone. The band search is not interrupted by it - that pass takes a token rather than
        // a predicate - so a cap shorter than the band's cost lands immediately after it instead.
        // **No clock on the opening while a round budget is in force.** A cap in milliseconds decides how
        // much of the opening gets built, so two presses of the same draw start from different chains and the
        // pairing the draws exist for is lost before the loop begins. Measured: with the loop bounded by rounds
        // and these two clocks left in, two ten press batches at an identical configuration still differed by
        // 608. See SolverSettings.RoundsPerWorker.
        var opens = env.OpeningMs <= 0 || env.RoundsPerWorker > 0
            ? DateTime.MaxValue
            : DateTime.UtcNow + TimeSpan.FromMilliseconds(env.OpeningMs);

        bool Early() => DateTime.UtcNow < opens && Waiting();

        // **A site simple enough to reason about is reasoned about BEFORE the opening, not after.**
        //
        // Nine or ten things of two kinds is not a search problem - what matters is which kind goes
        // first, and everything else follows. The exact solve for that was written for such a map
        // and then lived inside the restart search, where Destroy and Repair could never reach it:
        // every dump taken on this strategy reports "has not run", and that was the reason rather
        // than the site.
        //
        // **Before, because the opening is where the window goes.** It is four to eight seconds of
        // an eight second window on a big site - see the cap above, which exists for that - and
        // running the exact solve afterwards spends all of it constructing a chain that is about to
        // be thrown away. On a site this answers, the opening has nothing to add and is skipped
        // outright.
        //
        // Cheap when it does not apply: Exact gives up the moment it meets a fourth kind of content,
        // which is most sites and costs one pass over the targets.
        var best = Planner.TryEnumeratedSolve(env, double.NegativeInfinity);

        if (best is { Count: > 0 })
        {
            found?.Invoke(new List<Vector2>(best));
        }
        else
        {
            best = Opening(env, candidates, shortlist, random, Early, token, stream, out keptOpening, seed,
                band);

            // Kept so the tally can tell a worker that was never seeded from one that was seeded and lost its
            // opening before it could be kept. See KeepOpeningSaid.
            var seededWith = keptOpening;

            // **Only kept if the chain that came back still has it.** The opening shakes run inside Opening
            // and are free to move link one, so the seeded prefix and the chain built from it can already
            // disagree by the time it returns - and keeping a prefix the incumbent does not have rejects every
            // improvement for as long as it is kept, which stalls the worker instead of steadying it.
            if (keptOpening is { Count: > 0 } && best is { Count: > 0 } &&
                best.Count >= keptOpening.Count)
            {
                var intact = true;

                for (var i = 0; i < keptOpening.Count; i++)
                    intact &= best[i] == keptOpening[i];

                keptOpening = intact ? keptOpening : null;
            }
            else
            {
                keptOpening = null;
            }

            var keepShare = Role(env, stream) is { } role ? role.KeepOpening : 0d;

            // A share of the budget when the budget is a real limit, and of the improvement window when it is only
            // Planning.Unbounded. Taken of the ten minute stand-in, keep-opening=30 held for three minutes, which
            // is the whole of any unlimited solve: on Scorched Cay the three seeded workers kept their openings to
            // the end, refused 174 chains, and one finished at 9,611 against a pool best of 15,233.
            var keepBase = env.PressWindowMs > 0
                ? TimeSpan.FromMilliseconds(env.PressWindowMs)
                : budget >= Planning.Unbounded && settle > TimeSpan.Zero ? settle : budget;

            if (keptOpening != null && keepShare > 0d)
                keepOpeningUntil = DateTime.UtcNow + keepBase * keepShare;
            else
                keptOpening = null;

            if (seededWith != null)
                Interlocked.Increment(ref _seededWorkers);

            if (keptOpening != null)
                Interlocked.Increment(ref _workersKeepingOpening);

            // **And again afterwards, because the first ask can be too early to answer.** Exact
            // needs the router to confirm an order is legal and the router answers from flooded
            // ground, so on a cold site the first call comes back "waiting on the router" however
            // simple the site is. The opening warms exactly that ground. Free to repeat - Outright
            // returns at once once it has succeeded - and it takes the answer only if it beats what
            // the opening built.
            if (Planner.TryEnumeratedSolve(env, best.Count > 0 ? Planner.Score(env, best) : double.NegativeInfinity)
                is { Count: > 0 } worked)
            {
                best = worked;
                found?.Invoke(new List<Vector2>(best));
            }
        }

        constructed = true;

        // What this worker was told to favour, so the live readout can say whether the specialists
        // are the ones pulling away. See Leaning and Solving.Watching.
        // The worker's own role decides its bias, so the tearing mix is only consulted when no roles were
        // read - which is what an empty line leaves. See ThreadRoles.
        var mine = Role(env, stream);
        var told = mine != null ? (At: mine.Tear, Share: mine.Share) : (At: -1, Share: 0d);

        // The whole role in the roles line's words, not the bias alone, so the live readout says which worker
        // tours, refines or holds an enumerated opening. See ThreadRoles.RoleInLine.
        var lean = mine != null
            ? ThreadRoles.RoleInLine(mine)
            : told.At < 0
                ? "even"
                : told.Share == 70d
                    ? Destroys[told.At]
                    : $"{Destroys[told.At]}:{told.Share:0}";

        openingMs += watch.Elapsed.TotalMilliseconds;
        bandMs = band.Elapsed.TotalMilliseconds;
        // **The opening as Opening returned it**, which for an exploring worker is now the shaken
        // chain before any descent and for worker nought is its polish. Eight workers finishing on one
        // number says nothing on its own about which half failed: the shakes may be producing one
        // chain, or several that a descent walks onto the same local optimum.
        //
        // This figure could not tell them apart while Opening polished before returning, and that is
        // what it was added to do. Both were then measured by other means: at a shake cap of 2 and
        // again at 12, seven of eight workers built the identical score to the decimal - so the shake
        // was not the variable and the shared deterministic descent was. See Opening's tail.
        //
        // Measured on Craggy Peninsula: all eight opened AND finished at 105,184.4, 0.0%, with
        // 28,000 rounds between them and not one best. Measured again on a 15-explosive site:
        // 33,268.0 built by seven of eight, 0 bests between them.
        var raw = best is { Count: > 0 } ? Planner.Score(env, best) : 0d;

        phase.Restart();

        // **A requirement the opening missed, fetched before the loop starts.**
        //
        // Nothing in this file can reach a must-take that sits more than one link off the route:
        // every operator here moves a link and keeps the result only if it improves, and the ground
        // between is worse than where the chain stands. The charge for dropping one ranks the
        // answer afterwards; it does not build it. See Planner.MustTakeTour.
        if (env.Musts > 0 &&
            Planner.MustTakeTour(env, shortlist, best, stream, keptOpening?.Count ?? 0) is { Count: > 0 } demanded)
        {
            // **Not shaken afterwards.** The shake happens inside Opening, before this, and the
            // tour is what puts back what the shake tore off - so shaking the tour undoes the
            // fetch and leaves the requirement dropped with nothing left to restore it. Diversity
            // comes from the shaken chain this tour was computed against, and from the tour
            // chosen per worker.
            var polishing = System.Diagnostics.Stopwatch.StartNew();

            best = Planner.Improve(env, shortlist, demanded, () => Waiting() && polishing.Elapsed < MustTakePolish);

            mustTakePolishMs = polishing.Elapsed.TotalMilliseconds;

            ReleaseOpeningNotIn(best);
        }

        mustTakeMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();

        // **A touring worker is sent somewhere the pool would not otherwise go.** Its own construction, routed
        // through the n-th richest target that construction misses, n counted over the touring workers so no two
        // aim at the same one. Taken whether or not it scores better at once: the point is a different starting
        // region, and the loop that follows judges it. See Planner.TourThrough.
        if (Role(env, stream) is { Opening: ThreadRoles.Opens.Tour } &&
            Planner.RichestUnreached(env, best, TourSlot(env, stream), TourCount(env)) is var aim and >= 0 &&
            Planner.TourThrough(env, shortlist, best, aim) is { Count: > 0 } toured)
        {
            best = Planner.Improve(env, shortlist, toured, Waiting);

            if (stream >= 0 && stream < _touredTo.Length)
                _touredTo[stream] = env.Targets[aim].Grid;
        }

        touringMs = phase.Elapsed.TotalMilliseconds;

        var top = Planner.Score(env, best);

        Solving.Scored(stream, Planner.Plainly(env, best), top, lean);

        // **What the opening was worth, kept so the window can be priced against it.**
        //
        // The report says where a worker ended and never said where it started, so the one question
        // the whole afternoon turns on - how much is the improvement window actually buying? - could
        // only be inferred. It matters because everything else has come back flat: the answer did
        // not move for twice the rounds, nor for a candidate set swept from 291 spots to 1,063. If
        // the opening already scores within a per cent or two of the finish, the tearing is not what
        // decides a press and the band search is the only thing left that does.
        var opened = top;

        // What the site pays out of that, which is what the acceptance slack is a share of. Kept
        // beside top and updated with it, because Plainly walks the chain and the acceptance test runs
        // thousands of times a second. See the acceptance below.
        var plainTop = Planner.Plainly(env, best);

        // **The opening is a result, and it was the one result never offered to anybody.**
        //
        // Publishing happened only on an improvement, so the pool's incumbent did not exist until
        // some worker bettered its own opening - and on a press that inherits a chain nobody betters,
        // it never existed at all: eight threads, thirty kicks, nought adopted, every one of them
        // holding the same number they started with. The openings differ by more than anything that
        // happens afterwards - 4,464 against 3,422 on one measured press - so they are exactly what
        // a worker starting badly needs to hear about, and the last thing it was told.
        //
        // Publish is monotonic and serialised, so offering a weak opening costs a lock and is
        // ignored. See Solving.Across.
        found?.Invoke(new List<Vector2>(best));

        var chain = new List<Vector2>(best);
        var score = top;

        found?.Invoke(new List<Vector2>(best));

        // Adaptive weights over the four ways to tear. Moved by what works, which is the whole of
        // the "adaptive" in ALNS - a destroy that suits this site earns more turns. Started equal.
        var weight = new[] { 1d, 1d, 1d, 1d };

        // **The role's lean, held for the whole window and multiplied into every draw.** It was the starting
        // value of the weights above, and Reweigh moves them 30% of the way to the measured rate every 32
        // rounds, so after 500 rounds the lean kept under one per cent of its effect and a worker told
        // "seg 70%" drew reach more often than seg. Measured on Craggy Peninsula: 160 reach tries to 207 tears
        // for a seg worker, 350 to 161 for a rel worker. See Leaning and Draw.
        var roleLean = Leaning(env, stream);
        var used = new int[4];
        var won = new int[4];
        var paid = new int[4];

        // What each destroy has cost, so the weights can be about value per millisecond rather than
        // value per attempt. See Reweigh, which records why per-millisecond was considered.
        var spent = new double[4];

        var kept = 0;
        var bettered = 0;
        var restarts = 0;
        var rescued = 0;

        // The best the chain built by the last restart has reached, which is what the next kick judges once a
        // restart has happened. The record is kept across a restart and a fresh construction starts well below
        // it, so judged on the record a climbing restart still counted as stuck, and was still behind the pool at
        // the next kick, and was thrown away before it had the rounds to show whether it was going anywhere.
        // Meaningless until the first restart; before it, the record is the trajectory.
        var restartTop = double.NegativeInfinity;

        // When the chain this worker is working last improved - its record, or since a restart the restarted
        // chain's best. The stall restart reads it. See DestroyAndRepairSettings.StallRestartMs.
        var progressedAt = DateTime.UtcNow;

        // A refining worker searches like any other until its refine-after share of the window has passed, then
        // rebuilds from the pool's best as soon as that is better than its own record, and again at every kick.
        // The window is the same one keep-opening is a share of, counted from the start of the search. See
        // RefinedStart and ThreadRoles.Role.RefineAfter.
        var refining = Role(env, stream) is { Opening: ThreadRoles.Opens.Refine };

        // The worker that continues from the standing plan rebuilds its record's tail by rollout at every kick,
        // instead of shaking it and letting the polish walk it back to the same chain. The cut moves one link
        // earlier each kick, from two before the end to half way, then starts again. See Planner.TailRollout.
        var rollingTails = Role(env, stream) is { Opening: ThreadRoles.Opens.Continue };

        bool Stalled() =>
            env.StallRestartMs > 0 && !rollingTails && !refining &&
            (DateTime.UtcNow - (improved > progressedAt ? improved : progressedAt)).TotalMilliseconds >=
            env.StallRestartMs;
        var tailKicks = 0;
        var refinedYet = false;
        var refineBase = env.PressWindowMs > 0
            ? TimeSpan.FromMilliseconds(env.PressWindowMs)
            : budget >= Planning.Unbounded && settle > TimeSpan.Zero ? settle : budget;
        var refineFrom = deadline - budget + refineBase * (Role(env, stream)?.RefineAfter ?? 0d);


        // **What this worker had partway through, so the shape of the window can be read.**
        //
        // The pool is eight lottery tickets and the two ways to make it fewer-but-deeper or
        // more-but-shallower need opposite answers to one question: is the second half of a window where
        // workers climb, or have they finished? If a worker at four seconds is already within a per cent
        // or two of where it ends, the window can be halved and run twice for twice the tickets. If it
        // climbs late, it cannot, and the budget should instead be taken off the workers that are not
        // climbing. Nothing recorded this, so both plans were guesses.
        //
        // Absolute rather than half of the budget: the worker does not know its deadline - Waiting is a
        // predicate handed in - and presses on this site run 8.2 to 8.7 seconds, so four is the middle of
        // one to within a few per cent.
        var halfway = 0d;
        var marked = System.Diagnostics.Stopwatch.StartNew();

        // Read once: it is a setting and the loop below asks about it thousands of times a second.
        var slack = Slacking(env);
        var since = 0;

        // The same stagnation measured on the clock, so a worker running thirty milliseconds a round still kicks
        // before the improvement window runs out. Rounds alone left it at one kick in fifteen seconds. Off with a
        // round budget, which is there to make a press reproduce. See SolverSettings.StagnationKickPercent.
        var kickWindow = env.PressWindowMs > 0 ? TimeSpan.FromMilliseconds(env.PressWindowMs) : settle;
        var kickAfter = env.StagnationKickPercent > 0 && env.RoundsPerWorker <= 0 && kickWindow > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(kickWindow.TotalMilliseconds * env.StagnationKickPercent / 100d)
            : TimeSpan.MaxValue;
        var stuckSince = DateTime.UtcNow;
        var kickRounds = KickRounds(env);

        // How far towards the kick this worker is, nought to one, by whichever of rounds or the clock is further
        // along. The tear size climbs on it. See Torn.
        double Stuck()
        {
            var byRounds = kickRounds > 0 ? since / (double)kickRounds : 0d;

            if (kickAfter == TimeSpan.MaxValue)
                return byRounds;

            return Math.Max(byRounds, (DateTime.UtcNow - stuckSince).TotalMilliseconds / kickAfter.TotalMilliseconds);
        }

        var work = new List<Vector2>();

        // How many tears of each size were taken, so the escalation can be seen working rather than
        // assumed. A run whose sizes are all ones is a run that never got stuck; one with a tail out
        // to the maximum is the ceiling climbing as it should.
        var sized = new int[16];

        // Where in the chain the moves that paid were made: first third, middle, last third, and a
        // fourth slot for the reach operator, which picks its own cut. Three counts each - tried,
        // accepted, and set a record - because an operator can be busy where it cannot win. See Zoned.
        var zoneTried = new int[4];
        var zoneKept = new int[4];
        var zoneWon = new int[4];



        while (Waiting())
        {
            rounds++;

            // Stuck. Kick the record hard and carry on from there. See KickRounds.
            //
            // **The record, not a fresh construction.** Rebuilding the opening cost ten randomised
            // greedy builds and a full local search every time, which is most of a second out of a
            // window that was producing two thousand rounds - and it threw away the good chain to
            // start again from a worse one. Iterated local search does the opposite: it perturbs the
            // best it has, strongly, and descends again. Same escape, none of the cost, and the
            // ground it explores is around an answer already worth having.
            if ((++since > kickRounds && kickRounds > 0) || DateTime.UtcNow - stuckSince > kickAfter ||
                refining && !refinedYet && DateTime.UtcNow >= refineFrom && Solving.LeaderWorth() > top)
            {
                since = 0;
                restarts++;

                watch.Restart();

                // **Hopeless, so start again - from a chain of this worker's own making.**
                //
                // Half of every pool finishes in a trap and they pile onto the same three chains;
                // see Solving.Behind for the counts. A worker that far down is not going to climb
                // out, because everything it is judged against is its own record and its record is
                // the problem.
                //
                // A fresh randomised construction rather than the leader's chain, and the difference
                // is the whole point. Adopting rescues the score and costs the independence: the
                // worker then explores where the leader already is, and the rare good answers only
                // ever come from somewhere nobody was looking. This throws away the dead trajectory
                // and keeps the worker its own.
                // **Or after enough kicks with nothing to show for them, whatever the pool is doing.**
                //
                // Behind is a RELATIVE test, and that is the case it cannot see: when every worker is
                // equally mediocre, nobody is behind anybody, so nobody restarts and all eight grind
                // the same basins for the whole window. Measured over five cold presses at 8s, the one
                // that scored 7,861 had its eight workers inside an 800 point band - best 7,861, median
                // 7,657, worst 7,074 - which is precisely the press where no worker found the good
                // basin and not one of them gave up looking.
                //
                // A press is worth the best of its workers, and threads are capped at eight by
                // decision, so the only way to buy more draws is to let one worker take several. Three
                // kicks without a record is a worker that has finished with its basin: the record is
                // kept across restarts, so a fresh construction costs the remaining rounds of a
                // trajectory that was not going to produce anything and can only add a ticket.
                //
                // Counted from the last record rather than from the start, so a worker that is still
                // climbing is never interrupted. See SolverSettings.BarrenKicks, which is nought by
                // default because the result that justified it did not survive ten presses.

                if (!refining &&
                    (env.RestartThreshold > 0d &&
                     Solving.Behind(rescued > 0 ? restartTop : top, env.RestartThreshold) ||
                     Stalled()))
                {
                    // **Shaken before it is polished, or the restart is a clone.**
                    //
                    // A plain randomised greedy build is only mildly random, so a restarted worker
                    // constructs roughly the chain everybody else has and polishes into the basin
                    // everybody else is in. Measured: three presses with restarts on, pool spreads of
                    // 105, 132 and 334 against 840 without them, and not one rare answer among them.
                    // Four corpses became four copies, and a copy contributes no more to a best-of
                    // than a corpse does.
                    //
                    // The shake is the one thing measured to widen a pool - the worker given seven of
                    // them produced both the worst chain of a press and the best of the day. A restart
                    // does not need to be good, since an opening is worth about thirty per cent of a
                    // finish; it needs to be somewhere nobody else is.
                    var afresh = Planner.Greedy(env, shortlist, random, Planner.Among);

                    if (env.RestartShakes > 0)
                        afresh = Shaken(env, shortlist, afresh, random, env.RestartShakes);

                    // **Not polished, for the reason Opening gives for an exploring worker.** Planner.Improve is one
                    // deterministic descent over every candidate, so it walks each fresh construction into the basin
                    // the other workers are already in. It is also the likeliest part of what a restart cost on a
                    // twenty explosive site: about four seconds each in all, measured, against a four second kick
                    // window, with this descent not timed on its own. The tearing loop descends from here with this
                    // worker's own operators.
                    chain = afresh;

                    // **A fresh construction does not fetch must-takes, so it gets the same tour as the opening.**
                    // Greedy builds for content only, and a must-take held is worth more than the whole site, so a
                    // restarted chain without one scores below the worker's own record by more than any tear can
                    // recover - and the tears cannot reach a must-take more than a link off the route. The ordinary
                    // kick below already does this; the restart did not.
                    if (env.Musts > 0 &&
                        Planner.MustTakeTour(env, candidates, chain, stream, keptOpening?.Count ?? 0) is
                            { Count: > 0 } fetched)
                    {
                        var polishingFetched = System.Diagnostics.Stopwatch.StartNew();

                        chain = Planner.Improve(env, candidates, fetched,
                            () => Waiting() && polishingFetched.Elapsed < MustTakePolish);

                        ReleaseOpeningNotIn(chain);
                    }

                    score = Planner.Score(env, chain);
                    restartTop = score;
                    progressedAt = DateTime.UtcNow;
                    rescued++;
                    Solving.Rescued();

                    // The record stands. It is a poor one, but it is this worker's answer until the
                    // new start beats it, and returning something worse than what was already found
                    // would be a strange way to recover.
                    kickMs += watch.Elapsed.TotalMilliseconds;
                    restartMs += watch.Elapsed.TotalMilliseconds;

                    // Counted from when the restart finished, not when it began. A restart takes seconds, so
                    // counted from its start the next kick was already due and fired on the following round.
                    stuckSince = DateTime.UtcNow;

                    continue;
                }

                var start = best;

                var shake = 1 + restarts % 3;

                // Before refine-after, a refining worker's kick is an ordinary one. Marked done even when there was
                // nothing to rebuild from, or the trigger above would fire on every round.
                var refineNow = refining && DateTime.UtcNow >= refineFrom;
                var refined = refineNow ? RefinedStart(env, candidates, random, stream) : null;

                if (refineNow && !refinedYet)
                {
                    refinedYet = true;

                    if (stream >= 0 && stream < _refineFirstMs.Length)
                        _refineFirstMs[stream] = (DateTime.UtcNow - (deadline - budget)).TotalMilliseconds;
                }

                var rolledTail = (List<Vector2>)null;

                if (rollingTails && refined == null && best.Count >= 4)
                {
                    var span = Math.Max(1, best.Count - 2 - best.Count / 2 + 1);
                    var keep = best.Count - 2 - tailKicks % span;

                    tailKicks++;
                    rolledTail = Planner.TailRollout(env, candidates, best, keep, TailWidth, random);

                    if (stream >= 0 && stream < _tailRollouts.Length)
                        _tailRollouts[stream]++;
                }

                if (rolledTail is { Count: > 0 })
                {
                    // Not polished, for the reason a refiner's rebuild is not: the polish is the descent that
                    // walked every kick of this worker back onto its own record.
                    chain = rolledTail;
                }
                else if (refined is { Count: > 0 })
                {
                    // Not descended with Improve: it is one deterministic hill climb and would carry the rebuilt
                    // tail back into the leader's own basin, which is the one place a refiner is not for. The
                    // tearing loop descends from here with this worker's operators.
                    chain = refined;
                }
                else
                {
                    chain = Planner.Improve(env, candidates,
                        Shaken(env, shortlist, start, random, shake), Waiting);
                }

                // **The kick is the one place a requirement can be dropped and not won back.**
                //
                // Ordinary rounds cannot lose one: the accept test allows a couple of per cent of
                // slack and dropping a must-take costs more than the whole site, so it is refused
                // like any other bad move. A kick does not go through that test - it tears a third
                // of the chain and descends from whatever is left - so a worker can arrive in the
                // infeasible region and spend the rest of the window there, because no operator
                // here can bridge back. Restored here, where it is five calls a search rather than
                // one per round.
                if (env.Musts > 0 &&
                    Planner.MustTakeTour(env, candidates, chain, stream, keptOpening?.Count ?? 0) is
                        { Count: > 0 } again)
                {
                    var polishingAgain = System.Diagnostics.Stopwatch.StartNew();

                    chain = Planner.Improve(env, candidates, again,
                        () => Waiting() && polishingAgain.Elapsed < MustTakePolish);

                    ReleaseOpeningNotIn(chain);
                }

                // Its own counter. A kick is a full local search and it recurs - billing it to the
                // opening made a recurring cost look like a fixed one.
                kickMs += watch.Elapsed.TotalMilliseconds;
                stuckSince = DateTime.UtcNow;

                score = Planner.Score(env, chain);

                if (rolledTail is { Count: > 0 } && score > top && stream >= 0 && stream < _tailRecords.Length)
                {
                    _tailRecords[stream]++;
                    _tailGain[stream] += score - top;
                }

                // **The hold does not guard this path, and adding it here cost dearly.** This is the record
                // from the band and reach pass, and reach is the most expensive operator in the search - 8.6ms
                // a try, several hundred tries a window. Refusing its records because they move the opening
                // spends all of that and keeps none of it: the count of chains the hold refused went from 23 a
                // press to 367, and rounds per worker fell from about 2,515 to 1,559.
                //
                // The hold's job is to stop the opening being undone in the first round, and the guard on the
                // tear acceptance does that. A worker that finds a genuine record by moving its opening has
                // earned it.
                if (score > top)
                {
                    best = new List<Vector2>(chain);


                    top = Planner.Score(env, best);
                    plainTop = Planner.Plainly(env, best);

                    chain.Clear();
                    chain.AddRange(best);
                    score = top;

                    improved = DateTime.UtcNow;
                    bettered++;

                    Solving.Scored(stream, plainTop, top, lean);
                    found?.Invoke(new List<Vector2>(best));
                }

                continue;
            }

            work.Clear();
            work.AddRange(chain);

            var how = Draw(random, weight, roleLean);

            used[how]++;

            // Minus one until a tear names a position. The reach operator chooses its own cut and does
            // not go through Tear, so its moves are counted apart rather than guessed at.
            var zone = -1;

            // The fourth is both halves at once - it chooses where to cut and what to build for -
            // so it does not go through Tear and Rebuild. See Reaching.
            if (how == 3)
            {
                watch.Restart();

                var reached = Reaching(env, candidates, shortlist, work, random, ref asked,
                    ref arrived);

                reachMs += watch.Elapsed.TotalMilliseconds;
                spent[how] += watch.Elapsed.TotalMilliseconds;

                if (reached == null)
                    continue;

                zone = 3;

                work.Clear();
                work.AddRange(reached);
            }
            else
            {
                watch.Restart();

                var torn = Torn(env, random, Stuck(), work.Count);
                var at = Tear(env, random, work, how, torn);

                if (torn < sized.Length)
                    sized[torn]++;

                if (at < 0)
                    continue;

                // **Which third of the chain this move worked on.** Propagation runs forwards, so a
                // rune first sourced early multiplies everything after it and the same rune late
                // multiplies almost nothing - position is not symmetric and no operator here is
                // biased by it. Recorded so that whether an early, middle or late move is the one
                // that pays becomes a reading rather than an opinion. See Zoned.
                zone = Zoned(at, work.Count);

                Rebuild(env, shortlist, reach, work, at, torn, random);

                tearMs += watch.Elapsed.TotalMilliseconds;
                spent[how] += watch.Elapsed.TotalMilliseconds;
            }

            if (work.Count < chain.Count || !Sound(env, work))
                continue;

            if (zone >= 0)
                zoneTried[zone]++;

            var worth = Planner.Score(env, work);

            // **Credited for being accepted, not only for setting a record.** A record happens once
            // or twice in a whole window, so scoring the operators on records alone gave all three
            // of them a rate near nought and left the weights flat - 797, 826 and 880 tries, which
            // is three equal shares dressed up as adaptation. Acceptance happens a hundred times and
            // is what the weights are actually able to learn from.
            if (worth > score || worth >= top * (1d - slack))
                paid[how]++;

            if (worth > top && Keeps(work))
            {
                // Polished only when it is already the best there is, because polishing is the
                // expensive half and most torn chains are not worth spending it on.
                watch.Restart();

                var polished = Planner.Improve(env, candidates, new List<Vector2>(work), Waiting);

                polishMs += watch.Elapsed.TotalMilliseconds;
                var after = Planner.Score(env, polished);

                polishes++;

                if (after > worth)
                {
                    polishesThatHelped++;
                    polishGain += after - worth;

                    work.Clear();
                    work.AddRange(polished);
                    worth = after;
                }

                top = worth;
                best = new List<Vector2>(work);
                improved = DateTime.UtcNow;
                since = 0;
                stuckSince = DateTime.UtcNow;
                won[how]++;
                bettered++;

                plainTop = Planner.Plainly(env, best);

                if (zone >= 0)
                    zoneWon[zone]++;

                Solving.Scored(stream, plainTop, top, lean);
                found?.Invoke(new List<Vector2>(best));
            }

            // Accepted, which is not the same as best: a chain a little worse than the record is
            // where the next record usually comes from.
            //
            // **The slack is a share of what the site pays, not of the score.** A held requirement
            // adds Held * Refused to every chain that holds it - a ceiling on the whole site, so it
            // dwarfs the part that varies - and taking a percentage of the total then means something
            // different on every site. Measured on a Grand site with one must-take: total 34,547 of
            // which 25,395 was insistence, so two per cent allowed 691 points of deviation against a
            // site worth 9,152. That is seven per cent of everything in play, where the figure was
            // calibrated at two per cent of 4,092 - about 82 points - and the doc on Slacking still
            // describes it that way.
            //
            // Subtracting the insistence cannot let a chain that DROPS a requirement through: losing
            // one costs the ceiling, which is larger than the whole site and therefore larger than
            // any slack computed from it.
            if (worth > score || worth >= top - plainTop * slack)
            {
                chain.Clear();
                chain.AddRange(work);
                score = worth;
                kept++;

                // A restarted chain that is still climbing is not stuck, whether or not it has reached the record.
                if (rescued > 0 && score > restartTop)
                {
                    restartTop = score;
                    since = 0;
                    stuckSince = DateTime.UtcNow;
                    progressedAt = DateTime.UtcNow;
                }

                if (zone >= 0)
                    zoneKept[zone]++;
            }

            if (halfway <= 0d && marked.ElapsedMilliseconds >= Partway)
                halfway = Planner.Plainly(env, best);

            if (rounds % 32 == 0)
                Reweigh(weight, used, paid, won, spent);
        }

        if (refinedYet && stream >= 0 && stream < _refinedFinal.Length)
            _refinedFinal[stream] = top;

        // **Why the loop stopped, read at the moment it did.** Workers were seen running nought rounds with seconds
        // of their eight still to go, and Waiting has three ways to say no - the deadline, cancellation, and the
        // improvement window - none of which the dump could tell apart.
        var now = DateTime.UtcNow;
        var ended = env.RoundsPerWorker > 0 && rounds >= env.RoundsPerWorker
            ? "rounds"
            : token.IsCancellationRequested
                ? "cancelled"
                : now >= deadline
                    ? "deadline"
                    : settle > TimeSpan.Zero && now - LastImprovement() >= settle
                        ? $"no improvement in the pool for {(now - LastImprovement()).TotalMilliseconds:N0}ms of a {settle.TotalMilliseconds:N0}ms window"
                        : "unknown";
        var leftMs = (deadline - now).TotalMilliseconds;

        // The final ordering, done here rather than at the return so its cost can be reported with the rest.
        // See the note at the return for why it is not held back.
        phase.Restart();

        var finished = Planner.Describe(env,
            Reversed(env, best, env.Placed?.Count ?? 0));

        finishMs = phase.Elapsed.TotalMilliseconds;

        // The same figures as numbers rather than prose, for the press history. See PressHistory.
        Solving.Counted(stream, rounds, bettered, zoneWon, halfway);

        // What this worker did, beside its score. See Solving.Said - the aggregate below is one
        // worker's, whichever finished last, and that is rarely the one worth looking at.
        Solving.Said(stream,
            $"built {raw:N0} " +
            (Shakes is { Length: 4 } shook && shook[0] + shook[1] + shook[2] + shook[3] > 0
                ? $"(shakes {shook[0]} stood, {shook[1]} short, {shook[2]} unsound, " +
                  $"{shook[3]} nothing to tear) "
                : "(unshaken) ") +
            $"-> opened {opened:N0} -> {top:N0} " +
            $"[zones tried {zoneTried[0]}/{zoneTried[1]}/{zoneTried[2]} reach {zoneTried[3]}, " +
            $"kept {zoneKept[0]}/{zoneKept[1]}/{zoneKept[2]} reach {zoneKept[3]}, " +
            $"won {zoneWon[0]}/{zoneWon[1]}/{zoneWon[2]} reach {zoneWon[3]}] " +
            $"({(opened > 0d ? (top - opened) / opened * 100d : 0d):+0.0;-0.0;0.0}%), " +
            $"{rounds:N0} rounds, {bettered:N0} bests, {restarts:N0} kicks " +
            $"({rescued:N0} restarted), " +
            $"reach {won[3]:N0} won of {used[3]:N0}, " +
            $"open {openingMs:N0}ms band {bandMs:N0}ms kick {kickMs:N0}ms (restarts {restartMs:N0}ms) " +
            $"reach {reachMs:N0}ms tear {tearMs:N0}ms polish {polishMs:N0}ms ({polishesThatHelped:N0} of {polishes:N0} added {polishGain:N0}) " +
            $"must-take {mustTakeMs:N0}ms (polish {mustTakePolishMs:N0}ms) tour {touringMs:N0}ms finish {finishMs:N0}ms; " +
            $"stopped on {ended} with {leftMs:N0}ms to the deadline");

        var spread = new List<string>();

        for (var i = 1; i < sized.Length; i++)
        {
            if (sized[i] > 0)
                spread.Add($"{i}x{sized[i]:N0}");
        }

        Telling = $"{rounds:N0} rounds, {kept:N0} accepted, {bettered:N0} new bests, " +
                  $"{restarts:N0} kicks on stagnation; " +
                  $"shortlist {shortlist.Count} of {candidates.Count}; " +
                  $"tears {Named(used, paid, won, spent)}; " +
                  $"sized {(spread.Count > 0 ? string.Join(" ", spread) : "none")} " +
                  $"(of {env.TearLeast} to {env.TearMost}); " +
                  $"reach arrived {arrived:N0} of {asked:N0} asked; " +
                  $"spent opening {openingMs:N0}ms (of which {bandMs:N0}ms on the shared band " +
                  $"search), kicks {kickMs:N0}ms, tearing {tearMs:N0}ms, " +
                  $"reaching {reachMs:N0}ms, polishing {polishMs:N0}ms";

        // **Re-ordered once, here, where it cannot redirect anything.** Reversed was first applied at
        // the opening and on every record, and that is a different thing from refining an answer: a
        // better chain mid-search descends into a different basin afterwards, sometimes a worse one.
        // Measured over ten matched draws, it produced the best result of the batch on two of them and
        // cost 2,692 on a third, for a net of -1,130. As the last thing a worker does it can only add
        // to what it returns, because the reversal kept is the one that scores highest.
        //
        // After Permuted and Slid rather than before: those two move links, and this one re-orders
        // whatever they leave.
        // **The final ordering is not held back.**
        //
        // Keeping the opening exists so the search tries the opening the enumeration chose instead of undoing it in the
        // first round. It has nothing to say about the finished answer: by the time these three run the window
        // is over, and the best order for a chain that exists is the best order. Freezing the opening here too
        // was added for a readout - so that keep-opening=100 would show its opening in the dump's
        // worker list - and it restricts the last pass for every worker that finishes before its budget, which
        // is where the last few hundred points of a chain are made.
        //
        // The consequence to live with: a worker that kept its opening may still return a chain whose first
        // link has moved, so the worker list is read against the branch list with that in mind.
        return finished;
    }

    /// <summary>
    /// A chain worth tearing at: the best of the band search and several randomised greedy builds,
    /// polished.
    ///
    /// **Greedy cannot find this site's answer and the band search can, in under a second.** The
    /// second run of this mode opened at 4,345 from ten randomised constructions while the band
    /// search alone reached 9,395.6 in 800ms on the same site - and Mixed, which is the band search
    /// followed by a general one, finished at 12,984.2. A destroy-and-repair that starts a thousand
    /// points behind the cheapest thing on the shelf is measuring its opening, not its operators.
    ///
    /// So the opening is whichever of the two is better, and the tearing starts from there. This is
    /// what the literature means by constructing with a decent heuristic before improving: the
    /// neighbourhood search is for polishing a good answer, not for discovering one.
    ///
    /// Only the leader is polished, because the polish is the expensive half by a wide margin and a
    /// build that is behind before it starts is not going to pass by being tidied. See Starts.
    /// </summary>
    /// <param name="band">
    /// Times the band search, which is shared between workers and therefore mostly WAITING.
    ///
    /// Banded computes once per environment behind a lock, so the first worker pays nine hundred
    /// milliseconds of work and the other seven pay the same in idling - and both landed in the
    /// opening's total, where they read as construction cost. They are not: one is work that could
    /// be done before the workers start, the other is nothing at all.
    /// </param>
    /// <summary>
    /// Whether the seeded openings were actually kept, counted rather than reasoned about.
    ///
    /// **Because two explanations for the same dump were both wrong.** A press where no worker finished on one
    /// of the two enumerated branches was explained first by the opening shakes and then by the record
    /// reordering; the reordering was a real fault and fixing it changed nothing, because nothing on the page
    /// said whether keeping an opening had ever engaged. These three numbers say it: how many workers were given
    /// an enumerated opening, how many still had it when keeping it began, and how many chains keeping it refused.
    ///
    /// Keeping that refuses nothing had nothing to protect. A worker seeded and not keeping its opening lost it
    /// before the search began.
    /// </summary>
    internal static string KeepOpeningSaid =>
        $"{Volatile.Read(ref _workersKeepingOpening)} of {Volatile.Read(ref _seededWorkers)} seeded worker(s) kept their opening, " +
        $"refusing {Volatile.Read(ref _refusedWhileKeepingOpening):N0} chain(s); " +
        $"{Volatile.Read(ref _releasedForMustTake)} released one because the must-take tour replaced its chain";

    private static int _seededWorkers;

    private static int _workersKeepingOpening;

    private static int _refusedWhileKeepingOpening;

    /// <summary>
    /// The longest the polish after a must-take tour may run.
    ///
    /// **Capped because it was running until the window stopped it.** The polish is a full local search - sweep,
    /// reorder, reverse, shift - and it stopped only when the improvement window did, so it grew to fill whatever
    /// time the worker reached it with. Measured on Craggy Peninsula, twenty explosives and two must-takes: 0.9 to
    /// 1.9 seconds a worker, of an eight second window, before the search loop - which improves the same chain by
    /// other moves - ran a single round. The first sweep is what straightens the tour's bridging links, and that
    /// is early; half a second is chosen, not measured, and a paired batch decides whether it holds.
    /// </summary>
    private static readonly TimeSpan MustTakePolish = TimeSpan.FromMilliseconds(500);

    /// <summary>Kept openings let go because a must-take tour no longer started with them. See ReleaseOpeningNotIn.</summary>
    private static int _releasedForMustTake;

    /// <summary>
    /// Starts the per-solve opening tallies over - kept openings and touring targets - before any worker runs. See
    /// KeepOpeningSaid and TouringSaid.
    /// </summary>
    internal static void ForgetOpeningTallies()
    {
        Volatile.Write(ref _seededWorkers, 0);
        Volatile.Write(ref _workersKeepingOpening, 0);
        Volatile.Write(ref _refusedWhileKeepingOpening, 0);
        Volatile.Write(ref _releasedForMustTake, 0);
        Array.Clear(_touredTo);
        Array.Clear(_refines);
        Array.Clear(_refineKept);
        Array.Clear(_refineOf);
        Array.Clear(_refineFromWorth);
        Array.Clear(_refinedFinal);
        Array.Clear(_refineFirstMs);
    }

    private static List<Vector2> Opening(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> shortlist, Random random, Func<bool> waiting, CancellationToken token,
        int shakes, out List<Vector2> seeded, List<Vector2> seed = null,
        System.Diagnostics.Stopwatch band = null)
    {
        // Which enumerated opening this worker was given, for the caller to hold it in place for a while.
        // Null when the worker drew its own. See SolverSettings.ThreadRoles.
        seeded = null;

        // `shakes` is the worker number, so nought is the one that exploits. See below.
        var stream = shakes;

        // What the shakes did, for the report: stood, short, unsound, nothing to tear. See Shaken.
        Shakes = new int[4];

        List<Vector2> best = null;
        var top = double.NegativeInfinity;

        // Last time's answer, if the ground still allows it. Checked rather than trusted: explosives
        // may have gone down since, which moves the origin and can strand a link that was fine.
        if (seed is { Count: > 0 } && Sound(env, seed))
        {
            best = shakes > 0
                ? Shaken(env, shortlist, seed, random, Math.Min(shakes, Math.Max(1, env.OpeningShakes)),
                    Shakes)
                : new List<Vector2>(seed);

            top = Planner.Score(env, best);
        }

        // The bands next, since it is the one that knows about the shape of the site. Worked out
        // once for the whole solve however many workers and restarts ask for it. See Banded.
        band?.Start();

        // `shakes` IS the worker number - Search passes its stream in under that name, because the
        // opening is shaken by it. See Opening's signature.
        var bands = Banded(env, token, shakes);

        band?.Stop();

        if (bands is { Count: > 0 } banded)
        {
            // **Capped, and the cap is now a setting because what it was tuned against turned out
            // not to matter.**
            //
            // Two was chosen when three looked like demolition - the fourth worker came back at
            // 11,454 where the others reached 14,735 - and that reasoning treats a weak opening as a
            // loss. It is not. Openings were measured at 1,159 to 1,297 against finishes above
            // 4,100: the window is worth +217%, so the opening decides almost nothing about the
            // score.
            //
            // What it does decide is whether the workers are different from each other, and at a cap
            // of two every worker from the third onwards gets the same two shakes of the same band
            // chain and is then polished onto the same local optimum. Measured on eight threads:
            // three of them opened on the identical chain, twice running, and several finished on
            // identical numbers. Those are not eight samples. See SolverSettings.OpeningShakes.
            var opened = shakes > 0
                ? Shaken(env, shortlist, banded, random, Math.Min(shakes, Math.Max(1, env.OpeningShakes)),
                    Shakes)
                : new List<Vector2>(banded);

            var worth = Planner.Score(env, opened);

            // **A shaken opening is taken because it is different, not because it is better.**
            //
            // This kept it only when it beat what was already in hand, and a shake is a
            // perturbation - it makes the chain worse nearly every time, by design. So every
            // worker fell back to the same unshaken band chain, and the pool explored one point
            // with eight threads. Measured on Craggy Peninsula: all eight BUILT 106,088, before
            // any polish, and finished there. The cap on shakes was blamed for this and was never
            // the reason; raising it makes the shaken chain score worse still and be refused more
            // surely.
            //
            // Worker nought is the exception and keeps the best construction, so the pool never
            // loses the greedy answer while the others go looking. That is the ordinary division
            // in a multi-start: one exploits, the rest explore.
            if (stream > 0 || worth > top)
            {
                best = opened;
                top = worth;
            }
        }

        // **An exploring worker opens on a randomised construction of its own, not on the chain
        // everybody else has.**
        //
        // This is the one thing measured to give the pool real variance, and it was found by accident:
        // on a COLD press there is no seed, and Banded hands its answer to exactly one worker and null
        // to whoever asks while it is building - so seven workers fell through to the greedy draw
        // below. They built about 4,600 apiece against the band worker's 29,072, and finished at
        // 32,435, 32,538, 32,701, 32,765, 33,124, 33,273 and 36,056. The site scored 10,655.
        //
        // The next press on the same site with the same settings had a seed, so every worker opened
        // from it, and five of eight finished at 33,276.5 to the decimal for a site score of 7,875 -
        // the figure it had been stuck on all session. Same site, same switches, the openings the only
        // difference, and a third more content on the varied one.
        //
        // **The shake cannot substitute for it, and now it is known why.** With the tear escalation in
        // place the shake does stand - one to three times per worker - and the built scores still come
        // back identical to the unshaken chain: Rebuild re-inserts the best candidates it can find for
        // the hole, and those are the spots that were just torn out of it. A perturbation the repair
        // greedily undoes is not a perturbation. The counters read 1 to 3 stood against 7 to 53 short,
        // so what survives is the smallest tear, which is exactly the one Rebuild puts straight back.
        //
        // Taken because it is different rather than because it is better, which is this file's rule
        // for an opening already. An opening is worth about thirty per cent of a finish - the cold
        // press proves it in the direction that matters, 4,600 to over 32,000 - so a weak start costs
        // little and a shared one costs the whole pool.
        //
        // Worker nought keeps the seed, the bands and the polish, so the incumbent is never lost.
        if (stream > 0)
        {
            // **One worker in four ignores the enumeration and draws its own.** The same proportion this
            // file already reserves for a thread that never adopts, and for the same reason: if the
            // enumeration is wrong on this site, somebody has to be looking somewhere else. See
            // SolverSettings.ThreadRoles.
            // **The role says so, rather than a modulo saying so.** See ThreadRoles and the comment on
            // SolverSettings.ThreadRoles for what the modulo produced by accident.
            var hedging = Role(env, stream) is
                              { Opening: ThreadRoles.Opens.Fresh or ThreadRoles.Opens.Tour or ThreadRoles.Opens.Refine } ||
                          Role(env, stream) == null && stream % 4 == 3;
            var openings = env.Openings;
            var drawn = (List<Vector2>)null;

            if (!hedging && openings is { Count: > 0 })
            {
                // **Counted over the workers that take an enumerated opening**, so the ones that continue
                // from the incumbent or draw their own do not leave gaps in the rotation.
                //
                // Read off the roles rather than worked out arithmetically. It was `stream - 1 - stream / 4`,
                // which counted the hedges below this worker on the assumption that every fourth one hedges -
                // true of the modulo the roles replaced, and wrong for any line that puts them elsewhere.
                var slot = Slot(env, stream);

                // Spread across the branches the site offers before across variations of one branch - see
                // Openings.OpeningForWorker, and the measurement that a ranked list put three workers on one
                // branch and one on the other. Completed with this worker's own randomness, so two workers on
                // the same opening still explore differently.
                var prefix = Openings.OpeningForWorker(env, openings, slot);

                if (prefix is { Count: > 0 })
                {
                    drawn = Planner.Greedy(env, candidates, random,
                        1 + random.Next(Math.Max(1, env.OpeningChoices)), null, prefix);

                    // Reported so the caller can hold it in place for a share of the window, and the
                    // positioning the enumeration chose is actually tried before the operators may undo it.
                    // See SolverSettings.ThreadRoles.
                    if (drawn is { Count: > 0 })
                        seeded = new List<Vector2>(prefix);
                }
            }

            drawn ??= Planner.Greedy(env, candidates, random,
                1 + random.Next(Math.Max(1, env.OpeningChoices)));

            if (drawn is { Count: > 0 })
            {
                best = drawn;
                top = Planner.Score(env, best);
            }
        }

        // The greedy starts, which are what a worker opens on when the bands produced nothing.
        //
        // **Skipped once an exploring worker has its own chain**, because the first of them is a
        // deterministic greedy - no random, one sample - and it is the same chain on every thread.
        // Leaving it in put that chain back over the shaken one the moment it scored higher, which
        // it usually does, and undid the diversity above.
        for (var i = 0; i < Starts && (stream == 0 || best == null); i++)
        {
            if (i > 0 && !waiting())
                break;

            var raw = Planner.Greedy(env, candidates, i == 0 ? null : random,
                i == 0 ? 1 : 1 + random.Next(Math.Max(1, env.OpeningChoices)));
            var worth = Planner.Score(env, raw);

            if (worth <= top)
                continue;

            top = worth;
            best = raw;
        }

        best ??= Planner.Greedy(env, candidates, null, 1);

        // **The polish runs for the exploiting worker only, because it is what erased the diversity.**
        //
        // Planner.Improve is a hill climb with no random in it, over the same candidate set for every
        // worker, so it is one deterministic descent - and the shaken openings all sit in the basin of
        // the same local optimum. Every worker therefore arrived at the identical chain no matter how
        // differently it started. Measured on a 15-explosive site: seven of eight workers BUILT
        // 33,268.0 to the decimal, 0 bests between them over 3,000 to 4,500 rounds each, with the
        // shake raised to 12 - so the shake was not the variable, the descent was.
        //
        // The comment on `raw` above named these two possibilities and wanted opposite fixes - more
        // shake, or a narrower descent. More shake has been tried, at 2 and at 12, and changed nothing
        // measurable. This is the other one.
        //
        // Nothing is lost by dropping it. The tearing loop below descends too, and it does so with THIS
        // worker's operator mix rather than a shared hill climb - so the descent becomes part of what
        // makes the workers differ instead of the thing that makes them identical. An opening is worth
        // about thirty per cent of a finish, and a shaken chain is taken because it is different rather
        // than because it is better, which is already this file's rule.
        //
        // Worker nought keeps the polish, so the pool never loses the good construction while the
        // others go looking. That is the same division as everywhere else here.
        return stream == 0 ? Planner.Improve(env, candidates, best, waiting) : best;
    }

    /// <summary>
    /// What this worker's opening shakes did: stood, short, unsound, nothing to tear. See Shaken.
    ///
    /// Thread static because there is one per worker and they run at once. Read straight after
    /// Opening returns, on the same thread that filled it.
    /// </summary>
    [System.ThreadStatic]
    private static int[] Shakes;

    /// <summary>
    /// Which third of the chain a tear at this position works on, or three for the reach operator,
    /// which chooses its own cut and is counted apart.
    ///
    /// Thirds by link index rather than by distance, because the objective is a sequence: what matters
    /// about a link is how many links come after it to inherit what it unearths, not how far along the
    /// route it stands. See the zone counters.
    /// </summary>
    private static int Zoned(int at, int links)
    {
        if (links <= 0)
            return 0;

        var third = links / 3;

        return at < Math.Max(1, third) ? 0 : at < Math.Max(2, third * 2) ? 1 : 2;
    }

    /// <summary>What the last run did, for the dump.</summary>
    public static string Telling { get; private set; } = "has not run";

    /// <remarks>
    /// **"won" credits whoever lands the final improving move, not whoever made it reachable - and an
    /// operator reading nought there can still be the difference between two very different answers.**
    ///
    /// Measured on a six link site: reach won NOTHING in any press, while giving its bridges three
    /// links instead of one took the pool from 5,562 to 7,221 and the threads escaping their basin
    /// from nought of eight to six of eight. Reverting that one setting put both back. What reach was
    /// doing was ACCEPTED moves - sixty seven of them - each repositioning the current chain, after
    /// which "related" made the improving move and took the credit.
    ///
    /// So read kept and won together. Neither alone describes an operator's worth, and won alone
    /// nearly got reach written off as inert on a site where it was carrying the result.
    /// </remarks>
    private static string Named(int[] used, int[] paid, int[] won, double[] spent)
    {
        var names = new[] { "segment", "worst", "related", "reach" };
        var said = new List<string>();

        // **What an attempt cost is the half of this that was missing.** Tried / kept / won rates an
        // operator per attempt, and per attempt reach was the best of the four - two and a half wins
        // per thousand against about one for the rest, over ten runs of one site. Per second it was
        // the worst of the four by a factor of nearly three, because an attempt cost it nine times
        // more, and that is the number a search with a fixed window actually spends. Reweigh has had
        // the per-operator time since it learned to rank by it; only the dump could not see it.
        for (var i = 0; i < names.Length; i++)
        {
            said.Add($"{names[i]} {used[i]:N0} tried / {paid[i]:N0} kept / {won[i]:N0} won" +
                     $" at {(used[i] > 0 ? spent[i] * 1000d / used[i] : 0d):N0}us");
        }

        return string.Join(", ", said);
    }

    /// <summary>
    /// The spots a repair is allowed to reach for: the richest, plus enough of the rest to keep the
    /// site connected. See Spread.
    /// </summary>
    private static List<Vector2> Shortlist(PlanEnvironment env, List<Vector2> candidates)
    {
        var worth = new List<(Vector2 At, double Solo)>(candidates.Count);
        var one = new List<Vector2> { Vector2.Zero };

        foreach (var at in candidates)
        {
            one[0] = at;
            worth.Add((at, Planner.Score(env, one)));
        }

        worth.Sort((a, b) => b.Solo.CompareTo(a.Solo));

        var (wanted, spread, sparse) = Listing(env);
        var kept = new List<Vector2>();

        for (var i = 0; i < worth.Count && kept.Count < wanted; i++)
            kept.Add(worth[i].At);

        var rich = kept.Count;

        // **The spacing relaxes until the spread is actually filled, because a fixed one cannot fit
        // two sizes of site.**
        //
        // The spread exists for the connective ground between clusters - the spots a chain needs in
        // order to leave one - and it is chosen from what is left after the rich picks, at least this
        // far from everything already kept. On a Grand site of 2,788 candidates the rich picks leave
        // plenty of open ground and 45 grid yields 87 of the 200 asked for. On an ordinary expedition
        // of 871 they blanket it, and 45 yields FIVE of a hundred: the half of the shortlist that
        // exists to let a chain travel was, in effect, missing.
        //
        // Measured by hand on that site, dropping the spacing: 5 picks at 45, 35 at 20, 90 at 10 -
        // and the scores moved with it, 7,221 flat at 45 against a mode of 7,317 and no press below
        // 7,221 at 10. A dose response across three settings, with the count moving as predicted.
        //
        // So the SETTING becomes a starting point rather than an answer, and the quota is what is
        // honoured. It steps down while the spread is underfilled and stops the moment it is not,
        // which leaves a Grand site exactly as it was - 45 already fills there, so nothing relaxes -
        // and lets a small one find its own spacing without anybody choosing a preset per site size.
        //
        // Deliberately not a mid-solve adjustment. What went wrong is a property of the site's
        // geometry and is known the moment the candidates exist; nothing about it changes as the
        // search proceeds, and a moving shortlist would be a moving target for no gain.
        var relaxed = sparse;
        var steps = 0;

        while (true)
        {
            for (var i = rich; i < worth.Count && kept.Count < rich + spread; i++)
            {
                var at = worth[i].At;
                var clear = true;

                foreach (var already in kept)
                {
                    if (Vector2.DistanceSquared(at, already) < relaxed * relaxed)
                    {
                        clear = false;

                        break;
                    }
                }

                if (clear)
                    kept.Add(at);
            }

            // **The whole quota, not half of it.** Half was the first rule and the measurements say it
            // stops in the wrong place: this site yielded 5 picks at 45 grid, 35 at 20 and 90 at 10,
            // and the scores tracked the count - 7,221 flat at 45, a 7,257 at 20, a mode of 7,317 at
            // 10. A half-quota stop lands around 20 and leaves the best of it unclaimed.
            //
            // The floor is what stops this running away on a site with genuinely no room, and it is
            // the honest brake: a distance, below which picks stop being a spread at all. A fraction
            // of the quota is not - it is a guess about how many are enough, and the evidence here is
            // that the answer is "all of them, if the ground allows".
            if (spread <= 0 || kept.Count - rich >= spread || relaxed <= Tightest)
                break;

            relaxed = MathF.Max(Tightest, relaxed * 0.5f);
            steps++;
        }

        var got = kept.Count - rich;

        Spreading = spread <= 0
            ? "no spread asked for - the shortlist is the richest spots and nothing else"
            : got >= spread
                ? $"filled - {got:N0} of {spread:N0} connective spots at {relaxed:0.#} grid apart"
                : got >= spread * 3 / 4
                    ? $"nearly - {got:N0} of {spread:N0} connective spots at {relaxed:0.#} grid " +
                      $"apart, {(relaxed <= Tightest ? "and the spacing is at its floor" : "still relaxing")}"
                    : $"STARVED - only {got:N0} of {spread:N0} connective spots, at {relaxed:0.#} " +
                      $"grid apart and {(relaxed <= Tightest ? "already at the floor" : "still relaxing")}. " +
                      "This is the half of the shortlist a chain needs in order to leave a cluster; " +
                      "short of it, the search can rearrange inside clusters and not travel between " +
                      "them. Either the rich picks are blanketing the site - try fewer - or there is " +
                      "genuinely no open ground here.";

        Listed = $"{rich:N0} richest asked {wanted:N0}, {kept.Count - rich:N0} spread asked " +
                 $"{spread:N0}, {kept.Count:N0} of {candidates.Count:N0}" +
                 (steps == 0
                     ? $" - spacing {sparse:0.#} filled it first time"
                     : $" - spacing relaxed {sparse:0.#} to {relaxed:0.#} over {steps} step" +
                       (steps == 1 ? "" : "s") +
                       (relaxed <= Tightest && kept.Count - rich < spread / 2
                           ? ", and the site still has no room for more"
                           : ""));

        return kept;
    }

    /// <summary>
    /// How close together the spread will ever be pushed. See Shortlist's relaxing.
    ///
    /// A floor, because the picks stop being a spread long before the spacing reaches nothing: at a
    /// few grid apart a hundred of them cover the same ground twenty would, at a hundred times the
    /// ranking cost per repair. Eight grid is a little under a quarter of a blast radius, so two spots
    /// that far apart still catch meaningfully different markers - and it is the last term of an exact
    /// halving from the default 64, which keeps the sequence legible in the log: 64, 32, 16, 8. The
    /// first floor tried was five, which is both past the point of usefulness and lands the sequence
    /// on 5.6.
    /// </summary>
    private const float Tightest = 8f;

    /// <summary>What the shortlist actually kept against what it asked for. See Listing.</summary>
    public static string Listed { get; private set; } = "has not run";

    /// <summary>
    /// Whether the connective half of the shortlist is actually being filled, in words.
    ///
    /// **The counter that found the fault, stated as a verdict rather than left to be worked out.**
    /// "5 spread asked 100" was sitting in the dump for as long as the shortlist has existed and read
    /// as a detail; it was the whole reason a chain could not leave a cluster on a small site. A line
    /// that says STARVED when the spread is under half its quota is a line somebody notices.
    ///
    /// Reported separately from Listed because the two answer different questions - what was built,
    /// and whether it was enough. See Shortlist's relaxing.
    /// </summary>
    public static string Spreading { get; private set; } = "has not run";

    /// <summary>
    /// What this worker starts out believing about the four destroys.
    ///
    /// Balanced for one worker in five and leaning hard for the rest - seventy per cent to one
    /// destroy and ten to each of the others. Fixed rather than random, so a worker's behaviour is
    /// repeatable and the eight of them between them cover every operator twice over on eight
    /// threads.
    ///
    /// Held for the whole window and multiplied into every draw, beside the weights Reweigh learns. It was
    /// once only the starting value of those weights, and Reweigh erased it within about 500 rounds, so the
    /// role named in the line stopped deciding anything early in the window. See Draw and
    /// SolverSettings.ThreadRoles.
    /// </summary>
    /// <summary>
    /// Each worker's starting operator weights, as the search itself works them out.
    ///
    /// **Because the mix shifted and no readout said whose.** A batch was measured spending its window on the
    /// reach operator - 435 tears against 2,001 on an earlier batch of the same site, with reaching up from
    /// 4,494ms to 5,436ms - and the roles said nothing was different. A role naming a bias and the weights the
    /// search leans by are two claims, and only the second decides anything.
    ///
    /// The role's lean, which stays fixed for the window, not the weights Reweigh learns beside it.
    /// </summary>
    internal static string LeaningsSaid()
    {
        var b = new System.Text.StringBuilder();

        b.AppendLine($"    worker  {string.Join("    ", Destroys)}");

        for (var stream = 0; stream < _leaned.Length; stream++)
        {
            var weight = Volatile.Read(ref _leaned[stream]);

            if (weight == null)
                continue;

            var said = new List<string>();

            foreach (var one in weight)
                said.Add(one.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5));

            b.AppendLine($"    {stream,6}  {string.Join("  ", said)}");
        }

        return b.ToString().TrimEnd();
    }

    /// <summary>
    /// Records the weights a worker actually leaned by, so the readout cannot be taken from the wrong
    /// environment.
    ///
    /// **The first attempt at this readout asked Leaning for the weights using Scoring.LastEnv, which is the
    /// environment built to score a chain and not the one the pool searches under.** It carries no roles, so
    /// every worker read as unbiased and the readout looked like the bug it was meant to find. What a worker
    /// used is a fact about the run, so it is written down as the run makes it.
    /// </summary>
    private static double[] Leaned(double[] weight, int stream)
    {
        if (stream >= 0 && stream < _leaned.Length)
            Volatile.Write(ref _leaned[stream], weight);

        return weight;
    }

    private static readonly double[][] _leaned = new double[16][];

    private static double[] Leaning(PlanEnvironment env, int stream)
    {
        var weight = Leaned(new[] { 1d, 1d, 1d, 1d }, stream);

        if (stream < 0)
            return weight;

        var told = Role(env, stream) is { } mine ? (At: mine.Tear, Share: mine.Share) : (At: -1, Share: 0d);

        // No role for this worker, or one told to stay even. Both mean the same thing and both are ordinary
        // rather than a mistake - a line shorter than the pool is how somebody says "the rest of them
        // balanced". See SolverSettings.ThreadRoles.
        if (told.At < 0)
            return weight;

        // The share named goes to the one favoured and the remainder is split equally. Kept as
        // plain proportions rather than percentages, because Draw rolls against the total and does
        // not care what they add up to.
        var rest = Math.Max(0d, 100d - told.Share) / 3d;

        for (var i = 0; i < weight.Length; i++)
            weight[i] = i == told.At ? Math.Max(0.01d, told.Share) : Math.Max(0.01d, rest);

        Leaned(weight, stream);

        return weight;
    }

    /// <summary>
    /// Whether this thread is one of the ones held out of sharing.
    ///
    /// Forgiving in the same way the tearing mix is, and for the same reason: it is a line somebody
    /// types, so spaces, a trailing comma and a word that is not a number all mean "not excluded"
    /// rather than failing the search. Read per kick, which is a handful of times a window - a
    /// string split there is nothing against the local search it precedes.
    ///
    /// See SolverSettings.ShareNot.
    /// </summary>
    private static bool Excluded(string held, int stream)
    {
        if (string.IsNullOrWhiteSpace(held))
            return false;

        foreach (var part in held.Split(','))
        {
            if (int.TryParse(part.Trim(), out var one) && one == stream)
                return true;
        }

        return false;
    }

    /// <summary>The names a thread's entry may use, in the order the weights are held.</summary>
    private static readonly string[] Destroys = ["seg", "worst", "rel", "reach"];

    /// <summary>
    /// What one thread was told to favour, and how hard.
    ///
    /// Parsed per solve rather than per round, and forgiving on purpose: this is a line somebody
    /// types, so extra spaces, a trailing comma and an unknown word all mean "even" rather than
    /// failing the search. An unreadable entry costs that thread its bias, which is a thread running
    /// the ordinary search - the thing every fifth one is doing anyway.
    /// </summary>
    /// <summary>
    /// This worker's place in the rotation over the enumerated openings, counting only the workers that take
    /// one.
    ///
    /// The openings are spread over the branches a site offers, so what matters is a contiguous number over the
    /// workers being seeded - a gap where a worker continues from the incumbent or draws its own would leave a
    /// branch uncovered. See Openings.OpeningForWorker.
    /// </summary>
    /// <summary>This worker's place among the workers given an enumerated opening. See Openings.OpeningForWorker.</summary>
    internal static int Slot(PlanEnvironment env, int stream)
    {
        if (env.Roles is not { Count: > 0 } roles)
            return Math.Max(0, stream - 1 - stream / 4);

        var slot = 0;

        for (var i = 0; i < stream && i < roles.Count; i++)
        {
            if (roles[i].Opening == ThreadRoles.Opens.Enumerated)
                slot++;
        }

        return slot;
    }

    /// <summary>
    /// This worker's place among the workers whose opening is a tour, so each aims at a different target. See
    /// Planner.RichestUnreached.
    /// </summary>
    private static int TourSlot(PlanEnvironment env, int stream)
    {
        var slot = 0;

        for (var i = 0; i < stream && env.Roles != null && i < env.Roles.Count; i++)
        {
            if (env.Roles[i].Opening == ThreadRoles.Opens.Tour)
                slot++;
        }

        return slot;
    }

    /// <summary>How many workers in the pool tour. See Planner.RichestUnreached.</summary>
    private static int TourCount(PlanEnvironment env) =>
        env.Roles == null ? 0 : System.Linq.Enumerable.Count(env.Roles, r => r.Opening == ThreadRoles.Opens.Tour);

    /// <summary>Where each touring worker was sent in the last solve, by worker, or zero. See TouringSaid.</summary>
    private static readonly Vector2[] _touredTo = new Vector2[16];

    /// <summary>Which target each touring worker was routed through, for the dump.</summary>
    internal static string TouringSaid
    {
        get
        {
            var said = new List<string>();

            for (var i = 0; i < _touredTo.Length; i++)
            {
                if (_touredTo[i] != Vector2.Zero)
                    said.Add($"worker {i} through ({_touredTo[i].X:0},{_touredTo[i].Y:0})");
            }

            return said.Count == 0 ? "no worker toured" : string.Join(", ", said);
        }
    }

    /// <summary>
    /// A refining worker's new start: the first links of the pool's best chain, the rest rebuilt by a randomised
    /// greedy completion with this worker's own random, and the must-takes fetched. Null when the pool has no
    /// best yet, or its best is too short to split, and the caller kicks as any other worker would.
    ///
    /// How many links are kept is the role's keep-leader, or else spread across the refining workers - two keep a
    /// third and two thirds, four keep a fifth, two fifths, three fifths and four fifths - so no two rebuild the
    /// same part of the chain. At least one link is kept and at least one rebuilt.
    /// </summary>
    private static List<Vector2> RefinedStart(PlanEnvironment env, List<Vector2> candidates, Random random, int stream)
    {
        var (leader, leaderWorth) = Solving.Leader();

        if (leader is not { Count: > 1 })
            return null;

        var share = Role(env, stream) is { KeepLeader: >= 0d } role
            ? role.KeepLeader
            : (RefineSlot(env, stream) + 1d) / (RefinerCount(env) + 1d);
        var keep = Math.Clamp((int)Math.Round(leader.Count * share), 1, leader.Count - 1);
        var prefix = leader.GetRange(0, keep);
        var rebuilt = Planner.Greedy(env, candidates, random,
            1 + random.Next(Math.Max(1, env.OpeningChoices)), null, prefix);

        if (rebuilt is not { Count: > 0 })
            return null;

        if (env.Musts > 0 &&
            Planner.MustTakeTour(env, candidates, rebuilt, stream, keep) is { Count: > 0 } fetched)
            rebuilt = fetched;

        if (stream >= 0 && stream < _refines.Length)
        {
            _refines[stream]++;
            _refineKept[stream] = keep;
            _refineOf[stream] = leader.Count;
            _refineFromWorth[stream] = leaderWorth;
        }

        return rebuilt;
    }

    /// <summary>This worker's place among the refining workers. See RefinedStart.</summary>
    private static int RefineSlot(PlanEnvironment env, int stream)
    {
        var slot = 0;

        for (var i = 0; i < stream && env.Roles != null && i < env.Roles.Count; i++)
        {
            if (env.Roles[i].Opening == ThreadRoles.Opens.Refine)
                slot++;
        }

        return slot;
    }

    /// <summary>How many workers in the pool refine. See RefinedStart.</summary>
    private static int RefinerCount(PlanEnvironment env) =>
        env.Roles == null ? 0 : System.Linq.Enumerable.Count(env.Roles, r => r.Opening == ThreadRoles.Opens.Refine);

    /// <summary>
    /// Per refining worker in the last solve: how many times it rebuilt from the pool's best, how many links it
    /// kept the last time and out of how many, what that best scored when it was taken, and the worker's record at
    /// the end. The comparison that says whether refining paid is the last two: a record above the chain it was
    /// rebuilt from is a better answer than the pool had. See RefiningSaid.
    /// </summary>
    private static readonly int[] _refines = new int[16];

    /// <summary>See _refines.</summary>
    private static readonly int[] _refineKept = new int[16];

    /// <summary>See _refines.</summary>
    private static readonly int[] _refineOf = new int[16];

    /// <summary>See _refines.</summary>
    private static readonly double[] _refineFromWorth = new double[16];

    /// <summary>See _refines.</summary>
    private static readonly double[] _refinedFinal = new double[16];

    /// <summary>When each refining worker first rebuilt, in milliseconds from the start of its search. See _refines.</summary>
    private static readonly double[] _refineFirstMs = new double[16];

    /// <summary>What each refining worker did in the last solve, for the dump.</summary>
    internal static string RefiningSaid
    {
        get
        {
            var said = new List<string>();

            for (var i = 0; i < _refines.Length; i++)
            {
                if (_refines[i] > 0)
                {
                    var against = _refinedFinal[i] - _refineFromWorth[i];

                    said.Add($"worker {i} rebuilt from the pool's best {_refines[i]} time(s), first at " +
                             $"{_refineFirstMs[i]:N0}ms, the last keeping " +
                             $"{_refineKept[i]} of {_refineOf[i]} links of a {_refineFromWorth[i]:N0} chain, and ended at " +
                             $"{_refinedFinal[i]:N0} ({(against >= 0 ? "+" : "")}{against:N0} against it)");
                }
            }

            return said.Count == 0 ? "no worker refined" : string.Join("; ", said);
        }
    }

    /// <summary>
    /// How many next links a tail rollout tries. Ten is chosen, not measured: at two finishes each and about
    /// seven links a finish, a rollout is some 140 greedy steps and 20 scored chains, a small share of a kick
    /// that spent about 1.5s in the polish it replaces.
    /// </summary>
    private const int TailWidth = 10;

    /// <summary>
    /// Per worker since the plugin loaded: how many tail rollouts it ran at its kicks, how many of them set a
    /// record outright, and what those records added. Records the tearing loop sets later from a rolled tail are not
    /// counted here. See RollingTailsSaid.
    /// </summary>
    private static readonly int[] _tailRollouts = new int[16];

    /// <summary>See _tailRollouts.</summary>
    private static readonly int[] _tailRecords = new int[16];

    /// <summary>See _tailRollouts.</summary>
    private static readonly double[] _tailGain = new double[16];

    /// <summary>
    /// What the tail rollouts have done since the plugin loaded, for the dump. Not cleared per solve: the
    /// continuous reroll mode starts the next pass within a tenth of a second, so a per-solve count read in a
    /// dump was nearly always of a pass too young to have kicked.
    /// </summary>
    internal static string RollingTailsSaid
    {
        get
        {
            var said = new List<string>();

            for (var i = 0; i < _tailRollouts.Length; i++)
            {
                if (_tailRollouts[i] > 0)
                {
                    said.Add($"worker {i} rolled {_tailRollouts[i]} tail(s), {_tailRecords[i]} setting a record " +
                             $"at once, worth {_tailGain[i]:N0}");
                }
            }

            return said.Count == 0 ? "no tail rolled" : string.Join("; ", said);
        }
    }

    /// <summary>This worker's role, or null when no line described the pool. See ThreadRoles.</summary>
    private static ThreadRoles.Role Role(PlanEnvironment env, int stream) =>
        env.Roles is { Count: > 0 } roles && stream >= 0 && stream < roles.Count ? roles[stream] : null;

    /// <summary>
    /// Whether this worker opens on the enumerated openings, and so has to wait for them. True for a worker no
    /// roles line describes, because Opening then draws on the enumeration for every worker but the hedges.
    /// </summary>
    internal static bool TakesEnumeratedOpening(PlanEnvironment env, int stream) =>
        stream > 0 && Role(env, stream) is null or { Opening: ThreadRoles.Opens.Enumerated };

    private static (int At, double Share) Told(string mix, int stream)
    {
        if (string.IsNullOrWhiteSpace(mix))
            return (-1, 0d);

        var parts = mix.Split(',');

        if (stream >= parts.Length)
            return (-1, 0d);

        var said = parts[stream].Trim();

        if (said.Length == 0)
            return (-1, 0d);

        var share = 70d;
        var colon = said.IndexOf(':');

        if (colon >= 0)
        {
            if (double.TryParse(said[(colon + 1)..].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var asked))
                share = Math.Clamp(asked, 1d, 100d);

            said = said[..colon].Trim();
        }

        for (var i = 0; i < Destroys.Length; i++)
        {
            if (string.Equals(said, Destroys[i], StringComparison.OrdinalIgnoreCase))
                return (i, share);
        }

        return (-1, 0d);
    }

    /// <summary>
    /// Picks a destroy operator in proportion to how well each has been doing, times the role's lean. An even
    /// lean is equal for all four, so it leaves the draw to the learned weights alone.
    /// </summary>
    private static int Draw(Random random, double[] weight, double[] roleLean)
    {
        var total = 0d;

        for (var i = 0; i < weight.Length; i++)
            total += weight[i] * roleLean[i];

        var roll = random.NextDouble() * total;

        for (var i = 0; i < weight.Length; i++)
        {
            roll -= weight[i] * roleLean[i];

            if (roll <= 0d)
                return i;
        }

        return weight.Length - 1;
    }

    /// <summary>
    /// Moves the weights towards what has been winning, keeping a floor so nothing is ever ruled out.
    /// </summary>
    private static void Reweigh(double[] weight, int[] used, int[] paid, int[] won, double[] spent)
    {
        for (var i = 0; i < weight.Length; i++)
        {
            if (used[i] == 0)
                continue;

            // **A record is worth very much more than an accepted move, and twenty was not enough.**
            // Measured across eight workers: worst-removal was accepted seventy two times in six
            // hundred and forty four and set no record at all, while reach was accepted nine times
            // in six hundred and thirty five and set the only one that mattered - the chain that
            // broke a band six workers were stuck in. At twenty, the operator that goes nowhere
            // outscored the operator that escapes, because acceptance is a lateral move inside the
            // band and there are far more of them.
            //
            // Ropke and Pisinger score records, improvements and acceptances separately for exactly
            // this reason; the ratio between them is the part that has to suit the problem, and here
            // the records are rare and worth everything.
            var rate = (won[i] * 100d + paid[i]) / used[i];

            // **Reward per attempt, and dividing by cost was tried twice now - it is measured, it
            // works, and it is not worth it.**
            //
            // Reach is twelve to sixteen times the cost of the other three: 10,185us an attempt
            // against 623 for related, 849 for segment and 1,736 for worst, taking 22.3 seconds of
            // a 33.9 second solve. Dividing the rate by each operator's own cost per attempt does
            // exactly what it should - reach fell from 2,187 attempts to 561 and reaching from
            // 22.3s to 9.0s.
            //
            // **The score went with it.** From scratch, 23,517 to 20,092; from three laid by hand,
            // 23,907 to 22,462. Fifteen per cent of the answer for twelve seconds.
            //
            // The reason is in the same tables: across both runs segment, worst and related won
            // NOTHING between them, and reach won every record there was - five of five on one.
            // The cheap operators are cheap because they shuffle inside the band; reach is what
            // leaves it. Charging it for the difference buys a faster search that finds less.
            //
            // So: per attempt, and the spent array stays because it is what settled this. If the
            // question is asked a third time, the answer is these two lines of dump, not another
            // switch.
            weight[i] = 0.7d * weight[i] + 0.3d * (0.05d + rate);
        }
    }

    /// <summary>
    /// Tears <paramref name="torn"/> links out of the chain, and returns where the hole starts.
    ///
    /// Three ways, which is the set Ropke and Pisinger found carries most of the benefit: a random
    /// contiguous run, the links worth least, and the links nearest a randomly chosen one.
    /// </summary>
    /// <summary>Rebuild's scratch collections, one set per thread. See RebuildInner.</summary>
    [ThreadStatic] private static List<(int At, Vector2 Spot, double Worth)> _near;

    [ThreadStatic] private static HashSet<int> _held;

    [ThreadStatic] private static List<(int Spot, double Gain)> _ranked;

    [ThreadStatic] private static List<Vector2> _work;

    private static int Tear(PlanEnvironment env, Random random, List<Vector2> chain, int how,
        int torn)
    {
        using var phase = new Planner.Phase(Planner.PhaseTear);

        return TearInner(env, random, chain, how, torn);
    }

    /// <summary>The body of Tear, wrapped so its allocation is attributed. See Planner.Phases.</summary>
    private static int TearInner(PlanEnvironment env, Random random, List<Vector2> chain, int how,
        int torn)
    {
        if (chain.Count <= torn)
            return -1;

        switch (how)
        {
            case 0:
            {
                var at = random.Next(chain.Count - torn + 1);

                chain.RemoveRange(at, torn);

                return at;
            }

            case 1:
            {
                // What each link is worth where it stands: the chain with it, less the chain
                // without. The cheapest ones go.
                var whole = Planner.Score(env, chain);
                var worst = -1;
                var least = double.MaxValue;

                for (var i = 0; i < chain.Count; i++)
                {
                    var was = chain[i];

                    chain.RemoveAt(i);

                    var less = whole - Planner.Score(env, chain);

                    chain.Insert(i, was);

                    if (less < least)
                    {
                        least = less;
                        worst = i;
                    }
                }

                if (worst < 0)
                    return -1;

                var at = Math.Min(worst, chain.Count - torn);

                chain.RemoveRange(at, torn);

                return at;
            }

            default:
            {
                // Shaw's relatedness, in the only currency this problem has: where the links are.
                var pick = random.Next(chain.Count);
                var near = chain[pick];
                var order = new List<int>();

                for (var i = 0; i < chain.Count; i++)
                    order.Add(i);

                order.Sort((a, b) => Vector2.DistanceSquared(chain[a], near)
                    .CompareTo(Vector2.DistanceSquared(chain[b], near)));

                var least = chain.Count;

                for (var i = 0; i < torn && i < order.Count; i++)
                    least = Math.Min(least, order[i]);

                var at = Math.Max(0, Math.Min(least, chain.Count - torn));

                chain.RemoveRange(at, torn);

                return at;
            }
        }
    }

    /// <summary>
    /// Fills the hole back in, one link at a time, best first.
    ///
    /// Positions are limited to a window around the hole - see Shift - which is what keeps this
    /// affordable: a repair that tried every position in the chain would cost sixteen times as much
    /// for a move nobody asked for, since the rest of the chain is exactly what the destroy chose to
    /// keep.
    /// </summary>
    /// <summary>How many of the best-ranked spots are actually scored. See Rebuild.</summary>
    private const int Looked = 24;

    /// <summary>
    /// How many more are drawn at random from the rest.
    ///
    /// **Ranking by what a spot catches would never pick a bridge.** A cell that joins two clusters
    /// catches nothing by definition, so it sorts last and would never be scored - which is the same
    /// trap the shortlist fell into and the reason Spread exists there. These are the insurance: a
    /// handful of spots that the ranking says are worthless, tried anyway, because the ranking
    /// cannot see the one thing they are for.
    /// </summary>
    private const int Wild = 8;

    private static void Rebuild(PlanEnvironment env, List<Vector2> shortlist, Near reach,
        List<Vector2> chain, int at, int holes, Random random)
    {
        using var phase = new Planner.Phase(Planner.PhaseRebuild);

        RebuildInner(env, shortlist, reach, chain, at, holes, random);
    }

    /// <summary>The body of Rebuild, wrapped so its allocation is attributed. See Planner.Phases.</summary>
    private static void RebuildInner(PlanEnvironment env, List<Vector2> shortlist, Near reach,
        List<Vector2> chain, int at, int holes, Random random)
    {
        // **Reused per thread, cleared rather than rebuilt.** These are scratch: they are already
        // cleared at the top of every hole, they never escape this method, and neither Fold nor
        // WorthOfSplicedChain keeps a reference to what it is handed - checked, the latter adds to
        // the work list and winds it back to where it found it.
        //
        // They were the allocation. Rebuild measured 80KB a call with the scoring tally proven
        // reused 3,964 times out of 3,974, so Begin was never what it spent; what is left after
        // the nested reach queries is these four growing to the shortlist's size on every call and
        // being dropped. Clearing keeps the capacity they have already grown to.
        var near = _near ??= new List<(int At, Vector2 Spot, double Worth)>();
        var held = _held ??= new HashSet<int>();
        var ranked = _ranked ??= new List<(int Spot, double Gain)>();

        near.Clear();
        held.Clear();
        ranked.Clear();

        // One tally for every hole, wound back between them rather than allocated again. See the
        // loop below, and Planner.Spliced for what it is carrying.
        // Scratch: it dies with this call and nothing else on this thread holds one meanwhile.
        // See Planner.BeginScratch for what that buys and what it requires.
        var tally = Planner.BeginScratch(env, chain.Count + holes + 1);
        var work = _work ??= new List<Vector2>();

        work.Clear();

        for (var hole = 0; hole < holes; hole++)
        {
            var bestAt = -1;
            var bestSpot = Vector2.Zero;
            var most = double.NegativeInfinity;

            near.Clear();

            var first = Math.Max(0, at - Shift);
            var last = Math.Min(chain.Count, at + Shift);

            // **Rank on what a spot ADDS, then score only the few that could win.**
            //
            // This scored every spot in the shortlist at every slot - five hundred full chain
            // scorings at eight microseconds each, per hole, per round - and almost all of them were
            // spent on spots that catch nothing the chain does not already have. What a spot adds is
            // a lookup in the coverage index and a sum over the handful of markers it touches, which
            // is two orders of magnitude cheaper than scoring the chain to find out.
            //
            // Ranked on the marker's own worth plus what a remnant is worth where it stands, because
            // a remnant's reward lives in its combinations rather than its weight - the same measure
            // Remnants sorts on, and for the same reason.
            held.Clear();

            foreach (var link in chain)
            {
                foreach (var index in Planner.CaughtIndicesAt(env, link))
                    held.Add(index);
            }

            ranked.Clear();

            for (var s = 0; s < shortlist.Count; s++)
            {
                var spot = shortlist[s];

                if (chain.Contains(spot))
                    continue;

                var gain = 0d;

                foreach (var index in Planner.CaughtIndicesAt(env, spot))
                {
                    if (held.Contains(index))
                        continue;

                    var target = env.Targets[index];

                    gain += Planner.WorthOfTarget(target) + MathF.Max(0f, target.Rough(0f));
                }

                ranked.Add((s, gain));
            }

            ranked.Sort((a, b) => b.Gain.CompareTo(a.Gain));

            var looked = Math.Min(Looked, ranked.Count);

            // The wildcards, drawn from what the ranking dismissed. See Wild.
            for (var i = 0; i < Wild && looked < ranked.Count; i++)
            {
                var pick = looked + random.Next(ranked.Count - looked);

                (ranked[looked], ranked[pick]) = (ranked[pick], ranked[looked]);

                looked++;
            }

            // **Slot outside, candidate inside - which is the other way round from how this read.**
            //
            // Every trial here asks what the chain is worth with one link inserted, and everything
            // BEFORE the insertion point is the same question for every candidate tried at that
            // point. Walking the candidates outside meant the slot changed on every trial and no
            // prefix was ever worth keeping; walking the slots outside means the prefix is folded
            // once per slot, and since the slots run in increasing order it is walked forward rather
            // than rebuilt. See Planner.Spliced.
            //
            // The order the trials happen in changes, and nothing downstream cares: every one of
            // them lands in `near` and the winner is drawn from that by worth.
            work.Clear();
            Planner.Unfold(env, tally);

            for (var k = 0; k < first; k++)
            {
                work.Add(chain[k]);
                Planner.Fold(env, tally, work, chain[k]);
            }

            for (var slot = first; slot <= last; slot++)
            {
                for (var i = 0; i < looked; i++)
                {
                    var index = ranked[i].Spot;
                    var spot = shortlist[index];

                    // The reach half of Fits, from the table. See Near.
                    if (!reach.Reaches(slot == 0 ? env.Origin : chain[slot - 1], index))
                        continue;

                    if (slot < chain.Count && !reach.Reaches(chain[slot], index))
                        continue;

                    if (!Planner.Spaced(env, chain, spot))
                        continue;

                    // Inserting at slot means the chain is the prefix, then this spot, then
                    // everything from slot on - so the tail starts where the insertion does.
                    var worth = Planner.WorthOfSplicedChain(env, tally, work, spot, chain, slot);

                    if (worth > most)
                    {
                        most = worth;
                        bestAt = slot;
                        bestSpot = spot;
                    }

                    near.Add((slot, spot, worth));
                }

                // On to the next insertion point, which puts this link behind it.
                if (slot < chain.Count)
                {
                    work.Add(chain[slot]);
                    Planner.Fold(env, tally, work, chain[slot]);
                }
            }

            if (bestAt < 0)
                return;

            // Everything a whisker behind the winner is a fair choice; the rest goes.
            var floor = most > 0d ? most * Nearly : most / Nearly;

            for (var i = near.Count - 1; i >= 0; i--)
            {
                if (near[i].Worth < floor)
                    near.RemoveAt(i);
            }

            // Among the ones that came within a whisker of the best, at random. See Nearly.
            if (near.Count > 1)
            {
                var pick = near[random.Next(near.Count)];

                bestAt = pick.At;
                bestSpot = pick.Spot;
            }

            chain.Insert(bestAt, bestSpot);
        }
    }

    /// <summary>
    /// Cuts the chain short and rebuilds the tail towards a piece of content it is missing.
    ///
    /// **Every other operator here is greedy, and a detour's first step is a loss.** Tear six links
    /// out and the repair fills them one at a time by whatever scores best right now - so it rebuilds
    /// roughly what it removed, because the first link of a route to somewhere else catches nothing
    /// and loses to a link that sits back down where the old one was. Measured on a Grand site: six
    /// and a half thousand rounds, one new best, and eight workers spread across a thousand points
    /// and all stuck. The neighbourhood was not too small - the tears reached six links and the
    /// escalation was visibly working - it was greedy in the one direction that mattered.
    ///
    /// So this one is not greedy. It picks something rich the chain does not have, cuts at a random
    /// point, and BRIDGES to it - laying links across ground that catches nothing, which is exactly
    /// the move a best-insert repair will never make - then fills whatever is left greedily. It is
    /// the same operator that gets a must-take on the far side of a dig site, aimed at whatever the
    /// chain happens to be missing rather than at what somebody marked.
    ///
    /// ExpeditionIcons calls its version reach mutation and says in as many words that it is what
    /// gets the third runestone, because mutation "can never walk a path off one runestone and onto
    /// another - every intermediate step scores below staying put".
    /// </summary>
    private static List<Vector2> Reaching(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> shortlist, List<Vector2> chain, Random random, ref int asked, ref int arrived)
    {
        if (chain.Count < 2)
            return null;

        asked++;

        // What the chain already has, so the target is something it is genuinely missing.
        var held = new HashSet<int>();


        foreach (var link in chain)
        {
            foreach (var index in Planner.CaughtIndicesAt(env, link))
                held.Add(index);
        }

        // Where to cut. Early gives the bridge room and throws away more of what the chain earned;
        // late is the reverse. Selection judges that better than any rule here would.
        var keep = 1 + random.Next(chain.Count - 1);
        var work = new List<Vector2>(chain.GetRange(0, keep));
        var left = env.Explosives - keep;

        // **Half the tail to travel, half to catch things with.**
        //
        // The bridge is bounded only by the explosive budget, so a target on the far side of the dig
        // will happily eat every remaining link getting there and arrive with nothing left to do.
        // That chain scores badly and is rejected, so it breaks nothing - it wastes the round, and
        // this operator is the most expensive of the four because it finishes with a full greedy
        // construction. Half is arbitrary and is the point at which a detour stops paying more often
        // than it pays.
        // **Half the tail, or a few links, whichever is more - because half of a short tail is
        // nothing.**
        //
        // The half rule was tuned on a Grand site, where half of fourteen remaining links is seven and
        // a bridge can go anywhere. On an ordinary expedition of six explosives it hands out ONE or
        // TWO, and the operator that exists to reach content the chain is missing cannot reach
        // anything. Measured on a five link site: 3,432 bridges of 7,410 ran out of explosives after
        // 1.5 hops, NOUGHT were walled off by the ground, and reach won nothing at all across two
        // presses - while the thing it was failing to reach was the furthest remnant on the site,
        // worth 1,270 to any chain that caught it.
        //
        // The floor is a link count rather than a fraction for the same reason the ceiling is a
        // fraction: a bridge needs a minimum number of steps to be a bridge at all, and that minimum
        // does not shrink because the chain is short. Bounded by what is actually left, so it can
        // never promise links the chain does not have. See SolverSettings.BridgeLinks.
        var spend = Math.Max(Math.Min(left, env.BridgeLinks), left / 2);

        // **Charged at the straight line, which is a lower bound on the journey.** Dividing the reach by the
        // learnt detour ratio was tried as a setting and removed: one site-wide average refuses reaches that
        // would have worked. The ratio is still learnt and reported. See Detour.
        var span = (float)(spend * env.Reach) + env.Blast;
        var rich = new List<(int Index, double Worth)>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (held.Contains(i))
                continue;

            var target = env.Targets[i];

            if (target.Shunned)
                continue;

            // Only what the links in hand could actually get to from the cut. The same test Stone
            // makes before setting off towards something, and for the same reason.
            if (Vector2.Distance(work[^1], target.Grid) > span)
                continue;

            var worth = Planner.WorthOfTarget(target) + MathF.Max(0f, target.Rough(0f));

            if (worth > 0d)
                rich.Add((i, worth));
        }

        if (rich.Count == 0)
            return null;

        rich.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        // One of the best few at random rather than the single best, so repeated rounds try
        // different places rather than the same place over and over.
        //
        // **It is NOT worth pre-testing that the shortlist covers the target.** That was tried, on
        // the reading of the note below that a fall-off in arrivals means no shortlist spot sits on
        // the marker. Measured over a full window: nought targets rejected out of 1,087, and
        // arrivals unchanged at 45%. Every marker reach picks is already covered; whatever defeats
        // Fetch happens in the bridging between here and there, not at the far end.
        var want = rich[random.Next(Math.Min(Aimed, rich.Count))].Index;

        var taken = new HashSet<int>();
        var from = work[^1];

        // The shortlist here too, for the same reason the fill below uses it: Fetch scans every
        // candidate it is given at every hop, and there are several hops. Watch "reach arrived N of
        // M" - if arrivals fall off, the shortlist has no spot sitting on the target and the full
        // set has to come back.
        // What the straight line said this would take, kept before the bridge moves the end of the
        // chain and the question becomes unanswerable. See Detour.
        var guessed = Linked(env, Vector2.Distance(work[^1], env.Targets[want].Grid));

        if (!Planner.Fetch(env, shortlist, work, taken, ref from, want, keep + spend,
                Planner.Bridging))
            return null;

        arrived++;

        // Arrived, so the journey is now a known length and the site can be asked what its detours
        // cost. Only arrivals: a bridge that ran out never learnt how far it was, and counting one
        // would teach "further than the budget" - the very thing being measured - until nothing was
        // admitted at all.
        System.Threading.Interlocked.Add(ref _detourWalked, Math.Max(1, work.Count - keep));
        System.Threading.Interlocked.Add(ref _detourGuessed, guessed);

        // **Filled from the shortlist, not the whole site.**
        //
        // This finished with a full greedy construction over every candidate, and the timing said
        // what that cost: eight and a third seconds of a window against a hundred and nine
        // milliseconds for all the tearing put together, for eighty eight calls. Ninety four
        // milliseconds each, almost all of it here.
        //
        // The shortlist is five hundred spots of four thousand, chosen as the richest plus a spread
        // to keep the site connected - which is the same set the repair builds from and is more than
        // enough to fill a tail. It buys roughly eight times as many reaches, and reaches are what
        // found the record.
        //
        // **And filled greedily, which is deliberate and was measured.**
        //
        // The handful-at-random every other construction here uses was tried, on the reading that
        // this was the last plain greedy repair in the plugin and plain greedy has no second answer.
        // It measured worse - 4,282.5 against 3,066.5 on the same deterministic worker - but that
        // pair straddled a plugin reload that quietly unfroze the prices, so most of the gap is not
        // attributable. Off by default and unresolved.
        //
        // The reading behind it was wrong regardless. Where it cuts and what it aims at are BOTH drawn afresh every
        // attempt, and the chain being cut differs too - so this operator already explores as widely
        // as anything here. The fill is the one part doing the opposite job, making each of those
        // varied bridges as good as it can be, and randomising that bought nothing and spent the
        // exploitation. Noise belongs in a repair that is otherwise deterministic. See
        // The switch that offered the other behaviour is gone: its argument was refuted here, its
        // one measurement was spoiled by a reload, and it sat off. Always the best fill now.
        return Planner.Greedy(env, shortlist, null, 1, null, work);
    }

    /// <summary>How many of the richest missed targets are drawn between. See Reaching.</summary>
    private const int Aimed = 8;

    /// <summary>
    /// How much further a bridge actually walks than the straight line said it would.
    ///
    /// **Two bridges in five are aimed at somewhere the chain cannot get to, and that is knowable
    /// before a single hop is spent.** Reaching admits a target when the straight line to it fits
    /// inside the links it has - and then the router walks a route, which is never shorter than the
    /// line and is usually a good deal longer. Measured over one press: 4,494 arrived, 535 genuinely
    /// walled off, and 3,580 simply out of explosives after 3.2 hops. Only the 535 are the ground
    /// refusing; the 3,580 are the admission test being optimistic, and each one paid nearly a full
    /// attempt at about 8.9ms to find out.
    ///
    /// So the site is asked what its own detours cost. Every bridge that ARRIVES contributes the
    /// links it really used against the links the straight line predicted, and the running ratio
    /// tightens the test for everything asked afterwards. Learnt rather than written down because it
    /// is a property of the dig site: open ground is near enough 1, and a site of corridors is
    /// nearer 2, and no constant is right for both.
    ///
    /// **Only arrivals teach it**, which is deliberate. A bridge that ran out of links never found
    /// out how far the journey was, so counting it would say "further than the budget", which is the
    /// thing being measured, and the ratio would climb until nothing was admitted at all.
    /// </summary>
    private static long _detourWalked;

    private static long _detourGuessed;

    /// <summary>
    /// The multiplier to charge a straight line by, or 1 before anything has arrived.
    ///
    /// Clamped, because this multiplies a budget and both ends of getting it wrong are bad: below 1
    /// it would admit MORE than the straight line, which no route can justify, and far above 2 it
    /// would refuse most of the site on the evidence of a handful of awkward bridges. Within those
    /// bounds a wrong answer costs a few wasted attempts, which is what the whole thing is for.
    /// </summary>
    private static double Detour =>
        _detourGuessed <= 0
            ? 1d
            : Math.Clamp((double)_detourWalked / _detourGuessed, 1d, 2.5d);

    /// <summary>What the detour has learnt, for the dump. See Detour.</summary>
    public static string Detoured =>
        _detourGuessed <= 0
            ? "no bridge has arrived yet"
            : $"{Detour:0.00}x the straight line, over {_detourGuessed:N0} links predicted " +
              $"and {_detourWalked:N0} walked";

    /// <summary>Forgets it, so a solve is judged on its own site. See Detour.</summary>
    public static void Undetour()
    {
        System.Threading.Interlocked.Exchange(ref _detourWalked, 0);
        System.Threading.Interlocked.Exchange(ref _detourGuessed, 0);
    }

    /// <summary>
    /// How many links a straight line to <paramref name="span"/> away would need.
    ///
    /// The inverse of the span the admission test builds: a bridge crosses Reach per link and the
    /// blast covers the last stretch, so the line needs (distance - blast) / reach of them, and
    /// never fewer than one.
    /// </summary>
    private static int Linked(PlanEnvironment env, float span) =>
        Math.Max(1, (int)MathF.Ceiling((span - env.Blast) / MathF.Max(1f, env.Reach)));

    /// <summary>
    /// Tears and rebuilds <paramref name="times"/> stretches at random, without caring whether the
    /// result is better - a kick rather than a move.
    ///
    /// **Independent workers starting from an identical chain are one worker.** Four of them opened
    /// at the same deterministic band chain and every one returned it to the decimal: 9,402.8 four
    /// times over. Nothing separated them, because the opening was shared and the local search that
    /// follows it is deterministic too. The seeds reached the random stream and the random stream had
    /// nothing left to decide.
    ///
    /// So the opening is shaken by the worker's own number of kicks before it is polished. Same good
    /// starting region, different point inside it, different basin found - which is what multi-start
    /// is supposed to buy and was not.
    /// </summary>
    /// <param name="tally">
    /// Optional, four counters: how many shakes stood, how many were put back because the rebuild
    /// left the chain short, how many because it left it illegal, and how many found no run to tear.
    ///
    /// **Here because a shake that reverts is indistinguishable from one that never ran.** Every
    /// iteration below puts the chain back if the rebuild does not produce a legal chain of the same
    /// length, so a site where the rebuild cannot fill the hole returns the input untouched - and the
    /// pool then explores one point with eight threads, which is the symptom this was added under.
    /// Measured on a 15-explosive site with a must-take: seven of eight workers BUILT the identical
    /// score with the shake cap at 2, at 12, and with the opening's descent removed, so the shake is
    /// the remaining suspect and nothing counted it.
    /// </param>
    internal static List<Vector2> Shaken(PlanEnvironment env, List<Vector2> shortlist,
        List<Vector2> chain, Random random, int times, int[] tally = null)
    {
        var reach = new Near(env, shortlist);
        var work = new List<Vector2>(chain);

        for (var i = 0; i < times; i++)
        {
            var was = new List<Vector2>(work);

            // **A requirement is NOT protected from the tear, and two attempts at protecting it
            // both made things worse.** Refusing a tear that drops one reverted nearly every
            // shake, because the tear operators go for the least valuable run and a bridging arm
            // out to a marked marker is exactly that - so the links the perturbation most needs to
            // move are the ones a guard would pin. Rejecting cost the whole shake; retrying the
            // position four times still failed on every worker, since most positions break an arm.
            //
            // Measured, in order: 106,399 identical on all eight with the guard, 111,998 identical
            // with the guard and retries, against 21,809 plain with neither. So the tear runs, and
            // MustTakeTour afterwards fetches back whatever it took - repair rather than
            // prevention, which is what the ALNS reading said in the first place.
            // **A kick has to be bigger than the local search can put back.** One to four links
            // out of fifteen, rebuilt from a shortlist, is inside Improve's basin - it sweeps
            // every one of nearly four thousand candidates against every link, so it simply
            // polished the perturbation away and all four workers came back at 10,552.3 to the
            // decimal, twice running. Iterated local search says the perturbation must be
            // strong enough that the descent cannot retrace it; a third of the chain is the
            // usual rule of thumb.
            var biggest = Math.Min(Math.Max(Math.Max(3, env.TearMost), work.Count / 3),
                Math.Max(1, work.Count - 1));

            // **Tried smaller until one stands, because a third of the chain does not always go
            // back.** A tear of that size asks the rebuild to refill five consecutive links, and on a
            // site whose links already run at 96 to 108 of a 108 budget there is usually no set of
            // five that reaches. The rebuild then returns a short chain, the shake reverts, and the
            // worker opens on the chain it started with - which is how eight threads came to explore
            // one point.
            //
            // Measured on a 15-explosive site at (1037,567): every shake on every worker reverted -
            // 0 stood against 2, 3, 4, 5, 6 and 7 attempts - so the perturbation the pool's diversity
            // rests on had never once happened there. The counters that say so are the tally above.
            //
            // A smaller perturbation that stands beats a larger one that is undone, and the reason for
            // preferring the large one has weakened: Opening no longer descends on an exploring
            // worker's chain, so there is no longer a sweep of four thousand candidates waiting to
            // retrace a gentle kick. The size still starts at a third and only comes down when the
            // ground refuses it.
            for (var torn = biggest; torn >= 1; torn--)
            {
                var at = Tear(env, random, work, random.Next(3), torn);

                if (at < 0)
                {
                    work.Clear();
                    work.AddRange(was);

                    if (tally is { Length: > 3 })
                        tally[3]++;

                    continue;
                }

                Rebuild(env, shortlist, reach, work, at, torn, random);

                // A kick that leaves the chain short is one the rebuild could not fill.
                if (work.Count < was.Count)
                {
                    work.Clear();
                    work.AddRange(was);

                    if (tally is { Length: > 1 })
                        tally[1]++;

                    continue;
                }

                // A kick that leaves the chain illegal is no kick at all.
                if (!Sound(env, work))
                {
                    work.Clear();
                    work.AddRange(was);

                    if (tally is { Length: > 2 })
                        tally[2]++;

                    continue;
                }

                if (tally is { Length: > 0 })
                    tally[0]++;

                break;
            }
        }

        return work;
    }

    /// <summary>
    /// Which of the shortlist's spots can be thrown from a given point, worked out once per point.
    ///
    /// **The same question, asked from the same place, six hundred times over.** A repair fixes the
    /// link before a hole and then asks every spot in the shortlist whether it can be reached from
    /// it - and asks again at the next slot, the next hole, and the next round, because the chain
    /// barely moves between them. Each of those is a distance test and a dictionary lookup in the
    /// router. Held as one bit per spot, it is a shift and a mask.
    ///
    /// **Doubt is kept apart from denial, which is what makes it safe to cache at all.** The router
    /// answers Yes, No, or "not looked yet", and the third of those turns into Yes as the ground is
    /// learnt - so writing it down as No would freeze a cold site's ignorance for the rest of the
    /// solve. That is exactly the mistake Route.Ask refuses to make with its own pair cache. So the
    /// unsettled ones get a second bit and are asked again each time, and promoted the moment the
    /// router has an answer.
    ///
    /// One of these per search, so nothing survives a solve and there is no staleness to reason
    /// about beyond the one case above.
    /// </summary>
    private sealed class Near
    {
        private readonly PlanEnvironment _env;
        private readonly List<Vector2> _spots;
        private readonly Dictionary<long, (ulong[] Yes, ulong[] Doubt)> _from = new();

        public Near(PlanEnvironment env, List<Vector2> spots)
        {
            _env = env;
            _spots = spots;
        }

        /// <summary>Whether <paramref name="index"/> of the shortlist can be thrown from here.</summary>
        public bool Reaches(Vector2 at, int index)
        {
            var (yes, doubt) = Of(at);
            var word = index >> 6;
            var bit = 1UL << (index & 63);

            if ((yes[word] & bit) != 0)
                return true;

            if ((doubt[word] & bit) == 0)
                return false;

            // Unsettled when the table was built. Ask again, and keep the answer if there is one now.
            if (Planner.Says(_env, at, _spots[index]) is not Certainty.Unknown)
            {
                doubt[word] &= ~bit;

                if (Planner.Reaches(_env, at, _spots[index]))
                    yes[word] |= bit;
            }

            return Planner.Reaches(_env, at, _spots[index]);
        }

        private (ulong[] Yes, ulong[] Doubt) Of(Vector2 at)
        {
            var key = Planner.Key(at);

            if (_from.TryGetValue(key, out var already))
                return already;

            var words = (_spots.Count + 63) / 64;
            var made = (new ulong[words], new ulong[words]);

            for (var i = 0; i < _spots.Count; i++)
            {
                var said = Planner.Says(_env, at, _spots[i]);

                if (said == Certainty.Yes)
                    made.Item1[i >> 6] |= 1UL << (i & 63);
                else if (said == Certainty.Unknown)
                    made.Item2[i >> 6] |= 1UL << (i & 63);
            }

            _from[key] = made;

            return made;
        }
    }

    /// <summary>Whether <paramref name="spot"/> may sit at <paramref name="slot"/>.</summary>
    internal static bool Fits(PlanEnvironment env, List<Vector2> chain, int slot, Vector2 spot)
    {
        var before = slot == 0 ? env.Origin : chain[slot - 1];

        if (!Planner.Reaches(env, before, spot))
            return false;

        if (slot < chain.Count && !Planner.Reaches(env, spot, chain[slot]))
            return false;

        return Planner.Spaced(env, chain, spot);
    }

    /// <summary>
    /// Whether a whole chain is legal: every link thrown from the one before, and none on top of
    /// another.
    ///
    /// Checked after a repair rather than trusted, because a destroy leaves the chain briefly
    /// illegal by construction - the link before the hole and the link after it are not neighbours
    /// in the game's sense - and a repair that cannot fill every hole leaves it that way.
    /// </summary>
    /// <summary>
    /// Reverses a run of links and keeps it if it scores better, until nothing does.
    ///
    /// **The chain's ORDER is worth thousands and nothing in the search could change it.** Measured on
    /// one press: the winning chain and the median worker's had nought links in common, the median
    /// collected 230 points MORE content across 62 markers the winner missed, and scored 2,438 LESS.
    /// The score is content plus propagation and propagation is order-dependent - a rune pays over the
    /// monsters unearthed AFTER the remnant carrying it - so a chain can take everything and arrange it
    /// so that nothing multiplies. That is what the stuck workers are doing.
    ///
    /// **Exhaustive ordering cannot help on a Grand site.** Permuted returns the chain untouched unless
    /// the unplaced part is at or below SolverSettings.PermuteUpTo links, and a Grand chain is fifteen,
    /// whose orderings number 1.3 trillion. So the operator that addresses ordering has never once run
    /// where ordering is worth the most.
    ///
    /// **Reversal is the cheap half of it.** This is 2-opt, the standard move for a routing problem:
    /// for a fifteen link chain there are 105 runs to reverse, each one a legality walk and a score
    /// against a measured 42us. A hundred of those is four milliseconds, against a factorial.
    ///
    /// Repeated until a pass finds nothing, which is an ordinary 2-opt local search rather than a single
    /// sweep - the first reversal usually opens a second.
    ///
    /// **Every link is re-checked rather than only the two joins.** A reversal leaves the run's internal
    /// distances alone, so on a symmetric model only the boundaries could break - and this model is not
    /// symmetric: the clamp is directional, Lands(a,b) and Lands(b,a) are different questions, and a
    /// reversed run travels its own links the other way. See Wire.Lands.
    /// </summary>
    /// <summary>
    /// Walks from the pool's best chain towards another worker's, one link at a time, and keeps the best
    /// chain seen on the way.
    ///
    /// **Eight workers finish, one answer is kept, and seven are thrown away untouched.** Measured on
    /// this site: the winner and the median worker had NOUGHT links in common out of fifteen, and the
    /// median collected 230 points more content while scoring 2,438 less. That is not a weak search, it
    /// is a different one - and two structurally different good-ish chains are exactly the raw material
    /// this move needs.
    ///
    /// **Path relinking (Glover), which with ALNS is one of the two families that lead on the team
    /// orienteering problem.** The plugin already had the other one, and the GRASP half of this one - the
    /// randomised greedy openings - and none of the relinking. The idea is simple: take the initiating
    /// solution, repeatedly adopt one link from the guiding solution, score every step, and keep the best
    /// point on the path. The interesting chains are in the middle: they are not reachable from either
    /// end by any single-link move, which is the same argument destroy-and-repair rests on.
    ///
    /// **Spliced by the run rather than link by link, and the first version proved why.** Walking one
    /// link at a time offered three legal candidates across three pairs - because a link taken from a
    /// chain with nothing in common lands about a hundred grid from where its neighbours can reach, and
    /// the reach test refuses it. A run keeps the guide's own consecutive links, so only the two joins
    /// can break.
    ///
    /// Cost is every window of the chain: 105 on fifteen links, a legality walk each and a score for the
    /// ones that pass, at a measured 42us. Three guides is a few tens of milliseconds once per press,
    /// after every worker has finished, so it costs the search nothing.
    /// </summary>
    /// <returns>The best chain found on the paths, or null when none beat what it was given.</returns>
    internal static List<Vector2> Relinked(PlanEnvironment env, List<List<Vector2>> chains,
        out string said)
    {
        said = "off";

        if (!env.Relink)
            return null;

        said = "nothing to relink";

        if (env?.Targets == null || chains is not { Count: > 1 })
            return null;

        // Ranked by what the site pays, so the initiator is the pool's answer and the guides are the
        // strongest of the rest. A guide identical to the initiator has nothing to offer and is skipped
        // by the difference walk itself, at the cost of one comparison.
        var ranked = new List<(double Worth, List<Vector2> Chain)>();

        foreach (var chain in chains)
        {
            if (chain is { Count: > 1 })
                ranked.Add((Planner.Score(env, chain), chain));
        }

        if (ranked.Count < 2)
            return null;

        ranked.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        var initiator = ranked[0].Chain;
        var top = ranked[0].Worth;
        var was = top;
        var found = (List<Vector2>)null;
        var walked = 0;
        var pairs = 0;

        for (var g = 1; g < ranked.Count && pairs < Guides; g++)
        {
            var guide = ranked[g].Chain;

            if (guide.Count != initiator.Count)
                continue;

            pairs++;

            var work = new List<Vector2>(initiator);

            // **A run at a time, because one link at a time is illegal almost always.** Adopting a single
            // link from a chain with nothing in common puts it about a hundred grid from where its
            // neighbours can reach, and the reach test refuses it: measured, three pairs offered THREE
            // legal candidates between them and the walk found nothing. A contiguous run carries the
            // guide's own consecutive links, which are legal among themselves, so only the two joins can
            // fail - and those are two tests rather than fifteen.
            //
            // Every window, which is 105 of them on a fifteen link chain. Each is a legality walk and,
            // when it passes, one score.
            for (var i = 0; i < initiator.Count; i++)
            for (var j = i; j < initiator.Count; j++)
            {
                work.Clear();
                work.AddRange(initiator);

                var differs = false;

                for (var k = i; k <= j; k++)
                {
                    work[k] = guide[k];

                    if (Vector2.Distance(initiator[k], guide[k]) >= 1f)
                        differs = true;
                }

                // The same chain, so there is nothing to score. Cheaper to notice than to score.
                if (!differs)
                    continue;

                if (!Sound(env, work))
                    continue;

                var legal = true;

                for (var k = i; k <= j && legal; k++)
                    legal = env.CanPlace(work[k]);

                if (!legal)
                    continue;

                walked++;

                var worth = Planner.Score(env, work);

                if (worth <= top)
                    continue;

                top = worth;
                found = new List<Vector2>(work);
            }
        }

        said = found == null
            ? $"{pairs} pair(s), {walked:N0} chain(s) walked, nothing beat the pool's {was:N0}"
            : $"{pairs} pair(s), {walked:N0} chain(s) walked, found {top:N0} against the pool's " +
              $"{was:N0} - better by {top - was:N0}";

        return found;
    }

    /// <summary>
    /// How many other workers the best chain is relinked with.
    ///
    /// Three, because the cost is a triangle per pair and the guides are ranked - the fourth best chain
    /// is normally a near copy of one already used, so the pairs after the first few buy repetition. A
    /// bound rather than a budget.
    /// </summary>
    private const int Guides = 3;

    internal static List<Vector2> Reversed(PlanEnvironment env, List<Vector2> chain, int laid)
    {
        if (chain is not { Count: > 3 } || !env.ReverseRuns)
            return chain;

        var head = Math.Max(0, Math.Min(laid, chain.Count));

        if (chain.Count - head < 3)
            return chain;

        var best = new List<Vector2>(chain);
        var top = Planner.Score(env, best);
        var work = new List<Vector2>(best);

        for (var pass = 0; pass < Reversals; pass++)
        {
            var moved = false;

            for (var i = head; i < best.Count - 1; i++)
            for (var j = i + 1; j < best.Count; j++)
            {
                work.Clear();
                work.AddRange(best);
                work.Reverse(i, j - i + 1);

                if (!Sound(env, work))
                    continue;

                var worth = Planner.Score(env, work);

                if (worth <= top)
                    continue;

                top = worth;
                best = new List<Vector2>(work);
                moved = true;
            }

            if (!moved)
                break;
        }

        return best;
    }

    /// <summary>
    /// How many sweeps of reversals a chain gets before it is left alone. See Reversed.
    ///
    /// Four, because each sweep is 105 reversals on a fifteen link chain and the answer is normally
    /// found in the first two - a bound rather than a budget, so a pathological chain cannot spend a
    /// worker's window here.
    /// </summary>
    private const int Reversals = 4;

    /// <summary>
    /// How far into its window a worker's score is noted, in milliseconds. See the halfway reading.
    ///
    /// Four seconds, against presses of 8.2 to 8.7 on the site this was written for. A fixed figure rather
    /// than a share of the budget because a worker is handed a predicate and never told its deadline.
    /// </summary>
    private const int Partway = 4000;

    internal static bool Sound(PlanEnvironment env, List<Vector2> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var before = i == 0 ? env.Origin : chain[i - 1];

            if (!Planner.Reaches(env, before, chain[i]))
                return false;

            if (!Planner.Spaced(env, chain, chain[i], i))
                return false;
        }

        return true;
    }
}
