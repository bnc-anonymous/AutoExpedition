using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace AutoExpedition;

/// <summary>
/// The same search on several threads at once, each from its own seed, best answer kept.
///
/// **One thread was never a decision, it was where the code started.** ExpeditionIcons runs five
/// independent populations and sums their generations; RuneHighlighter runs two. This plugin ran one
/// - so on an identical window it did a fifth of the searching and reported it as a strategy
/// difference.
///
/// Independent runs with a different random stream each, and the best of them taken, is the lowest
/// risk parallelism there is: nothing is shared, nothing is merged, and a worker that finds nothing
/// costs only its own time. It is also what the literature does with multi-start methods, which is
/// what most of these modes are.
///
/// **What made it safe to do only now.** Every scratch buffer in Planner, including the coverage
/// index, is [ThreadStatic]; Route's caches are concurrent and its flood is behind a lock; and the
/// terrain and obstacle snapshots are read-only. Route was the one piece of shared mutable state in
/// the search path, and it crashed a solve four times in one dig site before that was fixed.
///
/// The diagnostics are the honest exception: Planner's counters - rounds, sweeps, openings - are
/// plain statics, so with several workers they report a blend rather than one run. Nothing reads
/// them to make a decision, and the per-worker scores below say what actually happened.
/// </summary>
internal static class Solving
{
    /// <summary>What the last parallel run scored, per worker, for the dump.</summary>
    public static string Spread { get; private set; } = "single threaded";

    /// <summary>
    /// The best chain any worker has published this run, for a stuck worker to start from.
    ///
    /// **Eight workers searched independently and seven of their eight seconds were thrown away.**
    /// Measured on one press: thread scores of 3,422, 3,600, 4,198, 4,282, 4,336, 4,451, 4,488 and
    /// 5,222. The answer is the best of them, so seven workers spent a full window arriving
    /// somewhere that was discarded, and two of them finished below where a greedy opening starts.
    /// Independent multi-start is the safest parallelism there is and this is what it costs.
    ///
    /// The player already had the fix and was paying a keypress for it: a second press inherits the
    /// first's chain as its floor and improves on it - measured at +207 on a site where a press from
    /// nothing gains nothing. That is an exchange of solutions with a latency of one keypress. Doing
    /// it inside the window is the same mechanism without the second press, and it is what
    /// cooperative parallel search means in the literature - workers that exchange the incumbent
    /// rather than a portfolio of strangers.
    ///
    /// **Offered at the moment a worker is stuck and about to throw its chain away anyway**, so it
    /// costs nothing: the kick already rebuilds from a shaken chain, and this only changes WHICH
    /// chain is shaken. See Adopt for the two guards that stop the pool collapsing onto one answer.
    /// </summary>
    private static List<Vector2> _shared;

    private static double _sharedWorth = double.NegativeInfinity;

    private static readonly object _share = new();

    private static int _migrations;

    /// <summary>How many kicks started from somebody else's chain, for the dump. See Adopt.</summary>
    public static int Migrations => _migrations;

    /// <summary>
    /// The pool's best chain, if it is enough better than <paramref name="mine"/> to be worth moving
    /// to.
    ///
    /// **A margin, because without one every kick migrates and eight searches become one.** Two
    /// workers three points apart are exploring equally good ground and moving either onto the
    /// other's chain buys nothing while costing all the diversity that found the good chain in the
    /// first place. So a worker only moves when the pool is materially ahead of it - which is
    /// exactly the case that was being wasted, a worker at 3,422 grinding away while another sat on
    /// 5,222.
    ///
    /// Caller-side there is a second guard: some workers never ask. See Repair's kick.
    /// </summary>
    public static List<Vector2> Adopt(double mine)
    {
        lock (_share)
        {
            if (_shared == null || _sharedWorth <= mine * Margin)
                return null;

            _migrations++;

            return new List<Vector2>(_shared);
        }
    }

    /// <summary>
    /// Whether the pool is far enough ahead of <paramref name="mine"/> to call this worker lost.
    ///
    /// **Half of every pool finishes in a trap, and the traps are the same three chains.** Across
    /// every measured press with sharing off, 38 of 77 workers finished below 4,000 while their
    /// neighbours found 4,130 - and they piled onto three values, 3,072 sixteen times and 3,846 or
    /// 3,847 eight more. A worker down there has a record of its own to be judged against, so its
    /// acceptance window is sixty points wide around a chain a quarter below what the site offers,
    /// and the kick tears it and the polish puts it straight back. It spends the rest of the window
    /// confirming a dead answer.
    ///
    /// Reads the same number Adopt does and is used for the opposite purpose: not to move the worker
    /// onto the leader's chain - which rescues it and costs the independence that produces the rare
    /// good answers - but to tell it that its own is not worth continuing. See Repair's rescue.
    /// </summary>
    public static bool Behind(double mine, double margin)
    {
        lock (_share)
        {
            return _shared != null && _sharedWorth > 0d &&
                   mine < _sharedWorth * (1d - margin);
        }
    }

    /// <summary>How many workers gave up on a dead chain, for the dump. See Behind.</summary>
    public static int Rescues => _rescues;

    private static int _rescues;

    /// <summary>Counts one, from the worker that did it. See Behind.</summary>
    public static void Rescued() => System.Threading.Interlocked.Increment(ref _rescues);

    /// <summary>
    /// How far ahead the pool has to be before a stuck worker moves to it.
    ///
    /// Half a per cent, which on a four thousand point site is twenty points - comfortably inside
    /// the run-to-run noise of about a hundred and eighty, so it never fires on two workers that
    /// have found the same thing by different routes, and always fires on the gap this exists to
    /// close.
    /// </summary>
    private const double Margin = 1.005d;

    /// <summary>
    /// Somewhere for a worker to say what it did, so the spread is more than eight numbers.
    ///
    /// **Eight scores tell you they differ and nothing about why.** One worker reached 16,257.1
    /// where the rest sat between 13,767 and 15,690, and every counter that might have explained it
    /// - rounds, which operators won, how many kicks - was a static that the last worker to finish
    /// had overwritten. So the line described some worker, and not the interesting one.
    ///
    /// Written into a slot per worker rather than a shared string, and the array is replaced at the
    /// start of each run so nothing survives into the next one.
    /// </summary>
    public static void Said(int worker, string what)
    {
        var slots = _said;

        if (slots != null && worker >= 0 && worker < slots.Length)
            slots[worker] = what;
    }

    private static string[] _said = [];

    /// <summary>
    /// What each worker's best is RIGHT NOW, and what it leans on, for the overlay.
    ///
    /// **Spread says what the workers did and says it afterwards.** It is built once every thread
    /// has finished, which is the wrong moment for the question this answers: whether the eight of
    /// them are exploring different ground or converging on one answer, watched while it happens. A
    /// set of numbers climbing together says the diversification is not working; one pulling away
    /// says it is.
    ///
    /// Written by each worker into its own slot and read by the frame, so there is no lock and no
    /// agreement about when. A double write is atomic on the platforms this runs on, and a torn
    /// read would cost one wrong number for one frame in a readout nobody acts on.
    /// </summary>
    public static (double Plain, double Total, string Lean)[] Watching()
    {
        var plains = _plain;
        var bests = _best;
        var leans = _lean;

        if (bests == null || plains == null || leans == null || bests.Length == 0)
            return [];

        var said = new (double, double, string)[bests.Length];

        for (var i = 0; i < bests.Length; i++)
        {
            said[i] = (i < plains.Length ? plains[i] : bests[i], bests[i],
                i < leans.Length ? leans[i] ?? "" : "");
        }

        return said;
    }

    /// <summary>
    /// A worker's best so far - what it is worth to a reader, what it is worth to the search, and what
    /// it was told to favour.
    ///
    /// **Both figures, because they are different numbers and the readout was showing the wrong one.**
    /// A required marker is weighted above everything else in the site put together, so the objective
    /// carries a synthetic bonus for holding it - and these lines reported that, while the green score
    /// beside them reported content plus propagation. On a site with one must-take the same chain read
    /// 18,176 here and 12,096 there, which looks like one of them being broken.
    ///
    /// So the plain figure leads, matching the score on screen, and the objective follows in brackets
    /// only where the two differ. Verdict.Plain already said this in as many words: the search wants
    /// Total, bonus and all, and any figure a person reads wants Plain.
    /// </summary>
    public static void Scored(int worker, double plain, double best, string lean)
    {
        var plains = _plain;
        var bests = _best;
        var leans = _lean;

        if (bests == null || worker < 0 || worker >= bests.Length)
            return;

        bests[worker] = best;

        if (plains != null && worker < plains.Length)
            plains[worker] = plain;

        if (leans != null && worker < leans.Length)
            leans[worker] = lean;
    }

    private static double[] _best = [];
    private static double[] _plain = [];
    private static string[] _lean = [];

    /// <param name="worker">
    /// The search itself, given its worker number - which is the seed every mode varies its random
    /// stream by - and the callback to publish a better chain through.
    /// </param>
    public static Plan Across(PlanEnvironment env, int threads, Action<List<Vector2>> found,
        Func<int, Action<List<Vector2>>, Plan> worker, string engine = "search")
    {
        threads = Math.Max(1, threads);

        _said = new string[threads];
        _best = new double[threads];
        _plain = new double[threads];
        _lean = new string[threads];

        for (var i = 0; i < threads; i++)
        {
            _best[i] = double.NegativeInfinity;
            _plain[i] = double.NegativeInfinity;
        }

        // A run is judged on its own, and an incumbent carried in from the last one would be a floor
        // nobody asked for. See Adopt.
        lock (_share)
        {
            _shared = null;
            _sharedWorth = double.NegativeInfinity;
            _migrations = 0;
            _rescues = 0;
        }

        if (threads == 1)
        {
            var only = worker(0, found);

            Spread = _said[0] is { Length: > 0 } alone
                ? $"single threaded - {alone}"
                : "single threaded";

            return only;
        }

        // **Publishing is serialised and monotonic.** The overlay draws whatever comes through here,
        // and several workers improving at once would otherwise walk it backwards every time a
        // slower one caught up with where a faster one had already been.
        var gate = new object();
        var shown = double.NegativeInfinity;

        void Publish(List<Vector2> chain)
        {
            if (chain is not { Count: > 0 })
                return;

            var worth = Planner.Score(env, chain);

            lock (gate)
            {
                if (worth <= shown)
                    return;

                shown = worth;

                found?.Invoke(new List<Vector2>(chain));
            }

            // And kept, so a worker that is stuck can start from it instead of from its own. The
            // same copy the overlay draws; publishing is already the moment a chain became worth
            // telling anybody about. See Adopt.
            lock (_share)
            {
                if (worth <= _sharedWorth)
                    return;

                _sharedWorth = worth;
                _shared = new List<Vector2>(chain);
            }
        }

        var answers = new Plan[threads];
        var tasks = new Task[threads];

        for (var i = 0; i < threads; i++)
        {
            var n = i;

            // Recorded on the worker's own thread, not the one that starts it. A search fans out
            // over these and the thread above them does almost nothing, so measuring that one
            // reports a search as very nearly free. See BackgroundWork.
            tasks[i] = Task.Run(() =>
                answers[n] = BackgroundWork.Record($"{engine} (worker)", () =>
                {
                    try
                    {
                        return worker(n, Publish);
                    }
                    finally
                    {
                        Planner.CountedUp();
                    }
                }));
        }

        Task.WaitAll(tasks);

        Plan best = null;
        var top = double.NegativeInfinity;
        var said = new List<string>();

        for (var i = 0; i < answers.Length; i++)
        {
            var plan = answers[i];

            if (plan is not { Points.Count: > 0 })
            {
                said.Add($"{i}: nothing");

                continue;
            }

            var worth = Planner.Score(env, plan.Points);
            var did = _said.Length > i && _said[i] is { Length: > 0 } ? $" ({_said[i]})" : "";

            said.Add($"{i}: {worth:N1}{did}");

            if (worth <= top)
                continue;

            top = worth;
            best = plan;
        }

        // The spread between workers is the number worth reading: all of them landing on the same
        // score means the extra threads are re-finding one answer and the seeds are not separating
        // them, which is a reason to change the search rather than to buy more of it.
        Spread = $"{threads} workers - {string.Join(", ", said)}";

        return best ?? Plan.Empty;
    }
}
