using ExileCore2;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Which of the chain's links have stopped being drawn, because the chain has been set off.
///
/// A laid chain is a plan, and once it has gone off there is nothing left for the line to say: the explosives
/// are spent and no further one may be placed. So every link stops drawing the moment the site is set off.
///
/// **Not when the fighting is over.** This used to drop a link only once no live monster stood inside its blast,
/// on the reading that a detonated chain is a list of jobs. That kept yellow lines across the screen through the
/// whole fight, which read as a plan still to be placed.
///
/// **Set off by either signal, and remembered.** The live read of the detonator needs its entity loaded, and it
/// unloads as you walk into the site - it then reads -1, and this took that for "not set off", emptied the list,
/// and brought every link back. Measured: "set off yes" latched and Detonated() = -1 in one dump, with the player
/// 430 grid from the detonator. See Detonator.SetOffHere.
///
/// Per link rather than one switch, so a signal that says which explosive has gone off - the game's own
/// ExpeditionExplosiveFuse entities are a candidate, not yet confirmed - can drop them one at a time without
/// touching the callers.
/// </summary>
internal sealed class Cleared
{
    private readonly HashSet<int> _done = new();
    private uint _area;
    private int _links;

    /// <summary>Whether this link has stopped being drawn.</summary>
    public bool Is(int index) => _done.Contains(index);

    /// <summary>How many of the chain's links have stopped being drawn, for the readout.</summary>
    public int Count => _done.Count;

    /// <summary>Marks every link finished once the site has been set off, and none before.</summary>
    public void Observe(GameController gc, List<Vector2> points)
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

        for (var i = 0; i < points.Count; i++)
            _done.Add(i);
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
