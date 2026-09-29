using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// What every solve on this site has been worth, so a change to the search is judged on a
/// distribution rather than on the last press.
///
/// **One press is not a measurement, and this session proved it four times.** Four explanations for
/// the pool collapsing onto one chain were each supported by a single dump and each turned out to be
/// wrong - the shake count, thread sharing, the opening's descent, and the shake itself. The figures
/// that would have separated them were there all along; nothing kept them side by side.
///
/// So each press records what it produced and what the pool looked like producing it, and the dump
/// prints the spread with the share of presses that met the target. A configuration is better than
/// another when its distribution is, which is a claim that needs several presses and cannot be read
/// off one.
///
/// Cleared on an area change, like every other reading: a different site is a different problem and
/// mixing two of them hides both.
/// </summary>
internal static class PressHistory
{
    /// <summary>What one solve produced, and what the pool looked like producing it.</summary>
    /// <param name="Plain">What the site pays for the chain that was kept - the figure on screen.</param>
    /// <param name="Total">The same chain as the search scored it, insistence included.</param>
    /// <param name="PoolBest">The best worker's site value, which is normally the kept chain's.</param>
    /// <param name="PoolMid">The median worker's, which says whether the pool is a pool.</param>
    /// <param name="PoolWorst">The worst worker's, which says what the weakest thread contributed.</param>
    /// <param name="Winner">Which worker's chain was kept.</param>
    /// <param name="ZoneWon">
    /// Records that were set by a tear in the first third, the middle, the last third, and by the
    /// reach operator, which chooses its own cut. See Repair's zone counters.
    /// </param>
    /// <param name="Draw">
    /// Which draw of the random numbers this press was. Batches walk 1..N, so two batches line up press
    /// for press and a difference between them is the configuration rather than the dice.
    /// </param>
    internal readonly record struct Press(
        DateTime When, Vector2 Site, int Links, double Plain, double Total,
        double PoolBest, double PoolMid, double PoolWorst, int Workers,
        long Rounds, int Bests, int Winner, int[] ZoneWon, double Ms, int Draw = 0,
        double Relinked = 0d, double[] Pool = null, int Distinct = 0, double Partway = 0d,
        (double Worth, Plan Plan)[] Chains = null,

        /// <summary>
        /// What each worker slot scored this press, in slot order rather than sorted.
        ///
        /// **Because every other reading here loses which worker did it.** Chains and Pool are sorted best
        /// first, so a batch could say four of eight workers were unproductive and not say whether it was the
        /// same four each press. A worker slot carries an opening, a tearing bias and an independence flag, so
        /// if the rate differs by slot then the configuration is worth something and reallocating workers onto
        /// the productive ones is the change to make; if it is flat, the configuration is noise. See
        /// expedition_solve_plan.md 7.15 step 0, which exists to decide exactly that. Indexed by slot, which is
        /// the worker's stream.
        /// </summary>
        double[] ByWorker = null);

    /// <summary>
    /// Every chain every press of this batch produced, with the draw it came from, best first.
    ///
    /// **Because a batch's interesting comparison is across draws and the window could only show one.**
    /// Solving.Pool holds the last press, so after ten presses it described the tenth - which is the one
    /// press nobody has a question about. Eighty chains at fifteen links apiece is nothing to hold, and it
    /// lets the same window answer "which draw found the good route, and what did the others do instead".
    /// </summary>
    public static List<(int Draw, double Worth, Plan Plan)> Every()
    {
        var all = new List<(int Draw, double Worth, Plan Plan)>();

        lock (_gate)
        {
            foreach (var press in _presses)
            {
                foreach (var (worth, plan) in press.Chains ?? [])
                    all.Add((press.Draw, worth, plan));
            }
        }

        all.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        return all;
    }

    /// <summary>
    /// The target a press is measured against, as the site pays it.
    ///
    /// Ten thousand, because that is the figure this site was asked to reach routinely in one eight
    /// second window - the goal the accumulator was built to report against rather than a property of
    /// the solver. A site that cannot pay it will read nought per cent here and that is information
    /// about the site, not a fault.
    /// </summary>
    public const double Target = 10_000d;

    private static readonly List<Press> _presses = new();

    private static readonly object _gate = new();

    /// <summary>How many presses are kept. Beyond this the oldest goes.</summary>
    private const int Most = 40;

    public static void Add(Press press)
    {
        lock (_gate)
        {
            _presses.Add(press);

            if (_presses.Count > Most)
                _presses.RemoveAt(0);
        }
    }

    /// <summary>Emptied on an area change, because a different site is a different problem.</summary>
    public static void Forget()
    {
        lock (_gate)
            _presses.Clear();
    }

    public static int Count
    {
        get
        {
            lock (_gate)
                return _presses.Count;
        }
    }

    /// <summary>
    /// The distribution, then one line per press, newest last.
    ///
    /// **The summary first, because the per-press lines are what it is computed from and not the
    /// answer.** A reader comparing two configurations wants the median and the share that met the
    /// target; the rows are there to show whether one press is carrying the median.
    /// </summary>
    public static string Spelled()
    {
        List<Press> had;

        lock (_gate)
            had = new List<Press>(_presses);

        if (had.Count == 0)
            return "  no solve has finished here yet";

        var text = new StringBuilder();
        var plains = new List<double>(had.Count);

        foreach (var press in had)
            plains.Add(press.Plain);

        plains.Sort();

        var met = 0;

        foreach (var plain in plains)
        {
            if (plain >= Target)
                met++;
        }

        var median = plains.Count % 2 == 1
            ? plains[plains.Count / 2]
            : (plains[plains.Count / 2 - 1] + plains[plains.Count / 2]) / 2d;

        text.AppendLine($"  {had.Count} press(es): median {median:N0}, " +
                        $"best {plains[^1]:N0}, worst {plains[0]:N0}, " +
                        $"spread {plains[^1] - plains[0]:N0}; " +
                        $"{met} of {had.Count} reached {Target:N0} " +
                        $"({100d * met / had.Count:0.#}%)");

        // **The pool's own spread averaged over the presses, because that is what a change to the
        // search is aimed at.** A pool whose median sits close to its best is one where the extra
        // threads are contributing; one whose median sits near its worst is a single search with
        // company. Either can produce a good press by luck, and only this tells them apart.
        var gap = 0d;
        var bestOfPool = 0d;

        foreach (var press in had)
        {
            gap += press.PoolBest - press.PoolMid;
            bestOfPool += press.PoolBest;
        }

        text.AppendLine($"  the pool, averaged: best {bestOfPool / had.Count:N0}, " +
                        $"best-to-median gap {gap / had.Count:N0}");

        // **How long the presses actually took, because the window is a settle and not a clock.**
        // SettleMs is a no-improvement timeout, so with no solve cap a press runs until it stops
        // improving - measured between 8 and 13 seconds on one site. A configuration that raises the
        // median by taking half again as long has not done what it looks like it has done, and this is
        // the line that says so.
        var runs = new List<double>(had.Count);

        foreach (var press in had)
            runs.Add(press.Ms);

        runs.Sort();

        var spent = 0d;

        foreach (var ms in runs)
            spent += ms;

        text.AppendLine($"  time per press: median {runs[runs.Count / 2] / 1000d:0.#}s, " +
                        $"range {runs[0] / 1000d:0.#}-{runs[^1] / 1000d:0.#}s, " +
                        $"mean {spent / runs.Count / 1000d:0.#}s");

        var gained = 0d;
        var gainedOn = 0;

        foreach (var press in had)
        {
            if (press.Relinked <= 0d)
                continue;

            gained += press.Relinked;
            gainedOn++;
        }

        text.AppendLine($"  relinking: better on {gainedOn} of {had.Count} press(es), " +
                        $"{gained:N0} points in total");

        // **The upper tail of the WORKER distribution, which is the right thing to read and was not
        // being read.**
        //
        // A press scores the maximum of its workers, so the press figures above are one observation of
        // a max apiece - ten of them in a batch, and noisy enough that a change worth a few hundred
        // points cannot be told from a coin. The same batch holds EIGHTY worker outcomes, and what
        // "routinely over the target" asks is really what fraction of workers clear it: at a fraction p
        // a press succeeds with probability 1-(1-p)^8. Estimating p from eighty samples is about three
        // times less noisy than counting successes among ten.
        //
        // **And it is the measure that does not punish exploration.** A pool where most workers go
        // looking and score badly has a low median by design; what matters is whether the tail got
        // fatter. Judging on the median would push the pool back towards eight threads polishing one
        // chain, which is the failure this whole section started from.
        var workers = new List<double>();
        var distinct = 0d;

        foreach (var press in had)
        {
            distinct += press.Distinct;

            foreach (var worth in press.Pool ?? [])
                workers.Add(worth);
        }

        if (workers.Count > 0)
        {
            workers.Sort();

            var over = 0;

            foreach (var worth in workers)
            {
                if (worth >= Target)
                    over++;
            }

            var p = (double)over / workers.Count;

            text.AppendLine($"  workers: {workers.Count} sample(s), " +
                            $"{over} over {Target:N0} ({100d * p:0.#}%), " +
                            $"p90 {workers[(int)(workers.Count * 0.9)]:N0}, " +
                            $"p75 {workers[(int)(workers.Count * 0.75)]:N0}, " +
                            $"median {workers[workers.Count / 2]:N0}");

            // What that rate implies for a press, which is the number the target is stated in - and it
            // is a prediction from the workers rather than a count of presses, so it moves on eighty
            // samples instead of ten.
            text.AppendLine($"  a press of {had[^1].Workers} workers at that rate clears the target " +
                            $"{100d * (1d - Math.Pow(1d - p, Math.Max(1, had[^1].Workers))):0.#}% of " +
                            $"the time");
        }

        // **Whether the second half of a window is where the pool climbs**, which decides between
        // halving the window and running it twice for twice the tickets, and taking the budget off the
        // workers that have stopped climbing. See Repair's Partway.
        var atHalf = 0d;
        var atEnd = 0d;
        var counted = 0;

        foreach (var press in had)
        {
            if (press.Partway <= 0d)
                continue;

            atHalf += press.Partway;
            atEnd += press.PoolBest;
            counted++;
        }

        if (counted > 0)
        {
            text.AppendLine($"  the pool's best at 4s was {atHalf / counted:N0} against " +
                            $"{atEnd / counted:N0} at the end - the second half is worth " +
                            $"{100d * (atEnd - atHalf) / Math.Max(1d, atHalf):0.#}%");
        }

        // **How many of the eight chains were actually different.** Eight workers returning three chains
        // is five threads spent re-finding an answer the pool already had, and nothing counted it.
        text.AppendLine($"  distinct chains per press: {distinct / had.Count:0.#} of " +
                        $"{had[^1].Workers}");

        text.AppendLine("   draw   when      site    total   pool best      mid    worst  by  rounds " +
                        "bests   won E/M/L/reach  relink     ms");

        foreach (var press in had)
        {
            var zones = press.ZoneWon is { Length: 4 } z
                ? $"{z[0]}/{z[1]}/{z[2]}/{z[3]}"
                : "-";

            text.AppendLine(
                $"   {press.Draw,4} " +
                $"{press.When.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} " +
                $"{press.Plain,8:N0} {press.Total,8:N0} " +
                $"{press.PoolBest,10:N0} {press.PoolMid,8:N0} {press.PoolWorst,8:N0} " +
                $"{press.Winner,3} {press.Rounds,7:N0} {press.Bests,5} " +
                $"{zones,17} {press.Relinked,7:N0} {press.Ms,6:N0}");
        }

        BySlot(text);

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// What each worker slot scored across the batch, which says whether a slot's configuration matters.
    ///
    /// Flat rates mean the openings, tearing biases and independence flags a slot carries are noise, and only
    /// decorrelation and depth are left to work on. Rates that differ mean the opposite. See Press.ByWorker.
    /// </summary>
    private static void BySlot(StringBuilder text)
    {
        var presses = new List<Press>();

        lock (_gate)
        {
            foreach (var press in _presses)
            {
                if (press.ByWorker is { Length: > 0 })
                    presses.Add(press);
            }
        }

        if (presses.Count == 0)
            return;

        var slots = 0;

        foreach (var press in presses)
            slots = Math.Max(slots, press.ByWorker.Length);

        text.AppendLine();
        text.AppendLine($"  what each worker slot scored, over {presses.Count} press(es):");
        text.AppendLine("   slot  presses   median     best    worst   >=10k  won the press");

        for (var slot = 0; slot < slots; slot++)
        {
            var scores = new List<double>();
            var won = 0;

            foreach (var press in presses)
            {
                if (slot < press.ByWorker.Length && double.IsFinite(press.ByWorker[slot]))
                    scores.Add(press.ByWorker[slot]);

                if (press.Winner == slot)
                    won++;
            }

            if (scores.Count == 0)
                continue;

            scores.Sort();

            var over = 0;

            foreach (var score in scores)
            {
                if (score >= 10000d)
                    over++;
            }

            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"   {slot,4} {scores.Count,8} {scores[scores.Count / 2],8:N0} {scores[^1],8:N0} " +
                $"{scores[0],8:N0} {over,7} {won,14}"));
        }
    }
}
