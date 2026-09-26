using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The shortest path that a real line can take, rather than the shortest walk on a lattice.
///
/// **The grid flood cuts corners a taut wire cannot, and that is why it comes in short.** An eight
/// way step may pass diagonally between two blocked cells whenever they are not both blocked, which
/// on the ground means squeezing through the point where two square obstacles touch. The plugin's
/// own rule forbids it only when BOTH neighbours are blocked, so a diagonal past a single corner is
/// allowed and costs root two - while the line it stands for clips the obstacle.
///
/// Measured against the game's own answer: on one link the client drew its chain as two straight
/// runs totalling 89.22, and the lattice flood called the same pair 85.36. Short by 3.9, which is
/// the reach the planner gives away and then has to take back at the cursor.
///
/// So this is Theta*: the same grid for candidate points, but a node's parent may be any earlier
/// node it can see, and the cost is the straight distance between them. What comes out is a polyline
/// through open space rather than a staircase - the same shape the connector poles trace.
///
/// Line of sight is deliberately strict. It samples finely and refuses if any sampled cell is
/// blocked, which is what stops the corner cutting the lattice permits; a coarse test here would
/// reintroduce the very error being measured.
/// </summary>
internal static class Taut
{
    /// <param name="blocked">What counts as solid. Cells below this much room are obstacles.</param>
    /// <returns>Path length in grid units, or -1 when nothing can reach it.</returns>
    public static double Length(Terrain ground, Vector2 from, Vector2 to, float reach, int blocked,
        out List<Vector2> path)
    {
        path = null;

        var start = Cell(from);
        var goal = Cell(to);

        bool Open(int x, int y) => (ground?.Room(new Vector2(x, y)) ?? 5) >= blocked;

        if (!Open(goal.X, goal.Y))
            return -1d;

        // Finely sampled, because the whole point is to refuse the diagonal slip between two corners
        // that a cell-by-cell walk allows.
        bool Sees((int X, int Y) a, (int X, int Y) b)
        {
            var span = MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)) * 4;

            for (var i = 0; i <= span; i++)
            {
                var t = span == 0 ? 0f : i / (float)span;

                if (!Open((int)MathF.Round(a.X + (b.X - a.X) * t),
                        (int)MathF.Round(a.Y + (b.Y - a.Y) * t)))
                    return false;
            }

            return true;
        }

        var best = new Dictionary<(int X, int Y), double> { [start] = 0d };
        var parent = new Dictionary<(int X, int Y), (int X, int Y)> { [start] = start };
        var queue = new PriorityQueue<(int X, int Y), double>();

        queue.Enqueue(start, 0d);

        while (queue.TryDequeue(out var at, out var cost))
        {
            if (best.TryGetValue(at, out var had) && had < cost)
                continue;

            if (at == goal)
            {
                path = [];

                for (var step = goal; step != parent[step]; step = parent[step])
                    path.Add(new Vector2(step.X, step.Y));

                path.Add(new Vector2(start.X, start.Y));
                path.Reverse();

                return cost;
            }

            var owner = parent[at];

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var next = (at.X + dx, at.Y + dy);

                    if (!Open(next.Item1, next.Item2))
                        continue;

                    // **The Theta* step: reach past the parent when it can be seen.** That is what
                    // turns a staircase into a straight run, and what makes the length comparable
                    // with a wire rather than with a walk.
                    var (tie, price) = Sees(owner, next)
                        ? (owner, best[owner] + Gap(owner, next))
                        : (at, cost + Gap(at, next));

                    if (price > reach)
                        continue;

                    if (best.TryGetValue(next, out var was) && was <= price)
                        continue;

                    best[next] = price;
                    parent[next] = tie;
                    queue.Enqueue(next, price);
                }
            }
        }

        return -1d;
    }

    private static double Gap((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private static (int X, int Y) Cell(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));
}
