using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using System;
using System.Collections.Generic;
using System.Numerics;
using Color = System.Drawing.Color;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The ground the map's author marked as no-placement, drawn.
///
/// **This is the rule that could not be found in any terrain layer, because it is not terrain.** The
/// client hashes the string `expedition_no_placement` at load - FNV-1a over its utf-16 units, folded
/// to sixteen bits, giving 0x970C - and stores it in a global. When the placement route is built it
/// asks, for the point being aimed at, whether that point falls inside an object on the point's TILE
/// carrying that tag. Those objects are authored volumes: a dig site carries regions its designer
/// marked as forbidden, and no walkability, targeting, height or tile-name layer says anything about
/// them. That is why every one of those read identically across a frontier the game enforced exactly.
///
/// Read the way the game reads it. For a point, the tile is `x / 23`, `y / 23` indexed into the tile
/// array at `TerrainStruct+0x28`, 0x38 bytes a record. The tile's own object list is the vector of
/// 16-byte entries at `TileStructure+0x10`. An object carries its tag at `+0x40`, its size at `+0x44`
/// and `+0x48`, and its centre through `[[obj+8] + 0x98]` at `+0x444` and `+0x448` - the same pair of
/// fields the player's position is read from. The test is an axis-aligned box, half-width rounded the
/// signed way:
///
/// <code>
/// half = size / 2;  inside when  pos - half &lt;= p &lt; pos + (size - half)
/// </code>
///
/// **The entry does not point at the object directly.** The game resolves two further levels before
/// comparing, through a function that also touches a reference count. Rather than replicate that -
/// which would mean guessing at a shared-pointer layout - each level of the chain is tried and the one
/// whose `+0x40` carries the tag is taken. A wrong level simply does not match.
/// </summary>
public static class Forbidden
{
    /// <summary>
    /// Where the next explosive may and may not go, as a dot on every spot you can actually click.
    ///
    /// **This is the rule, not a reading of it.** Placement is decided in `0x141F5A6B0` and its
    /// terms are all known exactly - see NOTES 1d and 1e:
    ///
    /// <code>
    /// routable  the coarse routing grid's byte is non-zero        (Peek.Coarse)
    /// apart     d^2 &gt; 400 from every placed explosive, detonator exempt
    /// reach     the ROUTED length of the wire is within the reach  (Wire)
    /// </code>
    ///
    /// **One dot per grid unit, because that is the resolution the game places at.** An earlier
    /// version drew a cross every other cell, which joined into streaks that were part green and
    /// part red with gaps between them - it looked like the coarse routing grid because the crosses
    /// were three grid long and the gaps were the stride, and neither of those is anything the
    /// player can click. A dot sits on a spot the indicator can occupy and says yes or no about
    /// that spot alone.
    ///
    /// **From the chain head only.** The next explosive runs its wire from the newest link, so that
    /// is the only origin whose answer is the one being asked. Drawing a disc round every explosive
    /// cost several times as much, ran into the drawing budget and got cut off part way - which read
    /// as a bug in the rule rather than as a budget.
    /// </summary>
    public static void Placement(Graphics graphics, GameController gc, List<RectangleF> covered,
        AutoExpeditionSettings settings)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        var chain = Border.Chain(gc);

        if (chain.Count == 0)
            return;

        // The newest link - the detonator when nothing is down yet, since that is what the first
        // wire runs from.
        var head = chain[^1];
        var area = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);
        var reach = MathF.Max(1f, Detonator.PlacementRange(gc));
        var apart = MathF.Max(1f, Safe.Read(() => settings.Debug.ApartAtLeast.Value, 20f));

        // **Worked out once and kept, because none of it moves.** Reading the ground, routing the
        // wire and asking the terrain its height are all far too expensive to repeat per frame for
        // tens of thousands of spots; what changes per frame is only where the camera puts them.
        //
        // **The settings that change what gets worked out belong in the mark.** Turning the clamp
        // layer on is not a drawing change - the exploits are found inside Gather - so leaving it
        // out meant the toggle did nothing at all until something else happened to move: the ground
        // had already been gathered without them and the cache had no reason to notice. Anything
        // Gather reads has to be here, or switching it on is silent.
        var mark = (area, chain.Count, head.X, head.Y, reach, apart);

        if (_marked != mark)
        {
            _marked = mark;

            // **Timed apart from the drawing, because they are different costs on different
            // clocks.** Gathering routes wires and reads ground for every cell of the field; it
            // runs when the chain head moves and not otherwise. Drawing runs every frame. Averaged
            // together, one re-gather of a few hundred milliseconds is indistinguishable from the
            // steady cost being high, and the two want opposite fixes.
            using (Spent.On("Forbidden/Gather"))
                Gather(gc, settings, head, chain, reach, apart);
        }

        // **Culled in the world before anything is projected, because the projection IS the cost.**
        //
        // The field is one dot per grid cell over a disc of the whole reach, which is some
        // twenty-eight thousand of them, and every one was projected to the screen and drawn -
        // measured at 8.2ms a frame, more than half of everything this plugin does. The screen
        // holds a fraction of that disc at any zoom, so most of the work was a projection followed
        // immediately by a discard.
        //
        // The radius is derived rather than guessed: one extra projection a frame gives how many
        // pixels a grid unit is worth right now, and the window's half-diagonal divided by that is
        // how far a visible dot can be. Zoom changes it and this follows. A quarter is added back
        // because the ground is not flat - a dot on a rise projects further than its distance
        // suggests - and the exact test is still the one below, on the screen position itself.
        var span = Pixels(camera, player);
        // Half the window's diagonal is the furthest a visible point can be from its centre, and a
        // tenth is added for the ground not being flat: a dot on a rise projects further than its
        // distance on the floor suggests. Squared, so the test below needs no square root.
        var beyond = span <= 0f
            ? float.MaxValue
            : MathF.Pow(new Vector2(window.Width, window.Height).Length() * 0.55f / span, 2f);

        Held = _dots.Count;
        Drew = 0;
        Across = span;
        Radius = beyond >= float.MaxValue ? 0f : MathF.Sqrt(beyond);

        foreach (var (at, good, grid) in _dots)
        {
            if (beyond < float.MaxValue && Vector2.DistanceSquared(
                    new Vector2(at.X, at.Y), new Vector2(player.X, player.Y)) > beyond)
                continue;

            var on = Safe.Read((camera, at), static x => x.camera.WorldToScreen(x.at), Vector2.Zero);

            if (on == Vector2.Zero || Panels.Covers(covered, on))
                continue;

            // **Faint until the game has been asked, solid once it has.** The sweep works outward
            // from the player and a dig site is several screens wide, so the useful thing to see at
            // a glance is which ground has actually been put to the game and which is still only a
            // prediction. A spot the game disagreed with is drawn in neither colour - it is the one
            // output of the whole exercise that wants acting on. See Frontier and Checked.
            var said = Checked.Here.Verdict(grid);
            var tone = said == false ? Disputed : good ? Placeable : Refusing;
            var size = said == null ? Dot : Dot + 0.5f;

            if (said == null)
                tone = Color.FromArgb(Faint, tone.R, tone.G, tone.B);

            Drew++;

            graphics.DrawBox(new RectangleF(on.X - size, on.Y - size, size * 2f, size * 2f), tone);
        }

    }

    /// <summary>
    /// Every spot within reach of the chain head, with the game's verdict and its place in the
    /// world. See Placement for why this is worked out once rather than per frame.
    /// </summary>
    private static void Gather(GameController gc, AutoExpeditionSettings settings, Vector2 head,
        List<Vector2> chain, float reach, float apart)
    {
        _dots.Clear();

        var slab = Peek.Coarse(gc);

        if (slab == null)
            return;

        // The ground model carries the game's own routing. Latched on the area, since Terrain.Read
        // materialises whole grids and the Wire behind it caches routed lengths - rebuilding it
        // would throw that cache away exactly as it became worth having.
        //
        // **But a model with no wire is not worth keeping, and this kept one for a whole map.**
        // Terrain.Read takes the coarse grid only if it can be trusted, and one of the trust checks
        // is that the grid contains the detonator - which a grid still belonging to the last map's
        // dig site does not. A snapshot taken on such a frame has no wire, so every reach question
        // it is asked comes back as the straight-line distance: the dots then go green out to a
        // straight ninety while the game spends that budget along a wire that bends, and clamps
        // short of the spots they promised. It held until the plugin was reloaded, because the area
        // never changed and nothing else could throw the snapshot away.
        //
        // So an unrouted snapshot is retried rather than latched. Once it routes it is kept, which
        // is the whole of what the latch was for.
        var area = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);

        if (_ground == null || _grounded != area || !_ground.Routing)
        {
            _ground = Terrain.Read(gc);
            _grounded = area;
        }

        // The detonator is exempt from spacing, so it is not one of the explosives to stand off.
        var bombs = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacedExplosiveGridPositions, null);

        var spacing = apart * apart;
        var edge = reach + Margin;
        var ox = (int)head.X;
        var oy = (int)head.Y;

        for (var y = oy - (int)edge; y <= oy + (int)edge; y++)
        {
            for (var x = ox - (int)edge; x <= ox + (int)edge; x++)
            {
                var cell = new Vector2(x, y);

                if (Vector2.DistanceSquared(head, cell) > edge * edge)
                    continue;

                var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(cell),
                    Vector3.Zero);

                if (world == Vector3.Zero)
                    continue;

                _dots.Add((world, Allows(slab, _ground, head, bombs, cell, spacing, reach), cell));
            }
        }

    }

    /// <summary>
    /// How many dots the field holds, and how many of them the last frame actually drew.
    ///
    /// **The cull cannot be judged by comparing two runs**, which is what was tried first: every
    /// stage of a frame moves with what is on the site and what else the machine is doing, so a
    /// figure that went up says nothing about whether the cull works. These two say it directly.
    /// </summary>
    public static int Held { get; private set; }

    /// <summary>And how many survived to be drawn. See Held.</summary>
    public static int Drew { get; private set; }

    /// <summary>
    /// The two numbers the cull radius is worked out from, so a bad radius is readable as one.
    ///
    /// The first attempt culled four dots in a hundred, and from the counts alone there is no way
    /// to tell an honest radius from a scale read wrongly - which is what it was. See Pixels.
    /// </summary>
    public static float Across { get; private set; }

    /// <summary>And what that made the radius, in world units. See Across.</summary>
    public static float Radius { get; private set; }

    /// <summary>
    /// How many screen pixels one world unit is worth right now, or nought when it cannot be read.
    ///
    /// Two projections: the player, and the player a world unit to one side. The difference is the
    /// scale, which is what a zoom level actually is - so nothing here has to know what the zoom
    /// levels are or which one is in use.
    /// </summary>
    private static float Pixels(Camera camera, Vector3 player)
    {
        if (player == Vector3.Zero)
            return 0f;

        // **Over a long baseline, because a one unit step is lost in the rounding.** The first
        // version stepped a single world unit and divided by nothing, and whatever came back put
        // the cull radius so far out that it removed four dots in a hundred. A hundred units is
        // several grid squares - large enough that the answer is the scale rather than the
        // rounding, small enough to stay on screen and so inside the same projection behaviour.
        const float Baseline = 100f;

        var here = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player),
            Vector2.Zero);
        var over = Safe.Read((camera, player), static x =>
            x.camera.WorldToScreen(x.player + new Vector3(Baseline, 0f, 0f)), Vector2.Zero);

        return here == Vector2.Zero || over == Vector2.Zero
            ? 0f
            : Vector2.Distance(here, over) / Baseline;
    }

    /// <summary>The three terms of the placement rule, together. See Placement.</summary>
    private static bool Allows(Peek.Slab slab, Terrain ground, Vector2 head,
        GameOffsets2.Native.Vector2i[] bombs, Vector2 cell, float spacing, float reach)
    {
        if (!slab.Routable((int)cell.X, (int)cell.Y))
            return false;

        // **The site's own forbidden rectangles, first, because the routine tests them first.**
        // `0x141F5A6B0` asks whether the requested point carries `expedition_no_placement` before it
        // converts a coordinate or routes anything, and a hit writes validity 0 and returns. Ground
        // inside one refuses however open every published grid calls it - which is what a green dot
        // over the expedition NPC was. See Volumes.
        if (ground != null && ground.Forbids(cell))
            return false;

        if (bombs != null)
        {
            for (var i = 0; i < bombs.Length; i++)
                if (Vector2.DistanceSquared(new Vector2(bombs[i].X, bombs[i].Y), cell) <= spacing)
                    return false;
        }

        // The straight line first, because the wire is never shorter - so this rules a spot out
        // without routing anything. It cannot rule one IN: what decides that is where the wire's
        // clamped end LANDS, which is what Aiming asks. See Wire.Lands.
        //
        var far = reach + 1f;

        if (Vector2.DistanceSquared(head, cell) > far * far)
            return false;

        // **Answered through the same method the planner calls**, Aiming rather than Reaches, so
        // the overlay and the search can never answer differently. They did once: readings taken
        // from the placement indicator used to outrank the model wherever one existed, and a link
        // of 91 grid on a predicted reach of 90 was planned on the strength of one while the dot
        // underneath was drawn RED, because the dot asked the wire and the plan did not.
        return ground == null || ground.Aiming(head, cell, reach, out _);
    }


    /// <summary>Every spot in range of the chain head, in the world, with its verdict.</summary>
    private static readonly List<(Vector3 At, bool Good, Vector2 Grid)> _dots = new();

    /// <summary>The ground model behind the overlay, kept until the area changes. See Gather.</summary>
    private static Terrain _ground;

    private static uint _grounded;

    private static (uint Area, int Links, float X, float Y, float Reach, float Apart) _marked;

    /// <summary>Where an explosive may go - the planner's own green, so the overlay reads as the plan does.</summary>
    private static readonly Color Placeable = Color.FromArgb(255, 90, 255, 120);

    /// <summary>Where it may not. The plugin's warning red.</summary>
    private static readonly Color Refusing = Color.FromArgb(255, 235, 90, 90);


    private const float Margin = 5f;

    /// <summary>Half a dot, in pixels. Small enough that neighbours stay separate spots.</summary>
    private const float Dot = 1.5f;

    /// <summary>What a spot the game disagreed with us about is drawn in. See Frontier.</summary>
    private static readonly Color Disputed = Color.White;

    /// <summary>How solid an untested spot is drawn, against a tested one at full strength.</summary>
    private const int Faint = 110;

}
