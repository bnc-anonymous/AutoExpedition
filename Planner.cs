using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

namespace AutoExpedition;

/// <param name="Weight">What this is worth. Zero means the player does not want it and it is dropped.</param>
/// <param name="Carries">
/// For a remnant, the PERCENTAGE uplift its propagating rune applies to monsters unearthed after it.
///
/// The one term in the objective that is not a property of the target. The game states what
/// propagation does - "added to all Monsters unearthed after this Remnant" - so the value of a rune
/// is a percentage of the monsters the rest of the chain catches. The same remnant taken first is
/// worth far more than taken last, and worth more again in a chain that goes on to catch rares.
///
/// Zero for everything that is not a remnant, and for a remnant whose propagation has not been read.
/// </param>
/// <param name="Runes">
/// The distinct runes this remnant could carry forward, with their weights.
///
/// Only needed when runes are NOT assumed to stack, where the objective has to know which rune is
/// which rather than only how much a remnant is worth - two remnants carrying Opulent are worth one
/// Opulent, and telling that from two remnants carrying Opulent and Power requires the names.
/// </param>
/// <param name="Waves">
/// For a remnant, the monster weight its own detonation brings - "each Rune adds another wave of
/// monsters", so it scales with sockets.
///
/// It counts as monsters for propagation, because that is what it is. A rune carried into a chain
/// that ends at a six socket remnant reaches six waves of monsters it would otherwise have missed,
/// and a model that only counted marker monsters would rate that chain as carrying the rune
/// nowhere.
/// </param>
/// <param name="Choices">
/// For a remnant, every way it could be taken: the reward's weight, the percentage that same
/// combination carries forward, and which runes those are.
///
/// **A remnant is one decision, and it used to be scored as two.** Weight carried the price of the
/// best reward and Carries the percentage of the best rune, added together - which says you can
/// have both, and you cannot: picking a combination picks the reward and the propagation at once.
///
/// It cannot be resolved before the search, because which one wins depends on where the remnant
/// lands: a rune carried by the first link is worth every monster after it and by the last is worth
/// nothing. So the options come in as a list and the objective takes the best of them against the
/// downstream weight that position actually has.
///
/// Null for anything that is not a remnant, and for a remnant with no priced rewards - and then
/// Weight and Carries behave exactly as they did.
/// </param>
/// <param name="Plain_REMOVED">
/// **Gone.** It held what a marker was worth before the insistence was added to its weight, back
/// when insistence WAS a weight - so every readout had to ask for this one and every scorer had to
/// remember not to. Insistence is a flag now (see Must), Weight is the marker's own worth again,
/// and one number needs no second number to undo it.
///
/// **A required remnant is weighted above everything else in the site put together, on purpose**,
/// so that the chain is built around it at any scale the player chooses. That makes it useless in
/// any figure about how much of a site a plan takes: one target holding eighty per cent of the
/// content is trivially included by both the plan and the ceiling, so the share reads as "almost
/// optimal" on every site that has one, which is the opposite of informative. Measured on a
/// Peninsula site: 10,996 of 13,384 content in a single blast, and a plan "getting 99% of the
/// ceiling" that was really getting 99% of one remnant.
///
/// So the search keeps Weight, which is what makes it reach, and the readouts use this.
/// </param>
/// <param name="Sets">
/// Everything this target detonates when it is caught, as indices into the target list.
///
/// **A barrel is not worth points, it is worth a blast.** Catching one sets off everything inside
/// its own radius - sixty grid against an explosive's thirty five - and that includes other barrels,
/// so what a chain actually takes is the closure rather than a circle. Worked out once per
/// environment, because it depends on nothing the search does.
///
/// Empty for everything else, which is nearly everything.
/// </param>
/// <param name="NonStacking">
/// Effects this target grants that pay once however many are taken, each with its percentage.
///
/// **A relic grants several things at once and only some of them are switches.** One that gives
/// rarity, quantity and "runic monsters are duplicated" stacks in two of those three - so the object
/// cannot be the unit the non-stacking rule applies to, or turning it off would throw away the two
/// that do add up. Each effect is booked against its own name instead, which also makes two
/// different relics that both grant duplication share a single credit.
/// </param>
/// <param name="Must">
/// Whether the player has said this one has to be taken.
///
/// **A statement about whether a chain is allowed, not about what it is worth.** It used to be a
/// weight: a marked marker carried more than the rest of the site put together, which ranks a chain
/// that takes it above one that does not and was, for a while, indistinguishable from a rule. It is
/// not the same thing, and the difference showed up in three places - the size of that number had to
/// be recomputed per solve and drifted by a factor of three inside one site, it leaked into the
/// monster total a carried rune multiplies, and nothing could ever SAY that a plan had dropped one,
/// because by then the marker was just a large number among other numbers.
///
/// So the flag is carried and the weight is left alone. See Verdict.Missed, which is what refuses
/// the chain, and PlanEnvironment.Refused, which is what it costs.
/// </param>
/// <param name="Once">
/// The name of an effect that pays only once however many are taken, or empty when it adds up.
///
/// **Some effects are a switch rather than a quantity.** "Runic monsters are duplicated" doubles
/// everything the rest of the chain unearths and a second one doubles nothing - so an objective that
/// sums them will bend a chain towards collecting four, and score it at four times what it is worth.
///
/// A name rather than a flag, because "does not stack" is a statement about an EFFECT and not about
/// an object: two relics granting the same thing are the pair that must not stack, and two granting
/// different things stack with each other perfectly well. The name is what tells those apart, and it
/// is the same key the unknown weights file them under.
/// </param>
internal sealed record PlanTarget(Vector2 Grid, float Radius, float Weight, TargetKind Kind,
    float Carries = 0f, (string Id, float Weight)[] Runes = null, float Waves = 0f,
    (string Id, float Weight)[] PropagatingRuneWeights = null,
    (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices = null,
    string Once = "", (string Id, int Tag, float Percent, float Weight)[] NonStacking = null,
    int[] Sets = null, bool Must = false,
    long Mask = 0L, (string Id, int Tag, float Percent, bool Flat)[] Spread = null,
    int Group = 0,
    (long Mask, float Worth, float Many)[] Parts = null,
    // What each entry of Choices is called, same order, for publishing the one taken. At the end
    // because these are positional and everything before it is matched by position. See Picked.
    string[] Named = null,
    // What identifies each entry, same order again. A name is not an identity - see
    // Weighing.RecipeIdsOfTarget - so the marker saying which row was taken matches on this.
    string[] Recipes = null,
    // The rare and magic wave worth each entry adds over the remnant's own recipe, same order, for Gaining Traction.
    // See Weighing.MagicAndRareWavesOfChoices.
    (float Rare, float Magic)[] MagicAndRareWavesOfChoices = null,
    // What each entry's runes do to this remnant's own waves - the table's "own" effects - same order, or null for
    // none. Anything replacing Choices replaces this with it. See Weighing.OwnEffectsOfChoices and OwnOfChoice.
    (int Tag, bool Count, float Factor, float WaveShare)[][] OwnEffectsOfChoices = null,
    // The lift each entry's recipe holds in any slot, per amplifier class - 1 on the rune class for a Power rune - same
    // order, or null for none. Anything replacing Choices replaces this with it. See Weighing.HeldLiftOfChoices and
    // HeldLiftsOfChoice.
    float[][] HeldLiftOfChoices = null,
    // The share of this remnant's own waves each entry's propagated runes are on, same order, or null for all of them.
    // Anything replacing Choices replaces this with it. See Weighing.CarriedWaveSharesOfChoices and WaveShareOfCarried.
    (string Id, float Share)[][] CarriedWaveSharesOfChoices = null,
    // How many waves this remnant brings, or nought when not known. See Weighing.WavesOfRemnant and
    // ShareOfWavesPassedLift.
    int WaveCount = 0,
    // The rune in each slot of each entry's recipe, in slot order, same order as Choices, or null when not known.
    // Anything replacing Choices replaces this with it. See Weighing.SlotRunesOfChoices and SlotRunesOfChoice.
    string[][] SlotRunesOfChoices = null,
    // The monsters each entry's runes add to this remnant's waves - the table's "per" effects - same order, or null
    // for none. Anything replacing Choices replaces this with it. See Weighing.CreatedOfChoices and CreatedByOwnEffects.
    (int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment)[][] CreatedOfChoices = null,
    // What Gaining Traction multiplies this remnant's normal, magic and rare waves by, by remnants completed before it,
    // or null for the environment's own rule. See Weighing.TractionByBeforeOfTarget and Planner.TractionScalesOfTarget.
    (float Normal, float Magic, float Rare)[] TractionByBefore = null)
{
    /// <summary>The rune in each slot of combination c's recipe, or null when not known. See Propagation.RunesPerWave.</summary>
    public string[] SlotRunesOfChoice(int c) =>
        SlotRunesOfChoices is { } all && c >= 0 && c < all.Length ? all[c] : null;

    /// <summary>
    /// The share of this remnant's waves a lift passed in from an earlier link reaches: all but the first. Measured on 15
    /// remnants receiving Power on 6 maps (2026-10-02): the monsters of wave 1 carried their runes plain, every wave from
    /// the second on carried them empowered (98% of 454 monsters on waves 3-8). Waves 1 and 2 carry the same runes, and
    /// the split between them is read from timing - the plain ones arrived first, the empowered about 3 s later. One
    /// with an unknown wave count gets all of them.
    /// </summary>
    public float ShareOfWavesPassedLift => WaveCount >= 2 ? (WaveCount - 1f) / WaveCount : 1f;

    /// <summary>
    /// What this target files into the tag and class pools: its OWN weight under its own tags, and
    /// each part of what it is made of under the tags of the row that earned it.
    ///
    /// **A container's weight is not its own.** A cage is worth nothing and holds three rares worth
    /// sixty; a monstermarker is worth nothing and is one monster of an undecided tier. Filing the
    /// rolled figure under the container's tags said the sixty was sixty of CAGE - so a modifier
    /// aimed at rare monsters missed the rares, and one aimed at caged monsters multiplied a thing
    /// that does not exist. The parts carry the tags of the rows they came from, because that is
    /// where the creature is described. See Weighing.PartsOfTarget.
    ///
    /// **Parts the per-marker payout will pay are left out**, because that loop pays them at this
    /// marker's own rate as well as the chain's - a remnant's ordinary runes reach its own waves
    /// and nothing else - and a part filed here as well would be paid twice.
    ///
    /// Nothing is being paid twice today, and it is worth saying why the guard is here anyway. A
    /// class is built from the tags a mask carries that some effect is SCOPED to, plus the monster
    /// tag at slot nought - see Relevance - and class nought is the one the payout skips. A remnant
    /// carries no weight_modifiable tag, so it never reaches the class at all; a strongbox carries
    /// one but carries neither the monster tag nor any scoped tag, so its class comes out nought
    /// too. Both escape by a property of what they happen to be tagged rather than by a rule. Write
    /// "strongbox=20" on a relic one day and the box's guards, which are already inside its weight,
    /// start being paid here as well as per marker.
    ///
    /// The own share is the rolled weight less the parts, so the three cases fall out of one
    /// subtraction: a chest keeps all of it, a cage keeps none, and a strongbox keeps the box.
    ///
    /// Nothing at all for a marker the plan is avoiding, whose weight is below every legal chain -
    /// the pools are what modifiers multiply, and there is nothing there to multiply.
    ///
    /// Held rather than rebuilt: the scoring reads this per covered marker per scored chain, half a
    /// million times a solve. Two threads racing to build it is benign - they build the same array
    /// from the same fields and the reference assignment is atomic.
    /// </summary>
    /// <summary>
    /// Whether this object's parts are settled by the per-marker payout rather than the chain-wide
    /// one, which is true of anything holding runes of its own.
    ///
    /// **The two sides of this must not drift**, so both read it here: Contributions leaves such
    /// parts out, and the payout walks them. A part in both is paid twice; a part in neither is not
    /// paid at all. It is a combination to choose between that makes the difference - a remnant's
    /// ordinary runes reach its own waves and nothing else, so its waves have to be priced against
    /// the rates in its own sockets. Anything else has no rate of its own, and the chain-wide payout
    /// gives the same answer more cheaply.
    /// </summary>
    public bool PartsSettledPerMarker => Choices is { Length: > 0 };

    public (long Mask, float Worth, float Many)[] Contributions
    {
        get
        {
            if (_contributions != null)
                return _contributions;

            var worth = MathF.Max(0f, Weight);

            if (worth <= 0f)
                return _contributions = [(Mask, 0f, 1f)];

            if (Parts is not { Length: > 0 })
                return _contributions = [(Mask, worth, 1f)];

            var own = worth;

            foreach (var (_, part, _) in Parts)
                own -= part;

            // Paid per marker instead, at this marker's own rate as well as the chain's. See above.
            if (PartsSettledPerMarker)
                return _contributions = [(Mask, MathF.Max(0f, own), 1f)];

            // A container keeps nothing of its own, which is the ordinary case for anything made of
            // parts - so the parts themselves are the answer and no array is built.
            if (own <= 0.0001f)
                return _contributions = Parts;

            var made = new (long Mask, float Worth, float Many)[Parts.Length + 1];

            // The container counts as one of itself beside the things it is made of.
            made[0] = (Mask, own, 1f);
            Parts.CopyTo(made, 1);

            return _contributions = made;
        }
    }

    private (long Mask, float Worth, float Many)[] _contributions;

    /// <summary>
    /// The monster weight blowing this up puts on the ground, which is what a carried rune scales.
    ///
    /// **Read from what the thing is made of, not from what kind it is.** This was a list of five
    /// TargetKinds - Monster, Elite, Hatch, Caged, Monolith - answering "is this object's own weight
    /// monsters", beside a mask on the same target already saying so. Two descriptions of one fact,
    /// and the list could only ever be right about the kinds somebody had thought of: a row whose
    /// tags said monster went uncounted unless its kind was named as well.
    ///
    /// So: this object's own weight when the object IS a monster, plus every part of it that is one.
    /// A cage is not a monster and holds three; a siren egg is priced as the monsters it hatches and
    /// holds nothing; a chest is neither.
    ///
    /// Parts are counted whichever payout pays them, unlike Contributions - this is not a payout, it
    /// is the pool the payouts multiply, and a remnant's waves belong in it however they are settled.
    ///
    /// Nought for a marker the plan is avoiding, for the reason Contributions gives.
    /// </summary>
    public float MonstersUnearthed
    {
        get
        {
            if (_monstersUnearthed is { } had)
                return had;

            var worth = MathF.Max(0f, Weight);

            if (worth <= 0f)
                return (_monstersUnearthed = 0f).Value;

            var monsters = 0f;
            var own = worth;

            foreach (var (mask, part, _) in Parts ?? [])
            {
                own -= part;

                if ((mask & (1L << Tags.Monsters)) != 0L)
                    monsters += part;
            }

            if ((Mask & (1L << Tags.Monsters)) != 0L)
                monsters += MathF.Max(0f, own);

            return (_monstersUnearthed = monsters).Value;
        }
    }

    private float? _monstersUnearthed;

    /// <summary>
    /// What this remnant's rare and magic wave monsters are worth, each tier apart, and how much of each is monsters for
    /// the pools runes pay on: its Parts carrying the rare or magic tag. Nought on anything but a remnant. The shares
    /// Gaining Traction scales - see Planner.TractionScales and Planner.Settle.
    /// </summary>
    ///
    /// **Worked out on every read, not cached.** A record compares and hashes by every field, private ones included,
    /// so a cache filled on one thread changes the target's hash under a collection another thread keyed on it: the
    /// edge points' names threw KeyNotFoundException on every solve once workers filled this during another's build
    /// (2026-10-01). It is a sum over a handful of parts.
    public (float Rare, float Magic, float RareMonsters, float MagicMonsters) MagicAndRareWaves
    {
        get
        {
            var (rare, magic, rareMonsters, magicMonsters) = (0f, 0f, 0f, 0f);

            if (Kind == TargetKind.Remnant)
            {
                foreach (var (mask, part, _) in Parts ?? [])
                {
                    if (part <= 0f)
                        continue;

                    var monster = (mask & (1L << Tags.Monsters)) != 0L;

                    if ((mask & (1L << Tags.Rares)) != 0L)
                    {
                        rare += part;
                        rareMonsters += monster ? part : 0f;
                    }
                    else if ((mask & (1L << Tags.Magics)) != 0L)
                    {
                        magic += part;
                        magicMonsters += monster ? part : 0f;
                    }
                }
            }

            return (rare, magic, rareMonsters, magicMonsters);
        }
    }

    /// <summary>
    /// What Gaining Traction adds to combination c through the waves it has over the remnant's own recipe, at the given
    /// scales; negative for a combination with fewer. Nought without the scales or the list. See Planner.Settle.
    /// </summary>
    public float TractionOfChoice(int c, float magicScale, float rareScale) =>
        MagicAndRareWavesOfChoices is { } changes && c >= 0 && c < changes.Length
            ? changes[c].Rare * (rareScale - 1f) + changes[c].Magic * (magicScale - 1f)
            : 0f;

    /// <summary>
    /// The factors combination c's "own" effects put on one of this remnant's wave parts: on its worth (count and
    /// weight effects both) and on its number (count effects only). One and one without any. See Planner.Settle's
    /// payout, which scales each part by these.
    /// </summary>
    public (float Worth, float Many) OwnFactorsOfPart(int c, long mask) => OwnFactorsOfPart(c, mask, 0d);

    /// <summary>
    /// The same, with the bonuses among the "own" effects lifted by an empowering rate passed in from earlier links: a
    /// factor above one becomes 1 + (factor - 1)(1 + lift x reach), a factor below one is left alone.
    ///
    /// **The reach is the share of the effect's waves the lift is on.** A passed-in lift misses wave 1 (see
    /// ShareOfWavesPassedLift). A rune in slot 1 or 2 is on every wave, so the lift reaches all but one of them; a rune
    /// in slot 3 or later only starts on its own slot's wave, so the lift reaches all of them. Cutting both by the
    /// wave-1 share understated Time in slot 4 of 5 by a fifth.
    ///
    /// **Power doubles a rune's bonuses and not its penalties** (user, 2026-10-05): Bond's +34% normal monsters become
    /// +68% where a Power reaches them, while its -78% magic and -67% rare monsters stay as they are. The doubling is
    /// the rule, not a measurement: 2 runs hold Bond with Power. See Planner.Settle's payout, which passes the rune
    /// class's lift at this remnant.
    /// </summary>
    /// <summary>
    /// The lift passed in from earlier links that combination c's own effects take: none when it holds the empowering
    /// rune itself, since the two do not stack and its own already applies; otherwise all of it. See Best and
    /// Planner.PredictedWavesOf, which must agree.
    /// </summary>
    public static double PassedLiftOfChoice(PlanEnvironment env, float[] heldLifts, double passedLift)
    {
        var holdsPower = passedLift > 0d && env.Amplified is { RuneClass: >= 0 } amplified &&
                         heldLifts is { } liftsHeld && amplified.RuneClass < liftsHeld.Length &&
                         liftsHeld[amplified.RuneClass] > 0f;

        return holdsPower ? 0d : passedLift;
    }

    public (float Worth, float Many) OwnFactorsOfPart(int c, long mask, double lift)
    {
        if (OwnEffectsOfChoices is not { } all || c < 0 || c >= all.Length || all[c] is not { } effects)
            return (1f, 1f);

        var (worth, many) = (1f, 1f);

        foreach (var (tag, count, factor, waveShare) in effects)
        {
            if ((mask & (1L << tag)) == 0L)
                continue;

            var reach = waveShare < 1f ? lift : lift * ShareOfWavesPassedLift;
            var lifted = factor > 1f && reach > 0d ? (float)(1d + (factor - 1d) * (1d + reach)) : factor;

            worth *= lifted;

            if (count)
                many *= lifted;
        }

        return (worth, many);
    }

    /// <summary>
    /// What combination c's "own" effects change in the worth of this remnant's own waves, at the given Gaining
    /// Traction scales: the change on each wave part, and on the rare and magic waves the combination has over the
    /// remnant's recipe. Nought without such effects. Content counts the waves at face value through Weight, Gaining
    /// Traction's bonus and TractionOfChoice; this is the difference the effects make to all three. See Planner.Settle.
    /// </summary>
    public float OwnOfChoice(int c, float magicScale, float rareScale) => OwnOfChoice(c, magicScale, rareScale, 0d);

    /// <summary>The same with the bonuses lifted by an empowering rate reaching these waves. See OwnFactorsOfPart.</summary>
    public float OwnOfChoice(int c, float magicScale, float rareScale, double lift)
    {
        if (OwnEffectsOfChoices is not { } all || c < 0 || c >= all.Length || all[c] == null)
            return CreatedByOwnEffects(c, magicScale, rareScale, lift);

        var change = 0f;
        var (rareMask, magicMask) = (0L, 0L);

        foreach (var (mask, part, _) in Parts ?? [])
        {
            var rare = (mask & (1L << Tags.Rares)) != 0L;
            var magic = !rare && (mask & (1L << Tags.Magics)) != 0L;

            if (rare && rareMask == 0L)
                rareMask = mask;

            if (magic && magicMask == 0L)
                magicMask = mask;

            var (worth, _) = OwnFactorsOfPart(c, mask, lift);

            if (part > 0f && worth != 1f)
                change += part * (rare ? rareScale : magic ? magicScale : 1f) * (worth - 1f);
        }

        if (MagicAndRareWavesOfChoices is { } waves && c < waves.Length)
        {
            if (rareMask != 0L)
                change += waves[c].Rare * rareScale * (OwnFactorsOfPart(c, rareMask, lift).Worth - 1f);

            if (magicMask != 0L)
                change += waves[c].Magic * magicScale * (OwnFactorsOfPart(c, magicMask, lift).Worth - 1f);
        }

        return change + CreatedByOwnEffects(c, magicScale, rareScale, lift);
    }

    /// <summary>
    /// OwnOfChoice as the payout pays it: the change on each of the recipe's wave parts and each "per" effect's worth
    /// times what the shares in force on these waves multiply that class by, where OwnOfChoice counts them at face value.
    /// The waves the combination has over its recipe stay at face value, as the payout leaves them. For Best.
    ///
    /// **Ranked at face value, own effects lost to rewards on any site with relics.** Every share in force pays on the
    /// monsters an own effect adds - on a Frigid Bluffs site (2026-10-07) relics and runes multiplied a remnant's
    /// monsters by 4.1 and its rares by 16 - so an option holding Oath was ranked 638 below Uhtred's Saga and paid
    /// 1,152 above it.
    ///
    /// Leaves out the combination's own local shares and a passed-in lift, both of which the payout puts in the product.
    /// See Planner.SharesInForceAt and Planner.PaidProductOfMask.
    /// </summary>
    public float OwnPaidOfChoice(int c, float magicScale, float rareScale, double lift, PlanEnvironment env,
        float[] shares, int groups, int width)
    {
        var change = 0f;

        if (OwnEffectsOfChoices is { } all && c >= 0 && c < all.Length && all[c] != null)
        {
            var (rareMask, magicMask) = (0L, 0L);

            foreach (var (mask, part, _) in Parts ?? [])
            {
                var rare = (mask & (1L << Tags.Rares)) != 0L;
                var magic = !rare && (mask & (1L << Tags.Magics)) != 0L;

                if (rare && rareMask == 0L)
                    rareMask = mask;

                if (magic && magicMask == 0L)
                    magicMask = mask;

                var (worth, _) = OwnFactorsOfPart(c, mask, lift);

                if (part > 0f && worth != 1f)
                    change += part * (rare ? rareScale : magic ? magicScale : 1f) * (worth - 1f) *
                              (float)Planner.PaidProductOfMask(env, shares, groups, width, mask);
            }

            if (MagicAndRareWavesOfChoices is { } waves && c < waves.Length)
            {
                if (rareMask != 0L)
                    change += waves[c].Rare * rareScale * (OwnFactorsOfPart(c, rareMask, lift).Worth - 1f);

                if (magicMask != 0L)
                    change += waves[c].Magic * magicScale * (OwnFactorsOfPart(c, magicMask, lift).Worth - 1f);
            }
        }

        if (CreatedOfChoices is { } created && c >= 0 && c < created.Length && created[c] is { } entries)
        {
            foreach (var entry in entries)
                change += CreatedOfEntry(c, entry, magicScale, rareScale, lift) *
                          (float)Planner.PaidProductOfMask(env, shares, groups, width, entry.Mask);
        }

        return change;
    }

    /// <summary>
    /// What the monsters combination c's "per" effects add to this remnant's waves are worth at face value, all of
    /// them. Added to content by OwnOfChoice; the payout lifts each by the shares reaching its row. See CreatedOfEntry.
    /// </summary>
    /// <param name="lift">A lift passed in from earlier links, taken as a bonus is. See OwnFactorsOfPart.</param>
    public float CreatedByOwnEffects(int c, float magicScale, float rareScale, double lift)
    {
        if (CreatedOfChoices is not { } all || c < 0 || c >= all.Length || all[c] is not { } created)
            return 0f;

        var worth = 0f;

        foreach (var entry in created)
            worth += CreatedOfEntry(c, entry, magicScale, rareScale, lift);

        return worth;
    }

    /// <summary>
    /// What one "per" effect of combination c adds, at face value: so many of its row for each monster of the counted
    /// tag on this remnant's waves, as they are scored - Gaining Traction and the other own effects' counts included -
    /// at the row's worth apiece.
    ///
    /// **Built for the Death rune's merge** (2026-10-05): slain normal and magic monsters merge into a Runemarked rare,
    /// so what drives it is how many normals and magics there are, and what it adds is a row of its own. See
    /// TableGrammar.Effect.Per and NOTES, "Death merges".
    /// </summary>
    public float CreatedOfEntry(int c, (int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment) entry, float magicScale, float rareScale,
        double lift) =>
        entry.WorthEach > 0f ? CreatedCountOfEntry(c, entry, magicScale, rareScale, lift) * entry.WorthEach : 0f;

    /// <summary>
    /// How many monsters of its row one "per" effect of combination c adds: CreatedOfEntry before the worth apiece. The
    /// prediction the census records reads this, so it counts what the scoring pays. See Planner.PredictedWavesOf.
    /// </summary>
    public float CreatedCountOfEntry(int c, (int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment) entry, float magicScale, float rareScale,
        double lift)
    {
        var (source, rate, waveShare, _, _, _, empowerment) = entry;

        if (source < 0 || rate <= 0f || Parts is not { Length: > 0 } parts)
            return 0f;

        var counted = 0f;
        var (magicWorth, magicMany) = (0f, 0f);

        foreach (var (mask, part, many) in parts)
        {
            var rare = (mask & (1L << Tags.Rares)) != 0L;
            var magic = !rare && (mask & (1L << Tags.Magics)) != 0L;

            if ((mask & (1L << source)) != 0L)
                counted += many * (rare ? rareScale : magic ? magicScale : 1f) * OwnFactorsOfPart(c, mask, lift).Many;

            if (magic)
            {
                magicWorth += part;
                magicMany += many;
            }
        }

        // The magic waves this combination has over the remnant's own recipe, as monsters. See
        // MagicAndRareWavesOfChoices, which keeps their worth.
        if (source == Tags.Magics && MagicAndRareWavesOfChoices is { } waves && c < waves.Length &&
            magicWorth > 0f && magicMany > 0f)
            counted += waves[c].Magic * magicScale / (magicWorth / magicMany);

        if (counted <= 0f)
            return 0f;

        var reach = waveShare < 1f ? lift : lift * ShareOfWavesPassedLift;

        // **Plain and empowered rates weighed by the waves a passed-in Power reaches**, rather than one rate lifted: the
        // share is the lift's reach, at most every wave. See Weighing.CreatedOfRunes.
        if (empowerment != 0)
        {
            var empoweredShare = Math.Clamp(lift, 0d, 1d) * (waveShare < 1f ? 1d : ShareOfWavesPassedLift);

            return (float)(rate * (empowerment == 2 ? empoweredShare : 1d - empoweredShare)) * counted;
        }

        return (float)(rate * (1d + reach)) * counted;
    }

    /// <summary>
    /// The lift combination c holds in any of its slots, per amplifier class as fractions - 1 on the rune class for a
    /// Power rune - or null for none.
    ///
    /// **A held Power empowers what its remnant propagates, from whichever slot.** Measured across the recordings
    /// (2026-10-01): remnants holding Power in an ordinary slot sent every propagated rune down the chain in its
    /// empowered form - Death, Soul, Vision, Celestial, Adaptive, Moon, Toxic and Stone, over 0 plain monsters to
    /// several hundred empowered - where the model lifted only that remnant's own waves. See Planner.Settle's booking
    /// and Best, which double the beneficial runes such a combination propagates.
    /// </summary>
    public float[] HeldLiftsOfChoice(int c) =>
        HeldLiftOfChoices is { } all && c >= 0 && c < all.Length ? all[c] : null;

    /// <summary>
    /// The share of this remnant's own waves rune id, propagated by combination c, is on: one for a rune in either of
    /// the first two slots or with nothing known, less for a later slot - slot k of n is on n - k + 1 of n waves. A
    /// propagating rune reaches every wave of the remnants after its own, but its own remnant's waves only from its
    /// slot onward, as an ordinary slot's does. See Propagation.WaveShareOfSlot and Planner.Settle's booking.
    /// </summary>
    public float WaveShareOfCarried(int c, string id)
    {
        if (CarriedWaveSharesOfChoices is not { } all || c < 0 || c >= all.Length || all[c] is not { } shares)
            return 1f;

        // A split-off share sits in its rune's slot. See Weighing.SplitShareKeyOf.
        var of = Weighing.RuneOfKey(id);

        foreach (var (rune, share) in shares)
        {
            if (string.Equals(rune, of, StringComparison.OrdinalIgnoreCase))
                return share;
        }

        return 1f;
    }

    /// <summary>
    /// The weight of this remnant's own waves under a tag, with Gaining Traction's extra at the given scales: what a
    /// booking from this remnant reaches on its own waves. Monsters for no tag; nought for a tag no wave carries.
    /// See WaveShareOfCarried.
    /// </summary>
    public float OwnWavesOfTag(int tag, float magicScale, float rareScale)
    {
        var waves = MagicAndRareWaves;

        if (tag < 0 || tag == Tags.Monsters)
            return MonstersUnearthed + (magicScale - 1f) * waves.MagicMonsters + (rareScale - 1f) * waves.RareMonsters;

        if (tag == Tags.Rares)
            return waves.RareMonsters * rareScale;

        if (tag == Tags.Magics)
            return waves.MagicMonsters * magicScale;

        if (tag == Tags.Normals)
            return MathF.Max(0f, MonstersUnearthed - waves.MagicMonsters - waves.RareMonsters);

        return 0f;
    }

    /// <summary>
    /// What this remnant's normal wave monsters are worth, and how much of that is monsters for the pools runes pay on:
    /// its Parts carrying neither the rare nor the magic tag. Nought on anything but a remnant. See MagicAndRareWaves.
    /// </summary>
    public (float Normal, float NormalMonsters) NormalWaves
    {
        get
        {
            var (normal, monsters) = (0f, 0f);

            if (Kind == TargetKind.Remnant)
            {
                foreach (var (mask, part, _) in Parts ?? [])
                {
                    if (part <= 0f || (mask & MagicOrRare) != 0L)
                        continue;

                    normal += part;
                    monsters += (mask & (1L << Tags.Monsters)) != 0L ? part : 0f;
                }
            }

            return (normal, monsters);
        }
    }

    /// <summary>The rare and magic tags together, as a mask. See MagicAndRareWaves.</summary>
    internal static readonly long MagicOrRare = (1L << Tags.Rares) | (1L << Tags.Magics);

    /// <summary>
    /// The same choice on the greedy pass's cruder terms.
    ///
    /// Greedy has not chosen the rest of the chain yet, so it values propagation by the explosives
    /// still in hand rather than by monster weight - a different scale, deliberately optimistic,
    /// and the exact objective is what decides whether the chain it built was any good. The choice
    /// still has to be made on the same terms it is being scored on, or greedy reaches for a
    /// remnant on the strength of a reward and a rune it cannot both have.
    /// </summary>
    /// <summary>Everything a scoped carry passes on, added up, for the estimates that cannot
    /// afford a per-tag answer.</summary>
    public float Spreading
    {
        get
        {
            if (Spread == null)
                return 0f;

            var total = 0f;

            foreach (var (_, _, percent, _) in Spread)
                total += percent;

            return total;
        }
    }

    public float Rough(float downstream)
    {
        // Greedy has no per-tag answer to hand and cannot afford to work one out, so a scoped carry
        // is estimated against the same downstream figure the flat one uses. That over-values a
        // carry scoped to chests on a site of monsters and under-values the reverse - which is what
        // an estimate is for. The exact objective decides whether the chain it opened was any good.
        if (Choices == null || Choices.Length == 0)
            return (Carries + Spreading) * downstream;

        var most = float.NegativeInfinity;

        // The local term is counted once, at face value, because greedy is measuring in explosives
        // rather than monsters and one explosive is exactly what a local rune reaches.
        //
        // **And the combination's scoped runes, which this was leaving out altogether.** The branch
        // above counts an object's own scoped carries through Spreading; this one counted only the
        // flat half, so a remnant whose best combination reaches chests looked worth nothing extra
        // to open with. The two halves of the same estimate disagreed.
        // **And what its runes' "own" effects do to its waves**, at face value, as greedy has no chain position to
        // scale them by. See OwnOfChoice.
        for (var c = 0; c < Choices.Length; c++)
        {
            var choice = Choices[c];
            var reach = 0f;

            foreach (var (_, _, percent, _) in choice.Spread ?? [])
                reach += percent;

            most = MathF.Max(most,
                choice.Reward + (choice.Carries + reach) * downstream + choice.Local + OwnOfChoice(c, 1f, 1f));
        }

        return most;
    }

    /// <summary>Whether this is worth considering at all: wanted, carrying, or offering something.</summary>
    /// <summary>
    /// Whether this target belongs in the coverage index at all.
    ///
    /// Anything with a weight, INCLUDING a negative one. An avoided marker has to be indexed or
    /// nothing would ever notice a blast had caught it, and the penalty for catching it would never
    /// be charged - see Insisted. Nought is the only weight that means "not part of the question".
    /// </summary>
    public bool Wanted => Weight != 0f || Carries > 0f || Choices is { Length: > 0 } ||
                          Spread is { Length: > 0 };

    /// <summary>Whether the player has said to leave this alone. See Insisted.</summary>
    public bool Shunned => Weight < 0f;

    /// <summary>
    /// How many must takes holding this one counts as: one, except the marker taken last in a search that holds
    /// remnants the player did not mark, which counts two, so that breaking it costs more than leaving out one of the
    /// remnants added. Settle and the incremental tally count it; PlanEnvironment.Musts is the sum over the must takes.
    /// See Repair.HeldEnvironment.
    /// </summary>
    public int MustWeight { get; init; } = 1;

    /// <summary>
    /// The best combination for this remnant, given what the chain catches from here onwards.
    ///
    /// Returns the reward weight to add and the percentage to propagate, as one pair, so the two
    /// halves can never come from different combinations again.
    /// </summary>
    /// <param name="downstream">
    /// Monster weight this link and every later one unearths - what a PROPAGATING rune reaches.
    /// </param>
    /// <param name="local">
    /// Monster weight this link alone unearths - what every other rune in the combination reaches.
    ///
    /// A remnant's runes are modifiers on the monsters its own explosive turns up; the propagating
    /// slot is the one that also follows the chain onwards. So both terms are real, they simply
    /// reach different distances, and a combination is chosen on the sum of them.
    /// </param>
    /// <param name="chose">
    /// Which entry of Choices was taken, so a detailed pass can publish the decision rather than
    /// leaving it to be inferred from the rune counts afterwards. See Planner.Picked.
    /// </param>
    public (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread) Best(float downstream, float local,
        PlanEnvironment env, float[][] sums, int at, int distinct, out int chose, float magicScale = 1f,
        float rareScale = 1f, double passedLift = 0d, float[] sharesInForce = null, int groups = 0, int width = 0,
        (int Key, int Link)[] laterSends = null, int laterCount = 0, float[] after = null)
    {
        chose = -1;

        // Null, NOT this target's own Spread. Settle spends that separately from
        // env.Targets[i].Spread, so returning it here would have both loops walking the same pairs
        // and paying them twice. The choice's Spread is what its RUNES reach, which an object with
        // no combinations has none of.
        if (Choices == null || Choices.Length == 0)
            return (0f, Carries, 0f, null, null, null);

        var best = Choices[0];
        var most = float.NegativeInfinity;

        chose = 0;

        for (var c = 0; c < Choices.Length; c++)
        {
            var choice = Choices[c];

            // **Ranked against what this chain actually unearths per tag**, not against a single
            // downstream figure standing in for all of them. The per-tag sums are built before the
            // loop that calls this, so a combination whose runes reach chests is compared on the
            // chests this chain will dig up rather than on its monsters.
            //
            // It costs a scan of env.Scoped per scoped tag - nought to three entries - and nothing
            // at all when no rune anywhere has a scope, since Spread is then null. Where the sums
            // are not to hand the old approximation stands in, which is the estimate paths only.
            var reach = 0d;

            // As paid where the shares in force are known: times the other groups on the tag's class. See
            // Planner.PaidFactorOfScopedEffect.
            double PaidFactor(string id, int tag, bool flat) =>
                sharesInForce == null || flat ? 1d : Planner.PaidFactorOfScopedEffect(env, sharesInForce, groups, width, id, tag);

            foreach (var (id, tag, percent, flat) in choice.Spread ?? [])
            {
                if (percent > 0f)
                    reach += percent * Planner.Reach(env, tag, sums, null, at, downstream) * PaidFactor(id, tag, flat);
            }

            // What the combination's scoped runes reach, before the lifts below add to it. See passedLift.
            var spreadReach = reach;

            // **Ranked on what the chain would actually gain, not on what the combination holds.**
            //
            // A rune an earlier link already sends here earns nothing when the chain is scored, so
            // paying for it in the comparison picks combinations the payout then refuses.
            //
            // **Confirmed by pinning the picks rather than by comparing plans.** Two plans scored
            // 1,023.2 and 886.8 and the discounted one was lower, which looked like a regression and
            // was not: those plans differed in more than their combinations, and the higher figure
            // came from a build whose waste counts could not see a banked duplicate. Overruling off
            // pins each remnant to a chosen combination, so the payout scores the same chain with only
            // the picks varying - and there the discounted choice wins. That is the comparison this
            // change rests on; the plan-against-plan one is not able to answer it.
            //
            // Both halves are discounted. distinct is nought on the estimate paths, where there is no
            // booked set to consult and the gross figure stands. See DiscountedForDuplicates.
            var carried = Planner.DiscountedForDuplicates(env, this, choice.Runes, choice.Carries, at, distinct);

            // As paid where the shares in force are known: each rune's factor, by its weight, over the monsters by type.
            // See Planner.PaidFactorOfCarriedEffect.
            var carriedFactor = 1d;

            if (sharesInForce != null && Planner.WeightsOfChosenRunes(this, choice.Runes) is { Length: > 0 } carriedRunes)
            {
                var (weighed, paidWeight) = (0d, 0d);

                foreach (var (id, weight) in carriedRunes)
                {
                    if (weight <= 0f)
                        continue;

                    weighed += weight;
                    paidWeight += weight * Planner.PaidFactorOfCarriedEffect(env, sharesInForce, groups, width, id, sums, at, downstream);
                }

                if (weighed > 0d)
                    carriedFactor = paidWeight / weighed;
            }

            // Less what a later remnant sends anyway. See Planner.LaterSentReach.
            var laterSent = Planner.LaterSentReach(env, this, choice.Runes, at, distinct, laterSends, laterCount, after);

            var held = Planner.DiscountedForDuplicates(env, this, choice.Locals, choice.Local, at, distinct);

            // **And what the lifts it holds add to what it propagates.** A held Power sends the beneficial runes it
            // carries down the chain empowered, so they count again by the lift; any class's held lift the same. See
            // HeldLiftsOfChoice.
            //
            // Not for a rune an earlier link already sends: monsters carry one copy of each rune, the earlier one,
            // and an earlier plain copy stays plain downstream however the later sender is lifted. Measured on 4
            // maps (2026-10-02): a rune sent plain and then again by a remnant holding Power reached the remnants
            // after it empowered on 1 monster of 170, and only where those remnants had Power themselves.
            var heldLifts = HeldLiftsOfChoice(c);

            if (heldLifts != null)
            {
                foreach (var (id, weight) in Planner.WeightsOfChosenRunes(this, choice.Runes) ?? [])
                {
                    var factor = Planner.HeldFactorOfEffect(env, id, heldLifts);

                    if (weight > 0f && factor > 1d && !Planner.IsRuneAlreadySentTo(env, this, id, at, distinct))
                        reach += (factor - 1d) * weight * downstream;
                }

                foreach (var (id, tag, percent, flat) in choice.Spread ?? [])
                {
                    var factor = Planner.HeldFactorOfEffect(env, id, heldLifts);

                    if (percent > 0f && factor > 1d && !Planner.IsRuneAlreadySentTo(env, this, id, at, distinct))
                        reach += (factor - 1d) * percent * Planner.Reach(env, tag, sums, null, at, downstream) * PaidFactor(id, tag, flat);
                }
            }

            // What the held lifts added, kept apart for the trail below.
            var heldLiftReach = reach - spreadReach;

            // **And what an amplifier it propagates lifts**: each class's lift on the shares already booked that the
            // class lifts, from this link on. Only for a rune that adds as well - a pure amplifier is weighed by its lift
            // as a carry, as Power always has been. See Planner.AmplifierWorthAt.
            foreach (var id in choice.Runes ?? [])
                reach += Planner.AmplifierWorthAt(env, id, heldLifts, at, sums, downstream, distinct);

            // **And a Power passed in from earlier links, as the payout counts it.** It lifts every rune this
            // combination sends down the chain and, from wave 2 on, the runes it keeps and the bonuses among their own
            // effects; not the reward. Ranked without it, a remnant receiving Power weighed its runes at half what the
            // payout then paid for them, against a reward the lift does not touch. Not for a combination holding a Power
            // itself, whose own is counted already and does not stack with it. See Settle's PowerPassedTo.
            var passed = PassedLiftOfChoice(env, heldLifts, passedLift);
            var onOwnWaves = passed * ShareOfWavesPassedLift;

            var traction = TractionOfChoice(c, magicScale, rareScale);

            // **As the payout pays it, where the shares in force are known.** See OwnPaidOfChoice.
            var own = sharesInForce != null
                ? OwnPaidOfChoice(c, magicScale, rareScale, passed, env, sharesInForce, groups, width)
                : OwnOfChoice(c, magicScale, rareScale, passed);
            var worth = choice.Reward + traction + own +
                        (float)(((carried * downstream - laterSent) * carriedFactor * (1d + passed) + held * local * (1d + onOwnWaves) +
                                 reach + spreadReach * passed) / 100d);

            // **What each option was ranked at, kept only when somebody is reading.** The winner
            // was published and the comparison thrown away, so a remnant taking an option worth
            // less than the one it would have been pinned to - measured at 1,653 less over one
            // chain - said nothing about why. These are the terms the choice was made on, which is
            // the only place that answer can come from.
            Planner.ChoiceRankingTrail?.Add(
                $"{(Named != null && c < Named.Length ? Named[c] : "?")}: reward {choice.Reward:N1}" +
                $" + carried {carried:N2}% x {downstream:N0}{(laterSent > 0d ? $" less {laterSent / 100d:N1} sent later anyway" : "")}" +
                $"{(carriedFactor != 1d ? $" x {carriedFactor:N2} as paid" : "")} + local {held:N2}% x {local:N0}" +
                $" + scoped {reach:N1} (spread {spreadReach:N1}, held lifts {heldLiftReach:N1}, amplifiers " +
                $"{reach - spreadReach - heldLiftReach:N1}, passed lift {passed:0.##}) + traction {traction:N1} + own {own:N1}" +
                (sharesInForce != null ? " (as paid)" : "") +
                $" = {worth:N1}");

            if (worth > most)
            {
                most = worth;
                best = choice;
                chose = c;
            }
        }

        return best;
    }
}

/// <param name="Origin">The last link in the chain, or the detonator. Never the player.</param>
/// <param name="Reach">How far the next link may be from the one before it.</param>
/// <param name="Blast">The blast radius. A target is caught at Blast plus its own radius.</param>
/// <param name="Apart">
/// How far apart two explosives have to be, in grid units. Zero means no limit.
/// </param>
/// <param name="Placed">
/// Explosives already down, which a replanned chain still has to keep clear of.
///
/// They are not in the chain - a replan starts from the last one placed and plans only what is
/// left - so without this a chain that doubled back would happily route through ground the first
/// half of it is already standing on.
/// </param>
/// <param name="CanReach">
/// Whether the ground between two links is clear, as opposed to whether a spot is placeable.
///
/// Two different questions and the second does not imply the first: both ends of a link can stand
/// on open ground with a rock in the middle. Null means every link is assumed clear, which is what
/// the planner did before it asked.
/// </param>
/// <summary>
/// Which edge points toward neighbours the shortlist is offered, and how they are spaced.
///
/// Carried on the environment rather than read from settings inside the search, because the search
/// runs on a background thread and the settings window is on another - a slider dragged mid-solve
/// would otherwise change the question halfway through answering it.
/// </summary>
internal sealed record SeedFamilies
{
    /// <summary>Whether heavy markers start edge points. See CandidateSpotSettings.HeavyEdgePoints.</summary>
    public bool HeavyEdgePoints { get; init; } = true;

    /// <summary>Whether rares start edge points. See CandidateSpotSettings.RareEdgePoints.</summary>
    public bool RareEdgePoints { get; init; } = true;

    /// <summary>The edge point step, in grid. See CandidateSpotSettings.EdgePointStepGrid.</summary>
    public float EdgePointStepGrid { get; init; } = 0f;
}

/// <summary>
/// How amplifier runes lift other runes' shares on one dig site, indexed by amplifier class - the tags in
/// Weighing.AmplifierTags, "rune" for Power and "spawner_rune" for a rune lifting the spawners.
///
/// A share an amplifier lifts is filed in a twin of its group, one twin per set of classes lifting it, and the payout
/// adds a group and its twins into one sum with each twin's part multiplied by its classes' factors. With nothing
/// lifted the twins are empty and the arithmetic is the plain group's. See Planner.Multiplied.
///
/// An amplifier can itself be lifted: Rebirth, tagged rune, has its lift multiplied by Power's. Its lift is
/// booked against its own effect number and accumulated per class down the chain. See AmplifiedByOfClass.
/// </summary>
internal sealed record Amplification(
    // How many classes there are: Weighing.AmplifierTags().Length when the site was built.
    int Classes,
    // By effect number: what it lifts each class by, as fractions, or null for none. See Weighing.LiftsOfRune.
    float[][] LiftsOfEffect,
    // By effect number: the classes lifting its share or its lift, as a mask. See Weighing.AmplifiedClassesOfRune.
    long[] MaskOfEffect,
    // By group: the classes lifting the shares filed in it - nought for a plain group.
    long[] MaskOfGroup,
    // By group: the plain group a twin adds into, or -1 for a plain group.
    int[] PlainOfGroup,
    // By group: the twins adding into this plain group, or null.
    int[][] TwinsOfGroup,
    // By plain group times (1 shifted by Classes) plus a mask: the twin for that set of classes, or -1.
    int[] TwinOfGroupAndMask,
    // By class: the classes lifting that class's own lift, as a mask.
    long[] AmplifiedByOfClass,
    // Which class is the one aimed at every rune - Power's - or -1 where none is. Own effects' bonuses are lifted by it.
    // See PlanTarget.OwnFactorsOfPart.
    int RuneClass = -1,
    // By group: how much of its classes' lift reaches the shares filed in it, 1 for all of it; null where every group
    // takes all of it. Below 1 for a rune whose row writes its share "plain" and "empowered". See LiftedFactorOfGroup.
    double[] ReachOfGroup = null)
{
    /// <summary>
    /// What the shares filed in group g are multiplied by, given the factor of each set of classes: its classes' factor,
    /// with only the reached part of the lift where ReachOfGroup says less than all - 1 + (factor - 1) x reach, so a
    /// share whose rune is lifted x1.2 by a Power that doubles others gets 1.2. See Weighing.PowerFactorOfRune.
    /// </summary>
    public double LiftedFactorOfGroup(int g, double[] factors)
    {
        var factor = factors[MaskOfGroup[g]];

        return ReachOfGroup is { } reach && g >= 0 && g < reach.Length ? 1d + (factor - 1d) * reach[g] : factor;
    }

    /// <summary>The group shares of this plain group lifted by these classes are filed in: the plain group itself for none.</summary>
    public int TwinOf(int plain, long mask)
    {
        if (mask == 0L || plain < 0)
            return plain;

        var at = plain * (1 << Classes) + (int)mask;

        return at >= 0 && at < TwinOfGroupAndMask.Length && TwinOfGroupAndMask[at] >= 0 ? TwinOfGroupAndMask[at] : plain;
    }

    /// <summary>
    /// Each class's lift with the lifts lifting it applied, into effective: a class's raw lift times one plus each
    /// lifting class's effective lift, in passes so a chain of two resolves. A cycle settles at the raw lifts' order.
    /// </summary>
    public void Effective(double[] raw, double[] effective)
    {
        for (var a = 0; a < Classes; a++)
            effective[a] = raw[a];

        for (var pass = 0; pass < Classes; pass++)
        {
            for (var a = 0; a < Classes; a++)
            {
                var by = a < AmplifiedByOfClass.Length ? AmplifiedByOfClass[a] : 0L;
                var lift = raw[a];

                for (var b = 0; b < Classes && by != 0L; b++)
                {
                    if ((by & (1L << b)) != 0L && b != a)
                        lift *= 1d + effective[b];
                }

                effective[a] = lift;
            }
        }
    }

    /// <summary>
    /// The factor each set of classes puts on a share they lift - the product of one plus each class's effective lift -
    /// indexed by mask, into factors, which must hold 1 shifted by Classes entries.
    /// </summary>
    public void FactorsOfMasks(double[] effective, double[] factors)
    {
        var masks = 1 << Classes;

        for (var m = 0; m < masks; m++)
        {
            var f = 1d;

            for (var a = 0; a < Classes; a++)
            {
                if ((m & (1 << a)) != 0)
                    f *= 1d + effective[a];
            }

            factors[m] = f;
        }
    }
}

internal sealed record PlanEnvironment(
    Vector2 Origin,
    float Reach,
    float Blast,
    int Explosives,
    IReadOnlyList<PlanTarget> Targets,
    Func<Vector2, bool> CanPlace,
    Func<Vector2, Vector2, Certainty> CanReach = null,
    //Whether aiming at a point actually lands the explosive on it, once the game's own wire has
    //been routed and clamped. Null falls back to the straight line. See Wire.Lands.
    Func<Vector2, Vector2, bool> Lands = null,
    float Apart = 0f,
    IReadOnlyList<Vector2> Placed = null,
    (string Id, int Tag, float Percent, int Group, (int X, int Y) From)[] Secured = null,
    string Banked = "",
    SeedFamilies Seeding = null,
    Action<TimeSpan> Insist = null,
    //The smallest and largest run of links a destroy may take out. Carried here rather than read
    //from settings for the same reason the seeding rules are: a running search keeps what it started
    //under. See SolverSettings.TearMost and Repair's escalation.
    int TearLeast = 1,
    int TearMost = 4,
    //Every effect name in this site, numbered. See Settle's Book - the scoring loop compares
    //numbers rather than strings because of it.
    Dictionary<string, int> Effects = null,
    //Which group each numbered effect adds inside of. See Weighing.GroupKeyOfEffect and Multiplied.
    int[] Bands = null,
    //How many distinct groups this site has, never less than the one default pool.
    int GroupCount = 1,

    //How many markers the player insisted on, so a chain can be told how many it dropped without
    //walking the site to count them.
    int Musts = 0,
    //What dropping one costs: more than any chain here can be worth, so no amount of content buys
    //one out. See Planning.Ceiling.
    double Refused = 0d,

    // Read once per solve and carried, rather than reached for per round: the search runs off the
    // main thread and the settings tree is not its to touch. See SolverSettings.
    bool VaryReachFill = true,
    int ShortlistRich = 400,
    int ShortlistSpread = 200,
    float ShortlistSparse = 64f,
    double AcceptSlack = 0.02d,
    int OpeningShakes = 2,
    //How far below the pool's best a worker has to fall before it throws its chain away and builds
    //a fresh one. The setting reads "At a kick, restart if behind the pool by (%)" and this is that figure as a
    //fraction; the words are the label's, because the label is what somebody greps for.
    double RestartThreshold = 0.10d,
    int RestartShakes = 0,

    /// <summary>
    /// Remnants a placed explosive already caught, carried for the readout and nothing else.
    ///
    /// Last in the list and outside Targets on purpose: these never enter the coverage loop, are
    /// never credited, and cannot be paid twice. See the registration beside Locally.
    /// </summary>
    IReadOnlyList<PlanTarget> Shown = null,

    /// <summary>
    /// The group number given to effects that scale other effects, or -1 when the site has none.
    ///
    /// **Not a group, a factor.** Everything else in Bands adds inside its group and the groups
    /// multiply each other; an empowering rate belongs to neither arrangement - it multiplies the
    /// whole propagation payout and is worth nothing on its own. Held as a band number so the
    /// scoring loop can recognise one by integer compare rather than by name, the same reason
    /// Effects is numbered at all. See Weighing.Empowering.
    ///
    /// Minus one when nothing in the site empowers, which makes the whole mechanism inert: nothing
    /// routes into the accumulator, the factor stays at one, and the score is identical to the
    /// penny. That is the property worth keeping - a site with no Power rune must not notice this
    /// exists.
    /// </summary>
    int Empowering = -1,

    /// <summary>
    /// The names of the runes the explosives already down are propagating, FOR THE READOUT ONLY.
    ///
    /// **Never booked, and that is the point.** The payout already handles these correctly: they go in
    /// as plain rates at the first link, and a remnant's own duplicate of one is never paid because
    /// Owned computes the local term as a difference against a prefix that already contains them.
    /// Proved by measurement rather than by reading - undoing four placed links so the same runes
    /// arrive live instead of banked leaves the route's score identical to the penny, 7,317 either
    /// way, over the same content, propagation and travel.
    ///
    /// What that arithmetic cannot do is COUNT. The line under a remnant says how many runes land on
    /// its waves and how many of its own are wasted, and both walk the booked set - so with the
    /// prefix banked it read 11 (6+5-0) where the same chain planned reads 10 (6+5-1). The missing
    /// strike is a name the readout could not see.
    ///
    /// Held separately rather than booked, because booking them would let Owned strike the duplicate
    /// a second time and break a payout that is currently right.
    ///
    /// **Carries WHERE each came from, because a caught remnant is in two lists at once.** A remnant
    /// under an explosive already placed is in Shown, so the readout can draw its line, and in the
    /// banking, so its runes pay forward to the links still to come. Counting the banked set wholesale
    /// then added a remnant's own runes to itself: a six socket remnant at the head of a three link
    /// chain read 8 (6+3-1), inheriting three runes from nobody but itself.
    /// </summary>
    (string Id, (int X, int Y) From)[] BankedRunes = null,

    /// <summary>
    /// The fewest links a bridge may spend reaching for missed content, when the tail allows.
    ///
    /// See Reaching's spend - half the tail starves this operator on a short chain, and a bridge needs
    /// a minimum number of steps whatever the chain length.
    /// </summary>
    int BridgeLinks = 3,

    /// <summary>
    /// The reach model alone, with the game's own measurements NOT consulted first.
    ///
    /// **For the comparison that could not see anything.** CanReach answers a measured pair from the
    /// measurement, which is right for planning - the client's word beats any model - and fatal for
    /// checking a model against the client, because every cell with a verdict to compare against is
    /// one the cursor has been over, so the measurement is exactly what comes back. The disagreement
    /// layer asked CanReach and therefore compared the game's answer with itself: it could never
    /// report a disagreement, whichever reach model was selected.
    ///
    /// Nothing in the search uses this. It exists so Border can ask the model what IT thinks about
    /// ground the game has already ruled on. See Border.Learn.
    /// </summary>
    Func<Vector2, Vector2, Certainty> Modelled = null,

    /// <summary>
    /// The longest the opening may take before the search moves on with what it has, or nought to
    /// let it finish. See SolverSettings.OpeningMs.
    /// </summary>
    int OpeningMs = 0,

    /// <summary>
    /// Whether a simple site is worked out exactly before the search runs.
    ///
    /// Carried rather than read, for the same reason the rest of these are: the search is off the
    /// main thread and the settings tree is not its to touch. See Planner.Outright and
    /// SolverSettings.UseEnumeratedSolve.
    /// </summary>
    bool UseEnumeratedSolve = true,

    /// <summary>
    /// Which draw of the random numbers this solve is.
    ///
    /// **Every seed in the search is a constant plus the worker's own number, so two solves of one
    /// site are the same eight searches.** Measured: five cold presses landing on 9,928.0 to the
    /// decimal, spread nought, and five uncapped ones landing on 10,692.1 three times and 11,196 twice.
    /// That reproducibility is worth having and it means a batch of presses samples one trajectory
    /// rather than a distribution - so "does this configuration usually win" could not be asked.
    ///
    /// Added to the seeds by whoever varies it. Nought is the old behaviour exactly, so a run that does
    /// not set it is the run this plugin always did. Last in the list because everything before it is
    /// matched by position at the one place an environment is built.
    /// </summary>
    int Draw = 0,

    /// <summary>
    /// How many near-best spots a randomised opening chooses among at each link - GRASP's restricted
    /// candidate list. See SolverSettings.OpeningChoices, and Repair.Opening for where it is spent.
    /// </summary>
    int OpeningChoices = 5,

    /// <summary>
    /// Whether a chain's links may be re-ordered by reversing a run of them. See Repair.Reversed, and
    /// SolverSettings.ReverseRuns for the measurement that asked for it.
    /// </summary>
    bool ReverseRuns = true,

    /// <summary>
    /// Whether the pool's best chain is relinked with the others once every worker has finished. See
    /// Repair.Relinked, and SolverSettings.Relink.
    /// </summary>
    bool Relink = false,

    /// <summary>
    /// The index in Targets of the marker the player wants taken last, or -1: the chain's final explosive must be the
    /// one whose blast catches it. It is a must take, and Settle counts it held only when the final link catches it.
    /// See Insisted.IsTakenLast.
    /// </summary>
    int TakenLast = -1,

    /// <summary>
    /// The enumerated openings a worker may start from, best first, or null when none were worked out.
    ///
    /// **The same word as the class that builds them, deliberately.** Grep Openings and both the enumerator
    /// and the thing it hands the search come back, because they are one subject. See Openings.Generate and
    /// Repair.Opening, which is where a worker takes one.
    /// </summary>
    IReadOnlyList<List<Vector2>> Openings = null,

    /// <summary>
    /// Whole chains built from orders of the capturable remnants, best first, for the workers whose role opens on one,
    /// or null when none were built. See RemnantOrder and ThreadRoles.Opens.RemnantOrder.
    /// </summary>
    IReadOnlyList<List<Vector2>> RemnantOrderChains = null,

    /// <summary>
    /// Whether the workers opening on remnant orders search their own order's chain exhaustively before their usual
    /// moves. See DestroyAndRepairSettings.RemnantOrdersFirst.
    /// </summary>
    bool RemnantOrdersFirst = false,

    /// <summary>
    /// What each worker is for, parsed once per solve. Null leaves every worker at the defaults.
    ///
    /// Carried rather than read per worker for the same reason every other knob here is: a running search keeps
    /// what it started under. See SolverSettings.ThreadRoles and ThreadRoles.Read.
    /// </summary>
    IReadOnlyList<ThreadRoles.Role> Roles = null,

    /// <summary>
    /// How many rounds a worker may run before it stops, or nought to stop on the clock instead.
    ///
    /// A measurement mode: with a round budget the work per press is fixed, so a draw reproduces and two
    /// configurations can be compared. See SolverSettings.RoundsPerWorker.
    /// </summary>
    int RoundsPerWorker = 0,

    /// <summary>
    /// How long a worker may go without a record before it kicks, as a percentage of the improvement window, or
    /// nought to kick on the round count alone. See SolverSettings.StagnationKickPercent.
    /// </summary>
    int StagnationKickPercent = 0,

    /// <summary>
    /// How many rounds a worker may run without a record before it kicks, or nought to kick on the clock alone.
    /// See SolverSettings.StagnationKickRounds.
    /// </summary>
    int StagnationKickRounds = 500,

    /// <summary>
    /// The window the kick, keep-opening and refine-after are shares of, in milliseconds, when it
    /// differs from the solve's own - a continuous reroll pass runs far longer than an ordinary one and keeps
    /// the ordinary clock for these. Nought uses the solve's own window. See RerollSettings.ContinuousPassMs.
    /// </summary>
    int PressWindowMs = 0,


    /// <summary>
    /// How long a worker's current chain may go without improving before it restarts, or nought for never. See
    /// DestroyAndRepairSettings.StallRestartMs.
    /// </summary>
    int StallRestartMs = 0,

    /// <summary>
    /// Gaining Traction: the fraction more magic packs in a remnant's waves for each remnant already completed in the
    /// area, or nought without it. From the table's Gaining Traction row while DebugSettings.GainingTraction is on.
    /// See Weighing.GainingTractionRates and Planner.TractionScales.
    /// </summary>
    float MagicPacksPerRemnantCompleted = 0f,

    /// <summary>The same for rare packs, which also sets how far rare modifier chance rises. See MagicPacksPerRemnantCompleted.</summary>
    float RarePacksPerRemnantCompleted = 0f,

    /// <summary>
    /// The map's increased number of rare packs as a fraction - map and atlas, as the remnant wave row is scaled by -
    /// which Gaining Traction's increase adds to rather than multiplying. Nought without it, or from a layout saved
    /// before it was kept. See Planner.TractionScales and AtlasStats.MapMonsterIncreases.
    /// </summary>
    float RareIncreaseOfMap = 0f,

    /// <summary>The same for magic packs. See RareIncreaseOfMap.</summary>
    float MagicIncreaseOfMap = 0f,

    /// <summary>
    /// What one extra modifier adds to a rare's worth, as a fraction of it, for the modifier half of Gaining Traction.
    /// See Weighing.RareModifierShareOfRare and Planner.TractionScales.
    /// </summary>
    float RareModifierShareOfRare = 0f,

    /// <summary>
    /// Remnants completed anywhere in the map before this chain goes off, counted towards Gaining Traction before
    /// the chain's own. Only remnants the scan watched finish: one already spent when first seen is never taken on.
    /// </summary>
    int RemnantsCompletedInArea = 0,

    /// <summary>
    /// Spots every worker's shortlist takes whatever they score, from a debug setting. Null for none. See
    /// DebugSettings.ForcedShortlistSpots.
    /// </summary>
    IReadOnlyList<Vector2> ForcedShortlistSpots = null,


    /// <summary>
    /// For each marker an explosive already down has caught, by cell, the number of the first placed
    /// explosive that catches it, counted from one in the order the game holds them. Lets the readout credit a
    /// caught remnant only with the banked runes of links at or before its own. Null when nothing is down, or
    /// on an environment read back from a file. See BankedRunes and Planner.IsBankedRuneReaching.
    /// </summary>
    IReadOnlyDictionary<(int X, int Y), int> LinkOfCaught = null,

    /// <summary>
    /// How the amplifier runes in this site lift other runes' shares: per effect its lifts and the classes lifting it,
    /// per group its twins. Null when nothing in the site lifts anything, or on an environment read back from a file -
    /// then a lift booked into the Empowering group reaches every share, as it did before twins. See Amplification and
    /// Planner.Multiplied.
    /// </summary>
    Amplification Amplified = null,

    /// <summary>
    /// For each group, the increase the map already gives its stat, as a fraction, which the group's shares add to:
    /// a group's factor is (1 + base + shares) / (1 + base). Set for the increased number of rare and magic monsters
    /// from AtlasStats.MapMonsterIncreases when DebugSettings.MapMonsterIncreases is on; null otherwise, and on an
    /// environment read back from a file, when every group starts from nothing. See Planner.Multiplied.
    /// </summary>
    float[] IncreaseBaseOfGroup = null,

    /// <summary>
    /// The whole site to score in, when explosives are down: every marker, and the detonator as the start, so a chain
    /// is scored as the explosives down plus the links after them, the figure the score on screen shows. Null when
    /// nothing is down, and on itself. See Head and Planning.Build.
    ///
    /// **This environment answers where the next link may go; Whole answers what a chain is worth.** The content the
    /// explosives down already catch is taken out of this one, and what they pay was banked in its place - and the
    /// banking undercounted what those links do to the ones after them: on a Frigid Bluffs site (2026-10-04), twelve
    /// explosives down, the banked scoring ranked a tail worth 73,060 as a whole chain above one worth 74,487, the
    /// last link carrying 2,814 where the whole chain carries it 20,664. Every score taken on this environment is
    /// taken on Whole, over the explosives down and the chain. See Planner.Evaluate and Planner.Begin.
    /// </summary>
    PlanEnvironment Whole = null,

    /// <summary>The explosives down, first first, which a chain scored on Whole follows. See Whole.</summary>
    IReadOnlyList<Vector2> Head = null)
{
    /// <summary>What EmpowerableTwinOfGroup holds for a group that is itself a twin.</summary>
    public const int EmpowerableTwin = -2;

    /// <summary>
    /// The distinct tags some effect in this dig site is scoped to, and nothing else.
    ///
    /// **Empty on almost every site, which is the point of holding it.** Building one accumulator
    /// per tag in the vocabulary would be twenty seven arrays walked per chain evaluation to serve
    /// the nought or two that any site actually references. Worked out once here, at construction,
    /// so the scoring loop reads a length rather than a lookup - and when the length is nought it
    /// does not read anything at all.
    ///
    /// The monster tag is left out deliberately: it already has an accumulator, so a scope naming it
    /// reads that instead of building a duplicate. See Planner.Reach.
    ///
    /// Computed in an initialiser rather than lazily because several search threads share one
    /// environment, and a field filled on first use is a race with no symptom anybody would trace.
    /// </summary>
    public int[] Scoped { get; } = Scopes(Targets);

    /// <summary>
    /// Every tag the grouped payout has to tell apart: monsters, plus whatever is scoped.
    ///
    /// **Monsters first and always**, because the unscoped share - what a plain rune means - lands
    /// there, and because whether a thing is a monster decides whether a local rune reaches it.
    /// Everything after it is a tag some effect on this site names.
    ///
    /// The payout buckets weight by the SET of these a thing carries rather than by each one
    /// separately, which is the whole reason this list exists: two modifiers multiply or add
    /// depending on what they are, and answering that needs to know that one rare monster is a
    /// monster and a rare monster and modifiable at once. One bucket per subset, so the list is
    /// deliberately short - on most sites it is one entry and there are two buckets.
    /// </summary>
    public int[] Relevant { get; } = Relevance(Scopes(Targets));

    private static int[] Relevance(int[] scoped)
    {
        var found = new int[1 + (scoped?.Length ?? 0)];

        found[0] = Tags.Monsters;

        for (var i = 0; i < (scoped?.Length ?? 0); i++)
            found[i + 1] = scoped[i];

        // A cap, because the bucket count doubles with every entry. Nothing observed comes near it;
        // a site that did would lose the tags past the cap rather than allocate a million buckets.
        return found.Length <= 10 ? found : found[..10];
    }

    private static int[] Scopes(IReadOnlyList<PlanTarget> targets)
    {
        if (targets == null)
            return System.Array.Empty<int>();

        List<int> found = null;

        void Take((string Id, int Tag, float Percent, bool Flat)[] spread)
        {
            foreach (var (_, tag, _, _) in spread ?? [])
            {
                if (tag == Tags.Monsters)
                    continue;

                found ??= new List<int>(2);

                if (!found.Contains(tag))
                    found.Add(tag);
            }
        }

        foreach (var target in targets)
        {
            if (target == null)
                continue;

            // What the object itself passes on...
            Take(target.Spread);

            // ...and what any combination it could offer would put in play. Every combination, not
            // the one that will be chosen: which is chosen depends on the chain, and the
            // accumulators have to exist before a chain is scored.
            foreach (var choice in target.Choices ?? [])
            {
                foreach (var (_, tag, _, _) in choice.Spread ?? [])
                {
                    if (tag == Tags.Monsters)
                        continue;

                    found ??= new List<int>(2);

                    if (!found.Contains(tag))
                        found.Add(tag);
                }
            }
        }

        return found?.ToArray() ?? System.Array.Empty<int>();
    }
}

/// <param name="Note">
/// Why the plan is the shape it is, in the words the prompt says out loud.
///
/// An empty plan has three quite different causes - no spot the terrain check will accept, no spot
/// within reach of the chain origin, and nothing reachable that is worth anything - and from the
/// outside all three look identical. Saying which is the difference between a bug report and a
/// setting to change.
/// </param>
/// <param name="Catches">
/// What each link was chosen FOR: the grid positions of the content it is counted as covering.
///
/// Index-parallel with Points, and the whole reason the plan is worth keeping rather than
/// re-deriving. Placement verifies exactly this set against the game's own highlighting, so the
/// question asked before a click is "is the game catching what this spot was picked for" rather
/// than "does my geometry still agree with itself". The second question is the one that produced
/// phantom expectations no amount of aiming could satisfy.
/// </param>
/// <param name="Plain">
/// What the chain is worth to the player: the same total with insistence taken back out.
///
/// **Carried rather than re-derived, because every caller that re-derived it got a different
/// answer.** Weight is the search's number and it is the right one for deciding which of two chains
/// wins; it is the wrong one for any column a person reads, because most of it can be a single
/// marker somebody marked or a reward threshold somebody set. See Verdict.Plain.
/// </param>
/// <param name="Missed">How many insisted markers this chain drops. See Verdict.Missed.</param>
internal sealed record Plan(List<Vector2> Points, double Weight, int Covered, string Note = "",
    List<Vector2[]> Catches = null, double Plain = 0d, int Missed = 0)
{
    public static readonly Plan Empty = new(new List<Vector2>(), 0d, 0);

    /// <summary>What link <paramref name="index"/> was chosen for, or nothing when unrecorded.</summary>
    public Vector2[] CaughtBy(int index) =>
        Catches != null && index >= 0 && index < Catches.Count ? Catches[index] : [];
}

/// <summary>
/// What a chain is worth, with the objective taken apart.
///
/// Split into its three terms because the interesting question about a disappointing plan is WHICH
/// term is wrong. A chain that scores lower than one a player laid by hand means the search is at
/// fault. A chain that scores higher and still looks worse means the objective is at fault - the
/// weights or the price put on propagation - and no amount of searching will fix that. One number
/// cannot tell those apart; three can, with the distance walked alongside them for context.
/// </summary>
/// <remarks>
/// A class, and measured as the better of the two.
///
/// It looked like an obvious struct - four numbers and a list, returned half a million times a
/// search and kept by nobody, so five hundred thousand allocations for nothing. Made one, it ran
/// eighteen percent slower: a forty byte struct is copied at every return and every read, while a
/// short-lived object is a pointer bump to allocate and costs nothing to collect. Left as it is, and
/// written down so the same reasoning does not get made twice.
/// </remarks>
/// <param name="Each">
/// The same two terms per link, as numbers rather than as prose.
///
/// Steps has carried this since the score card existed, formatted into sentences - which is right
/// for a file somebody reads and useless for anything that wants the figure. The overlay writes what
/// a blast is worth in the middle of its circle, and re-deriving that from the geometry would be a
/// second opinion about the same blast rather than the plan's own answer.
///
/// Filled only when the detail is asked for, like Steps, because the search scores millions of
/// chains and none of those want it.
/// </param>
/// <param name="Bonus">
/// How much of Content is must-have bonus rather than anything the site pays.
///
/// **A marker the chain is made to reach is weighted above the whole site put together**, which is
/// what makes the search build the chain around it - and it means the score is mostly that number
/// the moment anything is required. A Prairie site read 15,068 of which 7,834 was one remnant's
/// bonus; mark three more with the must-take key and the total triples while the plan is unchanged.
///
/// The search wants Total, bonus and all, because the bonus is how the insistence is expressed. Any
/// figure a person reads wants Plain. Carried as its own term rather than by scoring twice, so the
/// two can never disagree about the same chain.
/// </param>
/// <param name="Missed">
/// How many of the markers the player insisted on this chain does not take.
///
/// Anything above nought is an invalid chain. It is still scored, still drawn and still comparable -
/// a plan that drops one is what the player gets to see when nothing can take it, and "no plan" says
/// less than "here is the best there is, and it misses two of your three".
/// </param>
/// <param name="Held">
/// How many of them it does take - the same fact from the other side, and the one the score is built
/// from.
///
/// **Credited rather than charged, and that is not cosmetic.** Refusing by subtracting the price of
/// every miss is the same ordering and puts every chain on a site with marks below zero, where the
/// search opens with an incumbent of nought and a comparison of "better than what I have" - so
/// nothing would ever be kept and the plan would come back empty. Adding the same term for what a
/// chain HOLDS differs only by a constant across one solve, which cancels in every comparison, and
/// leaves the numbers where every caller already expects to find them.
/// </param>
internal sealed record Verdict(double Content, double Propagation, double Walked, int Covered,
    List<string> Steps, List<(double Content, double Carried)> Each = null,
    int Missed = 0, double Refused = 0d, int Held = 0)
{
    /// <summary>
    /// What the chain is worth to the player: what the site pays for what it catches.
    ///
    /// Comparable between sites and between solves, which Total is not - Total moves by tens of
    /// thousands when a marker is marked or unmarked and by nothing at all on the ground.
    ///
    /// **This used to subtract a Bonus term**, because insistence was added into the weight of the
    /// marker it applied to and had to be taken back out of every number a person reads. It is no
    /// longer in Content at all: it is Held * Refused, added at the other end, in Total only. So
    /// there is nothing to undo and no second copy of the arithmetic to disagree with this one.
    /// </summary>
    public double Plain => Content + Propagation;

    /// <summary>
    /// Content plus propagation. Distance is reported beside it and costs nothing.
    ///
    /// There was a travel term here, subtracting a per-grid-unit price from the score. It went
    /// because the constraint already does its job: every link has to be within placement reach of
    /// the one before, so a chain cannot sprawl however much the objective ignores distance - and
    /// a price on walking that cannot prevent anything is a price that only ever talks the planner
    /// out of content it could have had.
    ///
    /// Walked is still measured, because how far a chain runs is worth seeing on a score card even
    /// when nothing is charged for it.
    ///
    /// **A consequence worth keeping: the objective does not depend on the origin.** Walked is the
    /// only term that ever read it, and Walked is no longer in the total - so a chain scores the
    /// same wherever it is thrown from. That is what lets the beam score a SUFFIX on its own and
    /// treat the number as final, which is the whole basis of building the chain backwards. Put a
    /// term back that reads Origin and the beam's partial scores stop meaning anything.
    /// </summary>
    /// <summary>
    /// Content plus propagation, and above both of them whether the chain is allowed at all.
    ///
    /// Refused is larger than anything the rest of this can reach - see Planning.Ceiling - so the
    /// comparison is lexicographic by construction: a chain taking one more required marker beats
    /// any chain taking fewer, whatever either is worth, and among chains holding the same number
    /// the site's own arithmetic decides. That is what "a chain without it is invalid" means in a
    /// search that has to keep ordering the chains it rejects.
    /// </summary>
    public double Total => Content + Propagation + Held * Refused;
}

/// <summary>
/// Where to put the explosives.
///
/// The shape of the problem: a chain of N points, each within reach of the one before, the first
/// within reach of the detonator, maximising the weight of the content the blasts cover. It is a
/// max-coverage problem with a connectivity constraint, which is NP-hard in general and perfectly
/// tractable at the size a dig site actually is - five or six explosives against a hundred markers.
///
/// **Candidates come from the content, not from the whole map.** The obvious approach is to sample
/// positions at random and evolve them, which is what the PoE 1 planner this borrows its shape from
/// does; but there is no reason to ever consider a position that is not touching something. Every
/// useful blast centre either sits on a target or sits where two targets' catch circles overlap, so
/// those are the only positions generated. That turns a continuous search into a few hundred
/// discrete options and makes a greedy pass genuinely good rather than a starting point.
///
/// Everything is in grid units, on whole numbers, because that is the lattice the game snaps
/// placement to - planning to three decimals would be optimising coordinates that get rounded away.
/// </summary>
internal static class Planner
{

    /// <summary>
    /// How many of the best candidates a construction chooses between at each step.
    ///
    /// One would be the plain greedy answer every time and every restart identical. A handful is
    /// enough to send the chain off in a different direction without making it a random walk -
    /// each link is still one of the best few available, just not always the best.
    ///
    /// Shared with the reach operator's fill, which was the one construction in the plugin still
    /// taking the plain greedy answer. See Repair.Reaching.
    /// </summary>
    internal const int Among = 4;

    /// <summary>
    /// What each operator earned in the last search, for the dump.
    ///
    /// Written because an operator that does nothing looks exactly like an operator that does
    /// something, from the outside. Order was added to exploit a measured structure - that the best
    /// chains put their remnants early and their monsters late - and whether it ever fires is not
    /// something to infer from the score, which moves for a dozen reasons at once.
    /// </summary>
    public static string Operators { get; private set; } = "has not run";

    /// <summary>
    /// How many positions the last search had to choose from.
    ///
    /// The router's natural ceiling, since every source it can be asked to flood from is one of
    /// these. Published so the readout can say "all of it" rather than leaving a number that has
    /// stopped climbing to be interpreted.
    /// </summary>
    public static int Positions { get; set; }

    /// <summary>
    /// How many edge points are drawn, set when they are worked out. Nought draws nothing.
    /// </summary>
    public static int Drawn { get; set; }

    /// <summary>
    /// Whether the edge points button has asked for the edge points and the next tick has not yet worked them out.
    ///
    /// Working them out needs an environment - the markers, the reach, the ground - and the settings window has
    /// none of that in scope. So the button leaves the request here and the next tick answers it, which is the same
    /// arrangement the cache button uses. See ComputeEdgePointsForDrawing.
    /// </summary>
    public static bool EdgePointsPending { get; set; }

    /// <summary>
    /// The edge points as last worked out for drawing, one entry per point: its label, the point as the only cell
    /// of the second list, and an empty third list. See EdgePointsTowardNeighbours and Overlay.Shapes.
    ///
    /// Kept apart from the search so that drawing the points cannot change what the search does, and so a solve
    /// running in the background cannot rewrite what is on screen halfway through a frame.
    /// </summary>
    public static IReadOnlyList<(string Name, List<Vector2> Cells, List<Vector2> All)> Shapes
    {
        get;
        private set;
    } = new List<(string, List<Vector2>, List<Vector2>)>();

    /// <summary>
    /// Works out the edge points toward neighbours for the overlay and the dump, from the environment the planner
    /// would solve now, with how many points each edge point step from nought to ten would give on this site. See
    /// EdgePointsTowardNeighbours.
    /// </summary>
    public static void ComputeEdgePointsForDrawing(PlanEnvironment env)
    {
        if (env == null)
        {
            Shapes = new List<(string, List<Vector2>, List<Vector2>)>();
            Drawn = 0;

            return;
        }

        Shapes = EdgePointsTowardNeighbours(env, out var said, out var seeds, out var brightness, out var labels);
        EdgePointMarkerNames = labels;

        // How many points each step from nought to ten would give on this site, for choosing one. See
        // CandidateSpotSettings.EdgePointStepGrid.
        var perStep = new List<string>();

        if (env.Seeding is { } seeding)
        {
            for (var step = 0; step <= 10; step++)
            {
                var stepped = env with { Seeding = seeding with { EdgePointStepGrid = step } };

                perStep.Add($"{step}: {EdgePointsTowardNeighbours(stepped, out _, out _, out _, out _).Count}");
            }
        }

        EdgePointsSaid = said + (perStep.Count > 0
            ? $"{Environment.NewLine}  points at each step (grid): {string.Join(", ", perStep)}"
            : "");
        EdgePointColourSeeds = seeds;
        EdgePointBrightness = brightness;
        Drawn = Shapes.Count;
    }

    /// <summary>
    /// The cells of the edge points toward neighbours for one environment, worked out once and shared by every
    /// worker's shortlist. Empty when neither edge point switch is on. See Repair.Shortlist.
    ///
    /// Keyed on the environment by reference, which is exact: it is rebuilt per solve, never mutated during one,
    /// and carries the edge point settings.
    ///
    /// **Built by one thread while the others wait**, through a Lazy. ConditionalWeakTable.GetValue runs its factory
    /// on every thread that asks before the first stores, and every worker asks at once: measured on one Grand site
    /// (2026-09-30), 48 builds over 6 solves at about 305ms and 58MB each, the same points eight times a press.
    /// </summary>
    internal static List<Vector2> EdgePointCellsOfEnvironment(PlanEnvironment env) =>
        _edgePointCells.GetValue(env, static e => new Lazy<List<Vector2>>(() =>
            e.Seeding is { HeavyEdgePoints: true } or { RareEdgePoints: true }
                ? EdgePointsTowardNeighbours(e, out _, out _, out _, out _).ConvertAll(x => x.Cells[0])
                : new List<Vector2>(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanEnvironment,
        Lazy<List<Vector2>>> _edgePointCells = new();

    /// <summary>Which markers a blast here catches, as their indices in the target list, for comparing two spots.</summary>
    private static string ContentsCaughtAt(PlanEnvironment env, Vector2 at)
    {
        var text = new StringBuilder();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (Catches(env, at, env.Targets[i]))
                text.Append(i).Append(',');
        }

        return text.ToString();
    }

    /// <summary>
    /// For each heavy marker, and each other heavy marker or rare a next bomb could catch from its edge, the points on
    /// the first marker's catch ring that lead towards the second: one per distinct set of markers caught, the one
    /// nearest the second marker, only those no other point beats on both - catching at least as much weight and
    /// ending at least as near - and of those, going from the richest, only a point at least the edge point step
    /// nearer than the last one kept. Then across directions, a point is folded into another catching exactly the
    /// same markers that ends within the step of it towards every neighbour it leads to, and that one takes its
    /// neighbours.
    /// Rares are starting points as well as heavy markers when the settings ask for it. See
    /// CandidateSpotSettings.HeavyEdgePoints, RareEdgePoints and EdgePointStepGrid. See Openings.RingSpotsOfAnchor, which supplies the ring, and
    /// Openings.BestTowardEachNextArea, which makes the same trade between content and direction for a link of a
    /// chain.
    ///
    /// One ring per cell, however many pairs keep it, labelled with the heavy markers it catches and the
    /// neighbours it leads towards, as "R1+R3 > E2 L4". Coloured by the heavy markers it catches, from their
    /// positions, so a combination keeps its colour when the names renumber, and dimmed by how much of its origin's
    /// richest spot it catches. See EdgePointColourSeeds and EdgePointBrightness.
    ///
    /// A neighbour is within reach when the gap between the two catch distances is no more than a bomb's reach.
    /// Heavy is Openings.HeavyTargetsOfSite's measure. Near means the distance left before a bomb could catch the
    /// neighbour, nought for a point that already catches it.
    /// </summary>
    internal static List<(string Name, List<Vector2> Cells, List<Vector2> All)> EdgePointsTowardNeighbours(
        PlanEnvironment env, out string said, out List<int> seeds, out List<float> brightness,
        out List<(Vector2 Grid, string Name)> labels)
    {
        var found = new List<(string Name, List<Vector2> Cells, List<Vector2> All)>();
        var text = new StringBuilder();
        var placed = env.Placed ?? (IReadOnlyList<Vector2>)Array.Empty<Vector2>();
        // Nought links laid on top of the environment: its explosive count is already what is left in hand, so passing
        // the placed count took them off twice and valued a relic's effect over no links at all.
        var heavy = global::AutoExpedition.Openings.HeavyTargetsOfSite(env, 0)
            .Where(t => !t.Shunned).ToList();
        var neighbours = heavy.Concat(env.Targets.Where(t => t.Kind == TargetKind.Elite && !t.Shunned))
            .Distinct(ReferenceEqualityComparer.Instance).Cast<PlanTarget>().ToList();

        // Where edge points start from: heavy content and rares, each when its switch is on. Both are destinations
        // either way. A rare that passes the heavy test is a heavy starting point. See
        // CandidateSpotSettings.HeavyEdgePoints and RareEdgePoints.
        var anchors = (env.Seeding?.HeavyEdgePoints ?? true ? heavy : new List<PlanTarget>())
            .Concat(env.Seeding?.RareEdgePoints ?? true
                ? env.Targets.Where(t => t.Kind == TargetKind.Elite && !t.Shunned)
                : [])
            .Distinct(ReferenceEqualityComparer.Instance).Cast<PlanTarget>().ToList();

        // Short names, by kind and numbered in the target list's order, with the positions in the dump.
        //
        // **Numbered per letter, not per kind.** Kinds share letters - a chest and a caged encounter are both C - and
        // numbering each kind from one named two markers C1 on a Grand site (2026-09-30), which threw on the lookup
        // by name below and took down every worker's shortlist.
        // By the object, not its fields: a record hashes by every field, cached ones included, and a worker filling a
        // target's cache while this runs would move it out from under its key. See PlanTarget.MagicAndRareWaves.
        var names = new Dictionary<PlanTarget, string>(ReferenceEqualityComparer.Instance);
        var perLetter = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var target in env.Targets)
        {
            if (!neighbours.Any(n => ReferenceEquals(n, target)) || names.ContainsKey(target))
                continue;

            var letter = target.Kind switch
            {
                TargetKind.Remnant => "R",
                TargetKind.Elite => "E",
                TargetKind.Relic => "L",
                TargetKind.Chest => "C",
                _ => target.Kind.ToString()[..1],
            };

            perLetter[letter] = perLetter.GetValueOrDefault(letter) + 1;
            names[target] = letter + perLetter[letter];
        }

        var byCell = new Dictionary<Vector2, (double Caught, List<string> Toward)>();
        var step = env.Seeding?.EdgePointStepGrid ?? 0f;

        // Each cell's best share of the richest spot on the edge of a marker it was kept for. See
        // EdgePointBrightness.
        var shareOf = new Dictionary<Vector2, (double Share, string Of)>();
        var pairs = 0;

        foreach (var anchor in anchors)
        {
            var ring = new List<(Vector2 At, double Caught, double Toward)>();

            global::AutoExpedition.Openings.RingSpotsOfAnchor(env, anchor, env.Origin, placed, heavy, ring,
                askGround: false, anywhere: true);

            if (ring.Count == 0)
                continue;

            var caughtBy = ring.ToDictionary(x => x.At, x => ContentsCaughtAt(env, x.At));
            var heavyCaughtBy = ring.ToDictionary(x => x.At,
                x => string.Join(",", heavy.Where(t => Catches(env, x.At, t)).Select(t => names[t])));
            var anchorCatch = env.Blast + anchor.Radius;
            var richest = ring.Max(x => x.Caught);

            foreach (var neighbour in neighbours)
            {
                if (ReferenceEquals(neighbour, anchor))
                    continue;

                var neighbourCatch = env.Blast + neighbour.Radius;

                if (Vector2.Distance(anchor.Grid, neighbour.Grid) - anchorCatch - neighbourCatch > env.Reach)
                    continue;

                double Left(Vector2 at) => Math.Max(0d, Vector2.Distance(at, neighbour.Grid) - neighbourCatch);

                // The nearest point of each distinct set of markers caught.
                var nearest = new Dictionary<string, (Vector2 At, double Caught, double Left)>();

                foreach (var (at, weight, _) in ring)
                {
                    var left = Left(at);

                    if (!nearest.TryGetValue(caughtBy[at], out var had) || left < had.Left)
                        nearest[caughtBy[at]] = (at, weight, left);
                }

                // Only those no other catching the same heavy markers beats on both terms.
                //
                // **The same heavy markers, not any.** A point that catches a second remnant beside the anchor
                // catches more, and was allowed to cull the point that leaves it - but leaving it is a move: take one
                // remnant now and the other with a later link, after a relic whose effect it then carries. Measured on
                // one Grand site (2026-09-30): the chain that took one of two close remnants from (724,796), reaching a
                // relic from there, scored 59,429 against 55,757 for the same route taking both; the spot was culled
                // here, and with it on the shortlist by hand two presses of ten found the 59k chains.
                var points = nearest.Values.ToList();
                var front = points.Where(p => !points.Any(q =>
                        heavyCaughtBy[q.At] == heavyCaughtBy[p.At] &&
                        q.Caught >= p.Caught && q.Left <= p.Left && (q.Caught > p.Caught || q.Left < p.Left)))
                    .OrderByDescending(p => p.Caught)
                    .ToList();

                // And only a real step nearer each time. Round a ring the content falls and the distance shrinks a
                // little at almost every cell, so the front alone kept ten or more points a pair on one site.
                //
                // Within each set of heavy markers caught, for the reason the front is. See above.
                var kept = new List<(Vector2 At, double Caught, double Left)>();
                var lastOfSet = new Dictionary<string, double>();

                foreach (var point in front)
                {
                    var set = heavyCaughtBy[point.At];

                    if (lastOfSet.TryGetValue(set, out var last) && point.Left > last - step)
                        continue;

                    kept.Add(point);
                    lastOfSet[set] = point.Left;
                }

                if (kept.Count == 0)
                    continue;

                pairs++;

                foreach (var point in kept)
                {
                    if (!byCell.TryGetValue(point.At, out var entry))
                        byCell[point.At] = entry = (point.Caught, new List<string>());

                    if (!entry.Toward.Contains(names[neighbour]))
                        entry.Toward.Add(names[neighbour]);

                    var share = richest > 0d ? Math.Clamp(point.Caught / richest, 0d, 1d) : 1d;

                    if (!shareOf.TryGetValue(point.At, out var had) || share > had.Share)
                        shareOf[point.At] = (share, names[anchor]);
                }
            }
        }

        // **Folded across directions.** Each direction keeps the cell nearest its own neighbour, so two directions
        // wanting the same content kept two cells a grid or two apart: on one site (2026-09-30) (2186,1665) towards R2
        // and E5 and (2188,1665) towards L4, both catching 221, neither more than 1.3 grid nearer any of its
        // neighbours. A cell is folded into one catching exactly the same markers that ends within the step of it
        // towards every neighbour it leads to, richest first, and that one takes its neighbours. **The same markers,
        // not as much weight:** folding into any richer cell culled cells whose own marker the richer one does not
        // catch, and at a step of nought still folded every cell already catching its neighbour into any richer cell
        // catching it too.
        var markerNamed = names.ToDictionary(n => n.Value, n => n.Key);

        double LeftTo(Vector2 at, string neighbour) =>
            Math.Max(0d, Vector2.Distance(at, markerNamed[neighbour].Grid) - (env.Blast + markerNamed[neighbour].Radius));

        var folded = new Dictionary<Vector2, (double Caught, List<string> Toward)>();
        var contentsOf = new Dictionary<Vector2, string>();

        foreach (var (at, (caught, toward)) in byCell.OrderByDescending(x => x.Value.Caught))
        {
            var contents = contentsOf[at] = ContentsCaughtAt(env, at);
            var into = folded.FirstOrDefault(kept => contentsOf[kept.Key] == contents &&
                                                      toward.All(n => LeftTo(kept.Key, n) <= LeftTo(at, n) + step));

            if (into.Value.Toward == null)
            {
                folded[at] = (caught, new List<string>(toward));

                continue;
            }

            foreach (var n in toward)
            {
                if (!into.Value.Toward.Contains(n))
                    into.Value.Toward.Add(n);
            }

            if (shareOf.TryGetValue(at, out var gone) &&
                (!shareOf.TryGetValue(into.Key, out var stays) || gone.Share > stays.Share))
                shareOf[into.Key] = gone;
        }

        byCell = folded;

        // One ring per cell, coloured by the heavy markers it catches.
        seeds = new List<int>();
        brightness = new List<float>();

        foreach (var (at, (caught, toward)) in byCell.OrderByDescending(x => x.Value.Caught))
        {
            var taken = anchors.Where(t => Catches(env, at, t)).ToList();
            var label = $"{string.Join("+", taken.Select(t => names[t]).OrderBy(n => n))} > " +
                        string.Join(" ", toward);

            // From the markers' positions, so the colour follows the combination rather than the numbering.
            var seed = 0;

            foreach (var target in taken)
                seed += (int)MathF.Round(target.Grid.X) * 31 + (int)MathF.Round(target.Grid.Y) * 17;

            found.Add((label, new List<Vector2> { at }, new List<Vector2>()));
            seeds.Add(((seed % 997) + 997) % 997);

            var (best, of) = shareOf[at];

            brightness.Add((float)(EdgePointDimmest + (1d - EdgePointDimmest) * best));
            text.AppendLine($"    ({at.X:0},{at.Y:0})  {label}  catches {caught:N0}, {best:P0} of {of}'s richest");
        }


        var legend = string.Join("  ", names.Select(n => $"{n.Value} ({n.Key.Grid.X:0},{n.Key.Grid.Y:0})"));

        labels = names.Select(n => (n.Key.Grid, n.Value)).ToList();
        var combinations = found.Select(f => f.Name.Split(" > ")[0]).Distinct().Count();

        // Why each relic is or is not a starting point: its worth as the heavy test counts it, and whether it is a
        // must-avoid, which the edge points leave out.
        var downstream = Math.Max(0, env.Explosives - 1);

        double SwitchWorth(PlanTarget t) => global::AutoExpedition.Openings.SwitchWorthOfTarget(env, t);

        var relics = string.Join("; ", env.Targets.Where(t => t.Kind == TargetKind.Relic).Select(t =>
            $"({t.Grid.X:0},{t.Grid.Y:0}) worth {WorthOfTarget(t) + Math.Max(0f, t.Rough(downstream)) + SwitchWorth(t):N1}" +
            $"{(t.Shunned ? " MUST-AVOID" : "")}{(heavy.Contains(t) ? " heavy" : "")}"));

        said = (found.Count == 0
                   ? "  no heavy marker has a neighbour in reach"
                   : $"  {pairs} pair(s), {found.Count} point(s) in {combinations} colour(s); markers: {legend}" +
                     $"{Environment.NewLine}{text.ToString().TrimEnd()}") +
               $"{Environment.NewLine}  relics: {relics}";

        return found;
    }


    /// <summary>
    /// A number per drawn edge point that picks its colour, the same for points catching the same heavy markers. See
    /// EdgePointsTowardNeighbours and Overlay.Shapes.
    /// </summary>
    public static IReadOnlyList<int> EdgePointColourSeeds { get; private set; } = new List<int>();

    /// <summary>
    /// How bright each drawn edge point's colour is, from EdgePointDimmest to one: its weight caught as a share of the
    /// richest spot on the edge of the heavy marker it was kept for, the best share where it was kept for several.
    /// The points nearest a neighbour catch less by construction, so dim means content given up, not a worse route.
    /// </summary>
    public static IReadOnlyList<float> EdgePointBrightness { get; private set; } = new List<float>();

    /// <summary>The brightness of an edge point catching nothing, so it still shows against the ground.</summary>
    private const double EdgePointDimmest = 0.3d;

    /// <summary>
    /// The short names the edge point labels use - R3, E1, L2 - and the marker each belongs to, drawn on the markers
    /// while the edge points are. See EdgePointsTowardNeighbours.
    /// </summary>
    public static IReadOnlyList<(Vector2 Grid, string Name)> EdgePointMarkerNames { get; private set; } =
        new List<(Vector2, string)>();

    /// <summary>What EdgePointsTowardNeighbours last drew, for the dump; empty until the button is pressed.</summary>
    public static string EdgePointsSaid { get; private set; } = "";

    /// <summary>
    /// Whether the edge points towards each heavy marker's neighbours have been asked for and are to be drawn. See
    /// EdgePointsTowardNeighbours.
    /// </summary>
    public static bool TowardNeighbours { get; set; }

    /// <summary>A cell as one number, for keying a state on where the chain stands.</summary>
    internal static long Key(Vector2 at) =>
        ((long)MathF.Round(at.X) << 20) | (long)(MathF.Round(at.Y) + 1000f);

    /// <summary>
    /// Stops drawing the edge points and forgets the request behind them. The delete plan buttons call it. See
    /// Caches.
    /// </summary>
    public static void ClearDrawnSpots()
    {
        EdgePointsPending = false;
        Drawn = 0;
        TowardNeighbours = false;
        Shapes = new List<(string, List<Vector2>, List<Vector2>)>();
        EdgePointsSaid = "";
        EdgePointColourSeeds = new List<int>();
        EdgePointBrightness = new List<float>();
        EdgePointMarkerNames = new List<(Vector2, string)>();
    }

    private static int _sweeps;
    private static double _sweepGain;
    /// <summary>Stretches reversed, and what they were worth. See Reverse.</summary>
    private static int _reversals;

    private static double _reverseGain;

    private static int _orders;
    private static double _orderGain;

    /// <summary>
    /// Every position worth considering.
    ///
    /// Two families. A blast centred on a target catches that target, so every target's own
    /// position is offered. And for every pair of targets close enough to be caught together, the
    /// two positions where BOTH sit exactly on the edge of the blast.
    ///
    /// Those second ones are the important ones and they were wrong. The midpoint of a pair was
    /// offered instead, which is only the right answer when the pair happens to be diametrically
    /// opposite across the blast - and for any closer pair the midpoint sits well inside the
    /// circle, wasting the reach that would have pulled in a third marker. The best centre for a
    /// cluster is pinned by the two or three markers on its boundary, not by the middle of any two
    /// of them, and the boundary positions are exactly where the circles of radius r about each
    /// pair cross.
    ///
    /// Measured against a chain a player laid by hand, the old set was losing about eight points of
    /// content in a five-explosive chain: the same route, with every link a few grid units off the
    /// spot that would have caught one or two more markers.
    /// </summary>
    /// <summary>
    /// How many places to try round an object that cannot be stood on. See Candidates.
    ///
    /// Twelve, so the ring is sampled every thirty degrees - about nine grid apart at a catch radius
    /// of thirty seven, which is finer than the lattice the game rounds placements to. More would be
    /// more candidates for the search to walk and no more ground actually reachable.
    /// </summary>
    private const int Around = 12;

    internal static List<Vector2> Candidates(PlanEnvironment env, out int offered, out int refused)
    {
        using var phase = new Phase(PhaseCandidates);

        return CandidatesInner(env, out offered, out refused);
    }

    /// <summary>The body of Candidates, wrapped so its allocation is attributed. See Phases.</summary>
    private static List<Vector2> CandidatesInner(PlanEnvironment env, out int offered,
        out int refused)
    {
        var found = new HashSet<Vector2>();
        var seen = 0;
        var no = 0;

        void Offer(Vector2 at)
        {
            var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
            seen++;

            if (env.CanPlace(snapped))
                found.Add(snapped);
            else
                no++;
        }

        // **Anything worth catching, not only what has a weight of its own.** A relic's worth is mostly its effect -
        // more rares, pack size, duplicated runic monsters - which the score counts through Spread and NonStacking,
        // so one whose row carries no plain weight had no candidate on or around it and could only be caught by a
        // spot chosen for something else. Must-avoids still get none: Shunned is a negative weight.
        var wanted = env.Targets.Where(OffersCandidates).ToList();

        foreach (var target in wanted)
            Offer(target.Grid);

        // A ring round anything an explosive cannot be put on top of.
        //
        // **Content standing on unplaceable ground was unreachable, and quietly.** The two sources
        // above are the target's own cell and the intersections of pairs close enough to share a
        // blast - so an object whose cell the terrain refuses depends entirely on having a
        // neighbour within two catch radii. The Heath runic henges have neither: they are large
        // objects the game will not take a placement on, scattered a hundred grid and more apart.
        // Six of eight had no candidate anywhere, so a site with twenty explosives and nine things
        // to catch planned three links and stopped, with nothing in the readouts saying why.
        //
        // Sampling the circle it would be caught from fixes it generally rather than for henges:
        // any object on ground that refuses a bomb keeps the ring of ground that does not. The
        // inset is the same half unit the pair candidates use, for the same reason - the lattice
        // rounds, and a point exactly on the boundary rounds off it as often as on.
        foreach (var target in wanted)
        {
            if (env.CanPlace(target.Grid))
                continue;

            var reach = MathF.Max(1f, env.Blast + target.Radius - 0.5f);

            for (var step = 0; step < Around; step++)
            {
                var angle = step * MathF.Tau / Around;

                Offer(target.Grid + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach);
            }
        }

        // Both positions where a pair sits on the edge of the blast, for every pair near enough to
        // be caught together. Skipping pairs too far apart keeps the scoring loop clear of
        // candidates that cannot catch either of them.
        for (var i = 0; i < wanted.Count; i++)
        {
            for (var j = i + 1; j < wanted.Count; j++)
            {
                var a = wanted[i];
                var b = wanted[j];

                // The two catch distances differ when the markers differ in size, and a circle
                // through both is only defined for one radius - so the smaller is used. The larger
                // marker is then caught with room to spare, which is the safe way round.
                var reach = env.Blast + MathF.Min(a.Radius, b.Radius);
                var apart = Vector2.Distance(a.Grid, b.Grid);

                if (apart < 0.01f || apart > 2f * reach)
                    continue;

                // Constructed a shade inside the true circle, because the candidate is then
                // rounded to the lattice the game snaps placement to. On the exact circle both
                // markers sit ON the boundary, so a rounding of up to seven tenths of a unit in
                // the wrong direction drops one of the two markers the candidate exists to catch.
                // Half a unit of inset against a radius of thirty costs nothing measurable and
                // removes that entirely.
                var inset = MathF.Max(1f, reach - 0.5f);
                var middle = (a.Grid + b.Grid) / 2f;
                var half = apart / 2f;
                var out_ = MathF.Sqrt(MathF.Max(0f, inset * inset - half * half));
                var along = (b.Grid - a.Grid) / apart;
                var across = new Vector2(-along.Y, along.X) * out_;

                Offer(middle + across);
                Offer(middle - across);
            }
        }

        offered = seen;
        refused = no;

        return Allowed(env, found.ToList());
    }

    /// <summary>How many spots the last sweep threw away for catching something shunned. See Allowed.</summary>
    public static int Forbidden { get; private set; }

    /// <summary>
    /// Drops every spot whose blast catches a marker the chain must not catch.
    ///
    /// **This is the only real saving a refusal buys, and it is at the root rather than in the
    /// search.** A partial chain that has already caught a shunned marker cannot be pruned usefully
    /// - it scores below every legal chain, so the beam sorts it last and Trim cuts it on the same
    /// pass, whether its score is a large negative number or a sentinel. Nothing is explored that
    /// would not have been explored anyway.
    ///
    /// A SPOT is different. No legal chain may ever place an explosive here, so the spot is dead
    /// weight in every pass that touches it - and the passes touch it a great deal: Improve sweeps
    /// every candidate for every link and the beam expands every candidate at every step. Nine thousand candidates on a Grand site, so a
    /// spot removed once is removed from hundreds of thousands of tests.
    ///
    /// **Kept when it would leave nothing.** A ban that covered every placeable spot would otherwise
    /// turn "plan around this" into "no plan at all" - the exact failure the avoid weighting exists
    /// to prevent. Falling back to the full list puts the decision back on the objective, which will
    /// price those chains below zero and say so on screen.
    /// </summary>
    private static List<Vector2> Allowed(PlanEnvironment env, List<Vector2> spots)
    {
        Forbidden = 0;

        var shunned = new List<PlanTarget>();

        foreach (var target in env.Targets)
        {
            if (target.Shunned)
                shunned.Add(target);
        }

        if (shunned.Count == 0)
            return spots;

        var kept = new List<Vector2>(spots.Count);

        foreach (var at in spots)
        {
            var clear = true;

            foreach (var target in shunned)
            {
                if (Catches(env, at, target))
                {
                    clear = false;

                    break;
                }
            }

            if (clear)
                kept.Add(at);
        }

        if (kept.Count == 0)
            return spots;

        Forbidden = spots.Count - kept.Count;

        return kept;
    }

    /// <summary>
    /// Builds a chain a link at a time, each time taking one of the best few next steps.
    ///
    /// <paramref name="among"/> of one is the plain greedy answer, which is the first thing tried.
    /// Anything more picks at random from that many best candidates, which is what makes one
    /// restart differ from the next - and differ from the FIRST LINK onwards, which is the only
    /// difference that can change the direction of the whole chain.
    /// </summary>
    internal static List<Vector2> Greedy(PlanEnvironment env, List<Vector2> candidates,
        Random random, int among, Vector2? first = null, List<Vector2> keep = null)
    {
        using var phase = new Phase(PhaseGreedy);

        return GreedyInner(env, candidates, random, among, first, keep);
    }

    /// <summary>
    /// The chain's first <paramref name="keep"/> links, and the best whole chain found by trying each of the
    /// <paramref name="width"/> best next links in turn and finishing every one - once greedily and once with
    /// randomness among the best three. Null when nothing can follow the prefix.
    ///
    /// **Judged on the whole chain, not the next link.** A greedy tail takes the richest next step, and where
    /// taking a rich area last earns more propagation than taking it next, greedy never finds out: on Scorched
    /// Cay a 16,455 chain went east to a remnant at link 11 and then west, where a 16,630 chain went west first
    /// and took the remnant last, 137 content down and 312 propagation up. No tear reaches that, because every
    /// step between the two tails scores worse. Trying each first step and scoring the finished chain does.
    ///
    /// The candidates for the next link are ranked by what they catch and kept at least 16 grid apart, so the
    /// width is spent on directions rather than on neighbours of the richest spot.
    /// </summary>
    internal static List<Vector2> TailRollout(PlanEnvironment env, List<Vector2> candidates, List<Vector2> chain,
        int keep, int width, Random random)
    {
        if (env == null || chain == null || keep < 1 || keep >= chain.Count || width < 1)
            return null;

        var prefix = chain.GetRange(0, keep);
        var taken = new HashSet<int>();

        foreach (var at in prefix)
            Cover(env, at, taken);

        var from = prefix[^1];
        var priced = new List<(Vector2 At, double Gain)>();

        foreach (var candidate in candidates)
        {
            if (Span(from, candidate) > env.Reach || !Spaced(env, prefix, candidate))
                continue;

            priced.Add((candidate, NewWeight(env, candidate, taken, env.Explosives - keep - 1, true)));
        }

        priced.Sort(static (a, b) => b.Gain.CompareTo(a.Gain));

        var firsts = new List<Vector2>(width);

        foreach (var (at, gain) in priced)
        {
            if (firsts.Count >= width || gain <= 0d)
                break;

            var apart = true;

            foreach (var other in firsts)
                apart &= Vector2.DistanceSquared(at, other) >= 16f * 16f;

            if (apart && Reaches(env, from, at))
                firsts.Add(at);
        }

        List<Vector2> best = null;
        var top = double.NegativeInfinity;

        void Consider(List<Vector2> tried)
        {
            if (tried is not { Count: > 0 } || tried.Count <= keep)
                return;

            var worth = Score(env, tried);

            if (worth > top)
            {
                top = worth;
                best = tried;
            }
        }

        foreach (var first in firsts)
        {
            Consider(Greedy(env, candidates, null, 1, first, prefix));

            if (random != null)
                Consider(Greedy(env, candidates, random, 3, first, prefix));
        }

        return best;
    }

    /// <summary>The body of Greedy, wrapped so its allocation is attributed. See Phases.</summary>
    /// <summary>The priced candidates of one greedy step, kept per thread because greedy runs on every worker.</summary>
    [ThreadStatic] private static List<(int Index, Vector2 At, double Gain)> _priced;

    private static List<Vector2> GreedyInner(PlanEnvironment env, List<Vector2> candidates,
        Random random, int among, Vector2? first = null, List<Vector2> keep = null)
    {
        var chain = new List<Vector2>();
        var taken = new HashSet<int>();
        var from = env.Origin;
        var pick = new (Vector2 At, double Gain)[Math.Max(1, among)];

        // A prefix taken as given: the first few links stay exactly where they are and the rest is
        // built from there. See the tail restart in Search for why this exists.
        foreach (var at in keep ?? new List<Vector2>())
        {
            if (chain.Count >= env.Explosives)
                break;

            chain.Add(at);
            Cover(env, at, taken);
            from = at;
        }

        // A forced first link, for a restart that is being sent in a particular direction. After it
        // the chain is greedy again - the point is only ever to commit the FIRST step somewhere
        // else, because that is what the rest of the chain has to follow from.
        if (first != null && Reaches(env, from, first.Value) && Spaced(env, chain, first.Value))
        {
            chain.Add(first.Value);
            Cover(env, first.Value, taken);
            from = first.Value;
        }

        for (var step = chain.Count; step < env.Explosives; step++)
        {
            var held = 0;

            for (var i = 0; i < pick.Length; i++)
                pick[i] = (Vector2.Zero, double.NegativeInfinity);

            // **Priced first, and asked whether it reaches only in order of price, until the list is full.**
            //
            // This asked Reaches of every candidate before pricing it, and Reaches routes a wire whenever its
            // cache misses. Measured on Craggy Peninsula, twenty explosives: 4.2 million reach questions and 1.6
            // million wire searches in one solve, and the openings' 51 three-link rollouts - each a greedy build -
            // took 5.4 seconds. Pricing is a coverage lookup; only the handful at the top of the price list can be
            // chosen, so only they need the wire.
            //
            // **The same answer, not an approximation.** The list below keeps the best `among` reachable
            // candidates by price, the earlier one first where two prices tie - which is what the insertion into
            // it did, since a later equal price never displaced an earlier one. Sorting by price and then by
            // position, and filling in that order until the list is full, reproduces it exactly.
            var priced = _priced ??= new List<(int Index, Vector2 At, double Gain)>();

            priced.Clear();

            for (var c = 0; c < candidates.Count; c++)
            {
                var candidate = candidates[c];

                // Says' own first test, so this rejects nothing Reaches would accept and prices only what is in
                // range - without it every candidate on the site was priced every step, which offline, where a
                // reach question is a lookup, cost more than the wires it saved. See Says.
                if (Span(from, candidate) > env.Reach || !Spaced(env, chain, candidate))
                    continue;

                // Priced with the deferral in mind, because this is the one place with a chain
                // context: what has been taken already, and how many links are left to take
                // anything with. See NewWeight's defer.
                priced.Add((c, candidate, NewWeight(env, candidate, taken, env.Explosives - step - 1, true)));
            }

            priced.Sort(static (a, b) => b.Gain != a.Gain ? b.Gain.CompareTo(a.Gain) : a.Index.CompareTo(b.Index));

            foreach (var (_, candidate, gain) in priced)
            {
                if (held == pick.Length)
                    break;

                if (!Reaches(env, from, candidate))
                    continue;

                pick[held++] = (candidate, gain);
            }

            var choose = 0;

            if (random != null && held > 1)
            {
                // Only among the ones actually worth taking. Reaching down the list for a
                // candidate that covers nothing would be a random walk rather than a restart.
                var worth = 0;

                while (worth < held && pick[worth].Gain > 0d)
                    worth++;

                if (worth > 1)
                    choose = random.Next(worth);
            }

            var bestAt = pick[choose].At;
            var bestGain = pick[choose].Gain;

            // Nothing reachable is worth anything from here. That is not the end of the chain -
            // it may be that the content worth having is simply further away than one link - so
            // the chain steps towards it instead of stopping.
            if (bestGain <= 0d || bestAt == Vector2.Zero)
            {
                var stone = Stone(env, chain, taken, from, env.Explosives - step - 1);

                if (stone == null)
                    break;

                bestAt = stone.Value;
            }

            chain.Add(bestAt);
            Cover(env, bestAt, taken);
            from = bestAt;
        }

        return chain;
    }

    /// <summary>
    /// A link on empty ground, placed only to get the chain further out.
    ///
    /// The chain is a chain: each explosive has to be within reach of the one before, so content
    /// two reaches away is not unreachable, it just costs a link to get to. Without this the greedy
    /// pass stopped the moment nothing within one reach was worth anything, which made a cluster
    /// across the dig site invisible however rich it was - and that is the same class of mistake as
    /// scoring only remnants, just one step further out.
    ///
    /// Deliberately not offered for the LAST explosive. There, the original reasoning holds: an
    /// explosive kept in hand is worth more than one spent on bare ground, because nothing follows
    /// it to make use of the distance.
    ///
    /// It also refuses to set off towards something it could not reach even by spending every
    /// explosive left on walking, since that trades the whole chain for nothing.
    /// </summary>
    private static Vector2? Stone(PlanEnvironment env, List<Vector2> chain, HashSet<int> taken,
        Vector2 from, int left)
    {
        if (left <= 0)
            return null;

        var nearest = Vector2.Zero;
        var distance = float.MaxValue;

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (taken.Contains(i))
                continue;

            var target = env.Targets[i];

            // Wanted covers anything with a weight, negative ones included - they stay indexed so
            // the penalty gets charged wherever a blast happens to reach one. Setting off TOWARDS
            // one is a different question. See PlanTarget.Shunned.
            if (!target.Wanted || target.Shunned)
                continue;

            var out_ = Vector2.Distance(from, target.Grid);

            if (out_ < distance)
            {
                distance = out_;
                nearest = target.Grid;
            }
        }

        // Nothing left to want, or it is already underfoot - in which case it would have had a gain
        // and this would not have been asked.
        if (nearest == Vector2.Zero || distance < 1f)
            return null;

        // Unreachable even by spending every remaining explosive on the journey.
        if (distance > (left + 1) * env.Reach + env.Blast)
            return null;

        var direction = Vector2.Normalize(nearest - from);

        // As far along as the reach allows, and shorter if the ground will not take it.
        for (var fraction = 1f; fraction >= 0.5f; fraction -= 0.1f)
        {
            var at = from + direction * MathF.Min(env.Reach * fraction, distance);
            var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));

            if (snapped != from && env.CanPlace(snapped) && Reaches(env, from, snapped) &&
                Spaced(env, chain, snapped))
                return snapped;
        }

        return null;
    }

    /// <summary>
    /// One hop straight at <paramref name="goal"/>, as far as the reach and the ground allow.
    ///
    /// The fallback under Toward, which rings outward and can therefore walk around what is in the
    /// way. This one only tries the line, and is here because the two ask the router different
    /// questions: Toward insists on a confirmed Yes, where Reaches settles for what the router will
    /// say now. On a cold site that is the difference between a bridging link and no opening at all.
    ///
    /// Off the lattice rather than the candidate set on purpose: a bridging link exists to cover
    /// ground, and the ground between two clusters is exactly where no candidate is generated.
    /// </summary>
    private static Vector2? Straight(PlanEnvironment env, List<Vector2> chain, Vector2 from, Vector2 goal)
    {
        var distance = Vector2.Distance(from, goal);

        if (distance < 1f)
            return null;

        var direction = Vector2.Normalize(goal - from);

        for (var fraction = 1f; fraction >= 0.5f; fraction -= 0.1f)
        {
            var at = from + direction * MathF.Min(env.Reach * fraction, distance);
            var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));

            if (snapped != from && env.CanPlace(snapped) && Reaches(env, from, snapped) &&
                Spaced(env, chain, snapped))
                return snapped;
        }

        return null;
    }

    /// <summary>
    /// The best spot that catches <paramref name="want"/> and can be thrown from
    /// <paramref name="from"/>, or nothing when none can.
    ///
    /// "Best" by what it adds overall rather than by how squarely it sits on the marker, so a spot
    /// that catches the required one AND the chest beside it wins.
    /// </summary>
    private static Vector2? Grab(PlanEnvironment env, List<Vector2> candidates, List<Vector2> chain,
        HashSet<int> taken, Vector2 from, int want)
    {
        var best = Vector2.Zero;
        var most = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            if (!Catches(env, candidate, env.Targets[want]) || !Reaches(env, from, candidate) ||
                !Spaced(env, chain, candidate))
                continue;

            var gain = NewWeight(env, candidate, taken, env.Explosives - chain.Count - 1, true);

            if (gain <= most)
                continue;

            most = gain;
            best = candidate;
        }

        return best == Vector2.Zero ? null : best;
    }

    /// <summary>
    /// The must-take tours that start at the detonator, scored, worked out once per solve and shared by every worker.
    ///
    /// **They are the same for every worker**, because they start at the detonator rather than from a worker's own
    /// chain - and each is a full construction. Built on the first worker to ask and handed to the rest; the lock
    /// makes the others wait for it rather than build it again. Keyed on the environment and on the number of
    /// candidates, since the opening passes the shortlist and a kick passes every candidate. Callers copy what
    /// they take, so nothing here is changed by the polish that follows. See MustTakeTour.
    /// </summary>
    private static List<(List<Vector2> Tour, int Held, double Worth)> FromDetonator(PlanEnvironment env,
        List<Vector2> candidates)
    {
        var byCount = _fromDetonator.GetOrCreateValue(env);

        var built = byCount.GetOrAdd(candidates.Count, _ =>
            new Lazy<List<(List<Vector2> Tour, int Held, double Worth)>>(() =>
            {
                var tours = new List<(List<Vector2> Tour, int Held, double Worth)>();
                var began = Stopwatch.GetTimestamp();
                var (fetchBefore, completeBefore) = (_demandedFetchTicks, _demandedCompleteTicks);

                foreach (var tour in Demanded(env, candidates))
                    tours.Add((tour, Rate(env, tour).Held, Score(env, tour)));

                var detonatorTicks = Stopwatch.GetTimestamp() - began;
                var fetchTicks = _demandedFetchTicks - fetchBefore;
                var completeTicks = _demandedCompleteTicks - completeBefore;

                double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

                lock (_sharedTourBuilds)
                {
                    _sharedTourBuilds.Add($"{DateTime.Now:HH:mm:ss.fff} site copy " +
                                          $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(env):X8}, " +
                                          $"{candidates.Count} spots: detonator tours {Ms(detonatorTicks):N0}ms " +
                                          $"(fetching {Ms(fetchTicks):N0}ms, completing {Ms(completeTicks):N0}ms, " +
                                          $"scoring the rest), " +
                                          $"{Ms(Stopwatch.GetTimestamp() - began):N0}ms in all");

                    if (_sharedTourBuilds.Count > 12)
                        _sharedTourBuilds.RemoveAt(0);
                }

                return tours;
            }, LazyThreadSafetyMode.ExecutionAndPublication));

        return built.Value;
    }

    /// <summary>The shared detonator tours, per environment and candidate count. See FromDetonator.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanEnvironment,
        System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<List<(List<Vector2> Tour, int Held, double Worth)>>>>
        _fromDetonator = new();

    /// <summary>
    /// The points along a chain of <paramref name="links"/> links from which a fetch is tried: nought (the
    /// detonator) and at most <see cref="FetchPointsPerChain"/> of the links after it, spread evenly and always
    /// including the last.
    ///
    /// **Every link was tried, and on a twenty explosive site that was the whole window.** Each point is a full
    /// construction - a bridge to the target and a greedy completion of the rest - once per visiting order.
    /// Measured on Craggy Peninsula with two must-takes: 1.3 to 2.9 seconds per worker building them, against
    /// 0.2 to 0.7 polishing the one chosen, and every worker ran nought rounds of search. See MustTakeTour.
    /// </summary>
    private static IEnumerable<int> FetchPoints(int links)
    {
        yield return 0;

        if (links <= 1)
            yield break;

        var last = links - 1;
        var count = Math.Min(FetchPointsPerChain, last);
        var previous = 0;

        for (var i = 1; i <= count; i++)
        {
            var k = (int)Math.Round(i * (double)last / count);

            if (k <= previous)
                continue;

            previous = k;

            yield return k;
        }
    }

    /// <summary>
    /// How many points after the detonator a fetch is tried from. Chosen, not measured: six keeps two must-takes
    /// to fourteen constructions a worker where every link cost about forty. See FetchPoints.
    /// </summary>
    private const int FetchPointsPerChain = 6;

    /// <summary>
    /// The chain routed through one target it does not reach, fetched after whichever of its links scores best,
    /// or null when no such route keeps every must-take the chain already holds.
    ///
    /// **For exploration, not for a requirement.** On Scorched Cay the search without a must-take peaked at
    /// 8,625 over ten presses, while with the Divine Orb at (1105,568) marked - and fetched this way - it reached
    /// 10,144; a chain holding the orb was available to the unmarked search too and it never found one. Every
    /// worker converges on one route because nothing ever takes it far from where it started. This is the same
    /// construction as MustTakeTour, pointed at a target a worker is told to try rather than one it must hold,
    /// and unlike a must-take the result is only a starting point: the search may walk away from it.
    ///
    /// Scored on the objective, so a route that strands a held must-take loses to one that does not and the
    /// requirement cannot be traded for exploration.
    /// </summary>
    internal static List<Vector2> TourThrough(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, int target)
    {
        if (env == null || candidates == null || target < 0 || target >= env.Targets.Count)
            return null;

        var held = chain is { Count: > 0 } ? Rate(env, chain).Held : 0;
        var wants = new[] { target };
        List<Vector2> best = null;
        var top = double.NegativeInfinity;

        foreach (var k in FetchPoints(chain?.Count ?? 0))
        {
            var prefix = k == 0 ? null : chain.GetRange(0, k);

            foreach (var tour in Demanded(env, candidates, prefix, wants))
            {
                if (Rate(env, tour).Held < held)
                    continue;

                var worth = Score(env, tour);

                if (worth > top)
                {
                    top = worth;
                    best = tour;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// A rich target the chain does not reach, for the touring worker at <paramref name="rank"/> of
    /// <paramref name="stride"/>, or -1 when none of that worker's targets is missing. Must-takes are left out;
    /// MustTakeTour fetches those. Richest by the target's own worth, the same figure the enumerator anchors on.
    /// See TourThrough.
    ///
    /// The site's targets are ranked once, richest first, and the worker takes positions rank, rank + stride,
    /// rank + 2 x stride and so on, the first its chain misses. No two touring workers can therefore be sent to
    /// the same target. Ranked against each worker's own misses instead, as this was, two workers whose chains
    /// missed different things landed on the same target: with four touring workers on Scorched Cay, workers 1
    /// and 3 were both sent through (625,817).
    /// </summary>
    internal static int RichestUnreached(PlanEnvironment env, List<Vector2> chain, int rank, int stride)
    {
        if (env == null || rank < 0)
            return -1;

        var missing = new HashSet<int>(Missing(env, chain ?? [], false, int.MaxValue));
        var ranked = new List<int>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (!env.Targets[i].Must)
                ranked.Add(i);
        }

        ranked.Sort((a, b) => WorthOfTarget(env.Targets[b]).CompareTo(WorthOfTarget(env.Targets[a])));

        for (var k = rank; k < ranked.Count; k += Math.Max(1, stride))
        {
            if (missing.Contains(ranked[k]))
                return ranked[k];
        }

        return -1;
    }

    /// <summary>
    /// A chain that visits more of the must-takes than the one handed in, or null when there is no
    /// better one to be had.
    ///
    /// **The destroy-and-repair search cannot reach a distant must-take on its own, and this is
    /// what it calls to get there.** Demanded was written for a restart search that only the score
    /// card ever ran, and has since been removed - so the live path had the 43,255 charge for
    /// dropping a requirement and no operator able to satisfy one. A Craggy Peninsula site sat 335 grid west
    /// of a marked marker with 463 of budget spare and never went for it.
    ///
    /// **The charge alone cannot fix that, and the size of it is why.** Tear, Rebuild and the rest
    /// move a link and keep the result only if it improves; every position on the way to a marker
    /// three links off the route is worse than where the chain started. A penalty ranks the answer
    /// once it exists. It does not build one.
    ///
    /// Scored before it is handed back, so a tour that strands the rest of the chain is refused
    /// like any other move. Null when nothing is marked, nothing is missing, or no tour holds more
    /// than the chain already does.
    /// </summary>
    /// <param name="worker">
    /// Which thread is asking, so the pool does not all descend from one tour.
    ///
    /// **Handing every worker the best tour put the collapse back.** Demanded yields a chain per
    /// ordering of the must-takes, and taking the highest scoring one meant eight differently
    /// shaken openings had the identical tour stitched on and opened on the same chain - measured
    /// at 104,601 on all eight. Nought still takes the best, so the pool keeps the strongest tour;
    /// the rest take the others in turn, and fall back to the best when there are fewer orderings
    /// than threads.
    /// </param>
    /// <param name="keepAtLeast">
    /// How many leading links of <paramref name="chain"/> the caller would rather not lose - a seeded worker's
    /// kept opening. Tours that keep them are preferred among those holding the most must-takes; when none of
    /// those keeps them, the best tour is taken anyway and the caller has to release what it was keeping.
    /// </param>
    internal static List<Vector2> MustTakeTour(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, int worker = 0, int keepAtLeast = 0)
    {
        if (env == null || env.Musts <= 0 || candidates == null)
            return null;

        var had = chain == null ? 0 : Rate(env, chain).Held;

        if (had >= env.Musts)
            return null;

        var found = new List<(List<Vector2> Tour, int Held, double Worth, int Kept)>();

        foreach (var (tour, held, worth) in FromDetonator(env, candidates))
            found.Add((new List<Vector2>(tour), held, worth, 0));

        // **And the same fetch branching off the chain in hand, after each of its links.**
        //
        // Starting every tour at the detonator makes the first links a straight run to the must-take, and the
        // rest of the chain whatever greedy finds from there. The search cannot repair that afterwards: every
        // move that would take the mark later, or from another side, passes through chains that drop it, and
        // dropping one costs more than the site is worth. So the choice of WHEN to fetch has to be made here.
        // Keeping the first k links of the worker's own chain and fetching from the last of them offers every
        // point along it; k of nought is the tour above, so this can only add better options. Measured on
        // Scorched Cay before this: the mark held links one to eight on a straight run east, and the press
        // median was 3,931 with the mark against 8,405 without it.
        if (chain is { Count: > 1 })
        {
            foreach (var k in FetchPoints(chain.Count))
            {
                if (k == 0)
                    continue;

                foreach (var tour in Demanded(env, candidates, chain.GetRange(0, k)))
                    found.Add((tour, Rate(env, tour).Held, Score(env, tour), k));
            }
        }

        if (found.Count == 0)
            return null;

        // More requirements first, and only then more points - the same order the objective ranks
        // them in, made explicit because a tour holding one more is the point of this even when it
        // scores worse on the way there.
        found.Sort((a, b) => a.Held != b.Held
            ? b.Held.CompareTo(a.Held)
            : b.Worth.CompareTo(a.Worth));

        var top = found[0];

        if (top.Held <= had)
            return null;

        // Only the tours that hold as many as the best does are worth handing out; one that drops
        // a requirement is not a different opinion, it is a worse answer.
        var equal = found.FindAll(x => x.Held == top.Held);

        // A seeded worker keeps its opening where some tour lets it, so the fetch does not undo the seeding.
        if (keepAtLeast > 0 && equal.FindAll(x => x.Kept >= keepAtLeast) is { Count: > 0 } keeping)
            equal = keeping;

        return equal[worker <= 0 ? 0 : worker % equal.Count].Tour;
    }

    /// <summary>Ticks this thread has spent in Demanded's fetching and completing, for timing its callers.</summary>
    [ThreadStatic] private static long _demandedFetchTicks;

    [ThreadStatic] private static long _demandedCompleteTicks;

    /// <summary>
    /// Every build of the shared detonator tours, newest last, with which copy of the site it was for and what it
    /// spent - so a dump can say whether one solve built them more than once. See FromDetonator.
    /// </summary>
    private static readonly List<string> _sharedTourBuilds = new();

    /// <summary>The shared detonator tour builds, for the dump. See _sharedTourBuilds.</summary>
    internal static string SharedTourBuildsSaid
    {
        get
        {
            lock (_sharedTourBuilds)
                return _sharedTourBuilds.Count == 0
                    ? "    none yet"
                    : string.Join(Environment.NewLine, _sharedTourBuilds.Select(x => "    " + x));
        }
    }

    /// <summary>
    /// Chains that go and fetch every marker the player insisted on, one per order to fetch them in.
    ///
    /// **This is the only thing in the search that can build a route to a distant requirement, and
    /// without it the marks were decoration on a Grand site.** Nothing else can: greedy picks the
    /// next link by what it adds now, and a marker six hundred grid away adds nothing to any link
    /// within reach of the detonator, so it produces no pull at all until a chain happens to end up
    /// near it. Committing to an ORDER of remnants with each stop one hop from the last silently
    /// skips anything that needs bridging, and moving a single link onto a missed marker cannot be
    /// legal when the marker is two or three links past the end of the chain.
    ///
    /// Measured on the Grand site that prompted this: three marked remnants, the chain took one, and
    /// the other two sat 179 and 214 grid beyond its last link against a reach of 108.
    ///
    /// So the route is CONSTRUCTED rather than discovered. Bridge towards the marker until a spot
    /// that catches it is in reach, take the best such spot, then do the same for the next one, and
    /// let greedy spend whatever explosives are left on the monsters in between.
    ///
    /// Every order while there are four or few enough for that to be twenty four chains; nearest
    /// first beyond that, because the orderings are factorial and a player who has marked five
    /// things has already said most of what they mean.
    /// </summary>
    /// <param name="prefix">
    /// Links to keep at the head of every tour, fetching the must-takes from the last of them rather than from
    /// the detonator. Null starts at the detonator. See MustTakeTour, which tries a prefix of every length.
    /// </param>
    /// <param name="wants">
    /// The targets to fetch, or null for every must-take. See TourThrough, which fetches one that is not.
    /// </param>
    private static IEnumerable<List<Vector2>> Demanded(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> prefix = null, IReadOnlyList<int> wants = null)
    {
        var found = new List<int>();

        if (wants != null)
            found.AddRange(wants);
        else
            for (var i = 0; i < env.Targets.Count; i++)
                if (env.Targets[i].Must)
                    found.Add(i);

        if (found.Count == 0)
            yield break;

        IEnumerable<List<int>> orders;

        if (found.Count <= 4)
        {
            orders = Orders(found);
        }
        else
        {
            found.Sort((a, b) => Vector2.DistanceSquared(env.Origin, env.Targets[a].Grid)
                .CompareTo(Vector2.DistanceSquared(env.Origin, env.Targets[b].Grid)));

            orders = OrdersBeyondFour(env, found);
        }

        // The marker taken last is fetched last, since a tour that takes it earlier does not hold it. See
        // PlanEnvironment.TakenLast.
        var endsOnTakenLast = env.TakenLast >= 0 && found.Contains(env.TakenLast);

        if (endsOnTakenLast)
            orders = orders.Where(order => order[^1] == env.TakenLast)
                .DefaultIfEmpty(found.Where(i => i != env.TakenLast).Append(env.TakenLast).ToList())
                .ToList();

        foreach (var order in orders)
        {
            var chain = prefix == null ? new List<Vector2>() : new List<Vector2>(prefix);
            var taken = new HashSet<int>();
            var from = env.Origin;

            foreach (var at in chain)
            {
                Cover(env, at, taken);
                from = at;
            }

            var fetching = Stopwatch.GetTimestamp();

            foreach (var want in order)
            {
                // Already caught by a link laid on the way to an earlier one, which is common when
                // two of them sit near each other.
                if (taken.Contains(want))
                    continue;

                Fetch(env, candidates, chain, taken, ref from, want, asker: Touring);
            }

            _demandedFetchTicks += Stopwatch.GetTimestamp() - fetching;

            if (chain.Count == 0)
                continue;

            // The rest greedily, which is now only being asked where the monsters are - unless the tour ends on the marker
            // taken last, after which nothing may be placed: the explosives left over are for the search to lay earlier.
            var completing = Stopwatch.GetTimestamp();
            var completed = endsOnTakenLast && taken.Contains(env.TakenLast)
                ? chain
                : Greedy(env, candidates, null, 1, null, chain);

            _demandedCompleteTicks += Stopwatch.GetTimestamp() - completing;

            yield return completed;
        }
    }

    /// <summary>
    /// Extends the chain until it catches <paramref name="want"/>, laying bridging links across
    /// empty ground when the target is further off than one explosive can reach. Returns whether it
    /// got there; either way the chain is left wherever it actually reached.
    ///
    /// **This is the difference between a seed that can leave the detonator and one that cannot**,
    /// and it is worth stating because the seed that could not do it looked exactly like the seed
    /// that could. A spot is only offered when it CATCHES something, so a route to a cluster six
    /// hundred grid away has no candidate anywhere along the middle of it - and a builder that only
    /// ever asks for a covering spot in reach finds none, drops the target and quietly produces a
    /// local chain. Measured on a Grand site: the rich remnants sit 500 to 700 grid out against a
    /// reach of 108, and every seed dropped all eleven of them.
    ///
    /// The bridging hop is taken off the lattice rather than the candidate set, for the same reason.
    /// Toward rings outward and can therefore round an obstacle; Straight only tries the line but
    /// asks the router a cheaper question, which is what answers on a cold site. Both refuse a hop
    /// that does not close the distance, so this terminates on its own.
    /// </summary>
    /// <param name="ceiling">
    /// The longest the chain may grow while bridging, or nought for the whole explosive budget.
    ///
    /// **Without one, a distant target eats the entire tail.** The bridge lays links across ground
    /// that catches nothing, which is the point of it - and left to run it will spend every
    /// remaining explosive travelling and arrive with none left to catch anything. That chain then
    /// scores badly and is thrown away, so nothing is broken by it; what is wasted is the round.
    ///
    /// A must-take is worth the whole budget, which is why the default is still the whole budget.
    /// A merely rich marker is not. See Repair.Reaching.
    /// </param>
    /// <param name="asker">
    /// Which caller this is, so the counters can be read apart.
    ///
    /// **Three callers with different budgets were being counted as one.** The tour builders pass no
    /// ceiling at all, so they may spend the whole chain getting somewhere; the repair's reach
    /// operator allows half of what is left. Aggregated, four fifths of the failures belonged to the
    /// unbounded callers and were read as though they described the bounded one - which is a fix
    /// aimed at the wrong code. See Fetches.
    /// </param>
    internal static bool Fetch(PlanEnvironment env, List<Vector2> candidates, List<Vector2> chain,
        HashSet<int> taken, ref Vector2 from, int want, int ceiling = 0, int asker = 0)
    {
        var most = ceiling > 0 ? Math.Min(ceiling, env.Explosives) : env.Explosives;
        var hops = 0;

        while (chain.Count < most)
        {
            if (Grab(env, candidates, chain, taken, from, want) is { } at)
            {
                chain.Add(at);
                Cover(env, at, taken);
                from = at;

                Interlocked.Increment(ref _fetchWon[asker]);

                return true;
            }

            // **The bridging hop is geometry only, and making it value-aware was tried.**
            //
            // Ranking candidates that make progress by what they catch was aimed at a chain laid
            // by hand, whose three links east both travelled at full stretch and collected on the
            // way. It does not belong here. Fetch is called a handful of times by the must-take
            // tour and thousands by the repair's reach operator, so the change landed almost
            // entirely on the latter: 8,182 arrivals against 2,268, with 2,240 of them out of
            // explosives, and the site score fell from 23,517 to 21,907. In the best run on record
            // the tour was never asked at all.
            //
            // Two versions were measured. Worth among anything that made progress crept - a spot
            // two grid nearer carrying a monster beat one a reach nearer carrying nothing, and the
            // chain ran out of explosives holding one must-take of two. A travel floor of four
            // fifths fixed that and still scored below doing nothing.
            // No candidate list to hand here, so the rings are the whole of it - which is what
            // this caller has always had. See Toward.
            var hop = Toward(env, chain, null, from, env.Targets[want].Grid);

            if (hop == Vector2.Zero)
            {
                if (Straight(env, chain, from, env.Targets[want].Grid) is not { } line)
                {
                    // Nowhere to step. The router will not take the chain any closer and a straight
                    // line out of here is refused too, so the target is walled off from where the
                    // chain stands rather than merely far away.
                    Interlocked.Increment(ref _fetchStuck[asker]);
                    Interlocked.Add(ref _fetchStuckHops[asker], hops);

                    return false;
                }

                hop = line;
            }

            chain.Add(hop);
            Cover(env, hop, taken);
            from = hop;
            hops++;
        }

        // Steps were available the whole way and there were not enough explosives to finish the
        // journey. A different complaint entirely from being walled off: the target was reachable,
        // it was just further than the budget allowed.
        Interlocked.Increment(ref _fetchSpent[asker]);
        Interlocked.Add(ref _fetchSpentHops[asker], hops);

        return false;
    }

    /// <summary>The callers of Fetch, as the counters index them. See Fetch's asker.</summary>
    internal const int Touring = 0;

    internal const int Bridging = 1;

    private static readonly int[] _fetchWon = new int[2];
    private static readonly int[] _fetchStuck = new int[2];
    private static readonly int[] _fetchSpent = new int[2];
    private static readonly int[] _fetchStuckHops = new int[2];
    private static readonly int[] _fetchSpentHops = new int[2];

    /// <summary>
    /// Why the bridges the reach operator builds do not arrive, for the dump.
    ///
    /// **Between two and three of every five arrive, all night, and nothing said which half of the
    /// journey was at fault.** A bridge stops for one of two reasons and they want opposite
    /// responses: the chain ran out of explosives before it got there, which is a budget the caller
    /// chooses - see Reaching's half-the-tail rule - or the chain could not take another step at all,
    /// which is the router saying the target is walled off from where it stands. Tuning the budget
    /// when the ground is the problem does nothing, and the reverse likewise.
    ///
    /// The hop counts say how far each kind got, which separates "stuck immediately" from "stuck at
    /// the last step".
    /// </summary>
    public static string Fetches
    {
        get
        {
            var names = new[] { "tour", "reach" };
            var said = new List<string>();

            for (var i = 0; i < names.Length; i++)
            {
                // **A row that never fired still prints, saying so.** Skipping it made "this
                // builder was never called" and "this builder is not reported" look identical, and
                // a tour count of nothing was read as bridging that failed rather than bridging
                // that never ran - which is most of a diagnosis spent on the wrong half.
                if (_fetchWon[i] + _fetchStuck[i] + _fetchSpent[i] == 0)
                {
                    said.Add($"{names[i]} never asked");

                    continue;
                }

                said.Add($"{names[i]} {_fetchWon[i]:N0} arrived, {_fetchStuck[i]:N0} walled off " +
                         $"after {(_fetchStuck[i] == 0 ? 0d : (double)_fetchStuckHops[i] / _fetchStuck[i]):0.#}" +
                         $", {_fetchSpent[i]:N0} out of explosives after " +
                         $"{(_fetchSpent[i] == 0 ? 0d : (double)_fetchSpentHops[i] / _fetchSpent[i]):0.#}");
            }

            return said.Count == 0 ? "no bridges attempted" : string.Join("; ", said);
        }
    }

    /// <summary>Forgets it, so a solve is judged on its own. See Fetches.</summary>
    public static void Unfetch()
    {
        for (var i = 0; i < _fetchWon.Length; i++)
        {
            _fetchWon[i] = 0;
            _fetchStuck[i] = 0;
            _fetchSpent[i] = 0;
            _fetchStuckHops[i] = 0;
            _fetchSpentHops[i] = 0;
        }
    }

    /// <summary>
    /// Takes a chain as far as local search will go: the best candidate for each link, then the
    /// best position near it, until neither moves.
    ///
    /// The two do different jobs and both are needed. <see cref="Sweep"/> can move a link right
    /// across the dig site to a completely different cluster; <see cref="Polish"/> can only shuffle
    /// it a few units, but it works off the lattice rather than off the candidate set, so it is not
    /// limited by whether the geometry that built the candidates was exactly right.
    /// </summary>
    /// <param name="waiting">
    /// Whether the search still has time, asked between rounds and inside the sweep.
    ///
    /// **Without this a solve cannot honour its own stopping rule.** The clock used to be read only
    /// between seeds, and one pass of this is a sweep of every candidate against every link, each
    /// of which may make the router walk ground it has never walked. Measured on a cold site that
    /// was 4,157ms inside one uninterruptible stretch, against a window the player had set to
    /// 1,500 - so the setting was ignored and nothing at all was drawn until it finished.
    ///
    /// Null for callers with no clock, which then behave as before.
    /// </param>
    /// <param name="pinned">
    /// How many links at the head of the chain are commitments rather than suggestions.
    ///
    /// **A seed that forces an opening was not forcing anything.** Every local operator here moves
    /// any link it likes, the first included, so a chain built deliberately out of the north west
    /// was free to have its opening dragged back to whatever the greedy step liked on the very
    /// first polish - and it was, every time. That is why the search rediscovers the same chain
    /// from eight different directions, and why placing the first explosive by hand and solving the
    /// remaining four reaches scores the free search does not: placing it makes the commitment real,
    /// because the game will not let anything move it.
    ///
    /// So the seeds that mean to commit say how many links they are committing, and the operators
    /// leave those alone. The unpinned restarts are untouched, so nothing is given up: the search
    /// still gets to change its mind about the opening, it simply no longer does so before it has
    /// found out what the opening was worth.
    /// </param>
    internal static List<Vector2> Improve(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, Func<bool> waiting = null, int pinned = 0)
    {
        using var phase = new Phase(PhaseImprove);

        return ImproveInner(env, candidates, chain, waiting, pinned);
    }

    /// <summary>The body of Improve, wrapped so its allocation is attributed. See Phases.</summary>
    private static List<Vector2> ImproveInner(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, Func<bool> waiting = null, int pinned = 0)
    {
        for (var round = 0; round < 6; round++)
        {
            var before = Score(env, chain);

            // **Timed one operator at a time.** Improve measured 3.9MB a call and is nothing but
            // these five in a loop, so which of them it is cannot be read off the nesting - and
            // four guesses about this solve's allocation have already been counted and refuted.
            List<Vector2> swept, ordered, reversed, shifted;

            using (new Phase(PhaseSweep))
                swept = Sweep(env, candidates, chain, waiting, pinned);

            using (new Phase(PhaseOrder))
                ordered = Order(env, swept, pinned);

            using (new Phase(PhaseReverse))
                reversed = Reverse(env, ordered, pinned);

            using (new Phase(PhaseShift))
                shifted = Shift(env, reversed, pinned);

            using (new Phase(PhasePolish))
                chain = Polish(env, shifted, pinned);

            if (Score(env, chain) <= before + 0.0001d || waiting?.Invoke() == false)
                break;
        }

        return chain;
    }

    /// <summary>
    /// Takes one link out of the chain and puts it back somewhere else.
    ///
    /// **Measured against a chain worth seventeen points more: this is the move that was missing.**
    /// The two chains used almost the same five places. The better one saved its cheapest link for
    /// last; the found one took that link third, which pushed both remaining remnants later, cost
    /// them the monsters their runes would have carried over, and added an eighty seven grid walk
    /// across the site to get back.
    ///
    /// Turning one into the other means lifting the third link out and reinserting it at the end,
    /// with the fourth and fifth shifting forward. Order cannot do it: it swaps pairs, that rotation
    /// takes two swaps, and the chain in between is worse than both - so the swap is refused and the
    /// better arrangement on the far side is never seen. Sweep cannot do it either, since it changes
    /// which spot a link uses rather than when it is visited.
    ///
    /// A relocation is one move to the operator and any number of swaps to the chain, so the valley
    /// in between never has to be crossed. Every link into every position, which is twenty moves on
    /// a five link chain and two hundred on a fifteen, each one a legality check and a score.
    /// </summary>
    private static List<Vector2> Shift(PlanEnvironment env, List<Vector2> chain, int pinned = 0)
    {
        if (chain.Count < 3)
            return chain;

        var score = Score(env, chain);
        var from = Math.Min(pinned, chain.Count);
        var moved = true;

        // Bounded like the other operators: each pass takes a strict improvement, and a loop that
        // trusts only that is a loop that can hang the search.
        // **The untouched head here runs to the LOWER of the two positions**, which moves as the
        // partner does - so the prefix is wound to depth rather than only grown. Within one i it
        // still only ever rises: the depth is min(i, j), so it climbs with j while j is below i and
        // then sits at i for the rest of them. See Planner.Wind.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);

        for (var pass = 0; pass < 6 && moved; pass++)
        {
            moved = false;

            // The chain is replaced wholesale whenever a move is kept, so each pass starts over.
            work.Clear();
            Unfold(env, tally);

            for (var i = from; i < chain.Count; i++)
            {
                Wind(env, tally, work, chain, from);

                for (var j = from; j < chain.Count; j++)
                {
                    if (i == j)
                        continue;

                    var tried = Copied(chain);
                    var link = tried[i];

                    tried.RemoveAt(i);
                    tried.Insert(j, link);

                    // Everything below the lower of the two is the same in `tried` as in `chain`,
                    // which is what makes the carried prefix usable across both lists.
                    Wind(env, tally, work, chain, Math.Min(i, j));

                    // The whole chain, not the ends of the gap: moving a link moves every link
                    // after it, so reach and spacing have to hold all the way along.
                    if (!Walkable(env, tried) ||
                        WorthOfChainWithTail(env, tally, work, tried, work.Count) is var now &&
                        now <= score + 0.0001d)
                        continue;

                    _shifts++;
                    _shiftGain += now - score;
                    score = now;
                    chain = tried;
                    moved = true;

                    break;
                }

                if (moved)
                    break;
            }
        }

        return chain;
    }

    private static int _shifts;
    private static double _shiftGain;

    /// <summary>
    /// Tries the same spots in a different order, keeping the best.
    ///
    /// **Coverage does not care what order a chain is laid in and propagation cares about nothing
    /// else.** A rune pays over the monsters unearthed after the remnant that carries it, so moving
    /// a remnant earlier - or moving a monster-heavy blast later - is worth points without changing
    /// which markers are caught at all.
    ///
    /// Measured on one dig site: the best chain found and a chain worth a hundred and thirty points
    /// less both caught three remnants in their first three links. The difference was almost
    /// entirely carried value, 365 against 290, because the better chain had more monster weight
    /// standing behind its remnants. Nothing in the search was manipulating that - Sweep changes
    /// WHICH spot a link uses and Reverse flips the whole chain, but the order of the spots already
    /// chosen was never touched.
    ///
    /// Every pair, which is ten swaps on a five link chain and a hundred on fifteen. Each one has to
    /// be legal before it can be scored: swapping two links moves both of their neighbours, so the
    /// whole chain is re-checked for reach and spacing rather than just the two ends.
    /// </summary>
    private static List<Vector2> Order(PlanEnvironment env, List<Vector2> chain, int pinned = 0)
    {
        if (chain.Count < 3)
            return chain;

        var score = Score(env, chain);

        // The links before the earlier half of a swap are untouched by it, and untouched again for
        // every partner tried - so they are folded once and carried. See Planner.WorthOfChainWithTail.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);
        var start = Math.Min(pinned, chain.Count);

        for (var k = 0; k < start; k++)
        {
            work.Add(chain[k]);
            Fold(env, tally, work, chain[k]);
        }

        // Swaps start past the pinned head, and since the partner is always later than i, a pinned
        // link is never one half of a swap either.
        for (var i = start; i < chain.Count - 1; i++)
        {
            for (var j = i + 1; j < chain.Count; j++)
            {
                (chain[i], chain[j]) = (chain[j], chain[i]);

                // The chain already holds the swap, so the tail from i IS the rearranged one.
                if (Walkable(env, chain) && WorthOfChainWithTail(env, tally, work, chain, i) is var now &&
                    now > score + 0.0001d)
                {
                    _orders++;
                    _orderGain += now - score;
                    score = now;

                    Tally();

                    continue;
                }

                (chain[i], chain[j]) = (chain[j], chain[i]);
            }

            // This link is settled, whatever it ended up being, so it joins the prefix.
            work.Add(chain[i]);
            Fold(env, tally, work, chain[i]);
        }

        return chain;
    }

    /// <summary>
    /// Reverses a stretch of the chain, which is the reordering move that survives a long chain.
    ///
    /// **Swapping two links breaks a fifteen link chain and almost never breaks a five link one.**
    /// That is why Order and Shift were measured doing nothing on a Grand site - one accepted swap
    /// and zero relocations across a 4.8 second solve - while they earn their keep on small sites. A
    /// chain is a path, every link has to be thrown from the one before it, and a transposition
    /// changes FOUR of those adjacencies at once. On a short loose chain one of them sometimes still
    /// fits; on a long one pressed against the reach, four never do.
    ///
    /// Reversing chain[i..j] changes TWO. Everything inside the stretch keeps its neighbours - the
    /// path through them simply runs the other way, and a throw is symmetric - so only the two cuts
    /// have to be re-checked. That is the classic answer to reordering a path under distance
    /// constraints and it is the right shape for this problem.
    ///
    /// And reordering is worth more here than anywhere. The score is content plus propagation, and
    /// propagation is the rune of each remnant applied to whatever is unearthed AFTER it - so the
    /// order the chain fires in is a large part of a fifteen link site's score and almost none of a
    /// five link one's. The operator that exploits it was the one that could not run.
    /// </summary>
    private static List<Vector2> Reverse(PlanEnvironment env, List<Vector2> chain, int pinned = 0)
    {
        if (chain.Count < 3)
            return chain;

        var score = Score(env, chain);

        // Everything before the stretch is untouched by reversing it, and untouched again for every
        // length tried - so it is folded once and carried. See Planner.WorthOfChainWithTail.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);
        var start = Math.Min(pinned, chain.Count);

        for (var k = 0; k < start; k++)
        {
            work.Add(chain[k]);
            Fold(env, tally, work, chain[k]);
        }

        // Stretches start past the pinned head, and a pinned link is never inside one either, since
        // the stretch runs from i upwards.
        for (var i = start; i < chain.Count - 1; i++)
        {
            for (var j = i + 1; j < chain.Count; j++)
            {
                chain.Reverse(i, j - i + 1);

                // The chain already holds the reversal, so the tail from i IS the rearranged one.
                if (Walkable(env, chain) && WorthOfChainWithTail(env, tally, work, chain, i) is var now &&
                    now > score + 0.0001d)
                {
                    _reversals++;
                    _reverseGain += now - score;
                    score = now;

                    Tally();

                    continue;
                }

                chain.Reverse(i, j - i + 1);
            }

            // Settled, whichever way round it finished, so it joins the prefix.
            work.Add(chain[i]);
            Fold(env, tally, work, chain[i]);
        }

        return chain;
    }

    /// <summary>
    /// The targets a chain does not cover, richest first.
    /// </summary>
    /// <param name="insisted">
    /// Only the ones weighted above their own worth, which is what being insisted on means. See
    /// PlanTarget.Must - nothing else in the environment is marked that way.
    /// </param>
    private static List<int> Missing(PlanEnvironment env, List<Vector2> chain, bool insisted,
        int keep)
    {
        var covers = CoverageOfEnvironment(env);
        var held = new HashSet<int>();

        foreach (var at in chain)
        {
            foreach (var index in covers.Of(at))
                held.Add(index);
        }

        var found = new List<int>();

        for (var t = 0; t < env.Targets.Count; t++)
        {
            var target = env.Targets[t];

            if (held.Contains(t))
                continue;

            if (insisted)
            {
                // Insistence used to show up only as a weight above the marker's own worth, and
                // this read that gap to tell a required marker from an ordinary one. Nothing
                // inflates a weight any more, so the gap is always nought and the flag is the whole
                // test. See PlanTarget.Must.
                if (target.Must)
                    found.Add(t);

                continue;
            }

            // Worth chasing at all. A monster marker on its own is not the thing a person marks by
            // hand, and trying every one of three hundred would cost more than the search it is
            // meant to help.
            if (WorthOfTarget(target) > 0f)
                found.Add(t);
        }

        if (found.Count > keep)
        {
            found.Sort((a, b) => WorthOfTarget(env.Targets[b]).CompareTo(WorthOfTarget(env.Targets[a])));
            found.RemoveRange(keep, found.Count - keep);
        }

        return found;
    }

    /// <summary>Whether every link of a chain can be thrown from the one before it, and is spaced.</summary>
    private static bool Walkable(PlanEnvironment env, List<Vector2> chain)
    {
        var least = env.Apart * env.Apart;

        for (var i = 0; i < chain.Count; i++)
        {
            if (!Reaches(env, i == 0 ? env.Origin : chain[i - 1], chain[i]))
                return false;

            for (var j = 0; j < i; j++)
            {
                if (env.Apart > 0f &&
                    Vector2.DistanceSquared(chain[i], chain[j]) < least)
                    return false;
            }

            // **And from the explosives already on the ground, which are links too.**
            //
            // The spacing rule was checked between the links of the chain being scored, and during
            // a part-placed solve that chain is only what is LEFT - so a remaining link could be
            // planned within the minimum distance of an explosive already down. The game then
            // refuses the placement, the cell is marked refused, the next solve steps one cell
            // along and is refused again. Seen as a line of twenty eight refused cells running
            // beside a placed link, with the plan degrading at every step and the run pushing at a
            // wall it could not see.
            //
            // The placed ones are exempt from the legality test - they are facts - but they are not
            // exempt from the geometry, because the game applies it to what is on the ground rather
            // than to what this solve happens to be thinking about.
            if (env.Apart <= 0f || env.Placed == null)
                continue;

            for (var k = 0; k < env.Placed.Count; k++)
            {
                var gap = Vector2.DistanceSquared(chain[i], env.Placed[k]);

                // **A link is not too close to itself.**
                //
                // A chain being rated whole - the filed best chain, say - contains the links that
                // have already been placed, so each of those meets its own copy in this list at a
                // distance of nought and failed the spacing test instantly. The chain was then
                // declared not to fit the ground, marked stale, and could neither be restored on an
                // undo nor used as a floor by the next solve: the site simply forgot its own best
                // answer and searched again from nothing.
                //
                // Same spot means same explosive. The rule is about two of them being too close.
                if (gap < 1f)
                    continue;

                if (gap < least)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tries every candidate in turn for every link, keeping the best.
    ///
    /// This is what the random tweaking was standing in for, and standing in badly. A tweak picks
    /// one link and one candidate out of several thousand at random, so the chance of it landing on
    /// the one position that matters is small enough that the time budget was being spent on
    /// lottery tickets. Measured against a chain laid by hand, what survived was always the LAST
    /// link - the one greedy chooses with the fewest options left and the one a random search is no
    /// more likely to fix than any other.
    ///
    /// Going through them all instead is affordable because most are not worth evaluating: a
    /// candidate out of reach of the links either side of this one cannot go here whatever it
    /// covers, and that check is a subtraction against the thousands of scoring passes it saves.
    /// </summary>
    /// <summary>
    /// The candidates that lie within reach of both neighbours, without visiting the rest.
    ///
    /// **A uniform grid, built once per candidate list and kept against it.** Candidates do not
    /// change inside a solve, so the buckets are shared by every sweep on every thread - and keyed on
    /// the list object through a weak table, so they are collected with it rather than accumulating
    /// one grid per solve for the life of the session.
    ///
    /// The cell is the reach, which makes the search three buckets across whatever the reach happens
    /// to be. Only the first neighbour's disc narrows the set; the second's test is a single squared
    /// distance and stays in the caller.
    ///
    /// **It returns a superset, not the answer.** A bucket is square and a reach is round, so the
    /// corners let through candidates the distance tests still reject - which is why those tests stay
    /// exactly where they were. This changes how many are visited, never which are accepted.
    /// </summary>
    private static IEnumerable<Vector2> Near(List<Vector2> candidates, Vector2 before, float reach)
    {
        var buckets = Bucketed.GetValue(candidates, list => new Buckets(list, reach));

        return buckets.Around(before);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<Vector2>, Buckets>
        Bucketed = new();

    /// <summary>Candidate positions filed by grid cell, so a disc can be enumerated. See Near.</summary>
    private sealed class Buckets
    {
        private readonly Dictionary<(int X, int Y), List<Vector2>> _cells = new();
        private readonly float _side;

        public Buckets(List<Vector2> candidates, float reach)
        {
            _side = MathF.Max(1f, reach);

            foreach (var at in candidates)
            {
                var key = Cell(at);

                if (!_cells.TryGetValue(key, out var held))
                    _cells[key] = held = new List<Vector2>();

                held.Add(at);
            }
        }

        /// <summary>
        /// Everything within a reach of the point, as a superset.
        ///
        /// The cell is the reach, so a disc centred anywhere inside a cell reaches at most one cell
        /// further in each direction - three by three covers it whatever the reach is. The candidate
        /// also has to be within reach of the link AFTER it, which is not narrowed here: that test
        /// costs one squared distance and stays in the caller, where it already was.
        /// </summary>
        public IEnumerable<Vector2> Around(Vector2 before)
        {
            var middle = Cell(before);

            for (var y = middle.Y - 1; y <= middle.Y + 1; y++)
            {
                for (var x = middle.X - 1; x <= middle.X + 1; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var held))
                        continue;

                    for (var i = 0; i < held.Count; i++)
                        yield return held[i];
                }
            }
        }

        private (int X, int Y) Cell(Vector2 at) =>
            ((int)MathF.Floor(at.X / _side), (int)MathF.Floor(at.Y / _side));
    }

    private static List<Vector2> Sweep(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, Func<bool> waiting = null, int pinned = 0)
    {
        if (chain.Count == 0)
            return chain;

        var score = Score(env, chain);

        // **The prefix is the same question for every candidate, so it is asked once.**
        //
        // This is the widest loop in the search - every link against every candidate in the site -
        // and it rescored the whole chain for each of them. Everything BEFORE the link being
        // replaced is identical across all of those, and identical again at the next link but one
        // longer, so the tally is folded once and walked forward as the loop advances. Each trial
        // then pays for the candidate and the tail behind it rather than for the whole chain.
        //
        // See Planner.WorthOfSplicedChain, which does the folding and takes it back off, and Audit, which is
        // how the carried answer is known to agree with the honest one.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);
        var start = Math.Min(pinned, chain.Count);

        for (var k = 0; k < start; k++)
        {
            work.Add(chain[k]);
            Fold(env, tally, work, chain[k]);
        }

        for (var i = start; i < chain.Count; i++)
        {
            // Between links rather than between candidates. A link is a few hundred cheap distance
            // tests and a handful of expensive ones, so this is fine grained enough to stop within
            // a fraction of a second and coarse enough not to read the clock a million times.
            if (waiting?.Invoke() == false)
                break;

            var before = i == 0 ? env.Origin : chain[i - 1];
            var after = i + 1 < chain.Count ? chain[i + 1] : (Vector2?)null;
            var here = chain[i];

            // **Only the candidates that could possibly serve, found by position.** A replacement has
            // to sit within the reach of the link before it and of the link after it, so the ones
            // worth testing are the intersection of two discs - a small part of a site. Walking the
            // whole list to reject most of it on those two distance tests is the loop's bulk, and the
            // buckets answer it by arithmetic instead. Identical set, far fewer visits.
            // A cell wider than the reach, because Span measures from the near edges of two cells and
            // so allows a straight line up to about seven tenths of a cell longer. See Span.
            foreach (var candidate in Near(candidates, before, env.Reach + 1f))
            {
                if (candidate == here)
                    continue;

                // The cheap tests first, so a candidate on the far side of the site costs two
                // distance checks rather than a walk of every marker in the dig site - and the
                // ground between is only walked for one that survives them.
                //
                // **Span, the reach rule's own distance, not the straight line.** The straight line
                // refused links the rule and the game both allow: (700,819) to (726,714) is 108.17
                // straight and 107.6 by Span against a reach of 108, and a sweep that could not see
                // it left a chain on a Frigid Bluffs site 5 points short for three minutes of
                // solving, until some other move happened on it.
                if (Span(before, candidate) > env.Reach)
                    continue;

                if (after != null && Span(candidate, after.Value) > env.Reach)
                    continue;

                // **Separation before routing, because one is arithmetic and the other is a search.**
                // Spaced is a squared distance against each link already in the chain; Reaches routes
                // a wire. Asking them the other way round paid for a route on every candidate that
                // was going to be refused for sitting too close to an explosive.
                if (!Spaced(env, chain, candidate, i) ||
                    !Reaches(env, before, candidate) ||
                    (after != null && !Reaches(env, candidate, after.Value)))
                    continue;

                // Priced off the carried prefix rather than by rescoring the chain. The chain
                // itself is only written when a candidate wins, so a rejected one costs nothing to
                // put back.
                var now = WorthOfSplicedChain(env, tally, work, candidate, chain, i + 1);

                if (now > score + 0.0001d)
                {
                    _sweeps++;
                    _sweepGain += now - score;
                    score = now;
                    here = candidate;
                    chain[i] = candidate;

                    Tally();
                }
            }

            chain[i] = here;

            // On to the next link, which makes this one part of the prefix. Folded rather than
            // rebuilt, so the whole sweep folds each link once however many candidates it tries.
            work.Add(chain[i]);
            Fold(env, tally, work, chain[i]);
        }

        return chain;
    }

    /// <summary>
    /// Slides each link about on the lattice, keeping anything that scores better.
    ///
    /// The candidate set is built from geometry and the geometry is only as good as the numbers in
    /// it - the blast radius, the marker extent, the lattice the game rounds to. This is the part
    /// that does not care: it tries the neighbourhood of every link and keeps what actually scores
    /// higher, so a candidate set that is slightly off still lands on the right spot.
    ///
    /// It is what closes the gap against a chain laid by hand, because a hand does exactly this -
    /// puts the circle roughly right and then shuffles it until one more thing lights up.
    ///
    /// Hill climbing, so it finds the best spot near the chain it is given and nothing further. It
    /// is cheap enough to run on every chain the search keeps rather than once at the end: a few
    /// hundred evaluations against a scoring function that walks five links and ninety markers.
    /// </summary>
    private static List<Vector2> Polish(PlanEnvironment env, List<Vector2> chain, int pinned = 0)
    {
        if (chain.Count == 0)
            return chain;

        var score = Score(env, chain);
        var moved = true;

        // The same carried prefix Sweep uses, and for the same reason: eighty neighbours of each
        // link on each of twelve passes all ask about a chain whose front half does not change. See
        // Planner.WorthOfSplicedChain.
        //
        // Allocated once and unwound at the end of each pass rather than rebuilt - Polish runs on
        // every chain the search keeps, so a tally per pass would be twelve allocations over the
        // target list for each of them.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);
        var start = Math.Min(pinned, chain.Count);

        // Bounded, because each pass only accepts a strict improvement and the score is bounded
        // above - but a loop that trusts that and nothing else is a loop that can hang the search.
        for (var pass = 0; pass < 12 && moved; pass++)
        {
            moved = false;

            for (var k = 0; k < start; k++)
            {
                work.Add(chain[k]);
                Fold(env, tally, work, chain[k]);
            }

            for (var i = start; i < chain.Count; i++)
            {
                // The best this link has been, which is what the neighbourhood is measured around
                // and what a rejected neighbour is put back to. Keeping the ORIGINAL position for
                // that instead would undo an improvement the moment the next neighbour failed.
                var here = chain[i];

                for (var dy = -PolishSpan; dy <= PolishSpan; dy++)
                {
                    for (var dx = -PolishSpan; dx <= PolishSpan; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        var to = new Vector2(here.X + dx, here.Y + dy);

                        if (!env.CanPlace(to))
                            continue;

                        // Mutated in place and put back, rather than copying the chain for each of
                        // the eighty neighbours of each of five links on each of twelve passes.
                        //
                        // Legal reads the chain, so the neighbour goes in before it is asked; the
                        // pricing does not - WorthOfSplicedChain is given the neighbour and the tail after it,
                        // and never looks at this slot.
                        chain[i] = to;

                        if (Legal(env, chain, i) &&
                            WorthOfSplicedChain(env, tally, work, to, chain, i + 1) is var now &&
                            now > score + 0.0001d)
                        {
                            score = now;
                            here = to;
                            moved = true;
                        }
                        else
                        {
                            chain[i] = here;
                        }
                    }
                }

                // This link is settled for the pass, so it becomes part of the prefix the next one
                // is measured against. chain[i] already holds whatever won, so nothing is restored.
                work.Add(chain[i]);
                Fold(env, tally, work, chain[i]);
            }

            // Wound all the way back, so the next pass starts from an empty tally without
            // allocating another one. Reverse order, because Pop takes back the last link.
            for (var k = work.Count - 1; k >= 0; k--)
            {
                var was = work[k];

                work.RemoveAt(k);
                Pop(env, tally, work, was);
            }
        }

        return chain;
    }

    /// <summary>How far, in grid units, a link may be slid in each direction while polishing.</summary>
    private const int PolishSpan = 4;

    /// <summary>Whether link <paramref name="index"/> still reaches its neighbours, and can get there.</summary>
    private static bool Legal(PlanEnvironment env, List<Vector2> chain, int index)
    {
        var before = index == 0 ? env.Origin : chain[index - 1];

        if (!Reaches(env, before, chain[index]))
            return false;

        if (!Spaced(env, chain, chain[index], index))
            return false;

        return index + 1 >= chain.Count || Reaches(env, chain[index], chain[index + 1]);
    }

    /// <summary>
    /// Whether an explosive may go here, given the ones already in the chain and already on the
    /// ground.
    ///
    /// The game refuses two explosives closer than about twenty and a half grid units. Measured:
    /// five placed by hand as close together as the game would allow came out at 20.81, 20.81,
    /// 20.62 and 20.52 apart, so the true limit is at or a little under 20.5 and the default is set
    /// just above it - a spot the planner offers and the game then refuses is a stuck placement
    /// sequence, where a spot it declines to offer costs a little coverage and nothing else.
    ///
    /// The DETONATOR is exempt, and that is not an oversight: the first of those five went down one
    /// grid unit from it. Whatever the rule is, it is about explosives and not about the machine.
    ///
    /// Every pair, not just consecutive links. A chain that sets off in one direction and comes back
    /// can bring its fifth explosive alongside its first, and the game will refuse that exactly as
    /// readily as it refuses two in a row.
    /// </summary>
    internal static bool Spaced(PlanEnvironment env, List<Vector2> chain, Vector2 at, int ignore = -1)
    {
        if (env.Apart <= 0f)
            return true;

        var least = env.Apart * env.Apart;

        for (var i = 0; i < chain.Count; i++)
        {
            if (i != ignore && Vector2.DistanceSquared(chain[i], at) < least)
                return false;
        }

        if (env.Placed != null)
        {
            for (var i = 0; i < env.Placed.Count; i++)
            {
                if (Vector2.DistanceSquared(env.Placed[i], at) < least)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether one link can follow another: near enough, and with clear ground between.
    ///
    /// Walking the ground between is not a guess: the PoE1 planner this design descends from does
    /// the same thing, interpolating a handful of points along each new segment and requiring all
    /// of them to pass. It also refuses a rectangle around the detonator - which PoE2 evidently
    /// does not have, since an explosive has been placed one grid unit from ours - so that part is
    /// deliberately not carried over.
    ///
    /// Distance first, always, because it is a subtraction and the ground test walks the whole
    /// segment a cell at a time. Most rejections are on distance.
    /// </summary>
    /// <summary>
    /// Whether a link fits, with the doubt resolved the way the environment says to resolve it.
    ///
    /// Yes is always yes. Unknown is a yes only when the search has been told to try what the router
    /// has not reached. No is always no: that one is a fact about the
    /// ground rather than about the clock.
    /// </summary>
    internal static bool Reaches(PlanEnvironment env, Vector2 from, Vector2 to)
    {
        using var phase = new Phase(PhaseReaches);

        return ReachesInner(env, from, to);
    }

    /// <summary>The body of Reaches, wrapped so its allocation is attributed. See Phases.</summary>
    private static bool ReachesInner(PlanEnvironment env, Vector2 from, Vector2 to)
    {
        var said = Says(env, from, to);

        return said == Certainty.Yes;
    }

    /// <summary>
    /// The same question with the doubt left in. See <see cref="Certainty"/>.
    ///
    /// Distance first, always: it is a subtraction where the ground test walks the whole segment a
    /// cell at a time, and a pair too far apart is a definite no whatever the router knows.
    /// </summary>
    /// <summary>
    /// How far apart two spots are for the purpose of reach, which is not centre to centre.
    ///
    /// **The game measures to the nearest point of the destination CELL, and measuring to its centre
    /// is what refused 280 of 3,158 spots it had accepted.** A grid cell is a square a unit across;
    /// an explosive goes somewhere in it, not on its midpoint, so a cell whose centre sits past the
    /// limit can still have ground inside it that does not.
    ///
    /// Measured, on 4,224 cells the client ruled on from one explosive: under this rule the furthest
    /// accepted cell is 89.90 and NOT ONE of the 3,158 accepted cells exceeds 90.00, while the first
    /// refusals past them land at 90.08 and 90.20. A clean one-sided fit against a round number - and
    /// the round number is the reach the plugin already had.
    ///
    /// It also accounts for a spread that looked like noise. As a centre distance the same rule
    /// allows 90.50 along an axis, 90.59 at fifteen degrees, 90.64 at thirty and 91.22 at forty five,
    /// which is exactly the per-bearing variation the swept frontier showed. There is no single
    /// centre-distance limit to find, which is why every constant tried for one was a compromise.
    ///
    /// The allowance is at the destination end only. The source is an explosive standing at a known
    /// point rather than a square being chosen, and the data fits with one half-cell, not two.
    /// </summary>
    internal static float Span(Vector2 from, Vector2 to)
    {
        var dx = MathF.Max(MathF.Abs(to.X - from.X) - 0.5f, 0f);
        var dy = MathF.Max(MathF.Abs(to.Y - from.Y) - 0.5f, 0f);

        return MathF.Sqrt(dx * dx + dy * dy);
    }

    internal static Certainty Says(PlanEnvironment env, Vector2 from, Vector2 to)
    {
        // The straight line first, because the wire is never shorter than it - so this rules a pair
        // out without routing anything. What it cannot do is rule one IN: reach is a budget spent
        // along the wire, and a link whose ends are close but whose wire goes round something is
        // placed short of where it was aimed. See Wire and PlanEnvironment.Wired.
        if (Span(from, to) > env.Reach)
            return Certainty.No;

        // Not the wire's LENGTH against the reach - where its clamped end lands. Over open ground
        // a spot just past the budget still takes the bomb, because the cut end rounds back onto
        // it; round an obstacle the wire doubles back and it does not. See Wire.Lands.
        if (env.Lands != null && !env.Lands(from, to))
            return Certainty.No;

        // **Not asked again once Lands has answered, because it is the same question.**
        //
        // Planning builds CanReach as Reachable(terrain, blocking, range), whose whole body is
        // `lands != null && !lands(from, to) ? No : Yes` over the very delegate handed to Lands -
        // so a pair that passed the test above passes it again, having routed the wire a second
        // time to find out.
        //
        // That is the hottest path in the plugin. Wire.Landing calls Bend, which builds a polyline
        // per call, and the exclusive phase table put reaches at 176MB over 1,288,169 calls - 137
        // bytes apiece, allocated twice over for one answer.
        //
        // Rests on there being ONE place that builds a PlanEnvironment, which there is
        // (Planning.cs), and on CanReach being derived from the same Landing delegate there. If a
        // second construction ever passes a CanReach that asks something Lands does not, this has
        // to go back to asking both.
        if (env.Lands != null)
            return Certainty.Yes;

        return env.CanReach == null ? Certainty.Yes : env.CanReach(from, to);
    }

    /// <summary>
    /// The objective, in chain order, with its terms kept apart.
    ///
    /// <paramref name="detail"/> is off for the millions of calls the search makes and on for the
    /// handful a score card makes - the per-link text is the only part that allocates, and the
    /// search must not pay for a readout nobody is reading.
    /// </summary>
    /// <summary>Whether one of the game's own placed explosives is already on this spot.</summary>
    private static bool Standing(PlanEnvironment env, Vector2 at)
    {
        if (env.Placed == null)
            return false;

        for (var i = 0; i < env.Placed.Count; i++)
        {
            var was = env.Placed[i];

            if (MathF.Abs(was.X - at.X) < 1f && MathF.Abs(was.Y - at.Y) < 1f)
                return true;
        }

        return false;
    }

    internal static Verdict Evaluate(PlanEnvironment env, List<Vector2> chain, bool detail = false)
    {
        // On the whole site, over the explosives down and the chain, when explosives are down. A chain that already
        // starts with them - the whole route a caller holds - is scored as it is. See PlanEnvironment.Whole.
        if (env.Whole is { } whole && env.Head is { Count: > 0 } head && chain != null)
            return Evaluate(whole, StartsWithHead(chain, head) ? chain : Headed(head, chain, ref _headedForEvaluate), detail);

        _verdicts++;

        using var phase = new Phase(PhaseEvaluate);

        // **A chain standing on ground the game will not take is worth nothing, and the objective
        // never said so.**
        //
        // CanPlace was consulted in a dozen places - building the candidate list, sweeping,
        // relocating, fetching - and in none of them was it the scoring rule. That works only while
        // every producer remembers, and the one thing that cannot remember is a chain made EARLIER:
        // the game refuses a spot, the refusal is recorded, and the chain already on file still
        // scores what it always scored. It is then handed to the next solve as a seed, filed as the
        // site's best, and published to the screen, because nothing in the objective has an opinion
        // about legality. Seen on a live site: the placement run refused (694,1167), the re-solve it
        // triggered returned a chain starting at (694,1167), and pressing again reproduced it.
        //
        // Made a property of the score instead, where it cannot be forgotten: an illegal chain
        // loses to every legal one, including the empty one, so the search abandons it by preferring
        // anything else and a filed chain containing a refused spot stops being the floor.
        //
        // Cheap enough to be unconditional. CanPlace is a dictionary lookup against the refusals and
        // at most two grid reads; a link is a few of those against the thousands of markers each
        // score already walks.
        if (chain != null)
        {
            for (var i = 0; i < chain.Count; i++)
            {
                if (env.CanPlace(chain[i]))
                    continue;

                // **A link already on the ground is a fact, not a proposal.**
                //
                // The moment an explosive goes down, the game stops accepting a placement on that
                // spot - so every chain containing it failed the test above, including the true
                // one, and the score read minus infinity for the rest of the run. Legality is a
                // question about where the NEXT explosive may go; about the ones already placed it
                // has no standing, and answering it anyway condemned the chain for having worked.
                if (Standing(env, chain[i]))
                    continue;

                return new Verdict(double.NegativeInfinity, 0d, 0d, 0,
                    detail
                        ? new List<string>
                        {
                            $"link {i + 1} at ({chain[i].X:0},{chain[i].Y:0}) stands where the game " +
                            "will not take an explosive, so this chain cannot be placed",
                        }
                        : null);
            }
        }

        var count = env.Targets.Count;

        // Scratch, reused. This is called tens of thousands of times per solve now that each link
        // is tried against every candidate, and three arrays per call was making the search's main
        // cost the garbage collector rather than the arithmetic. Per thread, because a solve runs
        // off the main one and there is no promise only one runs at a time.
        // Stamped rather than cleared, and the covered ones listed as they are found.
        //
        // **Two full passes over every marker were being paid on every score.** One to blank the
        // array, one at the end to find the quarter of it that had been filled in - and this runs
        // half a million times in a complete search, so it is a third of the cost and none of it is
        // work. A visit number makes the blanking free, and a list of what was touched makes the
        // tail run over the markers actually caught instead of the whole site.
        var step = Buffer(ref _step, count);
        var seen = Buffer(ref _seen, count);
        var touched = Buffer(ref _touched, count);
        var found = 0;
        var visit = ++_visits;

        var content = 0d;
        var covered = 0;
        var musts = 0;
        var travel = 0d;
        var from = env.Origin;

        // The weight of MONSTERS each link unearths, so the propagation term can be worked out from
        // a running total. Monsters rather than remnants because that is what a carried rune
        // affects, and their weight rather than their number because a rare is worth more uplift
        // than a runic one.
        var monsters = Grow(_monsters, chain.Count);
        _monsters = monsters;

        // **One array per tag some effect here is scoped to, and none at all when none is.**
        //
        // Almost every dig site references no tags: nothing is scoped, this is null, and every line
        // below that touches it is skipped, so the ordinary case pays nothing for the machinery. A
        // site with scoped effects normally references one or two.
        //
        // The monster tag is deliberately NOT one of these - it is the accumulator that already
        // exists, so "monster=50" is arithmetically identical to the flat 50 it migrated from
        // rather than merely similar. See Tags and Weighing.ScopedEffectsOfTarget.
        var scoped = env.Scoped;
        var tagged = Tagged(scoped, chain.Count);
        var classed = Classes(env, chain.Count);
        var counted = HowMany(env, chain.Count);

        for (var i = 0; i < chain.Count; i++)
            monsters[i] = 0f;

        // Which markers each link catches, from the index rather than from the geometry.
        //
        // **This loop is the search's single biggest cost.** Every link against every marker is four
        // hundred distance tests a chain, and a complete stepwise search scores half a million
        // chains - measured at fifteen microseconds each, near eight of the nine seconds such a
        // search takes. A cell's coverage never changes, so it is looked up: the loop then runs over
        // the five or so markers a blast actually catches instead of the eighty three it does not.
        var covers = CoverageOfEnvironment(env);

        for (var s = 0; s < chain.Count; s++)
        {
            var point = chain[s];

            foreach (var caught in covers.Of(point))
            {
                // **A barrel takes everything its own blast reaches, and there it stops.** So what
                // a link catches is not the coverage index alone: anything in it that detonates adds
                // what its own circle covers to the same step. A barrel inside THAT is content it
                // catches and nothing more - these objects do not set each other off, which is why
                // only the directly caught one is expanded below. Walked with a worklist rather than
                // another loop over the site, since nearly every link catches no barrel at all and
                // pays nothing for the possibility.
                //
                // The step is the LINK's, not the barrel's: the secondary blast happens when the
                // explosive that set it off goes off, so everything it unearths comes up at that
                // point in the chain and a rune carried earlier reaches it. See PlanTarget.Sets.
                var work = Buffer(ref _chained, env.Targets.Count);
                var depth = 0;

                work[depth++] = caught;

                while (depth > 0)
                {
                    var i = work[--depth];

                    if (seen[i] == visit)
                        continue;

                    var target = env.Targets[i];

                    // Only the object the explosive itself reached. A barrel standing inside
                    // another barrel's blast is left as content, because the second blast never
                    // happens - see Planning.Chained.
                    if (i == caught && target.Sets is { Length: > 0 } sets)
                    {
                        foreach (var also in sets)
                        {
                            if (seen[also] != visit && depth < work.Length)
                                work[depth++] = also;
                        }
                    }


                // Wanted and caught are both already true of anything in the index - it is built
                // from exactly those two tests - so neither is repeated here.
                seen[i] = visit;
                step[i] = s;
                content += target.Weight;
                covered++;

                // Counted where the coverage is already being walked, so refusing a chain costs
                // nothing per score. See Verdict.Missed.
                if (target.Must)
                    musts += target.MustWeight;

                // Listed only if it can still pay something later.
                //
                // **The settling loop ran over every marker the chain catches and most have nothing
                // for it.** A chest has no combinations and carries no rune, so it contributes no
                // reward and no propagation there - its weight was already counted above. Twenty
                // five of those a chain against three that matter, so they are sorted here, where
                // the coverage is being walked anyway.
                // **Or granting a switch**, which was the other half of the same bug. A relic
                // whose only effect pays once has no Choices and no Carries - the non-stacking ones
                // are deliberately kept out of Carries - so it never reached the settling loop, and
                // the loop is where the once-only booking happens. Its switch was therefore never
                // booked at all: not deduplicated, not even credited. See PlanTarget.NonStacking.
                // **And a third time, for a scoped carry.** The migration moved every flat carry
                // into a scope - a bare percentage always meant the monsters unearthed downstream,
                // so it became "monster=n" - which emptied Carries on exactly the targets that were
                // propagating, and this test stopped naming them. Measured: a site scoring 7,831
                // came back at 5,627 over an identical chain, the whole difference being propagation
                // that was computed and then never read.
                // **And a fourth time, for anything that digs monsters up.** The payout pays a
                // marker's waves one marker at a time, because a remnant's ordinary runes reach its
                // own waves and nothing else - so a marker with waves that never reaches this list
                // has its monsters counted for coverage and paid nothing at all. A strongbox has no
                // combinations, carries nothing and grants nothing, and its guards are exactly that
                // case.
                if (target.Choices is { Length: > 0 } || target.Carries > 0f ||
                    target.NonStacking is { Length: > 0 } || target.Spread is { Length: > 0 })
                    touched[found++] = i;

                // EVERY tier counts, and this is worth stating because the guides make it look
                // otherwise. "Only Runic Monsters are affected by the modifiers from Remnants" is
                // drawing a line between expedition monsters and the ordinary map ones standing
                // around outside the dig site - not between the expedition tiers. Everything
                // unearthed by an explosive is a runic monster in that sense, elitemarker rares
                // included, and all of it is affected.
                //
                // Asked of the target rather than worked out here, and asked in one place - the
                // incremental tally below reads the same property. **There were three copies of this
                // rule once, they disagreed, and the disagreement was a must-have bonus leaking into
                // propagation**: two of them took a marked marker's inflated Weight where the third
                // took its Worth, so a marked rare put fifteen hundred points of imaginary monsters
                // under every carried rune on the site. One property, floored once, cannot drift.
                monsters[s] += target.MonstersUnearthed;

                // **The same weight again, bucketed by everything it is at once.** tagged keeps
                // one total per tag, which is all an additive pool ever needed; the grouped payout
                // needs to know that a wave rare is a monster AND a rare monster AND modifiable,
                // because that is what decides which modifiers meet on it. See PlanEnvironment
                // .Relevant and Settle's payout.
                //
                // A marker's own weight only. What it digs up is paid per marker below, since a
                // remnant's ordinary runes reach its own waves and nothing else.
                var contributions = classed != null || tagged != null
                    ? ContributionsOfTarget(target)
                    : null;

                if (classed != null)
                {
                    // **What they are worth, and how many of them there are.** A share multiplies
                    // the weight; a flat effect adds per thing it reaches, and cannot be paid at
                    // all without the count. See Settle's flats.
                    foreach (var (mask, part, many) in contributions)
                    {
                        var cls = ClassOfMask(env, mask);

                        classed[cls][s] += part;

                        if (counted != null)
                            counted[cls][s] += many;
                    }
                }

                if (tagged != null)
                {
                    foreach (var (mask, part, _) in contributions)
                    {
                        for (var k = 0; k < scoped.Length; k++)
                        {
                            if ((mask & (1L << scoped[k])) != 0L)
                                tagged[k][s] += part;
                        }
                    }

                    // **And the parts the chain-wide payout does not settle**, which wear the tags
                    // of what they are rather than of the marker that sent them. A remnant is tagged
                    // remnant, so a tier-scoped effect reaches its waves only through this - its own
                    // entry above carries nothing, since its weight is all parts. The monster tag
                    // needs no exclusion here: Scopes never admits it, because the plain monster
                    // total already counts these. See PartsSettledPerMarker.
                    if (target.PartsSettledPerMarker && target.Parts is { } apart)
                    {
                        foreach (var (mask, part, _) in apart)
                        {
                            for (var k = 0; k < scoped.Length; k++)
                            {
                                if ((mask & (1L << scoped[k])) != 0L)
                                    tagged[k][s] += part;
                            }
                        }
                    }
                }
                }
            }

            travel += Vector2.Distance(from, point);
            from = point;
        }

        return Settle(env, chain, step, seen, visit, monsters, tagged, classed, counted, touched, found,
            content, covered, musts, travel, detail);
    }

    /// <summary>
    /// A chain's coverage, carried from link to link instead of recomputed.
    ///
    /// **Nine tenths of the scoring in a stepwise search is rescoring what it already scored.** Every
    /// leaf walks all five links against the markers, four of which its parent walked a moment ago
    /// and whose answer cannot have changed - coverage is a union, so adding a link adds whatever it
    /// catches that nothing before it did, and takes nothing away.
    ///
    /// What cannot be carried is the settling: a rune's worth is decided by what comes after the
    /// remnant, so every one of those has to be worked out again each time the chain grows. That
    /// half is small - three remnants against twenty five markers - which is why splitting the two
    /// is worth anything at all.
    /// </summary>
    internal sealed class Running
    {
        public int[] Step;
        public int[] Seen;
        public int[] Touched;
        public float[] Monsters;

        /// <summary>The per-tag accumulators, or null when nothing here is scoped. See Planner.Tagged.</summary>
        public float[][] Tagged;

        /// <summary>The per-class buckets the grouped payout reads. See Planner.Classes.</summary>
        public float[][] Classed;

        /// <summary>How many of each class, for the flat half of an effect. See Planner.HowMany.</summary>
        public float[][] Counted;
        public int[] Added;
        public int Found;
        public int Visit;
            public double Content;
        public int Covered;
        public int Musts;
        public double Travel;

        /// <summary>
        /// The whole site this tally scores in, when explosives are down, with them folded in first at the lowest
        /// steps; null otherwise. Every link folded after them sits Offset steps along. See PlanEnvironment.Whole.
        /// </summary>
        public PlanEnvironment Over;

        /// <summary>The explosives down, folded in first. See Over.</summary>
        public IReadOnlyList<Vector2> Head;

        /// <summary>How many steps the explosives down take, which a chain's own links come after. See Over.</summary>
        public int Offset;
    }

    /// <summary>
    /// Points a tally at the whole site and folds the explosives down into it, when this environment has them;
    /// nothing otherwise. Called after every reset, since a reset forgets them with the rest. See Running.Over.
    /// </summary>
    private static void Headed(PlanEnvironment env, Running tally)
    {
        if (env.Whole is not { } whole || env.Head is not { Count: > 0 } head)
        {
            (tally.Over, tally.Head, tally.Offset) = (null, null, 0);

            return;
        }

        (tally.Over, tally.Head, tally.Offset) = (whole, head, head.Count);

        for (var i = 0; i < head.Count; i++)
            FoldAt(whole, tally, i, i == 0 ? whole.Origin : head[i - 1], head[i]);
    }

    /// <summary>A running tally for one environment, ready for its first link.</summary>
    /// <summary>
    /// A tally for scratch use, reusing this thread's last one when it is the same shape.
    ///
    /// **Measured: Rebuild allocates 342MB of a 547MB solve, 74KB a call over 4,625 calls**, and
    /// almost all of it is here - Begin builds seven arrays plus two jagged ones of
    /// 2^Relevant.Length rows each, and Rebuild wants a fresh one for every hole it fills.
    ///
    /// Reused only when every dimension matches exactly - the target count, the link count, and the
    /// shapes Owning and Fresh derive from the environment. A tally that is merely BIG ENOUGH is
    /// not the same object: the scoring reads lengths off these arrays, so handing it a longer one
    /// changes answers rather than wasting space, and wrong answers here are the expensive kind.
    /// On a mismatch it builds a new one, which is what it did every time before.
    ///
    /// **Scratch means scratch.** The caller must let it die with the call and must not hold a
    /// second tally at the same time on the same thread, which is why this is not what Begin does.
    /// See Repair.RebuildInner, its only caller.
    /// </summary>
    internal static Running BeginScratch(PlanEnvironment env, int links)
    {
        var scored = env.Whole is { } whole && env.Head is { Count: > 0 } ? whole : env;
        var count = scored.Targets.Count;
        var wide = Math.Max(links, 1) + (ReferenceEquals(scored, env) ? 0 : env.Head.Count);
        var shape = (count, wide, scored.Relevant?.Length ?? -1, scored.Scoped?.Length ?? -1);

        _scratches ??= new Dictionary<(int, int, int, int), Running>();

        if (!_scratches.TryGetValue(shape, out var had))
        {
            // **One slot was not enough and the measurement said so.** The first version kept a
            // single tally and reused it when the shape matched; the shape is derived from
            // chain.Count + holes + 1 and holes varies from tear to tear, so it almost never
            // matched and Rebuild went on allocating 75KB a call - 305MB against 342MB before,
            // which is no change at all.
            //
            // A handful of shapes covers it: the chain length barely moves during a solve and the
            // number of holes is drawn from a small range, so the set is small and stable. Capped
            // anyway, because an unbounded per-thread cache of jagged arrays is a leak with a
            // different name.
            if (_scratches.Count >= 16)
                _scratches.Clear();

            had = Begin(env, links);
            _scratches[shape] = had;
            _scratchMisses++;

            return had;
        }

        _scratchHits++;

        Array.Clear(had.Step);
        Array.Clear(had.Seen);
        Array.Clear(had.Touched);
        Array.Clear(had.Added);
        Array.Clear(had.Monsters);

        if (had.Tagged != null)
            foreach (var row in had.Tagged)
                Array.Clear(row);

        foreach (var row in had.Classed)
            Array.Clear(row);

        foreach (var row in had.Counted)
            Array.Clear(row);

        had.Found = 0;
        had.Visit = 1;
        had.Content = 0d;
        had.Covered = 0;
        had.Musts = 0;
        had.Travel = 0d;

        Headed(env, had);

        return had;
    }

    [ThreadStatic] private static Dictionary<(int, int, int, int), Running> _scratches;

    [ThreadStatic] private static long _scratchHits;

    [ThreadStatic] private static long _scratchMisses;

    /// <summary>
    /// How often a scratch tally was reused rather than built. See BeginScratch.
    ///
    /// Reported because the first attempt at this reused nothing and looked exactly like an
    /// attempt that worked: the allocation figure moved from 342MB to 305MB, which is what a
    /// different site and a different window look like too.
    /// </summary>
    public static (long Hits, long Misses) Scratching => (Volatile.Read(ref _allScratchHits),
        Volatile.Read(ref _allScratchMisses));

    private static long _allScratchHits;

    private static long _allScratchMisses;


    internal static Running Begin(PlanEnvironment env, int links)
    {
        // Over the whole site and room for the explosives down too, when there are any. See PlanEnvironment.Whole.
        var scored = env.Whole is { } whole && env.Head is { Count: > 0 } ? whole : env;
        var wide = links + (ReferenceEquals(scored, env) ? 0 : env.Head.Count);
        var count = scored.Targets.Count;

        var tally = new Running
        {
            Step = new int[count],
            Seen = new int[count],
            Touched = new int[count],
            Added = new int[Math.Max(wide, 1) + 1],
            Monsters = new float[Math.Max(wide, 1)],
            Tagged = Fresh(scored, wide),
            Classed = Owning(scored, wide),
            Counted = Owning(scored, wide),
            Visit = 1,
        };

        Headed(env, tally);

        return tally;
    }

    /// <summary>
    /// Folds one link into the tally WITHOUT working out what the chain is now worth.
    ///
    /// **Settling is not part of adding a link, and charging every add for it is most of the cost.**
    /// Settle combines the whole tally into a figure - the grouped payout over every class, the
    /// must-have bonus, the travel - and it depends on the chain as a whole, so folding five links
    /// and settling five times does the same work five times for four answers nobody reads.
    ///
    /// Split so a caller that wants the worth of a chain it is about to build can fold the run and
    /// ask Price once.
    ///
    /// The step a link is folded at is its position in the chain, so a run must be folded in
    /// increasing order: the coverage index records which link was FIRST to catch each marker, and
    /// first means the lowest step rather than the earliest call.
    /// </summary>
    internal static void Fold(PlanEnvironment env, Running tally, List<Vector2> chain, Vector2 at)
    {
        // After the explosives down, when the tally holds them. See Running.Over.
        var previous = chain.Count >= 2 ? chain[^2] : tally.Offset > 0 ? tally.Head[tally.Offset - 1] : env.Origin;

        FoldAt(tally.Over ?? env, tally, tally.Offset + chain.Count - 1, previous, at);
    }

    /// <summary>Folds one link in at a given step, after a given point. See Fold.</summary>
    private static void FoldAt(PlanEnvironment env, Running tally, int s, Vector2 previous, Vector2 at)
    {
        var covers = CoverageOfEnvironment(env);

        tally.Added[s] = tally.Found;
        tally.Monsters[s] = 0f;

        if (tally.Tagged != null)
        {
            for (var k = 0; k < tally.Tagged.Length; k++)
                tally.Tagged[k][s] = 0f;
        }

        for (var c = 0; c < tally.Classed.Length; c++)
        {
            tally.Classed[c][s] = 0f;
            tally.Counted[c][s] = 0f;
        }

        // **And what a barrel caught here sets off, as Evaluate counts it.** This took only what the
        // blast itself covers, and Evaluate adds everything a directly caught barrel's own blast
        // reaches at the same step: on a Frigid Bluffs chain (2026-10-04) the two came out 373 apart,
        // the same at every link, so Sweep and Polish compared a spliced price 373 short against the
        // whole score and refused a 5 point nudge the game's own best chain then took. Only the
        // object the explosive reached is expanded, and only when it is new here, as in Evaluate; a
        // barrel inside a barrel's blast is content. See PlanTarget.Sets.
        foreach (var caught in covers.Of(at))
        {
            if (tally.Seen[caught] == tally.Visit)
                continue;

            Take(caught);

            if (env.Targets[caught].Sets is { Length: > 0 } sets)
            {
                foreach (var also in sets)
                {
                    if (tally.Seen[also] != tally.Visit)
                        Take(also);
                }
            }
        }

        void Take(int i)
        {
            var target = env.Targets[i];

            tally.Seen[i] = tally.Visit;
            tally.Step[i] = s;
            tally.Content += target.Weight;
            tally.Covered++;

            if (target.Must)
                tally.Musts += target.MustWeight;

            if (target.Choices is { Length: > 0 } || target.Carries > 0f ||
                target.NonStacking is { Length: > 0 } || target.Spread is { Length: > 0 })
                tally.Touched[tally.Found++] = i;

            // The same property Evaluate reads, so the incremental scorer and the whole one cannot
            // drift. This used to be a second copy of the rule and the copy was missing two kinds,
            // so a siren egg's monsters were priced at nothing for propagation here and at full
            // value there. It is not a rule any more, it is a read.
            tally.Monsters[s] += target.MonstersUnearthed;

            var contributions = ContributionsOfTarget(target);

            foreach (var (mask, part, many) in contributions)
            {
                var cls = ClassOfMask(env, mask);

                tally.Classed[cls][s] += part;

                if (tally.Counted != null)
                    tally.Counted[cls][s] += many;
            }

            if (tally.Tagged != null)
            {
                var scoped = env.Scoped;

                foreach (var (mask, worth, _) in contributions)
                for (var k = 0; k < scoped.Length; k++)
                {
                    if ((mask & (1L << scoped[k])) != 0L)
                        tally.Tagged[k][s] += worth;
                }

                // **And what it digs up, which Evaluate has always added here and this did not.**
                //
                // The monsters a marker unearths wear the tags of monsters rather than of the marker
                // that sent them - see Weighing.PartsOfTarget - so a tier-scoped effect reaches the rares
                // in a remnant's waves. Evaluate folds that in; this loop never did, and Tagged is the
                // only thing scoped propagation reads.
                //
                // That is the whole of a drift that took an evening to find: content, coverage and
                // travel came out exact to three decimals over a million trials while propagation ran
                // a third short, deterministically, on every chain. Fifty four per cent of the
                // incremental scores the search steers by disagreed with the full evaluation - and
                // always in the same direction, so the search systematically undervalued propagation,
                // which is two thirds of what a good chain here is made of.
                //
                // The plain monster total never had the gap because MonstersUnearthed folds the
                // parts in, which is why nothing else disagreed and why the totals alone could not
                // say where to look. Same rule, same place in both, so the two cannot drift again.
                if (target.PartsSettledPerMarker && target.Parts is { } apart)
                {
                    foreach (var (mask, part, _) in apart)
                    for (var k = 0; k < scoped.Length; k++)
                    {
                        if ((mask & (1L << scoped[k])) != 0L)
                            tally.Tagged[k][s] += part;
                    }
                }
            }
        }

        tally.Travel += Vector2.Distance(previous, at);
    }

    /// <summary>
    /// Folds a run of links onto a tally and prices the result, then takes them all back off.
    ///
    /// **The shape every trial in the local search has, and the reason the prefix is worth keeping.**
    /// Sweep replaces one link and asks what the chain is now worth, for every candidate in the
    /// site; Rebuild inserts one and asks the same. In both, everything before the touched link is
    /// identical across all those questions - so it is folded once, and each trial pays only for the
    /// link it is trying and the tail behind it.
    ///
    /// <paramref name="work"/> must hold exactly the prefix, and <paramref name="tally"/> must have
    /// been folded over it. Both come back in that state, so the caller loops without rebuilding
    /// anything.
    ///
    /// The tail is folded in increasing order, which is required: the coverage index records which
    /// link was FIRST to catch a marker and first means the lowest step. Unfolded in reverse for the
    /// same reason - Pop takes the last link back, and takes it back exactly.
    /// </summary>
    /// <param name="first">The link being tried, folded at the prefix's own step.</param>
    /// <param name="tail">Where the rest comes from, and <paramref name="from"/> where it starts.</param>
    internal static double WorthOfSplicedChain(PlanEnvironment env, Running tally, List<Vector2> work,
        Vector2 first, List<Vector2> tail, int from)
    {
        var floor = work.Count;

        work.Add(first);
        Fold(env, tally, work, first);

        for (var k = from; k < tail.Count; k++)
        {
            work.Add(tail[k]);
            Fold(env, tally, work, tail[k]);
        }

        var worth = Price(env, tally, work);

        for (var k = work.Count - 1; k >= floor; k--)
        {
            var was = work[k];

            work.RemoveAt(k);
            Pop(env, tally, work, was);
        }

        return worth;
    }

    /// <summary>
    /// Prices the prefix already folded plus a tail read from wherever the caller has it, then takes
    /// the tail back off.
    ///
    /// **The general form, of which WorthOfSplicedChain is the special case.** That one takes a new link
    /// contiguous tail, which fits an insertion and a replacement and fits none of the moves that
    /// rearrange a chain: a swap, a reversed stretch and a link moved along all produce a tail that
    /// is a PERMUTATION of what was there, not an extension of it. Those three between them are most
    /// of what the local search does.
    ///
    /// The caller is expected to have made the rearrangement in the list it hands over - Order and
    /// Reverse mutate the chain in place before testing it, and Shift builds the moved copy - so
    /// this reads the tail as it stands rather than being told how it differs. Nothing here needs to
    /// know what kind of move it was.
    ///
    /// <paramref name="work"/> must hold exactly the prefix and comes back holding it. The tail is
    /// folded in increasing order and unfolded in reverse, for the reasons given on WorthOfSplicedChain.
    /// </summary>
    internal static double WorthOfChainWithTail(PlanEnvironment env, Running tally, List<Vector2> work,
        List<Vector2> tail, int from)
    {
        var floor = work.Count;

        for (var k = from; k < tail.Count; k++)
        {
            work.Add(tail[k]);
            Fold(env, tally, work, tail[k]);
        }

        var worth = Price(env, tally, work);

        for (var k = work.Count - 1; k >= floor; k--)
        {
            var was = work[k];

            work.RemoveAt(k);
            Pop(env, tally, work, was);
        }

        return worth;
    }

    /// <summary>
    /// Winds a carried prefix to a given depth, folding or unfolding as needed.
    ///
    /// For a loop whose fixed part grows and shrinks - Shift, where the untouched head runs to the
    /// lower of the two positions being exchanged. Folds forward when the depth rises, pops back
    /// when it falls, and does nothing when it is already there.
    /// </summary>
    internal static void Wind(PlanEnvironment env, Running tally, List<Vector2> work,
        List<Vector2> chain, int depth)
    {
        while (work.Count > depth)
        {
            var was = work[^1];

            work.RemoveAt(work.Count - 1);
            Pop(env, tally, work, was);
        }

        while (work.Count < depth && work.Count < chain.Count)
        {
            var at = chain[work.Count];

            work.Add(at);
            Fold(env, tally, work, at);
        }
    }

    /// <summary>
    /// Empties a tally so it can be folded again from nothing, without allocating another.
    ///
    /// **Bumping the stamp rather than blanking the arrays**, which is the same trick Pop uses to
    /// forget one link: everything keyed on the visit is invalidated at a stroke, and the arrays
    /// that are written before they are read - the per-step ones - need nothing done to them. What
    /// does have to be cleared is the handful of running totals, because those accumulate.
    ///
    /// For a caller that walks several independent chains over one tally, which is every one of them
    /// here: the local search does a pass per round and the repair a hole per tear, and allocating a
    /// tally over the whole target list for each would be most of what they cost.
    /// </summary>
    internal static void Unfold(PlanEnvironment env, Running tally)
    {
        tally.Visit++;
        tally.Found = 0;
        tally.Content = 0d;
        tally.Covered = 0;
        tally.Musts = 0;
        tally.Travel = 0d;

        Headed(env, tally);
    }

    /// <summary>What the chain in the tally is worth, without changing it. See Fold.</summary>
    internal static double Price(PlanEnvironment env, Running tally, List<Vector2> chain)
    {
        // Its own phase, so a trial's cost splits into the folding its caller does and the settling here.
        using var phase = new Phase(PhaseSettle);

        // Over the whole site, the explosives down in front, when the tally holds them. See Running.Over.
        if (tally.Over is { } over)
        {
            env = over;
            chain = Headed(tally.Head, chain, ref _headedForPrice);
        }

        return Settle(env, chain, tally.Step, tally.Seen, tally.Visit, tally.Monsters, tally.Tagged,
            tally.Classed, tally.Counted, tally.Touched, tally.Found, tally.Content, tally.Covered,
            tally.Musts, tally.Travel, false).Total;
    }

    /// <summary>
    /// Where a combination ranking is written down, or null while nobody is reading.
    ///
    /// The comparison PlanTarget.Best makes is the one decision a remnant's worth turns on, and it
    /// was the one thing the trail could not show: the winner was published and the losers
    /// forgotten. Thread static and set on the detailed pass only - the search runs Best millions of
    /// times and must not be building strings in it.
    /// </summary>
    [ThreadStatic] internal static List<string> ChoiceRankingTrail;

    /// <summary>
    /// Where a live tally differs from one folded over the same chain from scratch.
    ///
    /// The fresh one is by construction what Evaluate would compute, so the first field that differs is
    /// the one carrying the drift. Reports the field, the link, and both values - a name alone would
    /// leave the same guessing the totals did.
    /// </summary>
    private static string DriftBetweenTallies(PlanEnvironment env, Running tally, List<Vector2> chain)
    {
        // **Against what EVALUATE computes, not against another fold.**
        //
        // This first folded a fresh tally with the same Fold and compared the two, which cannot reveal
        // anything Fold omits - and what Fold omitted was the whole bug. It reported "every field
        // matches, the drift is inside Settle" for a drift that was in Fold, and sent the search for
        // it in the wrong place. A diagnostic that compares a thing against itself will always agree.
        //
        // Evaluate's own accumulation is the reference, so the gap is measured against the arithmetic
        // the incremental path is supposed to reproduce. Tagged is compared too, which it was not -
        // and Tagged was the field that differed.
        var fresh = Referenced(env, chain);

        var said = new List<string>();

        for (var s = 0; s < chain.Count && said.Count < 4; s++)
        {
            if (fresh.Tagged != null && tally.Tagged != null)
            {
                for (var k = 0; k < tally.Tagged.Length && said.Count < 4; k++)
                {
                    if (Math.Abs(fresh.Tagged[k][s] - tally.Tagged[k][s]) > 0.001f)
                    {
                        said.Add($"Tagged[{k}][{s}] {tally.Tagged[k][s]:N3} should be " +
                                 $"{fresh.Tagged[k][s]:N3}");
                    }
                }
            }

            if (Math.Abs(fresh.Monsters[s] - tally.Monsters[s]) > 0.001f)
                said.Add($"Monsters[{s}] {tally.Monsters[s]:N3} should be {fresh.Monsters[s]:N3}");

            for (var c = 0; c < tally.Classed.Length && said.Count < 4; c++)
            {
                if (Math.Abs(fresh.Classed[c][s] - tally.Classed[c][s]) > 0.001f)
                {
                    said.Add($"Classed[{c}][{s}] {tally.Classed[c][s]:N3} should be " +
                             $"{fresh.Classed[c][s]:N3}");
                }
            }
        }

        for (var i = 0; i < env.Targets.Count && said.Count < 8; i++)
        {
            if (fresh.Seen[i] == fresh.Visit && tally.Seen[i] == tally.Visit &&
                fresh.Step[i] != tally.Step[i])
                said.Add($"Step[{i}] link {tally.Step[i]} should be {fresh.Step[i]}");
        }

        if (Math.Abs(fresh.Content - tally.Content) > 0.001d)
            said.Add($"Content {tally.Content:N3} should be {fresh.Content:N3}");

        if (fresh.Covered != tally.Covered)
            said.Add($"Covered {tally.Covered} should be {fresh.Covered}");

        if (fresh.Found != tally.Found)
            said.Add($"Found {tally.Found} should be {fresh.Found}");

        return said.Count == 0
            ? "every accumulated field matches Evaluate's own - the drift is inside Settle"
            : string.Join(", ", said);
    }

    /// <summary>
    /// The tally as EVALUATE builds it, for the audit to compare a carried one against.
    ///
    /// Folds link by link like Fold does, but adds what each marker unearths into the tagged
    /// accumulators the way Evaluate's loop does - so the two differ exactly where the incremental
    /// path fails to reproduce the full one. See Drifted.
    /// </summary>
    private static Running Referenced(PlanEnvironment env, List<Vector2> chain)
    {
        var fresh = Begin(env, chain.Count);
        var walk = new List<Vector2>(chain.Count);

        foreach (var link in chain)
        {
            walk.Add(link);
            Fold(env, fresh, walk, link);
        }

        return fresh;
    }

    private static string _drifted = "";

    private static int _adrift;
    private static double _worst;
    private static string _worstAt = "";

    /// <summary>
    /// Checks a published plan against the ceiling that is supposed to be above it.
    ///
    /// **A bound is wrong in exactly the way an incremental tally is wrong: quietly.** Nothing
    /// crashes when RelaxedCeiling comes out below a reachable score - the search simply returns early
    /// believing it has won, and the card says it took everything. Four separate terms have gone
    /// missing from that method over its life and every one was found by a person reading two lines
    /// of the dump against each other, which is the check that should not need a person.
    ///
    /// So it is the same check the tally gets, and it is always on: the comparison is one
    /// subtraction against a figure computed once per solve, against a plan that is published at
    /// most twice. Nothing is asserted and nothing throws - the count and the worst case are kept
    /// for the dump, where a bound that has been beaten says so on its own.
    /// </summary>
    private static void BoundAudited(PlanEnvironment env, double scored)
    {
        var bound = RelaxedCeiling(env);

        if (bound <= 0d || double.IsInfinity(scored) || double.IsNaN(scored))
            return;

        lock (BoundGate)
        {
            _bounded++;

            if (scored <= bound + 0.0001d)
                return;

            _burst++;

            var over = scored - bound;

            if (over <= _worstBurst)
                return;

            _worstBurst = over;

            // Which half, since that is the whole question when one of these shows up. See
            // RelaxedCeilingTerms, filled by the call above.
            _worstBurstAt = $"{scored:N1} against {bound:N1} - the bound's content " +
                            $"{RelaxedCeilingTerms.Content:N1} and propagation {RelaxedCeilingTerms.Carried:N1}";
        }
    }

    /// <summary>Its own lock. It shared one with the incremental audit, which has gone.</summary>
    private static readonly object BoundGate = new();

    private static int _bounded;
    private static int _burst;
    private static double _worstBurst;
    private static string _worstBurstAt = "";

    /// <summary>Whether any published plan has beaten its ceiling. See BoundAudited.</summary>
    public static string Bounds =>
        _bounded == 0
            ? "no plan published yet"
            : _burst == 0
                ? $"{_bounded:N0} plans checked against the ceiling, every one under it"
                : $"{_bounded:N0} plans checked, {_burst:N0} ABOVE THE CEILING - worst " +
                  $"{_worstBurst:N1} over, at {_worstBurstAt}";

    /// <summary>
    /// Takes the last link back off, exactly.
    ///
    /// The markers it was the first to catch are forgotten by bumping the stamp on each of them, so
    /// nothing has to be blanked and a marker an earlier link also caught is untouched.
    /// </summary>
    internal static void Pop(PlanEnvironment env, Running tally, List<Vector2> chain, Vector2 at)
    {
        // After the explosives down, when the tally holds them. See Running.Over.
        var previous = chain.Count >= 1 ? chain[^1] : tally.Offset > 0 ? tally.Head[tally.Offset - 1] : env.Origin;

        PopAt(tally.Over ?? env, tally, tally.Offset + chain.Count, previous, at);
    }

    /// <summary>Takes back the link folded at a given step, after a given point. See Pop.</summary>
    private static void PopAt(PlanEnvironment env, Running tally, int s, Vector2 previous, Vector2 at)
    {
        var covers = CoverageOfEnvironment(env);

        // What Fold took at this step: the coverage, and what each barrel it caught new here set off.
        // The barrel's sets first, while its own step still says it was new here. See Fold.
        foreach (var caught in covers.Of(at))
        {
            if (tally.Seen[caught] != tally.Visit || tally.Step[caught] != s)
                continue;

            if (env.Targets[caught].Sets is { Length: > 0 } sets)
            {
                foreach (var also in sets)
                    Give(also);
            }

            Give(caught);
        }

        void Give(int i)
        {
            if (tally.Seen[i] != tally.Visit || tally.Step[i] != s)
                return;

            var target = env.Targets[i];

            tally.Seen[i] = 0;
            tally.Content -= target.Weight;
            tally.Covered--;

            if (target.Must)
                tally.Musts -= target.MustWeight;
        }

        tally.Found = tally.Added[s];
        tally.Travel -= Vector2.Distance(previous, at);
    }

    /// <summary>
    /// Everything after the coverage: the downstream totals, each remnant's combination, and the
    /// propagation they add up to.
    ///
    /// Shared by the ordinary scorer and the incremental one, so the two cannot drift. The coverage
    /// half is what a running chain can carry from link to link; this half depends on the whole
    /// chain - a rune's worth is decided by what comes after it - and has to be redone whenever the
    /// chain grows.
    /// </summary>
    private static Verdict Settle(PlanEnvironment env, List<Vector2> chain, int[] step, int[] seen,
        int visit, float[] monsters, float[][] tagged, float[][] classed, float[][] counted,
        int[] touched, int found,
        double content, int covered, int musts, double travel, bool detail)
    {
// Monster weight unearthed from each link ONWARDS, including that link's own.
        //
        // Inclusive because the game is: a remnant's modifiers reach "monsters unearthed by the
        // explosive placed on that Remnant, or an explosive placed after". The blast that sets a
        // remnant off also unearths whatever else it covers, and its own waves, and all of that is
        // affected. Counting only later links quietly threw away the largest single contribution a
        // rune ever makes - the one from the blast that picked it up.
        var after = Grow(_after, chain.Count);
        _after = after;

        // Nothing is credited to links that do not exist.
        //
        // **Tried, and it made partial chains outscore complete ones.** The idea was that a chain cut
        // off by a horizon values an early remnant against only the monsters inside that horizon, so
        // the links still to come were credited with the chain's average. It measured as no gain
        // when it went in, and then did real harm: a three link chain on a five explosive site came
        // back at 5,210.8 against the completed chain's 5,095.3, because two links' worth of
        // imaginary monsters were paying a rune that had nothing to carry. A score that rewards a
        // shorter answer is worse than a score that undervalues an early remnant.
        if (chain.Count > 0)
            after[chain.Count - 1] = monsters[chain.Count - 1];

        for (var s = chain.Count - 2; s >= 0; s--)
            after[s] = after[s + 1] + monsters[s];

        // The same sum, per tag, and inclusive for the same reason: the blast that picks an effect
        // up also unearths what it covers, and that is affected too.
        var taggedAfter = Suffix(tagged, chain.Count);

        // **Content and propagation per link, booked where the scoring books them**, for the blast labels and the
        // score card. Content goes to the link that first caught the object it belongs to. See ScoreOfEachBlast and
        // Lines.
        var perLink = detail || _recordingLinks;

        if (perLink)
        {
            _credited = Grow(_credited, chain.Count);
            _contentAtLink = Grow(_contentAtLink, chain.Count);

            for (var k = 0; k < chain.Count; k++)
            {
                _credited[k] = 0d;
                _contentAtLink[k] = 0d;
            }

            // The base weight of everything caught, which the coverage walk added to content before this was called.
            for (var i = 0; i < env.Targets.Count; i++)
            {
                if (seen[i] == visit)
                    _contentAtLink[step[i]] += env.Targets[i].Weight;
            }

            _chosenByTarget = Grow(_chosenByTarget, env.Targets.Count);

            for (var i = 0; i < env.Targets.Count; i++)
                _chosenByTarget[i] = -1;

            _linksRecorded = chain.Count;
        }

        void ContentToLink(int at, double amount)
        {
            if (perLink && at >= 0 && at < chain.Count)
                _contentAtLink[at] += amount;
        }

        // **Gaining Traction: a remnant's magic and rare packs, and its rares' modifiers, grow with every remnant
        // completed before it.** The scales are TractionScales'.
        //
        // The chain does not go on until a detonation is cleared, so a remnant first caught at link s comes after
        // every remnant caught at an earlier link, and after those completed elsewhere in the map. Its magic and rare
        // wave share is scaled by one plus the rate times that count: in its own worth, in the monster pools the runes
        // upstream of it pay on (every step up to its own, since those pools are inclusive suffix sums), and in the
        // payout on its own waves below. Taken from the game's text, "50% increased Monster Rarity per Remnant
        // Completed in Area", whose stat names magic and rare packs. Watched on one Grand site (2026-09-30) the
        // rise per remnant was near the text's; across seven sites it read lower, with the two confounded by rune
        // counts. "Completed before" is detonated before: every remnant an earlier link catches.
        var tractive = env.MagicPacksPerRemnantCompleted > 0f || env.RarePacksPerRemnantCompleted > 0f;
        var tractionOf = _tractionOf = Grow(_tractionOf, Math.Max(found, 1));
        var rareTractionOf = _rareTractionOf = Grow(_rareTractionOf, Math.Max(found, 1));
        var normalTractionOf = _normalTractionOf = Grow(_normalTractionOf, Math.Max(found, 1));
        var monstersExtra = _monstersExtra = Grow(_monstersExtra, Math.Max(chain.Count, 1));

        for (var t = 0; t < found; t++)
        {
            tractionOf[t] = 1f;
            rareTractionOf[t] = 1f;
            normalTractionOf[t] = 1f;
        }

        for (var s = 0; s < chain.Count; s++)
            monstersExtra[s] = 0f;

        if (tractive)
        {
            // Every remnant the chain catches, whatever was read of it: one whose combinations were never read went
            // off all the same. Counted by the link that first caught it; the ones one explosive catches go off
            // together, so none of them counts for another.
            var remnants = RemnantIndicesOf(env);
            var caughtAt = _remnantsCaughtAt = Grow(_remnantsCaughtAt, chain.Count + 1);

            for (var s = 0; s <= chain.Count; s++)
                caughtAt[s] = 0;

            foreach (var i in remnants)
            {
                if (seen[i] == visit)
                    caughtAt[step[i]]++;
            }

            int DetonatedBefore(int link)
            {
                var before = env.RemnantsCompletedInArea;

                // Only remnants caught at a link still sending, when the sources are cut off. See ScoreOfEachBlast.
                for (var s = 0; s < link && SendsFrom(s); s++)
                    before += caughtAt[s];

                return before;
            }

            // Its worth and the monster pools upstream runes pay on, for every caught remnant.
            foreach (var i in remnants)
            {
                if (seen[i] != visit)
                    continue;

                var before = DetonatedBefore(step[i]);

                if (before <= 0)
                    continue;

                var (normalScale, magicScale, rareScale) = TractionScalesOfTarget(env, env.Targets[i], before);
                var waves = env.Targets[i].MagicAndRareWaves;
                var normals = env.Targets[i].NormalWaves;
                var extraMonsters = (magicScale - 1f) * waves.MagicMonsters + (rareScale - 1f) * waves.RareMonsters +
                                    (normalScale - 1f) * normals.NormalMonsters;

                var tractionWorth = (magicScale - 1f) * waves.Magic + (rareScale - 1f) * waves.Rare +
                                    (normalScale - 1f) * normals.Normal;

                content += tractionWorth;
                ContentToLink(step[i], tractionWorth);
                monstersExtra[step[i]] += extraMonsters;

                for (var s = 0; s <= step[i]; s++)
                    after[s] += extraMonsters;
            }

            // The payout on its own waves and the scoped pools, which only remnants settled per marker have - those
            // are the touched ones with combinations.
            for (var t = 0; t < found; t++)
            {
                var i = touched[t];
                var target = env.Targets[i];

                if (target.Kind != TargetKind.Remnant)
                    continue;

                var before = DetonatedBefore(step[i]);

                if (before <= 0)
                    continue;

                var (normalScale, magicScale, rareScale) = TractionScalesOfTarget(env, target, before);

                tractionOf[t] = magicScale;
                rareTractionOf[t] = rareScale;
                normalTractionOf[t] = normalScale;

                if (taggedAfter != null && target.PartsSettledPerMarker && target.Parts is { } waves)
                {
                    var scoped = env.Scoped;

                    foreach (var (mask, part, _) in waves)
                    {
                        if (part <= 0f)
                            continue;

                        var tierScale = (mask & (1L << Tags.Rares)) != 0L ? rareScale
                            : (mask & (1L << Tags.Magics)) != 0L ? magicScale
                            : normalScale;

                        if (tierScale == 1f)
                            continue;

                        var extra = part * (tierScale - 1f);

                        for (var k = 0; k < scoped.Length; k++)
                        {
                            if ((mask & (1L << scoped[k])) == 0L || taggedAfter[k] == null)
                                continue;

                            for (var s = 0; s <= step[i]; s++)
                                taggedAfter[k][s] += extra;
                        }
                    }
                }
            }
        }

        var distinct = 0;

        // One credit per effect, against the most monsters it could ever reach - which is the
        // earliest link that offers it, since propagation only goes forwards.
        //
        // Kept in flat scratch arrays rather than a dictionary: a chain holds fifteen links and a
        // remnant a handful of runes, so a linear scan over a few dozen entries beats hashing in a
        // function called tens of thousands of times a solve.
        //
        // Shared by two callers now. It was written for runes when the stacking switch is off, and
        // an effect that pays once however many are taken wants exactly the same treatment whatever
        // that switch says. See PlanTarget.Once.
        void Book(string id, float weight, float reach, int tag = -1, int at = 0,
            (int X, int Y) owner = default, bool rune = false, bool flat = false, float[] heldLifts = null)
        {
            if (weight <= 0f || string.IsNullOrEmpty(id))
                return;

            // A flat effect always lands on something named - it comes from an effect's target - so
            // it never takes the untagged route, and the key below can assume a tag.
            if (flat && tag < 0)
                tag = Tags.Monsters;

            var band = GroupIndexOfEffect(env, id);
            var liftScale = 1f;

            // **Lifted at its source**, by the amplifiers its remnant holds: worth each lift more, and filed in the twin
            // for the classes left, so a chain-wide amplifier of a class already applied does not lift it a second time -
            // empowered is on or off. A lift this effect carries is scaled the same way. See PlanTarget.HeldLiftsOfChoice.
            if (heldLifts != null && env.Amplified is { } amplified && NumberOfEffect(env, id) is var number &&
                number >= 0 && number < amplified.MaskOfEffect.Length)
            {
                var ofShare = band >= 0 && band < amplified.MaskOfGroup.Length ? amplified.MaskOfGroup[band] : 0L;
                var ofLift = amplified.MaskOfEffect[number];
                var shareFactor = 1f;
                var held = 0L;

                for (var a = 0; a < amplified.Classes && a < heldLifts.Length; a++)
                {
                    if (heldLifts[a] <= 0f)
                        continue;

                    if ((ofShare & (1L << a)) != 0L)
                    {
                        shareFactor *= 1f + heldLifts[a];
                        held |= 1L << a;
                    }

                    if ((ofLift & (1L << a)) != 0L)
                        liftScale *= 1f + heldLifts[a];
                }

                if (held != 0L)
                {
                    // Only the part of the lift that reaches this rune's group - all of it, unless its row writes the
                    // share plain and empowered. See Amplification.LiftedFactorOfGroup.
                    var liftReach = amplified.ReachOfGroup is { } reaches && band >= 0 && band < reaches.Length
                        ? reaches[band]
                        : 1d;

                    weight *= (float)(1d + (shareFactor - 1d) * liftReach);
                    band = amplified.TwinOf(amplified.PlainOfGroup[band], ofShare & ~held);
                }
            }

            // **Numbered once per site, compared as integers ever after.** This was a linear scan of
            // case-insensitive string comparisons, run once per effect per covered marker per scored
            // chain - hundreds of thousands of chains in a solve, each walking a list of a few dozen
            // names and comparing them character by character. The names are fixed for the whole
            // site, so they are numbered when the environment is built and this compares ints.
            //
            // An effect the table has never seen scores nothing rather than crashing: the id is
            // still the key everywhere else, and a marker carrying a modifier that arrived after the
            // environment was built is one the objective has no weight for anyway.
            var key = NumberOfEffect(env, id);

            if (key < 0)
                return;

            // A scoped share is booked against the rune AND what it reaches, in a space of its own
            // so it cannot collide with the same rune's unscoped booking. Negative, because the
            // plain keys are not.
            if (tag >= 0)
                key = -((key * 64 + tag) * 2 + (flat ? 1 : 0) + 2);

            for (var r = 0; r < distinct; r++)
            {
                if (_carriedIds[r] != key)
                    continue;

                // **The earliest link this rune reaches monsters from, kept whatever wins on reach.**
                //
                // _carriedAt follows the STRONGEST source, because that is the entry being kept - and
                // "where is this rune strongest" is not the question a duplicate test asks. A rune
                // sourced at link 2 and again at link 5, the second reaching further, leaves
                // _carriedAt at 5, and anything reading it as "this rune arrives at 5" then denies
                // link 3 a rune that has been in force since link 2. The two facts are different and
                // both are needed, so both are recorded.
                //
                // Monsterly only, because a duplicate matters where the rune lands: an earlier
                // chest-scoped source of the same rune tells a remnant's monsters nothing.
                if (Tags.Monsterly(tag) && at < _carriedFrom[r])
                    _carriedFrom[r] = at;

                if (reach > _carriedAfter[r])
                {
                    _carriedAfter[r] = reach;
                    _carriedAt[r] = at;
                    _carriedBy[r] = owner;
                    _carriedTag[r] = tag;
                    _carriedGroup[r] = band;

                    // The winning source's weight too, since one source may send it empowered and another not.
                    _carriedWeights[r] = weight;
                    _carriedLiftScale[r] = liftScale;

                    if (Tags.Monsterly(tag))
                        _carriedRich[r] = reach;
                }

                return;
            }

            _carriedIds = Grow(_carriedIds, distinct + 1);
            _carriedWeights = Grow(_carriedWeights, distinct + 1);
            _carriedAfter = Grow(_carriedAfter, distinct + 1);

            _carriedIds[distinct] = key;
            _carriedWeights[distinct] = weight;
            _carriedAfter[distinct] = reach;

            // **Which group and which tag, so the payout can put the share where it belongs.** A
            // booking is one modifier credited once; the payout needs to know what it adds with and
            // what it lands on, and both are fixed the moment it is booked. See Multiplied.
            _carriedTag = Grow(_carriedTag, distinct + 1);
            _carriedGroup = Grow(_carriedGroup, distinct + 1);
            _carriedTag[distinct] = tag;
            _carriedGroup[distinct] = band;

            // **Which effect, and how much its source lifts its lift**, so the drain can add a lift the effect carries
            // per class. See Rate and Amplification.
            _carriedEffect = Grow(_carriedEffect, distinct + 1);
            _carriedLiftScale = Grow(_carriedLiftScale, distinct + 1);
            _carriedEffect[distinct] = NumberOfEffect(env, id);
            _carriedLiftScale[distinct] = liftScale;

            // Which link this was booked at, so the number drawn in that blast circle can be the
            // share the objective actually credited there. See CreditToLink.
            _carriedAt = Grow(_carriedAt, distinct + 1);
            _carriedBy = Grow(_carriedBy, distinct + 1);
            _carriedAt[distinct] = at;

            // **Which marker's booking won, so the overlay can name what a remnant FIRST brings.**
            // A rune is credited once, at the longest reach - the earliest link that offers it - and
            // every later remnant carrying the same rune adds nothing, because runes do not stack.
            // Naming it under all of them said the opposite of what the objective does.
            _carriedBy[distinct] = owner;
            _carriedNames = Grow(_carriedNames, distinct + 1);
            _carriedNames[distinct] = id;

            // **Whether this booking is a RUNE**, because relics and switches come through here too.
            // A relic's "% it passes on" and a "pays once" switch are propagating modifiers and
            // belong in the concentration depth, which counts everything landing on a monster - but
            // they are not runes, and the line under a remnant says runes. Counting them there had
            // a remnant reading ten when six runes and a relic effect arrived.
            _carriedRune = Grow(_carriedRune, distinct + 1);
            _carriedRune[distinct] = rune;

            // **Whether the weight held here is an amount or a share.** Everything else about a
            // booking is the same either way - one credit per effect per tag, at the longest reach -
            // so the two share this table and part company only at the payout. See the drain loop.
            _carriedFlat = Grow(_carriedFlat, distinct + 1);
            _carriedFlat[distinct] = flat;

            // Only the monster part of what it reaches joins the layering. A chest-scoped rune is
            // booked like any other and simply contributes nothing to concentration.
            _carriedRich = Grow(_carriedRich, distinct + 1);
            _carriedRich[distinct] = Tags.Monsterly(tag) ? reach : 0f;

            // Never-arrives rather than nought, because Grow zero fills and a nought here reads as
            // "in force from the first link" - the one wrong answer that suppresses everything. See
            // the upgrade path above and IsRuneBookedUpstream.
            _carriedFrom = Grow(_carriedFrom, distinct + 1);
            _carriedFrom[distinct] = Tags.Monsterly(tag) ? at : int.MaxValue;
            distinct++;
        }

        // Which switches have already been paid for, so the second one granting the same thing
        // pays nothing. The mirror of Book, for the flat weight rather than the share passed on.
        // **Sized and blanked BEFORE anything is credited**, which is where it belongs and not
        // where it was first written. The loop below credits as it goes, so blanking afterwards
        // erased every one of them and left the circles reading nought. Only on a detailed pass -
        // the search runs this half a million times and must not be writing to it.
        if (detail)
        {
            RuneTallyByRemnant.Clear();
            WavesPredictedByRemnant.Clear();
            Chosen.Clear();
            OwnEffectsUnderPassedPower.Clear();
            Rankings.Clear();
            _ratedAside?.Clear();
            PropagationTrailByRemnant.Clear();
            _lifting.Clear();
        }

        // How many remnants have ordinary runes worth counting, for the pass after the loop.
        var locals = 0;

        var switches = 0;
        var repeated = 0d;

        void Credit(string id, float weight, int at)
        {
            if (weight <= 0f || string.IsNullOrEmpty(id))
                return;

            var key = NumberOfEffect(env, id);

            if (key < 0)
                return;

            for (var r = 0; r < switches; r++)
            {
                if (_switchIds[r] != key)
                    continue;

                // Seen before: this object's copy of the weight was counted with its coverage and
                // is now taken back.
                repeated += weight;
                ContentToLink(at, -weight);

                return;
            }

            _switchIds = Grow(_switchIds, switches + 1);
            _switchIds[switches] = key;
            switches++;
        }

        /// <summary>Registers an effect as already granted, without crediting anything back.</summary>
        void Granted(string id)
        {
            var key = NumberOfEffect(env, id);

            if (key < 0)
                return;

            for (var r = 0; r < switches; r++)
            {
                if (_switchIds[r] == key)
                    return;
            }

            _switchIds = Grow(_switchIds, switches + 1);
            _switchIds[switches] = key;
            switches++;
        }

        // What the explosives already down bought, still paying out.
        //
        // A rune from a remnant an earlier blast caught applies to everything unearthed after it,
        // and every link being planned now is after it - so it multiplies the whole of what this
        // chain digs up. after[0] is exactly that: the monster weight from the first remaining link
        // onwards. It is not a constant the search can ignore either, which is the point: a plan
        // that unearths more monsters is worth more BECAUSE the rune is already in hand.
        //
        // **Every share is written down and paid at the end, rather than added as it is found.**
        //
        // The objective used to total these as it went, which only works while modifiers are
        // independent - each one multiplying an unchanged base and the results summed. They are
        // not: two raising the same stat add, two raising different stats compound, and a product
        // cannot be accumulated one term at a time without knowing what else lands on the same
        // creature. So the shares go into a table keyed by group, tag and link, and one walk down
        // the chain turns them into money. See Multiplied.
        var links = chain.Count;
        var width = env.Relevant.Length;
        var groups = Math.Max(1, env.GroupCount);
        var rates = Grow(_rates, groups * width * Math.Max(links, 1));

        _rates = rates;

        // The same shares less every rune's, for what no rune reaches. See Tags.UnaffectedByRunes.
        var ratesWithoutRunes = Grow(_ratesWithoutRunes, groups * width * Math.Max(links, 1));

        _ratesWithoutRunes = ratesWithoutRunes;

        for (var i = 0; i < groups * width * links; i++)
        {
            rates[i] = 0f;
            ratesWithoutRunes[i] = 0f;
        }

        // What flat effects add to each thing of a class, per link they are in force from.
        //
        // **No group, because a flat effect names no stat.** A share is a percentage of some stat,
        // and stats are what decide whether two shares add or compound, which is the whole of what
        // the group dimension is for. Three more weight per chest belongs to no stat and simply
        // adds to whatever else reaches the chest, so one bucket per class per link holds it.
        var flats = Grow(_flats, width * Math.Max(links, 1));

        _flats = flats;

        for (var i = 0; i < width * links; i++)
            flats[i] = 0f;

        // How much the chain's empowering effects scale everything else, per link. Untagged on
        // purpose: an effect that scales other effects scales them whatever they are pointed at.
        // One run of links per amplifier class; one class where the site has no amplification table, which is the
        // shape this had before classes. See Amplification.
        var amplifiers = env.Amplified?.Classes ?? 1;
        var lifts = Grow(_lifts, Math.Max(links * amplifiers, 1));

        _lifts = lifts;

        for (var i = 0; i < links * amplifiers; i++)
            lifts[i] = 0f;

        // An amount added to every thing of this tag the chain unearths from this link onwards.
        // Prefix summed with the rates below, so it pays forwards and not backwards, exactly as a
        // share does.
        void Flat(int tag, int at, float amount)
        {
            if (amount <= 0f || at < 0 || at >= links)
                return;

            var k = Indexed(env, tag < 0 ? Tags.Monsters : tag);

            if (k < 0)
                return;

            flats[k * links + at] += amount;
        }

        /// <param name="what">
        /// What this share is, for the trail. Null where the caller has nothing to name - which is
        /// not the same as nothing being there, and is why the trail says NOTHING NAMED rather than
        /// leaving the share out.
        /// </param>
        void Rate(int band, int tag, int at, float percent, string what = null, int effect = -1, float liftScale = 1f,
            bool fromRune = false)
        {
            if (at < 0 || at >= links)
                return;

            // **A lift never enters a group.** It is not a share of anything landing on a monster - it is a
            // multiplier on the shares its class lifts - so it is held aside per class and applied once, after the
            // groups have compounded. Read off the effect, so a rune that adds as well pays its share below. See
            // Amplification.
            if (env.Amplified is { } amplified && effect >= 0 && effect < amplified.LiftsOfEffect.Length &&
                amplified.LiftsOfEffect[effect] is { } own)
            {
                for (var a = 0; a < amplified.Classes && a < own.Length; a++)
                {
                    if (own[a] > 0f)
                        lifts[a * links + at] += own[a] * liftScale;
                }

                if (band == env.Empowering)
                    return;
            }
            else if (band == env.Empowering)
            {
                // With no table to read it from - an environment from a file, or a banked carry with no name - the
                // rate is the lift, on the one class there was before classes. See PlanEnvironment.Empowering.
                if (percent > 0f)
                    lifts[at] += percent / 100f;

                return;
            }

            if (percent <= 0f)
                return;

            var k = Indexed(env, tag < 0 ? Tags.Monsters : tag);

            if (k < 0)
                return;

            rates[(band * width + k) * links + at] += percent / 100f;

            if (!fromRune)
                ratesWithoutRunes[(band * width + k) * links + at] += percent / 100f;

            // **Kept aside because a rate is not a booking and the trail could only read bookings.**
            //
            // Most shares are booked - a booking is one modifier credited once, and the trail walks
            // those - but three kinds are rated straight in: a banked carry with no name to book
            // under, a scoped effect on an object that grants it every time, and a combination's
            // flat carry. Those reached the product and appeared in no list, so a third of the
            // multiplier lines on a live site read NOTHING NAMED while being perfectly real.
            //
            // Detailed passes only, and recorded here rather than at the call sites so a rate that
            // does not actually land - empowering, or a class this chain has none of - cannot be
            // named as though it had.
            if (detail && what != null)
                (_ratedAside ??= new List<(int, int, int, float, string)>()).Add((band, tag, at, percent, what));
        }

        // What the explosives already down bought, still paying out.
        //
        // A rune from a remnant an earlier blast caught applies to everything unearthed after it,
        // and every link being planned now is after it - so it goes in at the first link and
        // multiplies the whole of what this chain digs up. It is not a constant the search can
        // ignore either, which is the point: a plan that unearths more monsters is worth more
        // BECAUSE the rune is already in hand.
        //
        // **Booked, not rated.** It used to arrive as one figure - the largest single carry among
        // the caught remnants - and be dropped into the default pool as an untagged monster rate,
        // because there was nothing left to say which stat each part of it raised. Now it arrives
        // as the modifiers themselves, so it goes in the way every other modifier does and gets
        // grouping, tagging and multiplication for nothing.
        //
        // Booking also settles the duplicate: Book keeps one entry per effect id, so a relic the
        // chain is considering that duplicates what a placed blast already duplicates books nothing
        // and is worth nothing - which is the behaviour, and it used to take the second one.
        //
        // Reach of infinity because a rune already in hand applies to everything this chain digs
        // up, whichever link digs it.
        var propagation = 0d;

        _banked = 0;

        if (links > 0 && env.Secured != null)
        {
            // **The same two ways a live link banks a carry, because there are two.**
            //
            // A scoped modifier is booked under its own name, so a second copy of it adds nothing.
            // An UNSCOPED carry - the ordinary case, a rune passing a few per cent forward with no
            // stat named - is not a named effect at all: it goes in as a plain rate inside the
            // target's own group, which is what decides whether it adds to the chain's other
            // modifiers or multiplies with them.
            //
            // Banking only the first was why remnants contributed nothing while relics contributed
            // everything: a relic's reference table row carries a scope, and a rune's usually does
            // not. The score card showed it plainly - three caught remnants, rewards read, chosen
            // combinations read, and not one modifier between them.
            foreach (var (id, tag, percent, band, from) in env.Secured)
            {
                if (string.IsNullOrEmpty(id))
                {
                    // An unnamed banked carry is a caught remnant's runes. See the comment above the loop.
                    Rate(band, Tags.Monsters, 0, percent, $"banked from ({from.X},{from.Y})", fromRune: true);

                    // **Counted for the readout, because the Book cannot hold it.**
                    //
                    // An unscoped carry has no name, so there is nothing to book it under - it goes
                    // in as a plain rate and pays out on every later link, which is correct. What it
                    // is invisible to is the line under a remnant: that counts what the chain is
                    // sending onto these waves by walking the BOOKED modifiers, so five runes banked
                    // from bombs already on the ground showed up as nought inherited, and every new
                    // link read as though it were the chain's first. The score knew better the whole
                    // time; only the readout did not.
                    //
                    // Kept as a count rather than booked under a made-up name, because IsRuneBookedUpstream() is
                    // what strikes a remnant's duplicate local runes and a synthetic entry there
                    // would change the payout. This changes nothing that is priced. See Locally.
                    _banked++;

                    continue;
                }

                // **Booked with an OWNER, so the remnant that banked it is not told it inherited it.**
                //
                // A scoped carry from a remnant an explosive already covers is booked reaching
                // everything, which is right - it pays forward over the whole remaining chain. Booked
                // with no owner, though, Firsts cannot attribute it, so the inherited count credited it
                // to every remnant INCLUDING the one it came from: a six socket remnant read 7 (6+2-1),
                // inheriting two of its own runes. The live path has always passed the cell; the banked
                // path did not. See Firsts and PlanEnvironment.BankedRunes, which had the same fault by
                // the other route.
                // Owner yes, rune NO. These are relic effects and pay-once switches as well as runes,
                // and the line under a remnant counts runes - marking them all as runes added three to
                // every remnant's inherited figure, which is the fault _carriedRune exists to prevent.
                // The owner is what was actually missing.
                Book(id, percent, float.MaxValue, tag, 0, from);

                // **And it counts as already granted, which the weight side needs to know.**
                //
                // An effect that pays once however many objects grant it is handled in two halves:
                // Book stops the second copy adding anything to propagation, and Credit hands back
                // the marker weight the coverage loop added for it. Only the first half knew about
                // the explosives already down, so a chain that caught a second relic granting a
                // duplication already in hand got no propagation from it - correctly - and kept its
                // whole content weight anyway, which is enough to make the search spend a link on
                // it. Registering it here makes the next grant a repeat, which is what it is.
                Granted(id);
            }
        }

        // **The rune class's lift in force at a link from the links before it**, out of what is booked so far: the
        // strongest booked effect lifting that class - Power's - first reaching monsters before the link. The walk below
        // is in link order and books as it goes, so this is complete for every earlier link when a later one chooses.
        // The lifts themselves are written to `lifts` only after the walk, so they are read off the bookings. See
        // PlanTarget.Best's passedLift.
        double PowerPassedTo(int link)
        {
            if (env.Amplified is not { RuneClass: >= 0 } amplified)
                return 0d;

            var most = 0d;

            for (var r = 0; r < distinct; r++)
            {
                var effect = _carriedEffect[r];

                if (effect < 0 || effect >= amplified.LiftsOfEffect.Length ||
                    amplified.LiftsOfEffect[effect] is not { } liftsOfIt || amplified.RuneClass >= liftsOfIt.Length ||
                    liftsOfIt[amplified.RuneClass] <= 0f || _carriedFrom[r] >= link)
                    continue;

                most = Math.Max(most, liftsOfIt[amplified.RuneClass] * _carriedLiftScale[r]);
            }

            return most;
        }

        // **The shares in force at a link, laid out as the payout's prefix**, for a remnant ranking its combinations:
        // what has been rated at this link or before - relics, scoped carries, banked runes - and the runes booked so far
        // that are in force here, which are rated only after every choice is made. Links are walked in order, so both
        // hold everything sourced at or before the link. See PlanTarget.OwnPaidOfChoice.
        float[] SharesInForceAt(int at)
        {
            var shares = _sharesInForce = Grow(_sharesInForce, groups * width);

            for (var x = 0; x < groups * width; x++)
            {
                var sum = 0f;

                for (var a = 0; a <= at && a < links; a++)
                    sum += rates[x * links + a];

                shares[x] = sum;
            }

            for (var r = 0; r < distinct; r++)
            {
                if (_carriedFrom[r] > at || _carriedWeights[r] <= 0f || (_carriedFlat != null && _carriedFlat[r]))
                    continue;

                var g = _carriedGroup[r];
                var k = Indexed(env, _carriedTag[r] < 0 ? Tags.Monsters : _carriedTag[r]);

                if (g < 0 || g >= groups || g == env.Empowering || k < 0)
                    continue;

                shares[g * width + k] += _carriedWeights[r] / 100f;
            }

            return shares;
        }

        // **Cleared, because this is per-thread scratch reused on every score.** A marker with no
        // ordinary runes reads whatever the last chain left at its index otherwise, and attaches
        // somebody else's runes to its waves.
        _localsOf = Grow(_localsOf, found);
        _chosenOf = Grow(_chosenOf, found);

        for (var t = 0; t < found; t++)
        {
            _localsOf[t] = null;
            _chosenOf[t] = -1;
        }

        // **Walked in LINK order, which is what lets a choice see what is already on its way.**
        //
        // This loop chooses a combination and books its runes, marker by marker - so a marker handled
        // earlier has already booked by the time a later one chooses. In discovery order that is no
        // use: the remnant at link four can be reached before the one at link three, and then the
        // rune arriving from three is not in the set yet and the duplicate goes unseen. Propagation
        // only runs forwards, so link order is exactly the order in which the answers become known.
        //
        // Counted rather than sorted - a chain has a couple of dozen markers over a handful of links,
        // so buckets beat comparisons, and this runs on every scored chain. The POSITIONS in touched
        // are what is reordered, not touched itself: _localsOf is keyed by that position and the
        // payout loop below reads it back the same way.
        // **On every pass, not just the detailed one, because the ranking depends on it.**
        //
        // A duplicate can only be recognised once the link that sends it has been walked, so in
        // discovery order a remnant's view of what is already arriving depends on which markers the
        // coverage walk happened to reach first. That makes the objective differ between evaluations
        // of the SAME chain - and a plan chosen under an inconsistent objective is what produced the
        // run where the reported choices had no duplicate struck and the search had clearly been
        // striking them anyway. Three passes over a couple of dozen markers is the price of the
        // comparison meaning the same thing every time it is made.
        var order = Buffer(ref _order, Math.Max(found, 1));
        var counts = Buffer(ref _counts, chain.Count + 1);

        for (var k = 0; k <= chain.Count; k++)
            counts[k] = 0;

        for (var t = 0; t < found; t++)
            counts[step[touched[t]]]++;

        var running = 0;

        for (var k = 0; k < chain.Count; k++)
        {
            var many = counts[k];

            counts[k] = running;
            running += many;
        }

        for (var t = 0; t < found; t++)
            order[counts[step[touched[t]]]++] = t;

        // **Which runes each remnant would send, before any of them has chosen**, so a remnant can see a later one sending
        // the same rune. Choices are made in link order, so a later remnant's real pick is not known when an earlier one
        // ranks; this ranks every remnant once with nothing booked, as one would choose with nothing arriving. One
        // provisional pick each, not iterated. See Planner.LaterSentReach.
        var laterCount = 0;
        var trailKept = ChoiceRankingTrail;

        ChoiceRankingTrail = null;

        for (var k = 0; k < found; k++)
        {
            var i = touched[order[k]];

            if (env.Targets[i].Choices is not { Length: > 0 } offered)
                continue;

            var provisional = offered.Length == 1
                ? offered[0]
                : env.Targets[i].Best(after[step[i]], monsters[step[i]] + monstersExtra[step[i]], env, taggedAfter,
                    step[i], 0, out _, tractionOf[order[k]], rareTractionOf[order[k]]);

            foreach (var id in provisional.Runes ?? [])
            {
                if (id == null)
                    continue;

                _laterSends = Grow(_laterSends, laterCount + 1);
                _laterSends[laterCount++] = (NumberOfEffect(env, Weighing.RuneOfKey(id)), step[i]);
            }
        }

        ChoiceRankingTrail = trailKept;

        for (var k = 0; k < found; k++)
        {
            var t = order[k];
            var i = touched[t];

            // One combination, chosen here, paying for both halves of what this remnant is worth.
            // Before the search knows where the remnant sits there is no answer to give.
            // Collected only while somebody is reading, and only for an object with something to
            // choose between. See ChoiceRankingTrail.
            ChoiceRankingTrail = detail && env.Targets[i].Choices is { Length: > 1 }
                ? new List<string>(env.Targets[i].Choices.Length)
                : null;

            var choice = env.Targets[i].Best(after[step[i]], monsters[step[i]] + monstersExtra[step[i]],
                env, taggedAfter, step[i], distinct, out var chose, tractionOf[t], rareTractionOf[t],
                PowerPassedTo(step[i]),
                env.Targets[i].Choices is { Length: > 1 } ? SharesInForceAt(step[i]) : null, groups, width,
                _laterSends, laterCount, after);

            // The combination the full chain took, when ScoreOfEachBlast has pinned it, so that passes with fewer
            // sources differ in the sources alone.
            if (_pinnedChoices != null && i < _pinnedChoices.Length && _pinnedChoices[i] >= 0 &&
                env.Targets[i].Choices is { } offered && _pinnedChoices[i] < offered.Length)
            {
                chose = _pinnedChoices[i];
                choice = offered[chose];
            }

            // Gaining Traction on the waves this combination has over the remnant's own recipe, which its reward
            // counts at face value. See PlanTarget.TractionOfChoice.
            var tractionOfChoice = env.Targets[i].TractionOfChoice(chose, tractionOf[t], rareTractionOf[t]);

            // What its runes' "own" effects do to its own waves, and the combination kept for the payout below,
            // which scales the same parts. See PlanTarget.OwnOfChoice.
            var ownOfChoice = env.Targets[i].OwnOfChoice(chose, tractionOf[t], rareTractionOf[t]);

            content += tractionOfChoice + ownOfChoice + choice.Reward;
            ContentToLink(step[i], tractionOfChoice + ownOfChoice + choice.Reward);
            _chosenOf[t] = chose;

            if (perLink)
                _chosenByTarget[i] = chose;

            // The decision, kept where the readouts can see it. Detail only: the search runs this
            // millions of times and only the last pass describes the chain anybody is looking at.
            //
            // And only where a decision was actually made. This loop walks every marker the chain
            // touches, most of which are monsters and chests with no combinations to choose between -
            // published regardless, they filled the readout with "(unnamed), using 0 sockets" rows
            // that look like a remnant whose name could not be read. Best leaves chose at minus one
            // when there was nothing to choose, which is the same test.
            if (detail && chose >= 0)
            {
                var target = env.Targets[i];

                if (ChoiceRankingTrail is { Count: > 0 })
                    Rankings[CellKeyOf(target.Grid)] = ChoiceRankingTrail;

                Chosen[CellKeyOf(target.Grid)] = new Picked(
                    target.Named is { Length: > 0 } named && chose >= 0 && chose < named.Length
                        ? named[chose]
                        : "(unnamed)",
                    RunesInLocals(choice.Locals) + (choice.Runes?.Length ?? 0),
                    choice.Carries,
                    DiscountedForDuplicates(env, target, choice.Runes, choice.Carries, step[i], distinct),
                    choice.Local,
                    DiscountedForDuplicates(env, target, choice.Locals, choice.Local, step[i], distinct),
                    choice.Runes ?? [],
                    target.Recipes is { Length: > 0 } ids && chose >= 0 && chose < ids.Length
                        ? ids[chose] ?? ""
                        : "",
                    choice.Reward,
                    target.OwnOfChoice(chose, tractionOf[t], rareTractionOf[t]),
                    (float)after[step[i]]);
            }

            // The runes that stay put, worth their percentage of THIS blast. Outside the stacking
            // question entirely: a local rune reaches one explosive's worth of monsters, so two
            // remnants carrying the same one are two separate buffs on two separate packs.
            // **A rune that does not propagate reaches this remnant's own waves and nothing else.**
            //
            // It multiplied monsters[step], which is everything that blast unearthed - so a local
            // rune reached the monster markers the same explosive happened to catch, and where one
            // blast caught two remnants each one's local runes were paid against both one's waves
            // and the other's. Neither happens in the game: an ordinary slot affects the monsters
            // that remnant sends up, and the blast is not what couples them.
            //
            // The propagating slot is the one that reaches outwards, and it still does - see
            // choice.Carries below, against after[step], which is inclusive of this link and so
            // covers this blast's monsters, these waves, and everything the chain unearths later.
            if (choice.Local > 0f)
            {
                // Held for the payout rather than added here: these reach this remnant's own waves
                // and nothing else, so they join the product on those waves alongside whatever the
                // chain is already carrying onto them. See Multiplied's extra argument.
                _localsOf[t] = choice.Locals;
            }

            // **Written down for the concentration pass, not spent here.** These waves are the
            // one place on a dig site where several runes certainly land on the same monsters:
            // the ordinary slots all reach them, and so does every propagating rune already in
            // play. How many of the second kind there are is not known until the whole chain is
            // booked, so the arithmetic waits. See Locally.
            //
            // **Registered for the line under the remnant even when it sources nothing itself.**
            //
            // It used to need ordinary runes of its own, and sat inside the test for those runes
            // being worth something - so a remnant offering plain currency was never recorded, and
            // the overlay had nothing to draw under it however much the chain was sending its way.
            // Seen on a fifteen link chain catching five remnants: one line drawn, four missing,
            // and the four were the ones whose reward was 5x Random Currency, 3x Greater Regal Orb
            // and Sovereign Alloy. What lands on a remnant's waves does not depend on what that
            // remnant happens to be handing out.
            //
            // Only on a detailed pass, because the search runs this half a million times and these
            // arrays exist solely to be read back by the overlay - nothing here is priced. The
            // payout reads _localsOf, which is set above and is untouched by this.
            if (env.Targets[i].Waves > 0f &&
                ((choice.Local > 0f && choice.Locals is { Length: > 0 }) || detail))
            {
                _localCount = Grow(_localCount, locals + 1);
                _localReach = Grow(_localReach, locals + 1);

                _localRunes = Grow(_localRunes, locals + 1);
                _localGrid = Grow(_localGrid, locals + 1);
                _localOwn = Grow(_localOwn, locals + 1);
                _localCarry = Grow(_localCarry, locals + 1);
                _localRunes[locals] = choice.Locals;
                _localCarry[locals] = choice.Runes;
                _localSlots = Grow(_localSlots, locals + 1);
                _localSlots[locals] = env.Targets[i].SlotRunesOfChoice(chose);

                // **The two lists between the combination's runes and what gets booked**, because a
                // rune can be lost at either hop and the dump could not say which.
                //
                // Named filters choice.Runes against the target's own weighted list and SILENTLY
                // drops anything it cannot match - it returns found[..count] - so a rune the
                // combination offers can vanish with no diagnostic. And that weighted list is built
                // by Weighing.PropagatingRuneWeights, which holds every rune a propagating slot
                // could take. A rune the recipe names and that table does not hold leaves a
                // propagating socket that first-sources nothing,
                // which Wasted then counts as spare.
                //
                // Measured: a remnant at (602,901) drew "Life, Tempest" in the combinations window
                // and "Tempest" in the world, and the three lists below are what tells those apart.
                _localKnown = Grow(_localKnown, locals + 1);
                _localNamed = Grow(_localNamed, locals + 1);
                _localKnown[locals] = env.Targets[i].PropagatingRuneWeights ?? env.Targets[i].Runes;
                _localNamed[locals] = WeightsOfChosenRunes(env.Targets[i], choice.Runes);
                _localGrid[locals] = ((int)MathF.Round(env.Targets[i].Grid.X),
                    (int)MathF.Round(env.Targets[i].Grid.Y));

                // Its own propagating runes reach its own waves too - propagation is inclusive
                // of the link that offers it - so they are part of what it holds, and have to
                // come out of the inherited count or they are counted as arriving from
                // somewhere else.
                _localOwn[locals] = choice.Runes?.Length ?? 0;
                _localCount[locals] = RunesInLocals(choice.Locals);
                _localReach[locals] = after[step[i]];
                _localAt = Grow(_localAt, locals + 1);
                _localAt[locals] = step[i];
                locals++;
            }

            // **Nothing sent from a link past the cutoff**, when ScoreOfEachBlast is scoring with fewer sources. What it
            // catches is still content; a switch it repeats is still taken back.
            if (!SendsFrom(step[i]))
            {
                foreach (var (id, _, _, weight) in env.Targets[i].NonStacking ?? [])
                    Credit(id, weight, step[i]);

                continue;
            }

            // What this object passes on to particular kinds of thing. Nothing reaches here unless
            // somebody wrote a scope, and an effect that has one contributes through this INSTEAD of
            // through the flat carry below - see Weighing.PricedRowOfTarget, which is where that is enforced.
            // **What the combination's runes reach, spent the same way the object's own scope is.**
            //
            // Outside the stacking branch below, because it applies either way: a rune that reaches
            // chests reaches them whether or not two copies of it would stack, and the non-stacking
            // bookkeeping is about names rather than about tags. See Weighing.Reaching.
            foreach (var (rune, tag, percent, flat) in choice.Spread ?? [])
            {
                if (percent <= 0f)
                    continue;

                var got = Reach(env, tag, taggedAfter, after, step[i]);

                if (got <= 0f)
                    continue;

                // **Booked by name, always.** A monster carries one of each rune, so two
                // remnants both propagating the same scoped rune are one credit between them.
                // Booked per rune AND tag, so one rune reaching two things is two credits and
                // rightly.
                // Its own remnant's waves only from its slot onward. See PlanTarget.WaveShareOfCarried.
                var share = env.Targets[i].WaveShareOfCarried(chose, rune);

                if (share < 1f)
                    got = MathF.Max(0f, got - (1f - share) *
                        env.Targets[i].OwnWavesOfTag(tag, tractionOf[t], rareTractionOf[t]));

                Book(rune, percent, got, tag, step[i], CellKeyOf(env.Targets[i].Grid), true, flat,
                    env.Targets[i].HeldLiftsOfChoice(chose));
            }

            var spread = env.Targets[i].Spread;

            if (spread != null)
            {
                foreach (var (id, tag, percent, flat) in spread)
                {
                    if (percent <= 0f)
                        continue;

                    var reach = Reach(env, tag, taggedAfter, after, step[i]);

                    if (reach <= 0f)
                        continue;

                    if (env.Targets[i].Once.Length != 0)
                        Book(env.Targets[i].Once, percent, reach, tag, step[i], flat: flat);
                    else if (flat)
                        Flat(tag, step[i], percent);
                    else
                        Rate(GroupIndexOfEffect(env, id), tag, step[i], percent, id);
                }
            }

            // Each rune is remembered once, against the most remnants it could ever reach -
            // which is the earliest link that offers it, since propagation only goes forwards.
            // Kept in a flat scratch pair rather than a dictionary: a chain holds fifteen links and
            // a remnant a handful of runes, so a linear scan over a few dozen entries beats hashing
            // in a function called tens of thousands of times a solve.
            //
            // The runes of the combination just chosen, not every rune the remnant could ever
            // offer - the same coupling, applied to the names instead of the total.
            var runes = WeightsOfChosenRunes(env.Targets[i], choice.Runes);

            if (runes is { Length: > 0 })
            {
                foreach (var (id, weight) in runes)
                {
                    if (weight <= 0f || id == null)
                        continue;

                    // Its own remnant's waves only from its slot onward. See PlanTarget.WaveShareOfCarried.
                    var share = env.Targets[i].WaveShareOfCarried(chose, id);
                    var reach = share < 1f
                        ? MathF.Max(0f, after[step[i]] - (1f - share) *
                            env.Targets[i].OwnWavesOfTag(-1, tractionOf[t], rareTractionOf[t]))
                        : after[step[i]];

                    Book(id, weight, reach, -1, step[i],
                        CellKeyOf(env.Targets[i].Grid), true, heldLifts: env.Targets[i].HeldLiftsOfChoice(chose));
                }
            }
            else if (choice.Carries > 0f)
            {
                // **A carry that is not a rune, which had no path at all once stacking was off.**
                // Best returns the target's flat Carries when it has no combinations to choose
                // between - a relic's "% it passes on" is the case - and the loop below only ever
                // looked at runes, so an unscoped relic propagated nothing whenever the switch was
                // off. Which was always, since off is what the game does.
                //
                // Booked by name where the effect pays once however many objects grant it, added
                // flat where it does not. See PlanTarget.Once.
                if (env.Targets[i].Once.Length == 0)
                    Rate(env.Targets[i].Group, Tags.Monsters, step[i], choice.Carries,
                        // Formatted only for the dump, which is the only reader. See Rate.
                        detail ? $"({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0}) carries" : null);
                else
                    Book(env.Targets[i].Once, choice.Carries, after[step[i]], -1, step[i]);
            }

            // **The switches, which had the same gap.** Each is credited once under its own name
            // however many objects offer it, and every grant after the first hands back the flat
            // weight the coverage loop added for it. Both halves lived inside the stacking branch,
            // so with the switch off a site holding two "Runic Monsters are Duplicated" relics
            // counted the weight twice and the share not at all. See PlanTarget.NonStacking.
            foreach (var (id, tag, percent, weight) in env.Targets[i].NonStacking ?? [])
            {
                // **Booked against what it reaches, which it could not say before.**
                //
                // These were always booked at minus one - "whatever the chain unearths" - because
                // the magnitude came from a Carries cell that named no target. The effect names
                // one, so a row saying rare_monster doubles rares rather than lifting every
                // monster by the same figure, and the reach is that tag's rather than the whole
                // monster total. See Weighing.NonStacking.
                if (percent > 0f)
                {
                    var reach = Reach(env, tag, taggedAfter, after, step[i]);

                    if (reach > 0f)
                        Book(id, percent, reach, tag, step[i]);
                }

                Credit(id, weight, step[i]);
            }
        }

        // **The booked set, raw, before any of it is paid.**
        //
        // The loop below pays one share per ENTRY, and an entry is a key rather than a modifier:
        // Book files a scoped booking under a negative key built from the effect number and the
        // tag, and an unscoped one under the plain positive number. So one rune reaching monsters
        // by two routes - banked from an explosive already down, and propagated again by a remnant
        // this chain catches - would occupy two entries and be paid twice, and every readout there
        // was hid it, because the banked ones are flagged rune:false and the carrying diagnostic
        // shows only runes.
        //
        // Printed rather than asserted, because the alternative is reading the two call sites and
        // deciding what they must do - which is how this was argued about without being settled.
        // A name appearing twice with different keys is the whole proof.
        if (detail)
            Bookings = BookingTrail(distinct);

        // Each booking is one modifier, credited once, at the earliest link that offered it.
        // (the held ordinary runes are cleared before the collection loop - see _localsOf)
        // A lift is one per effect however many tags its rune is booked under, so the second booking of the same effect
        // pays its share and not its lift again. See Rate.
        var liftedCount = 0;

        _liftedEffects = Grow(_liftedEffects, Math.Max(distinct, 1));

        for (var r = 0; r < distinct; r++)
        {
            var effect = _carriedEffect[r];

            if (effect >= 0)
            {
                var already = false;

                for (var e = 0; e < liftedCount && !already; e++)
                    already = _liftedEffects[e] == effect;

                if (already)
                    effect = -1;
                else
                    _liftedEffects[liftedCount++] = effect;
            }

            if (_carriedFlat != null && r < _carriedFlat.Length && _carriedFlat[r])
                Flat(_carriedTag[r], _carriedAt[r], _carriedWeights[r]);
            else
                Rate(_carriedGroup[r], _carriedTag[r], _carriedAt[r], _carriedWeights[r], null, effect,
                    _carriedLiftScale[r], _carriedRune[r] || IsRuneName(_carriedNames[r]));
        }

        // **One walk down the chain, paying each class of thing what it is actually worth.**
        //
        // Forwards rather than backwards, because a modifier reaches what is unearthed at its own
        // link and afterwards - so the accumulator grows as the walk goes on, and every marker is
        // multiplied by whatever had been picked up by the time its blast went off.
        //
        // The base weight is already in content, so only the uplift is added here: weight times the
        // multiplier less one.
        //
        // Credited at the link the WEIGHT sits at rather than the link the modifier came from,
        // which is a change of meaning for the blast circles: a product has no per-modifier share
        // to attribute, and what a blast is worth is a question the payout can answer exactly.
        var prefix = Grow(_prefix, groups * width);

        _prefix = prefix;

        // The running sum of the shares no rune contributed. See ratesWithoutRunes.
        var prefixWithoutRunes = Grow(_prefixWithoutRunes, groups * width);

        _prefixWithoutRunes = prefixWithoutRunes;

        for (var i = 0; i < groups * width; i++)
        {
            prefix[i] = 0f;
            prefixWithoutRunes[i] = 0f;
        }

        // The same running sum for the flat amounts. One per class rather than per group and class,
        // for the reason flats gives.
        var flatly = Grow(_flatly, width);

        _flatly = flatly;

        for (var k = 0; k < width; k++)
            flatly[k] = 0f;

        var classes = 1 << width;
        var pooled = 0d;

        // The empowering effects in force by this link, accumulated exactly as the prefix is - an
        // effect reaches the links after it and not the ones before, and that is the whole of why
        // both of these are running sums over s rather than totals.
        // Each amplifier class's lift accumulated down the chain, what it comes to with the classes lifting it applied,
        // and the factor each set of classes puts on the shares it lifts. lifted is all of them together, for the
        // readouts and for an environment with no amplification table. See Amplification.
        var lifted = 0d;
        var amplified = env.Amplified;
        var liftedOf = _liftedOf = Grow(_liftedOf, amplifiers);
        var effective = _effective = Grow(_effective, amplifiers);
        var factors = _factors = Grow(_factors, 1 << Math.Max(1, amplifiers));
        var markerLifts = _markerLifts = Grow(_markerLifts, amplifiers);
        var markerEffective = _markerEffective = Grow(_markerEffective, amplifiers);
        var markerFactors = _markerFactors = Grow(_markerFactors, 1 << Math.Max(1, amplifiers));
        var mineLifts = _mineLifts = Grow(_mineLifts, amplifiers);
        var chainLifts = _chainLifts = Grow(_chainLifts, amplifiers);

        for (var a = 0; a < amplifiers; a++)
            liftedOf[a] = 0d;

        if (detail)
            _liftedFrom = -1;

        var paying = new Phase(PhaseSettlePayout);

        for (var s = 0; s < links; s++)
        {
            for (var i = 0; i < groups * width; i++)
            {
                prefix[i] += rates[i * links + s];
                prefixWithoutRunes[i] += ratesWithoutRunes[i * links + s];
            }

            for (var k = 0; k < width; k++)
                flatly[k] += flats[k * links + s];

            for (var a = 0; a < amplifiers; a++)
                liftedOf[a] += lifts[a * links + s];

            if (amplified != null)
            {
                amplified.Effective(liftedOf, effective);
                amplified.FactorsOfMasks(effective, factors);
                lifted = factors[(1 << amplifiers) - 1] - 1d;
            }
            else
            {
                lifted = liftedOf[0];
            }

            // **Which links it actually reaches, because "every payout" is not true.**
            //
            // An empowering rate is directional like any other propagation: it scales what the chain
            // unearths AFTER it and nothing before. A Power rune on the last link therefore lifts one
            // link's worth of waves, and the first wording here - taking the accumulator's final
            // value and calling it "every payout" - read as though it had lifted the whole chain.
            if (detail)
            {
                if (lifted > 0d && _liftedFrom < 0)
                    _liftedFrom = s;

                Lifting = lifted <= 0d && _liftedFrom < 0
                    ? "none in force - the payout is unchanged"
                    : $"{1d + lifted:0.###}x from link {_liftedFrom + 1} onwards - " +
                      $"{links - _liftedFrom} of {links} links scaled, the earlier ones untouched";

                RecordFactorsAtLink(env, s, prefix, groups, width, lifted, factors, distinct);
            }

            for (var c = 1; c < classes; c++)
            {
                var weight = classed[c][s];
                var many = counted[c][s];

                if (weight == 0f && many == 0f)
                    continue;

                // **Two terms, because they are paid on different things.** A share multiplies what
                // the class is WORTH, so it is paid on the weight; a flat effect adds a fixed amount
                // to each thing of the class, so the only thing that can pay it is the count. See
                // Planner.Counts, which exists for this term alone.
                var paid = weight * (Multiplied(prefix, null, groups, width, c, lifted, amplified, factors,
                               env.IncreaseBaseOfGroup) - 1d) +
                           many * Flatly(flatly, width, c);

                propagation += paid;
                CreditToLink(detail, s, paid);

                if (detail)
                    pooled += weight * Added(prefix, null, groups, width, c);
            }

            // What each marker at this link digs up, paid one marker at a time because a remnant's
            // ORDINARY runes reach its own waves and nothing else - so they belong in this product
            // and in no other. See Propagation.Locally.
            for (var t = 0; t < found; t++)
            {
                var i = touched[t];

                if (step[i] != s || !env.Targets[i].PartsSettledPerMarker ||
                    env.Targets[i].Parts is not { Length: > 0 } waving)
                    continue;

                var own = LocalSharesOfMarker(env, _localsOf, t, groups, s, distinct, mineLifts);
                var mineLift = 0d;

                // **A lift passed in from an earlier link misses this remnant's first wave.** What was booked at this
                // link is the remnant's own carried rune, already on its slot's share of the waves; what arrived from
                // before reaches all but wave 1. See PlanTarget.ShareOfWavesPassedLift.
                var passedShare = env.Targets[i].ShareOfWavesPassedLift;
                var passedCut = false;

                for (var a = 0; a < amplifiers; a++)
                {
                    var here = lifts[a * links + s];
                    var arrived = liftedOf[a] - here;

                    chainLifts[a] = arrived * passedShare + here;
                    passedCut |= passedShare < 1f && arrived > 0d;
                }

                var markerLifted = lifted;

                // **Per class, the stronger of the chain's lift and this remnant's own**, then the classes lifting each
                // applied, as the chain's are. With one class this is the maximum it always was. See Amplification.
                if (amplified != null)
                {
                    var any = false;

                    for (var a = 0; a < amplifiers; a++)
                    {
                        markerLifts[a] = Math.Max(chainLifts[a], mineLifts[a]);
                        any |= mineLifts[a] > 0d;
                    }

                    if (passedCut && !any)
                    {
                        amplified.Effective(markerLifts, markerEffective);
                        amplified.FactorsOfMasks(markerEffective, markerFactors);
                        markerLifted = markerFactors[(1 << amplifiers) - 1] - 1d;
                    }
                    else if (any)
                    {
                        amplified.Effective(markerLifts, markerEffective);
                        amplified.FactorsOfMasks(markerEffective, markerFactors);

                        var mineProduct = 1d;

                        for (var a = 0; a < amplifiers; a++)
                            mineProduct *= 1d + mineLifts[a];

                        mineLift = mineProduct - 1d;
                    }
                    else
                    {
                        for (var m = 0; m < 1 << amplifiers; m++)
                            markerFactors[m] = factors[m];
                    }
                }
                else
                {
                    mineLift = mineLifts[0];
                    markerLifted = chainLifts[0];
                }

                // **The rate a Power passed in from earlier links brings to these waves**, the rune class's, wave 1 cut.
                // Its own effects' bonuses are lifted by it, in the content here and in the payout below. Not where the
                // remnant holds a Power itself: Weighing.OwnEffectsOfRunes has lifted them by that already, and two
                // Powers on one remnant's waves do not stack. See PlanTarget.OwnFactorsOfPart.
                var runeClass = amplified?.RuneClass ?? 0;
                var holdsPower = env.Targets[i].HeldLiftsOfChoice(_chosenOf[t]) is { } heldLiftsHere &&
                                 runeClass >= 0 && runeClass < heldLiftsHere.Length && heldLiftsHere[runeClass] > 0f;
                // Uncut: each own effect takes the share of its waves the lift reaches. See PlanTarget.OwnFactorsOfPart.
                var ownLift = !holdsPower && runeClass >= 0 && runeClass < amplifiers
                    ? liftedOf[runeClass] - lifts[runeClass * links + s]
                    : 0d;

                if (ownLift > 0d)
                {
                    var ownLifted = env.Targets[i].OwnOfChoice(_chosenOf[t], tractionOf[t], rareTractionOf[t], ownLift);

                    var ownGained = ownLifted - env.Targets[i].OwnOfChoice(_chosenOf[t], tractionOf[t], rareTractionOf[t]);

                    content += ownGained;
                    ContentToLink(s, ownGained);

                    if (detail)
                        OwnEffectsUnderPassedPower[CellKeyOf(env.Targets[i].Grid)] = (ownLift, ownLifted);
                }

                // **Both figures, because a tie has no single owner.**
                //
                // The first attempt recorded the local rune only when it beat the chain, on the
                // reasoning that an equal propagated rate had already done the work. That is the
                // wrong way round: at equal rates each is redundant GIVEN the other, and of the two
                // the local rune is the unconditional one - it is in that socket however the chain is
                // routed, while the propagation only arrives if the route puts a source upstream. A
                // tie credited to the chain credits the half that could have been left out.
                //
                // So neither is chosen. Both are kept and the reader is told there were two, which is
                // the only account that does not pick a winner where the arithmetic does not.
                if (detail && (mineLift > 0d || lifted > 0d))
                    _lifting[CellKeyOf(env.Targets[i].Grid)] = (mineLift, lifted);

                foreach (var (mask, listedPart, listedMany) in waving)
                {
                    // Its magic and rare waves as Gaining Traction grows them. See the bonus above.
                    var scale = (mask & (1L << Tags.Rares)) != 0L ? rareTractionOf[t]
                        : (mask & (1L << Tags.Magics)) != 0L ? tractionOf[t]
                        : normalTractionOf[t];
                    // And the "own" effects of the combination taken, their bonuses lifted by the Power reaching
                    // these waves. See PlanTarget.OwnFactorsOfPart.
                    var (ownWorth, ownMany) = env.Targets[i].OwnFactorsOfPart(_chosenOf[t], mask, ownLift);
                    var part = listedPart * scale * ownWorth;
                    var many = listedMany * scale * ownMany;

                    if (part <= 0f && many <= 0f)
                        continue;

                    var cls = ClassOfMask(env, mask);

                    // **One factor, the strongest empowering rate reaching these waves.**
                    //
                    // An empowering rune that propagates scales everything the chain unearths after
                    // it - that is lifted, accumulated over the links. One sitting in an ordinary slot
                    // reaches this remnant's waves and nothing else, so it scales only this payout, and
                    // it scales the WHOLE of it: the runes propagated here from earlier links as well
                    // as its siblings, because all of them land on these same monsters.
                    //
                    // **The two used to multiply, which stacked them.** A chain propagating fifty into
                    // a remnant holding its own fifty scaled that payout by 2.25, where two propagated
                    // Powers give 1.5 because Book keeps one entry per rune and the second upgrades
                    // the first rather than joining it. Three routes to the same pair of runes, two
                    // different answers. Taking the maximum is the same rule Owned applies within one
                    // remnant, so all four cases - one local, one propagated, two local, two
                    // propagated - now agree.
                    //
                    // Correct by position without a test for it: lifted is accumulated at the top of
                    // this same link's turn, so it holds what reaches link s and nothing sourced
                    // after it. That is the check Booked was getting wrong.
                    //
                    // **Handed to Multiplied rather than applied to its result, which is per-rune
                    // rather than per-chain.** Power reads "Runes gain: Empowered": it makes each
                    // rune's modifier stronger, and the stronger modifiers then compound. So the lift
                    // belongs inside the product, on each group's share, not outside it on the total.
                    //
                    // The two agree exactly on one rune and diverge as they accumulate, because
                    // (prod(1+s) - 1)(1+p) is not prod(1+s(1+p)) - 1. Measured on shares from a live
                    // table - Adaptive 4, Opulent 48, Oath 32, Death 32, Bond 32, one Power at fifty -
                    // the old form paid 3.8102 and this pays 4.9104, which is 29% more. Two runes
                    // differ by 1.8% and five small ones by 4.1%, so it grows with the socket count
                    // rather than being a rounding question.
                    //
                    // It still pays nothing when nothing propagates, which is the property the old
                    // form was chosen for: with no shares the product is one and the bonus nought,
                    // whichever side the lift sits on. Nobody is paid for a Power rune with nothing to
                    // empower. See Weighing.GroupKeyOfEffect.
                    //
                    // **The lift reaches only the shares of runes the empowering effect names**, held in
                    // twin groups - see PlanEnvironment.EmpowerableTwinOfGroup. Relic shares and the
                    // runes it does not name are not lifted. On an environment without twins, one read
                    // back from a file, it reaches every share, as it did before.
                    var paid = part * (Multiplied(prefix, own, groups, width, cls,
                                  Math.Max(markerLifted, mineLift), amplified, markerFactors, env.IncreaseBaseOfGroup) - 1d) +
                               many * Flatly(flatly, width, cls);

                    propagation += paid;
                    CreditToLink(detail, s, paid);

                    if (detail)
                        pooled += part * Added(prefix, own, groups, width, cls);
                }

                // **What the remnant's "per" effects add, lifted by the shares reaching their row.** A row tagged
                // unaffected_by_runes takes the shares no rune contributed - relics, and the map's increases through
                // the base - and no lift; any other row takes the chain's as a part does. Their face value is in
                // content already. See PlanTarget.CreatedOfEntry and Tags.UnaffectedByRunes.
                if (env.Targets[i].CreatedOfChoices is { } createdAll && _chosenOf[t] >= 0 &&
                    _chosenOf[t] < createdAll.Length && createdAll[_chosenOf[t]] is { } created)
                {
                    foreach (var entry in created)
                    {
                        var worth = env.Targets[i].CreatedOfEntry(_chosenOf[t], entry, tractionOf[t],
                            rareTractionOf[t], ownLift);

                        if (worth <= 0f)
                            continue;

                        var cls = ClassOfMask(env, entry.Mask);
                        var paid = entry.UnaffectedByRunes
                            ? worth * (Multiplied(prefixWithoutRunes, null, groups, width, cls, 0d, null, null,
                                env.IncreaseBaseOfGroup) - 1d)
                            : worth * (Multiplied(prefix, own, groups, width, cls, Math.Max(markerLifted, mineLift),
                                amplified, markerFactors, env.IncreaseBaseOfGroup) - 1d);

                        propagation += paid;
                        CreditToLink(detail, s, paid);
                    }
                }
            }
        }

        paying.Dispose();

        // **The stacking bonus is gone, because the payout now stacks.**
        //
        // Two terms used to be added here, and both existed for the same reason: the payout was a
        // SUM of rune percentages, so a monster carrying six runes was paid as though it carried
        // them one at a time. Concentrate reproduced that additive total as a staircase and added a
        // fixed increment per rune of depth; Locally did the same for a remnant's own slots over
        // its own waves. Both were approximations of compounding, with a slider where a measurement
        // should be.
        //
        // The grouped payout computes that compounding exactly - weight x (prod over stat groups of
        // (1 + sum of that group's rates) - 1), with prefix carrying every propagating rune and own
        // carrying the remnant's local ones. Runes raising different stats multiply; runes raising
        // the same stat add, which is what the game does. So the bonus is now a second payment for
        // the same effect, and a guess sitting on top of an exact answer.
        //
        // The local pass stays for what only it knows: which of a remnant's runes are wasted
        // because the chain already sends them, and the socket counts the overlay draws under it.
        // That bookkeeping never depended on the bonus and used to be skipped whenever the slider
        // sat at nought.
        // **The remnants a placed explosive already caught, for the line under them and nothing else.**
        //
        // Content the chain has already taken is moved out of Targets when the environment is built
        // - see Planning's split into left and caught - because the next explosive must not be
        // offered credit for it a second time; its worth is carried separately as the held term.
        // That is right for the objective and it silently broke the readout: RuneTallyByRemnant is filled by
        // walking Targets, so a remnant under one of the fourteen laid blasts could never get a
        // line, whatever the chain was sending onto its waves. Fourteen blank remnants and one
        // drawn, which is what it looked like from the outside.
        //
        // Registered here rather than put back into Targets, and the distinction is the whole
        // safety of it: these do not enter the coverage loop, are never credited, and cannot be
        // paid twice. They borrow the reach of whichever link caught them and are read by Locally
        // exactly like any other remnant - same bookings, same Firsts, same arithmetic - so the
        // line under a caught remnant says the same thing it would have said before the blast went
        // down. Only on a detailed pass; the search never builds this list.
        if (detail && env.Shown is { Count: > 0 })
        {
            foreach (var already in env.Shown)
            {
                var hit = -1;
                var reachAt = env.Blast + already.Radius;

                for (var k = 0; k < chain.Count; k++)
                {
                    if (Vector2.DistanceSquared(chain[k], already.Grid) > reachAt * reachAt)
                        continue;

                    hit = k;

                    break;
                }

                if (hit < 0)
                    continue;

                var mine = already.Best(after[hit], monsters[hit], env, taggedAfter, hit, 0, out var mineChose);

                _localCount = Grow(_localCount, locals + 1);
                _localReach = Grow(_localReach, locals + 1);
                _localRunes = Grow(_localRunes, locals + 1);
                _localGrid = Grow(_localGrid, locals + 1);
                _localOwn = Grow(_localOwn, locals + 1);

                _localCarry = Grow(_localCarry, locals + 1);
                _localCarry[locals] = mine.Runes;
                _localSlots = Grow(_localSlots, locals + 1);
                _localSlots[locals] = already.SlotRunesOfChoice(mineChose);
                _localRunes[locals] = mine.Locals;
                _localGrid[locals] = ((int)MathF.Round(already.Grid.X),
                    (int)MathF.Round(already.Grid.Y));
                _localOwn[locals] = mine.Runes?.Length ?? 0;
                _localCount[locals] = RunesInLocals(mine.Locals);
                _localReach[locals] = after[hit];
                _localAt = Grow(_localAt, locals + 1);
                _localAt[locals] = hit;
                locals++;
            }
        }

        using (new Phase(PhaseSettleLocally))
            Locally(env, distinct, locals, detail);

        // **What each link was credited with, published for the audit.**
        //
        // _credited is filled as the payout walks the links and is thread static, so a readout on the
        // frame cannot see it - which left the per-blast audit able to say what a blast CATCHES and
        // not what it is paid. Copied on the detailed pass only, the same pass RuneTallyByRemnant and Chosen are
        // published from, so all three describe one chain. See Credited and Dump's intent section.
        if (detail)
        {
            var credits = new double[chain.Count];

            for (var k = 0; k < chain.Count && _credited != null && k < _credited.Length; k++)
                credits[k] = _credited[k];

            Credits = credits;

            var factorsSaid = new string[chain.Count];

            for (var k = 0; k < chain.Count && _factored != null && k < _factored.Length; k++)
                factorsSaid[k] = _factored[k] ?? "";

            Factors = factorsSaid;
        }

        // Every grant of a switch after the first. Taken off content rather than never added,
        // because the coverage loop is where a marker's weight is counted and it has no idea which
        // other markers the chain caught. See the Credit local.
        //
        // **Content only**, which is now the only place it could go: insistence left Content when
        // it stopped being a weight, so there is no second term for a repeated switch to be taken
        // off twice.
        content -= repeated;

        // Only on a detailed pass, which is the one the dump and the score card ask for - the
        // search itself runs this half a million times and must not be writing to shared state.
        if (detail)
            Repeats = (repeated, switches);

        List<(double Content, double Carried)> each = null;
        var steps = detail
            ? Lines(env, chain, step, after, monsters, taggedAfter, seen, visit, out each)
            : null;

        if (detail)
        {
            Propagated = propagation;
            Pooled = pooled;
        }

        // **The marker taken last is held only when the chain's final link catches it.** It is a must take, so a chain
        // that catches it earlier loses one must take's credit and ranks as a chain that missed one: every chain
        // ending on it comes before every chain that does not, and the ranking is otherwise unchanged. Here, where the
        // whole scorer and the incremental one meet, so both agree. See PlanEnvironment.TakenLast.
        // **What the model expects each remnant's waves to bring, once everything has been paid**, so it uses what the
        // payout used: the combination chosen, the Gaining Traction scales, and the lift a passed-in Power gave its own
        // effects, as OwnEffectsUnderPassedPower recorded it (none where it was not lifted). See WavesPredictedByRemnant.
        if (detail)
        {
            for (var t = 0; t < found; t++)
            {
                var target = env.Targets[touched[t]];

                if (target.Kind != TargetKind.Remnant || _chosenOf[t] < 0)
                    continue;

                var cell = CellKeyOf(target.Grid);
                var paidLift = OwnEffectsUnderPassedPower.TryGetValue(cell, out var under) ? under.Lift : 0d;

                WavesPredictedByRemnant[cell] = PredictedWavesOf(target, _chosenOf[t], normalTractionOf[t], tractionOf[t],
                    rareTractionOf[t], paidLift);
            }
        }

        if (env.TakenLast >= 0 && musts > 0 && TakenBeforeTheFinalLink(env, chain, step, seen, visit))
            musts -= env.Targets[env.TakenLast].MustWeight;

        return new Verdict(content, propagation, travel, covered, steps, each,
            Math.Max(0, env.Musts - musts), env.Refused, musts);
    }

    /// <summary>
    /// Whether the chain catches the marker taken last at a link before its final one, so an explosive is placed after
    /// it. Everything the final blast catches, set-off barrels included, shares its step. See PlanEnvironment.TakenLast.
    /// </summary>
    private static bool TakenBeforeTheFinalLink(PlanEnvironment env, List<Vector2> chain, int[] step, int[] seen, int visit)
    {
        var last = env.TakenLast;

        return last < seen.Length && seen[last] == visit && step[last] != chain.Count - 1;
    }

    /// <summary>
    /// The chosen combination's runes, with their weights, or the remnant's whole set when it has
    /// no combinations to choose between.
    ///
    /// The weights come off the target's own rune list rather than being looked up again, so the
    /// search stays free of settings reads - a rune the target does not list is one whose weight is
    /// zero, and a zero weight rune changes nothing.
    /// </summary>
    internal static (string Id, float Weight)[] WeightsOfChosenRunes(PlanTarget target, string[] chosen)
    {
        // **No recipe chosen, so there is nothing to weigh and the bounded guess stands.**
        //
        // Choices returns null on a remnant whose rewards have not been read, and its propagating
        // slots are then empty - which makes Valuation.Passing hand back the whole rune pool. Taking
        // all of it booked 32 runes against one remnant and read as 229,939 of a 233,444 chain. So
        // the fallback is the strongest rune per slot rather than the lookup. See
        // Weighing.StrongestRunePerPropagatingSlot.
        if (chosen == null)
            return target.Runes;

        // **Weighed against every rune the propagating slots could hold, not against a per-slot
        // pick.** The pick answers a different question and cannot answer this one: it takes a
        // maximum per slot, and slots are correlated, so its answer can be a rune set no recipe
        // produces. The chosen recipe has already decided WHICH runes; this only says what each is
        // worth. See Weighing.PropagatingRuneWeights.
        var table = target.PropagatingRuneWeights ?? target.Runes;

        if (table == null)
            return null;

        // **Worked out once per recipe and handed back after that.** Settle asks this twice for every remnant a chain
        // touches on every trial, and building the array each time was 1,152 of the 1,232 bytes a settle allocated,
        // measured offline on one Grand site (2026-09-30). The answer depends only on the recipe's rune list and the
        // weight table, both fixed objects, so it is kept against the list and reused while the table is the same
        // one. Callers only read it, as they already did the target.Runes returned above.
        if (RuneWeightsOfRecipe.TryGetValue(chosen, out var kept) && ReferenceEquals(kept.Table, table))
            return kept.Weights;

        var weights = WeightsOfChosenRunesUncached(table, chosen);

        RuneWeightsOfRecipe.AddOrUpdate(chosen, new RuneWeightsOfChosenRecipe(table, weights));

        return weights;
    }

    /// <summary>What WeightsOfChosenRunes worked out for a recipe's rune list, and the table it was read from.</summary>
    private sealed record RuneWeightsOfChosenRecipe((string Id, float Weight)[] Table, (string Id, float Weight)[] Weights);

    /// <summary>
    /// WeightsOfChosenRunes' answers, kept against each recipe's rune list and dropped with it. See
    /// WeightsOfChosenRunes.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string[], RuneWeightsOfChosenRecipe>
        RuneWeightsOfRecipe = new();

    /// <summary>The body of WeightsOfChosenRunes, without its cache.</summary>
    private static (string Id, float Weight)[] WeightsOfChosenRunesUncached((string Id, float Weight)[] table,
        string[] chosen)
    {
        var found = new List<(string, float)>(chosen.Length);

        // **Every entry of the rune, not the first**: a rune with a split-off share is listed under its id and under
        // that share's key, and the recipe naming the rune takes both. See Weighing.SplitShareKeyOf.
        foreach (var id in chosen)
        {
            foreach (var (known, weight) in table)
            {
                if (string.Equals(Weighing.RuneOfKey(known), id, StringComparison.OrdinalIgnoreCase))
                    found.Add((known, weight));
            }
        }

        return found.ToArray();
    }

    /// <summary>
    /// What a remnant's own ordinary runes add, given what is already propagating into its waves.
    ///
    /// **The waves are the most concentrated thing on a dig site and the layering could not see
    /// them.** An ordinary slot reaches its own remnant's waves and nothing else, so five of them
    /// put five modifiers on the same monsters - and those monsters are ALSO carrying every rune
    /// propagating from this link and every link before it. Six or seven deep is ordinary there,
    /// where the general pools rarely pass three.
    ///
    /// Counted as the difference rather than as a layer of its own, which is what keeps it from
    /// paying twice. The general pass has already given these waves a bonus at whatever depth the
    /// propagating runes reach them - they are part of after[] like any other monster weight - so
    /// what is owed here is the depth WITH the ordinary runes less the depth without, over this
    /// remnant's wave weight alone.
    ///
    /// The propagating runes reaching a link are the ones booked with at least that link's reach:
    /// after[] falls as the chain goes on, so a rune whose reach is greater or equal was booked at
    /// or before it. That is the same nesting the staircase is built on.
    /// </summary>
    /// <summary>
    /// Whether a rune is already booked reaching at least this far.
    ///
    /// The booked set is what the chain propagates, and a booking whose reach is at least this
    /// link's reach was made at or before it - the same nesting the staircase is built on. So a
    /// local rune matching one of those is landing on waves that already have it.
    /// </summary>
    /// <summary>
    /// Whether the chain is already sending this rune to a remnant at this link.
    ///
    /// Asked so a rune that does not stack is not paid for twice - once as the chain's and once as
    /// the copy in a remnant's own socket. It was wrong in both directions at once.
    ///
    /// **It compared reach where it needed order.** "Already reaching here" is a question about where
    /// in the chain the rune was sourced, and reach is no proxy for that: a rune sourced at link five
    /// can reach further than one sourced at link two, so a remnant at link three had its own copy
    /// struck by a rune that had not happened yet. That undervalued, and it undervalued most on
    /// exactly the remnants a long chain is built to reach. The order is now compared directly, and
    /// against the earliest link the rune reaches monsters from rather than the link of its strongest
    /// source - see Book's _carriedFrom, which exists because those are different links.
    ///
    /// **And it asked about one key where a rune may be filed under several.** A scoped share is
    /// booked against the rune AND what it reaches, under a negative key; this looked up only the
    /// plain positive one, so a scoped rune's local copy went unrecognised and was paid in full on
    /// top of the chain's. That overvalued. Scoped bookings are matched too now - see Filed.
    ///
    /// The two faults pushed opposite ways, which is why neither showed as a drift in the audit: the
    /// figures were wrong per remnant and roughly self cancelling across a site.
    /// </summary>
    /// <summary>
    /// What a set of runes is worth once the ones already reaching here are struck.
    ///
    /// **The choice was made at gross and paid at net, and the gap was invisible.** Best ranks a
    /// combination on the summed weight of its runes; the payout then discards any rune an earlier
    /// link already sends - Book keeps one entry per rune, Owned skips a local the chain is already
    /// carrying - so a combination could be chosen for a rune it would never be the source of.
    /// Measured on one remnant: Expansive Alloy chosen on a carry of 8 with Gasp already first
    /// sourced a link earlier, and paid as 4, while a combination worth 2 genuinely new runes lost.
    ///
    /// Applied to both halves, because both are summed the same way and both are struck the same way
    /// in the payout. On the site it was found on the local half was the larger number on three
    /// remnants of five, so fixing only the carried half would have fixed the smaller error.
    ///
    /// Subtracted from the gross rather than summed afresh: a target with no rune list still has a
    /// carry - a relic's flat percentage is the case - and rebuilding the total from the runes alone
    /// would quietly drop it. See Settle's booking loop, which is what fills the set this reads.
    /// </summary>
    internal static float DiscountedForDuplicates(PlanEnvironment env, PlanTarget target,
        (string Id, float Weight)[] runes, float gross, int at, int distinct)
    {
        if (runes is not { Length: > 0 } || gross <= 0f)
            return gross;

        var cell = CellKeyOf(target.Grid);
        var struck = 0f;

        foreach (var (id, weight) in runes)
        {
            if (weight > 0f && id != null && IsRuneAlreadySent(env, id, cell, at, distinct))
                struck += weight;
        }

        return struck <= 0f ? gross : Math.Max(0f, gross - struck);
    }

    /// <summary>
    /// The same, for a combination's propagating runes, which are held as bare ids.
    ///
    /// **Separate because the obvious version allocated, and this runs everywhere.** The first
    /// attempt paired the ids with their weights through Named and handed the result to the overload
    /// above - one array per COMBINATION, per marker, per scored chain, plus a nested scan of
    /// case-insensitive string comparisons to build it. The search stopped being able to get through
    /// its rounds: workers that used to open on the inherited chain's own score came up hundreds of
    /// points short of it, which reads as the inheritance being broken and is really the clock
    /// running out.
    ///
    /// Nothing is built here. The cheap test comes first - Booked is a scan of integers - and the
    /// weight is only looked up for a rune actually struck, which is the rare case. A combination
    /// duplicating nothing costs one integer scan per rune and no string comparison at all.
    /// </summary>
    internal static float DiscountedForDuplicates(PlanEnvironment env, PlanTarget target, string[] chosen,
        float gross, int at, int distinct)
    {
        if (chosen is not { Length: > 0 } || gross <= 0f ||
            target.Runes is not { Length: > 0 } held)
            return gross;

        var cell = CellKeyOf(target.Grid);
        var struck = 0f;

        foreach (var id in chosen)
        {
            if (id == null)
                continue;

            // Each key of the rune struck on its own booking: its id and any split-off share, which Book keeps apart.
            // See Weighing.SplitShareKeyOf.
            foreach (var (known, weight) in held)
            {
                if (string.Equals(Weighing.RuneOfKey(known), id, StringComparison.OrdinalIgnoreCase) &&
                    IsRuneAlreadySent(env, known, cell, at, distinct))
                    struck += weight;
            }
        }

        return struck <= 0f ? gross : Math.Max(0f, gross - struck);
    }

    /// <summary>
    /// Whether the BANKED set is sending this rune here from somewhere else.
    ///
    /// **Booked cannot answer it, and that left the discount half blind.** A remnant an explosive
    /// already covers passes its runes on as plain rates rather than as bookings the reach test can
    /// see - see PlanEnvironment.BankedRunes - so on a chain with three links down, most of what
    /// reaches the links ahead is invisible to Booked. Measured on one: Gasp banked from (1231,678)
    /// was arriving at (1114,684), and the combination carrying Gasp was credited for it in full.
    ///
    /// Third time this omission has been made in a different place. Elsewhere counts these for the
    /// inherited figure and Doubled strikes them from the local count; this is the same rule for the
    /// half that ranks combinations.
    /// </summary>
    private static bool IsRuneBankedElsewhere(PlanEnvironment env, string id, (int X, int Y) cell)
    {
        if (env.BankedRunes is not { Length: > 0 } banked)
            return false;

        foreach (var (name, from) in banked)
        {
            if (IsBankedRuneReaching(env, from, cell) && string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a rune banked from one caught remnant reaches the waves of another remnant.
    ///
    /// Never its own. Otherwise only forward along the chain: a remnant the chain has yet to take is after every
    /// explosive already down and receives everything banked, while a caught remnant receives only what was
    /// banked at or before its own link - the same blast included, as for live links. Every banked rune used to
    /// reach every caught remnant, so after a re-solve with twelve explosives down the second remnant of the
    /// chain was credited with runes from the thirteenth. The game passes runes forward only: on a Grand site recorded
    /// 2026-09-30 the third remnant's monsters carried the runes of the second and none from later links.
    /// Readout only; the payout books banked carries at the first link, which every link still to plan follows.
    /// </summary>
    private static bool IsBankedRuneReaching(PlanEnvironment env, (int X, int Y) from, (int X, int Y) cell)
    {
        if (from == cell)
            return false;

        if (env.LinkOfCaught == null || !env.LinkOfCaught.TryGetValue(cell, out var mine))
            return true;

        return !env.LinkOfCaught.TryGetValue(from, out var theirs) || theirs <= mine;
    }

    /// <summary>Whether anything upstream is already sending this rune to the given remnant.</summary>
    internal static bool IsRuneAlreadySentTo(PlanEnvironment env, PlanTarget target, string id, int at, int distinct) =>
        IsRuneAlreadySent(env, id, CellKeyOf(target.Grid), at, distinct);

    /// <summary>
    /// What the shares in force on a remnant's waves multiply one part of them by in the payout, for the part's class:
    /// Multiplied over shares laid out as the payout's prefix, with no lift. For PlanTarget.OwnPaidOfChoice. See
    /// SharesInForceAt.
    /// </summary>
    internal static double PaidProductOfMask(PlanEnvironment env, float[] shares, int groups, int width, long mask) =>
        Multiplied(shares, null, groups, width, ClassOfMask(env, mask), 0d, null, null, env.IncreaseBaseOfGroup);

    /// <summary>
    /// What one point of an unscoped carried effect is paid per point of the monster weight from link at onwards, given
    /// the shares in force: the weight split into rare, magic and the rest, each paid the product of every other group
    /// on its class, over the effect's own group's base. 1 where the split or the group is not to hand. For
    /// PlanTarget.Best, so a carried rune is ranked as paid, as scoped ones and own effects are.
    ///
    /// The split comes from the per-tag sums, which hold a tag only when some effect on the site is scoped at it. A tag
    /// nothing is scoped at has no share of its own, so its monsters are paid as the rest are and need no split.
    ///
    /// Without it a carried Cold at +1% was ranked at 1% of the downstream monsters on a site whose relic doubled
    /// rares, where the payout paid about 2% on the rares among them, while a rune scoped at rares was ranked at the
    /// doubled figure.
    /// </summary>
    internal static double PaidFactorOfCarriedEffect(PlanEnvironment env, float[] shares, int groups, int width, string id,
        float[][] sums, int at, float downstream)
    {
        var g = id == null ? -1 : GroupIndexOfEffect(env, id);
        var k = Indexed(env, Tags.Monsters);

        if (shares == null || downstream <= 0f || g < 0 || g >= groups || k < 0 || g == env.Empowering)
            return 1d;

        var rares = Math.Min(Reach(env, Tags.Rares, sums, null, at), downstream);
        var magics = Math.Min(Reach(env, Tags.Magics, sums, null, at), downstream - rares);
        var rest = downstream - rares - magics;
        var monster = (1L << Tags.Monsters) | (1L << Tags.Modifiables);

        var paid = rest * PaidFactorOfShare(env, shares, groups, width, g, k, ClassOfMask(env, monster | (1L << Tags.Normals)));

        if (rares > 0f)
            paid += rares * PaidFactorOfShare(env, shares, groups, width, g, k, ClassOfMask(env, monster | (1L << Tags.Rares)));

        if (magics > 0f)
            paid += magics * PaidFactorOfShare(env, shares, groups, width, g, k, ClassOfMask(env, monster | (1L << Tags.Magics)));

        return paid / downstream;
    }

    /// <summary>
    /// What one point of a scoped effect is paid per point of the weight it reaches, given the shares in force: the
    /// product of every other group on the tag's class, over the effect's own group's base. 1 where the effect's group
    /// or tag is not one the payout lays out. For PlanTarget.Best, so a scoped carry is ranked as paid, as its own
    /// effects are. See OwnPaidOfChoice.
    ///
    /// Without it a site's relics were in the own effects and not in the spread: a Bond at +15% rare monsters on a
    /// site whose relic doubled rares was ranked at half its pay against own effects counted at the full product, and
    /// lost to a reward the chain then scored 124 below it (2026-10-09).
    ///
    /// The shares are those in force at the ranking link, so a relic first reached further down is not counted.
    /// </summary>
    internal static double PaidFactorOfScopedEffect(PlanEnvironment env, float[] shares, int groups, int width, string id, int tag)
    {
        var g = id == null ? -1 : GroupIndexOfEffect(env, id);
        var k = Indexed(env, tag < 0 ? Tags.Monsters : tag);

        if (shares == null || g < 0 || g >= groups || k < 0 || g == env.Empowering)
            return 1d;

        var mask = (1L << (tag < 0 ? Tags.Monsters : tag)) | (1L << Tags.Modifiables);

        if (tag >= 0 && Tags.Monsterly(tag))
            mask |= 1L << Tags.Monsters;

        return PaidFactorOfShare(env, shares, groups, width, g, k, ClassOfMask(env, mask));
    }

    /// <summary>
    /// What one point of a share in group g at relevant tag k is paid on class cls per point of that class's weight:
    /// the product of every other group on the class, over group g's base. See PaidFactorOfScopedEffect.
    /// </summary>
    private static double PaidFactorOfShare(PlanEnvironment env, float[] shares, int groups, int width, int g, int k, int cls)
    {
        var slot = g * width + k;
        var held = shares[slot];
        var before = Multiplied(shares, null, groups, width, cls, 0d, null, null, env.IncreaseBaseOfGroup);

        // The product is linear in one group's share, so one step of a point gives the factor exactly.
        shares[slot] = held + 0.01f;
        var after = Multiplied(shares, null, groups, width, cls, 0d, null, null, env.IncreaseBaseOfGroup);
        shares[slot] = held;

        return (after - before) / 0.01d;
    }

    /// <summary>
    /// The part of what a combination's propagated runes reach that a later remnant on the chain sends them to anyway:
    /// for each rune it would be the first to send, its weight times what the chain unearths from the first later link
    /// whose provisional pick sends it. In the units of carried percent times monster weight. For PlanTarget.Best. See
    /// Settle's provisional pass.
    ///
    /// **A rune sent again further down is worth only the links before the second sender.** Booking keeps the earlier
    /// copy, so the chain gains it from this link to the later one, and the later remnant's socket would have covered
    /// the rest. Ranked on the whole of the downstream reach, 3x Orb of Alchemy sending Adaptive tied 3x Glassblower's
    /// Bauble sending Prismatic on a Frigid Bluffs layout where a remnant four links on sent Adaptive too, and scored
    /// 198.5 below it.
    /// </summary>
    internal static double LaterSentReach(PlanEnvironment env, PlanTarget target, string[] runes, int at, int distinct,
        (int Key, int Link)[] later, int laterCount, float[] after)
    {
        if (later == null || laterCount == 0 || runes is not { Length: > 0 } || after == null)
            return 0d;

        var covered = 0d;

        foreach (var (id, weight) in WeightsOfChosenRunes(target, runes) ?? [])
        {
            if (id == null || weight <= 0f || IsRuneAlreadySentTo(env, target, id, at, distinct))
                continue;

            var key = NumberOfEffect(env, Weighing.RuneOfKey(id));
            var first = int.MaxValue;

            for (var x = 0; x < laterCount; x++)
            {
                if (later[x].Key == key && later[x].Link > at && later[x].Link < first)
                    first = later[x].Link;
            }

            if (first < after.Length)
                covered += weight * after[first];
        }

        return covered;
    }

    /// <summary>Whether anything upstream is already sending this rune to that remnant.</summary>
    private static bool IsRuneAlreadySent(PlanEnvironment env, string id, (int X, int Y) cell, int at,
        int distinct) =>
        IsRuneBookedUpstream(env, id, at, distinct) || IsRuneBankedElsewhere(env, id, cell);

    private static bool IsRuneBookedUpstream(PlanEnvironment env, string id, int mineAt, int distinct)
    {
        var key = NumberOfEffect(env, id);

        if (key < 0)
            return false;

        for (var r = 0; r < distinct; r++)
        {
            if (IsBookingThisRune(_carriedIds[r], key) && _carriedFrom[r] <= mineAt)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a booked key is this rune, however it was filed. See Book's negative keys.
    ///
    /// Book stores an unscoped booking under the rune's own number and a scoped one under
    /// -((number * 64 + tag) * 2 + flat + 2), so a rune reaching three kinds of thing holds three
    /// entries and not one of them equals its number. The low bit tells an amount from a share,
    /// which are different effects that may share a name and a tag. Decoded rather than re-derived,
    /// so the two stay in step.
    /// </summary>
    private static bool IsBookingThisRune(int booked, int key)
    {
        if (booked == key)
            return true;

        if (booked >= 0)
            return false;

        var packed = (-booked - 2) / 2;

        return packed >= 0 && packed / 64 == key;
    }

    /// <summary>Adds to a link's share, when a detailed pass is keeping them. See _credited.</summary>
    private static void CreditToLink(bool detail, int at, double paid)
    {
        if ((detail || _recordingLinks) && _credited != null && at >= 0 && at < _credited.Length)
            _credited[at] += paid;
    }

    /// <summary>
    /// Every factor in force at one link, written out, so a propagation figure can be checked.
    ///
    /// **A score nobody can take apart is a score nobody can argue with.** The trail said what a
    /// blast CATCHES and what it was paid, and nothing in between: "propagation 598.1" with no
    /// working. Three things were invisible, and each is a different way to be wrong.
    ///
    /// - **What each modifier was worth after empowering.** A Power rune makes every other rune's
    ///   modifier stronger, so Adaptive's +4% arrives as +6%, and the trail printed the 4.
    /// - **Which group each share went into.** That is the whole of whether two modifiers add or
    ///   multiply, and it was decided by Weighing.GroupKeyOfEffect and reported nowhere.
    /// - **The product.** The bonus is a product over groups, and only its result was printed.
    ///
    /// Written from the same arrays the payout reads, at the same point in the same walk, which is
    /// the only arrangement in which the explanation cannot drift from the thing explained. The
    /// blast circles were a second calculation once and they disagreed with the score they sat
    /// inside; see Credits.
    ///
    /// Detailed passes only. The search runs this loop hundreds of thousands of times and must not be
    /// building strings in it.
    /// </summary>
    private static void RecordFactorsAtLink(PlanEnvironment env, int at, float[] prefix, int groups, int width,
        double lifted, double[] factors, int distinct)
    {
        _factored = Grow(_factored, Math.Max(at + 1, 1));

        var said = new StringBuilder();

        // **Every class the chain lifts, not just monsters.**
        //
        // This printed the monster class alone, on the reasoning that almost everything a chain
        // unearths is a monster and one class keeps the line readable. It stopped being true the
        // moment a relic said rare_monster: ElitesDuplicated doubling rares vanished out of the
        // section entirely - present in the score, absent from the only place that explains the
        // score - and the per-link worths no longer added up to anything a reader could check.
        //
        // A class with nothing in force is skipped, so an ordinary site still prints one line.
        var relevant = env.Relevant;

        for (var k = 0; k < relevant.Length; k++)
        {
            var anything = false;

            for (var g = 0; g < groups && !anything; g++)
                anything = prefix[g * width + k] != 0f;

            if (!anything)
                continue;

            var cls = 1 << k;

        // **Listed from the same loop that builds the product, so the two cannot disagree.** They
        // used to be two walks: the names came from the booked carries with three filters on them,
        // and the factors came from the accumulated per-group shares with none - so any booking a
        // filter dropped multiplied the score and was named nowhere. Seen on a live site, twice in
        // one dump: "factors" listing two entries above a product of four, with the two scoped
        // relic rates that made up the difference appearing in neither the list nor anything else a
        // reader could total.
        //
        // One pass now. Each group prints what it is made of, what those come to, and the factor
        // that follows - and the factors multiplied are the same values, in the same order.
            var product = new List<string>();

            for (var g = 0; g < groups; g++)
            {
                var add = prefix[g * width + k];

                if (add == 0f)
                    continue;

            // The lift on this group's shares: its classes' factor where there is a table, and every class together
            // where there is not. A plain group's mask is nought, so its factor is one. See Amplification.
            var amplified = env.Amplified;
            var groupLift = amplified != null && g < amplified.MaskOfGroup.Length
                ? amplified.LiftedFactorOfGroup(g, factors) - 1d
                : lifted;
            var factor = 1d + add * (1d + groupLift);

            product.Add(factor.ToString("0.####", CultureInfo.InvariantCulture));

            said.Append("      group ")
                .Append(g == 0 ? "0 (the unclassified pool)" : g.ToString())
                .Append(": ");

            // Which bookings landed in this group and reach monsters from this link. Every one of
            // them is a modifier somebody can point at on the ground.
            var named = new List<string>();

            for (var r = 0; r < distinct; r++)
            {
                // **Which class this booking lands in, asked the way Rate asks it.** A booking
                // carries a tag and Rate turns that into a class index - minus one meaning "reaches
                // whatever the chain unearths", which is the monster class. Testing Monsterly here
                // instead listed the monster bookings under every class once this loop began
                // printing more than one.
                //
                // In force at this link is _carriedFrom, not _carriedAt: the first is where the
                // booking starts paying, the second is the link whose circle gets the credit.
                if (_carriedGroup == null || r >= _carriedGroup.Length || _carriedGroup[r] != g ||
                    _carriedTag == null || r >= _carriedTag.Length ||
                    Indexed(env, _carriedTag[r] < 0 ? Tags.Monsters : _carriedTag[r]) != k ||
                    _carriedFrom == null || r >= _carriedFrom.Length || _carriedFrom[r] > at ||
                    _carriedWeights == null || r >= _carriedWeights.Length || _carriedWeights[r] <= 0f)
                    continue;

                named.Add($"{(_carriedNames != null && r < _carriedNames.Length ? _carriedNames[r] : null) ?? "?"} " +
                          $"+{_carriedWeights[r].ToString("0.##", CultureInfo.InvariantCulture)}%");
                }

                // And the shares that were rated straight in rather than booked. Same three
                // tests: this group, this class, and in force by this link. See Rate's what.
                foreach (var (band, tag, from, percent, what) in _ratedAside ?? [])
                {
                    if (band != g || Indexed(env, tag < 0 ? Tags.Monsters : tag) != k ||
                        from > at || percent <= 0f)
                        continue;

                named.Add($"{what} +{percent.ToString("0.##", CultureInfo.InvariantCulture)}%");
            }

            // **A group whose parts cannot be named is the thing worth seeing.** The share is real -
            // it is what the payout multiplies by - so if nothing here accounts for it, the booking
            // that produced it is reaching the scoring by a path this trail does not know about.
            said.Append(named.Count > 0
                    ? string.Join(" + ", named)
                    : "NOTHING NAMED - a share the trail cannot account for")
                .Append(" = +")
                .Append((add * 100d).ToString("0.##", CultureInfo.InvariantCulture))
                .Append('%');

            if (groupLift > 0d)
            {
                said.Append(" lifted x")
                    .Append((1d + groupLift).ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" -> +")
                    .Append((add * (1d + groupLift) * 100d).ToString("0.##", CultureInfo.InvariantCulture))
                    .Append('%');
            }

                said.Append("  -> x")
                    .Append(factor.ToString("0.####", CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            var mult = Multiplied(prefix, null, groups, width, cls, lifted, env.Amplified, factors,
                env.IncreaseBaseOfGroup);

            said.Append("      ")
                .Append(Tags.Known[relevant[k]])
                .Append(": ")
                .Append(product.Count == 0 ? "nothing propagating here" : string.Join(" x ", product))
                .Append(" = ").Append(mult.ToString("0.####", CultureInfo.InvariantCulture))
                .Append("  -> bonus +")
                .Append(((mult - 1d) * 100d).ToString("0.#", CultureInfo.InvariantCulture))
                .Append('%')
                .AppendLine();
        }

        _factored[at] = said.Length == 0 ? "nothing propagating here" : said.ToString().TrimEnd();
    }

    /// <summary>
    /// Every factor in force at each link of the last detailed pass. See Trailed.
    /// </summary>
    public static string[] Factors { get; private set; } = [];

    [ThreadStatic] private static string[] _factored;

    private static void Locally(PlanEnvironment env, int distinct, int locals, bool detail)
    {
        if (locals <= 0)
            return;

        for (var n = 0; n < locals; n++)
        {
            var reach = _localReach[n];
            var mineAt = _localAt != null && n < _localAt.Length ? _localAt[n] : 0;
            var runes = 0;

            // How many RUNES reach these waves from earlier links, which is what the line under the
            // remnant counts back. Not how much percentage they carry: that is the payout's business
            // and the payout now does it in one place. See the note at the call site.
            // **Named as well as counted, because "0 inherited" is unarguable and unexplainable.**
            //
            // A remnant reading 6 (6+0-0) beside neighbours reading 1 (0+1-0) is either right or a
            // bug, and the count alone cannot say which: nought means either nothing upstream
            // propagates, or something does and the reach test refused it. The three reasons a
            // booking is skipped here are each a different fault, so the ones skipped are written
            // down with the ones kept. See Carrying.
            var telling = detail ? new List<string>() : null;

            for (var r = 0; r < distinct; r++)
            {
                // Judged on ORDER, because reach cannot tell blast two from blast four when the
                // blasts between them unearth nothing. See _localAt.
                //
                // Against _carriedFrom rather than _carriedAt, which is the correction Booked needed
                // too: _carriedAt is the link of the STRONGEST source, so a rune in force since link
                // two but strongest at link five read as arriving at five. One question, one field,
                // in the display and in the payout alike - two tests for it is how these came to
                // disagree in the first place.
                if (_carriedRich[r] <= 0f || _carriedWeights[r] <= 0f || _carriedFrom[r] > mineAt)
                {
                    if (telling != null && _carriedRune[r])
                    {
                        telling.Add($"{_carriedNames[r]} from " +
                                    $"({_carriedBy[r].X},{_carriedBy[r].Y}) SKIPPED (" +
                                    (_carriedRich[r] <= 0f ? "not on monsters"
                                        : _carriedWeights[r] <= 0f ? "no weight"
                                        : $"reaches monsters from link {_carriedFrom[r] + 1}, after " +
                                          $"this one at {mineAt + 1}") + ")");
                    }

                    continue;
                }

                // A split-off share is its rune's second booking and not another rune. See Weighing.SplitShareKeyOf.
                if (_carriedRune[r] && !Weighing.IsSplitShareKey(_carriedNames[r]))
                {
                    runes++;

                    // With the owner, because "which remnant is this rune credited to" is the
                    // question every disagreement about a first source turns out to be. See Firsts.
                    telling?.Add($"{_carriedNames[r]} from ({_carriedBy[r].X},{_carriedBy[r].Y}) " +
                                 $"reaches {_carriedRich[r]:0.#}");
                }
            }

            // **The ordinary runes the chain is already propagating into these waves, struck out.**
            //
            // Runes do not stack. A rune in an ordinary slot reaches this remnant's own waves, and
            // if something earlier in the chain is already sending that same rune there, the copy
            // adds nothing at all. The propagating half has enforced that from the start - see Book
            // - and this half never did, so a remnant whose ordinary slots duplicated the chain was
            // paid in full for them.
            //
            // Taken off here rather than never added, because the loop credits the local term before
            // there is a chain to compare against: what reaches a link is not known until every
            // booking is in. Same reason Locally is a post pass at all.
            // Counted by rune, a lift key as its rune: Power held for its lift is filed as a lift key, and compared
            // by that name it never matched the Power arriving from upstream. See RunesInLocalsArriving.
            var wasted = RunesInLocalsArriving(_localRunes[n], id => IsRuneBookedUpstream(env, id, mineAt, distinct));

            // **Counted, not charged for.** The duplicate is never paid in the first place now -
            // see Owned, which skips a local rune the chain is already sending to these waves - so
            // taking it off again here would strike it twice. What survives is the COUNT, which the
            // line under a remnant needs: "9 (5+6-2)" is two local runes that landed on waves that
            // already had them.
            _localCount[n] = Math.Max(0, _localCount[n] - wasted);

            // What the overlay says under this remnant, worked out by the thing that knows. See
            // RuneTallyByRemnant - the alternative is the overlay recomputing it and drifting, which is how the
            // blast circles came to disagree with the score they sat inside.
            if (detail)
            {
                // **A propagating rune the chain already carries is wasted too.**
                //
                // Confirmed against a remnant by hand: five sockets holding Rebirth, Sky,
                // Bloodletting, Time and Volcanic, with Rage, Time, Volcanic, Oath, Arcane and Life
                // arriving from earlier links. Time and Volcanic are both already there, so nine
                // distinct runes land - and this read ten, because only the LOCAL duplicate was
                // being struck. Time sat in a propagating slot and went uncounted.
                //
                // The scoring was right all along: Book keeps whichever booking reaches furthest, so
                // this remnant's Time lost to the earlier one and earned nothing. It was the readout
                // that disagreed with it.
                //
                // Firsts is what tells the two apart - a propagating rune this remnant is NOT the
                // first source of is one the chain already had.
                var adds = RunesFirstSourcedBy(_localGrid[n], distinct);

                // What is on its way here from further back, by name, for a line that has to be
                // answerable about a combination this remnant has not been given. Strictly earlier,
                // so nothing this remnant might itself be the source of is in it. See Runes.Arriving.
                // **At or before this link, less what this remnant is the source of.**
                //
                // Strictly earlier was the first rule, chosen so a remnant could not see its own runes
                // arriving - and it threw away the ones coming from a remnant the SAME blast catches,
                // which are every bit as present. Caught by the window disagreeing with the ground:
                // Vision reached at 678.1, the same reach as this link, from the other remnant under
                // blast five, and a local Vision here went unstruck. Inherited counted it and this did
                // not, which is why the two lists were one apart.
                //
                // Its own are removed by name instead, which is what adds is - the same subtraction
                // Inherited makes a line below. See Firsts.
                var arriving = new List<string>();

                for (var r = 0; r < distinct; r++)
                {
                    if (!_carriedRune[r] || Weighing.IsSplitShareKey(_carriedNames[r]) || _carriedRich[r] <= 0f ||
                        _carriedWeights[r] <= 0f || _carriedFrom[r] > mineAt)
                        continue;

                    if (adds.Contains(_carriedNames[r], StringComparer.OrdinalIgnoreCase))
                        continue;

                    if (!arriving.Contains(_carriedNames[r], StringComparer.OrdinalIgnoreCase))
                        arriving.Add(_carriedNames[r]);
                }

                // **And the BANKED ones, which are booked nowhere and so appear in no loop above.**
                //
                // A remnant under an explosive already down sends its runes forward as plain rates -
                // see PlanEnvironment.BankedRunes - so on a chain with three links laid, most of what
                // reaches a later remnant arrives this way and none of it is in the carried arrays.
                // Counted in Inherited by Elsewhere and missing from the names entirely: a remnant
                // reading three inherited reported nothing arriving, so a combination offering a rune
                // the chain had already sent looked like the first source of it. Same omission as the
                // one Elsewhere exists to fix, in the half that names them.
                foreach (var (name, from) in env.BankedRunes ?? [])
                {
                    if (IsBankedRuneReaching(env, from, _localGrid[n]) &&
                        !arriving.Contains(name, StringComparer.OrdinalIgnoreCase))
                        arriving.Add(name);
                }

                // **And of what it BANKED**, which is booked nowhere and so appears in no Firsts. A
                // remnant under a placed explosive sends its runes forward as plain rates - see
                // PlanEnvironment.BankedRunes - so without this it read as the source of nothing while
                // every later remnant inherited from it.
                // **Propagated runes the scoring does not price, by name.** A rune worth nothing to the score - one whose
                // row is an own effect only, as Oath's is - is never booked, so the booked set above cannot name it,
                // though the game passes it on all the same. A remnant sending Oath read "13 (5+11-3)" with nothing
                // named, against "14 (5+11-2) Oath" in the combinations window, which counts by name. Taken from each
                // remnant's chosen propagating sockets: one at or before this link arrives here, and this remnant's own
                // is one it first sources unless it is already arriving.
                var unpriced = 0;
                var unpricedOwn = 0;

                for (var m = 0; m < locals; m++)
                {
                    if (m == n || _localCarry == null || m >= _localCarry.Length ||
                        (_localAt != null && m < _localAt.Length && _localAt[m] > mineAt))
                        continue;

                    foreach (var id in _localCarry[m] ?? [])
                    {
                        if (id == null || adds.Contains(id, StringComparer.OrdinalIgnoreCase) ||
                            arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                            continue;

                        arriving.Add(id);
                        unpriced++;
                    }
                }

                if (_localCarry != null && n < _localCarry.Length && _localCarry[n] is { Length: > 0 } ownCarry)
                {
                    var withUnpriced = new List<string>(adds);

                    foreach (var id in ownCarry)
                    {
                        if (id != null && !withUnpriced.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                            !arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            withUnpriced.Add(id);
                            unpricedOwn++;
                        }
                    }

                    adds = withUnpriced.ToArray();
                }

                if (env.BankedRunes is { Length: > 0 } sent)
                {
                    var mineSent = new List<string>(adds);

                    foreach (var (name, from) in sent)
                    {
                        if (from == _localGrid[n] && !mineSent.Contains(name, StringComparer.OrdinalIgnoreCase))
                            mineSent.Add(name);
                    }

                    adds = mineSent.ToArray();
                }
                var spare = Math.Max(0, _localOwn[n] - adds.Length);

                if (telling != null)
                {
                    PropagationTrailByRemnant[_localGrid[n]] =
                        $"reach {reach:0.#}, first sources [{string.Join(", ", adds)}], " +
                        $"booked [{(telling.Count == 0 ? "nothing" : string.Join("; ", telling))}]" +
                        $", plus {BankedRunesReaching(env, _localGrid[n])} banked from elsewhere of " +
                        $"[{string.Join(", ", (env.BankedRunes ?? []).Select(b => $"{b.Id}@{b.From.X},{b.From.Y}"))}]" +

                        // **What this remnant's own sockets hold, by name, and what each is worth.**
                        //
                        // A remnant with six sockets read 5 (5+0-0), and the missing one was a fixed
                        // Power rune - which scales other runes rather than monsters, so it has no
                        // worth of its own. Owned skips a local rune whose worth is nought and has no
                        // empowering branch at all, so a Power rune in an ordinary slot is invisible
                        // to the payout, to the socket count, and to the WRT multiplier alike.
                        //
                        // Named here so the difference between "not in the list" and "in the list and
                        // ignored" is visible, because the fix is not the same one. See LocalSharesOfMarker.
                        $"; ordinary sockets [{string.Join(", ", (_localRunes[n] ?? []).Select(r => $"{r.Id}={r.Worth:0.#}"))}]" +

                        // **And the propagating ones, which are sockets too.** Sockets counts both
                        // and this line named one, so a first source read as a rune the remnant did
                        // not have - the reading that sent two rounds of diagnosis after a fault
                        // that was in the printing. Named separately rather than merged, because
                        // which slot a rune sits in is the whole difference between reaching this
                        // remnant's own waves and reaching the rest of the chain.
                        $"; propagating sockets [{string.Join(", ", _localCarry != null && n < _localCarry.Length ? _localCarry[n] ?? [] : [])}]" +

                        // **The two hops between that list and what books**, so a rune that drops
                        // out says where. "carries weighted" is every rune the remnant could hold
                        // with its weight; "offered to booking" is what survived Named. A name in
                        // the propagating sockets and missing from the first was dropped by
                        // Weighing.PropagatingRuneWeights for weighing nought; present in the first and
                        // missing from the second was dropped by Named for not matching.
                        $"; carries weighted [{string.Join(", ", (_localKnown != null && n < _localKnown.Length ? _localKnown[n] ?? [] : []).Select(r => $"{r.Id}={r.Weight:0.##}"))}]" +
                        $"; offered to booking [{string.Join(", ", (_localNamed != null && n < _localNamed.Length ? _localNamed[n] ?? [] : []).Select(r => $"{r.Id}={r.Weight:0.##}"))}]" +
                        (_lifting.TryGetValue(_localGrid[n], out var mine) &&
                         (mine.Mine > 0d || mine.Chain > 0d)
                            ? $"; empowered by {1d + Math.Max(mine.Mine, mine.Chain):0.###} - " +
                              (mine.Mine > 0d && mine.Chain > 0d
                                  ? $"its own runes give {1d + mine.Mine:0.###} and the chain " +
                                    $"propagates {1d + mine.Chain:0.###}, which do not stack, so " +
                                    (Math.Abs(mine.Mine - mine.Chain) < 0.0005d
                                        ? "either alone would do it"
                                        : mine.Mine > mine.Chain
                                            ? "its own applies and the propagated one is redundant"
                                            : "the propagated one applies and its own is redundant")
                                  : mine.Mine > 0d
                                      ? "from its own runes"
                                      : "propagated here from an earlier link")
                            : "");
                }

                // Written, so whatever was out of date no longer is. See RuneTallyOutOfDate.
                RuneTallyOutOfDate = false;

                RuneTallyByRemnant[_localGrid[n]] = new RuneTally(
                    // Counts every socket, empowering ones included - they were invisible here too,
                    // which is what made a six socket remnant read five. See LocalSharesOfMarker's lift.
                    Sockets: RunesInLocals(_localRunes[n]) + _localOwn[n],

                    // Less what it FIRST sources, not less what it holds. Subtracting everything it
                    // holds took the duplicated Time out of the inherited count as well, where it
                    // genuinely belongs - it does arrive from an earlier link. That was a second
                    // error in the same line, cancelling half of the first and leaving the total
                    // one too high rather than two.
                    // Plus what the explosives already down are sending, which is booked nowhere and
                    // reaches everything after them - so it reaches this remnant.
                    //
                    // **The NAMES, not the bookings.** This counted Secured entries, and a caught
                    // remnant contributes one entry carrying its whole flat percentage however many
                    // runes that is - so three caught remnants read as three runes inherited whether
                    // they carried three or seven. A remnant alone in its chain reported 7 (5+3-1) with
                    // nothing else propagating to it. The same mistake _carriedRune exists to prevent
                    // on the booked path, made again on the banked one. See PlanEnvironment.BankedRunes.
                    // The unpriced runes this remnant first sources were never among the booked ones counted in runes.
                    Inherited: Math.Max(0, runes - (adds.Length - unpricedOwn)) + unpriced +
                               BankedRunesReaching(env, _localGrid[n]),

                    // Plus a local rune the banked set is already sending here, which the booked
                    // check above cannot see. See PlanEnvironment.BankedRunes.
                    Wasted: wasted + spare + IsAlreadyArriving(env, _localRunes[n], _localGrid[n]),
                    FirstSourced: adds,
                    Arriving: arriving.ToArray(),
                    Propagating: _localCarry != null && n < _localCarry.Length ? _localCarry[n] ?? [] : [],
                    Downstream: DownstreamCarriesOf(n, mineAt, locals),
                    Empowered: EmpoweredCarriesOf(n),
                    PerWave: Propagation.RunesPerWave(_localSlots != null && n < _localSlots.Length ? _localSlots[n] : null,
                        arriving));
            }
        }
    }

    /// <summary>
    /// How many banked runes reach this remnant from somewhere OTHER than itself.
    ///
    /// A remnant an explosive already covers banks its own runes forward, and is also drawn - so
    /// counting the whole banked set had it inheriting from itself. Its own are already reported as
    /// sockets; what it inherits is what everything else banked. See PlanEnvironment.BankedRunes.
    /// </summary>
    private static int BankedRunesReaching(PlanEnvironment env, (int X, int Y) cell)
    {
        if (env.BankedRunes is not { Length: > 0 } banked)
            return 0;

        var many = 0;

        foreach (var (_, from) in banked)
        {
            if (IsBankedRuneReaching(env, from, cell))
                many++;
        }

        return many;
    }

    /// <summary>
    /// How many of a remnant's own runes the BANKED set is already sending onto its waves.
    ///
    /// The booked check cannot answer this - banked carries are deliberately not booked, see
    /// PlanEnvironment.BankedRunes - so a duplicate of one went uncounted and the line read one strike
    /// short. For the readout only: the payout has always handled these, and nothing here is priced.
    /// </summary>
    private static int IsAlreadyArriving(PlanEnvironment env, (string Id, float Worth)[] locals,
        (int X, int Y) cell)
    {
        if (env.BankedRunes is not { Length: > 0 } banked || locals is not { Length: > 0 })
            return 0;

        var many = 0;

        foreach (var (id, _) in locals)
        {
            if (id == null)
                continue;

            foreach (var (name, from) in banked)
            {
                // Its own banked runes are its sockets, counted as sockets. Only another remnant's
                // banking wastes a local slot here.
                if (IsBankedRuneReaching(env, from, cell) && string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                {
                    many++;

                    break;
                }
            }
        }

        return many;
    }

    /// <summary>
    /// How many unscoped carries the explosives already down are sending forward.
    ///
    /// For the line under a remnant and nothing else - they are priced as plain rates and this counts
    /// them so the readout can say so. See Settle's banking of env.Secured.
    /// </summary>
    [ThreadStatic] private static int _banked;

    /// <summary>Per link, how much the empowering effects in force there scale everything.</summary>
    [ThreadStatic] private static float[] _lifts;

    /// <summary>Per class: the chain's lift so far, with its lifters applied, and the factors per set of classes. See Settle.</summary>
    [ThreadStatic] private static double[] _liftedOf;

    [ThreadStatic] private static double[] _effective;

    [ThreadStatic] private static double[] _factors;

    /// <summary>The same for one marker, the stronger of the chain's and its own runes' per class. See Settle.</summary>
    [ThreadStatic] private static double[] _markerLifts;

    [ThreadStatic] private static double[] _markerEffective;

    [ThreadStatic] private static double[] _markerFactors;

    /// <summary>One marker's own runes' lift per class. See LocalSharesOfMarker.</summary>
    [ThreadStatic] private static double[] _mineLifts;

    /// <summary>The chain's lift per class as it reaches one remnant's waves, wave 1 cut. See PlanTarget.ShareOfWavesPassedLift.</summary>
    [ThreadStatic] private static double[] _chainLifts;

    /// <summary>
    /// What the empowering effects came to on the last scored chain, for the dump.
    ///
    /// Says "none" on a site where nothing empowers, which is the state that must score exactly as
    /// it did before this existed. See PlanEnvironment.Empowering.
    /// </summary>
    public static string Lifting { get; private set; } = "none - no scored chain yet";

    /// <summary>The first link an empowering rate was in force at, or -1. See Lifting.</summary>
    [ThreadStatic] private static int _liftedFrom;

    /// <summary>
    /// Which link each registered remnant sits at, for comparing bookings by ORDER.
    ///
    /// **Reach cannot order the chain when a blast unearths nothing.** A booking was judged to reach a
    /// remnant when its reach was at least the remnant's, on the reasoning that after[] falls as the
    /// chain goes on - which holds only while every link adds monsters. A chain whose first and third
    /// blasts touch nothing has after[] flat across them, so blast two and blast four carry the
    /// identical 1626.1, and runes sourced at blast four were counted as reaching blast two. Seen on a
    /// six socket remnant at blast two inheriting Moon and Toxic from blasts four and five, neither of
    /// which had been placed.
    ///
    /// The step index is exact whatever a blast unearths, and it is already recorded - see Book's at.
    /// </summary>
    [ThreadStatic] private static int[] _localAt;

    [ThreadStatic] private static int[] _localCount;
    [ThreadStatic] private static (int X, int Y)[] _localGrid;
    [ThreadStatic] private static int[] _localOwn;
    [ThreadStatic] private static float[] _localReach;
    [ThreadStatic] private static (string Id, float Worth)[][] _localRunes;

    /// <summary>
    /// The PROPAGATING runes of the combination each remnant was given, by name.
    ///
    /// **Because _localRunes is only half of what a remnant holds and the dump said "sockets".** A
    /// remnant's sockets are its ordinary runes plus its propagating ones - Sockets adds _localOwn
    /// to _localRunes for exactly that reason - but the line printed only the ordinary ones under
    /// that word. A propagating rune then appeared as a first source of something the same line
    /// said the remnant did not have, which reads as a bug in the propagation and is not one. See
    /// Carrying.
    /// </summary>
    [ThreadStatic] private static string[][] _localCarry;

    /// <summary>The rune in each slot of each local's chosen recipe, for its count per wave. See RuneTally.PerWave.</summary>
    [ThreadStatic] private static string[][] _localSlots;

    /// <summary>
    /// Every rune the remnant could carry, with the weight Runes.Weight gave it. From
    /// Weighing.PropagatingRuneWeights, which holds every rune a propagating slot could take.
    /// </summary>
    [ThreadStatic] private static (string Id, float Weight)[][] _localKnown;

    /// <summary>
    /// The combination's runes after matching against the list above, which is what Book is
    /// offered. Anything in the propagating sockets and missing here was dropped by Named.
    /// </summary>
    [ThreadStatic] private static (string Id, float Weight)[][] _localNamed;

    /// <summary>
    /// What is landing on one remnant's waves: what it holds, what arrived, what was wasted.
    ///
    /// **Filled by the objective as it scores, never recomputed.** Three readouts have now been
    /// found disagreeing with the score they sat beside - the blast circles, the coverage test and
    /// the propagating candidates - every one of them a second copy of a rule that had moved on.
    /// The total reconciles by construction: sockets plus inherited less wasted.
    ///
    /// Keyed on the marker's cell, because the overlay has a Target and the scorer has an index
    /// into a list that is rebuilt every solve.
    /// </summary>
    /// <param name="Arriving">
    /// The runes reaching this remnant from an EARLIER link, by name.
    ///
    /// **Held because the same line has to be answerable for a combination nobody has taken.** The
    /// counts here describe the chosen one - its rune count, and which of its runes were already on
    /// the way - and the combinations window asks the question per row, where every row has a
    /// different rune count and a different overlap with what is arriving. The counts cannot be
    /// re-used there; the names can, because what upstream sends does not depend on which combination
    /// this remnant ends up with.
    ///
    /// Sourced strictly before this link, so it is independent of what this remnant is the first
    /// source of - which does change with the combination. See Options and Propagation.Waves.
    /// </param>
    /// <param name="Sockets">
    /// Runes the remnant holds itself, local and propagating together. Every socket counts,
    /// empowering ones included - leaving those out made a six socket remnant read five.
    /// </param>
    /// <param name="Inherited">
    /// Runes arriving from earlier links of the chain, plus those sent by explosives already on the
    /// ground. Propagation only goes forwards, so nothing later contributes.
    /// </param>
    /// <param name="Wasted">
    /// Runes that land here and add nothing. Two causes, and both are counted:
    ///
    /// The same rune already arrives from an earlier link. Runes do not stack, so a second source
    /// of one is worth nought.
    ///
    /// A propagating socket whose rune is first sourced by nobody - `spare` in the code that builds
    /// this. Before the booking was fixed that meant the rune had been lost between the chosen
    /// recipe and the weight table; now it means the rune is genuinely redundant.
    /// </param>
    /// <param name="FirstSourced">
    /// Of the remnant's own propagating runes, the ones this remnant is the FIRST source of in the
    /// chain. Naming a rune under every remnant carrying it says the opposite of what the scoring
    /// does, because only the first one is credited.
    /// </param>
    /// <param name="Arriving">Every rune reaching this remnant from anywhere else, by name.</param>
    /// <param name="Propagating">The runes this remnant's chosen combination propagates, in socket order.</param>
    /// <param name="Downstream">The runes a later remnant in the chain propagates with the combination it is taking.</param>
    /// <param name="Empowered">
    /// The runes in Propagating the game sends down the chain empowered, because the same combination holds Power. Every
    /// one of them, not only those the scoring lifts. See EmpoweredCarriesOf.
    /// </param>
    /// <param name="PerWave">
    /// How many distinct runes each wave's monsters carry, wave 1 first, from the chosen recipe's slots and Arriving;
    /// null when the slots are not known. The last wave has every slot in force, so it should equal Total. See
    /// Propagation.RunesPerWave.
    /// </param>
    internal readonly record struct RuneTally(int Sockets, int Inherited, int Wasted,
        string[] FirstSourced,
        string[] Arriving = null,
        string[] Propagating = null,
        string[] Downstream = null,
        string[] Empowered = null,
        int[] PerWave = null)
    {
        /// <summary>Distinct runes actually reaching these waves. The number drawn under a remnant.</summary>
        public int Total => Math.Max(0, Sockets + Inherited - Wasted);
    }

    /// <summary>
    /// What a remnant's waves would be if it took a particular combination.
    ///
    /// **The counts under a remnant and the counts in the combinations window are one rule, and were
    /// two.** The window asks about combinations nobody has taken, so it cannot read RuneTallyByRemnant and used to
    /// work them out for itself - an intersection of what a row holds against what is arriving, which
    /// is a plausible reading of the same idea and not the same arithmetic. It disagreed with the
    /// ground on a remnant where a rune sat in both a local and a propagating slot: "10 (5+6-1) Bond"
    /// in the window against "9 (5+6-2)" underneath it, one remnant described two ways on one screen.
    ///
    /// The terms that matter are the ones Locally uses, so they are spelt out here once:
    ///
    /// - sockets counts SLOTS, not distinct runes. Two slots holding the same rune are two slots, and
    ///   deduplicating them is what lost the second strike.
    /// - a local rune the chain already sends is wasted, whether it is booked or banked.
    /// - a propagating slot this remnant is not the first source of is wasted too - it is passing on
    ///   something already passing.
    /// - what it adds is the propagating runes left after that.
    ///
    /// Inherited and Arriving come from the standing figures, because what reaches a remnant does not
    /// depend on which combination it ends up taking - every earlier link has already chosen. That is
    /// what makes this answerable for a row nobody has picked.
    /// </summary>
    /// <summary>
    /// The runes later remnants in the chain propagate with the combinations they are taking, by name: those at a later
    /// link than this one. For the line under a remnant, which marks a rune it sends that a later remnant sends too.
    /// </summary>
    private static string[] DownstreamCarriesOf(int n, int mineAt, int locals)
    {
        var found = new List<string>();

        for (var m = 0; m < locals; m++)
        {
            if (m == n || _localCarry == null || m >= _localCarry.Length || _localAt == null || m >= _localAt.Length ||
                _localAt[m] <= mineAt)
                continue;

            foreach (var id in _localCarry[m] ?? [])
            {
                if (id != null && !found.Contains(id, StringComparer.OrdinalIgnoreCase))
                    found.Add(id);
            }
        }

        return found.ToArray();
    }

    /// <summary>
    /// The runes local n propagates that the game sends empowered: all of them when its combination holds Power in any
    /// socket, less a rune that only lifts others and so has nothing to empower, Power itself among them.
    ///
    /// **Every propagated rune, not the ones the scoring lifts.** Measured across the recordings (2026-10-01): a remnant
    /// holding Power sent Death, Soul, Vision, Celestial, Adaptive, Moon, Toxic and Stone down the chain empowered. The
    /// scoring lifts only the runes whose table row carries the tag Power lifts - HeldFactorOfEffect - because doubling
    /// the others pays nothing, so this line can mark a rune the plan scores the same either way. Only Power was
    /// measured; another rune holding a lift is not taken to do the same. See RuneTally.Empowered.
    /// </summary>
    private static string[] EmpoweredCarriesOf(int n) =>
        EmpoweredCarriesOfCombination(_localCarry != null && n < _localCarry.Length ? _localCarry[n] : null,
            _localRunes != null && n < _localRunes.Length ? _localRunes[n] : null);

    /// <summary>
    /// The runes a combination propagates that the game sends empowered, from what it propagates and what its ordinary
    /// sockets hold. The rule for the line under a remnant and for each row of the Runeshape Combinations window. See
    /// EmpoweredCarriesOf.
    /// </summary>
    internal static string[] EmpoweredCarriesOfCombination(string[] carried, (string Id, float Worth)[] locals)
    {
        if (carried is not { Length: > 0 })
            return [];

        var holdsPower = carried.Any(IsPower) || (locals ?? []).Any(x => IsPower(Weighing.RuneOfKey(x.Id)));

        if (!holdsPower)
            return [];

        return carried.Where(id => id != null && (Weighing.LiftsOfRune(id) == null || Weighing.HasShareEffect(id)))
            .ToArray();

        static bool IsPower(string id) => string.Equals(id, "power", StringComparison.OrdinalIgnoreCase);
    }

    internal static RuneTally RuneTallyOfOption(RuneTally remnant,
        (string Id, float Worth)[] optionLocals, string[] optionPropagates, string[] optionSlots = null)
    {
        var arriving = remnant.Arriving ?? [];
        var wasted = 0;
        var adds = new List<string>();

        wasted += RunesInLocalsArriving(optionLocals, id => arriving.Contains(id, StringComparer.OrdinalIgnoreCase));

        foreach (var id in optionPropagates ?? [])
        {
            if (id == null)
                continue;

            if (arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                wasted++;
            else
                adds.Add(id);
        }

        // Propagating names every rune the row would send, upstream duplicates too, so the window can colour them as the
        // line under the remnant does. Downstream is the remnant's standing figure, like Arriving: what later remnants
        // send is set by their own choices, not by which row this one takes.
        return new RuneTally(RunesInLocals(optionLocals) + (optionPropagates?.Length ?? 0), remnant.Inherited,
            wasted, adds.ToArray(), arriving,
            Propagating: (optionPropagates ?? []).Where(id => id != null).ToArray(),
            Downstream: remnant.Downstream,
            Empowered: EmpoweredCarriesOfCombination(optionPropagates, optionLocals),
            PerWave: Propagation.RunesPerWave(optionSlots, arriving));
    }

    /// <summary>
    /// How many runes a combination's local entries are. A rune can have several: its share, its lift key and any
    /// split-off share, and is one rune. Counted by Weighing.RuneOfKey, each rune at its first entry. See
    /// Weighing.LiftKeyOf and Weighing.SplitShareKeyOf.
    /// </summary>
    internal static int RunesInLocals((string Id, float Worth)[] locals) =>
        RunesInLocalsArriving(locals, _ => true);

    /// <summary>
    /// How many of the runes in a combination's local slots already arrive, counted as RunesInLocals counts them: each
    /// rune once, however many entries it has. On a Grazed Prairie site (2026-10-06) a remnant holding Power, filed as its
    /// lift key, with Power arriving from upstream read "16 (7+10-1)" against 15 runes on its last wave, from counting
    /// one rune per entry.
    /// </summary>
    internal static int RunesInLocalsArriving((string Id, float Worth)[] locals, Func<string, bool> arrives)
    {
        if (locals is not { Length: > 0 })
            return 0;

        var count = 0;

        for (var i = 0; i < locals.Length; i++)
        {
            var rune = Weighing.RuneOfKey(locals[i].Id);

            if (rune == null)
                continue;

            var earlier = false;

            for (var j = 0; j < i && !earlier; j++)
                earlier = string.Equals(Weighing.RuneOfKey(locals[j].Id), rune, StringComparison.OrdinalIgnoreCase);

            if (!earlier && arrives(rune))
                count++;
        }

        return count;
    }

    /// <summary>A grid position as the cell everything else keys on.</summary>
    private static (int X, int Y) CellKeyOf(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));

    /// <summary>
    /// What each link of the last scored chain was credited with in propagation.
    ///
    /// Content is a property of the markers a blast catches and can be re-derived from the
    /// environment; this cannot, because it is the product of every rune reaching that link and the
    /// order they arrived in. Published rather than recomputed for the reason the rest of this pass
    /// is: a readout that re-derives a rule is a readout that will disagree with it.
    /// </summary>
    public static double[] Credits { get; private set; } = [];

    /// <summary>What each remnant's waves are wearing, from the last detailed pass.</summary>
    public static readonly Dictionary<(int X, int Y), RuneTally> RuneTallyByRemnant = new();

    /// <summary>
    /// How many monsters of each tier the model expects a remnant's waves to bring: with Gaining Traction and the chosen
    /// combination's own count effects (Bond's x0.33 on rares, say), and with traction but without them. Expected
    /// counts, not worth.
    /// </summary>
    /// <param name="EachWave">
    /// The same with its own effects, wave by wave from wave 1, each as normal/magic/rare and separated by spaces, from
    /// Weighing.PacksOfEachWave. Empty when the wave count is not known.
    /// </param>
    internal readonly record struct WavesPredicted(float Normal, float Magic, float Rare, float NormalWithoutOwn,
        float MagicWithoutOwn, float RareWithoutOwn, string EachWave);

    /// <summary>
    /// Each remnant's WavesPredicted from the last detailed pass, by cell, written beside RuneTallyByRemnant. The census
    /// records it with the counts it saw, so the model's estimates can be compared with what came over many runs. See
    /// Spawns.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), WavesPredicted> WavesPredictedByRemnant = new();

    /// <summary>
    /// What the scoring holds a remnant's waves to bring, as monsters by tier, for the census to audit against what came.
    /// Each term is one the payout pays, counted rather than priced:
    ///
    /// - its wave parts, each part's monsters times its tier's Gaining Traction scale and the count factor combination
    ///   c's own effects put on it under the lift paid (OwnFactorsOfPart);
    /// - the magic and rare waves combination c has over the remnant's recipe (MagicAndRareWavesOfChoices), as monsters
    ///   at the parts' worth apiece, on the same scales and factors, as TractionOfChoice and OwnOfChoice pay them;
    /// - the monsters its "per" effects add, in the tier of the row they add (CreatedCountOfEntry, which CreatedOfEntry
    ///   prices).
    ///
    /// Without its own effects is the first two with no own factor and nothing added. Wave by wave, the first two
    /// follow Weighing.PacksOfEachWave's share of each tier and the added monsters the waves from their slot's on - the
    /// scoring itself is not wave by wave, so that split is the wave model's shape and not a figure the solver pays.
    /// </summary>
    private static WavesPredicted PredictedWavesOf(PlanTarget target, int c, float normalScale, float magicScale,
        float rareScale, double lift)
    {
        float normal = 0f, magic = 0f, rare = 0f, normalWithout = 0f, magicWithout = 0f, rareWithout = 0f;
        float rareWorth = 0f, rareMany = 0f, magicWorth = 0f, magicMany = 0f;
        long rareMask = 0L, magicMask = 0L;

        foreach (var (mask, worth, many) in target.Parts ?? [])
        {
            var isRare = (mask & (1L << Tags.Rares)) != 0L;
            var isMagic = !isRare && (mask & (1L << Tags.Magics)) != 0L;

            // The worth apiece the choice's extra waves are counted at, over every part of the tier, as
            // CreatedCountOfEntry counts them.
            if (isRare)
            {
                rareWorth += worth;
                rareMany += many;
                rareMask = rareMask == 0L ? mask : rareMask;
            }
            else if (isMagic)
            {
                magicWorth += worth;
                magicMany += many;
                magicMask = magicMask == 0L ? mask : magicMask;
            }

            if (many <= 0f || (mask & (1L << Tags.Monsters)) == 0L)
                continue;

            var own = target.OwnFactorsOfPart(c, mask, lift).Many;

            if (isRare)
            {
                rareWithout += many * rareScale;
                rare += many * rareScale * own;
            }
            else if (isMagic)
            {
                magicWithout += many * magicScale;
                magic += many * magicScale * own;
            }
            else
            {
                // As the scorer pays a normal part: Gaining Traction on it (Settle), and its own effects' change on it
                // unscaled (OwnOfChoice scales rare and magic parts only), so not the product of the two.
                normalWithout += many * normalScale;
                normal += many * normalScale + many * (own - 1f);
            }
        }

        if (target.MagicAndRareWavesOfChoices is { } changes && c >= 0 && c < changes.Length)
        {
            if (rareMask != 0L && rareWorth > 0f && rareMany > 0f)
            {
                var extra = changes[c].Rare / (rareWorth / rareMany) * rareScale;

                rareWithout += extra;
                rare += extra * target.OwnFactorsOfPart(c, rareMask, lift).Many;
            }

            if (magicMask != 0L && magicWorth > 0f && magicMany > 0f)
            {
                var extra = changes[c].Magic / (magicWorth / magicMany) * magicScale;

                magicWithout += extra;
                magic += extra * target.OwnFactorsOfPart(c, magicMask, lift).Many;
            }
        }

        // The wave model's share of each tier, wave by wave, for the split below.
        var waves = Weighing.PartsOfEachWave(target.WaveCount);
        var shape = new (float Normal, float Magic, float Rare)[waves.Length];
        var (allNormal, allMagic, allRare) = (0f, 0f, 0f);

        for (var w = 0; w < waves.Length; w++)
        {
            foreach (var (mask, _, many) in waves[w])
            {
                if (many <= 0f || (mask & (1L << Tags.Monsters)) == 0L)
                    continue;

                if ((mask & (1L << Tags.Rares)) != 0L)
                    shape[w].Rare += many;
                else if ((mask & (1L << Tags.Magics)) != 0L)
                    shape[w].Magic += many;
                else
                    shape[w].Normal += many;
            }

            allNormal += shape[w].Normal;
            allMagic += shape[w].Magic;
            allRare += shape[w].Rare;
        }

        var perWave = new (float Normal, float Magic, float Rare)[waves.Length];

        for (var w = 0; w < waves.Length; w++)
        {
            perWave[w] = (allNormal > 0f ? normal * shape[w].Normal / allNormal : 0f,
                allMagic > 0f ? magic * shape[w].Magic / allMagic : 0f,
                allRare > 0f ? rare * shape[w].Rare / allRare : 0f);
        }

        // The monsters its "per" effects add, to the totals and to the waves from their slot's on: the last waves holding
        // the entry's share of the monsters it counts.
        if (target.CreatedOfChoices is { } createdOfChoices && c >= 0 && c < createdOfChoices.Length &&
            createdOfChoices[c] is { } entries)
        {
            foreach (var entry in entries)
            {
                var count = target.CreatedCountOfEntry(c, entry, magicScale, rareScale, lift);

                if (count <= 0f)
                    continue;

                var tier = (entry.Mask & (1L << Tags.Rares)) != 0L ? 2 : (entry.Mask & (1L << Tags.Magics)) != 0L ? 1 : 0;

                if (tier == 2)
                    rare += count;
                else if (tier == 1)
                    magic += count;
                else
                    normal += count;

                float Counted(int w) => entry.Source == Tags.Rares ? shape[w].Rare
                    : entry.Source == Tags.Magics ? shape[w].Magic
                    : entry.Source == Tags.Normals ? shape[w].Normal
                    : shape[w].Normal + shape[w].Magic + shape[w].Rare;

                var whole = 0f;

                for (var w = 0; w < waves.Length; w++)
                    whole += Counted(w);

                if (whole <= 0f)
                    continue;

                var first = waves.Length - 1;
                var reached = Counted(first);

                while (first > 0 && reached / whole < Math.Min(1f, entry.WaveShare) - 0.001f)
                    reached += Counted(--first);

                for (var w = first; w < waves.Length; w++)
                {
                    var added = reached > 0f ? count * Counted(w) / reached : 0f;

                    perWave[w] = tier == 2 ? (perWave[w].Normal, perWave[w].Magic, perWave[w].Rare + added)
                        : tier == 1 ? (perWave[w].Normal, perWave[w].Magic + added, perWave[w].Rare)
                        : (perWave[w].Normal + added, perWave[w].Magic, perWave[w].Rare);
                }
            }
        }

        var eachWave = new System.Text.StringBuilder();

        foreach (var (waveNormal, waveMagic, waveRare) in perWave)
        {
            eachWave.Append(eachWave.Length > 0 ? " " : "")
                .Append(System.FormattableString.Invariant($"{waveNormal:0.#}/{waveMagic:0.#}/{waveRare:0.#}"));
        }

        return new WavesPredicted(normal, magic, rare, normalWithout, magicWithout, rareWithout, eachWave.ToString());
    }

    /// <summary>
    /// Whether RuneTallyByRemnant describes a chain that has since changed under it.
    ///
    /// **A roll changes what a remnant passes on, and the table cannot know that until a pass rewrites
    /// it.** The figures are published by the detailed pass and read by the overlay - deliberately, so
    /// that the ground and the objective are one answer and cannot drift. The cost is that between a
    /// roll and the next pass the line under a remnant states the OLD runes with full confidence, which
    /// is the reading that prompted this.
    ///
    /// **Set rather than cleared, because the table is load-bearing for more than the line.** Overlay
    /// tests ContainsKey to decide whether a remnant is drawn at all when rewards are switched off, so
    /// emptying it would make whole remnant blocks disappear rather than one line. The table stays and
    /// this says not to trust it.
    ///
    /// Propagation is chain-wide, so a roll invalidates every entry and not only the rolled remnant's:
    /// what that one passes on is what the ones after it receive.
    /// </summary>
    public static bool RuneTallyOutOfDate { get; set; }

    /// <summary>
    /// Which combination the objective took at each remnant, from the last detailed pass.
    ///
    /// **The decision the whole plan rests on, and it was never written down.** The search chooses a
    /// combination per remnant while scoring - reward and runes are one choice, so it must - and then
    /// discarded it, keeping only the score it produced. Everything downstream that needed to know
    /// re-derived it: the dump inferred it from the rune counts, and the combinations window computed
    /// its own answer from its own estimates, which quietly disagreed whenever an explosive was on the
    /// ground. Two guesses at a fact the solver already had.
    ///
    /// Written only on a detailed pass, so it describes the chain on screen rather than one of the
    /// millions the search tried, and cleared with RuneTallyByRemnant for the same reason.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), Picked> Chosen = new();

    /// <summary>
    /// Per remnant a Power reaches from earlier links, on the last detailed pass: the lift on its own waves (wave 1
    /// cut) and what its combination's own effects then change in those waves, as the payout scored them. For the
    /// dump, beside the unlifted figure. See Settle's ownLift.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), (double Lift, float Own)> OwnEffectsUnderPassedPower = new();

    /// <summary>
    /// How each remnant's combinations ranked, for the cell they stand on. See ChoiceRankingTrail.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), List<string>> Rankings = new();

    /// <param name="Reward">What the combination yields, as the window names it.</param>
    /// <param name="Sockets">How many of the remnant's sockets it uses.</param>
    /// <param name="Carries">The summed weight it puts into propagating slots, before duplicates.</param>
    /// <param name="Kept">
    /// The same, after striking every rune the chain already sends here - which is what the payout
    /// will actually pay. Both are printed: where they differ is where a combination was being
    /// offered credit for a rune it is not the source of, and the difference is the whole of the
    /// fault this pair exists to make visible. See DiscountedForDuplicates.
    /// </param>
    /// <param name="Local">The summed weight of the runes that stay put, before duplicates.</param>
    /// <param name="Held">The same, after striking the ones already arriving.</param>
    /// <param name="Carrying">Which runes it propagates, by id.</param>
    /// <param name="Worth">
    /// What the combination's reward is worth as weight, which Settle adds to the chain's content.
    ///
    /// **Published because a blast's content could not otherwise be reconciled.** The dump totals
    /// what each blast catches from the markers' own weights, and the scorer adds this on top - so
    /// the per-blast figures came to 1,048.5 against a plan whose content was 2,911.6, with the
    /// difference being four remnants' rewards and nothing saying so.
    /// </param>
    /// <param name="Own">What the combination's own effects add to the remnant's own waves, with Gaining Traction, in points. See PlanTarget.OwnOfChoice.</param>
    /// <param name="Downstream">The monster pool a rune it carries pays on from this link, in points. See PlanTarget.Best.</param>
    internal readonly record struct Picked(string Reward, int Sockets, float Carries, float Kept,
        float Local, float Held, string[] Carrying, string Recipe = "", float Worth = 0f, float Own = 0f,
        float Downstream = 0f);

    /// <summary>
    /// Why each remnant's inherited count came out as it did, for the dump. See Locally.
    ///
    /// Cleared with RuneTallyByRemnant and written only on a detailed pass, so it describes the same chain the
    /// lines under the remnants do.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), string> PropagationTrailByRemnant = new();

    /// <summary>
    /// What each remnant's OWN empowering runes scale its waves by, for the dump. See LocalSharesOfMarker's lift.
    ///
    /// Written only on a detailed pass and cleared with RuneTallyByRemnant, so it describes the chain on screen. A
    /// remnant absent from here has no empowering rune in its own sockets, which is most of them.
    /// </summary>
    /// <summary>
    /// Per remnant, what its own empowering runes are worth and what the chain is propagating in.
    ///
    /// Both, because either alone misattributes a tie. See the recording site.
    /// </summary>
    public static readonly Dictionary<(int X, int Y), (double Mine, double Chain)> _lifting = new();

    /// <summary>
    /// The runes this marker is the first on the chain to propagate.
    ///
    /// **Only the first source, because only the first source is worth anything.** Book credits a
    /// rune once, against the earliest link that offers it, and every later remnant carrying the
    /// same one contributes nothing - runes do not stack. Naming it under all of them told the
    /// player the opposite of what the objective had just decided.
    /// </summary>
    /// <summary>
    /// Every booking the last detailed pass paid out, one line each. See BookingTrail.
    ///
    /// An instrument rather than a readout for ordinary use: it exists to answer whether one
    /// modifier can occupy two entries of the booked set and be paid for both.
    /// </summary>
    public static string Bookings { get; private set; } = "no detailed pass yet";

    /// <summary>
    /// The booked set spelled out: what each entry is, which key it sits under, and what it pays.
    ///
    /// Sorted by name so the duplicate this exists to find lands next to its twin. The key is
    /// printed as Book files it - plain for an unscoped booking, and scoped as the tag it is built
    /// from - because two entries for one name under ONE key is impossible by construction, and
    /// two under different keys is exactly the fault.
    ///
    /// Reach of the largest float is how a banked booking says it pays forward over the whole
    /// remaining chain; it is printed as "all" rather than as 3.4e38.
    /// </summary>
    private static string BookingTrail(int distinct)
    {
        if (distinct <= 0)
            return "nothing booked on this chain";

        var lines = new List<string>(distinct);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var r = 0; r < distinct; r++)
        {
            var name = _carriedNames[r] ?? "(unnamed)";

            names[name] = names.TryGetValue(name, out var seen) ? seen + 1 : 1;

            lines.Add($"    {name}  {(_carriedIds[r] < 0 ? $"scoped key {_carriedIds[r]} tag " +
                          $"{_carriedTag[r]}" : $"plain key {_carriedIds[r]}")}  " +
                      $"group {_carriedGroup[r]}  weight {_carriedWeights[r]:0.###}  " +
                      $"from link {(_carriedAt[r] + 1)}  " +
                      $"in force from {(_carriedFrom[r] == int.MaxValue ? "never" : $"link {_carriedFrom[r] + 1}")}  " +
                      $"reach {(_carriedAfter[r] >= float.MaxValue ? "all" : _carriedAfter[r].ToString("0.#"))}  " +
                      $"owner ({_carriedBy[r].X},{_carriedBy[r].Y})  " +
                      $"rune {_carriedRune[r]}");
        }

        lines.Sort(StringComparer.Ordinal);

        var twice = new List<string>();

        foreach (var (name, count) in names)
        {
            if (count > 1)
                twice.Add($"{name} x{count}");
        }

        twice.Sort(StringComparer.Ordinal);

        return $"{distinct} entries, each paid one share" +
               (twice.Count > 0
                   ? $" - BOOKED MORE THAN ONCE: {string.Join(", ", twice)}"
                   : " - no name appears twice") +
               "\n" + string.Join("\n", lines);
    }

    private static string[] RunesFirstSourcedBy((int X, int Y) cell, int distinct)
    {
        List<string> found = null;

        for (var r = 0; r < distinct; r++)
        {
            // A split-off share names no rune of its own; its rune's booking names it. See Weighing.SplitShareKeyOf.
            if (_carriedBy[r] != cell || _carriedNames[r] == null || _carriedWeights[r] <= 0f ||
                Weighing.IsSplitShareKey(_carriedNames[r]))
                continue;

            found ??= new List<string>(2);

            if (!found.Contains(_carriedNames[r], StringComparer.OrdinalIgnoreCase))
                found.Add(_carriedNames[r]);
        }

        return found?.ToArray() ?? System.Array.Empty<string>();
    }

    /// <summary>
    /// The propagation term that bonus was part of, so the dump can print it as a share.
    ///
    /// The absolute figure says nothing about whether it is reasonable - it is a guess multiplied by
    /// however many runes a chain happens to carry. The fraction is what shows it taking over.
    /// </summary>
    public static double Propagated { get; private set; }

    /// <summary>
    /// Everything a detailed pass publishes about the chain it scored.
    ///
    /// **One record because it is one thing**: the figures belonging to the route on screen. A pass
    /// about something ELSE overwrites all of them, so a caller that wants the route's back has to
    /// put back all of them - and the three times this went wrong, it was because somebody put back
    /// the one the symptom pointed at. See Planning's Spot, the only such caller, and Restore.
    ///
    /// Add a field here in the same change that publishes one from a detailed pass. Restore is what
    /// the check would be if there were one: it names every member, so a member missing from this
    /// record is a member missing from the restore.
    /// </summary>
    public readonly record struct DetailedPass(
        double Propagated,
        double Pooled,
        string Lifting,
        string Bookings,
        double[] Credits,
        string[] Factors,
        (double Given, int Distinct) Repeats,
        Dictionary<(int X, int Y), RuneTally> RuneTallyByRemnant,
        Dictionary<(int X, int Y), Picked> Chosen,
        Dictionary<(int X, int Y), List<string>> Rankings);

    /// <summary>
    /// What a detailed pass has published, copied, so it survives another pass running over it.
    ///
    /// The three tables are copied rather than referenced: a pass CLEARS them before filling them,
    /// so a reference would be emptied out from under the holder.
    /// </summary>
    public static DetailedPass Published =>
        new(Propagated, Pooled, Lifting, Bookings, Credits, Factors, Repeats,
            new Dictionary<(int X, int Y), RuneTally>(RuneTallyByRemnant),
            new Dictionary<(int X, int Y), Picked>(Chosen),
            new Dictionary<(int X, int Y), List<string>>(Rankings));

    /// <summary>Puts back everything Published handed out. See DetailedPass.</summary>
    public static void Restore(DetailedPass was)
    {
        Propagated = was.Propagated;
        Pooled = was.Pooled;
        Lifting = was.Lifting;
        Bookings = was.Bookings;
        Credits = was.Credits ?? [];
        Factors = was.Factors ?? [];
        Repeats = was.Repeats;

        Refill(RuneTallyByRemnant, was.RuneTallyByRemnant);
        Refill(Chosen, was.Chosen);
        Refill(Rankings, was.Rankings);
    }

    /// <summary>Empties one of the published tables and fills it again from a copy.</summary>
    private static void Refill<TValue>(Dictionary<(int X, int Y), TValue> into,
        Dictionary<(int X, int Y), TValue> from)
    {
        into.Clear();

        foreach (var (cell, value) in from ?? [])
            into[cell] = value;
    }

    /// <summary>The monster part of each booked rune's reach. See Booked.</summary>
    [ThreadStatic] private static float[] _carriedRich;

    /// <summary>Which link booked each one, for attributing the propagation to a blast.</summary>
    [ThreadStatic] private static int[] _carriedAt;

    /// <summary>Which marker's booking won it, so only the first source names a rune.</summary>
    [ThreadStatic] private static (int X, int Y)[] _carriedBy;

    [ThreadStatic] private static string[] _carriedNames;

    /// <summary>Which tag and which group each booking was made under. See Settle's payout.</summary>
    [ThreadStatic] private static int[] _carriedTag;

    [ThreadStatic] private static int[] _carriedGroup;

    /// <summary>Each booking's effect number, and the lift its source's held amplifiers put on its lift. See Book.</summary>
    [ThreadStatic] private static int[] _carriedEffect;

    [ThreadStatic] private static float[] _carriedLiftScale;

    /// <summary>The effect numbers whose lift the drain has already added this pass, one each. See Settle's drain.</summary>
    [ThreadStatic] private static int[] _liftedEffects;

    /// <summary>
    /// Shares that were rated straight into the product instead of being booked, with what each
    /// one is. Detailed passes only. See Rate and RecordFactorsAtLink.
    /// </summary>
    [ThreadStatic]
    private static List<(int Band, int Tag, int At, float Percent, string What)> _ratedAside;

    /// <summary>The ordinary runes of each covered marker, kept until its waves are paid.</summary>
    [ThreadStatic] private static (string Id, float Worth)[][] _localsOf;

    /// <summary>The combination each touched marker took, by its position in touched; minus one for none. See Settle.</summary>
    [ThreadStatic] private static int[] _chosenOf;
    [ThreadStatic] private static bool[] _carriedRune;

    /// <summary>
    /// What each link of the chain was credited with, in propagation. Detailed passes only.
    ///
    /// **The circles on the ground used to be a second calculation and they had drifted.** The
    /// number in a blast circle was worked out in Lines from the chosen combination's flat carry -
    /// no non-stacking, no scoped runes, no concentration, and a local term multiplied by everything
    /// the blast unearthed rather than by the remnant's own waves. Every one of those was fixed in
    /// the objective and none of them in the readout, so the figures on the ground bore no relation
    /// to the score in the corner.
    ///
    /// Filled by the scoring loop as it credits things, rather than recomputed afterwards, which is
    /// the only arrangement in which the two cannot drift again.
    /// </summary>
    [ThreadStatic] private static double[] _credited;

    /// <summary>
    /// Content per link, booked by Settle at the link that first caught what it belongs to: weight, Gaining Traction,
    /// own effects, reward, less a repeated switch. Filled on a detailed pass or while _recordingLinks is set.
    /// </summary>
    [ThreadStatic] private static double[] _contentAtLink;

    /// <summary>Whether Settle keeps _credited, _contentAtLink and _chosenByTarget on a pass that is not detailed.</summary>
    [ThreadStatic] private static bool _recordingLinks;

    /// <summary>How many links the last recorded pass scored, the head of laid explosives included.</summary>
    [ThreadStatic] private static int _linksRecorded;

    /// <summary>The combination each target took on the last recorded pass, by target index, or -1.</summary>
    [ThreadStatic] private static int[] _chosenByTarget;

    /// <summary>Combinations Settle takes instead of choosing, by target index, -1 for none. See ScoreOfEachBlast.</summary>
    [ThreadStatic] private static int[] _pinnedChoices;

    /// <summary>Whether only objects caught up to _sourcesThrough send anything. See SendsFrom.</summary>
    [ThreadStatic] private static bool _sourcesLimited;

    [ThreadStatic] private static int _sourcesThrough;

    /// <summary>Whether what is caught at this link sends anything on the current pass. See ScoreOfEachBlast.</summary>
    private static bool SendsFrom(int link) => !_sourcesLimited || link <= _sourcesThrough;

    /// <summary>
    /// What each blast adds, as the blast labels draw it. Content and Propagation are what is paid at the blast;
    /// ToLaterBlasts is what the objects it catches add at the blasts after it. The first two add up across the chain to
    /// ChainContent and ChainPropagation, the chain's own figures; PinnedContent and PinnedPropagation are the chain
    /// scored with every combination pinned, which must match them. See ScoreOfEachBlast.
    /// </summary>
    internal sealed record BlastScores(List<(double Content, double Propagation, double ToLaterBlasts)> Each,
        double ChainContent, double ChainPropagation, double PinnedContent, double PinnedPropagation);

    /// <summary>
    /// What each blast in the chain adds to the score when it goes off, and what the objects it catches add to the
    /// blasts after it.
    ///
    /// What a blast adds is what Settle books at its link: the content of everything it is first to catch - weight,
    /// Gaining Traction from the remnants before, own effects, reward - and the propagation paid on what it unearths,
    /// from its own runes and every earlier blast's. With the combinations fixed, nothing after a link changes what is
    /// booked at it, so these add up to the chain's score and the sum over the first k blasts is the score of the chain
    /// stopped there.
    ///
    /// What it adds to later blasts is what its objects send: their runes and relic effects, the lifts they hold, and
    /// the Traction their completion gives the remnants after. The chain is scored once per link with only the objects
    /// caught up to that link sending anything; what the blasts after a link gain when its objects start sending is
    /// its figure. These overlap with the later blasts' own figures, being part of them, so they do not add up to
    /// anything. Where two senders compound - a Power at link 9 lifting a rune first sent at link 12 - the
    /// compounding goes to the later sender. Every combination is pinned to the one the full chain takes, so the passes
    /// differ in their senders alone.
    /// </summary>
    internal static BlastScores ScoreOfEachBlast(PlanEnvironment env, List<Vector2> chain)
    {
        if (env == null || chain is not { Count: > 0 })
            return null;

        try
        {
            _recordingLinks = true;
            _pinnedChoices = null;
            _sourcesLimited = false;

            var full = Evaluate(env, chain);

            if (double.IsNegativeInfinity(full.Content))
                return null;

            var links = _linksRecorded;
            var contentAt = new double[links];
            var propagationAt = new double[links];

            Array.Copy(_contentAtLink, contentAt, links);
            Array.Copy(_credited, propagationAt, links);

            _pinnedChoices = (int[])_chosenByTarget.Clone();
            _sourcesLimited = true;

            // What is booked at each link with only the objects caught up to link k sending, k from -1.
            var bookedWithSenders = new double[links + 1][];
            var pinned = (Content: 0d, Propagation: 0d);

            for (var k = -1; k < links; k++)
            {
                _sourcesThrough = k;

                var scored = Evaluate(env, chain);
                var booked = new double[links];

                for (var j = 0; j < links; j++)
                    booked[j] = _contentAtLink[j] + _credited[j];

                bookedWithSenders[k + 1] = booked;
                pinned = (scored.Content, scored.Propagation);
            }

            var each = new List<(double Content, double Propagation, double ToLaterBlasts)>(links);

            for (var s = 0; s < links; s++)
            {
                var toLater = 0d;

                for (var j = s + 1; j < links; j++)
                    toLater += bookedWithSenders[s + 1][j] - bookedWithSenders[s][j];

                each.Add((contentAt[s], propagationAt[s], toLater));
            }

            return new BlastScores(each, full.Content, full.Propagation, pinned.Content, pinned.Propagation);
        }
        finally
        {
            _recordingLinks = false;
            _pinnedChoices = null;
            _sourcesLimited = false;
        }
    }

    [ThreadStatic] private static int[] _carriedIds;

    /// <summary>Switches already paid for on this chain. See the Credit local in Evaluate.</summary>
    [ThreadStatic] private static int[] _switchIds;

    /// <summary>
    /// What the last judged chain gave back for collecting a switch twice, and how many distinct
    /// switches it holds.
    ///
    /// Here to be checkable. "Runic monsters are duplicated pays once" is a claim about an objective
    /// nobody can see, and it was wrong in two separate places while reading as correct - so the
    /// number it takes back off is worth printing rather than trusting. See Credit.
    /// </summary>
    public static (double Given, int Distinct) Repeats { get; private set; }
    [ThreadStatic] private static float[] _carriedWeights;
    [ThreadStatic] private static float[] _carriedAfter;

    /// <summary>
    /// The earliest link each booked rune reaches MONSTERS from, or int.MaxValue for never.
    ///
    /// Separate from _carriedAt, which follows whichever source reaches furthest. See Book.
    /// </summary>
    [ThreadStatic] private static int[] _carriedFrom;
    [ThreadStatic] private static float[][] _tagged;
    [ThreadStatic] private static float[][] _taggedAfter;
    [ThreadStatic] private static float[] _monsters;
    [ThreadStatic] private static float[] _after;

    /// <summary>
    /// A scratch array at least this long, KEEPING what is already in it.
    ///
    /// **It used to hand back a fresh one and throw the contents away.** Every caller is an
    /// accumulator - the booked runes, the switches already paid for, the remnants with ordinary
    /// runes - and each grows by one just before writing the next entry. So the seventeenth rune on
    /// a chain allocated a new array and silently erased the sixteen before it, and the chain that
    /// triggered it scored its propagation at almost nothing. Growing by one at a time meant every
    /// rune after that did it again.
    ///
    /// Invisible on an ordinary site, where the distinct runes dedupe to about ten. A Grand chain is
    /// fifteen links with up to two propagating slots each and reaches it easily.
    ///
    /// Doubled rather than fitted, so a chain that needs thirty does not reallocate fourteen times.
    /// </summary>
    /// <summary>Settle's Gaining Traction scratch: each touched target's factor, each link's extra monsters, and how
    /// many remnants each link first catches. See Settle.</summary>
    [ThreadStatic] private static float[] _tractionOf;

    [ThreadStatic] private static float[] _monstersExtra;

    [ThreadStatic] private static float[] _rareTractionOf;

    /// <summary>Settle's Gaining Traction scratch: each touched remnant's factor on its normal waves. See Settle.</summary>
    [ThreadStatic] private static float[] _normalTractionOf;

    /// <summary>
    /// What Gaining Traction multiplies this remnant's normal, magic and rare waves by after so many remnants: its own
    /// table, worked out from its wave count when the environment was built, or the environment's rule with normals
    /// untouched where it has none - a layout saved before the pack model. See Weighing.TractionByBeforeOfTarget.
    /// </summary>
    internal static (float Normal, float Magic, float Rare) TractionScalesOfTarget(PlanEnvironment env, PlanTarget target, int before)
    {
        if (before <= 0)
            return (1f, 1f, 1f);

        if (target?.TractionByBefore is { Length: > 0 } byBefore)
            return byBefore[Math.Min(before, byBefore.Length - 1)];

        var (magic, rare) = TractionScales(env, before);

        return (1f, magic, rare);
    }

    /// <summary>
    /// What Gaining Traction multiplies a remnant's magic and rare waves by after the given number of remnants: magic
    /// by its packs, rare by its packs and by the modifiers its modifier chance buys. The rates are the table's Gaining
    /// Traction row.
    ///
    /// **Added to the map's increase, not multiplied on it.** The stat is an increased number of magic and rare packs
    /// (MapExpedition2RemnantNumberOfMagicAndRarePacksPct...PerRemnantCompleted, 50), and increases add: a pack count
    /// already at +217% from the map and atlas goes to +267% after one remnant, x1.16, where multiplying made it x1.5.
    /// Fitted on 406 wave rares in 34 runs (2026-10-05), multiplying at +25% predicted 575 and adding at +50% 406; the
    /// patch notes give +50% (0.5.4 Hotfix 3). So the count scales by (1 + increase + rate x before) / (1 + increase),
    /// the wave row having been scaled by the map's (1 + increase) already. See NOTES, "Gaining Traction".
    ///
    /// **The modifiers, from the chance, by a rule read off the game's text.** The stat raises rare monster modifier
    /// chance by the same percent as the rare packs. "Monster Modifier Chance increases the potential number of Rare
    /// Monster Modifiers with each modifier above 4 requiring twice as much Modifier Chance" - so a fifth at 100%, a
    /// sixth at 300%, a seventh at 700% - which is log2(1 + chance) extra modifiers at those points, and between them
    /// the same curve, standing in for the chance of reaching the next. Each extra modifier adds the table's rare
    /// modifier row's share of a rare's worth (5 of 20 as shipped). The interpolation is this plugin's reading; the page
    /// gives no formula. Recorded rares with six or more remnants before carried about 1.4 more modifiers than the first
    /// remnant's (2026-09-30, one bucket of 40).
    /// </summary>
    internal static (float Magic, float Rare) TractionScales(PlanEnvironment env, int before)
    {
        if (before <= 0 || (env.MagicPacksPerRemnantCompleted <= 0f && env.RarePacksPerRemnantCompleted <= 0f))
            return (1f, 1f);

        var raised = env.RarePacksPerRemnantCompleted * before;
        var extraModifiers = MathF.Log2(1f + raised);
        var rareCount = (1f + env.RareIncreaseOfMap + raised) / (1f + env.RareIncreaseOfMap);
        var magicCount = (1f + env.MagicIncreaseOfMap + env.MagicPacksPerRemnantCompleted * before) /
                         (1f + env.MagicIncreaseOfMap);

        return (magicCount, rareCount * (1f + env.RareModifierShareOfRare * extraModifiers));
    }

    [ThreadStatic] private static int[] _remnantsCaughtAt;

    /// <summary>
    /// The indices of the remnants among an environment's targets, per thread, worked out again when the target list
    /// changes. Keyed on the list rather than the environment, since a copy made with other targets keeps the
    /// original's initialisers. See Settle's Gaining Traction.
    /// </summary>
    private static int[] RemnantIndicesOf(PlanEnvironment env)
    {
        if (ReferenceEquals(_remnantIndicesFor, env.Targets))
            return _remnantIndices;

        var found = new List<int>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (env.Targets[i].Kind == TargetKind.Remnant)
                found.Add(i);
        }

        _remnantIndicesFor = env.Targets;
        _remnantIndices = found.ToArray();

        return _remnantIndices;
    }

    [ThreadStatic] private static IReadOnlyList<PlanTarget> _remnantIndicesFor;

    [ThreadStatic] private static int[] _remnantIndices;

    /// <summary>
    /// The most Gaining Traction can add to a remnant's magic and rare waves on this site, as a fraction: the rare scale
    /// less one, which is the larger, at every other remnant here and every one completed in the map before it. For the
    /// ceilings, which must stay above any chain. See TractionScales.
    /// </summary>
    internal static float MostTractionOf(PlanEnvironment env)
    {
        if (env.MagicPacksPerRemnantCompleted <= 0f && env.RarePacksPerRemnantCompleted <= 0f)
            return 0f;

        var remnants = 0;

        foreach (var target in env.Targets)
        {
            if (target.Kind == TargetKind.Remnant)
                remnants++;
        }

        var most = Math.Max(0, remnants - 1 + env.RemnantsCompletedInArea);
        var (magic, rare) = TractionScales(env, most);

        // The larger of the two, since the rates are set apart and either may be the bigger. See TractionScales.
        var largest = MathF.Max(magic, rare) - 1f;

        // And each remnant's own table, normals included, which the scoring reads. See TractionScalesOfTarget.
        foreach (var target in env.Targets)
        {
            if (target.Kind != TargetKind.Remnant || target.TractionByBefore is not { Length: > 0 })
                continue;

            var (n, m, r) = TractionScalesOfTarget(env, target, most);

            largest = MathF.Max(largest, MathF.Max(n, MathF.Max(m, r)) - 1f);
        }

        return largest;
    }

    /// <summary>
    /// The most Gaining Traction can add to a target on this site, for the ceilings: its magic and rare waves and the
    /// best of its combinations' extra waves, at the largest scale. See MostTractionOf.
    ///
    /// Plus the most any combination's "own" effects add to its waves, where one raises them, at the largest scale for
    /// both tiers - an upper bound on the magic one. Those waves are monsters, so the same amount bounds the monster
    /// pool. Nought where every own effect lowers them, which leaves the ceilings above every chain as they were.
    /// </summary>
    private static (float Content, float Monsters) MostTractionOfTarget(PlanTarget target, float traction)
    {
        var own = 0f;

        for (var c = 0; c < (target.OwnEffectsOfChoices?.Length ?? 0); c++)
            own = MathF.Max(own, target.OwnOfChoice(c, 1f + MathF.Max(0f, traction), 1f + MathF.Max(0f, traction)));

        if (traction <= 0f)
            return (own, own);

        var waves = target.MagicAndRareWaves;
        var normals = target.NormalWaves;
        var choices = 0f;

        foreach (var (rare, magic) in target.MagicAndRareWavesOfChoices ?? [])
            choices = MathF.Max(choices, rare + magic);

        return (traction * (waves.Rare + waves.Magic + normals.Normal + choices) + own,
            traction * (waves.RareMonsters + waves.MagicMonsters + normals.NormalMonsters) + own);
    }

    private static T[] Grow<T>(T[] held, int size)
    {
        if (held != null && held.Length >= size)
            return held;

        var grown = new T[Math.Max(Math.Max(size, 16), (held?.Length ?? 0) * 2)];

        held?.CopyTo(grown, 0);

        return grown;
    }

    [ThreadStatic] private static int[] _step;

    /// <summary>Which visit last touched a marker, so the step array never has to be blanked.</summary>
    /// <summary>The worklist a barrel's chain is walked with. See where it is used in Evaluate.</summary>
    [ThreadStatic] private static int[] _chained;

    [ThreadStatic] private static int[] _seen;

    /// <summary>The markers this chain covers, in the order they were found.</summary>
    [ThreadStatic] private static int[] _touched;

    /// <summary>Positions in touched, in link order. See Settle's choice loop.</summary>
    [ThreadStatic] private static int[] _order;

    /// <summary>Bucket counts for that ordering, one per link.</summary>
    [ThreadStatic] private static int[] _counts;

    [ThreadStatic] private static int _visits;

    /// <summary>A scratch array at least this long, grown once and kept.</summary>
    private static int[] Buffer(ref int[] held, int size)
    {
        if (held == null || held.Length < size)
            held = new int[Math.Max(size, 16)];

        return held;
    }

    /// <summary>One line per link, saying what it is for and what it is worth. Score cards only.</summary>
    private static List<string> Lines(PlanEnvironment env, List<Vector2> chain, int[] step,
        float[] after, float[] monsters, float[][] sums, int[] seen, int visit,
        out List<(double Content, double Carried)> each)
    {
        var text = new List<string>();
        var from = env.Origin;

        each = new List<(double Content, double Carried)>(chain.Count);

        for (var s = 0; s < chain.Count; s++)
        {
            var point = chain[s];
            // What the scoring loop booked at this link. See Settle's ContentToLink.
            var weight = _contentAtLink != null && s < _contentAtLink.Length ? _contentAtLink[s] : 0d;
            var kinds = new SortedDictionary<string, int>();

            for (var i = 0; i < env.Targets.Count; i++)
            {
                // The stamp as well as the step: the step array is no longer blanked between
                // scorings, so a stale entry from an earlier chain would otherwise read as a marker
                // this link catches.
                if (seen[i] != visit || step[i] != s)
                    continue;

                var target = env.Targets[i];
                var name = target.Kind.ToString().ToLowerInvariant();
                kinds[name] = kinds.TryGetValue(name, out var had) ? had + 1 : 1;
            }

            var carried = _credited != null && s < _credited.Length ? _credited[s] : 0d;

            each.Add((weight, carried));

            var caught = new List<string>();

            foreach (var pair in kinds)
                caught.Add($"{pair.Key} {pair.Value}");

            var walk = Vector2.Distance(from, point);

            text.Add($"  {s + 1,2}  ({point.X,4:0},{point.Y,4:0})  " +
                     $"content {weight,9:N1}  carried {carried,9:N1}  walked {walk,6:N1}  " +
                     (caught.Count > 0 ? string.Join(", ", caught) : "nothing"));

            from = point;
        }

        return text;
    }

    /// <summary>
    /// What one chain is worth, for comparing the plan against what a player actually did.
    ///
    /// The same function the search maximises, so the two numbers are commensurable. That is the
    /// whole value of it: a chain laid by hand can be put through the identical objective, and the
    /// comparison then says something about the plugin rather than about two different opinions.
    /// </summary>
    public static Verdict Judge(PlanEnvironment env, List<Vector2> chain) => Evaluate(env, chain, true);

    /// <summary>The same number without the per-link text, for a readout that wants it every frame.</summary>
    public static Verdict Rate(PlanEnvironment env, List<Vector2> chain) => Evaluate(env, chain);

    /// <summary>
    /// Copies a chain, counting it.
    ///
    /// **A solve allocates 3.1GB and nothing said what of.** Evaluate is allocation-free, so the
    /// candidate loops copying the chain they are about to score are the standing suspicion - and a
    /// suspicion is not a measurement. Every copy in the search goes through here, so the dump can
    /// multiply a count by a length and say whether these are the gigabytes or a rounding error.
    ///
    /// Counted per thread and not interlocked, for the reason LeafCalls gives: eight workers
    /// contending on one counter would change what is being measured, and an order of magnitude is
    /// what is wanted.
    /// </summary>
    private static List<Vector2> Copied(List<Vector2> chain)
    {
        _copies++;
        _copied += chain?.Count ?? 0;

        return new List<Vector2>(chain);
    }

    [ThreadStatic] private static long _verdicts;

    [ThreadStatic] private static long _copies;

    [ThreadStatic] private static long _copied;

    /// <summary>Chains copied, and links across them, since the last reset. See Copied.</summary>
    public static (long Copies, long Links) Copying => (Volatile.Read(ref _allCopies),
        Volatile.Read(ref _allCopied));

    /// <summary>
    /// How many Verdicts the search has built, which is one per scored chain.
    ///
    /// **Verdict is a sealed record, so each one is a heap object**, and Score builds a whole one
    /// to read a single double off it. Nine fields puts it near ninety bytes, so this count
    /// multiplied out is either most of a search's allocation or it is not - and the chain copies
    /// looked just as convincing before they were counted and came to nothing. See Evaluate.
    /// </summary>
    public static long Verdicts => Volatile.Read(ref _allVerdicts);

    private static long _allVerdicts;

    private static long _allCopies;

    private static long _allCopied;

    /// <summary>
    /// Folds this thread's counts into the shared totals. Called as a worker finishes, so the
    /// hot path stays a plain increment on a thread-local. See Solving.Across.
    /// </summary>
    public static void CountedUp()
    {
        Interlocked.Add(ref _allCopies, _copies);
        Interlocked.Add(ref _allCopied, _copied);
        Interlocked.Add(ref _allVerdicts, _verdicts);
        Interlocked.Add(ref _allScratchHits, _scratchHits);
        Interlocked.Add(ref _allScratchMisses, _scratchMisses);
        _scratchHits = 0;
        _scratchMisses = 0;

        if (_phaseBytes != null)
            for (var i = 0; i < _phaseBytes.Length; i++)
            {
                Interlocked.Add(ref AllPhaseBytes[i], _phaseBytes[i]);
                Interlocked.Add(ref AllPhaseCalls[i], _phaseCalls[i]);
                Interlocked.Add(ref AllPhaseTicks[i], _phaseTicks?[i] ?? 0L);
                _phaseBytes[i] = 0;
                _phaseCalls[i] = 0;

                if (_phaseTicks != null)
                    _phaseTicks[i] = 0;
            }
        _copies = 0;
        _copied = 0;
        _verdicts = 0;
    }

    /// <summary>Starts the counts again. See Caches.Clear.</summary>
    public static void ForgetCounts()
    {
        Interlocked.Exchange(ref _allCopies, 0);
        Interlocked.Exchange(ref _allCopied, 0);
        Interlocked.Exchange(ref _allVerdicts, 0);
        Interlocked.Exchange(ref _allScratchHits, 0);
        Interlocked.Exchange(ref _allScratchMisses, 0);

        for (var i = 0; i < AllPhaseBytes.Length; i++)
        {
            Interlocked.Exchange(ref AllPhaseBytes[i], 0);
            Interlocked.Exchange(ref AllPhaseCalls[i], 0);
            Interlocked.Exchange(ref AllPhaseTicks[i], 0);
        }
    }

    /// <summary>
    /// The names of the phases a search is broken into for allocation, in index order.
    ///
    /// **Three hypotheses about a search's two and a half gigabytes have now been counted and all
    /// three came to nothing** - the chain copies, the verdicts, and the scoring path that both
    /// belong to. What the counts did establish is that the search scores only eleven thousand
    /// chains, so the garbage is not per-score at all: it is roughly 227KB for every chain that
    /// reaches a score, spent somewhere between choosing candidates and arriving at one.
    ///
    /// So the phases are measured rather than guessed at a fourth time. Nested phases count their
    /// children's bytes as well as their own - Reaches inside Improve appears in both - which the
    /// dump says, because the alternative is bookkeeping that can itself be wrong.
    /// </summary>
    private static readonly string[] Phases =
        {
            "candidates", "greedy", "improve", "evaluate", "reaches",
            "whole (repair)", "whole (grasp)", "whole (beam)",
            "repair/tear", "repair/rebuild",
            "improve/sweep", "improve/order", "improve/reverse", "improve/shift",
            "improve/polish", "reaches/clamp", "reaches/route", "price/settle",
            "settle/payout", "settle/locally", "rebuild/rank", "rebuild/reach row",
            "shortlist", "shortlist/edge points",
        };

    internal const int PhaseCandidates = 0;
    internal const int PhaseGreedy = 1;
    internal const int PhaseImprove = 2;
    internal const int PhaseEvaluate = 3;
    internal const int PhaseReaches = 4;

    internal const int PhaseRepair = 5;

    internal const int PhaseGrasp = 6;

    internal const int PhaseBeam = 7;

    internal const int PhaseTear = 8;

    internal const int PhaseRebuild = 9;

    internal const int PhaseSweep = 10;

    internal const int PhaseOrder = 11;

    internal const int PhaseReverse = 12;

    internal const int PhaseShift = 13;

    internal const int PhasePolish = 14;

    /// <summary>A search for an aim that lands on a spot the direct aim cannot. See Terrain.Aiming and SnapModel.Aim.</summary>
    internal const int PhaseClamp = 15;

    /// <summary>A route searched for because the router had none on record. See Wire.Bend.</summary>
    internal const int PhaseRoute = 16;

    /// <summary>Settling a folded tally into a score, for the incremental pricing. See Price.</summary>
    internal const int PhaseSettle = 17;

    /// <summary>Settle's loop over links that pays out the rates in force. See Settle.</summary>
    internal const int PhaseSettlePayout = 18;

    /// <summary>Settle's check of each remnant's own runes against those arriving. See Locally.</summary>
    internal const int PhaseSettleLocally = 19;

    /// <summary>Rebuild ranking the shortlist by what each spot adds, for one hole. See Repair.RebuildInner.</summary>
    internal const int PhaseRebuildRank = 20;

    /// <summary>A row of the repair's reach table: one link position asked about every shortlist spot. See Repair.Near.</summary>
    internal const int PhaseReachRow = 21;

    /// <summary>A worker building its shortlist before its first round. See Repair.Shortlist.</summary>
    internal const int PhaseShortlist = 22;

    /// <summary>The edge points a shortlist takes, worked out or fetched. See Planner.EdgePointCellsOfEnvironment.</summary>
    internal const int PhaseShortlistEdgePoints = 23;

    [ThreadStatic] private static long[] _phaseBytes;

    [ThreadStatic] private static long[] _phaseCalls;

    private static readonly long[] AllPhaseBytes = new long[Phases.Length];

    private static readonly long[] AllPhaseCalls = new long[Phases.Length];

    /// <summary>
    /// Stopwatch ticks each phase spent itself, its nested phases taken out, per thread and then across all
    /// workers - the same accounting as the bytes. Summed over every worker, so a phase's total can be many times
    /// a press. See Phase.
    /// </summary>
    [ThreadStatic] private static long[] _phaseTicks;

    private static readonly long[] AllPhaseTicks = new long[Phases.Length];

    /// <summary>The ticks the open phase's nested phases have taken. See Phase and _phaseChildBytes.</summary>
    [ThreadStatic] private static long _phaseChildTicks;

    /// <summary>
    /// Measures what one phase of a search allocates on the thread running it.
    ///
    /// Thread-local and un-interlocked in the hot path, folded into the shared totals when the
    /// worker finishes - for the reason LeafCalls gives about contending counters.
    /// </summary>
    /// <summary>
    /// What the phase currently open has already had taken by phases nested inside it.
    ///
    /// Per thread, because the search runs a phase stack per worker and one worker's nesting says
    /// nothing about another's. See Phase.
    /// </summary>
    [ThreadStatic] private static long _phaseChildBytes;

    /// <summary>
    /// One phase of the search, measuring what it allocated.
    ///
    /// **Exclusive of what it calls, which it did not used to be.** A phase counted everything
    /// allocated while it was open, including every phase nested inside it, so the totals overlapped
    /// and the largest number belonged to the most-called phase rather than to the one allocating.
    /// Read straight off the dump that made the point: "reaches 434MB over 9,031,470 calls", the
    /// biggest line in the table - against a body that cannot allocate at all, since Says is a
    /// subtraction and two null checks and both delegates it guards are null in every environment
    /// this builds. Nine million samples of the thread's allocation counter, attributing whatever
    /// the thread did between the two reads.
    ///
    /// So a phase now subtracts what its children took and hands its own full spend up to whatever
    /// encloses it. The columns then add up to the whole instead of double counting it, and a phase
    /// with a big number is a phase to look at.
    /// </summary>
    internal readonly struct Phase : IDisposable
    {
        private readonly int _id;
        private readonly long _had;
        private readonly long _enclosing;
        private readonly long _began;
        private readonly long _enclosingTicks;

        public Phase(int id)
        {
            _id = id;

            // What the enclosing phase has banked so far, put aside so this one starts from nothing
            // and can measure its own children.
            _enclosing = _phaseChildBytes;
            _phaseChildBytes = 0L;
            _enclosingTicks = _phaseChildTicks;
            _phaseChildTicks = 0L;

            _had = GC.GetAllocatedBytesForCurrentThread();
            _began = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var took = Stopwatch.GetTimestamp() - _began;
            var spent = GC.GetAllocatedBytesForCurrentThread() - _had;

            // Everything under this phase was banked into _phaseChildBytes as each child closed, so
            // what is left is what this phase allocated itself. Time the same way.
            (_phaseBytes ??= new long[Phases.Length])[_id] += spent - _phaseChildBytes;
            (_phaseCalls ??= new long[Phases.Length])[_id]++;
            (_phaseTicks ??= new long[Phases.Length])[_id] += took - _phaseChildTicks;

            // And this phase, in full, is a child of whatever encloses it.
            _phaseChildBytes = _enclosing + spent;
            _phaseChildTicks = _enclosingTicks + took;
        }
    }

    /// <summary>What each phase of the search has allocated, and how often it ran. See Phases.</summary>
    public static IEnumerable<(string Name, long Bytes, long Calls, double Ms)> PhaseTotals()
    {
        for (var i = 0; i < Phases.Length; i++)
            yield return (Phases[i], Volatile.Read(ref AllPhaseBytes[i]),
                Volatile.Read(ref AllPhaseCalls[i]),
                Volatile.Read(ref AllPhaseTicks[i]) * 1000d / Stopwatch.Frequency);
    }

    internal static double Score(PlanEnvironment env, List<Vector2> chain) => Evaluate(env, chain).Total;

    /// <summary>Whether a chain starts with these explosives, as a route that includes the ones down does. See Evaluate.</summary>
    private static bool StartsWithHead(List<Vector2> chain, IReadOnlyList<Vector2> head)
    {
        if (chain.Count < head.Count)
            return false;

        for (var i = 0; i < head.Count; i++)
        {
            if (Vector2.DistanceSquared(chain[i], head[i]) >= 1f)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The explosives down followed by the chain, in a list this thread reuses: Evaluate is called millions of times
    /// a solve and is allocation free. The caller must not keep it. See PlanEnvironment.Whole.
    /// </summary>
    private static List<Vector2> Headed(IReadOnlyList<Vector2> head, List<Vector2> chain, ref List<Vector2> buffer)
    {
        buffer ??= new List<Vector2>(head.Count + chain.Count);
        buffer.Clear();

        for (var i = 0; i < head.Count; i++)
            buffer.Add(head[i]);

        buffer.AddRange(chain);

        return buffer;
    }

    [ThreadStatic] private static List<Vector2> _headedForEvaluate;

    [ThreadStatic] private static List<Vector2> _headedForPrice;

    /// <summary>
    /// What a chain is worth to a READER - content and propagation, without the insistence bonus.
    ///
    /// The search maximises Total, which carries a synthetic weight for holding a required marker so
    /// that a chain dropping one loses to every chain that keeps it. That weight is not loot, and a
    /// figure anybody looks at should not include it: on a site with one must-take the same chain reads
    /// 18,176 to the objective and 12,096 to the eye. See Verdict.Plain.
    /// </summary>
    internal static double Plainly(PlanEnvironment env, List<Vector2> chain) =>
        Evaluate(env, chain).Plain;

    /// <summary>
    /// The orders tried for more must takes than every order can be: nearest the detonator first, nearest neighbour
    /// from the detonator, and nearest neighbour starting at each must take in turn, with repeats dropped. Distances
    /// are straight lines, which is enough to rank an order; the tour built from it routes properly.
    ///
    /// **Why more than one.** Nearest the detonator first was the only order, and it cannot leave for a far marker and
    /// come back: on an Exhumed Ruins site (2026-10-06) with seven must takes, a chest 250 grid south of the detonator
    /// was held only by a chain that went west, south to the chest and back north east, and offline no worker held all
    /// seven from that one order. See Demanded.
    /// </summary>
    private static List<List<int>> OrdersBeyondFour(PlanEnvironment env, List<int> nearestFirst)
    {
        var made = new List<List<int>> { new(nearestFirst) };

        List<int> Chained(Vector2 from, int? start)
        {
            var left = new List<int>(nearestFirst);
            var order = new List<int>();

            if (start is { } first)
            {
                order.Add(first);
                left.Remove(first);
                from = env.Targets[first].Grid;
            }

            while (left.Count > 0)
            {
                var at = from;
                var next = left.OrderBy(i => Vector2.DistanceSquared(at, env.Targets[i].Grid)).First();

                order.Add(next);
                left.Remove(next);
                from = env.Targets[next].Grid;
            }

            return order;
        }

        void Add(List<int> order)
        {
            if (!made.Any(o => o.SequenceEqual(order)))
                made.Add(order);
        }

        Add(Chained(env.Origin, null));

        foreach (var start in nearestFirst)
            Add(Chained(env.Origin, start));

        return made;
    }

    /// <summary>Every order a handful of things could be taken in.</summary>
    private static IEnumerable<List<int>> Orders(List<int> of)
    {
        if (of.Count <= 1)
        {
            yield return new List<int>(of);

            yield break;
        }

        for (var i = 0; i < of.Count; i++)
        {
            var rest = new List<int>(of);

            rest.RemoveAt(i);

            foreach (var tail in Orders(rest))
            {
                var one = new List<int> { of[i] };

                one.AddRange(tail);

                yield return one;
            }
        }
    }

    /// <summary>
    /// Which markers a blast at each cell would catch, worked out once for a whole site.
    ///
    /// **A cell's coverage never changes; only which of the markers are still standing does.**
    /// Answering what a blast at a cell would be worth from scratch means testing the cell against all
    /// eighty odd markers. Measured on one site, when a since-removed band scan asked it of every cell
    /// of a disc: three hundred and seventy nine redraws at twenty six milliseconds apiece, ten of the
    /// twelve seconds a complete search took.
    ///
    /// Indexed instead. Coverage is geometry and the geometry is fixed, so each cell keeps the list
    /// of markers it catches and a redraw becomes a sum over five indices rather than a sweep of
    /// eighty. What changes between redraws - which markers are gone - is applied to the list, not
    /// recomputed from the ground.
    ///
    /// Built lazily per environment and dropped when the environment changes, which for a solve
    /// means once.
    /// </summary>
    private sealed class Coverage
    {
        private readonly Dictionary<long, int[]> _at = new();
        private readonly PlanEnvironment _env;

        public Coverage(PlanEnvironment env) => _env = env;

        public int[] Of(Vector2 at)
        {
            var key = Key(at);

            if (_at.TryGetValue(key, out var already))
                return already;

            var found = new List<int>();

            for (var i = 0; i < _env.Targets.Count; i++)
                if (_env.Targets[i].Wanted && Catches(_env, at, _env.Targets[i]))
                    found.Add(i);

            var made = found.ToArray();

            _at[key] = made;

            return made;
        }

        private readonly Dictionary<int, int[]> _remnantsNear = new();

        /// <summary>
        /// The remnants close enough to a marker that a link after one of them could still catch it, in index order:
        /// within a reach, two blasts and both markers' extents. The distance half of Planner.Waiting, which is
        /// geometry and does not change within an environment.
        /// </summary>
        public int[] RemnantsWithinDeferral(int target)
        {
            if (_remnantsNear.TryGetValue(target, out var already))
                return already;

            var found = new List<int>();
            var marker = _env.Targets[target];

            for (var i = 0; i < _env.Targets.Count; i++)
            {
                var other = _env.Targets[i];

                if (other.Kind != TargetKind.Remnant)
                    continue;

                var span = _env.Reach + 2f * _env.Blast + marker.Radius + other.Radius;

                if (Vector2.DistanceSquared(marker.Grid, other.Grid) <= span * span)
                    found.Add(i);
            }

            var made = found.ToArray();

            _remnantsNear[target] = made;

            return made;
        }
    }

    /// <summary>
    /// Per thread, and that is not a nicety.
    ///
    /// **A solve runs on a background thread while the overlay scores the live plan on the main
    /// one, with a different environment.** Shared, each thread's first call would throw away the
    /// other's index and rebuild it - so the cache would be cold on nearly every call, which is the
    /// opposite of what it is for - and worse, two threads could be inside the same Dictionary at
    /// once, which corrupts it rather than merely slowing it down.
    /// </summary>
    [ThreadStatic] private static Coverage _covers;

    [ThreadStatic] private static PlanEnvironment _covered;

    /// <summary>The index for this environment, rebuilt when the environment changes.</summary>
    /// <summary>
    /// The most this site could possibly pay, or nought when that cannot be said cheaply.
    ///
    /// **A bound has to be a bound.** A greedy estimate of one was tried and removed: it landed
    /// BELOW a real chain, which makes it useless for stopping and misleading as a yardstick. This
    /// takes the other approach: it scores the site with four of the rules dropped, which is why it
    /// is called a RELAXED ceiling rather than a best score.
    ///
    /// What it lets a chain do that no chain can:
    ///
    /// 1. **Reach anywhere.** Links have no range and no spacing, so the content is whatever the N
    ///    richest blasts cover rather than what can be strung together.
    /// 2. **Propagate backwards.** Every carrier's percentage lands on every monster in reach of
    ///    those blasts, where the scorer pays a rune only what is unearthed at its own link and
    ///    after it. Several carriers cannot all be first; here they all are.
    /// 3. **Take every combination at once.** A remnant offers one, and this counts the best of
    ///    them per group - so a remnant that must choose between a rune and a reward contributes as
    ///    though it took both.
    /// 4. **Catch a target twice.** A target covered by two of the N spots counts in both.
    ///
    /// **So the figure is not a target and the fraction of it a plan reaches is not how much is
    /// left.** On a dense site a real chain takes a few per cent of it and is still optimal. Only
    /// equality means anything, and what it means is that the relaxations stopped being
    /// relaxations.
    ///
    /// That happens, which is the whole reason this exists. On a sparse site - nine things to catch
    /// and twenty explosives - the chain genuinely does take everything and every carrier genuinely
    /// does precede the monsters it lifts, so the ceiling is met and the search stops instead of
    /// re-rolling openings for another eight seconds. On a dense one it never fires, at a cost of
    /// one pass over the targets per solve.
    ///
    /// Its second job is as an invariant: a published plan that EXCEEDS it says the scorer has a
    /// term this does not, which is how three of them were found - the local runes, a carrier's own
    /// waves, and the must-take bonus. See BoundAudited.
    ///
    /// Travel is unpriced, so it adds nothing here. A must-have remnant's bonus is included,
    /// because it is part of the score the search is maximising.
    /// </summary>
    /// <summary>Whether the last search stopped because nothing could beat what it had.</summary>
    public static bool StoppedAtRelaxedCeiling { get; private set; }



    /// <summary>What the sparse-site solve managed, win or lose. See Exact.</summary>
    public static string EnumeratedSolveOutcome { get; private set; } = "has not run";

    /// <summary>
    /// Forgets the last exact solve, so each press is reported on its own.
    ///
    /// **Per solve, from the frame, because the guard against redoing it is the text itself.**
    /// Outright stops once EnumeratedSolveOutcome starts with "took", which is what keeps it from re-running every
    /// round of a warm search - and that same text would then survive into the next solve and the
    /// next site. It used to be reset inside the restart search, which Destroy and Repair never entered.
    /// </summary>
    internal static void ForgetEnumeratedSolve() => EnumeratedSolveOutcome = "has not run";

    /// <param name="carried">
    /// What each target could add to each group, as RelaxedCeiling works it out - entry i belongs to
    /// env.Targets[i]. Null asks for the content and monster halves alone.
    /// </param>
    private static (double Content, double Monsters, Dictionary<int, double> Shares, double Product) Best(
        PlanEnvironment env, (int Band, double Share)[][] carried = null)
    {
        var links = Math.Max(1, env.Explosives);
        var candidates = Candidates(env, out _, out _);

        if (candidates.Count == 0)
            return (double.MaxValue, double.MaxValue, null, double.MaxValue);

        var covers = CoverageOfEnvironment(env);
        var worth = new List<double>(candidates.Count);
        var brings = new List<double>(candidates.Count);
        var traction = MostTractionOf(env);

        // What each candidate spot could contribute to each group, one list per group. The blast
        // is the unit because the chain is chosen in blasts: whatever a spot covers arrives
        // together or not at all.
        var perGroup = carried == null ? null : new Dictionary<int, List<double>>();
        var here = carried == null ? null : new Dictionary<int, double>();

        // And what each spot's own shares multiply out to. See the product bound below.
        var products = carried == null ? null : new List<double>(candidates.Count);

        foreach (var at in candidates)
        {
            var content = 0d;
            var monsters = 0d;

            here?.Clear();

            foreach (var index in covers.Of(at))
            {
                var target = env.Targets[index];

                var (tractionContent, tractionMonsters) = MostTractionOfTarget(target, traction);

                content += MathF.Max(0f, target.Weight) + tractionContent;
                monsters += target.MonstersUnearthed + tractionMonsters;

                if (carried != null && index < carried.Length && carried[index] != null)
                {
                    foreach (var (band, share) in carried[index])
                        here[band] = here.GetValueOrDefault(band) + share;
                }

                if (target.Choices == null)
                    continue;

                var reward = 0f;

                foreach (var choice in target.Choices)
                    reward = MathF.Max(reward, choice.Reward);

                content += reward;
            }

            worth.Add(content);
            brings.Add(monsters);

            if (here == null)
                continue;

            var mine = 1d;

            foreach (var (band, share) in here)
            {
                if (!perGroup.TryGetValue(band, out var list))
                    perGroup[band] = list = new List<double>(candidates.Count);

                list.Add(share);
                mine *= 1d + share;
            }

            products.Add(mine);
        }

        worth.Sort();
        brings.Sort();

        var afford = 0d;
        var unearthed = 0d;

        for (var i = 0; i < links; i++)
        {
            if (i < worth.Count)
                afford += worth[worth.Count - 1 - i];

            if (i < brings.Count)
                unearthed += brings[brings.Count - 1 - i];
        }

        // **The blast-wise product, which bounds the same thing a different way.**
        //
        // Shares add inside a group and the groups multiply, and for shares at or above nothing
        // 1 + a + b is never more than (1 + a)(1 + b). So the whole product over groups is at most
        // the product, over the blasts a chain is made of, of what each blast's own shares multiply
        // to - and at most the largest N of those, since every factor is at least one.
        //
        // It is not better than the per-group bound, it is DIFFERENT: this one is tight where the
        // shares sit on a few spots and loose where neighbouring candidates cover the same rich
        // marker, and the per-group one is the other way round. Both are ceilings, so the smaller
        // is a ceiling too, and which of them wins says which shape the site has.
        var product = 1d;

        if (products != null)
        {
            products.Sort();

            for (var i = 0; i < links && i < products.Count; i++)
                product *= products[products.Count - 1 - i];
        }

        Dictionary<int, double> shares = null;

        if (perGroup != null)
        {
            shares = new Dictionary<int, double>(perGroup.Count);

            // **The N richest spots per group, which no chain of N blasts can beat.** Each group is
            // maximised by whichever spots suit it, and those need not be the same N in two groups
            // - which only makes this looser, and a ceiling is allowed to be loose. What it is not
            // allowed to be is beatable, and it is not: a chain is N blasts, each blast's
            // contribution to a group is one of these entries, so their sum is at most the largest
            // N of them.
            foreach (var (band, list) in perGroup)
            {
                list.Sort();

                var sum = 0d;

                for (var i = 0; i < links && i < list.Count; i++)
                    sum += list[list.Count - 1 - i];

                shares[band] = sum;
            }
        }

        return (afford, unearthed, shares, products == null ? double.MaxValue : product);
    }

    internal static double RelaxedCeiling(PlanEnvironment env)
    {
        // A bound on what Evaluate returns, so over the same whole site when explosives are down. See
        // PlanEnvironment.Whole.
        if (env?.Whole is { } whole && env.Head is { Count: > 0 })
            return RelaxedCeiling(whole);

        if (env == null || env.Targets.Count == 0)
            return 0d;

        var content = 0d;
        var monsters = 0d;
        var traction = MostTractionOf(env);

        foreach (var target in env.Targets)
        {
            var (tractionContent, tractionMonsters) = MostTractionOfTarget(target, traction);

            content += MathF.Max(0f, target.Weight) + tractionContent;
            monsters += target.MonstersUnearthed + tractionMonsters;

            // **A remnant's reward is content and it was not in the ceiling.** Settle adds the
            // chosen combination's price to content - that is what makes one combination worth more
            // than another - and RelaxedCeiling counted only the target's own weight, which is what the
            // remnant is worth for BEING a remnant. Sixteen points short of the score on a Caldera
            // site, found by the bound being beaten by exactly that.
            //
            // The richest combination, since no chain can do better than pick the best one.
            if (target.Choices == null)
                continue;

            var reward = 0f;

            foreach (var choice in target.Choices)
                reward = MathF.Max(reward, choice.Reward);

            content += reward;
        }

        // Each carrier against everything it could possibly reach, which is not everything.
        //
        // **A carrier never reaches what its own blast brings.** Propagation is what is unearthed
        // AFTER a remnant, and a remnant's own waves come out with it - so the monsters it can lift
        // are the site's total less its own. Subtracting that is still an over-estimate, since two
        // carriers cannot both be first, and it is a markedly tighter one on a site whose carriers
        // are themselves the monster sources. The bound firing more often is the whole point: a
        // bound nothing ever reaches saves no time at all.
        // What the explosives actually in hand could reach, which is usually far less than the site.
        //
        // **A bound over the whole site is no bound at all when there are two explosives left.** The
        // terms above add up everything standing in the dig, so on a nearly finished chain the
        // ceiling sits thousands above anything two blasts could take and the search runs its full
        // window proving what it already had. The count is the missing constraint: no chain can
        // catch more than its richest N blasts, whatever N is.
        //
        // Over-estimated on purpose, because the N richest spots overlap and a real chain cannot
        // have them all - which keeps it a bound rather than an answer. See Best, called below once
        // the shares it also bounds have been worked out.

        // **Summed where the payout multiplies, and multiplied where it sums.** Settle prices
        // propagation as monsters * (product over GROUPS of (1 + that group's shares) - 1): shares
        // naming one stat add, and different stats multiply. Two distinct runes at forty per cent
        // are 1.4 x 1.4; two relics both raising item rarity are 1 + 0.4 + 0.4.
        //
        // Adding every share bounded the sum and not the product, and a site holding several
        // distinct runes outran it - measured at 5,407.0 paid against a term of 1,547.0.
        // Multiplying every share separately bounds it and is useless: the same site came out at
        // 689,041.6, a hundred and twenty seven times the answer, which is a bound that stops
        // nothing and tells a reader nothing.
        //
        // So the shares are gathered by the group the scoring itself would put them in, summed
        // within it and multiplied across, which is the payout's own shape. Every share on the site
        // counts and every combination's, not the chosen one's - a remnant can take only one, so
        // this over-counts on purpose and stays a ceiling.
        var mine = new Dictionary<int, double>();
        var one = new Dictionary<int, double>();

        // Kept per target rather than summed as they are found, because what bounds them is which
        // BLAST could carry them - see Best. Entry i belongs to env.Targets[i], and is null for the
        // great majority of markers, which propagate nothing.
        var sharesOfTarget = new (int Band, double Share)[env.Targets.Count][];

        static void Into(Dictionary<int, double> into, PlanEnvironment where, string id, double percent)
        {
            if (percent <= 0d)
                return;

            // An id names the stat, which is what decides the group. A flat carry has none - it is
            // the degenerate case aimed at monsters - so it lands in the default pool, where
            // everything unclassified adds together.
            var band = id == null ? 0 : GroupIndexOfEffect(where, id);

            into[band] = into.GetValueOrDefault(band) + percent / 100d;
        }

        static void KeepLargestPerGroup(Dictionary<int, double> into, Dictionary<int, double> of)
        {
            foreach (var (band, share) in of)
                into[band] = Math.Max(into.GetValueOrDefault(band), share);
        }

        for (var at = 0; at < env.Targets.Count; at++)
        {
            var target = env.Targets[at];

            mine.Clear();

            // **One representation per object, not all of them.** A combination's Carries IS the
            // sum of its propagating runes' rates, and the rune list is those same runes one at a
            // time - so counting both counts every rune twice. The old bound took the larger of the
            // two for exactly this reason; gathering them per group has to keep that, or nine
            // remnants inflate the ceiling by a factor nobody can account for. Measured before this
            // was noticed: 291,362.4 against 5,407.0 paid.
            //
            // And a remnant takes ONE combination, so its choices are the best of them rather than
            // the sum. Still a ceiling: the best per group, over every combination, is at least
            // what any single one of them brings.
            if (target.Choices != null)
            {
                for (var c = 0; c < target.Choices.Length; c++)
                {
                    var choice = target.Choices[c];

                    // The lifts the combination holds lift what it carries, and nothing below counts them: a local
                    // total leaves amplifiers out. Every carried share at every held class's lift, which over-counts and
                    // so stays a ceiling. See PlanTarget.HeldLiftsOfChoice.
                    var empowered = 1d;

                    foreach (var heldLift in target.HeldLiftsOfChoice(c) ?? [])
                        empowered *= 1d + heldLift;

                    one.Clear();

                    Into(one, env, null, choice.Carries * empowered);
                    Into(one, env, null, choice.Local);

                    foreach (var (id, _, percent, _) in choice.Spread ?? [])
                        Into(one, env, id, percent * empowered);

                    // **And what a rune that adds as well lifts**, each class as a factor of its own on everything. A
                    // pure amplifier is already here as its carry, as Power always was. Over-counts, so stays a ceiling.
                    foreach (var id in choice.Runes ?? [])
                    {
                        if (!Weighing.HasShareEffect(id) || Weighing.LiftsOfRune(id) is not { } lifts)
                            continue;

                        for (var a = 0; a < lifts.Length; a++)
                        {
                            if (lifts[a] > 0f)
                                one[LiftBandOfClass(a)] = Math.Max(one.GetValueOrDefault(LiftBandOfClass(a)),
                                    lifts[a] * empowered);
                        }
                    }

                    KeepLargestPerGroup(mine, one);
                }
            }

            one.Clear();

            Into(one, env, null, target.Carries);

            // Not stacking, so the rune weights are what pays rather than a combination's total.
            if (target.Runes != null)
            {
                foreach (var (id, weight) in target.Runes)
                    Into(one, env, id, weight);
            }

            KeepLargestPerGroup(mine, one);

            // **Everything scoped, which this had never read.** A relic granting "increased Rare
            // Monsters" carries its rate in Spread, and its flat carry is zeroed precisely because
            // the scope answers instead - see Weighing.PricedRowOfTarget. These are the object's
            // own, in force whatever it offers, so they add to whichever representation won.
            one.Clear();

            foreach (var (id, _, percent, _) in target.Spread ?? [])
                Into(one, env, id, percent);

            // A switch that pays once however many objects grant it. Still a factor in force.
            foreach (var (id, _, percent, _) in target.NonStacking ?? [])
                Into(one, env, id, percent);

            foreach (var (band, share) in one)
                mine[band] = mine.GetValueOrDefault(band) + share;

            // What this object could bring, filed against the object rather than added to a
            // site-wide total. Summing here was the whole of why the bound sat far above anything
            // reachable: every carrier on the site went into one product as though a single chain
            // could hold all of them, so a site with nine remnants and nine relics bounded its
            // propagation at a figure no five blasts could approach.
            if (mine.Count == 0)
                continue;

            var held = new (int Band, double Share)[mine.Count];
            var next = 0;

            foreach (var (band, share) in mine)
                held[next++] = (band, share);

            sharesOfTarget[at] = held;
        }

        // **The three halves bounded by the same constraint: a chain is N blasts.** Content and
        // monsters have been cut to the N richest spots for some time; the shares were not, and
        // they are the term that decides the product. See Best.
        var (afford, unearthed, bounded, blastwise) = Best(env, sharesOfTarget);

        content = Math.Min(content, afford);
        monsters = Math.Min(monsters, unearthed);

        // Nothing to place, so nothing constrains the shares and the site's own total stands. Best
        // answers this way when the site offers no candidate spot at all.
        var shares = bounded ?? Everything(sharesOfTarget);

        var mult = 1d;

        foreach (var (_, share) in shares)
            mult *= 1d + share;

        // Whichever ceiling is lower. See Best: they bound the same product by different routes and
        // neither dominates, so the site decides which applies.
        var grouped = mult;

        if (blastwise < mult)
            mult = blastwise;

        // **What the product is made of, because a bound nobody can take apart is a bound nobody
        // can fix.** Three faults have been found in this term by reading the total and guessing;
        // the groups and the pool it multiplies are what actually say where a wrong figure comes
        // from. Built once per solve, not per score.
        RelaxedCeilingShares = string.Join(", ", shares
            .Where(pair => pair.Value > 0.0001d)
            .OrderByDescending(pair => pair.Value)
            .Take(8)
            .Select(pair => $"g{pair.Key}={pair.Value * 100d:0.#}%")) +
            $" (by group {grouped:N2}x, blast-wise {(blastwise >= double.MaxValue ? "n/a" : blastwise.ToString("N2"))}x" +
            $" - {(blastwise < grouped ? "blast-wise" : "by group")} wins)";

        RelaxedCeilingPool = (monsters, mult);

        // One product against the site's monsters covers the local half as well: the densest single
        // blast is a subset of them, and (A-1) + (B-1) <= AB - 1 for rates at or above nothing.
        var carried = monsters * (mult - 1d);

        // What the explosives already down are still paying out. Carried rather than local, so it
        // reaches everything the rest of the chain digs up.
        if (env.Secured != null)
        {
            // The rough figure the greedy opener uses. A sum rather than the grouped product, and
            // deliberately: this is an ordering heuristic for which spot to try first, not the
            // objective, and the exact answer is a few lines away in Evaluate.
            var banked = 0f;

            foreach (var (_, _, percent, _, _) in env.Secured)
                banked += percent;

            if (banked > 0f)
                carried += banked / 100d * monsters;
        }

        // **And what the chain is required to take, which on a marked site is most of the number.**
        //
        // Third time a term the scorer has went missing here - first the local runes, then a
        // carrier's own waves, now this. Total credits Held * Refused for every insisted marker a
        // chain holds (see Verdict.Held), so a ceiling built only out of content and propagation is
        // beaten by any chain holding a single one of them, and the search stops on its first check
        // believing it has won. Measured on the Grand site this was written for: ten marked
        // remnants, a plan scoring 516,441.2 against a bound of 89,741.0, declared optimal with one
        // of the ten never reached.
        //
        // Every one of them held, since no chain can do better than that.
        var musts = env.Musts * env.Refused;

        // **Kept so a beaten bound can be attributed rather than argued about.** Three terms have
        // gone missing from here over time and each was found by the total being beaten - which says
        // only THAT it is wrong, never which half. The dump prints these beside the score's own
        // content and propagation, so the next one names itself. Written on the frame that asks, not
        // from the search.
        RelaxedCeilingTerms = (content, carried, musts);
        RelaxedCeilingNow = content + carried + musts;

        return RelaxedCeilingNow;
    }

    /// <summary>
    /// Every object's shares added together, by group - the bound RelaxedCeiling used before it could say
    /// which blasts would carry them.
    ///
    /// Only for a site offering no candidate spot, where there is no blast to bound anything by.
    /// </summary>
    private static Dictionary<int, double> Everything((int Band, double Share)[][] carried)
    {
        var shares = new Dictionary<int, double>();

        foreach (var held in carried)
        {
            foreach (var (band, share) in held ?? [])
                shares[band] = shares.GetValueOrDefault(band) + share;
        }

        return shares;
    }

    /// <summary>
    /// The bound as it last came out, for readouts that cannot afford to work it out again.
    ///
    /// RelaxedCeiling walks every candidate spot and everything each one covers, which is a solve's
    /// worth of work rather than a frame's - so the overlay reads this and the dump, which is asked
    /// for once, calls the method. It belongs to whatever environment was last bounded.
    /// </summary>
    internal static double RelaxedCeilingNow { get; private set; }

    /// <summary>The bound's three terms as RelaxedCeiling last worked them out. See RelaxedCeiling.</summary>
    internal static (double Content, double Carried, double Musts) RelaxedCeilingTerms { get; private set; }

    /// <summary>The largest groups feeding the bound's product, and what each came to.</summary>
    internal static string RelaxedCeilingShares { get; private set; } = "";

    /// <summary>The monster pool the bound multiplies, and the multiplier it built.</summary>
    internal static (double Monsters, double Mult) RelaxedCeilingPool { get; private set; }

    /// <summary>
    /// How many kinds of thing a site may hold before the exact solve gives up on it.
    ///
    /// Three. The work is a permutation of the kinds - six orders at three, twenty four at four -
    /// and each order costs a greedy cover of every group, so the cost turns up sharply while the
    /// sites it helps do not. A dig with four kinds of content on it is not the sparse case this is
    /// for.
    /// </summary>
    private const int MostKindsToEnumerate = 3;

    /// <summary>
    /// The right answer outright, for a site simple enough to reason about instead of search.
    ///
    /// **A sparse site has one answer and the search spends eight seconds confirming it.** Nine
    /// things and twenty explosives is not a search problem: the content divides into a couple of
    /// kinds, and what matters is which kind goes first. Everything else follows - cover each kind
    /// as cheaply as you can, in that order, and the chain is determined.
    ///
    /// So the kinds are permuted, each order is built greedily, and the best is kept. Six orders
    /// at three kinds, each costing one pass over the candidates per link. It returns a chain, not
    /// a promise: the caller compares it against <see cref="RelaxedCeiling"/>, and only a chain that
    /// reaches that bound proves nothing better exists.
    ///
    /// **Ordering is the whole reason this is not just greedy coverage.** Content does not care what
    /// order it is taken in; propagation cares about nothing else, since a carried rune is worth
    /// what comes after it. A greedy pass over both at once takes the remnant first because it is
    /// worth the most on its own, and loses more than it gains in what the relics would have lifted.
    ///
    /// Null when the site is not simple enough, or when no order produces a legal chain - in which
    /// case the ordinary search does what it always did.
    /// </summary>
    /// <summary>
    /// The exact answer for a simple site, for any strategy that wants it, or null.
    ///
    /// **Lifted out of the restart search, since removed, where it was reachable only by the fallback strategy.** The
    /// sparse solve was written for a map holding about ten things of two kinds, and it lived inside
    /// the restart search - so with Destroy and Repair selected, which is the default and the one
    /// that wins the bake-offs, it never ran at all. Every dump taken on that strategy says "working
    /// it out rather than searching: has not run", and that was the reason rather than the site.
    ///
    /// Returns a chain only when it beats what the caller already holds, so a strategy can use it as
    /// a floor and carry on searching from there. It is not a stop: reaching the right answer and
    /// proving it is the right answer are different things, and only RelaxedCeiling claims the second.
    ///
    /// Records what happened in EnumeratedSolveOutcome either way, which is what the score card prints.
    /// </summary>
    internal static List<Vector2> TryEnumeratedSolve(PlanEnvironment env, double beat)
    {
        if (env is not { UseEnumeratedSolve: true })
            return null;

        if (EnumeratedSolveOutcome.StartsWith("took", StringComparison.Ordinal))
            return null;

        var found = EnumeratedSolve(env);

        if (found is not { Count: > 0 } || !Walkable(env, found))
        {
            // Three different failures, and they used to read as one. Too many kinds is a site this
            // was never meant for; waiting is a router that has not looked yet; no legal chain is
            // the real refusal, and the only one worth investigating.
            // **And what each order said, which only the success path was printing.**
            //
            // InOrder returns a reason per order and the loop collects every one into
            // OrderingScores - "Remnant>Relic none (stuck at (x,y) with 6 left...)". That is exactly
            // the detail the refusal needs and it was appended to the took line alone, so the case
            // the comment above calls the only one worth investigating reported nothing but its own
            // name. Measured on the runic henge site: "no legal chain in any order (Remnant, Relic)"
            // and no way to tell which order failed, or where, or whether a chain was found and then
            // refused as unwalkable.
            var orders = OrderingScores.Length > 0 ? $" [{OrderingScores}]" : "";

            EnumeratedSolveOutcome = TooManyKindsToEnumerate
                ? $"not attempted - too many kinds of content ({ContentKindsFound})"
                : RouterNotReady
                    ? $"waiting on the router - no order confirmed legal yet ({ContentKindsFound})"
                    : found is { Count: > 0 }
                        // Found a chain and the router refused to walk it, which is not the same as
                        // finding none and read identically until now.
                        ? $"built a chain of {found.Count} links in some order and the router will " +
                          $"not walk it ({ContentKindsFound}){orders}"
                        : $"no legal chain in any order ({ContentKindsFound}){orders}";

            return null;
        }

        var worth = Score(env, found);

        EnumeratedSolveOutcome = $"took {worth:N1} over {found.Count} links " +
            $"({ContentKindsFound}) [{OrderingScores}]";

        return worth > beat ? found : null;
    }

    internal static List<Vector2> EnumeratedSolve(PlanEnvironment env)
    {
        if (env == null || env.Targets.Count == 0)
            return null;

        // Grouped by kind, which is what "a couple of things on this site" means in practice: eight
        // henges are one problem, not eight.
        var kinds = new List<TargetKind>();
        var groups = new List<List<int>>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            // Shunned ones are not a group to be covered - the whole point of them is not to be.
            if (!env.Targets[i].Wanted || env.Targets[i].Shunned)
                continue;

            var kind = env.Targets[i].Kind;
            var at = kinds.IndexOf(kind);

            if (at < 0)
            {
                if (kinds.Count == MostKindsToEnumerate)
                {
                    // Named, because "too many kinds" is not a useful thing to be told. Which kinds
                    // is the difference between a site this was never meant for and a stray
                    // classification putting a fourth name on a site that has two.
                    kinds.Add(kind);
                    ContentKindsFound = string.Join(", ", kinds);
                    TooManyKindsToEnumerate = true;

                    return null;
                }

                kinds.Add(kind);
                groups.Add(new List<int>());
                at = kinds.Count - 1;
            }

            groups[at].Add(i);
        }

        ContentKindsFound = string.Join(", ", kinds);
        TooManyKindsToEnumerate = false;

        if (groups.Count == 0)
            return null;

        var candidates = Candidates(env, out _, out _);
        var covers = CoverageOfEnvironment(env);
        var order = new int[groups.Count];
        List<Vector2> best = null;
        var most = double.NegativeInfinity;

        // Bring the router's work forward rather than waiting for it to accrue.
        //
        // A sparse site asks a few dozen reach questions and wants them now; the search asks
        // thousands and has a window. Without this the first call is answered almost entirely with
        // "not looked yet" and the whole thing defers to a search it should have replaced.
        env.Insist?.Invoke(TimeSpan.FromMilliseconds(250));

        for (var i = 0; i < order.Length; i++)
            order[i] = i;

        var waiting = false;
        var said = new List<string>();

        foreach (var tried in Orders(order))
        {
            var chain = InOrder(env, candidates, covers, groups, tried, ref waiting, out var why);
            var names = new List<string>();

            foreach (var group in tried)
                names.Add(kinds[group].ToString());

            var name = string.Join(">", names);

            if (chain == null)
            {
                // Which orders could not be built matters more than that one could. An order that
                // fails is a route the site does not allow; an order that succeeds badly is this
                // working as designed on a site that really is worth less that way round.
                said.Add($"{name} none ({why})");

                continue;
            }

            chain = Complete(env, chain, candidates);

            var worth = Score(env, chain);

            // A chain that stopped short says so beside its score: "Relic>Remnant 1,240" and
            // "Relic>Remnant 1,240 (partial)" are different claims about the same number.
            said.Add(why.Length > 0 ? $"{name} {worth:N0} (partial - {why})" : $"{name} {worth:N0}");

            if (worth <= most)
                continue;

            most = worth;
            best = chain;
        }

        OrderingScores = string.Join(", ", said);

        RouterNotReady = best == null && waiting;

        return best;
    }

    /// <summary>
    /// Whether the last exact solve failed only because the router had not looked yet.
    ///
    /// The difference between "this site is not simple enough" and "ask me again in a moment", which
    /// is what decides whether retrying is worth anything. See Certainty.
    /// </summary>
    internal static bool RouterNotReady { get; private set; }

    /// <summary>Which kinds of content the last exact solve found, for the readout.</summary>
    internal static string ContentKindsFound { get; private set; } = "";

    /// <summary>Whether it gave up on the count of kinds rather than on the chains.</summary>
    internal static bool TooManyKindsToEnumerate { get; private set; }

    /// <summary>What every order of the kinds managed, for the readout.</summary>
    internal static string OrderingScores { get; private set; } = "";

    /// <summary>Where the nearest thing still wanted stands.</summary>
    /// <summary>
    /// The order to visit a group's markers in, worked out once before the walk starts.
    ///
    /// **Only the exact solve uses this.** Nearest is called from one place - the travel goal inside
    /// InOrder - so nothing the ordinary search does is affected by any of it.
    ///
    /// **Nearest-first strands outliers, and that is what it was doing.** When nothing is in reach
    /// the walk stepped towards whichever marker was closest, which on a ring of content means going
    /// round the near side and arriving at the far one with the budget gone. Measured on the runic
    /// henge site: eight relics, and the one at (1237,706) - the only one off on its own - was never
    /// approached at all, while ten of the twenty links caught nothing. The site does admit full
    /// coverage; an earlier dump with the player standing at that end caught all eight.
    ///
    /// So the order is decided up front instead of one hop at a time: nearest-neighbour from where
    /// the chain stands, then 2-opt to uncross it. Both are the textbook cheap pair, and the second
    /// is what fixes the first - a nearest-neighbour tour that doubles back crosses itself, and
    /// uncrossing is exactly the move that pulls the far marker onto the way round rather than
    /// leaving it to the end.
    ///
    /// **It only sets the direction to travel when nothing is in reach.** Which candidate to place
    /// on is untouched, so anything catchable on the way is still caught and the order is a
    /// suggestion the walk is free to satisfy early.
    ///
    /// An open path, not a loop: the chain does not come home, so the last marker has no return leg
    /// and the arithmetic below leaves it out.
    /// </summary>
    private static List<int> Routed(PlanEnvironment env, HashSet<int> left, Vector2 from)
    {
        var route = new List<int>(left);

        if (route.Count <= 2)
            return route;

        // Nearest-neighbour, which is the starting tour and not the answer.
        var pool = new List<int>(route);
        var at = from;

        route.Clear();

        while (pool.Count > 0)
        {
            var pick = 0;
            var nearest = float.MaxValue;

            for (var i = 0; i < pool.Count; i++)
            {
                var gap = Vector2.DistanceSquared(env.Targets[pool[i]].Grid, at);

                if (gap >= nearest)
                    continue;

                nearest = gap;
                pick = i;
            }

            route.Add(pool[pick]);
            at = env.Targets[pool[pick]].Grid;
            pool.RemoveAt(pick);
        }

        // 2-opt over the open path. Reversing a run swaps which two legs join it to the rest, so the
        // test is those two legs against their replacements - and the tail has only one leg, which
        // is why the end of the path is handled separately.
        //
        // Bounded rather than run to convergence. A handful of markers converges in one or two
        // passes and this is inside a permutation of the kinds, so a cap keeps the cost flat.
        for (var pass = 0; pass < 8; pass++)
        {
            var moved = false;

            for (var i = 0; i < route.Count - 1; i++)
            {
                for (var j = i + 1; j < route.Count; j++)
                {
                    var before = i == 0 ? from : env.Targets[route[i - 1]].Grid;
                    var head = env.Targets[route[i]].Grid;
                    var tail = env.Targets[route[j]].Grid;

                    var was = Vector2.Distance(before, head);
                    var now = Vector2.Distance(before, tail);

                    if (j + 1 < route.Count)
                    {
                        var after = env.Targets[route[j + 1]].Grid;

                        was += Vector2.Distance(tail, after);
                        now += Vector2.Distance(head, after);
                    }

                    if (now >= was - 0.01f)
                        continue;

                    route.Reverse(i, j - i + 1);
                    moved = true;
                }
            }

            if (!moved)
                break;
        }

        return route;
    }

    private static Vector2 Nearest(PlanEnvironment env, HashSet<int> left, Vector2 from)
    {
        var at = Vector2.Zero;
        var nearest = float.MaxValue;

        foreach (var index in left)
        {
            var gap = Vector2.DistanceSquared(from, env.Targets[index].Grid);

            if (gap >= nearest)
                continue;

            nearest = gap;
            at = env.Targets[index].Grid;
        }

        return at;
    }

    /// <summary>
    /// One legal step that gets closer to where the chain needs to be.
    ///
    /// Rings outward from the head, keeping whichever placeable spot ends up nearest the goal - so
    /// a step is worth taking only if it makes progress. A step that does not is refused, which is
    /// what stops a chain pacing back and forth between two spots it can legally occupy.
    /// </summary>
    private static Vector2 Toward(PlanEnvironment env, List<Vector2> chain,
        List<Vector2> candidates, Vector2 from, Vector2 goal) =>
        Toward(env, chain, candidates, from, goal, out _);

    /// <summary>
    /// As Toward, and says why the invented points were refused when none survives.
    ///
    /// **The rings are the only source of travel on a spread site.** Candidates are anchored to
    /// content - one per target, a ring round the ones a blast cannot sit on, and midpoints between
    /// pairs near enough to catch together - so nothing in that list is a stepping stone by
    /// intention. When a chain has to cross ground holding nothing, these points are the whole of
    /// its options, and "no legal step gets closer" was reported without saying which test refused
    /// them.
    /// </summary>
    private static Vector2 Toward(PlanEnvironment env, List<Vector2> chain,
        List<Vector2> candidates, Vector2 from, Vector2 goal, out string rings)
    {
        var tried = 0;
        var cannotPlace = 0;
        var unrouted = 0;
        var crowded = 0;
        var noCloser = 0;

        var apart = MathF.Max(1f, env.Apart);
        var best = Vector2.Zero;
        var closest = Vector2.DistanceSquared(from, goal);

        // **A real spot first, because the rings below invent points that catch nothing.**
        //
        // The walk reaches here when no candidate catches anything still wanted in THIS group, and
        // it then looked only at synthetic points on rings around `from`. Two things follow from
        // that and both were costing chains.
        //
        // A candidate is a spot the plan already knows it can place on and that covers content -
        // just not content this group is looking for yet. Stepping onto one is free progress: the
        // loop below already credits whatever a stone catches by accident, so a stone that lands on
        // the next group's content has done that group's work early rather than wasting a link. A
        // ring point can only ever catch something by luck.
        //
        // And a ring point exists only at a distance of at least `apart` from `from`, so where the
        // goal is nearer than that the rings all overshoot and nothing gets closer. That reads as
        // "no legal step gets closer" with the goal plainly in reach - measured on the runic henge
        // site, stuck 52 grid from the nearest thing left.
        //
        // Nearest to the goal wins, and it must actually close the gap: same test as the rings, so
        // a candidate that is no better than standing still is not a step.
        foreach (var candidate in candidates ?? new List<Vector2>())
        {
            var gap = Vector2.DistanceSquared(candidate, goal);

            if (gap >= closest || Says(env, from, candidate) != Certainty.Yes ||
                !Spaced(env, chain, candidate, chain.Count))
                continue;

            closest = gap;
            best = candidate;
        }

        if (best != Vector2.Zero)
        {
            rings = "";

            return best;
        }

        closest = Vector2.DistanceSquared(from, goal);

        for (var ring = env.Reach; ring >= apart; ring -= apart / 2f)
        {
            var steps = Math.Max(8, (int)(ring / 4f));

            for (var step = 0; step < steps; step++)
            {
                var angle = step * MathF.Tau / steps;
                var at = from + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring;
                var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
                var gap = Vector2.DistanceSquared(snapped, goal);

                tried++;

                if (gap >= closest)
                {
                    noCloser++;

                    continue;
                }

                if (!env.CanPlace(snapped))
                {
                    cannotPlace++;

                    continue;
                }

                if (!Reaches(env, from, snapped))
                {
                    unrouted++;

                    continue;
                }

                if (!Spaced(env, chain, snapped, chain.Count))
                {
                    crowded++;

                    continue;
                }

                closest = gap;
                best = snapped;
            }

            // The outermost ring that makes progress is the one to take: a stepping stone wants to
            // cover as much ground as the reach allows, not to shuffle.
            if (best != Vector2.Zero)
            {
                rings = "";

                return best;
            }
        }

        rings = $"{tried} invented points: {noCloser} get no closer, {cannotPlace} cannot be " +
                $"placed on, {unrouted} the router will not reach, {crowded} are inside the spacing";

        return best;
    }

    /// <summary>Every ordering of a handful of groups. Six of them at three, which is the cap.</summary>
    private static IEnumerable<int[]> Orders(int[] of)
    {
        if (of.Length <= 1)
        {
            yield return of;

            yield break;
        }

        for (var i = 0; i < of.Length; i++)
        {
            var rest = new int[of.Length - 1];
            var at = 0;

            for (var j = 0; j < of.Length; j++)
            {
                if (j != i)
                    rest[at++] = of[j];
            }

            foreach (var tail in Orders(rest))
            {
                var whole = new int[of.Length];

                whole[0] = of[i];
                Array.Copy(tail, 0, whole, 1, tail.Length);

                yield return whole;
            }
        }
    }

    /// <summary>
    /// A chain that covers each group before starting the next, or null where that cannot be done.
    ///
    /// Greedy within a group and exact between them, which is the split that makes this cheap: the
    /// expensive question is the order, and there are six of those.
    /// </summary>
    private static List<Vector2> InOrder(PlanEnvironment env, List<Vector2> candidates,
        Coverage covers, List<List<int>> groups, int[] order, ref bool waiting, out string why)
    {
        why = "";

        var chain = new List<Vector2>();
        var taken = new HashSet<int>();
        var from = env.Origin;

        foreach (var group in order)
        {
            var left = new HashSet<int>(groups[group]);

            left.ExceptWith(taken);

            // Decided before the walk starts rather than one hop at a time. See Routed.
            var route = Routed(env, left, from);
            var visiting = 0;

            while (left.Count > 0)
            {
                if (chain.Count >= env.Explosives)
                {
                    why = $"ran out of explosives with {left.Count} left to catch";

                    // **The chain it got to is kept, and earns its place like any other.**
                    //
                    // Total coverage or nothing was the contract, and it made the exact solve abstain
                    // on the sites where the ORDER matters most. Measured on the runic henge site:
                    // nine markers, twenty explosives, and the ordinary search leaves one relic
                    // uncaught too - so full coverage is not available there, and this refused rather
                    // than offering the henges-first chain it had already built.
                    //
                    // Nothing is assumed about it being good. It goes through Complete and Score
                    // exactly as a finished chain does and only wins by scoring higher; a chain
                    // covering eight of nine scores below one covering nine of nine by construction,
                    // because Score values what is caught.
                    //
                    // An empty chain is still nothing - there is no answer in having placed no links.
                    return chain.Count > 0 ? chain : null;
                }

                var pick = Vector2.Zero;
                var caught = 0;
                var nearest = float.MaxValue;

                foreach (var candidate in candidates)
                {
                    // "Not yet" is not "no". A candidate the router has not looked at yet is the
                    // reason this used to give up and hand the site back to the search - see
                    // Certainty. Noted and skipped, so the caller knows to ask again rather than
                    // concluding the order is impossible.
                    var says = Says(env, from, candidate);

                    // Deferring is the better answer here whatever the switch says: this is the
                    // exact solve, it is cheap to retry, and it is asked again as the ground is
                    // learnt. Being hopeful only changes what happens when it is NOT retried.
                    if (says == Certainty.Unknown)
                    {
                        waiting = true;

                        continue;
                    }

                    if (!Reaches(env, from, candidate) ||
                        !Spaced(env, chain, candidate, chain.Count))
                        continue;

                    var here = 0;

                    foreach (var index in covers.Of(candidate))
                    {
                        if (left.Contains(index))
                            here++;
                    }

                    if (here == 0)
                        continue;

                    var gap = Vector2.DistanceSquared(from, candidate);

                    // Most of the group first, then the shortest hop - a link that catches two is
                    // worth a detour, and between two that catch the same the nearer keeps the rest
                    // of the chain's reach available.
                    if (here < caught || (here == caught && gap >= nearest))
                        continue;

                    caught = here;
                    nearest = gap;
                    pick = candidate;
                }

                // Nothing in reach catches anything - so travel, rather than give up.
                //
                // **A link that catches nothing is still a link.** This only ever placed blasts on
                // content, so the moment the next thing was further than one throw it declared the
                // whole order impossible - on a site whose eight henges span four hundred grid with
                // a reach of a hundred and sixty. The chain needs stepping stones and had no way to
                // make one, which read in the report as "no legal chain in any order".
                //
                // Toward the nearest thing still wanted, because a step in no particular direction
                // is how a chain wanders off and strands itself.
                if (pick == Vector2.Zero)
                {
                    // The next marker on the route still wanting to be caught. Anything the walk
                    // picked up on the way - including by accident - is simply skipped over.
                    while (visiting < route.Count && !left.Contains(route[visiting]))
                        visiting++;

                    var goal = visiting < route.Count
                        ? env.Targets[route[visiting]].Grid
                        : Nearest(env, left, from);

                    if (goal == Vector2.Zero)
                    {
                        why = "nothing left to walk towards";

                        return null;
                    }

                    var step = Toward(env, chain, candidates, from, goal, out var rings);

                    if (step == Vector2.Zero)
                    {
                        // **Which test refuses, because "no legal step" names four of them.**
                        //
                        // A step has to be placeable, routable from here, spaced off the other
                        // links, and closer to the goal than standing still. The message said only
                        // that none passed, and two changes were made against it on reasoning alone
                        // before anybody knew which one was doing the refusing. Counted over the
                        // candidates, which is the set that matters: the rings are invented points
                        // and these are the spots the plan already believes in.
                        var cannotPlace = 0;
                        var unrouted = 0;
                        var crowded = 0;
                        var noCloser = 0;

                        foreach (var candidate in candidates)
                        {
                            if (Vector2.DistanceSquared(candidate, goal) >=
                                Vector2.DistanceSquared(from, goal))
                                noCloser++;
                            else if (!env.CanPlace(candidate))
                                cannotPlace++;
                            else if (Says(env, from, candidate) != Certainty.Yes ||
                                     !Reaches(env, from, candidate))
                                unrouted++;
                            else if (!Spaced(env, chain, candidate, chain.Count))
                                crowded++;
                        }

                        why = $"stuck at ({from.X:0},{from.Y:0}) with {left.Count} left, " +
                              $"nearest at ({goal.X:0},{goal.Y:0}) " +
                              $"{Vector2.Distance(from, goal):0} away - of {candidates.Count} " +
                              $"candidates {noCloser} get no closer, {cannotPlace} cannot be placed " +
                              $"on, {unrouted} the router will not reach, {crowded} are inside the " +
                              $"spacing of a link already down; and {rings}";

                        return null;
                    }

                    // A stepping stone still goes off, and what it catches is caught.
                    //
                    // **Not claiming it is how the chain got stuck eight grid from its last
                    // target.** The step was blasting the remnant - well inside the catch radius -
                    // and the remnant stayed on the wanted list, so the loop went looking for a
                    // candidate to catch it and found every one of them inside the spacing of the
                    // stone it had just placed. A link that catches something by accident has
                    // caught it.
                    foreach (var index in covers.Of(step))
                        taken.Add(index);

                    left.ExceptWith(taken);

                    chain.Add(step);
                    from = step;

                    continue;
                }

                foreach (var index in covers.Of(pick))
                    taken.Add(index);

                left.ExceptWith(taken);
                chain.Add(pick);
                from = pick;
            }
        }

        return chain;
    }

    /// <summary>
    /// The monster weight a target's own blast brings out, whether as a kind or as waves.
    ///
    /// **Worth, not Weight, and that distinction was leaking a must-have bonus into propagation.**
    /// Weight carries the insistence - fifteen hundred points on a marked rare, on purpose, so the
    /// search reaches it - and for a monster kind this function fed that straight into the monster
    /// total a carried rune multiplies. Marking five rares did not just make the chain want them, it
    /// made every rune on the site appear to pay out over eight thousand points of imaginary
    /// monsters.
    ///
    /// Measured: a guided chain reported 5,768.9 with the bonus supposedly removed, and the same
    /// chain scored 2,403.3 the moment the marks came off. Nothing about it had changed. The
    /// difference was propagation priced against inflated monsters, which Plain does not subtract
    /// because Plain only knows about the content half of the bonus.
    ///
    /// A marked rare is worth reaching. It is not worth MORE MONSTERS, and only the content term is
    /// entitled to say otherwise.
    /// </summary>
    /// <summary>
    /// What this target files into the per-tag and per-class pools, as tagged amounts.
    ///
    /// Worked out once per target and kept on it, because the scoring walks this per covered marker
    /// per scored chain. See PlanTarget.Contributions, which is the rule.
    /// </summary>
    private static (long Mask, float Worth, float Many)[] ContributionsOfTarget(PlanTarget target) =>
        target.Contributions;

    /// <summary>
    /// Which class of thing this is: the set of relevant tags it carries, as an index.
    ///
    /// **A set rather than a tag**, because whether two modifiers add or multiply is decided on one
    /// creature, and one creature is several tags at once. A wave rare is monster, rare_monster and
    /// modifiable together, so a chest-scoped modifier misses it and two monster-scoped ones meet
    /// on it. See PlanEnvironment.Relevant.
    /// </summary>
    private static int ClassOfMask(PlanEnvironment env, long mask)
    {
        // **Nothing scales what is not modifiable, whatever else it is tagged.**
        //
        // The tag was a peer of the others - a barrel without it was still reached by anything
        // scoped at "scenery", so taking it off changed nothing and the column said something it
        // did not mean. It is a precondition instead: no weight_modifiable, no uplift, from any
        // modifier, however aimed.
        //
        // Class nought is the one the payout skips, so this costs a bit test and puts the weight
        // somewhere no share is ever applied. Its content still counts - a barrel is worth what it
        // is worth - it simply cannot be multiplied.
        if ((mask & (1L << Tags.Modifiables)) == 0L)
            return 0;

        var relevant = env.Relevant;
        var found = 0;

        for (var k = 0; k < relevant.Length; k++)
        {
            if ((mask & (1L << relevant[k])) != 0L)
                found |= 1 << k;
        }

        return found;
    }

    /// <summary>Where a tag sits in the relevant list, or -1 for one nothing here can reach.</summary>
    private static int Indexed(PlanEnvironment env, int tag)
    {
        var relevant = env.Relevant;

        for (var k = 0; k < relevant.Length; k++)
        {
            if (relevant[k] == tag)
                return k;
        }

        return -1;
    }

    /// <summary>
    /// What everything picked up so far multiplies this class of thing by.
    ///
    /// **Added inside a group, multiplied between groups.** That is the game's own arithmetic: two
    /// modifiers raising the same stat sum - "50% increased number of Rare Monsters" twice is
    /// +100% - and modifiers raising different stats compound, because they are different factors of
    /// one product. Quantity per monster times how many monsters there are is not quantity plus
    /// monsters. See TableGrammar.Effect, whose "as" clause is where a row says its stat.
    ///
    /// <paramref name="extra"/> is the marker's own ordinary runes, which reach its waves and
    /// nothing else - so they join the product here rather than in the chain-wide accumulator, and
    /// only for the monster part of a class.
    /// </summary>
    /// <param name="lift">
    /// Every class's lift together, applied to every share - only where there is no amplification table, an
    /// environment read back from a file. See PlanEnvironment.Amplified.
    /// </param>
    /// <param name="factors">
    /// The factor each set of amplifier classes puts on the shares it lifts, indexed by mask. See
    /// Amplification.FactorsOfMasks.
    /// </param>
    private static double Multiplied(float[] prefix, float[] extra, int groups, int width, int cls,
        double lift = 0d, Amplification amplified = null, double[] factors = null, float[] bases = null)
    {
        var mult = 1d;

        for (var g = 0; g < groups; g++)
        {
            // **Shares add to what the map already gives the stat**, so they are worth that much less: the factor is
            // (1 + base + shares) / (1 + base). See PlanEnvironment.IncreaseBaseOfGroup.
            var start = bases != null && g < bases.Length ? 1d + bases[g] : 1d;

            // **With twins, each lift reaches only the shares its classes lift.** A twin is summed with its plain group
            // below, at the factor of the classes lifting it, and skipped as a group of its own. See Amplification.
            if (amplified == null || factors == null)
            {
                var add = ShareOfGroup(prefix, extra, g, width, cls);

                if (add != 0f)
                    mult *= 1d + add * (1d + lift) / start;

                continue;
            }

            if (g < amplified.PlainOfGroup.Length && amplified.PlainOfGroup[g] >= 0)
                continue;

            var twins = g < amplified.TwinsOfGroup.Length ? amplified.TwinsOfGroup[g] : null;
            var total = (double)ShareOfGroup(prefix, extra, g, width, cls);

            foreach (var twin in twins ?? [])
                total += ShareOfGroup(prefix, extra, twin, width, cls) * amplified.LiftedFactorOfGroup(twin, factors);

            if (total != 0d)
                mult *= 1d + total / start;
        }

        return mult;
    }

    /// <summary>One group's shares reaching this class of thing, the marker's own ordinary runes included. See Multiplied.</summary>
    private static float ShareOfGroup(float[] prefix, float[] extra, int g, int width, int cls)
    {
        var add = extra != null && (cls & 1) != 0 ? extra[g] : 0f;
        var at = g * width;

        for (var k = 0; k < width; k++)
        {
            if ((cls & (1 << k)) != 0)
                add += prefix[at + k];
        }

        return add;
    }

    /// <summary>
    /// The same shares added into one pool, which is what the objective used to do.
    ///
    /// **Kept as the check, not as an alternative.** Every group sitting in the default pool must
    /// make the grouped payout and this agree to the penny - that is what guarantees a site nobody
    /// has classified scores exactly as it did before groups existed. The dump prints both, so the
    /// claim is testable in a dig site rather than asserted here. See Planner.Pooled.
    /// </summary>
    private static double Added(float[] prefix, float[] extra, int groups, int width, int cls)
    {
        var add = 0d;

        for (var g = 0; g < groups; g++)
        {
            if (extra != null && (cls & 1) != 0)
                add += extra[g];

            var at = g * width;

            for (var k = 0; k < width; k++)
            {
                if ((cls & (1 << k)) != 0)
                    add += prefix[at + k];
            }
        }

        return add;
    }

    /// <summary>
    /// What flat effects add to ONE thing of this class.
    ///
    /// **No groups and no product**, which is the whole difference between this and Multiplied. A
    /// share belongs to a stat, and which stats meet on one creature is what decides whether two of
    /// them add or compound. A flat effect names no stat - "excavated_chest.weight += 3" is three
    /// more weight on the chest whatever else reaches it - so every flat amount whose tag this class
    /// carries simply adds.
    ///
    /// Class nought never reaches here from the chain-wide payout and reads nought from the
    /// per-marker one, which is the same rule shares follow: nothing that is not weight_modifiable
    /// is changed by anything. See ClassOfMask.
    /// </summary>
    private static double Flatly(float[] flat, int width, int cls)
    {
        var add = 0d;

        for (var k = 0; k < width; k++)
        {
            if ((cls & (1 << k)) != 0)
                add += flat[k];
        }

        return add;
    }

    /// <summary>
    /// What the last detailed chain would have scored on the old additive pool, for comparison.
    ///
    /// Only written on a detailed pass, which is the one the dump asks for - the search runs this
    /// half a million times and must not be writing to shared state.
    /// </summary>
    public static double Pooled { get; private set; }

    /// <summary>One bucket of weight per class of thing per link, cleared.</summary>
    private static float[][] Classes(PlanEnvironment env, int links)
    {
        var want = 1 << env.Relevant.Length;
        var classed = _classed;

        if (classed == null || classed.Length < want)
        {
            classed = new float[want][];
            _classed = classed;
        }

        for (var c = 0; c < want; c++)
        {
            classed[c] = Grow(classed[c], links);

            for (var s = 0; s < links; s++)
                classed[c][s] = 0f;
        }

        return classed;
    }

    /// <summary>
    /// The same buckets again, holding HOW MANY rather than what they are worth.
    ///
    /// A share multiplies weight and needs none of this. A flat effect - "excavated_chest.weight
    /// += 3" - is worth three per chest, so the only thing that can pay it is a count, and the
    /// pipeline carried weight alone until it was asked for. See TableGrammar.ContributionsOfRow.
    /// </summary>
    private static float[][] HowMany(PlanEnvironment env, int links)
    {
        var want = 1 << env.Relevant.Length;
        var counted = _counted;

        if (counted == null || counted.Length < want)
        {
            counted = new float[want][];
            _counted = counted;
        }

        for (var c = 0; c < want; c++)
        {
            counted[c] = Grow(counted[c], links);

            for (var s = 0; s < links; s++)
                counted[c][s] = 0f;
        }

        return counted;
    }

    [ThreadStatic] private static float[][] _counted;

    [ThreadStatic] private static float[][] _classed;

    /// <summary>The share each group picks up at each link, per relevant tag. See Multiplied.</summary>
    [ThreadStatic] private static float[] _rates;

    /// <summary>Scratch for Settle's SharesInForceAt.</summary>
    [ThreadStatic] private static float[] _sharesInForce;

    /// <summary>Scratch for Settle's provisional pass: each provisional pick's propagated runes and its link.</summary>
    [ThreadStatic] private static (int Key, int Link)[] _laterSends;

    /// <summary>The shares no rune contributed, per group, tag and link. See Tags.UnaffectedByRunes.</summary>
    [ThreadStatic] private static float[] _ratesWithoutRunes;

    /// <summary>Their running sum down the chain. See _ratesWithoutRunes.</summary>
    [ThreadStatic] private static float[] _prefixWithoutRunes;

    /// <summary>Whether a booked name is a rune's, by the table holding a rune row of it. Cached per thread. A banked
    /// rune is booked as no rune for the line under remnants, so the flag alone cannot say. See Settle's payout.</summary>
    [ThreadStatic] private static Dictionary<string, bool> _runeNames;

    private static bool IsRuneName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        _runeNames ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (!_runeNames.TryGetValue(name, out var rune))
            _runeNames[name] = rune = Wrt.Of(Wrt.Id.Rune(Weighing.BaseOfLiftKey(name))) != null;

        return rune;
    }

    /// <summary>What flat effects add per thing, by class and link. See Settle's flats.</summary>
    [ThreadStatic] private static float[] _flats;

    /// <summary>The running sum of the above, by class. See Settle's flatly.</summary>
    [ThreadStatic] private static float[] _flatly;

    /// <summary>Whether each booking holds an amount rather than a share. See Book.</summary>
    [ThreadStatic] private static bool[] _carriedFlat;

    /// <summary>The same, accumulated forwards as the payout walks the chain.</summary>
    [ThreadStatic] private static float[] _prefix;

    /// <summary>One marker's own ordinary runes, by group, for its waves alone.</summary>
    [ThreadStatic] private static float[] _own;

    /// <summary>
    /// A scratch array per referenced tag, cleared, or null when nothing here is scoped.
    /// </summary>
    private static float[][] Tagged(int[] scoped, int links)
    {
        if (scoped == null || scoped.Length == 0 || links == 0)
            return null;

        var tagged = _tagged;

        if (tagged == null || tagged.Length < scoped.Length)
        {
            tagged = new float[scoped.Length][];
            _tagged = tagged;
        }

        for (var k = 0; k < scoped.Length; k++)
        {
            tagged[k] = Grow(tagged[k], links);

            for (var s = 0; s < links; s++)
                tagged[k][s] = 0f;
        }

        // **Past the tags in use, nothing.** The buffer is kept per thread across environments, so one built for a
        // site with more scoped tags left arrays here sized for an older, shorter chain, and Suffix, which walks the
        // whole buffer, indexed past their end: after a reroll on a Grazed Prairie site (2026-10-06) every score
        // threw. Suffix skips a null entry.
        for (var k = scoped.Length; k < tagged.Length; k++)
            tagged[k] = null;

        return tagged;
    }

    /// <summary>Each tag's worth from a link onwards, the way after[] is built from monsters[].</summary>
    /// <summary>
    /// The tally's own per-tag arrays, which it keeps across a push and a pop.
    ///
    /// Not the per-thread scratch Evaluate borrows: that is reused on every call and the tally holds
    /// its arrays between them, so sharing would have one overwrite the other halfway through a
    /// greedy fill.
    /// </summary>
    /// <summary>
    /// The tally's own class buckets, kept between a push and a pop for the same reason Fresh
    /// exists: the per-thread scratch is reused on every call and would be overwritten halfway
    /// through a greedy fill.
    /// </summary>
    private static float[][] Owning(PlanEnvironment env, int links)
    {
        var made = new float[1 << env.Relevant.Length][];

        for (var c = 0; c < made.Length; c++)
            made[c] = new float[Math.Max(links, 1)];

        return made;
    }

    private static float[][] Fresh(PlanEnvironment env, int links)
    {
        var scoped = env?.Scoped;

        if (scoped == null || scoped.Length == 0)
            return null;

        var made = new float[scoped.Length][];

        for (var k = 0; k < scoped.Length; k++)
            made[k] = new float[Math.Max(links, 1)];

        return made;
    }

    private static float[][] Suffix(float[][] tagged, int links)
    {
        if (tagged == null || links == 0)
            return null;

        var sums = _taggedAfter;

        if (sums == null || sums.Length < tagged.Length)
        {
            sums = new float[tagged.Length][];
            _taggedAfter = sums;
        }

        for (var k = 0; k < tagged.Length; k++)
        {
            if (tagged[k] == null)
                continue;

            sums[k] = Grow(sums[k], links);
            sums[k][links - 1] = tagged[k][links - 1];

            for (var s = links - 2; s >= 0; s--)
                sums[k][s] = sums[k][s + 1] + tagged[k][s];
        }

        return sums;
    }

    /// <summary>
    /// What a tag is worth from this link onwards.
    ///
    /// The monster tag reads the accumulator that was always here, which is what makes a migrated
    /// "monster=n" identical to the flat n it came from rather than close to it.
    /// </summary>
    /// <summary>
    /// What a tag is worth from this link onwards. Internal because PlanTarget.Best ranks with it.
    /// </summary>
    internal static float Reach(PlanEnvironment env, int tag, float[][] sums, float[] after, int at,
        float monsters = 0f)
    {
        if (tag == Tags.Monsters)
            return after == null ? monsters : after[at];

        if (sums == null)
            return 0f;

        var scoped = env.Scoped;

        for (var k = 0; k < scoped.Length; k++)
        {
            if (scoped[k] == tag)
                return sums[k] == null ? 0f : sums[k][at];
        }

        return 0f;
    }

    /// <summary>
    /// What a target is worth, floored at nothing.
    ///
    /// **It used to be three lines picking between two weights**, because a marked marker's Weight
    /// carried a bonus bigger than the site and this was how a readout got underneath it. Nothing
    /// inflates a Weight any more - insistence is PlanTarget.Must and it is scored on the chain, not
    /// on the marker - so the two numbers it chose between are the same number.
    ///
    /// The floor stays, and it is doing real work: an avoided marker carries a weight below every
    /// legal chain, and nothing that asks "what is this worth" wants that answer.
    /// </summary>
    internal static float WorthOfTarget(PlanTarget target) => MathF.Max(0f, target.Weight);

    /// <summary>
    /// What a blast at this cell catches, as indices into the target list.
    ///
    /// The coverage index itself, which is looked up rather than recomputed and is cached per cell
    /// for the life of the environment. Exposed so a search outside this class can ask what a spot
    /// would add without paying for a whole chain scoring to find out. See Repair.Rebuild.
    /// </summary>
    internal static int[] CaughtIndicesAt(PlanEnvironment env, Vector2 at) => CoverageOfEnvironment(env).Of(at);

    /// <summary>
    /// This site's number for an effect name, or -1 when it has none.
    ///
    /// One hash lookup where the scoring loop used to run a linear scan of case-insensitive string
    /// comparisons. See Settle's Book, and PlanEnvironment.Effects for where the table comes from.
    /// </summary>
    private static int NumberOfEffect(PlanEnvironment env, string id) =>
        env.Effects != null && env.Effects.TryGetValue(id, out var key) ? key : -1;

    /// <summary>
    /// Which group an effect adds inside of, or the default pool for anything unclassified.
    ///
    /// The default pool is group nought and everything lands in it until somebody says otherwise,
    /// which is what makes an unclassified site score exactly as it did before groups existed. See
    /// TableGrammar.Effect's "as" clause.
    /// </summary>
    /// <summary>
    /// The factor the lifts a remnant holds put on one effect it propagates: the product of one plus each held class's
    /// lift, over the classes lifting the effect. One where none applies. See PlanTarget.HeldLiftsOfChoice.
    /// </summary>
    internal static double HeldFactorOfEffect(PlanEnvironment env, string id, float[] heldLifts)
    {
        if (heldLifts == null || env.Amplified is not { } amplified)
            return 1d;

        var number = NumberOfEffect(env, id);

        if (number < 0 || number >= amplified.MaskOfEffect.Length)
            return 1d;

        var mask = amplified.MaskOfEffect[number];
        var factor = 1d;

        for (var a = 0; a < amplified.Classes && a < heldLifts.Length; a++)
        {
            if ((mask & (1L << a)) != 0L && heldLifts[a] > 0f)
                factor *= 1d + heldLifts[a];
        }

        // **Only the part of the lift its group takes**, as the payout applies it: Bond's row writes 15% plain and 18%
        // empowered, so a held Power lifts it by a fifth, not double. Ranked at double, a Grazed Prairie remnant
        // (2026-10-06) holding Power and sending Bond was credited 288 for the lift where the payout paid 58, and chose
        // a combination that cost its chain about 500. See Amplification.LiftedFactorOfGroup.
        var band = GroupIndexOfEffect(env, id);

        return amplified.ReachOfGroup is { } reach && band >= 0 && band < reach.Length
            ? 1d + (factor - 1d) * reach[band]
            : factor;
    }

    /// <summary>
    /// What a propagated rune that adds and lifts is worth for its lift at link at, in the units Best's reach adds
    /// (percent times weight): each class it lifts, times the shares already booked that the class lifts, times what they
    /// reach from this link on. Only bookings made before this point of the walk are seen, so a rune lifting shares booked
    /// further down the chain is under-ranked here; the payout counts them. Nought for a rune that only lifts - Best
    /// weighs that as a carry, as it always weighed Power. See Amplification.
    /// </summary>
    internal static double AmplifierWorthAt(PlanEnvironment env, string id, float[] heldLifts, int at, float[][] sums,
        float downstream, int distinct)
    {
        if (env.Amplified is not { } amplified || string.IsNullOrEmpty(id) || !Weighing.HasShareEffect(id))
            return 0d;

        var number = NumberOfEffect(env, id);

        if (number < 0 || number >= amplified.LiftsOfEffect.Length || amplified.LiftsOfEffect[number] is not { } lifts)
            return 0d;

        var scale = HeldFactorOfEffect(env, id, heldLifts);
        var worth = 0d;

        for (var r = 0; r < distinct && _carriedEffect != null && r < _carriedEffect.Length; r++)
        {
            var effect = _carriedEffect[r];

            if (effect < 0 || effect >= amplified.MaskOfEffect.Length || _carriedWeights[r] <= 0f)
                continue;

            var mask = amplified.MaskOfEffect[effect];
            var lift = 0d;

            for (var a = 0; a < amplified.Classes && a < lifts.Length; a++)
            {
                if ((mask & (1L << a)) != 0L)
                    lift += lifts[a];
            }

            if (lift > 0d)
                worth += lift * scale * _carriedWeights[r] * Reach(env, _carriedTag[r], sums, null, at, downstream);
        }

        return worth;
    }

    /// <summary>
    /// The key the relaxed ceiling files one amplifier class's lift under, apart from every real group, so it multiplies
    /// as a factor of its own. See RelaxedCeiling.
    /// </summary>
    private static int LiftBandOfClass(int a) => -1000 - a;

    /// <summary>Whether any amplifier lifts this effect. See Weighing.AmplifiedClassesOfRune.</summary>
    internal static bool IsEmpowerableEffect(PlanEnvironment env, string id) =>
        env.Amplified is { } amplified && NumberOfEffect(env, id) is var number && number >= 0 &&
        number < amplified.MaskOfEffect.Length && amplified.MaskOfEffect[number] != 0L;

    private static int GroupIndexOfEffect(PlanEnvironment env, string id)
    {
        var key = NumberOfEffect(env, id);

        return key >= 0 && env.Bands != null && key < env.Bands.Length ? env.Bands[key] : 0;
    }

    /// <summary>
    /// One marker's own ordinary runes, gathered by group, for the waves they reach.
    ///
    /// Null when it has none, which is most markers - the payout then skips the extra term rather
    /// than walking an array of noughts per marker per scored chain.
    /// </summary>
    /// <param name="lift">
    /// How much this remnant's own empowering runes scale everything landing on its waves.
    ///
    /// **A rune with no worth of its own was being dropped, and a Power rune has none by definition.**
    /// Its whole effect is to multiply the others, so the worth test below - right for every ordinary
    /// rune, where nought means nothing to pay - threw out the one rune whose nought is the point. A
    /// six socket remnant read 5 (5+0-0) and changing the multiplier in the table moved nothing.
    ///
    /// Handed back separately rather than added to a group, because a group would pay for it on a
    /// chain with nothing to empower: (1+p)(1+r)-1 is p + r + pr where what is wanted is r(1+p). Same
    /// distinction as the propagating side - see PlanEnvironment.Empowering - applied to the half that
    /// stays put.
    /// </param>
    /// <param name="mineAt">
    /// Which link's waves are being paid, so a duplicate is judged on order. See Booked.
    /// </param>
    private static float[] LocalSharesOfMarker(PlanEnvironment env, (string Id, float Worth)[][] locals, int t,
        int groups, int mineAt, int distinct, double[] lift)
    {
        for (var a = 0; a < lift.Length; a++)
            lift[a] = 0d;

        var mine = locals != null && t < locals.Length ? locals[t] : null;

        if (mine is not { Length: > 0 })
            return null;

        var own = Grow(_own, groups);
        var any = false;

        _own = own;

        for (var g = 0; g < groups; g++)
            own[g] = 0f;

        foreach (var (id, worth) in mine)
        {
            if (id == null)
                continue;

            // **Before the worth test, because an empowering rune has no worth to pass it with.**
            //
            // It scales what lands on these waves - the runes propagated here from earlier links and
            // this remnant's own sockets alike - so it is taken out of the groups entirely and applied
            // once, after they have compounded. A duplicate is still struck: a second source of the
            // same empowering rune adds nothing, which is the rule for every rune.
            if (env.Empowering >= 0 && GroupIndexOfEffect(env, id) == env.Empowering)
            {
                // **The strongest rate in force, not the sum of them - empowering runes do not
                // stack.**
                //
                // This added, and the duplicate test it leaned on could not see the case that
                // matters: Booked consults the PROPAGATED set only, so it has no memory of an
                // empowering rune already read from this same remnant. Two Power runes in one
                // remnant therefore both counted, and a remnant holding two at fifty scaled its
                // waves by two rather than by one and a half. Power and Bait are a second way in -
                // two different ids carrying the identical "Runes gain: Empowered" - so testing the
                // id against itself would not have caught it either.
                //
                // Taking the maximum is what non-stacking means when the rates differ: the stronger
                // applies and the weaker is redundant, rather than the two averaging or adding. With
                // equal rates it is the same answer by either reading.
                //
                // Nothing is struck here for the propagated copy any more. Booked was the wrong
                // instrument for it twice over - it compares reach where it needed chain position, so
                // a Power sourced at a LATER link could suppress this one, and it looks up only the
                // plain key while a scoped booking is stored under a negative one, so a tagged Power
                // was never found at all. The payout composes the two halves instead, where the
                // accumulated figure is already correct by position. See the call site.
                // Per class, the slot's share of the waves times what the rune lifts that class by; with no table to
                // read it from, the worth is the lift on the one class there was before classes.
                var number = NumberOfEffect(env, id);

                if (env.Amplified is { } amplified && number >= 0 && number < amplified.LiftsOfEffect.Length &&
                    amplified.LiftsOfEffect[number] is { } lifts)
                {
                    for (var a = 0; a < lift.Length && a < lifts.Length; a++)
                        lift[a] = Math.Max(lift[a], lifts[a] * (worth / 100d));
                }
                else
                {
                    lift[0] = Math.Max(lift[0], worth / 100d);
                }

                continue;
            }

            if (worth <= 0f)
                continue;

            // **Runes do not stack, and an ordinary slot is no exception.** If something earlier in
            // the chain is already propagating this rune into these waves, the copy sitting in this
            // remnant's own socket adds nothing at all. The propagating half has enforced that from
            // the start - see Book - and this is the same rule for the half that stays put.
            //
            // Skipped here rather than corrected afterwards. It used to be paid in full and taken
            // off again in a post pass, which worked while the payout was a sum and does not now: a
            // flat subtraction cannot undo a term that went inside a product.
            if (IsRuneBookedUpstream(env, id, mineAt, distinct))
                continue;

            own[GroupIndexOfEffect(env, id)] += worth / 100f;
            any = true;
        }

        return any ? own : null;
    }

    /// <summary>
    /// The scratch random used to scatter spare explosives. Per thread, since a solve runs off the
    /// main one and two of them must not share a sequence.
    /// </summary>
    [ThreadStatic]
    private static Random _dice;

    /// <summary>
    /// Somewhere legal to put an explosive that has nothing to catch.
    ///
    /// **Two goes at this were both wrong in the same way: they were tidy.** Taking the first legal
    /// spot on a ring walked the spare bombs outward in a line twenty one apart; taking the one
    /// nearest the route's end gathered them into an arc around whatever the route finished on. Both
    /// read as a pattern drawn on purpose, because a deterministic rule over a regular lattice is
    /// exactly that.
    ///
    /// So they are scattered instead. Random angle, random distance out to a few spacings from where
    /// the route ends, and one of the legal samples taken at random - which puts them on open ground
    /// near where the player is standing when the spare ones go down, without arranging them.
    ///
    /// The ring scan stays underneath as the fallback. Scattering can come up empty on tight ground
    /// and an explosive that cannot be placed stops the expedition starting, so the tidy answer is
    /// still better than no answer.
    /// </summary>
    private static Vector2 Anywhere(PlanEnvironment env, List<Vector2> chain, Vector2 from,
        Vector2 focus)
    {
        var apart = MathF.Max(1f, env.Apart);
        var about = focus == Vector2.Zero ? from : focus;
        var spread = MathF.Min(env.Reach, apart * 6f);

        _dice ??= new Random(20260915);

        var found = new List<Vector2>();

        for (var tries = 0; tries < 200 && found.Count < 8; tries++)
        {
            var angle = (float)(_dice.NextDouble() * Math.Tau);
            var ring = apart + (float)_dice.NextDouble() * MathF.Max(1f, spread - apart);
            var at = about + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring;
            var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));

            if (env.CanPlace(snapped) && Reaches(env, from, snapped) &&
                Spaced(env, chain, snapped, chain.Count))
                found.Add(snapped);
        }

        if (found.Count > 0)
            return found[_dice.Next(found.Count)];

        // Nothing scattered stuck, so take whatever is legal rather than leave an explosive in hand.
        for (var ring = apart; ring <= env.Reach; ring += apart / 2f)
        {
            var steps = Math.Max(8, (int)(ring / 4f));

            for (var step = 0; step < steps; step++)
            {
                var angle = step * MathF.Tau / steps;
                var at = from + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring;
                var snapped = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));

                if (env.CanPlace(snapped) && Reaches(env, from, snapped) &&
                    Spaced(env, chain, snapped, chain.Count))
                    return snapped;
            }
        }

        return Vector2.Zero;
    }

    /// <summary>
    /// What a chain actually takes, counted the same way the ceiling counts it.
    ///
    /// Not Evaluate's content, which is the score and rightly includes the must-have bonus - this
    /// is the other half of a comparison and has to be measured on the same ruler as the thing it
    /// is compared against. See PlanTarget's Plain.
    /// </summary>
    internal static (double Content, int Markers) Taken(PlanEnvironment env, List<Vector2> chain)
    {
        if (env == null || chain == null || chain.Count == 0)
            return (0d, 0);

        var covers = CoverageOfEnvironment(env);
        var taken = new HashSet<int>();
        var content = 0d;

        foreach (var at in chain)
        {
            foreach (var index in covers.Of(at))
            {
                if (taken.Add(index))
                    content += WorthOfTarget(env.Targets[index]);
            }
        }

        return (content, taken.Count);
    }

    private static Coverage CoverageOfEnvironment(PlanEnvironment env)
    {
        // Kept across a copy of the environment that changes nothing the index reads - the markers, the blast and
        // the reach. The openings' rollouts each pass a copy with fewer explosives, and rebuilding on reference alone
        // threw the worker's index away on every one of them.
        if (!ReferenceEquals(_covered, env) &&
            !(_covered != null && ReferenceEquals(_covered.Targets, env.Targets) &&
              _covered.Blast == env.Blast && _covered.Reach == env.Reach))
        {
            _covered = env;
            _covers = new Coverage(env);
        }

        return _covers;
    }

    /// <param name="defer">
    /// Whether a monster that a later link could catch after a rune should be priced down here.
    ///
    /// **A rare is worth the same to greedy wherever it sits in the chain, and to the real objective
    /// it is not.** A rune pays over everything unearthed AFTER its remnant, so the identical rare
    /// caught before the remnants earns nothing from them and caught after earns the uplift - and
    /// the constructive side could not see the difference, because Rough is about what a REMNANT
    /// carries and returns nought for a monster. Greedy therefore swept up a cluster of rares on the
    /// first link and left the remnants to buff whatever happened to be near them later, which is
    /// the arrangement a player fixes by eye: take one rare fewer at the start, and let the remnants
    /// pick that one up instead.
    ///
    /// Only where the swap is actually available, which is the delicate half. Deferring a marker
    /// that nothing later can reach does not move it down the chain, it loses it - so the price is
    /// only discounted when some remnant still to be taken is close enough that a link following it
    /// could catch this marker. See Waiting, which tests the necessary condition exactly.
    /// </param>
    private static double NewWeight(PlanEnvironment env, Vector2 at, HashSet<int> taken,
        int downstream, bool defer = false)
    {
        var weight = 0d;
        var covers = CoverageOfEnvironment(env);

        // **The cell's coverage list, when the spot is a whole cell.** Coverage.Of applies the same Wanted and
        // Catches filter as the loop below, in the same index order, so the sum is the same to the last bit. It is
        // keyed on the rounded cell, so a spot off the lattice is priced by the loop instead. Measured on one Grand
        // site of 408 markers (2026-09-30): the greedy build spent 118ms a chain on its own, most of it here, walking
        // every marker for every spot in reach at every step.
        if (at.X == MathF.Round(at.X) && at.Y == MathF.Round(at.Y))
        {
            foreach (var i in covers.Of(at))
                if (!taken.Contains(i))
                    weight += WorthOfCaughtTarget(env, covers, i, taken, downstream, defer);

            return weight;
        }

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (taken.Contains(i))
                continue;

            var target = env.Targets[i];

            if (!target.Wanted)
                continue;

            if (!Catches(env, at, target))
                continue;

            weight += WorthOfCaughtTarget(env, covers, i, taken, downstream, defer);
        }

        return weight;
    }

    /// <summary>What one marker a blast catches adds to NewWeight, deferral included. See NewWeight's defer.</summary>
    private static double WorthOfCaughtTarget(PlanEnvironment env, Coverage covers, int index,
        HashSet<int> taken, int downstream, bool defer)
    {
        var target = env.Targets[index];
        var worth = target.Weight + target.Rough(downstream);

        // Gaining Traction, counted off the remnants this chain has taken already - the estimate's own view of which
        // went off before this one. Its combinations' extra waves are left to the exact objective. See Settle.
        if (target.Kind == TargetKind.Remnant &&
            (env.MagicPacksPerRemnantCompleted > 0f || env.RarePacksPerRemnantCompleted > 0f))
        {
            var before = env.RemnantsCompletedInArea;

            foreach (var r in RemnantIndicesOf(env))
            {
                if (taken.Contains(r))
                    before++;
            }

            var (normalScale, magicScale, rareScale) = TractionScalesOfTarget(env, target, before);
            var waves = target.MagicAndRareWaves;

            worth += (magicScale - 1f) * waves.Magic + (rareScale - 1f) * waves.Rare +
                     (normalScale - 1f) * target.NormalWaves.Normal;
        }

        if (defer && downstream > 0 && target.Weight > 0f)
        {
            var rate = Waiting(env, covers, index, taken);

            if (rate > 0f)
            {
                // What waiting would be worth, discounted for the chance it does not happen.
                //
                // Taken now the marker is worth w; taken after the rune it is worth w(1+c), so
                // the price of taking it now is w.c/(1+c) - that is the arithmetic that makes
                // the two moments comparable. Halved, because the two outcomes are not
                // symmetrical: a deferral that works gains the uplift, and a deferral that fails
                // loses the whole marker. Where the bet is that lopsided the tie belongs to
                // taking it.
                var price = worth * (rate / (1f + rate)) * Chance;

                worth -= price;
                _deferred++;
                _deferGain += price;
            }
        }

        return worth;
    }

    /// <summary>
    /// How much uplift this marker would gain by being caught after a remnant still to be taken,
    /// or nought when no later link could catch it anyway.
    ///
    /// The condition is necessary and exact rather than a guess: a blast catching the remnant stands
    /// within a blast of it, the link after that stands within a throw of the blast, and it catches
    /// this marker only by standing within a blast of it. So the two can be in that relationship at
    /// all only if they are closer together than those three legs put end to end. Further apart than
    /// that and deferring is not a rearrangement, it is a loss.
    /// </summary>
    private static float Waiting(PlanEnvironment env, Coverage covers, int index, HashSet<int> taken)
    {
        if (env.Targets[index].Kind is not (TargetKind.Monster or TargetKind.Elite))
            return 0f;

        var best = 0f;

        // Only the remnants close enough, which is geometry and so worked out once per environment. See
        // Coverage.RemnantsWithinDeferral.
        foreach (var i in covers.RemnantsWithinDeferral(index))
        {
            if (taken.Contains(i))
                continue;

            var other = env.Targets[i];
            var carries = other.Carries;

            foreach (var choice in other.Choices ?? [])
                carries = MathF.Max(carries, choice.Carries);

            if (carries > best)
                best = carries;
        }

        return best;
    }

    /// <summary>
    /// How much of a deferral's arithmetic value to actually charge. See NewWeight's defer.
    ///
    /// A half. The upside of waiting is the uplift; the downside is the marker. Nothing here knows
    /// which will happen, and the exact objective is what finally judges the chain either way - so
    /// this only has to lean the construction in the right direction, not be right.
    /// </summary>
    private const float Chance = 0.5f;

    /// <summary>How much greedy left for later links, and how often, for the dump.</summary>
    private static int _deferred;

    private static double _deferGain;

    private static void Cover(PlanEnvironment env, Vector2 at, HashSet<int> taken)
    {
        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (!taken.Contains(i) && Catches(env, at, env.Targets[i]))
                taken.Add(i);
        }
    }

    /// <summary>The blast touching a marker is enough; it need not reach its middle.</summary>
    /// <summary>Whether candidate spots are offered on and around a target. See Candidates.</summary>
    internal static bool OffersCandidates(PlanTarget target) =>
        !target.Shunned && (target.Weight > 0f || target.Wanted || target.NonStacking is { Length: > 0 });

    internal static bool Catches(PlanEnvironment env, Vector2 at, PlanTarget target) =>
        Vector2.DistanceSquared(at, target.Grid) <=
        (env.Blast + target.Radius) * (env.Blast + target.Radius) &&
        !Missed.Here.Misses(at, target.Grid);

    /// <summary>
    /// Turns a chain into a plan, recording what each link is for.
    ///
    /// The per-link content is worked out the same way the score was - first link to catch a piece
    /// of content is the one credited with it - so the plan says exactly what the search was paid
    /// for, and placement can hold the game to that rather than to a recomputation of it.
    /// </summary>
    private static void Tally() =>
        Operators = $"priced {_deferred} catches down for later links, by {_deferGain:N1} across " +
                    "every candidate weighed rather than every one chosen; " +
                    $"sweep took {_sweeps} moves worth {_sweepGain:N1}, " +
                    $"reorder took {_orders} swaps worth {_orderGain:N1}, " +
                    $"reverse took {_reversals} stretches worth {_reverseGain:N1}, " +
                    $"relocate took {_shifts} moves worth {_shiftGain:N1}";

    /// <summary>
    /// Fills a chain out to the explosives available, greedily, from the site's own spots.
    ///
    /// **An explosive in hand is a link that was never used.** A short chain can happen several ways
    /// - a search running out of time mid-pass, a greedy construction with no legal next step, a
    /// horizon that never got a second look - and none of them mean the chain is finished. Observed
    /// on one site: two strategies handed back three link chains where five explosives were
    /// available, and they still outscored the five link answers, which means the real best was
    /// higher than anything that ran.
    ///
    /// Greedy is the right tool here and not a compromise: the links already chosen are fixed, so
    /// the question is only which single spot adds most, and that is exactly what greedy answers.
    /// </summary>
    internal static List<Vector2> Complete(PlanEnvironment env, List<Vector2> chain,
        List<Vector2> candidates = null)
    {
        if (env == null || chain == null || chain.Count >= env.Explosives)
            return chain;

        var full = Copied(chain);

        // Worked out afresh only when the caller has none. Inside the search it does - and rebuilding
        // the candidate set per restart, a couple of hundred times a solve, costs more than the
        // filling it is there to do.
        candidates ??= Candidates(env, out _, out _);

        // The last link that was placed for a reason, set once the first valueless one turns up.
        var ended = Vector2.Zero;

        while (full.Count < env.Explosives)
        {
            var from = full.Count == 0 ? env.Origin : full[^1];
            var pick = Vector2.Zero;

            // What the chain is worth before this link, so a link that adds nothing can be told
            // apart from one that adds a little. They want completely different treatment.
            var most = Score(env, full);
            var gained = false;

            foreach (var spot in candidates)
            {
                if (full.Contains(spot) || !Reaches(env, from, spot) ||
                    !Spaced(env, full, spot, full.Count))
                    continue;

                full.Add(spot);

                var worth = Score(env, full);

                full.RemoveAt(full.Count - 1);

                if (worth <= most)
                    continue;

                most = worth;
                pick = spot;
                gained = true;
            }

            // A link that catches nothing does not belong on the content's geometry.
            //
            // **Spare explosives were landing in neat arcs around markers they were not catching.**
            // Every candidate is content-derived - a marker's own cell, a ring at catch distance
            // round one the terrain refuses, the intersection of two catch circles - so a filler
            // picked from that list snaps to the same lattice the real links use and the chain comes
            // out looking drawn with a compass. Twenty explosives on a site needing nine is most of
            // a chain arranged for no reason.
            //
            // So a link that gains nothing is put on open ground beside the chain's end instead,
            // which is where the player is standing by the time the spare ones go down.
            if (!gained)
            {
                pick = Vector2.Zero;

                // Latched at the first valueless link, not before it: the completion may still have
                // worthwhile links to add, and the route's end is where the LAST of those lands.
                if (ended == Vector2.Zero)
                    ended = from;
            }

            // Nothing left that is worth anything, and the explosives still have to go down.
            //
            // **The expedition does not start until every explosive is placed.** So a chain that
            // has taken all the content is not a finished chain - it is a finished chain plus
            // however many bombs are still in hand, and those have to go somewhere legal or the
            // player places them by hand. Measured on a Heath site: twenty explosives, nine things
            // worth catching, and a plan of nineteen links that took a hundred per cent of the site
            // and still could not be started.
            //
            // Open ground first, and the candidate list only if the ground refuses. **That order is
            // the whole fix** - it was the other way round, so a filler took the nearest content
            // candidate and Anywhere almost never ran, which is why the spare explosives kept
            // arriving in an arc around a henge however Anywhere itself was written.
            if (pick == Vector2.Zero)
                pick = Anywhere(env, full, from, ended);

            // The ground had nothing legal, so take a content spot rather than leave an explosive
            // in hand. The nearest one, because a filler link is pure travel: it catches nothing,
            // and the only thing it can do badly is strand the links after it.
            if (pick == Vector2.Zero)
            {
                var nearest = float.MaxValue;

                foreach (var spot in candidates)
                {
                    if (full.Contains(spot) || !Reaches(env, from, spot) ||
                        !Spaced(env, full, spot, full.Count))
                        continue;

                    var gap = Vector2.DistanceSquared(from, spot);

                    if (gap >= nearest)
                        continue;

                    nearest = gap;
                    pick = spot;
                }
            }

            if (pick == Vector2.Zero)
                break;

            full.Add(pick);
        }

        return full;
    }

    internal static Plan Describe(PlanEnvironment env, List<Vector2> chain)
    {
        var verdict = Evaluate(env, chain);

        BoundAudited(env, verdict.Total);
        var catches = new List<Vector2[]>(chain.Count);
        var taken = new HashSet<int>();

        foreach (var point in chain)
        {
            var here = new List<Vector2>();

            for (var i = 0; i < env.Targets.Count; i++)
            {
                if (taken.Contains(i))
                    continue;

                var target = env.Targets[i];

                if (!target.Wanted)
                    continue;

                if (!Catches(env, point, target))
                    continue;

                taken.Add(i);
                here.Add(target.Grid);
            }

            catches.Add(here.ToArray());
        }

        // **A plan that drops a requirement says so.** It is still the plan - the best chain there
        // is, is worth seeing even when nothing can take everything - but it must not look like a
        // considered answer, which is exactly what it looked like on the site that prompted this.
        // A marker taken last that the chain catches before its final link counts as missed (see Settle), and is said
        // apart: it was reached, just not at the end.
        var notLast = env.TakenLast >= 0 && verdict.Missed > 0 && taken.Contains(env.TakenLast) &&
                      verdict.Missed > Enumerable.Range(0, env.Targets.Count).Count(i => env.Targets[i].Must && !taken.Contains(i));
        var unreached = verdict.Missed - (notLast ? 1 : 0);

        var note = verdict.Missed > 0
            ? "INVALID: " + string.Join("; ", new[]
            {
                unreached > 0
                    ? $"{unreached} of {env.Musts} must-take marker{(env.Musts == 1 ? "" : "s")} cannot be reached by any chain found"
                    : null,
                notLast ? "the marker taken last cannot be caught by the final explosive of any chain found" : null,
            }.Where(x => x != null))
            : "";

        return new Plan(chain, verdict.Total, verdict.Covered, note, catches, verdict.Plain,
            verdict.Missed);
    }
}
