using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Which of the chain's links have stopped being drawn, because their explosives have gone off.
///
/// Before the site is set off every link is drawn. After, the chain goes off in order along the wire, and once a
/// link's explosive has gone off that link and every one before it stop being drawn, for good: the line left shows
/// the part of the chain still to go off.
///
/// **Gone off is the explosive gone.** Each explosive put down is an entity,
/// Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive, and the game deletes it when it goes off - expected
/// from play, not yet seen in a dump. So a link has gone off when no explosive stands at it while it is within
/// Debug.ScoutReach of the player, where the game holds every entity; out of that range a missing explosive says
/// nothing. Its states are not read for this: on a Lush Isle site two minutes after setting off (2026-10-05) the
/// four still ahead of the burn read activated=0, and what a state might read in the moment before deletion is not
/// known. Said prints them link by link, so a dump shows whether that holds. See Dump.Detonating.
///
/// **History.** Until 2026-09-28 a link was dropped once no live monster stood inside its blast, which kept lines up
/// through the fight; from then every link was dropped the moment the site was set off, which left no line at all.
///
/// **Set off by either signal, and remembered.** The live read of the detonator needs its entity loaded, and it
/// unloads as you walk into the site, reading -1. See Detonator.SetOffHere.
/// </summary>
internal sealed class Cleared
{
    private readonly HashSet<int> _done = new();
    private uint _area;
    private int _links;
    private DateTime _looked;

    /// <summary>Whether this link has stopped being drawn.</summary>
    public bool Is(int index) => _done.Contains(index);

    /// <summary>How many of the chain's links have stopped being drawn, for the readout.</summary>
    public int Count => _done.Count;

    /// <summary>What the last look at the explosives found, link by link, for the dump. See Dump.Detonating.</summary>
    public static string Said { get; private set; } = "not looked yet";

    /// <summary>
    /// How often the entity list is walked for the explosives. The whole list, since a spent detonator's things are
    /// not in the valid buckets (see Detonator.ExplosivesDetonated); a few thousand entities four times a second.
    /// </summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>How near an explosive must stand to a link to be that link's, in grid units.</summary>
    private const float SameSpot = 8f;

    /// <summary>
    /// Marks every link up to the last one whose explosive has gone off, once the site has been set off; none before.
    /// </summary>
    /// <param name="loadedWithin">How far from the player the game holds every entity, in grid units.</param>
    public void Observe(GameController gc, List<Vector2> points, float loadedWithin)
    {
        if (points == null || points.Count == 0)
            return;

        // A new plan is a new set of links. Judged on the count rather than the contents because a re-plan
        // mid-site keeps the links it has already placed, and those keep their indices.
        if (points.Count != _links)
        {
            _done.Clear();
            _links = points.Count;
        }

        if (!Detonator.SetOffHere(gc))
        {
            _done.Clear();

            return;
        }

        if (_done.Count == points.Count || DateTime.UtcNow - _looked < LookEvery)
            return;

        _looked = DateTime.UtcNow;

        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (entities == null || player == Vector2.Zero)
            return;

        // The explosives standing now, with their two states.
        var explosives = new List<(Vector2 At, long Activated, long Exploded)>();

        foreach (var entity in entities)
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (!metadata.EndsWith("Expedition/ExpeditionExplosive", StringComparison.Ordinal))
                continue;

            explosives.Add((Safe.Read(entity, static e => e.GridPos, Vector2.Zero),
                Target.Stated(entity, "activated"), Target.Stated(entity, "exploded")));
        }

        // Where each link's explosive went down: the game's own list while it still answers, the plan's spot after.
        var placed = Detonator.PlacedExplosiveGridPositions(gc);
        var said = new StringBuilder();
        var lastGoneOff = -1;

        for (var i = 0; i < points.Count; i++)
        {
            var at = i < placed.Length && placed[i] != Vector2.Zero ? placed[i] : points[i];
            var found = -1;

            for (var e = 0; e < explosives.Count; e++)
            {
                if (Vector2.Distance(explosives[e].At, at) <= SameSpot &&
                    (found < 0 || Vector2.Distance(explosives[e].At, at) < Vector2.Distance(explosives[found].At, at)))
                    found = e;
            }

            var inRange = Vector2.Distance(player, at) <= loadedWithin;
            var goneOff = found < 0 && inRange;

            if (goneOff)
                lastGoneOff = i;

            said.Append(i == 0 ? "" : "; ").Append($"{i + 1} ({at.X:0},{at.Y:0}) ");
            said.Append(found >= 0
                ? $"explosive activated={explosives[found].Activated} exploded={explosives[found].Exploded}"
                : inRange ? "no explosive, in range" : "no explosive, out of range");
            said.Append(goneOff ? " - gone off" : "");
        }

        for (var i = 0; i <= lastGoneOff; i++)
            _done.Add(i);

        Said = $"{explosives.Count} explosive(s) loaded, {_done.Count} of {points.Count} link(s) not drawn: {said}";
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
}
