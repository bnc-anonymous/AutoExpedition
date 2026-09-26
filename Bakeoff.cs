using ExileCore2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Every strategy on one site, one after another, with the best kept.
///
/// **A dig site is used once and never seen again.** It is generated with the map and gone when the
/// map is, so a strategy cannot be re-tested on a site that suited it, and two runs on different
/// sites say nothing about each other. The only sound comparison is all of them on the same site in
/// the same visit - which is tedious by hand, was done by hand twice, and both times something moved
/// underneath it that only showed up afterwards.
///
/// So this runs the lot. Each gets the window it would get alone, its answer is drawn as it lands,
/// every one is written to trials.csv, and the plan left on screen at the end is the best of them.
/// </summary>
internal sealed class Bakeoff
{
    /// <summary>
    /// The strategies this run will try, in order - whichever of SolverSettings.Comparable are
    /// ticked when it starts.
    ///
    /// Fixed at Begin rather than read per step, so a tick changed halfway through a run cannot
    /// leave the tally describing one set of searches and the scores describing another.
    /// </summary>
    private readonly List<string> _order = new();

    private readonly List<(string Name, double Score, Plan Plan, string Threads, int Used, int Many)>
        _done = new();
    private int _next;

    /// <summary>Whether a run is in progress, so the tick knows to drive it.</summary>
    public bool Running { get; private set; }

    /// <summary>
    /// The same fact, reachable without the instance, so a solve can tell it is being compared.
    ///
    /// **A comparison must not hand each strategy the last one's answer.** Every solve files its
    /// chain as the site's best and every solve takes that file as its floor, so run four in a row
    /// and each one starts at whatever its predecessors reached - which makes the column a ratchet
    /// rather than four answers. Measured: Edge only 28,988.4 then Whole site 30,078.5 then Beam and
    /// Mixed reporting 30,078.5 to the decimal, which is not three searches agreeing, it is two of
    /// them handing back the third's chain. See Planning.Start, which skips the floor while this is
    /// set.
    /// </summary>
    public static bool Comparing { get; private set; }

    /// <summary>
    /// One line per strategy, reachable by the overlay without holding the instance.
    ///
    /// Lines rather than one long string: four scores run together are read as a sentence and
    /// compared by squinting, where a column is compared at a glance - and comparing them is the
    /// only reason this exists.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Best)> Showing { get; private set; } =
        new List<(string, bool)>();

    /// <summary>Whether there is anything to compare strategies on. See Begin.</summary>
    public static bool Possible(int explosives) => explosives > 0;

    /// <summary>Puts one line in the tally slot, for when there is nothing to run.</summary>
    public static void Say(string what) =>
        Showing = new List<(string, bool)> { (what, false) };

    /// <returns>False when nothing is ticked, so the caller can say so and do nothing.</returns>
    public bool Begin(AutoExpeditionSettings settings)
    {
        _order.Clear();

        foreach (var name in SolverSettings.Comparable)
        {
            if (settings?.Solver == null || settings.Solver.Compares(name))
                _order.Add(name);
        }

        if (_order.Count == 0)
        {
            Say("no searches are ticked to compare - see Solver, Compared by the cold key");

            return false;
        }

        _done.Clear();
        _next = 0;
        Running = true;
        Comparing = true;

        Showing = new List<(string, bool)>
        {
            ($"trying {_order.Count} search{(_order.Count == 1 ? "" : "es")}...", false),
        };

        return true;
    }

    /// <summary>
    /// Drives the run: collects the last answer, starts the next, and finishes by adopting the best.
    /// </summary>
    public void Tick(AutoExpedition plugin, Planning planning, Action<string> start)
    {
        if (!Running || planning.Searching)
            return;


        // The answer to whatever was started last time round.
        if (_next > 0 && _next <= _order.Count)
        {
            var name = _order[_next - 1];

            var used = 0;
            var many = 0;

            _done.Add((name, planning.Plan?.Weight ?? 0d, planning.Plan, Solving.Spread, used, many));
            Retell();
        }

        if (_next >= _order.Count)
        {
            Finish(planning);

            return;
        }

        start(_order[_next++]);
    }

    /// <summary>Keeps the best answer on screen and says which it was.</summary>
    private void Finish(Planning planning)
    {
        Running = false;
        Comparing = false;

        var best = ("", double.NegativeInfinity, (Plan)null);

        foreach (var (name, score, plan, _, _, _) in _done)
            if (plan is { Points.Count: > 0 } && score > best.Item2)
                best = (name, score, plan);

        if (best.Item3 != null)
            planning.Adopt(best.Item3);

        Retell();


        // Nothing is added to say which won: the colour says it, and a line that only names the
        // winner is a line that repeats what is already on screen.
    }


    /// <summary>
    /// The tally as it stands, with the leader marked.
    ///
    /// Marked as it goes rather than only at the end, so the column always says which is winning -
    /// which is the question being asked while it runs, not just after.
    /// </summary>
    private void Retell()
    {
        var top = double.NegativeInfinity;

        foreach (var (_, score, plan, _, _, _) in _done)
            if (plan is { Points.Count: > 0 } && score > top)
                top = score;

        var lines = new List<(string, bool)>();
        var detail = new List<string>();

        foreach (var (name, score, plan, threads, _, _) in _done)
        {
            var made = plan is { Points.Count: > 0 };

            // **Shown in plain, ranked on the search's number, and those are different columns.**
            // The total carries the insistence - a marker marked by hand, or one clearing the
            // reward threshold - and on the site this was written for that was 25,986 of a 30,078
            // row, identical on every strategy, burying the 1,090 points that actually separated
            // them. Ranking still uses the total, because a chain that takes a required marker
            // genuinely does beat one that does not; the number beside it is what the site pays.
            var said = made
                ? $"{plan.Plain:N1}" + (plan.Missed > 0 ? $" (misses {plan.Missed})" : "")
                : "nothing to place";

            lines.Add(($"{name}: {said}", made && Math.Abs(score - top) < 0.0001d));

            // **The per-worker detail goes to the dump, not to the screen.**
            //
            // It was put in the tally so that eight workers agreeing to the decimal could be seen,
            // which was the right thing to want and the wrong place to put it: each row runs to two
            // hundred characters of rounds, kicks, reach counts and milliseconds, so a three mode
            // comparison filled the screen with text nobody can read at a glance and buried the
            // three numbers the key was pressed for.
            //
            // The tally answers "which mode won and by how much". Everything about HOW is a
            // question somebody asks afterwards, sitting still, reading. See Threads.
            if (!string.IsNullOrEmpty(threads) && threads != "single threaded")
                detail.Add($"{name} - {threads}");
        }

        Showing = lines;
        Threads = detail.Count > 0 ? string.Join("; ", detail) : "single threaded";
    }

    /// <summary>
    /// What every worker of every compared strategy did, for the dump. See Retell.
    ///
    /// Kept per strategy rather than read from Solving.Spread, which is a static the last search to
    /// finish overwrites - so a six row comparison reported one row's workers six times.
    /// </summary>
    public static string Threads { get; private set; } = "nothing compared yet";
}
