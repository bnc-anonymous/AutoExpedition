using System.Collections.Generic;
using System.Linq;

namespace AutoExpedition;

/// <summary>
/// What a thing IS, as a set of names, derived rather than typed.
///
/// **The weights describe categories and the game keeps inventing objects.** A weight per
/// TargetKind answers "what is a chest worth" and cannot answer "what is an excavated chest worth",
/// which is what a relic modifier actually says - "increased Quantity of Items Found in Excavated
/// Chests" applies to some chests and not others, and there is nowhere in the current model to put
/// that. Tags are the missing half: an object carries several, a rule names the ones it applies to,
/// and a modifier that affects one kind of thing can say so.
///
/// **Derived from what the scan already read, never hand-entered.** Every target arrives with its
/// metadata path, its art, the game's own minimap classification, its kind and its chest tier, and
/// between them those say what the thing is. Asking a player to tag each new object by hand would
/// rebuild exactly the backlog the unknown entities list exists to drain - the point of that list
/// is that a new tileset costs a weight, not a code change, and tagging by hand would make it cost
/// both.
///
/// **Nothing reads these yet, and that is the whole of this stage.** They are drawn in the entity
/// reference table so the derivation can be judged against a real dig site before any plan depends
/// on it. A wrong tag here costs a second look; a wrong tag that a carry is already scoped to costs
/// a chain routed round the wrong content, and there is no way to tell from the outside.
///
/// Named in the game's own style - lower case, underscores - because the vocabulary these have to
/// agree with eventually is the one on the modifiers themselves.
/// </summary>
internal static class Tags
{
    /// <summary>Everything this object is, sorted, without repeats.</summary>
    public static string[] Of(Target target)
    {
        if (target == null)
            return System.Array.Empty<string>();

        // **What somebody has written down beats what this can work out.**
        //
        // Derivation is a reading of the game and the game changes: a patch moves something, adds a
        // rarity, introduces a container that is a chest in every way except the one the classifier
        // looks at - and then the plugin is wrong about a thing in front of you with no way to be
        // told. Two places can tell it. An object it could not classify has its own row and its tags
        // live there; a kind it recognises is answered for its kind, since saying a reward chest is
        // a chest is a statement about reward chests rather than about this one.
        // **Null is "nobody has said", empty is "somebody said none".** Tested apart, because an
        // object whose tags were deliberately cleared is unreachable by every modifier and that is
        // a thing a person is entitled to mean - see Wrt.Row.Alike. Folded together, clearing a
        // cell stored an answer that read back as silence and the derivation painted over it.
        // **The row that identifies the thing, between its own row and its kind's.**
        //
        // A target is already resolved to a row to be priced - the art a marker wears, the metadata
        // a strongbox's chest carries, the row a remnant's kind records - and that row is where its
        // tags belong, since it is the one place already saying what this object IS. Asked between
        // the two that were here so that a row stating its tags is believed over the answer for its
        // whole kind, while a row silent about them still falls through to that answer.
        //
        // See Weighing.RowOfTarget, which is the resolution the weighing itself uses.
        var key = Unknowns.Key(target);
        var said = Wrt.Of(Wrt.Id.Found(key))?.Tags
                   ?? (Weighing.RowOfTarget(target) is { } row ? Wrt.Of(row)?.Tags : null)
                   ?? Wrt.Of(Weighing.RowIdOfTarget(target))?.Tags;

        if (said == null)
        {
            var older = Unknowns.MarksOf(key);

            said = older.Length > 0 ? older : Marks.Of(Marks.Key(target));

            if (said.Length == 0)
                said = null;
        }

        if (said != null)
            return Read(said);

        return Derive(target);
    }

    /// <summary>
    /// What this object is by the plugin's own reading, ignoring anything written down about it.
    ///
    /// The table needs both: what a row says now, and what it would say if nobody had told it
    /// anything - so that clearing an edit puts it back to following the game rather than freezing
    /// it at whatever was derived the day somebody typed over it.
    /// </summary>
    public static string[] Derive(Target target)
    {
        if (target == null)
            return System.Array.Empty<string>();

        var tags = new List<string>(4);

        void Add(string tag)
        {
            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
                tags.Add(tag);
        }

        // **"Runic monster" is the game's word for anything a blast spawns, not a rarity.**
        // Those come up normal, magic or rare, so the broad word is what the monster tag already
        // says and a runic_monster tag beside it said the same thing twice. The rarities are what a
        // modifier actually names - "50% increased number of Rare Monsters" - and they are what the
        // tags carry.
        //
        // TargetKind.Monster is the monstermarker, and a monstermarker comes up white OR blue, so it
        // carries both: a modifier reaching either reaches this marker, because catching it is how
        // you would get one. TargetKind.Elite is the rare marker.
        switch (target.Kind)
        {
            case TargetKind.Monster:
                // **One tier, not both.** This used to claim normal AND magic on the grounds that a
                // monstermarker comes up white or blue and catching it is how you would get either.
                // That held while the monster tag was the only one the scoring ever looked up; it
                // does not now. A tier-scoped modifier would give this marker's whole weight a full
                // uplift twice over - once as a normal monster and once as a magic one - for a
                // single creature that is only ever one of them.
                //
                // Editable on the row, like every other kind, so somebody who wants a blue
                // monstermarker reached can say so. See Weighing.Wearing.
                Add("monster");
                Add("normal_monster");
                break;

            case TargetKind.Elite:
                Add("monster");
                Add("rare_monster");
                break;

            // A boss is a caged monster in the only sense this vocabulary has: something a blast
            // lets out to fight you. The two kinds differ in what they are worth, which is a weight
            // and not a tag, so nothing here needs to tell them apart.
            case TargetKind.Caged:
                Add("monster");
                Add("caged_monster");
                break;

            // The same, plus what it actually is. A monolith summons a UNIQUE boss, and a unique is
            // the richest thing a rune can land on - so it earns the tag rather than sharing the
            // rare one. See Tags.Known.
            case TargetKind.Monolith:
                Add("monster");
                Add("caged_monster");
                Add("unique_monster");
                break;

            // **Nothing at all, deliberately.** A woken sentry fights for you, so no modifier that
            // makes the encounter richer or more numerous has anything to say about it - not one
            // aimed at monsters, and not one aimed at everything, because "everything" on a dig site
            // means everything the blast digs up to fight you.
            //
            // No tags means a mask of nought, so it matches no scope rather than merely missing the
            // one that would have reached it. Tags for it can be added the day something needs to
            // name a sentry; inventing them before then is guessing at a vocabulary.
            //
            // The propagation model already agreed about monsters: Planner.Unearths leaves sentries
            // out of the pool a carry multiplies, which the monster tag here was contradicting.
            // **Nothing at all, which is a stronger statement than "not modifiable".** A sentry
            // fights you; it is not content, so it carries no kind tag either and nothing can be
            // scoped at it. That is why it is absent from the not-modifiable list below rather
            // than listed there - and why this stays a return: an empty list left to emerge from
            // no case matching would stop being empty the day somebody adds a sentry tag.
            case TargetKind.Sentry:
                return System.Array.Empty<string>();

            // Every chest a dig site holds is one a blast unearths - the target is the signpost, and
            // what it becomes is an excavated chest. The ordinary chests standing around a map are
            // not markers and never reach the scan, so this is not the over-claim it looks like.
            case TargetKind.Chest:
                // One tag, not two. Every chest the scan meets is one a blast unearths - the
                // ordinary chests standing around a map are not markers and never reach it - so
                // "chest" and "excavated_chest" were carried by exactly the same rows and neither
                // could ever distinguish anything from the other. excavated_chest survives because
                // it is the one the game's own relic upsides name.
                Add("excavated_chest");
                break;

            // Deliberately NOT a chest. It is a container the blast frees rather than one it digs
            // up, and a modifier about excavated chests does not touch it.
            case TargetKind.Strongbox:
                Add("strongbox");
                break;

            case TargetKind.Remnant:
                Add("remnant");
                break;

            case TargetKind.Relic:
                Add("relic");
                break;

            case TargetKind.Hatch:
                // **A monster too, because what comes out of it is monsters.** The objective has
                // always treated it as one - Unearths counts a hatch's whole worth into the monster
                // total a carried rune multiplies - and the tags said otherwise, which nothing
                // noticed while the monster tag was the one tag the scoring never looked up. The
                // grouped payout does look it up, and a hatch missing it would have lost every
                // propagating rune on the site.
                Add("monster");
                Add("hatch");
                break;

            case TargetKind.Entrance:
                Add("entrance");
                break;

            case TargetKind.Barrel:
                Add("scenery");
                Add("barrel");
                break;

            case TargetKind.Scenery:
                Add("scenery");
                break;

            case TargetKind.Unknown:
                Add("unknown");
                break;
        }

        // **What the game says this one is, which the kind cannot.** A marker's kind is read off
        // its art and says "a monster stands here"; ObjectMagicProperties says which rarity, and a
        // unique among them is worth naming separately because it is the single richest thing a
        // propagating rune can be applied to.
        //
        // Added rather than replacing: a unique is still a monster, so everything aimed at monsters
        // still reaches it.
        if (string.Equals(target.Rarity, "Unique", System.StringComparison.OrdinalIgnoreCase))
        {
            Add("monster");
            Add("unique_monster");
        }

        // **The chest's tier is NOT a tag, and used to be eleven of them.** Common, Uncommon and
        // Rare added "normal", "magic" and "rare"; each Grand variant added "reward_chest" and a
        // word for its icon - currency, maps, trinkets, armour and the rest.
        //
        // A tag exists so a modifier can be aimed at a class of thing, and the game aims nothing at
        // a chest's tier: the relic upsides that reach chests say "in Excavated Chests", which is
        // excavated_chest and nothing finer. So those eleven could never be named by a scope, sat in
        // every chest's Tags cell, and said only what the row's own name already says. A vocabulary
        // that lists words nothing can use makes the words that matter harder to find.
        //
        // The tier is still read, still priced and still drawn - it picks the row, which is where a
        // chest's tier belongs. See Scan.Tier and Overlay.Name.

        // **A remnant IS modifiable, and excluding it made one row unpayable.**
        //
        // A share only reaches content carrying this tag, so a remnant being outside the list meant
        // no effect could ever touch one - and the runic henge row is
        // `remnant.weight *= +100% as henge_on_remnant`, the only row in the table that aims at a
        // remnant. It was parsed, it showed in the dump's "multiplies" line, and it paid nothing:
        // measured on Moor of Fallen Skies, a chain walked henges-first by hand scored the same
        // 1,015 as one that took the remnant first.
        //
        // Every other effect in the table targets monster.weight, and monsters are modifiable - so
        // this row was the only one that could expose it, and it is a custom row, which is why it
        // went unnoticed.
        //
        // Nothing else changes: setting:Henge is the only row anywhere aiming at `remnant.`, so the
        // blast radius of this is that one effect.
        //
        // Barrel, Entrance and Relic stay out. They carry no weight worth modifying - a barrel and
        // an entrance are ways through the ground rather than content - and see the note on the
        // henge-on-henge effect, which wants Relic reconsidered on its own evidence rather than
        // dragged along with this.
        //
        // The sentry is not in this list because it never reaches it - see the return above.
        if (target.Kind is not (TargetKind.Barrel or TargetKind.Entrance or TargetKind.Relic))
            Add(Modifiable);

        // Alphabetical, except that the one saying a thing can be modified at all comes first.
        // It is the tag every row shares, so a column of them lines up and the eye reads past it to
        // the tags that differ - where sorted among them it lands in a different place per row and
        // has to be looked for.
        tags.Sort(Ordered);

        return tags.ToArray();
    }

    /// <summary>
    /// The order tags read in: the one saying a thing can be modified at all first, then alphabetical.
    ///
    /// It is the tag every row shares, so a column of them lines up and the eye reads past it to the
    /// tags that differ - where sorted among them it lands in a different place per row and has to be
    /// looked for.
    ///
    /// Extracted so the derivation and anything else building a tag list sort the same way. A column
    /// where one row reads "weight_modifiable, monster" and the next "monster, weight_modifiable" is
    /// two facts about the ordering and none about the tags.
    /// </summary>
    public static readonly System.Comparison<string> Ordered = static (a, b) =>
        a == Modifiable
            ? b == Modifiable ? 0 : -1
            : b == Modifiable
                ? 1
                : string.CompareOrdinal(a, b);

    /// <summary>The same order, for a list somebody has gathered rather than derived. See Ordered.</summary>
    public static string[] Sorted(IEnumerable<string> tags)
    {
        var all = new List<string>(tags);

        all.Sort(Ordered);

        return all.ToArray();
    }

    /// <summary>
    /// The same, as one string, for a table cell and for searching.
    ///
    /// Comma separated, because several of these carry an underscore of their own - "chest
    /// excavated_chest rare_monster" reads as one run of words with punctuation in the middle of
    /// it, where "chest, excavated_chest" reads as a list.
    /// </summary>
    public static string Line(Target target) => string.Join(", ", Of(target));

    /// <summary>
    /// Every tag that exists, in the order their bits are numbered.
    ///
    /// **An OPEN vocabulary: a tag written in the table is a tag, immediately.** This was a fixed
    /// list, on the reasoning that free text lets a misspelling read as a tag nothing carries and
    /// score nought without complaining. The cost of that was far worse than the typo it prevented:
    /// the Tags column could not introduce a tag, so a row saying "runic_henge" was dropped by
    /// Mask - silently, which is the very failure the closed list was meant to avoid - and every
    /// new kind of thing needed a code change to be nameable at all.
    ///
    /// It is also the half of the grammar that was inconsistent with the other half. A stat name
    /// in an effect's "as" clause has always been open - Planning's band table mints a band for
    /// any string it has not seen - so "as runic_henge" worked while "runic_henge.weight" did not,
    /// in one expression.
    ///
    /// A name nothing carries still scores nothing. What changes is that it is a tag nobody has
    /// used rather than a word the system refuses to hear, and the table can say so: the Tags cell
    /// hover lists what is in use and how many rows carry each, where a mistyped tag shows up as a
    /// group of one.
    ///
    /// The seed list below is what the plugin knows how to derive by itself - see Derive. Order
    /// fixes the bit each tag gets, so append rather than insert; tags minted from the table take
    /// the next bits in the order they are first asked for, which is why nothing persists a mask.
    /// </summary>
    public static string[] Known => _known;

    /// <summary>
    /// The registry behind Known, replaced wholesale rather than mutated.
    ///
    /// Readers index this from the scoring threads while a table edit can mint a tag on any of
    /// them, so a grown array is published by one assignment and the old one is left intact for
    /// whoever is part-way through it. See Bit.
    /// </summary>
    private static string[] _known =
    {
        "weight_modifiable",
        "monster", "normal_monster", "magic_monster", "rare_monster", "unique_monster",
        "caged_monster",
        "excavated_chest", "strongbox",
        "remnant", "relic", "hatch", "entrance", "scenery", "barrel", "unknown",

        // **The one tag that marks an EFFECT rather than a thing on the ground.**
        //
        // Everything above names something a blast can catch. This names the rows that change what
        // those things are worth, and it exists because one rune scales the other runes: Power reads
        // "Runes gain: Empowered", so its target is not a creature at all. Written in the new grammar
        // that is "rune *= 1.5", and without this the target resolved to nothing - which the dump's
        // v1-beside-v2 section reported as the only complaint on a 112 row table.
        //
        // In v1 the same fact was carried by a reserved word in one column with its rate in another,
        // pointing at nothing - see Weighing.Empowering and the Scaling constant. A tag is what it
        // always was.
        //
        // Appended, as the summary above requires: the bit each tag gets is its position here, and
        // inserting one renumbers the rest.
        "rune",
    };

    /// <summary>
    /// The tags the plugin can work out by itself, which is the list it starts with.
    ///
    /// Kept apart from Known because the two answer different questions once the vocabulary is
    /// open: Known is every tag anybody has named, and this is the ones Derive will produce
    /// without being told. A word outside it has to be written on a row to reach anything, so it
    /// is the right thing to check a suspected typo against. See Carried.
    /// </summary>
    private static readonly string[] Seeded = (string[])_known.Clone();

    /// <summary>
    /// Whether anything could carry this tag: the plugin derives it, or a row writes it down.
    ///
    /// **Not "is it a tag" - every word is a tag now.** The question worth asking of a typo is
    /// whether anything on the ground will ever answer to it, and that is either because the
    /// classifier produces it or because somebody has put it in a Tags cell. Neither is true of
    /// "rare_mosnter", which is exactly the case the closed vocabulary used to catch by refusing
    /// the word outright.
    /// </summary>
    public static bool Carried(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var said = tag.Trim();

        foreach (var seed in Seeded)
        {
            if (string.Equals(seed, said, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var pair in Wrt.Standing.Concat(Wrt.Yours))
        {
            foreach (var piece in (pair.Value?.Tags ?? "").Split(','))
            {
                if (string.Equals(piece.Trim(), said, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Held while the registry grows, so two threads cannot mint the same tag twice.</summary>
    private static readonly object Minting = new();

    /// <summary>
    /// How many tags there can be, which is how many bits a mask has.
    ///
    /// A mask is a long and the sign bit is not used, so sixty-three. Nothing approaches it - the
    /// seed list is seventeen - but an open vocabulary has no natural ceiling, and silently
    /// wrapping round to bit nought would make one tag mean another.
    /// </summary>
    private const int Most = 63;

    /// <summary>Anything a propagating modifier may scale. Named here so it is spelt once.</summary>
    public const string Modifiable = "weight_modifiable";

    /// <summary>
    /// A thing no rune share reaches, though relics and the map's increases still do: the Runemarked rares of the
    /// Death rune's merge, which carry no rune modifier. Written on a row; read by Planner.Settle's payout, which pays
    /// such a thing from the non-rune shares alone. See NOTES, "Death merges".
    /// </summary>
    public const string UnaffectedByRunes = "unaffected_by_runes";

    /// <summary>
    /// Whether a tag names monsters, so a rune scoped to it lands on things that fight you.
    ///
    /// **Concentration is a monster phenomenon and nothing else.** Several runes at once on one
    /// rare is worth more than the sum of them; several runes reaching the same CHEST is worth
    /// exactly the sum, because a chest has no defences to compound and no rarity to multiply. So
    /// the bonus has to know which of the two a scoped rune is aimed at. See Planner's Evaluate.
    ///
    /// Minus one is the unscoped case - a rune with no scope reaches the monsters the chain
    /// unearths, which is the default a rune has always meant.
    /// </summary>
    public static bool Monsterly(int tag)
    {
        if (tag < 0 || tag == Monsters)
            return true;

        if (tag >= Known.Length)
            return false;

        return Known[tag].EndsWith("_monster", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The monster tag's bit, held because the scoring loop asks for it on every scoped effect.
    ///
    /// It is the one tag with an accumulator that already existed - the monsters unearthed
    /// downstream - so a scope naming it reads that rather than building a second one, which is
    /// what makes a migrated "monster=n" arithmetically the same as the flat n it replaced.
    /// </summary>
    public static readonly int Monsters = Bit("monster");

    /// <summary>The three monster tiers, so a remnant's waves can be split the way a scope names
    /// them. See Weighing.PartsOfTarget.</summary>
    public static readonly int Rares = Bit("rare_monster");

    public static readonly int Magics = Bit("magic_monster");

    public static readonly int Normals = Bit("normal_monster");

    /// <summary>The bit for the tag anything modifiable carries.</summary>
    public static readonly int Modifiables = Bit(Modifiable);

    /// <summary>
    /// A rune, for the effect that scales other effects. See Known.
    ///
    /// **Meant to be derived from the id rather than typed on thirty-four rows.** A rune row is
    /// self-identifying - its id is in the rune namespace - and asking anybody to tag them all by
    /// hand would be the same bargain the Multiplicative behaviour column lost: thirty-three of
    /// thirty-four rune rows in a live table carry nothing there, because a cell nobody has to fill
    /// in is a cell nobody fills in. Stage four is where that derivation lands.
    /// </summary>
    public static readonly int Runes = Bit("rune");

    /// <summary>The tags in a written list, keeping the ones that exist and the order given.</summary>
    public static string[] Read(string said)
    {
        if (string.IsNullOrWhiteSpace(said))
            return System.Array.Empty<string>();

        var kept = new List<string>(4);

        foreach (var piece in said.Split(','))
        {
            var name = piece.Trim();

            if (Bit(name) >= 0 && !kept.Contains(name))
                kept.Add(name);
        }

        return kept.ToArray();
    }

    /// <summary>Which bit a tag is, or -1 for a name nothing carries.</summary>
    public static int Bit(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return -1;

        var said = tag.Trim();
        var known = _known;

        for (var i = 0; i < known.Length; i++)
        {
            if (string.Equals(known[i], said, System.StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return Mint(said);
    }

    /// <summary>
    /// Gives a tag nobody has used before a bit of its own.
    ///
    /// **A tag written in the table is a tag.** Nothing here decides whether it is a good one: a
    /// word nothing carries scores nothing, exactly as a stat name nothing shares multiplies
    /// alone, and the table shows which are in use so a typo is visible rather than refused.
    /// </summary>
    private static int Mint(string tag)
    {
        lock (Minting)
        {
            var known = _known;

            // Asked again under the lock, because two threads can miss the same tag at once.
            for (var i = 0; i < known.Length; i++)
            {
                if (string.Equals(known[i], tag, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            if (known.Length >= Most)
                return -1;

            var grown = new string[known.Length + 1];

            System.Array.Copy(known, grown, known.Length);
            grown[known.Length] = tag;

            // One assignment, after the array is whole: a reader holding the old one finishes with
            // it and the next reader sees the new. See _known.
            _known = grown;

            return known.Length;
        }
    }

    /// <summary>
    /// Everything this object is, as bits, for the scoring loop.
    ///
    /// A long rather than an int: twenty seven tags is comfortably inside an int today and one
    /// tileset away from not being, and a silent overflow into the sign bit is not a failure anybody
    /// would find quickly.
    /// </summary>
    public static long Mask(Target target)
    {
        var mask = 0L;

        foreach (var tag in Of(target))
        {
            var bit = Bit(tag);

            if (bit >= 0)
                mask |= 1L << bit;
        }

        return mask;
    }

    /// <summary>
    /// Reads a scope - "rare_monster=50, excavated_chest=20" - into the tags it names.
    /// </summary>
    /// <remarks>
    /// **Both separators, because both get typed.** The plugin already has a field of this shape in
    /// Rewards - Prices by hand, which uses "name=value", and a colon is what anybody writing a list
    /// of scopes reaches for first. Accepting either costs a character in the parser and saves the
    /// question.
    ///
    /// An empty scope is not an error and not nothing - it is undecided, which is why it returns no
    /// pairs and no complaint, and why the table draws it red rather than the parser refusing it.
    /// </remarks>
    public static (int Tag, float Percent)[] Scope(string scope, out string wrong)
    {
        wrong = "";

        if (string.IsNullOrWhiteSpace(scope))
            return System.Array.Empty<(int, float)>();

        var found = new List<(int, float)>(2);

        foreach (var piece in scope.Split(','))
        {
            var part = piece.Trim();

            if (part.Length == 0)
                continue;

            var at = part.IndexOfAny(new[] { '=', ':' });

            if (at <= 0)
            {
                wrong = part;

                continue;
            }

            var name = part[..at].Trim();
            var bit = Bit(name);

            if (bit < 0)
            {
                wrong = name;

                continue;
            }

            if (!float.TryParse(part[(at + 1)..].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                wrong = part;

                continue;
            }

            found.Add((bit, percent));
        }

        return found.ToArray();
    }
}
