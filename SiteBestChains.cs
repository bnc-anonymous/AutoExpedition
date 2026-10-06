using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The best chains every solve at this dig site finished with, kept until the site or the area changes - not cleared by
/// deleting the plan.
///
/// Deleting the plan throws away the chain on screen and the press pool with it, so whether a fresh solve can find its
/// way back to a route it held a minute ago could not be checked: the route was gone. Each entry keeps the chain, what
/// it scored when it was found, where its first link started from, and the table revision it was scored under, so a
/// later reader can score it again on the site as it stands now and see whether the search is falling short of it.
///
/// Recorded from Planning when a solve's plan is settled. See ChainPanel.
/// </summary>
internal static class SiteBestChains
{
    /// <summary>One chain a solve finished with.</summary>
    internal sealed record Entry(
        List<Vector2> Route,
        Vector2 Origin,
        double PlainThen,
        DateTime When,
        int Run,
        int Draw,
        int Revision);

    /// <summary>How many to keep, best first by what they scored when found.</summary>
    private const int Kept = 20;

    private static readonly List<Entry> _entries = new();

    private static uint _area;
    private static Vector2 _site;

    /// <summary>The chains, best first by what they scored when found. A copy, safe to read while solves record.</summary>
    public static List<Entry> Entries
    {
        get
        {
            lock (_entries)
                return new List<Entry>(_entries);
        }
    }

    /// <summary>The dig site the entries belong to.</summary>
    public static Vector2 Site => _site;

    /// <summary>
    /// Files a chain a solve finished with. A different area or dig site starts the list again; the same route already
    /// listed - every link within a grid unit - keeps the better of its two scores and the later time.
    /// </summary>
    public static void Record(uint area, Vector2 site, List<Vector2> route, Vector2 origin, double plain, int run,
        int draw, int revision)
    {
        if (route is not { Count: > 0 } || site == Vector2.Zero)
            return;

        lock (_entries)
        {
            if (area != _area || Vector2.Distance(site, _site) > 1f)
            {
                _entries.Clear();
                _area = area;
                _site = site;
            }

            var copy = new List<Vector2>(route);
            var entry = new Entry(copy, origin, plain, DateTime.Now, run, draw, revision);
            var same = _entries.FindIndex(x => SameRoute(x.Route, copy) && Vector2.Distance(x.Origin, origin) < 1f);

            if (same >= 0)
            {
                if (_entries[same].PlainThen >= plain)
                    entry = entry with { PlainThen = _entries[same].PlainThen };

                _entries.RemoveAt(same);
            }

            _entries.Add(entry);
            _entries.Sort((a, b) => b.PlainThen.CompareTo(a.PlainThen));

            if (_entries.Count > Kept)
                _entries.RemoveRange(Kept, _entries.Count - Kept);
        }
    }

    /// <summary>Empties the list, for the window's own button.</summary>
    public static void Clear()
    {
        lock (_entries)
            _entries.Clear();
    }

    /// <summary>Whether two routes visit the same spots in the same order, within a grid unit each.</summary>
    internal static bool SameRoute(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (Vector2.Distance(a[i], b[i]) >= 1f)
                return false;
        }

        return true;
    }
}
