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
    /// The climb is timed off the stagnation kick: the ceiling reaches its maximum just as the kick
    /// would fire, so the two are one escalation rather than two unrelated ones.
    /// </summary>
    private static int Torn(PlanEnvironment env, Random random, int since, int links)
    {
        var least = Math.Max(1, env.TearLeast);
        var most = Math.Max(least, env.TearMost);
        var steps = most - least + 1;
        var climb = steps > 1 ? Math.Max(1, Stale / steps) : int.MaxValue;
        var ceiling = Math.Min(most, least + since / climb);

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
    /// How many rounds without a new best before the search starts again somewhere else.
    ///
    /// **Five thousand rounds and nought new bests is not a search, it is a walk.** The acceptance
    /// rule lets the working chain drift within a couple of per cent of the record, which is what
    /// stops it sitting on one answer - and on a chain that is already locally optimal under a much
    /// stronger local search than any tear, it drifts for the rest of the window and never climbs
    /// back. Restarting on stagnation is what every iterated local search does about that, and the
    /// record is kept across restarts so nothing is lost by trying.
    /// </summary>
    private const int Stale = 500;

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
        // Off, every worker takes worker nought's seeding - the configured one - and they all come
        // out with the same opening, which is what this did before. See VaryOpenings.
        var seeding = Asking(env, env.VaryOpenings ? stream : 0);

        if (seeding == null)
            return null;

        var key = (env, Varied(env.VaryOpenings ? stream : 0));

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
        var plan = Edges.Search(asked, BandTime, TimeSpan.Zero, token, null);
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
        var random = new Random(20260916 + env.Targets.Count + stream * 7919);

        var deadline = DateTime.UtcNow + budget;
        var improved = DateTime.UtcNow;

        // Whether the opening has produced a chain yet, which is what Waiting reads to know whether
        // the window has anything to run against. See Waiting.
        var constructed = false;

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

            return DateTime.UtcNow < deadline && !token.IsCancellationRequested &&
                   (settle <= TimeSpan.Zero || DateTime.UtcNow - improved < settle);
        }

        // **Where the window went, because inferring it has been wrong three times tonight.**
        // Rounds fell from 6,546 to 10 between two runs and the counters said nothing about which
        // part had eaten the time - the opening, the tears, the reach operator or the polish. One
        // stopwatch each and the next dump answers it by being read rather than reasoned about.
        var asked = 0;
        var arrived = 0;

        // **One counter called "opening" was hiding three different things.**
        //
        // It held the first construction, the stagnation kick's rebuild, and - through Banded's lock
        // - however long this worker sat waiting for another one to finish the band search. Read as
        // a single number it says "setup is expensive", which is true and useless: the setup happens
        // once, the kick happens every time the search gets stuck, and the wait is pure idling that
        // no amount of tuning the opening would remove. Measured at 4,257ms on a fifteen link site,
        // against 1,126ms of actual tearing, with no way to tell which third to attack.
        var bandMs = 0d;
        var openingMs = 0d;
        var kickMs = 0d;
        var reachMs = 0d;
        var tearMs = 0d;
        var polishMs = 0d;
        var watch = new System.Diagnostics.Stopwatch();
        var band = new System.Diagnostics.Stopwatch();

        // Something worth tearing at, and not the same thing every worker is tearing at. See Starts.
        watch.Restart();

        // **The opening's own clock, on top of the window's.** It otherwise runs until it finishes,
        // which on a Grand site is four to eight seconds of an eight second window. Nought leaves
        // that alone. The band search is not interrupted by it - that pass takes a token rather than
        // a predicate - so a cap shorter than the band's cost lands immediately after it instead.
        var opens = env.OpeningMs <= 0
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
            best = Opening(env, candidates, shortlist, random, Early, token, stream, seed, band);

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
        var told = env.VaryOperators ? Told(env.TearingMix, stream) : (At: -1, Share: 0d);

        var lean = told.At < 0
            ? "even"
            : told.Share == 70d
                ? Destroys[told.At]
                : $"{Destroys[told.At]}:{told.Share:0}";

        openingMs += watch.Elapsed.TotalMilliseconds;
        bandMs = band.Elapsed.TotalMilliseconds;
        // **The opening, in its best order.** Ordering is solved exactly below this link count, so
        // there is no reason to begin the tearing from a worse arrangement of the same spots - and the
        // opening is what decides which region the whole window explores. See Permuted.
        // **The opening as CONSTRUCTED, before anything polishes it.** Eight workers finishing on
        // one number says nothing about which half failed: the shakes may be producing one chain,
        // or eight different ones that the polish walks onto the same local optimum. Those want
        // opposite fixes - more shake against a narrower descent - and the figure printed beside
        // the finish was already past the polish, so it could not tell them apart.
        //
        // Measured on Craggy Peninsula: all eight opened AND finished at 105,184.4, 0.0%, with
        // 28,000 rounds between them and not one best. See SolverSettings.OpeningShakes, whose own
        // note predicted exactly this at a cap of two.
        var raw = best is { Count: > 0 } ? Planner.Score(env, best) : 0d;

        best = Slid(env, shortlist, Permuted(env, best, env.Placed?.Count ?? 0),
            env.Placed?.Count ?? 0);

        // **A requirement the opening missed, fetched before the loop starts.**
        //
        // Nothing in this file can reach a must-take that sits more than one link off the route:
        // every operator here moves a link and keeps the result only if it improves, and the ground
        // between is worse than where the chain stands. The charge for dropping one ranks the
        // answer afterwards; it does not build it. See Planner.MustTakeTour.
        if (env.Musts > 0 &&
            Planner.MustTakeTour(env, shortlist, best, stream) is { Count: > 0 } demanded)
        {
            // **Not shaken afterwards.** The shake happens inside Opening, before this, and the
            // tour is what puts back what the shake tore off - so shaking the tour undoes the
            // fetch and leaves the requirement dropped with nothing left to restore it. Diversity
            // comes from the shaken chain this tour was computed against, and from the tour
            // chosen per worker.
            best = Planner.Improve(env, shortlist, demanded, Waiting);
        }

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
        // the "adaptive" in ALNS - a destroy that suits this site earns more turns - and started
        // either equal or leaning, depending on whether this worker is a specialist. See Leaning.
        var weight = Leaning(env, stream);
        var used = new int[4];
        var won = new int[4];
        var paid = new int[4];

        // What each destroy has cost, so the weights can be about value per millisecond rather than
        // value per attempt. See Reweigh, which records why per-millisecond was considered.
        var spent = new double[4];

        var rounds = 0;
        var kept = 0;
        var bettered = 0;
        var restarts = 0;
        var migrated = 0;
        var rescued = 0;

        // Read once: it is a setting and the loop below asks about it thousands of times a second.
        var slack = Slacking(env);
        var since = 0;

        var work = new List<Vector2>();

        // How many tears of each size were taken, so the escalation can be seen working rather than
        // assumed. A run whose sizes are all ones is a run that never got stuck; one with a tail out
        // to the maximum is the ceiling climbing as it should.
        var sized = new int[16];



        while (Waiting())
        {
            rounds++;

            // Stuck. Kick the record hard and carry on from there. See Stale.
            //
            // **The record, not a fresh construction.** Rebuilding the opening cost ten randomised
            // greedy builds and a full local search every time, which is most of a second out of a
            // window that was producing two thousand rounds - and it threw away the good chain to
            // start again from a worse one. Iterated local search does the opposite: it perturbs the
            // best it has, strongly, and descends again. Same escape, none of the cost, and the
            // ground it explores is around an answer already worth having.
            if (++since > Stale)
            {
                since = 0;
                restarts++;

                watch.Restart();

                // **Shaken from the POOL's best, not this worker's, when the pool is well ahead.**
                //
                // The kick is about to throw this chain away and descend from a perturbed copy of
                // it, so which chain gets perturbed is free to choose - and a worker stuck at 3,422
                // perturbing its own is spending the rest of the window exploring around an answer
                // that is not going to be the one kept. See Solving.Adopt.
                //
                // **One worker in four never asks**, which is what stops this becoming a single
                // search on eight threads. Those keep hunting their own ground, so the diversity
                // that produced the good chain in the first place is still there to produce another
                // - and if one of them finds something better, it is published and the other six
                // move to it at their next kick. A margin on the other side means nobody moves for
                // a difference that is inside the noise.
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
                if (env.RescueBelow > 0d && Solving.Behind(top, env.RescueBelow))
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

                    chain = Planner.Improve(env, candidates, afresh, Waiting);

                    score = Planner.Score(env, chain);
                    rescued++;
                    Solving.Rescued();

                    // The record stands. It is a poor one, but it is this worker's answer until the
                    // new start beats it, and returning something worse than what was already found
                    // would be a strange way to recover.
                    kickMs += watch.Elapsed.TotalMilliseconds;

                    continue;
                }

                var start = best;
                var borrowed = false;

                if (env.ShareBest && !Excluded(env.ShareNot, stream) &&
                    Solving.Adopt(top) is { } theirs)
                {
                    start = theirs;
                    borrowed = true;
                    migrated++;
                }

                // **Shaken harder when the chain is somebody else's, or the worker re-derives it.**
                //
                // A kick's perturbation has to be stronger than the descent that follows can undo -
                // that is the whole of iterated local search, and it is why one shake here tears a
                // third of the chain. Adopting raises the bar: the worker is being dropped into a
                // basin it did not find, and a gentle shake leaves it inside, so it descends back
                // onto the chain it was handed and spends the rest of the window confirming it.
                // Measured on the first press where sharing fired, three adopters finished on the
                // identical 4,490.8 - better than any of them managed alone, so the adoption paid,
                // but all three landed on the same point rather than exploring around it.
                //
                // What is wanted from a migration is not the leader's answer, which the pool already
                // has, but a DIFFERENT answer near it. See SolverSettings.AdoptedShake.
                var shake = borrowed
                    ? Math.Max(1, env.AdoptedShake) + restarts % 2
                    : 1 + restarts % 3;

                chain = Planner.Improve(env, candidates,
                    Shaken(env, shortlist, start, random, shake), Waiting);

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
                    Planner.MustTakeTour(env, candidates, chain, stream) is { Count: > 0 } again)
                {
                    chain = Planner.Improve(env, candidates, again, Waiting);
                }

                // Its own counter. A kick is a full local search and it recurs - billing it to the
                // opening made a recurring cost look like a fixed one.
                kickMs += watch.Elapsed.TotalMilliseconds;

                score = Planner.Score(env, chain);

                if (score > top)
                {
                    // **Every record, in its best order, because that is where it compounds.**
                    //
                    // Ordering the final answer once fixes the answer; ordering each record fixes what
                    // every later tear starts from. A handful of records a window, a few milliseconds
                    // each - see Permuting for what it actually costs - against a swing measured at
                    // forty per cent of the propagation on an identical spot set.
                    best = Permuted(env, new List<Vector2>(chain), env.Placed?.Count ?? 0);

                    // **And slid along the route**, which is the compound move the operators cannot
                    // reach - see Slid. On a record, because that is the chain worth refining and the
                    // cost is a couple of dozen scored chains.
                    best = Slid(env, shortlist, best, env.Placed?.Count ?? 0);

                    top = Planner.Score(env, best);

                    chain.Clear();
                    chain.AddRange(best);
                    score = top;

                    improved = DateTime.UtcNow;
                    bettered++;

                    Solving.Scored(stream, Planner.Plainly(env, best), top, lean);
                    found?.Invoke(new List<Vector2>(best));
                }

                continue;
            }

            work.Clear();
            work.AddRange(chain);

            var how = Draw(random, weight);

            used[how]++;

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

                work.Clear();
                work.AddRange(reached);
            }
            else
            {
                watch.Restart();

                var torn = Torn(env, random, since, work.Count);
                var at = Tear(env, random, work, how, torn);

                if (torn < sized.Length)
                    sized[torn]++;

                if (at < 0)
                    continue;

                Rebuild(env, shortlist, reach, work, at, torn, random);

                tearMs += watch.Elapsed.TotalMilliseconds;
                spent[how] += watch.Elapsed.TotalMilliseconds;
            }

            if (work.Count < chain.Count || !Sound(env, work))
                continue;

            var worth = Planner.Score(env, work);

            // **Credited for being accepted, not only for setting a record.** A record happens once
            // or twice in a whole window, so scoring the operators on records alone gave all three
            // of them a rate near nought and left the weights flat - 797, 826 and 880 tries, which
            // is three equal shares dressed up as adaptation. Acceptance happens a hundred times and
            // is what the weights are actually able to learn from.
            if (worth > score || worth >= top * (1d - slack))
                paid[how]++;

            if (worth > top)
            {
                // Polished only when it is already the best there is, because polishing is the
                // expensive half and most torn chains are not worth spending it on.
                watch.Restart();

                var polished = Planner.Improve(env, candidates, new List<Vector2>(work), Waiting);

                polishMs += watch.Elapsed.TotalMilliseconds;
                var after = Planner.Score(env, polished);

                if (after > worth)
                {
                    work.Clear();
                    work.AddRange(polished);
                    worth = after;
                }

                top = worth;
                best = new List<Vector2>(work);
                improved = DateTime.UtcNow;
                since = 0;
                won[how]++;
                bettered++;

                Solving.Scored(stream, Planner.Plainly(env, best), top, lean);
                found?.Invoke(new List<Vector2>(best));
            }

            // Accepted, which is not the same as best: a chain a little worse than the record is
            // where the next record usually comes from.
            if (worth > score || worth >= top * (1d - slack))
            {
                chain.Clear();
                chain.AddRange(work);
                score = worth;
                kept++;
            }

            if (rounds % 32 == 0)
                Reweigh(weight, used, paid, won, spent);
        }

        // What this worker did, beside its score. See Solving.Said - the aggregate below is one
        // worker's, whichever finished last, and that is rarely the one worth looking at.
        Solving.Said(stream,
            $"built {raw:N0} -> opened {opened:N0} -> {top:N0} " +
            $"({(opened > 0d ? (top - opened) / opened * 100d : 0d):+0.0;-0.0;0.0}%), " +
            $"{rounds:N0} rounds, {bettered:N0} bests, {restarts:N0} kicks " +
            $"({migrated:N0} adopted, {rescued:N0} restarted), " +
            $"reach {won[3]:N0} won of {used[3]:N0}, " +
            $"open {openingMs:N0}ms band {bandMs:N0}ms kick {kickMs:N0}ms " +
            $"reach {reachMs:N0}ms tear {tearMs:N0}ms");

        var spread = new List<string>();

        for (var i = 1; i < sized.Length; i++)
        {
            if (sized[i] > 0)
                spread.Add($"{i}x{sized[i]:N0}");
        }

        Telling = $"{rounds:N0} rounds, {kept:N0} accepted, {bettered:N0} new bests, " +
                  $"{restarts:N0} kicks on stagnation ({migrated:N0} started from the pool's best); " +
                  $"shortlist {shortlist.Count} of {candidates.Count}; " +
                  $"tears {Named(used, paid, won, spent)}; " +
                  $"sized {(spread.Count > 0 ? string.Join(" ", spread) : "none")} " +
                  $"(of {env.TearLeast} to {env.TearMost}); " +
                  $"reach arrived {arrived:N0} of {asked:N0} asked; " +
                  $"spent opening {openingMs:N0}ms (of which {bandMs:N0}ms on the shared band " +
                  $"search), kicks {kickMs:N0}ms, tearing {tearMs:N0}ms, " +
                  $"reaching {reachMs:N0}ms, polishing {polishMs:N0}ms";

        return Planner.Describe(env,
            Slid(env, shortlist, Permuted(env, best, env.Placed?.Count ?? 0),
                env.Placed?.Count ?? 0));
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
    private static List<Vector2> Opening(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> shortlist, Random random, Func<bool> waiting, CancellationToken token,
        int shakes, List<Vector2> seed = null, System.Diagnostics.Stopwatch band = null)
    {
        // `shakes` is the worker number, so nought is the one that exploits. See below.
        var stream = shakes;

        List<Vector2> best = null;
        var top = double.NegativeInfinity;

        // Last time's answer, if the ground still allows it. Checked rather than trusted: explosives
        // may have gone down since, which moves the origin and can strand a link that was fine.
        if (seed is { Count: > 0 } && Sound(env, seed))
        {
            best = shakes > 0
                ? Shaken(env, shortlist, seed, random, Math.Min(shakes, Math.Max(1, env.OpeningShakes)))
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
                ? Shaken(env, shortlist, banded, random, Math.Min(shakes, Math.Max(1, env.OpeningShakes)))
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

            var raw = Planner.Greedy(env, candidates, i == 0 ? null : random, i == 0 ? 1 : 1 + random.Next(5));
            var worth = Planner.Score(env, raw);

            if (worth <= top)
                continue;

            top = worth;
            best = raw;
        }

        best ??= Planner.Greedy(env, candidates, null, 1);

        return Planner.Improve(env, candidates, best, waiting);
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
    /// A starting belief only. Reweigh runs from the first round and moves these towards whatever is
    /// actually returning, so a lean that turns out to be wrong costs a few rounds rather than the
    /// window. See SolverSettings.VaryOperators.
    /// </summary>
    private static double[] Leaning(PlanEnvironment env, int stream)
    {
        var weight = new[] { 1d, 1d, 1d, 1d };

        if (!env.VaryOperators || stream < 0)
            return weight;

        var told = Told(env.TearingMix, stream);

        // No entry for this thread, or told to stay even. Both mean the same thing and both are
        // ordinary rather than a mistake - a list shorter than the thread count is how somebody says
        // "the rest of them balanced". See SolverSettings.TearingMix.
        if (told.At < 0)
            return weight;

        // The share named goes to the one favoured and the remainder is split equally. Kept as
        // plain proportions rather than percentages, because Draw rolls against the total and does
        // not care what they add up to.
        var rest = Math.Max(0d, 100d - told.Share) / 3d;

        for (var i = 0; i < weight.Length; i++)
            weight[i] = i == told.At ? Math.Max(0.01d, told.Share) : Math.Max(0.01d, rest);

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

    /// <summary>Picks a destroy operator in proportion to how well each has been doing.</summary>
    private static int Draw(Random random, double[] weight)
    {
        var total = 0d;

        foreach (var w in weight)
            total += w;

        var roll = random.NextDouble() * total;

        for (var i = 0; i < weight.Length; i++)
        {
            roll -= weight[i];

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

        // **Charged at what this site's detours actually cost, not at the straight line.**
        //
        // The line is a lower bound on the journey and was being used as though it were the journey,
        // so anything needing a detour was admitted and then failed three hops later. Dividing the
        // reach by the learned ratio is the same statement from the other side: a link is worth less
        // ground than it looks on a site that makes you go round things. See Detour.
        var rate = env.EstimateDetour ? Detour : 1d;
        var span = (float)(spend * env.Reach / rate) + env.Blast;
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
    internal static List<Vector2> Shaken(PlanEnvironment env, List<Vector2> shortlist,
        List<Vector2> chain, Random random, int times)
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
            {
                // **A kick has to be bigger than the local search can put back.** One to four links
                // out of fifteen, rebuilt from a shortlist, is inside Improve's basin - it sweeps
                // every one of nearly four thousand candidates against every link, so it simply
                // polished the perturbation away and all four workers came back at 10,552.3 to the
                // decimal, twice running. Iterated local search says the perturbation must be
                // strong enough that the descent cannot retrace it; a third of the chain is the
                // usual rule of thumb.
                var torn = Math.Min(Math.Max(Math.Max(3, env.TearMost), work.Count / 3),
                    Math.Max(1, work.Count - 1));
                var at = Tear(env, random, work, random.Next(3), torn);

                if (at < 0)
                {
                    work.Clear();
                    work.AddRange(was);

                    continue;
                }

                Rebuild(env, shortlist, reach, work, at, torn, random);

                // A kick that leaves the chain illegal is no kick at all.
                if (work.Count < was.Count || !Sound(env, work))
                {
                    work.Clear();
                    work.AddRange(was);
                }
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
    /// The best ORDERING of a chain's planned links, found by trying every one of them.
    ///
    /// **An expedition site takes six explosives, and six things have seven hundred and twenty
    /// orderings.** That is nothing beside the eight hundred thousand trials a window already runs,
    /// and it settles exactly the question the heuristics cannot: propagation pays a rune against the
    /// waves it reaches, so the same six spots catching the same markers are worth wildly different
    /// amounts depending on which remnant comes first.
    ///
    /// Measured on one site, two presses of the same spot set: content identical to four figures,
    /// the same six remnants caught, near-identical total rune landings (21 against 20) - and
    /// propagation of 5,298.6 against 3,785.1. A forty per cent swing, entirely in which remnant the
    /// runes happened to land on. The reordering operators were not idle either: the WORSE press made
    /// more swaps (884 against 749) and banked more cumulative worth, and still finished 1,513 short.
    /// Adjacent swapping cannot walk from one permutation basin to another; enumeration does not have
    /// to.
    ///
    /// **Only the planned tail moves.** Links already on the ground cannot be reordered, so the laid
    /// prefix is held and the permutations run over what is left - which is also what keeps this
    /// affordable as bombs go down rather than only at the start.
    ///
    /// Bounded by the link count because the factorial is the whole point: six is 720, eight is
    /// 40,320, and a Grand site's fifteen is 1.3 trillion. Above the bound the heuristics stay in
    /// charge. See SolverSettings.PermuteUpTo.
    ///
    /// **Off by default, because it was measured and it wins nothing.** Five cold presses of a
    /// six-link site enumerated about 14,160 orderings and improved the chain nought times: reorder
    /// and reverse already find the best sequence at this length. Which settles what the swing above
    /// actually was - not the order the blasts are visited in, but which spots are chosen and how the
    /// markers fall across them. A remnant's reach is the monsters unearthed after it, so two chains
    /// catching the identical set can distribute it very differently, and no enumeration of orderings
    /// reaches that.
    ///
    /// Kept rather than deleted on the chance a differently shaped site pays for it. Permuting is what
    /// says whether it ever does.
    /// </summary>
    /// <summary>How many orderings have been enumerated, and what it cost. See Permuted.</summary>
    public static string Permuting =>
        _permutes == 0
            ? "not run"
            : $"{_permutes:N0} calls over {_permuted:N0} orderings in {_permuteMs:N0}ms, " +
              $"{_permuteWon:N0} improved the chain";

    private static long _permutes;
    private static long _permuted;
    private static long _permuteWon;
    private static double _permuteMs;

    /// <summary>Forgets the tally, so a solve is judged on its own. See Permuting.</summary>
    public static void Unpermute()
    {
        System.Threading.Interlocked.Exchange(ref _permutes, 0);
        System.Threading.Interlocked.Exchange(ref _permuted, 0);
        System.Threading.Interlocked.Exchange(ref _permuteWon, 0);
        System.Threading.Interlocked.Exchange(ref _permuteMs, 0d);
    }

    internal static List<Vector2> Permuted(PlanEnvironment env, List<Vector2> chain, int laid)
    {
        if (chain == null || env.PermuteUpTo <= 0)
            return chain;

        var head = Math.Max(0, Math.Min(laid, chain.Count));
        var tail = chain.Count - head;

        if (tail < 2 || tail > env.PermuteUpTo)
            return chain;

        var order = new List<Vector2>(chain.GetRange(head, tail));
        var work = new List<Vector2>(chain);
        var best = new List<Vector2>(chain);
        var top = Planner.Score(env, chain);
        var was = top;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var seen = 0L;

        void Walk(int at)
        {
            if (at == order.Count)
            {
                seen++;

                for (var i = 0; i < order.Count; i++)
                    work[head + i] = order[i];

                // Legality first: most permutations break the link-to-link reach, and scoring one is
                // far dearer than refusing it.
                if (!Sound(env, work))
                    return;

                var worth = Planner.Score(env, work);

                if (worth <= top)
                    return;

                top = worth;
                best = new List<Vector2>(work);

                return;
            }

            for (var i = at; i < order.Count; i++)
            {
                (order[at], order[i]) = (order[i], order[at]);

                Walk(at + 1);

                (order[at], order[i]) = (order[i], order[at]);
            }
        }

        Walk(0);

        // Written down rather than assumed. The whole case for enumerating is that a factorial this
        // small is cheap, and a claim like that belongs in the dump where it can be checked.
        System.Threading.Interlocked.Increment(ref _permutes);
        System.Threading.Interlocked.Add(ref _permuted, seen);

        if (top > was)
            System.Threading.Interlocked.Increment(ref _permuteWon);

        lock (PermuteGate)
            _permuteMs += watch.Elapsed.TotalMilliseconds;

        return best;
    }

    private static readonly object PermuteGate = new();

    /// <summary>
    /// Slides a run of links along the route, all together, and keeps it if the whole move pays.
    ///
    /// **A compound move, because the parts of it are not worth making.** Observed by hand on two
    /// chains 96 points apart: the last four links had each shifted a little further along the route,
    /// toward where the next one used to be, and the only link that gained anything was the last. Every
    /// step of that is neutral or slightly worse on its own, so single-link relocation rejects the
    /// first one and never sees the fourth; and tear-and-rebuild does not find it either, since the
    /// rebuild scores the best-ranked few candidates in a window around the hole rather than a joint
    /// displacement of everything after it.
    ///
    /// This is the ejection-chain idea in its simplest useful form: make the compound move ONE move,
    /// so it is judged by what it is worth as a whole. Each link in the run steps toward the link that
    /// follows it - the direction the observation actually had, rather than a common translation - and
    /// snaps to the nearest legal candidate, because a chain may only stand on spots the site offers.
    ///
    /// Cheap enough to run on every record: a handful of run lengths against a handful of offsets is
    /// twenty-odd scored chains, where a window already runs hundreds of thousands. See Sliding for
    /// whether it ever pays.
    /// </summary>
    internal static List<Vector2> Slid(PlanEnvironment env, List<Vector2> shortlist,
        List<Vector2> chain, int laid)
    {
        if (chain == null || shortlist == null || env.SlideBy <= 0f)
            return chain;

        var head = Math.Max(0, Math.Min(laid, chain.Count));

        if (chain.Count - head < 2)
            return chain;

        var best = chain;
        var top = Planner.Score(env, chain);
        var was = top;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tried = 0;

        // From the longest run to the shortest, because the observation was of the whole tail moving
        // and a shorter run is a special case of it.
        for (var from = head; from <= chain.Count - 2; from++)
        {
            for (var step = 1; step <= Slides; step++)
            {
                var how = env.SlideBy * step;
                var work = new List<Vector2>(best);
                var shifted = 0;

                for (var i = from; i < work.Count; i++)
                {
                    // Toward the link that follows, or toward the one before when this is the last -
                    // the tail has to keep going the way the route was already going.
                    var ahead = i + 1 < work.Count
                        ? work[i + 1] - work[i]
                        : work[i] - work[i - 1];

                    if (ahead.LengthSquared() < 0.01f)
                        continue;

                    var want = work[i] + Vector2.Normalize(ahead) * how;
                    var spot = Nearest(shortlist, want);

                    if (spot == work[i])
                        continue;

                    work[i] = spot;
                    shifted++;
                }

                tried++;

                // **How many links actually moved, because the snap can undo the slide.** Each link is
                // nudged along the route and then pulled to the nearest spot the site offers - and if
                // the spots are further apart than the nudge, it lands back where it started. A slide
                // that shifts one link of four is not the compound move this exists for; it is a
                // relocation the operators already make. See Sliding.
                System.Threading.Interlocked.Add(ref _slideShifted, shifted);

                if (shifted > 1)
                    System.Threading.Interlocked.Increment(ref _slideReal);

                if (shifted == 0 || !Sound(env, work))
                    continue;

                var worth = Planner.Score(env, work);

                if (worth <= top)
                    continue;

                top = worth;
                best = work;
            }
        }

        System.Threading.Interlocked.Increment(ref _slides);
        System.Threading.Interlocked.Add(ref _slid, tried);

        if (top > was)
        {
            System.Threading.Interlocked.Increment(ref _slideWon);

            lock (SlideGate)
                _slideGain += top - was;
        }

        lock (SlideGate)
            _slideMs += watch.Elapsed.TotalMilliseconds;

        return best;
    }

    /// <summary>How many multiples of the offset to try. See Slid.</summary>
    private const int Slides = 4;

    /// <summary>The shortlist spot nearest a wanted position. See Slid.</summary>
    private static Vector2 Nearest(List<Vector2> spots, Vector2 want)
    {
        var best = want;
        var near = float.MaxValue;

        foreach (var spot in spots)
        {
            var apart = Vector2.DistanceSquared(spot, want);

            if (apart >= near)
                continue;

            near = apart;
            best = spot;
        }

        return best;
    }

    /// <summary>What sliding has cost and won, for the dump. See Slid.</summary>
    public static string Sliding =>
        _slides == 0
            ? "not run"
            : $"{_slides:N0} calls over {_slid:N0} slides in {_slideMs:N0}ms, " +
              $"{_slideWon:N0} improved the chain by {_slideGain:N0} in total; " +
              $"{_slideReal:N0} of those slides moved more than one link, " +
              $"{(_slid == 0 ? 0d : (double)_slideShifted / _slid):0.0} links moved on average";

    private static long _slides;
    private static long _slid;
    private static long _slideWon;
    private static double _slideMs;
    private static double _slideGain;
    private static long _slideShifted;
    private static long _slideReal;

    private static readonly object SlideGate = new();

    /// <summary>Forgets the tally, so a solve is judged on its own. See Sliding.</summary>
    public static void Unslide()
    {
        System.Threading.Interlocked.Exchange(ref _slides, 0);
        System.Threading.Interlocked.Exchange(ref _slid, 0);
        System.Threading.Interlocked.Exchange(ref _slideWon, 0);
        System.Threading.Interlocked.Exchange(ref _slideShifted, 0);
        System.Threading.Interlocked.Exchange(ref _slideReal, 0);

        lock (SlideGate)
        {
            _slideMs = 0d;
            _slideGain = 0d;
        }
    }

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
