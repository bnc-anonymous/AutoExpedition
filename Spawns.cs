using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// The modifiers of the relics counted in Relics, by id - what they do to the monsters that follow, which a
        /// count cannot say. A site with relics granting more rares, magic monsters, pack size or duplicated runic
        /// monsters is not comparable with one without, and nothing said which sites those were. On a remnant row,
        /// the relics caught at or before its link; on the site row, every relic the site held.
        /// </summary>
        public readonly SortedSet<string> RelicMods = new(StringComparer.Ordinal);

        /// <summary>Site row only: the map's visible modifiers as stat=value, read when the chain went off.</summary>
        public string MapStats = "";

        /// <summary>
        /// Site row only: the server's atlas stats on rare and magic monsters or packs, as stat=value, read when the chain
        /// went off. The map's visible stats leave the atlas out - "186% increased number of rare monsters" was map 126
        /// plus atlas 60 - so a map's total needs both. See AtlasStats.ConcernsMonsterRarity.
        /// </summary>
        public string AtlasStats = "";

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

        /// <summary>
        /// Remnant rows only: the remnant's "activated" state each time it changed, as value@seconds after the
        /// detonation, the propagated runes arriving at it and how many of those it already held, and which
        /// explosive of the chain took it (1 for the first, 0 for none). Blank or -1 on other rows. See Waves.
        /// </summary>
        public string States = "";

        /// <summary>See States.</summary>
        public int Inherited = -1;

        /// <summary>See States.</summary>
        public int Wasted = -1;

        /// <summary>See States.</summary>
        public int ChainLink = -1;

        /// <summary>Remnant rows only: see Waves.Runes, Arriving, Encounter and EncounterRares.</summary>
        public string Runes = "";

        /// <summary>See Runes.</summary>
        public string Arriving = "";

        /// <summary>
        /// Remnant rows only: the runes the remnant's own first-wave monsters carried beyond its first two sockets -
        /// what was actually passed in, where Arriving is what the plan expected. Empty where none was seen. A rune
        /// passed in that is also in one of the first two sockets cannot be told apart. See ObservedOfRemnant.
        /// </summary>
        public string ArrivingSeen = "";

        /// <summary>
        /// Remnant rows only: when the remnant's first first-wave monster appeared, in seconds after the run's first
        /// arrival, or -1 - the order the chain actually went off in, where ChainLink is inferred from where the
        /// explosives lie. See ObservedOfRemnant.
        /// </summary>
        public double FirstWaveSeconds = -1d;

        /// <summary>
        /// Remnant rows only: how many normal, magic and rare monsters the model expected the remnant's waves to bring,
        /// with its own effects and without, and the table fingerprint it was worked out under. Null on other rows and
        /// where no detailed pass priced the remnant. See Planner.WavesPredictedByRemnant and Wrt.TableFingerprint.
        /// </summary>
        public Planner.WavesPredicted? Predicted;

        /// <summary>See Predicted.</summary>
        public string PredictedTable = "";

        /// <summary>See Runes.</summary>
        public int Encounter = -1;

        /// <summary>See Runes.</summary>
        public int EncounterRares = -1;

        /// <summary>Remnant rows only: see EncounterWavesOfRemnant. -1 and blank on other rows.</summary>
        public int EncounterWaveCount = -1;

        /// <summary>See EncounterWaveCount.</summary>
        public string EncounterWaveSizes = "";

        /// <summary>Remnant rows only: the waves seen, from arrivals' runes. See Spawns.RuneWaves. Blank on other rows.</summary>
        public string RuneWaves = "";

        /// <summary>Remnant rows only: the highest wave seen from arrivals' runes, or -1. See Spawns.RuneWaves.</summary>
        public int HighestRuneWave = -1;

        /// <summary>Remnant rows only: see Waves.RemnantStateChanges, ControllerModChanges and ControllerStatChanges.</summary>
        public string RemnantStateChanges = "";

        /// <summary>See RemnantStateChanges.</summary>
        public string ControllerModChanges = "";

        /// <summary>See RemnantStateChanges.</summary>
        public string ControllerStatChanges = "";

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
        /// The remnant's "activated" state through the run, value@seconds after the detonation, one entry per
        /// change. The state steps through several values after the chain goes off - 3, 5, 6, 7 were seen at
        /// one site over six minutes - and whether those steps are waves is what this is for.
        /// </summary>
        public readonly System.Text.StringBuilder States = new();

        /// <summary>The last state written into States, so only changes are kept.</summary>
        public long LastState = long.MinValue;

        /// <summary>
        /// The propagated runes the plan had arriving at this remnant, and how many of those it already
        /// held, from the last detailed pass before detonation; -1 when it had none. See Planner.RuneTally.
        /// </summary>
        public int Inherited = -1;

        /// <summary>See Inherited.</summary>
        public int Wasted = -1;

        /// <summary>
        /// The chosen combination's runes in slot order, a propagating slot marked *, and the propagated runes
        /// the plan had arriving at it, by name. See Valuation.ChosenRuneNames and Planner.RuneTally.Arriving.
        /// </summary>
        public string Runes = "";

        /// <summary>See Runes.</summary>
        public string Arriving = "";

        /// <summary>The plan's predicted counts for this remnant, read every pass until detonation. See Blast.Predicted.</summary>
        public Planner.WavesPredicted? Predicted;

        /// <summary>See Blast.Predicted.</summary>
        public string PredictedTable = "";

        /// <summary>
        /// The monsters that arrived within the wave radius while this remnant's encounter was running - its
        /// "activated" state at 5, which on the one site recorded is the stretch from starting the encounter to
        /// the fight ending - and how many of those were rare. Nothing is excluded for standing near a marker:
        /// the markers in the same radius are counted beside it instead. See Near.
        /// </summary>
        public int Encounter;

        /// <summary>See Encounter.</summary>
        public int EncounterRares;

        /// <summary>
        /// When each of the encounter's monsters arrived, in seconds after the detonation. The raw material
        /// for the wave count - see EncounterWavesOfRemnant.
        /// </summary>
        public readonly List<double> EncounterTimes = new();

        /// <summary>When the state first read EncounterRunning, in seconds after the detonation; -1 before.</summary>
        public double EncounterStart = -1d;

        /// <summary>Whether the state has read ChainDetonated and RewardGiven. See RemnantsRewarded.</summary>
        public bool SetOff;

        /// <summary>See SetOff.</summary>
        public bool Rewarded;

        /// <summary>Seconds after the detonation at which this remnant gave its reward, or -1. See Drops.</summary>
        public double RewardSeconds = -1d;

        /// <summary>
        /// Every StateMachine value on the remnant other than "activated" (which States holds), as
        /// name=value@seconds after the detonation, one entry per change. Recorded to find out whether any of
        /// them counts waves or monsters; nothing is known to.
        /// </summary>
        public readonly System.Text.StringBuilder RemnantStateChanges = new();

        /// <summary>See RemnantStateChanges: the last value of each, so only changes are written.</summary>
        public readonly Dictionary<string, long> RemnantStateValues = new(StringComparer.Ordinal);

        /// <summary>
        /// The same for the RuneEncounterController standing on the remnant: its modifiers (mod:id=1) and buffs
        /// (buff:name=stacks) in one log, its stats in another. It is the entity most likely to hold a wave
        /// number or the runes in force, if anything does. It has no StateMachine component - read off a dump
        /// of Frigid Bluffs, 2026-09-30 - so there are no states to record. Matched by position, within
        /// ControllerRange. See ControllerChanges.
        /// </summary>
        public readonly System.Text.StringBuilder ControllerModChanges = new();

        /// <summary>See ControllerModChanges.</summary>
        public readonly Dictionary<string, long> ControllerModValues = new(StringComparer.Ordinal);

        /// <summary>See ControllerModChanges.</summary>
        public readonly System.Text.StringBuilder ControllerStatChanges = new();

        /// <summary>See ControllerModChanges.</summary>
        public readonly Dictionary<string, long> ControllerStatValues = new(StringComparer.Ordinal);

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

    /// <summary>The counted monsters' arrivals by entity id, so each one's death can be noted. See Deaths.</summary>
    private readonly Dictionary<uint, Arrival> _arrivalsById = new();

    /// <summary>
    /// Ground items already looked at: those down before the chain went off, so they are never taken for drops, and
    /// every one seen since. See Drops.
    /// </summary>
    private readonly HashSet<uint> _itemsSeen = new();

    /// <summary>Drops recorded this run, in the order first seen, each a row of drops.csv. See Drops.</summary>
    private readonly List<RecordedDrop> _drops = new();

    /// <summary>The same drops by ground item id, for reading each again while it lies there. See Drops.</summary>
    private readonly Dictionary<uint, RecordedDrop> _dropsById = new();

    /// <summary>
    /// One recorded drop: the columns fixed when it was first seen, and what later reads of it found. The stack is read
    /// on every frame the item is on the ground because a single read on its first frame is not known to be its final
    /// size: one map's Verisium was recorded far below what the player picked up. See DropsHeader.
    /// </summary>
    private sealed class RecordedDrop
    {
        public string FirstColumns;
        public int LargestStack;
        public float PlayerDistance;
        public double FirstSeen;
        public double LastSeen;

        /// <summary>The item's base name, where it landed and its first stack, for knowing it again when re-dropped. See Drops.</summary>
        public string Name;
        public Vector2 At;
        public int FirstStack;

        /// <summary>Where the remnant it is credited to stands, or null. See DropsHeader.</summary>
        public Vector2? RemnantAt;

        public override string ToString() =>
            string.Join(",", FirstColumns,
                LargestStack.ToString(CultureInfo.InvariantCulture),
                PlayerDistance.ToString("0.#", CultureInfo.InvariantCulture),
                (LastSeen - FirstSeen).ToString("0.###", CultureInfo.InvariantCulture),
                RemnantAt is { } remnant ? ((int)remnant.X).ToString(CultureInfo.InvariantCulture) : "-1",
                RemnantAt is { } again ? ((int)again.Y).ToString(CultureInfo.InvariantCulture) : "-1");
    }

    /// <summary>Each chest's opened state when last read, by entity id, so an opening is seen as it happens. See ChestOpenings.</summary>
    private readonly Dictionary<uint, bool> _chestOpened = new();

    /// <summary>chests.csv's rows for this run. See ChestOpenings.</summary>
    private readonly List<string> _chestRows = new();

    /// <summary>drops.csv's rows for this run. See RecordedDrop.</summary>
    private List<string> DropRows() => _drops.ConvertAll(x => x.ToString());

    /// <summary>Items a remnant was credited with this run, in the order first seen, each a row of remnant_items.csv. See RemnantItem.</summary>
    private readonly List<RecordedDrop> _remnantItems = new();

    /// <summary>The same items by ground item id, for reading each again while it lies there. See RemnantItem.</summary>
    private readonly Dictionary<uint, RecordedDrop> _remnantItemsById = new();

    /// <summary>remnant_items.csv's rows for this run. See RemnantItem.</summary>
    private List<string> RemnantItemRows() => _remnantItems.ConvertAll(x => x.ToString());

    /// <summary>Each counted monster's non-rune affixes at its last read, by entity id. See BondTransfers.</summary>
    private readonly Dictionary<uint, HashSet<string>> _affixesById = new();

    /// <summary>Deaths of monsters carrying Bond this run, kept for crediting gains to. See BondTransfers.</summary>
    private readonly List<(uint Id, double Seconds, Vector2 At, MonsterRarity Rarity, bool Empowered, HashSet<string> Affixes)>
        _bondDeaths = new();

    /// <summary>Rows of bond_deaths.csv and bond_gains.csv for this run. See BondTransfers.</summary>
    private readonly List<string> _bondDeathRows = new();

    private readonly List<string> _bondGainRows = new();

    /// <summary>When affixes were last read. See BondTransfers.</summary>
    private DateTime _affixesReadAt;

    private bool _frozen;

    /// <summary>
    /// What the plan credited the route with when the run was frozen, per link and in total, for the census row's last
    /// four columns. Null until taken. See Planning.CreditOfRoute.
    /// </summary>
    private (List<(double Content, double Carried)> Each, double Content, double Propagation)? _routeCredit;
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
    /// How far past the blast radius a blast sets a remnant off, in grid units. Measured: remnants 0.9, 1.3 and 2.0 grid
    /// past the radius were set off by that blast, and one 3.8 past was not - it waited for a later blast 20.6 away
    /// (2026-10-01; the wave timings say which blast). So somewhere between 2 and 3.8; 3 until a case says otherwise.
    /// See BlastThatCaught.
    /// </summary>
    private const float RemnantReachSlack = 3f;

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
    /// Whether this run began after the chain had already gone off - the player left the area and came back - with
    /// a remnant still waiting to be fought. Only the remnants fought after that are written, every monster already
    /// standing there is excluded, and the ending column reads "resumed-" before how it ended, because the site,
    /// blast and chain rows count only what arrived after the return. See ArmAfterReturn.
    /// </summary>
    private bool _resumed;

    /// <summary>
    /// How this run ended, written into every one of its rows.
    ///
    /// The difference between a complete observation and an abandoned one, and it has to be in the
    /// file rather than inferred from it: a run that ended because the map was left is missing
    /// however many waves had not arrived, and pooling it with a finished one would drag every
    /// per-socket average down by an unknown amount. With the column, "rewarded only" is a filter.
    /// </summary>
    private string _end = "";

    /// <summary>
    /// When the "Expedition Complete" banner was first seen during this run, in seconds after the detonation,
    /// with "-early" when explosives were still in hand; blank when it never showed. Recorded, not acted on:
    /// the banner has been seen with monsters still to come, so it does not end the run. See RemnantsRewarded.
    /// </summary>
    private string _banner = "";

    private bool Ending(string how)
    {
        _end = (_resumed ? "resumed-" : "") + how;

        return true;
    }

    public void AreaChange(BaseSettingsPlugin<AutoExpeditionSettings> plugin, uint areaHash)
    {
        if (_end.Length == 0)
            _end = (_resumed ? "resumed-" : "") + "left-area";

        Write(plugin);
        Reset();

        _area = areaHash;
        _runs = 0;
        _finished.Clear();
    }

    /// <summary>Ready for the next dig site, in this area or the next.</summary>
    /// <summary>The map's visible modifiers when the run froze. See Blast.MapStats.</summary>
    private string _mapStats = "";

    /// <summary>The atlas's stats on rare and magic monsters when the run froze. See Blast.AtlasStats.</summary>
    private string _atlasStats = "";

    private void Reset()
    {
        _mapStats = "";
        _atlasStats = "";
        _blasts.Clear();
        _waves.Clear();
        _markers.Clear();
        _arrivals.Clear();
        _arrivalsById.Clear();
        _itemsSeen.Clear();
        _drops.Clear();
        _dropsById.Clear();
        _remnantItems.Clear();
        _remnantItemsById.Clear();
        _chestOpened.Clear();
        _chestRows.Clear();
        _affixesById.Clear();
        _bondDeaths.Clear();
        _bondDeathRows.Clear();
        _bondGainRows.Clear();
        _before.Clear();
        _frozen = false;
        _routeCredit = null;
        _written = false;
        _done = false;
        _live = false;
        _resumed = false;
        _site = Vector2.Zero;
        _end = "";
        _banner = "";
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
        AutoExpeditionSettings settings, Valuation valuation, float radius, Planning planning = null)

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
            // The "Expedition Complete" banner is noted, not obeyed. It has been seen with monsters still to come,
            // and once on the first detonation of a site with four explosives in hand, so ending on it cut off
            // every encounter started afterwards. The run ends RemnantDropWindow after every remnant set off has given its reward,
            // on walking to another detonator, or on the timeout; leaving the area ends it too. The banner's
            // time goes in its own column. Blind means the panel could not be read, which says nothing.
            if (_banner.Length == 0 && Finished.Showing(gc))
            {
                var spent = Safe.Read(() => Detonator.PanelUnreadable(gc) || Detonator.ExplosivesInHand(gc) <= 0, true);

                _banner = (DateTime.UtcNow - _first).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                          (spent ? "" : "-early");
            }

            // Not at the last reward but RemnantDropWindow after it: a remnant's drops land about a second after its
            // reward, so ending on the reward lost the last remnant's Verisium every run - on Scorched Cay
            // (2026-10-03) the remnant with the most kills of the run, 204 monsters, and nothing recorded.
            if (RemnantsRewarded() && (DateTime.UtcNow - _first).TotalSeconds - LastRewardSeconds() > RemnantDropWindow)
                _done = Ending("rewarded");
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
                MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 120f)));

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
                FreezeRun(gc);

            if (!_frozen && !_live && activated >= 1 && ArmAfterReturn(gc, monsters))
                FreezeRun(gc);

            // What the plan credited the route with as it went off, once per run. See Planning.CreditOfRoute.
            if (_frozen && _routeCredit == null)
                _routeCredit = planning?.CreditOfRoute();

            if (!_frozen)
            {
                foreach (var monster in monsters)
                {
                    var id = Safe.Read(monster, static e => e.Id, 0u);

                    if (id != 0u)
                        _before.Add(id);
                }

                // Whatever is lying about before the chain goes off is not a drop of anything it unearths.
                foreach (var item in ItemsOnGround(gc))
                {
                    var id = Safe.Read(item, static e => e.Id, 0u);

                    if (id != 0u)
                        _itemsSeen.Add(id);
                }

                return;
            }
        }

        // Where the player stood when each monster was first seen, so a late sighting of a monster that spawned
        // out of loading range can be told from a late spawn. See the fromPlayer column of arrivals.csv.
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id == 0u || _before.Contains(id) || Ignore(monster) || !_counted.Add(id))
                continue;

            var at = Safe.Read(monster, static e => e.GridPos, Vector2.Zero);

            // Within the site, or within the wave radius of one of its remnants: a remnant's waves can arrive
            // further from every explosive than the site limit, and were dropped before anything counted them.
            if (at == Vector2.Zero ||
                (Away(at, site) > Site && !NearRemnant(at,
                    MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 120f)))))
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

            // Every rarity's modifiers, not only magic and up: a remnant's wave monsters carry one modifier per rune in
            // force, which is what says which remnant and which wave each came from. See RuneWaves.
            var arrival = new Arrival
            {
                Monster = monster,
                Seen = DateTime.UtcNow,
                At = at,
                LastAt = at,
                Rarity = rarity,
                Id = id,
            };

            _arrivalsById[id] = arrival;

            arrival.Mods = ModNames(arrival.Monster);
            arrival.NoDropsAtArrival = NoDropsOfMonster(monster);
            arrival.WatchedStatsAtArrival = WatchedStatsOfMonster(monster);
            arrival.RarityNow = rarity;
            arrival.MaxLifeAtArrival = MaxLifeOfMonster(monster);
            arrival.MaxLifeHighest = arrival.MaxLifeAtArrival;
            arrival.Row = string.Join(",",
                _run,
                since.ToString("0.###", CultureInfo.InvariantCulture),
                rarity.ToString(),
                ((int)at.X).ToString(CultureInfo.InvariantCulture),
                ((int)at.Y).ToString(CultureInfo.InvariantCulture),
                BlastThatCaught(at, _radius + Slack).ToString(CultureInfo.InvariantCulture),
                Which(_waves, at, MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 120f)))
                    .ToString(CultureInfo.InvariantCulture),
                player == Vector2.Zero ? "-1" : ((int)Vector2.Distance(player, at)).ToString(CultureInfo.InvariantCulture),
                Clean(name));
            _arrivals.Add(arrival);

            // And separately, against whichever remnant it appeared beside - a different question
            // from which blast it belongs to, and one the blast rows cannot answer, since a wave
            // arrives minutes after the explosive that started it.
            Wave_(at, rarity, since,
                MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 120f)));

            // Against the nearest remnant whose encounter is running, within the wave radius. Crediting every
            // running remnant in range counted each monster twice when two encounters 88 grid apart ran at once.
            // See Waves.Encounter.
            var waveRadius = MathF.Max(1f, Safe.Read(() => settings.Recording.WaveRadius.Value, 120f));
            Waves running = null;

            foreach (var remnant in _waves)
            {
                if (remnant.LastState != EncounterRunning || Vector2.Distance(at, remnant.At) > waveRadius)
                    continue;

                if (running == null || Vector2.Distance(at, remnant.At) < Vector2.Distance(at, running.At))
                    running = remnant;
            }

            if (running != null)
            {
                running.Encounter++;
                running.EncounterTimes.Add(since);

                if (rarity == MonsterRarity.Rare)
                    running.EncounterRares++;
            }

            _last = DateTime.UtcNow;
        }

        ModsNotYetRead();
        RemnantStates(scan);
        ControllerChanges(monsters);
        // Each part behind its own Data collection switch. Deaths are watched whenever anything needs them - drops and
        // Bond gains are credited to deaths - and written only when their own switch is on. See Write.
        var deaths = Safe.Read(() => settings.Recording.CollectMonsterDeaths.Value, true);
        var drops = Safe.Read(() => settings.Recording.CollectCurrencyDrops.Value, true);
        var bond = Safe.Read(() => settings.Recording.CollectBondTransfers.Value, true);
        var remnantDrops = Safe.Read(() => settings.Recording.CollectRemnantDrops.Value, false);

        if (deaths || drops || bond || remnantDrops)
            Deaths(monsters);

        if (deaths)
            Strengthening(monsters);

        if (bond)
            BondTransfers(monsters,
                TimeSpan.FromMilliseconds(Safe.Read(() => settings.Recording.BondTransferReadMs.Value, 60)));

        if (drops || remnantDrops)
        {
            Drops(gc, settings, drops, remnantDrops);
            ChestOpenings(gc);
        }
    }

    /// <summary>Starts recording a run: its clock, its key, and the map's and atlas's monster stats.</summary>
    private void FreezeRun(GameController gc)
    {
        _frozen = true;
        _first = DateTime.UtcNow;
        _run = $"{_area:X8}-{_session}-{++_runs}";

        // The map's own modifiers, which change what spawns as much as a relic does. See Blast.MapStats.
        _mapStats = Safe.Read(() =>
        {
            var said = new List<string>();

            foreach (var (stat, value) in gc.IngameState.Data.MapStatsVisible)
                said.Add($"{stat}={value}");

            said.Sort(StringComparer.Ordinal);

            return string.Join(" ", said);
        }, "");

        // And the atlas's part of the same totals, which the map's visible stats leave out. See Blast.AtlasStats.
        _atlasStats = Safe.Read(() =>
        {
            var said = new List<string>();

            foreach (var (stat, value) in gc.IngameState.ServerData.AtlasStats)
            {
                if (AtlasStats.ConcernsMonsterRarity(stat.ToString()))
                    said.Add($"{stat}={value}");
            }

            said.Sort(StringComparer.Ordinal);

            return string.Join(" ", said);
        }, "");
    }

    /// <summary>
    /// Whether to start a run on a dig site whose chain went off before the recorder was watching, which is what
    /// coming back after leaving the area looks like. Only when a remnant of the site still reads ChainDetonated -
    /// set off, its encounter not started - because that encounter can still be recorded whole; an encounter already
    /// running when the player came back has lost its early waves. Every monster and item already present is
    /// marked as seen first, so nothing from before the return is counted. See _resumed.
    /// </summary>
    private bool ArmAfterReturn(GameController gc, IEnumerable<Entity> monsters)
    {
        if (_site == Vector2.Zero || _finished.Contains(((int)_site.X, (int)_site.Y)))
            return false;

        var waiting = false;

        foreach (var marker in _markers)
        {
            if (marker.Kind == TargetKind.Remnant && Safe.Read(() => marker.State("activated"), -1L) == ChainDetonated)
            {
                waiting = true;

                break;
            }
        }

        if (!waiting)
            return false;

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id != 0u)
                _before.Add(id);
        }

        foreach (var item in ItemsOnGround(gc))
        {
            var id = Safe.Read(item, static e => e.Id, 0u);

            if (id != 0u)
                _itemsSeen.Add(id);
        }

        _resumed = true;

        return true;
    }

    /// <summary>
    /// Watches what Bond moves, so its worth can be measured rather than argued. Bond's modifier holds
    /// ChancePctToTransferRareModAndHealRareMonsterWithin80UnitsOnDeath: on death, a chance to move a modifier to a
    /// rare within range. What was not known: whether magic monsters carrying it transfer too, whether magic or normal
    /// monsters can receive, how often it fires and how many modifiers it moves. The first recorder could not say -
    /// it watched rares only and credited a receiver's gain again for every death in a three second window.
    ///
    /// Every counted monster's non-rune affixes are re-read, at most every readEvery and only while a monster
    /// carrying Bond is alive, so a quiet site costs nothing. A Bond carrier's death, any rarity, goes to
    /// bond_deaths.csv with its affixes and the monsters alive within BondRange by rarity. A monster of any rarity
    /// gaining affixes goes to bond_gains.csv once per read, against its own previous read, credited to the nearest
    /// Bond death within BondRange in the BondCreditWindow seconds before, with how many deaths qualified and how many
    /// of the gained affixes that monster had - the proof of a transfer, which a gain from anything else will lack.
    /// </summary>
    private void BondTransfers(IEnumerable<Entity> monsters, TimeSpan readEvery)
    {
        var now = DateTime.UtcNow;

        if (now - _affixesReadAt < readEvery)
            return;

        _affixReadEvery = readEvery;

        _affixesReadAt = now;

        var since = (now - _first).TotalSeconds;

        // Bond carriers are known from their arrival modifiers; with none alive there is nothing to watch.
        var bondAlive = _arrivalsById.Values.Any(x => x.DeathSeconds < 0d && CarriesBond(x.Mods));

        // A carrier that has died since the last read: noted with its last affixes and who was near it.
        foreach (var arrival in _arrivalsById.Values)
        {
            if (arrival.DeathSeconds < 0d || arrival.BondDeathWritten || !CarriesBond(arrival.Mods))
                continue;

            arrival.BondDeathWritten = true;

            var affixes = _affixesById.TryGetValue(arrival.Id, out var had) ? had : AffixesOf(arrival.Mods);
            var near = _arrivalsById.Values
                .Where(x => x.Id != arrival.Id && (x.DeathSeconds < 0d || x.DeathSeconds >= arrival.DeathSeconds) &&
                            Vector2.Distance(x.LastAt, arrival.LastAt) <= BondRange)
                .ToList();
            var empowered = arrival.Mods.Contains(BondModifier + "Power", StringComparison.Ordinal);

            _bondDeaths.Add((arrival.Id, arrival.DeathSeconds, arrival.LastAt, arrival.Rarity, empowered, affixes));
            _bondDeathRows.Add(string.Join(",",
                _run,
                arrival.Id.ToString(CultureInfo.InvariantCulture),
                arrival.DeathSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                ((int)arrival.LastAt.X).ToString(CultureInfo.InvariantCulture),
                ((int)arrival.LastAt.Y).ToString(CultureInfo.InvariantCulture),
                arrival.Rarity.ToString(),
                empowered ? "1" : "0",
                Clean(string.Join(" ", affixes)),
                near.Count(x => x.Rarity is MonsterRarity.Rare or MonsterRarity.Unique).ToString(CultureInfo.InvariantCulture),
                near.Count(x => x.Rarity == MonsterRarity.Magic).ToString(CultureInfo.InvariantCulture),
                near.Count(x => x.Rarity == MonsterRarity.White).ToString(CultureInfo.InvariantCulture)));
        }

        if (!bondAlive && _bondDeaths.Count == 0)
            return;

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id == 0u || !_arrivalsById.TryGetValue(id, out var arrival))
                continue;

            // A monster seen dead is read once more while its corpse is still listed. Deaths runs before this in the
            // same pass, so without it a rare that gains an affix and dies between two reads is never seen gaining it,
            // which is common when Bond monsters and rares die in the same burst.
            var dead = arrival.DeathSeconds >= 0d;

            if (dead && (arrival.Vanished || arrival.AffixesReadAfterDeath))
                continue;

            arrival.AffixesReadAfterDeath = dead;

            var affixes = AffixesOf(ModNames(monster));

            if (affixes.Count == 0)
                continue;

            if (_affixesById.TryGetValue(id, out var before))
            {
                var gained = affixes.Where(x => !before.Contains(x)).ToList();

                if (gained.Count > 0)
                    NoteGain(arrival, gained, since, dead);
            }

            _affixesById[id] = affixes;
        }
    }

    /// <summary>One gain of affixes, credited to the nearest Bond death that could have given it. See BondTransfers.</summary>
    private void NoteGain(Arrival receiver, List<string> gained, double since, bool receiverDead)
    {
        var candidates = 0;
        var nearest = BondRange;
        (uint Id, double Seconds, Vector2 At, MonsterRarity Rarity, bool Empowered, HashSet<string> Affixes)? from = null;

        foreach (var death in _bondDeaths)
        {
            var after = since - death.Seconds;
            var distance = Vector2.Distance(death.At, receiver.LastAt);

            if (after < -_affixReadEvery.TotalSeconds || after > BondCreditWindow || distance > BondRange)
                continue;

            candidates++;

            if (distance <= nearest)
            {
                nearest = distance;
                from = death;
            }
        }

        _bondGainRows.Add(string.Join(",",
            _run,
            since.ToString("0.###", CultureInfo.InvariantCulture),
            receiver.Id.ToString(CultureInfo.InvariantCulture),
            receiver.Rarity.ToString(),
            ((int)receiver.LastAt.X).ToString(CultureInfo.InvariantCulture),
            ((int)receiver.LastAt.Y).ToString(CultureInfo.InvariantCulture),
            Clean(string.Join(" ", gained)),
            gained.Count.ToString(CultureInfo.InvariantCulture),
            from == null ? "-1" : from.Value.Id.ToString(CultureInfo.InvariantCulture),
            from == null ? "" : from.Value.Rarity.ToString(),
            from == null ? "" : from.Value.Empowered ? "1" : "0",
            from == null ? "-1" : nearest.ToString("0.#", CultureInfo.InvariantCulture),
            from == null ? "-1" : (since - from.Value.Seconds).ToString("0.###", CultureInfo.InvariantCulture),
            candidates.ToString(CultureInfo.InvariantCulture),
            from == null ? "-1" : gained.Count(x => from.Value.Affixes.Contains(x)).ToString(CultureInfo.InvariantCulture),
            receiverDead ? "1" : "0"));
    }

    /// <summary>A monster's modifiers less its rune modifiers: the affixes Bond could move. See BondTransfers.</summary>
    private static HashSet<string> AffixesOf(string mods) =>
        new((mods ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith(RuneModifierPrefix, StringComparison.Ordinal)), StringComparer.Ordinal);

    /// <summary>Whether a monster's modifiers include Bond's, plain or empowered.</summary>
    private static bool CarriesBond(string mods) =>
        (mods ?? "").Split(' ').Any(x => x.StartsWith(BondModifier, StringComparison.Ordinal));

    /// <summary>The prefix every rune's monster modifier carries. See RuneSetOfArrival.</summary>
    private const string RuneModifierPrefix = "ExpeditionMonsterModRune";

    /// <summary>Bond's monster modifier; empowered it ends "Power".</summary>
    private const string BondModifier = "ExpeditionMonsterModRuneBond";

    /// <summary>
    /// Bond's range in grid, from its stat's 80 units read as grid - transfers were seen 25 to 41 grid out and none
    /// between 45 and 95. See NOTES.md, "Bond's transfer range".
    /// </summary>
    private const float BondRange = 80f;

    /// <summary>How long after a Bond death a gain is credited to it, in seconds: a read's interval and some slack.</summary>
    private const double BondCreditWindow = 1.5d;

    /// <summary>
    /// The read interval in force, from Data collection's Bond transfer read interval: how much earlier than a death's
    /// stamp the read that saw its gain may be. See BondTransfers.
    /// </summary>
    private TimeSpan _affixReadEvery = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Notes the death of every counted monster: when its entity first reads not alive, at its last position, or when
    /// it leaves the entity list unseen dying - corpse removed, or out of loading range - marked vanished. Written to
    /// deaths.csv; what Drops attributes a drop to.
    /// </summary>
    private void Deaths(IEnumerable<Entity> monsters)
    {
        var since = (DateTime.UtcNow - _first).TotalSeconds;
        var listed = new HashSet<uint>();

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id == 0u || !_arrivalsById.TryGetValue(id, out var arrival) || arrival.Vanished)
                continue;

            listed.Add(id);

            if (Safe.Read(monster, static e => e.IsAlive, true))
            {
                // Alive after being seen dead: the same entity came back, which Rebirth and Time may do. Counted, and its
                // first death stays the one on record.
                if (arrival.SeenDead)
                {
                    arrival.Revivals++;
                    arrival.SeenDead = false;
                }

                if (arrival.DeathSeconds < 0d)
                {
                    var at = Safe.Read(monster, static e => e.GridPos, Vector2.Zero);

                    if (at != Vector2.Zero)
                        arrival.LastAt = at;
                }

                continue;
            }

            // Each time it is seen dead, the corpse's no-drop stat, kept at its highest.
            if (!arrival.SeenDead)
            {
                arrival.SeenDead = true;
                arrival.NoDropsAtDeath = Math.Max(arrival.NoDropsAtDeath, NoDropsOfMonster(monster));

                if (arrival.Revivals == 0)
                    arrival.WatchedStatsAtDeath = WatchedStatsOfMonster(monster);
            }

            if (arrival.DeathSeconds < 0d)
                arrival.DeathSeconds = since;
        }

        foreach (var arrival in _arrivalsById.Values)
        {
            if (arrival.DeathSeconds >= 0d || listed.Contains(arrival.Id))
                continue;

            arrival.DeathSeconds = since;
            arrival.Vanished = true;
        }
    }

    /// <summary>
    /// Stats read off each monster besides the no-drop one, chosen from what F6 dumps showed on rune-carrying monsters
    /// (2026-10-01): MaximumWard on 68 of 518, 93% of them carrying Oath - the patch note's Oath monster that holds Ward
    /// and spends it summoning; IsSpectreWithDeathAction on 25, every one carrying Rebirth; MonsterDropHigherLevelGear on
    /// 79 rares and uniques, a loot property of map monsters too.
    /// </summary>
    private static readonly GameStat[] WatchedStats =
    [
        GameStat.MaximumWard,
        GameStat.IsSpectreWithDeathAction,
        GameStat.MonsterDropHigherLevelGear,
    ];

    /// <summary>A monster's WatchedStats that are not nought, as "name=value" joined by spaces; empty when none or unreadable.</summary>
    private static string WatchedStatsOfMonster(Entity monster) =>
        Safe.Read(monster, static e =>
        {
            if (e.GetComponent<Stats>()?.StatDictionary is not { } stats)
                return "";

            var said = new List<string>(WatchedStats.Length);

            foreach (var stat in WatchedStats)
            {
                if (stats.TryGetValue(stat, out var value) && value != 0)
                    said.Add($"{stat}={value.ToString(CultureInfo.InvariantCulture)}");
            }

            return string.Join(" ", said);
        }, "");

    /// <summary>
    /// Each living monster's rarity and maximum life, read at most every StrengtheningReadEvery: how often its rarity rose
    /// and the highest life it reached. The Death rune reads "Slain Monsters may merge into stronger Monsters", and no new
    /// monster was seen after a Death monster's death on any recorded map (2026-10-02), so a merge may strengthen one
    /// already standing; a rarity or life rise on a living monster is what that would look like. Written to deaths.csv.
    /// </summary>
    private void Strengthening(IEnumerable<Entity> monsters)
    {
        var now = DateTime.UtcNow;

        if (now - _strengtheningReadAt < StrengtheningReadEvery)
            return;

        _strengtheningReadAt = now;

        foreach (var monster in monsters)
        {
            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (id == 0u || !_arrivalsById.TryGetValue(id, out var arrival) || arrival.SeenDead || arrival.Vanished ||
                !Safe.Read(monster, static e => e.IsAlive, false))
                continue;

            var rarity = Rarity(monster);

            if (RankOfRarity(rarity) > RankOfRarity(arrival.RarityNow))
                arrival.RarityRises++;

            arrival.RarityNow = rarity;
            arrival.MaxLifeHighest = Math.Max(arrival.MaxLifeHighest, MaxLifeOfMonster(monster));
        }
    }

    /// <summary>Normal, magic, rare, unique as 0 to 3, rather than trusting the enum's own order.</summary>
    private static int RankOfRarity(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 1,
        MonsterRarity.Rare => 2,
        MonsterRarity.Unique => 3,
        _ => 0,
    };

    /// <summary>How often Strengthening reads every living monster. A read is two components per monster.</summary>
    private static readonly TimeSpan StrengtheningReadEvery = TimeSpan.FromMilliseconds(250);

    private DateTime _strengtheningReadAt = DateTime.MinValue;

    /// <summary>A monster's maximum life, or -1 when unreadable.</summary>
    private static int MaxLifeOfMonster(Entity monster) =>
        Safe.Read(monster, static e => e.GetComponent<Life>()?.MaxHP ?? -1, -1);

    /// <summary>
    /// A monster's MonsterNoDropsOrExperience stat: 0 for an ordinary monster, 1 or more for one that gives no items and
    /// no experience; -1 when its stats cannot be read. In F6 dumps it was set on wave monsters carrying Rebirth far more
    /// often than on others (2026-10-01), and its values of 2 suggest it counts returns. A stat, not a modifier, so the
    /// modifier list in arrivals.csv never showed it.
    /// </summary>
    private static int NoDropsOfMonster(Entity monster) =>
        Safe.Read(monster, static e =>
            e.GetComponent<Stats>()?.StatDictionary is { } stats
                ? stats.TryGetValue(GameStat.MonsterNoDropsOrExperience, out var value) ? value : 0
                : -1, -1);

    /// <summary>
    /// Records every chest seen opening while the run is recorded: where, when, and what it is. A chest already open when
    /// first seen is not recorded, since when it opened is not known.
    ///
    /// **So a remnant's drops can be told from a chest's.** A currency drop is credited to the nearest remnant that gave
    /// its reward within RemnantDropWindow and RemnantDropRadius, whatever else opened there; a chest opened beside it in
    /// that window - a bright currency chest drops divines fairly often - was counted as the remnant's. With the openings
    /// written down, the analysis can leave out drops near one. Behind the currency drops switch, since it serves them.
    /// Strongboxes open themselves when the chain sets them off; they are recorded too, flagged, and readers may drop them.
    /// </summary>
    private void ChestOpenings(GameController gc)
    {
        var chests = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Chest, out var of) ? of : null, null);

        if (chests == null)
            return;

        var since = (DateTime.UtcNow - _first).TotalSeconds;

        foreach (var chest in chests)
        {
            var id = Safe.Read(chest, static e => e.Id, 0u);

            if (id == 0u)
                continue;

            var opened = Safe.Read(chest, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false);

            if (!_chestOpened.TryGetValue(id, out var was))
            {
                _chestOpened[id] = opened;

                continue;
            }

            if (was || !opened)
                continue;

            _chestOpened[id] = true;

            var at = Safe.Read(chest, static e => e.GridPos, Vector2.Zero);

            _chestRows.Add(string.Join(",",
                _run,
                since.ToString("0.###", CultureInfo.InvariantCulture),
                ((int)at.X).ToString(CultureInfo.InvariantCulture),
                ((int)at.Y).ToString(CultureInfo.InvariantCulture),
                id.ToString(CultureInfo.InvariantCulture),
                Clean(Safe.Read(chest, static e => e.Path, "") ?? ""),
                Clean(Safe.Read(chest, static e => e.RenderName, "") ?? ""),
                Safe.Read(chest, static e => e.GetComponent<ObjectMagicProperties>()?.Rarity.ToString(), "") ?? "",
                Safe.Read(chest, static e => e.GetComponent<Chest>()?.IsStrongbox ?? false, false) ? "1" : "0",
                Safe.Read(chest, static e => e.GetComponent<Chest>()?.IsLarge ?? false, false) ? "1" : "0"));
        }
    }

    /// <summary>
    /// Records each new ground item named in RecordingSettings.DropsToRecord: where and when it appeared, its stack
    /// size, and the monster that most likely dropped it - the nearest death within DropFromDeath grid in the
    /// DropAfterDeath seconds before, with how many deaths qualified, so ambiguous drops can be left out. One landing at a
    /// remnant as it completes - its reward, or what it drops when it shatters - is credited to that remnant and to no
    /// monster. See RemnantDropped.
    ///
    /// With remnantDrops, every new ground item a remnant is credited with, whatever its name, also goes to
    /// remnant_items.csv. See RemnantItem.
    /// </summary>
    private void Drops(GameController gc, AutoExpeditionSettings settings, bool currencies, bool remnantDrops)
    {
        var wanted = currencies ? ItemNamesToRecord(settings) : new HashSet<string>();

        if (wanted.Count == 0 && !remnantDrops)
            return;

        var since = (DateTime.UtcNow - _first).TotalSeconds;
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        foreach (var item in ItemsOnGround(gc))
        {
            var id = Safe.Read(item, static e => e.Id, 0u);

            // A drop already recorded is read again: its stack, and that it is still there. See RecordedDrop. An item can be
            // in both files, so both are read.
            var readAgain = false;

            foreach (var byId in new[] { _dropsById, _remnantItemsById })
            {
                if (id == 0u || !byId.TryGetValue(id, out var known))
                    continue;

                var now = Safe.Read(item, static e => e.GetComponent<WorldItem>()?.ItemEntity?.GetComponent<Stack>()?.Size ?? 0, 0);

                known.LargestStack = Math.Max(known.LargestStack, now);
                known.LastSeen = since;
                readAgain = true;
            }

            if (readAgain)
                continue;

            if (id == 0u || _itemsSeen.Contains(id))
                continue;

            var inner = Safe.Read(item, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);
            var path = Safe.Read(inner, static e => e.Metadata, "") ?? "";

            // An item first seen before its own entity has loaded has no path, so no name. It is read again on a later
            // frame rather than marked seen: marking it lost 9 of 1,107 remnant items as blank rows (2026-10-07), and
            // would lose a recorded currency the same way.
            if (path.Length == 0)
                continue;

            _itemsSeen.Add(id);
            var name = path.Length == 0
                ? ""
                : Safe.Read(() => gc.Files.BaseItemTypes.Translate(path)?.BaseName, "") ?? "";
            var stack = Math.Max(1, Safe.Read(inner, static e => e.GetComponent<Stack>()?.Size ?? 1, 1));
            var at = Safe.Read(item, static e => e.GridPos, Vector2.Zero);

            if (remnantDrops)
                RemnantItem(gc, id, inner, path, name, stack, at, since, player);

            if (!wanted.Contains(name))
                continue;

            // **The same drop again, not a new one.** With the inventory full, picking an item up drops it straight back
            // where it was, under a new id - which counted one stack of 8 Chaos Orbs twice (2026-10-07). So an item of the
            // same name and stack, within RedropRadius of a recorded drop no longer on the ground, inside RedropWindow,
            // is that drop: it is read on from here and not recorded again.
            var redropped = _drops.Find(x => x.Name == name && x.FirstStack == stack && x.LastSeen < since &&
                                             since - x.LastSeen <= RedropWindow && Vector2.Distance(x.At, at) <= RedropRadius);

            if (redropped != null)
            {
                _dropsById[id] = redropped;
                redropped.LastSeen = since;

                continue;
            }

            var remnant = RemnantDropped(at, since, name, out var asReward);
            Arrival from = null;
            var candidates = 0;
            var nearest = DropFromDeath;

            if (remnant < 0)
            {
                foreach (var arrival in _arrivalsById.Values)
                {
                    var after = since - arrival.DeathSeconds;

                    if (arrival.DeathSeconds < 0d || after < -DropBeforeDeath || after > DropAfterDeath)
                        continue;

                    var distance = Vector2.Distance(arrival.LastAt, at);

                    if (distance > DropFromDeath)
                        continue;

                    candidates++;

                    if (distance <= nearest)
                    {
                        nearest = distance;
                        from = arrival;
                    }
                }
            }

            var recorded = new RecordedDrop
            {
                LargestStack = stack,
                PlayerDistance = player == Vector2.Zero ? -1f : Vector2.Distance(player, at),
                FirstSeen = since,
                LastSeen = since,
                Name = name,
                At = at,
                FirstStack = stack,
                RemnantAt = remnant >= 0 ? _waves[remnant].At : null,
            };

            _dropsById[id] = recorded;
            _drops.Add(recorded);

            recorded.FirstColumns = string.Join(",",
                _run,
                since.ToString("0.###", CultureInfo.InvariantCulture),
                ((int)at.X).ToString(CultureInfo.InvariantCulture),
                ((int)at.Y).ToString(CultureInfo.InvariantCulture),
                Clean(name),
                stack.ToString(CultureInfo.InvariantCulture),
                from == null ? "-1" : from.Id.ToString(CultureInfo.InvariantCulture),
                from == null ? "-1" : nearest.ToString("0.#", CultureInfo.InvariantCulture),
                from == null ? "-1" : (since - from.DeathSeconds).ToString("0.###", CultureInfo.InvariantCulture),
                candidates.ToString(CultureInfo.InvariantCulture),
                remnant.ToString(CultureInfo.InvariantCulture),
                asReward ? "1" : "0");
        }
    }

    /// <summary>
    /// Records a new ground item to remnant_items.csv if a remnant is credited with it - landing within RemnantDropRadius
    /// of a remnant in the RemnantDropWindow after its reward, the rule drops.csv uses - whatever the item is. Its base
    /// name, item class, rarity (from its Mods component, "None" for an item without one, such as currency), item level
    /// and metadata path are kept with it, and whether it is the remnant's reward. A re-drop from a full inventory is read
    /// as the drop it was, as in Drops.
    ///
    /// **Every item rather than four currencies**, because four were too few to tell one rune's effect from luck: 229
    /// Chaos Orbs and 111 Divine Orbs over 34 Grand runs (2026-10-07). A remnant drops many items, each a loot event.
    /// </summary>
    private void RemnantItem(GameController gc, uint id, Entity inner, string path, string name, int stack, Vector2 at,
        double since, Vector2 player)
    {
        var remnant = RemnantDropped(at, since, name, out var asReward);

        if (remnant < 0)
            return;

        var redropped = _remnantItems.Find(x => x.Name == name && x.FirstStack == stack && x.LastSeen < since &&
                                                since - x.LastSeen <= RedropWindow && Vector2.Distance(x.At, at) <= RedropRadius);

        if (redropped != null)
        {
            _remnantItemsById[id] = redropped;
            redropped.LastSeen = since;

            return;
        }

        var rarity = Safe.Read(inner, static e => e.GetComponent<Mods>() is { } mods ? mods.ItemRarity.ToString() : "None", "None");
        var level = Safe.Read(inner, static e => e.GetComponent<Mods>()?.ItemLevel ?? -1, -1);
        var itemClass = path.Length == 0 ? "" : Safe.Read(() => gc.Files.BaseItemTypes.Translate(path)?.ClassName, "") ?? "";

        var recorded = new RecordedDrop
        {
            LargestStack = stack,
            PlayerDistance = player == Vector2.Zero ? -1f : Vector2.Distance(player, at),
            FirstSeen = since,
            LastSeen = since,
            Name = name,
            At = at,
            FirstStack = stack,
            RemnantAt = _waves[remnant].At,
            FirstColumns = string.Join(",",
                _run,
                since.ToString("0.###", CultureInfo.InvariantCulture),
                ((int)at.X).ToString(CultureInfo.InvariantCulture),
                ((int)at.Y).ToString(CultureInfo.InvariantCulture),
                remnant.ToString(CultureInfo.InvariantCulture),
                asReward ? "1" : "0",
                Clean(name),
                Clean(itemClass),
                rarity,
                level.ToString(CultureInfo.InvariantCulture),
                stack.ToString(CultureInfo.InvariantCulture),
                Clean(path)),
        };

        _remnantItemsById[id] = recorded;
        _remnantItems.Add(recorded);
    }

    /// <summary>
    /// The index of the remnant that dropped this, or -1: the nearest within RemnantDropRadius whose reward state came at
    /// most RemnantDropWindow seconds before. A remnant drops items when it shatters as well as its reward, so the name
    /// is not required to match; asReward says whether it names the remnant's selected reward. Seen on Frigid Bluffs
    /// (2026-10-01): a Divine Orb 3.8 s and 16 grid from one completing remnant and two Chaos Orbs 1.1 s and 23 grid
    /// from another, neither naming the reward. See Drops.
    /// </summary>
    private int RemnantDropped(Vector2 at, double since, string name, out bool asReward)
    {
        var found = -1;
        var nearest = RemnantDropRadius;

        for (var i = 0; i < _waves.Count; i++)
        {
            var remnant = _waves[i];
            var after = since - remnant.RewardSeconds;
            var distance = Vector2.Distance(at, remnant.At);

            if (remnant.RewardSeconds < 0d || after < -1d || after > RemnantDropWindow || distance > nearest)
                continue;

            nearest = distance;
            found = i;
        }

        // The whole reward name, less a leading "3x ", must equal the item's base name. A substring test said a Chaos Orb
        // was the reward "3x Perfect Chaos Orb".
        asReward = found >= 0 && string.Equals(
            Regex.Replace(_waves[found].Selected ?? "", @"^\d+x\s+", ""), name, StringComparison.OrdinalIgnoreCase);

        return found;
    }

    /// <summary>RecordingSettings.DropsToRecord as a set of base names.</summary>
    private static HashSet<string> ItemNamesToRecord(AutoExpeditionSettings settings)
    {
        var text = Safe.Read(() => settings.Recording.DropsToRecord.Value, "") ?? "";

        return new HashSet<string>(
            text.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The item entities lying on the ground, or none.</summary>
    private static IEnumerable<Entity> ItemsOnGround(GameController gc) =>
        Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.WorldItem, out var of) ? of : null, null)
        ?? (IEnumerable<Entity>)Array.Empty<Entity>();

    /// <summary>
    /// How far from a death a drop may appear and still be credited to it, in grid. A first guess: a drop lands at or
    /// beside the corpse. drops.csv keeps the distance and the count of deaths in range, so attribution can be redone.
    /// </summary>
    private const float DropFromDeath = 8f;

    /// <summary>How many seconds after a death its drop may appear. A first guess; see DropFromDeath.</summary>
    private const double DropAfterDeath = 3d;

    /// <summary>How many seconds before a death is noticed its drop may already show, since deaths are read per pass.</summary>
    private const double DropBeforeDeath = 0.5d;

    /// <summary>
    /// How far from a remnant what it drops on completing may land, in grid. Two seen at 16 and 23; a first guess past
    /// them. See RemnantDropped.
    /// </summary>
    private const float RemnantDropRadius = 30f;

    /// <summary>How many seconds after a remnant's reward state what it drops may land. A first guess; see RemnantDropped.</summary>
    private const double RemnantDropWindow = 15d;

    /// <summary>How near a recorded drop an item of the same name and stack must land to be that drop re-dropped. Chosen, not measured. See Drops.</summary>
    private const float RedropRadius = 3f;

    /// <summary>How long after a recorded drop left the ground it may come back as itself. Chosen, not measured. See Drops.</summary>
    private const double RedropWindow = 30d;

    /// <summary>How far from a remnant its RuneEncounterController may stand, in grid. Seen on the same cell.</summary>
    private const float ControllerRange = 3f;

    /// <summary>
    /// Notes changes in the modifiers, buffs and stats of each remnant's RuneEncounterController. The
    /// controller is a monster-typed entity on the remnant's cell, and Ignore keeps it out of the counts.
    /// </summary>
    private void ControllerChanges(IEnumerable<Entity> monsters)
    {
        if (_waves.Count == 0)
            return;

        var since = (DateTime.UtcNow - _first).TotalSeconds;

        foreach (var monster in monsters)
        {
            var metadata = Safe.Read(monster, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("RuneEncounterController", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var at = Safe.Read(monster, static e => e.GridPos, Vector2.Zero);

            foreach (var remnant in _waves)
            {
                if (Vector2.Distance(remnant.At, at) > ControllerRange)
                    continue;

                NoteChanges(ModAndBuffValues(monster), remnant.ControllerModValues,
                    remnant.ControllerModChanges, since);
                NoteChanges(StatValues(monster), remnant.ControllerStatValues,
                    remnant.ControllerStatChanges, since);

                break;
            }
        }
    }

    /// <summary>An entity's StateMachine values by name, leaving out one name, or none when unreadable.</summary>
    private static List<(string Name, long Value)> StateValues(Entity entity, string leaveOut)
    {
        var found = new List<(string, long)>();
        var states = entity == null ? null : Safe.Read(entity, static e => e.GetComponent<StateMachine>()?.States, null);

        if (states == null)
            return found;

        for (var i = 0; i < states.Count; i++)
        {
            var name = Safe.Read(states[i], static x => x.Name, null);

            if (string.IsNullOrEmpty(name) || name == leaveOut)
                continue;

            found.Add((name, Safe.Read(states[i], static x => x.Value, -1L)));
        }

        return found;
    }

    /// <summary>An entity's modifiers as mod:id=1 and its buffs as buff:name=stacks, or none when unreadable.</summary>
    private static List<(string Name, long Value)> ModAndBuffValues(Entity entity)
    {
        var found = new List<(string, long)>();
        var mods = Safe.Read(entity, static e => e.GetComponent<ObjectMagicProperties>()?.Mods, null);

        foreach (var mod in mods ?? [])
        {
            if (!string.IsNullOrEmpty(mod))
                found.Add(("mod:" + mod, 1L));
        }

        var buffs = Safe.Read(entity, static e => e.GetComponent<Buffs>()?.BuffsList, null);

        foreach (var buff in buffs ?? [])
        {
            var name = Safe.Read(buff, static x => x.Name, null);

            if (!string.IsNullOrEmpty(name))
                found.Add(("buff:" + name, Safe.Read(buff, static x => (long)x.BuffStacks, 0L)));
        }

        return found;
    }

    /// <summary>An entity's stats by name, or none when unreadable.</summary>
    private static List<(string Name, long Value)> StatValues(Entity entity)
    {
        var found = new List<(string, long)>();
        var stats = Safe.Read(entity, static e => e.GetComponent<Stats>()?.StatDictionary, null);

        if (stats == null)
            return found;

        foreach (var (stat, value) in stats)
            found.Add((stat.ToString(), value));

        return found;
    }

    /// <summary>
    /// Appends name=value@seconds for every value that is new or differs from the last one seen, and
    /// name=gone@seconds for one no longer there, and remembers them. Entries are separated by |. An empty
    /// reading is taken as unreadable rather than as everything gone.
    /// </summary>
    private static void NoteChanges(List<(string Name, long Value)> now, Dictionary<string, long> had,
        System.Text.StringBuilder into, double since)
    {
        if (now.Count == 0)
            return;

        var at = since.ToString("0.#", CultureInfo.InvariantCulture);

        foreach (var (name, value) in now)
        {
            if (had.TryGetValue(name, out var was) && was == value)
                continue;

            had[name] = value;

            if (into.Length > 0)
                into.Append('|');

            into.Append(name).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append('@').Append(at);
        }

        if (had.Count == now.Count)
            return;

        foreach (var name in new List<string>(had.Keys))
        {
            if (now.Exists(x => x.Name == name))
                continue;

            had.Remove(name);

            if (into.Length > 0)
                into.Append('|');

            into.Append(name).Append("=gone@").Append(at);
        }
    }

    /// <summary>
    /// Reads again the modifiers of any arrival that had none on first sight, for
    /// ModReadWindow after it appeared. ObjectMagicProperties is not always readable the instant an entity
    /// streams in - see Target.Valued - so a blank at arrival is not yet an answer.
    /// </summary>
    private void ModsNotYetRead()
    {
        var now = DateTime.UtcNow;

        foreach (var arrival in _arrivals)
        {
            if (arrival.Monster == null || arrival.Mods.Length > 0)
                continue;

            if (now - arrival.Seen > ModReadWindow)
            {
                arrival.Monster = null;

                continue;
            }

            arrival.Mods = ModNames(arrival.Monster);

            if (arrival.Mods.Length > 0)
                arrival.Monster = null;
        }
    }

    /// <summary>How long after its arrival a monster's modifiers are retried. See ModsNotYetRead.</summary>
    private static readonly TimeSpan ModReadWindow = TimeSpan.FromSeconds(5);

    /// <summary>A monster's modifier ids, space separated, or empty when none were readable.</summary>
    private static string ModNames(Entity monster)
    {
        if (monster == null)
            return "";

        var mods = Safe.Read(() => monster.GetComponent<ObjectMagicProperties>()?.Mods, null);

        return mods == null ? "" : string.Join(" ", mods);
    }

    /// <summary>
    /// The gap between two encounter arrivals that starts a new wave, in seconds. Measured on one Grand site
    /// (2026-09-30): a wave arrives within one sweep, its monsters timestamped within a few hundredths of a
    /// second of each other, while waves came 2 to 10 seconds apart. Half a second separates those; 2 seconds
    /// merged two waves on two remnants.
    /// </summary>
    private const double WaveGap = 0.5d;

    /// <summary>
    /// Which wave of which remnant each arrival came from, read from the rune modifiers it carries, and per remnant
    /// the waves seen as wave:monsters/rares joined by spaces, with the highest wave seen.
    ///
    /// **Waves are told apart by runes, because arrival times cannot do it.** A monster from a remnant's wave k
    /// carries ExpeditionMonsterModRune&lt;Rune&gt; for every rune in force: the runes propagated to the remnant and its
    /// own first k slots - slots 1 and 2 on waves 1 and 2, one slot more on each wave after. So an arrival whose rune
    /// set is exactly that for some remnant and k is that remnant's wave k, whichever remnant it stands nearest. Waves 1
    /// and 2 carry the same set and are written together as 1-2. A rune upgraded by Power carries "Power" after its
    /// name, and an added one "Addon"; both are read as the rune. Matched against the arrivals of five sites
    /// (2026-09-30): 720 arrivals to one remnant and wave, 11 to more than one, settled by distance, and the waves seen
    /// on each remnant watched to the end ran 1-2, 3, 4 up to its rune count. Grouping by arrival time matched the
    /// socket count on 18 of 39 remnants. An arrival matching no remnant is left out.
    ///
    /// **Only inside the remnant's own encounter.** A remnant's propagating runes reach everything unearthed after it,
    /// so a marker's monster set off by a later explosive can carry exactly a remnant's full set and read as its last
    /// wave. Seen on one Grand site (2026-09-30): a remnant whose five waves brought 5 rares and 19 magic read 29 and 61
    /// by the end, its "wave 5" holding 211 monsters. The chain does not detonate the next explosive until the last
    /// one's monsters are dead, so a remnant's encounter runs from its link's start to the next link's: a link starts
    /// at the earliest arrival whose set fits one of its remnants and no other remnant, since nothing can carry a
    /// remnant's runes before it goes off. An arrival fitting a remnant outside that window is left out and counted in
    /// OutsideEncounter. A remnant whose link never started is matched without a window.
    /// </summary>
    private Dictionary<int, SortedDictionary<int, (int Monsters, int Magic, int Rares)>> RuneWaves()
    {
        var byRemnant = new Dictionary<int, SortedDictionary<int, (int Monsters, int Magic, int Rares)>>();
        var expected = new List<(int Remnant, int Wave, HashSet<string> Runes)>();

        for (var i = 0; i < _waves.Count; i++)
        {
            var slots = RuneNames(_waves[i].Runes);

            // The runes its own monsters show were passed in, where they show any; the plan's prediction otherwise. See
            // ObservedOfRemnant.
            var observed = ObservedOfRemnant(i);
            var arriving = RuneNames(observed.FirstWaveSeconds >= 0d ? observed.Arriving : _waves[i].Arriving);

            for (var k = 2; k <= slots.Count; k++)
            {
                var set = new HashSet<string>(arriving, StringComparer.Ordinal);

                set.UnionWith(slots.GetRange(0, k));
                expected.Add((i, k, set));
            }
        }

        // Each link's encounter window. See the summary.
        var linkOf = new int[_waves.Count];

        for (var i = 0; i < _waves.Count; i++)
            linkOf[i] = BlastThatCaught(_waves[i].At, _radius + RemnantReachSlack) + 1;

        var linkStart = new Dictionary<int, DateTime>();

        foreach (var arrival in _arrivals)
        {
            var carried = RuneSetOfArrival(arrival);

            if (carried.Count == 0)
                continue;

            var only = -1;

            foreach (var (remnant, _, runes) in expected)
            {
                if (!runes.SetEquals(carried) || remnant == only)
                    continue;

                only = only < 0 ? remnant : -2;

                if (only == -2)
                    break;
            }

            if (only < 0)
                continue;

            var link = linkOf[only];

            if (!linkStart.TryGetValue(link, out var had) || arrival.Seen < had)
                linkStart[link] = arrival.Seen;
        }

        var started = linkStart.Keys.OrderBy(x => x).ToList();

        bool InsideEncounter(int remnant, DateTime seen)
        {
            if (!linkStart.TryGetValue(linkOf[remnant], out var from))
                return true;

            var next = started.FirstOrDefault(x => x > linkOf[remnant]);

            return seen >= from && (next == 0 || seen < linkStart[next]);
        }

        OutsideEncounter = 0;

        foreach (var arrival in _arrivals)
        {
            var carried = RuneSetOfArrival(arrival);

            if (carried.Count == 0)
                continue;

            var best = (Remnant: -1, Wave: -1, Distance: float.MaxValue);
            var fitted = false;

            foreach (var (remnant, wave, runes) in expected)
            {
                if (!runes.SetEquals(carried))
                    continue;

                fitted = true;

                if (!InsideEncounter(remnant, arrival.Seen))
                    continue;

                var distance = Vector2.Distance(arrival.At, _waves[remnant].At);

                if (distance < best.Distance)
                    best = (remnant, wave, distance);
            }

            if (best.Remnant < 0)
            {
                if (fitted)
                    OutsideEncounter++;

                continue;
            }

            if (!byRemnant.TryGetValue(best.Remnant, out var waves))
                byRemnant[best.Remnant] = waves = new SortedDictionary<int, (int Monsters, int Magic, int Rares)>();

            var had = waves.GetValueOrDefault(best.Wave);

            waves[best.Wave] = (had.Monsters + 1,
                had.Magic + (arrival.Rarity == MonsterRarity.Magic ? 1 : 0),
                had.Rares + (arrival.Rarity is MonsterRarity.Rare or MonsterRarity.Unique ? 1 : 0));
        }

        return byRemnant;
    }

    /// <summary>How many arrivals the last RuneWaves left out for fitting a remnant outside its encounter.</summary>
    private int OutsideEncounter;

    /// <summary>The runes an arrival carries, read from its rune modifiers, Power and Addon read as the rune.</summary>
    private static HashSet<string> RuneSetOfArrival(Arrival arrival)
    {
        var carried = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in arrival.Mods.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!mod.StartsWith(RuneModPrefix, StringComparison.Ordinal))
                continue;

            var rune = mod.Substring(RuneModPrefix.Length);

            if (rune.EndsWith("Addon", StringComparison.Ordinal))
                rune = rune.Substring(0, rune.Length - "Addon".Length);

            if (rune.Length > "Power".Length && rune.EndsWith("Power", StringComparison.Ordinal))
                rune = rune.Substring(0, rune.Length - "Power".Length);

            carried.Add(rune);
        }

        return carried;
    }

    /// <summary>The start of a remnant wave monster's rune modifier. See RuneWaves.</summary>
    private const string RuneModPrefix = "ExpeditionMonsterModRune";

    /// <summary>A list of rune names as the remnant rows write them, in order, the propagating mark taken off.</summary>
    private static List<string> RuneNames(string runes) =>
        (runes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('*')).ToList();

    /// <summary>
    /// What a remnant's own monsters say happened to it, rather than what the plan expected: the runes passed in to it,
    /// and when its waves began.
    ///
    /// Every wave's monsters carry the runes passed in plus the remnant's sockets up to that wave, in socket order. So
    /// each set carried by an arrival nearest to it, holding its first two sockets' runes, less those two, is a guess at
    /// what was passed in; a guess can hold the remnant's own later runes, which are held and passed in at once. A guess
    /// is scored by how many of the remnant's waves it explains, then by how many monsters, and the earliest monster it
    /// explains gives the time. Only runes some other remnant of the site propagates can be passed in.
    ///
    /// The plan's Arriving is its prediction, made with its own recipe picks and chain order: on 2026-10-01 a remnant's
    /// monsters all carried a rune from a remnant the plan put after it. Taking the earliest monster's set, as this did
    /// before, picked up a neighbour's monsters: on BB354D31 (869,1012) three runes that never arrived, and (913,443) the
    /// waves of (930,396), set off with it 50 grid away. Waves rank ahead of monsters because a marker set off later
    /// carries every rune propagated so far, so its monsters can look exactly like one wave of a remnant and outnumber
    /// it, but they never fill out the remnant's later waves.
    ///
    /// Nearest remnant with no distance limit, not the wave radius: wave monsters were seen 33 to 87 grid from their
    /// remnant.
    /// </summary>
    private (string Arriving, double FirstWaveSeconds) ObservedOfRemnant(int index)
    {
        var slots = RuneNames(_waves[index].Runes);

        if (slots.Count < 2)
            return ("", -1d);

        var first = slots.Take(2).ToHashSet(StringComparer.Ordinal);

        // Each socket prefix of two or more: the remnant's own part of each wave.
        var prefixes = new List<HashSet<string>>();

        for (var k = 2; k <= slots.Count; k++)
            prefixes.Add(slots.Take(k).ToHashSet(StringComparer.Ordinal));

        // What any other remnant of the site propagates - its slots marked with a star.
        var passable = new HashSet<string>(StringComparer.Ordinal);

        for (var j = 0; j < _waves.Count; j++)
        {
            if (j == index)
                continue;

            foreach (var token in (_waves[j].Runes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.EndsWith('*'))
                    passable.Add(token.TrimEnd('*'));
            }
        }

        // The remnant's monsters, by the exact rune set they carry, with the earliest sighting of each set.
        var sets = new Dictionary<string, (HashSet<string> Runes, int Count, DateTime Earliest)>(StringComparer.Ordinal);

        foreach (var arrival in _arrivals)
        {
            var carried = RuneSetOfArrival(arrival);

            if (!first.IsSubsetOf(carried) || Which(_waves, arrival.At, float.MaxValue) != index)
                continue;

            var said = string.Join(" ", carried.OrderBy(r => r, StringComparer.Ordinal));

            sets[said] = sets.TryGetValue(said, out var had)
                ? (had.Runes, had.Count + 1, arrival.Seen < had.Earliest ? arrival.Seen : had.Earliest)
                : (carried, 1, arrival.Seen);
        }

        HashSet<string> best = null;
        (int Waves, int Monsters) bestScore = (-1, -1);
        var bestTime = DateTime.MaxValue;

        foreach (var candidate in sets.Values)
        {
            var guess = candidate.Runes.Where(r => !first.Contains(r)).ToHashSet(StringComparer.Ordinal);

            if (!guess.IsSubsetOf(passable))
                continue;

            var waves = 0;
            var monsters = 0;
            var start = DateTime.MaxValue;

            for (var k = 0; k < prefixes.Count; k++)
            {
                var wave = new HashSet<string>(guess, StringComparer.Ordinal);

                wave.UnionWith(prefixes[k]);

                // Two prefixes give one set where a later socket repeats a rune already in force; counted once.
                if (k > 0 && prefixes.Take(k).Any(p => p.Union(guess).ToHashSet(StringComparer.Ordinal).SetEquals(wave)))
                    continue;

                var said = string.Join(" ", wave.OrderBy(r => r, StringComparer.Ordinal));

                if (!sets.TryGetValue(said, out var seen))
                    continue;

                waves++;
                monsters += seen.Count;

                if (seen.Earliest < start)
                    start = seen.Earliest;
            }

            var score = (waves, monsters);

            if (score.CompareTo(bestScore) > 0 || score.Equals(bestScore) && start < bestTime)
            {
                best = guess;
                bestScore = score;
                bestTime = start;
            }
        }

        if (best == null || bestTime == DateTime.MaxValue)
            return ("", -1d);

        return (string.Join(" ", best.OrderBy(r => r, StringComparer.Ordinal)), (bestTime - _first).TotalSeconds);
    }

    /// <summary>A remnant's waves seen, as RuneWaves writes them: "1-2:14/1 3:9/0", waves 1 and 2 together.</summary>
    private static string RuneWavesSaid(SortedDictionary<int, (int Monsters, int Magic, int Rares)> waves) =>
        waves == null
            ? ""
            : string.Join(" ", waves.Select(x =>
                $"{(x.Key == 2 ? "1-2" : x.Key.ToString(CultureInfo.InvariantCulture))}:{x.Value.Monsters}/{x.Value.Rares}"));

    /// <summary>
    /// How many waves a remnant's encounter arrived in, and each one as size@seconds after the encounter
    /// started, joined by |. A wave is a run of arrivals with no gap over WaveGap. A single monster
    /// arriving between waves is counted as a wave of one, and the sizes are written so a reader can discount those.
    /// </summary>
    private static (int Count, string Sizes) EncounterWavesOfRemnant(Waves remnant)
    {
        if (remnant.EncounterTimes.Count == 0)
            return (0, "");

        var times = new List<double>(remnant.EncounterTimes);
        times.Sort();

        var start = remnant.EncounterStart >= 0d ? remnant.EncounterStart : times[0];
        var sizes = new List<string>();
        var from = times[0];
        var size = 1;

        for (var i = 1; i <= times.Count; i++)
        {
            if (i < times.Count && times[i] - times[i - 1] <= WaveGap)
            {
                size++;

                continue;
            }

            sizes.Add(size.ToString(CultureInfo.InvariantCulture) + "@" +
                      (from - start).ToString("0.#", CultureInfo.InvariantCulture));

            if (i < times.Count)
            {
                from = times[i];
                size = 1;
            }
        }

        return (sizes.Count, string.Join("|", sizes));
    }

    /// <summary>
    /// Notes each remnant's "activated" state whenever it changes, against the seconds since the detonation, so
    /// a remnant row says when its encounter moved on and the arrivals near it can be lined up against that.
    /// Read off the live targets every sweep once the run has frozen. See Waves.States.
    /// </summary>
    private void RemnantStates(Scan scan)
    {
        var since = (DateTime.UtcNow - _first).TotalSeconds;

        foreach (var target in _site == Vector2.Zero ? scan.Targets : scan.At(_site))
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            foreach (var remnant in _waves)
            {
                if (Vector2.Distance(remnant.At, target.Grid) > 1f)
                    continue;

                NoteChanges(StateValues(target.Entity, "activated"), remnant.RemnantStateValues,
                    remnant.RemnantStateChanges, since);

                var state = Safe.Read(() => target.State("activated"), long.MinValue);

                if (state == long.MinValue || state == remnant.LastState)
                    break;

                if (remnant.States.Length > 0)
                    remnant.States.Append('|');

                remnant.States.Append(state.ToString(CultureInfo.InvariantCulture)).Append('@')
                    .Append(since.ToString("0.#", CultureInfo.InvariantCulture));
                remnant.LastState = state;

                if (state == EncounterRunning && remnant.EncounterStart < 0d)
                    remnant.EncounterStart = since;

                if (state == ChainDetonated)
                    remnant.SetOff = true;

                if (state == RewardGiven)
                {
                    remnant.Rewarded = true;

                    if (remnant.RewardSeconds < 0d)
                        remnant.RewardSeconds = since;
                }

                break;
            }
        }
    }

    /// <summary>
    /// Whether every remnant the chain set off has reached its reward, which is the end of every encounter the
    /// site can have. A remnant counts as set off once its "activated" state has read ChainDetonated; one never
    /// started stays there, and the run then ends another way. False while no remnant has been set off.
    ///
    /// This replaced ending on the "Expedition Complete" banner, which has been seen in game with monsters still
    /// to come and would have cut off the encounters after it. See _banner.
    /// </summary>
    private bool RemnantsRewarded()
    {
        var any = false;

        foreach (var remnant in _waves)
        {
            if (!remnant.SetOff)
                continue;

            if (!remnant.Rewarded)
                return false;

            any = true;
        }

        return any;
    }

    /// <summary>When the last remnant set off gave its reward, in seconds into the run, or minus one. See Observe.</summary>
    private double LastRewardSeconds()
    {
        var last = -1d;

        foreach (var remnant in _waves)
        {
            if (remnant.SetOff && remnant.RewardSeconds > last)
                last = remnant.RewardSeconds;
        }

        return last;
    }

    /// <summary>The remnant "activated" state right after the chain goes off. See EncounterRunning.</summary>
    private const long ChainDetonated = 3;

    /// <summary>The remnant "activated" state once its reward is given. See EncounterRunning.</summary>
    private const long RewardGiven = 7;

    /// <summary>
    /// The remnant "activated" state while its encounter runs. Read off one Grand site on Grazed Prairie
    /// (2026-09-30): every remnant of the chain went 3 after the detonation, 5 when its encounter was started,
    /// 6 when the fight ended, 7 at the reward; a remnant outside the chain sat at 8.
    /// </summary>
    private const long EncounterRunning = 5;

    /// <summary>Whether a spot lies within the wave radius of any remnant of the run. See the site test in Observe.</summary>
    private bool NearRemnant(Vector2 at, float wave)
    {
        foreach (var remnant in _waves)
        {
            if (Vector2.Distance(at, remnant.At) <= wave)
                return true;
        }

        return false;
    }

    /// <summary>Every monster's arrival, unsummarised, written beside the counts.</summary>
    private readonly List<Arrival> _arrivals = new();

    /// <summary>
    /// One row of arrivals.csv: every column but the last, and the monster's modifiers, which may be read a
    /// sweep or two after it arrived. Monster is held only until those are read. See ModsNotYetRead.
    /// </summary>
    private sealed class Arrival
    {
        public string Row = "";
        public string Mods = "";
        public Entity Monster;
        public DateTime Seen;
        public Vector2 At;
        public MonsterRarity Rarity;

        /// <summary>The entity's id, joining this row to deaths.csv and drops.csv.</summary>
        public uint Id;

        /// <summary>Where it was last seen, which is where it died once it has. See Spawns.Deaths.</summary>
        public Vector2 LastAt;

        /// <summary>Seconds after the detonation at which it died or vanished, or -1 while it lives. See Spawns.Deaths.</summary>
        public double DeathSeconds = -1d;

        /// <summary>Whether it left the entity list without being seen dying. See Spawns.Deaths.</summary>
        public bool Vanished;

        /// <summary>Whether its death has gone to bond_deaths.csv. See Spawns.BondTransfers.</summary>
        public bool BondDeathWritten;

        /// <summary>Whether its affixes have had their one read after death. See Spawns.BondTransfers.</summary>
        public bool AffixesReadAfterDeath;

        /// <summary>Its MonsterNoDropsOrExperience stat when first seen, or -1 when unreadable. See NoDropsOfMonster.</summary>
        public int NoDropsAtArrival = -1;

        /// <summary>The highest MonsterNoDropsOrExperience read on its corpse, or -1 when never read dead. See Spawns.Deaths.</summary>
        public int NoDropsAtDeath = -1;

        /// <summary>Whether it was dead at the last read, so a living read after it is a revival. See Spawns.Deaths.</summary>
        public bool SeenDead;

        /// <summary>How many times it was seen alive again after being seen dead. See Spawns.Deaths.</summary>
        public int Revivals;

        /// <summary>WatchedStats when first seen, as name=value for those not nought. See WatchedStatsOfMonster.</summary>
        public string WatchedStatsAtArrival = "";

        /// <summary>WatchedStats on its corpse at its first death, the same way. See Spawns.Deaths.</summary>
        public string WatchedStatsAtDeath = "";

        /// <summary>Its maximum life when first seen, or -1 when unreadable. See Spawns.Strengthening.</summary>
        public int MaxLifeAtArrival = -1;

        /// <summary>The highest maximum life read on it while alive, or -1 when never read. See Spawns.Strengthening.</summary>
        public int MaxLifeHighest = -1;

        /// <summary>Its rarity at the last read while alive. See Spawns.Strengthening.</summary>
        public MonsterRarity RarityNow;

        /// <summary>How many times its rarity was read higher than at the read before, while alive. See Spawns.Strengthening.</summary>
        public int RarityRises;

        public override string ToString() => Row + "," + Clean(Mods) + "," + Id.ToString(CultureInfo.InvariantCulture) + "," +
                                             NoDropsAtArrival.ToString(CultureInfo.InvariantCulture) + "," +
                                             Clean(WatchedStatsAtArrival);

        /// <summary>Its row of deaths.csv. See DeathsHeader.</summary>
        public string DeathRow(string run) => string.Join(",",
            run,
            Id.ToString(CultureInfo.InvariantCulture),
            DeathSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            ((int)LastAt.X).ToString(CultureInfo.InvariantCulture),
            ((int)LastAt.Y).ToString(CultureInfo.InvariantCulture),
            Rarity.ToString(),
            Vanished ? "1" : "0",
            NoDropsAtDeath.ToString(CultureInfo.InvariantCulture),
            Revivals.ToString(CultureInfo.InvariantCulture),
            Clean(WatchedStatsAtDeath),
            RarityRises.ToString(CultureInfo.InvariantCulture),
            RarityNow.ToString(),
            MaxLifeAtArrival.ToString(CultureInfo.InvariantCulture),
            MaxLifeHighest.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>arrivals.csv's columns. A file whose first line differs is moved aside before writing.</summary>
    private const string ArrivalsHeader = "run,seconds,rarity,x,y,blast,remnant,fromPlayer,name,mods,id,noDrops,watchedStats";

    /// <summary>
    /// bond_deaths.csv's columns: a monster carrying Bond dying, its non-rune affixes, and how many monsters of each
    /// rarity stood within BondRange. See BondTransfers.
    /// </summary>
    private const string BondDeathsHeader = "run,id,seconds,x,y,rarity,empowered,affixes,raresNear,magicNear,normalNear";

    /// <summary>
    /// bond_gains.csv's columns: a monster gaining affixes, the Bond death it is credited to (or -1), how many deaths
    /// qualified, how many of the gained affixes that death's monster had, and whether the gain was seen on the
    /// receiver's corpse at its one read after death. See BondTransfers.
    /// </summary>
    private const string BondGainsHeader =
        "run,seconds,receiver,rarity,x,y,gained,gainedCount,dier,dierRarity,empowered,distance,afterDeath,candidates,fromDier," +
        "receiverDead";

    /// <summary>deaths.csv's columns: every counted monster that died or vanished. See Deaths.</summary>
    private const string DeathsHeader = "run,id,seconds,x,y,rarity,vanished,noDrops,revivals,watchedStats,rarityRises,rarityLast,maxLifeAtArrival,maxLifeHighest";

    /// <summary>
    /// drops.csv's columns. monster is the dropping monster's id or -1; remnant the index of the remnant that dropped it as
    /// it completed, or -1, and asReward whether the item is that remnant's selected reward. stack is the size on the first
    /// frame the item was seen and stackLargest the largest read while it lay there; playerDistance how far the player
    /// was, in grid, when it was first seen, and seenFor how long it stayed in sight. See Drops and RecordedDrop.
    ///
    /// remnantX and remnantY are where the credited remnant stands, or -1: the remnant index alone was not stable across
    /// a run (2026-10-07: one remnant credited with divines 144 s and over 1,000 grid apart), so a reader can check the
    /// drop landed near it.
    /// </summary>
    /// <summary>
    /// chests.csv's columns: every chest seen opening during a run. path is the chest's metadata, name its render name,
    /// rarity its own, and strongbox and large the Chest component's flags. See ChestOpenings.
    /// </summary>
    private const string ChestsHeader = "run,seconds,x,y,id,path,name,rarity,strongbox,large";

    /// <summary>
    /// remnant_items.csv's columns: every item a remnant is credited with as it completes, whatever it is. remnant is its
    /// index and asReward whether the item is its selected reward, as in drops.csv; item the base name, class the item
    /// class, rarity Normal / Magic / Rare / Unique or None for an item with no Mods component, itemLevel -1 where
    /// unread, path the item's metadata. stack, stackLargest, playerDistance, seenFor, remnantX and remnantY as in
    /// drops.csv. See RemnantItem.
    /// </summary>
    private const string RemnantItemsHeader =
        "run,seconds,x,y,remnant,asReward,item,class,rarity,itemLevel,stack,path,stackLargest,playerDistance,seenFor,remnantX,remnantY";

    private const string DropsHeader =
        "run,seconds,x,y,item,stack,monster,distance,afterDeath,candidates,remnant,asReward,stackLargest,playerDistance,seenFor," +
        "remnantX,remnantY";

    /// <summary>
    /// The index of the blast that set off whatever is at a point, or -1: the earliest in the chain within range, since
    /// the chain goes off in placement order and the first blast to reach a marker spends it. For a remnant the range
    /// is the blast radius plus RemnantReachSlack, because a remnant has extent of its own. Nearest-first was the
    /// earlier rule, and it filed a remnant caught by blasts 1 and 12 at link 12. An inference from geometry: where a
    /// remnant's monsters say otherwise, the census's observed order (firstWaveSeconds) is the one to trust.
    /// </summary>
    private int BlastThatCaught(Vector2 at, float range)
    {
        for (var i = 0; i < _blasts.Count; i++)
        {
            if (Vector2.Distance(at, _blasts[i].At) <= range)
                return i;
        }

        return -1;
    }

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

            if (Planner.RuneTallyByRemnant.TryGetValue(
                    ((int)MathF.Round(marker.Grid.X), (int)MathF.Round(marker.Grid.Y)), out var tally))
            {
                remnant.Inherited = tally.Inherited;
                remnant.Wasted = tally.Wasted;
                remnant.Arriving = string.Join(" ", tally.Arriving ?? []);
            }

            if (Planner.WavesPredictedByRemnant.TryGetValue(
                    ((int)MathF.Round(marker.Grid.X), (int)MathF.Round(marker.Grid.Y)), out var predicted))
            {
                remnant.Predicted = predicted;
                remnant.PredictedTable = Wrt.TableFingerprint;
            }

            remnant.Runes = valuation?.ChosenRuneNames(marker.Entity) ?? "";

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
        // A resumed run may see no explosives down - they went off before the return - and is written without them.
        //
        // **Only a run that started.** Between runs the site is still snapshotted every pass and the on-screen tally is
        // kept, so leaving the area after a run had been written wrote it again: the same run key, its markers and
        // blasts, and every count nought. Seven runs from 2026-09-30 on carry such a second copy.
        if (_written || !_frozen || (_blasts.Count == 0 && !_resumed) || _counted.Count == 0 || _markers.Count == 0)
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

        site.MapStats = _mapStats;
        site.AtlasStats = _atlasStats;

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
            chain.RelicMods.UnionWith(blast.RelicMods);
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

        // Which wave of which remnant each arrival was, from its runes. See RuneWaves.
        var runeWaves = RuneWaves();

        // One row per remnant, on the remnant's own terms: everything that turned up within the
        // wave radius of it, however long after. Sockets and used are the columns it exists for.
        for (var i = 0; i < _waves.Count; i++)
        {
            var remnant = _waves[i];

            // After a return, only the remnants fought since: one finished before it would be written with no waves.
            if (_resumed && !remnant.SetOff)
                continue;

            var link = BlastThatCaught(remnant.At, _radius + RemnantReachSlack) + 1;
            var row = new Blast
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
                States = remnant.States.ToString(),
                Inherited = remnant.Inherited,
                Wasted = remnant.Wasted,
                ChainLink = BlastThatCaught(remnant.At, _radius + RemnantReachSlack) + 1,
                Runes = remnant.Runes,
                Arriving = remnant.Arriving,
                ArrivingSeen = ObservedOfRemnant(i).Arriving,
                FirstWaveSeconds = ObservedOfRemnant(i).FirstWaveSeconds,
                Predicted = remnant.Predicted,
                PredictedTable = remnant.PredictedTable,
                Encounter = remnant.Encounter,
                EncounterRares = remnant.EncounterRares,
                EncounterWaveCount = EncounterWavesOfRemnant(remnant).Count,
                EncounterWaveSizes = EncounterWavesOfRemnant(remnant).Sizes,
                RuneWaves = RuneWavesSaid(runeWaves.GetValueOrDefault(i)),
                HighestRuneWave = runeWaves.GetValueOrDefault(i) is { Count: > 0 } seen ? seen.Keys.Max() : -1,
                RemnantStateChanges = remnant.RemnantStateChanges.ToString(),
                ControllerModChanges = remnant.ControllerModChanges.ToString(),
                ControllerStatChanges = remnant.ControllerStatChanges.ToString(),
            };

            // Relics caught at or before this remnant's link, since a relic acts on what is unearthed after it.
            for (var j = 0; j < link && j < _blasts.Count; j++)
                row.RelicMods.UnionWith(_blasts[j].RelicMods);

            rows.Add(Row(when, "remnant", i, row));
        }

        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "spawns.csv");

                var header = Header;

                // A file whose columns no longer match is moved aside rather than appended to.
                //
                // **Rows of two different shapes in one CSV are worse than two files.** Everything
                // read out of here is read by column, so a single header with rows of two widths
                // silently shifts every later field - and the fields are counts, which look
                // perfectly plausible when they are wrong. The old rows are still evidence about the
                // sites they came from, so they are kept under their own name.
                if (File.Exists(_path))
                {
                    // **Closed before the move.** The enumerator used to be left open, and its handle on this
                    // very file made File.Move fail with "being used by another process" the first time the
                    // columns changed - on Craggy Peninsula that lost a whole Grand site's census.
                    string first;

                    using (var lines = File.ReadLines(_path).GetEnumerator())
                        first = lines.MoveNext() ? lines.Current : null;

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
                var directory = Path.GetDirectoryName(_path) ?? ".";
                var timeline = Path.Combine(directory, "arrivals.csv");

                // Moved aside when its columns changed, as spawns.csv is above.
                if (File.Exists(timeline))
                {
                    string first;

                    using (var lines = File.ReadLines(timeline).GetEnumerator())
                        first = lines.MoveNext() ? lines.Current : null;

                    if (!string.Equals(first, ArrivalsHeader, StringComparison.Ordinal))
                    {
                        File.Move(timeline,
                            Path.Combine(directory, $"arrivals_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
                    }
                }

                if (!File.Exists(timeline))
                    File.WriteAllText(timeline, ArrivalsHeader + "\n");

                File.AppendAllLines(timeline, _arrivals.ConvertAll(x => x.ToString()));

                if (Safe.Read(() => plugin.Settings.Recording.CollectMonsterDeaths.Value, true))
                    AppendUnderHeader(Path.Combine(directory, "deaths.csv"), DeathsHeader, DeathRows());
            }

            if (_drops.Count > 0)
                AppendUnderHeader(Path.Combine(Path.GetDirectoryName(_path) ?? ".", "drops.csv"), DropsHeader, DropRows());

            if (_remnantItems.Count > 0)
                AppendUnderHeader(Path.Combine(Path.GetDirectoryName(_path) ?? ".", "remnant_items.csv"), RemnantItemsHeader,
                    RemnantItemRows());

            AppendUnderHeader(Path.Combine(Path.GetDirectoryName(_path) ?? ".", "chests.csv"), ChestsHeader, _chestRows);

            AppendUnderHeader(Path.Combine(Path.GetDirectoryName(_path) ?? ".", "bond_deaths.csv"), BondDeathsHeader,
                _bondDeathRows);
            AppendUnderHeader(Path.Combine(Path.GetDirectoryName(_path) ?? ".", "bond_gains.csv"), BondGainsHeader,
                _bondGainRows);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the spawn census: {ex.Message}", 5f);

            // **Kept rather than lost.** The run is reset after this whatever happened, so a file that cannot be
            // written to - held open by a spreadsheet, or anything else - used to throw the whole site away. The
            // rows go to a file of their own instead, with the header they were built under, to be merged by hand.
            try
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, $"spawns_unsaved_{stamp}.csv"), System.Linq.Enumerable.Concat(new[] { Header }, rows));

                if (_arrivals.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(directory, $"arrivals_unsaved_{stamp}.csv"),
                        System.Linq.Enumerable.Concat(new[] { ArrivalsHeader }, _arrivals.ConvertAll(x => x.ToString())));
                    File.WriteAllLines(Path.Combine(directory, $"deaths_unsaved_{stamp}.csv"),
                        System.Linq.Enumerable.Concat(new[] { DeathsHeader }, DeathRows()));
                }

                if (_drops.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(directory, $"drops_unsaved_{stamp}.csv"),
                        System.Linq.Enumerable.Concat(new[] { DropsHeader }, DropRows()));
                }

                if (_remnantItems.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(directory, $"remnant_items_unsaved_{stamp}.csv"),
                        System.Linq.Enumerable.Concat(new[] { RemnantItemsHeader }, RemnantItemRows()));
                }

                if (_chestRows.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(directory, $"chests_unsaved_{stamp}.csv"),
                        System.Linq.Enumerable.Concat(new[] { ChestsHeader }, _chestRows));
                }

                DebugWindow.LogMsg($"[AutoExpedition] The spawn census was saved to spawns_unsaved_{stamp}.csv instead.", 5f);
            }
            catch (Exception again)
            {
                DebugWindow.LogError($"[AutoExpedition] Could not save the spawn census anywhere: {again.Message}", 5f);
            }
        }
    }

    /// <summary>deaths.csv's rows for this run: every counted monster that died or vanished.</summary>
    private List<string> DeathRows() =>
        _arrivalsById.Values.Where(x => x.DeathSeconds >= 0d).Select(x => x.DeathRow(_run)).ToList();

    /// <summary>
    /// Appends rows to a file under its header, moving a file with a different first line aside first, as spawns.csv
    /// and arrivals.csv are.
    /// </summary>
    private static void AppendUnderHeader(string path, string header, List<string> rows)
    {
        if (rows.Count == 0)
            return;

        var directory = Path.GetDirectoryName(path) ?? ".";

        if (File.Exists(path))
        {
            string first;

            using (var lines = File.ReadLines(path).GetEnumerator())
                first = lines.MoveNext() ? lines.Current : null;

            if (!string.Equals(first, header, StringComparison.Ordinal))
            {
                File.Move(path, Path.Combine(directory,
                    $"{Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            }
        }

        if (!File.Exists(path))
            File.WriteAllText(path, header + "\n");

        File.AppendAllLines(path, rows);
    }

    /// <summary>The spawn census's columns. A file whose first line differs is moved aside before writing.</summary>
    private const string Header =
        "when,run,areaHash,level,radius,scope,index,x,y," +
        "elites,monsterMarkers,remnants,sockets,used," +
        "rareChests,magicChests,normalChests," +
        "relics,caged,hatches,entrances,scenery," +
        "normal,magic,rare,unique,contested," +
        "lateNormal,lateMagic,lateRare,lateUnique,seconds,selected,ending,banner," +
        "inherited,wasted,chainLink,states,runes,arriving,encounterMonsters,encounterRares," +
        "encounterWaves,encounterWaveSizes,remnantStates,controllerMods,controllerStats,relicEffects,mapStats," +
        "runeWaves,highestRuneWave,names,atlasStats,arrivingSeen,firstWaveSeconds," +
        "planContent,planPropagation,linkContent,linkPropagation," +
        "predictedNormal,predictedMagic,predictedRare,predictedNormalWithoutOwn,predictedMagicWithoutOwn," +
        "predictedRareWithoutOwn,predictedTable,predictedEachWave";

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

                foreach (var mod in (marker.Mods ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    into.RelicMods.Add(mod.Trim());

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
        Clean(_banner),
        blast.Inherited.ToString(CultureInfo.InvariantCulture),
        blast.Wasted.ToString(CultureInfo.InvariantCulture),
        blast.ChainLink.ToString(CultureInfo.InvariantCulture),
        Clean(blast.States),
        Clean(blast.Runes),
        Clean(blast.Arriving),
        blast.Encounter.ToString(CultureInfo.InvariantCulture),
        blast.EncounterRares.ToString(CultureInfo.InvariantCulture),
        blast.EncounterWaveCount.ToString(CultureInfo.InvariantCulture),
        Clean(blast.EncounterWaveSizes),
        Clean(blast.RemnantStateChanges),
        Clean(blast.ControllerModChanges),
        Clean(blast.ControllerStatChanges),
        Clean(string.Join(" ", blast.RelicMods)),
        Clean(blast.MapStats),
        Clean(blast.RuneWaves),
        blast.HighestRuneWave.ToString(CultureInfo.InvariantCulture),
        Clean(Names(blast)),
        Clean(blast.AtlasStats),
        Clean(blast.ArrivingSeen),
        blast.FirstWaveSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        // What the plan credited: the whole route, then the link this row was caught by. Empty when not taken.
        Credit(_routeCredit?.Content),
        Credit(_routeCredit?.Propagation),
        Credit(LinkCredit(blast.ChainLink)?.Content),
        Credit(LinkCredit(blast.ChainLink)?.Carried),
        ExpectedCount(blast.Predicted?.Normal),
        ExpectedCount(blast.Predicted?.Magic),
        ExpectedCount(blast.Predicted?.Rare),
        ExpectedCount(blast.Predicted?.NormalWithoutOwn),
        ExpectedCount(blast.Predicted?.MagicWithoutOwn),
        ExpectedCount(blast.Predicted?.RareWithoutOwn),
        Clean(blast.PredictedTable),
        Clean(blast.Predicted?.EachWave ?? ""));

    /// <summary>An expected count to two places, or empty when there is none. See Blast.Predicted.</summary>
    private static string ExpectedCount(float? expected) =>
        expected is { } x && !float.IsNaN(x) && !float.IsInfinity(x) ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

    /// <summary>A plan figure for the census, or empty when there is none.</summary>
    private static string Credit(double? worth) =>
        worth is { } x && !double.IsNaN(x) && !double.IsInfinity(x) ? x.ToString("0.#", CultureInfo.InvariantCulture) : "";

    /// <summary>What the plan credited chain link n (from one) with, or null. See _routeCredit.</summary>
    private (double Content, double Carried)? LinkCredit(int link) =>
        _routeCredit is { } credit && link >= 1 && link <= credit.Each.Count ? credit.Each[link - 1] : null;

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

    /// <summary>
    /// The rare, magic and normal monsters that have arrived since the chain went off, and per remnant, in chain order,
    /// how many of them its rune waves brought so far - for a readout taken between detonations. Rares are counted
    /// with uniques, as RuneWaves counts them. A monster a blast unearths from a marker, or one carrying a set no
    /// remnant's wave carries, is in the total and on no remnant.
    /// </summary>
    public string WavesSoFar()
    {
        if (_arrivals.Count == 0)
            return "  nothing has arrived since the chain went off";

        var rares = _arrivals.Count(a => a.Rarity is MonsterRarity.Rare or MonsterRarity.Unique);
        var magic = _arrivals.Count(a => a.Rarity == MonsterRarity.Magic);
        var normal = _arrivals.Count - rares - magic;
        var byRemnant = RuneWaves();
        var placedRares = byRemnant.Values.Sum(w => w.Values.Sum(x => x.Rares));
        var placedMagic = byRemnant.Values.Sum(w => w.Values.Sum(x => x.Magic));
        var placedNormal = byRemnant.Values.Sum(w => w.Values.Sum(x => x.Monsters - x.Magic - x.Rares));
        var text = new System.Text.StringBuilder();

        text.AppendLine($"  {_arrivals.Count} monsters have arrived since the chain went off: {rares} rare or unique, " +
                        $"{magic} magic, {normal} normal. The remnants' rune waves account for {placedRares} of the " +
                        $"rares, {placedMagic} of the magic and {placedNormal} of the normal; the rest are the markers' " +
                        $"own monsters or carried no remnant's rune set, {OutsideEncounter} of them one that fitted a " +
                        "remnant but arrived outside its encounter.");

        foreach (var i in Enumerable.Range(0, _waves.Count)
                     .OrderBy(i => BlastThatCaught(_waves[i].At, _radius + RemnantReachSlack)))
        {
            var remnant = _waves[i];
            var link = BlastThatCaught(remnant.At, _radius + RemnantReachSlack) + 1;
            var waves = byRemnant.GetValueOrDefault(i);

            text.AppendLine($"    link {(link > 0 ? link.ToString(CultureInfo.InvariantCulture) : "?"),2}  remnant " +
                            $"({remnant.At.X:0},{remnant.At.Y:0}), {remnant.Used} rune(s) used: " +
                            (waves is { Count: > 0 }
                                ? $"{waves.Values.Sum(x => x.Rares)} rare(s), {waves.Values.Sum(x => x.Magic)} magic, " +
                                  $"{waves.Values.Sum(x => x.Monsters - x.Magic - x.Rares)} normal, by wave " +
                                  string.Join(" ", waves.Select(x =>
                                      $"{(x.Key == 2 ? "1-2" : x.Key.ToString(CultureInfo.InvariantCulture))}:" +
                                      $"{x.Value.Rares}r/{x.Value.Magic}m/{x.Value.Monsters - x.Value.Magic - x.Value.Rares}n"))
                                : "no wave monsters matched yet"));
        }

        return text.ToString().TrimEnd();
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
                      : "the detonator has not been seen unfired and no remnant is waiting to be fought - not recording this one");
        }

        var counted = 0;
        var contested = 0;

        foreach (var blast in _blasts)
        {
            counted += blast.Normal + blast.Magic + blast.Rare + blast.Unique;
            contested += blast.Contested;
        }

        var died = _arrivalsById.Values.Count(x => x.DeathSeconds >= 0d && !x.Vanished);
        var vanished = _arrivalsById.Values.Count(x => x.Vanished);

        return (_resumed
                   ? $"RESUMED after a return to the area - {_before.Count} monsters already here excluded, " +
                     "only remnants fought from here on are written; "
                   : "") +
               $"{_blasts.Count} blasts over {_markers.Count} markers, radius {_radius:0.#}; " +
               $"{_counted.Count} monsters unearthed, {counted} inside a blast, {contested} in two, " +
               $"{(DateTime.UtcNow - _first).TotalSeconds:0}s since the first; {died} seen dying, {vanished} vanished, " +
               $"{_drops.Count} recorded drop(s), {_bondDeathRows.Count} Bond death(s), {_bondGainRows.Count} affix gain(s); " +
               $"no-drop stat on {_arrivalsById.Values.Count(x => x.NoDropsAtArrival > 0)} at arrival and " +
               $"{_arrivalsById.Values.Count(x => x.NoDropsAtDeath > 0)} at death, " +
               $"{_arrivalsById.Values.Count(x => x.NoDropsAtArrival < 0)} unreadable at arrival; " +
               $"{_arrivalsById.Values.Sum(x => x.Revivals)} revival(s) of a monster seen dead";
    }
}
