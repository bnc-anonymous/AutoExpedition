using ExileCore2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoExpedition;

/// <summary>
/// The live plan: what the game says right now, turned into a search, and the answer kept.
///
/// Split from the planner itself so the planner stays a function of numbers with no game in it -
/// which is what makes it possible to reason about, and what would make it testable without a
/// client running. Everything that reads memory happens here, on the main thread, before the search
/// starts.
///
/// The search runs off the main thread because it is allowed to take a moment and the HUD is not.
/// It is cancelled on a zone change and on being asked again, so the worst case is a discarded
/// answer rather than a stale one.
/// </summary>
internal sealed class Planning
{
    private Task<Plan> _search;
    private CancellationTokenSource _cancel;
    private uint _area;
    private string _strategy = "";
    private DateTime _began;
    private int _level;

    // Which revision of the table and of the weights the standing plan was solved under, what they
    // read the last time they were looked at, and when they last moved. See Stale.
    private int _tableAt = -1;
    private int _weighedAt = -1;
    private int _modelAt = -1;
    private int _sawTable = -1;
    private int _sawWeighed = -1;
    private int _sawModel = -1;
    private DateTime _moved = DateTime.MinValue;

    /// <summary>The chain as it stands, or an empty plan when there is none.</summary>
    public Plan Plan { get; private set; } = Plan.Empty;

    /// <summary>Which dig site the plan is for, so it is not drawn over a different one.</summary>
    public Vector2 Site { get; private set; }

    /// <summary>What happened, in one or two words, for the line above the placement button.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The same thing at length, for the debug line.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>
    /// The whole route from the detonator: the links already laid, then the links still planned.
    ///
    /// **One list, decided once, and everything else is a view of it.** The chain used to live in
    /// three places that each held a different version of the truth - the plan, which is only the
    /// remainder; a prefix snapshot taken at solve time; and the game's own placed list, whose count
    /// and positions arrive on different frames - and no two of them agreed during an undo. Every
    /// consumer therefore rebuilt "the chain we decided on" out of the other two, each with its own
    /// arithmetic and its own off-by-one, which is why the score walked downwards as explosives came
    /// back off: 5,322, then 3,954, then 1,897, then a plan that could not be placed at all.
    ///
    /// Nothing is rebuilt now. The route is written down whole when a solve finishes, Laid says how
    /// far along it the chain has got, and undo is that number going down.
    /// </summary>
    public IReadOnlyList<Vector2> Chain => _chain;

    private List<Vector2> _chain = [];

    /// <summary>
    /// How many of the chain's links are behind the plan - the cursor into Chain.
    ///
    /// The invariant this class keeps: Chain is its first Laid links followed by Plan.Points. So the
    /// plan is always the chain's own tail, the laid links are always the chain's own head, and
    /// neither half can be derived wrongly because neither half is derived at all.
    ///
    /// It moves only when a solve runs or an explosive comes back OFF the ground. Placing one
    /// changes nothing here: the link that just went down is already in the chain and already in the
    /// plan, which is what lets the drawing keep its blast circles and the readout keep counting to
    /// five.
    ///
    /// **So this is not how many explosives are down, and it has been read as though it were.** On
    /// a plan solved from the detonator it starts at nought and stays there however much of the
    /// chain is laid. What is on the ground is this plus the plan's own placed spots - see
    /// Placement.PlacedOf, and Overlay.Bombs, which composes the pair for the count under the score.
    /// </summary>
    public int Laid => _laid;

    private int _laid;

    /// <summary>
    /// The explosives on the ground when the running solve started, held until it finishes.
    ///
    /// The solve plans a tail from where the chain now stands, so this is the head that tail belongs
    /// to - and it has to be read at the moment the search is given its environment, not when the
    /// answer comes back, because by then the player may have placed another. See Start.
    /// </summary>
    private Vector2[] _behind = [];

    /// <summary>
    /// The reach the plan was worked out against.
    ///
    /// Kept so the placement run can tell a plan that has gone stale from one that is merely
    /// further along: if the next planned spot is further than this from where the chain now
    /// starts, the chain has left the plan and no amount of nudging the cursor will fix it.
    /// </summary>
    public float Reach { get; private set; }

    private DateTime _until;

    /// <summary>How many search workers are running now, across every solve including cancelled ones.</summary>
    private static int _workersRunning;

    /// <summary>
    /// The slots solver workers take before they search, one per thread the settings allow, shared by every solve.
    ///
    /// **A new solve does not wait for the one it supersedes to stop** - Start only cancels it, and a worker notices
    /// at its next check, which the must-take tour, the opening and the final ordering do not make. While scouting a
    /// presolve pass is superseded every one to six seconds, so the two overlapped constantly: measured up to 19
    /// workers at once against a setting of 8, on 16 logical processors, with the frames stalling. A worker now
    /// waits here for a slot, and gives up waiting if its own solve is cancelled first.
    ///
    /// Replaced, not resized, when the thread setting changes; workers holding the old one release it as they
    /// finish, so for one solve the two can overlap.
    /// </summary>
    private static SemaphoreSlim WorkerSlots(int threads)
    {
        lock (WorkerSlotsGate)
        {
            if (_workerSlots == null || _workerSlotCount != threads)
            {
                _workerSlots = new SemaphoreSlim(threads, threads);
                _workerSlotCount = threads;
            }

            return _workerSlots;
        }
    }

    private static readonly object WorkerSlotsGate = new();

    private static SemaphoreSlim _workerSlots;

    private static int _workerSlotCount;

    /// <summary>Solver workers running right now, across every search still running. See Spent.LongGaps.</summary>
    public static int WorkersRunning => Volatile.Read(ref _workersRunning);

    /// <summary>
    /// How many workers of an earlier search were still running when the last enumeration began.
    ///
    /// **A cancelled search does not stop at once.** Its workers notice the token at their next check, and a
    /// must-take construction can run a second or two before it gets there - so a presolve pass that replaces
    /// another can enumerate with eight workers still busy beside it. See Openings.GroundTicks.
    /// </summary>
    public static int RunningAtEnumeration { get; private set; }

    /// <summary>
    /// How much of the solve is left, in seconds, or zero when nothing is being solved.
    ///
    /// For the countdown over the placement button. A search always runs its full budget - the
    /// first answer arrives immediately and the rest of the time is spent trying to beat it - so
    /// this counts down honestly rather than jumping to zero early.
    /// </summary>
    public float Left => Searching ? MathF.Max(0f, (float)(_until - DateTime.UtcNow).TotalSeconds) : 0f;

    /// <summary>
    /// How long the countdown counts from, in seconds.
    ///
    /// The whole budget in fixed mode; the settle window otherwise, where it starts again on every
    /// improvement - so the bar refilling means the search is still finding things.
    /// </summary>
    public float Window { get; private set; } = 1f;

    /// <summary>
    /// Volatile, because two threads touch it and one of them draws.
    ///
    /// The search writes this from its own thread and the render thread reads it every frame.
    /// Without the barrier the reader is entitled to keep a stale copy, and it did: the live drawing
    /// showed the previous improvement rather than the newest one, consistently one behind. Nothing
    /// about the handover needed a lock - the reference swap is atomic and the list behind it is
    /// never touched again once published - but "atomic" is not the same as "visible".
    /// </summary>
    /// <summary>The environment the running search was given, so a chain can still be described if
    /// the search is stopped part way.</summary>
    private PlanEnvironment _env;

    /// <summary>The environment the plan on screen was solved against, so it can be taken apart.</summary>
    public PlanEnvironment Env => _env;

    /// <summary>What became of the last solve's chain when this one started. See Start.</summary>
    public static string Seeded { get; private set; } = "has not run";

    /// <summary>
    /// Whether the search running now is a rehearsal rather than something the player asked for.
    ///
    /// The overlay reads this to stay quiet: a presolve that drew a chain, moved the score and put
    /// "Solving" over the detonator would be the plugin answering a question nobody asked, halfway
    /// across the map. See Rehearsal.
    /// </summary>
    public static bool Rehearsing { get; private set; }

    /// <summary>
    /// Whether the solve running is a pass the continuous reroll mode asked for, which runs until a roll or the
    /// action key rather than to a countdown. The score area says so instead of showing the pass's clock. See
    /// Rehearsal.Continuing.
    /// </summary>
    public static bool RunningContinuous { get; private set; }

    /// <summary>
    /// When a solve last published a chain better than the one drawn, in UTC, or MinValue before any has. Read by
    /// the score area in the continuous reroll mode, where the time since the last gain is what says whether the
    /// search is still finding anything. A solve starts from the standing plan as its floor, so only a real gain
    /// moves it; after a roll the first chain against the changed site does.
    /// </summary>
    public static DateTime LastGainAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// The dig site the player has actually asked for a plan at, or nothing.
    ///
    /// **Per site, not per area, and a map holds two.** As a plain flag this was set by the first
    /// press and stayed set, so walking on to the second expedition with a rehearsal running drew
    /// its chain and its blast circles across ground the player had not asked about - the plugin
    /// answering the next question before it was put, which is the one thing the presolve is not
    /// supposed to do.
    /// </summary>
    public static Vector2 ShownAt { get; private set; }

    /// <summary>Whether a plan has been asked for at this dig site. See ShownAt.</summary>
    public static bool Showing(Vector2 site) =>
        site != Vector2.Zero && Vector2.Distance(ShownAt, site) < 1f;

    /// <summary>
    /// Says the player has asked to see this site's plan, without starting a search for it.
    ///
    /// **Stopping a rehearsal is asking to see it, and only starting a search recorded the ask.**
    /// A presolve runs while you walk in and draws nothing; the press that stops it goes through
    /// Stop rather than Start, so ShownAt was never set and the chain, its blast circles and the
    /// numbered links stayed hidden after the very press that was meant to reveal them. The score
    /// moved, which made it look as though the plan had been lost rather than withheld.
    /// </summary>
    public static void Show(Vector2 site)
    {
        if (site != Vector2.Zero)
            ShownAt = site;
    }

    /// <summary>What became of the best chain on file when this solve started. See Kept.</summary>
    public static string Filed { get; private set; } = "has not run";

    private string _areaName = "";
    private Vector2 _areaSize;
    private uint _areaHash;

    private volatile List<Vector2> _live;
    private int _found;

    /// <summary>
    /// When each improvement reached the screen, and what it was worth. See Better.
    ///
    /// **A pass's length says nothing about how it was spent.** "12,101ms, 4 improvements" is the
    /// same line whether the search found everything in the first second and then proved it for
    /// eleven, or crawled up the whole way - and those want opposite responses. Every entry here is
    /// a moment the improvement window was reset, so the gaps between them are what the settle
    /// setting is actually being spent on.
    ///
    /// Bounded, since a long pass on a Grand site can publish a great many. The last ones are the
    /// interesting ones - the early climb is expected, the question is what the tail bought.
    /// </summary>
    /// <summary>
    /// One moment the improvement window was reset, and everything true of the plan at it.
    ///
    /// A named shape rather than a long tuple, because it is carried into the presolve's pass
    /// history and formatted in two places - and a nine field tuple written out three times is
    /// three chances for two of them to disagree about which number is which.
    /// </summary>
    public readonly record struct Improvement(double Ms, int Links, double Plain, double Content,
        double Propagation, double Walked, int Held, int Markers, int Loaded);

    public static IReadOnlyList<Improvement> Improvements => _steps;

    /// <summary>
    /// A copy of the series, for a caller that means to keep it past the next solve.
    ///
    /// **The live list is cleared when a solve starts.** A presolve pass that wanted to keep what
    /// it did therefore had until the next pass was dispatched and no longer, which is exactly the
    /// window in which the approach is interesting. Taken under the lock the search publishes
    /// through. See Rehearsal's pass history.
    /// </summary>
    /// <summary>How many of a site's markers the game currently has loaded. See Improvement.</summary>
    private static int Loaded(Scan scan, Vector2 site)
    {
        if (scan == null || site == Vector2.Zero)
            return 0;

        var many = 0;

        foreach (var target in scan.At(site))
        {
            if (target.Live)
                many++;
        }

        return many;
    }

    /// <summary>
    /// One solve, from what launched it to what ended it. See Journey.
    ///
    /// **Every solve, not only the presolve's.** This began life in Rehearsal, which could only
    /// see the passes it dispatched itself - so a plan the player asked for, a re-solve after a
    /// refused spot, a reroll loop and a bake-off step all happened in the gaps between the lines
    /// and the timeline read as though nothing had. Start is what every one of them goes through,
    /// so the record belongs here.
    ///
    /// Ended says what finished it, which nothing could say before: a pass stopped with the action
    /// key filed as though it had run its window out, because Stop could not tell who had called
    /// it.
    /// </summary>
    public readonly record struct Run(int Number, double Started, double Ms, string Cause,
        bool Rehearsing, bool Asked, double Before, double After, int Found, string Ended,
        int Markers, int Loaded, int Readable, int Links, bool Live, bool Inside, int Table,
        Improvement[] Steps);

    /// <summary>Every solve at this dig site, oldest first. See Run and Journeyed.</summary>
    public static IReadOnlyList<Run> Journey => _journey;

    /// <summary>
    /// The timeline as lines, every one stamped with the milliseconds since the site was first
    /// seen and named by its run, so a solve beginning, each improvement it published and whatever
    /// ended it read in the order they happened.
    /// </summary>
    public static string Journeyed
    {
        get
        {
            lock (_journey)
            {
                if (_journey.Count == 0 && _pending == null)
                    return "nothing solved here yet";

                var b = new System.Text.StringBuilder(
                    "in milliseconds since this dig site was first seen:");

                foreach (var run in _journey)
                    Told(b, run);

                // The solve in flight, so a dump taken mid-search is not a timeline that stops
                // one line short of the thing being looked at. Its improvements are its series,
                // which is the same count _found keeps and is reachable from here.
                if (_pending is { } now)
                {
                    var steps = Series();

                    Told(b, now with { Steps = steps, Found = steps.Length });
                }

                return b.ToString();
            }
        }
    }

    private static void Told(System.Text.StringBuilder b, Run run)
    {
        b.Append($"\n    {run.Started,9:N0}  run {run.Number}  began    ")
         .Append($"floor {run.Before:N1} on the ")
         .Append(run.Rehearsing ? "presolve window" : "press window")
         .Append($"; {run.Markers} markers, {run.Loaded} loaded, {run.Readable} remnants ")
         .Append($"readable, {run.Links} explosives, live {run.Live}, inside {run.Inside}, ")
         .Append($"table rev {run.Table}; asked for by {run.Cause}");

        foreach (var step in run.Steps ?? [])
        {
            b.Append($"\n    {run.Started + step.Ms,9:N0}  run {run.Number}  improved ")
             .Append(SaidRow(step));
        }

        b.Append($"\n    {run.Started + run.Ms,9:N0}  run {run.Number}  ")
         .Append(run.Ended == "still running" ? "running  " : "ended    ")
         .Append($"{run.After:N1} ")
         .Append($"({(run.After - run.Before >= 0d ? "+" : "")}{run.After - run.Before:N1}) ")
         .Append($"after {run.Ms:N0}ms, {run.Found} improvements - {run.Ended}");

        // **What it published against what it handed over, when those differ.**
        //
        // A run's After is Plan.Plain read as the run closes, and the series is what the search
        // actually put on screen. They should agree: Stop keeps the best chain found and Poll
        // settles the plan before this is read. Seen disagreeing on a stopped run - the series
        // ended at 6,839 and the run filed 6,793.1, its own opening floor - so the two are printed
        // together rather than leaving a reader to spot it by eye across two lines.
        if (run.Steps is { Length: > 0 } steps &&
            Math.Abs(steps[^1].Plain - run.After) > 0.05d)
        {
            b.Append($"  [PLAN DISAGREES: the search last published {steps[^1].Plain:N1}, " +
                     $"the plan handed over is {run.After:N1}]");
        }
    }

    private static readonly List<Run> _journey = new();

    /// <summary>How many solves are kept. More than an approach has ever produced.</summary>
    private const int Journeys = 60;

    /// <summary>The solve in flight, waiting on whatever ends it. See CloseRun.</summary>
    private static Run? _pending;

    /// <summary>Which site the timeline belongs to, as the moment it was first seen. See Start.</summary>
    private static DateTime _sighted = DateTime.MinValue;

    /// <summary>
    /// Closes the solve in flight, filing it under what ended it.
    ///
    /// Called from all three places a solve can finish - the window running out in Poll, Stop, and
    /// a fresh Start superseding one still running - so a run cannot end without the timeline
    /// hearing which of the three it was.
    /// </summary>
    private void CloseRun(string ended)
    {
        if (_pending is not { } run)
            return;

        _pending = null;

        var after = Plan.Points.Count > 0 ? Plan.Plain : run.Before;

        lock (_journey)
        {
            if (_journey.Count >= Journeys)
                _journey.RemoveAt(0);

            _journey.Add(run with
            {
                Ms = _began == DateTime.MinValue ? 0d : (DateTime.UtcNow - _began).TotalMilliseconds,
                After = after,
                Found = _found,
                Ended = ended,
                Steps = Series(),
            });
        }
    }

    public static Improvement[] Series()
    {
        lock (_steps)
            return _steps.ToArray();
    }

    /// <summary>
    /// One improvement's figures, without any leading time.
    ///
    /// Split out because the approach timeline stamps these with the time since the site was first
    /// seen and the single-solve line stamps them with the time since that solve began. Same
    /// columns in the same order either way, which is the whole reason it is one method.
    /// </summary>
    public static string SaidRow(Improvement step) =>
        $"{step.Links} links  plain {step.Plain,9:N0}" +
        $"  content {step.Content,8:N0}" +
        $"  propagation {step.Propagation,8:N0}" +
        $"  walked {step.Walked,6:N0}" +
        $"  held {step.Held}" +
        $"  |  {step.Markers} markers, {step.Loaded} loaded";

    private static readonly List<Improvement> _steps = new();

    private const int Steps = 40;

    /// <summary>
    /// The chain already on screen when this solve started, and what it is worth in this solve's
    /// own environment.
    ///
    /// **A second look must never be able to make the answer worse.** The search publishes its best
    /// so far as it goes, and the readout draws that while it is running - so a fresh run's first
    /// few improvements, which start from nothing, replaced a finished plan on screen and the score
    /// fell off a cliff before climbing back. Worse, the finished plan was replaced for real if the
    /// second run happened to end below the first: the result was taken whatever it was.
    ///
    /// So the standing chain becomes a floor. Nothing below it is published while the search runs,
    /// and if the search ends below it the standing chain is what stays.
    ///
    /// Only when the problem has not moved. A different site, or an explosive placed since, is a
    /// different question and the old answer is not a floor on it - there the floor is zero, which
    /// is the behaviour this always had.
    /// </summary>
    private List<Vector2> _standing;

    private double _floor;

    /// <summary>
    /// What the last solve did with its window, in the terms the question "is pressing again worth
    /// anything" is actually asked in.
    ///
    /// **Three numbers answer it and none of them were written down.** Where the solve started -
    /// the plan it inherited - where it ended, and how many times the answer on screen improved in
    /// between. Nought improvements over four hundred rounds is a search that had nothing left to
    /// find; nought over three rounds is a window too short to find it. Those want opposite
    /// responses and looked identical from outside.
    ///
    /// The stream number is here for the same reason: it says this run drew different random
    /// openings from the last one, which is the difference between searching again and repeating.
    /// </summary>
    public static string Progress { get; private set; } = "nothing solved yet";

    /// <summary>Why each remnant did or did not get a line under it. See Breakdown.</summary>
    public static string Waved { get; private set; } = "no detailed pass yet";

    /// <summary>
    /// What the explosives already down are still paying, and why any of them pay nothing.
    ///
    /// **Built for exactly this failure and only visible in a window nobody was looking at.** The
    /// trace names which caught marker gave what, and which gave nothing and for which of the four
    /// reasons - and it lived on the scorecard alone, so a session working from dumps could see the
    /// symptom (held 0 of 0, every new bomb scored as though it were the chain's first) and never
    /// the cause.
    ///
    /// Taken from the environment the screen is actually using rather than from the static behind it,
    /// which is written by every background build. See Secured's note.
    /// </summary>
    public static string Banking { get; private set; } = "no detailed pass yet";

    /// <summary>What asked for the last solve. See Start's cause.</summary>
    public static string Asked { get; private set; } = "nothing yet";

    /// <summary>
    /// What each press of the key has been worth, in order.
    ///
    /// **Pressing again is a search in its own right and nothing was measuring it.** A solve
    /// inherits the last one's chain as a floor, so a second press starts where the first finished
    /// and improves on it - measured on one site, run 11 opened at 4,464.7 and ended at 5,050.1, a
    /// gain of 586 that no single cold solve of that site has come near. The dump shows one press's
    /// inheritance, so the shape of that climb lived only across as many dumps as there were
    /// presses.
    ///
    /// Held as a list, so the question the climb answers - is another press still finding anything,
    /// or has it flattened - can be read in one place. That is the same question the improvement
    /// counter answers within a solve, asked across them.
    ///
    /// Cleared with the plan, because the floor is cleared with it: a series that carried across a
    /// reset would describe a climb whose foundation had been taken away. See Forgetting.
    /// </summary>
    public static IReadOnlyList<(int Run, double Opened, double Ended)> Climb => _climb;

    private static readonly List<(int Run, double Opened, double Ended)> _climb = new();

    /// <summary>How many presses to keep. Enough to see a curve, few enough to read.</summary>
    private const int Climbs = 20;

    /// <summary>Forgets the climb, for a reset that takes the floor away with it. See Climb.</summary>
    public static void Forgetting()
    {
        lock (_climb)
            _climb.Clear();
    }

    /// <summary>What the climb says, for the dump.</summary>
    public static string Climbed
    {
        get
        {
            lock (_climb)
            {
                if (_climb.Count == 0)
                    return "no solve yet at this site";

                var said = new List<string>();

                foreach (var (run, opened, ended) in _climb)
                {
                    said.Add(double.IsNegativeInfinity(opened) || opened <= 0d
                        ? $"{run}: from nothing to {ended:N0}"
                        : $"{run}: {opened:N0} to {ended:N0} ({ended - opened:+#,0;-#,0;0})");
                }

                return string.Join(", ", said);
            }
        }
    }

    /// <summary>
    /// How many solves have finished here, so a reader can wait on the answer rather than on the
    /// plan object.
    ///
    /// **A new plan object is not the same event as a new answer.** The plan is handed back fresh
    /// whenever anything re-describes the chain - an undo walking the cursor back, a bake-off
    /// adopting a winner - and a reader gated on the reference cannot tell those from a search
    /// having produced something new. The reroll pass was, and it costs what a solve costs, so
    /// every undo dispatched one about a chain nobody had asked a question about.
    ///
    /// Bumped only where a search hands over: Poll and Stop. See Rolling.Consider.
    /// </summary>
    public static int Solves { get; private set; }

    private int _runs;
    private Vector2 _counting;

    /// <summary>
    /// The best chain the search has found so far, while it is still looking.
    ///
    /// Published as a finished copy from the search's own thread, so reading it needs no lock: the
    /// reference swap is atomic and the list behind it is never touched again once handed over.
    ///
    /// It is the chain rather than the number because the number has to be worked out the same way
    /// the finished plan's is - from the detonator, by the same objective - or it would jump to a
    /// different basis the moment the search stopped.
    /// </summary>
    public List<Vector2> Live => _live;

    /// <summary>Bumped whenever a better chain is published, so a reader knows to recompute.</summary>
    public int Found => _found;

    /// <summary>
    /// Whether a search is running or has finished without Poll collecting it yet.
    ///
    /// **Until collected, not until complete.** It read false the moment the task completed, and every caller
    /// that starts a solve when this is false runs later in the same tick than Poll - so a search finishing in
    /// that gap was cancelled with its answer uncollected: the batch of repeated presses clears the plan first,
    /// and a presolve pass or a reroll re-solve cancels in Start. Measured: 3 of 40 repeated presses on Craggy
    /// Peninsula scored in the press history but reached neither the plan, the run log nor trials.csv, two of
    /// them the best of their batch. Poll collects on the next tick, so this reads true for one tick longer.
    /// </summary>
    public bool Searching => _search != null;

    public bool Ready => Plan.Points.Count > 0;

    /// <summary>
    /// Whether the last solve proved nothing here can score higher.
    ///
    /// Worth saying out loud rather than leaving in a dump: it is the one answer that makes pressing
    /// again pointless, and without it a player who suspects the plan re-presses forever.
    /// </summary>
    public bool ProvenBest { get; private set; }





    /// <summary>
    /// Puts a plan on screen that this search did not produce.
    ///
    /// For the bake-off, which runs several searches and keeps the best - the winner has to be the
    /// one the overlay draws and placement follows, and it is not the one that finished last.
    /// </summary>
    /// <summary>
    /// Follows the chain back up when an explosive is taken off the ground.
    ///
    /// **Undo used to be a search problem and it never was one.** The plan holds only what is left,
    /// so taking a link back frees one the plan has no entry for - and the answer was to go looking
    /// for it: in the filed chain, re-read from disk, re-validated against the ground, and rejected
    /// whenever the two could not be made to agree. They often could not. The count and the position
    /// list update on different frames, the file is the best chain ever found here rather than the
    /// one being walked, and a cell the placement run was once refused stays refused - so the third
    /// undo of a five link chain reliably came back with "does not fit what is on the ground now"
    /// and the readout collapsed to a plan of one.
    ///
    /// There is nothing to look for. The chain is in hand, whole, and the link just freed is the one
    /// in front of the cursor. So the cursor moves back and the plan is the chain from there - the
    /// same list, re-described against the environment the shorter prefix makes, with its score and
    /// its drawing intact.
    ///
    /// Placing is not handled here at all: the link that goes down is already in both halves, so
    /// nothing has to happen. See Laid.
    /// </summary>
    /// <returns>Whether the plan now matches the ground, so the caller knows to re-solve or not.</returns>
    public bool Track(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation)
    {
        if (_chain.Count == 0 || Searching)
            return false;

        var placed = Detonator.PlacedExplosiveGridPositions(gc) ?? [];

        // Only as far as the count and the positions agree, which on an undo frame is the count.
        // See Ahead, which reads the same two fields for the same reason.
        var down = Math.Clamp(Math.Min(Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0),
            placed.Length), 0, _chain.Count);

        // Only the links this chain laid can be walked back up. An explosive placed by hand off the
        // plan makes what is on the ground a different route, and taking one of those back leaves a
        // chain nobody planned - which is a re-solve, and says so by handing back false.
        for (var i = 0; i < down; i++)
        {
            if (Vector2.Distance(placed[i], _chain[i]) >= Astray.Owns(settings))
                return false;
        }

        // **Most undos need nothing done, and that is the point of holding the route whole.**
        //
        // The plan runs from the cursor to the end of the chain and is not trimmed as explosives go
        // down, so a link laid FROM the plan is still one of its spots - taking it back off simply
        // makes that spot unplaced again. The drawing keeps its blast circles, the count under the
        // score goes from 4/5 back to 3/5, and the score does not move at all, because nothing about
        // the route has changed. Which is what was wanted: undo one link, keep the plan.
        //
        // Only when the undo goes back PAST where the plan begins is there anything to do, and that
        // happens when the plan was solved mid-chain - the freed link belongs to the head, which the
        // plan has no entry for.
        if (down >= _laid)
            return true;

        // Rebuilt because the environment depends on what is down: the origin moves back to the
        // link before the cursor and the content the freed blast would have taken is in the world
        // again. Everything ABOUT the chain is unchanged - this only re-prices it.
        var env = Build(gc, settings, scan, blast, valuation, true, true, out _, out _);

        if (env == null)
            return false;

        // GetRange copies, so the plan gets its own list and _chain is never handed out to be
        // mutated - but Adopt puts the same points straight back, so the chain itself does not move.
        var left = _chain.GetRange(down, _chain.Count - down);

        // **Every link still has to be ground the game will take.**
        //
        // The links BEHIND the cursor were accepted by the game, so walking back up them is always
        // safe - but the ones ahead of it are planned rather than proved, and a planned spot can
        // stop being placeable while the chain sits there: the placement run tries it, the game
        // turns it away, and Refused remembers that for the life of the site. Restoring it anyway
        // put a chain on screen that cannot be laid, and the objective said so exactly as it should
        // - "1,987 / -∞", with the plan's first link on a cell the dump reports as canplace False
        // refused True.
        //
        // Handing back false is the right answer rather than repairing it here. A chain with a dead
        // link is a search problem, and the caller's response is to ask for a search - seeded from
        // this same chain, so what comes back is this route with the dead link routed around rather
        // than a cold start. The chain in hand is untouched either way, which is the whole point of
        // holding it: the re-solve has a floor to beat instead of a blank site.
        foreach (var at in left)
        {
            if (!env.CanPlace(at))
                return false;
        }

        _laid = down;
        _env = env;
        Site = Detonator.DetonatorGridPosition(gc);

        Adopt(Planner.Describe(env, left));

        return true;
    }

    /// <summary>
    /// Puts a line on the status readout from outside, for something the player did rather than something
    /// the solver decided. See the chain browser.
    /// </summary>
    public void Announce(string detail) => Say("", detail);

    public void Adopt(Plan plan)
    {
        if (plan is not { Points.Count: > 0 })
            return;

        Plan = plan;
        _live = null;

        Rechain();

        Say("", $"{Plan.Points.Count} explosives over {Plan.Covered} markers");
    }

    /// <summary>
    /// Writes the whole route down: the links already laid, then the plan that follows them.
    ///
    /// **The only place a chain is ever composed.** Every reader takes Chain as it stands rather
    /// than assembling one of its own out of the plan and the game's placed list, which is what
    /// stops two readers disagreeing about the same route.
    ///
    /// The head is kept from the chain already held rather than re-read from the game, because the
    /// game's two accounts of it - a count and a position array - do not arrive on the same frame,
    /// and the whole point of writing the route down is not to have to reconcile them again. Start
    /// seeds it from the ground once per solve, which is the moment the two are agreed upon.
    /// </summary>
    private void Rechain()
    {
        var chain = new List<Vector2>(_laid + Plan.Points.Count);

        for (var i = 0; i < _laid && i < _chain.Count; i++)
            chain.Add(_chain[i]);

        chain.AddRange(Plan.Points);

        _chain = chain;
        _laid = chain.Count - Plan.Points.Count;
    }

    private List<Vector2> _split;
    private int _splitAt = -1;
    private List<(double Content, double Carried)> _each;

    /// <summary>
    /// What the last detailed pass actually covered, for the dump.
    ///
    /// The pass fills Planner.RuneTallyByRemnant, which the overlay reads to write each remnant's waves - so
    /// "RuneTallyByRemnant holds three remnants" and "the pass covered seven links of fifteen" are the same fact
    /// said twice, and only the second one says why. Cached on the plan object, so it can be several
    /// seconds old and still be the pass whose figures are on screen.
    /// </summary>
    public static string Detailed { get; private set; } = "has not run";

    /// <summary>
    /// What each link of a chain is worth, taken apart into content and what it passes on.
    /// </summary>
    /// <remarks>
    /// Worked out once per chain rather than per frame. A detailed scoring walks every marker to
    /// attribute it, which is nothing next to a search and is not nothing sixty times a second - and
    /// the answer cannot change while the chain does not.
    /// </remarks>
    /// <summary>
    /// What a single blast at this spot would be worth, on the plan's own terms.
    ///
    /// **A blast is only worth anything in the context of a chain**, since a carried rune pays over
    /// what comes after it - so this prices the spot as a chain of ONE, thrown from where the next
    /// explosive would be thrown from. That is the honest comparison for somebody choosing between
    /// two pieces of ground with the circle up: what does putting it here, now, collect.
    ///
    /// Worked out fresh each time it is asked, which is once a frame at most and only while the
    /// setting is on and the circle is up.
    /// </summary>
    public (double Content, double Carried) Spot(Vector2 at)
    {
        var env = _env;

        if (env == null || at == Vector2.Zero)
            return (0d, 0d);

        // **The detailed pass publishes to shared statics, and this one must not.**
        //
        // Planner.RuneTallyByRemnant is filled by whatever chain was last scored in detail, and the overlay reads
        // it to write what each remnant's waves are wearing. This is a detailed pass too - it has to
        // be, Each only exists when detail is on - but it is about ONE hypothetical blast, not the
        // chain. Left to publish, it cleared the chain's figures and replaced them with a single
        // spot's, so raising the placement circle wiped the yellow line off every remnant the cursor
        // was not sitting on. Drawn in this order every frame: the chain's pass, then this, then the
        // remnants that read the result.
        //
        // Kept and put back rather than suppressed, because the figures belong to the chain and the
        // chain has not changed - this is a question asked beside it, not instead of it.
        //
        // **And what is at stake is not only a readout.** Options.Solved reads Planner.Chosen, and
        // the placement run reads Options.Solved to decide which reward to CLICK - so a stale entry
        // there is a wrong action rather than a wrong number.

        // **Taken wholesale, because naming the tables by hand is what kept going wrong.**
        //
        // This pass runs AFTER the chain's, every frame the cursor is over ground a bomb can go on,
        // so everything a detailed pass publishes is left describing one hypothetical blast unless
        // it is put back. Three separate fixes were spent restoring one member each - RuneTallyByRemnant, then
        // the propagation totals, then Chosen, which gated the placement run's click - and each
        // time the rest stayed wrong.
        //
        // What was still wrong when this was written: Credits, which is what the blast circles
        // draw; Factors and Rankings, which are the dump's "solver's intent, link by link"; Repeats
        // and Bookings. All of them were reporting the spot under the cursor.
        //
        // Planner.Published is now the whole bundle and Restore puts the whole bundle back, so
        // anything added to the pass later is carried by the record rather than by somebody
        // remembering this method exists. See Planner.DetailedPass.
        var published = Planner.Published;

        var each = Planner.Evaluate(env, new List<Vector2> { at }, detail: true).Each;

        Planner.Restore(published);

        return each is { Count: > 0 } ? each[0] : (0d, 0d);
    }

    /// <summary>
    /// What each blast is worth: what it catches, and what the chain would lose without it.
    ///
    /// **The second figure is a counterfactual, not a share of the total.** Propagation is a
    /// property of the chain rather than of any one blast - a rune booked at link nought pays out
    /// over every link after it - so the scoring loop credits it to the blast that BOOKED the rune.
    /// That answers "where did this number come from", which is a question about the arithmetic,
    /// while the number drawn in a blast circle is read as an answer to "what do I get for putting
    /// this one here", which is a question about a decision. The two are different, and where they
    /// differ the credited figure misleads in both directions: an early propagating remnant is
    /// charged with propagation that only exists because of the links that follow it, and those
    /// links show nothing for carrying it.
    ///
    /// So the propagation each link is shown is the chain's propagation less the propagation of the
    /// same chain WITHOUT that link. That is what dropping the blast would actually cost, which is
    /// the decision in front of the player.
    ///
    /// **These do not sum to the total, and should not.** Propagation is superadditive: two links
    /// each carrying a rune to the other's monsters are each worth more alone than half the pair.
    /// A column of marginal values adding up to more than the chain is the honest shape of that,
    /// not an error to be normalised away - and normalising it would put the misleading number
    /// back.
    ///
    /// Cost is one extra scoring pass per link, none of them detailed, and only when the chain
    /// object changes. The detailed pass is run LAST so the shared figures the dump reads -
    /// Propagated, Pooled, RuneTallyByRemnant - are the ones belonging to the real chain.
    /// </summary>
    /// <summary>
    /// Scores the explosives already down when there is no plan, so the readout is right on arrival.
    ///
    /// **A reload with bombs on the ground drew nothing until something solved.** Every figure the
    /// overlay writes over a remnant comes from the detailed pass, the pass comes from Breakdown, and
    /// Breakdown needs an environment - which only a solve builds. So walking back to a half-finished
    /// dig, or reloading the plugin mid-chain, showed the propagating runes on no remnant at all until
    /// the key was pressed, and pressing the key is not how you find out what is already true.
    ///
    /// Everything needed is readable from the game: the placed positions ARE the chain. So the site is
    /// built once and the laid links scored on their own, which fills the same table a planned chain
    /// fills and leaves the plan untouched - there is no plan to touch.
    ///
    /// **Once, latched on the site and the count.** Building a site is the expensive thing the action
    /// key does; doing it per frame would cost a frame per frame. It runs again when another explosive
    /// goes down, which is exactly when the answer has changed.
    /// </summary>
    public void Showing(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation)
    {
        if (Ready || Searching)
            return;

        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed is not { Length: > 0 })
            return;

        var site = Detonator.DetonatorGridPosition(gc);
        var mark = (site, placed.Length);

        if (_showed == mark)
            return;

        _showed = mark;

        var env = Build(gc, settings, scan, blast, valuation, true, true, out _, out _);

        if (env == null)
            return;

        _env = env;
        _chain = [..placed.Select(at => new Vector2(at.X, at.Y))];
        _laid = _chain.Count;

        // The tail is empty and the head is everything, which is the one shape Breakdown refused.
        Breakdown([]);
    }

    /// <summary>Which site and how many down the readout was last built for. See Showing.</summary>
    private (Vector2 Site, int Placed) _showed = (Vector2.Zero, -1);

    public List<(double Content, double Carried)> Breakdown(List<Vector2> tail)
    {
        var env = _env;

        // **An empty tail is a question, not a mistake, once the route is held whole.** Everything
        // below scores the laid links and the planned ones together, so a chain that is entirely laid
        // is answerable - and it is exactly the case a reload lands in. Refusing it is what left the
        // remnants blank until something solved. See Showing.
        if (env == null || tail == null || (tail.Count == 0 && _laid <= 0))
            return null;

        // **The cursor is part of the question, so it is part of the key.**
        //
        // What this works out is scored over the whole route - the laid links and the planned ones -
        // so an answer is only still good while BOTH halves are the ones it was computed from. The
        // plan object alone was the key, which is right for the tail and says nothing about the
        // head, and that is a difference that can only appear once a route is held whole.
        //
        // In practice the two move together: the only thing that changes Laid is an undo, and an
        // undo adopts a new plan. Keyed on both anyway, because "in practice they move together" is
        // exactly the kind of invariant that stops being true quietly.
        if (ReferenceEquals(_split, tail) && _splitAt == _laid)
            return _each;

        _split = tail;
        _splitAt = _laid;

        // **Scored over the WHOLE route, and only the tail handed back.**
        //
        // The caller draws numbers in the circles it can see, which are the links still to place -
        // but neither thing measured here is a property of those links alone. What a blast is worth
        // is what the chain would lose without it, and the links already down are part of that
        // chain; and the detailed pass publishes Planner.RuneTallyByRemnant, which the overlay reads to write
        // what each remnant's waves are wearing.
        //
        // Given the tail alone, both go wrong together. Seen on a fifteen link chain with seven
        // laid: RuneTallyByRemnant held only the remnants the remaining eight reach, so every remnant covered by
        // a blast already on the ground lost its yellow line entirely. Before the route was held
        // whole this never showed, because a plan solved from scratch WAS the whole chain - the tail
        // and the route were the same list, and the difference had nowhere to appear.
        var head = Math.Min(_laid, _chain.Count);
        var chain = new List<Vector2>(head + tail.Count);

        for (var i = 0; i < head; i++)
            chain.Add(_chain[i]);

        chain.AddRange(tail);

        // Without each link in turn, before the detailed pass, so nothing it publishes is stale.
        var without = new double[chain.Count];

        if (chain.Count > 1)
        {
            var shorter = new List<Vector2>(chain.Count - 1);

            for (var i = 0; i < chain.Count; i++)
            {
                shorter.Clear();

                for (var k = 0; k < chain.Count; k++)
                {
                    if (k != i)
                        shorter.Add(chain[k]);
                }

                without[i] = Planner.Rate(env, shorter).Propagation;
            }
        }

        var full = Planner.Evaluate(env, chain, detail: true);
        var all = full.Each;

        Detailed = $"{chain.Count} links ({head} laid + {tail.Count} planned), " +
                   $"{DateTime.UtcNow:HH:mm:ss}";

        // **Which remnants the pass could have written a line for, and which it did.**
        //
        // Three separate guesses were spent on why only the remnant under the PLANNED link carries
        // yellow text while the fourteen already down carry none, and reading the code cannot settle
        // it: the answer is in the values, and the values are locals on a worker thread. The pass is
        // handed the whole route and says so, RuneTallyByRemnant is written by nothing else that does not put it
        // back, and still only one remnant appears.
        //
        // So the three things that could each explain it get written down together: whether the
        // remnant is still a target at all, whether it has waves for anything to land on, and how
        // far it sits from the nearest link in the chain just scored. One of those three is false
        // for the fourteen and true for the one.
        var waved = new List<string>();

        var everything = new List<PlanTarget>(env.Targets);

        if (env.Shown is { Count: > 0 })
            everything.AddRange(env.Shown);

        foreach (var target in everything)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            var cell = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));
            var near = float.MaxValue;

            foreach (var link in chain)
                near = MathF.Min(near, Vector2.Distance(link, target.Grid));

            waved.Add($"({cell.Item1},{cell.Item2}) waves {target.Waves:0.#} " +
                      $"weight {target.Weight:0.#} nearest link {near:0.#} " +
                      $"caught {(near <= env.Blast + target.Radius ? "YES" : "no")} " +
                      $"scored {(env.Targets.Contains(target) ? "YES" : "no")} " +
                      $"drawn {(Planner.RuneTallyByRemnant.ContainsKey(cell) ? "YES" : "NO")}");
        }

        Waved = waved.Count == 0 ? "no remnant targets in the environment" : string.Join("; ", waved);

        Banking = string.IsNullOrWhiteSpace(env.Banked)
            ? "nothing recorded - no explosive has caught anything yet"
            : env.Banked;

        if (all == null)
        {
            _each = null;

            return null;
        }

        for (var i = 0; i < all.Count && i < without.Length; i++)
        {
            // Never below nothing. A link whose removal RAISES the chain's propagation is
            // possible - it can free an explosive the rest use better - and "this blast is
            // worth minus four" in a circle on the ground is not a thing worth drawing.
            all[i] = (all[i].Content, Math.Max(0d, full.Propagation - without[i]));
        }

        // The slice the caller asked about, so its indices still line up with the circles it draws.
        _each = head > 0 && head < all.Count
            ? all.GetRange(head, all.Count - head)
            : all;

        return _each;
    }


    /// <summary>
    /// **Gone - see Ceiling, which is the same idea done once and done correctly.**
    ///
    /// It priced insistence as a WEIGHT on the marker, large enough to dominate the site, and that
    /// one decision is what grew every duplicate term the scoring had: a second worth on the target
    /// to hold the real number, a Worth() to undo the inflation, an Unearths() to stop it leaking
    /// into propagation, a Bonus on the verdict to subtract it back out, and a Plain beside every
    /// Total. Insistence is a rule now, counted on the chain rather than added to a marker, and all
    /// of those went with it.
    ///
    /// The number itself was also not a bound, which is written up on Ceiling.
    ///
    /// What a remnant the player has said they must have is worth to the search.
    ///
    /// Derived from the dig site rather than fixed, and that is the whole point. It was a constant
    /// thousand, chosen when a whole chain scored about a hundred and seventy - six times the
    /// entire site, comfortably dominant. Weights are a preference though, and a player who scales
    /// theirs up turns "must take" into "quite fancy": at remnant 100 and rare monster 33 a full
    /// chain scores some eight hundred and fifty, and a thousand no longer outranks simply
    /// covering everything else.
    ///
    /// So it is computed as more than the best conceivable score without it - every marker's weight
    /// plus the most its propagation could ever pay - which makes covering a required remnant beat
    /// covering the whole rest of the site by construction, at any scale anybody chooses.
    ///
    /// Still finite, and still deliberately not a hard constraint: an unreachable required remnant
    /// costs the plan nothing but the chance at it, where a real requirement would produce no plan.
    /// </summary>
    /// <summary>
    /// More than any chain in this dig site can possibly score - the price of dropping one marker
    /// the player insisted on.
    ///
    /// **A refusal wants a ceiling, and MustHave was not one.** That figure adds a sum of
    /// PERCENTAGES to a sum of weights and multiplies it by a COUNT of remnants, where the objective
    /// pays propagation as percent/100 of downstream MONSTER WEIGHT - so on a site carrying three
    /// hundred monster markers the real propagation term can outgrow the bound meant to dominate it.
    /// It never did in practice, which is the worst way for a bound to be wrong: nothing announces
    /// it, and the day it happens a must-take quietly becomes a preference again.
    ///
    /// This one is dimensionally honest. Content cannot exceed every marker's weight plus every
    /// reward; propagation cannot exceed every carried rune applying to every monster the site
    /// holds. Loose by a wide margin, which costs nothing - a ceiling only has to be a ceiling.
    ///
    /// The same number for every chain in a solve, so it cancels between any two of them that drop
    /// the same count. What it decides is only the ORDER of the counts, which is the whole point.
    /// </summary>
    private static double Ceiling(List<Target> content, AutoExpeditionSettings settings)
    {
        var total = 0d;
        var carried = 0d;
        var monsters = 0d;

        foreach (var target in content)
        {
            var worth = MathF.Max(0f, Weighing.WeightOfTarget(target, settings));

            total += worth + MathF.Max(0f, Weighing.Reward(target, settings));
            carried += MathF.Max(0f, Weighing.Carries(target));

            // **And what it passes on through a scope, which is where a relic's magnitude lives.**
            //
            // This summed the flat carry only. That was the whole of a relic's propagation while a
            // setting: row's effects could not be read; now they can, the carry is nought and the
            // percentage arrives here instead - so without this the ceiling stops covering the
            // propagation term it exists to dominate, which is the one way a ceiling can be wrong.
            foreach (var (_, _, percent, _) in Weighing.ScopedEffectsOfTarget(target) ?? [])
                carried += MathF.Max(0f, percent);
            monsters += worth + MathF.Max(0f, Weighing.Waves(target));
        }

        return total + carried / 100d * monsters + 1d;
    }

    /// <summary>
    /// Builds the environment from the game and starts the search.
    ///
    /// Returns what to tell the player. Every way this can fail to produce a plan is a thing the
    /// player can do something about - stand in a dig site, put the placement indicator down once
    /// so the blast radius can be read - so each one says which.
    /// </summary>
    /// <summary>
    /// Everything the search needs, read off the game. One place, three callers.
    ///
    /// Shared by the planner, the live score and the score card, because a score worked out against
    /// a different environment than the plan was searched in is not a comparison - it is two
    /// different questions with one label. <paramref name="why"/> carries the reason there is no
    /// environment to be had, in the words the status line uses.
    ///
    /// Terrain is optional because reading the grid materialises the whole thing, and scoring a
    /// chain that already exists has no use for a predicate about where one could go.
    /// </summary>
    public static PlanEnvironment Build(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation, bool withTerrain, bool remaining, out string why,
        out string detail)
    {
        why = "";
        detail = "";

        var site = Detonator.DetonatorGridPosition(gc);

        if (site == Vector2.Zero)
            return Fail("No site", "no dig site here", out why, out detail);

        // **No longer a thing that can fail.** This used to refuse the solve with "the blast radius
        // has never been read - show the placement circle once, ever", because the only source was
        // the drawn art and the art is only drawn in placement mode. The radius is computed from the
        // map's own stats now, so it exists from the moment the area loads. See Detonator.ExplosionRadius.
        var radius = blast.Radius(gc, settings);

        // Only when PLANNING. The score card and the running score rate the placed chain and the
        // planned one against one environment so the two are comparable, and an environment with
        // the covered content removed scores the placed chain at nothing - every marker it already
        // caught having been taken out of the world it is being judged in. The fraction collapsed
        // towards 0/0 as explosives went down, which is the opposite of what it is for.
        // A spent remnant is scenery: it cannot be opened, it carries nothing forward and there is
        // nothing left to take from it. Leaving it in weighed a hundred points of remnant plus its
        // sockets on a thing the chain would gain nothing by reaching.
        var (content, caught) = Remaining(scan.At(site),
            remaining ? Detonator.PlacedExplosiveGridPositions(gc) : null, radius.Value);

        if (content.Count == 0)
        {
            return Fail("Empty", scan.At(site).Count == 0
                ? "nothing found in the dig site yet"
                : "everything here is already covered by the explosives that are down",
                out why, out detail);
        }

        // A remnant rich enough to be required is weighted above every other thing in the site put
        // together, which makes the search build the chain around it. Large rather than mandatory:
        // a hard requirement on a site where no legal chain reaches it would produce no plan at
        // all, where this produces the best chain that does reach it - and the best chain there is
        // if none can.
        // More than any chain here can be worth, which is what both kinds of insistence are
        // measured against: a required marker a chain drops, and an avoided one it takes.
        var ceiling = Ceiling(content, settings);


        List<PlanTarget> Project(List<Target> of) => of
            // The reward is NOT added into the weight here any more. It is one of a remnant's
            // choices now, and which choice wins depends on where the chain puts it - so the price
            // and the propagation are decided together, inside the search, rather than both being
            // counted in full before it starts.
            .Select(t => new PlanTarget(t.Grid, Extents.Of(t),
                // The player pointing at a marker is the same kind of statement as a remnant being
                // rich enough to be required, so it is said the same way - and saying "not that one"
                // is the same statement with the sign flipped. See Insisted.
                //
                // A negative weight is a real term in the objective rather than a filter: the chain
                // may still take an avoided marker when the ground leaves it no choice, and it will
                // cost it the same as insisting gains. Refusing outright would produce no plan at
                // all on a site where the only route runs past it, which is the failure the
                // requirement count avoids in the other direction. See Verdict.Held.
                // **Or a modifier the character cannot fight**, which is the same statement
                // made once in the settings instead of once per marker. See MustAvoidMods.
                // **Taking is a rule, avoiding is a price, and only one of them is a weight.**
                //
                // A required marker keeps its own worth and carries a flag, so the chain that drops
                // it is refused rather than outscored - see PlanTarget.Must. An avoided one is
                // charged more than any chain can earn, which is not the same statement and should
                // not be: a chain whose only route out runs past an avoided marker should still be
                // offered, at a cost, rather than refused outright. See Insisted.Said.Avoid.
                Insisted.Here.Avoids(t.Grid) || MustAvoidMods.Bans(t)
                    ? Weighing.WeightOfTarget(t, settings) - (float)ceiling
                    : Weighing.WeightOfTarget(t, settings),
                t.Kind, Weighing.Carries(t),
                // The bounded guess, for a remnant whose rewards have not been read and so has no
                // chosen recipe. See Planner.WeightsOfChosenRunes.
                Weighing.StrongestRunePerPropagatingSlot(t),
                Weighing.Waves(t),

                // And the lookup that weighs whatever the chosen recipe names. Nothing chooses from
                // it - see Weighing.PropagatingRuneWeights.
                Weighing.PropagatingRuneWeights(t),
                // The locked combination for a rolled remnant, which cannot be changed again - see
                // Weighing.Choices. Lost once already, in a repair that restored this method from an
                // older commit; it is the difference between pricing what a remnant IS and pricing
                // what it could have been.
                Weighing.Choices(t, settings, Pinned(t, settings, valuation)),
                // The name of an effect that pays once however many are taken, or nothing. Only the
                // unknown weights can say it - everything the plugin recognises is priced in code
                // and stacks, which is what the shipped relics do. See PlanTarget.Once.
                Weighing.Once(t),
                // The switches it grants, priced one at a time. See PlanTarget.NonStacking.
                Weighing.NonStacking(t),
                // **One kind of requirement, from one place.** This used to be two: the markers
                // somebody pointed at, and a live re-reading of the reward threshold beside them.
                // They behaved identically, so the plan could not say which was which, and the
                // second could not be argued with - un-marking a remnant the threshold liked did
                // nothing, because nothing had been marked. The threshold now writes into the same
                // store the key does, once per remnant, and this reads only that. See
                // Insisted.Automatic.
                //
                // A site can still hold more requirements than any chain could string together, and
                // that is fine by construction: chains are ranked by how many they HOLD, so ten
                // requirements with room for four still produces the best chain there is and says it
                // dropped six. It degrades to the old preference ordering rather than to no answer.
                // See Verdict.Held.
                Must: Insisted.Here.Wants(t.Grid),
                // What it IS, as bits, and what it passes on to particular kinds of thing. Worked
                // out here, once per environment, rather than in the search - see Tags.
                Mask: Tags.Mask(t),
                Spread: Weighing.ScopedEffectsOfTarget(t),
                // What this object is made of, as tagged amounts - a monstermarker's weight shared
                // between the tiers it could turn out to be, a cage's rares, a remnant's waves. Each
                // part carries the tags of the ROW THAT EARNED IT, which is what lets a modifier
                // aimed at rare monsters reach the rares inside something that is not itself one.
                // See Weighing.PartsOfTarget and PlanTarget.Contributions.
                Parts: Weighing.PartsOfTarget(t),
                // What each combination is called, walked exactly as Choices is - the same Pinned
                // argument, so a locked remnant's single entry is named by the same single name.
                // Without this the objective's choice publishes as "(unnamed)". See Planner.Chosen.
                Named: Weighing.Names(t, settings, Pinned(t, settings, valuation)),
                // What identifies each of those, same order, so the readouts can say which row the
                // planner took without joining on what it yields. See Weighing.RecipeIdsOfTarget.
                Recipes: Weighing.RecipeIdsOfTarget(t, settings, Pinned(t, settings, valuation))))
            .ToList();

        var targets = Project(content);

        // The remnants a placed explosive already caught: never scored, never credited,
        // carried only so the line under them can say what the chain sends their way.
        // See PlanEnvironment.Shown.
        var shown = Project(caught);

        // **What each barrel sets off, worked out once.**
        //
        // A second pass because it is a statement about the target list as a whole: which of them
        // stand inside which barrel's radius, and which of those are barrels themselves. Nothing the
        // search does changes it, so it is settled here and read for free half a million times a
        // solve. See PlanTarget.Sets.
        Chained(targets, scan.At(site));

        // How many the chain is required to take, and what dropping one costs. Worked out once
        // here rather than per score, and zero when nothing is marked so an ordinary solve pays
        // nothing at all for the machinery. See PlanTarget.Must and Ceiling.
        var musts = 0;

        foreach (var target in targets)
        {
            if (target.Must)
                musts++;
        }

        var refused = musts > 0 ? ceiling : 0d;

        // Every effect name this site can produce, numbered, so the scoring loop compares integers.
        // Ordinal-ignore-case because that is what the scan it replaces used, and the names come
        // from two places - the unknown weights file and the rune table - that do not agree on
        // capitalisation. See Planner.Numbered.
        var effects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Number(string id)
        {
            if (!string.IsNullOrEmpty(id) && !effects.ContainsKey(id))
                effects[id] = effects.Count;
        }

        foreach (var target in targets)
        {
            Number(target.Once);

            foreach (var (id, _, _, _) in target.NonStacking ?? [])
                Number(id);

            foreach (var (id, _) in target.Runes ?? [])
                Number(id);

            foreach (var (id, _, _, _) in target.Spread ?? [])
                Number(id);

            foreach (var choice in target.Choices ?? [])
            {
                foreach (var (id, _, _, _) in choice.Spread ?? [])
                    Number(id);

                foreach (var (id, _) in choice.Locals ?? [])
                    Number(id);
            }
        }

        // **Which group each of those adds inside of**, worked out here for the same reason the
        // numbering is: the scoring loop runs half a million times a solve and must not be reading
        // settings, let alone a table file, while it does. See Weighing.GroupKeyOfEffect.
        //
        // Group nought is the default pool. Everything unclassified lands there and adds, which is
        // what the objective did before groups existed - so a site nobody has classified scores
        // exactly as it used to, and filling the column in is an improvement rather than a
        // migration.
        // Read here and carried, for the reason the numbering itself is: Combining runs inside this

        var bands = new int[effects.Count];
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = 0 };

        foreach (var (id, key) in effects)
        {
            var group = Weighing.GroupKeyOfEffect(id);

            if (!named.TryGetValue(group, out var band))
            {
                band = named.Count;
                named[group] = band;
            }

            bands[key] = band;
        }

        // Which band number the empowering effects landed on, or -1 when nothing in this site
        // empowers - in which case the whole mechanism is inert and the score is unchanged to the
        // penny. See PlanEnvironment.Empowering.
        var empowering = named.TryGetValue(Weighing.Empowering, out var lift) ? lift : -1;

        if (!targets.Any(t => t.Wanted))
        {
            return Fail("Worthless", "nothing in the dig site is worth anything at these weights",
                out why, out detail);
        }

        // **The model, and nothing but the model.** PlacementRange is the game's own arithmetic -
        // the percentage stat plus a hundred, times the base, over a hundred, in integers - so it
        // is the limit rather than an estimate of it, and there is nothing for a reading to
        // improve. Reach.Range stays as a readout and a check on that arithmetic; see Reach.
        //
        // **It used to take the larger of the two, and that is what put planned spots on red
        // ground.** A measured 90.7 against a predicted 90 is a tenth of a percent, but every other
        // consumer - the overlay's green and red, the placement aim, the boundary sweep - asks
        // PlacementRange alone, so the planner was the one thing in the plugin working to a
        // different limit. It chose links the overlay had already drawn as out of range, which is
        // exactly the contradiction a player should never have to referee.
        //
        // A measurement cannot beat the routine it is a measurement OF. It can only be short,
        // because the cursor was never swept that far or the sweep went round a wall, or long,
        // because the indicator settled a fraction past the cell. Neither is news about the limit.
        var range = MathF.Max(1f, Detonator.PlacementRange(gc));

        // Planning around what is in the way is not a preference for a solve - a chain that ignores
        // the terrain is a chain the game will refuse to place. It is off for one caller only, and
        // that caller is not solving: Scoring values a chain that already exists, which has no use
        // for a predicate about where one COULD go. See Terrain.
        var terrain = withTerrain ? Terrain.Read(gc) : null;

        // **Whether this solve could route, recorded because its answer outlives it.**
        //
        // Terrain.Read takes the coarse routing grid only if Trusted accepts it, and it is not there
        // to accept until the client starts the encounter - see Detonator.PanelReady, which says outright
        // that a presolve made on the approach has to be re-opened once the routing grid exists. A
        // snapshot without it has no Wire, and a Terrain with no Wire answers every reach question
        // with the straight-line distance: the most generous answer there is, since the budget is
        // spent along a wire that bends.
        //
        // A chain built under that does not stay in its own solve. It is carried into the next one
        // as a floor, so a link that only fits on a straight line can be inherited by solves that
        // would never have proposed it - and nothing downstream could tell such a plan from one
        // built against the router.
        // **Only a caller that asked for terrain says anything about routing.** Scoring passes
        // withTerrain false and would otherwise write "no ground model" over the answer belonging to
        // the solve that actually produced the standing plan, which is what the dump reports.
        if (withTerrain)
            Routed = terrain == null ? null : terrain.Routing;

        // **So it does not solve at all.**
        //
        // Asking for terrain checking and getting the straight line is not a degraded answer, it is
        // a different question answered confidently: the straight line is always the MOST generous
        // reading of the reach, so every link such a solve proposes is one the game may clamp short
        // of. Measured - a link of 86.65 against a reach of 90, re-tested as reachable, and put at
        // (1153,1452) by the game when it was asked for at (1156,1446).
        //
        // **Worse than no plan, because a plan is kept.** The chain becomes the floor the next solve
        // may not go below and the best-on-file for the site, so one solve taken before the routing
        // grid was readable can hold a link nothing later will drop. Refusing leaves the previous
        // answer standing and says why; solving writes a wrong one over it.
        //
        // The grid arrives when the client starts the encounter, which is also when the detonator
        // panel fills in - see Detonator.PanelReady, which says a presolve made on the approach has to be
        // re-opened once it exists. This is the other half of that: until it does, there is nothing
        // to re-open, because nothing was written.
        //
        // **A model that was asked for and did not arrive is refused, not treated as absent by
        // choice.**
        //
        // This used to read `terrain != null && !terrain.Routing`. The note excusing the gap said a
        // null terrain meant terrain checking was switched off and the player had asked for a plain
        // distance test - which is true of the one caller that passes withTerrain false, and says
        // nothing about a caller that asked for terrain and got null because Terrain.Read found no
        // pathfinding data. Those two are the same value and opposite facts, and only the guard can
        // tell them apart, because only the guard knows which was asked for.
        //
        // Falling through meant Reachable, which answers Certainty.Yes to every pair with no model,
        // so the search accepted whatever it liked.
        //
        // An unchecked plan is worse than no plan, because a plan is kept: it becomes the floor the
        // next solve may not go below and the best-on-file for the site.
        if (withTerrain && (terrain == null || !terrain.Routing))
        {
            // Short, like the other refusals here. Why it matters is above; what the player needs is
            // which thing is missing and that waiting fixes it.
            return Fail("No router",
                Terrain.Broken ?? (terrain == null
                    ? "the pathfinding grid has not been read yet, so there is nothing to plan against"
                    : "the dig site has not started, so the routing grid is not there yet"),
                out why, out detail);
        }


        // Doodads standing in the dig site. Within a chain's worth of the detonator, because that
        // is as far as any link can be and the rest would be distance tests for nothing.
        var blocking = withTerrain
            ? Obstacles.Read(gc, Detonator.DetonatorGridPosition(gc), range * Math.Max(1, Detonator.ExplosivesInHand(gc)))
            : null;

        return new PlanEnvironment(Detonator.LastExplosiveGridPosition(gc), range, radius.Value,
            Math.Max(1, Detonator.ExplosivesInHand(gc)), targets,
            Placeable(terrain, blocking),
            Reachable(terrain, blocking, range),
            Landing(terrain, range),
            MathF.Max(0f, settings.Debug.ApartAtLeast.Value),
            Detonator.PlacedExplosiveGridPositions(gc),
            Secured(caught, settings, valuation),
            Banked,
            // Always carried for the strategies that are nothing but bands, or that open from them:
            // with the seed toggle off this was null and the mode fell back to hardcoded defaults,
            // quietly ignoring the very sliders it exists to test.
            //
            // **Destroy and repair and GRASP were added to this list the hard way.** Both open from
            // the band search now - it reaches 9,395.6 in under a second on a site where ten
            // randomised greedy builds reach 4,345 - and both guard that call on Seeding being
            // present. It was not, because only Edge only and Mixed were named here, so the opening
            // silently fell back to greedy and the mode scored 4,345.0 twice in a row to the decimal
            // while its counters barely moved. A null here does not fail, it quietly does less.
            // **Always.** This was a switch, plus a list of the modes that open from the band
            // search, and getting that list wrong was expensive - a mode missing from it fell back
            // to greedy and scored 4,345.0 twice over to the decimal while its counters barely
            // moved. One mode is left and it opens from the bands, so both the list and the switch
            // are gone and the families are simply built.
            new SeedFamilies(settings.Solver.Advanced.CandidateSpots.SpotsPerPair.Value, settings.Solver.Advanced.CandidateSpots.SpotsPerRare.Value,
                settings.Solver.Advanced.CandidateSpots.SpotsPerRemnant.Value, settings.Solver.Advanced.CandidateSpots.SpotSpread.Value,
                settings.Solver.Advanced.CandidateSpots.SpotSlack.Value / 100f, settings.Solver.Advanced.CandidateSpots.LeanHeavy.Value,
                settings.Solver.Advanced.CandidateSpots.CellsPerBand.Value,
                settings.Solver.Advanced.BandSearch.EdgeChainLinks.Value, settings.Solver.Advanced.BandSearch.EdgeBranches.Value,
                settings.Solver.Advanced.BandSearch.EdgeHorizon.Value),
            null,
            Math.Max(1, Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.TearLeast.Value, 1)),
            Math.Max(1, Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.TearMost.Value, 4)),
            effects,
            bands,
            named.Count,
            musts, refused,

            // Read here, on the main thread, and carried on the environment. The search runs off the
            // main thread and must not reach into the settings tree. See SolverSettings.
                false,
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.ShortlistRich.Value, 400),
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.ShortlistSpread.Value, 200),
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.ShortlistSparse.Value, 64f),
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.AcceptSlack.Value, 2f) / 100d,
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.OpeningShakes.Value, 2),
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.RescueBelow.Value, 10f) / 100d,
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.RestartShakes.Value, 0),
            shown,
            empowering,
            Names,
            Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.BridgeLinks.Value, 2),
            Draw: Draws,
            OpeningChoices: Safe.Read(
                () => settings.Solver.Advanced.DestroyAndRepair.OpeningChoices.Value, 5),
            ReverseRuns: Safe.Read(
                () => settings.Solver.Advanced.DestroyAndRepair.ReverseRuns.Value, true),
            Relink: Safe.Read(
                () => settings.Solver.Advanced.DestroyAndRepair.Relink.Value, true),
            OpeningMs: Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.OpeningMs.Value, 0),
            UseEnumeratedSolve: Safe.Read(() => settings.Solver.Advanced.UseEnumeratedSolve.Value, true));
    }

    /// <summary>
    /// The router this site would use, and the spots worth asking it about, without solving.
    ///
    /// **The warm-up was reading a snapshot and lagging a solve behind the site.** It took its
    /// markers from the last environment record, which is fixed at the moment that solve ran - so
    /// nothing discovered while walking was ever asked about, the rotation covered a stale count,
    /// and before the first press there was no record at all and therefore no warming, on exactly
    /// the press it exists to help.
    ///
    /// This is the same two pieces the environment carries, built from the live scan instead: the
    /// reach question, and the marker positions to ask it about. Everything else Build does - the
    /// weights, the choices, the seeding families - is about SCORING, and a warm-up has no opinion
    /// about scores.
    /// </summary>
    public static (Func<Vector2, Vector2, Certainty> Reach, Action<TimeSpan> Insist,
        List<Vector2> Spots, Vector2 Origin, float Blast, float Range) Ground(GameController gc,
            AutoExpeditionSettings settings, Scan scan, Blast blast)
    {
        var site = Detonator.DetonatorGridPosition(gc);

        if (site == Vector2.Zero)
            return (null, null, null, Vector2.Zero, 0f, 0f);

        var here = scan.At(site);

        if (here.Count < 2)
            return (null, null, null, Vector2.Zero, 0f, 0f);

        // The model and nothing but the model, for the reasons given in Build.
        var range = MathF.Max(1f, Detonator.PlacementRange(gc));
        var terrain = Terrain.Read(gc);
        var blocking = Obstacles.Read(gc, site, range * Math.Max(1, Detonator.ExplosivesInHand(gc)));

        // **No ground model, nothing to warm.** With terrain checking off the reach question is a
        // distance test and a lookup in what the game has told us - no router, no floods, nothing
        // that can be worked out in advance. Warming then asked hundreds of questions that cost
        // nothing, counted nought floods against them, and announced "Warmed: 100%" on a map the
        // player had not reached the expedition in yet: a readout measuring a thing that was not
        // happening.
        if (terrain == null && blocking == null)
            return (null, null, null, Vector2.Zero, 0f, 0f);

        var spots = new List<Vector2>(here.Count);

        foreach (var target in here)
        {
            if (!target.Spent)
                spots.Add(target.Grid);
        }

        return (Reachable(terrain, blocking, range),
            null,
            spots, Detonator.LastExplosiveGridPosition(gc), blast.Radius(gc, settings) ?? 30f, range);
    }

    /// <summary>
    /// The content the explosives already down will NOT catch.
    ///
    /// Replanning mid-chain used to weigh every marker in the site, including the ones sitting
    /// under an explosive that is already placed - so the search would happily spend one of the
    /// remaining explosives covering them a second time and score it as new weight. On a chain
    /// resumed halfway that is an explosive thrown away for nothing, and the score card would have
    /// agreed with it.
    ///
    /// Dropped rather than zeroed, because the propagation model wants them gone too and for a
    /// reason of its own: explosives detonate in the order they were placed, so everything an
    /// earlier one unearths comes up BEFORE the links being planned now. A rune carried by a
    /// remnant in the remaining chain reaches "monsters unearthed after this Remnant", which those
    /// are not. Leaving them in the pool would have the model propagating backwards in time.
    /// </summary>
    /// <summary>
    /// Whether an explosive already on the ground has taken this target.
    ///
    /// **Per target, because an egg is caught from fifteen grid further out than a marker is** -
    /// this decides what the bombs already down have claimed, and a single radius would write off
    /// the things most easily reached. See Extents.
    ///
    /// Public because the overlay has to ask the same question: a re-solved plan holds only what is
    /// LEFT, so a marker an earlier blast already took appears in no plan and read as unreached.
    /// One rule with two callers rather than two readings of it.
    /// </summary>
    public static bool Taken(Target target, Vector2[] placed, float radius)
    {
        if (target == null)
            return false;

        var reach = radius + Extents.Of(target);

        foreach (var at in placed ?? [])
        {
            if (Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                return true;
        }

        return false;
    }

    private static (List<Target> Left, List<Target> Caught) Remaining(List<Target> content,
        Vector2[] placed, float radius)
    {
        var left = new List<Target>();
        var caught = new List<Target>();

        foreach (var target in content)
        {
            if (target.Spent)
                continue;

            if (Taken(target, placed, radius))
                caught.Add(target);
            else
                left.Add(target);
        }

        return (left, caught);
    }

    /// <summary>
    /// The propagation already banked, as a percentage, from remnants the placed explosives caught.
    ///
    /// **A rune you already own is not worth nothing, and that is what this fixes.** Those remnants
    /// are dropped from the pool because re-covering them would waste an explosive - right for
    /// coverage, wrong for propagation. A rune reaches "monsters unearthed after this Remnant", and
    /// everything the remaining chain digs up comes after it, so the rune goes on paying for links
    /// that have not been placed yet. Dropping the remnant dropped the rune with it, so a chain
    /// re-solved halfway valued its own downstream monsters lower than the original solve did - and
    /// picked different spots because of it. That is the whole of "it will not carry on with the
    /// plan it just gave me".
    ///
    /// Read from the combination the remnant is actually SET to wherever the game will say, because
    /// a spent remnant's choice is made and the best it could have offered is no longer on the
    /// table. Falls back to the best when the choice cannot be read, which errs high - the right
    /// direction for something that was previously counted as zero.
    ///
    /// Taken at the maximum rather than summed, matching what the search
    /// does with the remnants it can still reach.
    /// </summary>
    /// <summary>
    /// What the explosives already on the ground carry forward, as the modifiers they are.
    ///
    /// **This used to be one number: the single largest carry percentage among the caught
    /// remnants.** Max, not sum, and untagged - so four placed blasts carrying four different stats
    /// were represented by whichever happened to be biggest, and the rest simply did not exist for
    /// the rest of the solve. Worse, only REMNANTS were looked at, so a relic already caught - the
    /// Vaal one that duplicates runic monsters, say - contributed nothing at all, and the search
    /// would cheerfully spend a link taking a second one because as far as it knew there was no
    /// first.
    ///
    /// Measured on a live site: four links placed exactly on plan, and the re-solve of the last one
    /// rated its own pick 2,136 against the filed chain's 1,821 while the whole-chain figure moved
    /// the other way, 5,301 down to 5,191. Two objectives disagreeing about the same chain, and
    /// this was why.
    ///
    /// So the modifiers travel as modifiers: id, tag and percentage, exactly as a live link's do.
    /// The objective books them at the first link, which gets grouping and multiplication for free
    /// - and gets the duplicate case right by construction, because Book keeps one entry per effect
    /// id and a second copy of one already in hand therefore adds nothing.
    /// </summary>
    private static (string Id, int Tag, float Percent, int Group, (int X, int Y) From)[] Secured(
        List<Target> caught,
        AutoExpeditionSettings settings, Valuation valuation)
    {
        if (caught == null || caught.Count == 0)
        {
            Banked = "nothing has been caught yet";

            // **The names go with it, or the last site's runes are still being carried.** This return
            // reset the description and left the data, so Names kept whatever the previous dig site
            // put there - and every remnant here inherited it. Seen on a fresh site: "nothing has been
            // caught yet" printed beside "plus 10 banked from elsewhere", every one of those ten at a
            // cell in the area before it, remnants reading 3 sockets and 13 inherited, and 3,750 of a
            // 4,533 score coming from propagation that had no source in the area at all.
            //
            // Not a readout fault. The objective reads the same array, so the chain was being shaped
            // around carrying runes that were not there.
            Names = null;

            return null;
        }

        var found = new List<(string, int, float, int, (int X, int Y))>();
        var trace = new List<string>();
        var named = new List<(string Id, (int X, int Y) From)>();

        foreach (var target in caught)
        {
            var was = found.Count;

            // Which remnant banked what, so the scoring loop can tell a remnant's own carry from one
            // that reached it. See PlanEnvironment.Secured.
            var cell = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));
            // Everything a blast catches propagates, not only remnants. A relic is the case that
            // made this obvious, and it was the one kind excluded.
            // Group nought, which is what a live link uses: PlanTarget.Group is never set from the
            // table, so Rate always lands in the default pool, and Book works its own band out from
            // the effect id. Passing anything else here would make the banked copy of a modifier
            // multiply differently from the modifier itself.
            const int band = 0;

            // **The switches, which Spread leaves out on purpose.**
            //
            // Spread carries the effects that STACK; an effect that pays once however many objects
            // grant it travels separately, through NonStacking, precisely so the objective can credit it
            // once and hand back the weight of every later copy. "Runic monsters are duplicated" is
            // one of those - so a relic granting it, caught by an explosive already placed, left no
            // trace in the bank at all, and the next chain treated the next such relic as the first
            // anybody had seen. It got no propagation from it, correctly, and the whole of its
            // marker weight, which is enough to spend a link on.
            //
            // Percentage and name both, and the name matters even at nought per cent: registering
            // it is what makes the next grant a repeat. See Planner.Granted.
            foreach (var (id, _, percent, _) in Weighing.NonStacking(target) ?? [])
                found.Add((id, -1, percent, band, cell));

            if (target.Kind != TargetKind.Remnant)
            {
                foreach (var (id, tag, percent, _) in Weighing.ScopedEffectsOfTarget(target) ?? [])
                    found.Add((id, tag, percent, band, cell));

                continue;
            }

            // A remnant propagates what its CHOSEN combination carries, which is a fact now that it
            // has been spent - no need for the planner's per-choice guessing.
            //
            // **The recipe id, because that is what the lock compares.** Weighing.Locked tests a
            // reward's Recipe first and only falls back to its Name where the game states no id, so
            // handing it a name matched nothing at all: Choices returned every combination
            // unlocked, and this then read the propagation off whichever one happened to be first
            // in the list rather than off the one the remnant is set to.
            var chosen = Safe.Read(() => valuation?.ChosenRecipeId(target.Entity), null)
                         ?? Safe.Read(() => valuation?.ChosenName(target.Entity), null);
            var taken = false;

            if (!string.IsNullOrWhiteSpace(chosen))
            {
                // Locked to what it landed on: Choices collapses to the one combination when it is
                // told which, which is the same route the planner uses to pin a rolled remnant.
                var only = Weighing.Choices(target, settings, chosen);

                if (only is { Length: > 0 })
                {
                    foreach (var (id, tag, percent, _) in only[0].Spread ?? [])
                        found.Add((id, tag, percent, band, cell));

                    // The rune NAMES as well, for the line under a remnant. Never booked - see
                    // PlanEnvironment.BankedRunes - and collected here because this is the one place
                    // the chosen combination is in hand.
                    var where = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));

                    foreach (var rune in only[0].Runes ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(rune))
                            named.Add((rune, where));
                    }

                    // **And the flat carry, which is most of what a remnant passes on.**
                    //
                    // Its runes usually name no stat, so the spread above is empty and the whole of
                    // what it carries lives in this one percentage. A live link books it as a rate
                    // inside the remnant's own group - or under a name where the effect pays once -
                    // and the banked copy has to do the same or it is not the same modifier.
                    if (only[0].Carries > 0f)
                    {
                        var once = Weighing.Once(target) ?? "";

                        found.Add((once, once.Length > 0 ? -1 : Tags.Monsters,
                            only[0].Carries, band, cell));
                    }

                    taken = true;
                }
            }

            // Nothing readable about what it landed on, so fall back to what the remnant itself
            // says it can pass on. Better an approximation of the right shape than a number of the
            // wrong one.
            if (!taken)
            {
                foreach (var (id, tag, percent, _) in Weighing.ScopedEffectsOfTarget(target) ?? [])
                    found.Add((id, tag, percent, band, cell));
            }

            // **What this marker gave, or why it gave nothing.**
            //
            // Banking silently produced a list of relics and no remnants at all on a site whose
            // placed links demonstrably caught remnants, and there are four places that could
            // swallow one: no rewards read, no chosen combination read, a chosen name the reward
            // list does not hold, or a combination that genuinely carries nothing forward. A total
            // cannot tell those apart and neither can reading the code.
            trace.Add($"      {target.Kind} at ({target.Grid.X:0},{target.Grid.Y:0}): " +
                      (found.Count > was
                          ? $"{found.Count - was} modifier(s)"
                          : target.Kind == TargetKind.Remnant
                              ? $"nothing - rewards {target.Rewards?.Count ?? 0}, chosen " +
                                $"\"{Safe.Read(() => valuation?.ChosenName(target.Entity), null) ?? "unreadable"}\""
                              : "nothing - its reference table row propagates nothing"));
        }

        Banked = trace.Count > 0
            ? string.Join("\n", trace)
            : "nothing has been caught yet";

        Names = named.Count > 0 ? named.ToArray() : null;

        return found.Count > 0 ? found.ToArray() : null;
    }

    /// <summary>
    /// What each caught marker contributed to the banked modifiers, for the score card.
    ///
    /// **Copied onto the environment rather than read from here.** It is a static written by every
    /// Build, and builds happen on background threads - so a score card that read this one printed
    /// a list of modifiers from its own environment beside a trace from whichever presolve last
    /// ran. Three remnants and no relics against a banked list of four relic effects, which is not
    /// a contradiction in the plugin but was read as one for most of an evening.
    ///
    /// Written here because Secured is where it is known, and immediately handed to the record.
    /// </summary>
    private static string Banked { get; set; } = "nothing built yet";

    /// <summary>
    /// The runes the caught remnants are propagating, by name, for the readout. See Secured.
    ///
    /// Written beside Banked and handed straight to the environment for the same reason: a static read
    /// later would belong to whichever build finished last.
    /// </summary>
    private static (string Id, (int X, int Y) From)[] Names { get; set; }

    /// <summary>
    /// Whether aiming at a point lands the explosive on it. See Wire.Lands.
    ///
    /// Null when there is no ground model, which leaves Says on the straight line - the right answer
    /// when there is no routing grid to bend around anything.
    /// </summary>
    private static Func<Vector2, Vector2, bool> Landing(Terrain terrain, float reach) =>
        terrain == null ? null : (from, to) => terrain.Aiming(from, to, reach, out _);

    /// <summary>
    /// Whether a spot will take an explosive, by the game's own rule: the ground model's no-placement volumes and
    /// routable coarse cells. See Terrain.Placeable.
    ///
    /// **Scenery is not part of it.** Doodads were treated as solid discs of 3.8 grid - the same 41.3 world unit
    /// bounds every one of them reports, so a default rather than a size - and a spot inside one was refused. The
    /// placement routine, decoded in full (NOTES 1d, 0x141F5A6B0), tests no-placement volumes, the separation from
    /// placed explosives, a route over the coarse grid and the reach along it, and no entity at all; the doodad
    /// rule came from a correlation measured before that. On Scorched Cay it refused (941,517), 3.2 grid from a
    /// doodad at (942,520), where the game places, and a player confirmed no doodad on that site blocks placement.
    /// The doodads are still read, for the dump.
    /// </summary>
    private static Func<Vector2, bool> Placeable(Terrain terrain, Obstacles blocking)
    {
        // **The model decides, and there is no list of exceptions.** Refusals used to be consulted
        // above everything, on the reasoning that the grids were a guess at the game's rule and a
        // refusal was the game stating it. That was right while the rule WAS a guess. It is now
        // 0x141F5A6B0 translated from the binary, so a refusal contradicting it is not a spot to
        // avoid - it is a fault in the model, or in what it was given, and remembering the spot
        // hides exactly the evidence that would find it.
        if (terrain == null && blocking == null)
            return static _ => true;

        return at => terrain == null || terrain.Placeable(at);
    }

    /// <summary>
    /// How long a solve may take, which the router needs so it can take its share of it.
    ///
    /// The same arithmetic Start uses, read from the settings rather than threaded through - the
    /// environment is built in several places and only one of them is the solve.
    /// </summary>
    /// <param name="links">
    /// How many explosives the chain has, which decides how much of the window the router gets.
    ///
    /// **A Grand chain is three times as long and far more than three times the problem.** The
    /// number of pairs a search asks about grows with the square of the links, and the ground it has
    /// to learn grows with the area the chain can cover - so the same allowance that warms a five
    /// link site leaves a fifteen link one reasoning about straight lines. Measured on one Grand
    /// site: 138,986 questions asked, 919 answered, 124,167 refused for want of budget.
    ///
    /// Scaled by the square of the ratio, capped at four. Three times the links is nine times the
    /// pairs, which would be nine - the cap is there because this is the router's allowance and not
    /// the solve's, and handing it everything starves the search it exists to serve.
    /// </param>
    /// <summary>
    /// How long the search may go without improving before it stops, for this size of site.
    ///
    /// Fifteen explosives is a Grand site and five or six is an ordinary one. Nothing in between has
    /// been seen, and if it ever is it takes the ordinary window - which errs towards a shorter
    /// search on a site nobody has met, rather than towards a long wait on every small one.
    /// </summary>
    private static int Settling(AutoExpeditionSettings settings, int links) =>
        Math.Max(50, links >= Detonator.GrandExplosives
            ? settings.Solver.Timing.SettleMsGrand.Value
            : settings.Solver.Timing.SettleMs.Value);

    /// <summary>
    /// The same window for a rehearsal, which is a far longer one because nobody is waiting for it.
    ///
    /// Split by site size for the reason Settling is: fifteen links is much more than three times
    /// the search of five. See Rehearsal.
    /// </summary>
    private static int Rehearsed(AutoExpeditionSettings settings, int links) =>
        Math.Max(100, links >= Detonator.GrandExplosives
            ? settings.Solver.Presolve.PresolveMsGrand.Value
            : settings.Solver.Presolve.PresolveMs.Value);


    private static TimeSpan Budget(AutoExpeditionSettings settings, int links)
    {

        var ratio = Math.Clamp(links / 5f, 1f, 2f);
        var scale = ratio * ratio;

        // **Twice the window, not ten times it.** The router takes a share of this to walk ground
        // with, and at ten times a player asking to stop 1,500ms after the last improvement was
        // granting six seconds of flooding - which then ran inside a single pass, ignored the
        // window and drew nothing until it was done. Twice keeps a cold site able to learn enough
        // ground to answer at all, while keeping the allowance recognisably related to the number
        // the player typed.
        return TimeSpan.FromMilliseconds(Settling(settings, links)) * 2 * scale;
    }

    /// <summary>
    /// Whether a link between two spots fits in the reach, once it has gone round what is in the way.
    ///
    /// A path, not a line. The chain reshapes around terrain and pays for the detour out of its
    /// length, so the question is how long the link really is rather than whether anything touches
    /// the straight segment between its ends. Asking the second question refused every link with a
    /// doodad near the line, including the many that are perfectly legal and merely longer - see
    /// Route for what that was costing.
    ///
    /// One Route for the whole environment, so the answer for a pair is worked out once however many
    /// times the search asks.
    /// </summary>
    /// <param name="detour">
    /// How far round an obstacle a link may go, as a multiple of the reach. See DetourBudget.
    /// </param>
    /// <summary>
    /// Whether the last solve's ground model routed, or null where it had no ground model at all.
    ///
    /// Read by the dump. A plan carried forward as a floor keeps whatever its links were tested
    /// against, so this is what says which. See Build.
    /// </summary>
    public static bool? Routed { get; private set; }

    private static Func<Vector2, Vector2, Certainty> Reachable(Terrain terrain, Obstacles blocking,
        float reach)
    {
        // With no ground model there is nothing to route along, so the distance is all there is -
        // env.CanReach null means "anything inside it is fine".
        if (terrain == null && blocking == null)
            return static (_, _) => Certainty.Yes;

        // **The wire, and nothing above it.** Measured pairs used to answer first, on the reasoning
        // that an observation beats a fitted model. The model is no longer fitted: it is the game's
        // own routine, translated, so there is nothing for a reading to improve - and the readings
        // came from the placement indicator, which has been caught holding a stale one.
        var lands = Landing(terrain, reach);

        return (from, to) => lands != null && !lands(from, to) ? Certainty.No : Certainty.Yes;
    }

    private static PlanEnvironment Fail(string status, string says, out string why, out string detail)
    {
        why = status;
        detail = says;

        return null;
    }

    /// <summary>
    /// What "no limit" is actually worth, since there is no such thing as an unbounded deadline.
    ///
    /// **TimeSpan.MaxValue is not a budget, it is an exception waiting to be thrown.** The search
    /// turns its budget into a moment - DateTime.UtcNow plus the span - and the maximum span put an
    /// hour past the end of time, which the runtime reports as an unrepresentable DateTime. So "no
    /// limit" is a number large enough that nothing reaches it and small enough to add to a clock.
    ///
    /// Ten minutes. The improvement window is what actually stops a solve; this is only here so the
    /// arithmetic has something to work with, and a search still improving after ten minutes is a
    /// fault rather than a search.
    /// </summary>
    internal static readonly TimeSpan Unbounded = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a solve gets when it is only being asked to mend one link.
    ///
    /// **A micro-adjustment was getting the full window and it felt exactly as wrong as it was.** A
    /// spot that would not light teaches the planner a few ruled-out cells and nothing else: the
    /// chain it inherits is already the answer to everything but that, and what is wanted back is
    /// the same chain with one link moved. Handing that the eight second Grand window meant a
    /// progress bar, a pause, and a plan that mostly agreed with the one before it - repeated every
    /// time the key was pressed.
    ///
    /// A tenth of a second, which is the budget for something that has to feel instant. The seed
    /// and the floor are what make it safe: the previous chain is the starting point and nothing
    /// below it can be published, so a short window can only polish - at worst it hands back what
    /// it was given.
    /// </summary>
    private static readonly TimeSpan Mending = TimeSpan.FromMilliseconds(100);

    /// <param name="mend">
    /// Whether this is a repair rather than a fresh question. See Mending.
    ///
    /// **Nothing passes true any more.** Its one caller was the placement run, which re-solved when
    /// the game refused a spot or put an explosive somewhere other than the planned one. Both now
    /// stop and record instead, because either is the model being wrong about a cell and solving
    /// around it removed the evidence - see PlacementDisagreements. The repair mode is kept because
    /// a short seeded window that can only polish is the right shape for any future caller that
    /// wants one link moved, not because anything is using it.
    /// </param>
    /// <param name="rehearsing">
    /// Whether this is a presolve rather than a press: a longer improvement window, and nothing
    /// drawn from it until the player asks. See Rehearsal.
    /// </param>
    /// <param name="cause">
    /// What asked for this solve, in the words a dump should say.
    ///
    /// **Most searches are not the player.** A weight or table edit, a rolled remnant, an explosive
    /// going down away from the plan or being taken back off, a rehearsal, each step of a bake-off
    /// and the cold solve comparison all re-solve on their own account - so "run 3 of this site"
    /// after a single press of the action key is not a contradiction, it is two automatic re-solves
    /// nobody could see. That cost a round of diagnosis: a score read off the screen was put down to
    /// a press that had not produced it, and then to a missing floor that was never the reason.
    ///
    /// A refused spot used to be on that list and is not any more; the run stops on one instead.
    /// </param>
    public string Start(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation, CancellationToken zone, bool mend = false,
        bool rehearsing = false, string cause = "the action key", bool looped = false, bool continuous = false)
    {
        Asked = cause;

        // Timed as a whole and at its environment build, because a start on a cold Grand site was seen stopping the
        // game's thread for five seconds with no solver running. See Spent.LongGaps.
        using var starting = Spent.On("Planning.Start");

        Planner.Unfetch();
        Planner.ForgetEnumeratedSolve();
        Repair.Undetour();

        // A press takes over from a rehearsal rather than queueing behind it. Cancel below does the
        // work; this only has to record which kind of solve is now running, since the overlay shows
        // one and not the other.
        Rehearsing = rehearsing;
        RunningContinuous = continuous;

        // **Nothing here records an ask, because eight of the nine things that start a search are
        // not one.**
        //
        // This used to read "if (!rehearsing) ShownAt = Detonator.DetonatorGridPosition(gc)", on the reading that a
        // search which is not a rehearsal is a search the player asked for. It is not: a weight
        // changing, the game refusing a spot, a remnant being rolled, an explosive going down away
        // from the plan, a bake-off step and the cold solve comparison all re-solve on their own
        // account, and every one of them marked the site as being on show.
        //
        // What that cost is not the drawing - the presolve offers its chain up anyway - but which
        // PROMISE the pass is held to. Showing(site) is what Rehearsal reads to decide whether
        // somebody is waiting, and the two kinds of solve are bounded by different settings:
        // a rehearsal by PresolveCapMs, a press by MaxSolveMs. So a site wrongly marked shown had
        // its background passes quietly released from the presolve's own ceiling and held to the
        // press's instead, which on a install with MaxSolveMs at nought is no ceiling at all.
        // Observed: a presolve pass ran 9,252ms against a 4,000ms PresolveCapMs, on a site whose
        // action key had never been pressed, because clearing the caches rebuilt the reference
        // table and the re-solve that followed marked the site asked for.
        //
        // The improvement windows happened to match on that install, so nothing about the pass
        // LOOKED wrong - which is why this was read off the ceiling rather than off the window.
        //
        // The press records itself, in one place, before it decides what the press means. See
        // Planning.Show and the action key in AutoExpedition.

        // **A read this plugin cannot verify is a reason to stop, not a reason to guess.** The
        // routing grid IS the placement rule; without it there is no model, only a straight-line
        // stand-in that looks exactly as confident on screen and quietly plans worse chains. Before
        // this, a patch that moved the offsets cost the player nothing visible and every plan after
        // it. See Terrain.Broken and the repair procedure in Offsets.
        if (Terrain.Broken != null)
            return Say("Offsets are broken", Terrain.Broken);

        var explosives = Detonator.ExplosivesInHand(gc);

        if (explosives <= 0)
            return Say(Detonator.FormattedExplosivesPlacedOutOfTotal(gc, "Spent"), "no explosives left");

        PlanEnvironment env;
        string why;
        string detail;

        using (Spent.On("Planning.Start/Build"))
            env = Build(gc, settings, scan, blast, valuation, true, true, out why, out detail);

        if (env == null)
            return Say(why, detail);

        // Asked again on the far side of Build, which is where the grid is actually read for this
        // solve. The check above catches the standing diagnosis; this one catches a read that only
        // just went wrong, so a press can never slip past between the overlay's read and this one.
        if (Terrain.Broken != null)
            return Say("Offsets are broken", Terrain.Broken);

        Cancel();

        _cancel = CancellationTokenSource.CreateLinkedTokenSource(zone);
        var token = _cancel.Token;

        // What the last solve of this same site came up with, handed to the new one as a starting
        // point - or as much of it as is still ahead of the chain. See Ahead.
        var seed = _chain.Count > 0 && Vector2.Distance(Site, Detonator.DetonatorGridPosition(gc)) < 1f
            ? Ahead(_chain, gc, Astray.Owns(settings))
            : null;

        // **A plan the chain has walked away from is not a plan, and it was being kept anyway.**
        // The standing chain is held as a floor and a fallback now, and both are wrong the moment
        // an explosive goes down somewhere the plan did not ask for: its links run from an origin
        // that has moved, its score counts content a blast somewhere else may already have taken,
        // and the readouts go on drawing a total nothing can reach. Deviated is the existing test
        // for exactly that - more explosives placed since the plan than the plan has spots with
        // explosives on them - and it only drove a word on the HUD.
        //
        // Dropped here rather than the instant it happens, because placement follows the plan and
        // taking it away mid-run leaves it with nothing to follow. A re-solve is the point at which
        // it is about to be replaced anyway.
        //
        // Not when the seed survived: that is the case where nothing has been placed since, so the
        // plan is exactly as relevant as it was when it was made.
        if (seed == null && Placement.Deviated(gc, this))
        {
            Plan = Plan.Empty;
            Say("Replanning", "explosives have gone down away from the plan, so it has been dropped");
        }

        _env = env;

        // **The readout is re-scored against the new environment now, not when the search finishes.**
        //
        // Breakdown publishes Planner.RuneTallyByRemnant - the figures the line under each remnant is
        // written from - and the overlay already calls it every frame with the plan's points. It
        // answers from cache while the tail and the laid count are unchanged, which they are after a
        // roll: the CHAIN did not move, the runes on it did. So the line went on stating the runes the
        // roll replaced until the search ended and published, which on a large site is seconds.
        //
        // Env above is freshly built and PlanTarget snapshots its runes and choices, so it is the
        // first thing in the process that knows about the roll. Dropping the cache here means the
        // overlay's own next call re-scores against it and the line is right on the next frame.
        //
        // One chain scored, which is what the overlay pays whenever a plan changes - the search is
        // the expensive part and this is not it.
        _split = null;

        // Counted per site, so "run 4" means the fourth press here rather than the fourth this
        // session - which is the number the question is about.
        if (Vector2.Distance(_counting, Detonator.DetonatorGridPosition(gc)) >= 1f)
        {
            _counting = Detonator.DetonatorGridPosition(gc);
            _runs = 0;
            ForgetSiteScores();
        }

        _runs++;
        _found = 0;
        _steps.Clear();

        // Re-scored against THIS solve's environment rather than carried over as a number: content
        // may have streamed in since, and two scores on two bases cannot be compared. Dropped if
        // any of its spots is one the game has refused since, because those are learnt between
        // solves and a chain holding one is not a plan worth defending.
        var dropped = "";
        var keeps = seed != null && Placeable(env, seed, out dropped);

        // **A chain that does not hold a must-take is provably not the answer, so it is dropped.**
        //
        // The insistence bonus is sized larger than anything the site can pay - see PlanEnvironment
        // Refused - so any chain holding a required marker beats any chain that does not, whatever
        // either is worth. Once a marker has been insisted on that the standing chain misses, that
        // chain cannot be the plan, and keeping it does active harm: it is the seed every worker
        // starts from, so the search is anchored in a basin where the answer is not. With never adopt
        // set to worker nought alone, seven of eight then adopt it.
        //
        // This is the bug that produced a must-take the chain never took: a remnant rolled into an
        // expensive reward was marked, the search was handed the old chain and a second, and no
        // amount of tearing reached a marker the route did not pass.
        //
        // Dropped rather than demoted, and the filed best with it - that one is re-scored against
        // this environment and would lose to any chain holding the marker anyway, so offering it
        // buys nothing and biases the opening.
        var insisted = Dropping(env, keeps ? seed : null);

        if (insisted)
        {
            keeps = false;
            dropped = dropped.Length > 0
                ? dropped + "; and it misses a marker that has since been insisted on"
                : "it misses a marker that has since been insisted on";
        }

        _standing = keeps ? seed : null;
        _floor = _standing == null ? 0d : Planner.Score(env, _standing);

        // **The best chain this site has ever given up, which the last press may not have been.**
        //
        // The chain above is the PREVIOUS plan, and previous is not the same as best: undo every
        // explosive and it is gone, press the key on a cold cache and it is a greedy answer, deviate
        // once and it is dropped. So the strongest route ever found here is kept on disk and offered
        // beside it, re-scored against this environment because a number written down last session
        // cannot be compared with one from this one. See Kept.
        //
        // Whichever is worth more becomes the floor. Nothing is lost by carrying both: they are two
        // candidate seeds and the search is about to try to beat them anyway.
        _areaName = Safe.Read(gc, static g => g.Area.CurrentArea.Name, "") ?? "";
        _areaSize = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, Vector2.Zero);

        // The live read rather than the field AreaChange sets, so a solve pressed before the first
        // area change of a session still files under the right site instead of under nought.
        _areaHash = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);

        Kept.Load(_areaHash, Detonator.DetonatorGridPosition(gc), _areaName, _areaSize);

        var filed = Kept.Chain is { Count: > 0 } ? Ahead(Kept.Chain, gc, Astray.Owns(settings)) : null;

        // **Not while strategies are being compared.** The file is the strongest chain this site
        // has ever given up, and during a bake-off that is whichever strategy has already run - so
        // taking it as the floor means every row after the first reports at least its predecessor's
        // score, and reports its predecessor's CHAIN whenever it cannot beat it. See
        // Bakeoff.Comparing.
        var unfit = "";

        if (Bakeoff.Comparing)
        {
            Filed = "not taken - strategies are being compared, so each one starts from nothing";
        }
        else if (filed is { Count: > 0 } && Placeable(env, filed, out unfit))
        {
            var worth = Planner.Score(env, filed);

            if (_standing == null || worth > _floor)
            {
                _standing = filed;
                _floor = worth;
                Kept.Stale = false;
                Filed = $"the filed best chain gave the floor, worth {worth:N1}";
            }
            else
            {
                Kept.Stale = false;
                Filed = $"the filed best chain scores {worth:N1} here, under the standing " +
                        $"{_floor:N1}";
            }
        }
        else
        {
            // **Unusable, so it must not block a better one from being filed.** The comparison
            // that decides what gets kept is a comparison of scores, and a chain that cannot be
            // placed any more still has one - so a route ruled out by a refused cell or a link the
            // game has since said is too long would sit in the file for ever, beating everything
            // offered afterwards and never being used itself. Marked rather than deleted: it is the
            // best thing this site ever gave up, and one spot going bad is not a reason to burn it.
            Kept.Stale = Kept.Chain is { Count: > 0 };

            // **With the reason, because "does not fit" is four different faults wearing one coat.**
            //
            // A link on refused ground, a link the router now says cannot be thrown to, a chain
            // whose prefix no longer matches what is down, and an empty file all arrive here and
            // all used to read identically. Placeable works the reason out and it was being thrown
            // away at the call - so three separate evenings ended with the same sentence on screen
            // and no way to tell which of the four it meant.
            Filed = Kept.Chain is { Count: > 0 }
                ? "the filed best chain does not fit what is on the ground now, so it will not " +
                  "block a new one being filed" +
                  (filed is { Count: > 0 }
                      ? $" - {(unfit.Length > 0 ? unfit : "no reason given")}"
                      : $" - nothing of it continues from the {Detonator.PlacedExplosiveGridPositions(gc)?.Length ?? 0} " +
                        "explosives already down")
                : "nothing filed for this site yet";
        }

        // **Whether the floor exists at all is the first thing to know when the score moves.** With
        // a standing chain the score on screen cannot fall, by construction; without one there is
        // nothing to hold it up and the search's own early answers are drawn as they come, which
        // looks identical to a bug and needs telling apart from one. See Seeded.
        Seeded = seed == null
            ? "nothing to carry over - no plan at this site yet, or the chain has left it"
            : keeps
                ? $"carried the previous {seed.Count} spots over as a floor worth {_floor:N0}"
                : $"DROPPED the previous chain, so there is no floor: {dropped}";

        // Before Site is moved on, because the loop window turns on whether THIS site already had
        // an answer. See looping.
        var solved = Ready && Site != Vector2.Zero &&
                     Vector2.Distance(Site, Detonator.DetonatorGridPosition(gc)) < 1f;

        Site = Detonator.DetonatorGridPosition(gc);
        Reach = env.Reach;
        // **The one moment the ground is read, and it is kept aside rather than applied.**
        //
        // Everything else takes the route from Chain, so the game's two accounts of what is down -
        // a count and a position array, which do not arrive on the same frame - are reconciled here
        // and nowhere else: a link is known only as far as both agree.
        //
        // Held until the solve finishes rather than written into the chain now. The chain in hand is
        // still the answer on screen for as long as this search runs, and a search can be cancelled
        // or come back with nothing - overwriting it here would mean every press threw away the
        // route it was trying to improve on. See Poll, which composes the new one.
        var already = Detonator.PlacedExplosiveGridPositions(gc) ?? [];

        _behind = already[..Math.Min(Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0),
            already.Length)];

        // **And the cursor moves now, not when the answer comes back.**
        //
        // The search is planning a TAIL from where the chain stands, and everything that reads a
        // number off the screen while it runs - the score above all - has to know how much chain is
        // behind that tail. Left until Poll, the cursor still said what it said before the four
        // explosives went down, so a re-solve of the last link was scored as though it were the
        // whole route: the number fell from 5,862 to 407 for the length of the search and snapped
        // back when it finished. Nothing was wrong with either figure; they were answers to
        // different questions, and only one of them was the question on screen.
        //
        // The tail kept is the part of the old route still ahead of the ground, which is the seed
        // the search is about to be given - so the chain stays a whole route throughout, and if the
        // search is cancelled it is still the right one. Nothing when the ground has left the route
        // entirely: there is no chain to anchor then, and Deviated has already dropped the plan.
        _laid = _behind.Length;
        _chain = [.._behind, ..(seed ?? [])];

        // "No improvement" keeps the budget as a CEILING rather than as the answer, ten times the
        // settle window. A stopping rule written in terms of improvements cannot promise to
        // terminate - the score is bounded above so it must in practice, but "must in practice" is
        // not something to hang a background task on.
        // **A trivial problem stops because it is SOLVED, not because it is small.** This briefly
        // scaled the window down when few explosives were left, which is the right instinct wearing
        // the wrong clothes: the reason a one-bomb problem does not need four seconds is that the
        // best answer is provable in milliseconds, and there is already a mechanism for stopping
        // when the answer is proved. Sizing the clock by the number of explosives guesses at that
        // instead of asking - and it would go on guessing wrong for a two-bomb problem that is
        // genuinely hard, or a fifteen-bomb one that is not. See Planner.RelaxedCeiling.
        // **The short window belongs to the reroll loop and to nothing else.**
        //
        // Rolling a remnant re-asks a question the player asked a second ago: the chain is seeded
        // from the previous one and has a single remnant's difference to absorb, so a second is
        // plenty and waiting longer is what made the loop feel slow. A press of the key is not
        // that. It is the player asking for the best answer this site has, and they are entitled to
        // the window they set for it however many times they press.
        //
        // This was judged on there simply being a plan already - which is true of every press after
        // the first, so the key quietly stopped honouring its own setting. The caller says which
        // kind of solve this is instead, because only the caller knows. See Rehearsal.
        // **And never while a must-take has just been missed**, whatever asked for the solve.
        //
        // The reroll loop's second is for a question the player asked a second ago, where the chain
        // is seeded from the previous one and has a single remnant's difference to absorb. A marker
        // insisted on that the chain misses is the opposite: the seed has been thrown away, there is
        // nothing to absorb from, and the search is starting over. A second is not enough to find a
        // route through a marker the old chain never passed - which is exactly what happened.
        var looping = looped && solved && !insisted;

        var settle = mend
            ? Mending
            : TimeSpan.FromMilliseconds(rehearsing
                ? Rehearsed(settings, Detonator.ExplosiveCount(gc))
                : looping
                    ? Math.Max(50, Safe.Read(() => settings.Solver.Reroll.LoopSolveMs.Value, 1000))
                    : Settling(settings, Detonator.ExplosiveCount(gc)));

        // Nought means the improvement window is the only thing that stops a solve. See MaxSolveMs.
        //
        // **A rehearsal had its own ceiling and did not need one.** The reasoning was that a
        // background pass would otherwise hold a core for as long as you stood near the site -
        // which the improvement window already prevents, since a pass that stops finding anything
        // ends on its own. What the second cap actually did was end passes that were still
        // improving: with the cap and the window both at four seconds the cap always bit first,
        // and the window it was sitting on top of could never expire.
        var ceiling = Safe.Read(() => settings.Solver.Timing.MaxSolveMs.Value, 0);

        var budget = mend
            ? Mending
            : ceiling > 0
                ? TimeSpan.FromMilliseconds(ceiling)
                : Unbounded;

        // **A continuous reroll pass runs its own length, as a fixed window.** Otherwise it is an ordinary
        // presolve pass, stopped by the improvement window and the time cap, and at 8s it rarely beats a standing
        // plan from the top of the distribution before the next pass starts over on a new draw. The window the
        // pass would have had is kept for the shares taken of it. See RerollSettings.ContinuousPassMs.
        var pressWindowMs = 0;

        if (continuous && !mend)
        {
            var passMs = Detonator.ExplosiveCount(gc) >= Detonator.GrandExplosives
                ? Safe.Read(() => settings.Solver.Reroll.ContinuousPassMsGrand.Value, 24000)
                : Safe.Read(() => settings.Solver.Reroll.ContinuousPassMs.Value, 12000);

            if (passMs > 0)
            {
                pressWindowMs = (int)settle.TotalMilliseconds;
                settle = TimeSpan.FromMilliseconds(passMs);
                budget = settle;
            }
        }

        var strategy = Safe.Read(() => settings.Solver.Advanced.Strategy.Value, SolverSettings.DestroyRepair);

        // What this solve is, for the trials log. All three were declared and never set - lost in a
        // repair - so every row recorded a blank strategy, level nought, and a duration measured
        // from DateTime.MinValue: sixty four billion seconds in a column meant to say how long a
        // solve took. Nothing reads that file until somebody asks a question of it, which is how a
        // number that absurd sat there unnoticed.
        // **One solve superseding another is an ending too**, and it is the one that used to
        // vanish: Start cancels whatever is running before it plans, so a pass stopped this way
        // left no line at all. Before _began moves on, or the superseded run is filed as lasting
        // nought milliseconds.
        CloseRun("superseded by a new solve");

        _strategy = strategy;

        _began = DateTime.UtcNow;
        _level = Safe.Read(gc, static g => g.Area.CurrentArea.RealLevel, 0);

        {
            var where = Detonator.DetonatorGridPosition(gc);

            // **A new dig site is a new timeline.** Rehearsal owns the one answer to when a site
            // was first seen, so this follows it rather than deciding again - two answers to that
            // question is how two timelines come to disagree about the same moment.
            if (Rehearsal.Sighted != _sighted)
            {
                _sighted = Rehearsal.Sighted;

                lock (_journey)
                    _journey.Clear();
            }

            var (_, _, missing, remnants) = scan?.Reach(where) ?? (0, 0, 0, 0);
            var stood = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

            _pending = new Run(_runs,
                Rehearsal.Sighted == DateTime.MinValue
                    ? 0d
                    : (_began - Rehearsal.Sighted).TotalMilliseconds,
                0d, cause, rehearsing, Showing(where), _floor, _floor, 0, "still running",
                env.Targets.Count, Loaded(scan, where), remnants - missing,
                Detonator.ExplosiveCount(gc), Detonator.PanelReady(gc),
                stood != Vector2.Zero &&
                    Vector2.Distance(stood, where) <= Detonator.BaseReach(gc),
                Wrt.Revision, null);
        }

        // **One search, so there is nothing to dispatch on.** Destroy and repair is the only
        // mode left; the stretched variant is the same operators against a ground model Build
        // widens, so it differs in what it is allowed to reach and not in what it does. See
        // Terrain.Aiming.

        // Only the modes built out of repetition. Beam and Edge only are deterministic, so copies of
        // them would do identical work in parallel. See Solving.
        var threads = Math.Max(1, Safe.Read(() => settings.Solver.Threads.Value, 1));

        Window = (float)settle.TotalSeconds;
        _until = DateTime.UtcNow + settle;

        // **Not null.** The readouts draw the live chain while a search runs, so blanking this and
        // then refusing to publish anything below the standing plan left them with nothing to draw
        // - the score disappeared for the whole of a re-solve that could not improve, which reads as
        // a fault rather than as "no better answer yet". The chain already on screen stays on screen
        // until something beats it. Null only when there is nothing to keep.
        _live = _standing;

        // **Where the score stands as this solve begins, noted as a score like any published one.** The history only
        // heard from improvements, so a solve that could not beat the standing chain added nothing - and a site
        // whose score had stopped climbing read as a site with no scores at all, which the reroll advice waits out
        // to its longest wait rather than starting at once. Measured: two presolve passes that published nothing
        // against a standing 15,144, and the advice "WAITING - no whole 8s window of scores yet". See SiteScoreRise.
        if (_standing is { Count: > 0 })
            NoteSiteScore(Planner.Plainly(env, _standing));

        var margin = MathF.Max(0f, settings.Solver.Timing.MinImprovement.Value);

        // The best score that has actually reached the screen, which starts at the standing plan's.
        // Single search thread, so a plain captured local is enough - see Planner, which has no
        // Parallel and no Task of its own.
        var shown = _floor;

        void Better(List<Vector2> chain)
        {
            var worth = Planner.Score(env, chain);

            // **The search publishes chains that are not better than what is drawn, and says so
            // twice.** Its first greedy answer goes out before the seed is even considered, and its
            // own best improves from there - so on a re-solve the callback fires repeatedly while
            // the plan on screen never moves. Everything below what is drawn stops here.
            if (worth <= shown)
                return;

            shown = worth;
            _live = chain;
            LastGainAt = DateTime.UtcNow;
            Interlocked.Increment(ref _found);

            // Recorded where the window is reset, so the two can never disagree about what counts
            // as an improvement. See Improvements.
            // **Rated again rather than reusing the gate's figure.** Score answers the objective's
            // question - one number, the thing being maximised - and what a reader wants at this
            // moment is where that number came from. A handful of improvements a solve makes the
            // second pass affordable; the gate stays on Score so nothing about what counts as an
            // improvement is changed by recording it.
            var told = Planner.Rate(env, chain);

            NoteSiteScore(told?.Plain ?? 0d);

            lock (_steps)
            {
                if (_steps.Count == Steps)
                    _steps.RemoveAt(0);

                _steps.Add(new Improvement((DateTime.UtcNow - _began).TotalMilliseconds,
                    chain.Count, told?.Plain ?? worth, told?.Content ?? 0d,
                    told?.Propagation ?? 0d, told?.Walked ?? 0d, told?.Held ?? 0,
                    env.Targets.Count,
                    // **This site's, not the map's.** LiveCount is every target the scan holds
                    // anywhere, so a row read "108 markers, 120 loaded" - two figures over two
                    // different populations sitting side by side, which is exactly the comparison
                    // the column exists to invite.
                    Safe.Read(() => Loaded(scan, Detonator.DetonatorGridPosition(gc)), 0)));
            }

            // The countdown starts again, which is what makes it mean "how long since this last got
            // better" rather than "how long until it gives up regardless".
            //
            // **After the gate, not before it.** It was the other way round for one revision, on the
            // reasoning that the search beating its own previous best is progress worth waiting on.
            // It is not what the words say: the timer is what the player reads as "time to improve",
            // and it was resetting while the score sat still - so a re-solve that could not beat the
            // standing plan ran the full budget looking settled the whole way.
            _until = DateTime.UtcNow + settle;
        }

        // **Measured, because a search makes the drawing stutter and nothing said why.**
        //
        // The search runs on a pool thread, and the per-stage byte counts read
        // GC.GetAllocatedBytesForCurrentThread - which is thread-local, so every byte the search
        // allocates was outside every figure the frame table reports. A collection is not: it stops
        // every thread in the process, and the drawing is one of them.
        //
        // Undercounts a search that fans out over several workers, since only the thread that runs
        // this delegate is counted. An undercount is still enough to say whether the search is the
        // order of magnitude that matters. See Spent, and the Solve line in the dump.
        // **What each worker is for, read once for the whole pool.** One line describes every worker, so a
        // worker cannot be given two roles by two rules that do not know about each other. See ThreadRoles.
        env = env with
        {
            RoundsPerWorker = Math.Max(0,
                Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.RoundsPerWorker.Value, 0)),
            StagnationKickPercent = Math.Max(0,
                Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.StagnationKickPercent.Value, 50)),
            StagnationKickRounds = Math.Max(0,
                Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.StagnationKickRounds.Value, 500)),
            PressWindowMs = pressWindowMs,
            StallRestartMs = Math.Max(0,
                Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.StallRestartMs.Value, 0)),
            Roles = ThreadRoles.Read(
                Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.ThreadRoles.Value, ""),
                Math.Max(1, Safe.Read(() => settings.Solver.Threads.Value, 8))),
        };

        // The environment the workers search under, roles included, so a reader of Env - the chain panel's list of
        // which worker takes which opening - sees the same one. It was stored before the roles were attached.
        _env = env;

        // **The openings, worked out once for the whole pool.** Every worker would otherwise draw its own
        // greedy opening and most of them would draw the same wrong one - see SolverSettings.ThreadRoles.
        // Here rather than in a worker because it is the same answer for all of them and it is not cheap
        // enough to do eight times.
        // **Worked out when a worker asks for one, rather than on a switch of its own.** A line that gives no
        // worker an enumerated opening is the same statement as switching the enumeration off, and two ways of
        // saying it can disagree. See ThreadRoles.
        // The tallies the dump reads are per solve, and the enumeration below resets them only when it runs.
        Repair.ForgetOpeningTallies();

        var wanted = false;

        foreach (var role in env.Roles ?? [])
            wanted |= role.Opening == ThreadRoles.Opens.Enumerated;

        // Read here, on the game's thread, because the settings tree is not the search's to touch.
        var openingLinks = Safe.Read(() => settings.Solver.Advanced.DestroyAndRepair.OpeningLinks.Value, 2);

        var share = budget;
        var settling = settle;

        // **The enumeration runs on the solve's task, not on the game's thread.** It was here, before the task,
        // and it costs one to two seconds on a Grand site - so every presolve pass whose marker set had changed
        // froze the game for that long. Measured on Craggy Peninsula while scouting: 1,944ms enumerating, a
        // worst tick of 2,299ms, eighteen passes about 2.4s apart. The workers already ask the ground from off
        // the game's thread, so the enumeration can too; Openings.Generate takes a lock for passes that overlap.
        // Its own thread at low priority, not a pool thread: it waits on the workers for the whole window. See
        // BackgroundWork.StartAtLowPriority.
        _search = BackgroundWork.StartAtLowPriority(() => MeasuredSearch(() =>
        {
            var run = env;

            // **Only the workers that open on an enumerated opening wait for the enumeration.** It ran to
            // completion before any worker started, so a worker told to continue, draw fresh or tour sat idle
            // for it too. Measured on Craggy Peninsula, twenty explosives: 1,898ms of enumeration a press, with
            // five of eight workers never reading what it produced.
            // Read before the enumeration, for its Said line. See RunningAtEnumeration.
            if (wanted)
                RunningAtEnumeration = Volatile.Read(ref _workersRunning);

            var enumerated = wanted
                ? BackgroundWork.StartAtLowPriority(() =>
                {
                    // A pass superseded before its turn at the enumeration's lock has nobody left to seed.
                    if (token.IsCancellationRequested)
                        return env;

                    Openings.Generate(env, openingLinks);

                    var seeds = new List<List<Vector2>>();

                    foreach (var opening in Openings.Last)
                        seeds.Add(opening.Links);

                    return seeds.Count > 0 ? env with { Openings = seeds } : env;
                })
                : null;

            // A line where every worker takes an enumerated opening has nobody to run meanwhile, so the window
            // waits for the enumeration as it did before any worker could start without it.
            var independent = false;

            for (var n = 0; n < threads; n++)
                independent |= !Repair.TakesEnumeratedOpening(run, n);

            if (enumerated != null && !independent)
                run = enumerated.Result;

            // **The improvement window starts now, when the first workers do, not when Start was called.**
            // Expire cancels on _until, so with the window counted from Start an enumeration run before the
            // workers spent it. Measured on Craggy Peninsula: 5.9s enumerating of an 8s window, and all eight
            // workers cancelled with 4 to 5.5s of their own deadlines left, having run nought rounds. A DateTime is written in one piece on the 64-bit runtime this loads
            // in, so the game's thread reading it for the countdown sees either value whole.
            // Not for a search already superseded: Start cancels the one it replaces, and that one reaching here late
            // would move the new search's countdown.
            if (!token.IsCancellationRequested)
                _until = DateTime.UtcNow + settle;

            var slots = WorkerSlots(threads);

            return Solving.Across(run, threads, Better,
                (n, publish) =>
                {
                    var mine = run;

                    // **Waits that end when the solve is cancelled.** A plain Result held a pool thread until the
                    // enumeration finished, however long ago the solve was superseded - and while scouting a
                    // presolve pass is superseded every one to six seconds, so the waiting workers of dead
                    // passes piled up behind enumerations queued on one lock. Waited for before taking a worker
                    // slot, so a worker idling on the enumeration does not hold one.
                    if (enumerated != null && Repair.TakesEnumeratedOpening(run, n))
                    {
                        try
                        {
                            enumerated.Wait(token);
                            mine = enumerated.Result;
                        }
                        catch (OperationCanceledException)
                        {
                            return Plan.Empty;
                        }
                    }

                    // A slot of the thread count, shared with every solve still running. See WorkerSlots.
                    try
                    {
                        slots.Wait(token);
                    }
                    catch (OperationCanceledException)
                    {
                        return Plan.Empty;
                    }

                    Interlocked.Increment(ref _workersRunning);

                    try
                    {
                        return Repair.Search(mine, share, settling, token, publish, n, seed);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _workersRunning);
                        slots.Release();
                    }
                },
                "repair");
        }), token);

        return Say("Solving path", $"planning {explosives} explosives over {env.Targets.Count} markers");
    }

    /// <summary>
    /// Runs a search with a stopwatch and an allocation counter round it. See the note above.
    /// </summary>
    private static Plan MeasuredSearch(Func<Plan> search) =>
        BackgroundWork.Record("search (co-ordinating)", search);

    /// <summary>
    /// The part of a whole route that is still ahead of the chain, or null when the chain is not on
    /// that route at all.
    ///
    /// **Placing the plan's own next link used to throw the plan away.** The test was equality on
    /// the placed count, so laying three explosives exactly where the plan asked and pressing again
    /// started the search from nothing: no seed, no floor, and a score that climbed back up from a
    /// greedy opening. The reason it was written that way is real - a plan solved with two
    /// explosives down is a chain FROM the second one, and once a third goes down its first link is
    /// history - but that argues for taking the rest of it, not for dropping all of it.
    ///
    /// Asked of a WHOLE route, the question is simply where along it the chain has got to, and both
    /// callers have one: this solve's own chain, and the best chain on file. Undo needs no separate
    /// answer - it is the same reading with a smaller number - which is the whole gain from writing
    /// the route down rather than reassembling it. It used to need a second branch, a snapshot of
    /// where the explosives were when the plan was made, and an argument about which of the game's
    /// two accounts of "how many are down" to believe.
    ///
    /// The explosives down have to BE the route's first links, in order, or the chain is on some
    /// other route and this one's tail is not a seed for it.
    /// </summary>
    /// <summary>
    /// Whether a chain has to be thrown away because a marker has been insisted on that it misses.
    ///
    /// **Not a preference and not a tie-break.** The objective adds a bonus per held must-take that is
    /// larger than the whole site, so the comparison between "holds it" and "does not" is decided
    /// before content is looked at. A chain that misses one is therefore beaten by every chain that
    /// holds one, and no amount of loot buys it back.
    ///
    /// So it is not a worse plan, it is not a plan. Keeping it as a seed anchors the search where the
    /// answer is not. See Start, and reroll_plan.md.
    /// </summary>
    private static bool Dropping(PlanEnvironment env, List<Vector2> chain)
    {
        if (env == null || env.Musts <= 0 || chain is not { Count: > 0 })
            return false;

        return Planner.Rate(env, chain).Held < env.Musts;
    }

    private static List<Vector2> Ahead(List<Vector2> chain, GameController gc, float owns)
    {
        if (chain is not { Count: > 0 })
            return null;

        var placed = Detonator.PlacedExplosiveGridPositions(gc) ?? [];

        // Only as far as the count and the positions agree. They are read from the same panel and
        // do not update on the same frame - an undo drops the count first and the array a frame
        // later - so a link is known to be down only when both say so. See Chain.
        var down = Math.Clamp(Math.Min(Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0),
            placed.Length), 0, chain.Count);

        // The same tolerance the overlay and the placement run use to call a spot laid. See
        // Astray.Owns - an explosive a few grid off a link is still that link's.
        for (var i = 0; i < down; i++)
        {
            if (Vector2.Distance(placed[i], chain[i]) >= owns)
                return null;
        }

        // Nothing left to place is not a seed, it is a finished chain.
        return down >= chain.Count ? null : chain.GetRange(down, chain.Count - down);
    }

    /// <summary>
    /// The combination this remnant is committed to, or null while the plan may choose for itself.
    ///
    /// Two ways to be committed and they are not the same kind of thing.
    ///
    /// **Rolled is a fact.** Liquid Verisium fixes the combination and the menu still opens, still
    /// clicks, and changes nothing - so a plan built around any other option is built around
    /// something that will not happen.
    ///
    /// **Overruling off is a promise.** The choice could still be changed; the player has said not
    /// to. The valuation deliberately no longer collapses the option list for it, so the plan's own
    /// preference survives to be drawn beside the choice it is deferring to - the pin belongs here,
    /// where the chain is built, rather than there, where the list is read.
    /// </summary>
    private static string Pinned(Target target, AutoExpeditionSettings settings, Valuation valuation)
    {
        // The recipe rather than its reward's name: two combinations can yield one reward and
        // propagate differently, and a pin is the one place with no second chance. See
        // Valuation.ChosenRecipeId.
        if (target.Rerolled)
            return Safe.Read(() => valuation?.ChosenRecipeId(target.Entity), null);

        // **A must take earned by a reward's price pins that reward.**
        //
        // Insisted.Automatic reads Reroll.Worth, which is the price of the richest offer, so a remnant
        // clears the threshold because of ONE combination. Marking the remnant and then leaving the
        // objective to choose freely among its offers honours the letter of that and none of the point:
        // reported from the game, a 471ex Masterwork Rune made a remnant must take and a 12ex reward
        // with better runes was taken instead. The chain took the remnant and left the money.
        //
        // The recipe rather than the reward's name, like every other pin here - two combinations can
        // yield one reward and propagate differently, and Reward carries the recipe it came from.
        // Rewards[0] because the list is ordered by price and that is the entry Worth read.
        //
        // **Below the two pins above it, both of which outrank it.** A rolled remnant cannot be
        // changed at all, so what it landed on is the only truth about it. And Overrule off means "do
        // not override my choice", which a pin from the planner would do precisely.
        if (Safe.Read(() => Insisted.Here.MarkedForRewardValue(target.Grid), false) &&
            Safe.Read(() => settings.Rewards.Overrule.Value, true))
        {
            var earned = Safe.Read(() => target.Rewards[0].Recipe, null);

            if (!string.IsNullOrEmpty(earned))
                return earned;
        }

        return Safe.Read(() => settings.Rewards.Overrule.Value, true)
            ? null
            : Safe.Read(() => valuation?.ChosenRecipeId(target.Entity), null);
    }

    /// <summary>
    /// Whether every spot of a chain is still somewhere an explosive may go.
    ///
    /// Placement only - not reach, not spacing. Those depend on the site and the throw distance,
    /// neither of which can change while the detonator and the placed count have not; what does
    /// change between solves is what the game has refused, and that is what this catches.
    /// </summary>
    private static bool Placeable(PlanEnvironment env, List<Vector2> chain) =>
        Placeable(env, chain, out _);

    private static bool Placeable(PlanEnvironment env, List<Vector2> chain, out string why)
    {
        why = "";

        for (var i = 0; i < chain.Count; i++)
        {
            if (env.CanPlace(chain[i]))
                continue;

            why = $"spot {i + 1} at {chain[i].X:0},{chain[i].Y:0} is refused ground now";

            return false;
        }

        // **And that every link can still be thrown, which is the half that was missing.**
        // The standing chain is kept as a floor and handed back when the search cannot beat it -
        // deliberately, so a re-solve can never make things worse. But a chain the game has since
        // said is impossible is not a floor, it is a wrong answer being protected: the search
        // correctly refuses to produce it, scores lower than the thing it is being compared against,
        // and Poll hands the impossible one back. That is why a spot covered in red crosses stayed
        // in the plan however many times it was re-solved.
        //
        // Only a definite no. Unknown is the router not having looked yet, which is the ordinary
        // state of most pairs at the start of a solve and no reason to throw a chain away.
        if (env.CanReach == null)
            return true;

        for (var i = 0; i < chain.Count; i++)
        {
            if (env.CanReach(i == 0 ? env.Origin : chain[i - 1], chain[i]) != Certainty.No)
                continue;

            why = $"the throw to spot {i + 1} at {chain[i].X:0},{chain[i].Y:0} is out of range";

            return false;
        }

        return true;
    }

    /// <summary>
    /// Fills in what each barrel detonates when an explosive reaches it.
    ///
    /// **One level deep: these objects do not set each other off.** ExpeditionIcons states it
    /// plainly - an oil derrick going off beside a Faridun explosive leaves it intact, and only a
    /// placed explosive triggers one. This walked a closure instead, on the reasoning that a barrel
    /// inside another barrel's radius must go off with it, and nothing here ever tested that.
    ///
    /// A barrel found inside another barrel's circle is therefore listed as content it catches, like
    /// any other marker, and its own circle is not added on top. See Planner, which stops the same
    /// expansion one level down.
    ///
    /// The radius is the barrel's own - the game states it per object - plus the marker's extent, on
    /// the same reasoning every other catch uses: a blast takes a thing when it touches it rather
    /// than when it reaches its middle.
    ///
    /// **Which objects those are is decided by the radius, not by TargetKind.Barrel.** Only
    /// ExplodingFill_BoomBarrel takes that kind, and a tileset states a blast on objects the
    /// classifier has never heard of - FaridunExplosive and OilWell in Stagnant Basin - which fall
    /// under Unknown and would otherwise contribute nothing but their own weight of one.
    /// </summary>
    private static void Chained(List<PlanTarget> targets, List<Target> content)
    {
        // The radius each one goes off with, matched back to the scan by position - PlanTarget
        // carries what the objective needs and this is the one thing it does not already have.
        //
        // Walked from the scan inward rather than over every plan target, because the handful that
        // state a radius is much the shorter of the two lists.
        var reach = new float[targets.Count];
        var barrels = new List<int>();

        foreach (var known in content)
        {
            if (known.Sets <= 0f)
                continue;

            for (var i = 0; i < targets.Count; i++)
            {
                if (reach[i] > 0f || Vector2.Distance(known.Grid, targets[i].Grid) >= 1f)
                    continue;

                reach[i] = known.Sets;
                barrels.Add(i);

                break;
            }
        }

        if (barrels.Count == 0)
            return;

        var found = new List<int>();

        foreach (var start in barrels)
        {
            found.Clear();

            for (var j = 0; j < targets.Count; j++)
            {
                if (j == start)
                    continue;

                var span = reach[start] + targets[j].Radius;

                if (Vector2.DistanceSquared(targets[start].Grid, targets[j].Grid) > span * span)
                    continue;

                found.Add(j);
            }

            targets[start] = targets[start] with { Sets = found.ToArray() };
        }
    }

    /// <summary>How little content makes a site too sparse for the band search to be worth running.</summary>
    private const int Sparse = 30;

    /// <summary>
    /// Collects the answer once the search has one. Called every tick; costs nothing until it does.
    /// </summary>
    public void Poll(AutoExpedition plugin = null, Scan scan = null)
    {
        Expire();

        if (_search == null || !_search.IsCompleted)
            return;

        var search = _search;
        _search = null;

        if (search.IsCanceled)
            return;

        if (search.IsFaulted)
        {
            Say("Failed", "the search failed: " + (search.Exception?.GetBaseException().Message ?? "unknown"));
            DebugWindow.LogError("[AutoExpedition] " + Detail, 5f);

            return;
        }

        // The standing chain wins a tie and wins a loss. See _standing. Re-described rather than
        // kept as the old Plan object so its numbers come from the environment just searched.
        Plan = _standing != null && _env != null &&
               (search.Result.Points.Count == 0 || search.Result.Weight < _floor)
            ? Planner.Describe(_env, _standing)
            : search.Result;

        // **What was on screen wins a tie, because a tie is not an improvement.**
        //
        // The search publishes only what beats what is drawn, and then hands back whatever it
        // finished holding - which can be a different chain of exactly equal worth. Under the
        // objective those two are the same answer; on screen they are two different routes, and the
        // plan quietly became the one nobody had been looking at. Keeping the published one costs
        // nothing by construction and makes what you watched be what you get.
        if (_live is { Count: > 0 } && _env != null && Plan.Points.Count > 0 &&
            Planner.Score(_env, _live) >= Planner.Score(_env, Plan.Points) - 0.0001d &&
            !ReferenceEquals(_live, Plan.Points))
            Plan = Planner.Describe(_env, _live);

        // What was last put on screen against what is being handed over, both on the same
        // environment. The score jumping the instant a pass ends says these two differ, and nothing
        // said by how much or in which direction - so it says it here, before _live is thrown away.
        var last = _live is { Count: > 0 } && _env != null
            ? Planner.Score(_env, _live)
            : double.NaN;

        Progress = $"env at ({_env?.Origin.X ?? 0f:0},{_env?.Origin.Y ?? 0f:0}) over " +
                   $"{_env?.Targets.Count ?? 0} markers, {_env?.Explosives ?? 0} explosives; " +
                   $"run {_runs} of this site on random stream {Planner.Stream}, " +
                   (_floor > 0d
                       ? $"opened with {_floor:N1} inherited"
                       : "opened from nothing") +
                   $", ended at {Plan.Plain:N1} - " +
                   $"{_found} improvement{(_found == 1 ? "" : "s")} drawn over " +
                   $"{Planner.Rounds} restart round{(Planner.Rounds == 1 ? "" : "s")}" +
                   (double.IsNaN(last)
                       ? "; nothing was published to the screen during it"
                       : $"; the last chain published to the screen scored {last:N1} over " +
                         $"{_live.Count} links, the plan handed over scores " +
                         $"{(_env == null ? Plan.Plain : Planner.Plainly(_env, Plan.Points)):N1} " +
                         $"over {Plan.Points.Count}");

        // **Closed here, where the plan it produced is settled.** Read any later and the figure
        // belongs to whichever solve has started since - which is what the presolve's own history
        // did, filing a pass that published 6,928 as having ended at 1,669.6 because another solve
        // had begun before anything got round to asking. See Run.
        CloseRun(_found > 0 ? "ran its window out" : "ran its window out having published nothing");

        // One entry per press, in order, so the climb across presses can be read as a series. See
        // Climb.
        lock (_climb)
        {
            if (_climb.Count >= Climbs)
                _climb.RemoveAt(0);

            // **Recorded as the figure a person reads, not as the objective.**
            //
            // The objective carries a synthetic bonus for holding a marker the player insisted on, so on
            // a site with one must-take this series was reporting 18,195 to 18,391 for a chain worth
            // 12,102 to 12,298. Six thousand of every entry was insistence rather than loot, and the
            // gain between two presses - the whole point of the series - was buried in it. See
            // Verdict.Plain and Solving.Watching, which had the same fault.
            _climb.Add((_runs,
                _standing == null || _env == null ? 0d : Planner.Plainly(_env, _standing),
                Plan.Points.Count > 0 && _env != null
                    ? Planner.Plainly(_env, Plan.Points)
                    : 0d));
        }

        ProvenBest = Planner.StoppedAtRelaxedCeiling && Plan.Points.Count > 0;



        _standing = null;
        _floor = 0d;

        // Whatever the strategy returned, spend the explosives it left behind. See Planner.Complete.
        if (_env != null && Plan.Points.Count > 0 && Plan.Points.Count < _env.Explosives)
        {
            var full = Planner.Complete(_env, Plan.Points);

            if (full.Count > Plan.Points.Count)
                Plan = Planner.Describe(_env, full);
        }

        _live = null;

        Solves++;

        // **The route is written down here, whole, and this is the only solve-side place it is.**
        // Everything above has been deciding what the tail should be - the standing chain, the
        // published chain, the leftover explosives - and those arguments are finished. The head is
        // the ground as it stood when this solve was given its environment, which is what the tail
        // was planned from. See _behind.
        _laid = _behind.Length;
        _chain = [.._behind, ..Plan.Points];

        // One line per solve, so the pair of runs at this site can be read back later. See Trials.
        if (plugin != null && _env != null)
        {
            var remnants = 0;
            var rares = 0;

            foreach (var target in _env.Targets)
            {
                if (target.Kind == TargetKind.Remnant)
                    remnants++;
                else if (target.Kind == TargetKind.Elite)
                    rares++;
            }

            Trials.Record(plugin, _strategy, _area, _level, _env.Origin, Plan,
                _env.Targets.Count, remnants, rares, _env.Explosives,
                (DateTime.UtcNow - _began).TotalSeconds,
                Edges.Last);
        }

        if (Plan.Points.Count == 0)
        {
            Say("No route", Plan.Note.Length > 0 ? Plan.Note : "no plan could be made");
        }
        else
        {
            // Nothing to say in the short form: how much of the plan is down is more useful there,
            // and the overlay works that out from the game rather than from anything kept here.
            //
            // **Unless the plan breaks a rule, which is the one thing it must not do quietly.** A
            // chain that drops a must-take used to look exactly like one that took everything, so
            // the only way to find out was to count the marks on the minimap by eye.
            Say(Plan.Note.Length > 0 ? "Invalid" : "",
                Plan.Note.Length > 0
                    ? Plan.Note
                    : $"{Plan.Points.Count} explosives over {Plan.Covered} markers");
        }
    }

    /// <summary>
    /// Throws the plan away when the dig site underfoot is not the one it was made for.
    ///
    /// A map can hold several expeditions. Clear one, walk to the next, and the plan for the first
    /// is still sitting there looking ready - so the key means "place" when it should mean "scan",
    /// and what it would place is a chain of positions belonging to a dig site several screens
    /// back. The overlay already refused to DRAW it, which made the state invisible rather than
    /// harmless.
    ///
    /// Keyed on the detonator position, which is what tells one site from another everywhere else.
    /// </summary>
    /// <returns>True when a plan was discarded, so the caller can stop whatever it was doing.</returns>
    /// <summary>
    /// Whether the standing plan answers a question that has since changed, and wants re-solving.
    ///
    /// **A weight edited mid-site changed nothing you could see.** Scan already re-prices a remnant's
    /// rewards when either revision moves, which covers the reward text - but the plan, its score and
    /// every weight baked into the environment were computed once and kept. Setting a rare monster to
    /// two thousand left the score, and the numbers drawn over the ground, exactly as they were: the
    /// edit had applied and nothing had re-read it. Pressing solve again was the only way to see your
    /// own change, which reads like the setting not working.
    ///
    /// **Settled before acting, because a weight is dragged rather than typed.** One adjustment is
    /// dozens of changes; solving on each would start and cancel a search every frame and never
    /// finish one. The plan is re-solved once the numbers have held still for a moment instead.
    ///
    /// The table's revision, which is now the whole of it: every weight is a row, so there is no second
    /// for the settings tree behind it.
    /// </summary>
    public bool Stale(AutoExpeditionSettings settings, int settleMs = 400)
    {
        var table = Wrt.Revision;

        // **A setting that changes the reach model has to be counted here.** One was, briefly, and
        // taught the lesson: the environment holds the reach test as a delegate built when the
        // environment was built, so a setting picking a different test never reaches the plan - or the
        // crosses drawn from it - until something else happens to trigger a solve. Nothing did, and
        // the two models appeared to agree. Kept as a slot rather than deleted for whatever replaces
        // it. See NOTES section 2b.
        const int model = 0;

        // **The weights had a revision of their own and no longer do.** Every weight is a row, so the
        // table's revision is the whole of "has an answer changed". Kept as a name rather than folded
        // away so the three-way comparison below still reads as three things. See Wrt.Revision.
        var weighed = table;

        // Nothing solved yet, so there is nothing to be stale: adopt whatever is current.
        if (_tableAt < 0 || Site == Vector2.Zero)
        {
            EnumeratedSolveOutcome(table, weighed, model);

            return false;
        }

        if (table == _tableAt && weighed == _weighedAt && model == _modelAt)
            return false;

        // Moved again - restart the clock rather than counting from the first change of a drag.
        if (table != _sawTable || weighed != _sawWeighed || model != _sawModel)
        {
            _sawTable = table;
            _sawWeighed = weighed;
            _sawModel = model;
            _moved = DateTime.UtcNow;

            return false;
        }

        if (DateTime.UtcNow - _moved < TimeSpan.FromMilliseconds(Math.Max(0, settleMs)))
            return false;

        EnumeratedSolveOutcome(table, weighed, model);

        return true;
    }

    /// <summary>Records which revisions the plan now answers. See Stale.</summary>
    private void EnumeratedSolveOutcome(int table, int weighed, int model)
    {
        _tableAt = table;
        _weighedAt = weighed;
        _modelAt = model;
        _sawTable = table;
        _sawWeighed = weighed;
        _sawModel = model;
        _moved = DateTime.MinValue;
    }

    public bool Moved(Vector2 site)
    {
        if (Site == Vector2.Zero || site == Vector2.Zero || Vector2.Distance(site, Site) < 1f)
            return false;

        Forget();

        return true;
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            Forget();

            // **And the climb, which is a statement about ONE site's floor.**
            //
            // It was cleared with the plan but not with the area, so walking into a second dig
            // appended to the first one's series - read back as "34: 75,276, 35: from nothing to
            // 16,824, ... 56: 2,914", four sites' presses in one list with nothing saying where one
            // ended. A series whose entries describe different questions is worse than no series.
            Forgetting();
        }

        _area = areaHash;
        ShownAt = Vector2.Zero;
    }

    /// <summary>
    /// The site score each time a solve at this site published a better chain, across solves, for asking how fast it
    /// is still climbing. Cleared with the site. See SiteScoreRise.
    /// </summary>
    private static readonly List<(DateTime At, double Plain)> _siteScores = new();

    /// <summary>Notes the site score a better chain reached. Called from the search's threads.</summary>
    private static void NoteSiteScore(double plain)
    {
        lock (_siteScores)
        {
            if (_siteScores.Count > 0 && plain <= _siteScores[^1].Plain)
                return;

            if (_siteScores.Count >= 512)
                _siteScores.RemoveAt(0);

            _siteScores.Add((DateTime.UtcNow, plain));
        }
    }

    private static void ForgetSiteScores()
    {
        lock (_siteScores)
            _siteScores.Clear();
    }

    /// <summary>
    /// How much the site score rose over the last window, as a fraction of where it stood at the window's start.
    /// Positive infinity when nothing has been noted for a whole window yet, since a rise cannot be ruled out.
    /// </summary>
    internal static double SiteScoreRise(TimeSpan window)
    {
        lock (_siteScores)
        {
            if (_siteScores.Count == 0)
                return double.PositiveInfinity;

            var from = DateTime.UtcNow - window;
            var before = -1;

            for (var i = _siteScores.Count - 1; i >= 0 && before < 0; i--)
            {
                if (_siteScores[i].At <= from)
                    before = i;
            }

            if (before < 0 || _siteScores[before].Plain <= 0d)
                return double.PositiveInfinity;

            return (_siteScores[^1].Plain - _siteScores[before].Plain) / _siteScores[before].Plain;
        }
    }

    /// <summary>This site's improvement window in milliseconds: the "Time to improve" setting for its size.</summary>
    internal static int ImprovementWindowMs(AutoExpeditionSettings settings, int links) => Settling(settings, links);

    /// <summary>Throws the plan away, for a new area or a new question.</summary>
    public void Forget()
    {
        Cancel();
        ForgetSiteScores();
        Plan = Plan.Empty;
        _chain = [];
        _laid = 0;
        Site = Vector2.Zero;
        Status = "";
        Detail = "";

        // **What the last detailed pass published describes a chain that has just been thrown away.**
        //
        // Both tables are cleared at the START of a detailed pass, which is the right place while one
        // is always coming - and after a plan is deleted none is: Breakdown returns early on an empty
        // tail with nothing laid, so no pass runs and the entries survive the plan they describe. The
        // yellow line then reports runes landing on a remnant no chain reaches, and Options.EnumeratedSolveOutcome
        // hands the ground text and the placement run a combination chosen for a route that is gone.
        //
        // Cleared here because this is where the route stops existing. See Planner.Chosen.
        Planner.RuneTallyByRemnant.Clear();
        Planner.Chosen.Clear();
    }

    /// <summary>
    /// Which draw of the random numbers the next solve gets. See PlanEnvironment.Draw.
    ///
    /// **Bumped by whoever wants two solves to differ, and by nobody else.** Left alone it stays at
    /// nought, which is the search this plugin has always run - every seed a constant plus the worker's
    /// number, so a site's answer is reproducible to the decimal. Two things bump it: a batch measuring
    /// whether a change helps, or its five presses are one press counted five times (see RepeatedPresses);
    /// and the continuous reroll mode, before each pass past the one the presolve would have stopped on,
    /// because a pass seeded from the same chain with the same numbers repeats the pass before it (see
    /// Rehearsal.Continuing).
    /// </summary>
    public static int Draws { get; set; }

    /// <summary>Says one thing two ways: a word for the HUD, a sentence for the debug line.</summary>
    private string Say(string status, string detail)
    {
        Status = status;
        Detail = detail;

        return status;
    }

    /// <summary>
    /// Stops a search that is running, keeping the best chain it had reached.
    ///
    /// The key that starts a solve stops one, which is the behaviour a player expects from a thing
    /// that is visibly working: pressing it again while the bar is counting down used to throw the
    /// search away and start an identical one, so there was no way to say "that is good enough,
    /// stop". The best chain found so far is already published, so stopping keeps it rather than
    /// losing it.
    /// </summary>
    /// <returns>Whether there was a search to stop.</returns>
    public bool Stop(string by = "the player, with the action key")
    {
        if (_search == null || _env == null)
            return false;

        // Whatever the search had reached is the answer now. Live is published from the search's own
        // thread on every improvement, so it is the best chain rather than a half-built one.
        var reached = _live;

        Cancel();

        if (reached is { Count: > 0 })
        {
            Plan = Planner.Describe(_env, reached);

            // Written down whole, exactly as a solve that ran to the end does it. Stopping early is
            // still a solve producing a route, and a route half recorded is the state every reader
            // used to have to guess its way out of. See Poll.
            Solves++;
            _laid = _behind.Length;
            _chain = [.._behind, ..Plan.Points];

            Say("Stopped", $"stopped early - keeping the best chain found, over {Plan.Points.Count} spots");
        }
        else
        {
            Say("Stopped", "stopped before it had found anything");
        }

        CloseRun($"stopped by {by}");

        _live = null;

        return true;
    }

    /// <summary>
    /// Ends a search whose improvement window has run out.
    ///
    /// **The countdown used to be a readout of a stop rather than the stop itself.** The search kept
    /// its own clock and only consulted it between operators, so one polish pass over a three
    /// hundred marker site, or the band slice the mixed strategy runs first, could carry it well
    /// past zero - and "Solving 0.0s" sat on screen looking like a hang. Two clocks that agree most
    /// of the time are worse than one, because the times they disagree are the times somebody is
    /// watching.
    ///
    /// Cancelling is enough. The token is what Waiting already tests, so the search returns its best
    /// chain normally at the next check rather than throwing - nothing is lost, and nothing has to
    /// be unwound.
    ///
    /// Not while there is no chain yet. A cold site spends its first seconds discovering which links
    /// are legal at all, and the search deliberately treats that as progress; stopping on the dot
    /// there would end the solve on whatever the straight line happened to allow, which is the thing
    /// that rule exists to prevent.
    /// </summary>
    private void Expire()
    {
        if (_search is not { IsCompleted: false } || DateTime.UtcNow <= _until)
            return;

        if (_live is { Count: > 0 } || Plan.Points.Count > 0)
            _cancel?.Cancel();
    }

    private void Cancel()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
        _search = null;
    }
}
