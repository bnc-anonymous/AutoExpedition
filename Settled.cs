using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The game's verdict on a cell, recorded only when it is unambiguously ABOUT that cell.
///
/// **Both evidence sets this HUD collects are polluted, in opposite directions, by the same
/// mechanism.** An alignment search over 186 accepted and 404 refused cells could not find any shift
/// or cutoff of the pathfinding grid that agreed with the game better than 79%, with errors on both
/// sides - 61 accepted cells reading below the cutoff and 62 refused reading at or above it. A
/// deterministic predicate does not score 79%, so the fault was in the evidence, not the grid:
///
/// - Refused holds every cell the circle showed red, and red has three causes. Bad ground is one.
///   Out of placement range is another, and this HUD already knows a clamp cannot tell those two
///   apart. Inside the minimum separation of a placed explosive is the third - see Forgive. Only
///   the first says anything about terrain, and the other two are why 62 refused cells read 5.
/// - Reached holds the indicator entity's own position, which CLAMPS: sweep the cursor past the
///   limit and the entity parks on the boundary while the animation keeps answering. The accepted
///   cells sitting in solid rock are not scattered, they trace LINES - a column at x=1214 and a
///   diagonal band across x=1215-1218 - which is the shape of a parked entity dragged along a
///   boundary arc, not the shape of ground somebody pointed at.
///
/// So the gate here is that the entity and the cursor are on the SAME CELL. Then the player is
/// pointing at the cell the entity is standing on, the entity is not parked anywhere, and the
/// animation is answering about that cell and no other. This is the inference the animation reading
/// replaced - see Detonator.PlaceableNow - and it was dropped for being silent whenever the cursor
/// sat between cells. That was the wrong trade for a measurement: a quiet reading that is right
/// beats a plentiful one that is wrong, and a sweep still produces plenty.
///
/// Nothing routes by this. It exists to judge Terrain against, so the planner's behaviour is
/// unchanged while the model is under suspicion.
/// </summary>
internal sealed class Settled
{
    public static readonly Settled Here = new();

    /// <summary>
    /// The verdict per cell, last reading winning.
    ///
    /// Concurrent for the same reason Refused's set is: the frame writes while a dump or the search
    /// may be reading. Last reading wins because the animation is known to hold its previous state a
    /// beat too long, so a later look at the same cell is the better one.
    /// </summary>
    /// <param name="Yes">How many readings called this cell placeable.</param>
    /// <param name="No">How many called it refused.</param>
    /// <param name="Explained">
    /// Whether this reading has a non-terrain reason available, captured at the moment it was taken.
    ///
    /// **The spacing rule refuses good ground, and the clean set was still full of it.** Gating on the
    /// entity and the cursor agreeing removes range refusals, because an out-of-range cursor is one
    /// the entity is not standing on - but it does nothing about the separation ring, where the
    /// cursor and the entity agree perfectly and the game says no for a reason that is about the
    /// chain rather than the terrain. That showed up as 48 of 69 refusals sitting on ground reading
    /// 1 or better, and it is why the whole alignment table came in within a few points of the
    /// trivial "always placeable" baseline.
    ///
    /// Recorded per reading rather than worked out when the dump is written, because the answer
    /// depends on where the explosives were AT THE TIME and they move as the site is played.
    /// </param>
    ///
    /// <remarks>
    /// **Both counts are kept, because the repetition is the evidence.** This held one verdict per
    /// cell with the last reading winning, which threw away about seven looks per cell and with them
    /// the only thing that separates the two live explanations of the refusals on file.
    ///
    /// Those refusals lie on two straight axis-aligned lines a single cell from accepted ground.
    /// A real boundary nothing in either terrain grid encodes would look like that - and so would the
    /// animation lagging a frame behind the cursor, because a horizontal mouse sweep IS a horizontal
    /// line. The animation is already known to hold a stale state too long in the other direction.
    ///
    /// Lag produces a MINORITY of wrong readings on a cell looked at many times; a boundary produces
    /// unanimity. So the measurement uses only cells whose readings all agree, and the mixed ones are
    /// reported rather than silently resolved - if the lines turn out to be mostly mixed, that is the
    /// answer.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<((int X, int Y) Cell,
        (int X, int Y) From), (int Yes, int No, bool Explained)> _cells = new();

    private uint _area;
    private int _agreed;
    private int _apart;

    /// <summary>Offers one frame's reading, kept only if the two positions name one cell.</summary>
    /// <param name="entity">Where the indicator entity is standing. Clamped by the game.</param>
    /// <param name="cursor">Where the cursor asks for. Unclamped. See Detonator.RequestedGridPosition.</param>
    /// <param name="explained">Whether the chain could account for a refusal here. See _cells.</param>
    /// <param name="origin">
    /// Where the next explosive would be thrown FROM when this reading was taken.
    ///
    /// **A verdict without it is not about anything.** These were keyed on the cell alone and kept for
    /// the life of the area, so readings taken with one explosive as the chain's head were pooled with
    /// readings taken from another - and the pool then said the game had allowed cells a hundred and
    /// thirty grid from a spot whose range measures ninety. True of some origin, meaningless of that
    /// one. Reach is a claim about a pair, and half a pair is not a smaller claim, it is a different
    /// one.
    /// </param>
    public void Observe(Vector2 entity, Vector2 cursor, bool placeable, bool explained, Vector2 origin)
    {
        if (entity == Vector2.Zero || cursor == Vector2.Zero)
            return;

        var at = Cell(entity);

        if (at != Cell(cursor))
        {
            _apart++;
            return;
        }

        _agreed++;

        _cells.AddOrUpdate((at, Cell(origin)),
            _ => (placeable ? 1 : 0, placeable ? 0 : 1, explained),
            (_, had) => (had.Yes + (placeable ? 1 : 0), had.No + (placeable ? 0 : 1),
                had.Explained || explained));
    }

    /// <summary>
    /// The cells every reading agreed about, which is the only evidence worth measuring against.
    ///
    /// A cell looked at once counts: one reading cannot contradict itself, and a boundary swept over
    /// briefly is still a boundary. What is excluded is a cell the game called both things. See
    /// _cells.
    /// </summary>
    public IEnumerable<((int X, int Y) Cell, bool Placeable, bool Explained, int Looks,
        (int X, int Y) From)> Verdicts
    {
        get
        {
            foreach (var (key, said) in _cells)
            {
                if (said.Yes > 0 && said.No > 0)
                    continue;

                yield return (key.Cell, said.Yes > 0, said.Explained, said.Yes + said.No, key.From);
            }
        }
    }

    /// <summary>The cells the game called both placeable and not. See _cells.</summary>
    public IEnumerable<((int X, int Y) Cell, int Yes, int No)> Mixed
    {
        get
        {
            foreach (var (key, said) in _cells)
            {
                if (said.Yes > 0 && said.No > 0)
                    yield return (key.Cell, said.Yes, said.No);
            }
        }
    }

    public int Count => _cells.Count;

    public string Describe()
    {
        var yes = 0;
        var no = 0;
        var spacing = 0;
        var mixed = 0;
        var mixedLooks = 0;
        var minority = 0;

        foreach (var (_, said) in _cells)
        {
            if (said.Yes > 0 && said.No > 0)
            {
                mixed++;
                mixedLooks += said.Yes + said.No;
                minority += Math.Min(said.Yes, said.No);
                continue;
            }

            if (said.Yes > 0)
            {
                yes++;
            }
            else
            {
                no++;

                if (said.Explained)
                    spacing++;
            }
        }

        if (_cells.Count == 0)
            return $"nothing settled yet ({_apart} readings discarded as clamped or between cells)";

        return $"{_cells.Count} cells settled from {_agreed} readings " +
               $"({_apart} discarded as clamped or between cells)" + Environment.NewLine +
               $"    unanimous: {yes} placeable, {no} refused, of which {spacing} are within the " +
               $"separation of an explosive or the detonator and say nothing about ground" +
               Environment.NewLine +
               $"    mixed: {mixed} cells the game called both, over {mixedLooks} readings of which " +
               $"{minority} were the minority verdict" +
               (mixed > 0
                   ? $" - a boundary is unanimous, so these are the lag"
                   : " - nothing contradicted itself");
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash == _area)
            return;

        _area = areaHash;
        _cells.Clear();
        _agreed = 0;
        _apart = 0;
    }

    private static (int X, int Y) Cell(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));
}
