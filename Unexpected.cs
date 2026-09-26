using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Things a blast would set off that the scan has never heard of.
///
/// **This looks for the state, not for the metadata, and that is the entire idea.** Every expedition
/// object an explosive can activate carries a `glow_epk` state - it is what the game sets to light
/// up the content under the placement circle - so carrying it is the game's own statement that this
/// is something a blast acts on. The scan works the other way round, off a list of metadata
/// prefixes it has been taught, and anything not on that list it walks straight past without a
/// word.
///
/// That is how the Verisium Sentry stayed hidden: an object standing in the dig site, lighting up
/// for every placement it fell inside, worth nothing to the planner because nothing was looking for
/// it. There was no symptom to chase - a planner that has never heard of a thing does not complain
/// about it. The only way that class of gap shows itself is by asking the game what it thinks is
/// activatable and comparing that against what we think is there.
///
/// So anything with the state and no matching target gets a red line and its name on screen, which
/// is a standing invitation to go and read what it is. Grand Expeditions bring their own objects,
/// and this is how they will turn up rather than being quietly ignored.
///
/// Cheap enough to leave on: the state list is walked for entities near the dig site only, the
/// answer is cached for half a second, and carrying a `glow_epk` at all is rare enough that nearly
/// everything is rejected on the first state it reads.
/// </summary>
internal static class Unexpected
{
    /// <summary>One thing the game would activate and the scan does not know about.</summary>
    internal sealed class Found
    {
        /// <summary>
        /// Which entity this was, so one thing is one finding wherever it walks.
        ///
        /// **Findings are keyed on the cell, and a monster does not stay in one.** Remembering by
        /// cell is what makes a Grand site knowable - the game unloads what is behind you, so a sweep
        /// only ever sees the part you are standing in - and it is exactly right for a marker, which
        /// never moves. A boss fights.
        ///
        /// Seen in game: one BlackJaw, alive and then dead at (1817,715), with FOUR red warnings
        /// around it at (1825,717), (1816,711), (1808,715) and (1809,716). One entity, four cells it
        /// had passed through, and the scan only ever clears the cell the thing currently occupies -
        /// so the trail stood for the life of the site.
        ///
        /// Not the same fault as the Vaal statues, which really are three separate entities and are
        /// dealt with by withdrawing a finding once the thing stops carrying a glow_epk. A trail
        /// cannot be withdrawn that way, because the cells it is left in hold nothing at all.
        ///
        /// Nought where the id could not be read, which falls back to the old cell-only behaviour.
        /// </summary>
        public uint Who { get; init; }

        public Vector2 Grid { get; init; }

        /// <summary>
        /// Where it stood, read once and kept - NOT re-read from the entity.
        ///
        /// **A culled entity's position is not its position.** Findings are remembered for the life
        /// of the dig site, and a later sweep that saw the same object again would overwrite this
        /// from an entity the game had since unloaded - which reads as nought, or as whatever is
        /// left in that memory. The line then ran from the player to nowhere in particular, and
        /// only after walking away, which is exactly when the entity gets dropped.
        ///
        /// Worked out from the grid cell instead, which is remembered and cannot go stale, against
        /// the terrain height there. The same thing the marker drawing does for an unloaded marker.
        /// </summary>
        public Vector3 World { get; set; }
        public string Meta { get; init; } = "";

        /// <summary>Whether it is lit right now, which is to say the explosive under the cursor would take it.</summary>
        public bool Lit { get; init; }

        /// <summary>
        /// When this cell was first found to be carrying a glow_epk nobody could name.
        ///
        /// **The two sweeps run on their own clocks and this one is faster.** An entity that streams
        /// in is read here before the scan has classified it, so for up to a sweep it is genuinely
        /// unknown - and a red line drawn across the screen for a second and then taken away says
        /// "something is wrong" about a marker that is perfectly ordinary. The permanent version of
        /// that race was fixed by dropping a finding once the scan can name the thing; this is the
        /// transient version, and it needs the opposite: say nothing until the scan has had its go.
        /// </summary>
        public DateTime Since { get; init; }

        /// <summary>Whether this has gone unnamed long enough to be worth saying out loud.</summary>
        public bool Settled => DateTime.UtcNow - Since >= Grace;

        /// <summary>The tail of the metadata, which is the part worth reading on screen.</summary>
        public string Name
        {
            get
            {
                var cut = Meta.LastIndexOf('/');

                return cut >= 0 && cut < Meta.Length - 1 ? Meta[(cut + 1)..] : Meta;
            }
        }
    }

    /// <summary>The buckets worth asking. Anything activatable is in one of them.</summary>
    /// <summary>
    /// Buckets that are cheap to read whole, because a dig site's own objects live in them.
    ///
    /// Everything else is read too - see Read - but these are the ones worth naming, since a miss
    /// here is a miss of the ordinary content rather than of a surprise.
    /// </summary>
    private static readonly EntityType[] Named =
    [
        EntityType.IngameIcon,
        EntityType.MiscellaneousObjects,
        EntityType.Monster,
        EntityType.Chest,
    ];

    /// <summary>
    /// How long a cell must go unnamed before it is reported.
    ///
    /// Comfortably more than a scan sweep, which is what it is waiting for. Being slow to report is
    /// free - the thing it is looking for has been standing there since the site generated and will
    /// still be there in two seconds - where being quick to report is a red line over an ordinary
    /// marker, which is the fault this exists to avoid rather than one it can afford to cause.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2.5);

    private static List<Found> _found = new();

    /// <summary>Everything doubted, settled or not. _found is the part of it worth reporting.</summary>
    private static List<Found> _held = new();

    private static DateTime _read = DateTime.MinValue;

    /// <summary>
    /// Everything near this dig site that the game would activate and the scan is not holding.
    ///
    /// Matched on position rather than on identity, because that is what the scan is keyed on and
    /// because a target and its entity are not always the same handle - two entities routinely
    /// share a grid cell. Within a cell of a known target means known.
    /// </summary>
    /// <summary>
    /// How far from the detonator a blast could possibly be set off, and so how far to look.
    ///
    /// **A fixed two hundred grid was too small for a small site and nowhere near a Grand one.** A
    /// chain of five links at ninety grid apiece already reaches four hundred and fifty; fifteen
    /// links reach thirteen hundred. Observed on a Grand site: a subarea entrance carrying a
    /// glow_epk sat seven hundred grid out and was never looked at, so the one detector built to
    /// find content nobody has classified could not see the most interesting thing on the map.
    ///
    /// A quarter over, because the chain can wander and the detonator is not the centre of the site.
    /// </summary>
    public static float Around(GameController gc)
    {
        var links = Math.Max(1, Safe.Read(Detonator.Info(gc), static i => i.TotalExplosiveCount, 0));

        // A Grand Expedition has no useful radius.
        //
        // Fifteen explosives is a Grand site and nothing else is, and those sprawl far enough that
        // any circle drawn round the detonator is either too small to hold the site or so large it
        // is not a limit at all. The test that matters is carrying a glow_epk, which is rare enough
        // to check against every entity the game has loaded, so on these sites nothing is excluded
        // for being far away.
        if (links >= Detonator.GrandExplosives)
            return float.MaxValue;
        // Ninety grid is the placement range every site has shown so far, and this only needs to
        // be the right order of magnitude - it decides where to look, not where to place.
        const float reach = 90f;

        return Math.Clamp(links * reach * 1.25f, 200f, 4000f);
    }

    /// <param name="everyMs">
    /// The poll rate, which this rides rather than keeping a rhythm of its own.
    ///
    /// **It had a hardcoded half second, so turning the poll rate down changed nothing here.** What
    /// this does is compare what the game is offering against what the scan holds, and the scan is
    /// rebuilt by the sweep - so asking more often than the sweep runs answers the same question
    /// twice, and asking less often delays every red line by up to half a second on top of whatever
    /// the sweep already cost. One rhythm, set in one place, and it is the one the player can see.
    /// </param>
    public static IReadOnlyList<Found> Read(GameController gc, Scan scan, Vector2 site, float range,
        int everyMs)
    {
        if (DateTime.UtcNow - _read < TimeSpan.FromMilliseconds(Math.Max(50, everyMs)))
            return _found;

        _read = DateTime.UtcNow;

        if (site == Vector2.Zero)
            return _found;

        var known = new HashSet<(int X, int Y)>();

        // **Classified is not the same as understood.** The scan now files anything with the state
        // as TargetKind.Unknown rather than walking past it, which would have silenced this - the
        // thing has a marker and a weight, so by the old test it was known. It is not: its weight is
        // a placeholder nobody has agreed to. So it stays doubted, and stays ringed in red, until
        // somebody has priced it - which is exactly the moment the red line has done its job.
        foreach (var target in scan.Targets)
        {
            if (Unknowns.Unread(target))
                continue;

            known.Add(((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y)));
        }

        // Remembered, not re-read.
        //
        // **A Grand site is walked, not seen.** The game unloads what is behind you, so a sweep only
        // ever knows the part of the map you are standing in - and on a site big enough to need
        // fifteen explosives that is a small fraction of it. Skirting the edge and coming back with
        // a complete picture is only possible if each sweep adds to what the last one found.
        //
        // Keyed on the cell, so the same object seen twice is one entry, and cleared when the area
        // changes like everything else here.
        //
        // **A finding is dropped the moment the scan can name the thing.** Remembering was letting a
        // race become permanent: the two sweeps run on their own clocks, so a marker that loads in
        // between them is read here before the scan has classified it, recorded as content nobody
        // knows about, and then kept for the life of the site - a red line standing over an ordinary
        // runic monster marker that the scan has known about for minutes. Seeding past findings past
        // the same test the live ones face is all it takes, and it costs nothing: an object the scan
        // genuinely does not know is not in that set however often it is looked up.
        var seen = new Dictionary<(int X, int Y), Found>();

        // Cells that hold something right now which a blast would NOT set off, and cells this sweep
        // actually recorded. A remembered finding standing on the first and not the second is a
        // memory of a thing that has stopped being content. See below.
        var inert = new HashSet<(int X, int Y)>();
        var wrote = new HashSet<(int X, int Y)>();

        // Which cell each entity is in THIS sweep, so the cells it used to be in can be dropped.
        // See Found.Who.
        var here = new Dictionary<uint, (int X, int Y)>();

        foreach (var had in _held)
        {
            var cell = ((int)MathF.Round(had.Grid.X), (int)MathF.Round(had.Grid.Y));

            if (!known.Contains(cell))
                seen[cell] = had;
        }


        // Every bucket, not a list of the ones we thought of.
        //
        // **Each fixed list has missed something.** The first held four types and missed a siren egg
        // filed under Terrain; adding Terrain would have missed whatever comes next. The signal is
        // carrying a glow_epk, which is a property of the object and not of the bucket it happens
        // to be filed in, so the search is over everything the game has loaded and the test decides.
        //
        // Affordable because the test is ordered: a metadata check throws out the overwhelming
        // majority before anything reaches into a component.
        var buckets = Safe.Read(gc, static g => g.EntityListWrapper.ValidEntitiesByType, null);

        if (buckets == null)
            return _found;

        foreach (var (bucket, entities) in buckets)
        {
            if (entities == null)
                continue;

            var mine = Array.IndexOf(Named, bucket) >= 0;

            foreach (var entity in entities)
            {
                // **The cheap test first, which is what this has always claimed to do.**
                //
                // Outside the buckets a dig site keeps its own objects in, only things whose path
                // says Expedition can belong to one. That was the argument, and the code did not
                // follow it: GridPos came first, and GridPos reaches into the Positioned component
                // for a live reading - so every rock and railing in the zone was read before
                // anything threw it out. Metadata is a string the entity already holds.
                //
                // Measured before the swap: the sweep cost 23.6ms and runs twice a second by
                // default, which is a stall rather than a cost - see DebugSettings.SweepMs.
                //
                // Read once and used twice, since the casing test below wants the same string.
                var path = Safe.Read(entity, static e => e.Metadata ?? "", "");

                if (!mine && !path.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
                    continue;

                // The casing is not a second object to doubt: the monster inside it is the target,
                // and the scan holds that. See Scan, where the casing is skipped.
                if (path.Contains("EncasedMonster", StringComparison.OrdinalIgnoreCase))
                    continue;

                var at = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

                if (at == Vector2.Zero || Vector2.Distance(at, site) > range)
                    continue;

                var cell = ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));

                if (known.Contains(cell))
                    continue;

                // **What a blast acts on carries inherent_explosion_radius**, and carrying
                // glow_epk does not say that. Every ordinary strongbox in the zone carries
                // glow_epk at nought, so the Chest bucket - which skips the Expedition path test,
                // being one of the buckets a dig site keeps its own objects in - let a plain map
                // strongbox through and it was drawn as unnamed content with a line to it.
                //
                // Measured over every dump on record: 253 sightings carry
                // inherent_explosion_radius and every one has Expedition in its path, including
                // the awkward ones this test exists for - the boss egg, the devourer segment, the
                // root blocker. Carrying glow_epk WITHOUT it is two kinds only: the detonator,
                // which is named content anyway, and the strongbox that caused this.
                //
                // Noted rather than merely skipped, because something remembered here may need
                // withdrawing. See the sweep below.
                if (!ReactsToBlast(entity))
                {
                    inert.Add(cell);

                    continue;
                }

                var glow = Glow(entity);

                wrote.Add(cell);

                var who = Safe.Read(entity, static e => e.Id, 0u);

                if (who != 0u)
                    here[who] = cell;

                // Once, however many buckets it turns up in.
                known.Add(cell);

                // Overwrites rather than skips: a second look can find it lit where the first found
                // it dark, and the later reading is the true one.
                // Kept, rather than replaced, when this cell is already on record: only the lit
                // flag is worth refreshing, and everything else about a remembered finding is more
                // trustworthy than a second reading of an entity that may be half unloaded.
                var had = seen.TryGetValue(cell, out var before) ? before : null;

                seen[cell] = new Found
                {
                    // Kept from the earlier reading when this one could not be read, so a finding
                    // does not lose its identity to one bad frame.
                    Who = who != 0u ? who : had?.Who ?? 0u,
                    Grid = at,
                    World = had is { World: var was } && was != Vector3.Zero ? was : Where(gc, at),
                    Meta = Safe.Read(entity, static e => e.Metadata, "") ?? "",
                    Lit = glow == 1,

                    // The clock starts when the cell was FIRST doubted, not when it was last looked
                    // at. See Since.
                    Since = had?.Since ?? DateTime.UtcNow,
                };
            }
        }

        // **Nothing is there any more, and we are close enough to be sure of it.**
        //
        // This is the general case the two rules below are special cases of, and the one that matters:
        // the game spawns something carrying a glow_epk, it is doubted because nothing can name it
        // yet, and then the game DELETES it - a statue that never emerged, a boss that despawned, a
        // casing that opened. The finding outlives the object and stands over empty ground for the
        // rest of the site, with a line drawn to it.
        //
        // **Why this cannot simply be "no entity there".** Findings are remembered precisely because
        // the game unloads what is behind you: on a Grand site a sweep only ever sees the part you
        // are standing in, so an empty cell across the map is the normal case and forgetting on it
        // would throw away the whole point of remembering.
        //
        // Near the player it is a different question. The placement run reads glow_epk off markers at
        // the full reach of a link to decide whether a blast is the planned one - so entities at that
        // distance are demonstrably loaded and readable. A cell that close with nothing in it is
        // empty, not unloaded. Anything further away keeps its finding.
        var eye = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (eye != Vector2.Zero)
        {
            foreach (var empty in seen.Where(x => !wrote.Contains(x.Key) &&
                                                  !inert.Contains(x.Key) &&
                                                  Vector2.Distance(eye, x.Value.Grid) <= Certain)
                         .Select(static x => x.Key)
                         .ToList())
                seen.Remove(empty);
        }

        // **A cell a thing has walked out of is not a finding, it is a place it used to be.**
        //
        // Dropped only against positive evidence: the same entity has been seen THIS sweep, and it
        // is somewhere else. An entity that is merely unloaded says nothing and its finding stands,
        // which is the whole point of remembering. See Found.Who.
        foreach (var stale in seen.Where(x => x.Value.Who != 0u &&
                                              here.TryGetValue(x.Value.Who, out var at) &&
                                              at != x.Key)
                     .Select(static x => x.Key)
                     .ToList())
            seen.Remove(stale);

        // **A warning is withdrawn once the thing can be seen again and is no longer content.**
        //
        // Findings are remembered for the life of the site, because a Grand site is walked rather
        // than seen and a sweep only knows the part you are standing in. That is right for a marker,
        // which is either there or not - and wrong for something the game changes underneath us.
        //
        // A Vaal statue boss is three entities: one emerges, takes a name and a rarity and becomes
        // the unique the table knows, and the other two are left dead, untargetable and carrying no
        // states at all. All three looked alike and nameless when they were first doubted, so all
        // three were filed as content nobody could name - and the two that never became anything
        // stood as red warnings over the site for good, pointing at objects a blast cannot touch.
        //
        // Only where the thing is loaded and can actually be re-read: an unloaded cell says nothing,
        // and forgetting on absence would undo the remembering this exists for.
        foreach (var cell in inert.Where(x => !wrote.Contains(x)).ToList())
            seen.Remove(cell);

        // Everything doubted is held, including what is still inside its grace; the list handed back
        // is the settled part of it. Keeping both is what lets a cell carry its clock across reads
        // rather than starting it again every half second, which would mean it never matured.
        _held = new List<Found>(seen.Values);
        _found = _held.FindAll(static x => x.Settled);

        // **Filing happens where the object is classified, not here.**
        //
        // This did file them, by metadata, and that put a second row in the table for anything the
        // scan had already filed under a different spelling - a relic is keyed on the mod naming its
        // effect, not on the scenery it stands in. Two rows for one object meant setting the weight
        // on the one the settings tab happened to show left the other unset, so the red line and the
        // label stayed exactly where they were and the slider appeared to do nothing.
        //
        // Nothing is lost by dropping it: everything carrying the state now becomes a target, so the
        // scan sees it all. See Scan and Unknowns.Key.

        return _found;
    }

    /// <summary>Forgets everything seen here, for a new dig site. See Caches.</summary>
    /// <summary>A grid cell in the world, at the height of the ground there.</summary>
    private static Vector3 Where(GameController gc, Vector2 grid) =>
        Safe.Read((gc, grid),
            static x => x.gc.IngameState.Data.ToWorldWithTerrainHeight(x.grid), Vector3.Zero);

    public static void Forget()
    {
        _held = new List<Found>();
        _found = new List<Found>();
        _read = DateTime.MinValue;
    }

    /// <summary>
    /// How close a cell has to be before an empty one proves the thing is gone.
    ///
    /// Not a guess at the game's culling radius, which is not stated anywhere. It is the distance the
    /// plugin already trusts itself to read entities at: a link reaches ninety grid and the placement
    /// run reads glow_epk off the markers at the far end of one to decide whether the blast about to
    /// happen is the planned one. If entities are readable there - and the whole placement gate rests
    /// on it - then a cell inside that range with nothing in it is empty rather than unloaded.
    ///
    /// Deliberately not larger. Every grid unit past what can be proven turns "it is gone" back into
    /// "I cannot see it", which is the reading that would undo the remembering. See the sweep in Read.
    /// </summary>
    private const float Certain = 90f;

    /// <summary>This entity's glow_epk, or -1 when it does not carry one.</summary>
    private static long Glow(Entity entity) => State(entity, "glow_epk");

    /// <summary>
    /// Whether the game marks this object as something an explosive acts on.
    ///
    /// `inherent_explosion_radius` is the game's own answer, and it is a property of the object
    /// rather than of the bucket it is filed in - which is the test this class has always claimed
    /// to apply. glow_epk was standing in for it and could not: an ordinary map strongbox carries
    /// glow_epk at nought exactly as an expedition one does before a blast reaches it.
    /// </summary>
    private static bool ReactsToBlast(Entity entity) =>
        State(entity, "inherent_explosion_radius") >= 0;

    /// <summary>One named state's value, or -1 when the entity does not carry it.</summary>
    private static long State(Entity entity, string name)
    {
        var states = Safe.Read(entity, static e => e.GetComponent<StateMachine>()?.States, null);

        if (states == null)
            return -1;

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i]?.Name == name)
                return Safe.Read(states[i], static x => x.Value, -1L);
        }

        return -1;
    }
}
