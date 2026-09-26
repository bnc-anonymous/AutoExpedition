using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// What a hand-placed explosive cost, measured against the spot the plan asked for.
///
/// **Placing by hand is a supported way to play and it had no feedback at all.** The automated run
/// closes a loop on the game's own placement indicator, so it lands on the planned cell or says why
/// it could not - see <see cref="Placement"/>, and the triangles Overlay.Unlit draws for markers the
/// game refused to light. Somebody driving the cursor themselves has none of that. They aim at a
/// drawn circle, click, and the only thing that changes is the score: a little lower, and yellow.
/// Which content they just gave up, and whether they gave up any, is not said anywhere.
///
/// Two questions, and they deserve different answers because they lead to different actions:
///
/// **Off the spot.** The explosive is clearly meant for this link and is not on it. Worth saying
/// because the error compounds - the next link is measured from this one - but nothing has been lost
/// yet, and taking it back off to shave two grid would be a waste of a revert.
///
/// **Off the spot AND short of something.** Content the plan chose this link FOR is now outside the
/// blast. That is a decision to make while the explosive can still be taken back, and it needs to
/// name the markers rather than the count, because "one marker" says nothing about whether it was a
/// barrel or the runic monsters the whole chain was built around.
/// </summary>
internal static class Astray
{
    /// <summary>
    /// How far an explosive may sit from a planned link and still be taken as that link.
    ///
    /// **Half the minimum separation, which makes the answer unambiguous rather than tuned.** Two
    /// links of a chain are at least Apart from each other by construction, so within half of that
    /// there is exactly one link an explosive can belong to, and no threshold has to be guessed at.
    ///
    /// It used to be Exact everywhere, which is about a grid unit - far tighter than anybody places
    /// by hand. So a hand-laid chain read as a chain that had left the plan: every link counted as a
    /// deviation, the plan was dropped and re-solved after each one, and the drawn route the player
    /// was aiming at moved out from under them. That is the same number this widens, so the whole
    /// plugin now agrees about when an explosive IS a planned link, and disagrees only about whether
    /// it is exactly on it.
    ///
    /// Automation is unaffected either way: it lands inside Exact or it reports a failure.
    /// </summary>
    public static float Owns(AutoExpeditionSettings settings) =>
        Owned = MathF.Max(3f, Safe.Read(() => settings.Debug.ApartAtLeast.Value, 21f) * 0.5f);

    /// <summary>
    /// The same number for the static helpers that have no settings to hand.
    ///
    /// Overlay.Already and Placement.Next are both reached from places that were never given the
    /// settings tree, and threading it through half a dozen signatures to read one slider would be
    /// a worse trade than keeping the answer here. Refreshed once a tick by the plugin, so it is at
    /// worst a frame behind a setting nobody changes mid-site.
    /// </summary>
    public static float Owned { get; private set; } = 3f;

    /// <summary>Whether this explosive is the one laid for that planned link.</summary>
    public static bool Owns(AutoExpeditionSettings settings, Vector2 placed, Vector2 planned) =>
        Vector2.Distance(placed, planned) < Owns(settings);

    /// <summary>One explosive that did not land where it was meant to.</summary>
    /// <param name="Lost">
    /// The markers this link was chosen for that its blast no longer reaches, by grid position.
    /// Empty when the only thing wrong is the position itself.
    /// </param>
    internal sealed record Verdict(Vector2 At, Vector2 Meant, float Off, List<Vector2> Lost)
    {
        public bool Missed => Lost.Count > 0;
    }

    /// <summary>
    /// Every planned link with an explosive near it but not on it, and what that cost.
    ///
    /// Walked over the PLAN rather than the chain, so a link is matched to the explosive nearest it
    /// rather than by position in the list - which is what lets it read a chain laid out of order,
    /// and what keeps it honest when an undo has left a gap in the middle.
    ///
    /// The coverage test is the bare geometric one - blast radius against the marker's own extent,
    /// the same pair Planner.Catches uses - and deliberately not the planner's full test. That one
    /// also consults <see cref="Missed"/>, which records markers the GAME declined to light from a
    /// cell whatever the geometry said. Folding that in here would report "you missed it" for a
    /// marker no placement at that spot could have taken, which is not what the player did.
    /// </summary>
    public static List<Verdict> Of(GameController gc, AutoExpeditionSettings settings,
        Planning planning, List<Target> targets, float blast)
    {
        var plan = planning?.Plan;

        if (plan is not { Points.Count: > 0 } || blast <= 0f)
        {
            Last = plan is not { Points.Count: > 0 }
                ? "no plan to measure against"
                : "the blast radius has never been read";

            return null;
        }

        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed == null || placed.Length == 0)
        {
            Last = "nothing placed yet";

            return null;
        }

        var owns = Owns(settings);
        var found = new List<Verdict>();

        for (var i = 0; i < plan.Points.Count; i++)
        {
            var meant = plan.Points[i];
            var off = float.MaxValue;
            var at = Vector2.Zero;

            foreach (var was in placed)
            {
                var gap = Vector2.Distance(was, meant);

                if (gap < off)
                {
                    off = gap;
                    at = was;
                }
            }

            // Nothing laid for this link yet, or laid so far away it is somebody else's spot -
            // which is a deviation rather than a wobble, and Placement.Deviated says so already.
            if (at == Vector2.Zero || off >= owns)
                continue;

            var lost = new List<Vector2>();

            foreach (var wanted in plan.CaughtBy(i))
            {
                // The marker's own size, which is most of the answer for a barrel cluster and all
                // of it for a chest. Nearest by grid, because Catches records positions and the
                // extent lives on the target.
                var edge = blast + Extents.Of(Nearest(targets, wanted));

                if (Vector2.Distance(at, wanted) > edge)
                    lost.Add(wanted);
            }

            // Exact is exact. An explosive on the spot the plan asked for is nothing to report;
            // anything else is Inexact, whether or not it also cost a marker.
            if (lost.Count == 0 && off <= 0f)
                continue;

            found.Add(new Verdict(at, meant, off, lost));
        }

        Last = found.Count == 0
            ? $"every explosive down is on the spot the plan asked for, over {plan.Points.Count} links"
            : string.Join("; ", found.ConvertAll(v =>
                $"({v.At.X:0},{v.At.Y:0}) is {v.Off:0.#} grid off ({v.Meant.X:0},{v.Meant.Y:0})" +
                (v.Missed
                    ? $" and MISSES {v.Lost.Count}: " +
                      string.Join(" ", v.Lost.ConvertAll(l => $"({l.X:0},{l.Y:0})"))
                    : " and still catches everything it was chosen for")));

        return found.Count > 0 ? found : null;
    }

    /// <summary>
    /// What the last look found, for the dump.
    ///
    /// Written every time Of runs, which is once a frame while the site is drawn. The question it
    /// answers - "why is nothing being said about an explosive that plainly missed something" - has
    /// exactly two answers, the pairing and the geometry, and only the numbers tell them apart.
    /// </summary>
    public static string Last { get; private set; } = "has not looked yet";

    /// <summary>The marker standing at a grid position, for its extent and its name.</summary>
    public static Target Nearest(List<Target> targets, Vector2 grid)
    {
        if (targets == null)
            return null;

        Target best = null;

        // Its own number rather than Exact's: the positions Catches records ARE target grids, so
        // this is a lookup and not a tolerance, and it must not tighten because the label's
        // threshold did.
        var near = 1.5f;

        foreach (var target in targets)
        {
            var gap = Vector2.Distance(target.Grid, grid);

            if (gap >= near)
                continue;

            near = gap;
            best = target;
        }

        return best;
    }
}
