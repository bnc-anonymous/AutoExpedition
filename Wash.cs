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
    private static (int Wide, int High, int Stamp, int Colour) _built = (0, 0, -1, 0);

    private static bool _have;

    /// <summary>Forgets the texture, so a new area builds its own. See Scouted.AreaChange.</summary>
    public static void Forget()
    {
        _built = (0, 0, -1, 0);
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
    public static bool Ready(Graphics graphics, Scouted scouted, Colour colour)
    {
        if (graphics == null || scouted is not { Ready: true })
            return false;

        var wide = scouted.Wide;
        var high = scouted.High;

        if (wide <= 0 || high <= 0)
            return false;

        var now = (wide, high, scouted.Stamp, colour.ToArgb());

        if (_have && now == _built)
            return true;

        try
        {
            using var image = new Image<Rgba32>(wide, high);
            var paint = new Rgba32(colour.R, colour.G, colour.B, colour.A);

            image.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < high; y++)
                {
                    var row = rows.GetRowSpan(y);

                    for (var x = 0; x < wide; x++)
                        row[x] = scouted.Unpainted(x, y) ? paint : default;
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
    public static (Vector2 A, Vector2 B, Vector2 C, Vector2 D) Corners(Graphics graphics,
        Scouted scouted)
    {
        var wide = scouted.Wide * Scouted.Tile;
        var high = scouted.High * Scouted.Tile;

        return (At(graphics, 0, 0), At(graphics, wide, 0),
            At(graphics, wide, high), At(graphics, 0, high));
    }

    public static nint Texture(Graphics graphics) =>
        _have ? Safe.Read(() => graphics.GetTextureId(Name), 0) : 0;

    private static Vector2 At(Graphics graphics, float x, float y)
    {
        var grid = new Vector2(x, y);

        return Safe.Read(() => graphics.GridToMap(grid, grid), Vector2.Zero);
    }
}
