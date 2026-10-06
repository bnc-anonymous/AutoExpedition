using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoExpedition;

/// <summary>
/// When each step of arriving at a dig site happened, counted from the area change: the detonator's entity, the
/// detonator panel, the first markers, every remnant priced, the first presolve pass and the first frame anything was
/// drawn. For the dump.
///
/// **Entering an expedition showed nothing for five to ten seconds, and nothing said which wait it was.** Render draws
/// nothing until the detonator panel exists, the presolve does not start until every remnant is priced or ten seconds
/// have passed, and on a Frigid Bluffs site (2026-10-04) the first presolve pass began 19.5 s after the site was first
/// seen. Each wait is stamped here once per area, so a dump taken at the detonator says which one it was.
/// </summary>
internal static class SiteArrival
{
    /// <summary>The steps of arriving, in the order they are expected. See Note.</summary>
    internal enum Step
    {
        DetonatorEntity,
        DetonatorPanel,
        FirstMarkers,
        RemnantsPriced,
        FirstPresolvePass,
        FirstDraw,
    }

    private static readonly object Gate = new();
    private static DateTime _entered = DateTime.MinValue;
    private static readonly Dictionary<Step, double> _at = new();

    /// <summary>Starts the clock again, for an area change. See AutoExpedition.AreaChange.</summary>
    public static void Entered()
    {
        lock (Gate)
        {
            _entered = DateTime.UtcNow;
            _at.Clear();
        }
    }

    /// <summary>Whether a step has been stamped since the area changed, so a caller can skip the test that would stamp it.</summary>
    public static bool Noted(Step step)
    {
        lock (Gate)
            return _at.ContainsKey(step);
    }

    /// <summary>Stamps a step the first time it happens after the area change; later calls change nothing.</summary>
    public static void Note(Step step)
    {
        lock (Gate)
        {
            if (_entered != DateTime.MinValue && !_at.ContainsKey(step))
                _at[step] = (DateTime.UtcNow - _entered).TotalMilliseconds;
        }
    }

    /// <summary>Each step and when it happened, or that it has not, for the dump.</summary>
    public static string Said
    {
        get
        {
            lock (Gate)
            {
                if (_entered == DateTime.MinValue)
                    return "no area change seen since the plugin loaded";

                return string.Join(", ", Enum.GetValues<Step>().Select(step =>
                    $"{Named(step)} {(_at.TryGetValue(step, out var ms) ? $"{ms / 1000d:0.0} s" : "not yet")}")) +
                       $" - counted from the area change {(DateTime.UtcNow - _entered).TotalSeconds:0} s ago";
            }
        }
    }

    private static string Named(Step step) => step switch
    {
        Step.DetonatorEntity => "detonator entity",
        Step.DetonatorPanel => "detonator panel",
        Step.FirstMarkers => "first markers",
        Step.RemnantsPriced => "every remnant priced",
        Step.FirstPresolvePass => "first presolve pass",
        Step.FirstDraw => "first draw",
        _ => step.ToString(),
    };
}
