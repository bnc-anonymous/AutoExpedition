using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Which of the chain's blasts have been fought out, so the route can stop showing them.
///
/// A laid chain is a plan; a detonated chain is a list of jobs. Once the explosives have gone off
/// the line on the ground stops meaning "put one here" and starts meaning "there are things alive
/// over there" - and the useful version of that only shows the parts still true. So after
/// detonation a link is dropped from the drawing when nothing it unearthed is left standing, and
/// when the last one goes there is no line at all.
///
/// **Cleared is defined as no live monster inside that blast's radius, and it is sticky.** Sticky
/// because the alternative flickers: monsters wander, so an area that has been emptied can be
/// walked back into by a straggler from the next blast along, and a segment that reappears after
/// you have finished with it is worse than one that lingers. Once an area has been seen empty it
/// stays cleared until the plan changes or the area does.
///
/// The radius is the blast's own, which is the right question: the monsters that came out of a
/// blast are the ones standing in it. Anything that has walked further than that is somebody else's
/// problem, and the next link along usually owns it.
///
/// Nothing here runs before detonation. While the chain is being laid every link is still to be
/// placed and the whole route is worth seeing, which is what the placed-spot rules already handle.
/// </summary>
internal sealed class Cleared
{
    private readonly HashSet<int> _done = new();
    private uint _area;
    private int _links;

    /// <summary>Whether this link has been fought out and should stop being drawn.</summary>
    public bool Is(int index) => _done.Contains(index);

    /// <summary>How many of the chain's blasts are finished with, for the readout.</summary>
    public int Count => _done.Count;

    /// <summary>
    /// Looks at what is still alive around each link that is not already finished with.
    ///
    /// Only the unfinished ones are tested, so the work falls away as the site is cleared - by the
    /// end this walks the monster list for nothing at all. It is one pass over the monsters per
    /// link rather than a spatial index because a chain is five links and the alternative is an
    /// index rebuilt every frame for a list that changes every frame.
    /// </summary>
    public void Observe(GameController gc, List<Vector2> points, float radius)
    {
        if (points == null || points.Count == 0 || radius <= 0f)
            return;

        // A new plan is a new set of jobs. Judged on the count rather than the contents because a
        // re-plan mid-site keeps the links it has already placed, and those keep their indices.
        if (points.Count != _links)
        {
            _done.Clear();
            _links = points.Count;
        }

        // Before the chain goes off there is nothing to have cleared, and every link is still a
        // placement rather than a fight.
        if (Detonator.ExplosivesDetonated(gc) < 1)
        {
            _done.Clear();

            return;
        }

        if (_done.Count == points.Count)
            return;

        var monsters = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var of)
                ? of
                : null, null);

        if (monsters == null)
            return;

        for (var i = 0; i < points.Count; i++)
        {
            if (_done.Contains(i))
                continue;

            if (!Alive(monsters, points[i], radius))
                _done.Add(i);
        }
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _done.Clear();
            _links = 0;
        }

        _area = areaHash;
    }

    /// <summary>Whether anything hostile is still standing inside this blast.</summary>
    private static bool Alive(List<Entity> monsters, Vector2 at, float radius)
    {
        foreach (var monster in monsters)
        {
            if (!Safe.Read(monster, static e => e.IsAlive, false) ||
                !Safe.Read(monster, static e => e.IsHostile, false))
                continue;

            var grid = Safe.Read(monster, static e => e.GridPos, Vector2.Zero);

            if (grid != Vector2.Zero && Vector2.Distance(grid, at) <= radius)
                return true;
        }

        return false;
    }
}
