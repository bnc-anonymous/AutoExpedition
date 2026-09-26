using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// What each blast actually turns into, counted rather than assumed.
///
/// Every weight in this plugin rests on an answer nobody has: an elite marker is worth six times a
/// runic one *because that seemed about right*. The questions behind those numbers are all of the
/// same shape and all answerable by looking - does an elite marker always unearth one rare, or
/// sometimes two? What does a plain monster marker bring, and at what rarities? Do a remnant's
/// waves really scale with its sockets? Answer those and the weight table stops being taste over
/// most of its range.
///
/// **Per blast, not per marker, and that is the whole design.** The first version of this attributed
/// each monster to the nearest marker, which cannot work at the density a dig site actually has:
/// measured over eighty five markers, the median gap to the nearest neighbour is TEN grid units and
/// a tenth of them are within five. Attribution at that spacing is a coin flip, and a table built
/// from coin flips would read as though an elite marker unearths half a rare.
///
/// A blast is a different proposition. It covers thirty grid of ground and everything in it goes off
/// together, so what has to be separated is explosives - which the game keeps twenty grid apart at
/// minimum, and which the plugin knows the positions of exactly. So a row is one explosive: the
/// markers it covered, by kind, and the monsters that came up, by rarity.
///
/// That turns the question into a regression rather than an attribution. Fifty blasts with varying
/// compositions - two elites and no chests here, four monster markers and a remnant there - and
/// least squares gives what each kind of marker contributes. No single blast has to be separable
/// for that to work; they only have to differ from each other.
///
/// A whole-chain row goes in beside them, with no attribution at all: every marker the chain
/// covered against every monster it unearthed. That one is unimpeachable, and it is the check on
/// whether the per-blast split is telling the truth - the blast rows must add up to it.
///
/// And beside all of it, a timeline: one line per monster, with the second it appeared, where, and
/// which blast and remnant were nearest. No interpretation, because the interpretation is the part
/// nobody has evidence for yet - whether arrivals separate in time at all, whether markers really
/// unearth at detonation, whether waves wait for the last one to die. Those are questions the
/// timeline answers and the summary rows only assume.
///
/// Two guards keep the map out:
///
/// 1. **New since the chain was armed.** Every monster id in the area is recorded until the first
///    one is unearthed, and only ids that were not in that set are counted. This is the guard that
///    does the work, and it exists because the obvious version - filter on expedition metadata -
///    is wrong: six dumps contain exactly one thing under Metadata/Monsters/LeagueExpedition, the
///    RuneEncounterController, which is bookkeeping. What a dig site unearths is ordinary map
///    monsters with ordinary metadata. A name filter would have counted nothing and looked like it
///    was working.
/// 2. **Inside a blast.** A monster further than the blast radius from every explosive belongs to
///    none of them. It still reaches the chain row if it is anywhere near the site, because the
///    chain row is about the site rather than about any one explosive.
///
/// Blasts overlap - the radius is thirty and the minimum spacing twenty - so a monster inside two
/// of them is credited to the nearer and counted in a contested column, which is how the analysis
/// can tell a clean row from a crowded one.
/// </summary>
internal sealed class Spawns
{
    /// <summary>One explosive, what it covered, and what came up.</summary>
    private sealed class Blast
    {
        public Vector2 At;
        public int Elites;
        public int Monsters;
        public int Remnants;
        public int Sockets;
        public int RareChests;
        public int MagicChests;
        public int NormalChests;

        /// <summary>
        /// The kinds a Grand site and the newer tilesets add, each counted on its own.
        ///
        /// **They were all landing in Monsters, because the switch below ended in a default.** That
        /// was right when everything not a remnant, an elite or a chest really was a monster marker.
        /// It stopped being right the moment a siren egg, a relic, a gated encounter, an entrance
        /// and a pile of explodable huts were content - and the run that could have priced all five
        /// of them recorded them as eighty six monster markers instead.
        ///
        /// Every one of those is carrying a weight somebody invented, this file is the only thing
        /// that can replace an invented weight with a measured one, and it could not see them.
        /// </summary>
        public int Relics;

        public int Caged;

        public int Hatches;

        public int Entrances;

        public int Scenery;

        public int Normal;
        public int Magic;
        public int Rare;
        public int Unique;
        public int Contested;

        /// <summary>The same four again, for anything that turned up after the opening moments.</summary>
        public int LateNormal;
        public int LateMagic;
        public int LateRare;
        public int LateUnique;

        /// <summary>Borrowed by the remnant rows, which are a composition of exactly one remnant.</summary>
        public int Used;
        public string Selected = "";
        public double Seconds;

        public readonly SortedDictionary<string, int> Names = new(StringComparer.Ordinal);
    }

    /// <summary>One remnant, and everything that turned up near it however long it took.</summary>
    private sealed class Waves
    {
        public Vector2 At;
        public int Sockets;
        public int Used;
        public string Selected = "";
        public int Normal;
        public int Magic;
        public int Rare;
        public int Unique;
        public int LateNormal;
        public int LateMagic;
        public int LateRare;
        public int LateUnique;
        public double Last;

        /// <summary>
        /// The other markers standing inside this remnant's wave radius.
        ///
        /// Contamination, measured rather than assumed away. A wave rare and an elite marker's rare
        /// are both rares appearing near this remnant, and nothing in the entity tells them apart -
        /// so the row records how much company the remnant had, and a row with none is a clean
        /// sample of what a remnant alone produces.
        /// </summary>
        public readonly Blast Near = new();

        public int Late => LateNormal + LateMagic + LateRare + LateUnique;
    }

    private readonly List<Waves> _waves = new();
    private readonly List<Blast> _blasts = new();
    private readonly List<Target> _markers = new();
    private readonly HashSet<uint> _counted = new();

    /// <summary>Every monster that was already in the area before anything was unearthed.</summary>
    private readonly HashSet<uint> _before = new();

    private bool _frozen;
    private bool _written;
    private uint _area;
    private int _level;
    private float _radius;
    private string _path;
    private int _rows;
    private DateTime _first;

    /// <summary>Rows written this session.</summary>
    public int Rows => _rows;

    /// <summary>How far outside a blast a monster may appear and still be one of its, in grid units.</summary>
    private const float Slack = 6f;

    /// <summary>
    /// How much nearer one explosive has to be than the next before the credit is called clean.
    ///
    /// Blasts overlap by design, so this does not decide WHICH blast gets the monster - the nearer
    /// one always does - only whether the row can be trusted on its own.
    /// </summary>
    private const float Clearly = 8f;

    /// <summary>
    /// How far from the dig site a new monster still counts towards the chain row.
    ///
    /// Measured from the nearest part of the CHAIN, not from the detonator - see <see cref="Away"/>.
    /// </summary>
    private const float Site = 120f;

    /// <summary>
    /// How far a spot is from the dig site, taking the chain as the site rather than the detonator.
    ///
    /// This was distance from the detonator, and a dig site is much bigger than that suggests: one
    /// measured site ran from the detonator at (933,915) out to a marker at (1174,838), two hundred
    /// and fifty three grid away. A hundred and twenty grid from the detonator therefore cut off the
    /// far half of the chain - monsters unearthed by the last two explosives were excluded from
    /// their own expedition's totals, and the player standing there to fight them counted as having
    /// wandered off.
    ///
    /// The explosives are where the chain actually is, so the nearest of them is the honest measure.
    /// The detonator stays in the reckoning for the moments before any explosive is down.
    /// </summary>
    private float Away(Vector2 at, Vector2 site)
    {
        var closest = site == Vector2.Zero ? float.MaxValue : Vector2.Distance(at, site);

        foreach (var blast in _blasts)
            closest = MathF.Min(closest, Vector2.Distance(at, blast.At));

        return closest;
    }

    /// <summary>
    /// The dividing line the "late" columns use, in seconds after the first monster.
    ///
    /// **It is a guess resting on an unproved premise and should be read as one.** The premise is
    /// that a marker's monsters arrive with the blast and a remnant's waves arrive later, so a time
    /// cut separates them. Nobody has watched that happen. It is plausible - the chain goes off in
    /// a few seconds and waves are described as coming in rounds - and it is unverified, and the
    /// chain itself detonates explosive by explosive, so even the first part is smeared.
    ///
    /// So the columns it produces are a convenience, not evidence. The evidence is the timeline
    /// file, which records every arrival with its own timestamp and no interpretation at all. Once
    /// there is a real arrival pattern to look at, this number gets set from it - or thrown away,
    /// if arrivals turn out not to separate in time.
    /// </summary>
    private static readonly TimeSpan Wave = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The backstop, and nothing more.
    ///
    /// A run used to end when the player got two hundred grid from the chain, and that was wrong:
    /// dying and walking back is a normal thing to do in the middle of an expedition, and it ended
    /// the run and threw away every wave still to come. Distance is not evidence that an encounter
    /// is over - it is evidence about where somebody is standing.
    ///
    /// So a run now ends on three things that mean something: leaving the area, walking up to a
    /// DIFFERENT dig site, and - once it can be recognised - the game's own "expedition complete",
    /// which fires on the last monster's death and is the only one of the three that actually says
    /// the encounter finished. The first two say it was abandoned, and the row records which.
    ///
    /// This stays only as a guard against a recorder left running in an area for an hour. It is far
    /// longer than any real expedition, and a run it does end is marked as timed out rather than
    /// finished.
    ///
    /// Note what none of this does: it never counts monsters near the PLAYER. Every monster is
    /// tested against its distance from the chain, so dying and walking back across the map sweeps
    /// up nothing on the way.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(30);

    private bool _done;


    /// <summary>
    /// Groups every row of one expedition, so a map with two dig sites does not pool them.
    ///
    /// The timestamp alone will not do it: rows are written in the same second, but so are the
    /// rows of a second expedition finished a minute later in the same area, and an area hash is
    /// the same for both.
    /// </summary>
    private string _run = "";

    private int _runs;

    /// <summary>
    /// A token unique to this session, so run keys cannot collide between them.
    ///
    /// The key was the area hash and a counter, and the counter restarts every session - so two
    /// expeditions in the same map on different evenings both came out as "7C587B10-1" and pooled
    /// into one group on any join.
    /// </summary>
    private readonly string _session = (DateTime.UtcNow.Ticks % 0xFFFFFF).ToString("X6");

    /// <summary>Which dig site is being recorded, so a move to the other one ends this run.</summary>
    private Vector2 _site;

    /// <summary>Which site the counts on screen belong to, so they survive the run that made them.</summary>
    private Vector2 _counting;

    /// <summary>
    /// Every site whose run has been written, which is never recorded twice.
    ///
    /// A set rather than one position, because the detonator element does not stay on one
    /// encounter: with two dig sites in a map, Detonator.DetonatorGridPosition flips between them, so a single
    /// remembered site was displaced by the other one and then failed to block anything.
    /// </summary>
    private readonly HashSet<(int X, int Y)> _finished = new();

    /// <summary>Whether this site has been seen with an unspent remnant. See the note in Observe.</summary>
    private bool _live;

    /// <summary>
    /// How this run ended, written into every one of its rows.
    ///
    /// The difference between a complete observation and an abandoned one, and it has to be in the
    /// file rather than inferred from it: a run that ended because the map was left is missing
    /// however many waves had not arrived, and pooling it with a finished one would drag every
    /// per-socket average down by an unknown amount. With the column, "complete only" is a filter.
    /// </summary>
    private string _end = "";

    private bool Ending(string how)
    {
        _end = how;

        return true;
    }

    public void AreaChange(BaseSettingsPlugin<AutoExpeditionSettings> plugin, uint areaHash)
    {
        if (_end.Length == 0)
            _end = "left-area";

        Write(plugin);
        Reset();

        _area = areaHash;
        _runs = 0;
        _finished.Clear();
    }

    /// <summary>Ready for the next dig site, in this area or the next.</summary>
    private void Reset()
    {
        _blasts.Clear();
        _waves.Clear();
        _markers.Clear();
        _arrivals.Clear();
        _before.Clear();
        _frozen = false;
        _written = false;
        _done = false;
        _live = false;
        _site = Vector2.Zero;
        _end = "";
    }

    /// <summary>
    /// Watches until the first monster is unearthed, then counts everything after it.
    ///
    /// The before-picture cannot be taken at a fixed moment because there isn't one: markers load
    /// in as you approach, explosives go down one at a time, and the marker entities are consumed
    /// by the chain. So the markers, the explosive positions and the set of monsters already
    /// standing about are all refreshed every pass, and all frozen together the instant something
    /// new appears in the dig site.
    ///
    /// That instant is the detonation, and it is a signal the game gives rather than one inferred
    /// from a memory field whose meaning nobody has confirmed - no dump taken so far is of a
    /// detonated site, so there is nothing to confirm it against.
    /// </summary>
    public void Observe(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc, Scan scan,
        AutoExpeditionSettings settings, Valuation valuation, float radius)

    {
        if (!Safe.Read(() => settings.Recording.RecordSpawns.Value, false))
            return;

        var monsters = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var of)
                ? of
                : null, null);

        if (monsters == null)
            return;

        var site = Detonator.DetonatorGridPosition(gc);

        // Walking up to a different detonator is the one in-area ending that means something: the
        // player has moved on, and whatever is unearthed from here belongs to the next dig site.
        if (_frozen && !_done)
        {
            // The game saying so, which is the strongest ending there is - it fires on the last
            // monster's death, so nothing more is unearthed after it and the counts stop being a
            // lower bound.
            //
            // **Except the banner lies, and it lies early.** Seen in game: killing a unique monster
            // encased in ice raised "Expedition Complete" on the FIRST detonation of a site with
            // four explosives still in hand. Believed, that banks a fifth of a site's monsters as
            // the whole of it - and the site is then remembered as finished, so it is never measured
            // again in that map. One wrong number, permanently, from a popup.
            //
            // It cannot be told apart from a player who simply stopped early: both leave explosives
            // unspent and nothing on screen says which. So it is not judged, it is LABELLED. An
            // ending with explosives still in hand is recorded as complete-early, which a
            // "complete only" filter excludes exactly as it excludes an abandoned run. That is the
            // whole reason the ending is a column rather than something inferred. See _end.
            //
            // Blind means the panel could not be read at all, which contradicts nothing.
            if (Finished.Showing(gc))
            {
                var spent = Safe.Read(() => Detonator.PanelUnreadable(gc) || Detonator.ExplosivesInHand(gc) <= 0, true);

                _done = Ending(spent ? "complete" : "complete-early");
            }
            else if (_site != Vector2.Zero && site != Vector2.Zero && Vector2.Distance(site, _site) > 1f)
                _done = Ending("next-site");
            else if (DateTime.UtcNow - _first > Patience)
                _done = Ending("timeout");
        }

        // Finished with this one: write it out and start over, rather than sitting done for the
        // rest of the map. A regular map holds two dig sites and can hold more, and the tracker
        // used to record the first and ignore every one after it.
        if (_done)
        {
            Write(plugin);

            // Remembered across the reset, so the site that just ended cannot immediately arm
            // another run. Without it the recorder looped: end, reset, notice the remnants are
            // spent, arm, end again - every frame, which is what made the readout flicker as the
            // banner came up.
            if (_site != Vector2.Zero)
                _finished.Add(((int)_site.X, (int)_site.Y));

            Reset();

            return;
        }

        if (!_frozen)
        {
            Snapshot(gc, scan, valuation, radius,
                MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 25f)));

            // The remnants say when the chain has gone off, and nothing else here is reliable.
            //
            // This used to be "something new is standing in the dig site", which is wrong for a
            // reason that only shows up in play: the entity list holds what is near the player, so
            // monsters STREAM IN as you walk towards a site. Everything that loads after the first
            // pass looks new, and arriving at a dig site is precisely when that happens - the
            // counter was reading thirteen normal monsters before a single explosive was placed.
            // A one-pass warm-up cannot fix it, because the streaming continues for as long as the
            // walk does.
            //
            // A remnant's "activated" state reads 1 or 2 while the site is live and 6 once it has
            // been spent. Measured across two dumps of the same site: no state above 2 anywhere
            // before detonation, and three remnants at 6 with disable_expedition_highlight set
            // afterwards. That is the game stating the chain has gone off, and it cannot be
            // confused with walking about.
            // The TRANSITION from live to spent, not the spent state.
            //
            // A state is not an event, and this one outlives the thing it describes: a dig site
            // finished half an hour ago still has remnants reading 6, so walking back past it - or
            // simply reloading beside it - armed the recorder on a site that was over, and it then
            // counted whatever map monsters happened to be standing there. So the site has to have
            // been seen LIVE first, and the freeze is the moment it stops being.
            // The detonator's own state, watched for the transition from not-yet to gone-off.
            //
            // Seen at zero first, which is what makes it an event rather than a condition: a dig
            // site finished an hour ago still reads one, so walking past it - or reloading beside
            // it - must not arm anything. A site arrived at already detonated is never recorded,
            // which is right, because whatever it unearthed happened before anybody was counting.
            var activated = Detonator.ExplosivesDetonated(gc);

            if (activated == 0)
            {
                // Walking up to a NEW dig site is what clears the last one's counts. They are left
                // standing until then on purpose: the fight ends and the numbers stay on screen to
                // be read, rather than vanishing at the moment they become interesting.
                if (_site != Vector2.Zero && _site != _counting)
                {
                    _counting = _site;
                    _counted.Clear();
                    _normal = 0;
                    _magic = 0;
                    _rare = 0;
                    _unique = 0;
                }

                _live = true;
            }

            // Never while the banner is up. It says the encounter is over, which is the one thing
            // that cannot also be the moment to start watching one.
            if (_live && _site != Vector2.Zero && activated >= 1 && !Finished.Showing(gc) &&
                !_finished.Contains(((int)_site.X, (int)_site.Y)))
            {
                _frozen = true;
                _first = DateTime.UtcNow;
                _run = $"{_area:X8}-{_session}-{++_runs}";
            }

            if (!_frozen)
            {
                foreach (var monster in monsters)
                {
                    var id = Safe.Read(monster, static e => e.Id, 0u);

                    if (id != 0u)
                        _before.Add(id);
                }

                return;
            }
        }

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id == 0u || _before.Contains(id) || Ignore(monster) || !_counted.Add(id))
                continue;

            var at = Safe.Read(monster, static e => e.GridPos, Vector2.Zero);

            if (at == Vector2.Zero || Away(at, site) > Site)
                continue;

            var rarity = Rarity(monster);

            // Tallied before the blast is worked out, because the chain row counts every monster
            // the site unearthed whether or not any one explosive can be said to have done it.
            switch (rarity)
            {
                case MonsterRarity.Unique:
                    _unique++;

                    break;

                case MonsterRarity.Rare:
                    _rare++;

                    break;

                case MonsterRarity.Magic:
                    _magic++;

                    break;

                default:
                    _normal++;

                    break;
            }

            var since = (DateTime.UtcNow - _first).TotalSeconds;

            var name = Leaf(Safe.Read(monster, static e => e.Metadata, "") ?? "");

            Credit(at, rarity, name, DateTime.UtcNow - _first > Wave);

            _arrivals.Add(string.Join(",",
                _run,
                since.ToString("0.###", CultureInfo.InvariantCulture),
                rarity.ToString(),
                ((int)at.X).ToString(CultureInfo.InvariantCulture),
                ((int)at.Y).ToString(CultureInfo.InvariantCulture),
                Which(_blasts, at, _radius + Slack).ToString(CultureInfo.InvariantCulture),
                Which(_waves, at, MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 25f)))
                    .ToString(CultureInfo.InvariantCulture),
                Clean(name)));

            // And separately, against whichever remnant it appeared beside - a different question
            // from which blast it belongs to, and one the blast rows cannot answer, since a wave
            // arrives minutes after the explosive that started it.
            Wave_(at, rarity, since,
                MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 25f)));

            _last = DateTime.UtcNow;
        }
    }

    /// <summary>Every monster's arrival, unsummarised, written beside the counts.</summary>
    private readonly List<string> _arrivals = new();

    /// <summary>The index of the nearest thing within range, or -1. For the timeline only.</summary>
    private static int Which(List<Blast> of, Vector2 at, float range)
    {
        var found = -1;
        var closest = range;

        for (var i = 0; i < of.Count; i++)
        {
            var distance = Vector2.Distance(at, of[i].At);

            if (distance > closest)
                continue;

            closest = distance;
            found = i;
        }

        return found;
    }

    private static int Which(List<Waves> of, Vector2 at, float range)
    {
        var found = -1;
        var closest = range;

        for (var i = 0; i < of.Count; i++)
        {
            var distance = Vector2.Distance(at, of[i].At);

            if (distance > closest)
                continue;

            closest = distance;
            found = i;
        }

        return found;
    }

    /// <summary>
    /// Whether any remnant in the snapshot has been set off.
    ///
    /// Any, not all: the chain detonates in sequence and the first blast to reach a remnant spends
    /// it, so the first one to flip is the moment monsters start appearing. Waiting for all of them
    /// would start the count somewhere in the middle of the fight - and a chain rarely reaches every
    /// remnant in a site anyway, so "all" would often never arrive.
    ///
    /// The arming condition is NONE spent, then one spent. Not "some unspent, then some spent",
    /// which was the previous attempt and is satisfied the instant you look at a PARTIALLY spent
    /// site: a chain that reached two of three remnants leaves one at 1 and two at 6, so a site
    /// walked past afterwards - or reloaded beside - was both live and spent in the same frame, and
    /// armed immediately. A site arrived at already part-finished is now never recorded, which is
    /// right: whatever it unearthed happened before anybody was counting.
    ///
    /// A site with no remnants at all cannot be recognised this way and is never recorded either -
    /// a row that might be the map is worse than no row.
    /// </summary>
    /// <summary>How many remnants the snapshot holds, spent or not.</summary>
    private int Remnants()
    {
        var found = 0;

        foreach (var marker in _markers)
        {
            if (marker.Kind == TargetKind.Remnant)
                found++;
        }

        return found;
    }

    private bool Spent()
    {
        foreach (var marker in _markers)
        {
            if (marker.Kind == TargetKind.Remnant && marker.State("activated") >= 6)
                return true;
        }

        return false;
    }

    /// <summary>Bookkeeping entities the game parks on an encounter, which nobody fights.</summary>
    private static bool Ignore(Entity monster)
    {
        var metadata = Safe.Read(monster, static e => e.Metadata, "") ?? "";

        return metadata.IndexOf("EncounterController", StringComparison.OrdinalIgnoreCase) >= 0 ||
               metadata.IndexOf("/Daemon", StringComparison.OrdinalIgnoreCase) >= 0 ||
               metadata.IndexOf("/NPC/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The explosives that are down and the markers they cover, as things stand.
    ///
    /// Composition is worked out geometrically rather than from the plan, on purpose: the row has
    /// to describe what the game did, and the player may have placed by hand, undone, or gone
    /// somewhere the plan did not ask for. The plan is the plugin's opinion; the placed positions
    /// are a fact.
    /// </summary>
    private void Snapshot(GameController gc, Scan scan, Valuation valuation, float radius, float wave)
    {
        _level = Safe.Read(() => gc.IngameState.Data.CurrentAreaLevel, 0);
        _radius = radius > 0f ? radius : _radius;

        // Seen-live is a fact about one dig site, so it cannot survive a change of site.
        //
        // It used to, and that is what produced two hundred and eighteen identical runs from one
        // expedition. The detonator element flips between a map's two encounters, so a pass that
        // looked at the untouched site set seen-live, and the very next pass - looking at the
        // finished one - found a spent remnant and armed on the back of it. The banner was still
        // up, so it ended immediately, wrote, reset, and did the whole thing again next frame.
        if (_site != Vector2.Zero && Detonator.DetonatorGridPosition(gc) != _site)
            _live = false;

        _markers.Clear();
        _site = Detonator.DetonatorGridPosition(gc);

        // The site being stood in, not every marker in the map. Scan.Targets holds both dig sites
        // of a regular map, so snapshotting it put the other expedition's markers in the site row
        // and made every composition wrong - a row claiming sixteen elite markers for a chain that
        // could not reach half of them.
        foreach (var target in _site == Vector2.Zero ? scan.Targets : scan.At(_site))
        {
            if (target.Grid != Vector2.Zero)
                _markers.Add(target);
        }

        _blasts.Clear();

        foreach (var at in Detonator.PlacedExplosiveGridPositions(gc))
            _blasts.Add(new Blast { At = at });

        _waves.Clear();

        foreach (var marker in _markers)
        {
            if (marker.Kind != TargetKind.Remnant)
                continue;

            // Read every pass and kept from the last one before detonation, because the choice can
            // be changed right up until the explosive lands.
            var remnant = new Waves
            {
                At = marker.Grid,
                Sockets = marker.Sockets,
                Used = valuation?.ChosenRunes(marker.Entity) ?? 0,
                Selected = valuation?.ChosenName(marker.Entity) ?? "",
            };

            // Who else is standing inside the radius its waves will be counted in. Itself excluded;
            // another remnant included, because two remnants sharing a radius contaminate each
            // other exactly as much as an elite marker does.
            foreach (var other in _markers)
            {
                if (!ReferenceEquals(other, marker) &&
                    Vector2.Distance(other.Grid, marker.Grid) <= wave)
                    Add(remnant.Near, other);
            }

            _waves.Add(remnant);
        }

        if (_radius <= 0f)
            return;

        // A marker belongs to the blast whose middle is nearest it, among those that reach it. The
        // game sets it off once however many blasts cover it, so counting it once is the truth.
        foreach (var marker in _markers)
        {
            var blast = Nearest(marker.Grid, out _);

            if (blast == null)
                continue;

            Add(blast, marker);
        }
    }

    /// <summary>The blast covering this spot, if any, and whether a second one nearly does too.</summary>
    private Blast Nearest(Vector2 at, out bool contested)
    {
        contested = false;

        Blast nearest = null;
        var closest = float.MaxValue;
        var next = float.MaxValue;

        foreach (var blast in _blasts)
        {
            var distance = Vector2.Distance(at, blast.At);

            if (distance < closest)
            {
                next = closest;
                closest = distance;
                nearest = blast;
            }
            else if (distance < next)
            {
                next = distance;
            }
        }

        if (nearest == null || closest > _radius + Slack)
            return null;

        contested = next - closest < Clearly;

        return nearest;
    }

    private void Credit(Vector2 at, MonsterRarity rarity, string name, bool late)
    {
        var blast = Nearest(at, out var contested);

        if (blast == null)
            return;

        blast.Names[name] = blast.Names.TryGetValue(name, out var had) ? had + 1 : 1;

        if (contested)
            blast.Contested++;

        switch (rarity)
        {
            case MonsterRarity.Unique:
                blast.Unique++;

                if (late)
                    blast.LateUnique++;

                break;

            case MonsterRarity.Rare:
                blast.Rare++;

                if (late)
                    blast.LateRare++;

                break;

            case MonsterRarity.Magic:
                blast.Magic++;

                if (late)
                    blast.LateMagic++;

                break;

            default:
                blast.Normal++;

                if (late)
                    blast.LateNormal++;

                break;
        }
    }

    /// <summary>
    /// Counts a monster against the remnant it appeared beside, if any.
    ///
    /// Nearest remnant within the radius, with no ambiguity guard: remnants are tens of grid apart
    /// where markers are ten, so a monster inside one remnant's radius is very rarely inside
    /// another's - and where two remnants are close, one blast usually set off both and the
    /// question of which owns the wave is not one the file needs to answer.
    /// </summary>
    private void Wave_(Vector2 at, MonsterRarity rarity, double since, float radius)
    {
        Waves nearest = null;
        var closest = radius;

        foreach (var remnant in _waves)
        {
            var distance = Vector2.Distance(at, remnant.At);

            if (distance <= closest)
            {
                closest = distance;
                nearest = remnant;
            }
        }

        if (nearest == null)
            return;

        nearest.Last = since;

        var late = since > Wave.TotalSeconds;

        switch (rarity)
        {
            case MonsterRarity.Unique:
                nearest.Unique++;

                if (late)
                    nearest.LateUnique++;

                break;

            case MonsterRarity.Rare:
                nearest.Rare++;

                if (late)
                    nearest.LateRare++;

                break;

            case MonsterRarity.Magic:
                nearest.Magic++;

                if (late)
                    nearest.LateMagic++;

                break;

            default:
                nearest.Normal++;

                if (late)
                    nearest.LateNormal++;

                break;
        }
    }

    private static MonsterRarity Rarity(Entity monster) =>
        Safe.Read(monster, static e => e.GetComponent<ObjectMagicProperties>()?.Rarity ?? MonsterRarity.White,
            MonsterRarity.White);

    private static string Leaf(string metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return "?";

        var cut = metadata.LastIndexOf('/');

        return cut < 0 || cut == metadata.Length - 1 ? metadata : metadata[(cut + 1)..];
    }

    /// <summary>
    /// One row per blast plus one for the chain, written when the site is left behind.
    ///
    /// Blasts that covered nothing go in too. A table built only from blasts that produced
    /// something cannot tell "always brings a rare" from "sometimes brings a rare and sometimes
    /// nothing", and those are different answers.
    /// </summary>
    private void Write(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        // A run that snapshotted no markers never saw the dig site, whatever it counted afterwards.
        //
        // That happened, and the file it produced was worse than no file: a chain row of twenty
        // four normal, two magic and two rare monsters, all of them ordinary map monsters standing
        // about before the chain was detonated, with every marker column at zero. Nothing about
        // those numbers says they are wrong unless you know to look at the marker columns.
        if (_written || _blasts.Count == 0 || _counted.Count == 0 || _markers.Count == 0)
        {
            _written = true;

            return;
        }

        _written = true;

        var rows = new List<string>();
        var when = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

        // What the dig site HELD, before anything about where the chain went. This is the row that
        // answers "this expedition had eight elite markers and three remnants" - the chain row
        // cannot, because it only counts what an explosive reached, and the difference between the
        // two is how much of the site the route covered.
        var site = new Blast();

        foreach (var marker in _markers)
            Add(site, marker);

        foreach (var remnant in _waves)
            site.Used += remnant.Used;

        rows.Add(Row(when, "site", -1, site));

        var chain = new Blast();

        for (var i = 0; i < _blasts.Count; i++)
        {
            var blast = _blasts[i];

            rows.Add(Row(when, "blast", i, blast));

            chain.Elites += blast.Elites;
            chain.Monsters += blast.Monsters;
            chain.Remnants += blast.Remnants;
            chain.Sockets += blast.Sockets;
            chain.RareChests += blast.RareChests;
            chain.MagicChests += blast.MagicChests;
            chain.NormalChests += blast.NormalChests;
            chain.Relics += blast.Relics;
            chain.Caged += blast.Caged;
            chain.Hatches += blast.Hatches;
            chain.Entrances += blast.Entrances;
            chain.Scenery += blast.Scenery;
            chain.Contested += blast.Contested;
            chain.LateNormal += blast.LateNormal;
            chain.LateMagic += blast.LateMagic;
            chain.LateRare += blast.LateRare;
            chain.LateUnique += blast.LateUnique;
        }

        // The chain's monster counts are the whole set, not the sum of the blasts: a monster that
        // fell outside every blast radius is still one the chain unearthed, and leaving it out of
        // the honest row to make the arithmetic tidy would defeat the point of having one.
        chain.Normal = _normal;
        chain.Magic = _magic;
        chain.Rare = _rare;
        chain.Unique = _unique;

        rows.Add(Row(when, "chain", -1, chain));

        // One row per remnant, on the remnant's own terms: everything that turned up within the
        // wave radius of it, however long after. Sockets and used are the columns it exists for.
        for (var i = 0; i < _waves.Count; i++)
        {
            var remnant = _waves[i];

            rows.Add(Row(when, "remnant", i, new Blast
            {
                At = remnant.At,

                // The composition is the NEIGHBOURHOOD, plus the remnant itself. A remnant row with
                // every marker column at zero is one whose waves nothing else could be confused
                // with, and those are the rows the per-socket question wants.
                Elites = remnant.Near.Elites,
                Monsters = remnant.Near.Monsters,
                RareChests = remnant.Near.RareChests,
                MagicChests = remnant.Near.MagicChests,
                NormalChests = remnant.Near.NormalChests,
                Relics = remnant.Near.Relics,
                Caged = remnant.Near.Caged,
                Hatches = remnant.Near.Hatches,
                Entrances = remnant.Near.Entrances,
                Scenery = remnant.Near.Scenery,
                Remnants = 1 + remnant.Near.Remnants,
                Sockets = remnant.Sockets,
                Used = remnant.Used,
                Selected = remnant.Selected,
                Normal = remnant.Normal,
                Magic = remnant.Magic,
                Rare = remnant.Rare,
                Unique = remnant.Unique,
                LateNormal = remnant.LateNormal,
                LateMagic = remnant.LateMagic,
                LateRare = remnant.LateRare,
                LateUnique = remnant.LateUnique,
                Seconds = remnant.Last,
            }));
        }

        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "spawns.csv");

                const string header =
                    "when,run,areaHash,level,radius,scope,index,x,y," +
                    "elites,monsterMarkers,remnants,sockets,used," +
                    "rareChests,magicChests,normalChests," +
                    "relics,caged,hatches,entrances,scenery," +
                    "normal,magic,rare,unique,contested," +
                    "lateNormal,lateMagic,lateRare,lateUnique,seconds,selected,ending,names";

                // A file whose columns no longer match is moved aside rather than appended to.
                //
                // **Rows of two different shapes in one CSV are worse than two files.** Everything
                // read out of here is read by column, so a single header with rows of two widths
                // silently shifts every later field - and the fields are counts, which look
                // perfectly plausible when they are wrong. The old rows are still evidence about the
                // sites they came from, so they are kept under their own name.
                if (File.Exists(_path))
                {
                    var lines = File.ReadLines(_path).GetEnumerator();
                    var first = lines.MoveNext() ? lines.Current : null;

                    if (!string.Equals(first, header, StringComparison.Ordinal))
                    {
                        File.Move(_path,
                            Path.Combine(directory,
                                $"spawns_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
                    }
                }

                if (!File.Exists(_path))
                    File.WriteAllText(_path, header + "\n");
            }

            File.AppendAllLines(_path, rows);
            _rows += rows.Count;

            if (_arrivals.Count > 0)
            {
                var timeline = Path.Combine(Path.GetDirectoryName(_path) ?? ".", "arrivals.csv");

                if (!File.Exists(timeline))
                    File.WriteAllText(timeline, "run,seconds,rarity,x,y,blast,remnant,name\n");

                File.AppendAllLines(timeline, _arrivals);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the spawn census: {ex.Message}", 5f);
        }
    }

    /// <summary>Counts one marker into a composition, by kind and tier.</summary>
    private static void Add(Blast into, Target marker)
    {
        switch (marker.Kind)
        {
            case TargetKind.Elite:
                into.Elites++;

                break;

            case TargetKind.Remnant:
                into.Remnants++;
                into.Sockets += marker.Sockets;

                break;

            case TargetKind.Chest:
                switch (marker.Tier)
                {
                    case ChestTier.Rare:
                        into.RareChests++;

                        break;

                    case ChestTier.Common:
                        into.NormalChests++;

                        break;

                    default:
                        into.MagicChests++;

                        break;
                }

                break;

            case TargetKind.Relic:
                into.Relics++;

                break;

            // Counted together, because the column is about monsters a blast lets out and both
            // still are. Splitting it would change the shape of a file that is read across sessions
            // to answer a question neither kind has raised yet.
            case TargetKind.Caged:
            case TargetKind.Monolith:
                into.Caged++;

                break;

            case TargetKind.Hatch:
                into.Hatches++;

                break;

            case TargetKind.Entrance:
                into.Entrances++;

                break;

            case TargetKind.Scenery:
                into.Scenery++;

                break;

            default:
                into.Monsters++;

                break;
        }
    }

    private int _normal;
    private int _magic;
    private int _rare;
    private int _unique;

    private string Row(string when, string scope, int index, Blast blast) => string.Join(",",
        when,
        _run,
        _area.ToString(CultureInfo.InvariantCulture),
        _level.ToString(CultureInfo.InvariantCulture),
        _radius.ToString("0.#", CultureInfo.InvariantCulture),
        scope,
        index.ToString(CultureInfo.InvariantCulture),
        ((int)blast.At.X).ToString(CultureInfo.InvariantCulture),
        ((int)blast.At.Y).ToString(CultureInfo.InvariantCulture),
        blast.Elites.ToString(CultureInfo.InvariantCulture),
        blast.Monsters.ToString(CultureInfo.InvariantCulture),
        blast.Remnants.ToString(CultureInfo.InvariantCulture),
        blast.Sockets.ToString(CultureInfo.InvariantCulture),
        blast.Used.ToString(CultureInfo.InvariantCulture),
        blast.RareChests.ToString(CultureInfo.InvariantCulture),
        blast.MagicChests.ToString(CultureInfo.InvariantCulture),
        blast.NormalChests.ToString(CultureInfo.InvariantCulture),
        blast.Relics.ToString(CultureInfo.InvariantCulture),
        blast.Caged.ToString(CultureInfo.InvariantCulture),
        blast.Hatches.ToString(CultureInfo.InvariantCulture),
        blast.Entrances.ToString(CultureInfo.InvariantCulture),
        blast.Scenery.ToString(CultureInfo.InvariantCulture),
        blast.Normal.ToString(CultureInfo.InvariantCulture),
        blast.Magic.ToString(CultureInfo.InvariantCulture),
        blast.Rare.ToString(CultureInfo.InvariantCulture),
        blast.Unique.ToString(CultureInfo.InvariantCulture),
        blast.Contested.ToString(CultureInfo.InvariantCulture),
        blast.LateNormal.ToString(CultureInfo.InvariantCulture),
        blast.LateMagic.ToString(CultureInfo.InvariantCulture),
        blast.LateRare.ToString(CultureInfo.InvariantCulture),
        blast.LateUnique.ToString(CultureInfo.InvariantCulture),
        Seconds(blast).ToString("0.#", CultureInfo.InvariantCulture),
        Clean(blast.Selected),
        _end.Length == 0 ? "?" : _end,
        Clean(Names(blast)));

    /// <summary>When the last monster on this row turned up, in seconds after the first anywhere.</summary>
    private double Seconds(Blast blast) =>
        blast.Seconds > 0d ? blast.Seconds : _last <= _first ? 0d : (_last - _first).TotalSeconds;

    private DateTime _last;

    private static string Names(Blast blast)
    {
        if (blast.Names.Count == 0)
            return "";

        var text = new List<string>();

        foreach (var pair in blast.Names)
            text.Add($"{pair.Key} x{pair.Value}");

        return string.Join(" | ", text);
    }

    private static string Clean(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace(',', ';').Replace('\n', ' ').Trim();

    /// <summary>Live counts for the debug tally: what has been unearthed, by rarity.</summary>
    public (int Normal, int Magic, int Rare, int Unique, int Waves, int Far, bool Counting) Live =>
        (_normal, _magic, _rare, _unique, Waved,
            Math.Max(0, _counted.Count - (_normal + _magic + _rare + _unique)),
            _frozen && !_done);

    private int Waved
    {
        get
        {
            var late = 0;

            foreach (var remnant in _waves)
                late += remnant.Late;

            return late;
        }
    }

    /// <summary>What has been seen so far, for the readout.</summary>
    public string Describe()
    {
        if (_site != Vector2.Zero && _finished.Contains(((int)_site.X, (int)_site.Y)))
            return "this dig site has been recorded already";

        if (!_frozen)
        {
            return _markers.Count == 0
                ? "nothing seen yet"
                : $"{_markers.Count} markers and {_blasts.Count} explosives watched, " +
                  $"{_before.Count} monsters already here - " +
                  (_live
                      ? "waiting for the chain to go off"
                      : "the detonator has not been seen unfired - not recording this one");
        }

        var counted = 0;
        var contested = 0;

        foreach (var blast in _blasts)
        {
            counted += blast.Normal + blast.Magic + blast.Rare + blast.Unique;
            contested += blast.Contested;
        }

        return $"{_blasts.Count} blasts over {_markers.Count} markers, radius {_radius:0.#}; " +
               $"{_counted.Count} monsters unearthed, {counted} inside a blast, {contested} in two, " +
               $"{(DateTime.UtcNow - _first).TotalSeconds:0}s since the first";
    }
}
