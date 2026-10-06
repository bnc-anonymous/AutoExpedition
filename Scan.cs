using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

internal enum TargetKind
{
    Monster,
    Elite,
    Chest,
    Remnant,

    /// <summary>
    /// A Verisium Sentry: a dormant drone a blast wakes up, which then follows and empowers.
    ///
    /// Not a marker, which is why it took finding. A dormant one is a MiscellaneousObject - filed
    /// with the doodads and the detonator, not in the marker bucket the rest of the scan reads -
    /// sitting untargetable in the dig site until a blast reaches it.
    /// </summary>
    Sentry,

    /// <summary>
    /// Something a blast hatches rather than opens.
    ///
    /// A siren egg. It is a monster kind and not a chest kind on purpose - what comes out is
    /// unearthed by the explosive, so a remnant's rune reaches it, and counting it as a container
    /// would quietly drop it out of the propagation the whole plan turns on.
    ///
    /// **Every egg is worth the same, and the spawner pins do not say otherwise.** They looked like
    /// they might: SirenEggBossSpawner wears Marker_Pin_Boss art and sits a few grid from an egg, so
    /// a first count - fifteen pins against forty eggs - read as fifteen special eggs. A full lap of
    /// the site said something else. There are twenty-nine SirenEggBossSpawner and twenty-nine
    /// SirenEggSpawner, each boss pin within a few grid of a plain one, so every nest has both and
    /// the pin separates nothing. The fifteen were simply the ones loaded at the time, which is a
    /// fact about where the player was standing.
    ///
    /// The egg does carry its own boss_spawn state, which is the game stating it rather than a
    /// heuristic - but it read 0 on all forty before any blast, so there is nothing to read yet.
    /// That is where to look again once a detonation has been watched.
    /// </summary>
    Hatch,

    /// <summary>
    /// The way into a sub-area, which a blast opens.
    ///
    /// Only the sub-area cap so far. The Peninsula arena gate looked like a second one and is not -
    /// see TargetKind.Scenery.
    ///
    /// One on the Grand site it was found on, wearing SanctuaryCap_01 art under an
    /// ExpeditionCavernEntrance minimap icon, with a TriggerableBlockage and a glow_epk like
    /// everything else a blast sets off. It is an IngameIcon, so the sweep already walked past it -
    /// it fell through because the classifier only looked at things whose metadata began
    /// ExpeditionMarker.
    ///
    /// **Not a container and not a monster: what it is worth is behind it, not in it.** Catching it
    /// yields nothing directly - it opens a cavern the planner cannot see into and cannot plan for,
    /// since none of that content is loaded until the way in is open. So it is priced as access
    /// rather than as loot, and the weight says how much of a detour a whole extra area is worth.
    /// </summary>
    Entrance,

    /// <summary>
    /// A relic: an expedition object carrying its own modifiers rather than its own loot.
    ///
    /// Recognised by the "expedition_relic" state, which every one carries and nothing else does.
    /// Seen so far: the Peninsula goblin relic at 3, and the Prairie wisp traps at 7, 8 and 9 -
    /// all wearing an ExpeditionPOI minimap icon, all carrying ObjectMagicProperties and Stats,
    /// which containers here do not.
    ///
    /// **They are one family and they are not one thing.** The goblin relic prints "15% increased
    /// rarity of items dropped by monsters", which is a modifier on everything unearthed after it -
    /// propagation, and worth reaching for. A wisp trap prints "contains an Azmeri spirit", which is
    /// a thing you get where it stands. Same state, same components, same icon; one carries and the
    /// other does not, and only the words on the object say which.
    ///
    /// So the kind is decided by the state and the VALUE is per art, with the carrying ones named
    /// in Weighing. A relic nobody has read is worth the flat weight and carries nothing - which
    /// errs towards taking it in passing rather than towards bending a chain for a guess.
    /// </summary>
    Relic,

    /// <summary>
    /// Blast-activated scenery: something an explosive sets off that is not content in its own right.
    ///
    /// Explodable goblin huts on the Peninsula tileset, thirteen of them, and the exploding barrels
    /// beside them. Kept as content so a chain passing one takes it for free, at a weight that
    /// cannot pull a link out of position - the clam's reasoning exactly.
    ///
    /// **The arena gate is here too, and it took looking behind it to know that.** It has the shape
    /// of the sub-area cap - a TriggerableBlockage that unlocks ground rather than yielding
    /// anything where it stands - and was priced with it at sixty on that reasoning. What is
    /// actually through it is rogue exiles, which are not worth much, so a weight that would bend a
    /// chain towards it was buying a detour to a modest fight. Priced with the huts instead.
    ///
    /// The lesson is not about gates: two objects with identical components and states can be worth
    /// six times different amounts, and nothing readable from the entity says which. Only what
    /// comes out of them does.
    ///
    /// **The barrel is not really scenery and is filed here anyway, deliberately.** It carries
    /// inherent_explosion_radius 60, against an explosive's 35, so setting one off is a second and
    /// larger blast wherever it stands. Nothing in the planner models a secondary detonation, so
    /// pricing it as though the barrel itself were the prize is wrong in a way a weight cannot fix.
    /// Filed as scenery, flagged here, and worth modelling properly if the radius does what it says.
    /// </summary>
    Scenery,

    /// <summary>
    /// The named barrel, ExplodingFill_BoomBarrel, which explodes when a blast reaches it.
    ///
    /// **This kind is a name, not the mechanic.** What makes an object set off a second blast is
    /// Target.Sets being non-nought, and several tilesets state that on objects this kind never
    /// matches - FaridunExplosive at 80 and OilWell at 110 in Stagnant Basin, both filed under
    /// Unknown. Everything that acts on a second blast - the closure, the rings, the already-blown
    /// test - asks Sets rather than this kind, so the two lists are not the same length.
    ///
    /// **It is worth nothing and changes everything, which is why it cannot be a weight.** Catching
    /// one is a second detonation wherever it stands, and a second detonation is not a marker worth
    /// more points, it is a blast the chain did not have to place. A player uses them by clipping the
    /// edge of one deliberately.
    ///
    /// **And they set each other off.** Two stood five grid apart in the site this was written for,
    /// so the first reaches the second, the second reaches whatever is around it, and so on - which
    /// makes the coverage a closure rather than a circle. See PlanTarget.Sets.
    ///
    /// The radius is read from the state rather than assumed: the game states it per object, and
    /// nothing says every tileset uses the same number. It is larger than an explosive's own blast,
    /// not smaller - 60 on the Gallows barrel and 110 on the Basin derrick against an explosive's
    /// 34 to 37 - which is why clipping one is worth building a chain around. See Target.Sets.
    ///
    /// **Nothing highlights green for what a barrel will take.** The game lights up what an
    /// EXPLOSIVE catches, and a barrel's blast has not happened yet when the circle is up - so
    /// unlike every other coverage claim this one cannot be checked against the game before the
    /// click. Placement must not expect it, which is why what a link is chosen FOR excludes it.
    /// </summary>
    Barrel,

    /// <summary>
    /// Something a blast sets off that nothing here has been taught to recognise.
    ///
    /// **It is a target now rather than a red line, and the difference is that the planner can see
    /// it.** Unexpected finds these by the state the game sets on anything an explosive acts on, so
    /// their existence is not in doubt - only their value, which is a question for whoever opens
    /// one. Until somebody answers it they carry a nominal weight: enough to be drawn and routed
    /// past, far too little to bend a chain towards. See Unknowns.
    /// </summary>
    Unknown,

    /// <summary>
    /// Monsters penned behind a gate, which a blast lets out.
    ///
    /// The Peninsula rhoa gate: one rare and one unique, counted in game, waiting immediately behind
    /// it and fighting the moment it opens.
    ///
    /// **It is a monster kind for the same reason a hatch is, and a separate one because it is not
    /// worth the same.** An egg and a rhoa pen both unearth monsters where they stand - so both
    /// belong in the propagation term, where a container would not - but an egg is one small thing
    /// and this is a rare plus a unique. Filed under Hatch it took the egg's weight, which
    /// undersold it by about a factor of ten.
    ///
    /// **And it looks exactly like the arena gate, which is worth almost nothing.** Same
    /// TriggerableBlockage, same "active" state, same shape of object; behind one is a rare and a
    /// unique that fight you at once, behind the other is a walk to some rogue exiles. Nothing
    /// readable off the entity separates them. Both were classified by looking.
    /// </summary>
    Caged,

    /// <summary>
    /// A monolith, which summons a unique boss where it stands.
    ///
    /// **Its own kind because it is not worth what a gate is worth, and a kind is how this plugin
    /// says that.** It was filed under Caged on the reasoning that both are monsters a blast lets
    /// out - true, and it is why both sit in the propagation term - but the two are priced
    /// differently and a shared kind meant one row in the reference table answering for both, one
    /// colour, and one line in the census. What they have in common is where their weight is spent,
    /// not how much it is.
    ///
    /// Told by the table row path:*Monolith*, and only where the metadata also names Expedition; no
    /// state is checked.
    ///
    /// **Not yet seen.** No saved site holds an expedition monolith. The row alone matched only the
    /// essence encounter's Metadata/MiscellaneousObjects/Monolith and MiniMonolith (28 targets over 51
    /// saved sites), which an explosive does nothing to, and the solver spent blasts on them. A matches
    /// cell opens only at its ends, so "*Expedition*Monolith*" cannot say this, and the qualifier lives
    /// in Scan as the hatch's does. The states pick_boss, boss_spawned and boss_defeated were described
    /// here as the evidence; they were never read.
    /// </summary>
    Monolith,

    /// <summary>
    /// A buried strongbox: one an explosive digs up rather than one you walk to and open.
    ///
    /// **Every strongbox in a dig site carries glow_epk, and none of them were being scanned.** The
    /// state is the game saying a blast acts on this, which is the same test Unexpected uses to find
    /// things nobody has taught the scan about - and on a strongbox-themed Atoll map that was ten
    /// pieces of content the planner could not see. Only the one standing next to the player showed
    /// up in the unknown list, because that list is about what a blast under the cursor would take.
    ///
    /// They are not chests in the expedition sense: no marker, no chestmarker art, no tier to read.
    /// What they do have is a rarity the game states outright - White, Magic, Rare, Unique - which is
    /// a better ranking than the metadata name, since Armourer, Martial, Research and Large are the
    /// loot table rather than the value.
    /// </summary>
    Strongbox,
}

/// <summary>
/// Which chest a chest marker stands for.
///
/// Established by remembering where every marker stood, clearing the encounter and matching the
/// chests that appeared back to the marker they came from - eight chests, every one at a distance
/// of exactly zero, so there is no guesswork in it. See <see cref="Correlate"/>.
/// </summary>
internal enum ChestTier
{
    /// <summary>
    /// A marker art nobody has correlated yet.
    ///
    /// Weighted as though it were Uncommon, deliberately. An art nobody has matched is more likely
    /// to be one of the better ones than the plainest - and of the two ways to be wrong, walking to
    /// a chest that turns out white costs a few seconds where ignoring one that turns out yellow
    /// costs the chest.
    /// </summary>
    Unknown,

    /// <summary>
    /// A white Excavated Chest. chestmarker_signpost_01, _02 and _03 - all three, despite each
    /// having its own height.
    /// </summary>
    Common,

    /// <summary>
    /// A blue Excavated Chest. chestmarker2 and chestmarker2_02, confirmed separately.
    /// </summary>
    Uncommon,

    /// <summary>
    /// chestmarker3, which was predicted before it was seen and has now been seen.
    ///
    /// The naming was a guess: signpost for the white chest, chestmarker2 for the blue, so a
    /// chestmarker3 ought to exist and ought to be the yellow one. The art turning up settles the
    /// first half - the family is real and is not a fourth coat of paint on an existing tier, or it
    /// would be chestmarker2_03.
    ///
    /// And it is the rare one, confirmed in game rather than inferred from the naming - which had
    /// been the open half of it, since a family existing says nothing about what comes out of it.
    /// </summary>
    Rare,
    /// <summary>
    /// A Grand Expedition reward chest, of a kind the smaller sites do not have.
    ///
    /// **These are not the white, blue and yellow chests wearing new art.** A Grand site names its
    /// chests by what is in them - currency, uniques, maps, armour, trinkets - and one of them, the
    /// bright currency chest, is reported to drop a Divine Orb about two times in five, which is
    /// worth more than every ordinary chest on a small site put together. Mapping that onto "rare
    /// chest" would have the planner treat a jackpot as a nice extra.
    ///
    /// So they are their own tiers with their own weights, left for the player to price as the
    /// drops become known rather than guessed at from one sighting.
    /// </summary>
    GrandGeneric,

    GrandArmour,

    GrandMaps,

    GrandTrinkets,

    /// <summary>
    /// A clam, and anything else that drops gold for being in the way.
    ///
    /// Worth something and worth nothing: gold is gold, but no chain should bend towards it. A
    /// weight near zero says exactly that - it breaks a tie between two otherwise equal links and
    /// decides nothing else.
    /// </summary>
    Gold,

    /// <summary>The dull one: RewardChestCurrency.</summary>
    GrandCurrency,

    /// <summary>The bright one: RewardChestCurrencyRare.</summary>
    GrandCurrencyBright,

    GrandUnique,


    /// <summary>
    /// A Researcher's Strongbox, which drops currency and is the one worth going out of the way for.
    ///
    /// **Strongboxes are tiered like chests and were priced as one thing.** The loot table is the
    /// type - a Researcher's drops currency, an Armourer's or a Martial's drops its own gear - so a
    /// single weight for "strongbox" cannot say that one of them is worth a detour and the others
    /// are worth taking only when they are on the way. Tiering them gives the reference table a row
    /// each, which is the only place anybody can say so.
    ///
    /// Derived at weigh time rather than stored: a mound reads "StrongBox" and nothing else until
    /// the sweep meets the chest standing on its cell, so the type arrives after the target does.
    /// See Weighing.RowOfStrongbox.
    /// </summary>
    // **The three below are no longer produced.** A strongbox is priced by its base now, one row
    // each, so nothing classifies one into a tier - see Weighing.RowOfStrongbox. Kept rather than removed
    // because ChestTier is written into saved files and dropping a member renumbers the ones after
    // it, which would silently re-tier somebody's remembered chests.
    StrongboxResearcher,

    /// <summary>Any strongbox the game calls unique that is not a Researcher's.</summary>
    StrongboxUnique,

    /// <summary>Armourer, Martial, Large - the ones whose loot is their own gear.</summary>
    StrongboxOther,
}

/// <summary>
/// One piece of content in the dig site.
///
/// Everything that matters is held as a plain value rather than read back off the entity, because
/// **the entity does not last**. Walk far enough and the game unloads it; the record has to outlive
/// that, and a record that reaches into a freed memory object to answer "where were you" is worse
/// than one that simply remembers.
/// </summary>
internal sealed class Target
{
    public Vector2 Grid { get; init; }

    /// <summary>
    /// What this is. Settable only so a record made by an older classifier can be corrected in
    /// place - see Scan.Correct. Nothing else should be writing it after the target is made.
    /// </summary>
    public TargetKind Kind { get; set; }

    /// <summary>For a chest, which one. Unknown for everything else.</summary>
    public ChestTier Tier { get; init; }

    /// <summary>
    /// The model, which for a marker is what classified it. Kept rather than thrown away, because it
    /// is the raw evidence: an art this plugin has never seen can be read off the overlay and taken
    /// to the correlation tool.
    ///
    /// Filled in for every target and not only for markers. It used to be set on the marker branch
    /// alone, so the overlay's label was blank over every sentry, egg and clam - which is exactly
    /// the set of things whose art nobody has catalogued and most wants reading off the screen.
    /// </summary>
    public string Art { get; init; } = "";

    /// <summary>
    /// Whether the table says to ignore this target, asked again only when the table changes. A target scanned or
    /// remembered before its row was written is dropped by this rather than by being classified again, which a
    /// known target never is. See Scan.IgnoredByTable.
    /// </summary>
    public bool Ignored
    {
        get
        {
            if (_ignoredAt != Wrt.Revision)
                (_ignored, _ignoredAt) = (Scan.IgnoredByTable(Meta), Wrt.Revision);

            return _ignored;
        }
    }

    private bool _ignored;

    private int _ignoredAt = -1;

    /// <summary>
    /// The model as it can be read now: Art when the scan had it, otherwise read again - at most once a second -
    /// until the game has streamed it in, then kept. Art is read once, when the object is classified, and an
    /// object classified before its art loaded kept an empty one. See Unknowns.Key.
    /// </summary>
    public string ArtNow
    {
        get
        {
            if (Art.Length > 0)
                return Art;

            if (_artLate.Length > 0 || Entity == null || DateTime.UtcNow - _artTriedAt < TimeSpan.FromSeconds(1))
                return _artLate;

            _artTriedAt = DateTime.UtcNow;

            return _artLate = Scan.Art(Entity) ?? "";
        }
    }

    private string _artLate = "";

    private DateTime _artTriedAt = DateTime.MinValue;

    /// <summary>
    /// The magic mods the object carries, joined.
    ///
    /// **This is how a relic says what it is, and it took three tilesets to notice.** Every relic
    /// carries ObjectMagicProperties naming its own effect - ExpeditionRelicUpsideSpecialGoblinTotem,
    /// ExpeditionRelicUpsideSpecialRunicHenge, ExpeditionRelicUpsideSpecialAzmeriWisp - beside a
    /// dummy downside. That is the game stating the thing I had written down as unknowable, after
    /// concluding twice that only the words printed on the object could separate one relic from
    /// another.
    ///
    /// Better than either name it could be matched on: the metadata is a tileset's choice of words
    /// and the art is a model, where this is the effect itself. The three Azmeri wisp traps share
    /// one mod across three tiers and three arts, which is the game saying they are one thing.
    /// </summary>
    public string Mods
    {
        get
        {
            // **Re-read while it is empty, for the same reason the states are.**
            //
            // This was a snapshot taken the instant the target was classified, and a snapshot is
            // wrong twice over: ObjectMagicProperties is not always readable the moment an entity
            // streams in, and the key an object is filed under is now worked out once and kept. So a
            // relic classified a fraction too early was filed with no effect in its key, and the one
            // thing that separates five Vaal relics from each other - the mod naming what each
            // grants - was permanently missing from its name.
            //
            // Only while empty. A mod that has been read does not change, and re-reading it every
            // time this is asked would put a remote memory read in the middle of the scorer.
            // **And re-read when Reread has asked for it, without throwing away what is known.**
            //
            // Reread used to blank this. See Reread for why that cost a strongbox most of its
            // worth; the rule here is the same one the emptiness test already implements - a read
            // that comes back with nothing is a read that failed, not an object with no modifiers.
            if ((_mods.Length > 0 && !_wantsFreshMods) || Entity == null)
                return _mods;

            var read = Scan.Mods(Entity);

            if (read is not { Length: > 0 })
                return _mods;

            _wantsFreshMods = false;

            return _mods = read;
        }

        init => _mods = value ?? "";
    }

    private string _mods = "";

    /// <summary>Whether the modifiers have been read, without reading them. See Scan.Keep.</summary>
    public bool ModsKnown => _mods.Length > 0;

    /// <summary>
    /// Every modifier on this object with the values the game printed on it, implicit and explicit.
    ///
    /// **Mods above is names only, and that is why a pack count was being read off a ground label.**
    /// ObjectMagicProperties.Mods is a List&lt;string&gt;; ExplicitModData returns ItemMod, which
    /// carries Values, ValuesMinMax, Level and Group. Nothing here had ever read it, and
    /// Scan.ExplicitPacks records the resulting belief - "the mod ids carry no numbers" - as a fact
    /// about the game rather than about that one property.
    ///
    /// Latched and re-read exactly as Mods is, for the same two reasons: the component is not always
    /// readable the instant an entity streams in, and a strongbox's modifiers change when somebody
    /// rerolls them. Reread clears both.
    /// </summary>
    public (string Name, int[] Values, bool Implicit)[] Valued
    {
        get
        {
            // **A read that comes back with nothing is a read that failed.**
            //
            // This returned whatever the read gave, including null, and Reread blanked the latch on
            // every sweep for every live strongbox - so a box's modifiers were only ever as good as
            // the most recent read, with nothing protecting a good one from being replaced by a bad
            // one. Measured over two dumps of one site, the nine strongboxes in the pool were worth
            // 3,946 with 3,761 of that in guard packs, and 1,226 with 1,041 in packs a moment
            // later: the base row weight was 185 in both and every point of the difference was
            // TierSplitFromModifiers falling back to PacksAssumedOfStrongbox. The plan on screen
            // lost two thirds of its score for it.
            //
            // So the latch is only ever replaced by an answer. Rolling a box still moves it,
            // because a box being rolled is a box you are standing next to. See Reread.
            if (_valued != null && !_wantsFreshValued)
                return _valued;

            // **The box, then the mound.** A strongbox is two entities on one cell - the terrain fill
            // a blast breaks, and the chest inside it - and the modifiers are on the chest. The mound
            // usually becomes Entity because terrain streams in ahead of the chest, so asking Entity
            // alone found an object with no ObjectMagicProperties worth reading and reported every box
            // as having no modifiers at all. The same pair caught out the placement check, for the
            // same reason. See Held.
            //
            // Held first rather than only: where there is no pair, Held is null and the object itself
            // is the one carrying the mods.
            var read = (Held == null ? null : Scan.Valued(Held)) ??
                       (Entity == null ? null : Scan.Valued(Entity));

            if (read is not { Length: > 0 })
                return _valued;

            _wantsFreshValued = false;

            return _valued = read;
        }

        init => _valued = value;
    }

    private (string Name, int[] Values, bool Implicit)[] _valued;

    /// <summary>
    /// Asks the latched reads to take the next ANSWER from the entity, rather than discarding them.
    ///
    /// **For the one object on a dig site that changes.** Mods is read once on purpose - it is a
    /// remote read and a relic's modifiers are fixed for the life of the map - but a strongbox's are
    /// not: a currency use rerolls them, and a read-once latch meant a box rolled from normal to
    /// rare kept the worth of the box it used to be. See Scan.Guards, which is the only caller and
    /// the thing that rate-limits it.
    ///
    /// **It blanked them, and that was most of a plan's score.** Guards calls this every sweep for
    /// every live strongbox, so the modifiers were only ever as good as the most recent read - and
    /// a read that fails returns nothing rather than throwing, which is indistinguishable from a
    /// box with no modifiers. The guard packs are what a strongbox is mostly worth: measured over
    /// two dumps of one site, nine boxes went from 3,946 to 1,226, the base row weight was 185 in
    /// both, and all 2,720 of the difference was packs. The chain on screen lost two thirds of its
    /// score without the site having changed.
    ///
    /// So this no longer takes anything away. It says the latch is owed a fresh read, and the latch
    /// is replaced only when one succeeds. A reroll still moves it within a sweep, because a box
    /// being rolled is a box you are standing next to.
    /// </summary>
    public void Reread()
    {
        _wantsFreshMods = true;
        _wantsFreshValued = true;
    }

    /// <summary>
    /// Whether the latched reads are owed a fresh one. See Reread, Mods and Valued.
    ///
    /// Two flags rather than one, because the two are read independently and a successful read of
    /// either must not tell the other it is current.
    /// </summary>
    private bool _wantsFreshMods;

    private bool _wantsFreshValued;

    /// <summary>
    /// Whether a blast does anything to this, as opposed to it merely standing in a dig site.
    ///
    /// **Carrying glow_epk is not the same as being explodable, and that cost a wrong classification
    /// on two maps.** Every strongbox has the state - it is how the client lights things under the
    /// placement circle - so gating on it made ordinary walk-up-and-open strongboxes into blast
    /// targets, and the chain was planned around a box no explosive can touch.
    ///
    /// The state that separates them is inherent_explosion_radius. A buried strongbox has it, and so
    /// does the mound over it; a Blacksmith's Strongbox standing in the open does not, and neither
    /// does the "High" variant that turned up in the Atoll map. Read once at classification, because
    /// it is a property of what the object IS.
    /// </summary>
    public bool Explodes { get; set; }

    /// <summary>
    /// The strongbox standing on the same cell as this one, by metadata. Empty when there is none.
    ///
    /// **A buried strongbox is two entities on one cell and the planner can only have one.** The
    /// mound the blast breaks is ExplodingFill_StrongBox, filed under Terrain; the box itself is a
    /// Chest with the type and rarity on it and the guard count on its ground label. Neither alone
    /// says what the thing is worth: the mound is what the explosive acts on, and the chest is what
    /// you get.
    ///
    /// So the mound is the target and this is what it holds. Filled by the sweep when it meets the
    /// chest on a cell already spoken for - which it does every sweep, and which it used to answer
    /// by walking straight past.
    /// </summary>
    public string Holds { get; set; } = "";

    /// <summary>
    /// How many packs guard this strongbox implicitly, from the words written on it.
    ///
    /// **The game prints it and the number is nowhere else.** "Guarded by 6 packs of Monsters" is on
    /// the ground label and no stat or state carries the count - a modified box does have mods
    /// (ChestSummonMagics is one) but they are bare ids with no numbers attached, so the sentence on
    /// the label is the only place the figure appears. It is read where the player reads it.
    ///
    /// This is the total on the IMPLICIT line, above the separator, which every strongbox has
    /// whatever its rarity. The explicit modifiers are counted separately - see ExplicitPacks.
    ///
    /// **The implicit line names a rarity like an explicit one does**, so it is split across the three
    /// counts below rather than being one mixed number. Seen in game on two boxes an hour apart: a
    /// Blacksmith's read "Guarded by 6 packs of Monsters" and a Researcher's "Guarded by 6 packs of
    /// Rare Monsters". This total is kept beside the split because it is the read-once gate and
    /// because a file written before the split has only this.
    ///
    /// Zero means not read yet rather than none: every strongbox has at least one pack, so a zero is
    /// a label that was not up when the sweep went past, and it is asked again. That doubles as the
    /// gate for all six counts, since this one is never legitimately zero.
    /// </summary>
    public int Packs { get; set; }

    /// <summary>
    /// The implicit guarding packs by the rarity the line names. These sum to Packs.
    ///
    /// Split rather than mixed because the sentence is explicit about it. An earlier reading had
    /// these as three probabilities sharing one pack count - how often an implicit pack came up
    /// rare, magic or normal - which was wrong: the game states the rarity and does not mix.
    /// </summary>
    public int ImplicitPacks { get; set; }

    /// <summary>Implicit packs of magic monsters. See ImplicitPacks.</summary>
    public int ImplicitMagicPacks { get; set; }

    /// <summary>Implicit packs of rare monsters. See ImplicitPacks.</summary>
    public int ImplicitRarePacks { get; set; }

    /// <summary>
    /// Packs promised by an explicit "Guarded by n packs of ..." modifier, by the rarity it names.
    ///
    /// Kept apart from the implicit packs so each can carry its own estimate of how many monsters a
    /// pack holds. Both lines name a rarity, so the two sets ask the same question of a differently
    /// sourced pack.
    ///
    /// Read from the label rather than from the mod list, because the mod ids carry no numbers -
    /// ChestSummonMagics says a roll happened and not that it was for three packs.
    ///
    /// **Nothing in the words tells an explicit line from the implicit one.** Both are "Guarded by n
    /// packs of ... Monsters" and either can name any rarity, so they are separated by ORDER: the
    /// implicit line renders above the modifier block, so the first guarding sentence is the
    /// implicit one and every later one is explicit. That is an assumption about the label's layout
    /// rather than something the game states, and it is the part of this worth doubting first if a
    /// count ever looks wrong.
    /// </summary>
    public int ExplicitPacks { get; set; }

    /// <summary>Packs from an explicit "packs of Magic Monsters". See ExplicitPacks.</summary>
    public int ExplicitMagicPacks { get; set; }

    /// <summary>Packs from an explicit "packs of Rare Monsters". See ExplicitPacks.</summary>
    public int ExplicitRarePacks { get; set; }

    /// <summary>
    /// What each half of a paired object reads for a state, for the dump.
    ///
    /// "lit false" says the answer and not who gave it, which is no use when the whole question is
    /// which of two entities on one cell was asked. See Held.
    /// </summary>
    public string Halves(string name) =>
        Held == null
            ? $"{name} {State(name)} (one entity)"
            : $"{name} {State(name)}/{Stated(Held, name)} (self/held)";

    /// <summary>Which rarities the guarding packs are, for the marker line.</summary>
    private string Explicitly()
    {
        var said = Named(ImplicitRarePacks, "rare") + Named(ImplicitMagicPacks, "magic") +
                   Named(ImplicitPacks, "normal");

        var explicitly = Named(ExplicitRarePacks, "rare") + Named(ExplicitMagicPacks, "magic") +
                     Named(ExplicitPacks, "normal");

        return (said.Length > 0 ? $" ({said.Trim()})" : "") +
               (explicitly.Length > 0 ? $", explicit {explicitly.Trim()}" : "");
    }

    private static string Named(int packs, string rarity) => packs > 0 ? $"{packs} {rarity} " : "";

    /// <summary>
    /// What the game says this object's rarity is, as a word. Empty when it has none.
    ///
    /// Read once and kept, like everything else here, because it is wanted for an object the game
    /// may since have unloaded. See TargetKind.Strongbox, which is ranked by it.
    /// </summary>
    public string Rarity { get; set; } = "";

    /// <summary>
    /// The minimap icon's id, where the entity has one.
    ///
    /// A second name for the same object, and on a Grand site the more telling of the two: every
    /// reward chest there wears ChestCurrency.ao while the icon separates the bright chest from the
    /// dull one. Kept for the same reason as Art - it is what an unfamiliar object can be named by.
    /// </summary>
    public string Icon { get; init; } = "";

    /// <summary>
    /// The minimap icon as it can be read now: Icon when the scan had it, otherwise read again - at most once a
    /// second - until the game has set it, then kept. Read once at classification, an icon not there yet stayed
    /// empty: a relic and a sub-area entrance on a Frigid Bluffs site (2026-10-04) were filed without theirs and
    /// missed their shipped rows. See ArtNow and Unknowns.Key.
    /// </summary>
    public string IconNow
    {
        get
        {
            if (Icon.Length > 0)
                return Icon;

            if (_iconLate.Length > 0 || Entity == null || DateTime.UtcNow - _iconTriedAt < TimeSpan.FromSeconds(1))
                return _iconLate;

            _iconTriedAt = DateTime.UtcNow;

            return _iconLate = Scan.Icon(Entity) ?? "";
        }
    }

    private string _iconLate = "";

    private DateTime _iconTriedAt = DateTime.MinValue;

    /// <summary>
    /// What to call this on screen: the best name there is for it, never blank.
    ///
    /// The art if it has one, the minimap icon if it does not, the tail of its metadata if it has
    /// neither, and the kind as the last resort. A label that says nothing is worse than a label
    /// that says something coarse, because a blank one reads as "the overlay is broken" rather than
    /// as "this object has no model name".
    /// </summary>
    public string Label
    {
        get
        {
            if (Kind == TargetKind.Remnant)
                return "remnant, sockets " + Sockets;

            // Named by what decides its weight, so the label is a check on the reading rather than
            // a restatement of the metadata. A zero here is a label that has not been read yet.
            if (Kind == TargetKind.Strongbox)
            {
                var of = Holds.Length > 0 ? Holds : Meta;
                var cut = of.LastIndexOf('/');

                return (cut >= 0 && cut < of.Length - 1 ? of[(cut + 1)..] : "strongbox") +
                       (Rarity.Length > 0 ? $" ({Rarity})" : "") +
                       (Explodes
                           ? Packs > 0
                               ? $", {Packs} packs" + Explicitly()
                               : ", packs unread"
                           : ", not explodable");
            }

            // What is actually behind it, since neither the name nor the art says. Measured by
            // opening one: a single rogue exile, which is a unique and not much of one. Written on
            // the label because the whole point of naming a marker is to tell you what taking it

            // The same name the settings tab gives it, so a thing labelled on the ground can be
            // found in the list without translation. The tail of a metadata path is not a name -
            // "ExpeditionEncasedMonster" says nothing about which row prices it. See Unknowns.Name.
            // Anything the plugin has filed rather than recognised is named the way the settings
            // name it, so a label on the ground can be found in the list without translation.
            // Relics included: five Vaal relics share an art and differ only in what they grant, so
            // "tundravaalremnant.ao" names all five and none.
            if (Kind == TargetKind.Unknown || Unknowns.Unread(this))
                return Unknowns.Describe(this);

            if (Art.Length > 0)
                return Art;

            if (Icon.Length > 0)
                return Icon;

            var slash = Meta.LastIndexOf('/');
            var tail = slash < 0 ? Meta : Meta[(slash + 1)..];

            return tail.Length > 0 ? tail : Kind.ToString().ToLowerInvariant();
        }
    }

    public int Sockets { get; set; }



    /// <summary>
    /// How big this marker is on the ground, in grid units.
    ///
    /// An explosive catches a marker when the blast TOUCHES it, not when it reaches its middle, so
    /// the distance at which a thing is caught is the blast radius plus this. They differ a lot:
    /// 3.8 for a monster or chest marker, 4.55 for an elite, 6.8 for a remnant.
    /// </summary>
    public float Radius { get; init; }

    /// <summary>
    /// For a remnant, the outcomes it can still reach and what they are worth, best first.
    ///
    /// Remembered like everything else. It can only be read while the entity is loaded, and the
    /// question it answers - which remnant is worth walking to - is asked from the other side of
    /// the site, where it is not.
    /// </summary>
    public List<Reward> Rewards { get; set; } = new();

    /// <summary>
    /// Which revision of the reference table these rewards were priced under. See Wrt.Revision.
    ///
    /// Starts at -1 rather than 0 so a target that has never been priced cannot match a table that
    /// has never been edited.
    /// </summary>
    public int PricedUnder { get; set; } = -1;

    /// <summary>
    /// Whether this remnant's rewards were collapsed to its best option when they were priced: a must take with no
    /// reward chosen on it (see Valuation.Pinned). The list has to be priced again when that changes - the mark coming
    /// off or going on, or a reward being chosen on a must take: kept, it left a remnant un-marked with F3 holding one
    /// option, and a combination the player then set by hand could not be found in it.
    /// </summary>
    public bool PricedCollapsedToMustTake { get; set; }

    /// <summary>
    /// Which revision the "is anything about this still unpriced" answer was worked out under.
    ///
    /// **The same key the rewards use, for the same reason.** Working it out means building the key
    /// this object is filed under - eight fields joined into one string - looking it up, and doing
    /// the same for everything the object grants. That is allocations and long-string hashes, and
    /// the overlay asks it of every marker on the site every frame while the flag that draws the
    /// answer is on.
    ///
    /// Nothing can change the answer without moving the revision: editing a row moves it, and so
    /// does filing an object for the first time, since filing writes a row. See Wrt.Revision and
    /// Unknowns.Unread.
    ///
    /// Starts at -1 for the reason PricedUnder does: a target that has never been asked must not
    /// match a table that has never been edited.
    /// </summary>
    public int UnreadUnder { get; set; } = -1;

    /// <summary>What that answer was. See UnreadUnder.</summary>
    public bool UnreadAnswer { get; set; }

    /// <summary>
    /// When that answer was worked out, so it cannot outlive what the revision does not cover.
    ///
    /// **The revision is not the whole of the key, and the gap is streaming.** The answer is read
    /// through the key this object is filed under, and that key is built from its metadata, mods,
    /// art, icon, states, words, render and blessing - fields that arrive over several frames as
    /// the entity streams in. An object read early therefore has a different key from the same
    /// object read a second later, and nothing about that moves Wrt.Revision.
    ///
    /// Once Filed is set the key is fixed and the revision IS the whole key. Until then it is not,
    /// so the answer is also aged out. See Unknowns.Unread.
    /// </summary>
    public long UnreadAt { get; set; }

    /// <summary>
    /// Which reward the overlay last coloured green here, and what that answer was worked out from.
    ///
    /// **A scoring question on a drawing path, asked per remnant per frame.** Deciding which reward
    /// to green means asking what this remnant passes downstream, and that walks every marker on
    /// the site and then the whole plan - see Reroll.Downstream, which re-filters scan.At for each
    /// remnant it is asked about. Measured at 0.512ms a frame over four remnants.
    ///
    /// The answer moves when the plan moves, when the table is edited, or when the prices do. The
    /// first two are exact - a plan is one object and an edit moves the revision - and the third is
    /// a feed that refreshes every five seconds, so the age below catches it.
    /// </summary>
    public int TakeAnswer { get; set; } = -1;

    /// <summary>The plan that answer was worked out against. See TakeAnswer.</summary>
    public object TakePlan { get; set; }

    /// <summary>And the table revision. See TakeAnswer.</summary>
    public int TakeUnder { get; set; } = -1;

    /// <summary>And when, so a price that has moved shows within a second. See TakeAnswer.</summary>
    public long TakeAt { get; set; }


    /// <summary>
    /// For a remnant, the rune slots it passes on, and what could be in them. Null until read.
    ///
    /// Read once and kept, like everything else here, and for a stronger reason than most: it does
    /// not depend on NinjaPricer. The candidates come from the recipe tables, which are loaded with
    /// the game, so this is right on the first sweep whether anything is priced or not.
    ///
    /// Null rather than empty for "not asked yet", because a remnant that genuinely passes nothing
    /// on is a real answer and re-asking it every sweep for the rest of the map is not.
    /// </summary>
    public List<Passes> Passing { get; set; }

    /// <summary>
    /// For a remnant, the best and the average of everything it could become, and how many options
    /// there are. Zero options means it has not been read.
    ///
    /// Remembered with the rewards and for the same reason, and kept separately from them because
    /// the display wants the best while a judgement about rerolling wants the spread.
    /// </summary>
    public (double Best, double Mean, int Options) Spread { get; set; }

    /// <summary>The rune already in the ground, by id, and which slot it sits in. For the census.</summary>
    /// <summary>
    /// The combination this remnant was set to when its rewards were last read.
    ///
    /// **A settled list is read once and then frozen, and the thing that settles it can change.**
    /// With overruling off, choosing a reward collapses the option list to that one - and the test
    /// that triggers the re-read is "more than one option", which is false from then on. Change the
    /// pick afterwards and the plugin goes on showing the first one, for the life of the site.
    ///
    /// Empty when nothing has been chosen, which is also what a remnant nobody has touched reads as.
    /// </summary>
    public string Chose { get; set; } = "";

    public string FixedRune { get; set; } = "";

    public int FixedSlot { get; set; } = -1;

    /// <summary>
    /// The recipe the solver last picked at this remnant while no combination was chosen on it, or empty when it has
    /// never picked one. Frozen once a combination is chosen: with Overrule already chosen rewards off the solver is
    /// then pinned to the choice, so its later picks are the player's and say nothing about what it wanted. The
    /// overlay draws a chosen combination red when it differs from this. See Overlay.Remnants.
    /// </summary>
    public string SolverPickBeforeChoice { get; set; } = "";

    /// <summary>
    /// Whether this remnant is the result of a Liquid Verisium rather than of the map.
    ///
    /// The game says so outright, which is what makes the census able to keep the two populations
    /// apart instead of assuming they are the same one.
    /// </summary>
    public bool Rerolled => State("is_rerolled") > 0;

    /// <summary>
    /// Whether this remnant has been set off, after which it is scenery.
    ///
    /// A remnant already spent when it is first seen is never taken on at all - Classify refuses
    /// it. This is the other case, and the one that showed: a remnant seen alive, remembered, and
    /// then detonated. Nothing re-examined it, so its rewards, its propagation line, its reroll
    /// border and its pull on the route all outlived the remnant itself.
    ///
    /// It is deliberately NOT dropped from the scan. The spawn census watches for exactly this
    /// state to know the chain has gone off, and a remnant removed the instant it is spent is one
    /// the census can never see change.
    /// </summary>
    /// <summary>
    /// The metadata of the entity this target was made from.
    ///
    /// Kept so a later sweep can tell whether the entity it found at this cell is the same KIND of
    /// thing. Two entities routinely share a grid cell - every remnant has a RuneEncounterController
    /// standing exactly on it - and the record is keyed by cell.
    /// </summary>
    public string Meta { get; init; } = "";

    public bool Spent =>
                         // A strongbox no blast can touch. Spent is "nothing left for an explosive
                         // to do with it", which is exactly true of one that was never diggable -
                         // and saying it here rather than in each caller is what stops it being
                         // half done. It was weighted at nothing and still drew a marker, because
                         // the drawing asks this and not the weights. See Target.Explodes.
                         Kind == TargetKind.Strongbox && !Explodes ||
                         Kind == TargetKind.Remnant && State("activated") >= 6 ||
                         Kind == TargetKind.Sentry && State("activated") >= 1 ||
                         Kind == TargetKind.Entrance && State("expedition_detonated") >= 1 ||
                         // Anything with a blast of its own that has already blown - a barrel, or
                         // one of the tileset explodables the sweep files under Unknown. The
                         // classifier refuses one at the sweep, but a marker remembered from an
                         // earlier lap comes back without being re-read, so the question has to be
                         // askable here too. See Target.Sets.
                         Sets > 0f && State("expedition_detonated") >= 1 ||
                         Kind == TargetKind.Strongbox &&
                         (State("expedition_detonated") >= 1 || State("opened") >= 1);

    /// <summary>
    /// Whether this remnant has been blown but not yet shattered - the only state worth walking to.
    ///
    /// Six exactly, not six or more. The state carries on past the detonation: across every dump
    /// taken, remnants read 6 fifty six times and 7 seventy four times, and the sevens are all in
    /// dig sites that were finished with. So six is "blown, still standing, still offering its
    /// shatter button" and seven is "already shattered" - and testing for six-or-more had the loot
    /// count including every remnant ever dealt with and the lines pointing at them.
    ///
    /// Spent stays as six-or-more, because for everything else - drawing, planning - a shattered
    /// remnant and a blown one are equally finished.
    /// </summary>
    public bool Shatterable => Kind == TargetKind.Remnant && State("activated") == 6;

    /// <summary>Whether this chest has been opened, after which there is nothing to click.</summary>
    public bool Opened => Kind == TargetKind.Chest &&
                          Safe.Read(Entity, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false);

    /// <summary>What that said last time it was looked at, so a change can be noticed.</summary>
    public bool WasRolled { get; set; }

    /// <summary>Whether the game had this loaded at the last sweep. See Streaming.</summary>
    public bool WasLive { get; set; }

    /// <summary>
    /// For a remnant: true when it belongs to no dig site, false when it is confirmed to belong to the one it is filed
    /// under, null while nobody knows. Settled by picking a recipe and reading what the game does - see
    /// Scan.SettleLoneRemnants. Saved with the site. A lone remnant is left out of every dig site's targets.
    /// </summary>
    public bool? Lone { get; set; }

    /// <summary>When a recipe was first seen picked on this remnant with it still at activated 1. See Scan.SettleLoneRemnants.</summary>
    public DateTime? PickedWhileIdleSince { get; set; }

    /// <summary>
    /// Throws away everything read about what this remnant offers.
    ///
    /// Called when it stops being the same remnant. A Liquid Verisium replaces it outright - new
    /// rune layout, new socket count, a different set of rewards - and everything here is cached
    /// once and kept, on the reasoning that a remnant's options are fixed when the site is
    /// generated. That reasoning is right up until somebody rolls it, and then every cached answer
    /// describes a remnant that no longer exists: the old reward over the new one, advice about a
    /// price that has changed, a propagating rune that may not be there any more.
    ///
    /// The position is not cleared, because that has not changed and is what identifies it.
    /// </summary>
    public void Forget()
    {
        Rewards = new List<Reward>();
        Spread = default;
        Passing = null;
        FixedRune = "";
        FixedSlot = -1;
    }

    /// <summary>Which dig site this belongs to, held as that site's detonator position.</summary>
    public Vector2 Site { get; set; }

    /// <summary>The live entity while the game has it loaded, and null once it does not.</summary>
    public Entity Entity { get; set; }

    /// <summary>
    /// The second entity standing on this cell, where the thing is two entities. See Holds.
    ///
    /// **A strongbox is a mound and a box, and either may be the one lit.** The mound is the terrain
    /// fill a blast breaks and the box is the content inside it; they share a cell, and whichever
    /// the sweep met first became Entity, with the other held here. Reading only one half gets the
    /// lit state wrong both ways: one dump had glow_epk=1 on the chest and 0 on the mound, and
    /// another (2026-10-02, a box on the edge of a blast at (758,873)) had 1 on the mound and 0 on
    /// the chest. The placement check then reported the box unlit and called the blast the wrong
    /// one. So Glowing asks both. See Glowing and Scan.Attach.
    /// </summary>
    public Entity Held { get; set; }

    private Vector3 _world;

    /// <summary>
    /// Where this stands in the world, read once and kept.
    ///
    /// A marker never moves and the ground under it never changes height, so this is a constant
    /// dressed up as a memory read - and it was being read once per marker per frame, plus again by
    /// whatever else wanted the same answer.
    ///
    /// Live entities give it directly. One the game has unloaded falls back to the terrain height at
    /// its remembered grid position, which is the same answer by a longer route.
    /// </summary>
    /// <summary>
    /// How far up or down every drawn marker is moved, in world units. See
    /// DebugSettings.MarkerHeightOffset, which is the only thing that sets it.
    ///
    /// A static because Where takes a GameController and nothing else, and threading settings through
    /// twelve call sites to carry one diagnostic figure would be worse than this. Overlay.Draw
    /// refreshes it once a frame.
    /// </summary>
    public static float MarkerHeightOffset;

    /// <summary>
    /// How far every drawn marker is moved along the object's own facing, in world units. See
    /// DebugSettings.MarkerFacingOffset.
    /// </summary>
    public static float MarkerFacingOffset;

    /// <summary>
    /// How far every drawn marker is moved across the object's facing, in world units. See
    /// DebugSettings.MarkerSidewaysOffset.
    /// </summary>
    public static float MarkerSidewaysOffset;

    /// <summary>
    /// Whether a marker sits at the object's interact centre rather than the position it reports.
    ///
    /// **Measured over 170 entities: InteractCenter is exactly Render.Pos plus half the bounds, with
    /// no deviation and no rotation applied.** It is derived rather than observed, so it adds no new
    /// reading - but what it encodes is a claim worth testing, that the reported position is a CORNER
    /// of the bounding box and the object's middle is half the bounds away. That is per object and a
    /// global slider cannot express it: half the bounds is 20.7 world on an ordinary marker and 96.7
    /// on a sub-area cap.
    ///
    /// A switch rather than a correction, because it moves every marker at once and the ordinary ones
    /// look right where they are. If the caps line up under it and the markers stop lining up, the
    /// claim is true of large objects only and belongs in the extent table, not here.
    /// </summary>
    public static bool MarkersAtInteractCentre;

    /// <summary>
    /// Which way this object is turned, in radians, or nought where it cannot be read.
    ///
    /// Read live rather than kept, because the offset it feeds is a diagnostic being dragged about -
    /// a cached rotation would need the cache clearing to try a value. Positioned.Rotation is what
    /// ExpeditionIcons rotates the detonator's exclusion rectangle by, in quarter turns.
    /// </summary>
    public float Facing => Safe.Read(Entity, static e => e.GetComponent<Positioned>()?.Rotation ?? 0f, 0f);

    /// <summary>
    /// The diagnostic offsets applied to a position on its way to being drawn.
    ///
    /// Both are nought unless somebody has dragged them, so this is the identity in ordinary use. The
    /// facing offset is rotated by the object's own Positioned.Rotation, which is the point of it: the
    /// residual gap on a sub-area cap changes with how the cap is turned, and a fixed X or Y could not
    /// fit two caps facing different ways while one distance along the facing can.
    /// </summary>
    private Vector3 Shifted(Vector3 world)
    {
        if (MarkerHeightOffset == 0f && MarkerFacingOffset == 0f && MarkerSidewaysOffset == 0f)
            return world;

        // **Two components, because one could not describe the gap.** A single distance along the
        // facing was tried first and could not be made to fit: the offset has a sideways part as well,
        // which is what a corner anchor would give - the centre of a square footprint is along its
        // DIAGONAL from a corner, so equal parts along and across.
        //
        // The pair is the offset in the object's own frame, turned into world by the standard
        // rotation. Facing is read live, so both sliders answer on the next frame.
        var turn = MarkerFacingOffset == 0f && MarkerSidewaysOffset == 0f ? 0f : Facing;
        var cos = MathF.Cos(turn);
        var sin = MathF.Sin(turn);

        return new Vector3(
            world.X + cos * MarkerFacingOffset - sin * MarkerSidewaysOffset,
            world.Y + sin * MarkerFacingOffset + cos * MarkerSidewaysOffset,
            world.Z + MarkerHeightOffset);
    }

    public Vector3 Where(GameController gc)
    {
        // **The terrain answer is a stand-in for an entity that has not arrived, and must not outlive
        // it.**
        //
        // This cached on the first call and never asked again. A marker does not move, so that looked
        // safe, and it is not: the two branches do not agree. An object first asked about from across
        // the map has no entity yet, so it was filed at the terrain height under its cell and kept
        // that for the session - and the terrain is not always where the object is. The catacomb cap
        // reads 226.6 there against its entity's 30.4, and the art is at the entity's figure.
        //
        // **This is why four changes to the entity branch moved nothing.** For a target filed from a
        // distance that branch never ran again, so editing it, simplifying it away and putting it back
        // all produced the same drawn position. What gave it away was the diagnostic height slider
        // working while the base value would not budge: the slider is applied after the cache and the
        // branches are before it.
        //
        // So the entity is asked on every call until it answers once, and the answer is kept from then
        // on. One read while the object is out of range, nothing at all afterwards.
        // The switch changes what the answer IS, so a cached one taken under the other setting has
        // to go. See MarkersAtInteractCentre.
        if (_centred != MarkersAtInteractCentre)
        {
            _centred = MarkersAtInteractCentre;
            _fromEntity = false;
            _world = Vector3.Zero;
        }

        if (!_fromEntity)
        {
            // **Only where the game actually has the object, because asking a null one THROWS.**
            //
            // Safe.Read runs the lambda and catches, so reading Pos off a null Entity raises a null
            // reference and swallows it - and a caught exception is an object plus a captured stack
            // trace, not a cheap branch. Asking unconditionally, which is what this did for one
            // commit, cost 188 throws a frame: 8.6% of every read the plugin made, against nought
            // before it, and it put 633 extra reads and about 1.8ms on each frame. A retry has to be
            // free for the hundreds of targets whose entity never loads, and a null check is.
            var at = Live
                ? MarkersAtInteractCentre
                    ? Safe.Read(Entity, static e => e.GetComponent<Render>()?.InteractCenter ?? Vector3.Zero,
                        Vector3.Zero)
                    : Safe.Read(Entity, static e => e.Pos, Vector3.Zero)
                : Vector3.Zero;

            // The mound over a buried strongbox reports the world origin; the box on the same cell
            // has a real position. See Held.
            if (at == Vector3.Zero && Held != null)
                at = Safe.Read(Held, static e => e.Pos, Vector3.Zero);

            if (at != Vector3.Zero)
            {
                _world = at;
                _fromEntity = true;
            }
            else if (_world == Vector3.Zero)
            {
                _world = Safe.Read((gc, Grid),
                    static x => x.gc.IngameState.Data.ToWorldWithTerrainHeight(x.Grid), Vector3.Zero);
            }
        }

        return _world == Vector3.Zero ? Vector3.Zero : Shifted(_world);
    }

    /// <summary>
    /// Whether _world came from the object itself rather than from the terrain under its cell.
    ///
    /// The terrain figure is provisional - see Where - so this is what says whether the cached answer
    /// is the real one or a placeholder still waiting to be replaced.
    /// </summary>
    private bool _fromEntity;

    /// <summary>What MarkersAtInteractCentre was when _world was worked out. See Where.</summary>
    private bool _centred;

    /// <summary>Whether the game currently has this loaded.</summary>
    public bool Live => Entity != null;

    /// <summary>
    /// Whether the game is lighting this up for the explosive under the cursor.
    ///
    /// Only answerable while the entity is loaded, which costs nothing: the question is only ever
    /// asked of markers near the cursor, and those are exactly the ones the game is keeping.
    /// </summary>
    private int _glowFrame = -1;
    private bool _glow;

    /// <summary>
    /// Whether the game is lighting this up for the explosive under the cursor.
    ///
    /// Cached for one frame, because it is asked several times over: by the ring, by the count, by
    /// the extent measuring and by the site confirmation. Each ask is a component read plus a
    /// walk of the state list, and with eighty odd markers that was hundreds of reads a frame for
    /// an answer that cannot change between two draws.
    /// </summary>
    /// <summary>
    /// Whether an explosive already placed will take this, by the client's "in_range" state.
    ///
    /// Read off one dump on Sloughed Gully (2026-09-30) with two explosives down: all twenty objects
    /// reading in_range=1 were within 36.4 grid of one of them, except a Devourer boss egg at 44.1 whose
    /// size carries it in, and none of them also read in_placing_range. So something a placed explosive
    /// already has does not light under the placement circle, and its not lighting says nothing about
    /// the circle. See Placement.Coverage.
    /// </summary>
    public bool InPlacedBlast => Live && State("in_range") == 1;

    /// <summary>
    /// Whether this live marker's highlight can be read at all: glow_epk present on it or on its pair. A marker the
    /// client has only half loaded lists no states, and Glowing then reads false whatever the client draws - on a
    /// Grazed Prairie site (2026-10-06) a monster marker 16 grid inside the blast, with no states, no art and no
    /// rotation readable, was reported unlit while its three neighbours listed their states. See Placement.Coverage.
    /// </summary>
    public bool GlowReadable => Live && (State("glow_epk") >= 0 || Stated(Held, "glow_epk") >= 0);

    public bool Glowing
    {
        get
        {
            if (_glowFrame == Frame.Number)
                return _glow;

            _glowFrame = Frame.Number;

            // Either half: a strongbox is a mound plus a box, and either may be the one lit. See Held.
            _glow = Live && (State("glow_epk") == 1 || Stated(Held, "glow_epk") == 1);

            return _glow;
        }
    }

    /// <summary>
    /// Every state this object has, by name, sorted and joined.
    ///
    /// **The names are the classification; the values are its mood.** A thing carrying be_free is an
    /// encased monster whether it is free yet or not, and a thing carrying sockets is a remnant
    /// whether anything is socketed. So the set of names is a property of the model and belongs in
    /// the key, where the numbers beside them do not.
    ///
    /// Sorted, because the list comes back in memory order and nothing promises that is stable -
    /// unsorted, the same object keys two different ways at random.
    /// </summary>
    public string StateNames()
    {
        // **Read once and kept, like the art and the rarity beside it.**
        //
        // The game unloads what is behind you, so this answers differently depending on whether the
        // object happens to be in memory - and a key built on it therefore had two spellings for
        // every object, one with states and one without. Measured: every row in the table appeared
        // twice, the second a hollow copy of the first.
        //
        // Only a successful read is cached. An empty answer is "not loaded just now" rather than
        // "has no states", and caching that would make the hollow spelling permanent.
        if (_states.Length > 0)
            return _states;

        var states = Safe.Read(Entity, static e => e.GetComponent<StateMachine>()?.States, null);

        if (states == null)
            return "";

        var names = new List<string>(states.Count);

        for (var i = 0; i < states.Count; i++)
        {
            var name = Safe.Read(states[i], static x => x.Name, null);

            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);

        return _states = string.Join(",", names);
    }

    private string _states = "";

    /// <summary>
    /// The model's own name for its art, where it has one. Kept once read, for the reason on
    /// StateNames. See Unknowns.Key.
    /// </summary>
    public string Rendered
    {
        get
        {
            if (_rendered.Length > 0)
                return _rendered;

            return _rendered = Safe.Read(Entity, static e => e.GetComponent<Render>()?.Name, null)
                               ?? "";
        }
    }

    private string _rendered = "";

    /// <summary>
    /// The words on this thing's ground label, with the numbers taken out.
    ///
    /// **Normalised, or every strongbox in the game is its own kind of object.** The label says
    /// "guarded by 9 packs of monsters" and the nine is the instance rather than the kind, so a key
    /// built on the raw text would file a row per pack count - and the same goes for sockets, stack
    /// sizes and prices. Digits collapse to a hash so the sentence survives and the number does not.
    /// </summary>
    /// <summary>
    /// How far this thing's own explosion reaches, in grid units, or nought when it has none.
    ///
    /// Read from inherent_explosion_radius, which the game states per object. Nought on almost
    /// everything in a dig site, and non-nought is the game saying this object goes off on its own
    /// and takes what stands near it - which is what the chain closure and the blast rings are
    /// about. Measured in Stagnant Basin: 80 on FaridunExplosive, 110 on OilWell, nought on the
    /// other 148 objects that stated one at all.
    ///
    /// **The unit is grid**, on ExpeditionIcons' evidence rather than on anything measured here. It
    /// hard-codes 75 grid for the Gallows boom barrel and the Faridun explosive and 140 for the oil
    /// derrick, beside an explosive's own 34 that it measured by touching circles - so an object's
    /// own blast really is two to four times an explosive's, and the readings here of 60, 80 and 110
    /// are the same figures in the same unit.
    ///
    /// **They do not agree exactly, and nothing here explains the gap.** That plugin uses one figure
    /// for the two barrels where the game states 60 for the Gallows one and 80 for the Faridun, and
    /// 140 for the derrick where the game states 110. Its base radius carries a note saying how it
    /// was measured and these three do not, so they may be read off the screen rather than measured;
    /// the state is the game's own number and reads every object rather than the three somebody
    /// hard-coded. Worth settling, since 110 against 140 is a fifth of a derrick's blast.
    /// </summary>
    public float Sets
    {
        get
        {
            // **Read once from a live entity, then latched - including a latch on nought.**
            //
            // A marker outlives the entity it was read from - Remembered writes the site to disk so
            // a reload does not cost a lap - and a field added after those files were written comes
            // back empty for every marker in them. Measured: eighty one barrels restored, every one
            // reporting a radius of nought, so every circle was drawn at nothing and the closure
            // reached nothing either. So a nought that came off the disk still has to be asked again.
            //
            // A nought the state itself gave is a different thing and is kept, because this is asked
            // of every target on every drawing frame rather than of the handful the classifier had
            // already named barrels. Without the latch each of the several hundred objects that state
            // a radius of nought would walk its state list once a frame to say so again.
            if (_setsRead || Entity == null)
                return _sets;

            var stated = State("inherent_explosion_radius");

            // -1 is the state absent or the component unreadable, which is not a radius of nought:
            // keep asking until the entity answers one way or the other.
            if (stated < 0)
                return _sets;

            _setsRead = true;

            return _sets = stated;
        }

        set
        {
            _sets = value;

            // A radius restored from disk is an answer; a nought from disk is the field never having
            // been written, so it leaves the latch open for a live entity to settle.
            _setsRead = value > 0f;
        }
    }

    private float _sets;

    private bool _setsRead;

    public string Words { get; set; } = "";

    /// <summary>
    /// The crafted lines on this thing's label - what it actually grants.
    ///
    /// **Two Vaal relics are the same object with different text, and the text is the whole of the
    /// difference.** One gives 40% increased rarity of items in excavated chests, another 50%
    /// increased number of rare monsters, another duplicates runic monsters - the same model, the
    /// same art, the same states, and worth wildly different amounts. Nothing but the words
    /// separates them.
    ///
    /// The game marks them: an upside is written as an enchantment and a downside is not, so the
    /// enchanted lines are exactly the good half and the rest can be left alone. Read in the same
    /// sweep as Words, normalised the same way so a percentage does not make a new kind of thing.
    /// </summary>
    public string Blessing { get; set; } = "";

    /// <summary>
    /// When this target was first classified, which is what stops it being filed too early.
    ///
    /// **The key is built from fields that do not arrive together.** The states come off the entity
    /// the moment it is readable; the words on its ground label come from a sweep that runs a couple
    /// of times a second and only visits cells that have a label. So an object filed the instant it
    /// was classified went into the table under a key with no label in it, and again a moment later
    /// under one with - two rows for one thing, and the row on screen not the row in the settings.
    ///
    /// Waiting a beat costs nothing. Nothing is planned differently for an object that is filed two
    /// seconds late, and everything that names it has arrived by then.
    /// </summary>
    public DateTime Born { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The key this was filed under. See Born.
    ///
    /// Held so that the row an object went into is the row it stays in - the key is worked out from
    /// eight fields and several of them can flicker, so recomputing it every sweep would file one
    /// object under a handful of rows as the reads came and went.
    ///
    /// **One exception, and it is the name arriving.** A ground label is drawn when the player walks
    /// near, not when the entity loads, so an object first seen from across the site is filed before
    /// anything has named it. That is not a flicker, it is information that was not there before, so
    /// the label read re-files the object and drops the nameless row. See Unknowns.Refile.
    /// </summary>
    public string Filed { get; set; } = "";

    /// <summary>Whether enough time has passed to file this, and everything that names it is in.</summary>
    public bool Settled => DateTime.UtcNow - Born > TimeSpan.FromSeconds(2);

    /// <summary>
    /// One StateMachine value, without allocating to ask.
    ///
    /// The state list is walked by hand rather than with LINQ, and the read takes its state as an
    /// argument rather than capturing it - both to keep this allocation free, because it is called
    /// for every marker on every frame that draws.
    /// </summary>
    public long State(string name)
    {
        LeafCalls.StateReads++;

        return Stated(Entity, name);
    }

    /// <summary>The same, of a named entity, so the other half of a pair can be asked. See Held.</summary>
    /// <summary>
    /// One state's value on any entity, or -1 where the state is absent or unreadable.
    ///
    /// Internal rather than private because the scan asks it of an entity it has not made a Target
    /// for yet - whether the client lights a thing under the placement circle is part of deciding
    /// whether the thing is ours at all. One rule, one reader. See Scan.WaitFor.
    /// </summary>
    internal static long Stated(Entity of, string name)
    {
        if (of == null)
            return -1;

        var states = Safe.Read(of, static e => e.GetComponent<StateMachine>()?.States, null);

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

/// <summary>
/// What is in the dig site, and what was in it before you walked away.
///
/// Two things this has to get right that a plain per-frame sweep does not.
///
/// **It must not forget.** The game only keeps entities near the player loaded, and a dig site is
/// routinely several screens across - so a tally built from what is loaded empties itself as you
/// cross to the far side, which is precisely when you wanted it. Markers never move and none appear
/// after the site is generated, so anything once seen is remembered until the area changes. A Grand
/// Expedition - one large connected site with fifteen or more explosives - would be unusable any
/// other way.
///
/// **It must not merge two sites.** A map can hold more than one encounter and they sit far apart.
/// Each marker is filed under the detonator it is nearest to, which is safe precisely because they
/// are far apart, so the overlay and the planner can work on the site you are standing in without
/// the other one's content inflating the count or dragging the chain across the map.
///
/// The other thing worth understanding: none of the content exists as an entity while the chain is
/// being planned. A site holds identical `ExpeditionMarker` objects and a few `Expedition2Encounter`
/// remnants - the monsters arrive when the explosives go off and the chests only once the encounter
/// is cleared. What a marker stands for is the art it wears.
/// </summary>
internal sealed class Scan
{
    private const string MarkerMetadata = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
    /// <summary>
    /// What a remnant's metadata starts with.
    ///
    /// Internal because two other things need to ask it: the button paths on a ground label are a
    /// REMNANT'S button paths, and applying them to any label that happens to say "Expedition" is
    /// what made a strongbox's label read as one enormous button.
    /// </summary>
    internal const string RemnantMetadata = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    /// <summary>
    /// Where a dormant Verisium Sentry lives, which is with the scenery rather than the markers.
    ///
    /// NOT `Metadata/Monsters/LeagueExpeditionNew/Sentinels/KalguurDrone`. That is the drone the
    /// sentry has already become - it is what a woken sentry walks around as, and by the time it
    /// exists there is nothing left to plan for. The thing a blast can catch is this object, which
    /// sits in the site alongside the doodads and the detonator before anything goes off.
    /// </summary>
    private const string SentryMetadata = "Metadata/MiscellaneousObjects/Sentinel/";

    /// <summary>Everything ever seen in this area, keyed on where it stands.</summary>
    private readonly Dictionary<(int X, int Y), Target> _known = new();

    /// <summary>
    /// Whether a remnant has been rolled since anybody last asked.
    ///
    /// **Consumed rather than read**, because it exists to start exactly one re-solve. Left as a
    /// plain flag it would start a fresh one every tick until something happened to clear it, which
    /// is a solve per frame for a single roll.
    /// </summary>
    private bool _rolled;

    /// <summary>
    /// Whether a roll is waiting for a solve, without consuming it.
    ///
    /// For the one caller that has to act BEFORE the solve is dispatched: a presolve in flight
    /// blocks the dispatch, and stopping it is what lets the roll be answered now rather than when
    /// its window runs out. See AutoExpedition, where the stop is.
    /// </summary>
    public bool RollPending => _rolled;

    /// <summary>Whether a remnant was rolled since this was last called. Clears as it answers.</summary>
    public bool TakeRolled()
    {
        var was = _rolled;

        _rolled = false;

        return was;
    }

    private readonly GameController _gc;
    private uint _area;
    private DateTime _swept = DateTime.MinValue;

    /// <summary>When the whole entity list was last walked. See Tick and Missed.</summary>
    private DateTime _walked = DateTime.MinValue;

    /// <summary>How long between backstop walks of everything loaded. See Tick.</summary>
    /// <summary>
    /// How long between full walks.
    ///
    /// **Ten seconds was sized for a net, and the walk turned out to be the route.** A marker's
    /// art becomes readable when the player walks up to it, and the walk is the only thing that
    /// looks again - an entity handed to a callback may be remade as it streams, so a handle kept
    /// across ticks reads a dead address and a retry queue built on one rescued nothing at all.
    /// Ten seconds was therefore ten seconds of a marker not existing to the planner, which is
    /// what "unknown until I have stood near it a while" was.
    ///
    /// A second is affordable in a way it was not before: a cell already on record is skipped, and
    /// so is anything the membership test has already refused, so a repeat walk pays an id read
    /// for most of what it touches rather than a classify. The pass that measured 9.2ms did
    /// neither - it re-derived every rejection, every time.
    /// </summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(1);

    public Scan(GameController gc) => _gc = gc;

    /// <summary>Everything remembered, whichever site it belongs to.</summary>
    public List<Target> Targets => _known.Values.Where(t => !t.Ignored).ToList();

    private Vector2 _atSite = new(float.NaN, float.NaN);
    private int _atCount = -1;
    private int _atLone = -1;
    private List<Target> _at = new();

    /// <summary>Counts the lone remnant verdicts given, so the per-site list is rebuilt when one changes.</summary>
    private int _loneVerdicts;

    /// <summary>
    /// What belongs to one dig site, named by that site's detonator position.
    ///
    /// Cached, because the answer only changes when a marker is added or the site does - neither of
    /// which happens between frames. Built fresh it walks every known marker with a square root
    /// each and allocates a list, an iterator and a closure, once a frame, to arrive at the list it
    /// had last frame.
    /// </summary>
    /// <summary>
    /// Where everything the scan knows stands, whichever expedition it belongs to.
    ///
    /// **Every marker, not one kind.** This was remnants only, on the reasoning that they are what
    /// streams map-wide while monsters and chests are only remembered once you have been near them -
    /// and being remembered is exactly as good for this. A marker known from a previous lap, or one
    /// that streamed in on its own, says the same thing either way: there was expedition content
    /// here, so there may be more beside it.
    ///
    /// Relics were the case that showed it. They carry as far as remnants do - eighteen of them
    /// known on a map with none of them loaded - and every one was being ignored, so a site's relics
    /// added nothing to the ground worth searching.
    ///
    /// The same strongbox rule as Standing, and for the same reason: a box no blast can touch is not
    /// expedition content wherever it is standing. See Target.Explodes.
    ///
    /// Not filtered to a site on purpose: the point of asking is the site you have NOT walked to.
    /// See Scouted.Wanted and Display.UnscoutedGround.ShowUnscoutedFar.
    /// </summary>
    public List<Vector2> Landmarks()
    {
        var found = new List<Vector2>();

        foreach (var target in _known.Values)
        {
            if (target.Kind == TargetKind.Strongbox && !target.Explodes)
                continue;

            found.Add(target.Grid);
        }

        return found;
    }

    /// <summary>
    /// Where this site's expedition markers stand, for the ground they say there is more of.
    ///
    /// Spent ones included - a shattered remnant is still evidence that the expedition extends over
    /// that patch of ground, which is the only thing this answer is used for. See Scouted.Wanted.
    ///
    /// **A strongbox a blast cannot touch is not expedition content and must not extend the site.**
    /// An ordinary Blacksmith's Strongbox standing in a map has nothing to do with the dig, so a red
    /// patch appeared around one that happened to be within the site's radius, claiming unsearched
    /// expedition ground where there was none.
    ///
    /// It is the same trap Target.Explodes was written for, and the same answer: every strongbox
    /// carries glow_epk, because that is how the client lights things under the placement circle, so
    /// that state separates nothing. inherent_explosion_radius is what does - a buried strongbox has
    /// it and one standing in the open does not - and Explodes is that state, read once at
    /// classification and kept.
    ///
    /// Nothing else is filtered. The other kinds are recognised by expedition metadata, and even the
    /// Unknown bucket is found by the state the game sets on things an explosive acts on - so its
    /// members belong to the dig whatever else is unclear about them.
    /// </summary>
    public List<Vector2> Standing(Vector2 site)
    {
        var found = new List<Vector2>();

        foreach (var target in At(site))
        {
            if (target.Kind == TargetKind.Strongbox && !target.Explodes)
                continue;

            found.Add(target.Grid);
        }

        return found;
    }

    /// <summary>
    /// This site's markers, as far as a chain could actually walk through them.
    ///
    /// **Filing is by nearest detonator, and nearest has no limit.** A remnant standing on its own
    /// - and maps are full of those - is adopted by whichever site is least far away, however far
    /// that is. Seen on a walked-out site: a remnant 570 grid from the detonator, activated 0 where
    /// every real one read 1, filed here because the only other detonator was further still.
    ///
    /// A chain travels in hops of one reach, so the site is what can be strung together in those
    /// hops: a marker joins if it is within a reach of the detonator, or within a reach of a marker
    /// that has already joined. Nothing beyond the last hop can be linked to whatever it is filed
    /// under, and the answer grows on its own as markers stream in - a gap that looks unbridgeable
    /// from the boundary closes the moment something lands in the middle of it.
    ///
    /// **A grown set rather than a radius.** A radius from the detonator is a number somebody
    /// picked; this is the rule the chain is built under, so a sprawling site keeps all of itself
    /// and a lone marker across the map keeps none.
    /// </summary>
    public List<Vector2> Joined(Vector2 site, float reach)
    {
        var markers = Standing(site);
        var joined = new List<Vector2>();

        if (site == Vector2.Zero || reach <= 0f || markers.Count == 0)
            return joined;

        // Which have joined already, so one is not added twice and the front cannot grow for ever.
        // Not "taken" - Planning.Taken means an explosive on the ground has claimed a marker, and
        // that is a different thing about the same objects.
        var joinedAlready = new bool[markers.Count];
        var front = new List<Vector2> { site };

        while (front.Count > 0)
        {
            var next = new List<Vector2>();

            for (var i = 0; i < markers.Count; i++)
            {
                if (joinedAlready[i])
                    continue;

                foreach (var from in front)
                {
                    if (Vector2.Distance(from, markers[i]) > reach)
                        continue;

                    joinedAlready[i] = true;
                    joined.Add(markers[i]);
                    next.Add(markers[i]);

                    break;
                }
            }

            front = next;
        }

        return joined;
    }

    private int _atRevision = -1;

    public List<Target> At(Vector2 site)
    {
        if (site == _atSite && _known.Count == _atCount && _loneVerdicts == _atLone && Wrt.Revision == _atRevision)
            return _at;

        _atSite = site;
        _atCount = _known.Count;
        _atLone = _loneVerdicts;
        _atRevision = Wrt.Revision;

        // A remnant confirmed to belong to no dig site is nobody's content, and nor is anything the table says to
        // ignore. See Target.Lone and Target.Ignored.
        _at = _known.Values.Where(t => Vector2.Distance(t.Site, site) < 1f && t.Lone != true && !t.Ignored).ToList();

        return _at;
    }

    /// <summary>
    /// Settles which remnants belong to no dig site, from what the game does when a recipe is picked.
    ///
    /// **A remnant attached to a dig site moves its "activated" state from 1 to 2 when a recipe is picked; a lone one
    /// stays at 1 and offers its shatter button, which starts its encounter.** Measured on one map (2026-09-30): six
    /// picks on site remnants each read 2 at the next record, and the lone remnant read 1 with "Perfect Chaos Orb"
    /// picked at about a dozen records over seventy minutes, then 5, 6 and 7 once shattered. One lone remnant, so a
    /// pattern rather than a rule. A pick is only judged lone after LoneAfter at 1, in case the state lags the pick.
    /// Nothing is ever judged without a pick, so an unsettled remnant stays in the plan. See Target.Lone.
    /// </summary>
    /// <param name="picked">The name of the recipe picked on a loaded remnant, or empty. See Valuation.ChosenName.</param>
    public void SettleLoneRemnants(Func<Entity, string> picked)
    {
        // A Grand Expedition has no lone remnants. See Detonator.Grand.
        if (Detonator.Grand(_gc))
            return;

        foreach (var target in _known.Values)
        {
            if (target.Kind != TargetKind.Remnant || target.Lone != null || !target.Live)
                continue;

            var name = Safe.Read(() => picked(target.Entity), "") ?? "";
            var activated = target.State("activated");

            if (name.Length == 0)
            {
                target.PickedWhileIdleSince = null;

                continue;
            }

            if (activated is 2 or 3)
            {
                target.Lone = false;
                _loneVerdicts++;

                continue;
            }

            if (activated != 1)
                continue;

            target.PickedWhileIdleSince ??= DateTime.UtcNow;

            if (DateTime.UtcNow - target.PickedWhileIdleSince.Value < LoneAfter)
                continue;

            target.Lone = true;
            _loneVerdicts++;
        }
    }

    /// <summary>How long a picked remnant must stay at activated 1 before it is judged lone. See SettleLoneRemnants.</summary>
    private static readonly TimeSpan LoneAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether a remnant not yet settled has no other expedition marker near it, which is what a lone remnant looked
    /// like on the one map it was seen on: its nearest neighbours were ordinary monsters, not markers. A suspicion
    /// only, for pointing the player at it to pick a recipe; it changes nothing the plan does. Never on a Grand
    /// Expedition, which has no lone remnants. See SettleLoneRemnants.
    ///
    /// **Only once the site's detonator panel is filled in**, which the game does when the player is at the site.
    /// Remnants are readable from across the map and other markers are not: from 371 grid off a site the scan held
    /// seven remnants and two other markers, so every remnant of the site read as having no marker near it (Flotsam,
    /// 2026-10-01). A regular site is small enough that its markers have loaded by the time the panel is filled in.
    /// </summary>
    public bool MaybeLone(Target remnant) =>
        remnant.Kind == TargetKind.Remnant && remnant.Lone == null && !Detonator.Grand(_gc) &&
        Detonator.PanelReady(_gc) && NearestMarkerTo(remnant) > LoneMarkerGap;

    /// <summary>
    /// How far the nearest other expedition marker - monster, rare, chest or relic - is from a target, or infinity for
    /// none.
    /// </summary>
    public float NearestMarkerTo(Target target)
    {
        var nearest = float.PositiveInfinity;

        foreach (var other in _known.Values)
        {
            if (ReferenceEquals(other, target) ||
                other.Kind is not (TargetKind.Monster or TargetKind.Elite or TargetKind.Chest or TargetKind.Relic))
                continue;

            nearest = MathF.Min(nearest, Vector2.Distance(other.Grid, target.Grid));
        }

        return nearest;
    }

    /// <summary>
    /// How far a remnant's nearest other expedition marker must be for it to be suspected lone, in grid: two blast
    /// radii. Chosen, not measured; the dump prints each remnant's nearest marker so it can be checked. See MaybeLone.
    /// </summary>
    private const float LoneMarkerGap = 60f;

    /// <summary>What is known of a remnant's dig site, in words, for the dump.</summary>
    public string MembershipSaid(Target remnant) =>
        Detonator.Grand(_gc) && remnant.Lone == null
            ? "Grand Expedition, which has no lone remnants"
            : remnant.Lone switch
        {
            true => "LONE (a recipe picked left it at activated 1) - left out of the site",
            false => "belongs to the site (a recipe picked moved it to activated 2)",
            _ => $"not settled, nearest marker {NearestMarkerTo(remnant):0.#} grid" +
                 (MaybeLone(remnant) ? " - MAY BE LONE, pick a recipe to find out"
                     : !Detonator.PanelReady(_gc) ? " (not judged until the site's panel is filled in - its markers may not have loaded)"
                     : ""),
        };

    /// <summary>How many separate dig sites have been seen in this area.</summary>
    public int Sites => _known.Values.Select(t => (t.Site.X, t.Site.Y)).Distinct().Count();

    /// <summary>
    /// Files every lit marker under the site that lit it, which is proof rather than proximity.
    ///
    /// Nearest-detonator is a good guess and is what ExileCore2 itself does - the detonator element
    /// resolves its encounter by proximity too - but it is still a guess, and two sites that happen
    /// to sprawl towards each other would have markers filed under the wrong one.
    ///
    /// The glow settles it. A marker only lights up for the encounter it belongs to, so anything
    /// lit while this site's indicator is down is that site's, whatever the distance said. Costs
    /// nothing: the markers are already being asked whether they glow, for the tally.
    /// </summary>
    public void Confirm(Vector2 site)
    {
        if (site == Vector2.Zero)
            return;

        foreach (var known in _known.Values)
        {
            if (!known.Glowing || known.Site == site)
                continue;

            known.Site = site;

            // The per-site list is cached on the marker count, which does not change when one is
            // refiled. Nudging it here is what stops the correction being invisible until the next
            // marker turns up.
            _atCount = -1;
        }
    }

    /// <summary>How many of the remembered markers the game still has loaded.</summary>
    public int LiveCount => _known.Values.Count(t => t.Live);

    /// <summary>
    /// The remnants of a site that are remembered but not currently loaded, and the total.
    ///
    /// **The one part of "is this site fully explored" that can actually be answered.** Nothing says
    /// how many markers a dig site has until they have all streamed, so "all of it is in range" is
    /// not a knowable statement. What IS knowable is that something we have already seen is out of
    /// range now - and for remnants that is the whole game, because a remnant which cannot be read
    /// carries no rewards, no runes and therefore no propagation, which is most of a chain's worth.
    ///
    /// Spent ones are excluded: their reward has been used and nothing about them is still to read.
    ///
    /// **Everything, and remnants separately, because they are not equally costly to be missing.**
    /// A remembered marker keeps its position and its kind, and its weight comes from the reference
    /// table by kind - so an unloaded chest is still counted and still covered. What unloading
    /// takes away is everything read from a component: a monster's rarity, a strongbox's pack
    /// count, and on a remnant the rewards and runes, which are where the propagation is and so
    /// most of what a chain is worth.
    ///
    /// Measured on a live site from the boundary: all fourteen remnants loaded and none of the
    /// ninety four chests, monsters, elites and relics. A check keyed on remnants alone therefore
    /// reports a site fully in range while nine tenths of its markers are not, which is why both
    /// figures come back and the readout says the larger one.
    /// </summary>
    public (int Missing, int All, int MissingRemnants, int Remnants) Reach(Vector2 site)
    {
        var missing = 0;
        var all = 0;
        var missingRemnants = 0;
        var remnants = 0;

        foreach (var target in At(site))
        {
            if (target.Spent)
                continue;

            var remnant = target.Kind == TargetKind.Remnant;

            all++;

            if (remnant)
                remnants++;

            if (target.Live)
                continue;

            missing++;

            if (remnant)
                missingRemnants++;
        }

        return (missing, all, missingRemnants, remnants);
    }

    /// <summary>
    /// Where the remembered sites live. Set once by the plugin; empty turns remembering off.
    /// </summary>
    public string Home { get; set; } = "";

    /// <summary>The real area, its name and its size, which is what a remembered file is keyed on.</summary>
    private uint _real;

    private string _name = "";

    private Vector2 _size;

    private int _saved;

    private DateTime _wrote;

    /// <param name="real">
    /// Whether this is the game changing area, as opposed to a cache clear borrowing the same call
    /// with a made up hash. Only a real change saves what was scouted and looks for what was
    /// scouted here before - a clear is explicitly asking to forget, and keying a file on a stamp
    /// nobody will ever ask for again would only litter the folder.
    /// </param>
    public void AreaChange(uint areaHash, bool real = false)
    {
        if (areaHash != _area)
        {
            // Out before it goes, while the records are still here to write.
            if (real)
                Keep();

            _known.Clear();
            _notOurs.Clear();
            _refusedPaths.Clear();
            HeldForAnotherLook = 0;
            _saved = 0;
            _savedMods = 0;
            _savedLone = _loneVerdicts;

            // A new area is walked at once rather than waiting out the backstop, and its first walk
            // is not counted as a miss - nothing has been told to us about this area yet.
            _walked = DateTime.MinValue;

            if (real)
            {
                _real = areaHash;
                _name = Safe.Read(() => _gc.Area.CurrentArea.Name, "") ?? "";
                _size = Safe.Read(() => _gc.IngameState.Data.AreaDimensions, Vector2.Zero);

                foreach (var had in Remembered.Load(Home, _real, _name, _size))
                {
                    // A file can be older than the classifier that wrote it. See Correct.
                    Correct(had);

                    _known[((int)MathF.Round(had.Grid.X), (int)MathF.Round(had.Grid.Y))] = had;
                }

                _saved = _known.Count;
                _savedMods = ModsKnownCount();
            }
        }

        _area = areaHash;
    }

    /// <summary>
    /// Writes what has been scouted, if there is more of it than last time.
    ///
    /// **Called as it goes rather than only on the way out.** A plugin reload is the case this
    /// exists for and it gives no warning - the instance holding a lap's worth of markers is simply
    /// gone - so waiting for an area change would save exactly the sessions that did not need it.
    ///
    /// **And if more modifiers are known than when it last wrote.** A relic's modifiers say what it grants and can only
    /// be read while it is loaded, so on a site whose markers were all found on the first pass they were read after
    /// the last write and never reached the file. See Remembered.Save.
    /// </summary>
    public void Keep()
    {
        if (_real == 0 || _known.Count <= _saved && ModsKnownCount() <= _savedMods && _loneVerdicts <= _savedLone)
            return;

        Remembered.Save(Home, _real, _name, _size, _known.Values);
        _saved = _known.Count;
        _savedMods = ModsKnownCount();
        _savedLone = _loneVerdicts;
        _wrote = DateTime.UtcNow;
    }

    /// <summary>How many known markers have their modifiers read. See Keep.</summary>
    private int ModsKnownCount() => _known.Values.Count(t => t.ModsKnown);

    /// <summary>How many had them at the last write. See Keep.</summary>
    private int _savedMods;

    /// <summary>How many lone remnant verdicts had been given at the last write. See Keep.</summary>
    private int _savedLone;

    /// <summary>
    /// Prices any remnant that is loaded and has not been priced yet.
    ///
    /// Once each, not once a frame: the recipes a remnant can reach are fixed when the site is
    /// generated, so the answer does not change. Only a remnant whose price came back zero is
    /// retried, since that is what an unanswered NinjaPricer looks like on the first sweep.
    /// </summary>
    private DateTime _priced = DateTime.MinValue;

    /// <summary>
    /// When the ground labels were last read: a strongbox's guarding pack count, and an unnamed
    /// object's name.
    ///
    /// On the sweep's rhythm and once each, like the pricing beside it: neither answer changes once
    /// the site is generated, so the only reason to ask twice is a label that was not up the first
    /// time. Walking the ground labels is cheap; walking them sixty times a second for an answer
    /// that cannot change is not.
    /// </summary>
    private DateTime _guarded = DateTime.MinValue;

    public void Guards(int everyMs)
    {
        if (DateTime.UtcNow - _guarded < TimeSpan.FromMilliseconds(everyMs))
            return;

        _guarded = DateTime.UtcNow;

        var wanted = false;

        foreach (var known in _known.Values)
        {
            // **Every live strongbox, not only one that has never been read.**
            //
            // A strongbox is the one thing on a dig site the player can change: a currency use
            // rerolls its rarity, its modifiers and the packs guarding it, and every one of those
            // was latched by a read-once gate written for content that cannot change. A box rolled
            // from normal to rare kept its old worth for the rest of the map, and re-solving could
            // not help because the input was stale rather than the search.
            //
            // Gated by this pass's own cadence rather than by a flag, which is the right shape for
            // it: the cost is one label walk per sweep on a site that has a box, and a roll is
            // noticed within a sweep rather than instantly. See Guards' caller.
            if (known.Kind == TargetKind.Strongbox && known.Live)
            {
                wanted = true;

                break;
            }

            // **Something that could be named and is not, which is the other half of this pass.**
            //
            // The name reading below was added inside this method and inherited a gate that asks
            // only about strongboxes - so on a site with none, or one where every strongbox had
            // already been read, the labels were never walked and nothing was ever named. An object
            // filed before its label loaded then kept its art file as its name for good:
            // "devourerbodysegment" for a thing the game plainly labels "Dormant Burrower", with
            // Refile sitting there unable to fire because Words never arrived for it to fire on.
            //
            // The same test the loop applies per label, asked here of the targets.
            if (known.Live && known.Words.Length == 0 && known.Blessing.Length == 0 &&
                known.Kind is TargetKind.Unknown or TargetKind.Relic)
            {
                wanted = true;

                break;
            }
        }

        if (!wanted)
            return;

        var labels = Ground.Labels(_gc);

        if (labels == null)
            return;

        foreach (var label in labels)
        {
            var entity = Safe.Read(() => label.ItemOnGround, null);
            var at = Safe.Read(() => entity?.GridPos ?? Vector2.Zero, Vector2.Zero);

            // Matched on the cell rather than on the entity, because the target's own entity is the
            // MOUND and the label belongs to the box standing on it. See Target.Holds.
            if (at == Vector2.Zero)
                continue;

            foreach (var known in _known.Values)
            {
                // The label is the game naming the thing in English, and for an object nothing has
                // been taught about that is the best identity there is. See Unknowns.Key.
                // **Matched on the entity, not on the cell, and that distinction cost a name.**
                //
                // An encased monster is two entities standing in one place: the casing a blast
                // breaks and the monster inside it. Only the monster has a ground label, so matching
                // by position handed the casing the monster's name - every casing became "Blood
                // Zealot" or "Gelid Zealot" depending on its occupant, and one kind of object split
                // into a row per flavour of thing inside it.
                //
                // The strongbox read below matches by cell deliberately, because there the target IS
                // the mound and the label belongs to the box standing on it. Here the opposite is
                // wanted: this is the identity of the thing itself.
                if (known.Words.Length == 0 && known.Blessing.Length == 0 &&
                    known.Kind is TargetKind.Unknown or TargetKind.Relic &&
                    known.Entity != null && entity != null &&
                    Safe.Read(() => known.Entity.Id, 0u) == Safe.Read(() => entity.Id, 1u))
                {
                    var element = Safe.Read(() => label.Label, null);
                    var was = known.Filed;

                    known.Words = Unknowns.Plain(Wording(element, 0));
                    known.Blessing = Unknowns.Plain(Blessings(element, 0, new List<string>()));

                    // Filed before it had a name, which happens whenever the object was first seen
                    // from further away than the game draws its label. See Unknowns.Refile.
                    if (was.Length > 0 && known.Words.Length > 0)
                        Unknowns.Refile(known, was, _known.Values);
                }

                if (known.Kind != TargetKind.Strongbox ||
                    Vector2.Distance(known.Grid, at) >= 1.5f)
                    continue;

                // **Re-read, and the rarity and the modifiers with it.** Those two are latched on
                // first sight like the packs were, and a roll changes all three at once - a box that
                // kept its old packs and gained a new rarity would be a third right.
                if (known.Held != null)
                {
                    var rarity = Rarity(known.Held);

                    if (rarity.Length > 0)
                        known.Rarity = rarity;

                    known.Reread();
                }

                var guard = new Guard();

                Guarding(Safe.Read(() => label.Label, null), 0, ref guard);

                // All six together or none, since the inherent line is what says the label was read
                // at all - a rolled count written while the inherent one was missing would be a box
                // described by its rolls alone.
                //
                // **Written only when something moved.** Every one of these feeds the site
                // fingerprint, which is what wakes a settled presolve - so assigning the same values
                // back every sweep would restart the search once a second for as long as you stood
                // beside a strongbox. See Rehearsal.Fingerprint.
                if (guard.Implicit &&
                    (known.Packs != guard.Normal + guard.Magic + guard.Rare ||
                     known.ImplicitPacks != guard.Normal ||
                     known.ImplicitMagicPacks != guard.Magic ||
                     known.ImplicitRarePacks != guard.Rare ||
                     known.ExplicitPacks != guard.RolledNormal ||
                     known.ExplicitMagicPacks != guard.RolledMagic ||
                     known.ExplicitRarePacks != guard.RolledRare))
                {
                    known.Packs = guard.Normal + guard.Magic + guard.Rare;
                    known.ImplicitPacks = guard.Normal;
                    known.ImplicitMagicPacks = guard.Magic;
                    known.ImplicitRarePacks = guard.Rare;
                    known.ExplicitPacks = guard.RolledNormal;
                    known.ExplicitMagicPacks = guard.RolledMagic;
                    known.ExplicitRarePacks = guard.RolledRare;
                }

                break;
            }
        }
    }

    /// <summary>
    /// The first real sentence under a ground label, which is the thing's name.
    /// <summary>
    /// A node's children, or nothing at all where it has none.
    ///
    /// **Reading Children on a leaf is what fills the log.** ExileCore2 resolves the collection by
    /// index and reports "Element with index: 0 not found" when there is no child nought - it logs
    /// rather than throwing, so Safe.Read never sees it and nothing in this plugin was ever the
    /// wiser. A recursive walk of a ground label asks it of every leaf, and most of a label's tree
    /// is leaves.
    ///
    /// ChildCount answers the same question without building anything. Guarding on it is the
    /// difference between a walk that is silent and one that writes a line per leaf per pass.
    /// </summary>


    ///
    /// Depth limited for the same reason Packs is: the label is a small tree and an unbounded walk
    /// of remote memory is a hang waiting for a bad read. The first text of a sensible length wins -
    /// a label leads with what the thing is called and follows with what it does.
    /// </summary>
    private static string Wording(Element at, int depth)
    {
        if (at == null)
            return "";

        // **The title block, which is the last child, before anything else.**
        //
        // A ground label is two blocks: what the thing does, then what it is called. Searching the
        // whole tree depth first meant the description was read first, and the description contains
        // the game's inline keyword links - "[Attack|Attack]", "[Runic]" - whose Text reads back as
        // three characters of nonsense rather than as words. Measured across fifteen labels in two
        // dumps: every one had its name in the last child, and every piece of nonsense was inside
        // the description block, at six different depths. So the search went to the description,
        // found three characters that passed for a name, and filed a Vaal Relic under a pointer.
        //
        // **Structural rather than a test on the characters, because the characters are a trap.**
        // The obvious guard is "reject text with no ASCII letters", which works on an English client
        // and files every object in the game under one row on a Chinese one. Where the name sits
        // does not depend on what language it is written in.
        var kids = Safe.Kids(at);

        if (kids is { Count: > 0 })
        {
            // Any length here: a name in a language that writes one in two characters is a name.
            var titled = Read(kids[^1], 0, 1);

            if (titled.Length > 0)
                return titled;
        }

        // Nothing in the title, so fall back to what this did before - never worse than it was.
        //
        // Four characters rather than three, only here, because this is the half of the label the
        // nonsense lives in and every instance of it measured three characters or fewer. A short
        // name missed in this fallback files the object nameless, which a later label read now
        // corrects; a pointer accepted files it under a row that can never recur. See Unknowns.Refile.
        return Read(at, 0, 4);
    }

    /// <summary>The first text under <paramref name="at"/> that reads like words, shortest allowed
    /// length given by <paramref name="least"/>.</summary>
    private static string Read(Element at, int depth, int least)
    {
        if (at == null || depth > 4)
            return "";

        // **A name the game is showing you is visible and has a size.**
        //
        // The last label reading nonsense after the title rule went in was a remnant's, whose junk
        // sat on an element measuring nothing and drawn to nobody - visible False, 0 by 0. Nothing
        // the player can read is shaped like that, and this is structural like the rule above it
        // rather than a judgement about which characters count as writing.
        if (!Safe.Read(at, static e => e.IsVisible, false) ||
            Safe.Read(at, static e => e.Width, 0f) <= 0f ||
            Safe.Read(at, static e => e.Height, 0f) <= 0f)
            return "";

        var text = Safe.Read(at, static e => e.Text, null) ?? "";

        if (text.Length >= least && text.Length <= 60 && !text.Contains('{') && !text.Contains('<'))
            return text;

        foreach (var kid in Safe.Kids(at) ??
                            new List<Element>())
        {
            var found = Read(kid, depth + 1, least);

            if (found.Length > 0)
                return found;
        }

        return "";
    }

    /// <summary>
    /// Every enchanted line under a label, joined - which is what the thing grants. See Blessing.
    ///
    /// The game writes an upside as an enchantment and a downside plainly, so matching the markup
    /// picks out the good half without having to understand a word of it. Sorted, because a label
    /// lists them in whatever order it likes and two relics granting the same pair of things are one
    /// kind of relic.
    /// </summary>
    private static string Blessings(Element at, int depth, List<string> found)
    {
        if (at == null || depth > 4)
            return "";

        var text = Safe.Read(at, static e => e.Text, null) ?? "";
        var from = text.IndexOf("<enchanted>{", StringComparison.Ordinal);

        if (from >= 0)
        {
            var to = text.IndexOf('}', from);

            if (to > from)
                found.Add(text[(from + 12)..to]);
        }

        foreach (var kid in Safe.Kids(at) ?? new List<Element>())
            Blessings(kid, depth + 1, found);

        if (depth > 0)
            return "";

        found.Sort(StringComparer.Ordinal);

        return string.Join("; ", found);
    }

    /// <summary>The six guarding counts a strongbox label can promise. See Target.Packs.</summary>
    private struct Guard
    {
        public int Normal;
        public int Magic;
        public int Rare;
        public int RolledNormal;
        public int RolledMagic;
        public int RolledRare;

        /// <summary>Whether the implicit line has been met, which is what makes the rest explicit.</summary>
        public bool Implicit => Normal + Magic + Rare > 0;
    }

    /// <summary>
    /// Every "Guarded by n packs of ..." sentence under this element, split by the rarity it names.
    ///
    /// **It walks the whole label now instead of stopping at the first match.** One number was
    /// enough while a strongbox only had its inherent line; a rolled one carries a second sentence,
    /// and returning whichever the tree happened to reach first meant reading 6 packs or 2 packs
    /// depending on the game's own layout, with nothing to say which had been read.
    ///
    /// **Invisible children are skipped, which matters more now that matches are summed.** The label
    /// holds hidden alternate layouts - branches that render a different arrangement of the same
    /// object - and a duplicate sentence found down one of those would be counted as a roll that
    /// never happened. First-match reading was immune to that and this is not.
    ///
    /// Depth limited because the label is a small tree and an unbounded walk of remote memory is a
    /// hang waiting for a bad read. Matched on the words rather than on the position of the child,
    /// since a label's shape is the game's business and the sentence is what it promises.
    /// </summary>
    private static void Guarding(Element at, int depth, ref Guard guard)
    {
        if (at == null || depth > 6 || !Safe.Read(at, static e => e.IsVisible, false))
            return;

        var text = Safe.Read(at, static e => e.Text, null) ?? "";
        var found = Counted(text);

        if (found > 0)
        {
            // **Order decides which line this is, and the rarity decides which count it lands in.**
            // The implicit line names a rarity exactly as an explicit one does, so there is no wording
            // to tell them apart - only that the implicit renders above the modifier block. The
            // first sentence is therefore the implicit one whatever rarity it names, which is a
            // correction: reading it as "normal means implicit" filed a Researcher's implicit rare
            // packs as explicit and left the implicit count at zero, so the gate never closed.
            var explicitly = guard.Implicit;

            if (text.IndexOf("rare", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (explicitly)
                    guard.RolledRare += found;
                else
                    guard.Rare = found;
            }
            else if (text.IndexOf("magic", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (explicitly)
                    guard.RolledMagic += found;
                else
                    guard.Magic = found;
            }
            else if (explicitly)
            {
                guard.RolledNormal += found;
            }
            else
            {
                guard.Normal = found;
            }
        }

        var kids = Safe.Kids(at);

        if (kids == null)
            return;

        foreach (var kid in kids)
            Guarding(kid, depth + 1, ref guard);
    }

    /// <summary>
    /// "Guarded by 9 packs of Monsters" -> 9. Zero when the sentence is not that one.
    ///
    /// **One is written as a word, and it was read as nothing.** The game does not print "1 pack": it
    /// prints "Guarded by a pack of Rare Monsters". This wanted a digit, found none, and returned
    /// nought - and the caller skips a nought, so the whole sentence was dropped. Seen in a Grand
    /// Expedition dump: a Jeweller's Strongbox carrying ChestSummonRares, whose one rolled rare pack
    /// vanished and left the box priced as eight normal packs. One of the ten distinct guarding
    /// sentences in that site was worded this way.
    ///
    /// The singular is the cheapest roll and so the commonest of the rare and magic lines, which is
    /// the worst case for silently losing it: the rarities it hides are the two worth the most.
    /// </summary>
    private static int Counted(string text)
    {
        if (text.Length == 0 || text.IndexOf("pack", StringComparison.OrdinalIgnoreCase) < 0)
            return 0;

        var number = 0;
        var seen = false;

        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                number = number * 10 + (c - '0');
                seen = true;
            }
            else if (seen)
            {
                break;
            }
        }

        // No digit anywhere, but the sentence promises packs, so it is the worded singular. Matched
        // on "a pack" rather than on the absence of a number alone: that keeps any future wording
        // this does not understand reading as nought, where it is at least visible as a miss rather
        // than silently counted as one.
        if (!seen && text.IndexOf(" a pack", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;

        return number;
    }

    /// <summary>
    /// Notices anything unrecognised, on the sweep's rhythm rather than on a key press.
    ///
    /// See Weighing.Catalogue for why this is not left to the weighing: a list of things the plugin
    /// cannot name is only useful while you are walking past them.
    /// </summary>
    public void Learn(AutoExpeditionSettings settings, int everyMs)
    {
        if (DateTime.UtcNow - _learned < TimeSpan.FromMilliseconds(Math.Max(50, everyMs)))
            return;

        _learned = DateTime.UtcNow;

        foreach (var known in _known.Values)
            Weighing.Catalogue(known, settings);
    }

    private DateTime _learned = DateTime.MinValue;

    public void Price(Valuation valuation, int everyMs)
    {
        // On the same rhythm as the sweep. A remnant whose price comes back zero - no NinjaPricer
        // yet, or its data still downloading - is retried, and retrying that every frame means
        // reading a component and walking three hundred recipes sixty times a second for nothing.
        if (DateTime.UtcNow - _priced < TimeSpan.FromMilliseconds(everyMs))
            return;

        _priced = DateTime.UtcNow;

        // Read once for the whole sweep rather than per remnant: it serialises the weight tree, and
        // the answer cannot change between two remnants of the same pass. See Wrt.Revision.

        foreach (var known in _known.Values)
        {
            // Nothing to price on a remnant that has been spent: its options are gone and its
            // rewards were taken.
            if (known.Kind != TargetKind.Remnant || !known.Live || known.Spent)
                continue;

            // Which slots carry forward, and which runes could fill them. Before the price gate
            // rather than after it, because this does not need NinjaPricer to be right - and a
            // remnant that priced on the first sweep would otherwise never be asked.
            // Null again after a roll, which is exactly when this needs asking a second time.
            known.Passing ??= valuation.Passing(known.Entity);

            // A remnant whose options have just collapsed to the one it is set to - which only
            // happens when the player has turned overruling off - has to be read again, because
            // the list cached before the choice still holds every option it used to have.
            //
            // Once is enough: the second read comes back with a single entry, so the count stops
            // being greater than one and this stops firing.
            // What it is set to, recorded rather than acted on.
            //
            // **This used to force a re-read and no longer has to.** The option list collapsed when
            // a choice was made with overruling off, so the list depended on the choice and had to
            // follow it - and the test for that ("more than one option left") was false the moment
            // it succeeded, which froze the list on the first pick for the life of the site.
            // Reachable no longer collapses for overruling, so the list does not depend on the
            // choice at all: the plan is pinned in Weighing.Choices and the overlay reads the choice
            // live. Nothing here needs to move when it changes.
            known.Chose = Safe.Read(() => valuation.ChosenName(known.Entity), null) ?? "";

            if (known.Chose.Length == 0 && Options.Solved(known) is var solved and >= 0)
                known.SolverPickBeforeChoice = known.Rewards[solved].Recipe;

            // Forgetting clears the rewards, so a rolled remnant comes back through here and is
            // read afresh without needing a second flag to say so.
            //
            // **And re-read when the table it was priced under has changed.** Priced once per site
            // was right while the pricing rule could not move, and the reference table can move it:
            // a weight, a scope, a grouping or an empower edited mid-site left every remnant quoting
            // a valuation computed under the old answer, with nothing anywhere saying so. The symptom
            // is an edit that appears to do nothing - the ground text, the plan and the score all
            // hold still - which reads exactly like a setting that does not work. See Wrt.Revision.
            // **And re-read when the rune it was priced under is no longer the rune it has.** A
            // reroll replaces the combination, and the encounter data can also be read before the
            // rune resolves; comparing what is readable NOW against what the cached price was taken
            // under covers both without needing a flag for either. See Valuation.FixedRune.
            var namedNow = Safe.Read(() => valuation.FixedRune(known.Entity), null) ?? "";
            var slotNow = Safe.Read(() => valuation.FixedSlot(known.Entity), -1);
            var collapsedNow = Safe.Read(() => Insisted.Here.MarkedForRewardValue(known.Entity.GridPos), false) &&
                               Safe.Read(() => valuation.ChosenByPlayer(known.Entity), null) == null;

            if (known.Rewards.Count > 0 && known.Rewards[0].Value > 0d &&
                known.PricedUnder == Wrt.Revision &&
                known.FixedRune == namedNow && known.FixedSlot == slotNow &&
                known.PricedCollapsedToMustTake == collapsedNow)
                continue;

            // **Half a read is not a price, and storing one wrecked a whole site.**
            //
            // A remnant that states a fixed rune POSITION and no fixed rune is a read that caught
            // the encounter data mid-flight. Reachable has nothing to filter on when that happens,
            // so it hands back every recipe the socket count admits - which is not this remnant's
            // option list, it is the generic one for its size. Cached, that is what the ground text,
            // the plan and the score all then quote.
            //
            // **Every remnant is re-priced at once, so one bad frame takes the lot.** The cache is
            // keyed on the table and weights revisions, and an edit to either bumps them - so
            // editing a weight, or rolling a remnant, re-prices all of them together. Caught in
            // game: nine remnants correct at 00:15, all nine holding exactly ten rewards - the cap,
            // which only an unfiltered list reaches - and no fixed rune between them at 01:09, then
            // correct again by 01:27 when something re-priced them on a good frame. In between, two
            // four-socket remnants offered a Divine Orb that was really on a third, the planner
            // routed to them for it, and the one that held it could not be told from the two that
            // did not.
            //
            // So an incomplete read stores nothing and is asked again next sweep. The previous
            // answer stands meanwhile, which is the right thing to hold: it was read whole.
            if (slotNow >= 0 && namedNow.Length == 0)
                continue;

            // **Every combination, because nothing bounds how many a remnant offers.** One unique
            // map gives a single remnant several hundred, and both the window and the overlay join
            // to this list row by row - a row with no entry here draws no propagation at all, which
            // is what a cap of ten did the moment combinations stopped being collapsed by reward
            // name. Uncapped is the only figure that cannot be wrong about a remnant nobody has met.
            //
            // The cost that a cap was buying is in the SEARCH rather than here: PlanTarget.Best
            // walks every choice per covered marker per scored chain. Left uncapped on purpose and
            // to be revisited if a site is ever slow enough to notice - see NOTES.
            var found = valuation.Top(known.Entity, int.MaxValue);

            if (found.Count > 0)
            {
                known.PricedUnder = Wrt.Revision;
                known.PricedCollapsedToMustTake = collapsedNow;
                known.Rewards = found;
                known.Spread = valuation.Spread(known.Entity);
                known.FixedRune = valuation.FixedRune(known.Entity) ?? "";
                known.FixedSlot = valuation.FixedSlot(known.Entity);
            }

        }
    }

    /// <summary>Folds whatever is loaded now into what is already known.</summary>
    /// <summary>Entity buckets walked as one, without copying any of them.</summary>
    private static IEnumerable<Entity> Both(
        IEnumerable<Entity> first, IEnumerable<Entity> second)
    {
        foreach (var entity in first)
            yield return entity;

        foreach (var entity in second)
            yield return entity;
    }

    /// <summary>
    /// The buckets a dig site keeps its own things in, which are walked whole.
    ///
    /// Markers are IngameIcon and the sentries and remnants are MiscellaneousObjects. The sentries
    /// are why these two are exempt from the metadata test below rather than merely cheaper: a
    /// dormant Verisium Sentry is filed under Metadata/MiscellaneousObjects/Sentinel and its path
    /// does not say "Expedition" anywhere, so a blanket filter would drop it.
    /// </summary>
    private static readonly EntityType[] Named =
    {
        EntityType.IngameIcon,
        EntityType.MiscellaneousObjects,
    };

    /// <summary>Everything loaded, with the obviously irrelevant thrown out cheaply. See Tick.</summary>
    /// <summary>
    /// How many entities the last sweep walked.
    ///
    /// The sweep's cost is per entity and the entity count is the client's business, not this
    /// plugin's - so a sweep measured at seventy milliseconds is either slow per entity or handed a
    /// great many of them, and nothing in the timing alone separates those. See Everything.
    /// </summary>
    public static int Walked { get; private set; }

    private static IEnumerable<Entity> Everything(
        IReadOnlyDictionary<EntityType, List<Entity>> buckets)
    {
        foreach (var (bucket, entities) in buckets)
        {
            if (entities == null)
                continue;

            var whole = Array.IndexOf(Named, bucket) >= 0;

            foreach (var entity in entities)
            {
                if (whole)
                {
                    yield return entity;

                    continue;
                }

                string metadata;

                using (Spent.On("Sweep/Filter"))
                    metadata = Safe.Read(entity, static e => e.Metadata ?? "", "");

                // **The word is not always in the path, and a buried strongbox is the case.** The
                // cheap filter is there so a map full of ordinary entities is not classified one by
                // one, and it works because nearly everything a dig site owns says so in its name.
                // ArmourerStrongboxHigh does not - it is filed with the ordinary strongboxes and
                // only its state says a blast acts on it - so it was thrown out a step before
                // anything looked at it, which is why it went on being reported as content the scan
                // does not know while a weight sat waiting for it.
                if (metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
                    metadata.Contains("StrongBoxes", StringComparison.OrdinalIgnoreCase))
                    yield return entity;
            }
        }
    }

    /// <summary>Whether the last Tick actually swept, as opposed to being too early to bother.</summary>
    public bool Swept { get; private set; }

    /// <summary>
    /// An entity the game has just loaded, handed over by the host rather than found by walking.
    ///
    /// **The walk exists because nothing was telling us.** ExileCore2 calls EntityAdded once per
    /// entity as it streams in, which is the same information the walk was re-deriving twice a
    /// second: it looked at every entity loaded - around eight hundred on a map - and classified
    /// the seven hundred and fifty that are nothing to a dig site all over again, because only
    /// successes go on the record. Measured at 9.2ms in the frame it lands on, which is a visible
    /// hitch twice a second beside a plugin whose cost is flat.
    ///
    /// Classifying here means each entity is classified once, ever. What remains periodic is the
    /// part that genuinely changes - whether a remnant has been rolled, whether a barrel has gone
    /// off - and that is a few dozen objects rather than eight hundred.
    /// </summary>
    /// <summary>
    /// Every way Arrived can end, counted.
    ///
    /// **Pushed alone cannot say why it is nought.** A session where the host announced entities
    /// sixty-five times and not one became a target has at least four explanations - no position
    /// to key on, a cell already occupied, a classify that failed, or a hold the membership test
    /// refused - and they want four different fixes. Naming the exits is one dump against another
    /// round of guessing, and this session has already spent several on the wrong one.
    /// </summary>
    public static int Announced { get; private set; }

    public static int WithoutGrid { get; private set; }

    public static int AlreadyKnown { get; private set; }

    public static int Refused { get; private set; }

    public void Arrived(Entity entity)
    {
        if (entity == null)
            return;

        Announced++;

        var grid = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

        // **A read that failed is not an answer, and this used to treat it as one.**
        //
        // The host announces an entity the moment it adds it, which is the earliest anything can
        // be asked about it and the least likely moment for its components to be readable - this
        // file says so in four other places. The sweep this replaced did not care, because it
        // tried again half a second later and every half second after that. Arrived got one
        // attempt at the worst moment and dropped whatever failed on the floor, so the only thing
        // that ever found those again was the ten second backstop walk: measured on one map, 23
        // entities arrived through the callback and the walk went on to find 58 it had not.
        //
        // ExpeditionIcons has no retry and needs none, because it decides membership from the
        // path and the entity type - both there at once - and defers the art read behind a Lazy.
        // This classifier cannot copy that: one metadata path covers monster, elite and chest
        // markers, and only the art tells them apart. So it keeps the entity and asks again.
        if (grid == Vector2.Zero)
        {
            WithoutGrid++;
            WaitFor(entity, Vector2.Zero);

            return;
        }

        var key = ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));

        if (_known.TryGetValue(key, out var existing))
        {
            AlreadyKnown++;
            Attach(existing, entity);

            return;
        }

        var found = Classify(entity, grid);

        if (found == null)
        {
            WaitFor(entity, grid);

            return;
        }

        found.Site = SiteFor(found.Grid);
        _known[key] = found;
        Pushed++;
    }

    /// <summary>
    /// Entities the host has announced that could not be classified yet, and how often each has
    /// been asked. See Arrived and Settling.
    /// </summary>


    /// <summary>Entities the membership test has already said no about. See WaitFor.</summary>
    private readonly HashSet<uint> _notOurs = new();

    /// <summary>
    /// The most refusals remembered, which is what keeps a repeat walk cheap.
    ///
    /// Two thousand covers every entity a map loads, so in practice the cap never bites; it is
    /// here so a map that loads something absurd cannot turn this into a leak.
    /// </summary>
    private const int Refusals = 2000;

    /// <summary>The distinct paths it said no about, so the test can be judged. See WaitFor.</summary>
    private readonly HashSet<string> _refusedPaths = new();

    /// <summary>Those paths, shortest last segment first, for the dump.</summary>
    public static string RefusedPaths { get; private set; } = "nothing refused yet";
    /// <summary>
    /// How far from a detonator counts as standing in the dig site, for the retry's membership.
    ///
    /// Wider than any chain can reach, because this decides only whether something is worth asking
    /// about again. See WaitFor.
    /// </summary>
    private const float Inside = 220f;

    /// <summary>Where the detonators are, read once a tick rather than once an entity.</summary>
    private List<Vector2> _sites;

    /// <summary>
    /// Which dig site a position belongs to, or nothing while no detonator can be read.
    ///
    /// **Nothing is not an answer, and a target filed under it stays there.** Nearest hands back
    /// Vector2.Zero when the detonator panel has not loaded, and Site is written once, when a
    /// target is classified - so a marker met before the panel arrives is filed under nowhere and
    /// At(site) never matches it again. Seen on a first entry: every remnant and strongbox read
    /// "0 here" while the counts beside them said seven and eighteen, and the plan, which can only
    /// see what is filed under the site it is solving, came out at 397. Filing below undoes it.
    ///
    /// Also the one place the detonators are read. It was DetonatorPositions per classified
    /// entity, and that builds a list every time.
    /// </summary>
    private Vector2 SiteFor(Vector2 grid)
    {
        _sites ??= DetonatorPositions(_gc);

        return Nearest(_sites, grid);
    }

    /// <summary>
    /// Files anything classified before a detonator could be read. See SiteFor.
    ///
    /// On the short rhythm, over the few dozen on record, and it does nothing at all once they all
    /// have a site - which is within one tick of the panel loading.
    /// </summary>
    private void Filing()
    {
        if (_sites is not { Count: > 0 })
            return;

        foreach (var known in _known.Values)
        {
            if (known.Site == Vector2.Zero)
                known.Site = Nearest(_sites, known.Grid);
        }
    }
    /// <summary>
    /// How many times an entity was kept for the next walk rather than refused. See WaitFor.
    ///
    /// A running count, not a queue length: nothing is held any more, because an entity handed to
    /// a callback may be remade as it streams and a kept handle reads a dead address. What is
    /// remembered is the opposite - which entities are NOT ours - so the walk can skip them and
    /// afford to come round again.
    /// </summary>
    public static int HeldForAnotherLook { get; private set; }

    /// <summary>
    /// Keeps an entity for another look, if there is reason to think it is ours.
    ///
    /// **Without a membership test this is the old sweep wearing a different name.** Nearly eight
    /// hundred entities on a map are nothing to a dig site, and asking all of them again twice a
    /// second is exactly the 18ms of classification the callbacks were brought in to stop.
    ///
    /// So the test is made once, here, off two things that do not need the art: the path, which is
    /// readable the instant the entity exists, and whether the client lights the thing under the
    /// placement circle. An unreadable path keeps it too - not knowing what something is, is the
    /// case this whole method exists for.
    /// </summary>
    private void WaitFor(Entity entity, Vector2 grid)
    {
        var id = Safe.Read(entity, static e => e.Id, 0u);

        if (id == 0 || _notOurs.Contains(id))
            return;

        // **Where it stands, first, because that test cannot be wrong.**
        //
        // The path and the glow are readings, and a reading can fail - which is the whole reason
        // this method exists, so leaning on one to decide whether to retry the other is circular.
        // A position is arithmetic against the detonators the scan already knows, and anything
        // standing inside a dig site is worth another look whatever its components say.
        //
        // Generous on purpose. The cost of keeping something that turns out to be scenery is
        // twenty classifies over ten seconds; the cost of refusing a marker is that it does not
        // exist to the planner until a walk happens to meet it with its art loaded, which is what
        // a re-entry made visible - the scan held remnants and strongboxes, the two kinds that
        // classify without art, and nothing else.
        var inside = false;

        foreach (var site in _sites ?? [])
        {
            if (Vector2.Distance(site, grid) <= Inside)
            {
                inside = true;

                break;
            }
        }

        var metadata = inside ? null : Safe.Read(entity, static e => e.Metadata, null);
        var ours = inside ||
                   metadata == null ||
                   metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
                   Target.Stated(entity, "glow_epk") >= 0;

        if (ours)
        {
            // Left for the next walk to try again with a fresh handle on it. Nothing is kept here
            // but the fact that it is worth asking about, because the entity itself will not
            // survive the wait. See Settling, which is gone for exactly that reason.
            HeldForAnotherLook++;

            return;
        }

        {
            // **Asked once per entity, not once per walk.** The walk offers every entity it could
            // not classify, which on a map is some eight hundred of them, and the test reads a
            // StateMachine for each one that the path did not already answer for. Measured at 818
            // refusals in a single session, all of them the same entities being re-refused every
            // ten seconds - which is the per-entity cost the callbacks were brought in to stop,
            // reintroduced by the thing meant to make them reliable.
            //
            // Only a positive no is remembered. An entity whose path could not be read is not
            // filed here, because that answer may differ the next time it is asked.
            if (metadata != null && _notOurs.Count < Refusals)
                _notOurs.Add(id);

            // **A count cannot say whether the test is right.** Six hundred and seventy-nine
            // refusals is either the map's doodads being correctly ignored or the markers being
            // thrown away, and those read identically. The distinct paths are a handful of strings
            // and they settle it at a glance.
            if (metadata != null && _refusedPaths.Count < 40)
                _refusedPaths.Add(metadata);

            Refused++;

            return;
        }

    }

    /// <summary>
    /// An entity the game has unloaded. The record stays and only the live handle goes.
    ///
    /// This is what the walk's opening move was for - nulling every handle and treating whatever
    /// was not put back as gone. That only works if the walk is one uninterrupted pass, which is
    /// what made it impossible to spread over frames. Told directly, it needs neither.
    ///
    /// Matched on the handle rather than on the cell, because entities share cells: a remnant and
    /// its encounter controller stand on the same spot, and one of them leaving is not the other
    /// one leaving.
    /// </summary>
    public void Departed(Entity entity)
    {
        if (entity == null)
            return;

        var grid = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

        if (grid == Vector2.Zero)
            return;

        if (!_known.TryGetValue(((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var known))
            return;

        if (ReferenceEquals(known.Entity, entity))
            known.Entity = null;

        if (ReferenceEquals(known.Held, entity))
            known.Held = null;
    }

    /// <summary>
    /// Takes in what can have changed, over the targets on record rather than over everything
    /// loaded. See Attach for the same work done to one target as its entity arrives.
    /// </summary>
    private void Refresh()
    {
        foreach (var known in _known.Values)
        {
            var entity = known.Entity;

            if (entity != null)
                Attach(known, entity);
        }
    }

    /// <summary>How many targets first reached the record through Arrived. See Missed.</summary>
    public static int Pushed { get; private set; }

    /// <summary>
    /// How many the backstop walk found that the callbacks had not.
    ///
    /// **The callbacks are believed only as far as this figure says to believe them.** Whether
    /// EntityAdded fires for every entity or only for the ones the host files as valid is not
    /// something this plugin can read out of the host, and the walk it replaces deliberately looked
    /// at everything - which is how a strongbox filed under an ordinary bucket was found at all.
    /// So the walk stays, on a long rhythm, and counts what it catches that arrived no other way.
    /// Nought over a session is the evidence for leaving it out; anything else is the reason not to.
    ///
    /// The first walk in an area is not counted, since nothing has been told to us yet.
    /// </summary>
    public static int Missed { get; private set; }

    /// <summary>Starts the counts again, so a run measures what it caused. See Caches.Clear.</summary>
    public static void Forgetting()
    {
        Pushed = 0;
        Missed = 0;
        Announced = 0;
        WithoutGrid = 0;
        AlreadyKnown = 0;
        Refused = 0;
    }

    public void Tick(int everyMs)
    {
        Swept = false;

        // Markers do not move and nothing is added to a site once it is generated, so this is about
        // noticing newly loaded ones rather than keeping up with anything.
        if (DateTime.UtcNow - _swept < TimeSpan.FromMilliseconds(everyMs))
            return;

        _swept = DateTime.UtcNow;
        Swept = true;
        _sites = DetonatorPositions(_gc);

        // **The walk is a backstop now, not the way things are found.** Arrived is told about each
        // entity as it loads, so the periodic job here is taking in what can have changed on the
        // few dozen targets on record - not classifying eight hundred entities to re-derive a
        // record that already exists. The walk measured 9.2ms in the frame it landed on, twice a
        // second, which is a visible hitch beside a plugin whose cost is flat.
        //
        // It still runs, seldom, because whether EntityAdded fires for every entity or only for the
        // ones the host files as valid is not something this plugin can read out of the host, and
        // what it replaces looked at everything on purpose. Missed counts what it catches that
        // arrived no other way, which is the evidence for how long this interval can get.
        if (DateTime.UtcNow - _walked < Backstop)
        {
            using (Spent.On("Scan.Tick/Refresh"))
                Refresh();

            using (Spent.On("Scan.Tick/Filing"))
                Filing();

            return;
        }

        // Nothing has been told to us about a new area yet, so its first walk finds everything and
        // none of that counts as the callbacks having missed anything. See Missed.
        var first = _walked == DateTime.MinValue;

        _walked = DateTime.UtcNow;

        // Every bucket, not a list of the ones we thought of.
        //
        // **Three times now a fixed list has missed something and the miss looked identical each
        // time**: a red "content the planner is ignoring" line standing over an object that was
        // right there in the entity list. The sentries were in MiscellaneousObjects when only
        // IngameIcon was read. The siren eggs were Terrain when only those two were read. The
        // Peninsula shatterables are Chest, which none of the three covered. Adding a fourth bucket
        // would have fixed this site and not the next one.
        //
        // Which bucket the game files an object in says nothing about whether a dig site cares about
        // it, so the test is what it IS, not where it is kept. Everything loaded is walked and the
        // classifier decides - the same reasoning the unknown-content sweep already runs on, which
        // is why that one keeps finding what this one misses.
        //
        // Affordable because the test is ordered: outside the two buckets a dig site keeps its own
        // things in, a metadata check throws out the overwhelming majority before anything reaches
        // into a component, and this runs once a second.
        var buckets = Safe.Read(_gc, static g => g.EntityListWrapper.ValidEntitiesByType, null);

        if (buckets == null)
            return;

        // Anything not seen this sweep has been unloaded. The record stays and only the live handle
        // goes, so nothing later reads a memory object the game has freed. Both handles: the second
        // half of a pair is freed exactly as the first is. See Target.Held.
        foreach (var known in _known.Values)
        {
            known.Entity = null;
            known.Held = null;
        }

        var sites = DetonatorPositions(_gc);

        // **The sweep itself, timed apart from the throttle.** Scan.Tick measures 0.750ms a frame
        // and 31.443ms in the frame it lands on, because it runs twice a second and does nothing
        // the rest of the time - so the average describes nothing anybody feels and the worst
        // describes all of it. See Spent, and DebugSettings.SweepMs for the interval.
        var sweeping = Spent.On("Scan.Tick/Sweep");

        var walked = 0;

        foreach (var entity in Everything(buckets))
        {
            walked++;

            // **Skipped on the strength of an answer already given**, which is what lets this run
            // every second instead of every ten. One id read and a set lookup against the
            // twenty-three microseconds a classify costs. See WaitFor, where the verdict is
            // reached, and Backstop for why the interval moved.
            if (_notOurs.Contains(Safe.Read(entity, static e => e.Id, 0u)))
                continue;

            // The state-taking overload throughout this loop, not a closure. A lambda that captures
            // entity allocates a holder and a delegate for every entity on every sweep, and the
            // sweep walks everything the client has loaded - tens of thousands on a full map, twice
            // a second. Everything() already reads its metadata this way for the same reason.
            Vector2 grid;

            using (Spent.On("Sweep/GridPos"))
                grid = Safe.Read(entity, static e => e.GridPos, Vector2.Zero);

            if (grid == Vector2.Zero)
                continue;

            var key = ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));

            // A marker already on record only needs its live handle back. Reclassifying it would
            // re-read the art - an Animated component, its base object and that object's path -
            // to arrive at the answer already stored, for every marker on every sweep.
            if (_known.TryGetValue(key, out var existing))
            {
                Attach(existing, entity);

                continue;
            }

            Target found;

            using (Spent.On("Sweep/Classify"))
                found = Classify(entity, grid);

            // **The walk drops what it cannot classify too, and it is the LAST route in.**
            //
            // Zoning in, the host adds its entities around the moment the area change reaches this
            // plugin, and an add that lands before AreaChange is wiped by the clear - it will
            // never be announced again. The walk is what recovers those, so a walk that meets one
            // before its art has streamed and drops it leaves nothing at all: measured on a
            // re-entry, 3 entities arrived through the callback, the walk classified none it did
            // not already have, and 90 markers sat with no target on them. The same retry, from
            // both routes, for the same reason Attach is shared by both.
            if (found == null)
            {
                WaitFor(entity, grid);

                continue;
            }

            found.Site = Nearest(sites, found.Grid);
            _known[key] = found;

            if (!first)
                Missed++;
        }

        // Ten seconds, and only when there is more than was last written. A lap of a Grand site
        // adds markers the whole way round, so this writes a few dozen times over a few minutes and
        // does nothing at all once the site is fully known.
        Walked = walked;

        // **Timed apart from the walk, because it is a disk write and the walk is not.** A file
        // written on the tick thread stalls the frame it lands in, and inside the sweep's own
        // stopwatch it reads as the sweep having a bad frame - which is what a worst-frame column
        // showing twenty milliseconds against a ten millisecond average looks like either way.
        sweeping.Dispose();

        if ((_known.Count > _saved || ModsKnownCount() > _savedMods || _loneVerdicts > _savedLone) &&
            DateTime.UtcNow - _wrote > TimeSpan.FromSeconds(10))
            using (Spent.On("Scan.Tick/Keep"))
                Keep();
    }

    /// <summary>
    /// Whether a spot belongs to no dig site: further from every detonator than a site reaches.
    ///
    /// **Maps are full of remnants attached to no expedition at all**, and they are a different
    /// thing to deal with: there is no chain to plan, nothing to detonate, and the whole of the job
    /// is to pick a reward and shatter it. Telling them apart from a dig site's own remnants is the
    /// distance to the nearest detonator and nothing else.
    /// </summary>
    public bool Loose(GameController gc, Vector2 grid)
    {
        var sites = DetonatorPositions(gc);

        if (sites.Count > 0 &&
            Vector2.Distance(Nearest(sites, grid), grid) <= Detonator.SiteReach(gc))
            return false;

        // **And nothing a dig site is made of standing beside it.**
        //
        // The detonator is one point and it can be the wrong one to measure from: a Grand site
        // sprawls, a detonator can be destroyed or never seen, and a map holds two. What actually
        // says "this is part of an expedition" is the company it keeps - a dig site is a field of
        // monster, elite and chest markers, and a remnant standing among them belongs to it
        // whatever the distance to the machine works out at.
        //
        // Remnants themselves are not counted. Two lone remnants near each other would each make
        // the other look like a dig site, which is exactly backwards.
        foreach (var target in Targets)
        {
            if (target.Kind is not (TargetKind.Monster or TargetKind.Elite or TargetKind.Chest))
                continue;

            if (Vector2.DistanceSquared(target.Grid, grid) <= Company * Company)
                return false;
        }

        return true;
    }

    /// <summary>
    /// How close a dig site's markers have to be for a remnant to count as one of theirs.
    ///
    /// Generous, because the cost of the two mistakes is not symmetrical. Calling a site's remnant
    /// loose means the key opens a window instead of planning, which is a wasted press at the worst
    /// moment; calling a lone remnant part of a site means the key ignores it, which is the
    /// behaviour that was there before and no worse than it.
    /// </summary>
    private const float Company = 150f;

    /// <summary>Every detonator in the area, which is what tells one dig site from another.</summary>
    private static List<Vector2> DetonatorPositions(GameController gc)
    {
        var found = new List<Vector2>();
        var encounters = Safe.Read(() => Detonator.Info(gc).Encounters, null);

        foreach (var encounter in encounters ?? new List<ExpeditionDetonatorEncounter>())
        {
            var at = Safe.Read(() => encounter.DetonatorGridPosition, default);

            if (at.X != 0 || at.Y != 0)
                found.Add(new Vector2(at.X, at.Y));
        }

        return found;
    }

    private static Vector2 Nearest(List<Vector2> sites, Vector2 grid)
    {
        if (sites.Count == 0)
            return Vector2.Zero;

        var nearest = sites[0];

        foreach (var site in sites)
        {
            if (Vector2.Distance(site, grid) < Vector2.Distance(nearest, grid))
                nearest = site;
        }

        return nearest;
    }

    /// <summary>
    /// A remnant's socket count, from the encounter data rather than from its state machine.
    ///
    /// **The state lies after a roll.** Observed on two rerolled remnants: it read four where the
    /// remnants had three and six, while the encounter data - the same source the recipe list is
    /// built from - was right both times. Counting sockets one way and pricing rewards the other
    /// describes two different remnants, and the per socket weight then pays for a socket that is
    /// not there.
    ///
    /// Falls back to the state when the data cannot be read at all, which beats nought.
    /// </summary>
    private static int SocketsOf(Target remnant)
    {
        var real = Valuation.SocketsOf(remnant?.Entity);

        return real > 0 ? real : (int)Math.Max(0, remnant?.State("sockets") ?? 0);
    }

    /// <summary>
    /// Brings a record made by an older classifier up to date, in place.
    ///
    /// **Changing how something is classified does not reclassify what is already on record.** The
    /// scan remembers a site for the life of the area and the file outlives even that, so on the
    /// map a change was written for - which is the map you are standing in when you write it -
    /// every one of the things it was meant to fix is already filed under the old answer and the
    /// sweep only hands it its entity back. Measured: eleven buried strongboxes went on reading as
    /// explodable scenery through three rebuilds, and the dump said "scenery 14" every time.
    ///
    /// Specific rather than general: reclassifying everything on every sweep would re-read the art
    /// and the components of every marker in the site sixty times a minute to arrive at the answer
    /// already stored, which is exactly what the existing-record path exists to avoid. This is a
    /// short list of classifications known to have moved, and an entry can be dropped once no
    /// remembered file is old enough to hold one.
    /// </summary>
    private static void Correct(Target known)
    {
        // The mound over a buried strongbox, which used to be filed with the huts and the barrels.
        // See TargetKind.Strongbox.
        if (known.Kind == TargetKind.Scenery &&
            known.Meta.Contains("ExplodingFill_StrongBox", StringComparison.OrdinalIgnoreCase))
        {
            known.Kind = TargetKind.Strongbox;
            known.Explodes = true;
        }

        // Classified before Explodes existed, which reads as "a blast does nothing to it" and would
        // quietly drop a real one to nothing. Asked again rather than assumed either way.
        if (known.Kind == TargetKind.Strongbox && !known.Explodes)
            known.Explodes = known.State("inherent_explosion_radius") >= 0;
    }

    /// <summary>
    /// Puts a live handle back on a target already on record, and takes in what can have changed.
    ///
    /// **Shared by the walk and by the arrival callback**, which is the point of it being a method:
    /// an entity reaching the plugin through EntityAdded and the same entity being found by a walk
    /// are the same event, and the two paths disagreeing about what to do with it is the kind of
    /// difference that shows up as a marker that works only after a reload. See Arrived and Tick.
    /// </summary>
    private void Attach(Target existing, Entity entity)
    {
            // Only if it is the same kind of thing standing there.
            //
            // The record is keyed by grid cell and entities share cells: every remnant has a
            // RuneEncounterController on exactly its position, and while the sweep was reading
            // the monster bucket looking for sentries that controller arrived second and took
            // the remnant's handle. The remnant then had a live entity with no
            // "activated" state on it, read as not spent, and the post-expedition pass could
            // not see a single shatterable remnant in a site full of them.
            string standing;

            using (Spent.On("Sweep/Metadata"))
                standing = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (existing.Meta.Length > 0 &&
                !string.Equals(standing, existing.Meta, StringComparison.Ordinal))
            {
                // One exception, and it is the whole reason a strongbox went unseen: the box
                // shares its cell with the mound the blast breaks, so whichever arrived second
                // was walked past. The second one is not a rival for the cell - it is the other
                // half of the same thing. See Target.Holds.
                //
                // **Either half can be the one on record.** A record made from the chest walked past the
                // mound arriving second, and on 2026-10-02 every strongbox on a site read one entity: at
                // (758,873), on the edge of a blast, the mound read glow_epk 1 and the chest 0, so the
                // placement check called the box unlit. The mound is what the blast breaks.
                var chest = standing.Contains("/StrongBoxes/", StringComparison.OrdinalIgnoreCase);
                var mound = standing.Contains("ExplodingFill_StrongBox", StringComparison.OrdinalIgnoreCase);

                if (existing.Kind == TargetKind.Strongbox && (chest || mound))
                {
                    // **The handle is taken every sweep, what it says is read once.** Holds is
                    // written to the site file and comes back with it, so a site restored from
                    // memory already knows what the box is - and a test for "not known yet"
                    // therefore never fired again, which would have left Held null on every
                    // site the plugin had seen before. The entity itself is also remade as it
                    // streams, so an address kept from one sweep is stale by the next.
                    existing.Held = entity;

                    if (chest && existing.Holds.Length == 0)
                    {
                        existing.Holds = standing;
                        existing.Rarity = Rarity(entity);
                    }
                }

                return;
            }

            existing.Entity = entity;

            using (Spent.On("Sweep/Correct"))
                Correct(existing);

            if (existing.Kind == TargetKind.Remnant)
            {
                int sockets;

                using (Spent.On("Sweep/Sockets"))
                    sockets = SocketsOf(existing);
                var rolled = existing.Rerolled;

                // A roll makes it a different remnant in the same place. The flag says so, and
                // so does the socket count - a roll can change that, which is confirmed rather
                // than assumed - so either moving means everything cached about it is about a
                // remnant that is gone.
                //
                // **A socket count of nought is the client not answering, not a remnant with no
                // sockets.** SocketsOf falls back to zero when the entity cannot be read, and the
                // client unloads entities while the window is not focused - so alt-tabbing made
                // every remnant look rolled. Their rewards were forgotten, and once a roll also
                // started a re-solve the next plan was built against a site whose remnants were
                // suddenly worth nothing at all. Measured across four dumps of one site: fourteen
                // priced remnants down to three, both must-takes gone with them, and the score on
                // screen falling from 18,266 to 10,209 with no setting touched.
                //
                // So a change is only believed from a reading that exists. An unreadable remnant
                // keeps what it had, including its rewards, until the client answers again - and
                // a real roll is still caught the moment it does, because the count it is
                // compared against was never overwritten with the nought.
                if (sockets > 0)
                {
                    if (rolled != existing.WasRolled || sockets != existing.Sockets)
                    {
                        existing.Forget();
                        _rolled = true;
                    }

                    existing.Sockets = sockets;
                    existing.WasRolled = rolled;
                }
            }
    }
    /// <summary>
    /// What this entity is, or null for the overwhelming majority that are nothing to a dig site.
    ///
    /// The grid position is handed in rather than read again: the sweep has already read it to key
    /// its record, and a component read measured 3.3 microseconds against the 23 this whole method
    /// costs. Its one caller has it. See Tick.
    /// </summary>
    private static Target Classify(Entity entity, Vector2 grid)
    {
        if (grid == Vector2.Zero)
            return null;

        var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

        if (IgnoredByTable(metadata))
            return null;

        // **Asked once.** This was five separate calls on the same string, one per kind tested, and
        // each one scans every matching rule in the weight reference table and then parses the
        // winning row's Kind out of a word. The answer cannot differ between them.
        var kinded = Kinded(metadata);

        // **Read on the way out, not on the way in.**
        //
        // These are five component reads, and they used to run for every entity the sweep touched.
        // The comment that stood here said they cost a cell that is classified once and then
        // remembered - which is true of a cell that classifies. Everything the sweep REJECTS is
        // classified again on the next sweep and every sweep after it, because only successes go on
        // the record, and on a map that is nearly every entity: measured at 791 classifications a
        // sweep against 788 entities walked, twenty-three microseconds each, 84% of the sweep.
        //
        // So each one is read at the point it is used. Every path that builds a Target still reads
        // all of them, exactly as before; the fall-through that returns null now reads none. The
        // relic test below needs only the entity, which is why it can stay where it is.
        //
        // Cached in a local so a path that mentions one twice still reads it once.
        var radius = float.NaN;

        float Radius() => float.IsNaN(radius)
            ? radius = Safe.Read(entity, static e => e.GetComponent<Render>()?.Bounds.X ?? 0f, 0f) /
                       Detonator.GridToWorld
            : radius;

        string art = null, icon = null, mods = null, rarity = null;

        string Arted() => art ??= Art(entity) ?? "";
        string Iconed() => icon ??= Icon(entity) ?? "";
        string Modded() => mods ??= Mods(entity) ?? "";
        string Raritied() => rarity ??= Rarity(entity) ?? "";

        if (kinded == TargetKind.Sentry)
        {
            var sentry = new Target
            {
                Grid = grid,
                Kind = TargetKind.Sentry,
                Radius = Radius(),
                Entity = entity,
                Meta = metadata,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };

            // Already woken, so there is nothing left for a blast to do with it.
            return sentry.State("activated") >= 1 ? null : sentry;
        }

        if (kinded == TargetKind.Remnant)
        {
            var remnant = new Target
            {
                Grid = grid,
                Kind = TargetKind.Remnant,
                Radius = Radius(),
                Entity = entity,
                Meta = metadata,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };

            // A spent remnant is KEPT, and that is a reversal worth explaining.
            //
            // It used to be refused here, on the reasoning that a used remnant is scenery. It is
            // not: it still carries a shatter button, and refusing it meant that after a plugin
            // reload beside a finished dig site there were no spent remnants in the scan at all -
            // so the post-expedition pass found nothing to do and F4 answered "there is no plan to
            // place". The spawn census wants them too, to see the site's state.
            //
            // Everything that should ignore one already asks Target.Spent: the overlay draws
            // nothing for it, the planner drops it from the content, and the pricing below skips
            // it. Keeping it costs a dictionary entry and answers two questions.

            remnant.Sockets = SocketsOf(remnant);

            // Read now so the first sweep after this does not mistake a remnant that was ALREADY
            // rolled before anybody saw it for one that has just been rolled.
            remnant.WasRolled = remnant.Rerolled;

            return remnant;
        }

        // Blast-activated scenery that is not a marker at all.
        //
        // **A Grand site scatters objects an explosive sets off that no marker points at.** The clam
        // is the one seen so far: a terrain object under Logbook_Reef that drops gold, which is
        // worth catching when the chain passes it and never worth a detour. It is kept as content
        // so the search can take it for free, at a weight that cannot pull a link out of position.
        //
        // A siren egg is the other, and the two spawner pins standing beside it are neither - they
        // carry no state machine and nothing sets them off, so they fall through to null here. See
        // TargetKind.Hatch for why they say nothing about which egg is which.
        // **The row carries the name, the code carries only the exception.** A matches cell cannot say
        // "and not a Spawner", so that half stays here - but it is the only half here, and the string
        // the binding turns on is in the table where it can be read and corrected.
        if (kinded == TargetKind.Hatch &&
            !metadata.Contains("Spawner", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Hatch,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // A gate with its monsters right behind it. See TargetKind.Hatch for why this is not an
        // entrance: the rhoas fight the moment it opens, so the blast unearths them.
        if (metadata.Contains("RhoaEncounterGate", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Caged,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // A relic, told by its own state rather than by its name. See TargetKind.Relic.
        //
        // **The name rule lasted one tileset.** "Relic in the metadata" caught the Peninsula's
        // GoblinRelic and missed the Prairie's wisp traps, which are the same object in every way
        // that matters - ExpeditionPOI icon, ObjectMagicProperties and Stats, and an expedition_relic
        // state reading 7, 8 and 9 where the goblin one read 3. The state is the game saying "this is
        // a relic, and this is which"; the name is a tileset's choice of words and will be different
        // again next time.
        //
        // **WispTrap_Primal, _Vivid and _Wild are now verified, and the doubt recorded here was
        // right until they were.** No entity dump held a wisp trap for a long time - they appeared
        // only in marker_extent.csv and streaming.csv, which record the art and not the metadata -
        // and three table rows had been bound to these strings on the strength of this comment alone,
        // which is why they were unbound again. Vivid, wild and primal are also Path of Exile 1
        // Affliction vocabulary, so the resemblance was not evidence.
        //
        // A dump of Grazed Prairie holds all three: six entities on
        // Logbook_Prairie/Objects/WispTrap_{Primal,Vivid,Wild}, each with a ground label reading
        // "Imprisoned Primal Wisp" and so on, and Noted naming the art each answered for. The rows
        // are bound again on that, by art, one per wisp - see Weighing.RelicWeightFromMods. The
        // state is still what says "this is a relic and this is which"; the name is a tileset's
        // choice of words and may differ in the next area that ships one.
        var relic = new Target
        {
            Grid = grid,
            Kind = TargetKind.Relic,
            Meta = metadata,
            Radius = Radius(),
            Entity = entity,
            Art = Arted(),
            Icon = Iconed(),
            Mods = Modded(),
            Rarity = Raritied(),
        };

        if (relic.State("expedition_relic") > 0)
            return relic;

        // A monolith: a boss behind a blast rather than monsters. The row matches any metadata naming
        // Monolith, so the essence encounter's crystals are turned away here. See TargetKind.Monolith.
        if (kinded == TargetKind.Monolith && metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Monolith,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // Huts, barrels, the arena gate, and whatever the next tileset calls them. See
        // TargetKind.Scenery - the gate is here rather than with the entrances because of what is
        // behind it, which was looked at rather than assumed.
        // Before the general explodable case, because this one is not a hut. See Target.Holds.
        if (metadata.Contains("ExplodingFill_StrongBox", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Strongbox,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),

                // The mound IS the explosion - it is what the blast breaks - so it never has to ask.
                Explodes = true,
            };
        }

        // ShatterDoodadNoLoot says what it is in its own name: something a blast breaks with nothing
        // behind it. Filed with the huts and the barrels, which is the same bargain.
        //
        // **Open question on the ARENA gate, which is here for holding rogue exiles.** A boss cave
        // was opened and held one rogue exile too, and that is priced at 80 - a few rares. Two names
        // for the same contents at scenery and at 80 cannot both be right. Must-take an arena gate
        // and see what comes out; if it is the same single unique, this line belongs with the
        // entrances above rather than with the barrels.
        // The barrel, before the scenery it would otherwise be filed with. Matched by name, which
        // catches ExplodingFill_BoomBarrel and nothing else - a tileset that ships a barrel under
        // another name falls past here to Unknown, and is picked up for its blast by Target.Sets
        // rather than by this kind. See TargetKind.Barrel.
        if (metadata.Contains("BoomBarrel", StringComparison.OrdinalIgnoreCase))
        {
            var barrel = new Target
            {
                Grid = grid,
                Kind = TargetKind.Barrel,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };

            // Already gone off, or nothing to go off with. The radius reads itself from the state
            // the first time it is asked, so a barrel whose entity is not loaded yet is still filed
            // and fills in when it is. See Target.Sets.
            return barrel.State("expedition_detonated") >= 1 ? null : barrel;
        }

        if (kinded == TargetKind.Scenery)
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Scenery,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // A plain destructible container. It is filed under Metadata/Chests like the reward chests
        // and is nothing like one - white, no minimap icon, no tier to read - so it takes the
        // lowest chest weight rather than an identity of its own.
        if (metadata.Contains("GenericShatterable", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Chest,
                Tier = ChestTier.Common,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // The way into a sub-area. See TargetKind.Entrance - this sits above the marker check
        // because it is not a marker, and it was drawing an "unknown blast target" line for it.
        // The way into a sub-area, and the Prairie gate with chests behind it. Both are the same
        // trade: blow it open, walk in, collect. Counted in game, the gate held six chests - four
        // magic and two rare - against the sub-area cap's four, which is the same order of thing and
        // priced from the same number. See TargetKind.Entrance.
        //
        // The ARENA gate is not here, and the difference is what is behind it rather than what it
        // looks like: rogue exiles, which are worth almost nothing, so it sits with the scenery.
        // **EncasedShrine was here and has been taken out, because one turned up and is not an
        // entrance.** Digsite ships `Objects/EncasedShrine` wearing `precursorsulfurpile01.ao`: a
        // sulfur pile 8.8 grid across on a 6x6 footprint, which a blast opens to reveal a shrine
        // rather than a way through the ground. Classifying it here cost it two things - kind:Entrance
        // weight 20 for anybody without a row of their own, and kind:Entrance size 188.04, so
        // Extents.Of measured a 8.8 grid object at 17.30 and the planner credited a catch from four
        // times its own radius away. It falls to Unknown now, which keeps its found: row in front of
        // whoever has to price it and lets a size typed there be read. See Extents.Of for why the row
        // and not the kind.
        //
        // **Two of the four that remain have never appeared in the game.** SubareaEntrance is on every
        // sub-area entrance seen - twenty-two sightings - GateBlocker is the Prairie gate, and
        // KaruiGateExplodable and BossCave are nought apiece. Those two are kept because this is the
        // CLASSIFIER and an arm that never fires costs nothing here: the failure it guards against is
        // an entrance the scan ignores, which is worse than a branch nobody takes. The shrine is what
        // the other side of that bargain looks like, so a new string goes in only when something has
        // been seen to be behind it. See Weighing.Entrance.
        if (metadata.Contains("SubareaEntrance", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("GateBlocker", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("KaruiGateExplodable", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("BossCave", StringComparison.OrdinalIgnoreCase))
        {
            var entrance = new Target
            {
                Grid = grid,
                Kind = TargetKind.Entrance,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };

            // Already blown open, so there is nothing left for an explosive to do with it.
            return entrance.State("expedition_detonated") >= 1 ? null : entrance;
        }

        if (metadata.Contains("ClamChest", StringComparison.OrdinalIgnoreCase))
        {
            return new Target
            {
                Grid = grid,
                Kind = TargetKind.Chest,
                Tier = ChestTier.Gold,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };
        }

        // A strongbox an explosive digs up. See TargetKind.Strongbox.
        //
        // Gated on the state rather than on the metadata alone, because the state is the game's own
        // statement that a blast acts on this - the same test Unexpected uses. A strongbox standing
        // in an ordinary map has no such state and is not the planner's business.
        if (metadata.Contains("/StrongBoxes/", StringComparison.OrdinalIgnoreCase))
        {
            var box = new Target
            {
                Grid = grid,
                Kind = TargetKind.Strongbox,
                Meta = metadata,
                Radius = Radius(),
                Entity = entity,
                Art = Arted(),
                Icon = Iconed(),
                Mods = Modded(),
                Rarity = Raritied(),
            };

            if (box.State("glow_epk") < 0)
                return null;

            // A blast acts on it, or it is furniture with a lid. See Target.Explodes.
            box.Explodes = box.State("inherent_explosion_radius") >= 0;

            // Already dug up or already looted, so an explosive has nothing left to do with it.
            return box.State("expedition_detonated") >= 1 || box.State("opened") >= 1 ? null : box;
        }

        // **Anything else the game says a blast acts on, rather than nothing at all.**
        //
        // glow_epk is the state the client sets on content under the placement circle, so carrying
        // it is the game stating that an explosive does something here - and every rule above this
        // line is the plugin's own guess at what that something is. Falling off the end of those
        // guesses used to mean the object did not exist as far as the planner was concerned: no
        // marker, no weight, no route past it, and a new tileset's objects were invisible until
        // somebody read a dump and wrote a rule.
        //
        // Now it becomes a target with whatever weight has been decided for it, which is one until
        // somebody decides otherwise. See Unknowns and TargetKind.Unknown.
        var unknown = new Target
        {
            Grid = grid,
            Kind = TargetKind.Unknown,
            Meta = metadata,
            Radius = Radius(),
            Entity = entity,
            Art = Arted(),
            Icon = Iconed(),
            Mods = Modded(),
            Rarity = Raritied(),
        };

        if (!metadata.StartsWith(MarkerMetadata, StringComparison.Ordinal))
        {
            // **The occupant is the target, not the casing - which is the other way round from how
            // this started.**
            //
            // An encased monster is two entities at one cell: ExpeditionEncasedMonster, and the
            // monster itself carrying be_free and glow_epk. Both are blast targets by the game's own
            // test, and counting both is one piece of content with two weights - so one of them has
            // to go.
            //
            // The casing goes. It is a generic shell reused for whatever a tileset wants to encase,
            // so its name says nothing about what breaking it buys, and every one of them would
            // share a single weight covering a white zealot and whatever else the game puts in one.
            // The monster inside is the thing with a name, a rarity and a value, and it carries its
            // own glow_epk - the game treats it as a blast target in its own right, not merely as a
            // consequence of one.
            if (metadata.Contains("EncasedMonster", StringComparison.OrdinalIgnoreCase))
                return null;

            if (unknown.State("glow_epk") < 0 || unknown.State("expedition_detonated") >= 1)
                return null;

            // Filing waits until everything that names it has arrived, which the weighing does on
            // its own rhythm. See Unknowns.Register.
            return unknown;
        }

        // The last word on what an object is, and the only reads this path needs. Everything that
        // falls to here and is not named by its art returns without touching the rest.
        var kind = Kind(Arted()) ?? Kind(Iconed());

        if (kind == null)
            return null;

        return new Target
        {
            Grid = grid,
            Kind = kind.Value,
            Tier = kind == TargetKind.Chest ? Tier(Arted(), Iconed()) : ChestTier.Unknown,
            Art = Arted(),
            Icon = Iconed(),
            Mods = Modded(),
            Rarity = Raritied(),
            Meta = metadata,
            Radius = Radius(),
            Entity = entity,
        };
    }

    /// <summary>The rarity the game states for this object, as a word. See Target.Rarity.</summary>
    private static string Rarity(Entity entity)
    {
        LeafCalls.ComponentReads++;

        var magic = Safe.Read(() => entity.GetComponent<ObjectMagicProperties>(), null);

        return magic == null ? "" : Safe.Read(() => magic.Rarity.ToString(), "") ?? "";
    }

    /// <summary>What the object's own mods say it does, joined. See Target.Mods.</summary>
    internal static string Mods(Entity entity)
    {
        LeafCalls.ComponentReads++;

        var magic = Safe.Read(() => entity.GetComponent<ObjectMagicProperties>(), null);

        if (magic == null)
            return "";

        var mods = Safe.Read(() => magic.Mods, null);

        return mods == null ? "" : string.Join(",", mods);
    }

    /// <summary>
    /// Both modifier lists with their values, the implicit ones flagged. See Target.Valued.
    ///
    /// Empty rather than null on an unreadable component, so a caller cannot tell "no mods" from "not
    /// loaded" by the shape - the latch above is what handles the second case, by not latching.
    /// </summary>
    internal static (string Name, int[] Values, bool Implicit)[] Valued(Entity entity)
    {
        var magic = Safe.Read(() => entity.GetComponent<ObjectMagicProperties>(), null);

        if (magic == null)
            return null;

        var found = new List<(string, int[], bool)>();

        void Take(List<ItemMod> mods, bool implicitly)
        {
            if (mods == null)
                return;

            foreach (var mod in mods)
            {
                var name = Safe.Read(() => mod.RawName, null);

                if (string.IsNullOrEmpty(name))
                    continue;

                var values = Safe.Read(() => mod.Values, null);

                found.Add((name, values == null ? [] : values.ToArray(), implicitly));
            }
        }

        Take(Safe.Read(() => magic.ImplicitModData, null), true);
        Take(Safe.Read(() => magic.ExplicitModData, null), false);

        return found.Count == 0 ? null : found.ToArray();
    }

    /// <summary>The marker's model, lowercased, file name only. Empty until the game has streamed the art in.</summary>
    internal static string Art(Entity entity)
    {
        LeafCalls.ComponentReads++;

        var path = Safe.Read(() => entity.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Path, "") ?? "";
        var slash = path.LastIndexOf('/');

        return (slash < 0 ? path : path[(slash + 1)..]).ToLowerInvariant();
    }

    /// <summary>
    /// Which chest, from the marker art.
    ///
    /// The naming the game uses for what spawns - LeagueFaction1Common, LeagueFaction1Uncommon -
    /// says plainly there are tiers above these and factions beside them that one encounter was
    /// never going to show. So an art nobody has matched yet is Unknown rather than assumed Common.
    /// </summary>
    /// <summary>
    /// Which tier of chest a marker's art means, where the art says.
    ///
    /// **Grand Expeditions name their chests by contents rather than by rarity.** Observed on one:
    /// ChestMarkers/ChestCurrency, ChestUniques, ChestTrinkets, ChestMaps and ChestArmour, taller
    /// markers than the small sites use and on a different path entirely. What they are worth is
    /// not known yet - the names suggest a currency chest and a unique chest are not the same prize
    /// - so they are left unranked and take the middling weight rather than being guessed at.
    /// </summary>
    /// <summary>The minimap icon's name, which on a Grand site says more than the art does.</summary>
    internal static string Icon(Entity entity)
    {
        LeafCalls.ComponentReads++;

        var icon = Safe.Read(() => entity.GetComponent<MinimapIcon>(), null);

        return icon == null ? "" : Safe.Read(() => icon.IconDat?.Id, "") ?? "";
    }

    /// <summary>
    /// Which kind the table says this metadata is, or null where no row claims it.
    ///
    /// **The binding is the only copy.** Three kinds were decided here by a metadata test AND bound
    /// to a row in the table, which is one fact written twice with nothing keeping the copies in
    /// step - the same fault Scan.Tier had. A row with no matches cell is also a row you cannot tell
    /// anything about from the table: "Explodable scenery" sat there with a weight and no visible
    /// reason it ever applied.
    ///
    /// Only the kinds whose test is purely the metadata. Hatch excludes "Spawner" and Monolith needs
    /// "Expedition" as well, and a matches cell can say neither - so those two stay here and say so.
    /// See TargetKind.Monolith.
    ///
    /// **Branch ORDER still decides, and it has to.** ExplodingFill_StrongBox, _BoomBarrel and
    /// _BoxxesofGold are one family, and the first two are a strongbox and a barrel before they are
    /// scenery. The tests below run in the same order they always did; this only changes what each
    /// one asks.
    /// </summary>
    /// <summary>
    /// Whether the table says to ignore this metadata: the path: row it answers to has Ignored in its Kind cell. A
    /// row's id may open either end with a star, so one row can cover a family - path:Metadata/Monsters/VaalMonsters/
    /// Zealots/* covers every Vaal Zealot. See TableGrammar.Matched and IgnoredKind.
    /// </summary>
    internal static bool IgnoredByTable(string metadata) =>
        string.Equals(Wrt.Of(TableGrammar.Matched(metadata ?? "", TableGrammar.Path))?.Kind, IgnoredKind,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The word in a path: row's Kind cell that makes the scan drop what the row matches. See IgnoredByTable.</summary>
    internal const string IgnoredKind = "Ignored";

    private static TargetKind? Kinded(string metadata)
    {
        var id = TableGrammar.Matched(metadata ?? "", TableGrammar.Path);

        // The row's own Kind field, not its id. A classifier row is named for the metadata that finds
        // it now - "path:.../SentinelRandomEncounterObject" - so the kind cannot be read out of the
        // name and is stored beside it. See Wrt.Kinded for the lookup the other way.
        return Enum.TryParse<TargetKind>(Wrt.Of(id)?.Kind, out var kind) &&
               kind != TargetKind.Unknown
            ? kind
            : null;
    }

    private static ChestTier Tier(string art, string icon)
    {
        // **The icon first, because on a Grand site the art cannot tell these apart.** Every reward
        // chest there wears ChestCurrency.ao, dull and bright alike, and the minimap is where they
        // differ - RewardChestCurrency against RewardChestCurrencyRare. The bright one is reported to
        // drop a Divine Orb about two times in five, so treating the two as one chest throws away the
        // difference between an ordinary pickup and the best thing on the site.
        var said = Tiered(TableGrammar.Matched(icon ?? "", TableGrammar.Icon));

        if (said != ChestTier.Unknown)
            return said;

        // Then the model, which is what a dig site has instead of an icon: the three signpost
        // heights are the three tiers, and the row each art binds to IS the chest. It used to be a
        // marker row with the chest as its only child - a pass-through carrying no weight of its
        // own, x1 every time - so the art now binds straight to the chest and the tier is read off
        // the row it lands on. See the Excavated Chest rows.
        said = Tiered(TableGrammar.Matched(art ?? "", TableGrammar.Art));

        if (said != ChestTier.Unknown)
            return said;

        // **An icon that says reward chest and answers to no row.** Not Unknown, because the thing is
        // plainly a reward chest and calling it unclassified would drop it out of a Grand site's
        // scan; not a named tier, because nobody has said which. The generic row is where a variant
        // the game adds lands until somebody writes it one.
        return icon is { Length: > 0 } &&
               icon.StartsWith("RewardChest", StringComparison.OrdinalIgnoreCase)
            ? ChestTier.GrandGeneric
            : ChestTier.Unknown;
    }

    /// <summary>
    /// The tier a chest row's id names, or Unknown for a row that is not a chest.
    ///
    /// **This is the whole of what Scan.Tier used to hold in code.** Six icons and three marker arts
    /// were mapped to tiers here AND bound to the same rows in the table's matches cells - one fact
    /// written twice, with nothing keeping the two in step. Adding a reward chest variant meant
    /// editing a switch; getting the two copies to disagree meant a chest priced as one thing and
    /// drawn as another.
    ///
    /// Read off the row's own Kind field rather than parsed out of its id. Spelling the tier into the
    /// id made the id load-bearing for classification, so a chest already identified by one minimap
    /// icon had to carry a second name - and the second name was the one the code depended on. See
    /// Wrt.Tiered for the lookup in the other direction.
    /// </summary>
    private static ChestTier Tiered(string id) =>
        Enum.TryParse<ChestTier>(Wrt.Of(id)?.Kind, out var tier) ? tier : ChestTier.Unknown;

    /// <summary>
    /// Which content a marker stands for, from the model it is showing.
    ///
    /// Matched on a substring rather than the exact file because the art comes in numbered variants
    /// - monstermarker, monstermarker_02, monstermarker_03 - which are the same content wearing a
    /// different coat. The chest signposts are numbered the same way and each has its own height,
    /// which looked like a tier and is not: all three spawn the same Common chest.
    /// </summary>
    private static TargetKind? Kind(string art) => art switch
    {
            // Elites spawn rare monsters - elitemarker and elitemarker_02 both produced one, with
        // four and three modifiers respectively. Two samples of one each cannot separate "the art
        // predicts the modifier count" from "rares roll a varying number of modifiers", and the
        // second is the ordinary explanation, so no tier is read into the elite variants.
        // **Case insensitively, which is not a tidy-up.** A Grand Expedition's chests are at
        // Doodads/Leagues/Expedition/ChestMarkers/ChestCurrency.ao and friends - capital C, capital
        // M - and an ordinal match on "chestmarker" does not contain that. Observed on one site:
        // eighteen chests, nine of them currency and six unique, classified as nothing at all and
        // dropped from the scan before the planner ever saw them.
        _ when art.Contains("elitemarker", StringComparison.OrdinalIgnoreCase) => TargetKind.Elite,
        _ when art.Contains("monstermarker", StringComparison.OrdinalIgnoreCase) => TargetKind.Monster,
        _ when art.Contains("chestmarker", StringComparison.OrdinalIgnoreCase) => TargetKind.Chest,

        // The minimap icon, for anything whose art has not loaded.
        //
        // **A Grand site is walked past, not stood in, and art loads late.** Its reward chests are
        // ordinary ExpeditionMarker objects whose doodad says which kind they are - and from across
        // the map that doodad reads as an empty string, so eighteen of them classified as nothing
        // and were dropped. The minimap icon is set from the moment the entity exists, names the
        // chest precisely (RewardChestCurrency, RewardChestUnique and the rest) and is the same
        // source the tier is read from, so it is the better identity and not merely a fallback.
        _ when art.StartsWith("rewardchest", StringComparison.OrdinalIgnoreCase) => TargetKind.Chest,
        _ => null,
    };
}
