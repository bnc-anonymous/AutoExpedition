using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
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
    /// The best chain any worker has published this run, and its score. Read by Behind, which restarts a worker
    /// that has fallen too far below it, and by Leader, which a refining worker rebuilds from.
    ///
    /// Workers never take it over whole. Sharing the best chain at a kick was built and removed: it raised the
    /// pool's mean and floor, neither of which is kept, and cost the independent outliers that set the press
    /// maximum - the best press in its measurement came from a worker left alone, once in two presses with
    /// sharing off and never in seven with it on.
    /// </summary>
    private static List<Vector2> _shared;

    private static double _sharedWorth = double.NegativeInfinity;

    /// <summary>
    /// What the site pays out of <see cref="_sharedWorth"/>, which is what the two margins below are
    /// a share of.
    ///
    /// **A held requirement adds a ceiling on the whole site to every chain that holds it**, so on a
    /// site with one the score is mostly a constant: measured at 25,402 of 34,908, leaving 9,506 that
    /// can actually move. A percentage of the total then means something different on every site -
    /// Behind's ten per cent asked for 3,490 points, which between two workers both holding the
    /// requirement is a third of everything in play, and no worker was ever that far down. Both
    /// margins are taken against this instead.
    /// </summary>
    private static double _sharedPlain;

    private static readonly object _share = new();

    /// <summary>
    /// When any worker of the running solve last raised the solve's best, in UTC ticks. The improvement
    /// window is measured from this rather than from each worker's own record, so no worker stops while
    /// another is still finding better chains. See Repair.Search's Waiting, and PoolImprovedAt.
    /// </summary>
    private static long _poolImprovedTicks = DateTime.UtcNow.Ticks;

    /// <summary>When the running solve's best last rose. See _poolImprovedTicks.</summary>
    internal static DateTime PoolImprovedAt => new(System.Threading.Interlocked.Read(ref _poolImprovedTicks), DateTimeKind.Utc);

    private static void PoolImproved() => System.Threading.Interlocked.Exchange(ref _poolImprovedTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// The pool's best chain so far and its score, or null and negative infinity before anything is published.
    /// A copy. Read by a refining worker, which keeps its first links and rebuilds the rest. See Repair's
    /// RefinedStart.
    /// </summary>
    public static (List<Vector2> Chain, double Worth) Leader()
    {
        lock (_share)
            return _shared == null ? (null, double.NegativeInfinity) : (new List<Vector2>(_shared), _sharedWorth);
    }

    /// <summary>The pool's best score so far, or negative infinity. See Leader.</summary>
    public static double LeaderWorth()
    {
        lock (_share)
            return _shared == null ? double.NegativeInfinity : _sharedWorth;
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
    /// Not used to move the worker onto the leader's chain - which rescues it and costs the independence
    /// that produces the rare good answers - but to tell it that its own is not worth continuing. See
    /// Repair's rescue.
    ///
    /// **Against the best found in this solve, not the plan carried into it.** The worker that continues from
    /// the standing plan publishes it at once, so it was the leader from the first second - and a standing plan
    /// is the best of every earlier solve, the top of the distribution. Measured in a 24s continuous reroll pass
    /// on Scorched Cay with 16,630 carried in: every other worker was ten per cent below it for most of the
    /// window, and two were restarted five and six times, none of them reaching past 15,100.
    /// </summary>
    public static bool Behind(double mine, double margin)
    {
        lock (_share)
        {
            return _foundWorth > 0d && mine < _foundWorth - _foundPlain * margin;
        }
    }

    /// <summary>
    /// Whether the worker on this thread started from the carried plan, so what it publishes is that plan or an
    /// improvement on it rather than something this solve found. Set by Repair.Search on the worker's own
    /// thread, which is the thread its publishes run on. See Behind.
    /// </summary>
    [ThreadStatic] public static bool PublishingFromCarried;

    /// <summary>
    /// Tells the pool what the plan carried into this solve scores, so Behind can leave it out. Called by every
    /// worker handed a seed, before it publishes anything.
    /// </summary>
    public static void Carried(double worth)
    {
        lock (_share)
            _carriedWorth = worth;
    }

    /// <summary>The carried plan's score, or NaN when none was carried in. See Carried.</summary>
    private static double _carriedWorth = double.NaN;

    /// <summary>The best score published this solve other than the carried plan, and its plain part. See Behind.</summary>
    private static double _foundWorth = double.NegativeInfinity;

    /// <summary>See _foundWorth.</summary>
    private static double _foundPlain;

    /// <summary>How many workers gave up on a dead chain, for the dump. See Behind.</summary>
    public static int Rescues => _rescues;

    private static int _rescues;

    /// <summary>Counts one, from the worker that did it. See Behind.</summary>
    public static void Rescued() => System.Threading.Interlocked.Increment(ref _rescues);

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

    /// <summary>
    /// What each worker did over its window, for the press history rather than for the search.
    ///
    /// Reported once by each worker as it finishes, so there is nothing to synchronise beyond the
    /// array being long enough - a worker writes only its own slot. See PressHistory.
    /// </summary>
    public static void Counted(int worker, long rounds, int bests, int[] zoneWon, double halfway = 0d)
    {
        if (worker < 0)
            return;

        lock (_gate)
        {
            if (_rounds.Length <= worker)
            {
                Array.Resize(ref _rounds, worker + 1);
                Array.Resize(ref _bests, worker + 1);
                Array.Resize(ref _zones, worker + 1);
                Array.Resize(ref _halfway, worker + 1);
            }

            _rounds[worker] = rounds;
            _bests[worker] = bests;
            _zones[worker] = zoneWon;
            _halfway[worker] = halfway;
        }
    }

    private static long[] _rounds = [];
    private static int[] _bests = [];
    private static int[][] _zones = [];
    private static double[] _halfway = [];

    private static readonly object _gate = new();

    /// <summary>
    /// The winning chain against the median one, link by link and catch by catch.
    ///
    /// **Every configuration tried on one site moved the median worker by nothing.** Across eight
    /// batches and five different changes the middle worker finished at 7,367, 7,452, 7,308, 7,370,
    /// 7,367 and so on - the same handful of figures whatever the openings, the operator mix, the
    /// restart policy or the draw. Most workers converge on the same few chains, and a press is worth
    /// about 7,400 plus whatever one worker finds if it escapes. So the question stopped being how much
    /// the search achieves and became WHAT it is stuck on, which no counter could answer.
    ///
    /// The arithmetic here is the readout's own - blast plus the marker's extent, the same rule
    /// Planner.Catches applies - rather than the coverage index, which is private to the search and
    /// keyed on an environment this has no business reaching into. It is a description, never a
    /// decision, and it runs once per solve over fifteen links and a few hundred markers.
    /// </summary>
    public static string Divergence { get; private set; } = "no solve yet";

    /// <summary>
    /// Every worker's finished chain from the last press, best first, with what the site pays for it.
    ///
    /// **Seven of eight are thrown away, and whether they are eight answers or eight versions of one
    /// answer cannot be told from a number.** The count of exactly-equal chains reads 7.8 of 8 on this
    /// site, which says only that they are not identical - two routes differing in one link count as two.
    /// The question is whether they share a pattern, and that is a question for the eye: the game already
    /// draws a plan with its links, its blast circles and the markers it catches, in real terrain.
    ///
    /// So the plans are kept rather than described again, and browsing them adopts them one at a time.
    /// See AutoExpedition's chain browser and Planning.Adopt.
    /// </summary>
    public static IReadOnlyList<(double Worth, Plan Plan)> Pool => _pool;

    private static List<(double Worth, Plan Plan)> _pool = new();

    /// <summary>
    /// What every worker opened with, and how far the good ones agree, for the dump.
    ///
    /// **"How many bombs is an opening" is a measurement, not a judgement.** It is the number of leading
    /// links the high-scoring chains agree on: where they part company is where the opening ends and the
    /// search begins. A regular site is expected to settle that at one or two links and a Grand at three
    /// or four, and nothing here has ever counted it.
    ///
    /// **And it decides where the diversity belongs.** If the best chains share an opening, then the seven
    /// workers that open from a randomised greedy draw are mostly starting wrongly and spending the window
    /// recovering - and enumerating the opening once, for everyone, would cost no diversity at all,
    /// because the differences are in the tail. If instead the best chains open differently from each
    /// other, the opposite holds and the openings are exactly what must not be shared.
    ///
    /// Agreement is positional and within a grid unit: link three of one chain against link three of
    /// another, since two chains that visit the same spot at different points in the route are not sharing
    /// an opening.
    /// </summary>
    public static string Openings { get; private set; } = "no solve yet";

    private static void Opened(PlanEnvironment env, Plan[] answers)
    {
        var ranked = new List<(double Worth, IReadOnlyList<Vector2> Chain)>();

        foreach (var plan in answers)
        {
            if (plan is { Points.Count: > 0 })
                ranked.Add((Planner.Plainly(env, plan.Points), plan.Points));
        }

        if (ranked.Count == 0)
        {
            Openings = "no chains";

            return;
        }

        ranked.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        var text = new StringBuilder(512);

        // **The opening most workers take, and whether the WINNER takes it.**
        //
        // This asked whether the better half agreed unanimously, and one dissenter answered nought - on a
        // press where six of eight workers opened on the same spot and the dissenter was the winner. That
        // is the most informative arrangement there is and the metric hid it. A plurality with the winner
        // outside it says the greedy opening is a trap most of the pool falls into, which is a different
        // problem from the pool disagreeing.
        var counts = new Dictionary<string, int>();

        foreach (var (_, chain) in ranked)
        {
            var key = $"{(int)MathF.Round(chain[0].X)},{(int)MathF.Round(chain[0].Y)}";

            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var modal = "";
        var most = 0;

        foreach (var (key, count) in counts)
        {
            if (count <= most)
                continue;

            most = count;
            modal = key;
        }

        // How far the workers sharing that first link stay together, which is the opening's real depth.
        var agreed = 0;

        for (var i = 0; i < ranked[0].Chain.Count; i++)
        {
            var seen = new Dictionary<string, int>();

            foreach (var (_, chain) in ranked)
            {
                if (i >= chain.Count)
                    continue;

                var key = $"{(int)MathF.Round(chain[i].X)},{(int)MathF.Round(chain[i].Y)}";

                seen[key] = seen.GetValueOrDefault(key) + 1;
            }

            var top = 0;

            foreach (var count in seen.Values)
                top = Math.Max(top, count);

            // A plurality rather than a majority: what matters is whether MANY workers walk the same
            // opening, not whether more than half do.
            if (top < Math.Max(2, ranked.Count / 3))
                break;

            agreed++;
        }

        var winnerToo = $"{(int)MathF.Round(ranked[0].Chain[0].X)}," +
                        $"{(int)MathF.Round(ranked[0].Chain[0].Y)}" == modal;

        text.AppendLine($"  {most} of {ranked.Count} workers open at ({modal}), and the plurality stays " +
                        $"together for {agreed} link(s)");

        text.AppendLine(winnerToo
            ? "  the winner is one of them - the crowd's opening is the good one"
            : "  **the winner is NOT one of them** - the crowd's opening is a trap, and most of the pool " +
              "is in it");

        // Every worker's first few links, so the shape of the agreement can be seen rather than trusted.
        // Four, because that is the most an opening is expected to be.
        for (var w = 0; w < ranked.Count; w++)
        {
            var said = new List<string>();

            for (var i = 0; i < ranked[w].Chain.Count && i < 4; i++)
                said.Add($"({ranked[w].Chain[i].X:0},{ranked[w].Chain[i].Y:0})");

            text.AppendLine($"    {ranked[w].Worth,8:N0}  {string.Join(" ", said)}");
        }

        Openings = text.ToString().TrimEnd();
    }

    /// <summary>What the relinking did, for the dump. See Repair.Relinked.</summary>
    public static string Relinking { get; private set; } = "no solve yet";

    /// <summary>
    /// What reversing the relinked chain has added since the plugin loaded: how many relinked chains were
    /// reversed, how many came out higher, and by how much in all. See Across, where it runs.
    /// </summary>
    public static string RelinkReversal =>
        $"{System.Threading.Volatile.Read(ref _relinkReversalsThatHelped):N0} of {System.Threading.Volatile.Read(ref _relinkReversals):N0} relinked " +
        $"chain(s) improved, {_relinkReversalGain:N0} in all";

    private static int _relinkReversals;
    private static int _relinkReversalsThatHelped;
    private static double _relinkReversalGain;

    private static void Diverged(PlanEnvironment env, Plan[] answers, Plan best)
    {
        if (env?.Targets == null || best is not { Points.Count: > 0 } || answers == null)
        {
            Divergence = "nothing to compare";

            return;
        }

        // The median worker by what the site pays, which is the figure the pool's spread is read in.
        var ranked = new List<(double Plain, Plan Plan)>();

        foreach (var plan in answers)
        {
            if (plan is { Points.Count: > 0 })
                ranked.Add((Planner.Plainly(env, plan.Points), plan));
        }

        if (ranked.Count < 2)
        {
            Divergence = "one worker answered, so there is no median to compare";

            return;
        }

        ranked.Sort((a, b) => a.Plain.CompareTo(b.Plain));

        var mid = ranked[ranked.Count / 2].Plan;
        var won = best.Points;
        var lost = mid.Points;

        var text = new StringBuilder(512);

        text.AppendLine($"  the winner scores {Planner.Plainly(env, won):N0}, " +
                        $"the median worker {ranked[ranked.Count / 2].Plain:N0}");

        // **Where the two routes part company, which is the first thing to know.** Two chains sharing
        // a prefix are one search that ended differently; two that diverge at link one are two searches.
        var shared = 0;

        for (var i = 0; i < won.Count && i < lost.Count; i++)
        {
            if (Vector2.Distance(won[i], lost[i]) >= 1f)
                break;

            shared++;
        }

        var common = 0;

        foreach (var at in won)
        {
            foreach (var other in lost)
            {
                if (Vector2.Distance(at, other) < 1f)
                {
                    common++;

                    break;
                }
            }
        }

        text.AppendLine($"  links: {shared} shared as a prefix, {common} of {won.Count} in common " +
                        $"anywhere, first difference at link {shared + 1}");

        text.AppendLine($"    winner {Spelled(won)}");
        text.AppendLine($"    median {Spelled(lost)}");

        // What each one takes that the other does not, which is the answer to "stuck on what".
        var onlyWon = new List<(double Weight, string What)>();
        var onlyLost = new List<(double Weight, string What)>();

        foreach (var target in env.Targets)
        {
            var byWinner = Catches(won, env, target);
            var byMedian = Catches(lost, env, target);

            if (byWinner == byMedian)
                continue;

            var said = $"{target.Kind} ({target.Grid.X:0},{target.Grid.Y:0}) worth {target.Weight:N0}";

            (byWinner ? onlyWon : onlyLost).Add((target.Weight, said));
        }

        onlyWon.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        onlyLost.Sort((a, b) => b.Weight.CompareTo(a.Weight));

        text.AppendLine($"  {Worth(onlyWon)} the winner takes and the median does not: {Few(onlyWon)}");
        text.AppendLine($"  {Worth(onlyLost)} the median takes and the winner does not: {Few(onlyLost)}");

        Divergence = text.ToString().TrimEnd();
    }

    /// <summary>Whether this chain catches the marker, by the same edge rule the planner uses.</summary>
    private static bool Catches(IReadOnlyList<Vector2> chain, PlanEnvironment env, PlanTarget target)
    {
        var reach = env.Blast + target.Radius;

        for (var i = 0; i < chain.Count; i++)
        {
            if (Vector2.DistanceSquared(chain[i], target.Grid) <= reach * reach)
                return true;
        }

        return false;
    }

    private static string Spelled(IReadOnlyList<Vector2> chain)
    {
        var said = new List<string>(chain.Count);

        foreach (var at in chain)
            said.Add($"({at.X:0},{at.Y:0})");

        return string.Join(" ", said);
    }

    private static string Worth(List<(double Weight, string What)> these)
    {
        var total = 0d;

        foreach (var (weight, _) in these)
            total += weight;

        return $"{these.Count} marker(s) worth {total:N0}";
    }

    /// <summary>The heaviest few, because a list of two hundred markers says nothing.</summary>
    private static string Few(List<(double Weight, string What)> these)
    {
        if (these.Count == 0)
            return "none";

        var said = new List<string>();

        for (var i = 0; i < these.Count && i < 6; i++)
            said.Add(these[i].What);

        return string.Join("; ", said) + (these.Count > 6 ? $"; and {these.Count - 6} more" : "");
    }

    /// <summary>
    /// Files what this press produced, so a change to the search is judged on a distribution.
    ///
    /// **The pool's figures in the site's own units, not the search's.** A held must-take adds a
    /// ceiling on the whole site to every chain that holds it, so totals differ by a few per cent
    /// where the content they buy differs by a third - see Solving._sharedPlain. The per-worker plain
    /// is already published by Scored, which is where these come from.
    /// </summary>
    /// <summary>
    /// How many of the workers' chains are actually different, by the spots they use in the order they
    /// use them.
    ///
    /// **Strict equality, not similarity, because the question is how many threads were wasted.** Two
    /// chains that agree on every rounded link are one answer found twice however they got there. A
    /// looser measure - links in common, or a distance - is the right tool for deciding whether to
    /// DIVERT a worker and the wrong one for counting what a press produced.
    /// </summary>
    private static int Distinct(Plan[] answers)
    {
        var seen = new HashSet<string>();

        foreach (var plan in answers)
        {
            if (plan is not { Points.Count: > 0 })
                continue;

            var said = new StringBuilder(plan.Points.Count * 10);

            foreach (var at in plan.Points)
            {
                said.Append((int)MathF.Round(at.X)).Append(',')
                    .Append((int)MathF.Round(at.Y)).Append(';');
            }

            seen.Add(said.ToString());
        }

        return seen.Count;
    }

    private static void Recorded(PlanEnvironment env, Plan best, double top, int threads, double ms,
        double relinked = 0d, Plan[] answers = null)
    {
        var plains = new List<double>(threads);
        var rounds = 0L;
        var bests = 0;
        var zones = new int[4];
        var winner = -1;
        var highest = double.NegativeInfinity;
        var partway = 0d;

        // What each slot scored, in slot order, so a batch can say whether it is the same workers that are
        // unproductive every press. Sorted readings cannot. See PressHistory.Press.ByWorker.
        var slotted = new double[threads];

        lock (_gate)
        {
            for (var i = 0; i < threads; i++)
                slotted[i] = i < _plain.Length ? _plain[i] : double.NaN;

            for (var i = 0; i < threads; i++)
            {
                if (i < _plain.Length && double.IsFinite(_plain[i]))
                    plains.Add(_plain[i]);

                if (i < _best.Length && _best[i] > highest)
                {
                    highest = _best[i];
                    winner = i;
                }

                if (i < _rounds.Length)
                    rounds += _rounds[i];

                if (i < _bests.Length)
                    bests += _bests[i];

                if (i < _halfway.Length && _halfway[i] > partway)
                    partway = _halfway[i];

                if (i < _zones.Length && _zones[i] is { Length: 4 } was)
                {
                    for (var z = 0; z < 4; z++)
                        zones[z] += was[z];
                }
            }
        }

        if (plains.Count == 0)
            return;

        plains.Sort();

        PressHistory.Add(new PressHistory.Press(
            DateTime.UtcNow,
            best is { Points.Count: > 0 } ? best.Points[0] : Vector2.Zero,
            best?.Points?.Count ?? 0,
            best is { Points.Count: > 0 } ? Planner.Plainly(env, best.Points) : 0d,
            top,
            plains[^1],
            plains[plains.Count / 2],
            plains[0],
            threads,
            rounds,
            bests,
            winner,
            zones,
            ms,
            env?.Draw ?? 0,
            relinked,
            plains.ToArray(),
            answers == null ? 0 : Distinct(answers),
            partway,
            _pool.ToArray(),
            slotted));
    }

    /// <param name="worker">
    /// The search itself, given its worker number - which is the seed every mode varies its random
    /// stream by - and the callback to publish a better chain through.
    /// </param>
    public static Plan Across(PlanEnvironment env, int threads, Action<List<Vector2>> found,
        Func<int, Action<List<Vector2>>, Plan> worker, string engine = "search")
    {
        threads = Math.Max(1, threads);

        var slots = threads;

        _said = new string[slots];
        _best = new double[slots];
        _plain = new double[slots];
        _lean = new string[slots];

        lock (_gate)
        {
            _rounds = new long[slots];
            _bests = new int[slots];
            _zones = new int[slots][];
            _halfway = new double[slots];
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < slots; i++)
        {
            _best[i] = double.NegativeInfinity;
            _plain[i] = double.NegativeInfinity;
        }

        // A run is judged on its own, and an incumbent carried in from the last one would be a floor
        // nobody asked for.
        lock (_share)
        {
            _shared = null;
            _sharedWorth = double.NegativeInfinity;
            _sharedPlain = 0d;
            _rescues = 0;
            _carriedWorth = double.NaN;
            _foundWorth = double.NegativeInfinity;
            _foundPlain = 0d;
        }

        PoolImproved();

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

        // The best of this solve, which is what the pool's improvement window watches.
        var solveBest = double.NegativeInfinity;

        void Publish(List<Vector2> chain)
        {
            if (chain is not { Count: > 0 })
                return;

            var worth = Planner.Score(env, chain);

            lock (gate)
            {
                if (worth > solveBest)
                {
                    solveBest = worth;
                    PoolImproved();
                }

                if (worth <= shown)
                    return;

                shown = worth;

                found?.Invoke(new List<Vector2>(chain));
            }

            // And kept as the pool's best, for Behind and Leader. The same copy the overlay draws;
            // publishing is already the moment a chain became worth telling anybody about.
            lock (_share)
            {
                // The carried plan, scored again or improved by the worker continuing from it, is not something this
                // solve found. Only the score was left out at first, and the continuing worker's first small gain
                // on it - 16,366 to 16,386 in one pass - became the yardstick again, and four workers were restarted
                // four to six times each. See Behind.
                if (worth > _foundWorth && worth != _carriedWorth && !PublishingFromCarried)
                {
                    _foundWorth = worth;
                    _foundPlain = Planner.Plainly(env, chain);
                }

                if (worth <= _sharedWorth)
                    return;

                _sharedWorth = worth;
                _sharedPlain = Planner.Plainly(env, chain);
                _shared = new List<Vector2>(chain);
            }
        }

        var answers = new Plan[threads];

        var tasks = new Task[threads];

        PoolImproved();

        for (var i = 0; i < threads; i++)
        {
            var n = i;

            // Recorded on the worker's own thread, not the one that starts it. A search fans out
            // over these and the thread above them does almost nothing, so measuring that one
            // reports a search as very nearly free. See BackgroundWork.
            // **A thread of its own at low priority, not a pool thread.** See BackgroundWork.StartAtLowPriority.
            // A worker is CPU-bound for the whole window, and held
            // on the pool it kept a pool thread busy the whole time - eight a solve, more while a superseded
            // solve winds down - so anything else in the process that queued pool work waited for the pool to
            // grow, which it does slowly. Measured on a cold Grand site: gaps of 0.5 to 1.5 seconds between
            // frames with 8 to 14 workers running, up to 12 items queued, and no stage of this plugin's own
            // over about 100ms inside them.
            tasks[i] = BackgroundWork.StartAtLowPriority(() =>
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

        // **The seven discarded chains, put to use.** See Repair.Relinked - this runs after every
        // worker has stopped, so it costs the search nothing, and it is the only thing here that can
        // produce a chain no single worker found.
        var pool = new List<List<Vector2>>();

        foreach (var plan in answers)
        {
            if (plan is { Points.Count: > 0 })
                pool.Add(new List<Vector2>(plan.Points));
        }

        var relinked = Repair.Relinked(env, pool, out var told);

        // **Reversed afterwards, because relinking's chain is the one whose order nobody has optimised.** Each
        // worker's answer went through reversal as it stopped; a relinked chain is spliced from two of them, and
        // the order around the splice is new. Kept only if it scores higher, so it cannot lower a press. Not
        // measured when written; RelinkReversal counts what it adds.
        if (relinked is { Count: > 0 })
        {
            var before = Planner.Score(env, relinked);
            var turned = Repair.Reversed(env, relinked, env.Placed?.Count ?? 0);
            var after = Planner.Score(env, turned);

            System.Threading.Interlocked.Increment(ref _relinkReversals);

            if (after > before)
            {
                System.Threading.Interlocked.Increment(ref _relinkReversalsThatHelped);

                lock (_share)
                    _relinkReversalGain += after - before;

                told += $"; reversed afterwards, +{after - before:N0}";
                relinked = turned;
            }
            else
                told += "; reversed afterwards, no gain";
        }

        Relinking = told;

        var gained = 0d;

        if (relinked is { Count: > 0 })
        {
            var was = Planner.Plainly(env, best?.Points ?? new List<Vector2>());

            best = Planner.Describe(env, relinked);
            top = Planner.Score(env, relinked);
            gained = Planner.Plainly(env, relinked) - was;
        }

        // Kept for the browser, ranked, so pressing the key walks from the answer downwards.
        var keep = new List<(double Worth, Plan Plan)>();

        foreach (var plan in answers)
        {
            if (plan is { Points.Count: > 0 })
                keep.Add((Planner.Plainly(env, plan.Points), plan));
        }

        keep.Sort((a, b) => b.Worth.CompareTo(a.Worth));
        _pool = keep;

        Recorded(env, best, top, threads, clock.Elapsed.TotalMilliseconds, gained, answers);
        Diverged(env, answers, best);
        Opened(env, answers);

        return best ?? Plan.Empty;
    }
}
