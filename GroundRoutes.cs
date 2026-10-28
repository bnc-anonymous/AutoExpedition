using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Straight lines and routes over the client's pathfinding grid (RawPathfindingData, rows by y, zero a wall, one to
/// five the distance to the nearest wall - see Terrain), for instant loot moving the character itself: a dodge roll and
/// the sprint after it go straight where they are aimed and stop at a wall, where a click's walk is routed round it by
/// the game. See Autoloot.RollIsFaster and Autoloot.StepOffScreenChase.
///
/// **Any cell not a wall counts as ground the character can cross**, as Terrain.Walkable does for a chain. How much room
/// the character's body needs is not measured; a route threaded through a one-cell gap the character cannot fit is the
/// error this would make.
/// </summary>
internal static class GroundRoutes
{
    /// <summary>Whether a cell is ground: on the grid and not a wall.</summary>
    public static bool Walkable(int[][] grid, int x, int y) => Room(grid, x, y) > 0;

    /// <summary>
    /// A cell's room: its grid value, the distance to the nearest wall in grid capped at 5, 0 for a wall; 0 off the grid.
    /// See Terrain's class summary for the measurement that showed the grid is a clearance field.
    /// </summary>
    public static int Room(int[][] grid, int x, int y)
    {
        if (grid == null || y < 0 || y >= grid.Length)
            return 0;

        var row = grid[y];

        return row != null && x >= 0 && x < row.Length ? row[x] : 0;
    }

    /// <summary>
    /// The room a straight line to steer a roll or sprint down keeps from walls where it can, in grid: a line grazing a
    /// wall's corner is hit by a roll set off a few degrees wide, as a chase's was, about 10 degrees off a line to the
    /// farthest point in sight and stopped against the corner (dump 2026-10-25 03:05:04). Chosen.
    /// </summary>
    public const int SteeringRoom = 3;

    /// <summary>How near a point with SteeringRoom to spare may be before a line with less is taken instead, in grid. Chosen.</summary>
    public const float SteeringRoomNearest = 8f;

    /// <summary>
    /// Whether every cell of the straight line between two grid points is ground, by Bresenham's walk, a diagonal step
    /// past a corner refused only when both cells beside it are walls - Terrain.Walk's rule. True when there is no grid,
    /// so a failure to read it costs a check rather than every roll.
    /// </summary>
    public static bool LineWalkable(int[][] grid, Vector2 from, Vector2 to) => LineWithRoom(grid, from, to, 1);

    /// <summary>
    /// Whether every cell of the straight line between two grid points has at least this much room - LineWalkable with
    /// room 1 - by the same walk and corner rule. True when there is no grid.
    /// </summary>
    public static bool LineWithRoom(int[][] grid, Vector2 from, Vector2 to, int room)
    {
        if (grid == null)
            return true;

        var x = (int)MathF.Round(from.X);
        var y = (int)MathF.Round(from.Y);
        var toX = (int)MathF.Round(to.X);
        var toY = (int)MathF.Round(to.Y);
        var dx = Math.Abs(toX - x);
        var dy = Math.Abs(toY - y);
        var sx = x < toX ? 1 : -1;
        var sy = y < toY ? 1 : -1;
        var error = dx - dy;

        for (var guard = dx + dy + 2; guard > 0; guard--)
        {
            // The cell stood on is not asked for room beyond ground: next to a wall, every line would fail at its start.
            var needs = x == (int)MathF.Round(from.X) && y == (int)MathF.Round(from.Y) ? 1 : room;

            if (Room(grid, x, y) < needs)
                return false;

            if (x == toX && y == toY)
                return true;

            var twice = error * 2;
            var alongX = twice > -dy;
            var alongY = twice < dx;

            if (alongX && alongY && Room(grid, x + sx, y) < room && Room(grid, x, y + sy) < room)
                return false;

            if (alongX)
            {
                error -= dy;
                x += sx;
            }

            if (alongY)
            {
                error += dx;
                y += sy;
            }
        }

        return true;
    }

    /// <summary>How far past the box around both ends a route may wander, in grid. Chosen.</summary>
    private const int RouteMargin = 80;

    /// <summary>The most cells a route search opens before giving up. Chosen: a box 400 a side is 160,000 cells.</summary>
    private const int RouteMostCells = 200_000;

    /// <summary>
    /// A route over ground from one grid point to another, by A* over the eight neighbours of each cell, a diagonal
    /// step only where both cells beside it are ground; the points along it, start first, or null when none is found
    /// within the box around both ends widened by RouteMargin, or within RouteMostCells. The end may stand on an item's
    /// own cell; it is taken as ground.
    /// </summary>
    public static List<Vector2> Route(int[][] grid, Vector2 from, Vector2 to)
    {
        if (grid == null)
            return null;

        int fx = (int)MathF.Round(from.X), fy = (int)MathF.Round(from.Y);
        int tx = (int)MathF.Round(to.X), ty = (int)MathF.Round(to.Y);
        int left = Math.Min(fx, tx) - RouteMargin, top = Math.Min(fy, ty) - RouteMargin;
        int width = Math.Abs(fx - tx) + 2 * RouteMargin + 1, height = Math.Abs(fy - ty) + 2 * RouteMargin + 1;

        if ((long)width * height > 4L * RouteMostCells)
            return null;

        bool Ground(int x, int y) => (x == tx && y == ty) || Walkable(grid, x, y);

        var cost = new float[width * height];
        var came = new int[width * height];
        Array.Fill(cost, float.MaxValue);

        int Index(int x, int y) => (y - top) * width + (x - left);
        bool Inside(int x, int y) => x >= left && y >= top && x < left + width && y < top + height;
        float Guess(int x, int y) => MathF.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty));

        var open = new PriorityQueue<int, float>();
        var start = Index(fx, fy);

        cost[start] = 0f;
        came[start] = -1;
        open.Enqueue(start, Guess(fx, fy));

        var opened = 0;

        while (open.TryDequeue(out var at, out _))
        {
            int ax = at % width + left, ay = at / width + top;

            if (ax == tx && ay == ty)
            {
                var route = new List<Vector2>();

                for (var step = at; step != -1; step = came[step])
                    route.Add(new Vector2(step % width + left, step / width + top));

                route.Reverse();
                return route;
            }

            if (++opened > RouteMostCells)
                return null;

            for (var ny = -1; ny <= 1; ny++)
            {
                for (var nx = -1; nx <= 1; nx++)
                {
                    if (nx == 0 && ny == 0)
                        continue;

                    int bx = ax + nx, by = ay + ny;

                    if (!Inside(bx, by) || !Ground(bx, by))
                        continue;

                    if (nx != 0 && ny != 0 && !Ground(ax + nx, ay) && !Ground(ax, ay + ny))
                        continue;

                    // Dearer beside a wall, so the route keeps to the middle of a way: a step onto a cell with room r of
                    // the grid's 5 costs (1 + (5 - r) / 5) times a step in the open. Chosen.
                    var room = Math.Max(1, Math.Min(5, Room(grid, bx, by)));
                    var step = cost[at] + (nx != 0 && ny != 0 ? 1.41421356f : 1f) * (1f + (5 - room) / 5f);
                    var next = Index(bx, by);

                    if (step >= cost[next])
                        continue;

                    cost[next] = step;
                    came[next] = at;
                    open.Enqueue(next, step + Guess(bx, by));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The farthest point along a route the straight line from its start still reaches over ground: where to aim a roll,
    /// a sprint or a click so it heads along the route without meeting a wall. The route's end when the whole of it is
    /// in a straight line, its second point when no farther one is.
    /// </summary>
    public static Vector2 FarthestStraightAlong(int[][] grid, List<Vector2> route)
    {
        if (route == null || route.Count == 0)
            return Vector2.Zero;

        var from = route[0];

        for (var i = route.Count - 1; i > 1; i--)
        {
            if (LineWalkable(grid, from, route[i]))
                return route[i];
        }

        return route.Count > 1 ? route[1] : route[0];
    }
}
