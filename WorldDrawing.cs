using System;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using Color = System.Drawing.Color;

namespace AutoExpedition;

/// <summary>
/// Lines and rings on the ground, drawn only where they lie in front of the camera.
///
/// **Camera.WorldToScreen does not refuse a point behind the camera; it mirrors it to an ordinary-looking screen
/// point.** Seen as a chain link drawn diagonally across the screen (see Overlay.Plausible), a barrel's ring
/// (2026-10-12) and a burning ground ring from the bottom of the screen to the top (2026-10-22). Its code is obfuscated
/// in this build, so the divide by depth behind it is inferred, not read. A check that each projected point lands near
/// the window does not catch it, as the mirrored point can land inside the window. ProjectionCheck in the dump compares
/// the matrix read here with WorldToScreen.
///
/// So the depth is taken from the camera's own matrix (Camera.Snapshot): a line is cut where it passes NearDepthOfPlayer
/// of the player's depth, the part behind is dropped, and only then are its ends projected by WorldToScreen. Which sign
/// of depth is in front is taken from the player, who is always in view, rather than assumed from a convention.
/// </summary>
internal static class WorldDrawing
{
    /// <summary>The depth a line is cut at, as a fraction of the player's depth. Chosen: well short of anything on screen.</summary>
    private const float NearDepthOfPlayer = 0.1f;

    /// <summary>Rim points tested before a ring is handed to ExileCore2, which projects it itself. Chosen.</summary>
    private const int RingPointsTested = 16;

    /// <summary>
    /// The camera's matrix and the depth lines are cut at, signed so in front is positive; null when the camera or the
    /// player cannot be read, or the player does not have a depth.
    /// </summary>
    private static (Matrix4x4 Matrix, float Sign, float Near)? DepthOfView(GameController gc, Camera camera)
    {
        var matrix = Safe.Read(camera, static c => (Matrix4x4?)c.Snapshot.Matrix, null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (matrix is not { } m || player == Vector3.Zero)
            return null;

        var playerDepth = Vector4.Transform(new Vector4(player, 1f), m).W;

        if (!float.IsFinite(playerDepth) || MathF.Abs(playerDepth) < 1e-3f)
            return null;

        return (m, MathF.Sign(playerDepth), MathF.Abs(playerDepth) * NearDepthOfPlayer);
    }

    private static float Depth((Matrix4x4 Matrix, float Sign, float Near) view, Vector3 world) =>
        Vector4.Transform(new Vector4(world, 1f), view.Matrix).W * view.Sign;

    /// <summary>Whether a point on the ground is in front of the camera, by the same test Line cuts at.</summary>
    internal static bool InFront(GameController gc, Vector3 world)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        return camera != null && DepthOfView(gc, camera) is { } view && Depth(view, world) > view.Near;
    }

    /// <summary>
    /// The screen ends of the line between two points on the ground, cut to the part in front of the camera, or null
    /// when none of it is or a point cannot be projected.
    /// </summary>
    internal static (Vector2 From, Vector2 To)? ScreenEnds(GameController gc, Camera camera, Vector3 from, Vector3 to)
    {
        if (camera == null || DepthOfView(gc, camera) is not { } view)
            return null;

        var fromDepth = Depth(view, from);
        var toDepth = Depth(view, to);

        if (!(fromDepth > view.Near) && !(toDepth > view.Near))
            return null;

        if (!(fromDepth > view.Near))
            from = Vector3.Lerp(from, to, (view.Near - fromDepth) / (toDepth - fromDepth));
        else if (!(toDepth > view.Near))
            to = Vector3.Lerp(from, to, (fromDepth - view.Near) / (fromDepth - toDepth));

        var a = Safe.Read((camera, from), static x => x.camera.WorldToScreen(x.from), Vector2.Zero);
        var b = Safe.Read((camera, to), static x => x.camera.WorldToScreen(x.to), Vector2.Zero);

        if (a == Vector2.Zero || b == Vector2.Zero || !float.IsFinite(a.X) || !float.IsFinite(a.Y) ||
            !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            return null;

        return (a, b);
    }

    /// <summary>A line between two points on the ground, the part behind the camera left out. See ScreenEnds.</summary>
    internal static void Line(Graphics graphics, GameController gc, Camera camera, Vector3 from, Vector3 to, float thickness,
        Color colour)
    {
        if (ScreenEnds(gc, camera, from, to) is { } ends)
            graphics.DrawLine(ends.From, ends.To, thickness, colour);
    }

    /// <summary>
    /// Whether a ring on the ground lies wholly in front of the camera, tested at RingPointsTested points of its rim at
    /// the centre's height. ExileCore2's DrawCircleInWorld projects the rim itself, so a ring failing this is not drawn
    /// rather than cut.
    /// </summary>
    internal static bool RingInFront(GameController gc, Vector3 centre, float radius)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null || DepthOfView(gc, camera) is not { } view)
            return false;

        for (var i = 0; i < RingPointsTested; i++)
        {
            var angle = i * MathF.Tau / RingPointsTested;

            if (!(Depth(view, centre + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f)) > view.Near))
                return false;
        }

        return true;
    }

    /// <summary>
    /// For the dump: points around the player at growing distances in eight directions, each with its depth by the
    /// camera matrix and two projections - the matrix's own (x/w and y/w scaled to the half size) and WorldToScreen's.
    /// Where the two agree for points in front, the matrix is read the way WorldToScreen reads it and the depth is the
    /// one it divides by; a point with a negative depth shows what WorldToScreen makes of a point behind the camera.
    /// </summary>
    internal static string ProjectionCheck(GameController gc)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var snapshot = Safe.Read(camera, static c => ((Matrix4x4, Vector2)?)(c.Snapshot.Matrix, c.Snapshot.HalfSize), null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (camera == null || snapshot is not { } s || player == Vector3.Zero)
            return "  camera, its matrix or the player not readable";

        var (matrix, half) = s;
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"  half size {half.X:0}x{half.Y:0}; cut at {NearDepthOfPlayer:0.##} of the player's depth");

        foreach (var distance in new[] { 0f, 1000f, 4000f })
        {
            for (var i = 0; i < (distance == 0f ? 1 : 8); i++)
            {
                var angle = i * MathF.Tau / 8;
                var world = player + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, 0f);
                var clip = Vector4.Transform(new Vector4(world, 1f), matrix);
                var own = new Vector2((clip.X / clip.W + 1f) * half.X, (1f - clip.Y / clip.W) * half.Y);
                var game = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

                lines.AppendLine($"  {distance,5:0} at {i * 45,3} degrees: depth {clip.W:0.###}, matrix ({own.X:0},{own.Y:0}), " +
                                 $"WorldToScreen ({game.X:0},{game.Y:0}){(InFront(gc, world) ? "" : ", cut as behind")}");
            }
        }

        return lines.ToString().TrimEnd();
    }

    /// <summary>Graphics.DrawCircleInWorld where the ring is in front of the camera. See RingInFront.</summary>
    internal static void Circle(Graphics graphics, GameController gc, Vector3 centre, float radius, Color colour, float thickness,
        int segments, bool followGround)
    {
        if (RingInFront(gc, centre, radius))
            graphics.DrawCircleInWorld(centre, radius, colour, thickness, segments, followGround);
    }

    /// <summary>Graphics.DrawFilledCircleInWorld where the circle is in front of the camera. See RingInFront.</summary>
    internal static void FilledCircle(Graphics graphics, GameController gc, Vector3 centre, float radius, Color colour,
        int segments, bool followGround)
    {
        if (RingInFront(gc, centre, radius))
            graphics.DrawFilledCircleInWorld(centre, radius, colour, segments, followGround);
    }
}
