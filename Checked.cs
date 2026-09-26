using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Which spots the frontier sweep has put to the game, and whether it agreed with us.
///
/// **A dig site is far larger than one screen, so the sweep is resumable by construction.** The
/// player walks, sweeps what is in front of them, walks again; this is what stops the second pass
/// re-testing the first pass's ground. Keyed on the cell, cleared when the area changes, and held in
/// memory rather than on disk because it describes this instance of this map and nothing else.
///
/// Three states, not two. Unchecked is the absence of a row; the other two are worth separating
/// because a disagreement is the only output of the sweep that anybody needs to act on.
/// </summary>
internal sealed class Checked
{
    /// <summary>The one the plugin uses. Static, because it is one table per session and every caller wants the same one.</summary>
    public static readonly Checked Here = new();

    private readonly ConcurrentDictionary<(int X, int Y), bool> _seen = new();

    private uint _area;

    /// <summary>How many spots have been put to the game at this site.</summary>
    public int Count => _seen.Count;

    /// <summary>How many of those the game disagreed with us about.</summary>
    public int Wrong
    {
        get
        {
            var many = 0;

            foreach (var (_, agreed) in _seen)
                if (!agreed)
                    many++;

            return many;
        }
    }

    /// <summary>Whether this spot has been tested, and if so whether we got it right.</summary>
    public bool? Verdict(Vector2 grid) =>
        _seen.TryGetValue(Cell(grid), out var agreed) ? agreed : null;

    /// <summary>Whether this spot still needs testing.</summary>
    public bool Wanted(Vector2 grid) => !_seen.ContainsKey(Cell(grid));

    /// <summary>Write down what the game said about a spot.</summary>
    public void Note(Vector2 grid, bool agreed) => _seen[Cell(grid)] = agreed;

    /// <summary>Every spot the game disagreed with us about, for the dump.</summary>
    public IEnumerable<(int X, int Y)> Disagreed
    {
        get
        {
            foreach (var (cell, agreed) in _seen)
                if (!agreed)
                    yield return cell;
        }
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
            _seen.Clear();

        _area = areaHash;
    }

    /// <summary>Forget everything, for a sweep somebody wants to run again from cold.</summary>
    public void Forget() => _seen.Clear();

    private static (int X, int Y) Cell(Vector2 grid) =>
        ((int)System.MathF.Round(grid.X), (int)System.MathF.Round(grid.Y));
}
