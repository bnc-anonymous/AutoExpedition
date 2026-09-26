using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// What the work off the drawing thread costs, kept where a frame window cannot lose it.
///
/// **Spent could not report a solve, and the reason generalises.** A stage there is a using block
/// that records on Dispose into a window of 240 frames, which is about four seconds. A solve runs
/// longer than that and often much longer, so a dump taken while one is in flight sees nothing at
/// all - the block has not closed - and a dump taken after one sees nothing either, because the
/// window it closed in has already rolled away. Three dumps were taken around a solve and every one
/// of them reported no solve.
///
/// So this does not roll. It counts jobs and adds up what they took, from the plugin loading or
/// from the last reset, and it says what is running right now.
///
/// **Bytes are the point of it.** A search allocates on a pool thread, and a collection stops every
/// thread in the process - including the one drawing. Nothing else in the plugin could see those
/// bytes: the frame table reads GetAllocatedBytesForCurrentThread, which is thread-local, so a
/// search's garbage fell outside every figure it reports while causing the pauses it blames on
/// nobody.
///
/// Counted per thread that runs a job, which is why the workers a search fans out over are recorded
/// individually rather than by the thread that starts them - that one does almost nothing and
/// measuring it says a search is free.
/// </summary>
internal static class BackgroundWork
{
    private sealed class Tally
    {
        public long Runs;
        public double Milliseconds;
        public long Bytes;
        public double Worst;
    }

    private static readonly Dictionary<string, Tally> Jobs = new(StringComparer.Ordinal);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static int _running;

    /// <summary>How many jobs are in flight, which is what a dump taken mid-solve wants to say.</summary>
    public static int Running => Volatile.Read(ref _running);

    /// <summary>
    /// Runs a job, recording how long it took and what it allocated on the thread it ran on.
    /// </summary>
    public static T Record<T>(string job, Func<T> work)
    {
        var had = GC.GetAllocatedBytesForCurrentThread();
        var from = Clock.ElapsedTicks;

        Interlocked.Increment(ref _running);

        try
        {
            return work();
        }
        finally
        {
            Interlocked.Decrement(ref _running);

            var took = (Clock.ElapsedTicks - from) * 1000d / Stopwatch.Frequency;
            var grew = GC.GetAllocatedBytesForCurrentThread() - had;

            lock (Jobs)
            {
                if (!Jobs.TryGetValue(job, out var tally))
                {
                    tally = new Tally();
                    Jobs[job] = tally;
                }

                tally.Runs++;
                tally.Milliseconds += took;
                tally.Bytes += grew;

                if (took > tally.Worst)
                    tally.Worst = took;
            }
        }
    }

    /// <summary>The same for a job that returns nothing.</summary>
    public static void Record(string job, Action work) =>
        Record(job, () =>
        {
            work();

            return 0;
        });

    /// <summary>Starts the counts again. See Caches.Clear.</summary>
    public static void Forget()
    {
        lock (Jobs)
            Jobs.Clear();
    }

    /// <summary>The table, most allocated first, since that is what stops the drawing.</summary>
    public static string Table()
    {
        var said = new StringBuilder();

        lock (Jobs)
        {
            if (Jobs.Count == 0)
                return $"  no work off the drawing thread has finished yet" +
                       (Running > 0 ? $" - {Running} running now" : "") + "\n";

            said.AppendLine($"  work off the drawing thread since the last reset" +
                            (Running > 0 ? $", {Running} running now" : "") + ":");
            said.AppendLine("    job                            runs      total      worst        allocated");

            var order = new List<KeyValuePair<string, Tally>>(Jobs);

            order.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));

            foreach (var (job, tally) in order)
            {
                said.AppendLine($"    {job,-28} {tally.Runs,6:N0} {tally.Milliseconds,9:0.0}ms " +
                                $"{tally.Worst,9:0.0}ms {tally.Bytes / 1024,13:N0}KB");
            }
        }

        return said.ToString();
    }
}
