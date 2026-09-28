using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The authored rectangles a dig site marks as `expedition_no_placement`, read from the tile data.
///
/// **This is a term of the placement rule, not a heuristic.** `0x141F5A6B0` - the routine the whole
/// wire model comes from - tests it twice, on the requested point at `0x141F5A7ED` and on the far
/// endpoint at `0x141F5AE68`, and a hit is a flat refusal:
///
/// <code>
/// 0x141F5A7F6   build a one-element polyline holding the requested point
/// 0x141F5A83E   mov byte ptr [r12 + 0x18], 0      &lt;- validity = false
/// 0x141F5A849   jmp to the exit
/// </code>
///
/// Not a clamp and not a relocation: the wire collapses to a point and the answer is no. That is the
/// same shape as the path taken when there is no routing grid at all, which is the strongest
/// statement the routine makes about anything.
///
/// **It is why ground that every published grid calls open still refuses.** Five terrain layers, the
/// height map, tile names, sub-tile data, doodads, markers and the entity list all read identically
/// across such a boundary, because none of them is where the answer lives - the answer is an object
/// inside the tile, and this is the only thing that reads it.
///
/// The tag is `expedition_no_placement`, 23 characters, hashed by the client's own FNV-1a over utf-16
/// and folded to 16 bits at `0x141F5A736`. That is 0x970C, which is both what the arithmetic gives
/// and what the global at rva 0x047159B4 holds at runtime.
///
/// **Read-only, like everything else here.** Nothing in this file writes to the game.
/// </summary>
internal sealed class Volumes
{
    /// <summary>
    /// The type descriptor key that actually resolves, when the one baked into Offsets no longer does.
    ///
    /// **A game patch moves these and the symptom is silent.** After the 28 September update the descriptor
    /// at Offsets.VolumeDescriptor resolved to no slot at all - measured, six instances with every lookup
    /// returning -1, where the day before three instances resolved and one carried the tag. Every authored
    /// no-placement rectangle then reads as ordinary ground, which is indistinguishable from a site that has
    /// none, so the planner routes onto ground the game refuses and nothing on the page says why.
    ///
    /// Nought until a repair has found something. See Repair and DebugSettings.RepairOffsets.
    /// </summary>
    internal static long Repaired { get; private set; }

    /// <summary>What the last read made of its offsets, for the dump and the overlay to say out loud.</summary>
    internal static string Said { get; private set; } = "not read yet";

    /// <summary>
    /// Whether the last read resolved no component at all, which is a broken offset rather than a site with no
    /// volumes. The two look identical from the outside, which is why this is recorded separately.
    /// </summary>
    internal static bool Broken { get; private set; }

    /// <summary>One forbidden rectangle, in grid units, half-open on its far edges.</summary>
    internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        /// <summary>`0x141DE9D00`: inside is `min &lt;= p &lt; max` on both axes.</summary>
        public bool Holds(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        public int Wide => Right - Left;

        public int High => Bottom - Top;
    }

    private readonly List<Box> _boxes = new();

    /// <summary>How many forbidden rectangles this site carries.</summary>
    public int Count => _boxes.Count;

    /// <summary>Every rectangle, for the dump and the overlay.</summary>
    public IReadOnlyList<Box> All => _boxes;

    /// <summary>
    /// Whether a grid point is inside a forbidden volume, which is the game refusing it outright.
    /// </summary>
    public bool Forbids(int x, int y)
    {
        for (var i = 0; i < _boxes.Count; i++)
            if (_boxes[i].Holds(x, y))
                return true;

        return false;
    }

    /// <summary>
    /// Sweeps the tiles the site could route through and collects every tagged rectangle.
    ///
    /// **Whole tiles, once, rather than a lookup per point.** `0x141E7E500` resolves and tests the
    /// objects of one tile per call, which is right for a routine asking about a handful of points
    /// and hopeless for an overlay asking about tens of thousands. The rectangles do not move, so
    /// they are read once and then answered from memory.
    /// </summary>
    /// <summary>
    /// The rectangles for this site, read once and kept.
    ///
    /// **Because the walk is thousands of remote reads and the answer never changes.** The tiles are
    /// authored, so the volumes in them are fixed for the life of the area - while Terrain.Read has
    /// eight callers and a solve rebuilds a snapshot several times over, which turned a one-off cost
    /// into several seconds at the front of every improvement window.
    /// </summary>
    public static Volumes For(GameController gc, Vector2 around, float span)
    {
        var mark = (Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u),
            (int)MathF.Round(around.X), (int)MathF.Round(around.Y));

        if (_readFor == mark && _read != null)
            return _read;

        _read = Read(gc, around, span);
        _readFor = mark;

        return _read;
    }

    private static Volumes _read;
    private static (uint Area, int X, int Y) _readFor;

    public static Volumes Read(GameController gc, Vector2 around, float span)
    {
        var found = new Volumes();

        var seat = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);

        if (seat <= Lowest)
            return found;

        var owner = Hop(gc, seat + Offsets.ExpeditionOwner);
        var session = Hop(gc, owner + Offsets.OwnerSession);
        var terrain = session + Offsets.SessionTerrain;

        if (session <= Lowest)
            return found;

        var wide = (int)Qword(gc, terrain + Offsets.NumTileIndexCols);
        var high = (int)Qword(gc, terrain + Offsets.NumTileIndexRows);
        var tiles = Hop(gc, terrain + Offsets.TileArray);

        if (wide <= 0 || high <= 0 || wide > Sane || high > Sane || tiles <= Lowest)
            return found;

        // The routine indexes `(y / 23) * wide + (x / 23)`, so the tiles to read are the ones the
        // interesting grid square covers, clamped to the map.
        var lx = Math.Max(0, (int)(around.X - span) / Tile);
        var rx = Math.Min(wide - 1, (int)(around.X + span) / Tile);
        var ty = Math.Max(0, (int)(around.Y - span) / Tile);
        var by = Math.Min(high - 1, (int)(around.Y + span) / Tile);

        var key = Key(gc);
        var seen = new HashSet<long>();
        var index = new Dictionary<long, int>();

        // **Counted because the two failures look identical from outside.** A site with no forbidden
        // rectangles and a site whose descriptor offset has moved both return an empty list. Instances found
        // with nothing resolved is the second one, and only the counts can say so.
        var instances = 0;
        var resolved = 0;

        for (var y = ty; y <= by; y++)
        {
            for (var x = lx; x <= rx; x++)
            {
                var record = tiles + ((long)y * wide + x) * Offsets.TileBytes;
                var at = Hop(gc, record + Offsets.TileObjectsFirst);
                var end = Hop(gc, record + Offsets.TileObjectsLast);

                if (at <= Lowest || end <= at || end - at > (long)Most * Offsets.ObjectEntryBytes)
                    continue;

                // **The whole vector in one read.** Every entry needs its pointer and its kind
                // byte, which are eight bytes apart inside a sixteen byte entry - so fetching them
                // separately was two remote reads per entry and thousands per site. One read of the
                // vector answers both for all of them.
                var raw = Safe.Read(() => gc.Memory.ReadBytes(at, (int)(end - at)), null);

                if (raw == null || raw.Length < end - at)
                    continue;

                for (var e = 0; e + Offsets.ObjectEntryBytes <= raw.Length;
                     e += Offsets.ObjectEntryBytes)
                {
                    var thing = BitConverter.ToInt64(raw, e);

                    if (thing <= Lowest)
                        continue;

                    // `0x141E7E5CC`: the binary search skips every entry whose byte at +8 has its
                    // low seven bits under four, so only the rest are candidates. Filtering on the
                    // byte directly is the same set without depending on the sortedness the search
                    // relies on.
                    if ((raw[e + Offsets.ObjectEntryKind] & 0x7F) < Volume)
                        continue;

                    var inst = Hop(gc, thing + Offsets.Inner);

                    if (inst <= Lowest || !seen.Add(inst))
                        continue;

                    instances++;

                    var part = Resolve(gc, inst, index, key);

                    if (part > Lowest)
                        resolved++;

                    if (part <= Lowest || Word(gc, part + Offsets.VolumeTag) != Offsets.NoPlacementTag)
                        continue;

                    var box = Rect(gc, part);

                    if (box.Wide > 0 && box.High > 0)
                        found._boxes.Add(box);
                }
            }
        }

        Broken = instances > 0 && resolved == 0;

        Said = Broken
            ? $"BROKEN: {instances} volume instance(s) found and none resolved a component, so the type " +
              "descriptor offset no longer matches the client. Every authored no-placement rectangle is " +
              "being read as ordinary ground."
            : $"{instances} instance(s), {resolved} resolved, {found._boxes.Count} forbidden rectangle(s)" +
              (Repaired != 0 ? $", using a repaired descriptor at {Repaired:X}" : "");

        return found;
    }

    /// <summary>
    /// `0x141DE9D00`: the rectangle is the component's size centred on its owner's position.
    ///
    /// The halving truncates toward zero and the two halves are not equal - the near edge takes
    /// `size / 2` and the far edge takes the remainder - so an odd size sits one unit past centre.
    /// Written the same way here rather than as a symmetric half-extent, because a rectangle placed
    /// half a unit differently is a different answer on the boundary, and the boundary is the whole
    /// question.
    /// </summary>
    private static Box Rect(GameController gc, long part)
    {
        var w = Int(gc, part + Offsets.VolumeWide);
        var h = Int(gc, part + Offsets.VolumeHigh);
        var owner = Hop(gc, part + Offsets.Inner);
        var placed = owner <= Lowest ? 0 : Hop(gc, owner + Offsets.OwnerPlaced);

        if (placed <= Lowest)
            return default;

        var ox = Int(gc, placed + Offsets.PlacedX);
        var oy = Int(gc, placed + Offsets.PlacedY);
        var halfW = w / 2;
        var halfH = h / 2;

        return new Box(ox - halfW, oy - halfH, ox + (w - halfW), oy + (h - halfH));
    }

    /// <summary>
    /// `0x1417306F0`: the component of a fixed static type, found through the type's own table.
    ///
    /// **The index is per TYPE, so it is looked up once and kept.** That is also why reading `+0x40`
    /// across every slot of every object could only ever produce noise - the tag lives on one
    /// component type and every other contributes whatever it happens to hold. The map decides which
    /// slot to read, and without it there is nothing to filter the noise with.
    /// </summary>
    private static long Resolve(GameController gc, long inst, Dictionary<long, int> index, long key)
    {
        var type = Hop(gc, inst + Offsets.Inner);

        if (type <= Lowest)
            return 0;

        if (!index.TryGetValue(type, out var slot))
        {
            slot = Slot(gc, Hop(gc, type + Offsets.TypeRegistry), key);

            // The baked descriptor no longer names a slot, so ask the client's own table which slot does.
            if (slot < 0 && Allowed())
                slot = Found(gc, inst, type);

            index[type] = slot;
        }

        if (slot < 0)
            return 0;

        var parts = Hop(gc, inst + Offsets.Components);

        return parts <= Lowest ? 0 : Hop(gc, parts + (long)slot * 8);
    }

    /// <summary>
    /// `0x1401600C0`: the open-addressed table that turns the static type key into a slot number.
    ///
    /// Entries are sixteen bytes, key then index; the side table holds one eight-byte slot each, a
    /// tag and an entry number. The tag is `0x100 or (h and 0xFF)` and rises by 0x100 per probe, so
    /// a slot's tag says both what it holds and how far it has been displaced.
    /// </summary>
    /// <summary>
    /// The descriptor key to look the component up by: the one a repair found, or the one baked in.
    ///
    /// Both the read and the dump go through this, or the dump would keep reporting the stale offset failing
    /// after a repair had already moved past it.
    /// </summary>
    private static long Key(GameController gc)
    {
        var start = Safe.Read(gc, static g => g.Memory.AddressOfProcess, 0L);

        return Repaired > Lowest ? Repaired : start + Offsets.VolumeDescriptor;
    }

    /// <summary>Whether a repair may be attempted at all. See DebugSettings.RepairOffsets.</summary>
    private static bool Allowed() => Dump.Settings?.Debug.RepairOffsets.Value != false;

    /// <summary>
    /// Finds the component slot by trying the ones the client's own table offers, keeping only a slot whose
    /// component carries the tag.
    ///
    /// **Self-validating, which is the only reason a repair is safe here.** The tag is known independently -
    /// `expedition_no_placement` hashed by the client's own FNV-1a is 0x970C, and the global at rva 0x047159B4
    /// holds the same at runtime - so a candidate is accepted only when the component it resolves actually
    /// carries it. A wrong guess cannot be adopted; it can only fail and leave the offset reported as broken.
    /// Guessing an offset with no such check reads whatever happens to lie at the address and treats plausible
    /// rubbish as truth, which is worse than failing, because failing is visible.
    ///
    /// The key that worked is remembered in Repaired so later reads resolve through the ordinary path instead
    /// of searching again. The table is small - capacity four on every site measured - and the walk is bounded,
    /// so a site with nothing to find costs a few reads once.
    /// </summary>
    private static int Found(GameController gc, long inst, long type)
    {
        var parts = Hop(gc, inst + Offsets.Components);
        var reg = Hop(gc, type + Offsets.TypeRegistry);

        if (parts <= Lowest || reg <= Lowest)
            return -1;

        var map = reg + Offsets.TableAt;
        var one = Hop(gc, map);
        var stop = Hop(gc, map + 8);

        if (one <= Lowest || stop <= one)
            return -1;

        // Sixteen bytes an entry: the descriptor key at +0, the slot it names at +8. The span is capped for the
        // same reason the dump caps it - a table that did not read must not become a long walk.
        for (var e = one; e < stop && e - one < 0x200; e += 16)
        {
            var held = Hop(gc, e);
            var slot = Int(gc, e + 8);

            if (held <= Lowest || slot < 0 || slot > 64)
                continue;

            var part = Hop(gc, parts + (long)slot * 8);

            if (part <= Lowest || Word(gc, part + Offsets.VolumeTag) != Offsets.NoPlacementTag)
                continue;

            Repaired = held;

            return slot;
        }

        return -1;
    }

    private static int Slot(GameController gc, long reg, long key)
    {
        if (reg <= Lowest || key <= Lowest)
            return -1;

        var map = reg + Offsets.TableAt;
        var first = Hop(gc, map);
        var last = Hop(gc, map + 8);

        if (first <= Lowest || last <= first)
            return -1;

        var slots = Hop(gc, map + Offsets.TableSlots);
        var many = Qword(gc, map + Offsets.TableRoom);
        var shift = Byte(gc, map + Offsets.TableShift);

        if (slots <= Lowest || many <= 0 || many > (long)Sane * Sane)
            return -1;

        var mixed = Fold(Hash(key));
        var tag = 0x100u | (uint)(mixed & 0xFF);
        var where = (long)(mixed >> shift);

        for (var tried = 0; tried <= many; tried++)
        {
            if (where >= many)
                where = 0;

            if ((uint)Int(gc, slots + where * 8) == tag)
            {
                var entry = first + (long)Int(gc, slots + where * 8 + 4) * 16;

                if (Hop(gc, entry) == key)
                    return Int(gc, entry + 8);
            }

            where++;
            tag += 0x100;
        }

        return -1;
    }

    /// <summary>
    /// Offsets.OwnerPlaced the walk stops, stage by stage, because "nothing found" has a dozen causes.
    ///
    /// A chain this long fails silently: one wrong offset and every later stage sees zero without
    /// saying so. Counting at each step turns that into a single line naming the link that broke.
    /// </summary>
    public static string Explain(GameController gc, Vector2 around, float span)
    {
        var b = new System.Text.StringBuilder();

        var seat = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info?.Address ?? 0L, 0L);
        var owner = Hop(gc, seat + Offsets.ExpeditionOwner);
        var session = Hop(gc, owner + Offsets.OwnerSession);
        var terrain = session + Offsets.SessionTerrain;

        b.AppendLine($"  ClientExpedition {seat:X}  owner {owner:X}  session {session:X}  " +
                     $"terrain {terrain:X}");

        if (session <= Lowest)
            return b.Append("  the pointer walk to the session failed").ToString();

        // Every candidate for the tile dimensions, side by side, rather than the one the routine
        // was read as using - if that reading is off the others say so immediately.
        for (var off = 0x10; off <= 0x30; off += 8)
            b.AppendLine($"    terrain+0x{off:X2}  qword {Qword(gc, terrain + off):X16}");

        var wide = (int)Qword(gc, terrain + Offsets.NumTileIndexCols);
        var high = (int)Qword(gc, terrain + Offsets.NumTileIndexRows);
        var tiles = Hop(gc, terrain + Offsets.TileArray);

        b.AppendLine($"  tilesWide {wide}  tilesHigh {high}  tileArray {tiles:X}");

        var dims = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, default);

        b.AppendLine($"  AreaDimensions {dims.X}x{dims.Y} grid, so {dims.X / Tile}x{dims.Y / Tile} " +
                     "tiles is what those two should read");

        if (wide <= 0 || high <= 0 || wide > Sane || high > Sane || tiles <= Lowest)
            return b.Append("  tile dimensions or the array pointer are not sane").ToString();

        var lx = Math.Max(0, (int)(around.X - span) / Tile);
        var rx = Math.Min(wide - 1, (int)(around.X + span) / Tile);
        var ty = Math.Max(0, (int)(around.Y - span) / Tile);
        var by = Math.Min(high - 1, (int)(around.Y + span) / Tile);

        int scanned = 0, withAny = 0, entries = 0, passed = 0, instanced = 0,
            resolved = 0, tagged = 0;

        var key = Key(gc);

        // **The verdict first, because the detail below is unreadable without it.** A reader seeing no
        // rectangles needs to know whether the site has none or the lookup failed. See Said.
        b.AppendLine($"  {Said}");
        b.AppendLine($"  module base {Safe.Read(gc, static g => g.Memory.AddressOfProcess, 0L):X}  " +
                     $"type descriptor key {key:X}" +
                     (Repaired > Lowest ? " (REPAIRED - the baked offset no longer resolves)" : ""));

        var index = new Dictionary<long, int>();
        var slots = new Dictionary<int, int>();
        var kinds = new Dictionary<int, int>();
        var seen = new HashSet<long>();

        for (var y = ty; y <= by; y++)
        {
            for (var x = lx; x <= rx; x++)
            {
                scanned++;

                var record = tiles + ((long)y * wide + x) * Offsets.TileBytes;
                var at = Hop(gc, record + Offsets.TileObjectsFirst);
                var end = Hop(gc, record + Offsets.TileObjectsLast);

                if (at <= Lowest || end <= at || end - at > (long)Most * Offsets.ObjectEntryBytes)
                    continue;

                withAny++;

                for (; at < end; at += Offsets.ObjectEntryBytes)
                {
                    entries++;

                    var thing = Hop(gc, at);

                    if (thing <= Lowest)
                        continue;

                    var kind = Byte(gc, at + Offsets.ObjectEntryKind) & 0x7F;
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

                    if (kind < Volume)
                        continue;

                    passed++;

                    var inst = Hop(gc, thing + Offsets.Inner);

                    if (inst <= Lowest || !seen.Add(inst))
                        continue;

                    instanced++;

                    var type = Hop(gc, inst + Offsets.Inner);

                    if (!index.TryGetValue(type, out var slot))
                    {
                        var reg = Hop(gc, type + Offsets.TypeRegistry);

                        slot = Slot(gc, reg, key);
                        index[type] = slot;

                        // Only a couple of distinct types reach here, so the table each one carries
                        // is worth printing whole: a lookup that fails against a sane-looking table
                        // is a different fault from one against a table that never read.
                        var map = reg + Offsets.TableAt;

                        b.AppendLine($"    type {type:X} reg {reg:X} -> slot {slot}");
                        b.AppendLine($"      entries {Hop(gc, map):X}..{Hop(gc, map + 8):X}  " +
                                     $"side {Hop(gc, map + Offsets.TableSlots):X}  capacity " +
                                     $"{Qword(gc, map + Offsets.TableRoom)}  shift {Byte(gc, map + Offsets.TableShift)}");

                        var mixed = Fold(Hash(key));

                        b.AppendLine($"      hash {mixed:X16} -> tag {0x100u | (uint)(mixed & 0xFF):X3}" +
                                     $" slot {(long)(mixed >> Byte(gc, map + Offsets.TableShift))}");

                        // **What the table actually holds.** A key of ours that is absent from a
                        // table of plausible module addresses means this type has no such component;
                        // one that is absent from a table of something else entirely means the key,
                        // or the walk to the table, is wrong. Those need different fixes, and only
                        // the contents tell them apart.
                        var one = Hop(gc, map);
                        var stop = Hop(gc, map + 8);

                        for (var e = one; e > Lowest && e < stop && e - one < 0x200; e += 16)
                        {
                            var held = Hop(gc, e);

                            b.AppendLine($"        key {held:X}  slot {Int(gc, e + 8)}" +
                                         $"  (module+{held - (key - Offsets.VolumeDescriptor):X})");
                        }
                    }

                    slots[slot] = slots.GetValueOrDefault(slot) + 1;

                    var part = Resolve(gc, inst, index, key);

                    if (part <= Lowest)
                        continue;

                    resolved++;

                    if (Word(gc, part + Offsets.VolumeTag) == Offsets.NoPlacementTag)
                        tagged++;
                }
            }
        }

        b.AppendLine($"  tiles {lx}..{rx} x {ty}..{by}: {scanned} scanned, {withAny} with an " +
                     $"object vector");
        b.AppendLine($"  entries {entries}; past the kind filter {passed}; distinct instances " +
                     $"{instanced}; resolved a component {resolved}; carrying the tag {tagged}");
        b.AppendLine($"  entry kinds seen: {Tally(kinds)}");
        b.AppendLine($"  resolver slots:   {Tally(slots)}   (-1 is the table lookup failing)");

        // **Every entry, ignoring the kind filter, purely to prove the lookup works.** If no type
        // anywhere yields a slot then the table walk is broken and the filtered result says nothing;
        // if some do, the walk is sound and the filtered types genuinely lack the component. The
        // routine would not look here - this is a test of the instrument, not of the rule.
        var anySlot = new Dictionary<int, int>();
        var everything = new HashSet<long>();

        for (var y = ty; y <= by; y++)
        {
            for (var x = lx; x <= rx; x++)
            {
                var record = tiles + ((long)y * wide + x) * Offsets.TileBytes;
                var at = Hop(gc, record + Offsets.TileObjectsFirst);
                var end = Hop(gc, record + Offsets.TileObjectsLast);

                if (at <= Lowest || end <= at || end - at > (long)Most * Offsets.ObjectEntryBytes)
                    continue;

                for (; at < end; at += Offsets.ObjectEntryBytes)
                {
                    var thing = Hop(gc, at);
                    var inst = thing <= Lowest ? 0 : Hop(gc, thing + Offsets.Inner);

                    if (inst <= Lowest || !everything.Add(inst))
                        continue;

                    var type = Hop(gc, inst + Offsets.Inner);

                    if (!index.TryGetValue(type, out var slot))
                    {
                        slot = Slot(gc, Hop(gc, type + Offsets.TypeRegistry), key);
                        index[type] = slot;
                    }

                    anySlot[slot] = anySlot.GetValueOrDefault(slot) + 1;
                }
            }
        }

        b.AppendLine($"  ignoring the kind filter: {everything.Count} instances, slots {Tally(anySlot)}");

        return b.ToString();
    }

    private static string Tally(Dictionary<int, int> counts)
    {
        var parts = new List<string>();

        foreach (var pair in counts)
            parts.Add($"{pair.Key}:{pair.Value}");

        parts.Sort(StringComparer.Ordinal);

        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    /// <summary>
    /// FNV-1a over the eight bytes of the key. Plain, with no extra round.
    ///
    /// **The trailing `imul` at `0x14016016A` is the loop's own last multiply, not another one.**
    /// The compiler hoists each multiply past the following xor - `h = (h * prime) ^ b` seven times
    /// after seeding with `seed ^ b0` - which leaves one multiply stranded after the final xor and
    /// reads exactly like an extra round. It is not: unrolled that way, the sequence is the ordinary
    /// `h ^= b; h *= prime` eight times over.
    ///
    /// Adding that round cost a dump. The key was in the table all along, at component slot 2; the
    /// wrong hash sent the probe to the wrong side-table slot with the wrong tag, and the lookup
    /// reported the type as simply not having the component.
    /// </summary>
    private static ulong Hash(long key)
    {
        var h = Seed;

        for (var i = 0; i < 8; i++)
        {
            h ^= (byte)(key >> (i * 8));
            h *= Prime;
        }

        return h;
    }

    /// <summary>The 128-bit multiply by the golden ratio, folded high against low.</summary>
    private static ulong Fold(ulong h)
    {
        var lo = Math.BigMul(h, Mix, out var hi);

        return hi ^ lo;
    }

    private static long Hop(GameController gc, long at) => Qword(gc, at);

    private static long Qword(GameController gc, long at)
    {
        if (at <= Lowest)
            return 0;

        var raw = Safe.Read(() => gc.Memory.ReadBytes(at, 8), null);

        return raw == null || raw.Length < 8 ? 0L : BitConverter.ToInt64(raw, 0);
    }

    private static int Int(GameController gc, long at)
    {
        var raw = at <= Lowest ? null : Safe.Read(() => gc.Memory.ReadBytes(at, 4), null);

        return raw == null || raw.Length < 4 ? 0 : BitConverter.ToInt32(raw, 0);
    }

    private static ushort Word(GameController gc, long at)
    {
        var raw = at <= Lowest ? null : Safe.Read(() => gc.Memory.ReadBytes(at, 2), null);

        return raw == null || raw.Length < 2 ? (ushort)0 : BitConverter.ToUInt16(raw, 0);
    }

    private static byte Byte(GameController gc, long at)
    {
        var raw = at <= Lowest ? null : Safe.Read(() => gc.Memory.ReadBytes(at, 1), null);

        return raw == null || raw.Length < 1 ? (byte)0 : raw[0];
    }


    private const ulong Seed = 0xCBF29CE484222325UL;
    private const ulong Prime = 0x100000001B3UL;
    private const ulong Mix = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// The low seven bits of an entry's kind byte that `0x141E7E5CC` searches past. A threshold
    /// rather than an offset, so it lives with the routine that tests it.
    /// </summary>
    private const int Volume = 4;

    /// <summary>How many grid units one tile spans.</summary>
    private const int Tile = 23;

    /// <summary>Guards against a bad pointer asking for an unbounded read.</summary>
    private const long Lowest = Offsets.Lowest;

    private const int Sane = 4096;
    private const int Most = 4096;
}
