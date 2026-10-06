using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// What is known about a dig site's ground for laying chains spot by spot: the candidate spots, what a blast at each
/// takes, as the targets' places, and each spot's neighbours - the spots one link from it, or one link into it, as the
/// router answers. Built once per ground and shared by every search on it, across solves and rerolls, until the
/// ground changes or a reset forgets it.
///
/// **One ground, not one per search.** The remnant order search kept its own, per walker, and the exhaustive spot
/// search worked out its own again every solve and asked the router about each pair of spots it needed, so a reset
/// that cleared one left the other, and after a reload the router was asked the same questions over again. See
/// RemnantOrder and ExhaustiveSpotSearch.
///
/// **The targets by place, not by index.** A new scan of the same ground does not keep the targets' order, so each
/// search maps the places onto its own scan's indices. See TargetsCaughtAt.
/// </summary>
internal sealed class SpotGround
{
    /// <summary>The ground key this was built for. See KeyOf.</summary>
    public long Key { get; }

    /// <summary>The candidate spots. See Planner.Candidates.</summary>
    public List<Vector2> Spots { get; }

    /// <summary>What a blast at each spot takes, set-offs included, as the targets' places. See CaughtFrom.</summary>
    public List<Vector2>[] CaughtAtPlaces { get; }

    /// <summary>
    /// The spots taking a remnant or a relic that multiplies what follows, which every spot's neighbours include
    /// whatever their direction, so a walk can end on any of them. See Linked.
    /// </summary>
    public HashSet<int> Endings { get; }

    private readonly Dictionary<Vector2, int> _indexOf = new();
    // **Each list worked out once, whichever thread asks first; the others wait for it.** Checked and then stored, two
    // threads asking for the same spot both worked it out: on a Frigid Bluffs site (2026-10-04) an order search split
    // across six threads asked the router 4.1 million questions where one thread asked 0.76 million.
    private readonly ConcurrentDictionary<int, Lazy<List<int>>> _next = new();
    private readonly ConcurrentDictionary<int, Lazy<List<int>>> _previous = new();

    /// <summary>
    /// Next's and Previous's lists once built, by spot, with the detonator last, so a walk asking for a spot's
    /// neighbours reads an array. Asked once per spot a walk expands - about a million times in a Grand site's order
    /// search - and the dictionary's GetOrAdd allocated a delegate on every call, since its factory captures the
    /// environment. The dictionaries still build each list once. See Next.
    /// </summary>
    private readonly List<int>[] _nextOfSpot;

    /// <summary>See _nextOfSpot.</summary>
    private readonly List<int>[] _previousOfSpot;

    private int IndexOfNeighbours(int spot) => spot == Detonator ? Spots.Count : spot;
    private long _reachAsked;
    private long _reachTicks;
    private long _listsBuilt;
    private long _listTicks;
    private long _backChecks;
    private long _backTicks;

    /// <summary>How many reach questions the neighbours have asked the router, and the ticks they took.</summary>
    public long ReachAsked => Interlocked.Read(ref _reachAsked);

    public long ReachTicks => Interlocked.Read(ref _reachTicks);

    /// <summary>
    /// How many neighbour lists have been built and the ticks building them took, the router's questions included; and
    /// how many reaches-back checks Previous has made and the ticks they took. Thread time, summed over the threads. For
    /// the order search's timings, which tell walking apart from what the router costs it. See RemnantOrder.LastTimings.
    /// </summary>
    public (long Built, long Ticks) Lists => (Interlocked.Read(ref _listsBuilt), Interlocked.Read(ref _listTicks));

    public (long Checks, long Ticks) BackChecks => (Interlocked.Read(ref _backChecks), Interlocked.Read(ref _backTicks));

    /// <summary>The spot standing for the detonator in Next and Previous.</summary>
    public const int Detonator = -1;

    private SpotGround(PlanEnvironment env, long key)
    {
        Key = key;
        Spots = Planner.Candidates(env, out _, out _);
        _nextOfSpot = new List<int>[Spots.Count + 1];
        _previousOfSpot = new List<int>[Spots.Count + 1];
        CaughtAtPlaces = new List<Vector2>[Spots.Count];
        Endings = new HashSet<int>();

        for (var s = 0; s < Spots.Count; s++)
        {
            _indexOf.TryAdd(Spots[s], s);

            var caught = CaughtFrom(env, Spots[s]);

            CaughtAtPlaces[s] = caught.Select(t => env.Targets[t].Grid).ToList();

            if (caught.Any(t => env.Targets[t].Kind == TargetKind.Remnant || RemnantOrder.Multiplies(env.Targets[t])))
                Endings.Add(s);
        }
    }

    /// <summary>The ground for this site as it stands: the one held when the ground is the same, else built.</summary>
    public static SpotGround Of(PlanEnvironment env)
    {
        var (key, parts) = KeyOf(env);
        var held = Volatile.Read(ref _held);

        if (held != null && held.Key == key)
            return held;

        lock (Gate)
        {
            if (_held != null && _held.Key == key)
                return _held;

            var built = new SpotGround(env, key);

            _builds++;
            _lastBuilt = DateTime.UtcNow;
            _lastReason = _lastParts is not { } last ? "first build on this site, or after a reset"
                : string.Join(", ", new[]
                    {
                        parts.Site != last.Site ? "the detonator, reach, blast, explosives or placed count" : null,
                        parts.Places != last.Places ? $"the targets' places or radii ({last.Count} targets then, {parts.Count} now)" : null,
                        parts.Offered != last.Offered ? "which targets the candidate spots are offered around" : null,
                        parts.Endings != last.Endings ? "which places hold a remnant or a multiplying relic" : null,
                        parts.Sets != last.Sets ? "what the targets set off" : null,
                    }.Where(x => x != null));
            _lastParts = parts;

            Volatile.Write(ref _held, built);

            return built;
        }
    }

    /// <summary>
    /// What the ground depends on, and only that: the detonator, reach, blast, explosives and placed count; each
    /// target's place and catch radius, which decide what a spot takes; which targets the candidate spots are offered
    /// around (see Planner.OffersCandidates); what each sets off; and which places hold a remnant or a relic that
    /// multiplies what follows, which decide Endings. A target's kind otherwise is not part of it. Keyed on every kind
    /// and mark, the ground and every neighbour list were built again after a warm start on a Frigid Bluffs site
    /// (2026-10-04), and which part had changed was not recorded; the parts are kept now for the dump. See LastBuiltSaid.
    /// </summary>
    private static (long Key, KeyParts Parts) KeyOf(PlanEnvironment env)
    {
        var site = HashCode.Combine(env.Origin, env.Reach, env.Blast, env.Explosives, env.Placed?.Count ?? 0);
        var (places, offered, endings, sets) = (0L, 0L, 0L, 0L);

        foreach (var target in env.Targets)
        {
            var at = target.Grid.GetHashCode();

            places = unchecked(places + HashCode.Combine(target.Grid, target.Radius));

            if (Planner.OffersCandidates(target))
                offered = unchecked(offered + at);

            if (target.Kind == TargetKind.Remnant || RemnantOrder.Multiplies(target))
                endings = unchecked(endings + at);

            foreach (var also in target.Sets ?? [])
            {
                if (also >= 0 && also < env.Targets.Count)
                    sets = unchecked(sets + HashCode.Combine(target.Grid, env.Targets[also].Grid));
            }
        }

        var key = unchecked((((places * 31 + offered) * 31 + endings) * 31 + sets) * 31 + site * 4294967296L +
                            env.Targets.Count);

        return (key, new KeyParts(site, places, offered, endings, sets, env.Targets.Count));
    }

    /// <summary>The parts of the ground's key, kept to say which changed. See KeyOf.</summary>
    private readonly record struct KeyParts(int Site, long Places, long Offered, long Endings, long Sets, int Count);

    private static int _builds;
    private static DateTime _lastBuilt;
    private static string _lastReason = "";
    private static KeyParts? _lastParts;

    /// <summary>How many times the ground has been built this session, when it last was and what had changed, for the dump.</summary>
    public static string LastBuiltSaid
    {
        get
        {
            lock (Gate)
                return _builds == 0 ? "not built yet"
                    : $"built {_builds} time(s), last {(DateTime.UtcNow - _lastBuilt).TotalSeconds:0}s ago; changed then: {(_lastReason.Length == 0 ? "none of its parts, so the key collided" : _lastReason)}";
        }
    }

    /// <summary>Forgets the ground, for a reset that throws away what was worked out about it. See Caches.Clear.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Volatile.Write(ref _held, null);
            _lastParts = null;
        }
    }

    private static SpotGround _held;
    private static readonly object Gate = new();

    /// <summary>The spot at a point, or -1 when the point is not one of the spots.</summary>
    public int IndexOf(Vector2 at) => _indexOf.TryGetValue(at, out var s) ? s : -1;

    /// <summary>
    /// What a blast at each spot takes, as this scan's indices: the places mapped onto the scan. Cheap enough to do
    /// for every search. See CaughtAtPlaces.
    /// </summary>
    public List<int>[] TargetsCaughtAt(PlanEnvironment env)
    {
        var indexAt = new Dictionary<Vector2, int>();

        for (var t = 0; t < env.Targets.Count; t++)
            indexAt.TryAdd(env.Targets[t].Grid, t);

        var caught = new List<int>[Spots.Count];

        for (var s = 0; s < Spots.Count; s++)
            caught[s] = CaughtAtPlaces[s].Where(indexAt.ContainsKey).Select(g => indexAt[g]).ToList();

        return caught;
    }

    /// <summary>
    /// The spots one link on from a spot, or from the detonator, by the solver's own reach: in each of Sectors
    /// directions the farthest spot that stands, and every spot within reach in Endings. Worked out once per spot.
    ///
    /// **Not every spot within reach.** Asking the router about each one asked it about most of a site - 8,309,516
    /// questions on one Scorched Cay, 3.7 s offline where each is a lookup, and in game each can route the game's
    /// wire. A walk of fewest links needs only the long throws in each direction and the spots a leg can end on, so
    /// in each direction the router is asked from the farthest spot inwards until one stands.
    /// </summary>
    public List<int> Next(PlanEnvironment env, int from)
    {
        var lists = _nextOfSpot;
        var index = IndexOfNeighbours(from);

        if (Volatile.Read(ref lists[index]) is { } had)
            return had;

        var built = NextBuilt(env, from);

        Volatile.Write(ref lists[index], built);

        return built;
    }

    private List<int> NextBuilt(PlanEnvironment env, int from) =>
        _next.GetOrAdd(from, f => new Lazy<List<int>>(() =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var built = Linked(env, f);

            Interlocked.Add(ref _listTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _listsBuilt);

            return built;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// The spots of Next that reach back to this one, for a walk towards a chain's beginning. Worked out once per spot.
    /// See RemnantOrder's backward walk.
    /// </summary>
    public List<int> Previous(PlanEnvironment env, int to)
    {
        var lists = _previousOfSpot;
        var index = IndexOfNeighbours(to);

        if (Volatile.Read(ref lists[index]) is { } had)
            return had;

        var built = PreviousBuilt(env, to);

        Volatile.Write(ref lists[index], built);

        return built;
    }

    private List<int> PreviousBuilt(PlanEnvironment env, int to) =>
        _previous.GetOrAdd(to, t => new Lazy<List<int>>(() =>
        {
            var forward = Next(env, t);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var back = forward.Where(s => Planner.Reaches(env, Spots[s], Spots[t])).ToList();

            Interlocked.Add(ref _backTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _backChecks, forward.Count);

            return back;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>A spot's neighbours: in each direction the farthest spot that stands, and every spot in Endings within reach.</summary>
    private List<int> Linked(PlanEnvironment env, int from)
    {
        var at = from == Detonator ? env.Origin : Spots[from];
        var list = new List<int>();
        var bySector = new List<(int Spot, float Span)>[Sectors];

        int SectorOf(int s)
        {
            var d = Spots[s] - at;

            return (int)((MathF.Atan2(d.Y, d.X) + MathF.PI) / (2f * MathF.PI) * Sectors) % Sectors;
        }

        bool Stands(int s)
        {
            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            var says = Planner.Says(env, at, Spots[s]);

            Interlocked.Add(ref _reachTicks, System.Diagnostics.Stopwatch.GetTimestamp() - asked);
            Interlocked.Increment(ref _reachAsked);

            return says == Certainty.Yes;
        }

        for (var s = 0; s < Spots.Count; s++)
        {
            if (s == from)
                continue;

            var span = Planner.Span(at, Spots[s]);

            if (span > env.Reach)
                continue;

            // Every spot a leg can end on is asked about, whatever its direction.
            if (Endings.Contains(s))
            {
                if (Stands(s))
                    list.Add(s);

                continue;
            }

            (bySector[SectorOf(s)] ??= new List<(int, float)>()).Add((s, span));
        }

        foreach (var sector in bySector)
        {
            if (sector == null)
                continue;

            sector.Sort((a, b) => b.Span.CompareTo(a.Span));

            foreach (var (s, _) in sector.Take(TriedPerSector))
            {
                if (Stands(s))
                {
                    list.Add(s);

                    break;
                }
            }
        }

        return list;
    }

    /// <summary>How many directions a spot's long throws are taken in. Chosen, not measured. See Next.</summary>
    private const int Sectors = 24;

    /// <summary>How many spots, farthest first, the router is asked about in one direction before it is given up. See Next.</summary>
    private const int TriedPerSector = 8;

    /// <summary>
    /// Every target a blast at this point takes: those it reaches, and what they set off - a barrel's explosion and
    /// whatever that reaches - followed as far as it goes, as the scoring does. See PlanTarget.Sets.
    ///
    /// **Not the blast alone.** Counting only what the blast reached left a remnant a barrel's explosion took looking
    /// uncaught, and the orders sent the chain straight back for it.
    /// </summary>
    public static List<int> CaughtFrom(PlanEnvironment env, Vector2 at)
    {
        var taken = new HashSet<int>();
        var reached = new Queue<int>();

        for (var t = 0; t < env.Targets.Count; t++)
        {
            if (Planner.Catches(env, at, env.Targets[t]))
                reached.Enqueue(t);
        }

        while (reached.Count > 0)
        {
            var t = reached.Dequeue();

            if (!taken.Add(t))
                continue;

            foreach (var also in env.Targets[t].Sets ?? [])
            {
                if (also >= 0 && also < env.Targets.Count && !taken.Contains(also))
                    reached.Enqueue(also);
            }
        }

        return taken.ToList();
    }
}
