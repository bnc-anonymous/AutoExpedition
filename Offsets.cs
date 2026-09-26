namespace AutoExpedition;

/// <summary>
/// Every offset this plugin reads the client's memory at, and how to repair them after a patch.
///
/// ExileCore2 states none of them. Both shipped assemblies are protected, so ILSpy returns a
/// different nonsense `FieldOffset` for the same field on every run and the runtime refuses to load
/// the structs at all - `Marshal.OffsetOf` cannot ask either. They are ours to maintain, and each one
/// names the instruction it was read from so it can be checked again later.
///
/// ## Repairing them
///
/// **Start from an anchor, never from the addresses below.** A patch moves the code as well as the
/// data, so an address here says where a number came from, not where to look next time.
///
/// ### 1. Notice
///
/// **The plugin says so itself.** `Terrain.Trusted` checks the routing grid against what a real one
/// has to be - a sane size, enough bytes for it, the detonator's own cell inside it, and not almost
/// entirely blocked - and a grid that fails any of those raises "Offsets are broken" in red, naming
/// the check that failed. `Planning.Start` then refuses to solve at all.
///
/// That refusal is deliberate and is the whole point: planning from a straight-line stand-in for the
/// game's placement rule draws exactly as confidently on screen and quietly produces worse chains,
/// so there is nothing to notice. A plugin that stops is a bug report; one that carries on is not.
///
/// To check without waiting to be told, stand at a dig site with the placement circle up and press
/// the **Dump key** (Debug settings), which writes a `site_*.txt` into the plugin's `dumps` folder.
/// In it:
///
/// <code>
/// volumes N ...     a count for each stage of the tile walk. The first stage reading nought names
///                   the offset that moved, which makes this the most useful check here.
/// engine-search     "the search has not run" means the A* node array is not where it was.
/// </code>
///
/// On screen, every spot drawn green means the routing grid is not being read at all.
///
/// ### 2. Confirm it is these and not an unhandled case
///
/// Run **Sweep the boundary and check it against the game** (Debug settings) before changing
/// anything. It walks the cursor over the ground and compares the game's own verdict against ours.
/// The rule is validated at 2,117 spots with none disagreeing, so disagreements in quantity mean
/// something read here has moved; a handful means a case never handled, which is different work.
///
/// ### 3. Recover from an anchor
///
/// Find something distinctive a patch will not rename, then read the offsets off the code that uses
/// it:
///
/// <code>
/// "expedition_no_placement"  utf-16 in .rdata  the placement routine, the tag global, the tag
///                                              lookup, the coarse conversion
/// 0xB21642C9                 dword literal     every divide by 23
/// 1.4142099618911743         float literal     the A* diagonal cost, so the router and its nodes
/// 0xCBF29CE484222325         qword literal     FNV-1a, so the table that resolves a component
/// </code>
///
/// How the volume chain was found: the string sits in `.rdata`, exactly one `lea` in `.text` points
/// at it, and that `lea` is three instructions into the placement routine. The `movzx` below it names
/// the tag global and the `call` after that names the tag lookup; the lookup gives the tile offsets,
/// and the resolver it calls gives the component table.
///
/// Cross-reference by scanning bytes rather than disassembling forwards - a linear sweep of a 48 MB
/// `.text` desynchronises on the first data island and then finds nothing at all.
///
/// ### 4. Two traps
///
/// **A module-relative address is not a file address.** TagGlobal and VolumeDescriptor are relative
/// to the image base and must be added to `Memory.AddressOfProcess`. Using the address a disassembler
/// prints matches nothing, for every type, and looks exactly like the field not existing.
///
/// **A failed lookup and a missing field read identically.** When the component resolver returned -1
/// for every type, that fitted both "the key is wrong" and "these types have no such component"
/// equally. Printing the table's own contents separated them - our key was in it. Print the
/// intermediate state instead of choosing between two explanations.
///
/// ### 5. Verify against the game
///
/// `Peek.Searched` reads the engine's own finished A* node array and compares its costs with ours
/// cell by cell, so a plausible guess cannot satisfy it. Then sweep the boundary again and expect no
/// disagreements, including inside any forbidden rectangle. A tagged-component count in the low
/// single figures out of a thousand instances is right; a large one means the filter is wrong rather
/// than the site unusual.
/// </summary>
internal static class Offsets
{
    // ---- the walk from the detonator element to the terrain ------------------------------------
    //
    // `0x141F5A7D5` calls `[vtable + 0x1A0]`, which is `lea rax, [rcx + 0x8D0]; ret` - so the route
    // builder asks its session for the terrain and the terrain sits at that fixed offset inside it.

    /// <summary>ClientExpedition + this is the object holding the session.</summary>
    public const int ExpeditionOwner = 0x48;

    /// <summary>That object + this is the session.</summary>
    public const int OwnerSession = 0x20F0;

    /// <summary>And the session + this is TerrainStruct. See VtableTerrain.</summary>
    public const int SessionTerrain = 0x8D0;

    /// <summary>The vtable slot that hands back the terrain, for confirming the walk by reading it.</summary>
    public const int VtableTerrain = 0x1A0;

    // ---- TerrainStruct -------------------------------------------------------------------------
    //
    // Indexed by `0x141E7E500`, which bounds-checks a point against these two times 23 and then
    // indexes the tile array by `(y / 23) * wide + (x / 23)`.

    // Named as GameOffsets2 names them. Where ExileCore2 already has a word for something, this uses
    // that word rather than a better one: two names for one field is how two files come to disagree
    // about what a number means, and the cost of looking it up is paid once.

    /// <summary>Tiles across.</summary>
    public const int NumTileIndexCols = 0x18;

    /// <summary>Tiles down.</summary>
    public const int NumTileIndexRows = 0x20;

    /// <summary>The tile array, one TileBytes record per tile.</summary>
    public const int TileArray = 0x28;

    /// <summary>The first of the four terrain layer vectors. `0x141D0D220` reads layer 0 here.</summary>
    public const int Layers = 0xD0;

    /// <summary>How far apart those four vectors sit.</summary>
    public const int LayerStride = 0x18;

    /// <summary>Bytes per packed row, two cells to a byte. `0x141D0D220` divides x by 2.</summary>
    public const int BytesPerRow = 0x130;

    // ---- one tile ------------------------------------------------------------------------------
    //
    // ExileCore2's GameOffsets2.TileStructure declares Size = 56, which is TileBytes, and an
    // EntitiesList StdVector, which is the pair below - so this much is already named upstream.

    /// <summary>One tile record. 0x38 = 56.</summary>
    public const int TileBytes = 0x38;

    /// <summary>The tile's own object vector: begin.</summary>
    public const int TileObjectsFirst = 0x10;

    /// <summary>And end.</summary>
    public const int TileObjectsLast = 0x18;

    /// <summary>One entry in that vector.</summary>
    public const int ObjectEntryBytes = 0x10;

    /// <summary>
    /// The byte `0x141E7E5CC` orders the vector by, whose low seven bits under four are skipped.
    /// </summary>
    public const int ObjectEntryKind = 0x8;

    // ---- an object, its type, and its components -----------------------------------------------
    //
    // `0x1417306F0`: the instance is `[[entry] + 8]`, its type `[inst + 8]`, the type's table
    // `[type + 0x28] + 0x28`, and the component array `[inst + 0x10]` indexed by what the table says.

    /// <summary>A pointer at +8 on both: entry to instance, and instance to type.</summary>
    public const int Inner = 0x8;

    /// <summary>The instance's component array, which the table's index selects from.</summary>
    public const int Components = 0x10;

    /// <summary>The type's registry, which holds the table.</summary>
    public const int TypeRegistry = 0x28;

    // ---- the table that turns a static type key into a component slot ---------------------------
    //
    // `0x1401600C0`, open-addressed: 16-byte entries of key then slot, an 8-byte side table of tag
    // then entry number, FNV-1a over the eight bytes of the key address folded by the golden ratio.

    /// <summary>The registry + this is the table.</summary>
    public const int TableAt = 0x28;

    /// <summary>The table's side table, eight bytes a slot.</summary>
    public const int TableSlots = 0x18;

    /// <summary>How many slots it has.</summary>
    public const int TableRoom = 0x20;

    /// <summary>How far to shift the folded hash to index them.</summary>
    public const int TableShift = 0x36;

    // ---- the tagged volume component -------------------------------------------------------------
    //
    // `0x141E7E621` compares the tag; `0x141DE9D00` builds the rectangle from the size and the
    // owner's position, near edge taking size/2 truncated and the far edge the remainder.

    /// <summary>The authored tag, as a word. See Volumes for the one this plugin looks for.</summary>
    public const int VolumeTag = 0x40;

    /// <summary>The rectangle's width and height, in grid units.</summary>
    public const int VolumeWide = 0x44;

    public const int VolumeHigh = 0x48;

    /// <summary>The component's owner + this is where it is placed.</summary>
    public const int OwnerPlaced = 0x98;

    /// <summary>And that + these is the position the rectangle is centred on.</summary>
    public const int PlacedX = 0x444;

    public const int PlacedY = 0x448;

    // ---- the coarse routing grid -----------------------------------------------------------------
    //
    // The grid `0x141F5A93F` searches. Layout from `0x141F5A84E` and `0x141D02C60`: width and height
    // as words at +0 and +2, its origin tile at +4 and +6, the connectivity masks at +8.

    /// <summary>
    /// ClientExpedition + this is the object the routing grid hangs off.
    ///
    /// **The grid is sized to the encounter, not to the map**, which is why it carries an origin
    /// tile at all: one Grand site read 121 x 121 coarse cells on a map 2300 grid across. Anything
    /// that checks it by comparing its dimensions with the area's is wrong.
    /// </summary>
    public const int ExpeditionRouting = 0x1A8;

    /// <summary>And that object + this is the grid.</summary>
    public const int RoutingGrid = 0x38;

    /// <summary>
    /// The grid + this is the A* node array, eight bytes a cell, which survives the search.
    ///
    /// The same number as RoutingGrid and a different structure, which is exactly the coincidence
    /// that made two files disagree about what Stride meant. Named apart on purpose.
    /// </summary>
    public const int RoutingNodes = 0x38;

    // ---- addresses within the module, not offsets within a structure ------------------------------
    //
    // Both are relative to the image base and must be added to Memory.AddressOfProcess. The client is
    // relocated, and using a file address matched nothing at all - see Volumes.Descriptor.

    /// <summary>Where the client stores the hash of the tag it tests. Written at `0x141F5A73B`.</summary>
    public const long TagGlobal = 0x047159B4;

    /// <summary>
    /// `expedition_no_placement`, hashed the way the client hashes it.
    ///
    /// Not an offset, but the same kind of liability: a constant read out of the client that a patch
    /// could move. `0x141F5A736` runs FNV-1a over the 23 utf-16 units and folds to 16 bits, which
    /// gives this - and it matches what TagGlobal holds at runtime, so it is certain two ways.
    /// </summary>
    public const ushort NoPlacementTag = 0x970C;

    /// <summary>The static type descriptor `0x14173071D` loads to find the volume component.</summary>
    public const long VolumeDescriptor = 0x1434D2E80 - 0x140000000;

    // ---- guards ------------------------------------------------------------------------------------

    /// <summary>Below this an address is not a pointer, it is a small number read by mistake.</summary>
    public const long Lowest = 0x10000;
}
