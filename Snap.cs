using System;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The lattice an explosive can actually be placed on.
///
/// The indicator does not follow the cursor smoothly - it jumps between fixed points on the floor.
/// That is worth knowing twice over.
///
/// For the planner, because a position off the lattice is not a position: a chain optimised to
/// three decimal places is optimising coordinates the game will round away, and the coverage it
/// claims is for a blast that never goes there.
///
/// For the placement loop, because it is why that loop does not correct. Move the cursor a few
/// pixels and the indicator sits exactly where it was, so anything measuring the error, moving
/// proportionally and measuring again sits still, concludes it has failed, moves harder, and
/// overshoots by a whole cell. That correcting loop existed and did exactly this; the answer to a
/// quantised control is to aim at the middle of the cell you want and accept an exact match, which
/// is what the placement step now does - one attempt, then it says so and stops.
///
/// So the step is measured rather than assumed. PlacementIndicatorGridPosition is a Vector2i, which
/// proves it is at least whole grid units, but says nothing about whether the real lattice is
/// coarser than that - and coarser is what "snapping" looks like on screen.
/// </summary>
internal sealed class Snap
{
    private Vector2 _last = Vector2.Zero;
    private uint _area;

    /// <summary>The smallest non-zero jump seen on each axis, which is the lattice step.</summary>
    private float _stepX = float.MaxValue;
    private float _stepY = float.MaxValue;

    public int Samples { get; private set; }

    public bool Ready => Samples > 0 && (_stepX < float.MaxValue || _stepY < float.MaxValue);

    /// <summary>
    /// The lattice step in grid units, or null until the indicator has been seen to move.
    ///
    /// The smaller of the two axes: a lattice that is finer one way than the other is still bounded
    /// by its finer step, and aiming at the middle of a cell is safe either way.
    /// </summary>
    public float? Step => Ready ? MathF.Min(_stepX, _stepY) : null;

    public string Describe() =>
        !Ready
            ? "not measured yet"
            : $"{Step:0.##} ({(_stepX < float.MaxValue ? _stepX.ToString("0.##") : "-")} x " +
              $"{(_stepY < float.MaxValue ? _stepY.ToString("0.##") : "-")}, {Samples} moves)";

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _last = Vector2.Zero;
            _stepX = float.MaxValue;
            _stepY = float.MaxValue;
            Samples = 0;
        }

        _area = areaHash;
    }

    public void Observe(Vector2 indicator)
    {
        if (indicator == Vector2.Zero)
            return;

        if (_last == Vector2.Zero)
        {
            _last = indicator;
            return;
        }

        var dx = MathF.Abs(indicator.X - _last.X);
        var dy = MathF.Abs(indicator.Y - _last.Y);

        if (dx == 0f && dy == 0f)
            return;

        // Per axis and independently: a diagonal move is evidence about both, and the smallest
        // non-zero move ever seen on an axis is that axis's step. A single large jump says nothing
        // - the cursor may simply have travelled - so only the minimum is kept.
        if (dx > 0f)
            _stepX = MathF.Min(_stepX, dx);

        if (dy > 0f)
            _stepY = MathF.Min(_stepY, dy);

        _last = indicator;
        Samples++;
    }
}
