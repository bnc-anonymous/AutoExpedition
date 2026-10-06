using ExileCore2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Numerics;
using Colour = System.Drawing.Color;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// The unscouted ground, as one texture drawn once, rather than as a heap of translucent quads.
///
/// **The quads could not be made to look right, and the reason is in how they are filled.** The
/// patches tile exactly - disjoint sets of tiles, shared edges computed from the same grid
/// coordinate on both sides - but a filled polygon is anti-aliased by feathering it outward, so two
/// neighbours each cover the boundary pixels partially. Two coats of a deliberately transparent
/// colour read as a different, stronger colour, so every seam drew a bright line and a field of
/// patches read as a hatch over the map. Shrinking each quad by the feather width helped and did
/// not fix it: the feather is not exactly half a pixel, it varies with the edge's angle, and the
/// map's projection gives every patch a different one.
///
/// **Radar had already solved this and solved it properly.** It draws the entire walkable map as a
/// generated image: build an Image&lt;Rgba32&gt;, hand it to Graphics.AddOrUpdateImage, and put it on
/// screen with one AddImageQuad over four projected corners. There are no seams in an image, the
/// blend happens once per pixel by construction, and a hundred and forty quads become one.
///
/// A pixel per tile, which is 108 by 134 on an ordinary map - about fourteen thousand pixels, built
/// only when the ground actually changes. The texture is sampled smoothly, so the edges come out
/// soft rather than as the staircase the tile grid really is, which is both better looking and more
/// honest about a boundary that is a sampling artefact rather than a real line.
/// </summary>
internal static class Wash
{
    /// <summary>What the texture is registered under. One per plugin, reused every area.</summary>
    private const string Name = "autoexpedition_unscouted";

    /// <summary>
    /// What the last image was built from, so it is rebuilt when it would differ and not otherwise.
    ///
    /// The mask changes when the player walks and when a marker turns up, which is a few times a
    /// second at most; the frame draws sixty times a second. Rebuilding per frame would be fourteen
    /// thousand pixels of work to produce a byte-identical image.
    /// </summary>
    private static (int Wide, int High, int Stamp, int Colour, int Height) _built = (0, 0, -1, 0, 0);

    private static bool _have;

    /// <summary>Forgets the texture, so a new area builds its own. See Scouted.AreaChange.</summary>
    public static void Forget()
    {
        _built = (0, 0, -1, 0, 0);
        _have = false;
    }

    /// <summary>
    /// Builds the image if it is out of date, and says whether there is one to draw.
    ///
    /// Transparent everywhere the ground is known or is not being searched, the wash colour
    /// everywhere it is not. The alpha is the setting's own, so the colour picker still means what
    /// it says - the difference is that it is now applied once per pixel instead of once per quad
    /// overlapping that pixel.
    /// </summary>
    /// <summary>
    /// The layer's image, built again when anything it shows has changed. <paramref name="heightAt"/> is the grid
    /// position whose terrain height the corners are projected at; each tile is moved by its own height against that
    /// one, as the map draws it. See Corners and Scouted.HeightOfTile.
    /// </summary>
    public static bool Ready(Graphics graphics, Scouted scouted, Colour colour, Vector2 heightAt)
    {
        if (graphics == null || scouted is not { Ready: true })
            return false;

        var wide = scouted.Wide;
        var high = scouted.High;

        if (wide <= 0 || high <= 0)
            return false;

        // In steps of 25 world units, since a whole tile's move takes about 350 and a reference read off the player
        // would otherwise rebuild the image with every step across a slope.
        var reference = MathF.Round(scouted.HeightAt(heightAt) / 25f) * 25f;

        Reference = reference;
        var now = (wide, high, scouted.Stamp, colour.ToArgb(), (int)reference);

        if (_have && now == _built)
            return true;

        try
        {
            using var image = new Image<Rgba32>(wide, high);
            var paint = new Rgba32(colour.R, colour.G, colour.B, colour.A);

            image.ProcessPixelRows(rows =>
            {
                // Each tile moved up the map by its height above the reference, in whole tiles, both ways at once,
                // as Radar moves its pixels: a height of h moves a cell h / GridToWorld / 2 grid units back on each axis.
                for (var y = 0; y < high; y++)
                {
                    for (var x = 0; x < wide; x++)
                    {
                        if (!scouted.Unpainted(x, y))
                            continue;

                        var shift = (int)MathF.Round((scouted.HeightOfTile(x, y) - reference) /
                                                      Detonator.GridToWorld / 2f / Scouted.Tile);
                        var (tx, ty) = (x - shift, y - shift);

                        if (tx >= 0 && ty >= 0 && tx < wide && ty < high)
                            rows.GetRowSpan(ty)[tx] = paint;
                    }
                }
            });

            graphics.AddOrUpdateImage(Name, image);
        }
        catch (Exception)
        {
            // A texture that could not be built is a layer that does not draw, which is what the
            // caller already handles. It must not be what takes the render down.
            _have = false;

            return false;
        }

        _built = now;
        _have = true;

        return true;
    }

    /// <summary>
    /// Puts it on screen: one image quad over the four projected corners of the whole tile grid.
    ///
    /// **The corners are the map's, not a patch's, which is the one thing this gives up.** Every
    /// quad used to carry its own terrain height, so the wash followed the ground; a single image
    /// takes one height for the whole map and sits a little off where the ground rises. Radar draws
    /// the entire walkable map this way and it reads fine, which is the evidence for accepting it -
    /// and a wash saying "you have not been here" does not need to be height accurate.
    /// </summary>
    /// <summary>
    /// The image's corners on the map, each projected at the terrain height of <paramref name="heightAt"/> rather than
    /// its own, since the tiles have been moved by their heights against that one. See Ready.
    /// </summary>
    public static (Vector2 A, Vector2 B, Vector2 C, Vector2 D) Corners(Graphics graphics,
        Scouted scouted, Vector2 heightAt)
    {
        var wide = scouted.Wide * Scouted.Tile;
        var high = scouted.High * Scouted.Tile;

        return (At(graphics, 0, 0, heightAt), At(graphics, wide, 0, heightAt),
            At(graphics, wide, high, heightAt), At(graphics, 0, high, heightAt));
    }

    /// <summary>
    /// The image's corners on the large map projected as Radar projects its walkable map, or null when the large map is
    /// not up or cannot be read: from the large map's own centre and scale, about the player, at the player's model
    /// height against <paramref name="reference"/>, the height the tiles were moved against. See Ready.
    ///
    /// **Not GridToMap, which drew the layer a few pixels low.** Projected through GridToMap the layer agreed with
    /// GridToMap to the pixel at the site, the player and the highest and lowest tiles painted, and still sat below the
    /// map's own walkable outlines on an Exhumed Ruins site (2026-10-05): spilling past the edges at the bottom of each
    /// area, a gap at the top. Radar anchors its image on the player's model height rather than a terrain height read
    /// at a point, and lines up with the map with its offsets at nought.
    /// </summary>
    /// <param name="heightOffset">
    /// How far above the ground to draw the layer, in world units. See UnscoutedDisplaySettings.UnscoutedHeightOffset.
    /// </param>
    public static (Vector2 A, Vector2 B, Vector2 C, Vector2 D)? CornersAboutPlayer(GameController gc, Scouted scouted,
        float reference, float heightOffset = 0f)
    {
        var large = Safe.Read(gc, static g => g.IngameState.IngameUi.Map.LargeMap.AsObject<ExileCore2.PoEMemory.Elements.SubMap>(), null);

        if (large == null || !Safe.Read(large, static m => m.IsVisible, false))
            return null;

        var centre = Safe.Read(large, static m => m.MapCenter, Vector2.Zero);
        var scale = Safe.Read(large, static m => (float)m.MapScale, 0f);
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var unclamped = Safe.Read(gc, static g => g.Player.GetComponent<ExileCore2.PoEMemory.Components.Render>()?.UnclampedHeight, null);

        if (centre == Vector2.Zero || scale <= 0f || player == Vector2.Zero || unclamped is not { } height)
            return null;

        var wide = scouted.Wide * Scouted.Tile;
        var high = scouted.High * Scouted.Tile;

        return (AboutPlayer(0, 0), AboutPlayer(wide, 0), AboutPlayer(wide, high), AboutPlayer(0, high));

        Vector2 AboutPlayer(float x, float y)
        {
            var (dx, dy) = (x - player.X, y - player.Y);
            // Height is subtracted: in this projection a larger height moves a point down the screen, which is why
            // Radar passes the player's height negated. Added, a positive offset lowered the layer.
            var dz = (reference - heightOffset - height) / Detonator.GridToWorld;

            return centre + scale * new Vector2((dx - dy) * CameraCos, (dz - (dx + dy)) * CameraSin);
        }
    }

    /// <summary>The camera's tilt the large map is drawn at, as Radar takes it: 38.7 degrees.</summary>
    private static readonly float CameraCos = MathF.Cos(38.7f * MathF.PI / 180f);

    private static readonly float CameraSin = MathF.Sin(38.7f * MathF.PI / 180f);

    /// <summary>The height Ready last moved the tiles against, for the corners. See CornersAboutPlayer.</summary>
    public static float Reference { get; private set; }

    public static nint Texture(Graphics graphics) =>
        _have ? Safe.Read(() => graphics.GetTextureId(Name), 0) : 0;

    private static Vector2 At(Graphics graphics, float x, float y, Vector2 heightAt)
    {
        var grid = new Vector2(x, y);

        return Safe.Read(() => graphics.GridToMap(grid, heightAt), Vector2.Zero);
    }
}
