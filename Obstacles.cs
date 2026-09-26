using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The things standing in the dig site that an explosive cannot be placed on or thrown past.
///
/// **Neither terrain grid knows about these, and that is why this exists.** The targeting grid
/// reads 5 under the detonator, under all ninety markers, and at every spot a plan wanted - one of
/// which the game refuses. A grid that says the same number everywhere is not describing anything.
/// What was actually in the way turned out to be an ENTITY: Metadata/MiscellaneousObjects/Doodad,
/// a rock standing on perfectly good ground. Terrain describes the ground; it has nothing to say
/// about what is standing on it.
///
/// Not only doodads. A doodad was what happened to be caught in the act, but nothing about the
/// problem is particular to them - anything standing in a dig site with a physical footprint is a
/// thing an explosive has to go round. So the rule is the other way about: everything in the
/// scenery bucket counts, and expedition content is excluded by name.
///
/// Excluding the content is the whole of the care needed here. Markers, remnants, the detonator and
/// the placement indicator all live in the same bucket as the scenery, and every one of them is
/// something the chain is meant to reach rather than avoid - an obstacle list that swallowed them
/// would refuse to plan at all. Anything without a readable footprint is skipped too, since an
/// obstacle of no size is one nothing can collide with.
///
/// Read once into a snapshot, for the same two reasons as the terrain: the search runs off the main
/// thread, and walking the entity list per candidate would be absurd.
///
/// The radius comes from the doodad's own Render bounds rather than a guess, which is the same
/// reading that told us a marker's art is 41 world units across. Floored, because a bounds of zero
/// would make an obstacle nothing can collide with, and that is worse than being slightly too fat.
/// </summary>
internal sealed class Obstacles
{
    private readonly (Vector2 At, float Radius)[] _found;

    private Obstacles((Vector2, float)[] found) => _found = found;

    /// <summary>How many are being avoided, for the readout.</summary>
    public int Count => _found.Length;

    /// <summary>Each obstacle, so a site can be written down and rebuilt later. See Capture.</summary>
    public IEnumerable<(Vector2 At, float Radius)> All => _found;

    /// <summary>Rebuilds a set from a capture, rather than from the entity list. See Capture.</summary>
    public static Obstacles From((Vector2, float)[] found) => new(found ?? []);

    /// <summary>The smallest an obstacle is taken to be, in grid units.</summary>
    private const float Least = 1.5f;

    /// <summary>
    /// Everything in the way near this dig site.
    ///
    /// Limited to the site because a map holds doodads everywhere and the chain cannot leave the
    /// site - so the rest are cells nothing will ever ask about, and each one costs a distance test
    /// on every segment the search considers.
    /// </summary>
    public static Obstacles Read(GameController gc, Vector2 site, float range)
    {
        var found = new List<(Vector2, float)>();

        // **Both buckets, because the thing that blocks is not always filed as an object.** This
        // read MiscellaneousObjects alone, and a Peninsula dig fills its ground with goblin huts
        // that the game files under Terrain - fifteen cells square, solid, and invisible to a
        // router that only looked in one bucket. The chain planned straight through them.
        var entities = new List<Entity>();

        foreach (var bucket in new[] { EntityType.MiscellaneousObjects, EntityType.Terrain })
        {
            var kind = bucket;
            var of = Safe.Read(
                () => gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(kind, out var found)
                    ? found
                    : null, null);

            if (of != null)
                entities.AddRange(of);
        }

        if (entities.Count == 0 || site == Vector2.Zero)
            return new Obstacles([]);

        foreach (var entity in entities)
        {
            if (!Blocking(entity))
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

            if (at == Vector2.Zero || Vector2.Distance(at, site) > range)
                continue;

            var bounds = Safe.Read(entity, static e => e.GetComponent<Render>()?.Bounds.X ?? 0f, 0f);

            // The bounds are the same 41.3 world units on every one of them - the figure a marker's
            // art reports too - so this is a default rather than a measurement of anything, and the
            // radius is uniform whatever is standing there. Kept because 3.8 grid is a plausible
            // size for a rock and a guessed constant would be no better founded.
            found.Add((at, MathF.Max(Least, bounds / Detonator.GridToWorld)));
        }

        return new Obstacles(found.ToArray());
    }

    /// <summary>
    /// Whether this entity is one of the things that actually blocks.
    ///
    /// The game says which, and it says so in the name. A dig site holds four kinds of scenery and
    /// only one of them is solid:
    ///
    /// <code>
    /// Doodad                 31 - the solid ones
    /// DoodadNoOverlap        26 - things may not OVERLAP it, which is the placement rule
    /// DoodadNoBlocking      200 - says outright that it does not block
    /// Doodad_Distant_4       20 - background scenery, not in the site at all
    /// DoodadInvisible         1 - nothing to walk into
    /// </code>
    ///
    /// **NoOverlap was missed, and it is the one that matters most.** The note below predicted a new
    /// kind of solid scenery would be missed and said that finding one was a matter of looking. On a
    /// later site the whole dig held exactly ONE entity ending in the plain `/Doodad` and twenty-six
    /// `DoodadNoOverlap`, so the filter was reading one obstacle in five hundred and seventeen.
    ///
    /// The name is the argument. Blocking is about MOVEMENT and an explosive does not walk, which is
    /// why NoBlocking is rightly ignored - but "no overlap" is about two things occupying the same
    /// ground, which is exactly what placing an explosive on a rock would be.
    ///
    /// This was briefly written the other way round, as "everything that is not expedition
    /// content", on the reasoning that the list of obstacles is open ended. It is not: two hundred
    /// of the two hundred and fifty two things standing in that dig site are explicitly labelled as
    /// not blocking, and treating them as walls would have hemmed the chain in far worse than
    /// missing the one rock did.
    ///
    /// So it is an exact match on the plain name. A new kind of solid scenery would be missed, and
    /// that is the right way round to be wrong - the dump lists everything standing in the site, so
    /// finding one is a matter of looking rather than of guessing.
    /// </summary>
    private static bool Blocking(Entity entity)
    {
        var meta = Safe.Read(entity, static e => e.Metadata, "") ?? "";

        // **Ends with Doodad, rather than ends with slash-Doodad.** The narrower test was written
        // against Metadata/MiscellaneousObjects/Doodad and matched nothing else; a Peninsula dig's
        // GoblinSwapDoodad is fifteen cells of solid hut and fell straight through it.
        //
        // The names that say they do NOT block still do not: DoodadNoBlocking and DoodadNoWobble
        // both fail this test, because neither ends in the word. That is the whole reason to match
        // the end of the name rather than to look for Doodad anywhere in it.
        return meta.EndsWith("Doodad", StringComparison.OrdinalIgnoreCase) ||
               meta.EndsWith("/DoodadNoOverlap", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every doodad of any kind near the site, with the kind carried, for the capture.
    ///
    /// **Separate from Read on purpose.** Read answers "what does the router treat as solid", which is
    /// a decision; this answers "what is actually standing here", which is evidence. Writing all of
    /// them down is what lets the decision be checked afterwards against the game's own verdicts
    /// rather than argued from the names.
    /// </summary>
    public static List<(string Kind, Vector2 At, float Radius)> Every(GameController gc, Vector2 site,
        float range)
    {
        var found = new List<(string, Vector2, float)>();

        var entities = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.MiscellaneousObjects, out var of)
                ? of
                : null, null);

        if (entities == null || site == Vector2.Zero)
            return found;

        foreach (var entity in entities)
        {
            var meta = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (meta.IndexOf("Doodad", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

            if (at == Vector2.Zero || Vector2.Distance(at, site) > range)
                continue;

            var bounds = Safe.Read(entity, static e => e.GetComponent<Render>()?.Bounds.X ?? 0f, 0f);
            var slash = meta.LastIndexOf('/');

            found.Add((slash >= 0 ? meta[(slash + 1)..] : meta, at,
                MathF.Max(Least, bounds / Detonator.GridToWorld)));
        }

        return found;
    }

    /// <summary>Whether an explosive could be put down here, or whether something is standing on it.</summary>
    /// <summary>
    /// Marks every cell these obstacles stand on, once, instead of being asked cell by cell.
    ///
    /// Covers() walks the whole list for one point, which is the right shape for a handful of
    /// questions and the wrong one for a flood: routing judges hundreds of thousands of cells and
    /// each was costing a pass over every doodad in the dig site. Stamping is the transpose - each
    /// obstacle paints the few dozen cells it actually occupies - and turns millions of distance
    /// tests into thousands.
    /// </summary>
    public void Stamp(Action<int, int> blocked)
    {
        foreach (var (where, radius) in _found)
        {
            var reach = (int)MathF.Ceiling(radius);

            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius)
                        continue;

                    blocked((int)MathF.Round(where.X) + dx, (int)MathF.Round(where.Y) + dy);
                }
            }
        }
    }

    public bool Covers(Vector2 at)
    {
        foreach (var (where, radius) in _found)
        {
            if (Vector2.DistanceSquared(at, where) <= radius * radius)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether anything stands between two links.
    ///
    /// Point against segment rather than sampling along it: the distance from an obstacle's centre
    /// to the line is one dot product and a clamp, where walking the segment cell by cell would
    /// both cost more and miss a thin obstacle between two samples.
    /// </summary>
    public bool Blocks(Vector2 from, Vector2 to)
    {
        foreach (var (where, radius) in _found)
        {
            if (Near(where, from, to) <= radius)
                return true;
        }

        return false;
    }

    private static float Near(Vector2 point, Vector2 from, Vector2 to)
    {
        var along = to - from;
        var length = along.LengthSquared();

        if (length < 0.0001f)
            return Vector2.Distance(point, from);

        var t = Math.Clamp(Vector2.Dot(point - from, along) / length, 0f, 1f);

        return Vector2.Distance(point, from + along * t);
    }

    /// <summary>
    /// The obstacles nearest this segment, for the dump.
    ///
    /// The distance is from the obstacle's centre to the line; it blocks when that is inside its
    /// radius. Printed with the radius beside it so a near miss is distinguishable from a hit, and
    /// so a radius that is wrong by a unit or two shows up as a string of near misses rather than
    /// as silence.
    /// </summary>
    public string Along(Vector2 from, Vector2 to)
    {
        if (_found.Length == 0)
            return "nothing in the way";

        var best = new List<(float Gap, float Radius, Vector2 At)>();

        foreach (var (where, radius) in _found)
            best.Add((Near(where, from, to) - radius, radius, where));

        best.Sort(static (a, b) => a.Gap.CompareTo(b.Gap));

        var text = new List<string>();

        for (var i = 0; i < best.Count && i < 3; i++)
        {
            var (gap, radius, at) = best[i];

            text.Add($"({at.X:0},{at.Y:0}) r{radius:0.#} {(gap <= 0f ? "BLOCKS" : $"clears by {gap:0.#}")}");
        }

        return string.Join(", ", text);
    }

    /// <summary>What was found, for the dump.</summary>
    public string Describe()
    {
        if (_found.Length == 0)
            return "none near the dig site";

        var biggest = 0f;
        var total = 0f;

        foreach (var (_, radius) in _found)
        {
            biggest = MathF.Max(biggest, radius);
            total += radius;
        }

        return $"{_found.Length}, radius {total / _found.Length:0.#} grid on average, {biggest:0.#} at most";
    }
}
