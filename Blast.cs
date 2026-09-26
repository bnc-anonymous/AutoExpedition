using ExileCore2;
using System;

namespace AutoExpedition;

/// <summary>
/// The explosion radius, worked out from the game's own expression rather than learned from it.
///
/// **Computed, not read.** `Detonator.ExplosionRadius` is `0x1417350B0` reimplemented - a base of 30
/// or 37 by the same area flag that makes the reach 90 or 108, scaled by
/// MapExpeditionExplosionAreaOfEffectPct through a square root and by
/// MapExpeditionExplosionRadiusPct directly, truncated. Both inputs are map stats, readable from the
/// moment the area loads.
///
/// That replaced a learned number and the thing that made it necessary. The radius can only be READ
/// while the placement indicator is up, so the first plan of every map used to need the placement key
/// pressed once, or a value remembered from an earlier map - and the remembered one was a single
/// number covering two bases, which is how a Grand site's 37 came to be used on an ordinary site
/// whose blast is 30.
///
/// The art is still read, for one reason: the routine adds `8 * n` to the base for a term this does
/// not model. Every reading so far has come back at the base exactly, so n is nought in ordinary
/// play. If one ever does not, Disagrees says so rather than the plan quietly being wrong.
/// </summary>
internal sealed class Blast
{
    private uint _area;
    private float _art;

    /// <summary>Whether the game's own circle has been seen in this map, which is only a check.</summary>
    public bool Measured => _art > 0f;

    /// <summary>
    /// How the figure was arrived at, term by term.
    ///
    /// Prints the computed answer and the drawn one side by side, because the whole reason the art is
    /// still read is to notice them parting company. See Disagrees.
    /// </summary>
    public string Spelled(GameController gc, AutoExpeditionSettings settings)
    {
        var art = Detonator.ArtRadius(gc);
        var computed = Detonator.ExplosionRadius(gc);
        var correction = settings.Debug.CircleCorrection.Value;
        var (area, radius) = Detonator.Modifiers(gc);

        return $"base {(Detonator.Grand(gc) ? Detonator.GrandBlast : Detonator.OrdinaryBlast):0.#} " +
               $"({(Detonator.Grand(gc) ? "grand" : "ordinary")}) x modifiers " +
               $"{Detonator.ModifierScale(gc):0.###} (area {area}%, radius {radius}%) " +
               $"= {computed:0.##}, correction {correction:0.##} -> " +
               $"using {Radius(gc, settings)?.ToString("0.##") ?? "nothing"}; " +
               $"art now {(art is > 0f ? art.Value.ToString("0.##") : "not drawn")}, " +
               $"art this map {(_art > 0f ? _art.ToString("0.##") : "none read yet")}" +
               (Disagrees(gc) is { } gap ? $" - DISAGREES by {gap:0.##}, see ExplosionRadius" : "");
    }

    /// <summary>
    /// By how much the drawn circle differs from the computed one, or null when they agree.
    ///
    /// **The one check on the model.** A gap means the `8 * n` term of `0x1417350B0` is not nought
    /// here, and that is a fact about the routine rather than about this map - so it wants finding
    /// out rather than correcting for.
    ///
    /// Half a grid of slack, because the art is a rendered size and the rule is an integer.
    /// </summary>
    public float? Disagrees(GameController gc)
    {
        if (_art <= 0f)
            return null;

        var gap = _art - Detonator.ExplosionRadius(gc);

        return MathF.Abs(gap) > 0.5f ? gap : null;
    }

    /// <summary>
    /// The explosion radius in grid units, with the circle correction applied.
    ///
    /// Never null any more. It was, and the caller that cared refused to solve with "the blast radius
    /// has never been read - show the placement circle once, ever". Nothing has to be shown now.
    /// </summary>
    public float? Radius(GameController gc, AutoExpeditionSettings settings) =>
        MathF.Max(1f, Detonator.ExplosionRadius(gc) + settings.Debug.CircleCorrection.Value);

    /// <summary>Takes the reading, which is kept only to be checked against. See Disagrees.</summary>
    public void Observe(GameController gc)
    {
        var art = Detonator.ArtRadius(gc);

        if (art is > 0f)
            _art = art.Value;
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
            _art = 0f;

        _area = areaHash;
    }
}
