using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;
using Graphics = ExileCore2.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Where the plugin BELIEVES the next explosive can reach, drawn on the ground cell by cell.
///
/// **The reach model has never been shown its own answer.** It is a scalar limit the game hands over -
/// see Reach - plus a router that floods walkable ground eight ways with a diagonal at root two and
/// admits a link when a route fits inside that limit. Both halves are plausible and neither has been
/// put beside what the client actually does: the only evidence either way has been a plan that needed
/// re-adjusting, which says something was wrong and nothing about which half.
///
/// So the belief is drawn. A green cross on every cell the model allows that borders one it refuses,
/// and a red cross on every cell it refuses that borders one it allows - a one-grid outline of the
/// plugin's own opinion. Sweeping the placement circle along it makes the disagreement visible as it
/// happens: the indicator going past a green cross means the model is too tight, stopping short of one
/// means it is too loose, and too loose is what shows as a spot the plan recommends and the game
/// then refuses.
///
/// **Cells rather than bearings, because a polar sweep cannot answer this.** Probing N directions
/// spaces the marks by the arc, so they crowd near the origin and thin out at the edge - which is
/// exactly where the boundary is. Walking cells gives one-grid resolution everywhere and shows shapes a
/// sweep cannot: a notch behind a pillar, or an island of reachable ground past an obstruction.
///
/// Debug only, and latched: the answer cannot move while the origin, the reach and the count of
/// measurements all stand still, so a sweep pays once and a still camera pays nothing.
/// </summary>
internal sealed class Border
{

    /// <summary>The detonator and every explosive down, which is what the chain runs between.</summary>
    internal static List<Vector2> Chain(GameController gc)
    {
        var centres = new List<Vector2>();
        var site = Detonator.DetonatorGridPosition(gc);

        if (site != Vector2.Zero)
            centres.Add(site);

        foreach (var at in Detonator.PlacedExplosiveGridPositions(gc) ?? [])
        {
            if (at != Vector2.Zero)
                centres.Add(at);
        }

        return centres;
    }

    /// <summary>
    /// Each explosive already down, crossed and numbered in the order the game holds them.
    ///
    /// Numbered from one, and the number is the game's own index - so "explosive 1" here and
    /// "explosive 1" in the placement readout are the same thing, which matters when the claim under
    /// test is about a direction between two of them. See Landed.
    /// </summary>
    public static void Placements(Graphics graphics, GameController gc, List<RectangleF> covered,
        string fixedSpots)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        var placed = Spots(fixedSpots) ?? Detonator.PlacedExplosiveGridPositions(gc);

        for (var i = 0; i < (placed?.Length ?? 0); i++)
        {
            Cross(graphics, gc, camera, placed[i], covered, Color.Cyan);

            var here = Ground(gc, placed[i]);

            if (here == Vector3.Zero)
                continue;

            var on = Safe.Read((camera, here), static x => x.camera.WorldToScreen(x.here),
                Vector2.Zero);

            if (on == Vector2.Zero || Panels.Covers(covered, on))
                continue;

            graphics.DrawText($"{i + 1}", on + new Vector2(6f, -16f), Color.Cyan);
        }
    }

    /// <summary>
    /// The spots written in the setting, or null when it names none. See Placements.
    ///
    /// Anything unparseable is skipped rather than defaulted, so a half-typed coordinate marks
    /// nothing instead of marking the wrong place while somebody is still typing the rest of it.
    /// </summary>
    private static Vector2[] Spots(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var found = new List<Vector2>();

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(',', StringSplitOptions.RemoveEmptyEntries);

            if (bits.Length != 2 ||
                !float.TryParse(bits[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(bits[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                continue;

            found.Add(new Vector2(x, y));
        }

        return found.Count > 0 ? found.ToArray() : null;
    }

    /// <summary>How far the cross reaches from its cell, in grid. Matches Overlay's SpoiltSize.</summary>
    private const float Size = 1.5f;

    /// <summary>
    /// One cross, drawn on the ground rather than on the screen.
    ///
    /// **The same construction Problem ground uses, for the reason its comment gives.** Two
    /// screen-space diagonals are the same shape wherever the camera points, so the mark floats above a
    /// site drawn at an angle instead of lying on it. Four corners taken along the world axes and
    /// projected individually give the ground's own X - and it has to be the AXES, not the diagonals:
    /// the map is drawn at forty five degrees, so grid diagonals project to a vertical and a horizontal
    /// and the mark turns into a plus sign.
    /// </summary>
    internal static void Cross(Graphics graphics, GameController gc, Camera camera, Vector2 grid,
        List<RectangleF> covered, Color colour)
    {
        var here = Ground(gc, grid);

        if (here == Vector3.Zero)
            return;

        var on = Safe.Read((camera, here), static x => x.camera.WorldToScreen(x.here), Vector2.Zero);

        if (on == Vector2.Zero || Panels.Covers(covered, on))
            return;

        var corners = new[]
        {
            Ground(gc, grid + new Vector2(-Size, 0f)),
            Ground(gc, grid + new Vector2(Size, 0f)),
            Ground(gc, grid + new Vector2(0f, -Size)),
            Ground(gc, grid + new Vector2(0f, Size)),
        };

        var screen = new Vector2[corners.Length];

        for (var i = 0; i < corners.Length; i++)
        {
            if (corners[i] == Vector3.Zero)
                return;

            screen[i] = Safe.Read((camera, corners[i]),
                static x => x.camera.WorldToScreen(x.Item2), Vector2.Zero);

            if (screen[i] == Vector2.Zero)
                return;
        }

        graphics.DrawLine(screen[0], screen[1], 1f, colour);
        graphics.DrawLine(screen[2], screen[3], 1f, colour);
    }

    private static Vector3 Ground(GameController gc, Vector2 grid) =>
        Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(grid), Vector3.Zero);

}
