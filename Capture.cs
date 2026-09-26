using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// The whole of a dig site's geometry, written to one file so the route model can be worked on
/// without the game running.
///
/// **The loop this replaces was the bottleneck, not the thinking.** Every change to the reach model
/// meant a build, a plugin reload, walking back to a site, placing explosives, sweeping a cursor and
/// pressing a dump key - minutes of the player's time per attempt, and the attempt only ever answers
/// the one question it was built to ask. Meanwhile the thing being reasoned about is a couple of
/// hundred kilobytes of grid that does not change.
///
/// So it is written down once. Everything the router is given - the pathfinding values, the scenery
/// discs, the site, the reach, the separation - plus the explosives already down and the verdicts the
/// game has handed over, which is what any new model has to reproduce.
///
/// The truths worth capturing are the asymmetric ones. Reach from one explosive to another is NOT the
/// same question in both directions - confirmed in game on this very site, where the chain goes from
/// the first to the second and not back - and Route is built on the opposite assumption, keying pairs
/// in a fixed order and flooding from whichever end it already has. A capture that records only "these
/// two are linked" could not have caught that; one that records a direction can.
/// </summary>
internal static class Capture
{
    /// <summary>How far around the site to record, in grid. See Write.</summary>
    /// <summary>The smallest half-width of terrain to record around the dig site, in grid.</summary>
    private const int Span = 160;

    /// <summary>How much ground to keep beyond the outermost thing recorded, in grid.</summary>
    private const int Margin = 24;

    /// <summary>The largest half-width worth writing, in grid. See Boxed.</summary>
    private const int Furthest = 600;

    /// <summary>
    /// Writes the site beside the dump it was taken with.
    ///
    /// Digits rather than anything structured, one per cell: the values are 0 to 5 and a grid of them
    /// is both the most compact form and the one that can be read by eye in an editor. Rows outside
    /// the recorded box are absent rather than zero-filled, because absent means "not known" and zero
    /// means "wall" - writing one as the other would teach an offline model that the site is ringed
    /// by cliffs.
    /// </summary>
    public static string Write(GameController gc, AutoExpeditionSettings settings,
        List<Target> targets, string directory)
    {
        var grid = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (grid == null || grid.Length == 0)
            return "no pathfinding grid to capture";

        var aiming = Safe.Read(() => gc.IngameState.Data.RawTerrainTargetingData, null);

        var site = Detonator.DetonatorGridPosition(gc);
        var ox = (int)site.X;
        var oy = (int)site.Y;

        var (left, top, right, bottom) = Boxed(gc, grid, ox, oy);

        if (right < left || bottom < top)
            return "the site is outside the pathfinding grid";

        var b = new StringBuilder();

        b.AppendLine("# AutoExpedition site capture");
        b.AppendLine("# Everything the router is given, so the model can be worked on offline.");
        b.AppendLine("# Distances are grid units. The grid block is the pathfinding data, one digit a");
        b.AppendLine("# cell, which is the ROUNDED STRAIGHT-LINE DISTANCE TO THE NEAREST BLOCKED CELL,");
        b.AppendLine("# capped at 5 - measured, not assumed; see Terrain's class doc.");
        b.AppendLine();

        b.AppendLine($"area {Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u)}");
        b.AppendLine($"name {Safe.Read(gc, static g => g.Area.CurrentArea.Area.Name, "?")}");
        b.AppendLine($"site {ox} {oy}");
        b.AppendLine($"reach {Number(Detonator.PlacementRange(gc))}");
        b.AppendLine($"apart {Number(MathF.Max(1f, settings.Debug.ApartAtLeast.Value))}");
        b.AppendLine($"blast {Number(Detonator.BlastRadius(gc, settings.Debug.CircleCorrection.Value) ?? 0f)}");
        b.AppendLine($"dims {Width(grid)} {grid.Length}");
        b.AppendLine($"box {left} {top} {right} {bottom}");
        b.AppendLine();

        foreach (var at in Detonator.PlacedExplosiveGridPositions(gc) ?? [])
            b.AppendLine($"placed {(int)at.X} {(int)at.Y}");

        // Every marker, because a model that routes correctly and ignores what the chain is FOR
        // cannot be scored offline against anything.
        foreach (var target in targets ?? [])
            b.AppendLine($"target {(int)target.Grid.X} {(int)target.Grid.Y} {target.Kind}");

        b.AppendLine();

        // **The chain as the game itself draws it.** The fuses and connector poles are the wire
        // between two explosives, and their positions are the route this plugin has spent a day
        // inferring. Read off one link: two straight runs meeting at a bend, poles sitting on exact
        // thirds of each, total 89.22 against a budget of 90 - while the grid flood called the same
        // pair 85.36. A taut polyline round a corner, not a lattice walk, and the difference is the
        // reach the planner has been giving away.
        //
        // **Written at full precision and with the kind attached, because both were being lost.**
        //
        // This recorded `wire (int)X (int)Y`, and the truncation was doing real damage. A pole read
        // back as a whole cell looked like proof that the game quantises them - it does not; the
        // client answers 1279.58 and the file said 1279. Worse, the error is up to a grid unit per
        // endpoint, so measured segment lengths on one link came out 17.03 to 18.44 where an equal
        // division of that run is 17.62. The unevenness was this line, not the game.
        //
        // The kind is kept for the same reason. A fuse and a pole share a GridPos, so a list of bare
        // points has each position twice with nothing to say which is which - and reconstructing a
        // link then depends on guessing, which is how a straight run was once read as two runs
        // meeting at a bend. See NOTES section 2.
        // **The world position as well, because GridPos is not the precise one.** GridPos is typed
        // as a float pair and reads as whole cells for these entities, while Pos on the same entity
        // carries the sub-cell truth - a placed explosive reading grid (1220,1822) sits at world
        // (13266.3, 19809.8). Which of the two the game tests against is exactly the open question
        // behind a cell being refused on a direct hover and accepted when the indicator clamps onto
        // it, so both are written and neither is inferred from the other. See NOTES section 9.
        foreach (var (at, kind, world) in Wires(gc))
        {
            b.AppendLine($"wire {Exact(at.X)} {Exact(at.Y)} {kind} {Exact(world.X)} {Exact(world.Y)}");
        }

        b.AppendLine();


        // **Far enough to reach the whole recorded box, not a fixed radius from the marker.** This
        // read Span * 2 around the dig site, and a chain is not local: on a Port site the head stood
        // 357 grid away, so the doodads beside it - the ones refusing ground both grids call open -
        // were never written down at all. Same fault as the terrain box had; see Boxed.
        var corner = MathF.Max(
            MathF.Max(Vector2.Distance(new Vector2(ox, oy), new Vector2(left, top)),
                Vector2.Distance(new Vector2(ox, oy), new Vector2(right, top))),
            MathF.Max(Vector2.Distance(new Vector2(ox, oy), new Vector2(left, bottom)),
                Vector2.Distance(new Vector2(ox, oy), new Vector2(right, bottom))));

        // **Every doodad, with its kind, whether or not the router treats it as solid.** The filter
        // is a decision and this is the evidence it has to answer to: on one site the dig held a
        // single plain `/Doodad` and twenty-six `DoodadNoOverlap`, so a file holding only what the
        // router already believes could never have shown that. See Obstacles.Every.
        foreach (var (kind, where, radius) in Obstacles.Every(gc, site, corner))
        {
            b.AppendLine($"doodad {kind} {Number(where.X)} {Number(where.Y)} {Number(radius)}");
        }

        // **IngameData.TileEntities, which nothing has ever read.** The doodad list above comes from
        // the streamed entity list, and the client's own figures say that drops things from about 169
        // units out - so it is a list of what is NEARBY, not a list of what is THERE. TileEntities
        // hangs off the terrain rather than off streaming, which makes it the one remaining candidate
        // for "the game knows about an obstacle we cannot see".
        //
        // Written flat, with whatever bounds each one carries, because the point is to find out what
        // is in it. Every cell the game has refused on ground both grids call open is a cell this
        // should be checked against; if it is empty or holds only scenery already in the doodad list,
        // that closes the last place the placement rule could be hiding in published data.
        var tiled = Safe.Read<List<Entity>>(() => gc.IngameState.Data.TileEntities, null);

        if (tiled != null)
        {
            var written = 0;

            foreach (var entity in tiled)
            {
                var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

                if (at == Vector2.Zero || Vector2.Distance(at, site) > corner)
                    continue;

                var meta = Safe.Read(entity, static e => e.Metadata, "") ?? "";
                var slash = meta.LastIndexOf('/');
                var bounds = Safe.Read(entity, static e => e.GetComponent<Render>()?.Bounds.X ?? 0f, 0f);

                b.AppendLine($"tileentity {(slash >= 0 ? meta[(slash + 1)..] : meta)} " +
                             $"{Number(at.X)} {Number(at.Y)} " +
                             $"{Number(bounds / Detonator.GridToWorld)}");
                written++;
            }

            b.AppendLine($"tileentities {tiled.Count} in the area, {written} in the box");
        }

        foreach (var (where, radius) in Obstacles.Read(gc, site, corner).All)
            b.AppendLine($"scenery {Number(where.X)} {Number(where.Y)} {Number(radius)}");

        b.AppendLine();

        // **The verdicts, which are the only part a new model can be judged against.** The grid is
        // input; these are the answers. Placeable is about one cell; reach is about an ordered pair,
        // and the order is the point - see the class doc.
        // The origin goes with every verdict. Reach is a claim about a pair, and a capture that
        // recorded only the cell would hand the offline model the same pooled nonsense that made a
        // sweep look as though the game allowed ground half again past its own range. See Settled.
        foreach (var (cell, placeable, _, looks, from) in Settled.Here.Verdicts)
        {
            b.AppendLine($"says {cell.X} {cell.Y} {(placeable ? "yes" : "no")} {looks} " +
                         $"{from.X} {from.Y}");
        }

        b.AppendLine();
        b.AppendLine("# wire lines are: wire <gridX> <gridY> pole|fuse <worldX> <worldY>. GridPos and");
        b.AppendLine("# Pos disagree - GridPos reads whole cells, Pos carries the sub-cell position -");
        b.AppendLine("# and which one the game tests is still open, so both are recorded.");
        b.AppendLine();

        // **Both grids, because the one left out turned out to be the one that is right.** This
        // recorded only the pathfinding data, on the reasoning that the targeting grid reads 5 almost
        // everywhere and so describes nothing. Over a 3,220 cell sweep the game accepted, every
        // single one reads 5 on targeting and 349 read below 4 on pathfinding - so the uniform grid
        // is the one with no false refusals in it, and the informative-looking one is wrong 11% of
        // the time. A capture that holds only one of them cannot be used to settle which.
        b.AppendLine("targeting");

        Rows(b, aiming, left, top, right, bottom);

        b.AppendLine("grid");

        Rows(b, grid, left, top, right, bottom);

        // **The game's own coarse routing grid, whole.** This is the array `0x141F5A6B0` indexes to
        // decide whether a clamped endpoint may land in a block - see NOTES 1d and Peek.Coarse. It is
        // written in full rather than windowed like the others: the whole map is a few tens of
        // kilobytes at half-tile resolution, and the question worth asking of it is whether it
        // predicts the verdicts recorded ELSEWHERE in this file, which a window around the indicator
        // cannot answer. One decimal value per cell, so a byte that encodes more than blocked/clear
        // stays legible.
        var slab = Peek.Coarse(gc);

        if (slab != null)
        {
            b.AppendLine("coarse " + slab.Wider + " " + slab.Higher + " " + slab.FromX + " " +
                         slab.FromY);

            for (var y = 0; y < slab.Higher; y++)
            {
                var line = new StringBuilder();

                line.Append(y);

                for (var x = 0; x < slab.Wider; x++)
                {
                    line.Append(' ');
                    line.Append(slab.Bytes[y * slab.Wider + x]);
                }

                b.AppendLine(line.ToString());
            }
        }

        // **Every `expedition_no_placement` rectangle the site carries.** Decoded rather than
        // fitted: `0x141F5A6B0` tests this tag on the requested point and refuses outright when it
        // hits, so a volume is ground the game will not take however open every published grid says
        // it is. Written before anything depends on it, because a pointer walk this long either
        // reads the authored rectangles or reads nothing at all, and the dump is how that is told
        // apart. See Volumes.
        var volumes = Volumes.Read(gc, new Vector2(ox, oy), corner);

        b.AppendLine();
        b.AppendLine($"volumes {volumes.Count}");
        b.Append(Volumes.Explain(gc, new Vector2(ox, oy), corner));

        foreach (var box in volumes.All)
        {
            b.AppendLine($"volume {box.Left} {box.Top} {box.Right} {box.Bottom} " +
                         $"{box.Wide}x{box.High}");
        }

        b.AppendLine();


        // **The engine's own finished A*, and ours beside it.** Everything else in this file is an
        // observation the model has to be scored against; this is the model's own working, checked
        // against the game's, cell for cell. `0x141D028B0` leaves its node array at `grid + 0x38`
        // and it survives the search, so the g value the engine reached every coarse cell at can be
        // read directly and compared with the g our Wire reached the same cell at.
        //
        // **This is the reading that replaces the scoring.** A landing is the end of a route, a
        // string-pull, a clamp and a spiral; when one disagrees with the game, the mismatch does not
        // say which of the four moved, and a percentage over many of them says even less. Two g
        // values on one coarse cell have no such ambiguity - they agree or the router is wrong, and
        // the lowest-g disagreement is the first step where it went wrong.
        //
        // The array holds only the LAST search, which is the one to wherever the indicator currently
        // sits - so hover the spot in question before dumping.
        // **From the CURSOR, not the indicator.** Read off the engine's own node array: with the aim
        // clamped, its g = 0 sat on the pointer's coarse cell and not the indicator's, and every one
        // of the 37 cells it had also visited was out by the same single step. The indicator is the
        // LANDING - the far end of a route that has already been cut and settled - so tracing from
        // it compares two different searches and calls the offset a routing bug. They agree only
        // while nothing clamps, which is exactly when the comparison has nothing to say.
        var head = Border.Chain(gc);
        var pointer = Safe.Read(() => gc.IngameState.ServerData.GridMousePosition, default);

        // **The landing comes off the indicator entity, not off the panel.**
        // `PlacementIndicatorGridPosition` is the point the cursor is asking for, unclamped - see
        // Detonator.RequestedGridPosition, which says so - and reading it as the landing left the comparison
        // below unable to fail in the only case it exists for. It answered AGREE whenever nothing
        // clamped, because then the request IS the landing, and DIFFER on every clamped aim
        // whatever the model said. One capture reported `landing: game 1903,1013  ours 1892,989
        // DIFFER` while the game had put the explosive on (1894,987): the model was out by under
        // three grid, and the line said nothing about it either way.
        var landed = Detonator.PlacementIndicatorGridPosition(gc);
        var aimX = (int)MathF.Round(landed.X);
        var aimY = (int)MathF.Round(landed.Y);

        if (slab != null && head.Count > 0 && (pointer.X != 0 || pointer.Y != 0))
        {
            var walkable = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);
            var wire = new Wire(walkable, slab);

            if (wire.Ready)
            {
                wire.Trace(head[^1], new Vector2(pointer.X, pointer.Y));
            }

            var costs = wire.Costs;

            b.AppendLine();
            b.AppendLine("engine-search");
            b.AppendLine($"  traced from head {head[^1].X},{head[^1].Y} to the pointer at " +
                         $"{pointer.X},{pointer.Y}; the game landed it at " +
                         (landed == Vector2.Zero
                             ? "- the indicator is not on screen, so there is nothing to compare"
                             : $"{aimX},{aimY}" +
                               ((int)MathF.Round(pointer.X) == aimX &&
                                (int)MathF.Round(pointer.Y) == aimY
                                   ? " (not clamped)"
                                   : " (CLAMPED - the two differ)")));
            b.AppendLine(Peek.Searched(gc,
                (x, y) => costs.TryGetValue((x, y), out var g) ? g : null));

            // **The clamp, against the game's own answer for the same aim.** With the router now
            // confirmed cell for cell against the engine's node array, a wrong verdict on the
            // overlay can only come from what happens after the search - the string-pull, the cut
            // to the reach, or the spiral. The indicator entity is the game's landing for this
            // cursor, so printing ours beside it measures exactly that stretch and nothing else.
            //
            // `says` is what the overlay draws: green means the bomb arrives where it was asked
            // for. A green dot the game clamps away from is this line disagreeing.
            if (wire.Ready && landed != Vector2.Zero)
            {
                var want = new Vector2(pointer.X, pointer.Y);
                var reach = MathF.Max(1f, Detonator.PlacementRange(gc));
                var ours = wire.Landing(head[^1], want, reach);
                var says = wire.Lands(head[^1], want, reach);

                b.AppendLine($"  reach {reach:0.##}; wire {wire.Length(head[^1], want):0.###}");
                b.AppendLine($"  landing: game {aimX},{aimY}   ours " +
                             $"{MathF.Round(ours.X)},{MathF.Round(ours.Y)}   " +
                             ((int)MathF.Round(ours.X) == aimX && (int)MathF.Round(ours.Y) == aimY
                                 ? "AGREE"
                                 : $"DIFFER by {Vector2.Distance(ours, new Vector2(aimX, aimY)):0.##} grid"));
                b.AppendLine($"  the overlay draws this spot {(says ? "GREEN" : "red")}" +
                             $" (Lands = {says})");
            }
        }

        // **The frame copies, back for one question.** The engine's line test reads `terrain + 0xD0`
        // (`0x141D0D220` layer 0) and WHICH of ExileCore2's arrays sits at that offset is the one
        // link in the chain taken on trust rather than tested. A frame copy differs from the static
        // grid exactly where the map has changed since it loaded, which is the shape of the
        // disagreement being chased: one line the static grid calls blocked and the game treats as
        // clear. See NOTES 1i.
        var framePath = Safe.Read<int[][]>(() => gc.IngameState.Data.RawFramePathfindingData, null);

        if (framePath != null && framePath.Length > 0)
        {
            b.AppendLine("framegrid");

            Rows(b, framePath, left, top, right, bottom);
        }

        var frameAim = Safe.Read<int[][]>(() => gc.IngameState.Data.RawFrameTerrainTargetingData, null);

        if (frameAim != null && frameAim.Length > 0)
        {
            b.AppendLine("frametargeting");

            Rows(b, frameAim, left, top, right, bottom);
        }

        var path = Path.Combine(directory, $"site_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        File.WriteAllText(path, b.ToString());

        return $"site captured to {Path.GetFileName(path)}: " +
               $"{right - left + 1} x {bottom - top + 1} cells around ({ox},{oy})";
    }

    /// <summary>One grid's rows as digits, which is both the most compact form and a readable one.</summary>
    /// <summary>
    /// The window of terrain to record: everything this file mentions, plus a margin.
    ///
    /// **A capture whose grid does not cover its own wire cannot answer anything asked of it.** This
    /// took a fixed Span around the dig site, and a chain is not a local thing - on a Port site the
    /// head sat 357 grid from the site marker while the box reached 160, so every pole, every clamp
    /// reading and the whole frontier they describe fell outside the recorded terrain. The file went
    /// on listing those positions, which made the omission invisible: the coordinates were all there
    /// and the ground under them was simply absent. Offline analysis then read the rows it did have,
    /// found the chain head standing on a blocked cell, and drew conclusions from the wrong region.
    ///
    /// So the box is derived from the contents rather than assumed: the site, every placed explosive,
    /// every pole and fuse, and every cursor reading. Span becomes the minimum rather than the size.
    ///
    /// Furthest caps it, because a capture nobody can open is no better than one that misses the
    /// action - a box is one digit per cell twice over, so 600 each way is already a megabyte.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) Boxed(GameController gc, int[][] grid,
        int ox, int oy)
    {
        var lowX = ox - Span;
        var highX = ox + Span;
        var lowY = oy - Span;
        var highY = oy + Span;

        void Cover(float x, float y)
        {
            lowX = Math.Min(lowX, (int)x - Margin);
            highX = Math.Max(highX, (int)x + Margin);
            lowY = Math.Min(lowY, (int)y - Margin);
            highY = Math.Max(highY, (int)y + Margin);
        }

        foreach (var at in Detonator.PlacedExplosiveGridPositions(gc) ?? [])
            Cover(at.X, at.Y);

        foreach (var (at, _, _) in Wires(gc))
            Cover(at.X, at.Y);


        lowX = Math.Max(lowX, ox - Furthest);
        highX = Math.Min(highX, ox + Furthest);
        lowY = Math.Max(lowY, oy - Furthest);
        highY = Math.Min(highY, oy + Furthest);

        return (Math.Max(0, lowX), Math.Max(0, lowY),
            Math.Min(Width(grid) - 1, highX), Math.Min(grid.Length - 1, highY));
    }

    private static void Rows(StringBuilder b, int[][] grid, int left, int top, int right, int bottom)
    {
        if (grid == null)
            return;

        for (var y = top; y <= bottom && y < grid.Length; y++)
        {
            var row = grid[y];

            if (row == null)
                continue;

            var line = new char[right - left + 1];

            for (var x = left; x <= right; x++)
            {
                var value = x >= 0 && x < row.Length ? row[x] : 0;

                line[x - left] = value is >= 0 and <= 9 ? (char)('0' + value) : '?';
            }

            b.Append(y).Append(' ').AppendLine(new string(line));
        }
    }

    /// <summary>
    /// Every fuse and connector pole on the ground, which together trace the chain. See Write.
    ///
    /// The kind travels with the position: they sit on the same cell, so without it the list holds
    /// each point twice and says nothing about which entity put it there.
    /// </summary>
    private static string Exact(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static IEnumerable<(Vector2 At, string Kind, Vector2 World)> Wires(GameController gc)
    {
        var found = new List<(Vector2 At, string Kind, Vector2 World)>();

        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);

        if (entities == null)
            return found;

        foreach (var entity in entities)
        {
            var path = Safe.Read(entity, static e => e.Metadata, "");

            if (path == null ||
                (!path.EndsWith("/ExpeditionExplosiveFuse", StringComparison.OrdinalIgnoreCase) &&
                 !path.EndsWith("/ExpeditionConnectorPole", StringComparison.OrdinalIgnoreCase)))
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

            if (at != Vector2.Zero)
            {
                var world = Safe.Read(entity, static e => e.Pos, default(System.Numerics.Vector3));

                found.Add((at,
                    path.EndsWith("/ExpeditionConnectorPole", StringComparison.OrdinalIgnoreCase)
                        ? "pole"
                        : "fuse",
                    new Vector2(world.X, world.Y)));
            }
        }

        return found;
    }

    private static int Width(int[][] grid)
    {
        var most = 0;

        foreach (var row in grid)
            most = row != null && row.Length > most ? row.Length : most;

        return most;
    }

    /// <summary>Invariant culture, so a capture written on one machine parses on another.</summary>
    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
