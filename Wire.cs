using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The game's own explosive routing, translated from `0x141F5A6B0`.
///
/// **This is not a model of the rule, it is the routine.** Every step below is the disassembly in
/// order - see NOTES 1d. Placement runs A* from the last explosive to the requested point on the
/// coarse routing grid, string-pulls the result with the engine's line test, then walks the polyline
/// accumulating length and CUTS it at the reach. The indicator is wherever that cut lands.
///
/// **The clamp relocates, it does not refuse.** The truncation happens and validity is still set to
/// 1, so aiming past the routed reach places the explosive elsewhere, at a valid but different spot.
/// That is why a straight-line reach test is wrong for both the overlay and the planner: it says yes
/// to a spot where the bomb will not actually land.
///
/// **Checked against a chain the game refused.** The player could not place the fourth explosive of
/// a planned chain; this routine costs that link 104.19 against a reach of 90.51, and passes the
/// other three. That is the evidence it rests on - one verdict from the game, not a score over a
/// sample. The offline samples cannot settle it, because a reading only enters them when the
/// indicator landed on the cursor cell, and the game clamps the indicator short precisely when the
/// wire is too long: the failing case removes itself from the data.
///
/// One real fix did come out of the offline work: cells outside a captured box must not count as
/// blocked, or every routed length passing near an edge is inflated.
/// </summary>
internal sealed class Wire
{
    /// <summary>
    /// The grid the engine's line test reads - `0x141D0D220` layer 0, which is `terrain + 0xD0`.
    ///
    /// **This is RawPathfindingData, and the detour above it is worth reading before changing it.**
    /// The offset arithmetic is plain in the disassembly and points at GridWalkableData. It was then
    /// switched to the targeting grid on the strength of an offline score - 3,565 accepted pairs,
    /// none over the reach, against 25 for this grid - and that score was worthless: the targeting
    /// grid reads 5 almost everywhere (5,864 of 6,561 cells on the site it was measured on), so the
    /// line test never blocks, the router degenerates to straight-line distance, and it was scoring
    /// the very test it was meant to improve on.
    ///
    /// **Settled against the game instead.** On a planned chain the game refused, the link the
    /// player could not place costs 104.19 against a reach of 90.51 through this grid, and comes
    /// back a clear straight line through the targeting one. A measurement that cannot fail is not
    /// a measurement - prefer one real in-game verdict to a clean offline number.
    /// </summary>
    private readonly int[][] _aim;

    private readonly Peek.Slab _slab;

    /// <summary>Routed lengths already worked out, since a solve asks about the same pairs.</summary>
    private readonly ConcurrentDictionary<(int, int, int, int), float> _known = new();

    public Wire(int[][] aim, Peek.Slab slab)
    {
        _aim = aim;
        _slab = slab;
    }

    /// <summary>Whether the client published enough for the routine to run at all.</summary>
    public bool Ready => _aim != null && _aim.Length > 0 && _slab != null;

    /// <summary>How many pairs have been routed, for the dump to report.</summary>
    public int Known => _known.Count;

    /// <summary>Every pair whose routed length is held, as integer cells. For a layout snapshot to replay. See Layout.</summary>
    internal IEnumerable<(int FromX, int FromY, int ToX, int ToY)> KnownPairs => _known.Keys;

    /// <summary>
    /// How many routed paths are held, which is the larger of the two caches and the one a cap has to count.
    ///
    /// Every miss adds one, whether or not a length is also asked for. Measured offline with these types: about
    /// 155 bytes a path of two or three points and 187 at five, against 59 for a cached length - so the paths are
    /// most of what the router holds. See MostPaths.
    /// </summary>
    public int Paths => Volatile.Read(ref _paths);

    /// <summary>How many paths are stored, counted as they are added. ConcurrentDictionary.Count locks every bucket.</summary>
    private int _paths;

    /// <summary>Whether the router has stopped storing new routes. See MostPaths.</summary>
    public bool Full => Volatile.Read(ref _paths) >= MostPaths;

    /// <summary>
    /// How many routed paths a router stores before it stops adding and only answers from what it holds.
    ///
    /// **A safety limit, not a working cap.** A router is kept across solves on one dig site and dropped when the
    /// site changes, so it grows only with the distinct routes one site asks about. A path costs about 155 bytes
    /// and its length another 59, measured offline, so four million is about 900MB - far above the 1.47 million
    /// one solve on a twenty explosive Grand site built, and there to protect the game's process if a site ever
    /// behaves unexpectedly. Benchmarked against the alternatives (8 threads, six million lookups): stopping when
    /// full cost 90ns a lookup against 99 uncapped, where an exact least-recently-used order cost 554 through its
    /// lock and two generations 166 with a lower hit rate. When full, the routes kept are the first asked for,
    /// which are also the ones asked for most. Chosen, not measured in play; the dump reports where sites settle.
    /// </summary>
    internal const int MostPaths = 4_000_000;

    /// <summary>
    /// Roughly how much the two caches hold, in bytes, from the per-entry costs measured offline. An estimate, for
    /// the dump: 59 a length, 155 a path of up to four points and 16 a point beyond.
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = 59L * _known.Count;

            foreach (var path in _bent.Values)
                bytes += 155L + 16L * Math.Max(0, (path?.Count ?? 0) - 4);

            return bytes;
        }
    }

    /// <summary>
    /// How long the wire runs between two points, in grid units, or infinity when no route exists.
    /// This is the quantity the game compares against the reach.
    /// </summary>
    public float Length(Vector2 from, Vector2 to)
    {
        var ax = (int)MathF.Round(from.X);
        var ay = (int)MathF.Round(from.Y);
        var bx = (int)MathF.Round(to.X);
        var by = (int)MathF.Round(to.Y);

        var pair = (ax, ay, bx, by);

        if (_known.TryGetValue(pair, out var had))
            return had;

        var length = Measure(pair);

        // Kept only while there is room. Past the limit it is still worked out and answered, just not stored.
        // See MostPaths.
        if (!Full)
            _known.TryAdd(pair, length);

        return length;
    }

    /// <summary>
    /// Where the explosive actually ends up when you aim at a point - the clamped end of the wire.
    ///
    /// **This is the question, and "is the length within the reach" is not.** The clamp relocates
    /// rather than refuses, and the indicator it produces is an integer grid point, so whether you
    /// get a bomb ON a spot is whether the clamped end ROUNDS BACK ONTO that spot - not whether the
    /// wire was short enough to arrive.
    ///
    /// The two come apart exactly where the ground does. Over open ground the wire IS the straight
    /// line, so a spot half a unit past the budget clamps half a unit back along that line and still
    /// rounds onto the spot asked for - which is why probing in the open reads the reach as 90.5
    /// when the budget is 90. Round an obstacle the wire doubles back, so the same half unit of
    /// overrun puts the landing several grid away and the bomb arrives nowhere near the spot.
    ///
    /// One rule, both behaviours, and no constant to tune.
    /// </summary>
    public Vector2 Landing(Vector2 from, Vector2 to, float reach)
    {
        var polyline = Bend(from, to);

        if (polyline == null || polyline.Count == 0)
            return from;

        var run = 0f;

        for (var i = 1; i < polyline.Count; i++)
        {
            var step = Vector2.Distance(polyline[i - 1], polyline[i]);

            // `0x141F5AC94`: the first segment that carries the running total past the reach is the
            // one that gets cut, and the end goes exactly that far along it.
            if (run + step > reach && step > 0f)
                return Settle(Cut(polyline[i - 1], polyline[i], reach - run), polyline[i - 1],
                    run, reach);

            run += step;
        }

        // **A wire that never runs out is not refined at all.** `0x141F5ACA9` jumps clear of both
        // the byte test and `0x141F5BF80` when the walk finishes inside the reach, so the landing
        // is the point asked for even when its coarse cell is unroutable - the request was already
        // validated against that array before any of this ran. Settling here moved landings the
        // game leaves alone.
        return to;
    }

    /// <summary>
    /// `0x140163a70`: where the wire is cut, as the integer grid point the game produces.
    ///
    /// The leftover budget is turned into a scale along the segment and each component is rounded
    /// half up - `floor(d * scale + 0.5)`, the 0.5 being the literal at `0x14360A898` and the
    /// `cvttss2si` plus sign fix-up being the floor. The result is added to the previous bend,
    /// which is an integer point, so the cut is one too.
    ///
    /// **The rounding is not cosmetic.** It is what decides whether the endpoint refinement runs
    /// at all: see Settle, whose gate compares the rounded cut's distance against the budget the
    /// unrounded one spent exactly.
    /// </summary>
    private static Vector2 Cut(Vector2 prior, Vector2 next, float left)
    {
        var dx = MathF.Round(next.X) - MathF.Round(prior.X);
        var dy = MathF.Round(next.Y) - MathF.Round(prior.Y);
        var span = MathF.Sqrt(dx * dx + dy * dy);
        var scale = span > 0f ? MathF.Max(0f, left) / span : 0f;

        return new Vector2(MathF.Round(prior.X) + MathF.Floor(dx * scale + 0.5f),
            MathF.Round(prior.Y) + MathF.Floor(dy * scale + 0.5f));
    }

    /// <summary>
    /// `0x141F5BF80`: when the end falls on ground the wire cannot finish in, walk outwards and take
    /// the first spot that will have it.
    ///
    /// The end is spiralled outward a cell at a time - up to 0xA9 = 169 of them - and the first
    /// candidate whose coarse cell is routable AND which the previous bend can see in a straight
    /// line becomes the landing. Skipped when the end is already on routable ground: the caller
    /// tests the byte at `0x141F5ADB2` and jumps past `0x141F5BF80` when it is set.
    ///
    /// **The gate is what makes the game leave explosives on unroutable cells.** `0x141F5C083`
    /// totals the run up to the previous bend and the distance from it to the ROUNDED cut, and
    /// refuses to relocate at all unless that comes to no more than the reach. The unrounded cut
    /// spends the budget exactly, so the rounding decides it: half up on each component can put
    /// the integer point a fraction further out than the budget allowed, and then nothing is
    /// accepted and the endpoint stays where it fell, zero byte and all.
    ///
    /// It is read off the loop rather than inferred. The comparison sits OUTSIDE the per-candidate
    /// tests and reads the caller's cut through the argument block, not the candidate, so it cannot
    /// vary across the spiral - it passes and every candidate is considered, or it fails and the
    /// whole of `0x141F5BF80` comes to nothing.
    ///
    /// **Measured over every capture on record: 87.5% of landings predicted before the gate, 90.1%
    /// after, over 13,810 corroborated readings from 74 sites, and not one site scores worse.**
    /// The sites it moves are the ones with unroutable ground near the frontier - one Stronghold
    /// capture goes from 79.1% to 92.6%, another from 82.7% to 97.0%, and three reach 100%. Run
    /// tools/replay.py over the dumps directory to reproduce that.
    ///
    /// The pair that found it: the A* matched the engine's own node array on all 58 cells it
    /// touched, so the route, the string-pull and the cut were all right, and the landing still
    /// came out as (1892,989) against the game's (1894,987). The cut fell on (1894,987), coarse
    /// (165,86), byte zero, and this spiral reached (1892,989) on its seventeenth candidate. The
    /// game had gone nowhere, because its gate had already refused.
    /// </summary>
    private Vector2 Settle(Vector2 cut, Vector2 prior, float run, float reach)
    {
        if (Routable(cut))
            return cut;

        if (run + Vector2.Distance(prior, cut) > reach)
            return cut;

        var x = (int)MathF.Round(cut.X);
        var y = (int)MathF.Round(cut.Y);
        var dx = 1;
        var dy = 0;
        var leg = 1;
        var along = 0;

        for (var tried = 0; tried < Spiral; tried++)
        {
            var at = new Vector2(x, y);

            if (Routable(at) && !LineBlocked((int)prior.X, (int)prior.Y, x, y))
                return at;

            var was = dx;

            x += dx;
            y += dy;

            if (++along == leg)
            {
                dy = -dy;
                along = 0;
                (dx, dy) = (dy, was);

                if (was == 0)
                    leg++;
            }
        }

        return cut;
    }

    /// <summary>Whether a point sits in a coarse cell the wire may finish in.</summary>
    private bool Routable(Vector2 at) =>
        RoutableCell(_slab.Cell((int)MathF.Round(at.X), (int)MathF.Round(at.Y)));

    /// <summary>
    /// Whether aiming at a spot actually puts the explosive on it.
    ///
    /// The landing is an integer grid point in the game - the scaled offset is added to an integer
    /// point at `0x141F5AD12` - so the test is whether it rounds back to the spot asked for. A spot
    /// up to half a cell past the budget therefore still takes the bomb, because the cut end rounds
    /// forward onto it.
    ///
    /// **That half cell is only claimable where the wire is the straight line.** Where it bends, the
    /// rounding is decided by the direction and length of the last segment, so predicting it needs the
    /// game's own polyline rather than one that agrees with it about length - and a bend our Bend puts
    /// in a different place, or a coarse cell it clips differently, moves the cut end by a whole cell
    /// while leaving the total almost unchanged. Seen: aim (1584,1294) off (1500,1305), wire 90.5
    /// against a budget of 90, this model landing it on the spot and the client landing it on
    /// (1583,1295) - one cell diagonally, which cost a strongbox at (1610,1276) and stopped the run
    /// with "Wrong spot".
    ///
    /// So on a bend the budget is the whole of the test. That refuses a link the game might have
    /// taken, which costs a little reach; the other way round proposes a link it will not take, which
    /// costs the run.
    /// </summary>
    public bool Lands(Vector2 from, Vector2 to, float reach)
    {
        var wired = Length(from, to);

        // **Inside the budget the walk never runs out, so the landing IS the point asked for** - see
        // Landing's own tail. Taken here because Length is memoised per integer pair where Landing
        // builds a polyline per call, and this is the hottest question in the plugin.
        if (wired <= reach)
            return true;

        if (Bends(from, to))
            return false;

        var at = Landing(from, to, reach);

        return (int)MathF.Round(at.X) == (int)MathF.Round(to.X) &&
               (int)MathF.Round(at.Y) == (int)MathF.Round(to.Y);
    }

    /// <summary>
    /// Whether the routed wire is longer than the straight line between the same two cells.
    ///
    /// Measured against the rounded ends, because that is what Length routes between - comparing a
    /// polyline over integer points with a distance between unrounded ones reads a bend where there
    /// is none. A hundredth of a cell of slack for the float arithmetic of summing the segments.
    /// </summary>
    private bool Bends(Vector2 from, Vector2 to)
    {
        var a = new Vector2(MathF.Round(from.X), MathF.Round(from.Y));
        var b = new Vector2(MathF.Round(to.X), MathF.Round(to.Y));

        return Length(from, to) > Vector2.Distance(a, b) + 0.01f;
    }

    /// <summary>
    /// Lands answered without routing where geometry settles it, or null where only a route can. No when the straight
    /// line is more than a cell past the reach - the wire is never shorter than it - or when the head's coarse cell is
    /// unroutable, which Route refuses outright. Yes when the straight line is clear and at least
    /// <paramref name="margin"/> inside the reach: the wire can still bend round a coarse waypoint the line test fails
    /// on, which is what the margin is for. Scored against Lands by the offline --estimate-check. See
    /// RemnantOrder.EstimatedReachOf.
    /// </summary>
    internal bool? LandsByEstimate(Vector2 from, Vector2 to, float reach, float margin)
    {
        var ax = (int)MathF.Round(from.X);
        var ay = (int)MathF.Round(from.Y);
        var bx = (int)MathF.Round(to.X);
        var by = (int)MathF.Round(to.Y);
        var straight = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

        if (straight > reach + 1f)
            return false;

        var start = _slab.Cell(ax, ay);

        if (start != _slab.Cell(bx, by) && !RoutableCell(start))
            return false;

        if (straight <= reach - margin && !LineBlocked(ax, ay, bx, by))
            return true;

        return null;
    }

    /// <summary>Whether the wire reaches - the routed length against the budget.</summary>
    public bool Reaches(Vector2 from, Vector2 to, float reach) => Length(from, to) <= reach;

    private float Measure((int AX, int AY, int BX, int BY) pair)
    {
        var polyline = Bend(new Vector2(pair.AX, pair.AY), new Vector2(pair.BX, pair.BY));

        if (polyline == null)
            return float.PositiveInfinity;

        var run = 0f;

        for (var i = 1; i < polyline.Count; i++)
            run += Vector2.Distance(polyline[i - 1], polyline[i]);

        return run;
    }

    /// <summary>
    /// The wire itself - every point it bends at, from one end to the other, or null when no route
    /// exists. Length and Landing are both just walks along this.
    /// </summary>
    /// <summary>
    /// The routed polyline between two points, worked out once per pair and kept.
    ///
    /// **Length was memoised and this was not, which is where the solve's time went.** `Lands` and
    /// `Landing` are what the planner asks - through Terrain.Aiming and env.CanReach - and both ran
    /// a fresh A* and string-pull on every call. One polish sweeps every link against every
    /// candidate over six rounds, so the same pair is asked for hundreds of times by one thread and
    /// again by the other seven.
    ///
    /// **Keyed without the reach, because the polyline does not depend on it.** The reach decides
    /// where the wire is CUT, which Landing does afterwards; the route itself is a property of the
    /// two endpoints and the ground. So one entry serves every reach.
    ///
    /// The list is handed out shared rather than copied, and no caller may modify it: Length walks
    /// it to total the segments and Landing walks it to find the cut. A copy per call would give
    /// back much of what the cache saves.
    /// </summary>
    private List<Vector2> Bend(Vector2 from, Vector2 to)
    {
        var key = ((int)MathF.Round(from.X), (int)MathF.Round(from.Y),
            (int)MathF.Round(to.X), (int)MathF.Round(to.Y));

        if (_bent.TryGetValue(key, out var had))
        {
            Interlocked.Increment(ref _hits);

            return had;
        }

        Interlocked.Increment(ref _misses);

        List<Vector2> made;

        var routing = System.Diagnostics.Stopwatch.GetTimestamp();

        using (new Planner.Phase(Planner.PhaseRoute))
            made = Route(from, to);

        Interlocked.Add(ref _routeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - routing);

        // Kept only while there is room; past the limit the path is still returned, just not stored. See MostPaths.
        if (!Full && _bent.TryAdd(key, made))
            Interlocked.Increment(ref _paths);

        return made;
    }

    /// <summary>Routed polylines already worked out. See Bend.</summary>
    /// <summary>
    /// The route search's working set, one per thread and cleared between routes. See Search.
    /// </summary>
    /// <summary>
    /// How often a routed polyline was already on record, and how often one had to be searched for.
    ///
    /// **Whether a cheaper reachability test is worth building depends entirely on this.** A miss
    /// runs an A* over the coarse grid, which is the expensive half of what a solve does; a hit is
    /// a dictionary lookup. ExpeditionIcons puts a cascade of cheaper tests in front of its own
    /// search and notes that searching properly costs more than the rest of its planner put
    /// together - but that only matters here in proportion to how often this misses, and that has
    /// never been measured.
    ///
    /// Interlocked because several search threads share one Wire, unlike the per-thread counters
    /// elsewhere: this is counted a few hundred thousand times a solve rather than millions, and a
    /// count that is wrong about the thing it is deciding is worse than a slightly slower one.
    /// </summary>
    public static (long Hits, long Misses) Routing => (Volatile.Read(ref _hits), Volatile.Read(ref _misses));

    /// <summary>The stopwatch ticks spent routing the misses, summed over the threads. See Routing.</summary>
    public static long RouteTicks => Volatile.Read(ref _routeTicks);

    private static long _hits;

    private static long _misses;

    private static long _routeTicks;

    /// <summary>Starts the counts again. See Caches.Clear.</summary>
    public static void ForgetCounts()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    [ThreadStatic] private static List<(int X, int Y)> _path;

    [ThreadStatic] private static PriorityQueue<(int X, int Y), float> _open;

    [ThreadStatic] private static Dictionary<(int X, int Y), float> _bestSoFar;

    [ThreadStatic] private static Dictionary<(int X, int Y), (int X, int Y)> _cameFrom;

    private readonly ConcurrentDictionary<(int, int, int, int), List<Vector2>> _bent = new();

    private List<Vector2> Route(Vector2 from, Vector2 to)
    {
        var start = _slab.Cell((int)from.X, (int)from.Y);
        var goal = _slab.Cell((int)to.X, (int)to.Y);

        if (start == goal)
            return new List<Vector2> { from, to };

        // **A head in an unroutable cell has no route to anywhere, and finding that out is what
        // costs.** The search below only ever arrives at a cell through the neighbour loop, which
        // skips walled ones, so a walled goal can never be dequeued: every pair runs to exhaustion
        // and floods the head's whole reachable component before returning null. Measured shape -
        // one bomb landed in coarse (165,86) on a Stronghold site, and the overlay's exploit sweep
        // then asked for thousands of routes back to it in a single frame, on the render thread.
        //
        // The game puts explosives in these cells: its A* runs from the request TO the explosive
        // and finds no route either, which is why the site reads as refusing placement everywhere
        // once one lands there. So this is the ordinary state of a bricked chain, not a rare one.
        if (!RoutableCell(start))
            return null;

        // **No shortcut for a clear straight line, because the engine has none.** It was tempting:
        // if the line to the target is clear, surely the pull straightens to it and the route IS
        // the line. That is wrong in a way that matters - the pull tests the line to each
        // intermediate COARSE WAYPOINT, not to the target, and a waypoint can be blocked when the
        // target is not. The engine runs the search at `0x141F5A93F` unconditionally and pulls
        // whatever it returns, so this does too.
        //
        // It costs a search per pair where there used to be a line test. The routed lengths are
        // cached, and a dig site's coarse grid is a few thousand cells, so this is affordable -
        // and being right is not optional here.

        // **The engine searches FROM the requested point TO the explosive, not the other way.**
        // Read off its own node array: the cell carrying g = 0 is the point being aimed at, and the
        // explosive carries the full path cost. The search is then walked back out through the
        // came-from bytes, so the path arrives in explosive-to-requested order.
        //
        // The direction is not cosmetic. The heuristic points at whichever end is the goal, so it
        // orders the open list differently and picks a different route between two of equal cost -
        // and the string-pull turns that into several units of wire. Searching the wrong way round
        // scored 90.2% against the game's own landings; searching this way scores 97.6%.
        var path = UseCellArraySearch ? SearchCells(goal, start) : Search(goal, start);

        if (path == null)
            return null;

        path.Reverse();

        // ---- string-pull, `0x141F5AA20`, index for index ---------------------------------------
        //
        // The loop runs to the second-to-last cell and tests the line from the last KEPT point to
        // cell i+1, keeping cell i only when that line is blocked. The final cell is handled apart
        // from the loop at `0x141F5AB1E`, and only earns its place if the line straight to the
        // requested point is blocked. The requested point is then always appended, which is why a
        // clear line needs no pull at all.
        var kept = new List<Vector2>(path.Count + 2) { from };

        for (var i = 0; i < path.Count - 1; i++)
        {
            var last = kept[^1];
            var next = Ground(path[i + 1]);

            if (LineBlocked((int)last.X, (int)last.Y, (int)next.X, (int)next.Y))
                kept.Add(Ground(path[i]));
        }

        var end = kept[^1];

        if (LineBlocked((int)end.X, (int)end.Y, (int)to.X, (int)to.Y))
            kept.Add(Ground(path[^1]));

        kept.Add(to);

        return kept;
    }

    /// <summary>A coarse cell as the grid point the route builder uses - `cell * 23 / 2`.</summary>
    private Vector2 Ground((int X, int Y) cell) =>
        new(cell.X * Span / 2 + _slab.FromX * Span, cell.Y * Span / 2 + _slab.FromY * Span);

    /// <summary>
    /// `0x141D0F340`: true when any cell on the Bresenham line reads below the clearance.
    ///
    /// The engine picks the longer axis as the major one and steps along it. That is reproduced
    /// rather than approximated - a different walk visits different cells and so answers differently
    /// along a diagonal.
    /// </summary>
    private bool LineBlocked(int ax, int ay, int bx, int by)
    {
        int major0, major1, minor0, minor1;
        bool down;

        if (Math.Abs(by - ay) > Math.Abs(bx - ax))
        {
            major0 = ay; major1 = by; minor0 = ax; minor1 = bx; down = true;
        }
        else
        {
            major0 = ax; major1 = bx; minor0 = ay; minor1 = by; down = false;
        }

        var run = Math.Abs(major1 - major0);

        if (run == 0)
            return BelowClearance(ax, ay);

        var rise = Math.Abs(minor1 - minor0);
        var step = minor0 < minor1 ? 1 : minor0 == minor1 ? 0 : -1;
        var along = major0 < major1 ? 1 : -1;
        var slip = -(run / 2);
        var major = major0;
        var minor = minor0;
        var end = major1 + along;

        while (major != end)
        {
            if (BelowClearance(down ? minor : major, down ? major : minor))
                return true;

            var next = slip + rise;

            major += along;

            if (next > 0)
            {
                minor += step;
                slip = next - run;
            }
            else
            {
                slip = next;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether Route searches with SearchCells rather than Search. The two return the same path for every pair; this is
    /// here so the offline check can time and compare them. See SearchCells.
    /// </summary>
    internal static bool UseCellArraySearch = true;

    /// <summary>
    /// Search, over flat per-thread arrays indexed by coarse cell instead of dictionaries keyed by cell, with a stamp per
    /// search so nothing is cleared between routes. The open list holds the same priorities in the same order, and a
    /// heap compares only priorities, so it dequeues the same cells as Search and returns the same path - checked over
    /// every recorded aim of two Grand sites offline. Falls back to Search when the start is off the grid, where the
    /// arrays have no index for it.
    /// </summary>
    private List<(int X, int Y)> SearchCells((int X, int Y) start, (int X, int Y) goal)
    {
        var wide = _slab.Wider;
        var cells = wide * _slab.Higher;

        if (start.X < 0 || start.Y < 0 || start.X >= wide || start.Y >= _slab.Higher)
            return Search(start, goal);

        if (_gOfCell == null || _gOfCell.Length < cells)
        {
            _gOfCell = new float[cells];
            _cameFromCell = new int[cells];
            _stampOfCell = new int[cells];
            _stamp = 0;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_stampOfCell);
            _stamp = 1;
        }

        var stamp = _stamp;
        var g = _gOfCell;
        var came = _cameFromCell;
        var stamps = _stampOfCell;
        var open = _openCells ??= new PriorityQueue<int, float>();

        open.Clear();

        var first = start.Y * wide + start.X;
        var target = goal.Y * wide + goal.X;

        g[first] = 0f;
        came[first] = -1;
        stamps[first] = stamp;
        open.Enqueue(first, Apart(start, goal));

        var seen = 0;

        while (open.TryDequeue(out var at, out _) && seen++ < Most)
        {
            if (at == target)
            {
                var path = _path ??= new List<(int X, int Y)>();

                path.Clear();

                for (var cell = at; cell >= 0; cell = came[cell])
                    path.Add((cell % wide, cell / wide));

                path.Reverse();

                return path;
            }

            var ax = at % wide;
            var ay = at / wide;
            var cost = g[at];
            var mask = _slab.Bytes[at];

            for (var i = 0; i < Steps.Length; i++)
            {
                if (((mask >> i) & 1) == 0)
                    continue;

                var nx = ax + Steps[i].X;
                var ny = ay + Steps[i].Y;

                if (!RoutableCell((nx, ny)))
                    continue;

                var next = ny * wide + nx;
                var step = cost + Steps[i].Cost;

                if (stamps[next] == stamp && g[next] <= step)
                    continue;

                g[next] = step;
                came[next] = at;
                stamps[next] = stamp;
                open.Enqueue(next, step + Apart((nx, ny), goal));
            }
        }

        return null;
    }

    [ThreadStatic] private static float[] _gOfCell;

    [ThreadStatic] private static int[] _cameFromCell;

    [ThreadStatic] private static int[] _stampOfCell;

    [ThreadStatic] private static int _stamp;

    [ThreadStatic] private static PriorityQueue<int, float> _openCells;

    /// <summary>
    /// The routed polyline between two points without the cache, for the offline check that compares the two searches.
    /// </summary>
    internal List<Vector2> RouteUncached(Vector2 from, Vector2 to) => Route(from, to);

    /// <summary>
    /// Whether a cell reads below the clearance the route builder asks for, which is 1.
    ///
    /// **Off the grid is NOT blocked, and getting that wrong is expensive.** Treating an unreadable
    /// cell as solid manufactures detours that do not exist and inflates every routed length passing
    /// near an edge - in the offline check it turned 25 pairs over the reach into 262.
    /// </summary>
    private bool BelowClearance(int x, int y)
    {
        if (y < 0 || y >= _aim.Length)
            return false;

        var row = _aim[y];

        if (row == null || x < 0 || x >= row.Length)
            return false;

        return row[x] < Clearance;
    }

    /// <summary>A* over routable coarse cells - the engine's `0x141D028B0`, eight-connected.</summary>
    private List<(int X, int Y)> Search((int X, int Y) start, (int X, int Y) goal)
    {
        // **Reused per thread, because this is where a solve's garbage was.** Measured: the search
        // asks about reach 787,066 times in one solve and 2.07GB of the 3.31GB it allocates is
        // spent inside those calls - 2.63KB each, which is these three collections growing to the
        // number of cells the route expands and then being thrown away.
        //
        // Cleared rather than rebuilt. A thread runs one route at a time, so one set of buffers per
        // thread is enough, and clearing a dictionary keeps the buckets it has already grown - so
        // the second route on a thread allocates nothing at all.
        //
        // Three hypotheses about this gigabyte were counted before this one and all three came to
        // nothing, which is why the phase table exists and why this is where it pointed.
        var open = _open ??= new PriorityQueue<(int X, int Y), float>();
        var best = _bestSoFar ??= new Dictionary<(int X, int Y), float>();
        var came = _cameFrom ??= new Dictionary<(int X, int Y), (int X, int Y)>();

        open.Clear();
        best.Clear();
        came.Clear();

        best[start] = 0f;

        // Held by reference, so it fills as the search runs and the dump can read it afterwards.
        // See Costs: this is the half of the comparison that is ours.
        //
        // Now a thread's reused buffer rather than a fresh dictionary, so it is cleared by the next
        // route that thread runs. The dump reads it straight after asking for one route, which is
        // the case it was written for; a read taken while a solve is running could already see
        // another thread's figures, and could now see a half-filled set of this one's.
        _costs = best;

        open.Enqueue(start, Apart(start, goal));

        var seen = 0;

        while (open.TryDequeue(out var at, out _) && seen++ < Most)
        {
            if (at == goal)
            {
                // Reused per thread like the rest of the working set. Route walks this to build
                // the polyline it caches and keeps no reference to it afterwards - checked - so
                // the only list that has to be a real allocation is the polyline itself.
                var path = _path ??= new List<(int X, int Y)>();

                path.Clear();
                path.Add(at);

                while (came.TryGetValue(at, out var prior))
                {
                    at = prior;
                    path.Add(at);
                }

                path.Reverse();

                return path;
            }

            var cost = best[at];
            var mask = _slab.Bytes[at.Y * _slab.Wider + at.X];

            for (var i = 0; i < Steps.Length; i++)
            {
                // `0x141D02C60`: the cell's byte says which of its eight neighbours may be stepped
                // to. Without this the router takes moves the game forbids, which shortens the wire
                // and puts the boundary further out than the game will actually place.
                if (((mask >> i) & 1) == 0)
                    continue;

                var next = (at.X + Steps[i].X, at.Y + Steps[i].Y);

                if (!RoutableCell(next))
                    continue;

                var step = cost + Steps[i].Cost;

                if (best.TryGetValue(next, out var had) && had <= step)
                    continue;

                best[next] = step;
                came[next] = at;
                open.Enqueue(next, step + Apart(next, goal));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the wire may route through a coarse cell - the same question Peek.Slab.Routable
    /// answers about a grid point, one layer down, on the cell the conversion produces.
    /// </summary>
    private bool RoutableCell((int X, int Y) cell) =>
        cell.X >= 0 && cell.Y >= 0 && cell.X < _slab.Wider && cell.Y < _slab.Higher &&
        _slab.Bytes[cell.Y * _slab.Wider + cell.X] != 0;

    private static float Apart((int X, int Y) a, (int X, int Y) b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The clearance the route builder passes - `mov r9d, 1` at `0x141F5AA98`.</summary>
    private const int Clearance = 1;

    /// <summary>How many grid units one tile spans.</summary>
    private const int Span = 23;

    /// <summary>
    /// The eight steps, in the engine's own order, with the engine's own costs.
    ///
    /// **Order matters, because it is the bit order of the routing byte.** `0x141D02FC0` is a jump
    /// table from neighbour index to coordinate, and it runs the four orthogonals first and the four
    /// diagonals after - not round the compass. Every compass ordering tried against the data failed
    /// for that reason; reading the table settled it in one go.
    ///
    /// The costs come from `0x141D02C99`: index below four costs 1, four and above costs the
    /// literal below, which is the engine's constant rather than a computed root of two.
    /// </summary>
    /// <summary>
    /// The g values our own A* reached, from the most recent Search.
    ///
    /// **Only ever read beside the engine's own node array, never instead of it.** `0x141D028B0`
    /// leaves one g per coarse cell behind at `grid + 0x38`, so the two can be put side by side and
    /// the first cell where they differ is the first place our router stops being the game's. That
    /// is a reading; scoring our landings against remembered ones is not, because a landing is the
    /// end of a route, a string-pull, a clamp and a spiral, and a mismatch says nothing about which
    /// of the four moved. See Peek.Searched.
    /// </summary>
    internal IReadOnlyDictionary<(int X, int Y), float> Costs => _costs;

    /// <summary>
    /// Route one pair purely so the search runs and Costs fills. Diagnostic; the answer is dropped. Searched every time
    /// and with Search, which is the one that fills Costs, as Route would search it: from the requested point.
    /// </summary>
    internal void Trace(Vector2 from, Vector2 to) =>
        Search(_slab.Cell((int)to.X, (int)to.Y), _slab.Cell((int)from.X, (int)from.Y));

    private Dictionary<(int X, int Y), float> _costs = new();

    private static readonly (int X, int Y, float Cost)[] Steps =
    {
        (0, -1, 1f), (1, 0, 1f), (0, 1, 1f), (-1, 0, 1f),
        (1, -1, Diagonal), (1, 1, Diagonal), (-1, 1, Diagonal), (-1, -1, Diagonal),
    };

    /// <summary>The engine's diagonal cost, at `0x0360ABF4`. Not MathF.Sqrt(2).</summary>
    private const float Diagonal = 1.4142099618911743f;

    /// <summary>A ceiling on the search, so a pathological site cannot stall a frame.</summary>
    private const int Most = 20000;

    /// <summary>How many cells the endpoint spiral tries - `cmp r13d, 0xa9` at `0x141F5C0F3`.</summary>
    private const int Spiral = 0xA9;
}
