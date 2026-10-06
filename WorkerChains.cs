using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Each search worker's own chain, carried from one solve to the next, and the rule that retires a chain that has
/// stopped being worth carrying.
///
/// **Carried, because a route shape needs more than one solve to show what it is worth.** Each worker but the first
/// used to draw a fresh opening at every solve, so no route other than the plan in hand was searched for longer than
/// one solve and the plan kept whatever route the first solve found. Measured offline on one Grand layout
/// (2026-10-03): from a 39,179 east-first seed, six 24s passes stalled at 42,205; carrying each worker's chain, the
/// same passes reached 45,403 by the fourth on a different route. See Repair.Search's own.
///
/// **Retired, because carrying alone keeps a dead shape forever.** A worker whose chain has stayed below
/// <see cref="RetireBelow"/> of the pool's best for <see cref="RetireAfter"/> solves in a row is given nothing to
/// carry, so its next solve draws a new opening of its own. Worker nought is never retired: it holds the plan in hand.
/// Both figures were chosen, not measured.
/// </summary>
internal sealed class WorkerChains
{
    /// <summary>The share of the pool's best a worker's chain must reach to count as still in contention.</summary>
    public const double RetireBelow = 0.9;

    /// <summary>How many solves in a row a worker may stay out of contention before its chain is retired.</summary>
    public const int RetireAfter = 2;

    private readonly List<Vector2>[] _chains;
    private readonly int[] _behind;
    private readonly double _retireBelow;
    private readonly int _retireAfter;

    /// <param name="workers">How many workers there are.</param>
    /// <param name="retireBelow">The share of the best below which a chain is out of contention. See RetireBelow.</param>
    /// <param name="retireAfter">
    /// How many solves out of contention retire a chain; nought or less never retires one. See RetireAfter.
    /// </param>
    public WorkerChains(int workers, double retireBelow = RetireBelow, int retireAfter = RetireAfter)
    {
        _chains = new List<Vector2>[Math.Max(0, workers)];
        _behind = new int[_chains.Length];
        _retireBelow = retireBelow;
        _retireAfter = retireAfter;
    }

    public int Workers => _chains.Length;

    /// <summary>What the last <see cref="Settle"/> did, for the dump. Empty before any.</summary>
    public string Said { get; private set; } = "";

    /// <summary>The chain worker n carries into its next solve, or null for a fresh start.</summary>
    public List<Vector2> Of(int worker) =>
        worker >= 0 && worker < _chains.Length ? _chains[worker] : null;

    /// <summary>Records the chain worker n ended a solve on. Each worker writes only its own slot.</summary>
    public void Ended(int worker, List<Vector2> chain)
    {
        if (worker >= 0 && worker < _chains.Length && chain is { Count: > 0 })
            _chains[worker] = new List<Vector2>(chain);
    }

    /// <summary>
    /// After a solve: scores every carried chain against the pool's best and retires the ones out of contention for
    /// too long. Call once all workers have ended.
    /// </summary>
    public void Settle(PlanEnvironment env)
    {
        var scores = new double[_chains.Length];
        var best = double.NegativeInfinity;

        for (var n = 0; n < _chains.Length; n++)
        {
            scores[n] = _chains[n] is { Count: > 0 } chain ? Planner.Plainly(env, chain) : double.NegativeInfinity;
            best = Math.Max(best, scores[n]);
        }

        if (double.IsNegativeInfinity(best) || best <= 0d)
        {
            Said = "nothing carried";

            return;
        }

        var retired = new List<int>();

        for (var n = 1; n < _chains.Length; n++)
        {
            if (_retireAfter <= 0 || scores[n] >= best * _retireBelow)
            {
                _behind[n] = 0;

                continue;
            }

            if (++_behind[n] < _retireAfter)
                continue;

            _chains[n] = null;
            _behind[n] = 0;
            retired.Add(n);
        }

        var shares = string.Join(" ", scores.Select((s, n) => double.IsNegativeInfinity(s)
            ? $"{n}:-"
            : string.Create(CultureInfo.InvariantCulture, $"{n}:{s / best:P0}")));
        var outcome = retired.Count == 0
            ? "none retired"
            : string.Create(CultureInfo.InvariantCulture,
                $"retired {string.Join(", ", retired)} after {_retireAfter} solves below {_retireBelow:P0}");

        Said = string.Create(CultureInfo.InvariantCulture, $"carried {shares} of the best {best:N0}; {outcome}");
    }
}
