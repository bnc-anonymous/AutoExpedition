using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// The weight reference table's grammar: what a thing is made of, and what it does to other things.
///
/// **A row is a thing, a thing can be made of other things, and what it does is one expression.**
/// That is the whole model, and it exists because the old table priced leaves while everything that
/// turned an entity into a leaf lived in code - Weighing.TierSplitOfTarget deciding a remnant is three waves,
/// Blended deciding a monstermarker is a weighted average, Scan.Kind deciding elitemarker.ao is a
/// rare monster. None of those is a preference; all of them are facts about the game, which is what
/// a reference table is for. See weight_reference_table.md.
///
/// Two cells carry it:
///
/// - **Children** - "monster.rare x1", "remnant.wave x3", "pack.rare xValues[-1]". What else you get
///   for blowing this up. A count is a float, so a fraction is a probability; a count may name a
///   value the game states on a modifier instead of a number.
/// - **Effect** - "monster *= 1.04", "rare_monster *= 1.5 as rare_monster_count". What this does to
///   everything it reaches. One cell where Propagation %, Multiplicative behaviour and the carry
///   number used to be three.
///
/// Nothing here reads a settings tree or touches remote memory, so it can be reasoned about on its
/// own - which the arithmetic it replaces could not be.
/// </summary>
internal static class TableGrammar
{
    // ---------------------------------------------------------------- children

    /// <summary>
    /// One child edge: what you also get, and how many.
    ///
    /// <paramref name="From"/> is null for a written count, and otherwise names where the game states
    /// it - "Values[-1]" on the modifier this row matched. **There is deliberately no third case for
    /// "a written count standing in for an unread one":** a fallback turns a broken read into a
    /// plausible wrong answer, and the whole reason the strongbox pack counts were being parsed off a
    /// ground label is that nobody could see which of the two had happened.
    /// </summary>
    internal readonly record struct Child(string Id, float Count, string From, bool Pool)
    {
        /// <summary>How the edge is written, so a round trip through the cell is the same text.</summary>
        public override string ToString() =>
            (Id ?? "") + (Pool ? " pool" : "") +
            (From != null ? " x " + From : " x" + Printed(Count));
    }

    /// <summary>
    /// Reads a Children cell. Empty list and no complaint when the cell is blank.
    ///
    /// **Every failure names itself.** A child id nobody has heard of, a count that is not a number,
    /// a value slot that is not a slot: each comes back in <paramref name="wrong"/> and the cell
    /// paints red. The v1 table's equivalent - a mistyped group name in Multiplicative behaviour -
    /// was silently a separate group, which is the class of fault this column must not repeat.
    /// </summary>
    public static Child[] Children(string text, out string wrong)
    {
        wrong = "";

        if (string.IsNullOrWhiteSpace(text))
            return [];

        var found = new List<Child>();

        foreach (var piece in text.Split(','))
        {
            var part = piece.Trim();

            if (part.Length == 0)
                continue;

            // The count is after the LAST " x", because an id may legitimately contain one - "x" is
            // not a reserved character in a slug and "mod.max_packs" would split on nothing, but
            // "pack.rare x2" and "a.x.b x2" both have to land on the final separator.
            var at = part.LastIndexOf(" x", StringComparison.Ordinal);

            if (at < 0)
            {
                wrong = $"'{part}' has no count - write it as 'monster.rare x1'";

                return [.. found];
            }

            var id = part[..at].Trim();
            var count = part[(at + 2)..].Trim();

            if (id.Length == 0)
            {
                wrong = $"'{part}' names nothing to take";

                return [.. found];
            }

            var pool = false;

            if (id.EndsWith(" pool", StringComparison.OrdinalIgnoreCase))
            {
                pool = true;
                id = id[..^5].TrimEnd();
            }

            if (Slot(count, out var slot))
            {
                found.Add(new Child(id, 0f, slot, pool));

                continue;
            }

            if (!float.TryParse(count, NumberStyles.Float, CultureInfo.InvariantCulture, out var many))
            {
                wrong = $"'{count}' is not a count - write a number, or " +
                        "'matched.Values[-1]' to take it from the modifier this row matched";

                return [.. found];
            }

            if (many < 0f)
            {
                wrong = $"'{count}' is negative - a count cannot be";

                return [.. found];
            }

            found.Add(new Child(id, many, null, pool));
        }

        return [.. found];
    }

    /// <summary>
    /// Whether a count names a value the game states, and which one.
    ///
    /// **Addressed from the end by default, because the count sits last.** Measured on twenty
    /// strongboxes: an implicit guarding modifier carries three values and an explicit one two, and in
    /// both the pack count is the final entry - ChestSummonStrongboxImplicitHigh [1, 0, 7] beside
    /// "Guarded by 7 packs of Monsters", ChestSummonRares [1, 1] beside "Guarded by a pack of Rare
    /// Monsters". So Values[-1] is the form a row normally wants, and a positive index is there for
    /// the mod where it turns out not to be.
    /// </summary>
    public static bool Slot(string text, out string slot)
    {
        slot = null;

        if (text == null)
            return false;

        var said = text.Trim();

        // **"matched." is the whole of what this prefix buys, and it is worth a word.** The count used
        // to read "Values[-1]" and nothing on the row said whose values those were: the connection to
        // the matches cell above it was real and implicit, which is the shape of fault this table
        // exists to remove. Written out, the cell reads as a sentence - one pack of rares per the last
        // value of the modifier I matched.
        //
        // The bare form is still accepted, because a file written before this says it that way and a
        // migration that rejects its own history is a migration that loses work. It is normalised on
        // the way back out, so a row rewrites itself the first time anybody edits it.
        if (said.StartsWith(Matching, StringComparison.OrdinalIgnoreCase))
            said = said[Matching.Length..].TrimStart();

        if (!said.StartsWith("Values[", StringComparison.OrdinalIgnoreCase) ||
            !said.EndsWith("]", StringComparison.Ordinal))
            return false;

        var inner = said[7..^1].Trim();

        if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;

        slot = Matching + "Values[" + inner + "]";

        return true;
    }

    /// <summary>
    /// What a count reads from: the modifier this row's matches cell bound it to. See Slot.
    /// </summary>
    public const string Matching = "matched.";

    /// <summary>Which entry of a mod's Values a slot names, negative counting from the end.</summary>
    public static int Index(string slot)
    {
        if (slot == null)
            return -1;

        var at = slot.IndexOf('[');

        return at > 0 && int.TryParse(slot[(at + 1)..^1], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var said)
            ? said
            : -1;
    }

    /// <summary>The value a mod states at a slot, or null where it does not state one.</summary>
    public static float? Valued(IReadOnlyList<int> values, string slot)
    {
        if (values == null || values.Count == 0 || slot == null)
            return null;

        var at = Index(slot);

        if (at < 0)
            at += values.Count;

        return at >= 0 && at < values.Count ? values[at] : null;
    }

    // ---------------------------------------------------------------- effects

    /// <summary>
    /// One effect: what it reaches, by how much, and which stat it shares.
    ///
    /// <paramref name="Stat"/> empty means the effect is its own factor. **That is the only thing the
    /// stat name does** - two rows naming the same stat add their contributions and the stat's total
    /// is one factor in the product; a row naming no stat multiplies on its own. It replaces
    /// Multiplicative behaviour's "+name" and "*name" sigils, where the same distinction was carried
    /// by punctuation in a different column from the number it applied to.
    ///
    /// <paramref name="Here"/> means the effect reaches this row's own children and nothing the chain
    /// unearths later - a remnant's own socketed rune over its own waves. Where an effect reaches
    /// otherwise is the planner's business and not the table's.
    /// </summary>
    internal readonly record struct Effect(string Target, string Attribute, bool Multiplies,
        float Amount, string Stat, bool Here, bool Percent = false)
    {
        public override string ToString()
        {
            var text = new StringBuilder();

            // **Always written out, even where it was left off.** The attribute is the difference
            // between scaling what a thing is worth and scaling what its modifier does, and until it
            // was written the difference was inferred from whatever the target happened to be - a rule
            // nowhere on the row. Normalising on the way out means the cell teaches the form rather
            // than the form having to be known before the cell can be read. See Effects.
            text.Append(Target).Append('.').Append(Attribute)
                .Append(Multiplies ? " *= " : " += ");

            // **Written back the way it was written in**, because the two spellings are not equally
            // readable for the same job and the row's author picked one. See Effects.
            if (Percent && Amount >= 0f)
                text.Append('+');

            text.Append(Precise(Amount));

            if (Percent)
                text.Append('%');

            if (!string.IsNullOrEmpty(Stat))
                text.Append(" as ").Append(Stat);

            if (Here)
                text.Append(" here");

            return text.ToString();
        }

        /// <summary>
        /// What this multiplies by, whichever way the magnitude was spelt.
        ///
        /// **Amount is what was typed and this is what it means**, rather than the other way round.
        /// Storing the factor and reconstructing the percentage for display looked equivalent and is
        /// not: 16.01 becomes the float 1.1601, and (1.1601f - 1) x 100 comes back as 16.009998, so a
        /// cell round-tripped through the parser would grow four digits nobody wrote. The typed number
        /// is the one somebody chose, so it is the one kept.
        /// </summary>
        public float Factor => Percent ? 1f + Amount / 100f : Amount;

        /// <summary>
        /// What this contributes to its stat, which is the factor less one.
        ///
        /// Separated from Amount because that is the arithmetic every consumer wants and getting it
        /// wrong is the classic error here: two rows at 1.5 sharing a stat are 1 + 0.5 + 0.5 = 2.0,
        /// not 1.5 x 1.5 = 2.25. The share is what adds; the stat total is what multiplies.
        /// </summary>
        public float Share => Multiplies ? Factor - 1f : Amount;
    }

    /// <summary>What a thing is worth. The attribute every target has unless it is an effect.</summary>
    public const string Weight = "weight";

    /// <summary>
    /// How strong an effect is, which is what Power scales.
    ///
    /// Named for what it reads as on the row - "rune.effect *= +50%" - rather than for what the
    /// solver does with it. The reader of that cell knows what an effect is, because the column is
    /// called Effect; "propagation" was a word from the scoring side that appeared nowhere a player
    /// looks.
    /// </summary>
    public const string Magnitude = "effect";

    /// <summary>
    /// Every attribute an effect may aim at, closed for the reason the tag vocabulary is closed.
    ///
    /// **An open set here is a typo that does nothing.** "monster.wieght" would read as a target named
    /// monster.wieght, find no row and no tag, and score nought - which is the failure this whole table
    /// exists to stop. Two entries is enough to be worth the check: the pair is exactly the ambiguity
    /// that used to be resolved by guessing from the target.
    ///
    /// **Count is deliberately absent.** It cannot differ from weight: both multiply the same currency,
    /// so "twice as many rares" and "rares are worth twice as much" are the same number by
    /// construction - 100 x 2 x 2 is 400 whichever way round it is read. Size is absent because nothing
    /// scales it. Append if the game turns out to have something that genuinely is neither.
    ///
    /// Nothing outside the table reads the attribute: which of the two a row means is decided by what
    /// its effect AIMS at, since only an effect can be aimed at the rune tag. The word is here so a
    /// row can be read as a sentence and so a target with no weight is refused rather than scored as
    /// nought. See Checked and Weighing.Lift.
    /// </summary>
    public static readonly string[] Attributes = { Weight, Magnitude };

    /// <summary>
    /// Reads an Effect cell. Empty list and no complaint when the cell is blank.
    ///
    /// The vocabulary of targets is deliberately open: any tag, and any row id. There is no list to
    /// add to, because the vocabulary is whatever the game turns out to have and a closed one would
    /// need a code change per relic rather than per mechanic. What is NOT open is whether the target
    /// resolves - see Checked, which is what stops a typo becoming a silently separate group.
    /// </summary>
    public static Effect[] Effects(string text, out string wrong)
    {
        wrong = "";

        if (string.IsNullOrWhiteSpace(text))
            return [];

        var found = new List<Effect>();

        foreach (var piece in text.Split(','))
        {
            var part = piece.Trim();

            if (part.Length == 0)
                continue;

            var multiplies = true;
            var at = part.IndexOf("*=", StringComparison.Ordinal);

            if (at < 0)
            {
                at = part.IndexOf("+=", StringComparison.Ordinal);
                multiplies = false;
            }

            if (at < 0)
            {
                wrong = $"'{part}' has no operator - write 'monster *= 1.04' or " +
                        "'excavated_chest += 3'";

                return [.. found];
            }

            var target = part[..at].Trim();
            var rest = part[(at + 2)..].Trim();

            if (target.Length == 0)
            {
                wrong = $"'{part}' does not say what it reaches";

                return [.. found];
            }

            // **The attribute is after the last dot, and a row id has none.** Ids are separated with
            // a slash - "monster/rare", "mod/guarded_rares" - precisely so this split is
            // unambiguous. With dots in both places "monster.weight" and a row called "monster.rare"
            // would be the same shape, and a typo would slide silently between the two readings.
            //
            // Left off entirely means weight, always, with no inference from what the target turns out
            // to be. That matters for the four shipped rows that are BOTH a thing and an effect - the
            // goblin relic is weight 10 and carries 15 - where "relic *= +50%" cannot say which half it
            // means and the old rule picked one without saying so. An attribute that is wrong for its
            // target is then refused rather than guessed: see Checked, and rune.weight.
            var attribute = Weight;
            var dot = target.LastIndexOf('.');

            if (dot > 0)
            {
                var named = target[(dot + 1)..].Trim();
                var known = false;

                foreach (var name in Attributes)
                {
                    if (string.Equals(name, named, StringComparison.OrdinalIgnoreCase))
                        known = true;
                }

                if (!known)
                {
                    wrong = $"'{named}' is not an attribute - write one of " +
                            string.Join(", ", Attributes);

                    return [.. found];
                }

                attribute = named.ToLowerInvariant();
                target = target[..dot].TrimEnd();

                if (target.Length == 0)
                {
                    wrong = $"'{part}' names an attribute and nothing to take it from";

                    return [.. found];
                }
            }

            var here = false;

            if (rest.EndsWith(" here", StringComparison.OrdinalIgnoreCase))
            {
                here = true;
                rest = rest[..^5].TrimEnd();
            }

            var stat = "";
            var said = rest.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);

            if (said >= 0)
            {
                stat = rest[(said + 4)..].Trim();
                rest = rest[..said].TrimEnd();

                if (stat.Length == 0)
                {
                    wrong = $"'{part}' says 'as' and then no stat";

                    return [.. found];
                }
            }

            // **A percentage and a factor say the same thing, and one of them says it clearly.**
            //
            // "+16.01%" and "1.1601" are the same effect. The percentage is what the game prints -
            // "50% increased number of Rare Monsters" - and it is the form that survives being read
            // at a glance: two runes tie-broken at 16 and 16.01 read as 16 and 16.01, where as
            // factors they are 1.16 and 1.1601 and the distinction is four characters deep.
            //
            // A bare factor stays legal because some effects are not increases at all. "Elites are
            // Duplicated" is x2, and writing that as +100% describes the arithmetic rather than the
            // modifier.
            //
            // A flat addition takes no percentage: "+= 3%" would mean three per cent of a weight
            // nobody named, which is not a thing the table can say.
            var percent = false;

            if (rest.EndsWith("%", StringComparison.Ordinal))
            {
                if (!multiplies)
                {
                    wrong = $"'{part}' adds a percentage of nothing - use *= for a percentage, " +
                            "or += with a flat number";

                    return [.. found];
                }

                percent = true;
                rest = rest[..^1].TrimEnd();

                if (rest.StartsWith("+", StringComparison.Ordinal))
                    rest = rest[1..].TrimStart();
            }

            if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var amount))
            {
                wrong = $"'{rest}' is not a number";

                return [.. found];
            }



            var effect = new Effect(target, attribute, multiplies, amount, stat, here, percent);

            // **A multiplier below nought would invert what it reaches**, which nothing in the game
            // does and which would silently make covered content worth less than not covering it. A
            // flat addition may legitimately be negative, and so may a percentage - "-20%" is a real
            // modifier - so the test is on the factor rather than on the number as written.
            if (multiplies && effect.Factor < 0f)
            {
                wrong = $"'{rest}' multiplies by a negative - did you mean += ?";

                return [.. found];
            }

            found.Add(effect);
        }

        return [.. found];
    }

    /// <summary>
    /// A row's effects, from the new cell if it has one and from the old three if it does not.
    ///
    /// **The one entry point, so nothing reads Scope, Carries or Combines again.** The defaults file
    /// still speaks v1 and a custom file somebody tuned certainly does, so the old three are read
    /// here and said in the new grammar rather than being migrated on disk before anything can use
    /// them. That ordering is deliberate: a translation that only ever runs in memory can be compared
    /// against the arithmetic it replaces, and a file rewrite cannot be undone.
    ///
    /// See Translated for the mapping, which is mechanical.
    /// </summary>
    public static Effect[] EffectsOfRow(string id, out string wrong)
    {
        var row = Wrt.Of(id);

        if (row == null)
        {
            wrong = "";

            return [];
        }

        // **The effect cell, and nothing else.** A row used to be translatable from a scope, a
        // combines and a carries when it had no effect of its own. Those cells are gone and
        // nothing reads them, so an effect is the only place a magnitude can be.
        return Effects(row.Effect ?? "", out wrong);
    }

    /// <summary>
    /// How a v1 row is named, for the two places the id decides what a number means.
    ///
    /// **A rune row's Weight is a percentage, not content**, and nothing about the row says so - the
    /// id does, and that is the whole of the problem this table is being rewritten to fix. rune:soul
    /// at 4 means "four per cent more monsters", while kind:MonsterRare at 20 means "a rare monster is
    /// worth twenty". One column, two units, told apart by a prefix.
    ///
    /// In v2 that row is weight nought with an effect and the ambiguity is gone. Until the defaults
    /// file is rewritten it has to be read somewhere, and here is better than in the four callers that
    /// would otherwise each decide for themselves. **Dies at stage four.**
    /// </summary>
    private static bool Runic(string id) =>
        id != null && id.StartsWith("rune:", StringComparison.Ordinal);

    /// <summary>
    /// What a row is worth as CONTENT, which for a rune is nothing.
    ///
    /// See Runic. A rune is an effect wearing a weight column, so counting its percentage as points of
    /// loot would add a number on the wrong scale into every total that reached it - and the Total
    /// column would then show 4 for a Soul Rune, which is the "no connection between changing a
    /// propagation value and a weight number" complaint in a new place.
    /// </summary>
    private static float Content(string id, Wrt.Row row)
    {
        if (row?.Weight == null)
        {
            // **A discovered row with no weight cell is unset, not nought.**
            //
            // The two layers disagreed about what an empty cell means. Unknowns.Of reads
            // "said?.Weight ?? had" - so the planner falls through to the discovery weight, which is
            // the ordinary default for most things and the kind's prior for an entrance. This
            // returned nought flat, so the table showed nought for a row the objective was scoring
            // at one. Eight shipped rows were in that state, among them a superunique boss and a
            // Gelid Zealot, both correctly tagged as monsters and both reading as worth nothing.
            //
            // Spotted from the tags: a row saying "weight_modifiable, monster" and totalling nought
            // is not a monster the game undervalues, it is two readers disagreeing. The table has to
            // show what is used, or the number in front of you is not the number in the plan.
            //
            // Asked of Unknowns rather than guessed, so the two cannot drift again.
            return id != null && id.StartsWith(Wrt.Id.Found(""), StringComparison.Ordinal)
                ? Unknowns.Of(id[Wrt.Id.Found("").Length..])
                : 0f;
        }

        // Only where the translation took the magnitude from Weight. A rune with a scope carries its
        // percentage in the scope, and its weight is then the non-stacking bookkeeping figure - still
        // not content, so still nought.
        return Runic(id) ? 0f : row.Weight.Value;
    }


    /// <summary>
    /// One translated effect, as a percentage, because that is what a v1 magnitude was.
    ///
    /// **Not a factor, and losing that cost the tie-breaks.** Every number the old columns held was a
    /// percentage - a scope's "monster=20", a rune row's weight, a relic's carry - and the first
    /// translation turned each into 1 + n/100 and printed it at three decimals. Two runes deliberately
    /// separated at 16 and 16.01, so that a chain forced to choose between equals always chooses the
    /// same one, both came out as "monster *= 1.16". The tie-break was still in the file and no longer
    /// on the row.
    ///
    /// Precise as well as percentage, so the increment survives whichever spelling is used - see
    /// Precise for why four decimals and not more.
    /// </summary>
    private static string Line(string target, float percent, string stat) =>
        target + "." + Weight + " *= " + (percent >= 0f ? "+" : "") + Precise(percent) + "%" +
        (stat.Length > 0 ? " as " + stat : "");

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// Everything wrong with one row, in the order a reader would meet it, or empty when it is sound.
    ///
    /// **A target that resolves to nothing is the fault this exists for.** In v1 a mistyped group name
    /// was a silently separate group and a mistyped scope tag was dropped by the parser with the row
    /// still looking filled in. Both are now stated, because a table nobody can trust is worth less
    /// than no table.
    /// </summary>
    public static string[] Checked(string id)
    {
        if (_said != Wrt.Revision)
        {
            _wrongs.Clear();
            _said = Wrt.Revision;
        }

        if (_wrongs.TryGetValue(id ?? "", out var had))
            return had;

        var found = Checking(id);

        _wrongs[id ?? ""] = found;

        return found;
    }

    private static readonly Dictionary<string, string[]> _wrongs = new(StringComparer.Ordinal);
    private static int _said = -1;

    private static string[] Checking(string id)
    {
        var row = Wrt.Of(id);

        if (row == null)
            return [];

        var wrongs = new List<string>();
        var kids = Children(row.Children, out var childWrong);

        foreach (var kid in kids)
        {
            if (kid.Pool)
            {
                // **Reported rather than refused, now that any word is a tag.** The question
                // is no longer whether the vocabulary admits it - it admits everything - but
                // whether anything will ever answer to it: the classifier derives it, or some
                // row's Tags cell writes it down. Neither is true of a misspelling.
                if (!Tags.Carried(kid.Id))
                    wrongs.Add($"'{kid.Id} pool' names a tag nothing carries - no row writes it " +
                               "and the classifier does not derive it");

                continue;
            }

            if (Wrt.Of(kid.Id) == null)
                wrongs.Add($"child '{kid.Id}' has no row");
        }

        if (childWrong.Length > 0)
            wrongs.Add(childWrong);

        var effects = EffectsOfRow(id, out var effectWrong);

        foreach (var effect in effects)
        {
            // A tag, or a row - one namespace, so a relic can name "rare_monster" and a rune can name
            // "monster/rare" and neither has to know which kind of thing the other is.
            if (!Tags.Carried(effect.Target) && Wrt.Of(effect.Target) == null)
            {
                wrongs.Add($"'{effect.Target}' is neither a tag nor a row");

                continue;
            }

            // **An effect that provably does nothing, said rather than scored as nought.**
            //
            // A rune has no weight - its number is a percentage and Content returns nought for it - so
            // "rune.weight *= +50%" multiplies zero and is silently inert. That is the shape of fault
            // this column was rewritten to end, and it is exactly the mistake the old syntax invited:
            // with no attribute to write, "rune *= +50%" had to be guessed at, and guessing right was
            // the only thing standing between a working Power rune and a dead one.
            if (string.Equals(effect.Attribute, Weight, StringComparison.Ordinal) &&
                Weightless(effect.Target))
            {
                wrongs.Add($"'{effect.Target}' has no weight to scale - did you mean " +
                           $"{effect.Target}.{Magnitude}?");
            }
        }

        if (effectWrong.Length > 0)
            wrongs.Add(effectWrong);

        // **A number that is stored, shown, and not read.** Two cells state a magnitude and the scope
        // wins both times: Translated returns on the scope pairs and never reaches the carry, and
        // Weighing.PricedRowOfTarget zeroes the carry outright whenever a scope exists - deliberately, with the
        // reason in its own comment, because counting both would pay the same uplift twice.
        //
        // So this is NOT a double count. It was first reported as one, on the strength of Planner's
        // `(choice.Carries + reach) * downstream` adding the two terms - which it does, but nothing
        // ever puts a scoped row's carry into the first of them. The guard is there and it is correct.
        //
        // What it is instead: the carry is **inert**, and on a merged row the inert number is the
        // user's. The settings migration wrote a figure they had tuned into Carries, a later shipped
        // row gained a Scope, the merge kept one cell from each - and the shipped number is the one
        // that scores. Worth complaining about because a tuned 10 quietly scoring as 50 is a
        // disagreement with the table that the table was answering on its own.
        foreach (var piece in (row.Matches ?? "").Split(','))
        {
            var part = piece.Trim();

            if (part.Length == 0)
                continue;

            var known = false;

            foreach (var prefix in Bindings)
            {
                if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    known = true;
            }

            if (!known)
            {
                wrongs.Add($"'{part}' is not a binding - it must start with one of " +
                           string.Join(", ", Bindings));
            }
            else if (Bindings.Any(x => string.Equals(part, x, StringComparison.OrdinalIgnoreCase)))
            {
                wrongs.Add($"'{part}' names a kind of binding and nothing to bind to");
            }
        }

        var rolled = Total(id);

        if (rolled.Wrong?.Length > 0)
            wrongs.Add(rolled.Wrong);

        return [.. wrongs];
    }

    /// <summary>
    /// Whether a target is effects rather than things, so scaling its weight would scale nothing.
    ///
    /// The rune tag is the one tag that marks an effect rather than something a blast can catch - see
    /// Tags.Runes - and a row is weightless when its own content is nought, which every rune row is.
    /// Everything else is a thing and has a weight, even where that weight is currently unset: an
    /// unpriced chest is a chest somebody has not answered for, not a chest that cannot be worth
    /// anything.
    /// </summary>
    private static bool Weightless(string target)
    {
        if (string.Equals(target, Tags.Known[Tags.Runes], StringComparison.OrdinalIgnoreCase))
            return true;

        var row = Wrt.Of(target);

        return row != null && Content(target, row) == 0f && row.Weight != null;
    }

    // ---------------------------------------------------------------- the rollup

    /// <summary>
    /// What a row is worth with everything it brings, and where a part of it could not be settled.
    ///
    /// **Fixed and PerValue are kept apart rather than added**, because a child whose count comes off
    /// a modifier has no total until an entity states the value. Folding it in at one would be a
    /// fallback wearing a total's clothes: the table would show a plausible number for something it
    /// cannot know, which is the fault this design exists to remove. So the cell shows "20 + 80 per
    /// Values[-1]" and the scanner computes the real figure where the value is in front of it.
    /// </summary>
    internal readonly record struct Rolled(float Fixed, (string From, float Each)[] PerValue,
        string Wrong)
    {
        public bool Settled => PerValue == null || PerValue.Length == 0;

        public override string ToString()
        {
            if (Wrong?.Length > 0)
                return "?";

            var text = new StringBuilder(Printed(Fixed));

            foreach (var (from, each) in PerValue ?? [])
                text.Append(" + ").Append(Printed(each)).Append(" per ").Append(from);

            return text.ToString();
        }
    }

    /// <summary>How deep a row may be made of rows. A remnant is three; this is room to spare.</summary>
    private const int Deepest = 8;

    // **The two memos below are main-thread only, and stage two has to deal with that.**
    //
    // The table draws every row every frame and the dump walks all of them, so both are worth caching
    // against Wrt.Revision - the counter that exists for exactly this, a reader recording what it
    // computed under and recomputing when the table moves.
    //
    // Once the planner reads Total the caching has to change, because a solve runs across eight
    // workers and two of them writing one dictionary is a torn read at best. The pattern already in
    // the codebase is the answer: numbers the search needs are computed once per solve and carried on
    // PlanEnvironment - see its Effects and Bands - rather than looked up from shared state inside the
    // scoring loop. Totals belong there too, as an array indexed the way Effects is.

    /// <summary>
    /// The rollup for one row: its own weight plus everything its children bring.
    ///
    /// Memoised against Wrt.Revision, because the table draws every row every frame and the planner
    /// asks per covered marker per scored chain. The revision counter already exists for exactly this
    /// - a reader records what it computed under and recomputes when the table moves. See
    /// Wrt.Revision.
    /// </summary>
    public static Rolled Total(string id)
    {
        if (_at != Wrt.Revision)
        {
            _totals.Clear();
            _at = Wrt.Revision;
        }

        if (_totals.TryGetValue(id ?? "", out var had))
            return had;

        var walking = new List<string>(Deepest);
        var rolled = Roll(id, walking, 0);

        _totals[id ?? ""] = rolled;

        return rolled;
    }

    private static readonly Dictionary<string, Rolled> _totals = new(StringComparer.Ordinal);
    private static int _at = -1;

    private static Rolled Roll(string id, List<string> walking, int depth)
    {
        if (string.IsNullOrEmpty(id))
            return new Rolled(0f, [], "");

        var row = Wrt.Of(id);

        // **Nothing written down is a fault in a CHILD and a normal state in a root.** A children cell
        // naming a row that does not exist is a typo and has to say so; a row nobody has priced yet is
        // exactly what "unset" means, and reporting it turned the Total column red for every
        // discovered object on the site - which is most of them, on a map nobody has walked before.
        if (row == null)
            return new Rolled(0f, [], depth == 0 ? "" : $"no row called '{id}'");

        // **A cycle is a validation error on the row that closes it, not a hang.** Named rather than
        // counted, because "remnant contains remnant.wave contains remnant" is the useful message and
        // "too deep" is not.
        if (walking.Contains(id, StringComparer.Ordinal))
            return new Rolled(0f, [], $"'{id}' contains itself: " +
                                      string.Join(" -> ", walking) + " -> " + id);

        if (depth >= Deepest)
            return new Rolled(0f, [], $"'{id}' is more than {Deepest} deep");

        var fixt = Content(id, row);
        var per = new List<(string, float)>();

        var kids = Children(row.Children, out var wrong);

        if (wrong.Length > 0)
            return new Rolled(fixt, [], wrong);

        walking.Add(id);

        try
        {
            foreach (var kid in kids)
            {
                var inner = Roll(kid.Id, walking, depth + 1);

                if (inner.Wrong?.Length > 0)
                    return new Rolled(fixt, [], inner.Wrong);

                // A child of a per-value child is per-value too, and there is nothing sound to
                // multiply two unknown values by, so it is refused rather than approximated.
                if (kid.From != null)
                {
                    if (!inner.Settled)
                        return new Rolled(fixt, [],
                            $"'{kid.Id}' is counted from {kid.From} and itself holds a counted " +
                            "child - one value per branch");

                    per.Add((kid.From, inner.Fixed));

                    continue;
                }

                fixt += kid.Count * inner.Fixed;

                foreach (var (from, each) in inner.PerValue ?? [])
                    per.Add((from, kid.Count * each));
            }
        }
        finally
        {
            walking.RemoveAt(walking.Count - 1);
        }

        return new Rolled(fixt, [.. per], "");
    }

    /// <summary>
    /// The rollup written out line by line, for the popup behind the Total column.
    ///
    /// **If a number in this plugin cannot be explained by a popup, it is a bug in the table.** That
    /// is the whole claim the rewrite makes, and this is where it is either true or not: every child,
    /// its count, what one of it is worth, and the product, down to the leaves.
    /// </summary>
    public static string Explain(string id)
    {
        var text = new StringBuilder();

        Explaining(id, text, 0, 1f, []);

        var rolled = Total(id);

        text.AppendLine();
        text.Append("total  ").Append(rolled);

        if (rolled.Wrong?.Length > 0)
            text.Append("   <- ").Append(rolled.Wrong);

        return text.ToString();
    }

    private static void Explaining(string id, StringBuilder text, int depth, float times,
        List<string> walking)
    {
        var row = Wrt.Of(id);
        var pad = new string(' ', depth * 2);

        if (row == null)
        {
            text.AppendLine($"{pad}{id}   <- no row");

            return;
        }

        var own = Content(id, row);
        var label = Wrt.Label(id);

        text.Append(pad).Append(id);

        if (label.Length > 0)
            text.Append("  (").Append(label).Append(')');

        text.Append("  weight ").Append(Printed(own));

        if (times != 1f)
            text.Append("  x").Append(Printed(times)).Append(" = ").Append(Printed(own * times));

        text.AppendLine();

        if (walking.Contains(id, StringComparer.Ordinal) || depth >= Deepest)
        {
            text.AppendLine($"{pad}  ...contains itself");

            return;
        }

        var kids = Children(row.Children, out var wrong);

        if (wrong.Length > 0)
        {
            text.AppendLine($"{pad}  <- {wrong}");

            return;
        }

        walking.Add(id);

        foreach (var kid in kids)
        {
            text.Append(pad).Append("  x").Append(kid.From ?? Printed(kid.Count)).Append(' ')
                .AppendLine(kid.Pool ? kid.Id + " (pool)" : "");

            Explaining(kid.Id, text, depth + 1,
                kid.From != null ? times : times * kid.Count, walking);
        }

        walking.RemoveAt(walking.Count - 1);
    }

    // ---------------------------------------------------------------- what binds a row to the world

    /// <summary>
    /// A modifier on the object, with the values the game printed on it.
    ///
    /// **One prefix, where there were two.** This was written as one per modifier list -
    /// ExplicitModData and ImplicitModData - on the reasoning that the game states which list a
    /// modifier came from and that is how implicit and explicit are told apart. True, and useless: a
    /// modifier's ID already says it. Across every dump taken, one hundred and forty-four distinct
    /// ids, not one appears in both lists - and the implicit guarding mods carry the word in their own
    /// names, ChestSummonStrongboxImplicitHigh against ChestSummonNormals.
    ///
    /// So the two prefixes distinguished nothing and cost a silent failure: pick the wrong list when
    /// writing a row and it matches nothing, with no complaint, because a row that matches nothing is
    /// indistinguishable from a modifier nobody has written a row for. One prefix cannot be got wrong.
    ///
    /// **Which list a modifier came from is still read and still reported** - the dump prints it per
    /// modifier - it simply is not what identifies the row. Splitting this again is a one-line change
    /// on the day an id turns up in both lists.
    /// </summary>
    public const string Mod = "mod:";

    /// <summary>
    /// Which row a modifier answers to, or null where nobody has written one for it.
    ///
    /// **The component path is on the row, so the row states its own source.** A strongbox's pack
    /// count is read off the ground label today, and Scan.ExplicitPacks writes that down as though it
    /// were a fact about the game - "the mod ids carry no numbers". That is true of
    /// ObjectMagicProperties.Mods, a list of names, and says nothing about ExplicitModData, which
    /// returns ItemMod and whose Values carry the count. Measured on twenty strongboxes: the count is
    /// the last value, and the mod id names the rarity, seven for seven against the labels.
    ///
    /// Implicit and explicit need no telling apart: they are different modifiers with different ids,
    /// and where they mean the same thing one row takes both - see the guarding rows, which carry two
    /// bindings each. What the game states is which LIST a modifier is in, and that -
    /// replacing Scan.Guarding's rule that the first guarding sentence rendered is the inherent one,
    /// documented there as "an assumption about the label's layout rather than something the game
    /// states, and the part of this worth doubting first".
    /// </summary>
    /// <summary>
    /// Which row a modifier answers to, matched EXACTLY on its id.
    ///
    /// Separate from Matched, which matches a substring and takes the longest - right for a metadata
    /// path or an art file, where a family and one of its members are both real answers, and wrong
    /// here: a modifier id is a whole name, and "ChestSummonRares" must not be found inside
    /// "ChestSummonRaresAndMagics" if the game ever ships one.
    /// </summary>
    public static string RowForModifier(string mod)
    {
        if (string.IsNullOrEmpty(mod))
            return null;

        Indexed();

        return _matches.GetValueOrDefault(Mod + mod);
    }

    /// <summary>Every matches: cell in the table, keyed by what it says. Rebuilt when the table moves.</summary>
    private static void Indexed()
    {
        if (_matchAt == Wrt.Revision)
            return;

        _matches.Clear();

        foreach (var (id, _) in Wrt.Standing.Concat(Wrt.Yours))
        {
            // **An id that names an identifier IS a binding.** A row bound to exactly one thing in the
            // world does not need a second name for itself: "icon:RewardChestCurrency" says what the
            // row is and what finds it in one string, and a matches cell repeating it would be a copy
            // free to disagree. So an id wearing a binding prefix registers itself, and matches is
            // left for the rows that genuinely bind several things - a relic wearing two modifier ids
            // and an art, say.
            //
            // A row named by another row as a CHILD keeps a short slug instead, because somebody has
            // to type it. That is the whole of the rule. See the row-id note in NOTES.
            if (Bindings.Any(x => id.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                _matches[id] = id;

            var said = Wrt.Of(id)?.Matches;

            if (said == null)
                continue;

            foreach (var piece in said.Split(','))
            {
                var part = piece.Trim();

                if (part.Length > 0)
                    _matches[part] = id;
            }
        }

        // **Parsed once, not on every question.** Matched used to take each rule apart as it
        // compared it - a substring to drop the prefix and a Trim to drop the stars, both of which
        // allocate - for every rule in the table, on every query. The sweep asks this for every
        // entity it walks, so that was hundreds of throwaway strings per entity, twice a second,
        // and it showed up as a gen0 collection every five frames.
        //
        // The rules only change when the table does, which is what Wrt.Revision already says.
        _rules.Clear();

        foreach (var (rule, id) in _matches)
        {
            var colon = rule.IndexOf(':');

            if (colon < 0)
                continue;

            var want = rule[(colon + 1)..];
            var core = want.Trim('*');

            if (core.Length == 0)
                continue;

            _rules.Add((rule[..(colon + 1)], core, want.StartsWith('*'), want.EndsWith('*'), id));
        }

        _matchAt = Wrt.Revision;
    }

    /// <summary>
    /// The matching rules with their prefix and stars already taken off. See Indexed and Matched.
    /// </summary>
    private static readonly List<(string Prefix, string Core, bool Opens, bool Ends, string Id)>
        _rules = new();

    private static readonly Dictionary<string, string> _matches = new(StringComparer.OrdinalIgnoreCase);
    private static int _matchAt = -1;

    /// <summary>A metadata path this row is bound to. See Matched.</summary>
    public const string Path = "path:";

    /// <summary>
    /// The art file a marker is wearing. See Matched.
    ///
    /// **The binding the table could never express.** Scan.Kind decides that elitemarker.ao is a rare
    /// monster and monstermarker.ao is a white one, and Weighing then maps those kinds to rows - so by
    /// the time the table saw the thing, the marker and the monster it stands for had already been
    /// merged. Nothing in the table said elitemarker_02.ao spawns a rare, and a patch adding
    /// elitemarker_04.ao needed a code change rather than a row.
    /// </summary>
    public const string Art = "art:";

    /// <summary>
    /// The minimap icon an object is drawn with. See Matched.
    ///
    /// **The identity a Grand site's chests have and their art does not.** Every reward chest there
    /// wears ChestCurrency.ao, dull and bright alike, and the icon is where they differ -
    /// RewardChestCurrency against RewardChestCurrencyRare. The icon is also set from the moment the
    /// entity exists, where the doodad reads as an empty string from across the map, which is what
    /// once dropped eighteen chests out of a scan.
    ///
    /// Longest match wins, which is what keeps CurrencyRare from being read as Currency - the rule
    /// that used to be the ORDER of a switch's arms, where getting it backwards was silent.
    /// </summary>
    public const string Icon = "icon:";

    /// <summary>
    /// Every form a matches: cell may take, so a misspelt one is refused rather than ignored.
    ///
    /// **A row that matches nothing looks exactly like a row nobody has written yet.** That is the
    /// failure this list exists to stop: type "are:elitemarker" and the row binds to no entity, prices
    /// nothing, and complains about neither - which is the shape of every fault this table is being
    /// rewritten to remove.
    /// </summary>
    public static readonly string[] Bindings = { Art, Icon, Path, Mod };

    /// <summary>
    /// Which row an object's metadata answers to, or null where nobody has written one.
    ///
    /// **The longest matching rule wins.** Two rows may both answer for one object - a family and one
    /// of its members - and the more specific is the one somebody wrote on purpose. Ordering a
    /// dictionary walk by length is cheap here because there are a few dozen rules, not because it is
    /// asked often: a target is classified once per area.
    /// </summary>
    public static string Matched(string metadata) => Matched(metadata, Path);

    /// <summary>
    /// The same, against whichever kind of identifier the caller holds. See Path and Art.
    ///
    /// **A rule matches the WHOLE name unless it says otherwise.** This matched a substring anywhere,
    /// which is looseness nothing asked for in either direction: "chestmarker2" would have answered
    /// for a bigchestmarker2x.ao, and every whole name in the table - eleven strongbox paths, six
    /// minimap icons - was being run through it for no benefit at all. It is also why the two Grand
    /// currency chests needed a longest-wins tiebreak, RewardChestCurrency being a substring of
    /// RewardChestCurrencyRare; matched whole, they simply are not each other.
    ///
    /// A star at either end opens that end, and it is a decision typed into a cell rather than what
    /// the engine assumes. **Almost nothing shipped uses one.** Every art, icon and named base is a
    /// whole identifier, families included: the three elite marker models are three rules, the five
    /// henges are five. Writing them out is longer and it is the whole point - you can read the table
    /// and know exactly what it answers for, and a model the game adds turns up as an unrecognised
    /// row rather than being quietly absorbed by a stem.
    ///
    /// **Two rules do use one, and they are the shape that earns it.** A strongbox is named
    /// &lt;Base&gt;Strongbox&lt;Tier&gt; and the tier is what the weight tracks, so
    /// <c>path:*StrongboxHigh</c> and <c>path:*StrongboxExpedition</c> price a base nobody has
    /// written a row for at its tier's number instead of at the unrecognised weight. The set of bases
    /// is open - three of the eight seen had no row, one of them in forty site files - so enumerating
    /// them goes stale the same way every time GGG adds one. Every named base still wins, because it
    /// is the longer rule.
    ///
    /// The difference from a stem over art files: those absorbed unknown OBJECTS into a specific row.
    /// These absorb an unpriced base into a row whose name says exactly that, which is a true
    /// statement about it rather than a guess at which thing it is.
    ///
    /// Length is measured on the rule without its stars, so an exact rule and a loose one of the same
    /// text tie rather than the loose one winning for being longer.
    /// </summary>
    public static string Matched(string said, string prefix)
    {
        if (string.IsNullOrEmpty(said))
            return null;

        LeafCalls.TableQueries++;

        Indexed();

        string found = null;
        var longest = 0;

        // Whole-prefix equality rather than StartsWith, which is the same test: a rule's prefix is
        // everything up to its first colon, and a prefix argument always ends in one - so
        // "pathological:X" no more begins with "path:" than it equals it.
        for (var i = 0; i < _rules.Count; i++)
        {
            var (rulePrefix, core, opens, ends, id) = _rules[i];

            if (core.Length <= longest ||
                !string.Equals(rulePrefix, prefix, StringComparison.Ordinal))
                continue;

            var hit = opens
                ? ends
                    ? said.Contains(core, StringComparison.OrdinalIgnoreCase)
                    : said.EndsWith(core, StringComparison.OrdinalIgnoreCase)
                : ends
                    ? said.StartsWith(core, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(said, core, StringComparison.OrdinalIgnoreCase);

            if (!hit)
                continue;

            found = id;
            longest = core.Length;
        }

        return found;
    }

    /// <summary>
    /// What one matched modifier brings, as the tier split of its children times the value it states.
    ///
    /// Nought where the row says nothing, and nought where the mod states no value at the slot its row
    /// names - which is an error rather than a default, and one the caller reports. **There is no
    /// written count standing in for an unread one**: that is what turns a broken read into a
    /// plausible wrong answer.
    /// </summary>
    public static (float Rare, float Magic, float Normal) Brought(string id, IReadOnlyList<int> values,
        out string wrong)
    {
        wrong = "";

        var row = Wrt.Of(id);

        if (row == null)
            return default;

        var rare = 0f;
        var magic = 0f;
        var normal = 0f;

        var kids = Children(row.Children, out var said);

        foreach (var kid in kids)
        {
            var count = kid.Count;

            if (kid.From != null)
            {
                var stated = Valued(values, kid.From);

                if (stated == null)
                {
                    wrong = $"{id} counts {kid.Id} from {kid.From} and the modifier states no value " +
                            "there";

                    continue;
                }

                count = stated.Value;
            }

            var (r, m, n) = TierSplitOfRow(kid.Id);
            var own = Wrt.Of(kid.Id) is { } inner ? Content(kid.Id, inner) : 0f;
            var mask = TagMaskOfRow(kid.Id);

            // The child's own weight lands in whichever tier it says it is, and whatever it brings
            // below it comes through Tiers - so a pack row holding monsters and a row that IS a
            // monster both work without a second rule.
            if ((mask & (1L << Tags.Rares)) != 0L)
                r += own;
            else if ((mask & (1L << Tags.Magics)) != 0L)
                m += own;
            else if (mask != 0L)
                n += own;

            rare += count * r;
            magic += count * m;
            normal += count * n;
        }

        if (said.Length > 0)
            wrong = said;

        return (rare, magic, normal);
    }

    // ---------------------------------------------------------------- what a row digs up

    /// <summary>
    /// Every distinct thing a row's children amount to, with what it is and what it is worth.
    ///
    /// **One description of a composition, which three copies of the rule used to be.** Weighing had
    /// Tiers for the total, Waving for the same thing as whole monsters, Splitting for a marker's own
    /// blend and Blended for the average of that blend - four functions over one fact, and two copies
    /// of a rule is how the blast circles came to contradict the score they sat inside.
    ///
    /// The root's OWN weight is deliberately excluded. A remnant is worth being a remnant and then
    /// worth its waves, and those are two different things reaching two different accumulators - the
    /// first is content at the link, the second is what a rune carried onto it multiplies. Total
    /// includes both; this is only the part below.
    ///
    /// Merged per leaf row rather than per tier, because whether two modifiers meet on one creature is
    /// decided by the tags ONE creature carries - a wave rare is monster, rare_monster and modifiable
    /// at once. The caller buckets by tier where it wants tiers.
    /// </summary>
    public static (string Id, long Mask, float Worth, float Many)[] ContributionsOfRow(string root)
    {
        if (_spreadAt != Wrt.Revision)
        {
            _spreads.Clear();
            _spreadAt = Wrt.Revision;
        }

        if (_spreads.TryGetValue(root ?? "", out var had))
            return had;

        var found = new Dictionary<string, float>(StringComparer.Ordinal);
        var counted = new Dictionary<string, float>(StringComparer.Ordinal);

        Spreading(root, 1f, found, counted, [], 0);

        var made = new (string, long, float, float)[found.Count];
        var at = 0;

        foreach (var (id, worth) in found)
            made[at++] = (id, TagMaskOfRow(id), worth, counted.GetValueOrDefault(id));

        _spreads[root ?? ""] = made;

        return made;
    }

    private static readonly Dictionary<string, (string, long, float, float)[]> _spreads =
        new(StringComparer.Ordinal);

    private static int _spreadAt = -1;

    /// <param name="many">
    /// How many of each id the walk arrives at, beside what they are worth.
    ///
    /// **It was being multiplied away.** Spreading already knows the count - it is the "times" it
    /// scales each child's worth by - and threw it out, so the pipeline below carried weight and
    /// nothing else. A flat effect ("excavated_chest.weight += 3") is worth three PER CHEST, which
    /// is unanswerable without this.
    /// </param>
    private static void Spreading(string id, float times, Dictionary<string, float> into,
        Dictionary<string, float> many, List<string> walking, int depth)
    {
        var row = Wrt.Of(id);

        if (row == null || depth >= Deepest || walking.Contains(id, StringComparer.Ordinal))
            return;

        var kids = Children(row.Children, out var wrong);

        if (wrong.Length > 0)
            return;

        walking.Add(id);

        foreach (var kid in kids)
        {
            // A child counted off a modifier cannot be spread without an entity in front of it - see
            // Rolled, and why the fixed and per-value halves are kept apart. The caller that has the
            // entity multiplies the pack rows itself; nothing here invents a count.
            if (kid.From != null || kid.Pool)
                continue;

            var inner = Wrt.Of(kid.Id);
            var own = inner == null ? 0f : Content(kid.Id, inner);

            if (own != 0f)
            {
                into[kid.Id] = into.GetValueOrDefault(kid.Id) + times * kid.Count * own;
                many[kid.Id] = many.GetValueOrDefault(kid.Id) + times * kid.Count;
            }

            Spreading(kid.Id, times * kid.Count, into, many, walking, depth + 1);
        }

        walking.RemoveAt(walking.Count - 1);
    }

    /// <summary>
    /// What a row says it is, as a tag mask.
    ///
    /// **Read from the row, not derived from a kind.** Weighing.Wearing fell back to "monster,
    /// modifiable, and whichever tier the caller passed in", which meant the tier was decided by the
    /// call site and the table showed tags on the rare row that governed only the marker - the hidden
    /// state the tags column exists to end. A composition leaf states what it is or it reaches no
    /// modifier at all, which Classed already handles: its content counts and cannot be scaled.
    /// </summary>
    public static long TagMaskOfRow(string id)
    {
        var said = Wrt.Of(id)?.Tags;

        if (said == null)
            return 0L;

        var mask = 0L;

        foreach (var name in Tags.Read(said))
        {
            var bit = Tags.Bit(name);

            if (bit >= 0)
                mask |= 1L << bit;
        }

        return mask;
    }

    /// <summary>
    /// A composition split into the three monster tiers, for the callers that want a total per tier.
    ///
    /// Bucketed off Spread so the split and the whole cannot disagree - which is exactly what Tiers
    /// and Waving were two chances to get wrong.
    /// </summary>
    public static (float Rare, float Magic, float Normal) TierSplitOfRow(string root)
    {
        var rare = 0f;
        var magic = 0f;
        var normal = 0f;

        foreach (var (_, mask, worth, _) in ContributionsOfRow(root))
        {
            if ((mask & (1L << Tags.Rares)) != 0L)
                rare += worth;
            else if ((mask & (1L << Tags.Magics)) != 0L)
                magic += worth;
            else
                normal += worth;
        }

        return (rare, magic, normal);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Three decimals and no trailing noise, matching what Wrt writes to the file.
    ///
    /// The same rounding on both sides is what lets a row survive a round trip and still compare
    /// equal to the shipped answer - see Wrt.Row.Nearly, and the prune that depends on it.
    /// </summary>
    public static string Printed(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The same, with room for a deliberate hair's breadth. For effect magnitudes only.
    ///
    /// **Three decimals is right for a weight and wrong for a factor.** A weight is written to the
    /// file at three and compares equal to the shipped answer at three - see Wrt.Row.Nearly, and the
    /// prune that depends on it. A factor carries the same information two places further in: a
    /// percentage tie-broken at 16.01 becomes the factor 1.1601, and three decimals rounds that to
    /// 1.16 - the same text the un-tie-broken 16 produces. Measured, not assumed: both printed
    /// "1.16", which is how the increments went missing from the first translation.
    ///
    /// **Four, and the reason is the reconstruction rather than the format.** A custom format string
    /// on a float prints its shortest round-trip spelling, so a typed 16.01 prints "16.01" at any
    /// width. What does not survive is deriving the percentage back out of a factor: (1.1601f - 1) x
    /// 100 is 16.009998, and 4.01 comes back as 4.009998, which six decimals faithfully shows and
    /// four rounds away. Effect.Amount keeps the typed number so that derivation never happens, and
    /// four decimals is the belt to that braces - past any increment anybody types, short enough to
    /// absorb the noise if something ever does build an effect from a factor.
    /// </summary>
    public static string Precise(float value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
