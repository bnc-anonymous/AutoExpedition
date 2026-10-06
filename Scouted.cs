using ExileCore2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Which parts of a Grand site have been walked near enough to load, and which have not.
///
/// **A Grand site is bigger than the client will hold.** The game streams entities in around the
/// player and drops them behind, so the scan only ever learns the part of the map somebody has stood
/// in - and on a site needing fifteen explosives that is a small fraction of it. The plan is only as
/// good as the lap that preceded it, and until now there was no way to tell how much of the lap was
/// left except by walking it and seeing whether the marker count went up.
///
/// So the ground is tiled, the tiles the player has been near are marked, and what is left over is
/// painted on the minimap. What "near" means is a radius rather than line of sight, which is the
/// conservative direction: a tile marked unseen that you have in fact seen costs a walk you did not
/// need, where the other mistake costs content the plan never hears about.
///
/// **Only walkable ground counts.** The terrain grid is static map data and is there from the first
/// frame, so the tiles that could never be stood on are known before anything is scouted - painting
/// those red would fill the map with places nobody was ever going to go and bury the answer.
/// </summary>
internal sealed class Scouted
{
    /// <summary>
    /// Grid units to a tile.
    ///
    /// Sixteen is a compromise between a map that reads as a wash of red and one that costs a
    /// thousand quads a frame. A whole 2370 by 1449 map is 149 by 91 tiles - under fourteen thousand
    /// bits, which is why this can be stored and restored without thinking about the size.
    /// </summary>
    public const int Tile = 16;

    private uint _area;
    private int _wide;
    private int _high;
    private bool[] _seen;
    private bool[] _ground;

    /// <summary>The terrain's height at each tile's middle, in world units, or null before the terrain is read. See HeightAt.</summary>
    private float[] _height;
    private bool _dirty;
    private DateTime _wrote;

    /// <summary>Whether there is anything to draw, which needs the terrain to have been read.</summary>
    public bool Ready => _ground != null;

    /// <summary>
    /// The one in play, so the dump can reach it.
    ///
    /// This layer is drawn behind four separate gates - a setting, a site size, a terrain read and
    /// an open map - and when it does not appear, every one of them looks equally likely from the
    /// outside. Guessing between them has already cost two rounds. See Describe.
    /// </summary>
    public static Scouted Live { get; private set; }

    public int Wide => _wide;

    public int High => _high;

    /// <summary>Where the remembered scouting lives. Empty turns the remembering off.</summary>
    public string Home { get; set; } = "";

    /// <summary>
    /// Throws away what has been scouted here, WITHOUT saving it first.
    ///
    /// **Not AreaChange, which keeps before it rebuilds.** That is right when a zone ends - the lap
    /// you just walked is worth having next time - and exactly wrong for a reset, because Keep
    /// writes the layer back to the file the same reset has just deleted. A clear that restores
    /// what it cleared is not a clear.
    ///
    /// The walkable mask stays. It is the map's own geometry, fixed when the map was generated, so
    /// it is not something the plugin worked out by standing here and there is nothing to forget
    /// about it. What goes is where you have walked, which is the whole of what this layer draws.
    ///
    /// Marked clean on the way out so the next Keep has nothing to write: an emptied mask saved
    /// over the file would say the plugin remembered an area it had been told to forget.
    /// </summary>
    public void Forget()
    {
        if (_seen != null)
            Array.Clear(_seen, 0, _seen.Length);

        _dirty = false;
    }


    /// <summary>
    /// Reads the terrain once and picks up whatever was scouted here before.
    ///
    /// The walkable mask is built here rather than per frame because it cannot change: it is the
    /// map's own geometry, fixed when the map was generated.
    /// </summary>
    public void AreaChange(uint areaHash, GameController gc)
    {
        // **Or the same area, if there is no ground yet.**
        //
        // The mask is built here and nowhere else, so a plugin reloaded in the middle of a map never
        // built one: the area had not changed, so nothing asked, and the layer stayed blank until
        // the next zone - which is the moment its own record of where you have walked is thrown
        // away. Reloading to see a change is the ordinary way to work on this, and it silently
        // turned off the one thing that survives a lap.
        //
        // Cheap to ask: the read happens once, and the guard below is what stops it happening twice.
        Live = this;

        if (areaHash == _area && _ground != null)
            return;

        Keep();

        _area = areaHash;
        _seen = null;
        _ground = null;
        _dirty = false;

        Build(gc);
    }

    /// <summary>
    /// Reads the terrain, if it can be read yet.
    ///
    /// **Separate from the area change because the area change is too early.** Zoning in fires it
    /// before the terrain data is there, so the dimensions come back zero, and doing this only once
    /// would leave the layer permanently off for exactly the case it was built for. Mark calls it
    /// again on every frame until it takes, which costs a vector read a frame until it does.
    /// </summary>
    private void Build(GameController gc)
    {
        var size = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, Vector2.Zero);

        if (size.X <= 0f || size.Y <= 0f)
            return;

        _wide = (int)MathF.Ceiling(size.X / Tile);
        _high = (int)MathF.Ceiling(size.Y / Tile);
        _seen = new bool[_wide * _high];
        _ground = new bool[_wide * _high];

        // **Dropped with the two beside them, because the map's size changes with the map.** These
        // are sized to the tile grid like everything else here, and keeping one from the last area
        // means indexing the new grid's tiles into the old grid's array - which is an exception on
        // any map larger than the one before it, and silently the wrong answer on a smaller one.
        // Build is the only place the dimensions move, so it is the only place that can say so.
        _wanted = null;
        _far = null;
        _drawn = null;
        _connected = null;
        Stamp++;
        Wash.Forget();

        for (var ty = 0; ty < _high; ty++)
        {
            for (var tx = 0; tx < _wide; tx++)
            {
                // The tile's middle stands for the tile. A finer test would be more honest about
                // the edges of a wall and would cost sixteen times as many lookups to say the same
                // thing about a tile that is either somewhere you walk or somewhere you do not.
                var at = new Vector2(tx * Tile + Tile / 2f, ty * Tile + Tile / 2f);

                _ground[ty * _wide + tx] =
                    Safe.Read((gc, at), static x => x.gc.IngameState.Data.GetPathfindingValueAt(x.at), 0)
                    >= 1;
            }
        }

        // The terrain's height at each tile's middle, read once, for drawing the layer where the map draws the ground.
        // See HeightAt.
        var heights = Safe.Read(gc, static g => g.IngameState.Data.RawTerrainHeightData, null);

        _height = new float[_wide * _high];

        if (heights != null)
        {
            for (var ty = 0; ty < _high; ty++)
            {
                for (var tx = 0; tx < _wide; tx++)
                {
                    var (mx, my) = (tx * Tile + Tile / 2, ty * Tile + Tile / 2);

                    if (my < heights.Length && heights[my] is { } row && mx < row.Length)
                        _height[ty * _wide + tx] = row[mx];
                }
            }
        }

        Restore();
    }

    /// <summary>
    /// Why the unscouted layer is or is not on screen, gate by gate. See Live.
    ///
    /// Every line answers one of the four things that can stop it, and the tile counts answer the
    /// fifth - a site walked end to end has nothing left to paint, which looks exactly like a layer
    /// that is switched off.
    /// </summary>
    public string Describe(GameController gc, AutoExpeditionSettings settings)
    {
        var grand = Detonator.Grand(gc);
        var on = grand
            ? settings.Display.UnscoutedGround.ShowUnscoutedGrand.Value
            : settings.Display.UnscoutedGround.ShowUnscoutedExpedition.Value;

        var links = Detonator.ExplosiveCount(gc);
        var map = Minimap.Showing(gc);


        if (_ground == null || _seen == null)
        {
            var size = Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, Vector2.Zero);

            return $"setting {on}, grand {grand} ({links} explosives), terrain NOT READ " +
                   $"(area {size.X:0}x{size.Y:0}), map open {map}";
        }

        var (left, ground) = Left();
        var seen = ground - left;

        var colour = settings.Display.UnscoutedGround.UnscoutedColour.Value;

        return $"setting {on}, grand {grand} ({links} explosives), map open {map}, " +
               (grand
                   ? _connected != null ? "searching the ground connected to the detonator" : "searching the whole map"
                   : $"searching within {settings.Display.UnscoutedGround.MarkerRadius.Value:0} grid of each marker" +
                     (settings.Display.UnscoutedGround.ShowUnscoutedFar
                         ? ", plus the sites nobody has walked to (drawn, never counted)"
                         : "")) +
               $", {_wide}x{_high} tiles, {ground} walkable to search, {seen} scouted, {left} not - " +
               $"painted in rgba({colour.R},{colour.G},{colour.B},{colour.A})" +
               Remaining() + PathfindingAgreement(gc);
    }

    /// <summary>
    /// Where the unscouted ground actually is, in grid, as patches rather than as a count.
    ///
    /// **A count is not an answer to "I thought I had covered it all".** The readout reads
    /// "Partial presolve" for as long as one tile of ground near a marker has not been walked
    /// near, and thirty-six tiles out of a couple of hundred is a patch somewhere on a minimap
    /// also shaded for every site nobody has walked to - see ShowUnscoutedFar, which draws ground
    /// that is never counted. Telling somebody the number and leaving them to find it is how a
    /// correct layer reads as a broken one.
    ///
    /// Flood filled over the counted mask only, so what it names is exactly what holds the label
    /// back. Costs a walk over the tile grid, which is what the line above already costs, and it
    /// only runs when a dump is written.
    /// </summary>
    private string Remaining()
    {
        if (_seen == null || _ground == null || _wanted == null)
            return "";

        var filled = new bool[_wide * _high];
        var patches = new List<(int Tiles, float X, float Y)>();
        var queue = new Queue<int>();

        for (var index = 0; index < filled.Length; index++)
        {
            if (filled[index] || !_ground[index] || !_wanted[index] || _seen[index])
                continue;

            queue.Clear();
            queue.Enqueue(index);
            filled[index] = true;

            int tiles = 0;
            float sx = 0f, sy = 0f;

            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                var tx = at % _wide;
                var ty = at / _wide;

                tiles++;
                sx += tx * Tile + Tile / 2f;
                sy += ty * Tile + Tile / 2f;

                // Four ways, not eight: two patches touching only at a corner are two walks.
                for (var side = 0; side < 4; side++)
                {
                    var nx = tx + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = ty + (side == 2 ? -1 : side == 3 ? 1 : 0);

                    if (nx < 0 || ny < 0 || nx >= _wide || ny >= _high)
                        continue;

                    var next = ny * _wide + nx;

                    if (filled[next] || !_ground[next] || !_wanted[next] || _seen[next])
                        continue;

                    filled[next] = true;
                    queue.Enqueue(next);
                }
            }

            patches.Add((tiles, sx / tiles, sy / tiles));
        }

        if (patches.Count == 0)
            return "";

        patches.Sort(static (a, b) => b.Tiles.CompareTo(a.Tiles));

        var b2 = new StringBuilder($". Unscouted, biggest first:");

        for (var i = 0; i < patches.Count && i < 4; i++)
            b2.Append($" {patches[i].Tiles} tiles around ({patches[i].X:0},{patches[i].Y:0})");

        if (patches.Count > 4)
            b2.Append($" and {patches.Count - 4} smaller");

        return b2.ToString();
    }

    /// <summary>
    /// How often the raw pathfinding grid, which TilesConnectedTo floods, agrees with GetPathfindingValueAt, which the
    /// walkable mask is read from, at the tiles' middles - as read, and with the grid moved a tile each way. Two grids
    /// that disagree put the connected ground somewhere other than the walkable ground, and the layer is their overlap.
    /// For the dump only: it reads every tile twice.
    /// </summary>
    private string PathfindingAgreement(GameController gc)
    {
        if (_ground == null)
            return "";

        var grid = Safe.Read(gc, static g => g.IngameState.Data.RawPathfindingData, null);

        if (grid is not { Length: > 0 })
            return "; the raw pathfinding grid could not be read";

        bool RawAt(int x, int y) => y >= 0 && y < grid.Length && grid[y] is { } row && x >= 0 && x < row.Length && row[x] != 0;

        var said = new StringBuilder($"; raw pathfinding grid {grid.Length} rows of {grid[0]?.Length ?? 0}, against the lookup at tile middles:");

        foreach (var (dx, dy) in new[] { (0, 0), (Tile, 0), (-Tile, 0), (0, Tile), (0, -Tile) })
        {
            var (agree, all) = (0, 0);

            for (var ty = 0; ty < _high; ty++)
            {
                for (var tx = 0; tx < _wide; tx++)
                {
                    var (mx, my) = (tx * Tile + Tile / 2, ty * Tile + Tile / 2);

                    all++;
                    agree += RawAt(mx + dx, my + dy) == _ground[ty * _wide + tx] ? 1 : 0;
                }
            }

            said.Append($" moved ({dx},{dy}) {100d * agree / Math.Max(1, all):0.0}%");
        }

        return said.ToString();
    }

    /// <summary>Marks everything within reach of where the player is standing.</summary>
    public void Mark(GameController gc, Vector2 player, float radius)
    {
        Live = this;

        if (_seen == null)
            Build(gc);

        Around(player, radius);

        if (_dirty && DateTime.UtcNow - _wrote > TimeSpan.FromSeconds(10))
            Keep();
    }

    /// <summary>
    /// Sets how much ground there is to search at all: a radius around every marker the scan holds.
    ///
    /// **Markers do not vouch for ground, they point at more of it.** Expedition content is laid
    /// down in clusters and a marker is almost never on its own, so a marker with nothing within a
    /// radius of it to the north is the site's own statement that there is nothing further north -
    /// and a marker at the edge of what has streamed in is the opposite, an arrow at ground that has
    /// not been looked at. That is the question this layer exists to answer, so the markers set the
    /// region and the walking sets what has been covered in it.
    ///
    /// **This was the other way round first and it was wrong.** Marking a radius around each marker
    /// as SEEN made every marker its own proof that nothing was hiding beside it, which is circular:
    /// the region was defined by the markers and then declared covered by the same markers, so there
    /// was nothing left to paint. What covers ground is having been near it - Mark, off the player -
    /// and nothing else.
    ///
    /// Tiles rather than circles, which is what keeps the drawing honest: two markers close together
    /// contribute to one set of tiles, so what Unseen hands back is a single merged outline however
    /// many markers made it.
    /// </summary>
    /// <param name="whole">
    /// Whether the whole map is the region, which is what a Grand site wants: it is bigger than the
    /// client will hold, every part of it is worth walking, and the markers cannot delimit ground
    /// nobody has been near enough to find a marker in.
    /// </param>
    /// <param name="elsewhere">
    /// Markers belonging to dig sites nobody has walked to, which are DRAWN but never counted.
    ///
    /// **Two regions, because the layer and the readout ask different questions.** The drawing is
    /// about the map - where is there still something nobody has been near - and a player who wants
    /// the other expedition shaded from the start is asking exactly that. "Partial presolve" is
    /// about the site in front of them, and a second expedition across the map would leave it
    /// reading partial for ever, which is a warning that is always on and therefore says nothing.
    /// </param>
    /// <param name="detonator">
    /// Where the site's detonator stands, in grid; with <paramref name="whole"/>, only the ground connected to it is
    /// searched. Zero searches every walkable tile. See TilesConnectedTo.
    /// </param>
    public void Wanted(GameController gc, IReadOnlyList<Vector2> markers,
        IReadOnlyList<Vector2> elsewhere, float radius, bool whole, Vector2 detonator = default)
    {
        Live = this;

        if (_seen == null)
            Build(gc);

        if (_ground == null)
            return;

        if (_wanted == null || _wanted.Length != _wide * _high)
            _wanted = new bool[_wide * _high];

        if (whole)
        {
            var connected = TilesConnectedTo(gc, detonator);

            for (var i = 0; i < _wanted.Length; i++)
                _wanted[i] = connected == null || connected[i];

            _drawn = _wanted;
            Stamp++;

            return;
        }

        // Rebuilt rather than added to, so a site whose markers turn out to be tighter than they
        // looked shrinks back. It is a statement about the scan as it stands, not a high-water mark
        // like the seen mask - which is about where a player has BEEN and cannot be taken back.
        Array.Clear(_wanted);
        Stamp++;
        Spread(_wanted, markers, radius);

        if (elsewhere is not { Count: > 0 })
        {
            _drawn = _wanted;

            return;
        }

        // The drawn region is the counted one plus the far sites, so nothing can be shaded that is
        // not also searched - the two can only differ by what was added here.
        if (_far == null || _far.Length != _wide * _high)
            _far = new bool[_wide * _high];
        Array.Copy(_wanted, _far, _wanted.Length);
        Spread(_far, elsewhere, radius);
        _drawn = _far;
    }

    /// <summary>
    /// Per tile, whether its middle is walkable ground connected to the detonator, or null when that cannot be read:
    /// the pathfinding grid flood filled cell by cell, eight ways, from the walkable cell nearest the detonator. Worked
    /// out once per area and detonator and kept. See Wanted.
    ///
    /// **A Grand site is searched over its own ground, not the whole map.** The map holds other walkable areas that no
    /// walk from the dig site reaches - on a Frigid Bluffs site (2026-10-04) four of 200 cells or more, 1,169 to 3,821
    /// grid from the detonator, one of them 130,895 cells - and they were painted unscouted for as long as nobody went
    /// there, which nobody can. The detonator's own area held all 443 markers that stand on walkable ground. One flood
    /// took 18 ms offline on that site's grid. See tools/offline FogAreas.
    ///
    /// Cell by cell rather than over the tiles, since a tile stands for its middle and a passage narrower than a tile
    /// can fall between two middles, which would cut the site in two.
    /// </summary>
    private bool[] TilesConnectedTo(GameController gc, Vector2 detonator)
    {
        if (detonator == Vector2.Zero)
            return null;

        var key = ((int)detonator.X, (int)detonator.Y);

        if (_connected != null && _connectedFor == key && _connected.Length == _wide * _high)
            return _connected;

        var grid = Safe.Read(() => gc.IngameState.Data.RawPathfindingData, null);

        if (grid is not { Length: > 0 })
            return null;

        var high = grid.Length;
        var wide = 0;

        foreach (var row in grid)
            wide = Math.Max(wide, row?.Length ?? 0);

        bool Open(int x, int y) => y >= 0 && y < high && grid[y] is { } row && x >= 0 && x < row.Length && row[x] != 0;

        // The walkable cell nearest the detonator, within a few, since it can stand on a cell the grid closes.
        var start = -1;

        for (var r = 0; r <= 6 && start < 0; r++)
        {
            for (var dy = -r; dy <= r && start < 0; dy++)
            {
                for (var dx = -r; dx <= r && start < 0; dx++)
                {
                    if (Open(key.Item1 + dx, key.Item2 + dy))
                        start = (key.Item2 + dy) * wide + key.Item1 + dx;
                }
            }
        }

        if (start < 0)
            return null;

        var reached = new bool[wide * high];
        var queue = new Queue<int>();

        reached[start] = true;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            var (x, y) = (at % wide, at / wide);

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (x + dx, y + dy);

                    if ((dx != 0 || dy != 0) && Open(nx, ny) && !reached[ny * wide + nx])
                    {
                        reached[ny * wide + nx] = true;
                        queue.Enqueue(ny * wide + nx);
                    }
                }
            }
        }

        var tiles = new bool[_wide * _high];

        for (var ty = 0; ty < _high; ty++)
        {
            for (var tx = 0; tx < _wide; tx++)
            {
                var (mx, my) = (tx * Tile + Tile / 2, ty * Tile + Tile / 2);

                tiles[ty * _wide + tx] = mx < wide && my < high && reached[my * wide + mx];
            }
        }

        _connected = tiles;
        _connectedFor = key;

        return tiles;
    }

    /// <summary>The tiles connected to the detonator, for the detonator they were worked out from. See TilesConnectedTo.</summary>
    private bool[] _connected;

    private (int X, int Y) _connectedFor;

    private void Spread(bool[] into, IReadOnlyList<Vector2> markers, float radius)
    {
        if (markers == null || radius <= 0f)
            return;

        var span = (int)MathF.Ceiling(radius / Tile);
        var reach = radius * radius;

        for (var m = 0; m < markers.Count; m++)
        {
            var middle = markers[m];

            if (middle == Vector2.Zero)
                continue;

            var cx = (int)(middle.X / Tile);
            var cy = (int)(middle.Y / Tile);

            for (var ty = Math.Max(0, cy - span); ty <= Math.Min(_high - 1, cy + span); ty++)
            {
                for (var tx = Math.Max(0, cx - span); tx <= Math.Min(_wide - 1, cx + span); tx++)
                {
                    var index = ty * _wide + tx;

                    if (into[index])
                        continue;

                    var at = new Vector2(tx * Tile + Tile / 2f, ty * Tile + Tile / 2f);

                    if (Vector2.DistanceSquared(at, middle) <= reach)
                    {
                        into[index] = true;
                        Stamp++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The region "Partial presolve" is counted over: this dig site and nothing else. See Wanted.
    /// </summary>
    private bool[] _wanted;

    /// <summary>That plus the sites nobody has walked to, when they are being shaded too.</summary>
    private bool[] _far;

    /// <summary>Whichever of the two is being drawn. Never null once Wanted has run.</summary>
    private bool[] _drawn;

    /// <summary>
    /// A number that changes whenever the picture would. See Wash.Ready.
    ///
    /// Bumped where the masks are written rather than derived from them: deriving it means walking
    /// fourteen thousand tiles to find out whether it is worth walking fourteen thousand tiles.
    /// </summary>
    public int Stamp { get; private set; }

    /// <summary>Whether this tile is ground that is being searched and has not been reached.</summary>
    /// <summary>
    /// The terrain's height at a tile's middle, in world units, or nought when not read.
    ///
    /// **The map draws raised ground further up the screen.** The layer is one image over four projected corners, and
    /// each corner was projected at the height of the map's own corner, so the whole layer sat where ground at that
    /// height would be: on an Exhumed Ruins site (2026-10-05) with its detonator at height 125 it drew below the map.
    /// Radar, which draws the whole walkable map the same way, moves every pixel by its own height. See Wash.
    /// </summary>
    public float HeightOfTile(int tx, int ty) =>
        _height == null || tx < 0 || ty < 0 || tx >= _wide || ty >= _high ? 0f : _height[ty * _wide + tx];

    /// <summary>The terrain's height at a grid position, as its tile's. See HeightOfTile.</summary>
    public float HeightAt(System.Numerics.Vector2 grid) => HeightOfTile((int)(grid.X / Tile), (int)(grid.Y / Tile));

    /// <summary>
    /// The middles of the highest and the lowest tile painted unscouted, or none, for measuring the layer's height move
    /// where it is largest. See Minimap.Unscouted.
    /// </summary>
    public (System.Numerics.Vector2 Highest, System.Numerics.Vector2 Lowest)? PaintedHeightExtremes()
    {
        // Worked out again only when the picture changes, since the wash line asks every frame the map is open.
        if (_extremesFor == Stamp)
            return _extremes;

        _extremesFor = Stamp;
        _extremes = null;

        if (_height == null)
            return null;

        var (highest, lowest) = (-1, -1);

        for (var ty = 0; ty < _high; ty++)
        {
            for (var tx = 0; tx < _wide; tx++)
            {
                if (!Unpainted(tx, ty))
                    continue;

                var i = ty * _wide + tx;

                if (highest < 0 || _height[i] > _height[highest])
                    highest = i;

                if (lowest < 0 || _height[i] < _height[lowest])
                    lowest = i;
            }
        }

        System.Numerics.Vector2 Middle(int i) => new((i % _wide) * Tile + Tile / 2f, (i / _wide) * Tile + Tile / 2f);

        _extremes = highest < 0 ? null : (Middle(highest), Middle(lowest));

        return _extremes;
    }

    private int _extremesFor = -1;

    private (System.Numerics.Vector2 Highest, System.Numerics.Vector2 Lowest)? _extremes;

    public bool Unpainted(int tx, int ty)
    {
        if (_ground == null || _drawn == null || _seen == null ||
            tx < 0 || ty < 0 || tx >= _wide || ty >= _high)
            return false;

        var index = ty * _wide + tx;

        return _ground[index] && _drawn[index] && !_seen[index];
    }

    private void Around(Vector2 middle, float radius)
    {
        if (_seen == null || middle == Vector2.Zero || radius <= 0f)
            return;

        var span = (int)MathF.Ceiling(radius / Tile);
        var cx = (int)(middle.X / Tile);
        var cy = (int)(middle.Y / Tile);
        var reach = radius * radius;

        for (var ty = Math.Max(0, cy - span); ty <= Math.Min(_high - 1, cy + span); ty++)
        {
            for (var tx = Math.Max(0, cx - span); tx <= Math.Min(_wide - 1, cx + span); tx++)
            {
                var index = ty * _wide + tx;

                if (_seen[index])
                    continue;

                var at = new Vector2(tx * Tile + Tile / 2f, ty * Tile + Tile / 2f);

                if (Vector2.DistanceSquared(at, middle) > reach)
                    continue;

                _seen[index] = true;
                _dirty = true;
                Stamp++;
            }
        }
    }

    /// <summary>
    /// How much of the site is still unknown, in tiles, and how much of it there is.
    ///
    /// **This is what "Partial" means now.** It used to mean that some of the site's markers were
    /// not being read this instant, which flips as entities stream in and out and says nothing
    /// about whether the site has been met - the same site would read whole, then partial, then
    /// whole again while nothing about it had changed. Ground nobody has been near, and that no
    /// marker vouches for, is a claim that stays true until somebody goes and looks.
    /// </summary>
    public (int Left, int All) Left()
    {
        if (_seen == null || _ground == null || _wanted == null)
            return (0, 0);

        int left = 0, all = 0;

        for (var ty = 0; ty < _high; ty++)
        {
            for (var tx = 0; tx < _wide; tx++)
            {
                var index = ty * _wide + tx;

                if (!_ground[index] || !_wanted[index])
                    continue;

                all++;

                if (!_seen[index])
                    left++;
            }
        }

        return (left, all);
    }

    // ------------------------------------------------------------------ remembering it

    /// <summary>
    /// Writes the scouting, in the same spirit as the marker records and for the same reason.
    ///
    /// One bit a tile, hex encoded, which is a kilobyte and a half for a whole map. Not worth
    /// compressing and very much worth being able to read with a text editor when it goes wrong.
    /// </summary>
    public void Keep()
    {
        if (!_dirty || _seen == null || string.IsNullOrEmpty(Home) || _area == 0)
            return;

        try
        {
            var directory = Path.Combine(Home, "sites");

            Directory.CreateDirectory(directory);

            var bits = new StringBuilder((_seen.Length + 3) / 4);

            for (var i = 0; i < _seen.Length; i += 4)
            {
                var nibble = 0;

                for (var b = 0; b < 4 && i + b < _seen.Length; b++)
                {
                    if (_seen[i + b])
                        nibble |= 1 << b;
                }

                bits.Append("0123456789abcdef"[nibble]);
            }

            File.WriteAllLines(Path.Combine(directory, $"scout_{_area}.txt"), new[]
            {
                $"tile\t{Tile}",
                $"size\t{_wide}\t{_high}",
                // Which marker format this lap was walked under. See Remembered.Format.
                $"markers\t{Remembered.Format}",
                bits.ToString(),
            });

            _dirty = false;
            _wrote = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not remember the scouting: {ex.Message}", 5f);
        }
    }

    private void Restore()
    {
        if (_seen == null || string.IsNullOrEmpty(Home) || _area == 0)
            return;

        try
        {
            var path = Path.Combine(Home, "sites", $"scout_{_area}.txt");

            if (!File.Exists(path))
                return;

            var lines = File.ReadAllLines(path);

            // Any disagreement about the shape and the file is about somewhere else.
            if (lines.Length < 4 ||
                lines[0] != $"tile\t{Tile}" ||
                lines[1] != $"size\t{_wide}\t{_high}")
                return;

            // **And any disagreement about the markers makes it about a sweep that no longer
            // exists.** Walking a site is how markers are found; if those were thrown away for
            // a format change, the walk has to happen again, and a record saying it already did
            // is worse than no record - it hides exactly the ground that most needs covering.
            // Bumping Remembered's version re-reds every map on its own. See Remembered.Format.
            if (lines[2] != $"markers\t{Remembered.Format}")
                return;

            var bits = lines[3];

            for (var i = 0; i < _seen.Length; i++)
            {
                var nibble = i / 4;

                if (nibble >= bits.Length)
                    break;

                var value = Convert.ToInt32(bits[nibble].ToString(), 16);

                _seen[i] = (value & (1 << (i % 4))) != 0;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the remembered scouting: {ex.Message}", 5f);
        }
    }
}
