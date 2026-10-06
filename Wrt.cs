using ExileCore2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// What everything is worth, in two layers: what the plugin ships knowing, and what you disagree
/// with.
///
/// **One table, two files, and the second only holds the difference.**
///
/// - weight_reference_table_defaults.json is shipped. It is the accumulated answer - every weight,
///   scope, size and tag that somebody has worked out and that everybody should start from.
/// - weight_reference_table_custom.json is yours. A row lands in it the moment you change something,
///   and holds only what you changed.
///
/// Read custom first, then defaults. That is the whole of the resolution, and it is what makes the
/// status column honest: a row you have touched reads "custom", one answered by the shipped file
/// reads "default", and one neither file mentions reads "unset" - which is the plugin saying it is
/// valuing something on a placeholder, not that the value happens to be low.
///
/// **A custom row that matches the default is deleted on load.** That is what keeps the arrangement
/// from rotting. Without it a file only ever grows, and the day a shipped weight is improved,
/// everybody who once touched that row is frozen on their old answer and never finds out. Pruning
/// means your file empties itself as your findings are promoted into the shipped one, and you go
/// back to receiving improvements you did not make.
///
/// **The promotion loop this exists for**: play, fill rows in, and they collect in your custom file
/// as deviations. When you are happy, fold them into the defaults file and ship it. Everybody starts
/// from your answer, and your own file prunes itself back to empty.
///
/// **Ids are namespaced**, because the rows are not one kind of thing. A discovered object is filed
/// under the eight fields that identify it, a rune under its name, a recognised category under its
/// kind and tier - and in one table those have to be told apart at a glance rather than by how many
/// pipes they contain. See Id.
/// </summary>
internal static class Wrt
{
    /// <summary>What is known about one row. Every field is optional: absent means "no opinion".</summary>
    internal sealed class Row
    {
        /// <summary>
        /// What this row is called on screen.
        ///
        /// **Said by the SHIPPED table and by nothing else.** The label used to come from the [Menu]
        /// attribute on a settings property, which meant the list of weights that exist, their
        /// names and their built-in values were all properties - and the table was a view of them
        /// rather than the thing itself. A custom row carries no name and inherits it; renaming a
        /// weight is a change to what ships, not to anybody's answer. See Label.
        /// </summary>
        public string Name;

        /// <summary>
        /// What this row is, for grouping and searching, and for nothing else.
        ///
        /// Entity, Monster, Rune, Effect, Modifier, Wave, Pack, Chest. It carries no arithmetic - its
        /// job is to let you find the things that are like this thing, which is what the Kind column
        /// was doing while also deciding what the row was worth.
        ///
        /// Shipped only, and excluded from Same and Empty for the same reason Name is: it is not an
        /// answer, so a row carrying only a type says nothing.
        /// </summary>
        public string Type;

        public float? Weight;

        /// <summary>How far from it a blast still catches it, in world units.</summary>
        public float? Size;

        /// <summary>What it is, comma separated, overriding what would be derived. See Tags.</summary>
        public string Tags;

        /// <summary>Whether a second one is worth anything. See Unknowns.Priced.Stacks.</summary>
        public bool? Stacks;

        /// <summary>
        /// What else you get for blowing this up - "monster.rare x1, remnant.wave x recipe.Runes".
        ///
        /// **The cell that ends the hardcoded composition.** A remnant being one wave per rune of so many
        /// monsters, a monstermarker being a weighted average of white and blue, a strongbox's packs
        /// splitting by rarity: all facts about the game, all of them in a switch in Weighing until
        /// now. A count is a float so a fraction is a probability, and a count may name a value the
        /// game states on a modifier rather than a number. See TableGrammar.Children.
        /// </summary>
        public string Children;

        /// <summary>
        /// What this does to everything it reaches - "monster *= 1.04 as monster_item_quantity".
        ///
        /// **One cell where Scope, Carries and Combines were three.** Those said the magnitude, the
        /// target and the grouping in three places and in three notations, so a row could not be read
        /// as a sentence and changing a propagation figure moved a weight elsewhere by an amount no
        /// cell explained. Here the factor is written the way the player reads it on the relic, and
        /// the optional "as" clause says the one thing grouping ever meant: shares a stat with.
        ///
        /// Null while the row still speaks v1, which is every row until the defaults file is rewritten
        /// - see Translated, which reads the old three and says what they would say here.
        /// </summary>
        public string Effect;

        /// <summary>
        /// What binds this row to something in the world - "art:elitemarker",
        /// "ObjectMagicProperties.ExplicitModData:ChestSummonRares".
        ///
        /// **The layer that had no table representation at all.** Which row a scanned entity answers
        /// to was decided by Scan.Kind and Scan.Tier - a switch over art file names - and then mapped
        /// to an id by another switch in Weighing. So the table could not say that elitemarker.ao
        /// spawns a rare monster: the marker and the monster had been merged into one row before the
        /// table ever saw them, and a patch adding elitemarker_04.ao needed a code change.
        ///
        /// Several forms, one per thing the game states. See TableGrammar.Matched.
        /// </summary>
        public string Matches;

        /// <summary>
        /// How often a fresh socket comes up as this, as a fraction. Rune rows only.
        ///
        /// **The third store, closed.** These lived in an array in Rolls.cs - twenty-nine of them
        /// against thirty-four rune rows in a live table - so the list of runes that exist was written
        /// down twice and the two could disagree without anybody noticing.
        ///
        /// Measured rather than read from the game, which is settled rather than open:
        /// Expedition2RunesWeight is named for the weights and exposes none - Id, SlotCount, RuneSlot,
        /// Rune, Level, and no frequency. Which runes a LEVEL admits is readable, from that Level
        /// field, so the pool is gated by the game and weighted by this.
        ///
        /// Used in exactly two places - the draw pool's total and the reroll advisor's enumeration -
        /// and never in the score of a remnant whose runes are known, which is every remnant the
        /// planner sees. See TableGrammar and reroll_plan.md.
        /// </summary>
        public float? Share;

        /// <summary>
        /// Whether anybody has actually decided this row's weight, or it is still the default.
        ///
        /// **This is what "unset" means, and it had nowhere to live.** A row's Status was worked out
        /// from which FILE it was in - shipped, yours, or neither - which cannot express "a discovered
        /// object nobody has priced yet". That state lived in a second store, unknown.json, along with
        /// the weight itself, so the one thing the table exists to show was the one thing it did not
        /// own. See Unknowns.
        ///
        /// Null means the question does not arise: every shipped row is an answer somebody wrote.
        /// </summary>
        public bool? Set;

        /// <summary>
        /// What the GAME calls this, where the game has told us. Never derived, never invented.
        ///
        /// **Separate from Name because they answer different questions.** Name is how the table reads
        /// - "Strongbox: Blacksmith's" - shaped so searching "Strongbox:" isolates the family and
        /// nothing else. This is the words a player sees on the ground, "Blacksmith's Strongbox",
        /// which is how you know the row is about the thing in front of you.
        ///
        /// Empty means nobody has read it yet, and stays empty rather than being filled with our own
        /// wording. Today's audits turned up a dozen names that looked like the game's and were ours -
        /// "Cartographer's Strongbox" on the Caster base, "Bright currency", "WispTrap_Vivid" - so a
        /// blank here is worth more than a plausible guess.
        /// </summary>
        public string NameInGame;

        /// <summary>
        /// What the scan classified this as when it filed it, for rows nothing else names; on a path: row, what the
        /// scan classifies whatever the row matches as. Ignored on a path: row drops what it matches from the scan
        /// altogether. See Scan.IgnoredByTable.
        /// </summary>
        public string Kind;

        /// <summary>Which object granted this effect, where one did. See Unknowns.Granted.</summary>
        public string Granter;

        /// <summary>When this was last met in a dig site, and when it was first met.</summary>
        public DateTime? Seen;

        /// <summary>
        /// A shallow copy, so a caller can ask what a change actually moved. See Wrt.Set.
        ///
        /// Every field is a value or a string, so shallow is whole. Written here rather than at the
        /// call site because the fields are public and a copy assembled by hand out there would
        /// quietly stop copying whichever field is added next.
        /// </summary>
        public Row Copy() => (Row)MemberwiseClone();

        public DateTime? First;

        public bool Same(Row other) =>
            other != null &&
            Nearly(Weight, other.Weight) &&

            Nearly(Size, other.Size) &&
            Stacks == other.Stacks &&
            Alike(Children, other.Children) &&
            Alike(Effect, other.Effect) &&
            Alike(Matches, other.Matches) &&
            Nearly(Share, other.Share) &&
            Decided(Set) == Decided(other.Set) &&
            Alike(Kind, other.Kind) &&
            Alike(NameInGame, other.NameInGame) &&
            Alike(Granter, other.Granter) &&
            Alike(Tags, other.Tags);

        /// <summary>
        /// Whether a Set cell says the weight is decided. Blank counts as decided, because a blank Set on a shipped row
        /// means an answer somebody wrote, and on a custom row means no opinion, which inherits the shipped row's. So a
        /// row marked decided agrees with a shipped row, and only one marked undecided differs from it: "custom" is a
        /// value changed from the shipped one, not a row somebody confirmed.
        /// </summary>
        public static bool Decided(bool? set) => set != false;

        // Seen and First are deliberately absent above. They are observations rather than answers, so
        // two rows that agree about everything decided are the same row whether or not one was met
        // more recently - and including them would stop a row ever being promoted or pruned, since
        // the clock always differs.

        /// <summary>
        /// Whether two answers are the same answer, where NOTHING and NO OPINION are different.
        ///
        /// **An empty string is a decision and null is its absence.** "This object carries no tags"
        /// is a thing somebody can mean - an effect that modifies effects would want exactly that,
        /// and so would anything a future patch makes unreachable - and it has to survive a save, a
        /// load and the prune that drops rows agreeing with the shipped answer. Folding the two
        /// together is what made clearing a cell impossible: it stored, then read back as silence,
        /// and the derived words painted over it a second later.
        /// </summary>
        private static bool Alike(string a, string b) =>
            a == null ? b == null : b != null && string.Equals(a, b, StringComparison.Ordinal);

        /// <summary>Floats written to three decimals, so a round trip through the file matches.</summary>
        private static bool Nearly(float? a, float? b) =>
            a == null && b == null || a != null && b != null && MathF.Abs(a.Value - b.Value) < 0.0005f;

        // Null in every field, not empty in them: a row saying "no tags" is a row with something
        // to say. See Alike.
        // Name is deliberately absent: it is not an answer, it is what the question is called, so a
        // row carrying only a name says nothing and a row that differs only by name differs in
        // nothing. See Name.
        public bool Empty =>
            Weight == null && Size == null && Stacks == null && Tags == null &&
            Children == null && Effect == null && Matches == null && Share == null &&
            Set == null && Kind == null && Granter == null && NameInGame == null;

        // Seen and First are left out on purpose. A row holding nothing but "I saw this once" has no
        // answer in it, and keeping it alive for that would stop the custom file ever emptying - the
        // clock moves every session, so such a row would never be prunable again.
    }

    /// <summary>How a row is named, so the three kinds of thing are told apart at a glance.</summary>
    internal static class Id
    {
        /// <summary>An object the plugin could not classify, under the key that identifies it.</summary>
        public static string Found(string key) => "found:" + key;

        /// <summary>A rune, under its name.</summary>
        public static string Rune(string name) => "rune:" + (name ?? "").ToLowerInvariant();

        /// <summary>
        /// The name back out of a rune's key, or null where the key is not a rune's.
        ///
        /// The inverse of Rune, kept beside it so the prefix is written once. The name that comes
        /// back is lower case, because that is what the key holds - anything showing it to somebody
        /// wants RuneInfo.Called, which is the game's own spelling.
        /// </summary>
        public static string RuneNamed(string id) =>
            id != null && id.StartsWith("rune:", StringComparison.Ordinal) ? id[5..] : null;

        /// <summary>A category the plugin recognises, under its kind and tier.</summary>
        public static string Kind(TargetKind kind, ChestTier tier)
        {
            var name = Named(kind);

            return tier == ChestTier.Unknown
                ? Whole(name) ? name : "kind:" + name
                : $"kind:{name}/{tier}";
        }

        /// <summary>
        /// What a kind is called in the table, which is not always what the enum calls it.
        ///
        /// **Three rows price monsters and the enum names none of them so you could tell.** A normal
        /// monster is TargetKind.Monster and a rare one is TargetKind.Elite, so the rows read
        /// "kind:Monster" and "kind:Elite" while the sliders behind them are labelled "Monster:
        /// Normal" and "Monster: Rare" - and the magic tier, which is a weight rather than a kind,
        /// had no kind row at all and sat under "setting:MagicMonster" among things that are not
        /// monsters.
        ///
        /// Reading the table you could not see that the three belong together, which matters because
        /// they are the three tiers of one thing: every remnant's waves are split between them, and a
        /// relic scoped to rare monsters reaches exactly one of the three.
        ///
        /// So the ids say it: MonsterNormal, MonsterMagic, MonsterRare. The enum is left alone - it
        /// names what the scanner classifies, which is a different question from what the table
        /// prices, and renaming TargetKind would drag the tag table and every saved file with it.
        /// </summary>
        public static string Named(TargetKind kind) => kind switch
        {
            TargetKind.Monster => "monster/normal",
            TargetKind.Elite => "monster/rare",
            _ => kind.ToString(),
        };

        /// <summary>
        /// Whether a name from Named is already a whole id, needing no prefix in front of it.
        ///
        /// **Only the rows another row NAMES are slugs, and that is the whole rule.** An id is either
        /// a key the code looks up - which nobody types, so its shape does not matter - or a word
        /// somebody writes in a Children cell, which has to read the same as everything else in that
        /// cell. Measured across the shipped table, ten of a hundred and nineteen rows are named by
        /// another row, and only the three monster tiers were still wearing a prefix: a cell read
        /// "kind:MonsterRare x1, pack/normal x10", two shapes in one line, one of them a namespace
        /// wearing a path's clothes.
        ///
        /// So the three moved and nothing else did. Giving a slug to rune:power or to a found: key
        /// buys nothing, because no cell will ever name them - a discovered row NAMES monster/rare in
        /// its own children, it is never named itself, and the direction only runs one way.
        /// </summary>
        private static bool Whole(string name) => name.Contains('/');

        /// <summary>
        /// The magic tier, which is a weight rather than a kind and belongs with the other two.
        ///
        /// Named here rather than through Setting so that all three monster rows read alike. See
        /// Named.
        /// </summary>
        public const string MagicMonsters = "monster/magic";

        /// <summary>
        /// What a row used to be called, for a file written before the tiers were named.
        ///
        /// A table is edited by hand and kept between versions, so a rename that quietly orphans
        /// somebody's row is a rename that loses their work - the row stays in the file, matches
        /// nothing, and the weight silently reverts. See Load.
        /// </summary>
        public static string[] Was(string id) => id switch
        {
            // Newest first. Two renames have happened to these three - kind:Monster and kind:Elite
            // became kind:MonsterNormal and kind:MonsterRare so the tiers read as one family, and
            // they are slugs now because a Children cell names them beside pack/normal. A file from
            // either era has to land on the current row, so every former name is listed rather than
            // only the last one.
            "monster/normal" => ["kind:MonsterNormal", "kind:Monster"],
            "monster/rare" => ["kind:MonsterRare", "kind:Elite"],
            "monster/magic" => ["kind:MonsterMagic", "setting:MagicMonster"],

            // The strongbox bases and the single-modifier rows, same rule.
            "mod:ChestSummonMagics" => ["mod/guarded_magics"],
            "path:Metadata/Chests/StrongBoxes/ResearchStrongboxExpedition" =>
                ["strongbox/research"],
            "path:Metadata/Chests/StrongBoxes/MartialStrongboxExpedition" =>
                ["strongbox/martial"],
            "path:Metadata/Chests/StrongBoxes/ArmourerStrongboxExpedition" =>
                ["strongbox/armourer"],
            "path:Metadata/Chests/StrongBoxes/JewellerStrongboxExpedition" =>
                ["strongbox/jeweller"],
            "path:Metadata/Chests/StrongBoxes/OrnateStrongboxExpedition" =>
                ["strongbox/ornate"],
            "path:Metadata/Chests/StrongBoxes/LargeStrongboxExpedition" =>
                ["strongbox/large"],
            "path:Metadata/Chests/StrongBoxes/CasterStrongboxExpedition" =>
                ["strongbox/caster"],
            "path:Metadata/Chests/StrongBoxes/BasicStrongboxExpedition" =>
                ["strongbox/basic"],
            "path:Metadata/Chests/StrongBoxes/BasicStrongboxHigh" => ["strongbox/basic_high"],
            "path:Metadata/Chests/StrongBoxes/JewellerStrongboxHigh" =>
                ["strongbox/jeweller_high"],
            "path:Metadata/Chests/StrongBoxes/LargeStrongboxHigh" => ["strongbox/large_high"],
            "path:Metadata/Chests/StrongBoxes/MartialStrongboxHigh" =>
                ["strongbox/martial_high"],
            "path:Metadata/Chests/StrongBoxes/ResearchStrongboxHigh" =>
                ["strongbox/research_high"],
            "path:Metadata/Chests/StrongBoxes/ArmourerStrongboxHigh" =>
                ["strongbox/armourer_high"],
            "path:Metadata/Chests/StrongBoxes/OrnateStrongboxHigh" => ["strongbox/ornate_high"],
            "path:Metadata/Chests/StrongBoxes/MapStrongboxExpedition" =>
                ["strongbox/map"],
            "path:Metadata/Chests/StrongBoxes/Unique/UniqueVaalStrongboxInteractionObject" =>
                ["strongbox/vaal"],
            "mod:StrongboxRareRobotGuardNoImmediateSpawn" => ["mod/strongbox_robot_guard"],
            "mod:ChestStrongboxSummonVaalMonstersImplicit" => ["mod/strongbox_vaal_monsters"],
            _ => [],
        };



        /// <summary>
        /// A shipped weight that prices something with no kind of its own.
        ///
        /// **Most of the shipped weights are not markers.** The wave composition, the per-socket
        /// bonus, the relic numbers - forty four weights and only seventeen name a kind the planner
        /// classifies. The rest had no id at all, so every column that keeps an answer in these
        /// files was dead on them, and the table read as though the plugin were refusing.
        ///
        /// The magic monster weight is the case that made it matter: it prices the magic monsters
        /// in every remnant's waves, those monsters carry tags like any other, and there was
        /// nowhere for anybody to say so.
        /// </summary>
        public static string Setting(string name) => "setting:" + (name ?? "");
    }

    private static readonly Dictionary<string, Row> Shipped = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Row> Mine = new(StringComparer.Ordinal);
    private static bool _dirty;

    /// <summary>Where YOUR file lives. Set once by the plugin; empty turns remembering off.</summary>
    public static string Home { get; set; } = "";

    /// <summary>
    /// Where the SHIPPED file lives, which is the plugin's own directory rather than yours.
    ///
    /// **A default that sits in a config folder is not shipped, it is just a second file somebody
    /// happens to have.** Both were read from Home, and since nothing ever put a defaults file
    /// there, Shipped was permanently empty: no row ever read "default", the prune that drops a
    /// custom row agreeing with the shipped answer never fired once, and the promotion loop this
    /// whole arrangement exists for had no second half.
    ///
    /// So the shipped answer travels with the code, where it can be version controlled and
    /// improved, and yours stays in your config where nothing overwrites it. See Load.
    /// </summary>
    public static string Source { get; set; } = "";

    public const string Defaults = "weight_reference_table_defaults.json";

    /// <summary>
    /// A short fingerprint of the table's two files, the shipped defaults and the player's own, so records made under
    /// different tables can be told apart: the census writes it beside each remnant's predicted counts. Revision
    /// restarts at every load and cannot do that. Worked out again when Revision moves; an edit reaches it once it has
    /// been saved. Empty before the table has a home. See Spawns.
    /// </summary>
    public static string TableFingerprint
    {
        get
        {
            var revision = Revision;

            if (_tableFingerprintAt == revision && _tableFingerprint != null)
                return _tableFingerprint;

            if (Home.Length == 0)
                return "";

            var text = new System.Text.StringBuilder();

            foreach (var path in new[] { Path.Combine(Source.Length > 0 ? Source : Home, Defaults), Path.Combine(Folder(), Custom) })
                text.Append(Safe.Read(() => File.Exists(path) ? File.ReadAllText(path) : "", "")).Append('');

            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));

            _tableFingerprint = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            _tableFingerprintAt = revision;

            return _tableFingerprint;
        }
    }

    private static string _tableFingerprint;

    private static int _tableFingerprintAt = -1;
    public const string Custom = "weight_reference_table_custom.json";

    public static Row Of(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        if (Resolved.TryGetValue(id, out var row))
            return row;

        return id.StartsWith("found:", StringComparison.Ordinal) ? FoundWithUnreadFields(id) : null;
    }

    /// <summary>Whether a row is stored under exactly this id, without the fallback Of makes for a discovered object.</summary>
    public static bool HasExactly(string id) => !string.IsNullOrEmpty(id) && Resolved.ContainsKey(id);

    /// <summary>
    /// The row a discovered object answers to when its id has an empty art, minimap icon or state list: the one row
    /// for the same object with those filled, where exactly one exists. Null when none does, or several could and it
    /// cannot be told which.
    ///
    /// **Those three are read off the live entity, and an entity out of load range reads them as empty.** An id built
    /// then matched no row, so the object was worth nothing and had no candidate spots: two encased Vaal Zealots on
    /// a Frigid Bluffs site (2026-10-04) lost their shipped weight of 2 whenever the player stood too far away, and
    /// with their spots went the ones a 74,000 chain was built on - every cold start from there topped out at
    /// 68,000. The same rule as the load's MergedOnMissingArtOrIcon, asked at lookup time; cached per revision.
    /// </summary>
    private static Row FoundWithUnreadFields(string id)
    {
        if (_foundFallbackAt != Revision)
        {
            _foundFallback.Clear();
            _foundFallbackAt = Revision;
        }

        return _foundFallback.GetOrAdd(id, key =>
        {
            var parts = key["found:".Length..].Split('|');

            if (parts.Length < 5 || (parts[2].Length > 0 && parts[3].Length > 0 && parts[4].Length > 0))
                return null;

            var prefix = "found:" + parts[0] + "|";
            Row only = null;
            var matched = 0;

            foreach (var (other, row) in Resolved)
            {
                if (!other.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var theirs = other["found:".Length..].Split('|');

                if (theirs.Length != parts.Length ||
                    !Enumerable.Range(0, parts.Length).All(i => theirs[i] == parts[i] || (i is 2 or 3 or 4 && parts[i].Length == 0)))
                    continue;

                only = row;
                matched++;
            }

            return matched == 1 ? only : null;
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Row> _foundFallback = new(StringComparer.Ordinal);

    private static int _foundFallbackAt = -1;

    /// <summary>Every row as it finally reads, yours layered over the shipped answer. See Of.</summary>
    private static readonly Dictionary<string, Row> Resolved = new(StringComparer.Ordinal);

    /// <summary>
    /// Rebuilds the merged view. Called wherever either layer changes, and nowhere else.
    ///
    /// Whole rather than incremental, because the two layers are a few hundred rows between them and
    /// a partial rebuild is a second rule about what shadows what. One rule, in one place.
    /// </summary>
    private static void Rebuilt()
    {
        Resolved.Clear();

        foreach (var id in Shipped.Keys.Concat(Mine.Keys).Distinct(StringComparer.Ordinal))
        {
            Shipped.TryGetValue(id, out var shipped);
            Mine.TryGetValue(id, out var mine);

            if (mine == null)
            {
                Resolved[id] = shipped;

                continue;
            }

            Resolved[id] = new Row
            {
                // Shipped first, matching Label: a name is what the question is called rather than an
                // answer to it, and a custom row written before names existed carries none.
                Name = shipped?.Name ?? mine.Name,
                Type = shipped?.Type ?? mine.Type,
                Weight = mine.Weight ?? shipped?.Weight,
                Size = mine.Size ?? shipped?.Size,
                Tags = mine.Tags ?? shipped?.Tags,
                Stacks = mine.Stacks ?? shipped?.Stacks,
                Children = mine.Children ?? shipped?.Children,
                Effect = mine.Effect ?? shipped?.Effect,
                Matches = mine.Matches ?? shipped?.Matches,
                Share = mine.Share ?? shipped?.Share,
                Set = mine.Set ?? shipped?.Set,
                Kind = mine.Kind ?? shipped?.Kind,
                NameInGame = mine.NameInGame ?? shipped?.NameInGame,
                Granter = mine.Granter ?? shipped?.Granter,
                // The later sighting wins rather than the custom layer: these are observations, not
                // opinions, and the shipped file can carry a first sighting from somebody else.
                Seen = mine.Seen > shipped?.Seen ? mine.Seen : shipped?.Seen ?? mine.Seen,
                First = mine.First < shipped?.First ? mine.First : shipped?.First ?? mine.First,
            };
        }
    }

    /// <summary>
    /// What a row is called: the shipped name, since that is the only place one is said.
    ///
    /// Shipped first rather than Of's usual Mine-then-Shipped, because a name is not something a
    /// custom file is meant to disagree about - and a custom row written before names existed has
    /// none at all, which would otherwise blank the label. See Row.Name.
    /// </summary>
    public static string Label(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";

        if (Shipped.TryGetValue(id, out var shipped) && shipped.Name != null)
            return shipped.Name;

        return Mine.TryGetValue(id, out var mine) ? mine.Name ?? "" : "";
    }

    /// <summary>
    /// Every row the shipped table defines, which is the list of weights that exist.
    ///
    /// This is what replaced reflecting over the settings properties. See Catalogue.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, Row>> Standing => Shipped;

    /// <summary>Which layer answered for this row. See the class summary.</summary>
    public static string Status(string id) =>
        string.IsNullOrEmpty(id) ? "unset"
        : Mine.ContainsKey(id) ? "custom"
        : Shipped.ContainsKey(id) ? "default"
        : "unset";

    /// <summary>
    /// Every row that says it empowers, with the percentage the scoring will actually read.
    ///
    /// **There was no way to tell from a dump what multiplier a run used.** An empower measurement
    /// was run twice, at nought and at fifty, and the two dumps came back identical to the decimal -
    /// which is the right answer for "the setting did nothing" and equally the right answer for "the
    /// setting never arrived". Nothing anywhere said which, so the comparison could not be settled
    /// either way and an evening's runs were spent on a question the output could not answer.
    ///
    /// Reads the layers the same way Of does - yours shadowing the shipped file - so what it prints
    /// is what Weighing.GroupKeyOfEffect and Weighing.LiftOfChoice are going to see, not what is on disk. Those
    /// two differ exactly when an edit has not landed, which is the case worth catching.
    /// </summary>
    public static IEnumerable<(string Id, float Percent, string Layer)> Empowering()
    {
        foreach (var id in Mine.Keys.Concat(Shipped.Keys).Distinct(StringComparer.Ordinal))
        {
            var row = Of(id);

            if (row == null)
                continue;

            // Asked the way the scoring asks, which is the whole point of this line - see the round
            // trip printed beside it in the dump. Combining now recognises an empowering row by its
            // effect aiming at the rune tag, so this has to ask the same question rather than the
            // question that used to be equivalent to it.
            var bare = id.StartsWith("rune:", StringComparison.Ordinal) ? id[5..] : id;
            var lift = Weighing.Lift(bare);

            if (lift > 0f)
            {
                yield return (id, lift, Status(id));

                continue;
            }

            // No second way to say it. An empowering row used to be marked by a reserved word in
            // the Combines cell with its rate in the Scope; both are gone, and Lift above reads the
            // effect, which is where a rune that scales other runes states itself.
        }
    }

    /// <summary>What the shipped file says, ignoring yours - for deciding whether yours still differs.</summary>
    /// <summary>
    /// Which row stands for a chest tier, or null where none does.
    ///
    /// **The tier used to be spelled into the id** - "kind:Chest/GrandCurrency" - so Weighing built
    /// that string to find the row and Scan parsed it back to read the tier. That made the id
    /// load-bearing for classification, which is why no chest id could be renamed: a chest already
    /// identified by one minimap icon was carrying a second name, and the second name was the one the
    /// code depended on.
    ///
    /// The tier is a field now and this is the lookup, so an id is free to be what identifies the
    /// thing in the world. Rebuilt when the table moves, like every other index here.
    /// </summary>
    public static string RowOfTier(ChestTier tier)
    {
        if (_tieredAt != Revision)
        {
            _tiers.Clear();
            _tieredAt = Revision;

            foreach (var (id, _) in Resolved)
            {
                var said = Of(id)?.Kind;

                if (said != null && Enum.TryParse<ChestTier>(said, out var was) &&
                    was != ChestTier.Unknown)
                    _tiers.TryAdd(was, id);
            }
        }

        return _tiers.GetValueOrDefault(tier);
    }

    private static readonly Dictionary<ChestTier, string> _tiers = new();
    private static int _tieredAt = -1;

    /// <summary>
    /// Which row prices a whole kind, or null where none does. The companion of RowOfTier.
    ///
    /// **Replaces building "kind:Sentry" and hoping a row is called that.** A sentry's row is
    /// identified by the metadata path the scan matches, which is what the row is called now - so the
    /// code cannot construct the id and has to ask. See the row-id rule: an id names the thing in the
    /// world, and only a row another row takes as a CHILD gets a slug.
    /// </summary>
    public static string RowOfKind(TargetKind kind)
    {
        if (_kindedAt != Revision)
        {
            _kinds.Clear();
            _kindedAt = Revision;

            foreach (var (id, _) in Resolved)
            {
                var said = Of(id)?.Kind;

                if (said != null && Enum.TryParse<TargetKind>(said, out var was) &&
                    was != TargetKind.Unknown)
                    _kinds.TryAdd(was, id);
            }
        }

        return _kinds.GetValueOrDefault(kind);
    }

    private static readonly Dictionary<TargetKind, string> _kinds = new();
    private static int _kindedAt = -1;

    public static Row Default(string id) =>
        id != null && Shipped.TryGetValue(id, out var row) ? row : null;

    /// <summary>
    /// Changes one row, writing it into your file.
    ///
    /// A change that brings the row back into agreement with the shipped answer removes it instead,
    /// so agreeing with the default is spelt the same way as never having disagreed.
    /// </summary>
    public static void Set(string id, Action<Row> change)
    {
        if (string.IsNullOrEmpty(id) || change == null)
            return;

        // **A blank row, not a copy of the resolved one.** It used to start from whatever currently
        // answered, so that changing one field did not blank the rest - which was necessary while Of
        // returned one layer or the other and is harmful now that it merges. A copy freezes every
        // other field at today's shipped value, which is how a custom row became a snapshot that
        // never heard about an improved default again. See Of.
        //
        // Every field left null means "no opinion", and Of fills those from the shipped row, so
        // changing one cell changes one cell.
        if (!Mine.TryGetValue(id, out var row))
            row = new Row();

        // What it said before, so this can tell an answer moving from an observation being
        // recorded. See below.
        var was = row.Copy();

        change(row);

        var shipped = Default(id);

        if (row.Empty || row.Same(shipped))
            Mine.Remove(id);
        else
            Mine[id] = row;

        // **A row can change without any answer changing, and the revision must not claim one did.**
        //
        // Same and Empty both leave Seen and First out on purpose - they are observations rather
        // than answers - and this did not. Unknowns.Register stamps Seen on every object it files,
        // so meeting an object bumped the revision; Planning.Stale watches that number and turned
        // each bump into a full re-solve and a re-price of every remnant. Walking into one dig site
        // moved the table forty-four times with nobody editing anything, and the approach spent it
        // on solves that were cancelled a few hundred milliseconds later. It also rewrote the custom
        // file each time, which is how that file came to have a timestamp nobody could account for.
        //
        // The row model already knows the difference, so this asks it rather than deciding again.
        // The write still happens and is still saved - an observation is worth keeping - it simply
        // is not news.
        if (row.Same(was))
        {
            _dirty = true;
            _noted++;
            Rebuilt();

            return;
        }

        Moved($"a row was edited: {id}");
    }

    /// <summary>
    /// Writes that recorded an observation and moved no answer. See Set and TimesMoved.
    ///
    /// Counted because the file still gets written for them, and a dump saying the table has not
    /// moved beside a file with a fresh timestamp is the kind of pair that costs an evening.
    /// </summary>
    public static int TimesNoted => _noted;

    private static int _noted;

    /// <summary>Puts a row back to the shipped answer, or to nothing where there is none.</summary>
    public static void Forget(string id)
    {
        if (id != null && Mine.Remove(id))
            Moved($"a row was put back to the shipped answer: {id}");
    }

    /// <summary>
    /// How many times the table has changed, so a reader can tell its answer has gone stale.
    ///
    /// **A remnant is priced once per site and the table can change underneath it.** Scan skips a
    /// remnant whose rewards it already has, which is right while the pricing rule is fixed and wrong
    /// the moment somebody edits a weight or a scope: the rewards on file were computed under the old
    /// table and nothing re-ran them, so the ground text, the plan and the score all went on quoting
    /// an answer to a question that had been withdrawn. Measured on one site, an empower set to fifty
    /// scored identically to nought because the fifty never reached the cached valuation - the two
    /// dumps agreed to the decimal and the edit had simply not been applied.
    ///
    /// A counter rather than a clear, because the pricing is expensive and most frames change
    /// nothing: a reader records the revision it priced at and re-prices when it no longer matches.
    /// Bumped on every mutation and on a fresh Load, which is the reload case.
    /// </summary>
    public static int Revision { get; private set; }

    /// <summary>A row moved. Both things that must then happen, in one place. See Revision.</summary>
    private static void Moved(string why)
    {
        _dirty = true;
        Revision++;
        Rebuilt();
        Noting(why);
    }

    /// <summary>
    /// What last moved the table, and how often it has moved. See Revision.
    ///
    /// **A revision bump re-solves the site, and nothing said who asked for it.** Planning.Stale
    /// watches this number, so every bump costs an automatic solve and re-scores the plan on
    /// screen - and three dumps in a row reported "the reference table or a weight changed" as the
    /// last thing to ask for a search on a session where no weight had been touched. A counter
    /// with no account of itself cannot distinguish a player editing a row from a load rewriting
    /// the file underneath them, and those want completely different responses.
    /// </summary>
    public static string LastMovedBy { get; private set; } = "has not moved this session";

    public static int TimesMoved { get; private set; }

    private static void Noting(string why)
    {
        TimesMoved++;
        LastMovedBy = $"{why} (revision {Revision}, {DateTime.UtcNow:HH:mm:ss} UTC)";
    }

    /// <summary>
    /// The table's answer changed although no row did: something the rows are read against, such as the map
    /// modifiers that scale the remnant wave row, moved. Bumps Revision so every reader re-prices, and writes nothing.
    /// See TableGrammar.SetRemnantWaveScales.
    /// </summary>
    public static void Repriced(string why)
    {
        Revision++;
        Noting(why);
    }

    /// <summary>Everything you have changed, for the dump and for handing to somebody.</summary>
    public static IEnumerable<KeyValuePair<string, Row>> Yours => Mine;

    public static int Changed => Mine.Count;

    public static int Known => Shipped.Count;

    /// <summary>Whether the shipped table has a row under this id.</summary>
    public static bool HasShippedRow(string id) => id != null && Shipped.ContainsKey(id);

    /// <summary>Whether your file has a row under this id.</summary>
    public static bool HasCustomRow(string id) => id != null && Mine.ContainsKey(id);

    /// <summary>
    /// Reads both files. The shipped one first, so pruning can see what it says.
    ///
    /// Two directories, not one: the shipped answer ships with the plugin and yours lives in your
    /// config. See Source.
    /// </summary>
    /// <summary>
    /// Every row whose id has changed, so both halves of a rename can walk the same list.
    ///
    /// Renaming a row is two jobs - move the row, and rewrite the cells naming it - and they were two
    /// loops over two copies of this list. One of them fell behind once already, which is how a tuned
    /// "pack/normal: kind:MonsterNormal x10" lost its child. See Renamed and Rereferenced.
    /// </summary>
    private static readonly string[] Moves =
    {
        "monster/normal", "monster/rare", "monster/magic",
        "chest/normal", "chest/magic", "chest/rare", "remnant",
        "art:reefclam.ao", "icon:RewardChestCurrency", "icon:RewardChestCurrencyRare",
        "icon:RewardChestMaps", "icon:RewardChestUnique", "icon:RewardChestTrinkets",
        "icon:RewardChestGeneric", "icon:RewardChestArmour",
        "path:*SirenEgg*", "path:*Monolith*",
        "path:Metadata/MiscellaneousObjects/Sentinel/SentinelRandomEncounterObject",
        "path:Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_BoxxesofGold",
        "mod:ChestSummonMagics",
        "path:Metadata/Chests/StrongBoxes/ResearchStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/MartialStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/ArmourerStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/JewellerStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/OrnateStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/LargeStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/CasterStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/BasicStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/BasicStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/JewellerStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/LargeStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/MartialStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/ResearchStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/ArmourerStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/OrnateStrongboxHigh",
        "path:Metadata/Chests/StrongBoxes/MapStrongboxExpedition",
        "path:Metadata/Chests/StrongBoxes/Unique/UniqueVaalStrongboxInteractionObject",
        "mod:StrongboxRareRobotGuardNoImmediateSpawn",
        "mod:ChestStrongboxSummonVaalMonstersImplicit",
    };

    /// <summary>Moves a row written under a former id onto the current one. See Id.Was.</summary>
    private static void Renamed(Dictionary<string, Row> rows)
    {
        if (rows == null)
            return;

        foreach (var now in Moves)
        {
            // Newest former name first, so a file carrying two eras of the same row keeps the later
            // one. See Id.Was.
            foreach (var then in Id.Was(now))
            {
                if (!rows.TryGetValue(then, out var row))
                    continue;

                rows.Remove(then);

                // A row already under the new name wins: it is the more recent of the two, and
                // overwriting it with the older one would undo an edit rather than rescue it.
                rows.TryAdd(now, row);
            }
        }

        Rereferenced(rows);
    }

    /// <summary>
    /// Rewrites a Children cell that still names a row by a former id.
    ///
    /// **Renaming a row is only half of renaming it.** Moving kind:MonsterNormal to monster/normal
    /// rescued the row itself and left every cell POINTING at it saying the old name - so a tuned
    /// "pack/normal: kind:MonsterNormal x10" in a live custom file lost its child, and the three
    /// modifier rows built on that pack lost theirs in turn. One rename, five broken rows, and the
    /// shipped ones were fine throughout because only a custom cell had been edited to name the old
    /// id.
    ///
    /// It failed loudly - a red total and "child 'kind:MonsterNormal' has no row" - which is the
    /// table working. It should not have needed to.
    ///
    /// Only the id is rewritten, never the count: "x10" is somebody's tuning and this is a rename.
    /// </summary>
    private static void Rereferenced(Dictionary<string, Row> rows)
    {
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var now in Moves)
        {
            foreach (var then in Id.Was(now))
                moved[then] = now;
        }

        foreach (var (_, row) in rows)
        {
            if (row.Children == null)
                continue;

            var pieces = row.Children.Split(',');
            var touched = false;

            for (var i = 0; i < pieces.Length; i++)
            {
                var part = pieces[i].Trim();
                var at = part.LastIndexOf(" x", StringComparison.Ordinal);

                if (at < 0 || !moved.TryGetValue(part[..at].Trim(), out var name))
                    continue;

                pieces[i] = name + part[at..];
                touched = true;
            }

            if (touched)
                row.Children = string.Join(", ", pieces.Select(x => x.Trim()));
        }
    }

    /// <summary>
    /// Drops the fields of a custom row that merely repeat the shipped answer.
    ///
    /// **Every custom file written before Of merged is a full snapshot**, because Set copied the whole
    /// resolved row whenever one cell was touched. Those snapshots still read correctly - every field
    /// is set, so every field wins - but they are frozen: the row can never hear about an improved
    /// default again, and a shipped field added later is shadowed by the null beside it.
    ///
    /// Safe by construction. A field equal to the shipped value resolves to that value whether it is
    /// present or absent, so removing it cannot change what anything reads. What it changes is what
    /// happens NEXT time the shipped answer moves.
    ///
    /// This is the half of the promotion loop that was missing. The class summary says a custom file
    /// "empties itself as your findings are promoted into the shipped one"; the prune that does it
    /// only ever fired on a row that agreed in EVERY field, which a snapshot almost never does. Now
    /// agreement is per field, so a row empties a cell at a time and Empty finishes it off.
    /// </summary>
    private static bool Thinned(string id, Row mine)
    {
        var shipped = Default(id);

        if (shipped == null || mine == null)
            return false;

        var moved = false;

        // Name and Type are never answers - see Row.Name and Row.Type - so a custom row carrying
        // either is carrying noise.
        if (mine.Name != null)
        {
            mine.Name = null;
            moved = true;
        }

        if (mine.Type != null)
        {
            mine.Type = null;
            moved = true;
        }

        if (mine.Weight != null && shipped.Weight != null &&
            MathF.Abs(mine.Weight.Value - shipped.Weight.Value) < 0.0005f)
        {
            mine.Weight = null;
            moved = true;
        }

        if (mine.Size != null && shipped.Size != null &&
            MathF.Abs(mine.Size.Value - shipped.Size.Value) < 0.0005f)
        {
            mine.Size = null;
            moved = true;
        }

        if (mine.Stacks != null && mine.Stacks == shipped.Stacks)
        {
            mine.Stacks = null;
            moved = true;
        }

        // Ordinal, because an empty string is a decision and must not be thinned against a shipped
        // null - "this carries no tags" is a thing somebody means. See Row.Alike.
        if (mine.Tags != null && string.Equals(mine.Tags, shipped.Tags, StringComparison.Ordinal))
        {
            mine.Tags = null;
            moved = true;
        }

        if (mine.Children != null &&
            string.Equals(mine.Children, shipped.Children, StringComparison.Ordinal))
        {
            mine.Children = null;
            moved = true;
        }

        if (mine.Effect != null && string.Equals(mine.Effect, shipped.Effect, StringComparison.Ordinal))
        {
            mine.Effect = null;
            moved = true;
        }

        // Decided agrees with a shipped row's blank, which means decided too. See Row.Decided.
        if (mine.Set != null && Row.Decided(mine.Set) == Row.Decided(shipped.Set))
            mine.Set = null;

        if (mine.Kind != null && string.Equals(mine.Kind, shipped.Kind, StringComparison.Ordinal))
            mine.Kind = null;

        if (mine.NameInGame != null &&
            string.Equals(mine.NameInGame, shipped.NameInGame, StringComparison.Ordinal))
            mine.NameInGame = null;

        if (mine.Granter != null && string.Equals(mine.Granter, shipped.Granter, StringComparison.Ordinal))
            mine.Granter = null;

        if (mine.Matches != null &&
            string.Equals(mine.Matches, shipped.Matches, StringComparison.Ordinal))
        {
            mine.Matches = null;
            moved = true;
        }

        if (mine.Share != null && shipped.Share != null &&
            MathF.Abs(mine.Share.Value - shipped.Share.Value) < 0.0005f)
        {
            mine.Share = null;
            moved = true;
        }

        return moved;
    }

    /// <summary>
    /// Moves a hand-tuned count out of the weight column and onto the child edge it belongs on.
    ///
    /// **Ten of the shipped weights were never weights.** "Remnant: Normal monsters spawned per wave"
    /// at 10 and "Strongbox: Normal monsters per implicit guarding pack" at 5 are COUNTS wearing the
    /// weight column, which is the same one-column-two-units fault as a rune's weight holding a
    /// percentage. In the new shape they are counts on a child edge, where a count belongs.
    ///
    /// The values move with them. Anybody who has tuned a pack size has that number in their custom
    /// file under a setting: id, and the shipped file no longer names those ids - at which point
    /// Load's own prune drops them as "a row nobody can reach". So this has to run BEFORE that walk,
    /// or a tuned count is deleted by the same pass that was written to prevent exactly this. See the
    /// prune below, and the standing rule that a migration must not silently discard tuned values.
    ///
    /// **Only where the row has no children of its own.** Running twice must not overwrite a cell
    /// somebody has since edited by hand, so an existing custom Children is left alone and the old
    /// setting row is still retired.
    ///
    /// Implicit and explicit packs are one thing - "guarded by 1 pack of Rare Monsters" is one pack
    /// whichever line it came from - so the two settings collapse onto one row. They could disagree
    /// in a file tuned before that was known, and then the implicit one is taken and the difference
    /// is reported rather than quietly resolved.
    /// </summary>
    private static int Counted(List<string> retire)
    {
        var moved = 0;

        // The row a monstermarker answers to. Named here as well as in Weighing because the migration
        // and the reader have to agree about which row it is, and a literal in two files is a literal
        // that will disagree.
        const string Marker = "monstermarker";

        // new row, which child, the setting that held the count, and its explicit twin where there is one
        var moves = new (string Row, string Child, string From, string Twin)[]
        {
            ("remnant", "remnant/wave", "setting:WaveCount", null),
            ("remnant/wave", "monster/rare", "setting:WaveRares", null),
            ("remnant/wave", "monster/magic", "setting:WaveMagic", null),
            ("remnant/wave", "monster/normal", "setting:WaveNormal", null),
            ("pack/normal", "monster/normal", "setting:StrongboxPack",
                "setting:StrongboxExplicitPack"),
            ("pack/magic", "monster/magic", "setting:StrongboxMagicPack",
                "setting:StrongboxExplicitMagicPack"),
            ("pack/rare", "monster/rare", "setting:StrongboxRarePack",
                "setting:StrongboxExplicitRarePack"),
            ("caged", "monster/rare", "setting:CagedRares", null),
        };

        // Gathered per row first, because a wave's three tiers are three settings and one cell.
        var cells = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var touched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (into, child, from, twin) in moves)
        {
            if (!Shipped.ContainsKey(into))
                continue;

            var said = Mine.TryGetValue(from, out var tuned) ? tuned.Weight : null;

            if (twin != null && Mine.TryGetValue(twin, out var other) && other.Weight != null &&
                said != null && MathF.Abs(other.Weight.Value - said.Value) >= 0.0005f)
            {
                DebugWindow.LogMsg(
                    $"[AutoExpedition] {from} is {said.Value:0.##} and {twin} is " +
                    $"{other.Weight.Value:0.##}. They are the same thing now - one pack of a rarity " +
                    $"is one pack whichever line promised it - so {said.Value:0.##} is kept.", 10f);
            }

            retire.Add(from);

            if (twin != null)
                retire.Add(twin);

            if (said == null)
                continue;

            touched.Add(into);

            if (!cells.TryGetValue(into, out var list))
                cells[into] = list = new List<string>();

            list.Add(child + " x" + said.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // **The marker's magic chance is the one that is not a count.** It is a percentage, and in the
        // new shape it is two fractional children whose counts are the two probabilities - which is
        // also why the blend and the tier split stopped needing separate code. Handled apart from the
        // loop above because one setting becomes two counts rather than one.
        if (Shipped.ContainsKey(Marker))
        {
            retire.Add(Id.Setting("MarkerMagic"));

            var said = Mine.TryGetValue(Id.Setting("MarkerMagic"), out var chance)
                ? chance.Weight
                : null;

            if (said != null && (!Mine.TryGetValue(Marker, out var mine) || mine.Children == null))
            {
                var magic = Math.Clamp(said.Value, 0f, 100f) / 100f;

                Set(Marker, r => r.Children =
                    $"{Id.Kind(TargetKind.Monster, ChestTier.Unknown)} " +
                    $"x{(1f - magic).ToString("0.####", CultureInfo.InvariantCulture)}, " +
                    $"{Id.MagicMonsters} " +
                    $"x{magic.ToString("0.####", CultureInfo.InvariantCulture)}");

                moved++;
            }
        }

        foreach (var into in touched)
        {
            // A cell somebody has edited is their answer and is not overwritten. The old setting row
            // is still retired, which is correct: it no longer feeds anything.
            if (Mine.TryGetValue(into, out var had) && had.Children != null)
                continue;

            // The children the shipped row does NOT carry a tuned value for stay as shipped, so a
            // partially tuned row keeps the shipped answer for the rest of it. Rebuilt on the shipped
            // cell rather than replacing it wholesale.
            var said = new List<string>();
            var tuned = cells[into];

            foreach (var piece in (Default(into)?.Children ?? "").Split(','))
            {
                var part = piece.Trim();

                if (part.Length == 0)
                    continue;

                var at = part.LastIndexOf(" x", StringComparison.Ordinal);
                var name = at < 0 ? part : part[..at].Trim();
                var swap = tuned.FirstOrDefault(
                    x => x.StartsWith(name + " x", StringComparison.Ordinal));

                said.Add(swap ?? part);
            }

            if (said.Count == 0)
                continue;

            Set(into, r => r.Children = string.Join(", ", said));
            moved++;
        }

        return moved;
    }


    /// <summary>
    /// Carries a tuned wisp trap weight onto whichever wisp rows the table currently ships.
    ///
    /// **The three are separate rows again, because the game does tell them apart.** They were
    /// collapsed into one on the reasoning that one Azmeri modifier covers all three and only the
    /// model differed - the modifier part is true and the conclusion was not. The tileset names them
    /// WispTrap_Primal, _Vivid and _Wild, the ground label reads "Imprisoned Primal Wisp" and so on,
    /// and a dump of Grazed Prairie carries all six entities with those paths and those labels. What
    /// was missing was which art went with which name, which the same dump settles:
    /// ezomytewisptrap01 is Primal, 02 is Vivid, 03 is Wild.
    ///
    /// Two generations of id have to be carried, because this has moved twice:
    ///
    ///  - setting:WispVivid / _Wild / _Primal, the original per-wisp sliders, go to their own rows.
    ///  - setting:WispTrap, the single row that briefly replaced them, goes to all three - it was one
    ///    number standing for all of them, so all of them is where it belongs.
    ///
    /// Without this the prune below would drop a tuned weight silently: a setting: id the shipped
    /// file no longer names is a row nobody can reach, so it goes. Nothing is written where nothing
    /// was tuned, and a row of your own always wins over this.
    /// </summary>
    private static int Wisped()
    {
        var moved = 0;

        // The per-wisp sliders first, each to the row that now carries it. A row of your own at the
        // destination is a decision already made, so it is left alone.
        foreach (var (was, now) in new[]
                 {
                     ("setting:WispPrimal", "setting:WispTrapPrimal"),
                     ("setting:WispVivid", "setting:WispTrapVivid"),
                     ("setting:WispWild", "setting:WispTrapWild"),
                 })
        {
            if (Mine.ContainsKey(now) || !Mine.TryGetValue(was, out var mine) ||
                mine.Weight is not { } weight)
                continue;

            Set(now, r => r.Weight = weight);
            moved++;
        }

        // Then the combined row, onto any of the three still holding the shipped number. It stood for
        // all three, so it answers for all three.
        if (Mine.TryGetValue("setting:WispTrap", out var both) && both.Weight is { } shared)
        {
            foreach (var now in new[] { "setting:WispTrapPrimal", "setting:WispTrapVivid", "setting:WispTrapWild" })
            {
                if (Mine.ContainsKey(now))
                    continue;

                Set(now, r => r.Weight = shared);
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Moves a discovery row filed under its ground label onto the one without it.
    ///
    /// **The label used to be part of an object's identity and is not any more.** It arrives later
    /// than everything else naming an object, so a thing was filed once before it could be read and
    /// again after, under two keys differing in that field alone - a Vaal relic in the shipped file,
    /// a Dormant Burrower in a live one. See Unknowns.Key.
    ///
    /// Both halves are kept when both exist, field by field, with the labelled row winning: it is
    /// the later sighting and the one somebody is more likely to have priced, since it is the one
    /// that reads properly in the table. Without this the labelled row simply stops being reachable
    /// - a key nothing will generate again - and whatever was set on it goes quiet rather than
    /// wrong, which is worse.
    /// </summary>
    private static int Labelled()
    {
        var moved = 0;

        foreach (var (id, mine) in Mine.ToList())
        {
            if (!id.StartsWith("found:", StringComparison.Ordinal))
                continue;

            var parts = id["found:".Length..].Split('|');

            if (parts.Length < 6 || parts[5].Length == 0)
                continue;

            parts[5] = "";

            var now = "found:" + string.Join("|", parts);

            Mine.Remove(id);

            if (!Mine.TryGetValue(now, out var already))
            {
                Mine[now] = mine;
                moved++;

                continue;
            }

            // Field by field, the labelled row winning where both say something.
            already.Weight ??= mine.Weight;
            already.Size ??= mine.Size;
            already.Share ??= mine.Share;
            already.Stacks ??= mine.Stacks;
            already.Name ??= mine.Name;
            already.Type ??= mine.Type;
            already.Tags ??= mine.Tags;
            already.Children ??= mine.Children;
            already.Effect ??= mine.Effect;
            already.Matches ??= mine.Matches;

            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Moves each discovery row filed under a render name onto the id without one, merging rows that differed in
    /// that field alone. See Unknowns.Key, which no longer puts the name in an id, and MergedRows.
    /// </summary>
    private static int MergedOnRenderName()
    {
        var moved = 0;

        foreach (var (id, mine) in Mine.ToList())
        {
            if (!id.StartsWith("found:", StringComparison.Ordinal))
                continue;

            var parts = id["found:".Length..].Split('|');

            if (parts.Length < 7 || parts[6].Length == 0)
                continue;

            parts[6] = "";
            MovedOnto(id, "found:" + string.Join("|", parts), mine);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Moves each discovery row filed before its art or its minimap icon had arrived onto the row of the same object
    /// with them, yours or shipped, where exactly one such row exists. With none it stays as it is; with several it
    /// cannot be told which it was, and stays as well. See Unknowns.Register, which now waits for both, and MergedRows.
    /// </summary>
    private static int MergedOnMissingArtOrIcon()
    {
        var moved = 0;

        foreach (var (id, mine) in Mine.ToList())
        {
            if (!id.StartsWith("found:", StringComparison.Ordinal))
                continue;

            var parts = id["found:".Length..].Split('|');

            if (parts.Length < 5 || (parts[2].Length > 0 && parts[3].Length > 0))
                continue;

            // The same object with what this row is missing: every field equal, except an empty art or icon here
            // that the other row has.
            bool Fuller(string x)
            {
                var other = x["found:".Length..].Split('|');

                return other.Length == parts.Length &&
                       Enumerable.Range(0, parts.Length).All(i =>
                           other[i] == parts[i] || (i is 2 or 3 && parts[i].Length == 0 && other[i].Length > 0));
            }

            var fuller = Mine.Keys.Concat(Shipped.Keys).Distinct(StringComparer.Ordinal)
                .Where(x => x != id && x.StartsWith("found:", StringComparison.Ordinal) && Fuller(x))
                .ToList();

            if (fuller.Count != 1)
                continue;

            MovedOnto(id, fuller[0], mine);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Whether a discovery row of yours is only the seed filing writes - weight at the placeholder, not agreed, no
    /// other answer in it - for an object the shipped file has a row for. Such a row shadows the shipped answer
    /// with the placeholder: after the shipped ids stopped carrying the render name, four Vaal Zealots on a Frigid
    /// Bluffs site (2026-10-04) were filed afresh at weight 1, unagreed, over shipped rows at 2. Dropped on load,
    /// so the shipped row answers. Any edit to the row keeps it. See Unknowns.Found.
    /// </summary>
    private static bool SeedOverShipped(string id, Row row)
    {
        if (!id.StartsWith("found:", StringComparison.Ordinal) || !Shipped.ContainsKey(id) || row.Set != false)
            return false;

        // The kind as filed, which the thinning may have taken off a row that agreed with the shipped one. A weight
        // thinned off the same way leaves none, which is as much a seed as the placeholder itself.
        var seed = Enum.TryParse<TargetKind>(row.Kind ?? Shipped[id].Kind, out var kind)
            ? Priors.Weight(kind) ?? Unknowns.Default
            : Unknowns.Default;

        return (row.Weight == null || MathF.Abs(row.Weight.Value - seed) < 0.0001f) &&
               row.Tags == null && row.Stacks == null && row.Children == null && row.Effect == null &&
               row.Matches == null && row.Share == null && row.NameInGame == null && row.Granter == null &&
               row.Name == null && row.Type == null;
    }

    /// <summary>
    /// Moves a row of yours to another id, merging it into the row there if there is one. See MergedRows.
    /// </summary>
    private static void MovedOnto(string from, string to, Row row)
    {
        Mine.Remove(from);

        Mine[to] = Mine.TryGetValue(to, out var already) ? MergedRows(already, row) : row;
    }

    /// <summary>
    /// Two rows of one object merged into one. The row somebody has agreed - Set, which the Status column shows -
    /// wins every field it holds, the other filling only what it leaves empty; between two agreed or two unagreed
    /// rows, the first. First is the earlier of the two and Seen the later. Every field of Row is carried, read off
    /// the type, so a field added later is not dropped here.
    /// </summary>
    private static Row MergedRows(Row a, Row b)
    {
        var (winner, other) = b.Set == true && a.Set != true ? (b, a) : (a, b);

        foreach (var field in typeof(Row).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (field.GetValue(winner) == null)
                field.SetValue(winner, field.GetValue(other));
        }

        winner.First = new[] { a.First, b.First }.Where(x => x != null).Min();
        winner.Seen = new[] { a.Seen, b.Seen }.Where(x => x != null).Max();

        return winner;
    }

    /// <summary>The file this table used to be called, so a rename does not read as an empty table.</summary>
    private const string WasCustom = "entity_reference_table_custom.json";

    /// <summary>
    /// Carries your table across the rename from Entity to Weight Reference Table.
    ///
    /// **A rename of a data file is a data migration, and skipping it loses the user's work silently.**
    /// Every tuned weight lives in the custom file. Renaming the constant without moving the file would
    /// have found nothing on disk, read an empty table, and fallen through to the shipped defaults - a
    /// table that looks freshly installed rather than one that failed to load, which is the worst way
    /// for this to go wrong because nothing about it looks like an error.
    ///
    /// Moved rather than copied, so there is one file and no question later about which is authoritative.
    /// If the new name already exists the old one is left exactly where it is and nothing is touched -
    /// the new file is the answer, and overwriting it from an older copy would be the same loss in
    /// reverse.
    /// </summary>
    private static void Rebadged()
    {
        try
        {
            var now = Path.Combine(Folder(), Custom);
            var was = Path.Combine(Folder(), WasCustom);

            if (File.Exists(now) || !File.Exists(was))
                return;

            File.Move(was, now);

            DebugWindow.LogMsg($"[AutoExpedition] Moved {WasCustom} to {Custom} - " +
                               "the table is the Weight Reference Table now.", 8f);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not rename {WasCustom}: {ex.Message}", 10f);
        }
    }

    public static void Load()
    {
        // A reload answers differently for every row at once, which is the widest change there is.
        Revision++;
        Noting("the table was loaded");

        if (Home.Length == 0)
            return;

        Read(Path.Combine(Source.Length > 0 ? Source : Home, Defaults), Shipped);

        Rebadged();

        Read(Path.Combine(Folder(), Custom), Mine);

        // **A row written under the old name is still somebody's answer.** The three monster tiers
        // were renamed so the table says they belong together; a file from before that carries
        // kind:Monster, kind:Elite and setting:MagicMonster, which now match nothing. Left alone the
        // row sits there looking edited while the weight quietly reverts to shipped.
        Renamed(Shipped);
        Renamed(Mine);

        // **A row of yours that says what the shipped file says is not a disagreement.**
        //
        // It is how a file rots: keep them and the day a shipped weight is improved, everybody who
        // once touched that row is frozen on their old answer with nothing saying so. Dropping them
        // means a custom file empties itself as its findings are promoted.
        // Before the prune, which would otherwise delete the tuned counts this reads. See Counted.
        //
        // **Now dropped as well as retired.** Nothing reads these settings any more - the composition
        // rows are what the objective walks - so a row still sitting here is a number somebody could
        // edit to no effect, which is the whole fault this table is being rewritten to end.
        var retire = new List<string>();
        var carried = Counted(retire) + Wisped() + Labelled() + MergedOnRenderName() + MergedOnMissingArtOrIcon();

        var gone = new List<string>(retire);
        var thinned = 0;

        var sized = 0;

        foreach (var (id, row) in Mine)
        {
            // **A size that used to live in a namespace of its own.**
            //
            // art: and family: rows were the only way to disagree with a measured extent, because
            // the measurement was consulted ahead of the object's own row - so every object with a
            // measured art had TWO rows, and the one you could see and edit was not the one that
            // answered. That is what put "caverncap" in the table beside "Sub-area entrance:
            // caverncap_01".
            //
            // The object's row carries its size now, so these are dropped rather than moved. Nothing
            // is lost: an art: row could only ever hold a correction to a measurement, and the one in
            // a live file was laircap at 188.04 - the measured default to two decimal places, which
            // overrode nothing. A real correction is retyped on the object's own row, where it now
            // takes effect. See Extents.Of.
            if (id.StartsWith("art:", StringComparison.Ordinal) ||
                id.StartsWith("family:", StringComparison.Ordinal))
            {
                gone.Add(id);
                sized++;

                continue;
            }

            // Field by field first, so Empty below can finish a row whose last real difference has
            // been promoted into the shipped file. See Thinned.
            if (Thinned(id, row))
                thinned++;

            if (row.Empty || row.Same(Default(id)) || SeedOverShipped(id, row))
            {
                gone.Add(id);

                continue;
            }

            // **A setting the plugin no longer has is a row nobody can reach.** These ids are the
            // plugin's own - a "found:" or a "rune:" names something in the game and outlives any
            // version, but a "setting:" names a weight this build either ships or does not. Drop a
            // weight and the row somebody had for it becomes invisible: the table lists what the
            // shipped file names, so it cannot be seen, edited or reset, and it sits in the file
            // for good. Seen immediately - deleting the per-socket bonus left setting:PerSocket
            // behind in a custom file written minutes earlier.
            if (id.StartsWith("setting:", StringComparison.Ordinal) && !Shipped.ContainsKey(id))
                gone.Add(id);
        }

        foreach (var id in gone)
            Mine.Remove(id);

        // Always, not only when something was pruned: the merged view is what every reader asks, and
        // a load that changed nothing in Mine still replaced the whole of Shipped.
        Rebuilt();

        if (sized > 0)
        {
            DebugWindow.LogMsg(
                $"[AutoExpedition] dropped {sized} size row(s) in the art:/family: namespace. A size " +
                "lives on the object's own row now, where the Size column has always looked like it " +
                "worked - it did not, for any art with a measurement. Retype one there if you had a " +
                "correction.", 12f);
        }

        // **This rewrites your file, so it is the one bump nobody asked for.** It is meant to run
        // once and settle: a row it drops is gone from Mine before Keep writes, so the next load
        // finds nothing to do. If this line fires on every load the migration is not converging,
        // and the cost is an automatic re-solve and a re-scored plan every time the plugin starts.
        if (gone.Count > 0 || thinned > 0 || carried > 0)
        {
            // **Named, not counted.** "dropped 12" cannot answer the question it raises, which is
            // whether the twelve were rows nobody needed or answers somebody had given - and those
            // look identical from a count. A discovery row carries Set, so dropping one puts its
            // object back to asking, and the symptom is a dig site full of things the plugin was
            // told about last session.
            var names = string.Join(", ", gone.Count > 8 ? gone.GetRange(0, 8) : gone) +
                        (gone.Count > 8 ? $", and {gone.Count - 8} more" : "");

            Moved($"a load migrated the file - dropped {gone.Count}" +
                  (gone.Count > 0 ? $" [{names}]" : "") +
                  $", thinned {thinned}, carried {carried}");
        }
    }

    /// <summary>Writes your file. The shipped one is never written - it arrives with the plugin.</summary>
    public static void Keep()
    {
        if (!_dirty || Home.Length == 0)
            return;

        _dirty = false;

        try
        {
            var text = new StringBuilder();

            text.AppendLine("{");
            text.AppendLine("  \"rows\": [");

            var first = true;

            foreach (var (id, row) in Mine)
            {
                if (!first)
                    text.AppendLine(",");

                first = false;

                text.Append("    { \"id\": \"").Append(Safely(id)).Append('"');

                // Written only if this row actually carries one, which a custom row normally does
                // not - it inherits the shipped name. See Row.Name.
                if (row.Name != null)
                    text.Append(", \"name\": \"").Append(Safely(row.Name)).Append('"');

                if (row.Type != null)
                    text.Append(", \"type\": \"").Append(Safely(row.Type)).Append('"');

                if (row.Weight != null)
                    text.Append(", \"weight\": ").Append(Number(row.Weight.Value));

                if (row.Size != null)
                    text.Append(", \"size\": ").Append(Number(row.Size.Value));

                if (row.Tags != null)
                    text.Append(", \"tags\": \"").Append(Safely(row.Tags)).Append('"');

                if (row.Stacks != null)
                    text.Append(", \"stacks\": ").Append(row.Stacks.Value ? "true" : "false");

                if (row.Children != null)
                    text.Append(", \"children\": \"").Append(Safely(row.Children)).Append('"');

                if (row.Effect != null)
                    text.Append(", \"effect\": \"").Append(Safely(row.Effect)).Append('"');

                if (row.Matches != null)
                    text.Append(", \"matches\": \"").Append(Safely(row.Matches)).Append('"');

                if (row.Share != null)
                    text.Append(", \"share\": ").Append(Number(row.Share.Value));

                if (row.Set != null)
                    text.Append(", \"set\": ").Append(row.Set.Value ? "true" : "false");

                if (row.NameInGame != null)
                    text.Append(", \"name_in_game\": \"").Append(Safely(row.NameInGame)).Append('"');

                if (row.Kind != null)
                    text.Append(", \"kind\": \"").Append(Safely(row.Kind)).Append('"');

                if (row.Granter != null)
                    text.Append(", \"granter\": \"").Append(Safely(row.Granter)).Append('"');

                if (row.Seen != null)
                    text.Append(", \"seen\": \"")
                        .Append(row.Seen.Value.ToString("O", CultureInfo.InvariantCulture)).Append('"');

                if (row.First != null)
                    text.Append(", \"first\": \"")
                        .Append(row.First.Value.ToString("O", CultureInfo.InvariantCulture)).Append('"');

                text.Append(" }");
            }

            if (!first)
                text.AppendLine();

            text.AppendLine("  ]");
            text.AppendLine("}");

            File.WriteAllText(Path.Combine(Folder(), Custom), text.ToString());
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write {Custom}: {ex.Message}", 5f);
        }
    }

    /// <summary>
    /// Reads one file into one layer.
    ///
    /// Parsed by hand for the reason Unknowns.Load is: the shape is six fields, the plugin has no
    /// JSON dependency of its own, and a file somebody has edited should survive a stray comma
    /// rather than take the plugin down with it.
    /// </summary>
    private static void Read(string path, Dictionary<string, Row> into)
    {
        if (!File.Exists(path))
            return;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var id = Between(line, "\"id\": \"", "\"");

                if (id.Length == 0)
                    continue;

                // **Present-and-empty is not the same as absent**, so the field is looked for
                // before it is read. Text() would fold the two together again. See Row.Alike.
                var row = new Row
                {
                    Name = Said(line, "name"),
                    Tags = Said(line, "tags"),
                    Children = Said(line, "children"),

                    Effect = Said(line, "effect"),
                    Matches = Said(line, "matches"),
                    Type = Said(line, "type"),
                    Kind = Said(line, "kind"),
                    NameInGame = Said(line, "name_in_game"),
                    Granter = Said(line, "granter"),
                };

                var set = Between(line, "\"set\": ", ",").TrimEnd(' ', '}').Trim();

                if (set is "true" or "false")
                    row.Set = set == "true";

                if (DateTime.TryParse(Said(line, "seen"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var seen))
                    row.Seen = seen;

                if (DateTime.TryParse(Said(line, "first"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var first))
                    row.First = first;

                if (float.TryParse(Between(line, "\"weight\": ", ",").TrimEnd(' ', '}'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                    row.Weight = weight;

                if (float.TryParse(Between(line, "\"size\": ", ",").TrimEnd(' ', '}'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                    row.Size = size;

                if (float.TryParse(Between(line, "\"share\": ", ",").TrimEnd(' ', '}'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
                    row.Share = share;

                var stacks = Between(line, "\"stacks\": ", ",").TrimEnd(' ', '}').Trim();

                if (stacks is "true" or "false")
                    row.Stacks = stacks == "true";

                if (!row.Empty)
                    into[id] = row;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(
                $"[AutoExpedition] Could not read {Path.GetFileName(path)}: {ex.Message}", 5f);
        }
    }

    /// <summary>One text field, or null where the row does not mention it at all. See Row.Alike.</summary>
    private static string Said(string line, string field)
    {
        var at = "\"" + field + "\": \"";

        return line.Contains(at, StringComparison.Ordinal) ? Between(line, at, "\"") : null;
    }

    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Between(string line, string after, string before)
    {
        var from = line.IndexOf(after, StringComparison.Ordinal);

        if (from < 0)
            return "";

        from += after.Length;

        var to = line.IndexOf(before, from, StringComparison.Ordinal);

        return to < 0 ? line[from..] : line[from..to];
    }

    private static string Safely(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "").Replace("\"", "");

    private static string Folder()
    {
        Directory.CreateDirectory(Home);

        return Home;
    }
}
