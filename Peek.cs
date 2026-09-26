using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// A read-only look at the terrain structure the client keeps, past what ExileCore2 surfaces.
///
/// **Why this exists.** Every published terrain source has been tested against the game's own
/// placement verdicts and none of them is the rule - see NOTES sections 1a and "TileEntities closes
/// the last door". The most thorough public reverse engineering of the terrain struct is
/// GameHelper2's `TerrainStruct`, and it is nearly complete:
///
/// <code>
/// 0x18  TotalTiles
/// 0x28  TileDetailsPtr        -> TileStructure, one per tile
/// 0x50  Unknown1              &lt;-- commented out, never identified
/// 0x68  Unknown2              &lt;-- commented out, never identified
/// 0xD0  GridWalkableData      = ExileCore2's RawPathfindingData
/// 0xE8  GridLandscapeData     = ExileCore2's RawTerrainTargetingData
/// 0x130 BytesPerRow
/// 0x134 TileHeightMultiplier
/// </code>
///
/// Two vectors in the middle of it have never been named by anyone, in any project. They are the last
/// place in the client's terrain data where a placement rule could be hiding, so this reports them.
///
/// **It locates the structure from the data rather than from an offset.** ExileCore2 does not publish
/// where the terrain struct sits inside IngameData, and hardcoding GameHelper's offsets would be
/// guessing across a different base and a different patch. So every std::vector-shaped triple in the
/// region is listed with its byte length, and the walkable grid is identified by the length it has to
/// have - rows times bytes-per-row. That fixes the struct's base, and the two unknowns are then read
/// relative to it.
///
/// **Read-only, and the API cannot be otherwise**: ExileCore2's `IMemory` exposes Read methods and
/// nothing else - there is no write call in it to reach for. Nothing here writes to the game.
/// </summary>
public static class Peek
{
    /// <summary>
    /// Everything found, as text for the debug dump. Never throws: a bad address reads as nothing.
    /// </summary>
    public static string Describe(GameController gc)
    {
        var b = new StringBuilder();
        var seat = Safe.Read(gc, static g => g.IngameState.Data.Address, 0L);
        var dims = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, default);

        b.AppendLine("  IngameData at " + seat.ToString("X") + ", area " + dims.X + " x " + dims.Y);

        if (seat == 0)
            return b.Append("  no address - not in game?").ToString();

        // The grid ExileCore2 hands us, so the same buffer can be recognised in raw memory by length.
        var grid = Safe.Read<int[][]>(() => gc.IngameState.Data.RawPathfindingData, null);
        var rows = grid?.Length ?? 0;
        var cols = rows > 0 ? grid[0]?.Length ?? 0 : 0;

        b.AppendLine("  RawPathfindingData is " + cols + " x " + rows +
                     "; nibble-packed that is " + (long)rows * ((cols + 1) / 2) + " bytes");
        b.AppendLine();

        var found = Vectors(gc, seat, Window);

        if (found.Count == 0)
            return b.Append("  no vector-shaped fields found").ToString();

        b.AppendLine("  std::vector-shaped triples in IngameData (offset, first, bytes):");

        foreach (var (at, first, bytes) in found)
            b.AppendLine("    +0x" + at.ToString("X4") + "  " + first.ToString("X12") + "  " +
                         bytes.ToString("N0").PadLeft(14) + " bytes" + Guess(bytes, rows, cols));

        // Anchor on the walkable grid: its length is rows * bytesPerRow, and bytesPerRow is at least
        // half the column count (two cells to a byte) and no more than the column count.
        var anchor = -1;

        foreach (var (at, _, bytes) in found)
        {
            if (rows <= 0 || bytes <= 0 || bytes % rows != 0)
                continue;

            var perRow = bytes / rows;

            if (perRow < (cols + 1) / 2 || perRow > cols)
                continue;

            // **The first, not the last.** There are four grids of identical shape - the two static
            // layers and their frame copies - laid out 0x18 apart, and GameHelper2's 0xD0 names the
            // first. Anchoring on the last put the struct base 0x48 too high and read the unknowns as
            // zero. With the first, the base lands on 0x8D0, which is exactly where GameHelper2's
            // AreaInstanceOffsets puts TerrainStruct - two independent maps agreeing.
            anchor = at;

            break;
        }

        b.AppendLine();

        if (anchor < 0)
        {
            b.AppendLine("  could not identify the walkable grid by its length, so the struct base is");
            b.AppendLine("  unknown - read the offsets above against GameHelper2's TerrainStruct by hand");

            return b.ToString();
        }

        var terrain = anchor - Offsets.Layers;

        b.AppendLine("  walkable grid at +0x" + anchor.ToString("X4") + ", so TerrainStruct begins at +0x" +
                     terrain.ToString("X4") + " (walkable sits 0x" + Offsets.Layers.ToString("X") + " into it)");

        var tiles = (long)dims.X / Tile * ((long)dims.Y / Tile);

        b.AppendLine("  every vector inside TerrainStruct, by its offset within it:");

        foreach (var (at, first, bytes) in found)
        {
            var inside = at - terrain;

            if (inside < 0 || inside >= Size)
                continue;

            b.AppendLine("    +0x" + inside.ToString("X3") + "  " + bytes.ToString("N0").PadLeft(14) +
                         " bytes  " + Named(inside) + Shape(bytes, rows, cols, tiles));
        }

        b.AppendLine();
        b.AppendLine("  the unnamed ones, first bytes:");

        foreach (var (at, first, bytes) in found)
        {
            var inside = at - terrain;

            if (inside < 0 || inside >= Size || Named(inside) != "")
                continue;

            b.AppendLine("    +0x" + inside.ToString("X3") + "  " + Bytes(gc, first, Preview));
        }

        b.AppendLine();
        b.Append(Subtiles(gc, seat, terrain, cols));
        b.AppendLine();
        b.Append(Sent(gc, rows, cols));
        b.AppendLine();
        b.Append(Hunt(gc));
        b.AppendLine();
        b.Append(Virtual(gc));
        b.AppendLine();
        b.Append(Cells(gc));

        return b.ToString();
    }

    /// <summary>
    /// Look for the clamp's reachable set: an array of grid cells near the dig site.
    ///
    /// **Why this is the thing to find.** The indicator does not refuse a cell it cannot reach, it
    /// SNAPS to the nearest cell it can - and measured against the game, 9,246 readings put the
    /// indicator on ground that direct aim refuses, with the verdict saying yes. So the set of cells
    /// the clamp will land on is not the set direct aim accepts, and it is chosen from something the
    /// client already holds. A precomputed reachable set would be exactly that, and it is the answer
    /// to "every placeable location" directly rather than a rule to be derived.
    ///
    /// Searched by SHAPE rather than by value, because the contents are not known in advance: a run of
    /// eight-byte entries that all read as a pair of int32 lying inside this area's grid and within a
    /// couple of chain lengths of the detonator is not something ordinary data does by accident. The
    /// explosives array was found the same way and is the proof the shape test works - it will show up
    /// here too, and seeing it confirms the search is functioning.
    /// </summary>
    private static string Cells(GameController gc)
    {
        var b = new StringBuilder();
        var site = Detonator.DetonatorGridPosition(gc);
        var dims = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, default);

        if (site == Vector2.Zero)
            return "  no dig site - cannot judge which coordinates are plausible";

        b.AppendLine("  looking for an array of grid cells near the site (" + (int)site.X + "," +
                     (int)site.Y + "):");

        var info = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info, null);
        var anchors = new List<(string Name, long At)>
        {
            ("ClientExpedition", Safe.Read(info, static i => i.Address, 0L)),
            ("ServerData", Safe.Read(gc, static g => g.IngameState.Data.ServerData?.Address ?? 0L, 0L)),
        };

        var many = Safe.Read(info, static i => i.Encounters?.Count ?? 0, 0);

        for (var i = 0; i < many && i < Encounters; i++)
            anchors.Add(("Encounter" + i,
                Safe.Read((info, i), static x => x.info.Encounters[x.i].Address, 0L)));

        var told = 0;
        var seen = new HashSet<long>();
        var queue = new List<(string Name, long At, int Depth)>();

        foreach (var (name, at) in anchors)
            if (at != 0)
                queue.Add((name, at, 0));

        for (var step = 0; step < queue.Count && told < Told; step++)
        {
            var (name, at, depth) = queue[step];
            var own = Safe.Read(() => gc.Memory.ReadBytes(at, depth == 0 ? Sweep : Listing), null);

            {
            for (var off = 0; own != null && off + 8 <= own.Length && told < Told; off += 8)
            {
                var ptr = BitConverter.ToInt64(own, off);

                if (ptr <= Offsets.Lowest || ptr >= Highest || (ptr & 7) != 0 || !seen.Add(ptr))
                    continue;

                var raw = Safe.Read(() => gc.Memory.ReadBytes(ptr, Listing), null);

                if (raw == null || raw.Length < 64)
                    continue;

                // Structures lead onward; a cell list need not hang off the anchor directly.
                if (depth == 0)
                {
                    for (var i = 0; i + 8 <= raw.Length && queue.Count < Reaches; i += 8)
                    {
                        var next = BitConverter.ToInt64(raw, i);

                        if (next > Offsets.Lowest && next < Highest && (next & 7) == 0)
                            queue.Add((name + "+0x" + off.ToString("X3"), next, 1));
                    }
                }

                var run = 0;
                var best = 0;

                for (var i = 0; i + 8 <= raw.Length; i += 8)
                {
                    var x = BitConverter.ToInt32(raw, i);
                    var y = BitConverter.ToInt32(raw, i + 4);
                    var near = x > 0 && y > 0 && x < dims.X && y < dims.Y &&
                               MathF.Abs(x - site.X) < Around2 && MathF.Abs(y - site.Y) < Around2;

                    run = near ? run + 1 : 0;
                    best = Math.Max(best, run);
                }

                if (best < Least2)
                    continue;

                told++;

                var first = BitConverter.ToInt32(raw, 0);
                var then = BitConverter.ToInt32(raw, 4);

                b.AppendLine("    [" + depth + "] " + name + "+0x" + off.ToString("X3") + " -> " +
                             ptr.ToString("X") + "  run of " + best + " plausible cells, starts (" +
                             first + "," + then + ")");
            }
            }
        }

        if (told == 0)
            b.AppendLine("    nothing shaped like a cell list behind any of them");

        return b.ToString();
    }

    /// <summary>How far from the dig site a coordinate may be and still look like one of its cells.</summary>
    private const int Around2 = 400;

    /// <summary>
    /// How long a run of plausible cells has to be before it is worth reporting.
    ///
    /// **Three, because the control case is four.** This was eight, and the explosives array - the
    /// known cell list, put there deliberately as the check that the search works - holds only the
    /// four explosives placed, so the threshold excluded the one thing that proves the search
    /// functions. It reported "nothing" and the nothing was mine.
    /// </summary>
    private const int Least2 = 3;

    /// <summary>How much of each buffer to read when looking for a cell list.</summary>
    private const int Listing = 0x1000;

    /// <summary>A cap on how many places the two-level walk may queue up.</summary>
    private const int Reaches = 600;

    /// <summary>
    /// Resolve the virtual calls the route builder makes, which static analysis cannot follow.
    ///
    /// **The one wall left in reading the code.** `SetPlacement` hands the route builder at
    /// `01F5A6B0` a session object, and that function dispatches four times through
    /// `call qword ptr [rax + 0x1a0]` - slot 52 of the object's vtable. A disassembler cannot say
    /// where those go, because the target depends on the object's runtime type. Nothing below the
    /// route builder reads the terrain grid in 71 statically reachable functions, so if the client
    /// queries terrain at all, it is behind one of these.
    ///
    /// The chain is walkable because the layout is known: `SetPlacement` passes
    /// `[[ClientExpedition+0x48] + 0x20f0]`, and ClientExpedition is the object ExileCore2 hands out
    /// as ExpeditionDetonatorInfo. Read the vtable pointer off it, take slot 0x1a0, and subtract the
    /// module base to get something that can be looked up in the disassembler.
    ///
    /// Read-only. It resolves an address and prints it.
    /// </summary>
    private static string Virtual(GameController gc)
    {
        var b = new StringBuilder();
        var expedition = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);

        if (expedition == 0)
            return "  no ClientExpedition - not at a dig site?";

        var seat = Safe.Read(gc, static g => g.Memory.AddressOfProcess, 0L);
        var held = Qword(gc, expedition + Offsets.ExpeditionOwner);
        var session = held == 0 ? 0 : Qword(gc, held + Offsets.OwnerSession);
        var table = session == 0 ? 0 : Qword(gc, session);

        b.AppendLine("  resolving the route builder's virtual calls:");
        b.AppendLine("    module base        " + seat.ToString("X"));
        b.AppendLine("    ClientExpedition   " + expedition.ToString("X"));
        b.AppendLine("    +0x48              " + held.ToString("X"));
        b.AppendLine("    +0x20f0 (session)  " + session.ToString("X"));
        b.AppendLine("    vtable             " + table.ToString("X"));

        if (table <= Offsets.Lowest)
            return b.Append("    chain broke - one of those is not a pointer").ToString();

        // The slot the route builder calls, plus its neighbours, since a nearby slot is often the
        // same family of query and naming one names the others.
        for (var slot = Offsets.VtableTerrain - 0x20; slot <= Offsets.VtableTerrain + 0x20; slot += 8)
        {
            var target = Qword(gc, table + slot);

            if (target <= Offsets.Lowest || seat == 0 || target < seat)
                continue;

            b.AppendLine("    [+0x" + slot.ToString("X3") + "] -> " + target.ToString("X") +
                         "   rva " + (target - seat).ToString("X8") +
                         (slot == Offsets.VtableTerrain ? "   <-- the one it calls" : ""));
        }

        // **Offsets.VtableTerrain 0x1A0 is `lea rax, [rcx + 0x8D0]; ret` - it hands back this+0x8D0, and 0x8D0 is
        // where TerrainStruct sits.** So the route builder asks for the terrain, four times. Worth
        // confirming rather than reading off the offset, because the session here is not the same
        // address ExileCore2 calls IngameData, and two objects can share a field offset by accident.
        // A TerrainStruct is recognisable: +0xD0 is a vector whose length is the whole packed grid.
        var terrain = session + Offsets.SessionTerrain;
        var first = Qword(gc, terrain + Offsets.Layers);
        var last = Qword(gc, terrain + Offsets.Layers + 8);
        var grid = Safe.Read<int[][]>(() => gc.IngameState.Data.RawPathfindingData, null);
        var rows = grid?.Length ?? 0;
        var cols = rows > 0 ? grid[0]?.Length ?? 0 : 0;

        b.AppendLine();
        b.AppendLine("  is what it returns a TerrainStruct?");
        b.AppendLine("    session+0x8D0      " + terrain.ToString("X"));
        b.AppendLine("    its +0xD0 vector   " + first.ToString("X") + " .. " + last.ToString("X") +
                     (last > first ? "  = " + (last - first).ToString("N0") + " bytes" : ""));
        b.AppendLine("    the grid should be " + ((long)rows * ((cols + 1) / 2)).ToString("N0") +
                     " bytes (" + cols + " x " + rows + ", two cells a byte)");
        b.AppendLine("    verdict            " +
                     (last > first && last - first == (long)rows * ((cols + 1) / 2)
                         ? "YES - the route builder is reading the terrain grid"
                         : "no - it is some other object at that offset"));

        // **The kind the route builder looks for on a tile.** `0x141E7E500` walks a tile's object
        // list and returns true when one of them contains the point AND its 16-bit id at +0x40 equals
        // a value the caller passes. Both call sites pass the same global, at rva 0x047159B4, and it
        // is uninitialised in the file - written at load. So it can only be read here.
        //
        // This is the last undecoded input on the placement path: a lookup for one particular kind of
        // object, by id, at a point. Knowing the number is the first half; the ids actually present on
        // the tiles around the indicator are printed beside it, so a match is visible on sight.
        var wanted = Word(gc, seat + Offsets.TagGlobal);

        b.AppendLine();
        b.AppendLine("  the kind the route builder searches tiles for:");
        b.AppendLine("    global at rva " + Offsets.TagGlobal.ToString("X8") + " = " + wanted +
                     " (0x" + wanted.ToString("X4") + ")");

        // Walk the tile records around the indicator and collect the ids their objects carry.
        var tiles = Qword(gc, terrain + Offsets.TileArray);
        var wide = Qword(gc, terrain + Offsets.NumTileIndexCols);
        var on = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacementIndicatorGridPosition ?? default, default);
        var kinds = new Dictionary<int, int>();

        if (tiles > Offsets.Lowest && wide > 0)
        {
            for (var ty = (int)on.Y / Tile - 1; ty <= (int)on.Y / Tile + 1; ty++)
            {
                for (var tx = (int)on.X / Tile - 1; tx <= (int)on.X / Tile + 1; tx++)
                {
                    if (tx < 0 || ty < 0 || tx >= wide) continue;

                    var rec = tiles + (ty * wide + tx) * Offsets.TileBytes;
                    var from = Qword(gc, rec + 0x10);
                    var till = Qword(gc, rec + 0x18);

                    for (var at = from; at > Offsets.Lowest && at + 16 <= till && at - from < 0x400; at += 16)
                    {
                        // **Follow every level, because the game resolves before reading.** The id
                        // is not at +0x40 off the entry: `0x141E7E500` takes `[entry]`, then `[+8]`,
                        // hands that to `0x1417306F0` which goes `[+8]` and `[+0x28]` again, and only
                        // then reads `word [+0x40]`. Rather than guess which level carries it, walk
                        // the chain and record the word at each, so the one holding the wanted id
                        // identifies itself.
                        var step = Qword(gc, at);

                        for (var deep = 0; deep < Deep && step > Offsets.Lowest; deep++)
                        {
                            var id = Word(gc, step + 0x40);
                            var key = deep * 100000 + id;

                            kinds[key] = kinds.TryGetValue(key, out var n) ? n + 1 : 1;
                            step = Qword(gc, step + (deep == 1 ? 0x28 : 8));
                        }
                    }
                }
            }
        }

        b.AppendLine("    ids on the tiles around the indicator (" + on.X + "," + on.Y + "):");

        if (kinds.Count == 0)
            b.AppendLine("      none readable");

        foreach (var pair in kinds)
        {
            var deep = pair.Key / 100000;
            var id = pair.Key % 100000;

            b.AppendLine("      level " + deep + "  id " + id + " (0x" + id.ToString("X4") + ") x" +
                         pair.Value + (id == wanted ? "   <-- MATCHES the one it searches for" : ""));
        }

        return b.ToString();
    }

    /// <summary>A 16-bit value at this address, or 0 if unreadable.</summary>
    private static int Word(GameController gc, long at)
    {
        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, 2), null);

        return raw == null || raw.Length < 2 ? 0 : BitConverter.ToUInt16(raw, 0);
    }


    /// <summary>How many levels of the object chain to read an id from. See Virtual.</summary>
    private const int Deep = 4;





    /// <summary>
    /// Find the object that holds the explosives, by looking for coordinates we already know.
    ///
    /// **The one structure whose contents are known in advance.** The disassembly says
    /// `ClientExpedition` keeps its placed explosives in a vector at `+0x1E0`, and ExileCore2 does not
    /// expose that object - `ExpeditionDetonatorInfo` is the UI element, not the ruleset. But the
    /// placed grid positions are known exactly, so the array can be found by searching for them, and
    /// whatever holds it is the object wanted.
    ///
    /// That matters even if the verdict turns out to come from the server: a per-cursor-move round
    /// trip is not credible at several hundred cell changes a second, so anything the server decides
    /// must arrive as a REGION and be cached - and a cache lives in a structure that can be found.
    ///
    /// Searches windows either side of every address already in hand. A hit prints the bytes around
    /// it, so an array of positions is recognisable by its stride.
    /// </summary>
    private static string Hunt(GameController gc)
    {
        var b = new StringBuilder();
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed == null || placed.Length == 0)
            return "  no explosives placed - nothing to search for";

        b.AppendLine("  hunting the object that holds the explosives");
        b.AppendLine("  looking for: " + string.Join(", ",
            Array.ConvertAll(placed, p => "(" + (int)p.X + "," + (int)p.Y + ")")));

        var info = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info, null);
        var anchors = new List<(string Name, long At)>
        {
            ("DetonatorInfo", Safe.Read(info, static i => i.Address, 0L)),
            ("ServerData", Safe.Read(gc, static g => g.IngameState.Data.ServerData?.Address ?? 0L, 0L)),
        };

        var many = Safe.Read(info, static i => i.Encounters?.Count ?? 0, 0);

        for (var i = 0; i < many && i < Encounters; i++)
            anchors.Add(("Encounter" + i,
                Safe.Read((info, i), static x => x.info.Encounters[x.i].Address, 0L)));

        var found = 0;

        foreach (var (name, at) in anchors)
        {
            if (at == 0)
                continue;

            var from = at - Span;
            var raw = Safe.Read(() => gc.Memory.ReadBytes(from, Span * 2), null);

            if (raw == null || raw.Length < 16)
            {
                b.AppendLine("    " + name + ": window unreadable");

                continue;
            }

            foreach (var one in placed)
            {
                var needle = new byte[8];

                BitConverter.GetBytes((int)one.X).CopyTo(needle, 0);
                BitConverter.GetBytes((int)one.Y).CopyTo(needle, 4);

                for (var i = 0; i + 8 <= raw.Length; i += 4)
                {
                    var same = true;

                    for (var j = 0; j < 8 && same; j++)
                        same = raw[i + j] == needle[j];

                    if (!same)
                        continue;

                    found++;
                    b.AppendLine("    (" + (int)one.X + "," + (int)one.Y + ") at " +
                                 (from + i).ToString("X") + "  = " + name + " + 0x" +
                                 (from + i - at).ToString("X"));
                    b.AppendLine("       around it: " + Bytes(gc, from + i - 16, 48));

                    break;
                }
            }
        }

        if (found == 0)
            b.AppendLine("    not found in any window - the object is elsewhere in the heap");

        // **Follow the pointers instead of widening the window.** Only the LAST position was found
        // inline, as a field of the encounter; the placed ARRAY is behind a pointer, and the heap is
        // far too large to sweep. So take every pointer in each anchor's own struct, read what it
        // points at, and look for the positions there. That is the step that reaches an object
        // ExileCore2 never exposes.
        b.AppendLine();
        b.AppendLine("  following pointers one level out:");

        var chased = 0;

        foreach (var (name, at) in anchors)
        {
            if (at == 0)
                continue;

            var raw = Safe.Read(() => gc.Memory.ReadBytes(at, Pointers), null);
            var gone = new HashSet<long>();

            for (var off = 0; raw != null && off + 8 <= raw.Length; off += 8)
            {
                var ptr = BitConverter.ToInt64(raw, off);

                if (ptr <= Offsets.Lowest || ptr >= Highest || (ptr & 7) != 0 || !gone.Add(ptr))
                    continue;

                var body = Safe.Read(() => gc.Memory.ReadBytes(ptr, Behind), null);

                if (body == null || body.Length < 8)
                    continue;

                foreach (var one in placed)
                {
                    var wantX = (int)one.X;
                    var wantY = (int)one.Y;

                    for (var i = 0; i + 8 <= body.Length; i += 4)
                    {
                        if (BitConverter.ToInt32(body, i) != wantX ||
                            BitConverter.ToInt32(body, i + 4) != wantY)
                            continue;

                        chased++;
                        b.AppendLine("    (" + wantX + "," + wantY + ") behind " + name + "+0x" +
                                     off.ToString("X3") + " -> " + ptr.ToString("X") + " + 0x" +
                                     i.ToString("X") + "   " + Bytes(gc, ptr + i - 8, 40));

                        break;
                    }
                }
            }
        }

        if (chased == 0)
            b.AppendLine("    nothing behind any pointer either");

        b.AppendLine();
        b.Append(Plain(gc, anchors));

        return b.ToString();
    }

    /// <summary>
    /// Every buffer behind ClientExpedition, classified by what its bytes look like.
    ///
    /// **Length was the wrong question.** The earlier passes measured buffers and asked whether the
    /// size was grid-shaped, which found nothing and produced a page of false positives from round
    /// capacities. A cached placement region is recognisable by its CONTENT instead: plain data, a
    /// handful of distinct byte values, no pointer-looking qwords. Pointers in this process all carry
    /// the same high bytes, so a buffer full of them is obvious and can be dismissed.
    ///
    /// `ExpeditionDetonatorInfo` is `ClientExpedition` - its +0x1E0 is the encounters vector the
    /// disassembly iterates - so everything reachable from it is the ruleset's own state. If the
    /// server sent a region and the client cached it, it is behind one of these pointers.
    /// </summary>
    private static string Plain(GameController gc, List<(string Name, long At)> anchors)
    {
        var b = new StringBuilder();

        b.AppendLine("  buffers behind ClientExpedition, by what their bytes look like:");

        var seen = new HashSet<long>();
        var told = 0;
        var reads = 0;

        // **Two levels, budgeted.** One level found only UI geometry, pointer tables and a single
        // high-entropy blob. A cached region reached through a container rather than a raw field
        // would sit one hop further out, so the pointers inside each first-level buffer are followed
        // too - with a hard cap on reads, because the fan-out is otherwise unbounded and this runs
        // where a stall is visible.
        var queue = new List<(string Path, long At, int Depth)>();

        foreach (var (name, at) in anchors)
            if (at != 0)
                queue.Add((name, at, 0));

        for (var step = 0; step < queue.Count && told < Told && reads < Reads; step++)
        {
            var (name, at, depth) = queue[step];
            var span = depth == 0 ? Pointers : Behind;
            var raw = Safe.Read(() => gc.Memory.ReadBytes(at, span), null);
            var taken = 0;

            for (var off = 0; raw != null && off + 8 <= raw.Length && told < Told && reads < Reads;
                 off += 8)
            {
                var ptr = BitConverter.ToInt64(raw, off);

                if (ptr <= Offsets.Lowest || ptr >= Highest || (ptr & 7) != 0 || !seen.Add(ptr))
                    continue;

                reads++;

                var body = Safe.Read(() => gc.Memory.ReadBytes(ptr, Behind), null);

                if (body == null || body.Length < 64)
                    continue;

                var kinds = new HashSet<byte>();
                var zero = 0;
                var pointerish = 0;

                for (var i = 0; i < body.Length; i++)
                {
                    kinds.Add(body[i]);

                    if (body[i] == 0)
                        zero++;
                }

                // Every heap pointer here shares its top bytes; count qwords that look like one.
                for (var i = 0; i + 8 <= body.Length; i += 8)
                {
                    var q = BitConverter.ToInt64(body, i);

                    if (q > Offsets.Lowest && q < Highest && (q & 7) == 0 && (q >> 32) != 0)
                        pointerish++;
                }

                var qwords = body.Length / 8;
                var dense = 100 * zero / body.Length;

                // **Stop filtering on guesses about what a mask looks like.** Two filters here
                // were each wrong at opposite ends. Dropping buffers more than 96% zero excluded the
                // shape a SPARSE mask has, where only the dig site is set. Dropping buffers with more
                // than 24 distinct byte values excluded the shape a DENSE one has, since a packed
                // bitmask runs 0xFF, 0x7F, 0x03, 0xC0 and can use most of the byte range. Between
                // them they rejected both ends of the only thing being looked for.
                //
                // Only one property really separates data from structure: a buffer of pointers is
                // full of qwords carrying this process's heap high bytes, and a mask is not.
                // Structure leads onward; follow it at depth 0 only, so the walk stays bounded.
                if (pointerish * 4 > qwords)
                {
                    if (depth < 1 && taken < Branch)
                    {
                        queue.Add((name + "+0x" + off.ToString("X3"), ptr, depth + 1));
                        taken++;
                    }

                    continue;
                }

                if (kinds.Count < 2)
                    continue;

                told++;

                // Set bits matter more than set bytes: a bitmask packs eight cells to a byte.
                var bits = 0;

                foreach (var one in body)
                    bits += System.Numerics.BitOperations.PopCount(one);

                b.AppendLine("    [" + depth + "] " + name + "+0x" + off.ToString("X3") + " -> " +
                             ptr.ToString("X") + "  " + kinds.Count + " distinct values, " + dense +
                             "% zero bytes, " + (100 * bits / (body.Length * 8)) + "% set bits, " +
                             pointerish + "/" + qwords + " pointer-shaped");
                b.AppendLine("       " + Bytes(gc, ptr, Preview));
            }
        }

        b.AppendLine("    (" + reads + " buffers read, " + queue.Count + " visited)");

        if (told == 0)
            b.AppendLine("    none - every buffer behind it is pointers, or all zeros, or high entropy");

        return b.ToString();
    }

    /// <summary>How many buffers to report, so one bad read cannot fill the file.</summary>
    private const int Told = 40;

    /// <summary>A hard cap on reads, because a two-level walk fans out without one.</summary>
    private const int Reads = 400;

    /// <summary>How many pointers to follow out of any one structure. See Plain.</summary>
    private const int Branch = 12;

    /// <summary>How much of an anchor's own struct to read pointers out of. See Hunt.</summary>
    private const int Pointers = 0x600;

    /// <summary>How much to read behind each pointer when chasing. See Hunt.</summary>
    private const int Behind = 0x800;

    /// <summary>How far either side of a known address to search. See Hunt.</summary>
    private const int Span = 0x20000;

    /// <summary>
    /// The expedition state the SERVER pushes, scanned for anything grid-shaped.
    ///
    /// **Because a per-cursor-move server query is not credible.** Sweeping the mouse changes cell
    /// several hundred times a second and the indicator answers instantly, so `blocked` cannot be a
    /// round trip per point. That leaves two possibilities: the client computes it, or the server sent
    /// a region ONCE and the client is looking it up.
    ///
    /// The second has never been tested. The terrain struct was searched for a mask and has none, but
    /// a server-provided mask would not live in terrain - it would live in the encounter state. So
    /// this scans the detonator info, each encounter, and ServerData for vector-shaped buffers, and
    /// flags any whose length is shaped by the grid. One bit per cell over this area is 906,108 bytes;
    /// a bitmask of the dig site alone would be far smaller, so anything that divides by the row count
    /// is worth seeing.
    /// </summary>
    private static string Sent(GameController gc, int rows, int cols)
    {
        var b = new StringBuilder();
        var info = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info, null);

        b.AppendLine("  what the server has pushed about this encounter:");

        var seats = new List<(string Name, long At)>
        {
            ("ServerData", Safe.Read(gc, static g => g.IngameState.Data.ServerData?.Address ?? 0L, 0L)),
            ("DetonatorInfo", Safe.Read(info, static i => i.Address, 0L)),
        };

        var many = Safe.Read(info, static i => i.Encounters?.Count ?? 0, 0);

        for (var i = 0; i < many && i < Encounters; i++)
        {
            var at = Safe.Read((info, i), static x => x.info.Encounters[x.i].Address, 0L);

            seats.Add(("Encounter" + i, at));
        }

        foreach (var (name, at) in seats)
        {
            if (at == 0)
            {
                b.AppendLine("    " + name + ": no address");

                continue;
            }

            b.AppendLine("    " + name + " at " + at.ToString("X") + ":");

            // **A low floor and a wide window on purpose.** The first pass used the 4 KB floor that
            // suits IngameData and a 0x600 window, and found nothing - but a mask of a DIG SITE rather
            // than the whole map is small: 200x200 cells is 5 KB and 100x100 is 1.25 KB, under that
            // floor entirely. A negative from the wrong threshold is not a negative.
            var found = Vectors(gc, at, Sweep, Small);

            if (found.Count == 0)
                b.AppendLine("      nothing vector-shaped within 0x" + Sweep.ToString("X"));

            foreach (var (off, first, bytes) in found)
                b.AppendLine("      +0x" + off.ToString("X4") + "  vector  " +
                             bytes.ToString("N0").PadLeft(12) + " bytes" + Guess(bytes, rows, cols) +
                             Bits(bytes, rows, cols));

            // A std::vector is three pointers; plenty of arrays are a pointer and a count instead,
            // and the triple test is blind to those.
            var raw = Safe.Read(() => gc.Memory.ReadBytes(at, Sweep), null);
            var said = new HashSet<long>();

            for (var off = 0; raw != null && off + 16 <= raw.Length; off += 8)
            {
                var ptr = BitConverter.ToInt64(raw, off);
                var count = BitConverter.ToInt64(raw, off + 8);

                if (ptr <= Offsets.Lowest || ptr >= Highest || (ptr & 7) != 0) continue;
                if (count < Small || count > 1 << 24) continue;

                // **Reject the shapes this heuristic invents.** Any pointer followed by any plausible
                // number matches it, and the first run proved that: every hit was either a round power
                // of two, which is a capacity and not a cell count, or the same pointer repeated with
                // a drifting neighbour. A mask's length has no reason to be a power of two, so drop
                // those, and drop a repeat of a pointer already reported.
                if ((count & (count - 1)) == 0) continue;
                if (!said.Add(ptr)) continue;

                b.AppendLine("      +0x" + off.ToString("X4") + "  ptr+count  " + ptr.ToString("X12") +
                             " x " + count.ToString("N0") + Guess(count, rows, cols) +
                             Bits(count, rows, cols));
            }
        }

        return b.ToString();
    }

    /// <summary>Whether a length is a plausible bitmask of the grid, whole or windowed.</summary>
    private static string Bits(long bytes, int rows, int cols)
    {
        if (rows <= 0 || cols <= 0 || bytes <= 0)
            return "";

        if (bytes == (long)rows * ((cols + 7) / 8))
            return "  <-- ONE BIT PER CELL over the whole area";

        // A window: any square-ish region whose bitmask would be this long.
        var side = (int)MathF.Sqrt(bytes * 8f);

        return side >= 32 && side <= 4096
            ? "  [a bitmask this size covers about " + side + " x " + side + " cells]"
            : "";
    }

    /// <summary>How far past each server object to sweep for buffers. See Sent.</summary>
    private const int Sweep = 0x2000;

    /// <summary>The smallest buffer the server scan reports - a dig-site mask would be small.</summary>
    private const long Small = 256;

    /// <summary>How many encounters to look at, so a bad read cannot loop long.</summary>
    private const int Encounters = 4;

    /// <summary>
    /// The per-tile records, and the sub-tile array hanging off each one.
    ///
    /// **The only data the client holds at finer than grid resolution.** Every layer tested so far is
    /// one value per grid cell, and a cell-sized layer cannot separate a refused cell from the
    /// accepted one beside it while reading the same in both - which is what the game does. An
    /// explosive's own footprint is about 3.8 grid, so if the game sweeps that against finer geometry
    /// rather than testing one cell, an obstacle no grid layer can express would refuse it.
    ///
    /// GameHelper2 names the field `SubTileHeight` and notes the array "used to be 23x23 subtiles but
    /// now they compressed the array so it's variable length". Whether it is only height is the
    /// question: 23 sub-tiles across a 23-grid tile is one per cell, so a plain height array is the
    /// height map we already have and already ruled out. A LENGTH that is not one entry per cell is
    /// the interesting answer, so the lengths are what this reports.
    /// </summary>
    private static string Subtiles(GameController gc, long seat, int terrain, int cols)
    {
        var b = new StringBuilder();
        var wide = Qword(gc, seat + terrain + Offsets.NumTileIndexCols);
        var high = Qword(gc, seat + terrain + Offsets.NumTileIndexCols + 8);
        var first = Qword(gc, seat + terrain + Offsets.TileArray);
        var last = Qword(gc, seat + terrain + Offsets.TileArray + 8);

        if (first <= 0 || last < first || wide <= 0)
            return "  tile array unreadable";

        var held = (last - first) / Offsets.TileBytes;

        b.AppendLine("  TotalTiles " + wide + " x " + high + "; tile array holds " + held +
                     " entries of 0x" + Offsets.TileBytes.ToString("X"));
        b.AppendLine("  (a tile spans " + Tile + " grid, so one sub-tile per cell would be " +
                     Tile * Tile + " entries)");

        // Around whatever the placement indicator is on, since that is the ground being tested.
        var on = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacementIndicatorGridPosition ?? default, default);
        var tx = on.X / Tile;
        var ty = on.Y / Tile;

        b.AppendLine("  indicator at (" + on.X + "," + on.Y + "), tile (" + tx + "," + ty + ")");
        b.AppendLine();
        b.AppendLine("   tile      subtile bytes   per cell   height  id     rot  tgt");

        for (var y = ty - Around; y <= ty + Around; y++)
        {
            for (var x = tx - Around; x <= tx + Around; x++)
            {
                if (x < 0 || y < 0 || x >= wide || y >= high)
                    continue;

                var at = first + (y * wide + x) * Offsets.TileBytes;
                var raw = Safe.Read(() => gc.Memory.ReadBytes(at, Offsets.TileBytes), null);

                if (raw == null || raw.Length < Offsets.TileBytes)
                    continue;

                var subs = BitConverter.ToInt64(raw, 0);
                var tgt = BitConverter.ToInt64(raw, 8);
                var height = BitConverter.ToInt16(raw, 0x30);
                var idX = raw[0x34];
                var idY = raw[0x35];
                var rot = raw[0x36];

                var sFirst = Qword(gc, subs);
                var sLast = Qword(gc, subs + 8);
                var bytes = sFirst > Offsets.Lowest && sLast >= sFirst ? sLast - sFirst : -1;

                // **The whole entry, verbatim.** The sub-tile pointer read as unreadable on every
                // tile, which means either it is null or it is not at offset 0 - and guessing between
                // those is how offsets get fitted to noise. The tgt path and the 0x38 stride are
                // already confirmed right by the names coming back, so printing the record settles
                // where the pointer actually is.
                b.Append("   (" + x + "," + y + ") raw ");

                foreach (var one in raw)
                    b.Append(one.ToString("X2"));

                b.AppendLine("  subptr " + subs.ToString("X"));

                // **A vector at +0x10 that GameHelper2 never mapped.** Its length varies tile to tile
                // across neighbours that are otherwise identical - 0, 16, 32, 48, 112, 160 bytes, all
                // multiples of sixteen. Sixteen bytes is the size of a position with flags, and a list
                // of positioned features inside a tile is the one thing that could separate two cells
                // that share a tile, which is exactly the case the game keeps refusing.
                var vFirst = BitConverter.ToInt64(raw, 0x10);
                var vLast = BitConverter.ToInt64(raw, 0x18);

                if (vFirst > Offsets.Lowest && vLast > vFirst && vLast - vFirst <= Most)
                {
                    var many = vLast - vFirst;

                    b.AppendLine("      +0x10 vector: " + many + " bytes = " + many / Wide +
                                 " x 16, contents:");

                    var body = Safe.Read(() => gc.Memory.ReadBytes(vFirst, (int)many), null);

                    for (var i = 0; body != null && i + Wide <= body.Length; i += Wide)
                    {
                        b.Append("        [" + (i / Wide) + "] ");

                        for (var j = 0; j < Wide; j++)
                            b.Append(body[i + j].ToString("X2"));

                        // The same bytes read as the things a per-tile record plausibly holds.
                        b.AppendLine("   f32 " +
                                     BitConverter.ToSingle(body, i).ToString("0.###") + " " +
                                     BitConverter.ToSingle(body, i + 4).ToString("0.###") + " " +
                                     BitConverter.ToSingle(body, i + 8).ToString("0.###") +
                                     "   i32 " + BitConverter.ToInt32(body, i) + " " +
                                     BitConverter.ToInt32(body, i + 4) + " " +
                                     BitConverter.ToInt32(body, i + 8) + " " +
                                     BitConverter.ToInt32(body, i + 12));
                    }
                }

                // And whatever the shared pointer at +0x00 actually points at, verbatim.
                b.AppendLine("      +0x00 points at: " + Bytes(gc, subs, Preview));

                b.AppendLine("   (" + x + "," + y + ")".PadRight(4) +
                             (bytes < 0 ? "  unreadable" : bytes.ToString("N0").PadLeft(14)) +
                             (bytes > 0 ? (bytes / (double)(Tile * Tile)).ToString("0.00").PadLeft(11)
                                        : "".PadLeft(11)) +
                             height.ToString().PadLeft(9) + "  " + idX + "/" + idY +
                             rot.ToString().PadLeft(6) + "  " + Text(gc, tgt + Vtable));
            }
        }

        return b.ToString();
    }

    /// <summary>
    /// The game's own coarse routing grid, read straight out of memory.
    ///
    /// **This is the placement rule, not a model of it.** Explosive placement is decided in
    /// `0x141F5A6B0`, which pathfinds from the last explosive to the requested point on a grid at
    /// HALF-TILE resolution and clamps the result to the reach. Both endpoints are converted with
    ///
    /// <code>coarse = (2 * (p - origin * 23 + 5)) / 23</code>
    ///
    /// - the 0xB21642C9 / sar 4 magic in the disassembly is a signed divide by 23 - which is the
    /// `((p + 5) * 2) / 23` block formula that was reconstructed statistically from 2,203 observed
    /// verdicts long before the routine was found. The code and the measurements agree exactly.
    ///
    /// The object hangs off `ClientExpedition + 0x1A8`, then `+ 0x38`, and its layout is fixed by the
    /// two places the routine indexes it:
    ///
    /// <code>
    /// 0x00  u16  width          bounds-checked against the coarse x
    /// 0x02  u16  height         bounds-checked against the coarse y
    /// 0x04  u16  origin tile x  multiplied by 23 in the conversion
    /// 0x06  u16  origin tile y
    /// 0x08  ptr  bytes          width * height, indexed [y * width + x]
    /// </code>
    ///
    /// **Non-zero means NAVIGABLE, not blocked.** `0x141F5ADB2` reads the byte and `jne`s PAST the
    /// endpoint refinement `0x141F5BF80` when it is set. Measured the same way round: over 1,963
    /// distinct cells with a corroborated verdict at one dig site, a zero byte was refused 929
    /// times and accepted **zero** times, while a non-zero byte was accepted 890 times. `byte == 0`
    /// is exactly necessary.
    ///
    /// **That holds for the point ASKED FOR, and not for where the explosive ends up.** A clamped
    /// landing is not validated against this array: on a Stronghold site the game put explosives on
    /// zero-byte cells 58 times out of 279 confirmed clamps. Those cells are worth avoiding for a
    /// different reason - placement routes from the request back to the last explosive, so a bomb
    /// in one leaves no route from anywhere and the site refuses every later placement.
    ///
    /// That also explains why so little of the array is set - about 6% on a typical map. It is not a
    /// sparse scattering of walls, it is the dig site's routable region drawn on a whole-map grid.
    ///
    /// That single array is what `Refused` has been inferring from red indicators three observations
    /// at a time; here it is as ground truth, for the whole map, before a single bomb is placed.
    ///
    /// Read-only, like everything else in this file.
    /// </summary>
    public static Slab Coarse(GameController gc)
    {
        var seat = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);

        if (seat <= Offsets.Lowest)
            return null;

        var holder = Hop(gc, seat + Offsets.ExpeditionRouting);

        if (holder <= Offsets.Lowest)
            return null;

        var at = Hop(gc, holder + Offsets.RoutingGrid);

        if (at <= Offsets.Lowest)
            return null;

        var head = Safe.Read(() => gc.Memory.ReadBytes(at, 16), null);

        if (head == null || head.Length < 16)
            return null;

        var wide = BitConverter.ToUInt16(head, 0);
        var high = BitConverter.ToUInt16(head, 2);
        var fromX = BitConverter.ToUInt16(head, 4);
        var fromY = BitConverter.ToUInt16(head, 6);
        var body = BitConverter.ToInt64(head, 8);

        // A misread pointer would otherwise ask for a gigabyte. The largest sane dig site is a few
        // hundred coarse cells on a side, so anything past this is a bad read rather than a big map.
        if (wide <= 0 || high <= 0 || (long)wide * high > Ceiling || body <= Offsets.Lowest)
            return null;

        var bytes = Safe.Read(() => gc.Memory.ReadBytes(body, wide * high), null);

        return bytes == null || bytes.Length < wide * high
            ? null
            : new Slab(wide, high, fromX, fromY, bytes);
    }

    /// <summary>
    /// The coarse routing grid, with the conversion the game itself uses.
    ///
    /// Kept as an object rather than read per query because the whole array is a few tens of kilobytes
    /// and the planner asks about thousands of points per search.
    /// </summary>
    public sealed class Slab
    {
        internal Slab(int wide, int high, int fromX, int fromY, byte[] bytes)
        {
            Wider = wide;
            Higher = high;
            FromX = fromX;
            FromY = fromY;
            Bytes = bytes;
        }

        /// <summary>How many coarse cells across, from the grid object's own header.</summary>
        public int Wider { get; }

        /// <summary>How many coarse cells down.</summary>
        public int Higher { get; }

        /// <summary>The grid's origin, in TILES. The conversion multiplies it by 23.</summary>
        public int FromX { get; }

        /// <summary>The grid's origin, in tiles, down the map.</summary>
        public int FromY { get; }

        /// <summary>One byte per coarse cell, row major. Non-zero is NAVIGABLE; zero is not.</summary>
        public byte[] Bytes { get; }

        /// <summary>How many cells the wire may route through, for the dump to report.</summary>
        public int RoutableCells
        {
            get
            {
                var many = 0;

                foreach (var one in Bytes)
                    if (one != 0)
                        many++;

                return many;
            }
        }

        /// <summary>
        /// A grid point as a coarse cell, exactly as `0x141F5A6B0` computes it.
        ///
        /// The divide is C#'s truncating integer divide, which is what the compiler's 0xB21642C9
        /// sequence implements - it rounds toward zero, and the sign fix-up in the disassembly
        /// (`shr 0x1f` then `add`) is precisely that.
        /// </summary>
        public (int X, int Y) Cell(int x, int y) =>
            (2 * (x - FromX * Span + Lift) / Span, 2 * (y - FromY * Span + Lift) / Span);

        /// <summary>
        /// Whether the wire may route through this grid point - a non-zero byte, on the grid.
        ///
        /// Off the grid is NOT routable, because the routine bails out with validity 0 on exactly
        /// that bounds check. Measured exactly necessary: 929 refusals and no accepts on zero bytes.
        /// </summary>
        public bool Routable(int x, int y)
        {
            var (cx, cy) = Cell(x, y);

            return cx >= 0 && cy >= 0 && cx < Wider && cy < Higher &&
                   Bytes[cy * Wider + cx] != 0;
        }

        /// <summary>How many grid units one tile spans. PoeMapExtension.TileToGridConversion.</summary>
        private const int Span = 23;

        /// <summary>The +5 in the conversion, which is what puts the boundaries where they land.</summary>
        private const int Lift = 5;
    }

    /// <summary>
    /// The engine's own finished A* search, read out of the node array it leaves behind.
    ///
    /// **This is the game's answer, not a reconstruction of it.** `0x141D028B0` keeps one entry per
    /// coarse cell in a vector at `grid + 0x38`, indexed `y * width + x`, eight bytes each:
    ///
    /// <code>
    /// +0  float  g, the cost the search reached this cell at   (0x141D02E00 writes it)
    /// +4  byte   the direction it came FROM, as a step index   (0x141D02DFB)
    /// +5  byte   on the open list                              (0x141D02D05)
    /// +6  byte   closed                                        (0x141D02BEB)
    /// </code>
    ///
    /// The came-from byte is the REVERSE of the step taken, via the table at `0x141F5ADE2`
    /// (`02 03 00 01 06 07 04 05`), so following it walks back towards the start.
    ///
    /// **It holds exactly one search - the last one.** The array is wiped at `0x141D0299B` on every
    /// call, and the heuristic is goal-directed, so this is the path to wherever the cursor last
    /// was and cannot be reused for anywhere else. That makes it a diagnostic, not an oracle: it is
    /// how you find out why our router and the game's disagree about a particular pair, by reading
    /// the engine's own g values instead of inferring them from where a bomb landed.
    /// </summary>
    public static string Searched(GameController gc, Func<int, int, float?> ours = null)
    {
        var slab = Coarse(gc);

        if (slab == null)
            return "  no routing grid - no dig site, or the offsets have moved";

        var seat = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);
        var holder = Hop(gc, seat + Offsets.ExpeditionRouting);
        var at = Hop(gc, holder + Offsets.RoutingGrid);
        var nodes = Hop(gc, at + Offsets.RoutingNodes);

        if (nodes <= Offsets.Lowest)
            return "  the search has not run, or the node array is not where it was";

        var b = new StringBuilder();
        var aim = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacementIndicatorGridPosition ?? default, default);

        var (cx, cy) = slab.Cell(aim.X, aim.Y);

        b.AppendLine("  last search, around the indicator at grid " + aim.X + "," + aim.Y +
                     " = coarse " + cx + "," + cy);
        b.AppendLine("  g = the engine's own cost; from = the step it arrived by; . = untouched");

        for (var y = cy - Window2; y <= cy + Window2; y++)
        {
            if (y < 0 || y >= slab.Higher)
                continue;

            var line = new StringBuilder();

            line.Append("   ").Append(y.ToString().PadLeft(4)).Append(' ');

            for (var x = cx - Window2; x <= cx + Window2; x++)
            {
                if (x < 0 || x >= slab.Wider)
                {
                    line.Append("        ");
                    continue;
                }

                var raw = Safe.Read(() => gc.Memory.ReadBytes(nodes + (long)(y * slab.Wider + x) * Entry8, Entry8), null);

                if (raw == null || raw.Length < Entry8)
                {
                    line.Append("   ?    ");
                    continue;
                }

                var cost = BitConverter.ToSingle(raw, 0);
                var came = raw[4];
                var open = raw[5];
                var shut = raw[6];

                // The came-from byte is the REVERSE step, per the table at `0x141F5ADE2`, so
                // printing the direction it points back along reconstructs the engine's own path:
                // start at the goal cell and follow the arrows home.
                line.Append(open == 0 && shut == 0
                    ? "    .    "
                    : $" {cost,5:0.0}{Arrow(came)}{(shut != 0 ? "*" : " ")}");
            }

            b.AppendLine(line.ToString());
        }

        b.AppendLine("  (* = closed; the arrow points back the way the search came)");

        if (ours != null)
            Diverges(gc, slab, nodes, ours, b);

        return b.ToString();
    }

    /// <summary>
    /// Where our A* stops being the engine's, read off the engine's own g values.
    ///
    /// **The first divergence is the whole answer.** A* costs only ever grow along a route, so the
    /// touched cell with the lowest g where the two disagree is the earliest point the searches
    /// parted - everything after it is downstream of that one difference. Sorting by the engine's g
    /// and printing the head of the list therefore names the cause rather than the symptoms, which a
    /// count of mismatched landings never can.
    ///
    /// A cell the engine touched and we did not is a difference too, and the more telling kind: it
    /// means the connectivity mask or the wall test let one of us through a step the other refused.
    /// </summary>
    private static void Diverges(GameController gc, Slab slab, long nodes,
        Func<int, int, float?> ours, StringBuilder b)
    {
        var cells = slab.Wider * slab.Higher;
        var raw = Safe.Read(() => gc.Memory.ReadBytes(nodes, cells * Entry8), null);

        if (raw == null || raw.Length < cells * Entry8)
        {
            b.AppendLine("  could not read the whole node array to compare against");
            return;
        }

        var apart = new List<(float Theirs, float? Ours, int X, int Y)>();
        var touched = 0;
        var agreed = 0;
        var missing = 0;

        // **Where each search began.** The cell reached at zero cost is the one the search started
        // from: the engine's, and ours.
        //
        // **A difference here is NOT a conversion bug**, and reading it as one cost a detour. The
        // conversion is settled from the binary - `0x141F5A84E` computes
        // `(2 * (p - (originTile * 23 - 5))) / 23`, which is Slab.Cell exactly - so when the two
        // zeros sit on different cells it is because the engine was asked about a different POINT,
        // not because it converted the same point differently. The requested point is not always
        // the cursor: it is refused outright when it carries `expedition_no_placement`, and the
        // indicator reports the landing rather than the request. Establish which point the engine
        // was given before reading anything into the cells.
        var theirZero = (X: -1, Y: -1);
        var ourZero = (X: -1, Y: -1);

        for (var y = 0; y < slab.Higher; y++)
        {
            for (var x = 0; x < slab.Wider; x++)
            {
                var at = (y * slab.Wider + x) * Entry8;

                if (raw[at + 5] == 0 && raw[at + 6] == 0)
                    continue;

                touched++;

                var theirs = BitConverter.ToSingle(raw, at);
                var mine = ours(x, y);

                if (theirs == 0f)
                    theirZero = (x, y);

                if (mine == 0f)
                    ourZero = (x, y);

                if (mine == null)
                    missing++;
                else if (MathF.Abs(mine.Value - theirs) <= Slack)
                {
                    agreed++;
                    continue;
                }

                apart.Add((theirs, mine, x, y));
            }
        }

        b.AppendLine();
        b.AppendLine($"  the aim's coarse cell: engine {theirZero.X},{theirZero.Y}   " +
                     $"ours {ourZero.X},{ourZero.Y}   " +
                     (theirZero == ourZero
                         ? "AGREE"
                         : "DIFFER - the engine was asked about another point, not another cell"));
        b.AppendLine($"  the engine touched {touched} coarse cells; we agreed on {agreed}, " +
                     $"differed on {apart.Count - missing}, and never reached {missing}");

        if (apart.Count == 0)
        {
            b.AppendLine("  our search IS the engine's, cell for cell.");
            return;
        }

        apart.Sort(static (l, r) => l.Theirs.CompareTo(r.Theirs));

        b.AppendLine("  earliest divergences, by the engine's own g (the first one is the cause):");

        for (var i = 0; i < apart.Count && i < Worst; i++)
        {
            var (theirs, mine, x, y) = apart[i];

            b.AppendLine($"    coarse {x,4},{y,4}  engine g {theirs,8:0.000}  ours " +
                         (mine == null ? "never reached" : $"{mine.Value,8:0.000}") +
                         $"   byte {slab.Bytes[y * slab.Wider + x],3}");
        }
    }

    /// <summary>Float slack when comparing our g against the engine's. Both are single precision.</summary>
    private const float Slack = 0.001f;

    /// <summary>How many divergences to print. The first is the cause; the rest are context.</summary>
    private const int Worst = 12;

    /// <summary>
    /// The came-from byte as an arrow pointing back towards the start of the search.
    ///
    /// The engine stores the REVERSE of the step it took - the table at `0x141F5ADE2` is
    /// `02 03 00 01 06 07 04 05`, which pairs 0 with 2, 1 with 3, 4 with 6 and 5 with 7 - so the
    /// stored index already points home and needs no flipping here. Index order is the jump table
    /// at `0x141D02FC0`: four orthogonals, then four diagonals.
    ///
    /// Diagonals are lettered rather than drawn: J points back north-east, 7 south-east, F
    /// south-west, L north-west.
    /// </summary>
    private static char Arrow(byte came) => came switch
    {
        0 => '^', 1 => '>', 2 => 'v', 3 => '<',
        4 => 'J', 5 => '7', 6 => 'F', 7 => 'L',
        _ => '?',
    };


    /// <summary>How many bytes one search record is - the engine indexes it by eight.</summary>
    private const int Entry8 = 8;

    /// <summary>How many coarse cells either side of the indicator to report.</summary>
    private const int Window2 = 6;

    /// <summary>
    /// The four terrain layers as the ENGINE reads them, beside what ExileCore2 hands us.
    ///
    /// **Because everything else here is inference from ExileCore2's arrays.** `0x141D0D220` does
    /// not read an array; it reads raw bytes at a fixed offset and unpacks them:
    ///
    /// <code>
    /// buf = [terrain + 0xD0 + layer * 0x18]
    /// off = y * BytesPerRow[terrain + 0x130] + x / 2      (signed shift, so x/2 rounds toward zero)
    /// b   = buf[off]
    /// v   = (x &amp; 1) ? b >> 4 : b &amp; 0xF                    (two cells per byte)
    /// v   = v == 5 ? 255 : v
    /// </code>
    ///
    /// So this decodes the same bytes the same way and prints them next to ExileCore2's own values
    /// for the same cells. If they disagree, every conclusion drawn from the published arrays is
    /// suspect - and that is exactly the open question about the line test in NOTES 1l, where the
    /// engine blocks lines that read clear on all four published grids at every clearance.
    ///
    /// Which array ExileCore2 puts at which offset has already been got wrong once this session,
    /// from another project's field names. This settles it by reading rather than by naming.
    /// </summary>
    public static string Layered(GameController gc)
    {
        var seat = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);

        if (seat <= Offsets.Lowest)
            return "  no dig site";

        var owner = Hop(gc, seat + Offsets.ExpeditionOwner);
        var session = Hop(gc, owner + Offsets.OwnerSession);
        var terrain = session + Offsets.SessionTerrain;

        if (session <= Offsets.Lowest)
            return "  could not walk ClientExpedition -> owner -> session";

        var head = Safe.Read(() => gc.Memory.ReadBytes(terrain + Offsets.BytesPerRow, 4), null);
        var wide = head == null || head.Length < 4 ? 0 : BitConverter.ToInt32(head, 0);

        if (wide <= 0 || wide > 1 << 20)
            return "  BytesPerRow at +0x130 reads " + wide + " - not a terrain struct";

        var b = new StringBuilder();

        b.AppendLine("  terrain struct at " + terrain.ToString("X") + ", BytesPerRow " + wide);

        var at = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacementIndicatorGridPosition ?? default, default);

        b.AppendLine("  comparing a " + (Span2 * 2 + 1) + " square around the indicator at grid " +
                     at.X + "," + at.Y);
        b.AppendLine();

        var published = new[]
        {
            ("RawPathfindingData", Safe.Read<int[][]>(() => gc.IngameState.Data.RawPathfindingData, null)),
            ("RawTerrainTargetingData", Safe.Read<int[][]>(() => gc.IngameState.Data.RawTerrainTargetingData, null)),
        };

        b.AppendLine("  layer  offset   cells that DIFFER from each published array (0 means identical)");

        for (var layer = 0; layer < Layers; layer++)
        {
            var buf = Hop(gc, terrain + Offsets.Layers + layer * Offsets.LayerStride);

            if (buf <= Offsets.Lowest)
            {
                b.AppendLine("  " + layer + "      +0x" + (Offsets.Layers + layer * Offsets.LayerStride).ToString("X3") +
                             "    (empty)");

                continue;
            }

            var line = new StringBuilder();
            var mine = new System.Collections.Generic.Dictionary<(int, int), int>();

            for (var y = at.Y - Span2; y <= at.Y + Span2; y++)
            {
                var row = Safe.Read(() => gc.Memory.ReadBytes(
                    buf + (long)y * wide + (at.X - Span2) / 2, Span2 + 2), null);

                if (row == null)
                    continue;

                for (var x = at.X - Span2; x <= at.X + Span2; x++)
                {
                    var i = x / 2 - (at.X - Span2) / 2;

                    if (i >= 0 && i < row.Length)
                        mine[(x, y)] = Cell(row[i], x);
                }
            }

            foreach (var (name, grid) in published)
            {
                var off = 0;
                var same = 0;

                foreach (var ((x, y), v) in mine)
                {
                    if (grid == null || y < 0 || y >= grid.Length || grid[y] == null ||
                        x < 0 || x >= grid[y].Length)
                        continue;

                    // ExileCore2 hands back 0..5; the engine maps 5 to 255. Compare like for like.
                    if ((v == 255 ? 5 : v) == grid[y][x])
                        same++;
                    else
                        off++;
                }

                line.Append("   ").Append(name).Append(' ').Append(off).Append('/')
                    .Append(off + same);
            }

            b.AppendLine("  " + layer + "      +0x" + (Offsets.Layers + layer * Offsets.LayerStride).ToString("X3") +
                         "  " + line);
        }

        b.AppendLine();
        b.AppendLine("  a layer identical to a published array IS that array; a layer matching none");
        b.AppendLine("  is a source this plugin has never read, and the line test may be using it");

        return b.ToString();
    }

    /// <summary>One cell out of a packed byte, the way `0x141D0D220` unpacks it.</summary>
    private static int Cell(byte packed, int x)
    {
        var v = (x & 1) != 0 ? packed >> 4 : packed & 0xF;

        return v == 5 ? 255 : v;
    }

    /// <summary>How many layers `0x141D0D220` will index - it refuses anything above four.</summary>
    private const int Layers = 4;



    /// <summary>How many cells either side of the indicator to print.</summary>
    private const int Span2 = 10;

    /// <summary>The pointer at this address, or 0 when it does not read.</summary>
    private static long Hop(GameController gc, long at)
    {
        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, 8), null);

        return raw == null || raw.Length < 8 ? 0L : BitConverter.ToInt64(raw, 0);
    }



    /// <summary>The most coarse cells a sane grid has, so a bad pointer cannot ask for a gigabyte.</summary>
    private const long Ceiling = 1 << 22;

    /// <summary>A std::wstring at this address, as text. See NativeUtf16Text for the layout.</summary>
    private static string Text(GameController gc, long at)
    {
        if (at <= Offsets.Lowest)
            return "";

        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, 32), null);

        if (raw == null || raw.Length < 32)
            return "";

        var length = BitConverter.ToInt64(raw, 0x10);
        var room = BitConverter.ToInt64(raw, 0x18);

        if (length <= 0 || length > 512)
            return "";

        // Short strings live in the sixteen bytes the pointer would occupy; longer ones on the heap.
        var from = room > 7 ? BitConverter.ToInt64(raw, 0) : at;
        var text = Safe.Read(() => gc.Memory.ReadStringU(from, (int)length * 2), null);

        if (string.IsNullOrEmpty(text))
            return "";

        var slash = text.LastIndexOf('/');

        return slash >= 0 ? text[(slash + 1)..] : text;
    }


    /// <summary>What GameHelper2 calls the field at this offset, or "" when nobody has named it.</summary>
    private static string Named(long inside) => inside switch
    {
        0x28 => "TileDetailsPtr",
        // **Identified here, and named nowhere else.** 41,736 bytes over a 2140 x 3381 area is
        // exactly 13,912 x 3, and 13,912 is 94 x 148 - the tile count plus one in each axis. So it is
        // three bytes per tile CORNER, a corner-sampled lattice, which is why GameHelper2's
        // TotalTilesPlusOne sits commented out at 0x40 immediately before it. Its bytes repeat
        // 04 00 0B across the whole buffer. Not a placement mask and not per cell.
        0x50 => "per tile CORNER, 3 bytes each",
        0xD0 => "GridWalkableData  = RawPathfindingData",
        0xE8 => "GridLandscapeData = RawTerrainTargetingData",
        0x100 => "(frame copy of walkable)",
        0x118 => "(frame copy of landscape)",
        _ => "",
    };

    /// <summary>How a length divides by the things that shape this area: cells, rows, tiles.</summary>
    private static string Shape(long bytes, int rows, int cols, long tiles)
    {
        var note = Guess(bytes, rows, cols);

        if (tiles > 0 && bytes % tiles == 0)
            note += "  [" + bytes / tiles + " per tile, " + tiles + " tiles]";

        if (rows > 0 && cols > 0)
        {
            var bits = (long)rows * ((cols + 7) / 8);

            if (bytes == bits)
                note += "  <-- EXACTLY one bit per cell";
        }

        return note;
    }

    /// <summary>The first few bytes of a buffer, as hex, to say what kind of thing it holds.</summary>
    private static string Bytes(GameController gc, long at, int many)
    {
        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, many), null);

        if (raw == null || raw.Length == 0)
            return "unreadable";

        var b = new StringBuilder();

        foreach (var one in raw)
            b.Append(one.ToString("X2")).Append(' ');

        return b.ToString();
    }

    /// <summary>Whether this length looks shaped by the grid, and how. See Describe.</summary>
    private static string Guess(long bytes, int rows, int cols)
    {
        if (rows <= 0 || bytes <= 0 || bytes % rows != 0)
            return "";

        var perRow = bytes / rows;

        if (perRow >= (cols + 1) / 2 && perRow <= cols)
            return "  <-- " + perRow + " per row, a nibble-packed grid";

        if (perRow * 8 >= cols && perRow * 8 <= cols * 2)
            return "  <-- " + perRow + " per row, ONE BIT per cell";

        return "  <-- divides by rows: " + perRow + " per row";
    }

    /// <summary>Every offset in the window whose three qwords read as a plausible std::vector.</summary>
    private static List<(int At, long First, long Bytes)> Vectors(GameController gc, long seat, int size,
        long least = Least)
    {
        var found = new List<(int, long, long)>();
        var raw = Safe.Read(() => gc.Memory.ReadBytes(seat, size), null);

        if (raw == null || raw.Length < 24)
            return found;

        for (var at = 0; at + 24 <= raw.Length; at += 8)
        {
            var first = BitConverter.ToInt64(raw, at);
            var last = BitConverter.ToInt64(raw, at + 8);
            var end = BitConverter.ToInt64(raw, at + 16);

            if (Sane(first, last, end) && last - first >= least)
                found.Add((at, first, last - first));
        }

        return found;
    }

    /// <summary>Whether three qwords hold together as a vector: in range, ordered, aligned.</summary>
    private static bool Sane(long first, long last, long end) =>
        first > Offsets.Lowest && first < Highest &&
        last >= first && end >= last && end < Highest &&
        (first & 7) == 0 && end - first < Biggest;

    private static long Qword(GameController gc, long at)
    {
        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, 8), null);

        return raw == null || raw.Length < 8 ? 0L : BitConverter.ToInt64(raw, 0);
    }

    /// <summary>How big TerrainStruct is, per GameHelper2, so the listing stops at its end.</summary>
    private const int Size = 0x240;

    /// <summary>How many grid units one tile spans, for the per-tile arithmetic.</summary>
    private const int Tile = 23;

    /// <summary>How many bytes of an unnamed buffer to show, to judge what it holds.</summary>
    private const int Preview = 32;




    /// <summary>Past the vtable of a TgtFileStruct, where its path string begins.</summary>
    private const int Vtable = 0x8;

    /// <summary>How many tiles either side of the indicator to report.</summary>
    private const int Around = 1;

    /// <summary>How wide one element of the +0x10 vector is. Every length seen divides by this.</summary>
    private const int Wide = 16;

    /// <summary>The most of that vector to read, so a misread length cannot ask for megabytes.</summary>
    private const long Most = 4096;


    /// <summary>How much of IngameData to sweep. The terrain struct sits well inside this.</summary>
    private const int Window = 0x1200;

    /// <summary>The smallest buffer worth reporting, so the listing is not all tiny vectors.</summary>
    private const long Least = 4096;

    private const long Highest = 0x7FFFFFFFFFFF;
    private const long Biggest = 0x40000000;
}
