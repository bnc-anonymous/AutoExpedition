using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Which ground the player can walk to from where they stand, for keeping the loot line off chests that cannot be
/// reached - a chest penned behind an explosive gate that is still shut, such as Arohongui's Hoard behind a Karui
/// fence gate.
///
/// Flooded over the client's pathfinding grid (RawPathfindingData, non-zero is walkable) from the player's cell, in a
/// square around the player, and kept for a second or until the player moves. Whether the grid marks a shut gate
/// as blocked is read from the client and not assumed: Describe prints the answer per chest, and the shut
/// TriggerableBlockage entities near it, so a dump shows whether the two agree.
///
/// Anything this cannot answer - no grid, a chest outside the flooded square - counts as reachable, so a failure
/// shows the line rather than hiding loot.
/// </summary>
internal static class WalkableFromPlayer
{
    /// <summary>Half the side of the flooded square, in grid. A dig site's loot is well inside it.</summary>
    private const int FloodHalfSide = 400;

    /// <summary>
    /// How near a reached cell must be to a chest's centre for the chest to count as reachable, in grid. A chest's
    /// own footprint is unwalkable, so its centre never is; this is a little more than the Hoard's half-width (74
    /// world across, about 7 grid).
    /// </summary>
    private const int ChestReachMargin = 8;

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1);

    /// <summary>How far the player may move before the flood is redone early, in grid.</summary>
    private const float RefreshAfterMoving = 6f;

    /// <summary>One flood, swapped in whole so a reader on another thread never sees half of two.</summary>
    private sealed record Flood(DateTime At, Vector2 From, int Left, int Top, int Side, bool[] Reached);

    private static volatile Flood _flood;

    /// <summary>Whether the player can walk to this spot. True when the grid cannot say. See the class doc.</summary>
    public static bool CanWalkTo(GameController gc, Vector2 player, Vector2 spot)
    {
        Refresh(gc, player);

        var flood = _flood;

        if (flood?.Reached is not { } reached)
            return true;

        var side = flood.Side;
        var cx = (int)MathF.Round(spot.X) - flood.Left;
        var cy = (int)MathF.Round(spot.Y) - flood.Top;

        if (cx < 0 || cy < 0 || cx >= side || cy >= side)
            return true;

        for (var dy = -ChestReachMargin; dy <= ChestReachMargin; dy++)
        {
            for (var dx = -ChestReachMargin; dx <= ChestReachMargin; dx++)
            {
                if (dx * dx + dy * dy > ChestReachMargin * ChestReachMargin)
                    continue;

                int x = cx + dx, y = cy + dy;

                if (x >= 0 && y >= 0 && x < side && y < side && reached[y * side + x])
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For the dump: whether this spot is reachable on foot, and every shut TriggerableBlockage within 60 grid of it,
    /// so a chest the line skips can be checked against the gate that pens it.
    /// </summary>
    public static string Describe(GameController gc, Vector2 player, Vector2 spot)
    {
        var walkable = CanWalkTo(gc, player, spot);
        var shut = new List<string>();

        foreach (var entity in Safe.Read(gc, static g => g.EntityListWrapper.OnlyValidEntities, null) ?? [])
        {
            var blockage = Safe.Read(entity,
                static e => e.GetComponent<ExileCore2.PoEMemory.Components.TriggerableBlockage>(), null);

            if (blockage == null || !Safe.Read(blockage, static b => b.IsClosed, false))
                continue;

            var grid = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);
            var away = Vector2.Distance(grid, spot);

            if (grid == Vector2.Zero || away > 60f || away < 1f)
                continue;

            var path = Safe.Read(entity, static e => e.Path, "") ?? "";

            shut.Add($"{path[(path.LastIndexOf('/') + 1)..]} at ({grid.X:0},{grid.Y:0}) {away:0} away");
        }

        return $"on foot {(_flood?.Reached == null ? "UNKNOWN (no pathfinding grid)" : walkable ? "reachable" : "NOT REACHABLE - no loot line")}" +
               $"; shut blockages within 60: {(shut.Count == 0 ? "none" : string.Join(", ", shut))}";
    }

    private static void Refresh(GameController gc, Vector2 player)
    {
        if (player == Vector2.Zero)
            return;

        var last = _flood;

        if (last != null && DateTime.UtcNow - last.At < RefreshEvery &&
            Vector2.Distance(player, last.From) < RefreshAfterMoving)
            return;

        var grid = Safe.Read(gc, static g => g.IngameState.Data.RawPathfindingData, null);

        if (grid == null || grid.Length == 0)
        {
            _flood = new Flood(DateTime.UtcNow, player, 0, 0, 0, null);

            return;
        }

        var side = FloodHalfSide * 2 + 1;
        var left = (int)MathF.Round(player.X) - FloodHalfSide;
        var top = (int)MathF.Round(player.Y) - FloodHalfSide;
        var reached = new bool[side * side];
        var queue = new Queue<int>();
        var seeded = false;

        bool Open(int x, int y)
        {
            var gy = top + y;
            var gx = left + x;

            if (gy < 0 || gy >= grid.Length)
                return false;

            var row = grid[gy];

            return row != null && gx >= 0 && gx < row.Length && row[gx] != 0;
        }

        void Seed(int x, int y)
        {
            if (x < 0 || y < 0 || x >= side || y >= side || reached[y * side + x] || !Open(x, y))
                return;

            reached[y * side + x] = true;
            queue.Enqueue(y * side + x);
        }

        // The player's own cell can read blocked when they stand against a wall, so the seed is any open cell
        // within two of it.
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                Seed(FloodHalfSide + dx, FloodHalfSide + dy);

        seeded = queue.Count > 0;

        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            int x = at % side, y = at / side;

            Seed(x + 1, y);
            Seed(x - 1, y);
            Seed(x, y + 1);
            Seed(x, y - 1);
        }

        // A player on no open cell at all seeds nothing, and an empty flood would hide every chest; that is no answer.
        _flood = new Flood(DateTime.UtcNow, player, left, top, side, seeded ? reached : null);
    }
}
