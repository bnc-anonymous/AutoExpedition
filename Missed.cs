using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Markers the game says a particular blast would NOT catch, against the plugin's belief that it
/// would.
///
/// **The coverage model is a disc and the game's is not.** Everything the planner believes about
/// what a blast takes comes from one distance test - the blast radius plus the marker's own extent,
/// both measured, both right on flat ground. On uneven ground they are not right: a marker over a
/// lip or down a slope sits inside the circle and does not light, and nothing readable says which
/// ones. The terrain grid describes where an explosive may be PLACED, not what it reaches from
/// there, and no height model has survived contact with a real site.
///
/// So this takes the answer rather than predicting it, exactly as <see cref="Refused"/> does for
/// placement. While the indicator is down the game highlights precisely the markers that explosive
/// would take; a marker the plan expected and the game does not light is a fact about those two
/// cells, and it is written here. The planner then stops crediting that blast with that marker, and
/// solves again.
///
/// Per pair, not per marker and not per cell. A marker unreachable from one side of a ridge is
/// usually reachable from the other, and a blanket "this marker cannot be caught" would throw away
/// the chain that catches it properly - which is the chain this is trying to find.
///
/// Per area, cleared on a zone change, for the same reason Refused is: the ground it describes does
/// not survive a new instance.
/// </summary>
internal sealed class Missed
{
    /// <summary>The one the plugin uses. Static, because it is one table per session and every caller wants the same one.</summary>
    public static readonly Missed Here = new();

    /// <summary>
    /// Every cell the lookup treats as missing, which is more than have been measured.
    ///
    /// **A miss at one cell is a miss at the cells beside it, and exact matching threw that away.**
    /// The planner is free to choose any spot; told that (1179,1483) does not catch a marker, it
    /// picks (1180,1483) next time, earns the credit again, walks there again and fails again. The
    /// measurement was real and the lesson did not survive one grid unit.
    ///
    /// So the neighbourhood is written down rather than the point. Expanded when it is recorded
    /// instead of searched for when it is read, because reading happens inside the scoring loop
    /// millions of times a solve and a radius test there would cost more than the search it is
    /// helping - where writing happens a few dozen times a site.
    /// </summary>
    private readonly HashSet<((int X, int Y) At, (int X, int Y) Target)> _pairs = new();

    /// <summary>How far a miss carries, in grid units. See _pairs.</summary>
    private const int Spread = 2;

    /// <summary>
    /// How many separate spots have to miss the same marker before the ground is blamed.
    ///
    /// **Learning one cell at a time takes as many attempts as there are cells.** A blast that
    /// cannot light a marker usually cannot light it from anywhere nearby - it is over a lip, or
    /// down a slope - and ruling out two grid at a time means walking the whole intersection to find
    /// that out, several key presses deep, having learnt at the end what the first three samples
    /// already implied.
    ///
    /// Three spread-out failures is enough to stop treating it as bad luck. The marker is then ruled
    /// out over a blast's worth of ground rather than a nudge of it, which is the conclusion the
    /// evidence actually supports.
    /// </summary>
    private const int Sure = 3;

    /// <summary>How far a marker is ruled out once the ground is blamed rather than the cell.</summary>
    private const int Wide = 10;

    /// <summary>How many separate spots have been seen to miss each marker. See Sure.</summary>
    private readonly Dictionary<(int X, int Y), HashSet<(int X, int Y)>> _blamed = new();

    /// <summary>What was actually measured, for the report. The lookup set holds far more.</summary>
    private readonly List<((int X, int Y) At, (int X, int Y) Target, int Link)> _said = new();

    private uint _area;

    public int Count => _said.Count;

    /// <summary>
    /// Writes down that a blast here does not take that marker.
    /// </summary>
    /// <returns>Whether this is new, so the caller knows a re-plan is worth the trouble.</returns>
    /// <param name="link">
    /// Which link of the chain this blast is, counting from one - what the number in the middle of
    /// the circle says. A coordinate names a cell and a number names the thing on screen, and the
    /// report is read by somebody looking at the screen.
    /// </param>
    public bool Note(Vector2 at, Vector2 target, int link)
    {
        if (at == Vector2.Zero || target == Vector2.Zero)
            return false;

        var here = Cell(at);
        var want = Cell(target);
        var known = false;

        for (var x = -Spread; x <= Spread; x++)
        {
            for (var y = -Spread; y <= Spread; y++)
                known |= _pairs.Add(((here.X + x, here.Y + y), want));
        }

        if (known)
            _said.Add((here, want, link));

        // Three separate spots missing the same marker is the ground, not the cell. See Sure.
        if (!_blamed.TryGetValue(want, out var from))
            _blamed[want] = from = new HashSet<(int X, int Y)>();

        if (from.Add(here) && from.Count >= Sure)
        {
            for (var x = -Wide; x <= Wide; x++)
            {
                for (var y = -Wide; y <= Wide; y++)
                    known |= _pairs.Add(((here.X + x, here.Y + y), want));
            }
        }

        return known;
    }

    /// <summary>Whether the game has already said this blast misses this marker.</summary>
    public bool Misses(Vector2 at, Vector2 target) =>
        _pairs.Count > 0 && _pairs.Contains((Cell(at), Cell(target)));

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _pairs.Clear();
            _said.Clear();
            _blamed.Clear();
        }

        _area = areaHash;
    }

    /// <summary>What has been learnt, for the dump - each entry is a fact and the list is short.</summary>
    public string Describe()
    {
        if (_said.Count == 0)
            return "nothing seen to miss what it should catch";

        var said = new List<string>();

        foreach (var (at, target, link) in _said)
        {
            said.Add(link > 0
                ? $"blast #{link} at ({at.X},{at.Y}) misses ({target.X},{target.Y})"
                : $"({at.X},{at.Y}) misses ({target.X},{target.Y})");

            if (said.Count == 6)
                break;
        }

        return $"{_said.Count} measured, {_pairs.Count} cells ruled out: " +
               string.Join(", ", said) + (_said.Count > said.Count ? ", ..." : "");
    }

    private static (int X, int Y) Cell(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));
}
