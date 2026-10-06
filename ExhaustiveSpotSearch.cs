using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Lays a few consecutive stops of a chain again, exactly - every spot that catches each stop considered, none
/// sampled away: every order of them, over every spot that catches each,
/// keeping the rest of the chain as it is. The stops are the remnants, the relics that multiply what follows and the
/// must-takes, as in RemnantOrder.
///
/// **Why exactly, and why every order.** On a Frigid Bluffs site (2026-10-04) the best chain by hand ended
/// (435,649) (491,737) (508,844) (581,902) (615,803): a relic and a small remnant in one blast, then the 8-socket
/// remnant last, 1,260 points above the solver's ending, which took the 8-socket remnant two links earlier. Every
/// spot of that ending sits at the edge of reach - (491,737) reaches (508,844) and (435,649) where (491,743) and
/// (490,739), a few cells away, do not - so the searches that keep a spread of spots per step lost it, and the moves
/// that change a link or two at a time could not get there through chains that scored worse. A window keeps every
/// spot that catches a stop as a state of its own, so a spot at the edge of reach survives, and tries the window's
/// stops in each order, since the ending wanted is a different order.
/// </summary>
internal static class ExhaustiveSpotSearch
{
    /// <summary>
    /// The chain with the first window of stops that improves it laid again, trying each window from the end of the
    /// chain back, or null when none does. One pass; the caller repeats it while it finds something.
    ///
    /// **The first window that improves it, not the one that improves it most.** Every window was tried and the best
    /// kept, so a pass cost every window whatever the first found - and a window of six stops costs about four times
    /// one of five: on a Frigid Bluffs site (2026-10-04) a pass of sixes took 14.7 s offline, the gain coming from the
    /// tail, the first window tried. The callers search again after a gain, so what a later window would have found is
    /// found then. See Repair.PassOfWindows.
    /// </summary>
    /// <param name="size">How many consecutive stops a window holds.</param>
    /// <param name="freeBefore">
    /// How many links before a window's first stop may move as well. One is needed where the ending wanted only
    /// works from a different link before it: (491,737) is reached from (435,649) but not from (428,639).
    /// </param>
    /// <summary>
    /// As RelayEachWindow over every size from <paramref name="smallest"/> to <paramref name="largest"/> at once, taking
    /// the windows by where they end, from the end of the chain back, each size in turn at each end: the same windows
    /// as a pass of each size, in an order that reaches every window at the tail before any further in. Null when none
    /// improves the chain.
    /// </summary>
    internal static List<Vector2> RelayEachWindowByEnd(PlanEnvironment env, List<Vector2> chain, int smallest,
        int largest, int freeBefore, Func<bool> stop = null)
    {
        var view = ViewOf(env);
        var stopsInOrder = StopsInChainOrder(env, chain, view.Catching.Keys.ToHashSet());
        var score = Planner.Score(env, chain);

        for (var last = stopsInOrder.Count - 1; last >= smallest - 1; last--)
        {
            for (var size = smallest; size <= largest; size++)
            {
                var first = last - size + 1;

                if (first < 0)
                    break;

                if (stop?.Invoke() == true)
                    return null;

                var relaid = RelayWindow(env, chain, view, stopsInOrder, first, size, freeBefore);

                if (relaid != null && Planner.Score(env, relaid) > score + 0.0001d)
                    return relaid;
            }
        }

        return null;
    }

    /// <summary>
    /// For the offline harness: every window of this size over the chain, from the end back - its stops by the links
    /// catching them, the links it lays again, and the best score it reaches or "no path". See RelayWindow.
    /// </summary>
    internal static List<string> WindowsSaid(PlanEnvironment env, List<Vector2> chain, int size, int freeBefore)
    {
        var view = ViewOf(env);
        var stopsInOrder = StopsInChainOrder(env, chain, view.Catching.Keys.ToHashSet());
        var score = Planner.Score(env, chain);
        var said = new List<string>
        {
            $"stops in chain order (target at link): {string.Join(" ", stopsInOrder.Select(x => $"{env.Targets[x.Stop].Grid.X:0},{env.Targets[x.Stop].Grid.Y:0}@{x.Link + 1}"))}"
        };

        for (var first = stopsInOrder.Count - size; first >= 0; first--)
        {
            var window = stopsInOrder.GetRange(first, Math.Min(size, stopsInOrder.Count - first));
            var from = Math.Max(0, window.Min(x => x.Link) - freeBefore);
            var to = window.Max(x => x.Link);
            var relaid = RelayWindow(env, chain, view, stopsInOrder, first, size, freeBefore);

            said.Add($"window at stops {first + 1}-{first + window.Count}, links {from + 1}-{to + 1}: " +
                     (relaid == null ? "no path" : $"{Planner.Score(env, relaid) - score:+#,##0.0;-#,##0.0;0}"));
        }

        return said;
    }

    /// <summary>For the offline harness: how many neighbours the chain's own spots have on the ground. See SpotGround.Next.</summary>
    internal static string NeighboursOfChainSaid(PlanEnvironment env, List<Vector2> chain)
    {
        var view = ViewOf(env);
        var counts = chain.Select(x => view.Ground.IndexOf(x)).Where(x => x >= 0)
            .Select(x => view.Ground.Next(env, x).Count).ToList();

        return counts.Count == 0
            ? "none of the chain's links is a ground spot"
            : $"{view.Ground.Spots.Count:N0} spots on the ground; the chain's links have {counts.Min()}-{counts.Max()} neighbours, " +
              $"{counts.Average():N0} on average ({string.Join(" ", counts)})";
    }

    internal static List<Vector2> RelayEachWindow(PlanEnvironment env, List<Vector2> chain, int size, int freeBefore,
        Func<bool> stop = null)
    {
        var view = ViewOf(env);
        var stopsInOrder = StopsInChainOrder(env, chain, view.Catching.Keys.ToHashSet());
        var score = Planner.Score(env, chain);

        for (var first = stopsInOrder.Count - size; first >= 0; first--)
        {
            if (stop?.Invoke() == true)
                break;

            var relaid = RelayWindow(env, chain, view, stopsInOrder, first, size, freeBefore);

            if (relaid != null && Planner.Score(env, relaid) > score + 0.0001d)
                return relaid;
        }

        return null;
    }

    /// <summary>
    /// The best chain laying the window of stops starting at <paramref name="first"/> again, in any order, from the
    /// link before it (or the one before that, with freeBefore) to the link after its last stop, or null when nothing
    /// fits. The links the window replaces are the ones from the first free link to the link catching its last stop.
    /// </summary>
    private static List<Vector2> RelayWindow(PlanEnvironment env, List<Vector2> chain, GroundView view,
        List<(int Stop, int Link)> stopsInOrder, int first, int size, int freeBefore)
    {
        var window = stopsInOrder.GetRange(first, Math.Min(size, stopsInOrder.Count - first));

        if (window.Count == 0)
            return null;

        // The links replaced: from the free links before the first stop's link to the last stop's link.
        var from = Math.Max(0, window.Min(x => x.Link) - freeBefore);
        var to = window.Max(x => x.Link);
        var prefix = chain.GetRange(0, from);
        var suffix = chain.GetRange(to + 1, chain.Count - to - 1);
        var links = env.Explosives - prefix.Count - suffix.Count;
        var anchor = from == 0 ? env.Origin : chain[from - 1];

        // Where the window starts from as one of the ground's spots, so its neighbours can be read; a link that is not
        // one of them, a spot placed by hand or slid by the polish, is asked about directly.
        var anchorSpot = from == 0 ? SpotGround.Detonator : view.Ground.IndexOf(anchor);
        var after = suffix.Count > 0 ? suffix[0] : (Vector2?)null;
        var stops = window.Select(x => x.Stop).Distinct().ToList();

        if (links < 1)
            return null;

        List<Vector2> best = null;
        var most = double.NegativeInfinity;

        foreach (var order in Orders(stops))
        {
            foreach (var middle in Paths(env, view, anchor, anchorSpot, after, order, links, window.Min(x => x.Link) - from))
            {
                var whole = new List<Vector2>(prefix.Count + middle.Count + suffix.Count);

                whole.AddRange(prefix);
                whole.AddRange(middle);
                whole.AddRange(suffix);

                if (!Repair.Sound(env, whole))
                    continue;

                var worth = Planner.Score(env, whole);

                if (worth > most)
                {
                    most = worth;
                    best = whole;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Every way through the stops in this order, a layer per stop: the free links first, then each stop at any spot
    /// catching it reached in one link from a spot of the layer before, or at no cost when that spot already catches
    /// it. Each spot keeps its own few best paths into it - fewest links, then the most caught on the way - so a spot
    /// is never dropped for a richer one beside it. Within the links allowed, and ending where the link after the
    /// window is reached.
    ///
    /// **A step read off the ground's neighbours, not asked pair by pair.** A spot's neighbours hold every spot within
    /// reach that takes a remnant or a multiplying relic, so the spots catching the next stop that a spot reaches are
    /// its neighbours among them; the free links take the neighbours as well, the long throws in each direction with
    /// them. Only a window starting from a link that is not one of the ground's spots asks the router directly. See
    /// SpotGround.Next.
    /// </summary>
    private static IEnumerable<List<Vector2>> Paths(PlanEnvironment env, GroundView view, Vector2 anchor, int anchorSpot,
        Vector2? after, List<int> order, int links, int free)
    {
        var spots = view.Ground.Spots;
        var catching = view.Catching;

        // A partial path: its spots, as indices, and its rank among paths into the same spot: fewest links, then what
        // its spots catch.
        var layer = new Dictionary<int, List<(List<int> Path, double Worth)>> { [-1] = new() { (new List<int>(), 0d) } };

        double Ranked(List<int> path)
        {
            var caught = new HashSet<int>();

            foreach (var s in path)
                caught.UnionWith(view.CaughtBy[s]);

            return -LinkWorth * path.Count + caught.Sum(t => Planner.WorthOfTarget(env.Targets[t]));
        }

        Vector2 At(int spot) => spot < 0 ? anchor : spots[spot];

        // The spots one link on: the ground's neighbours, or for an anchor that is not one of its spots every spot it
        // reaches. -1 is the anchor in a layer. See SpotGround.Next.
        IEnumerable<int> Onward(int at)
        {
            var spot = at >= 0 ? at : anchorSpot;

            if (spot >= 0 || spot == SpotGround.Detonator)
                return view.Ground.Next(env, spot);

            return Enumerable.Range(0, spots.Count).Where(s =>
                Planner.Span(anchor, spots[s]) <= env.Reach && Planner.Reaches(env, anchor, spots[s]));
        }

        void Keep(Dictionary<int, List<(List<int> Path, double Worth)>> into, int spot, List<int> path, double worth)
        {
            if (!into.TryGetValue(spot, out var kept))
                into[spot] = kept = new List<(List<int>, double)>();

            if (kept.Any(x => x.Path.SequenceEqual(path)))
                return;

            kept.Add((path, worth));
            kept.Sort((a, b) => b.Worth.CompareTo(a.Worth));

            if (kept.Count > PathsPerSpot)
                kept.RemoveAt(kept.Count - 1);
        }

        // The free links: any spot one link on, kept only if it leads somewhere, which the next layer decides.
        for (var f = 0; f < free; f++)
        {
            var next = new Dictionary<int, List<(List<int> Path, double Worth)>>();

            foreach (var (at, paths) in layer)
            {
                foreach (var s in Onward(at))
                {
                    foreach (var (path, worth) in paths)
                    {
                        if (path.Contains(s))
                            continue;

                        var longer = new List<int>(path) { s };

                        Keep(next, s, longer, Ranked(longer));
                    }
                }
            }

            layer = next;
        }

        foreach (var stop in order)
        {
            var next = new Dictionary<int, List<(List<int> Path, double Worth)>>();

            if (!catching.TryGetValue(stop, out var goals))
                yield break;

            var goalSet = view.CatchingSets[stop];

            // A must-take that is neither a remnant nor a multiplying relic is not among the spots every neighbour list
            // holds, so the spots catching it are asked about directly. See SpotGround.Endings.
            var amongNeighbours = env.Targets[stop].Kind == TargetKind.Remnant || RemnantOrder.Multiplies(env.Targets[stop]);

            foreach (var (at, paths) in layer)
            {
                // Already caught where the path stands: no link spent.
                if (at >= 0 && goals.Contains(at))
                {
                    foreach (var (path, worth) in paths)
                        Keep(next, at, path, worth);

                    continue;
                }

                var reached = amongNeighbours
                    ? Onward(at).Where(goalSet.Contains)
                    : goals.Where(g => Planner.Span(At(at), spots[g]) <= env.Reach && Planner.Reaches(env, At(at), spots[g]));

                foreach (var goal in reached)
                {
                    foreach (var (path, worth) in paths)
                    {
                        if (path.Count >= links || path.Contains(goal))
                            continue;

                        var longer = new List<int>(path) { goal };

                        Keep(next, goal, longer, Ranked(longer));
                    }
                }
            }

            layer = next;

            if (layer.Count == 0)
                yield break;
        }

        foreach (var (at, paths) in layer)
        {
            if (after != null && (Planner.Span(At(at), after.Value) > env.Reach || !Planner.Reaches(env, At(at), after.Value)))
                continue;

            foreach (var (path, _) in paths)
                yield return path.Select(x => spots[x]).ToList();
        }
    }

    /// <summary>
    /// How many paths into one spot a layer keeps, best first. More than one, since what a path caught on the way
    /// changes what the rest is worth. Chosen, not measured. See Paths.
    /// </summary>
    private const int PathsPerSpot = 3;

    /// <summary>What one more link counts against a path in Paths' ranking: more than anything a link catches.</summary>
    private const double LinkWorth = 1e7;

    /// <summary>
    /// The shared ground as one solve's scan sees it: what each spot takes, as the scan's indices, and the spots
    /// catching each stop. Mapped from the ground's places once per scan on each thread, which costs milliseconds; the
    /// ground itself, the spots, what each takes and each spot's neighbours, is built once per site and kept across
    /// solves. See SpotGround.
    /// </summary>
    private sealed record GroundView(SpotGround Ground, List<int>[] CaughtBy, Dictionary<int, List<int>> Catching,
        Dictionary<int, HashSet<int>> CatchingSets);

    private static GroundView ViewOf(PlanEnvironment env)
    {
        var ground = SpotGround.Of(env);

        if (ReferenceEquals(_viewEnv, env) && ReferenceEquals(_view?.Ground, ground))
            return _view;

        var caughtBy = ground.TargetsCaughtAt(env);
        var catching = CatchingOfStops(env, caughtBy);

        _view = new GroundView(ground, caughtBy, catching, catching.ToDictionary(x => x.Key, x => x.Value.ToHashSet()));
        _viewEnv = env;

        return _view;
    }

    [ThreadStatic] private static PlanEnvironment _viewEnv;

    [ThreadStatic] private static GroundView _view;

    /// <summary>Every order of a few stops.</summary>
    private static IEnumerable<List<int>> Orders(List<int> stops)
    {
        if (stops.Count <= 1)
        {
            yield return new List<int>(stops);

            yield break;
        }

        for (var i = 0; i < stops.Count; i++)
        {
            var rest = new List<int>(stops);

            rest.RemoveAt(i);

            foreach (var tail in Orders(rest))
                yield return new List<int> { stops[i] }.Concat(tail).ToList();
        }
    }

    /// <summary>
    /// The spots catching each stop, from what the ground says each spot takes, set-offs included. The stops are the
    /// remnants, the relics that multiply what follows and the must-takes. See SpotGround.CaughtFrom and
    /// RemnantOrder.Multiplies.
    /// </summary>
    private static Dictionary<int, List<int>> CatchingOfStops(PlanEnvironment env, List<int>[] caughtBy)
    {
        var stops = new HashSet<int>(Enumerable.Range(0, env.Targets.Count).Where(i =>
            env.Targets[i].Kind == TargetKind.Remnant && !env.Targets[i].Shunned ||
            RemnantOrder.Multiplies(env.Targets[i]) || env.Targets[i].Must));
        var catching = new Dictionary<int, List<int>>();

        for (var s = 0; s < caughtBy.Length; s++)
        {
            foreach (var t in caughtBy[s])
            {
                if (!stops.Contains(t))
                    continue;

                if (!catching.TryGetValue(t, out var list))
                    catching[t] = list = new List<int>();

                list.Add(s);
            }
        }

        return catching;
    }

    /// <summary>What a blast at a spot takes, a barrel it catches setting off what it reaches. See Planner.Fold.</summary>
    private static IEnumerable<int> CaughtAt(PlanEnvironment env, Vector2 at)
    {
        var caught = new HashSet<int>();

        foreach (var t in Planner.CaughtIndicesAt(env, at))
        {
            caught.Add(t);

            if (env.Targets[t].Sets is { Length: > 0 } sets)
                caught.UnionWith(sets);
        }

        return caught;
    }

    /// <summary>The stops the chain catches, each with the link that first catches it, in that order.</summary>
    private static List<(int Stop, int Link)> StopsInChainOrder(PlanEnvironment env, List<Vector2> chain,
        HashSet<int> stops)
    {
        var seen = new HashSet<int>();
        var inOrder = new List<(int, int)>();

        for (var link = 0; link < chain.Count; link++)
        {
            foreach (var t in CaughtAt(env, chain[link]))
            {
                if (stops.Contains(t) && seen.Add(t))
                    inOrder.Add((t, link));
            }
        }

        return inOrder;
    }
}
