using ExileCore2;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// The plan, and what is left to scout, drawn on the minimap.
///
/// **A Grand site does not fit on the screen.** Fifteen explosives span most of a 2370 by 1449 map,
/// so the world overlay can only ever show the two or three links you are standing among - and the
/// question a fifteen link chain raises is about its shape, which is exactly what you cannot see.
/// The minimap already holds the whole site.
///
/// **The projection is the engine's, not ours.** Graphics.GridToMap turns a grid position into a
/// point on whichever map is up, DrawCircleOnMap draws a ring at a grid radius around a grid
/// position, and MapSurfaceClip cuts both at the edge of the corner map. This class had its own copy
/// of that transform first - the isometric skew, the camera's 38.7 degree tilt, the terrain height
/// term - reconstructed from what MinimapIcons does, and it worked. It was still the wrong thing to
/// keep: a private copy of somebody else's projection is a second thing to be right, and it goes
/// wrong silently the day the game changes the first.
/// </summary>
internal static class Minimap
{
    /// <summary>
    /// How many sides a ring gets on the minimap.
    ///
    /// Fewer than the world rings get, and deliberately: a blast circle a hundred pixels across in
    /// the world is a dozen on the minimap, where the sides are below a pixel long well before the
    /// count matters. Fifteen of these a frame is the budget being spent.
    /// </summary>
    private const int Segments = 20;

    /// <summary>
    /// A ring on the map for every barrel's own blast. See the world drawing for why it is drawn.
    ///
    /// Here as well as in the world because a Grand site is walked rather than seen: the barrel that
    /// matters to a chain is routinely off screen when the chain is being planned, and the map is
    /// where the whole site is visible at once.
    /// </summary>
    public static void Barrels(Graphics graphics, AutoExpeditionSettings settings,
        List<Target> targets, HashSet<(int, int)> blown)
    {
        foreach (var target in targets)
        {
            if (target.Kind == TargetKind.Barrel && target.Sets > 0f && !target.Spent &&
                blown?.Contains(((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y)))
                    != true)
            {
                if (settings.Display.ThePlan.ShowBarrels)
                {
                    graphics.DrawCircleOnMap(target.Grid, false, target.Sets,
                        settings.Display.ThePlan.BarrelColour, 1f, Segments);
                }
            }
        }
    }

    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Plan plan, Blast blast, Cleared cleared, bool[] reach, bool everywhere)
    {
        if (plan is not { Points.Count: > 0 } || !Showing(gc))
            return;

        var radius = blast.Radius(gc, settings);

        if (radius == null)
            return;

        // Which spot is next, read off the game's own list exactly as the world overlay does, so the
        // two agree about which ring is emphasised.
        var placed = Detonator.PlacedExplosiveGridPositions(gc);
        var queue = -1;

        for (var i = 0; i < plan.Points.Count; i++)
        {
            if (!Already(placed, plan.Points[i]))
            {
                queue = i;

                break;
            }
        }

        using var clip = graphics.MapSurfaceClip();

        // The same rules the world drawing uses, for the same reasons - see Overlay's Chain.
        //
        // **A ring stops being drawn once its explosive is down**, because the game draws that one
        // itself and two rings on the same ground only make it harder to see which is the plan. A
        // LINK carries on being drawn though: a spot with its explosive on it is still part of the
        // route you are building, and a line that retracted as you placed would hide the shape of
        // the thing before it was finished. It is after detonation that a link stops being worth
        // drawing, and then the test is whether its blast has been fought out.
        //
        // Mirrored rather than simplified: the minimap and the world showing different pictures of
        // the same chain is worse than either picture on its own.
        var from = queue == 0 ? Detonator.LastExplosiveGridPosition(gc) : Vector2.Zero;
        var behind = false;
        var thickness = MathF.Max(0.5f, settings.Display.ThePlan.PlanThickness.Value);

        for (var i = 0; i < plan.Points.Count; i++)
        {
            var at = plan.Points[i];
            var gone = cleared.Is(i);
            var done = Already(placed, at);
            var next = i == queue;

            // The same answers the world drawing reached, passed in rather than asked again -
            // the two pictures of one chain must never disagree about them. See Overlay.Reachable.
            var ready = reach != null && i < reach.Length && reach[i] && (next || everywhere);
            var colour = ready ? settings.Display.ThePlan.StepColour : settings.Display.ThePlan.LaterColour;

            // The same switch the world drawing answers, so the two pictures of one chain agree
            // about which links are shown. See PlanDisplaySettings.ShowLater.
            if (!gone && !done && (ready || next || settings.Display.ThePlan.ShowLater))
            {
                // Radius in grid units, which is what the blast is measured in - no conversion,
                // unlike the world drawing where the same number has to be taken up to world units.
                graphics.DrawCircleOnMap(at, false, radius.Value, colour,
                    next ? 2f : 1f, Segments);
            }

            if (from != Vector2.Zero && !gone && !behind)
                graphics.DrawLineOnMap(from, at, thickness, colour);

            from = at;
            behind = gone;
        }
    }

    /// <summary>
    /// The ground nobody has been near, painted over the map as a single image.
    ///
    /// **It was a heap of filled quads and no amount of merging made it look right.** Every version
    /// of that idea - a tile each, a row each, greedily merged rectangles - has the same fault,
    /// which is that a translucent fill is anti-aliased by feathering outward, so the seam between
    /// two patches is painted twice and reads as a brighter line. A field of patches then reads as a
    /// hatch drawn over the map rather than as an area.
    ///
    /// One image has no seams in it. See Wash, and Radar, which draws the whole walkable map this
    /// way and is where the approach came from.
    /// </summary>
    public static void Unscouted(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Scouted scouted)
    {
        if (!scouted.Ready || !Showing(gc))
            return;

        var colour = settings.Display.UnscoutedGround.UnscoutedColour.Value;
        var surface = Surface(gc);

        // **One image over four projected corners, which is how Radar draws the walkable map.**
        // See Wash for why the quads had to go: a translucent fill is feathered outward, so every
        // seam between two patches was painted twice and a field of them read as a hatch.
        if (!Wash.Ready(graphics, scouted, colour))
        {
            Washed = (0, 0, "the texture could not be built", surface, Large(gc));

            return;
        }

        var texture = Wash.Texture(graphics);

        if (texture == 0)
        {
            Washed = (0, 0, "no texture registered", surface, Large(gc));

            return;
        }

        var (a, b, c, d) = Wash.Corners(graphics, scouted);

        if (a == Vector2.Zero && c == Vector2.Zero)
        {
            Washed = (0, 0, "the map projection gave nothing", surface, Large(gc));

            return;
        }

        // Its own draw list rather than the one MapSurfaceClip cuts, because that clip belongs to
        // the shapes list and AddImageQuad is not on it. The clip is done here instead, against the
        // surface the map is drawn on - the same rectangle that decided whether a quad was visible.
        var shapes = ImGuiNET.ImGui.GetBackgroundDrawList();

        shapes.PushClipRect(new Vector2(surface.X, surface.Y),
            new Vector2(surface.X + surface.Width, surface.Y + surface.Height), true);

        shapes.AddImageQuad(texture, a, b, c, d);
        shapes.PopClipRect();

        Washed = (1, 1,
            $"one image {scouted.Wide}x{scouted.High} over ({a.X:0},{a.Y:0})-({c.X:0},{c.Y:0})",
            surface, Large(gc));
    }



    /// <summary>
    /// What the last wash actually put on screen. See Unscouted.
    ///
    /// **"The draw call ran" is not "the player saw something."** This layer clears every gate it
    /// has - the setting, the site size, the terrain, an open map - and still shows nothing when its
    /// quads land outside the map surface, which is what happens whenever the part of the site you
    /// have not walked is the part the visible map does not reach. Nothing about that is visible
    /// from the gates, so the draw reports its own result.
    /// </summary>
    public static (int Drew, int Inside, string First, ExileCore2.Shared.RectangleF Surface, bool Large) Washed;

    /// <summary>
    /// Where a patch has to land to be seen, which is a different rectangle for each map.
    ///
    /// **The LARGE map's own rectangle lies.** It reports 0x0 while plainly open and covering the
    /// screen, so a test against it counted nothing as visible and said so with total confidence -
    /// a measurement that can only return one answer is worse than none, because it looks like
    /// evidence. The window is used instead, and the large map covers it anyway.
    ///
    /// **The SMALL one does not lie, and using the window for it was the bug.** It reports a real
    /// rectangle - 2190,9 362x362 in the dump this was found in - while the image quad is the whole
    /// tile grid projected, thousands of pixels tall. Clipped to the window rather than to the
    /// minimap, the unscouted layer painted across the screen; whether that looked wrong depended
    /// on where the site happened to sit, which is why it showed on some sites and not others.
    ///
    /// Falls back to the window if the small map reports nothing, because a clip of zero area draws
    /// nothing at all and silence is the worse failure of the two.
    /// </summary>
    private static ExileCore2.Shared.RectangleF Surface(GameController gc)
    {
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(),
            default(ExileCore2.Shared.RectangleF));

        if (Large(gc))
            return window;

        var map = Safe.Read(gc, static g => g.IngameState.IngameUi.Map, null);

        var small = map == null
            ? default
            : Safe.Read(map, static m => m.SmallMiniMap.GetClientRectCache,
                default(ExileCore2.Shared.RectangleF));

        return small.Width > 0f && small.Height > 0f ? small : window;
    }

    /// <summary>What each map says about itself, for the dump. See Washed.</summary>
    public static string Maps(GameController gc)
    {
        var map = Safe.Read(gc, static g => g.IngameState.IngameUi.Map, null);

        if (map == null)
            return "no map element";

        var small = Safe.Read(map, static m => m.SmallMiniMap.GetClientRectCache,
            default(ExileCore2.Shared.RectangleF));
        var large = Safe.Read(map, static m => m.LargeMap.GetClientRectCache,
            default(ExileCore2.Shared.RectangleF));

        return $"small {small.X:0},{small.Y:0} {small.Width:0}x{small.Height:0} " +
               $"visible {Safe.Read(map, static m => m.SmallMiniMap.IsVisibleLocal, false)}; " +
               $"large {large.X:0},{large.Y:0} {large.Width:0}x{large.Height:0} " +
               $"visible {Safe.Read(map, static m => m.LargeMap.IsVisibleLocal, false)}";
    }

    /// <summary>Whether the big map is the one up, which is the one that reaches the far corners.</summary>
    private static bool Large(GameController gc)
    {
        var map = Safe.Read(gc, static g => g.IngameState.IngameUi.Map, null);

        return map != null && Safe.Read(map, static m => m.LargeMap.IsVisibleLocal, false);
    }

    private static Vector2 At(Graphics graphics, float x, float y)
    {
        var grid = new Vector2(x, y);

        return graphics.GridToMap(grid, grid);
    }

    /// <summary>Whether either map is up, so nothing is drawn into a screen that has neither.</summary>
    /// <summary>
    /// The must-take markers, ringed on the minimap.
    ///
    /// The same pair of rings the world drawing uses, for the same reason - see Overlay's Musts. On
    /// a Grand site this is the half that matters: the marker you pointed at is usually nowhere near
    /// the screen by the time you are looking at the chain it changed.
    /// </summary>
    public static void Musts(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<Target> targets)
    {
        if ((Insisted.Here.Count == 0 && !(MustAvoidMods.Banned > 0 && MustAvoidMods.DrawOnMap)) ||
            targets == null || !Showing(gc))
            return;

        using var clip = graphics.MapSurfaceClip();

        // The planner's green for a must take and the refusal colour for a must avoid. See the
        // world drawing in Overlay's Musts, which this mirrors.
        var green = (Color)settings.Display.ThePlan.StepColour;
        var red = (Color)settings.Display.Remnants.Rewards.OverruledColour;

        foreach (var target in targets)
        {
            var said = Insisted.Here.Of(target.Grid);

            // The same automatic avoid the world drawing shows, under its own switch. See
            // MustAvoidMods.DrawOnMap.
            if (MustAvoidMods.DrawOnMap && said == Insisted.Said.Nothing &&
                MustAvoidMods.Bans(target))
                said = Insisted.Said.Avoid;

            if (said == Insisted.Said.Nothing)
                continue;

            var colour = said == Insisted.Said.Take ? green : red;

            graphics.DrawCircleOnMap(target.Grid, false, 3.2f, colour, 1.5f, Segments);
            graphics.DrawCircleOnMap(target.Grid, false, 4.4f, colour, 1.5f, Segments);
        }
    }

    /// <summary>
    /// Whether either map is up. Public because the dump asks it: every gate on a layer that is not
    /// appearing has to be answerable from outside, or the only way to tell which one is shut is to
    /// guess.
    /// </summary>
    public static bool Showing(GameController gc)
    {
        var map = Safe.Read(gc, static g => g.IngameState.IngameUi.Map, null);

        if (map == null)
            return false;

        // **Both guarded on IsValid, and only one of them was.**
        //
        // Reading a wrapper element that does not resolve makes ExileCore2 log "Element with index:
        // 0 not found" - it is not an exception and Safe.Read never sees it, so the miss is silent
        // to us and a line in the log to everybody else. The large map is absent whenever it is not
        // open, which is nearly always, and this runs once per frame from Draw and again from
        // Musts - both of which only start once there is a plan, which is exactly when the message
        // begins.
        //
        // IsValid asks whether the element resolved at all. Asking it first is the same discipline
        // the small map already had beside it.
        return Safe.Read(map, static m => m.SmallMiniMap.IsValid && m.SmallMiniMap.IsVisibleLocal, false) ||
               Safe.Read(map, static m => m.LargeMap.IsValid && m.LargeMap.IsVisibleLocal, false);
    }

    /// <summary>
    /// Whether an explosive is already standing on this spot. Mirrors the world overlay.
    ///
    /// **It did not mirror it.** This allowed two grid and Overlay's copy allows one and a half, so
    /// a spot between the two counted as placed here and unplaced there - one ring missing from the
    /// minimap, a different link emphasised as next, and the two pictures of one chain disagreeing
    /// about how far through it is. That is the exact thing the mirroring exists to prevent, and a
    /// tolerance is the easiest half of it to copy wrongly.
    /// </summary>
    private static bool Already(Vector2[] placed, Vector2 at)
    {
        foreach (var was in placed ?? [])
        {
            if (Vector2.DistanceSquared(was, at) < 1.5f * 1.5f)
                return true;
        }

        return false;
    }
}
