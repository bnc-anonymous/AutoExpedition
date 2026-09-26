using System;
using System.Collections.Generic;
using ExileCore2.Shared.Nodes;

namespace AutoExpedition;

/// <summary>
/// What a piece of content is worth to the planner.
///
/// Kept out of the planner itself so the planner stays a pure search over numbers, and out of the
/// scan so classification stays a statement about the game rather than about preference.
/// </summary>
internal static class Weighing
{
    /// <summary>
    /// What a remnant's REWARD is worth to the route, in weight.
    ///
    /// The missing half of the objective. Until now a two hundred exalt remnant pulled the chain no
    /// harder than an empty one - the planner scored remnants on being a remnant and on sockets,
    /// and the price was drawn on screen for the player to act on by hand. The only way to make the
    /// route care was "must take above", which is a cliff: nothing at all below the threshold and
    /// everything above it.
    ///
    /// This is the ramp that belongs underneath that cliff. The conversion is the same weight point
    /// rate the reward choice uses, so a player sets one number and both halves of the plugin agree
    /// about what currency is worth.
    ///
    /// Zero when no rate is set, which leaves the objective exactly as it was.
    /// </summary>
    public static float Reward(Target target, AutoExpeditionSettings settings)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return 0f;

        return Money(target.Rewards[0].Value, settings);
    }

    /// <summary>
    /// What a piece of content is worth, before anything about where it sits in the chain.
    ///
    /// Takes the whole settings tree rather than just the weights because two of the answers depend
    /// on a mode that lives elsewhere, and threading a second argument through five call sites to
    /// avoid saying so would only hide it.
    /// </summary>
    /// <summary>
    /// A price in exalts, in the weight the objective works in. Zero when unpriceable.
    ///
    /// The rate says what one weight point is worth in the display unit, so the other way round is
    /// a division - and prices are held in exalts, so they convert to display units first.
    ///
    /// </summary>
    /// <summary>
    /// A price in exalts as weight, through the one exchange rate. Internal so the reroll advisor
    /// prices a sampled reward exactly as the objective prices a real one - two conversions would
    /// eventually disagree, and the comparison between them is the whole decision. See Rolling.
    /// </summary>
    internal static float Money(double exalts, AutoExpeditionSettings settings)
    {
        var rate = Safe.Read(() => settings.Rewards.PointWorth.Value, 0f);

        if (rate <= 0f || exalts <= 0d)
            return 0f;

        var worth = (float)exalts / rate;

        return worth;
    }

    /// <summary>
    /// Every way this remnant could be taken: what the reward is worth, and what that same
    /// combination carries forward.
    ///
    /// **The two are one decision and were being scored as two.** The objective used to add the
    /// price of the best reward to the percentage of the best propagating rune, which assumes you
    /// can have both - and you cannot, because picking a combination picks both at once. A remnant
    /// whose most expensive option carries nothing and whose Opulent option is worth five exalts
    /// was scored as though it were the expensive one AND the Opulent one, which inflated it in
    /// exactly the ordering decision the plan exists to make.
    ///
    /// Which of them wins depends on where the remnant lands in the chain - a carried rune early is
    /// worth the monsters after it and late is worth nothing - so the choice cannot be made here.
    /// It is made inside the search, per candidate position, against the downstream weight that
    /// position actually has. This just lays out the options.
    ///
    /// Null when there is nothing to choose between, which leaves the objective on the plain
    /// weight and nothing is spent working out a maximum over one entry.
    /// </summary>
    /// <param name="locked">
    /// The one combination this remnant is stuck with, or null while it may still be chosen.
    ///
    /// **A rolled remnant is finished.** Liquid Verisium rerolls its runes, its sockets can change
    /// in the process, and what comes out is fixed - it cannot be rolled again and its combination
    /// cannot be changed, even though the window still opens and still looks clickable. Handing the
    /// search every combination for one of those says it can pick the best of them, and it cannot:
    /// the reward is whatever is set, and a plan built on the best of six options is worth less than
    /// it claims when five of them are unavailable.
    /// </param>
    public static (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals,
        string[] Runes,
        (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices(Target target,
        AutoExpeditionSettings settings, string locked = null)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(locked))
        {
            foreach (var reward in target.Rewards)
            {
                if (!Locked(reward, locked))
                    continue;

                return new[]
                {
                    (Money(reward.Value, settings), reward.Carries, reward.Local,
                        reward.Locals ?? [], reward.Carrying ?? [], ScopedEffectsOfReward(reward, settings)),
                };
            }

            // Set to something the price list does not know. Better to leave the options open than
            // to pin the remnant to a reward that cannot be found and value it at nothing.
        }

        var found =
            new (float, float, float, (string, float)[], string[],
                (string, int, float, bool)[])[target.Rewards.Count];

        for (var i = 0; i < target.Rewards.Count; i++)
        {
            var reward = target.Rewards[i];

            found[i] = (Money(reward.Value, settings), reward.Carries, reward.Local,
                reward.Locals ?? [], reward.Carrying ?? [], ScopedEffectsOfReward(reward, settings));
        }

        return found;
    }

    /// <summary>
    /// The stat a row's effect names with "as", or empty where it names none.
    ///
    /// One row can hold several effects - a relic that raises what its monsters drop AND what its
    /// chests do - so the first named stat answers. Two different stats on one row would be one
    /// modifier granting two things, which the grouping cannot express either way: a share is keyed
    /// by the modifier, not by the term.
    ///
    /// **The row's OWN cell, never TableGrammar.EffectsOfRow.** That method falls back to the
    /// translation of a v1 row when there is no effect cell, the translation asks what stat the row
    /// is grouped under, and that question arrives back here - which is an unbounded recursion on
    /// every row that has no effect cell, meaning most of the table.
    /// </summary>
    private static string StatNamedByEffect(string filed)
    {
        var said = Wrt.Of(filed)?.Effect;

        if (string.IsNullOrWhiteSpace(said))
            return "";

        foreach (var effect in TableGrammar.Effects(said, out _) ?? [])
        {
            if (string.IsNullOrWhiteSpace(effect.Stat))
                continue;

            // **Every name is a stat, with no reserved word among them.** There was one meaning
            // "the unclassified pool", and it had to go: a stat name is a stat name, so two rows
            // carrying it stacked with each other - the opposite of what unclassified means. A row
            // sharing no stat with anything says so by naming none.
            return effect.Stat.Trim();
        }

        return "";
    }

    public static float WeightOfTarget(Target target, AutoExpeditionSettings settings)
    {
        // **The table answers first.** A row in either weight reference table file is somebody
        // having decided what this kind is worth - yours if you changed it, the shipped one
        // otherwise - and it stands ahead of the number compiled into the settings. See Wrt.
        // **Every component asks the table, rather than the table answering for the whole target.**
        //
        // This used to short-circuit: one lookup on the target's kind, and if a row existed its
        // weight WAS the answer. That is right only for something whose worth is a single number,
        // and quietly wrong for everything else - the row replaced a calculation instead of feeding
        // it. Measured across the table, fourteen of forty-four weights moved nothing at all:
        //
        //  - kind:Relic ships at 1, so every relic was worth 1 and the five relic rows, the three
        //    wisp rows and all four carry values were unreachable.
        //  - kind:MonsterNormal answered for a monster marker, so the magic blend behind it -
        //    kind:MonsterMagic and setting:MarkerMagic - never ran.
        //  - kind:Strongbox/* answered for a strongbox, so its guarding packs were dropped and a
        //    box guarded by six packs read 5.
        //
        // So the dispatch below runs always, and each leaf reads its own row through Said. A row
        // then means the same thing everywhere: this part is worth that much.
        return WeightOfTargetByKind(target, settings);
    }

    /// <summary>
    /// What one weight is worth, which the reference table alone decides.
    ///
    /// **There is no second store any more.** Every weight used to exist twice - a slider under
    /// Weights in the menu, saved to AutoExpedition_settings.json, and a row in this table - and the
    /// two disagreed freely. For some weights the row won and the slider was decoration; for others
    /// the arithmetic read the slider and a row edited to ten changed nothing at all. Measured
    /// across the table, fourteen of forty-four rows moved no number anywhere.
    ///
    /// So the sliders are gone, and the shipped table carries what they held: every weight's name
    /// and its built-in value, in weight_reference_table_defaults.json, with yours layered over it.
    ///
    /// Nought when no row exists, which means a weight nobody shipped and nobody wrote - not a
    /// weight whose value went unread. See Wrt.
    /// </summary>
    private static float WeightOfRow(string id) => Wrt.Of(id)?.Weight ?? 0f;

    /// <summary>The companion value on a row - what taking it passes on. See WeightOfRow.</summary>
    /// <summary>What the plugin was built believing, before either table file spoke.</summary>
    private static float WeightOfTargetByKind(Target target, AutoExpeditionSettings settings) =>
        target.Kind switch
    {
        // **A remnant is worth what it digs up, and that is its children rolled up.**
        //
        // The flat bonus and the per-socket figure this replaced were removed on the reasoning that
        // "the monsters are priced as waves" - and they are not. Waves reach the score through
        // Planner.Unearths, which feeds the monster total a carried rune MULTIPLIES; the payout is
        // weight * (mult - 1), so the waves are a multiplicand with no base under them. Read through
        // Said, which takes a row's own Weight and ignores its children, a remnant therefore scored
        // its reward and nothing for the forty-odd monsters it sends up. A remnant with nothing
        // useful socketed was worth exactly its reward.
        //
        // Total is Said plus what the children come to, so the waves become content the way a
        // strongbox's guard packs already are - see Boxed, which adds them on the same reasoning.
        // Counting them here does not double count them against the propagation term, because that
        // term pays only the increment: content plus propagation is weight * mult, which is what the
        // game states - the runes in a remnant's sockets multiply the monsters it unearths.
        //
        // The magnitude comes from the remnant row's children and can be corrected there. The
        // shipped recipe - three waves of one rare, three magic and ten normal - carries no
        // measurement behind it; dumps/spawns.csv is the instrument that would settle it.
        TargetKind.Remnant => MathF.Max(0f,
            TableGrammar.Total(Wrt.RowOfKind(TargetKind.Remnant)).Fixed),
        // **The marker, not the monster.** Both of these used to answer with the price of the creature
        // the marker stands for, which is how the two came to be one row: elitemarker.ao WAS
        // kind:MonsterRare as far as the table could tell, so nothing in it could say that the marker
        // spawns a rare - or that it might spawn two, or something else as well. Now the marker is a
        // row of its own, bound to its art, and what it spawns is its children. See TableGrammar.Art.
        TargetKind.Elite => WeightOfMarker(target, Wrt.Id.Kind(TargetKind.Elite, ChestTier.Unknown)),
        TargetKind.Monster => WeightOfMarker(target, Marker),
        TargetKind.Sentry => WeightOfRow(Wrt.RowOfKind(TargetKind.Sentry)),
        TargetKind.Hatch => WeightOfRow(Wrt.RowOfKind(TargetKind.Hatch)),
        TargetKind.Entrance => Entrance(target),
        TargetKind.Relic => MathF.Max(0f, Relic(target).Weight),
        // Its rares are children now, so this is what the row rolls up to - content at the link, as
        // it has always been, rather than waves. See the caged row.
        TargetKind.Caged => MathF.Max(0f, TableGrammar.Total("caged").Fixed),
        TargetKind.Monolith => MathF.Max(0f, WeightOfRow(Wrt.RowOfKind(TargetKind.Monolith))),
        TargetKind.Scenery => WeightOfRow(Wrt.RowOfKind(TargetKind.Scenery)),

        // Whatever the player has decided, or the nominal weight until they do. See Unknowns.
        TargetKind.Unknown => Unread(target),
        TargetKind.Strongbox => WeightOfStrongbox(target),
        TargetKind.Chest => Chest(target, settings),
        _ => Normal(),
    };




    public static float Waves(Target target)
    {
        var (rare, magic, normal) = TierSplitOfTarget(target);

        return MathF.Max(0f, rare + magic + normal);
    }

    /// <summary>
    /// What a weight row's monsters wear when nobody has said otherwise.
    ///
    /// **Only the magic monster weight needs one.** The other two tiers are priced by weights that
    /// name a kind - the rare marker and the monstermarker - so the table derives their tags from
    /// the kind like anything else. Magic monsters have no marker of their own, so without this the
    /// cell would sit blank while the monsters it prices quietly wore three tags, which is the
    /// hidden state this column exists to end.
    ///
    /// Empty for every other shipped weight, because a number that prices a thing has no tags of
    /// its own to derive - and an empty derived answer is what lets anything typed be kept.
    /// </summary>
    public static string DerivedTagsFor(string filed) =>
        string.Equals(filed, Wrt.Id.MagicMonsters, StringComparison.Ordinal)
            ? string.Join(", ", Tags.Known[Tags.Modifiables], Tags.Known[Tags.Monsters],
                Tags.Known[Tags.Magics])
            : "";


    /// <summary>The row a monstermarker answers to where its art has not been read. See WeightOfMarker.</summary>
    private const string Marker = "monstermarker";

    /// <summary>
    /// Which row answers for this object, or null where nothing does.
    ///
    /// **One answer to "which row is this?", where there were four.** The weight came from a switch
    /// over the kind; the tags came from Tags.Of, which asks for a row NAMED after the kind; the tier
    /// split came from UnearthedPartsOfTarget, which asks Wrt.Kinded; and a monstermarker's split
    /// came from a fourth that bound on the art. Four resolutions of one question, agreeing by luck
    /// rather than by construction - an elite marker's whole weight landed under the marker's own
    /// tags and was right only because those tags happen to equal the row's it spawns, and a caged
    /// encounter's rares reached no tag at all. The fourth is gone; this is where the other three
    /// meet. An elite marker's whole weight lands under the marker's own tags today and is
    /// right only because those tags happen to equal the row's it spawns.
    ///
    /// The order is the one the weighing already used, most specific first: what the object itself
    /// has been filed as, then what the game says it looks like, then the row that answers for its
    /// whole kind. A caller wanting the arithmetic still asks WeightOfTargetByKind; this says only where the
    /// answer comes from, so the scoring, the tags and the composition can be read off one row.
    ///
    /// Null for a relic, whose row is chosen by the modifiers it carries rather than by any of
    /// these - see Relic, which resolves its own.
    /// </summary>
    public static string RowOfTarget(Target target)
    {
        if (target == null)
            return null;

        // Filed under its own key and priced there: an entrance, an unclassified object, and a
        // strongbox whose base nothing names. See Unread.
        var own = Safe.Read(() => Unknowns.Key(target), "");
        var mine = own.Length > 0 ? Wrt.Id.Found(own) : null;

        switch (target.Kind)
        {
            case TargetKind.Relic:
                return null;

            case TargetKind.Elite:
                return RowBoundToTarget(target) ?? Wrt.Id.Kind(TargetKind.Elite, ChestTier.Unknown);

            case TargetKind.Monster:
                return RowBoundToTarget(target) ?? Marker;

            case TargetKind.Chest:
                // The tier stands in only until the art or the icon has streamed in; an unmatched
                // one is read as Uncommon rather than Common. See Chest.
                return RowBoundToTarget(target) ??
                       Wrt.RowOfTier(target.Tier == ChestTier.Unknown ? ChestTier.Uncommon : target.Tier);

            case TargetKind.Strongbox:
                return RowOfStrongbox(target) ?? mine;

            case TargetKind.Caged:
                return "caged";

            case TargetKind.Entrance:
            case TargetKind.Unknown:
                return mine;

            default:
                // Remnant, Sentry, Hatch, Monolith, Scenery - one row apiece, found by the kind they
                // record. A barrel reaches the same default arm the weighing does, which prices it
                // as a normal monster.
                return Wrt.RowOfKind(target.Kind) ?? Wrt.Id.Kind(TargetKind.Monster, ChestTier.Unknown);
        }
    }

    /// <summary>
    /// Which row this object is bound to by what the game says it looks like, or null for none.
    ///
    /// **The art, then the icon.** The art is the more specific of the two - three chest signposts
    /// wear three models and share one icon - so it answers first where it has loaded. The icon is
    /// what a Grand site has instead: every reward chest there wears one model and the minimap is
    /// where the dull currency chest and the bright one differ.
    /// </summary>
    private static string RowBoundToTarget(Target target)
    {
        var art = Safe.Read(() => target.Art, "") ?? "";
        var found = art.Length > 0 ? TableGrammar.Matched(art, TableGrammar.Art) : null;

        if (found != null)
            return found;

        var icon = Safe.Read(() => target.Icon, "") ?? "";

        return icon.Length > 0 ? TableGrammar.Matched(icon, TableGrammar.Icon) : null;
    }

    /// <summary>
    /// What the marker in front of us is worth, rolled up through whatever it spawns.
    ///
    /// **Matched on the art, so the table decides rather than a switch.** Scan.Kind still classifies a
    /// marker into a TargetKind - which drives drawing, reach, must-takes and half the plugin besides -
    /// but what the thing is WORTH is now a row bound to the art file, and that row says what it
    /// spawns. An elitemarker with a second child, or a variant that turns out to spawn two rares, is
    /// a cell rather than a code change.
    ///
    /// Falls back to the row the kind used to answer with, for a marker seen before its art has
    /// streamed in - which happens constantly on a Grand site, where the chests are walked past rather
    /// than stood in and the doodad reads as an empty string from across the map. Not a guess: it is
    /// the same answer as before, and the art arriving replaces it.
    /// </summary>
    private static float WeightOfMarker(Target target, string fallback)
    {
        var rolled = TableGrammar.Total(RowBoundToTarget(target) ?? fallback);

        return MathF.Max(0f, rolled.Fixed);
    }

    /// <summary>
    /// What this object is made of, as tagged amounts: one entry per row its composition names.
    ///
    /// **The composition, asked once, for every kind.** One caller asked it for a monstermarker and
    /// UnearthedPartsOfTarget asked it for a remnant, and nothing asked it for anything else - so a
    /// caged encounter's three rares, which its row states as plainly as a remnant states its waves,
    /// reached no tag at all and a rare-scoped modifier missed them. The shares come from the same
    /// place both of those used, so this is one caller where there were two rules and a gap.
    ///
    /// Each entry carries the tag mask of the ROW THAT EARNED IT rather than of the object it came
    /// out of, which is what makes a tier-scoped effect reach the rares inside something that is not
    /// itself a rare. Tags do not inherit downwards - see Tags - so a rare in a cage is a rare
    /// monster and nothing else.
    ///
    /// The amounts sum to what the row rolls up to MINUS its own weight, so a caller holding the
    /// rolled figure can recover the object's own share by subtraction rather than by asking a
    /// second time. That is the arithmetic Planner.ContributionsOfTarget needs, and the reason
    /// nothing here returns the whole.
    ///
    /// Null where an object is made of nothing, which is most of a dig site - a chest, a marker with
    /// one outcome, anything filed by its own key - so the scoring pays nothing for the possibility.
    /// </summary>
    public static (long Mask, float Worth, float Many)[] PartsOfTarget(Target target)
    {
        if (target == null)
            return null;

        // **A strongbox is the exception, and it is one because the game states its parts rather
        // than the table.** Its guarding packs are printed on the box as modifiers, so its
        // composition is not a row anything can walk - see Waving, which buckets them back out of
        // the counts those modifiers carry.
        if (target.Kind == TargetKind.Strongbox)
            return UnearthedPartsOfTarget(target);

        var row = RowOfTarget(target);

        if (row == null)
            return null;

        var spread = TableGrammar.ContributionsOfRow(row);

        if (spread.Length == 0)
            return null;

        var made = new (long, float, float)[spread.Length];

        for (var i = 0; i < spread.Length; i++)
            made[i] = (spread[i].Mask, spread[i].Worth, spread[i].Many);

        return made;
    }

    /// <summary>
    /// What a target's waves send up, as whole monsters rather than as tag shares.
    ///
    /// **The same composition Tiers reads, unbucketed.** The scoring wants one entry per tag because the
    /// per-tag accumulators keep them apart; the grouped payout wants them together, since whether two
    /// modifiers multiply depends on the tags ONE monster carries. A wave rare is a monster and a rare
    /// monster and modifiable, all three, and an effect scoped to any of them reaches the same
    /// creature.
    ///
    /// Read off Spread so this and Tiers cannot describe different monsters - they were two
    /// hand-written copies of one fact, which is how a blast circle came to disagree with the score it
    /// sat inside.
    /// </summary>
    /// <summary>
    /// How many of a row's things a lump of weight is, which is the weight over one of them.
    ///
    /// For the splits built from tier weights rather than walked out of children, where the count
    /// never existed as a number and has to be recovered. See PartsOfTarget.
    /// </summary>
    private static float Apiece(string id, float worth)
    {
        var one = MathF.Max(0f, TableGrammar.Total(id).Fixed);

        return one > 0.0001f ? worth / one : 0f;
    }

    public static (long Mask, float Worth, float Many)[] UnearthedPartsOfTarget(Target target)
    {
        if (target == null)
            return null;

        // A strongbox's packs depend on what the game printed on the box, so its composition is not a
        // row the table can walk on its own. Bucketed back out of Tiers, which has the counts.
        if (target.Kind == TargetKind.Strongbox)
        {
            var (rare, magic, normal) = TierSplitOfTarget(target);

            if (rare + magic + normal <= 0f)
                return null;

            // **How many, worked back from what they are worth.** This split is built from tier
            // weights rather than walked out of a row's children, so the count is not to hand -
            // but the weights ARE count times the tier's own worth, so dividing returns it. A
            // tier priced at nothing has no countable monsters and says nought rather than
            // dividing by it.
            var rareRow = Wrt.Id.Kind(TargetKind.Elite, ChestTier.Unknown);
            var normalRow = Wrt.Id.Kind(TargetKind.Monster, ChestTier.Unknown);

            return
            [
                (TableGrammar.TagMaskOfRow(rareRow), rare, Apiece(rareRow, rare)),
                (TableGrammar.TagMaskOfRow(Wrt.Id.MagicMonsters), magic,
                    Apiece(Wrt.Id.MagicMonsters, magic)),
                (TableGrammar.TagMaskOfRow(normalRow), normal, Apiece(normalRow, normal)),
            ];
        }

        if (target.Kind != TargetKind.Remnant)
            return null;

        var spread = TableGrammar.ContributionsOfRow(Wrt.RowOfKind(TargetKind.Remnant));

        if (spread.Length == 0)
            return null;

        var made = new (long, float, float)[spread.Length];

        for (var i = 0; i < spread.Length; i++)
            made[i] = (spread[i].Mask, spread[i].Worth, spread[i].Many);

        return made;
    }

    /// <summary>
    /// How an effect combines with the others, as the key the objective groups it by.
    ///
    /// **The name says which stat.** See TableGrammar.Effect's "as" clause for the
    /// grammar. This turns an answer into a key:
    ///
    /// - nothing at all -> one shared key, so everything unclassified adds together;
    /// - "+increased_number_of_rares" -> that name, so every row carrying it adds with the rest;
    /// - "*rares_are_duplicated" -> the EFFECT'S OWN id, so it multiplies with everything, its own
    ///   name included. The name is then a label for the reader rather than a grouping, which is
    ///   what "multiplies with itself" means: two different mods that both duplicate compound.
    ///
    /// A name with no sigil is read as a plus, because that is what a bare name meant before the
    /// sigils existed and because adding is the more conservative of the two.
    ///
    /// The shared key is what makes an unclassified site behave exactly as it did before this
    /// existed - everything in one pool, added. So filling the column in is an improvement to make
    /// rather than a migration to complete.
    /// </summary>
    public static string GroupKeyOfEffect(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "";

        var rune = Wrt.Of(Wrt.Id.Rune(id));

        // **The effect says it now, where a reserved word in another column used to.** A rune that
        // scales other runes is one whose effect aims at the rune tag - "rune.propagation *= +50%" -
        // which is the fact itself rather than a flag beside it. Combines was cleared off rune rows
        // when their magnitudes moved into effects; keying on it here would have silently switched
        // Power off. See Wrt.Runed and Lift.
        if (Lift(id) > 0f)
            return Empowering;

        // **The effect's own "as" clause answers first, because it is the cell anybody can see.**
        //
        // The same fact was written in two places. TableGrammar's Effect carries a Stat - the
        // clause in "monster.weight *= +40% as item_rarity_monsters" - and its documentation says
        // outright that it REPLACES the sigil that Combines holds. Only Combines was read, and
        // Combines has no column: it is a v1 cell surfaced on a hover, marked to die at stage five.
        // So the grouping was decided by a cell nobody can edit while the cell they can edit was
        // ignored, and two shipped rows naming their stat plainly - ItemRarityMonster and PackSize
        // - fell into the pool where everything unclassified ADDS. Measured on a live chain: +40%
        // and +20% and a second pair of each came to 1 + 1.2 = x2.2, where two stats grouped are
        // (1 + 0.8)(1 + 0.4) = x2.52.
        //
        // Combines still answers where no effect names a stat, which is most of the table. No row
        // in either file carries both, so this reorders nothing that exists today - it decides
        // which wins when one eventually does, and the answer has to be the visible one.
        var stat = StatNamedByEffect(rune != null ? Wrt.Id.Rune(id) : Wrt.Id.Found(id));

        if (stat.Length > 0)
            return "+" + stat;

        // **No "as" clause means its own factor, which is what the grammar says it means.**
        //
        // TableGrammar.Effect: "Stat empty means the effect is its own factor. That is the only
        // thing the stat name does - two rows naming the same stat add their contributions, and a
        // row naming no stat multiplies on its own." This returned the empty string instead, which
        // is the unclassified POOL - so the one spelling the grammar gives for "do not pool me"
        // was the spelling that pooled a row.
        //
        // There is one spelling now and it means what the grammar says: no clause is its own
        // factor. A reserved word for "the pool" was tried and removed - it was a stat name like
        // any other, so two rows carrying it stacked, which is the opposite of unclassified.
        //
        // Runes take the same answer for the same reason, and always did: Bond, Tidal, Sky and
        // Oath grant different things, and different things are different factors of one product -
        // two of them are (1+a)(1+b), not 1+a+b.
        return "*" + id;
    }

    /// <summary>
    /// How much this scales other effects by, or nought where it scales nothing.
    ///
    /// **One reading, from the row's own effect.** The rate used to sit in the propagation cell as a
    /// bare number with no target, which is why that cell needed a special parse - there was nothing
    /// honest to write in it, since an effect that scales effects points at no creature. It points at
    /// the rune tag now and the number is its share, read the same way every other magnitude is.
    /// </summary>
    public static float Lift(string id)
    {
        // **The STORED effect, never the translated one.** TableGrammar.Of falls back to translating the old
        // three columns, and that translation asks Combining which group a row is in - so reading it
        // here would be Combining calling Lift calling Of calling Translated calling Combining, which
        // is a stack overflow on any rune row that has no effect written yet. A row still speaking v1
        // is recognised by the reserved word below, which is what it has.
        var said = Wrt.Of(Wrt.Id.Rune(id))?.Effect;

        if (said == null)
            return 0f;

        foreach (var effect in TableGrammar.Effects(said, out _))
        {
            if (string.Equals(effect.Target, Tags.Known[Tags.Runes],
                    StringComparison.OrdinalIgnoreCase))
                return effect.Share * 100f;
        }

        return 0f;
    }

    /// <summary>
    /// What to type in Multiplicative behaviour for a rune that scales the others. See GroupKeyOfEffect.
    /// </summary>
    public const string Empowering = "empower";


    /// <summary>
    /// The label an empowering row's percentage is written behind, so the cell says what it does.
    ///
    /// **A bare number in a column of tag=percent entries explains nothing.** It parsed and scored
    /// correctly and told a reader looking at the table nothing at all about why this row was
    /// different from the twenty above it. The label carries no meaning to the scoring - LiftWrittenInScope takes
    /// the number whatever precedes it - so it is free to be chosen for the person reading it.
    ///
    /// **The word "untagged" is in it on purpose, and is the whole point of the name.** It sits in a
    /// column where every other entry is a real tag, so a reader will take it for one, go looking for
    /// it among the tags, and find that nothing anywhere carries it. Saying so in the label itself is
    /// cheaper than letting them find out - a first draft read scales_other_runes, which is accurate
    /// about the effect and silent about the thing that actually confuses.
    /// </summary>
    public const string Scaling = "special_untagged_rune_scaling";

    /// <summary>
    /// The percentage on an empowering row, written bare or with any label in front of it.
    ///
    /// Deliberately forgiving about the label and deliberately strict about the number: the label
    /// carries no meaning here - there is nothing for an empowering rate to point at - so "30",
    /// "rune=30" and "runes: 30" all mean the same thing, and anything without a number in it means
    /// nothing at all. Summed over commas, like every other entry on the row.
    /// </summary>
    /// <summary>The same reading, for the table's cell to validate against. See LiftWrittenInScope.</summary>
    public static float LiftOfChoice(string scope) => LiftWrittenInScope(scope);

    /// <summary>
    /// Whether an empowering row's cell holds a number at all, and what it is.
    ///
    /// **Nought is a value here, not an absence.** LiftWrittenInScope only sums figures above nought, which is
    /// right for the scoring - nothing to scale by - and wrong for the cell, which refused to store a
    /// nought and so left no way to turn an empowering rune off. Blanking is no answer either: an
    /// empty propagation cell has never been storable on any row in that column.
    ///
    /// So the cell asks this instead, and a nought parses, stores, and leaves the factor at one.
    /// </summary>
    public static bool LiftsOfTarget(string scope, out float percent)
    {
        percent = 0f;

        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var any = false;

        foreach (var piece in scope.Split(','))
        {
            var part = piece.Trim();
            var at = part.IndexOfAny(new[] { '=', ':' });

            if (at >= 0)
                part = part[(at + 1)..].Trim();

            if (!float.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var read) || read < 0f)
                continue;

            percent += read;
            any = true;
        }

        return any;
    }

    private static float LiftWrittenInScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return 0f;

        var total = 0f;

        foreach (var piece in scope.Split(','))
        {
            var part = piece.Trim();
            var at = part.IndexOfAny(new[] { '=', ':' });

            if (at >= 0)
                part = part[(at + 1)..].Trim();

            if (float.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent) &&
                percent > 0f)
                total += percent;
        }

        return total;
    }

    /// <summary>
    /// The three tiers a target's waves are made of.
    ///
    /// **The one description of that composition**, so the total and the per-tier split cannot
    /// disagree - Waves is this added up. Two copies of a rule is how the blast circles came to
    /// contradict the score they sat inside.
    /// </summary>
    /// <summary>
    /// The three tiers a target's waves are made of, read from the table's composition rows.
    ///
    /// **This was the switch the reference table existed to replace.** It decided that a remnant is
    /// three waves of one rare, three magics and ten normals, and that a strongbox's guarding packs
    /// hold so many monsters of each rarity - facts about the game, in code, where nobody could see or
    /// change them. They are now child edges: kind:Remnant has remnant/wave x3, and remnant/wave has
    /// the three tiers with their counts. See TableGrammar.Spread.
    ///
    /// What is left here is the part that genuinely belongs in code: which ROW a live entity answers
    /// to, and how many packs the game says are guarding this particular box. The first goes when
    /// matches: lands; the second is a fact about the instance and stays.
    /// </summary>
    private static (float Rare, float Magic, float Normal) TierSplitOfTarget(Target target)
    {
        if (target == null)
            return default;

        // **What its row says it is made of, plus what its own modifiers bring.** Two sentences, and
        // neither mentions a kind - which is the whole change. A strongbox used to have a branch here
        // saying "your parts come off your modifiers", so that rule was true of exactly one kind of
        // thing and had to be written again for the next. It is the rule now.
        var own = target.Kind == TargetKind.Remnant
            ? TableGrammar.TierSplitOfRow(Wrt.RowOfKind(TargetKind.Remnant))
            : default;

        var (rare, magic, normal) = TierSplitFromModifiers(target);

        return (own.Rare + rare, own.Magic + magic, own.Normal + normal);
    }

    /// <summary>
    /// What an object's own modifiers bring it, whatever the object is.
    ///
    /// **A modifier the game states on an entity is one of that entity's children.** That is what the
    /// table has said since the modifier rows were written, and what the code said only about
    /// strongboxes: Weighing.Guarding walked a box's modifiers and matched them to rows, and nothing
    /// else did, so the general rule was implemented once per kind. A relic's modifiers still reach the
    /// scoring through a chain of hand-written string matchers in Relic, which is the same rule written
    /// a second time and worse - its own comment records a matcher that never fired at all.
    ///
    /// Nothing moves by making it general. Five modifier rows carry a mod binding today and all five
    /// are a strongbox's guards; everything else resolves to no row and brings nothing. Giving the
    /// relic upsides their bindings is the change that moves scores, and it is a change to the table
    /// rather than to this.
    /// </summary>
    private static (float Rare, float Magic, float Normal) TierSplitFromModifiers(Target target)
    {
        var valued = Safe.Read(() => target.Valued, null);

        if (valued is not { Length: > 0 })
            return PacksAssumedOfStrongbox(target);

        var rare = 0f;
        var magic = 0f;
        var normal = 0f;

        foreach (var (name, values, _) in valued)
        {
            var id = TableGrammar.RowForModifier(name);

            if (id == null)
                continue;

            var (r, m, n) = TableGrammar.Brought(id, values, out _);

            rare += r;
            magic += m;
            normal += n;
        }

        return (rare, magic, normal);
    }

    /// <summary>
    /// What to say about an object whose modifiers have not been read yet.
    ///
    /// **Not read yet is a state, not a guess, and only a strongbox has one.** A box has at least one
    /// guarding pack, and pricing it at nothing until its chest streams in would have the planner walk
    /// past the box on the very sweep that found it - so it is floored at a pack and Dump.Guards names
    /// the boxes being priced that way. That is the difference between a stated floor and the kind of
    /// fallback that turns a broken read into a plausible wrong answer.
    ///
    /// Everything else brings nothing, which is the truth: a marker with no modifiers has no
    /// modifiers.
    /// </summary>
    private static (float Rare, float Magic, float Normal) PacksAssumedOfStrongbox(Target target)
    {
        if (target.Kind != TargetKind.Strongbox || !target.Explodes)
            return default;

        var (_, _, one) = TableGrammar.TierSplitOfRow("pack/normal");

        return (0f, 0f, one);
    }



    /// <summary>
    /// What one monster of each rarity is worth, asked of the table exactly as a monster standing
    /// in the site would be. See WeightOfRow.
    ///
    /// The three ids are the ones Weighing.Of already looks a live monster up under, which is the
    /// point: the same monster must not be worth one number on its own and another inside a wave.
    /// </summary>


    private static float Normal() =>
        WeightOfRow(Wrt.Id.Kind(TargetKind.Monster, ChestTier.Unknown));

    /// <summary>
    /// The percentage uplift this remnant would apply to monsters unearthed after it.
    ///
    /// Kept apart from <see cref="Of"/> because it is the one term in the plan that is not a
    /// property of the target. The game states what propagation does - "added to all Monsters
    /// unearthed after this Remnant" - so what it is worth is a share of what the REST of the chain
    /// kills, and only the planner knows what that is. It multiplies this by the weight of the
    /// monsters downstream; the percentage on its own is meaningless.
    ///
    /// The best rune rather than the fixed one. Those are different questions, and the plugin used
    /// to answer the wrong one: the rune already in the ground only carries forward if it happens
    /// to sit in a slot that carries forward, and when it does not, taking the remnant passes on
    /// whatever the chosen combination puts in the slot that does.
    ///
    /// Zero for a remnant that has not been read yet, which is the safe direction: it makes a
    /// remnant look like an ordinary one rather than inventing a reason to route to it.
    /// </summary>
    /// <summary>
    /// What this relic is worth, and what it passes on.
    ///
    /// **Named one at a time, because relics share a state and not a purpose.** Every one carries
    /// expedition_relic - the goblin totem at 3, the Prairie wisp traps at 7, 8 and 9 - and that is
    /// the game saying "this is a relic" and nothing more. What each DOES is printed on the object
    /// and nowhere in its components: "15% increased rarity of items dropped by monsters" against
    /// "contains an Azmeri spirit". One modifies everything unearthed after it; the other is a thing
    /// you pick up where it stands. No reading of the entity separates them.
    ///
    /// Matched on the metadata, which is what the dump prints and what a tileset names its objects.
    /// One nobody has met falls through to the unknown weights, which pass nothing on - erring
    /// towards collecting it in passing rather than bending a chain around an assumed percentage.
    /// </summary>
    /// <summary>
    /// What a buried strongbox's LOOT is worth, from its type. The packs guarding it are waves.
    ///
    /// Type first and rarity second, because the type is the loot table: a Researcher's drops
    /// currency and the rest drop gear nobody crossed the site for. A unique of a dull type is more
    /// of a dull type, which is worth something and not worth the currency weight.
    /// </summary>
    private static float Strongbox(Target target)
    {
        // Standing in a dig site is not the same as being IN it. A strongbox no blast can touch is
        // loot you walk to like any other, and a chain planned around it is a chain spending an
        // explosive on nothing. See Target.Explodes.
        if (!target.Explodes)
            return 0f;

        var id = RowOfStrongbox(target);

        // **A base nobody has written a row for is not a generic strongbox, it is an unread one.**
        //
        // This used to hand back a strongbox/unknown row, and that row did the one thing this plugin
        // tries not to do: gave a confident-looking five to something nobody had looked at, gave the
        // same five to every such base, and said nothing about any of it. Worse than the relic case
        // it was copied from, because a strongbox is never filed - Catalogue registers unknowns and
        // relics - so there was no red line on the ground, no row in the table, no line in the dump,
        // and no way to price it even after noticing. Three bases reached that state at once:
        // Armourer's, Ornate and Researcher's uniques, one of them in forty site files.
        //
        // Filed like anything else instead, so it gets a row, a weight you can set, and a red label
        // on the ground saying so. See Unknowns and Weighing.Catalogue.
        return id == null
            ? PricedRowOfTarget(target, Unknowns.Register(target))
            : WeightOfRow(id);
    }

    /// <summary>
    /// Which row in the reference table answers for this object.
    ///
    /// **One rule, because two copies of it is a cell you can edit and a cell that is read.** The
    /// table wrote a size onto the row it shows for a strongbox and Extents read one off the
    /// discovered row for the same object, so typing a size in stored it somewhere nothing looked
    /// and the cell refilled itself from the old answer a frame later. It reads as a column that
    /// refuses to be edited, which is what it was.
    ///
    /// A thing the plugin recognises answers for its kind; a strongbox for its base; anything else
    /// for itself, on the discovered list. See Catalogue's site rows and Extents.Of.
    /// </summary>
    public static string RowIdOfTarget(Target target)
    {
        if (target == null)
            return "";

        if (target.Kind == TargetKind.Unknown)
            return Wrt.Id.Found(Safe.Read(() => Unknowns.Key(target), ""));

        var box = target.Kind == TargetKind.Strongbox && target.Tier == ChestTier.Unknown
            ? RowOfStrongbox(target)
            : null;

        return box ?? Wrt.Id.Kind(target.Kind, target.Tier);
    }

    /// <summary>
    /// Which row a strongbox answers to: its own, one per base.
    ///
    /// **Three rows priced eleven bases, and two of the three were "everything else".** A Researcher's
    /// had a row because somebody had noticed it was worth more; a Unique one had a row because the
    /// rarity was easy to test; every other base - Blacksmith's, Armourer's, Jeweller's, Ornate,
    /// Large, Cartographer's - shared one number called StrongboxOther, which is an average of things
    /// nobody had compared and which could not be disagreed with per base because there was only one
    /// of it. The same fault the entrances had, and fixed the same way: a row each. See Entrance.
    ///
    /// Matched on the held chest's metadata rather than on rarity, which is the right binding but was
    /// justified with a wrong reason worth correcting here. It said uniqueness tracks the base - "the
    /// four Unique boxes seen are the four *High bases ... so a rule about rarity was a rule about
    /// the base wearing a disguise". The first half is true and the conclusion does not follow, and
    /// the data now says so: of eleven *High sightings, three are Unique and eight are White or Magic.
    /// **A High base is not a unique one.** ArmourerStrongboxHigh has only ever been seen White.
    ///
    /// What the counts do support, weakly, is the other direction: nought of ninety-five
    /// *StrongboxExpedition sightings came up Unique against three of eleven High ones, so High may be
    /// the variant that CAN roll Unique, or may simply be a higher-level base. This cannot tell them
    /// apart.
    ///
    /// **So rarity is a dimension nothing reads.** Nothing in this file mentions Target.Rarity, and
    /// every binding is on the base, so a Unique strongbox and a White one of the same base are
    /// priced identically and no row can say otherwise. Left as it is deliberately rather than
    /// guessed at - see NOTES, which carries the table this was measured from.
    ///
    /// The chest, not the mound: a strongbox is two entities and only one of them says what it is.
    /// See Target.Holds and Target.Held.
    ///
    /// **Null where no row names this base**, rather than a catch-all row. See Strongbox for why
    /// that row was worse than nothing.
    /// </summary>
    internal static string RowOfStrongbox(Target target)
    {
        var of = target?.Holds is { Length: > 0 } held ? held : target?.Meta;

        if (of != null)
        {
            var id = TableGrammar.Matched(of);

            if (id != null)
                return id;
        }

        return null;
    }
    /// <summary>
    /// What a strongbox is worth all in: the box, plus the monsters guarding it.
    ///
    /// **Waves alone did not move the score, and that is what waves are for.** A wave is the basis
    /// propagation multiplies - monster weight unearthed after a carried rune - so on a site with no
    /// rune to carry, a box guarded by twelve packs and one guarded by six scored the same. The
    /// monsters are content in their own right as well: you kill them and they drop things, whether
    /// or not anything is buffing them.
    ///
    /// Counted in both places on purpose, exactly as a remnant's are. Its sockets pay once through
    /// PerSocket, which is content, and again through Waves, which is what a rune taken earlier
    /// multiplies. The same number in two terms because they answer two questions.
    /// </summary>
    private static float WeightOfStrongbox(Target target) =>
        Strongbox(target) + Waves(target);

    /// <summary>
    /// What a way in is worth, which depends on what is on the other side of it.
    ///
    /// A sub-area cap opens onto a room; a Karui gate opens onto one chest. The same action, two
    /// quite different prices, and the metadata is what tells them apart.
    /// </summary>
    /// <summary>
    /// What a sub-area entrance is worth - one number, and that is the honest answer.
    ///
    /// **This was three named entrances and a fallback, and all three were unreachable.** It matched
    /// "KaruiGate", "BossCave" and "EncasedShrine" in the metadata, and every entrance on record
    /// carries the same path - Metadata/Terrain/Gallows/Leagues/Expedition/Objects/
    /// ExpeditionSubareaEntrance, eighteen sightings, no exceptions. Those three words appear nowhere
    /// in the game's data. So every entrance fell through to the flat prior, the doc here claimed the
    /// opposite, and an edit to the boss cave's weight in a live custom file changed nothing at all.
    ///
    /// **A row per art, and deliberately not one row for all of them.** There was briefly a single
    /// "Sub-area entrance" bound to all fifteen observed arts, on the reasoning that the art is the
    /// only thing that differs between them - one metadata path, one icon, no mods, no label, no
    /// render name, and at the keyboard only the model changes.
    ///
    /// That confuses "we cannot tell them apart by looking" with "they are worth the same". What lies
    /// BEHIND a cap is the thing being priced, and a lair is not a vein; folding them does not answer
    /// that question, it makes it unaskable. Worse, a generalised row silently swallows an art nobody
    /// has met yet - a new tileset's cap would be priced at the average of things nobody compared,
    /// with nothing on the ground saying so.
    ///
    /// So each art gets its own row through the ordinary discovery path, and a cap the plugin has not
    /// met files itself and asks. If they do all turn out to be worth the same, that is fifteen rows
    /// agreeing - which is a finding, where one row is an assumption.
    /// </summary>
    private static float Entrance(Target target) => Unread(target);

    /// <summary>
    /// What an object nobody has taught us is worth, filing it as we go.
    ///
    /// **Filed here rather than only where it is first classified, so Forget means something.** The
    /// scan classifies a target once and caches it for the area, so a row dropped from the table was
    /// never re-added however many times you walked past the thing - Forget looked like a permanent
    /// deletion of an object that was still standing there. Registered on every weighing instead,
    /// which is what the unrecognised relics already did: Forget then resets a row to its default
    /// rather than hiding an object from the plugin, and the thing you are looking at always has a
    /// row to edit.
    /// </summary>
    private static float Unread(Target target)
    {
        return PricedRowOfTarget(target, Unknowns.Register(target));
    }

    /// <summary>
    /// Files anything unrecognised, without asking what it is worth.
    ///
    /// **Filing was a side effect of weighing, and weighing only happens when a solve builds an
    /// environment.** So an object nobody had taught the plugin about stayed invisible - no row in
    /// the settings, no red label on the ground, no red line - until the key was pressed, which is
    /// exactly backwards: the point of the list is to notice things while walking past them.
    ///
    /// Called on the scan's own rhythm instead. It runs the same paths the weighing does, so a relic
    /// the code recognises is still never filed, and nothing here decides anything about value.
    /// </summary>
    public static void Catalogue(Target target, AutoExpeditionSettings settings)
    {
        if (target == null)
            return;

        if (target.Kind == TargetKind.Unknown)
        {
            Unknowns.Register(target);

            return;
        }

        // Relic files itself when it falls off the end of the recognised list, which is the only
        // case that should produce a row.
        if (target.Kind == TargetKind.Relic)
            Relic(target);

        // A strongbox base with no row is the same case and was missing from it entirely, so an
        // unpriced base was never filed, never drew the red label, and never appeared in the table
        // to be priced. See Strongbox.
        if (target.Kind == TargetKind.Strongbox && RowOfStrongbox(target) == null)
            Unknowns.Register(target);
    }

    /// <summary>
    /// What an unpriced object is worth: its own row, plus a row for each thing it grants.
    ///
    /// **A relic is not one decision, it is several.** One can grant forty per cent rarity in
    /// excavated chests, twenty per cent increased quantity and runic monsters duplicated all at
    /// once, and the mixes multiply - five upsides make thirty one possible relics, each wanting a
    /// weight set by hand, and pricing one teaches you nothing about the next.
    ///
    /// So the upside is what carries a value and the object is what carries the rest. Price "runic
    /// monsters duplicated" once and every relic granting it is priced, whatever else it grants.
    ///
    /// Only the STACKING effects contribute here. One that pays once however many are taken is
    /// booked separately against its own name, or a chain collecting four would be paid four times
    /// for a thing that happens once. See Unique.
    /// </summary>
    /// <remarks>
    /// **Weight alone now.** This used to hand back a flat carry as well, gathered from the rows'
    /// Carries cells and zeroed wherever a scope existed so the same magnitude was not paid twice.
    /// There are no carries and no scopes: what an object passes on is its effect, read through
    /// ScopedEffectsOfTarget, and this is left with the one question it was always really asking -
    /// what is the thing worth to take.
    /// </remarks>
    private static float PricedRowOfTarget(Target target, string key)
    {
        var weight = Unknowns.Of(key);

        foreach (var effect in Unknowns.Effects(target))
            weight += Unknowns.Of(effect);

        return weight;
    }

    /// <summary>
    /// What the runes of one combination reach, summed per tag.
    ///
    /// **Resolved here, once per environment, rather than in the search.** A rune's scope is a
    /// string in the settings and turning it into tags means parsing it; doing that where a chain is
    /// scored would be parsing the same handful of strings tens of thousands of times a solve. A
    /// combination's runes do not change while a search runs, so its reach does not either.
    ///
    /// Only the scoped ones. A rune with no scope reaches the monsters the chain unearths and is
    /// already in reward.Carries and reward.Local - see Propagation.Carried, which leaves the
    /// scoped ones out so that each rune is paid exactly once.
    /// </summary>
    /// <summary>
    /// What each of those combinations is called, in the same order.
    ///
    /// **The objective chooses one and nothing could say which.** Choices is a tuple of numbers, so
    /// the combination the plan was scored with existed only as an index into an array nobody kept -
    /// which left the dump inferring it from the rune counts and the overlay re-deciding it from
    /// scratch. Both worked most of the time, and "most of the time" is not a basis for auditing a
    /// choice worth tens of exalts.
    ///
    /// Walked exactly as Choices walks it, including the locked case, so the two arrays cannot fall
    /// out of step. See Planner.Picked.
    /// </summary>
    /// <summary>
    /// Whether this reward is the one a remnant has been pinned to.
    ///
    /// Matched on the recipe, which is what identifies a combination - see Valuation.Reward. The
    /// name is accepted only where the game states no id, since two combinations can share one.
    /// </summary>
    private static bool Locked(Reward reward, string locked) =>
        reward.Recipe.Length > 0
            ? string.Equals(reward.Recipe, locked, StringComparison.Ordinal)
            : string.Equals(reward.Name, locked, StringComparison.OrdinalIgnoreCase);

    public static string[] Names(Target target, AutoExpeditionSettings settings, string locked = null)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(locked))
        {
            foreach (var reward in target.Rewards)
            {
                if (Locked(reward, locked))
                    return new[] { reward.Name };
            }
        }

        var found = new string[target.Rewards.Count];

        for (var i = 0; i < target.Rewards.Count; i++)
            found[i] = target.Rewards[i].Name;

        return found;
    }

    /// <summary>
    /// The same list as Names, holding what identifies each combination rather than what it yields.
    ///
    /// **Walked exactly as Names and Choices are**, so entry i is one combination across all three.
    /// A name is not an identity - a remnant can offer "Skill Level 20: Leylines" and "Skill:
    /// Leylines", six propagating slots against four, both named "Leylines" - so anything matching
    /// the planner's choice against a row on screen has to match on this. See Valuation.Reward.
    /// </summary>
    public static string[] RecipeIdsOfTarget(Target target, AutoExpeditionSettings settings,
        string locked = null)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(locked))
        {
            foreach (var reward in target.Rewards)
            {
                if (Locked(reward, locked))
                    return new[] { reward.Recipe };
            }
        }

        var found = new string[target.Rewards.Count];

        for (var i = 0; i < target.Rewards.Count; i++)
            found[i] = target.Rewards[i].Recipe;

        return found;
    }

    private static (string Id, int Tag, float Percent, bool Flat)[] ScopedEffectsOfReward(Reward reward,
        AutoExpeditionSettings settings)
    {
        if (reward?.Carrying == null || reward.Carrying.Length == 0)
            return null;

        List<(string, int, float, bool)> found = null;

        foreach (var rune in reward.Carrying)
        {
            var scope = Runes.Scope(rune);

            if (scope.Trim().Length == 0)
                continue;

            foreach (var (tag, percent) in Tags.Scope(scope, out _))
            {
                if (percent <= 0f)
                    continue;

                // **Kept per rune rather than summed per tag.** Two runes reaching the same thing
                // are two runes, and with stacking off each is credited once against the most it
                // could ever reach - which a total that has forgotten where it came from cannot be.
                // See Planner.Book.
                found ??= new List<(string, int, float, bool)>(2);
                found.Add((rune, tag, percent, false));
            }
        }

        return found?.ToArray();
    }

    /// <summary>
    /// What this object passes on to particular kinds of thing, summed per tag.
    ///
    /// **The half a flat percentage could never say.** "Increased Quantity of Items Found in
    /// Excavated Chests" reaches chests; "50% increased number of Rare Monsters" reaches rare
    /// monsters; the old single number reached the monsters unearthed downstream whatever the
    /// modifier actually said. Each scoped effect names its own tags and they are added up per tag,
    /// so two effects both reaching excavated chests make one accumulator lookup rather than two.
    ///
    /// **Stacking effects only, in this pass.** One that pays once however many are taken is booked
    /// against its own name through Unique, and routing that through a scope as well is a second
    /// change to the same arithmetic - worth doing separately, where it can be checked on its own.
    /// A non-stacking effect with a scope therefore still reaches monsters, as it did before.
    /// </summary>
    public static (string Id, int Tag, float Percent, bool Flat)[] ScopedEffectsOfTarget(Target target)
    {
        if (target == null)
            return null;

        List<(string, int, float, bool)> found = null;

        // **Merged per effect AND tag, not per tag.** It used to add every scope on an object into
        // one figure per tag, which was right while everything was pooled additively and is wrong
        // now: whether two modifiers add or multiply is a question about WHICH modifiers they are,
        // so the name has to survive the merge. Two shares of one effect on one tag still fold
        // together, because that is one modifier stated twice. See TableGrammar.Effect.
        void Take(string id)
        {
            // **An empowering row takes a bare percentage, because it has nothing to point at.**
            //
            // Every other propagation entry is tag=percent, and the tag is the whole of what makes
            // it meaningful: it says which things on the ground get their weight lifted. A rune that
            // scales other runes points at no entity at all, so there is no honest tag to write -
            // and the vocabulary has none for it, so the first spelling reached for was monster=30,
            // a tag the scoring then discards. A column that silently ignores half of what you typed
            // is worse than one that asks for less.
            //
            // So "30" is the form, and "rune=30" is accepted too because that is what somebody
            // reading "Runes gain: Empowered" will write. Either way only the number is used. See
            // Combining's reserved word and PlanEnvironment.Empowering.
            if (string.Equals(GroupKeyOfEffect(id), Empowering, StringComparison.OrdinalIgnoreCase))
            {
                // From the effect, not the scope text. See Lift.
                var lift = Lift(id);

                if (lift > 0f)
                {
                    found ??= new List<(string, int, float, bool)>(2);
                    // Any tag will do and none of them is read - see Rate's empowering branch,
                    // which takes the percentage and returns before the tag is looked at.
                    found.Add((id, Tags.Monsters, lift, false));
                }

                return;
            }

            // **The effect is where the magnitude lives now, not the scope cell.**
            //
            // These were two descriptions of one fact: scope said "monster=20" and effect said
            // "monster.weight *= +20%", and only the first was ever read here - so a row carrying
            // only an effect passed on NOTHING while displaying a percentage in the table. Three
            // shipped rows were in that state, the relic rarity and pack size upsides, each
            // asserting an uplift the planner never applied.
            //
            // EffectsOfRow answers for both: a row with an effect is parsed, and a row with only the
            // old cells is translated on the way past. So this reads one thing and no row is left
            // behind. See TableGrammar.Translated, which dies with the scope column in stage 5.
            foreach (var effect in TableGrammar.EffectsOfRow(Wrt.Id.Found(id), out _))
            {
                // **A flat effect keeps its own number.** A share is a percentage of what it
                // reaches and is carried as one; "+= 3" is three of whatever weight is, per thing
                // it reaches, and multiplying it by a hundred would make it three hundred. The two
                // travel together and are told apart by the flag rather than by their size.
                var flat = !effect.Multiplies;
                var percent = flat ? effect.Share : effect.Share * 100f;
                var tag = Tags.Bit(effect.Target);

                if (percent <= 0f || tag < 0)
                    continue;

                found ??= new List<(string, int, float, bool)>(2);

                for (var i = 0; i < found.Count; i++)
                {
                    // Two of one kind fold together; a share and a flat on one tag do not, since
                    // they are added to different things.
                    if (found[i].Item2 != tag || found[i].Item4 != flat ||
                        !string.Equals(found[i].Item1, id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    found[i] = (id, tag, found[i].Item3 + percent, flat);

                    return;
                }

                found.Add((id, tag, percent, flat));
            }
        }

        var own = Unknowns.Key(target);

        Take(own);

        foreach (var effect in Unknowns.Effects(target))
        {
            if (Unknowns.Stacking(effect))
                Take(effect);
        }

        // **And the row a relic is priced by, which this could not reach.**
        //
        // Take looks a row up in the found: namespace, so a relic answering to a setting: row - the
        // goblin totem, the sulphite stalagmite, the Karui totem, the runic henge - had its effect
        // cell read by nothing. That is the whole reason those four still carry a v1 Carries: it
        // was the only cell of theirs the scoring could see. See Weighing.Relic and STATE.md on
        // retiring Carries.
        if (target.Kind == TargetKind.Relic && RelicWeightFromMods(target) is { Length: > 0 } filed)
            TakeScopedEffectsOfRow(filed, ref found);

        return found?.ToArray();
    }

    /// <summary>
    /// The scoped effects written on one row, named by its whole id.
    ///
    /// Take builds a found: key from what it is given, which is right for a discovered object and
    /// wrong for a row that is already filed under its own namespace. Split out rather than given a
    /// flag, because the two are different questions: one asks what this THING passes on, the other
    /// what this ROW says.
    /// </summary>
    private static void TakeScopedEffectsOfRow(string filed,
        ref List<(string, int, float, bool)> found)
    {
        foreach (var effect in TableGrammar.EffectsOfRow(filed, out _))
        {
            var flat = !effect.Multiplies;
            var percent = flat ? effect.Share : effect.Share * 100f;
            var tag = Tags.Bit(effect.Target);

            if (percent <= 0f || tag < 0)
                continue;

            found ??= new List<(string, int, float, bool)>(2);

            var already = false;

            for (var i = 0; i < found.Count; i++)
            {
                if (found[i].Item2 != tag || found[i].Item4 != flat ||
                    !string.Equals(found[i].Item1, filed, StringComparison.OrdinalIgnoreCase))
                    continue;

                found[i] = (filed, tag, found[i].Item3 + percent, flat);
                already = true;

                break;
            }

            if (!already)
                found.Add((filed, tag, percent, flat));
        }
    }

    /// <summary>
    /// The effects this object grants that pay once however many are taken, with their percentages.
    ///
    /// Each is booked against its own name, so two relics both granting "runic monsters duplicated"
    /// are one credit between them - which is the thing the objective was getting wrong when a
    /// whole relic was the unit. See PlanTarget.Unique.
    /// </summary>
    public static (string Id, int Tag, float Percent, float Weight)[] NonStacking(Target target)
    {
        List<(string, int, float, float)> found = null;

        foreach (var effect in Unknowns.Effects(target))
        {
            if (Unknowns.Stacking(effect))
                continue;

            var priced = Unknowns.Of(effect);

            // **What it reaches, and how much, from the row's own effect.**
            //
            // This took the magnitude from Carries and never read a scope, so a row saying
            // "rare_monster.weight *= +100%" paid its carry against every monster instead - the
            // number from one cell and the target from nowhere. Carries is gone now, so the effect
            // is the only statement there is, and it says both.
            var share = 0f;
            var tag = Tags.Monsters;

            foreach (var said in TableGrammar.EffectsOfRow(Wrt.Id.Found(effect), out _))
            {
                var bit = Tags.Bit(said.Target);

                if (said.Share <= 0f || bit < 0)
                    continue;

                share = said.Share * 100f;
                tag = bit;

                break;
            }

            // **The WEIGHT travels with it, not only the share it passes on.**
            //
            // This booked the percentage and left the flat weight behind, and Unpriced adds that
            // weight to every object granting the effect - so four relics all granting "runic
            // monsters are duplicated" were worth four times its weight to the chain, for a switch
            // that flips once. The objective then did exactly what it was told and collected all
            // four. Only the propagation was ever deduplicated; the part a player actually sets is
            // the weight.
            //
            // And no percentage is no longer a reason to skip: a switch worth points but passing
            // nothing on still has to be booked, or it goes back to stacking.
            if (share <= 0f && priced <= 0f)
                continue;

            found ??= new List<(string, int, float, float)>();
            found.Add((effect, tag, share, priced));
        }

        return found?.ToArray();
    }

    /// <summary>
    /// The effect name to book a non-stacking carry under, or nothing when it adds up.
    ///
    /// The key the unknown weights file it under, which is exactly the right granularity: two
    /// objects granting the same thing share a key and therefore one credit, and two granting
    /// different things have different keys and stack with each other as they should.
    /// </summary>
    public static string Once(Target target)
    {
        if (target == null)
            return "";

        var key = Unknowns.Key(target);

        return Unknowns.Stacking(key) ? "" : key;
    }

    /// <summary>
    /// Which row a relic answers to, or null where nobody has written one for it.
    ///
    /// **Six hand-written matchers, now six cells.** This used to be a chain of branches asking
    /// whether a relic's mods contained "SpecialGoblinTotem" or its metadata contained
    /// "Objects/Sulphite" - fragments, in code, of names written down nowhere, so a relic the game
    /// added needed a build rather than a row. Worse, each branch RETURNED, so a relic matching one
    /// was priced on that alone.
    ///
    /// The three identifiers a relic has, in the order of how much each is worth trusting:
    ///
    ///  - **The modifier**, which is the game naming its own effect. Matched whole, never as a
    ///    fragment - see TableGrammar.RowForModifier. Each special relic wears two ids, an Upside and a Modifier,
    ///    and both are on the row, so either one finds it.
    ///  - **The art**, a model, which is what a sulphite pillar or a henge is known by anywhere its
    ///    modifiers have not streamed in - and the identifier that is set from the moment the entity
    ///    exists, where the rest of it reads as nothing from across the map.
    ///  - **The metadata**, a full path, for anything the first two cannot name.
    ///
    /// **The old chain's metadata fragments are gone rather than carried over, and that is the point
    /// of the rewrite.** It matched on "Objects/Sulphite", "GoblinRelic", "HeathHenge",
    /// "CrystallineBeastSkin", "Objects/Totem" and "WispTrap_" - and not one of those appears in any
    /// of the hundred and sixty-seven metadata paths on record, because no entity dump has ever
    /// contained one of the five special relics. Every relic that HAS been dumped reads
    /// Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic. There is no exact form to write, so
    /// nothing is written: five of them are bound by modifier and by art instead, and the three wisp
    /// traps, which have neither, bind to nothing until a dump names them.
    ///
    /// A relic that binds to nothing is filed with its full identity, ready to be bound by hand -
    /// which is the loop working. A guessed fragment is the loop being skipped, and it cannot be told
    /// apart from a relic nobody has met. See Noted, which reports what answered for each.
    ///
    /// All three sit in one matches: cell, so a row is found by whichever arrives first and it is the
    /// same row either way. Null means no row, which is filed rather than guessed at.
    ///
    /// **The three wisp traps are bound by art, one row each, and which art is which is measured.**
    /// This said their arts were known but not which was which, and refused to guess - correctly. A
    /// dump of Grazed Prairie settles it: six WispTrap entities, three metadata paths and three
    /// ground labels, with Noted reporting the art each answered for. ezomytewisptrap01 is Primal,
    /// 02 is Vivid, 03 is Wild.
    ///
    /// Bound by art rather than by metadata because art is what actually resolves here - it is tried
    /// first of the two, and it is set from the moment the entity exists where the path is not always
    /// read. The modifier is no use for this: one Azmeri modifier covers all three, so binding it
    /// would pick whichever row the index happened to hold, and no row claims it.
    /// </summary>
    internal static string RelicWeightFromMods(Target target)
    {
        if (target == null)
            return null;

        var mods = Safe.Read(() => target.Mods, "") ?? "";

        foreach (var piece in mods.Split(','))
        {
            var found = TableGrammar.RowForModifier(piece.Trim());

            if (found != null)
                return WeightNotedOnRow(target, found, "modifier");
        }

        var art = Safe.Read(() => target.Art, "") ?? "";
        var byArt = art.Length > 0 ? TableGrammar.Matched(art, TableGrammar.Art) : null;

        if (byArt != null)
            return WeightNotedOnRow(target, byArt, "art");

        var meta = Safe.Read(() => target.Meta, "") ?? "";
        var byPath = meta.Length > 0 ? TableGrammar.Matched(meta, TableGrammar.Path) : null;

        return byPath != null ? WeightNotedOnRow(target, byPath, "metadata") : WeightNotedOnRow(target, null, "");
    }

    /// <summary>
    /// Writes down which relic bound to which row, for the dump.
    ///
    /// **A binding that silently does not fire is the fault this table exists to end.** The old chain
    /// had one: a matcher for "UpsideExperienceKarui" against a game that says
    /// ExpeditionRelicUpsideExperience, which never fired once and said nothing about it. Nothing in
    /// the plugin could have shown that, because the only evidence was a branch not taken.
    ///
    /// So every relic weighed records what it is and what answered, matched or not, and the dump
    /// prints it. First sighting per art - this runs inside the weighing.
    /// </summary>
    private static string WeightNotedOnRow(Target target, string id, string by)
    {
        var art = Safe.Read(() => target.Art, "") ?? "";
        var meta = Safe.Read(() => target.Meta, "") ?? "";
        var key = art.Length > 0 ? art : meta;

        lock (Answers)
        {
            Answers.TryAdd(key,
                (id == null ? "no row" : $"{id}, by {by}") +
                $"{Environment.NewLine}      meta {meta}" +
                $"{Environment.NewLine}      mods [{Safe.Read(() => target.Mods, "")}]");
        }

        return id;
    }

    /// <summary>Which row every relic met so far answered to, for the dump. See Noted.</summary>
    public static IReadOnlyDictionary<string, string> WeightAnsweredByCode()
    {
        lock (Answers)
            return new Dictionary<string, string>(Answers, StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, string> Answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a relic is worth, and what it passes on, from the row bound to it.
    ///
    /// **A relic nobody has written a row for is not a generic relic, it is an unread one.** It is
    /// filed under the modifier that names its effect rather than averaged into an "unknown relic"
    /// weight, so it gets its own row, its own red label on the ground, and stops being priced the
    /// same as something it has nothing in common with. See Unknowns.
    /// </summary>
    private static (float Weight, float Carries) Relic(Target target)
    {
        var id = RelicWeightFromMods(target);

        // **The carry is not returned any more, because ScopedEffectsOfTarget now reads the row.**
        //
        // A setting: row's effect cell was unreachable, so this handed its Carries back as a flat
        // percentage and the objective paid it that way. Now that the row's effects are read like
        // any other, returning the carry as well would pay the same magnitude twice - once through
        // Spread and once as the target's own flat carry. See ScopedEffectsOfTarget, which takes
        // the relic's filed row.
        if (id != null)
            return (MathF.Max(0f, TableGrammar.Total(id).Fixed), 0f);

        // Filed once, when everything that names it has arrived. See Unknowns.Register.
        // Nought for the carry: a relic passes things on through its effects now, and Relic's
        // callers still take the pair. See Weighing.Carries.
        return (PricedRowOfTarget(target, Unknowns.Register(target)), 0f);
    }

    /// <summary>
    /// Whether this relic passes something on to the monsters unearthed after it.
    ///
    /// **Per art, because relics share a state and not a purpose.** The goblin relic's own text is
    /// "15% increased rarity of items dropped by monsters" - a modifier on monsters, which is what
    /// propagation means. The Prairie wisp traps say "contains an Azmeri spirit", which is something
    /// you collect where it stands and reaches nothing downstream. Both read expedition_relic, so
    /// the state cannot separate them and the metadata is a tileset's choice of words.
    ///
    /// Named rather than pattern-matched, and the default is NOT to carry: a relic nobody has read
    /// gets its flat weight and no propagation, so an unknown one is taken in passing instead of
    /// having a chain bent around a percentage somebody assumed it had.
    /// </summary>
    /// <summary>
    /// What a blast lets out from behind a gate, and why the monolith beside it is priced elsewhere.
    ///
    /// The Peninsula rhoa gate holds one rare and one unique; a Prairie monolith summons a unique
    /// boss on its own. Both are monsters a blast lets out, so both belong in the propagation term -
    /// but they are two kinds now, because that is the only thing they have in common.
    ///
    /// **The gate is counted in rares and the monolith is not, which is the difference between them
    /// rather than an inconsistency.** What is behind the gate is literally rare monsters, so its
    /// worth should follow the rare weight wherever that is set - retune rares and the gate is
    /// retuned with it. A unique boss is not a quantity of rares, and pricing it as one made it move
    /// whenever an unrelated number did. See TargetKind.Monolith.
    /// </summary>

    public static float Carries(Target target)
    {
        // A relic carries too, and it is the same kind of thing a rune is.
        //
        // **"15% increased rarity of items dropped by monsters", printed on the object.** That is a
        // modifier on monsters, site-wide, exactly as a propagating rune is - so it belongs in the
        // propagation term rather than in the content weight, and pricing it as a flat ten points
        // of loot was describing the wrong mechanic entirely. Nine relics on one site, each lifting
        // everything unearthed after it, is worth far more than ninety points and it also makes the
        // ORDER matter: a relic taken on the first link lifts fourteen blasts of monsters, and on
        // the last it lifts nothing.
        //
        // The carrier test downstream is "Carries > 0", never "is a remnant", so this is all it
        // takes - the propagation sum, the ordering operators and the score card pick it up
        // unchanged.
        if (target.Kind == TargetKind.Relic)
            return MathF.Max(0f, Relic(target).Carries);

        if (target.Kind != TargetKind.Remnant || target.Passing == null)
            return 0f;

        var best = 0f;

        foreach (var slot in target.Passing)
        {
            foreach (var rune in slot.Runes)
            {
                var weight = Runes.Weight(rune);

                if (weight > best)
                    best = weight;
            }
        }

        return best;
    }

    /// <summary>
    /// Which runes this remnant carries forward, one per propagating slot, with its weight.
    ///
    /// For the objective when runes are not assumed to stack, where knowing how much a remnant is
    /// worth is not enough - it has to know WHICH rune, so that the same one offered by two
    /// remnants is counted once. Null for anything that carries nothing, which the scorer reads as
    /// "skip me" without allocating an empty array per target per solve.
    ///
    /// **One per slot, because a slot holds one rune.** Valuation.Passing lists, for any slot but
    /// the fixed one, every rune that any still-reachable recipe could put there - a set of
    /// possibilities, not a reading of the ground. On a fresh remnant the propagating slots are
    /// EMPTY, so nothing narrows the recipes and the set is the whole rune pool. This used to take
    /// all of them and book each at full weight.
    ///
    /// **It bites whenever the rewards have not been read**, which is every remnant the chain has
    /// not been close enough to open. Choices returns null on Rewards.Count == 0, so the per-recipe
    /// view that picks one rune per slot never exists and this target-level path is what prices it.
    ///
    /// Measured, 2026-09-23: one remnant at (909,340), six sockets - one fixed rune and five empty
    /// slots - with no rewards read. 32 runes booked against it, each paying a share, on a chain
    /// worth 233,444 of which 229,939 was propagation. The score card credited it as the first
    /// source of every rune in the game, and the plan routed to it accordingly.
    ///
    /// **The strongest candidate, not all of them.** The slot will hold exactly one, and which is
    /// not knowable until the window is read - so this takes the best, which is the same answer
    /// Carries above already gives for the same remnant and keeps the two from disagreeing. It is
    /// still optimistic where a slot is unresolved, and bounded: one rune's worth rather than
    /// thirty-two.
    /// </summary>
    /// <summary>
    /// What every rune that could sit in a propagating slot is worth, as a LOOKUP.
    ///
    /// **Not a set of runes a remnant could have at once.** Slots are correlated - a remnant becomes
    /// one recipe, and the recipe fixes every slot together - so reading two entries here and
    /// treating the pair as reachable invents a remnant. Measured at (602,901): its propagating
    /// slots offer Fire, Stone, Life and Tempest, Cold, and the only pairings that exist are
    /// Life+Tempest, Life+Cold, Fire alone and Stone alone. Fire with Tempest is not a remnant, and
    /// picking a maximum per slot produced exactly that.
    ///
    /// So nothing may choose from this. It answers "what is this rune worth" for a rune that
    /// something else - the chosen recipe - has already named. See Planner.WeightsOfChosenRunes,
    /// which is its only caller, and StrongestRunePerPropagatingSlot for the case where no recipe
    /// has been chosen because the rewards have not been read.
    /// </summary>
    public static (string Id, float Weight)[] PropagatingRuneWeights(Target target)
    {
        if (target.Kind != TargetKind.Remnant || target.Passing is not { Count: > 0 })
            return null;

        var found = new List<(string Id, float Weight)>();

        foreach (var slot in target.Passing)
        {
            foreach (var rune in slot.Runes ?? [])
            {
                if (string.IsNullOrWhiteSpace(rune))
                    continue;

                var known = false;

                foreach (var (id, _) in found)
                {
                    if (string.Equals(id, rune, StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;

                        break;
                    }
                }

                if (!known)
                    found.Add((rune, Runes.Weight(rune)));
            }
        }

        return found.Count == 0 ? null : found.ToArray();
    }

    public static (string Id, float Weight)[] StrongestRunePerPropagatingSlot(Target target)
    {
        if (target.Kind != TargetKind.Remnant || target.Passing is not { Count: > 0 })
            return null;

        var found = new List<(string, float)>();

        foreach (var slot in target.Passing)
        {
            string pick = null;
            var most = 0f;

            foreach (var rune in slot.Runes)
            {
                var weight = Runes.Weight(rune);

                if (weight > most)
                {
                    most = weight;
                    pick = rune;
                }
            }

            if (pick == null)
                continue;

            // Two slots can still resolve to the same rune, and one rune is one credit - the same
            // rule the booking applies, kept here so the array does not carry a duplicate into it.
            var known = false;

            foreach (var (id, _) in found)
            {
                if (string.Equals(id, pick, StringComparison.OrdinalIgnoreCase))
                {
                    known = true;

                    break;
                }
            }

            if (!known)
                found.Add((pick, most));
        }

        return found.Count == 0 ? null : found.ToArray();
    }

    /// <summary>
    /// A chest, by tier.
    ///
    /// An unrecognised marker art is worth the Uncommon weight rather than the Common one. Rare
    /// chests are known to drop from expeditions and neither identified tier is one, so an
    /// unmatched art is more likely to sit above the two that are known than below - and of the two
    /// ways to be wrong, walking to a chest that turns out white costs a few seconds where ignoring
    /// one that turns out yellow costs the chest.
    /// </summary>
    private static float Chest(Target target, AutoExpeditionSettings settings)
    {
        var tier = target.Tier;

        // **The marker, where there is one.** A chestmarker is a signpost that spawns a chest, and the
        // two were one row for the same reason the elite marker and its rare were: the art decided a
        // tier in Scan and the tier decided a row, so the table only ever met the chest. A marker is a
        // row of its own now with the chest as its child.
        //
        // A Grand site's reward chest has no marker - it IS the chest - and answers to its tier's row
        // through the icon that names it. Either way the fallback is the tier row, which is the answer
        // this always gave, for a chest whose art and icon have both yet to stream in.
        var known = tier == ChestTier.Unknown ? ChestTier.Uncommon : tier;

        return WeightOfMarker(target, Wrt.RowOfTier(known));
    }
}
