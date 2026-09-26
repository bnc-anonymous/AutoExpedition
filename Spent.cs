using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Where a frame goes, measured rather than reasoned about.
///
/// **A plugin reported at eighty per cent of the host's time is a number with no second half.**
/// ExileCore2 times the whole of Render and nothing inside it, so the only thing that can be said
/// from its figure is that this plugin is the expensive one - which leaves a dozen candidates and
/// no way to order them. Every attempt to rank them by reading is a guess, and the drawing here is
/// full of things that are cheap per item and run over tens of thousands of items.
///
/// So each stage is timed where it runs, and the dump prints the table. One frame says nothing -
/// a solve lands in one of them and a garbage collection in another - so what is kept is the total
/// over a window of frames and the worst single frame in it, which are the two figures that
/// separate "this is always slow" from "this occasionally stalls".
///
/// **Cheap enough to leave on.** A Stopwatch timestamp is a few nanoseconds against stages measured
/// in hundreds of microseconds, and the alternative - a switch nobody turns on until something is
/// already wrong - means the measurement never exists for the frame that caused the complaint.
/// </summary>
internal static class Spent
{
    /// <summary>How many frames a window covers before the figures start again.</summary>
    private const int Window = 240;

    /// <summary>
    /// The stage that contains the others, so the table can say what they do not account for.
    ///
    /// A parent and its children in one list is only readable if the difference is stated: the
    /// children are named without a dot and the parent with one, which is the same rule the stage
    /// names already follow.
    /// </summary>
    private const string Whole = "Overlay.Draw";

    /// <summary>
    /// The other entry point, timed whole beside its parts.
    ///
    /// **ExileCore2 lists this plugin twice and only one of them was ever measured.** Render is
    /// what draws; Tick is the once-a-frame work behind it - the scan, the pricing, the polling,
    /// the saving - and every stage timed until now was inside Render. A host reporting an average
    /// this plugin's own table could not account for is the signature of a whole entry point going
    /// unwatched.
    /// </summary>
    private const string Other = "Tick";

    /// <summary>
    /// The stages that run OUTSIDE the whole, so the untimed figure does not subtract them from it.
    ///
    /// Named rather than pattern-matched. The first version took anything with a dot in its name to
    /// be outside, which is how Minimap.Draw - squarely inside - came off the total and overstated
    /// the gap by three quarters of a millisecond. A list is checkable against the call sites; a
    /// rule about punctuation is not.
    /// </summary>
    private static readonly HashSet<string> Outside = new(StringComparer.Ordinal)
    {
        "Catalogue.Draw", "Options.Draw", "Landed.Observe", "Panels.Covered", "Cleared.Observe",

        // Inside Reachable, which is itself inside the whole - so counting them again would take
        // them off the total twice. A stage with a slash in it is a part of the stage before it.
        "Ready/Pointed", "Ready/Owed", "Ready/Blocked", "Reachable/Ready", "Forbidden/Gather",
        "Scan.Tick/Sweep", "Scan.Tick/Keep", "Scan.Tick/Refresh", "Blocked/Furnished", "Blocked/Covered",
        "Banners/Follow", "Banners/Reads", "Banners/Search",



        // Inside the sweep, which is inside Scan.Tick. Timed per entity, so their call counts say
        // how many entities took each branch as well as what each branch costs.
        "Sweep/GridPos", "Sweep/Metadata", "Sweep/Correct", "Sweep/Sockets", "Sweep/Classify",
        "Sweep/Filter",

        // Called from everywhere, so its time is already inside whichever stage called it. Counted
        // as its own line to say how much of the frame goes on one reading of the game, and left
        // out of the sum for the same reason the others here are.
        "Detonator.PlacedExplosiveGridPositions", "Remnants/Labels", "Remnants/Unpriced", "Remnants/Anchor",
        "Remnants/Take", "Readout/MeasureText", "Status/Paint", "Status/ToggleRect",

        // The rest of the parts of Remnants. Advice and Waves were missing from this list while
        // being timed, so the untimed figure had them subtracted twice - a fifth of a millisecond
        // over a window, which is small enough to have gone unnoticed and wrong all the same.
        "Remnants/Advice", "Remnants/Waves", "Remnants/Button", "Remnants/Rewards",

        // Inside Ready/Owed, which is inside Reachable/Ready.
        "Owed/Selection", "Owed/Rates",
    };

    private static readonly Dictionary<string, Tally> Stages = new(StringComparer.Ordinal);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static int _frames;

    private sealed class Tally
    {
        public double Total;
        public long Bytes;
        public double Worst;
        public int Hits;
        public long LastBytes;
        public double LastTotal;
        public double LastWorst;
        public int LastHits;
    }

    /// <summary>
    /// Times one stage. Dispose ends it, so a stage is a using statement and cannot be left open.
    /// </summary>
    public static Timer On(string stage) => new(stage);

    internal readonly struct Timer : IDisposable
    {
        private readonly string _stage;
        private readonly long _from;
        private readonly long _had;

        public Timer(string stage)
        {
            _stage = stage;
            _had = GC.GetAllocatedBytesForCurrentThread();
            _from = Clock.ElapsedTicks;
        }

        public void Dispose()
        {
            var took = (Clock.ElapsedTicks - _from) * 1000d / Stopwatch.Frequency;

            // **What a stage allocates, beside what it costs.** A stage that is slow because it is
            // doing arithmetic and one that is slow because it is building throwaway strings look
            // identical in a column of milliseconds, and they want opposite fixes. They are told
            // apart here: the matcher that took a rule apart on every query read 24 microseconds an
            // entity and several hundred bytes with it, and the bytes are what named the cause.
            var grew = GC.GetAllocatedBytesForCurrentThread() - _had;

            lock (Stages)
            {
                if (!Stages.TryGetValue(_stage, out var tally))
                {
                    tally = new Tally();
                    Stages[_stage] = tally;
                }

                tally.Total += took;
                tally.Bytes += grew;
                tally.Hits++;

                if (took > tally.Worst)
                    tally.Worst = took;
            }
        }
    }

    /// <summary>
    /// Closes a frame. Called once per Render, and rolls the window over when it is full.
    ///
    /// The window is rolled rather than decayed so the figures are a straight sum over a known
    /// number of frames: an average with a decay constant in it is a number nobody can check
    /// against a stopwatch.
    /// </summary>
    /// <summary>
    /// Starts the window and every running total again.
    ///
    /// **A measurement run should measure what it caused.** The pause figure, the collection
    /// counts, the allocation totals and the leaf counts all run from when the plugin loaded, so a
    /// change tested by pressing the reset button and then dumping was being read against
    /// everything that happened before the press - including the area that was walked through to
    /// get somewhere worth testing. See Caches.Clear.
    /// </summary>
    public static void Forget()
    {
        lock (Stages)
        {
            Stages.Clear();
            _frames = 0;
            Framed = 0;
            Paused = 0d;
            Widest = 0d;
            Over20 = 0;
            Over50 = 0;
            _widest = 0d;
            _over20 = 0;
            _over50 = 0;
            _lastFrame = 0L;
            Collected = (0, 0, 0);
            Allocated = (0L, 0L, 0L);
            _mine = GC.GetAllocatedBytesForCurrentThread();
            _paused = GC.GetTotalPauseDuration();
            _allocated = GC.GetTotalAllocatedBytes(false);
            _counted = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }

        LeafCalls.Forget();
    }

    public static void Frame()
    {
        // **The frame itself, which is the only figure that can see a hang this plugin did not
        // cause.** Every stage measures code this plugin runs; a stop that lands in the host's loop,
        // or between Tick and Render, or in a collection nobody's stopwatch was open across, is
        // invisible to all of them - and the table then reports a worst frame of seven milliseconds
        // about a session with visible hangs in it.
        //
        // Wall time between one Render and the next. If this says a hundred milliseconds while the
        // stages sum to seven, the hang is real and is not in the stages.
        var ticked = Clock.ElapsedTicks;

        if (_lastFrame > 0L)
        {
            var apart = (ticked - _lastFrame) * 1000d / Stopwatch.Frequency;

            if (apart > _widest)
                _widest = apart;

            if (apart > 50d)
                _over50++;
            else if (apart > 20d)
                _over20++;
        }

        _lastFrame = ticked;

        lock (Stages)
        {
            if (++_frames < Window)
                return;

            foreach (var tally in Stages.Values)
            {
                tally.LastBytes = tally.Bytes;
                tally.Bytes = 0L;
                tally.LastTotal = tally.Total;
                tally.LastWorst = tally.Worst;
                tally.LastHits = tally.Hits;
                tally.Total = 0d;
                tally.Worst = 0d;
                tally.Hits = 0;
            }

            var now = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

            Collected = (now.Item1 - _counted.Gen0, now.Item2 - _counted.Gen1,
                now.Item3 - _counted.Gen2);
            _counted = now;

            var grew = GC.GetTotalAllocatedBytes(false);
            var ours = 0L;

            // LastBytes, not Bytes: the roll above has already moved the window's totals across
            // and zeroed the live ones, so reading Bytes here reported nought for every stage and
            // made this plugin look like it allocated nothing at all.
            foreach (var (stage, tally) in Stages)
                if (string.Equals(stage, Whole, StringComparison.Ordinal) ||
                    string.Equals(stage, Other, StringComparison.Ordinal))
                    ours += tally.LastBytes;

            // **The whole of this thread, beside the part of it inside a stage.**
            //
            // Three figures rather than two, because two could not tell apart the only two answers
            // that matter. A gap between the process and this plugin's stages is either work on
            // other threads - the host's, another plugin's - or work on THIS thread that no stage
            // has a stopwatch round, and the first says stop looking here while the second says
            // look harder. Frame runs once per Render, so this thread is the drawing thread.
            var mine = GC.GetAllocatedBytesForCurrentThread();

            Allocated = (grew - _allocated, ours, mine - _mine);
            _allocated = grew;
            _mine = mine;

            var stopped = GC.GetTotalPauseDuration();

            Paused = (stopped - _paused).TotalMilliseconds;
            _paused = stopped;

            LeafCalls.Rolled();

            Widest = _widest;
            Over20 = _over20;
            Over50 = _over50;
            _widest = 0d;
            _over20 = 0;
            _over50 = 0;

            Framed = _frames;
            _frames = 0;
        }
    }

    /// <summary>How many frames the reported window covered, or nought before the first one closed.</summary>
    public static int Framed { get; private set; }

    /// <summary>
    /// Collections during the reported window, by generation.
    ///
    /// **Stages that are unrelated do not stall together.** A table where the drawing, the sweep and
    /// the banners each report a worst frame near thirty milliseconds is not three slow stages - it
    /// is the process stopping, and whichever stage held the stopwatch when it did wears the whole
    /// pause. Ranking stages by their worst frame is meaningless until that is ruled out, and the
    /// collection count is what rules it out: a window with no gen2 collection in it did not stop
    /// for one.
    /// </summary>
    public static (int Gen0, int Gen1, int Gen2) Collected { get; private set; }

    /// <summary>
    /// How many milliseconds of the window the process spent stopped for collection.
    ///
    /// **The count says how often, this says how long, and only the second one is the stall.** A
    /// window with twenty collections in it and two milliseconds of pause has not stalled; the same
    /// count with sixty milliseconds of pause has. Inferring one from the other is what the counts
    /// alone invited, and a thirty millisecond frame is either most of this figure or none of it -
    /// which settles whether to keep looking inside the plugin at all.
    /// </summary>
    public static double Paused { get; private set; }

    /// <summary>The longest gap between two Renders in the window, in milliseconds. See Frame.</summary>
    public static double Widest { get; private set; }

    /// <summary>How many frames in the window took over 20ms, and over 50ms. See Frame.</summary>
    public static int Over20 { get; private set; }

    /// <summary>See Over20.</summary>
    public static int Over50 { get; private set; }

    private static long _lastFrame;

    private static double _widest;

    private static int _over20;

    private static int _over50;

    /// <summary>
    /// What the whole process allocated over the window, against what this plugin's stages did.
    ///
    /// **A collection stops everything, so whose garbage caused it is the question.** This plugin
    /// shares a process with the host and every other plugin, and a pause measured here is a pause
    /// they all take - including the one whose drawing looks smooth. Reducing what this plugin
    /// allocates is only worth doing if this plugin is what the collections are for, and the stage
    /// bytes cannot say that on their own: they are a share of an unknown total.
    ///
    /// Not exact - the process figure counts every thread and the stages only count their own - so
    /// it is read as a proportion and not a subtraction.
    /// </summary>
    public static (long Process, long Ours, long Thread) Allocated { get; private set; }

    private static long _allocated = GC.GetTotalAllocatedBytes(false);

    private static long _mine = GC.GetAllocatedBytesForCurrentThread();

    private static TimeSpan _paused = GC.GetTotalPauseDuration();

    private static (int Gen0, int Gen1, int Gen2) _counted =
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>
    /// The table, worst first.
    ///
    /// Per frame rather than per call, because the question is what a frame costs: a stage that
    /// runs once every sixty frames and takes a millisecond is a tenth of the cost of one that
    /// takes twenty microseconds every frame, and the per-call figure says the opposite.
    /// </summary>
    public static string Table()
    {
        var said = new StringBuilder();

        lock (Stages)
        {
            // **The window in progress when there is no closed one**, because a dump taken in the
            // first four seconds after a reload is exactly the dump somebody takes to find out why
            // the reload was needed. It refused to say anything at all then, which made the tool
            // useless at the one moment it was reached for.
            //
            // Said as partial, since a short window is a noisier one: a single stall lands in a
            // tenth of eighty frames and a fortieth of a full one.
            var closed = Framed > 0;
            var over = closed ? Framed : _frames;

            if (over == 0)
                return "  nothing measured yet - no frame has finished";

            said.AppendLine(closed
                ? $"  over the last {over} frames, worst stage first:"
                : $"  over {over} frames so far, worst stage first - a PARTIAL window, " +
                  $"{Window} is a whole one:");
            said.AppendLine(
                "    stage                         per frame    worst      calls   bytes/frame");

            // **What the stages do not account for**, which is the figure that matters when a
            // whole is timed beside its parts. A parent measured at fifteen milliseconds whose
            // children come to three is not a slow child - it is twelve milliseconds of code
            // nobody has put a stopwatch round, and no ranking of the children can find it.
            var whole = 0d;
            var parts = 0d;

            foreach (var (stage, tally) in Stages)
            {
                if (string.Equals(stage, Whole, StringComparison.Ordinal) ||
                    string.Equals(stage, Other, StringComparison.Ordinal))
                    whole += closed ? tally.LastTotal : tally.Total;
                else if (!Outside.Contains(stage))
                    parts += closed ? tally.LastTotal : tally.Total;
            }

            if (whole > 0d)
            {
                said.AppendLine($"    {"(Draw and Tick, untimed)",-28} " +
                                $"{Math.Max(0d, whole - parts) / over,8:0.000}ms " +
                                $"{"-",8} {"-",8} {"-",13}");
            }

            var order = new List<KeyValuePair<string, Tally>>(Stages);

            order.Sort((a, b) => (closed ? b.Value.LastTotal : b.Value.Total)
                .CompareTo(closed ? a.Value.LastTotal : a.Value.Total));

            foreach (var (stage, tally) in order)
            {
                var total = closed ? tally.LastTotal : tally.Total;
                var worst = closed ? tally.LastWorst : tally.Worst;
                var hits = closed ? tally.LastHits : tally.Hits;

                if (hits == 0)
                    continue;

                var bytes = closed ? tally.LastBytes : tally.Bytes;

                said.AppendLine($"    {stage,-28} {total / over,8:0.000}ms " +
                                $"{worst,8:0.000}ms {hits,8:N0} {bytes / over,13:N0}");
            }
        }

        return said.ToString();
    }
}
