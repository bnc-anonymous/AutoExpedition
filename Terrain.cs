using ExileCore2;
using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Where an explosive may be put.
///
/// Read once, into a snapshot, for two reasons. The search runs on a background thread and the
/// arrays behind this live in the game's memory, which is not somewhere to be reaching from off the
/// main thread; and each read materialises the whole grid, which is not something to do per
/// candidate in a scoring loop.
///
/// **Pathfinding data, not targeting data**, which is the opposite of what this started as. The
/// reasoning for targeting was sound - an explosive is placed at arm's length rather than walked
/// to - and it was wrong for a duller reason: that grid does not distinguish anything. Sampled
/// across a dig site it reads 5 for six thousand four hundred and sixty seven of its six and a half
/// thousand cells. Pathfinding reads zero for a thousand of them and one to five for the rest,
/// which is a description of somewhere.
///
/// **The number is CLEARANCE, not a kind of ground, and every threshold in this file was argued on
/// the wrong assumption.** Measured: 2,120 cells valued 1 to 4 around a dig site, each against the
/// real distance to the nearest zero. Rounded straight-line distance fits 93.6% of them; chebyshev
/// 77.8%, manhattan 73.4%, floor 81.7%, ceiling 79.5%. The misses that remain are all diagonals the
/// rounding settles - 1.41 reads 1, 2.24 reads 2, 3.16 reads 3. So the grid holds the distance to
/// the nearest blocking cell, capped at 5, which is a distance transform and not a classification.
///
/// Two things gave it away before the test. Over a dig site the counts of 1, 2, 3 and 4 come out
/// nearly equal - 138, 147, 109, 134 against 2,252 zeroes and 3,781 fives - which is what one-cell
/// bands wrapped around obstacles look like and is no property of a set of terrain types. And every
/// neighbourhood beside a blocker is a pure ramp perpendicular to it, rising by exactly one per cell
/// and constant along the wall: [2 3 4 / 2 3 4 / 2 3 4].
///
/// This reframes the whole question. <see cref="Placeable"/> asking for 4 does not mean "ground of
/// type 4 or better"; it means FOUR GRID UNITS OF ROOM, which is a footprint rule and a sensible one
/// for putting a barrel down. <see cref="Walkable"/> accepting anything non-zero does not mean "any
/// passable ground"; it means half a unit of room, so the router may thread a chain along a wall.
/// Those two being different is not obviously a bug - a link between two explosives is not a thing
/// that has to fit anywhere, so it may legitimately pass where a barrel cannot - but it is now a
/// question about physical room rather than about which integer to pick, and it should be settled by
/// clamp measurements rather than by tuning a cutoff.
///
/// The accuracy of the cutoffs, measured against 400 cells the game accepted and 814 it refused, all
/// gathered with the cursor and the indicator agreeing on one cell: 4 scores 89.3%, 3 scores 89.5%,
/// and non-zero scores 87.0%, against a 67.1% baseline for guessing "placeable" every time. So the
/// grid IS the placement rule and 4 is a fine place to draw it. The targeting grid scores 35.0% -
/// worse than guessing, because it reads 5 almost everywhere including 789 of those 814 refusals -
/// which finally disposes of it as a candidate.
///
/// The residual at 4 is 92 accepted cells reading below it. Those are content standing on the
/// ground: a remnant makes a patch nobody can walk through that an explosive can still be placed on,
/// so its own footprint eats the clearance around it. That is the honest cost of the rule, not a
/// fault in it.
///
/// A spot wrongly allowed shows up as a link the game refuses, which the placement sequence notices
/// and reports; a spot wrongly forbidden is content quietly left out of every plan, which nothing
/// would ever tell you about.
/// </summary>
internal sealed class Terrain
{
    private readonly int[][] _grid;

    /// <summary>
    /// Whether each solid cell asked about is shallow enough to build on. See Placeable.
    ///
    /// Memoised because the answer costs a small area scan and cannot be looked up: the clearance
    /// transform runs OUTWARD from solid cells, so inside them every value is 0 and the depth has to
    /// be measured. Concurrent because the search that asks is multi-threaded.
    /// </summary>
    private readonly ConcurrentDictionary<(int X, int Y), bool> _shallow = new();

    /// <summary>
    /// The game's own coarse routing grid, when it can be read. See Peek.Coarse.
    ///
    /// **When this is present it is the answer, and the rule below is not consulted.** It is the
    /// array `0x141F5A6B0` indexes to decide placement, measured exactly necessary over 12,014
    /// corroborated readings: a zero byte was refused 5,353 times and accepted zero times. Null when
    /// there is no dig site, or when a capture is being routed offline.
    /// </summary>
    private readonly Peek.Slab _slab;

    /// <summary>
    /// The game's own routing, when the client published enough to run it. See Wire.
    ///
    /// Null offline, and null before a dig site exists - both of which fall back to the straight
    /// line, which is the right answer when there is no routing grid to detour around.
    /// </summary>
    private readonly Wire _wire;

    /// <summary>
    /// The authored rectangles the site forbids outright. Empty when none were read. See Volumes.
    /// </summary>
    private Volumes _volumes = new();

    /// <summary>
    /// Where to point to reach a spot the reach cannot, and whether that is switched on at all.
    ///
    /// **One choke point, so the solver never learns the mechanic exists.** Everything that decides
    /// whether a link is legal goes through Aiming; the search, the repair and the placement step all
    /// ask the same question and get an aim back. That keeps the difference between the two builds to
    /// one method rather than to conditions threaded through a search, which is the only version of
    /// this that can be trusted to strip cleanly. See Aiming.
    /// </summary>

    private Terrain(int[][] grid, Peek.Slab slab = null, int[][] aim = null)
    {
        _grid = grid;
        _slab = slab;

        var wire = slab == null ? null : Reused(aim, slab) ?? new Wire(aim, slab);

        _wire = wire != null && wire.Ready ? wire : null;

        if (_wire != null)
            Keep(aim, slab, _wire);
    }

    /// <summary>
    /// The router the last snapshot built, and the grids it was built from, for the next snapshot to reuse.
    ///
    /// **A snapshot lives for one solve, and its router's cache died with it.** Every solve reads a new Terrain,
    /// and a new Wire starts with nothing routed - so each presolve pass and each press on a site searched the same
    /// pairs again. Measured on Craggy Peninsula, twenty explosives: 1.47 million wire searches in one solve, 70% of
    /// questions missing the cache, and a segment tear or a reach move costing about 31ms an attempt against under
    /// a millisecond and about seven on an ordinary site.
    ///
    /// A Wire's answers depend on nothing but the two grids it is given - see Wire's constructor and Measure - so
    /// the same grids give the same answers, and reusing it cannot change any result. The grids are compared cell
    /// for cell rather than trusted, because the coarse one is read live from the client each time.
    /// </summary>
    private static (int[][] Aim, Peek.Slab Slab, Wire Wire) _kept;

    /// <summary>Guards _kept, since a snapshot can be taken from more than one thread.</summary>
    private static readonly object KeptGate = new();


    /// <summary>The router kept across solves, for the dump. Null when none is kept. See _kept.</summary>
    internal static Wire KeptRouter
    {
        get
        {
            lock (KeptGate)
                return _kept.Wire;
        }
    }



    /// <summary>The kept router, when these grids are the ones it was built from and it is not full. See _kept.</summary>
    private static Wire Reused(int[][] aim, Peek.Slab slab)
    {
        lock (KeptGate)
        {
            var (keptAim, keptSlab, wire) = _kept;

            if (wire == null)
                return null;

            return SameSlab(keptSlab, slab) && SameGrid(keptAim, aim) ? wire : null;
        }
    }

    private static void Keep(int[][] aim, Peek.Slab slab, Wire wire)
    {
        lock (KeptGate)
            _kept = (aim, slab, wire);
    }

    /// <summary>
    /// Drops the kept router, so the next snapshot routes from nothing. Called by a cold start, which promises to
    /// forget the routed ground held in memory, and which the repeat batches rely on to make presses comparable.
    /// See _kept.
    /// </summary>
    internal static void ForgetRouting()
    {
        lock (KeptGate)
            _kept = default;
    }

    private static bool SameSlab(Peek.Slab a, Peek.Slab b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a == null || b == null || a.Wider != b.Wider || a.Higher != b.Higher || a.FromX != b.FromX ||
            a.FromY != b.FromY || a.Bytes.Length != b.Bytes.Length)
            return false;

        return a.Bytes.AsSpan().SequenceEqual(b.Bytes);
    }

    private static bool SameGrid(int[][] a, int[][] b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (var y = 0; y < a.Length; y++)
        {
            if (ReferenceEquals(a[y], b[y]))
                continue;

            if (a[y] == null || b[y] == null || !a[y].AsSpan().SequenceEqual(b[y]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// How far the wire actually runs between two points, which is what the reach is spent on.
    ///
    /// **Reach is a budget on the routed wire, not a radius.** `0x141F5A6B0` accumulates the
    /// polyline's segments and cuts it the moment the running total passes the reach, so a link
    /// whose ends are close in a straight line but whose wire has to go round something does not
    /// land where it was aimed - the game places it short. Infinity when nothing routes.
    ///
    /// Falls back to the straight line when there is no routing grid, which is also what the
    /// straight line is: with nothing to go round, the wire is the line.
    /// </summary>
    public float Wired(Vector2 from, Vector2 to) =>
        _wire?.Length(from, to) ?? Vector2.Distance(from, to);

    /// <summary>
    /// Whether aiming at a spot actually puts the explosive ON it.
    ///
    /// **Not "is the wire short enough".** The clamp relocates rather than refuses, so the question
    /// is where the wire's cut end lands, and over open ground that rounds back onto a spot half a
    /// unit past the budget while round an obstacle it does not. See Wire.Lands.
    ///
    /// With no routing grid there is nothing to bend around, so the wire is the straight line and
    /// the same rounding applies to it.
    /// </summary>
    public bool Reaches(Vector2 from, Vector2 to, float reach) =>
        _wire?.Lands(from, to, reach) ?? Vector2.Distance(from, to) <= reach + Rounding;

    /// <summary>Half a cell, which is how far a clamped landing may sit and still be on the spot.</summary>
    private const float Rounding = 0.5f;

    /// <summary>
    /// Where the explosive would actually land if you aimed at a spot - our prediction of the
    /// clamped end of the wire. See Wire.Landing.
    ///
    /// Exposed so the frontier sweep can put this against the game's own answer. Zero when there is
    /// no routing grid, which the caller reads as "nothing to compare".
    /// </summary>
    public Vector2 Landing(Vector2 from, Vector2 to, float reach) =>
        _wire?.Landing(from, to, reach) ?? Vector2.Zero;

    /// <summary>
    /// Builds a snapshot from rows rather than from the game, so a captured site can be routed
    /// against offline. A null row means "not recorded", which Walkable already reads as blocked -
    /// the same answer a wall gives, and the reason a capture records a box rather than the map.
    /// See Capture.
    /// </summary>
    public static Terrain From(int[][] grid) => grid == null ? null : new Terrain(grid);

    /// <summary>Takes the snapshot, or null when the grid cannot be read.</summary>
    public static Terrain Read(GameController gc)
    {
        // Each part timed apart: the pathfinding grid is read whole out of the game, the coarse grid is read and
        // checked, and the router is built new whenever the ground differs from the kept one. See Spent.LongGaps.
        using var reading = Spent.On("Terrain.Read");

        int[][] walkable;

        using (Spent.On("Terrain.Read/Pathfinding"))
            walkable = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (walkable == null || walkable.Length == 0)
            return null;

        // The coarse routing grid is the rule itself rather than a reading of it, so take it whenever
        // the client has one. It is absent outside a dig site, which is when nothing asks anyway.
        Peek.Slab coarse;

        using (Spent.On("Terrain.Read/Coarse"))
            coarse = Trusted(gc, Peek.Coarse(gc));

        Terrain made;

        using (Spent.On("Terrain.Read/Router"))
            made = new Terrain(walkable, coarse, walkable);

        using (Spent.On("Terrain.Read/Learned"))
            return Learned(gc, made);
    }


    /// <summary>
    /// Reads the site's forbidden rectangles into a fresh snapshot.
    ///
    /// **Around the detonator rather than the whole map.** The lookup walks every object of every
    /// tile it is given, and a map is 63 x 147 tiles where a dig site is a few dozen across - so the
    /// span is drawn from the detonator, wide enough that no chain reaches past it. Done once, here,
    /// because the rectangles are authored into the tiles and do not move.
    /// </summary>
    private static Terrain Learned(GameController gc, Terrain ground)
    {
        var seat = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.DetonatorGridPosition ?? default, default);

        if (seat.X != 0 || seat.Y != 0)
            ground._volumes = Volumes.For(gc, new Vector2(seat.X, seat.Y), Span);

        return ground;
    }

    /// <summary>
    /// Whether the site forbids this spot outright, whatever the grids say about it.
    ///
    /// **This is the term that explained ground every published layer calls open.** `0x141F5A6B0`
    /// tests the requested point for `expedition_no_placement` before it converts anything or routes
    /// anywhere, and a hit writes validity 0 and returns - see Volumes. Measured at a Port site: one
    /// rectangle, 54 x 45 grid from (1022,711), sitting over the expedition NPC, and a cursor inside
    /// it threw the explosive 42 grid away to just outside its edge.
    /// </summary>
    public bool Forbids(Vector2 at) => _volumes.Forbids((int)at.X, (int)at.Y);

    /// <summary>
    /// Stopwatch ticks this thread has spent routing a direct aim, and in the clamp search for a stretched one, with how
    /// many clamp searches ran. Per thread, so a caller can take the difference across its own calls while other threads
    /// route at the same time. See Openings, which reports the enumeration's share.
    /// </summary>
    [ThreadStatic] internal static long DirectAimTicks;

    /// <summary>See DirectAimTicks.</summary>
    [ThreadStatic] internal static long SnapSearchTicks;

    /// <summary>See DirectAimTicks.</summary>
    [ThreadStatic] internal static int SnapSearches;

    /// <summary>Clamp searches not run because the shortest route was already too long. See Aiming.</summary>
    [ThreadStatic] internal static int ClampRuledOut;

    /// <summary>
    /// How far past the reach the shortest route may be and still have a clamp searched for: the half cell the cut end
    /// rounds by, and some for this router's route differing from the game's. Three grid is chosen, not measured.
    /// </summary>
    private const float ClampSlack = 3f;

    /// <summary>
    /// Whether an explosive can be put on a spot, and where the cursor has to be to do it.
    ///
    /// **This is the one question the planner asks about a link, and the only place the two builds
    /// differ.** Reaches answers whether pointing straight at a spot puts the explosive there, which
    /// is the whole answer when nothing else is allowed. The aim comes back separately because where
    /// the bomb goes and where the cursor goes are not always the same thing - and a caller that
    /// needs one and not the other should not have to know which case it is in.
    ///
    /// Memoised on the pair, because a search asks about the same two cells thousands of times and
    /// the answer cannot change inside one solve.
    /// </summary>
    public bool Aiming(Vector2 from, Vector2 to, float reach, out Vector2 aim)
    {
        aim = to;

        return Reaches(from, to, reach);
    }


    /// <summary>
    /// Whether this snapshot routes, or is falling back to the straight line.
    ///
    /// **A model with no wire answers every reach question with the distance**, which is the one
    /// answer that is always too generous: the wire bends and the budget is spent along it. The
    /// fallback is right where there is genuinely no routing grid to bend around anything, and
    /// silently wrong where there is one and it could not be trusted at the moment this snapshot was
    /// taken - a slab that does not contain the detonator, which is what a slab belonging to the
    /// dig site of the previous map looks like. See Trusted, and Forbidden.Gather, which held one
    /// of those for a whole map.
    /// </summary>
    public bool Routing => _wire != null;

    /// <summary>Where to put the cursor to place on this spot. The spot itself, ordinarily.</summary>
    public Vector2 Pointing(Vector2 from, Vector2 to, float reach) =>
        Aiming(from, to, reach, out var aim) ? aim : to;


    /// <summary>Every forbidden rectangle, for the overlay to draw and the dump to print.</summary>
    public System.Collections.Generic.IReadOnlyList<Volumes.Box> Forbidden => _volumes.All;

    /// <summary>
    /// How far around the detonator to read tiles for forbidden rectangles. Generous: a chain on a
    /// Port site has been seen to start 357 grid from its head.
    /// </summary>
    public const float Span = 600f;

    /// <summary>
    /// Why the routing grid cannot be believed, or null when it can be. See Trusted.
    ///
    /// **A read this plugin cannot verify is a reason to stop, not a reason to guess.** The old
    /// behaviour was to fall back to straight lines and carry on, which is the worst of both: the
    /// overlay still drew confident greens, the planner still published a chain, and the only sign
    /// anything was wrong was that the chains had quietly got worse. Nobody reads a plan for whether
    /// it was built from the game's own rule or from a stand-in for it.
    ///
    /// So this is stated instead, once, and Planning.Start refuses on it.
    /// </summary>
    public static string Broken { get; private set; }

    /// <summary>Forgets a diagnosis on a zone change, so a bad site cannot condemn the next one.</summary>
    public static void Unbreak() => Broken = null;

    /// <summary>
    /// The routing grid, but only if it agrees the detonator is standing somewhere routable.
    ///
    /// **Because the failure is total and silent.** If the offsets move under a patch, or the
    /// pointer walk lands on something else, every byte reads zero, every cell is unroutable and the
    /// planner quietly finds nowhere on the map to put an explosive - which looks like a broken
    /// planner rather than a bad read. The detonator is by construction inside the region the wire
    /// routes through, so a grid that calls its cell unroutable is not this map's grid.
    ///
    /// **Only an active dig site can tell a broken read from an absent one.** Away from one there is
    /// no routing grid to read and nothing asks for it, so the same null means nothing is wrong.
    ///
    /// **And a seat is not an active dig site.** The detonator panel names every encounter in the
    /// map, with its grid position, from anywhere - so gating the diagnosis on "the panel gives a
    /// seat" would raise "offsets are broken" on the walk up to a site the client has not started
    /// yet, and refuse every solve with it. What separates the two is the panel stating a SIZE: the
    /// total reads nought until the site is live, and the site's real count afterwards. That is the
    /// same test Remaining uses to tell an unfilled panel from a spent one.
    /// </summary>
    private static Peek.Slab Trusted(GameController gc, Peek.Slab slab)
    {
        var seat = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.DetonatorGridPosition ?? default, default);

        var sited = seat.X != 0 || seat.Y != 0;
        var wrong = Wrong(slab, seat, sited);

        if (sited && Detonator.PanelReady(gc))
            Broken = wrong;

        return wrong == null ? slab : null;
    }

    /// <summary>
    /// What is wrong with a routing grid, in the words the warning should say, or null if nothing is.
    ///
    /// Split out of Trusted so each check can name itself. "Offsets are broken" on its own tells the
    /// player to reinstall something; which check failed tells whoever repairs them where to start,
    /// and that is the same list Offsets.cs walks. See Offsets.
    /// </summary>
    private static string Wrong(Peek.Slab slab, GameOffsets2.Native.Vector2i seat, bool digging)
    {
        if (slab == null || slab.Bytes == null)
            return "the coarse routing grid could not be read at all";

        if (slab.Wider <= 0 || slab.Higher <= 0 || slab.Wider > Most || slab.Higher > Most)
            return $"the routing grid reads {slab.Wider} x {slab.Higher} coarse cells, " +
                   $"which is not a size a grid has";

        if (slab.Bytes.Length < slab.Wider * slab.Higher)
            return $"the routing grid claims {slab.Wider} x {slab.Higher} cells but carries only " +
                   $"{slab.Bytes.Length} bytes";

        // **The grid has to cover the dig site, which is not the same as covering the map.** It is
        // sized to the encounter and carries its own origin tile - one Grand site read 121 x 121 on a
        // map 2300 grid across - so comparing its dimensions against the area's rejects a perfectly
        // good grid. Containment is the thing that actually matters and is structural: whether the
        // detonator's cell falls inside it, not what that cell happens to say.
        if (digging)
        {
            var (cx, cy) = slab.Cell(seat.X, seat.Y);

            if (cx < 0 || cy < 0 || cx >= slab.Wider || cy >= slab.Higher)
                return $"the detonator at ({seat.X}, {seat.Y}) converts to coarse cell " +
                       $"({cx}, {cy}), which is outside the {slab.Wider} x {slab.Higher} grid";
        }

        // A grid read from the wrong address is all zeroes far more often than it is anything else,
        // and a real one is mostly navigable. A proportion rather than a single byte, so that a site
        // which happens to be walled in one corner cannot fail it.
        var open = 0;

        for (var i = 0; i < slab.Bytes.Length; i++)
            if (slab.Bytes[i] != 0)
                open++;

        // **An absolute floor, because the share of the grid that is routable measures the MAP's size
        // and not whether the read worked.**
        //
        // The grid spans the whole map; a dig site occupies a small and roughly fixed part of it. So
        // the fraction falls as the map grows, and the test failed on a big one. Measured on two sites
        // that both plan perfectly well:
        //
        //   Scorched Cay   187 x 157 =  29,359 cells, 3,077 routable = 10.48%  - passed
        //   Port           271 x 223 =  60,433 cells, 3,001 routable =  4.97%  - REFUSED
        //
        // Nearly the same number of routable cells; Port is simply twice the area. A tester saw
        // "Offsets are broken" on an ordinary expedition because of it, and the old message claimed
        // 5% was a figure "a real grid never falls below" while holding a real grid that did.
        //
        // What the check is actually for is a read that produced nothing - a slab of zeroes, or a
        // pointer into the wrong place. That shows up as a handful of cells, not as three thousand, so
        // an absolute floor separates the two with an order of magnitude to spare and does not care how
        // large the map is.
        return open < Fewest
            ? $"only {open} of {slab.Bytes.Length} routing cells read as navigable, fewer than the " +
              $"{Fewest} a real grid has anywhere a dig site will fit"
            : null;
    }

    /// <summary>The largest a routing grid sanely gets, in coarse cells on a side.</summary>
    private const int Most = 4096;

    /// <summary>What percentage of a real routing grid is navigable, at the very lowest.</summary>
    /// <summary>
    /// The fewest routable coarse cells a real grid has, as an absolute count.
    ///
    /// Two hundred and fifty, against the three thousand both measured sites carry - twelve times the
    /// margin - and against the nought or handful a failed read gives. Replaced a 5% share, which was
    /// a measure of the map's area rather than of the read. See Wrong.
    /// </summary>
    private const int Fewest = 250;

    /// <summary>
    /// Whether the ground between two links is clear the whole way.
    ///
    /// A chain is a chain, and a link is not reachable just because both ends stand on good ground:
    /// put a rock between them and the game refuses, however placeable the destination is. Testing
    /// the endpoint alone is what let a plan open with an explosive that could not be placed.
    ///
    /// Walked a grid unit at a time and stopping at the first blocked cell, which is what keeps it
    /// affordable - a blocked segment usually fails within a few steps, and the search only pays
    /// the full length for links it is going to accept.
    ///
    /// Whether the game truly requires a clear line between links is NOT established. It is the
    /// obvious reading of an explosive being refused when both ends are fine, and it is what the
    /// original design called for, but nobody has watched the game accept a link through a wall.
    /// It rides on the same switch as the rest of the terrain check for that reason.
    /// </summary>
    public bool Clear(Vector2 from, Vector2 to) => Walk(from, to, null);

    /// <summary>
    /// The cells a segment passes through, in order, stopping at the first blocked one.
    ///
    /// Bresenham rather than "sample the line every so often and round". Sampling is the obvious
    /// way to write this and it is wrong in a way that is hard to see: the samples land on cell
    /// centres and the cells BETWEEN two samples are never asked about, so a wall one cell thick
    /// lying across the path can sit entirely in the gap. Raising the sample count does not fix it,
    /// because the grid has a resolution and once you are sampling every grid unit you are already
    /// testing every cell a sample can name - finer sampling just tests the same cells again.
    ///
    /// Where the line crosses a corner exactly, Bresenham moves diagonally and steps over the two
    /// cells either side of the corner. Both are tested, and the segment is only refused when BOTH
    /// are blocked, which is the same rule a body walking through a doorway gets: a diagonal gap
    /// between two walls is passable, a solid pair is not.
    ///
    /// <paramref name="note"/> is how the dump watches the same walk the planner does, so a link
    /// that fails can be read cell by cell instead of guessed at.
    /// </summary>
    private bool Walk(Vector2 from, Vector2 to, Action<Vector2, int> note)
    {
        var x = (int)MathF.Round(from.X);
        var y = (int)MathF.Round(from.Y);
        var toX = (int)MathF.Round(to.X);
        var toY = (int)MathF.Round(to.Y);

        var dx = Math.Abs(toX - x);
        var dy = Math.Abs(toY - y);
        var sx = x < toX ? 1 : -1;
        var sy = y < toY ? 1 : -1;
        var error = dx - dy;

        // A loop over a grid walk should not be able to run away, whatever the arithmetic does.
        for (var guard = dx + dy + 2; guard > 0; guard--)
        {
            var at = new Vector2(x, y);

            note?.Invoke(at, Value(at));

            if (!Placeable(at))
                return false;

            if (x == toX && y == toY)
                return true;

            var twice = error * 2;
            var alongX = twice > -dy;
            var alongY = twice < dx;

            // The corner case: the step is diagonal, so neither cell beside the corner is visited.
            // Blocked only when there is no way through at all.
            if (alongX && alongY)
            {
                var beside = new Vector2(x + sx, y);
                var other = new Vector2(x, y + sy);

                note?.Invoke(beside, Value(beside));
                note?.Invoke(other, Value(other));

                if (!Placeable(beside) && !Placeable(other))
                    return false;
            }

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

    /// <summary>The raw grid value at a cell, or -1 when it is off the grid.</summary>
    /// <summary>
    /// Whether the chain can pass over this cell, as opposed to be placed on it.
    ///
    /// A weaker test than Placeable, and deliberately: an explosive needs ground it can be built on,
    /// while the chain between two explosives only needs ground that is not solid. The pathfinding
    /// grid reads zero for a wall and anything above it for ground you can walk, so that is the
    /// line. See Route, which walks these cells to work out how long a link really is.
    /// </summary>
    /// <summary>How many rows the grid has, for anything sizing a buffer over the whole map.</summary>
    public int Height => _grid?.Length ?? 0;

    /// <summary>The width of the widest row, for the same reason.</summary>
    public int Width
    {
        get
        {
            var most = 0;

            foreach (var row in _grid ?? [])
                most = row != null && row.Length > most ? row.Length : most;

            return most;
        }
    }

    /// <summary>
    /// Whether the router may run a chain through this cell.
    ///
    /// **Non-zero means half a unit of room, not "passable ground".** The grid is a clearance field -
    /// see the class doc - so this lets the flood thread a link along a wall through a gap
    /// <see cref="Placeable"/> would refuse to stand a barrel in. Whether that is wrong depends on
    /// something not yet measured: a link between two explosives is not an object that has to fit
    /// anywhere, so it may legitimately pass where a barrel cannot.
    ///
    /// What is known is that it is the most permissive rule available and errs in one direction: of
    /// 814 refused cells it calls 130 passable, against 38 at four. A detour computed through gaps
    /// the game would not accept comes back shorter than the real one, which makes reach look longer
    /// and is consistent with plans that over-reach and have to be readjusted. The measurement that
    /// would settle it is the clamps, not another cutoff search. See Reach.Clamped and Route.Judge.
    /// </summary>
    public bool Walkable(int x, int y)
    {
        if (y < 0 || y >= _grid.Length)
            return false;

        var row = _grid[y];

        return row != null && x >= 0 && x < row.Length && row[x] != 0;
    }

    private int Value(Vector2 grid)
    {
        var y = (int)grid.Y;
        var x = (int)grid.X;

        if (y < 0 || y >= _grid.Length)
            return -1;

        var row = _grid[y];

        return row == null || x < 0 || x >= row.Length ? -1 : row[x];
    }

    /// <summary>
    /// The same walk the planner does, written out, for the dump.
    ///
    /// This exists because three dumps in a row said every planned spot reads 5 and the game
    /// refused one anyway. A spot is not a segment, and the segment is the thing that had never
    /// been looked at - so it prints what the walk actually saw rather than what the endpoints say.
    /// </summary>
    public string Trace(Vector2 from, Vector2 to)
    {
        var counts = new System.Collections.Generic.SortedDictionary<int, int>();
        var worst = new System.Collections.Generic.List<string>();

        var clear = Walk(from, to, (at, value) =>
        {
            counts[value] = counts.TryGetValue(value, out var had) ? had + 1 : 1;

            if (value <= 0 && worst.Count < 6)
                worst.Add($"({at.X:0},{at.Y:0})={value}");
        });

        var total = 0;

        foreach (var pair in counts)
            total += pair.Value;

        return $"{(clear ? "clear" : "BLOCKED")}, {total} cells [{Counts(counts)}]" +
               (worst.Count > 0 ? "  at " + string.Join(" ", worst) : "");
    }

    /// <summary>Whether an explosive may be placed on this grid cell.</summary>
    /// <summary>
    /// What both terrain grids say - under known-good ground, at the planned spots, and across the
    /// dig site as a whole.
    ///
    /// Two grids because one of them has already been ruled out. Every cell the targeting grid was
    /// asked about came back as 5: under the detonator, under all ninety markers, and at every spot
    /// a plan wanted to put an explosive, including one the game refuses. A number that is the same
    /// everywhere carries no information, and a predicate built on it cannot do anything but accept
    /// everything or reject everything.
    ///
    /// So the pathfinding grid is sampled beside it. Elsewhere in this HUD that one is read as
    /// zero for blocked and one to five for kinds of passable ground, which is at least a
    /// distinction - and the spread across the whole site is printed as well as the samples,
    /// because a grid that reads one value everywhere is the thing to rule out first, and a
    /// handful of samples cannot show that.
    ///
    /// **A third sample answers the question the first two cannot.** Known good is ground content
    /// stands on, which is not the same claim as ground an explosive may be placed on, and reading
    /// it as though it were is how the buildable threshold came to be argued from the wrong
    /// evidence. Game agreed is the cells Reached has a placement confirmation for. See Read.
    /// </summary>
    public static string Describe(GameController gc, System.Collections.Generic.List<Target> targets,
        Plan plan = null)
    {
        var targeting = Safe.Read(() => gc.IngameState.Data.RawTerrainTargetingData, null);
        var walking = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (targeting == null && walking == null)
            return "neither terrain grid is readable";

        var text = new System.Collections.Generic.List<string>();

        text.Add("  targeting: " + Read(targeting, gc, targets, plan));
        text.Add("  walkable:  " + Read(walking, gc, targets, plan));

        return Environment.NewLine + string.Join(Environment.NewLine, text);
    }

    /// <summary>One grid, sampled three ways.</summary>
    private static string Read(int[][] grid, GameController gc,
        System.Collections.Generic.List<Target> targets, Plan plan)
    {
        if (grid == null || grid.Length == 0)
            return "unreadable";

        int? At(Vector2 grid_)
        {
            var y = (int)grid_.Y;
            var x = (int)grid_.X;

            if (y < 0 || y >= grid.Length)
                return null;

            var row = grid[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        var known = new System.Collections.Generic.SortedDictionary<int, int>();

        void Note(Vector2 where)
        {
            if (At(where) is { } value)
                known[value] = known.TryGetValue(value, out var had) ? had + 1 : 1;
        }

        var site = Detonator.DetonatorGridPosition(gc);

        Note(site);

        foreach (var target in targets)
            Note(target.Grid);

        // Everything in a box around the dig site, to show whether the grid varies at all.
        var spread = new System.Collections.Generic.SortedDictionary<int, int>();

        for (var dy = -80; dy <= 80; dy += 2)
        {
            for (var dx = -80; dx <= 80; dx += 2)
            {
                if (At(new Vector2(site.X + dx, site.Y + dy)) is { } value)
                    spread[value] = spread.TryGetValue(value, out var had) ? had + 1 : 1;
            }
        }

        // **Where the game itself agreed an explosive could go**, which known good above is not.
        // That one is the detonator and every marker - ground content STANDS on - and standing
        // somewhere is not permission to build there, so it cannot say where the buildable
        // threshold belongs. These cells come from Reached: the placement indicator was moved over
        // each one and came back placeable. If none of them ever reads below Buildable, the flood's
        // "anything but zero" test is routing over ground the game refuses and should be tightened
        // to match Placeable; if a fair number do, the two thresholds differ for a real reason and
        // the model's fault is elsewhere.
        var agreed = new System.Collections.Generic.SortedDictionary<int, int>();
        var counted = new System.Collections.Generic.HashSet<(int X, int Y)>();

        // **Nothing to compare against any more.** This counted the grid's values at the cells the
        // placement indicator had confirmed, which was the only placement evidence kept. Those
        // readings are gone - see Planning.Placeable - so the comparison has no left-hand side.

        var below = 0;

        foreach (var pair in agreed)
        {
            if (pair.Key < Buildable)
                below += pair.Value;
        }

        var planned = new System.Collections.Generic.List<string>();

        foreach (var point in plan?.Points ?? new System.Collections.Generic.List<Vector2>())
            planned.Add($"({point.X:0},{point.Y:0})={(At(point)?.ToString() ?? "off grid")}");

        return $"known good [{Counts(known)}]   over the site [{Counts(spread)}]" +
               (counted.Count > 0
                   ? $"   game agreed [{Counts(agreed)}] - {below} of {counted.Count} " +
                     $"below {Buildable}"
                   : "   game agreed nothing yet - sweep the cursor with Confirmed reach on") +
               (planned.Count > 0 ? "   planned " + string.Join(" ", planned) : "");
    }

    /// <summary>
    /// The confirmed-placeable cells the pathfinding grid would refuse, and the ground around each.
    ///
    /// **This exists to separate two explanations of the same number.** The first sweep of this
    /// measurement said 54 of 289 cells the game agreed to read below 4, and 14 of them read 0 -
    /// which would mean the pathfinding grid is not what the game consults, and that Placeable is
    /// too strict rather than too loose. But the sweep was deliberately made AROUND A ROCK, so the
    /// sample is packed with cells that sit right against blocked ground, and an off-by-one in how a
    /// fractional position is turned into a cell index lands exactly there. Both stories predict a
    /// pile of low readings; only one predicts them isolated.
    ///
    /// So each low cell is printed with its 3x3 neighbourhood and its position to two decimals:
    ///
    /// - if a 4-or-better cell is next door, the reading is an indexing artefact and the fix is
    ///   how this HUD converts a position to a cell, not the threshold.
    /// - if the whole 3x3 is below 4, the grid genuinely is not the rule the game applies, and
    ///   both Placeable and the flood are refusing ground the game allows.
    ///
    /// The rounded histogram is printed beside the truncated one for the same reason: if rounding
    /// makes the low readings disappear, that is the answer on its own.
    /// </summary>
    public static string Agreed(GameController gc,
        System.Collections.Generic.List<Target> targets)
    {
        var grid = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (grid == null || grid.Length == 0)
            return "pathfinding grid unreadable";

        var aiming = Safe.Read(() => gc.IngameState.Data.RawTerrainTargetingData, null);

        static int? Aim(int[][] of, int x, int y)
        {
            if (y < 0 || y >= of.Length)
                return null;

            var row = of[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        int? At(int x, int y)
        {
            if (y < 0 || y >= grid.Length)
                return null;

            var row = grid[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        var seen = new System.Collections.Generic.HashSet<(int X, int Y)>();
        var rounded = new System.Collections.Generic.SortedDictionary<int, int>();
        var lines = new System.Collections.Generic.List<string>();

        var total = 0;
        var low = 0;
        var beside = 0;
        var isolated = 0;

        foreach (var to in System.Array.Empty<Vector2>())
        {
            var cx = (int)to.X;
            var cy = (int)to.Y;

            if (!seen.Add((cx, cy)))
                continue;

            total++;

            if (At((int)MathF.Round(to.X), (int)MathF.Round(to.Y)) is { } near)
                rounded[near] = rounded.TryGetValue(near, out var had) ? had + 1 : 1;

            if (At(cx, cy) is not { } value || value >= Buildable)
                continue;

            low++;

            var best = -1;
            var box = new System.Collections.Generic.List<string>();

            for (var dy = -1; dy <= 1; dy++)
            {
                var row = new System.Collections.Generic.List<string>();

                for (var dx = -1; dx <= 1; dx++)
                {
                    var at = At(cx + dx, cy + dy);

                    row.Add(at?.ToString() ?? "-");

                    if (at is { } one && one > best)
                        best = one;
                }

                box.Add(string.Join(" ", row));
            }

            if (best >= Buildable)
                beside++;
            else
                isolated++;

            // How far the nearest marker is, because that is what the story to beat predicts.
            // **A remnant makes ground you cannot walk on and can still place an explosive on**, so
            // if the low readings are remnant holes they sit close to a marker and the targeting
            // grid - which describes the ground rather than what stands on it - still reads high.
            // Both are printed, so the prediction either holds cell by cell or it does not.
            var closest = float.MaxValue;

            foreach (var mark in targets ?? new System.Collections.Generic.List<Target>())
            {
                var gap = Vector2.Distance(mark.Grid, new Vector2(cx, cy));

                if (gap < closest)
                    closest = gap;
            }

            // Capped, because a long sweep can confirm thousands and the point is made by forty.
            if (lines.Count < 40)
            {
                lines.Add($"    ({cx},{cy}) walkable {value}, targeting " +
                          $"{(aiming == null ? "?" : Aim(aiming, cx, cy)?.ToString() ?? "off grid")}, " +
                          $"nearest marker " +
                          $"{(closest == float.MaxValue ? "none" : closest.ToString("0.0"))}, " +
                          $"3x3 [{string.Join(" / ", box)}]");
            }
        }

        if (total == 0)
            return "nothing confirmed yet - sweep the cursor with the confirmed-reach layer on";

        var head = $"{low} of {total} confirmed-placeable cells read below {Buildable} when " +
                   $"truncated; rounded instead [{Counts(rounded)}]";

        if (low == 0)
            return head;

        head += Environment.NewLine +
                $"    of those {low}: {beside} have a {Buildable}-or-better cell in the 3x3 " +
                $"(an INDEXING fault), {isolated} do not (the GRID is not the rule)";

        return head + Environment.NewLine + string.Join(Environment.NewLine, lines) +
               (low > lines.Count ? Environment.NewLine + $"    ... and {low - lines.Count} more" : "");
    }

    /// <summary>
    /// Which offset and threshold, if any, make the pathfinding grid agree with the game.
    ///
    /// **Two sets of verdicts from the game itself are now on file and they cannot both be explained
    /// by a threshold.** The game refused a continuous column at x=1213 and accepted one at x=1214
    /// where our grid reads 0 for both, which no cutoff can reproduce; and it refused a row at y=661
    /// reading 2 while accepting the row at y=662 reading 3, which says the cutoff is 3 rather than
    /// the 4 Placeable uses. A mis-registered grid and a wrong constant look alike from one example,
    /// so both are searched at once instead of being argued about.
    ///
    /// For every small shift of the grid and every cutoff, this counts how often the grid agrees:
    /// a refused cell should read BELOW the cutoff and a confirmed cell AT OR ABOVE it. Whatever
    /// row comes out on top is the registration and threshold the evidence supports; if nothing gets
    /// near agreement at any shift, the pathfinding grid is not the rule the game applies for
    /// placement and the search has to move to the targeting grid or to something else entirely.
    ///
    /// The unshifted rows are printed whatever they score, because the question is whether a shift
    /// buys anything, and that cannot be read off a ranked list that may not contain the baseline.
    /// </summary>
    public static string Aligns(GameController gc)
    {
        // **Settled, not Reached and Refused.** Those two are what the first run of this used and
        // they cannot answer it: one holds clamped positions and the other holds refusals that may
        // be about range or separation rather than ground. See Settled, which gates on the entity
        // and the cursor naming one cell and so carries both verdicts about ground alone.
        var yes = new System.Collections.Generic.HashSet<(int X, int Y)>();
        var no = new System.Collections.Generic.HashSet<(int X, int Y)>();

        foreach (var (cell, placeable, explained, _, _) in Settled.Here.Verdicts)
        {
            if (placeable)
                yes.Add(cell);
            else if (!explained)
                no.Add(cell);
        }

        if (yes.Count == 0 || no.Count == 0)
        {
            return $"cannot align: {yes.Count} placeable and {no.Count} refused cells settled, " +
                   "and both are needed - sweep the cursor over the edge of some bad ground";
        }

        // **Both grids, because the reason pathfinding fails is now understood.** A remnant makes a
        // small patch of ground you cannot WALK through and can still put an explosive on, so the
        // pathfinding grid carries holes that have nothing to do with placement - which is exactly
        // the shape of its failure here, refusals and acceptances mixed at every cutoff. The
        // targeting grid describes the ground rather than what is standing on it, and it has read 5
        // for every single confirmed-placeable cell across four sweeps without one exception. It was
        // set aside early for being near-uniform over a dig site; most of a dig site IS placeable,
        // so uniformity is what a correct predicate looks like from that angle, not a fault.
        return Aligning(Safe.Read(() => gc.IngameState.Data.RawTerrainTargetingData, null),
                   "targeting", yes, no) +
               Environment.NewLine + "  " +
               Aligning(Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null),
                   "walkable", yes, no);
    }

    /// <summary>One grid against one pair of evidence sets. See Aligns.</summary>
    private static string Aligning(int[][] grid, string name,
        System.Collections.Generic.HashSet<(int X, int Y)> yes,
        System.Collections.Generic.HashSet<(int X, int Y)> no)
    {
        if (grid == null || grid.Length == 0)
            return $"{name} grid unreadable";

        int? At(int x, int y)
        {
            if (y < 0 || y >= grid.Length)
                return null;

            var row = grid[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        var rows = new System.Collections.Generic.List<(double Share, string Text, bool Plain)>();

        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                for (var cut = 1; cut <= 5; cut++)
                {
                    var right = 0;
                    var wrongYes = 0;
                    var wrongNo = 0;

                    foreach (var cell in yes)
                    {
                        if (At(cell.X + dx, cell.Y + dy) is { } value && value >= cut)
                            right++;
                        else
                            wrongYes++;
                    }

                    foreach (var cell in no)
                    {
                        if (At(cell.X + dx, cell.Y + dy) is { } value && value >= cut)
                            wrongNo++;
                        else
                            right++;
                    }

                    var share = right / (double)(yes.Count + no.Count);

                    rows.Add((share,
                        $"    dx {dx,2} dy {dy,2} cut {cut} -> {share * 100d:0.0}% " +
                        $"({wrongYes} accepted cells read below it, {wrongNo} refused read at or above)",
                        dx == 0 && dy == 0));
                }
            }
        }

        rows.Sort((a, b) => b.Share.CompareTo(a.Share));

        // **The percentages are unreadable without this.** The settled set is mostly placeable, so a
        // predicate that simply says yes to everything already scores the majority share - and the
        // first run of this table topped out at 85.1% against a baseline of 81.4%, which looks like
        // a result and is three points of nothing. Any row that does not clear this by a wide margin
        // is saying the grid carries no information about refusal.
        var trivial = Math.Max(yes.Count, no.Count) / (double)(yes.Count + no.Count);

        var text = new System.Collections.Generic.List<string>
        {
            $"{name}: {yes.Count} placeable and {no.Count} unexplained refused settled cells, " +
            $"always-guess baseline {trivial * 100d:0.0}%"
        };

        for (var i = 0; i < rows.Count && i < 6; i++)
            text.Add(rows[i].Text);

        text.Add("    - unshifted, for comparison -");

        foreach (var row in rows)
        {
            if (row.Plain)
                text.Add(row.Text);
        }

        return string.Join(Environment.NewLine, text);
    }

    /// <summary>
    /// The refusals neither grid explains, each with everything that might.
    ///
    /// **This is the other half of Agreed, and the half the information is now in.** Agreed asks why
    /// the game ACCEPTS ground the grids call bad, and that is answered: the pathfinding grid counts
    /// what is standing on the ground as well as the ground, so a remnant's no-walk patch is a hole
    /// with nothing to do with placement - 42 of 42 such cells read 5 on the targeting grid, the whole
    /// solid-zero column included.
    ///
    /// What is left is the mirror image. The targeting grid reads 5 for every cell the game accepted
    /// AND every cell it refused, so it is a necessary condition that never wrongly forbids - the
    /// right shape for the flood, since a spot wrongly forbidden is content quietly dropped from every
    /// plan - and it cannot account for a single refusal. Those refusals are real: the cursor and the
    /// entity agreed on the cell, and nothing about the chain's spacing covered one of them.
    ///
    /// So everything that could be the cause is printed per cell: how far the nearest marker is and
    /// what kind it is, because an explosive may not go on top of content; and how far the spot is
    /// from where the chain is throwing from against the reach we predict, because range is the other
    /// candidate and a clamp that lands on the cursor's own cell would not have been filtered out.
    /// </summary>
    public static string Puzzling(GameController gc,
        System.Collections.Generic.List<Target> targets)
    {
        var walking = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);
        var aiming = Safe.Read(() => gc.IngameState.Data.RawTerrainTargetingData, null);

        static int? Value(int[][] of, int x, int y)
        {
            if (of == null || y < 0 || y >= of.Length)
                return null;

            var row = of[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        var origin = Detonator.LastExplosiveGridPosition(gc);
        var reach = Detonator.PlacementRange(gc);

        var lines = new System.Collections.Generic.List<string>();
        var total = 0;
        var onContent = 0;
        var atRange = 0;

        foreach (var (cell, placeable, explained, looks, _) in Settled.Here.Verdicts)
        {
            if (placeable || explained)
                continue;

            total++;

            var spot = new Vector2(cell.X, cell.Y);

            var closest = float.MaxValue;
            var kind = "none";

            foreach (var mark in targets ?? new System.Collections.Generic.List<Target>())
            {
                var gap = Vector2.Distance(mark.Grid, spot);

                if (gap >= closest)
                    continue;

                closest = gap;
                kind = mark.Kind.ToString();
            }

            var away = origin == Vector2.Zero ? -1f : Vector2.Distance(origin, spot);

            // Two cheap tallies, so the answer does not have to be read off forty lines by eye.
            if (closest <= 3f)
                onContent++;

            if (reach > 0f && away >= reach - 2f)
                atRange++;

            if (lines.Count < 40)
            {
                lines.Add($"    ({cell.X},{cell.Y}) walkable " +
                          $"{Value(walking, cell.X, cell.Y)?.ToString() ?? "?"}, targeting " +
                          $"{Value(aiming, cell.X, cell.Y)?.ToString() ?? "?"}, nearest " +
                          $"{(closest == float.MaxValue ? "none" : $"{kind} at {closest:0.0}")}, " +
                          $"{away:0.0} from the origin of a range of {reach:0.0}, " +
                          $"refused on all {looks} looks");
            }
        }

        if (total == 0)
            return "no unexplained refusals - nothing to account for";

        return $"{total} refusals neither grid explains: {onContent} sit within 3 of a marker, " +
               $"{atRange} are at or beyond the range less two" + Environment.NewLine +
               string.Join(Environment.NewLine, lines) +
               (total > lines.Count
                   ? Environment.NewLine + $"    ... and {total - lines.Count} more"
                   : "");
    }

    /// <summary>
    /// Whether the grid's numbers are CLEARANCE rather than a kind of ground.
    ///
    /// **The whole threshold argument rests on this and it was never checked.** Four sweeps were
    /// spent asking which cutoff of 0-5 best predicts placement, on the assumption that the number
    /// classifies the ground. Two things say it does not. The counts of 1, 2, 3 and 4 over a dig site
    /// come out nearly equal - 138, 147, 109, 134 - which is what one-cell bands wrapped around
    /// obstacles look like and is no property of a classification. And every neighbourhood near a
    /// blocker is a pure ramp perpendicular to it, rising by exactly one per cell and constant along
    /// the wall.
    ///
    /// If it is a distance transform then "an explosive needs 4 or better" means it needs four cells
    /// of CLEARANCE, which is a sensible rule for a thing with a footprint - and the flood's "anything
    /// but zero" means the chain may hug a wall through a gap the placement rule itself calls too
    /// tight. That would make every detour come back shorter than the real one, which is exactly the
    /// complaint that started this: plans that reach too far and have to be readjusted.
    ///
    /// So each cell of value 1 to 4 is measured against the real distance to the nearest zero, three
    /// ways, because which metric fits says how the field was built. A near-perfect match on one of
    /// them settles it; a scatter says the number means something else and the cutoff search was the
    /// right question after all.
    /// </summary>
    public static string Clearance(GameController gc)
    {
        var grid = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (grid == null || grid.Length == 0)
            return "pathfinding grid unreadable";

        int? At(int x, int y)
        {
            if (y < 0 || y >= grid.Length)
                return null;

            var row = grid[y];

            return row == null || x < 0 || x >= row.Length ? null : row[x];
        }

        var site = Detonator.DetonatorGridPosition(gc);
        var ox = (int)site.X;
        var oy = (int)site.Y;

        // Per metric: how many cells of each value had that value as their measured distance.
        var fits = new int[5];
        var seen = 0;
        var byValue = new int[6];
        var hitByValue = new int[6];
        var wrong = new System.Collections.Generic.List<string>();

        for (var y = oy - Reachable; y <= oy + Reachable; y++)
        {
            for (var x = ox - Reachable; x <= ox + Reachable; x++)
            {
                if (At(x, y) is not { } value || value < 1 || value > 4)
                    continue;

                seen++;
                byValue[value]++;

                // The nearest blocked cell, searched out only as far as the value could mean.
                var chebyshev = int.MaxValue;
                var manhattan = int.MaxValue;
                var euclid = double.MaxValue;

                for (var dy = -Sunk; dy <= Sunk; dy++)
                {
                    for (var dx = -Sunk; dx <= Sunk; dx++)
                    {
                        if (At(x + dx, y + dy) is not { } near || near != 0)
                            continue;

                        var ax = Math.Abs(dx);
                        var ay = Math.Abs(dy);

                        chebyshev = Math.Min(chebyshev, Math.Max(ax, ay));
                        manhattan = Math.Min(manhattan, ax + ay);
                        euclid = Math.Min(euclid, Math.Sqrt(dx * dx + dy * dy));
                    }
                }

                if (chebyshev == value)
                    fits[0]++;

                if (manhattan == value)
                    fits[1]++;

                // Every rounding, because the first pass guessed one and the misses were all off by
                // exactly the amount that guess throws away. Floor says "at least this much room";
                // ceiling says "the nearest thing is no further than this", and they differ only
                // where the distance is not a whole number - which is every diagonal.
                if (euclid != double.MaxValue && (int)Math.Floor(euclid) == value)
                    fits[2]++;

                if (euclid != double.MaxValue && (int)Math.Ceiling(euclid) == value)
                {
                    fits[3]++;
                    hitByValue[value]++;
                }

                if (euclid != double.MaxValue && (int)Math.Round(euclid) == value)
                    fits[4]++;

                if (euclid != double.MaxValue && (int)Math.Ceiling(euclid) != value && wrong.Count < 12)
                {
                    wrong.Add($"    ({x},{y}) reads {value}, nearest blocked is " +
                              $"{(chebyshev == int.MaxValue ? "none within " + Look : chebyshev.ToString())} " +
                              $"by chebyshev, {(manhattan == int.MaxValue ? "-" : manhattan.ToString())} " +
                              $"by manhattan, " +
                              $"{(euclid == double.MaxValue ? "-" : euclid.ToString("0.00"))} straight");
                }
            }
        }

        if (seen == 0)
            return "no cells of value 1 to 4 near the site, so clearance cannot be tested";

        var each = new System.Collections.Generic.List<string>();

        for (var v = 1; v <= 4; v++)
        {
            if (byValue[v] > 0)
                each.Add($"{v}:{hitByValue[v] * 100d / byValue[v]:0}% of {byValue[v]}");
        }

        var text = $"clearance test over {seen} cells valued 1-4 around the site: " +
                   $"chebyshev {fits[0] * 100d / seen:0.0}%, " +
                   $"manhattan {fits[1] * 100d / seen:0.0}%, " +
                   $"straight-line floor {fits[2] * 100d / seen:0.0}%, " +
                   $"CEILING {fits[3] * 100d / seen:0.0}%, " +
                   $"round {fits[4] * 100d / seen:0.0}%" + Environment.NewLine +
                   $"    ceiling per value - {string.Join("  ", each)}";

        return wrong.Count == 0
            ? text
            : text + Environment.NewLine + string.Join(Environment.NewLine, wrong);
    }

    /// <summary>How far around the site the clearance test looks, in grid. See Clearance.</summary>
    private const int Reachable = 80;

    /// <summary>How far a cell may look for a blocked neighbour. See Clearance.</summary>
    private const int Look = 6;

    private static string Counts(System.Collections.Generic.SortedDictionary<int, int> counts)
    {
        var text = new System.Collections.Generic.List<string>();

        foreach (var pair in counts)
            text.Add($"{pair.Key}:{pair.Value}");

        return counts.Count == 0 ? "nothing" : string.Join(" ", text);
    }

    /// <summary>
    /// Whether an explosive may be placed on this cell.
    ///
    /// **Any room at all, not four grid units of it.** See the class doc: the grid holds the rounded
    /// distance to the nearest blocked cell, so this is a footprint rule and not a choice between
    /// kinds of ground - and the footprint turns out to be nearly nothing.
    ///
    /// **It was four, and four was wrong by a wide margin.** Four is where instantsc's PoE 1 planner
    /// draws it and it scored well on the first sample, which is how it survived. Two later and much
    /// larger samples agree it is far too strict:
    ///
    /// | endpoint test | wrongly refuses placeable | correctly refuses refused |
    /// | --- | --- | --- |
    /// | `>= 4` (was) | 346 of 3,158 &#183; **41 of 68** | 518 of 685 &#183; 207 of 250 |
    /// | `!= 0` (now) | 120 of 3,158 &#183; **9 of 68** | 366 of 685 &#183; 145 of 250 |
    ///
    /// The second figure in each cell is the swept capture the clamp rule was fitted from, where four
    /// threw away **60% of the ground the game had just agreed to**.
    ///
    /// **The two errors do not cost the same, which is what decides it.** Four catches more true
    /// refusals, and it pays for them with placeable ground the planner then never considers - a
    /// permanent loss, since nothing ever re-offers a cell the terrain rule rejected. A false
    /// acceptance is temporary: the game refuses it, <see cref="Refused"/> writes it down, and the
    /// next plan goes round it. So the test should err towards letting ground through.
    ///
    /// **Three of the accepted cells read 0, and those three were the whole answer.** No threshold on
    /// this grid can be sufficient, because the rule is not about the cell at all: an explosive may
    /// stand INSIDE solid ground, as long as open ground is within <see cref="Sinkable"/> of it. So
    /// this asks two questions - is the cell open, and if not, is it shallow - and the second is a
    /// measured distance rather than a lookup, because the clearance transform only runs outward from
    /// solid cells and says nothing inside them.
    ///
    /// That is a dilation of the open set, which is exactly the shape no per-cell layer can express,
    /// and it is why five terrain grids, the height map, the tile names and the entity list all read
    /// identically across a frontier the game enforced perfectly. Applied to readings that already
    /// pass reach and spacing it explains 192 refusals those two miss, at zero false refusals.
    ///
    /// Necessary, still not sufficient - 1,433 refusals remain unexplained by reach, spacing and
    /// depth together. <see cref="Refused"/> is the backstop in the other direction: whatever this
    /// rule gets wrong, the game's own refusal is recorded and the next plan goes round it.
    /// </summary>
    public bool Placeable(Vector2 grid)
    {
        var y = (int)grid.Y;
        var x = (int)grid.X;

        // **An authored refusal beats every grid, because the routine tests it first.** See Forbids.
        if (_volumes.Forbids(x, y))
            return false;

        // **The game's own grid, when we have it.** Everything below this line is a rule fitted to
        // observed verdicts; this is the array the game reads. Measured over 12,014 corroborated
        // readings at one dig site: not one cell the game accepted had a zero byte.
        if (_slab != null)
            return _slab.Routable(x, y);

        if (Open(x, y))
            return true;

        // Off the map is not shallow solid ground, it is nothing. Ask before the scan, so a cell
        // outside the arrays cannot be rescued by open ground that wraps round the edge of a row.
        if (y < 0 || y >= _grid.Length)
            return false;

        var row = _grid[y];

        if (row == null || x < 0 || x >= row.Length)
            return false;

        return _shallow.GetOrAdd((x, y), static (key, self) => self.Shallow(key.X, key.Y), this);
    }

    /// <summary>Whether this cell is open ground - not a solid cell. See Placeable.</summary>
    private bool Open(int x, int y)
    {
        if (y < 0 || y >= _grid.Length)
            return false;

        var row = _grid[y];

        return row != null && x >= 0 && x < row.Length && row[x] >= Buildable;
    }

    /// <summary>
    /// Whether this solid cell has open ground within Sinkable of it.
    ///
    /// A box scan rejected to a disc, rather than rings outward with an early exit. The box is 13
    /// cells on a side at the current constant, so the worst case is 169 array reads for an answer
    /// that is then cached forever; the ring version is faster on the cells that pass and slower on
    /// the ones that fail, and the ones that fail are the ones the planner asks about repeatedly.
    /// </summary>
    private bool Shallow(int x, int y)
    {
        for (var dy = -Sunk; dy <= Sunk; dy++)
        {
            for (var dx = -Sunk; dx <= Sunk; dx++)
            {
                if (dx * dx + dy * dy > Sinkable)
                    continue;

                if (Open(x + dx, y + dy))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The least room anywhere along a straight run, and whether it is blocked at all.
    ///
    /// **This is what separates the two live explanations of an over-reaching model.** A clamp says
    /// the game stopped the chain shorter than predicted; it does not say why. If the reach model is
    /// generous by the same amount whether the line to the spot is wide open or scraping past a rock,
    /// the fault is in the base figure - the diagonal charged as 14 tenths instead of 14.142, say -
    /// and has nothing to do with the ground. If it is generous only where the run is tight, the
    /// chain needs room and Walkable's half a unit is too permissive.
    ///
    /// Reported as the minimum clearance rather than a pass or fail, because the grid now has a
    /// physical reading and the question is how much room, not whether any. See the class doc.
    /// </summary>
    /// <returns>The lowest value on the line, and -1 when neither end can be read.</returns>
    public int Tightest(Vector2 from, Vector2 to)
    {
        var span = Vector2.Distance(from, to);

        if (span <= 0f)
            return Value(from);

        var steps = (int)MathF.Ceiling(span);
        var least = int.MaxValue;

        for (var i = 0; i <= steps; i++)
        {
            var at = Value(Vector2.Lerp(from, to, i / (float)steps));

            if (at < 0)
                continue;

            if (at < least)
                least = at;
        }

        return least == int.MaxValue ? -1 : least;
    }

    /// <summary>How much room this cell has, in grid units, capped at five. See the class doc.</summary>
    public int Room(Vector2 grid) => Value(grid);

    /// <summary>
    /// The smallest pathfinding value that counts as open ground. See Placeable for the working.
    ///
    /// One, so the rule is "not a blocked cell". Was four; that refused 60% of the ground the game
    /// accepted in the capture the clamp rule was fitted from.
    /// </summary>
    private const byte Buildable = 1;

    /// <summary>
    /// How far into solid ground an explosive may stand, as SQUARED grid units.
    ///
    /// **The rule is a dilation of the open set, not a test on the cell**, which is why every
    /// per-cell layer the client publishes failed to explain it. Measured over 9,322 corroborated
    /// readings: no accepted cell anywhere sits deeper than sqrt(40) = 6.3246, and 497 consecutive
    /// cells deeper than that were refused without exception. The smallest refused depth above the
    /// deepest accepted one is sqrt(41) = 6.4031, so the constant is pinned to [6.325, 6.403) -
    /// about 69 world units.
    ///
    /// **Squared, and an integer, because that is how the game states its distances.** The separation
    /// minimum was pinned to `d^2 > 400` from four hand-placed probes - 400 refuses, 401 accepts - so
    /// the client compares squared integer distances rather than lengths. Read the same way, this
    /// rule is exactly `d^2 &lt;= 40`: sqrt(40) is the deepest accepted cell and sqrt(41) the
    /// shallowest refused one, with nothing between because no lattice point lies there.
    ///
    /// Writing it as 6.36 and squaring at the comparison said the same thing about every cell, since
    /// every cell-to-cell squared distance is an integer - but it read as a fitted decimal when it is
    /// a round number in the game's own units.
    ///
    /// Euclidean is measured, not assumed. Fitted against octile, chebyshev and manhattan on the
    /// same readings, it explains the most refusals (497 against 474, 468, 387) and leaves a gap to
    /// the next refusal an order of magnitude tighter (0.079 against 0.172, 1.0, 1.0) - a correct
    /// metric separates the data closely, a wrong one only by luck. See NOTES section 1a.
    /// </summary>
    private const int Sinkable = 40;

    /// <summary>How far Shallow reaches, in whole cells - the ceiling of sqrt(Sinkable).</summary>
    private const int Sunk = 7;
}
