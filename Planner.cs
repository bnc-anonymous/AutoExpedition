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
    string[] Recipes = null)
{
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
        foreach (var choice in Choices)
        {
            var reach = 0f;

            foreach (var (_, _, percent, _) in choice.Spread ?? [])
                reach += percent;

            most = MathF.Max(most,
                choice.Reward + (choice.Carries + reach) * downstream + choice.Local);
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
        PlanEnvironment env, float[][] sums, int at, int distinct, out int chose)
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

            foreach (var (_, tag, percent, _) in choice.Spread ?? [])
            {
                if (percent > 0f)
                    reach += percent * Planner.Reach(env, tag, sums, null, at, downstream);
            }

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

            var held = Planner.DiscountedForDuplicates(env, this, choice.Locals, choice.Local, at, distinct);

            var worth = choice.Reward +
                        (float)((carried * downstream + held * local + reach) / 100d);

            // **What each option was ranked at, kept only when somebody is reading.** The winner
            // was published and the comparison thrown away, so a remnant taking an option worth
            // less than the one it would have been pinned to - measured at 1,653 less over one
            // chain - said nothing about why. These are the terms the choice was made on, which is
            // the only place that answer can come from.
            Planner.ChoiceRankingTrail?.Add(
                $"{(Named != null && c < Named.Length ? Named[c] : "?")}: reward {choice.Reward:N1}" +
                $" + carried {carried:N2}% x {downstream:N0} + local {held:N2}% x {local:N0}" +
                $" + scoped {reach:N1} = {worth:N1}");

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
/// How many spots to build per content family when seeding, and how to pick them.
///
/// Carried on the environment rather than read from settings inside the search, because the search
/// runs on a background thread and the settings window is on another - a slider dragged mid-solve
/// would otherwise change the question halfway through answering it.
/// </summary>
/// <param name="Edges">
/// Whether the search may use only the bands' edges, and nothing else on the site.
/// </param>
/// <param name="PerBand">
/// How many cells of each band the search may use, beyond the ones it picks for a reason.
/// </param>
internal sealed record SeedFamilies(int Pairs, int Rares, int Remnants, float Spread, float Slack,
    bool Heavy, int PerBand = 6, int Links = 1, int Branches = 4,
    int Horizon = 0);

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
    bool VaryOpenings = false,
    bool VaryOperators = true,
    string TearingMix = "",
    bool VaryReachFill = true,
    bool ShareBest = true,
    string ShareNot = "0",
    bool EstimateDetour = false,
    int ShortlistRich = 400,
    int ShortlistSpread = 200,
    float ShortlistSparse = 64f,
    double AcceptSlack = 0.02d,
    int OpeningShakes = 2,
    double RescueBelow = 0.10d,
    int RestartShakes = 0,
    int AdoptedShake = 3,

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
    /// The longest planned tail whose every ordering is tried, or nought to leave it to the operators.
    ///
    /// See Repair.Permuted for why a factorial is the right tool below this bound and hopeless above
    /// it.
    /// </summary>
    int PermuteUpTo = 0,

    /// <summary>
    /// The fewest links a bridge may spend reaching for missed content, when the tail allows.
    ///
    /// See Reaching's spend - half the tail starves this operator on a short chain, and a bridge needs
    /// a minimum number of steps whatever the chain length.
    /// </summary>
    int BridgeLinks = 3,

    /// <summary>
    /// How far a slide moves each link along the route, in grid units. Nought never slides.
    ///
    /// See Repair.Slid - a compound move whose individual steps are not worth making, so it has to be
    /// offered as one move or it is never made at all.
    /// </summary>
    float SlideBy = 0f,

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
    bool UseEnumeratedSolve = true)
{
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

    /// <param name="found">
    /// Called with a copy of the best chain every time a better one turns up, for a readout that
    /// wants to show the number climbing.
    ///
    /// A copy, because the search goes on to mutate its working chain in place - publishing the
    /// live one would hand a reader something that changes under it from another thread. Whoever is
    /// called here is on the search's thread, so they must do nothing but store it.
    /// </param>
    /// <param name="settle">
    /// How long to keep going after the last improvement, or zero to use the whole budget.
    ///
    /// The better stopping rule for a search made of restarts: how long it needs depends on how
    /// much there is to try, so "stop when nothing has improved for a while" spends time in
    /// proportion to whether time is doing any good. The budget stays on as a ceiling either way,
    /// because a rule about improvements is a rule that cannot promise to terminate.
    /// </param>
    /// <param name="margin">
    /// How much the score has to have gained since the settle clock last restarted before it
    /// restarts again. Zero means any improvement counts.
    ///
    /// Measured from the last restart rather than from the last improvement on purpose, so a run of
    /// small gains that add up keeps the search alive. A search grinding out a point at a time
    /// towards something real should not be cut off for taking small steps.
    /// </param>
    /// <param name="seed">
    /// A chain from an earlier solve of the same site, or null. Taken as the starting incumbent when
    /// it is still legal, so pressing the key again never begins below where it left off.
    /// </param>
    public static Plan Search(PlanEnvironment env, TimeSpan budget, TimeSpan settle, double margin,
        CancellationToken token, Action<List<Vector2>> found = null, List<Vector2> seed = null)
    {
        if (env.Explosives <= 0 || env.Targets.Count == 0)
            return Plan.Empty;

        // The whole search, so the phases inside it can be read against a total rather than against
        // each other. What the named phases do not account for is the figure that matters, which is
        // the same reason the frame table times its own entry points. See Phases.
        using var wholeSearch = new Phase(PhaseWhole);

        var candidates = Candidates(env, out var offered, out _);

        Positions = candidates.Count;

        // Narrowed to the places worth catching something at, when asked.
        //
        // **The wager is that a chain worth having passes through one of the best few spots for
        // some remnant or some rare.** Those are the pieces of content heavy enough to build a link
        // around, they are going to be caught by something, and where a blast catches one it may as
        // well be a blast that catches the most beside it.
        //
        // If that holds, it is worth a great deal: the site offers something like fourteen hundred
        // positions and the union of the best few per piece of content is a few dozen, so the same
        // budget searches a space twenty times smaller and does it properly rather than sampling.
        //
        // If it does not hold, the search will say so by scoring worse - which is why this is a
        // switch and not a decision. The likely failure is the bridging link: a spot that catches
        // almost nothing but joins two rich areas, which by construction is in no top few.
        if (candidates.Count == 0)
        {
            return Plan.Empty with
            {
                Note = offered == 0
                    ? "no content to place on"
                    : $"the terrain check refused all {offered} candidate spots",
            };
        }

        // The bands, once per solve. See Narrowed.
        List<(string Name, List<Vector2> Cells, List<Vector2> All)> bands = null;

        if (env.Seeding != null)
        {
            var seeding = env.Seeding;

            bands = Regions(env, candidates, seeding.Pairs, seeding.Rares, seeding.Slack,
                seeding.Heavy);

            Searched = bands;
        }

        _sweeps = 0;
        _sweepGain = 0d;
        _deferred = 0;
        _deferGain = 0d;
        _orders = 0;
        _orderGain = 0d;
        _reversals = 0;
        _reverseGain = 0d;
        _shifts = 0;
        _shiftGain = 0d;
        _fetches = 0;
        _fetchGain = 0d;
        _asPlaced = 0;
        _asPlacedBest = 0d;
        _asPlacedChain = null;
        _asPlacedSlipped = 0;
        _endgameBest = 0d;
        _endgameSlipped = 0;

        // **A fixed seed made every re-solve retry the same paths.** The restart operator draws its
        // openings from this stream, so a second press on an unchanged site walked the identical
        // sequence of random chains and could only find something new by accident - the router
        // having warmed up, or a round or two more fitting inside the window. Pressing again read
        // as "search harder" and mostly was not.
        //
        // Counted rather than clocked, so a solve is still reproducible: run number three of a site
        // is the same run number three every time, and the number is printed in the dump.
        Stream = Interlocked.Increment(ref _streams);

        var random = new Random(20260912 + Stream);

        // How much of the window the restart loop actually got through, for the dump: rounds beside
        // improvements is what says whether a re-solve explored or idled.
        Rounds = 0;

        // The clock starts before the first pass, not after it.
        //
        // These used to be set up below, which meant the opening greedy and its polish - the two
        // longest single stretches of a cold solve - ran with no way to check the time. A window of
        // 1,500ms was routinely blown by 4,000ms of routing inside them, and the setting looked
        // ignored because nothing had yet reached the point of reading it.
        var deadline = DateTime.UtcNow + budget;

        // **The deterministic openings had the whole window, and on a Grand site they took it.**
        //
        // Five dumps in a row reported nought restart rounds - which is to say the restart loop, and
        // with it Insisting, Chasing, the sweep and the endgame, did not execute once in a solve of
        // twenty one seconds. Everything before it is a fixed list of openings, each one polished by
        // Improve, and Improve on fifteen links against fourteen hundred candidates is seconds. The
        // list simply ran until the clock did.
        //
        // So the openings get a share and the loop gets the rest, always. A worse opening that is
        // then searched beats a better opening that is not: the loop is where the chain gets pulled
        // onto content it walked past, and none of that has ever run here.
        //
        // Measured against the improvement window rather than the ceiling, because the window is the
        // number the player set and the ceiling is usually unbounded.
        var opens = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(300d,
            (settle > TimeSpan.Zero ? settle : budget).TotalMilliseconds * Sown));

        var improved = DateTime.UtcNow;
        var marked = 0d;

        // Empty first, because Waiting() reads it: on a cold site a flood only counts as progress
        // while there is no chain, and the opening pass is exactly when that matters.
        var best = new List<Vector2>();
        var score = 0d;

        // The bands first, before anything else has spent the router's allowance.
        //
        // **Order matters here for a reason that has nothing to do with search quality.** The router
        // may have spent a share of the time elapsed so far, so at the start of a solve almost
        // nothing has accrued - and whatever runs first gets it. The opening greedy and its polish
        // sweep a thousand candidates and take the lot, so the enumeration, running afterwards, had
        // every one of its forty odd floods refused: it walked four thousand chains of a space
        // measured at half a million, in seventy milliseconds of a twelve hundred millisecond
        // allowance, and reported that as its answer.
        //
        // The enumeration needs one flood per band edge and nothing else, which fits inside the
        // opening allowance with room to spare. Greedy needs no ground at all to produce something -
        // it only needs it to produce something good - so it loses nothing by going second.
        // A quarter of the window, not a fixed 350ms.
        //
        // **Measured on one site: the whole space of chains over the band edges is about half a
        // million, and a scoring costs three microseconds - so all of it is a second and a half, and
        // the first four links of it are a fifth of a second.** The old slice was set when a narrow
        // pass meant twenty seven spots and it saw under one percent of that. Scaled to the window,
        // a four second solve spends a second here and covers most of the space; a short solve still
        // gets a proportionate look rather than none.
        var enumerating = TimeSpan.FromMilliseconds(Math.Clamp(budget.TotalMilliseconds * 0.25d,
            200d, 1200d));

        foreach (var chain in Narrowed(env, candidates, Waiting, null, bands, enumerating))
        {
            if (Score(env, chain) > score + 0.0001d)
                Keep(chain);

            if (!Waiting())
                break;
        }


        // Reported whether or not it wins, because "it did not fire" and "it fired and was beaten"
        // are different facts and the readout could not tell them apart.
        EnumeratedSolveOutcome = "not attempted - too many kinds of content";

        // Tried again as the ground is learned, not once at the start.
        //
        // **It asks whether a chain is legal, and at the start nothing knows.** The router answers
        // from flooded ground and floods cost time it has not accrued yet, so the first call gets
        // refusals - which read as "no legal chain in this order" and made the whole thing give up.
        // Seen plainly: the same site solved outright on a warm router and fell back to searching
        // on a cold one, for a hundred and twenty points.
        //
        // So it is retried while the search warms the ground for it. Cheap enough to repeat - six
        // orders over a handful of candidates - and it stops asking the moment it succeeds.
        void Outright()
        {
            if (Planner.TryEnumeratedSolve(env, score) is { Count: > 0 } found)
                Keep(found);
        }

        // The sparse case, answered before anything else is attempted.
        //
        // **Ahead of the seeding rather than after it**, for two reasons. A greedy seed publishes
        // itself the moment it exists, so running this second meant the player watched a worse
        // chain appear and be replaced a frame later - and on a site this answers outright, every
        // second of seeding, anchoring and restarting is spent improving on an answer that is
        // already provably the best there is.
        var perfect = RelaxedCeiling(env);

        StoppedAtRelaxedCeiling = false;

        Outright();

        if (perfect > 0d && score >= perfect - 0.0001d)
        {
            StoppedAtRelaxedCeiling = true;

            return Describe(env, best);
        }

        // Drawn before it is polished, not after.
        //
        // A greedy chain is available in milliseconds and the polish that follows it can take
        // seconds on a cold site - and nothing was published until the polish finished, so the
        // plugin sat blank through most of the window with a perfectly good chain in hand. The
        // first answer is shown as soon as it exists and improves under the player rather than
        // appearing at the end.
        var quick = Greedy(env, candidates, null, 1);

        if (best.Count == 0)
            found?.Invoke(Copied(quick));

        quick = Improve(env, candidates, quick, Waiting);

        if (Score(env, quick) > score + 0.0001d || best.Count == 0)
            Keep(quick);

        // Whatever the player insisted on, fetched before anything else is tried.
        //
        // **Ahead of the seeds, the anchors, the compass and the restart loop, because it is the
        // only one of them that can satisfy a requirement.** It used to run last, inside the restart
        // rounds, and on a Grand site those rounds never began: eight phases of openings and local
        // search spent the window first, and the dump that prompted this recorded nought rounds
        // completed with two of three marked remnants dropped. A rule that only applies when there
        // is time left over is not a rule.
        foreach (var opening in Demanded(env, candidates))
        {
            if (!Waiting())
                break;

            var chain = Improve(env, candidates, opening, Waiting);

            if (Score(env, chain) > score + 0.0001d || best.Count == 0)
                Keep(chain);
        }

        // The answer from last time, if there was one and it is still legal.
        //
        // Only the routing is remembered between presses; the search itself starts from a greedy
        // chain every time and has to rediscover what it already knew, which is why the score climbs
        // from a low number on every re-solve. A previous chain is a perfectly good starting
        // incumbent - it was legal a moment ago and nothing about the site has changed - so taking
        // it means pressing again can only improve on where it left off.
        //
        // Checked rather than trusted: explosives may have gone down since, which moves the origin
        // and can strand a link that was reachable before.
        if (seed is { Count: > 0 } && Walkable(env, seed))
        {
            var known = Improve(env, candidates, Copied(seed), Waiting);

            if (Score(env, known) > score)
            {
                best = known;
                score = Score(env, best);
            }
        }
        improved = DateTime.UtcNow;
        marked = score;

        found?.Invoke(Copied(best));

        if (best.Count == 0)
        {
            var nearest = float.MaxValue;

            foreach (var candidate in candidates)
                nearest = MathF.Min(nearest, Vector2.Distance(env.Origin, candidate));

            return Plan.Empty with
            {
                Note = nearest > env.Reach
                    ? $"nothing within reach: the nearest spot worth using is {nearest:0} grid from the chain origin and the reach is {env.Reach:0}"
                    : "nothing reachable is worth placing on",
            };
        }

        // The same move the restart rounds open with, made once before they do.
        //
        // Cheap - it is one relocate per missed requirement - and it is the difference between a
        // chain that is one link away from legal and one that stays invalid because the window ran
        // out before round one. See Insisting.
        if (best.Count > 0 && env.Musts > 0)
        {
            var fetched = Insisting(env, candidates, best);

            if (fetched != null && Score(env, fetched) > score + 0.0001d)
                Keep(Improve(env, candidates, fetched, Waiting));
        }

        // Running out of ideas ends it before the ceiling does.
        //
        // Learning the ground counts as an idea. Reachability is worked out lazily, so on a site
        // nobody has solved before the first minutes of a search are spent discovering which links
        // are legal at all - and a chain that uses newly opened ground cannot be found before the
        // **The improvement window measures improvement, so it does not start until there is
        // something to improve.**
        //
        // The opening is allowed half the window by design - see Sown - and it publishes nothing until
        // it has a complete chain, so on a site where it uses that half the clock was already half
        // gone the first time a score existed. Measured on a Grand site with an eight second window:
        // four seconds of opening, one chain at the end of it, and the score appearing with two
        // seconds left.
        //
        // This was hidden rather than handled before. The router reset the clock on every flood while
        // no chain existed, which had the same effect for a different reason - and removing the router
        // removed the disguise along with it. Saying it directly is better: the setting is called time
        // to improve, and time spent before the first chain is not that.
        //
        // The overall deadline still bounds the solve, so this cannot run away.
        bool Waiting()
        {
            if (best.Count == 0)
                improved = DateTime.UtcNow;

            return DateTime.UtcNow < deadline && !token.IsCancellationRequested &&
                   (settle <= TimeSpan.Zero || DateTime.UtcNow - improved < settle);
        }

        // The openings' own clock: the search's rule, plus a ceiling of its own. See Sown.
        bool Early() => DateTime.UtcNow < opens && Waiting();

        // A better chain is always kept. Whether it restarts the clock is a separate question, and
        // the answer is "once the gains since the last restart are worth having".
        void Keep(List<Vector2> chain)
        {
            best = chain;
            score = Score(env, best);

            if (score - marked >= margin)
            {
                marked = score;
                improved = DateTime.UtcNow;
            }

            found?.Invoke(Copied(best));
        }

        // Chains built to the shape that keeps winning, before anything is left to chance.
        //
        // **Every strong chain measured on a real dig site has the same structure: the remnants in
        // the opening links, and the last links spent digging up monsters for them.** A rune pays
        // over everything unearthed after its remnant, so a remnant taken first is worth several
        // times one taken last - and the best and worst chains in a table of six differed by ten per
        // cent on coverage and by ordering on everything else.
        //
        // Greedy cannot find that shape on purpose. It picks by what a spot adds right now, and what
        // a remnant adds depends on what comes after it, which has not been decided yet. So it
        // arrives at these chains by luck, and on a site with three remnants it found the good
        // arrangement about one solve in three.
        //
        // These are built the other way round: pick an order for the remnants, commit to reaching
        // them in that order, and let greedy fill what is left with the monsters that pay for them.
        // A handful of chains, deterministic, and each one is the answer to "what if the rune came
        // first" for a different rune.
        foreach (var opening in Remnants(env, candidates))
        {
            if (!Early())
                break;

            var chain = Improve(env, candidates, opening, Early);

            if (Score(env, chain) > score + 0.0001d)
                Keep(chain);
        }

        // The whole search again over a couple of dozen spots, then polished on the real set.
        //
        // **The narrow set is not a shortlist of good places, it is a shortlist of good LINKS.** Each
        // spot in it is the best few ways of catching one remnant together with one neighbour, or of
        // catching one rare - so a chain built out of them is a chain of links that each do a job the
        // site actually requires, which is the property the strongest recorded chains share and the
        // one greedy has no way to aim at.
        //
        // Measured against six chains recorded on one site, twenty seven such spots held eighteen of
        // the thirty links exactly and twenty three within ten grid, out of one thousand and eighty
        // two candidates. The best chain of the six had four of its five links on the set.
        //
        // Seeds rather than a restriction, and the same measurement says why: three of the thirty
        // links sat more than twenty grid from anything in the set. Searching only these would put
        // those chains permanently out of reach and give no sign it had happened. So the narrow
        // search runs first because it is nearly free - a greedy pass over twenty seven candidates
        // against one over a thousand - and whatever it finds is handed to the full search as a
        // starting point rather than as an answer.
        // And the same set used the way a player uses it: one link, then look again.
        var rolled = Rolling(env, candidates, Early);

        if (rolled.Count > 0)
        {
            var polished = Improve(env, candidates, rolled, Early);

            RolledBest = Score(env, polished);

            if (RolledBest > score + 0.0001d)
                Keep(polished);
        }

        // The richest spots on the site, each tried as the thing the chain is built around.
        //
        // A different question from the one the remnant seeds ask. Those commit to an ORDER for the
        // content that carries runes; these commit to a PLACE - the handful of positions worth the
        // most on their own - and ask what the best chain through each of them looks like. On a site
        // where one blast is worth two hundred and forty and the next best is half that, a chain
        // that misses it is almost certainly wrong, and greedy only finds it if its own first step
        // happens to lead there.
        //
        // Compass already forces an opening, but it picks the best in each of eight directions,
        // which is a question about spread rather than about value: the two richest spots on a site
        // are often in the same sector and only one of them is ever tried.
        foreach (var anchor in Anchors(env, candidates))
        {
            if (!Early())
                break;

            var chain = Improve(env, candidates, Greedy(env, candidates, null, 1, anchor), Early, 1);

            if (Score(env, chain) > score + 0.0001d)
                Keep(chain);
        }

        // Every direction out of the detonator, tried deliberately rather than hoped for.
        //
        // The best first link in each compass sector, each one then finished greedily and given the
        // same local search. Eight restarts, deterministic, and between them they commit the chain
        // to every way it could set off - which is the one thing a local search cannot discover for
        // itself and the thing a hand-laid chain beat it by seventy eight points on.
        foreach (var opening in Compass(env, candidates))
        {
            if (!Early())
                break;

            var chain = Improve(env, candidates, Greedy(env, candidates, null, 1, opening), Early, 1);

            if (Score(env, chain) > score + 0.0001d)
                Keep(chain);
        }

        // The openings the restarts draw from, worked out once.
        //
        // **Uniformly over every candidate is the same as not choosing at all.** A four second run
        // gets through a few hundred restarts and there are a thousand candidates, so each opening
        // is tried about once - and one greedy pass from an opening says almost nothing about what
        // that opening is worth, because the four links after it were themselves chosen greedily.
        // Measured against the player doing it by hand: placing the first explosive and solving the
        // remaining four reaches 1,316 where the free search settles at 1,295, and the difference is
        // not that the problem is smaller - the search reaches the same opening - it is that all
        // four seconds went on that one opening instead of a thousandth of them.
        //
        // So the restarts draw from the family spots, which are the couple of dozen places a link
        // does two jobs at once, plus the spread of rich openings the anchors already found. Each
        // one then gets tried dozens of times over a run rather than once, with different greedy
        // choices behind it each time.
        //
        // Everything is kept as the fallback, so a site with no families - or one where the pool
        // turns out to be a trap - still gets the search it had before.
        var openings = new List<Vector2>();

        if (env.Seeding != null)
        {
            var (pairs, rares, remnants, spread, slack, heavy, _, _, _, _) = env.Seeding;

            foreach (var (at, _, _) in Families(env, candidates, pairs, rares, remnants, spread,
                         slack, heavy))
                if (Reaches(env, env.Origin, at))
                    openings.Add(at);
        }

        foreach (var anchor in Anchors(env, candidates))
            if (!openings.Contains(anchor))
                openings.Add(anchor);

        if (openings.Count == 0)
            openings = candidates;

        // Everything, or only the edges.
        //
        // **A small space can be searched properly and a large one cannot.** The full candidate set
        // is a thousand places and four seconds buys a few hundred chains through it; the bands'
        // edges are a few dozen places chosen precisely because they are where a link both catches
        // what it should and leans as far as it can towards the next one. Restricted to those, the
        // same window covers most of the space rather than sampling it.
        //
        Openings = openings.Count;

        // What the openings cost against what they were allowed, because "nought restart rounds"
        // took five dumps to notice and this is the number that would have said it at a glance.
        var spare = (opens - DateTime.UtcNow).TotalMilliseconds;

        Sowing = spare <= 0d
            ? "spent their whole share and were cut off"
            : $"finished with {spare:N0}ms of their share left";

        // The best any opening has been shown to be worth, and how many have been tried at all.
        //
        // **Sixteen openings sharing four seconds is sixteen shallow answers.** Solving the
        // remainder of an opening properly is the expensive part, and spreading it evenly means
        // none of them gets solved properly - which is exactly the gap against a player who places
        // the first explosive and gives the remaining four links the whole window. Measured: the
        // free search settles at 1,298 however it poses the problem, and the same site with one
        // explosive down reaches 1,319 in seconds.
        //
        // So the budget is spent in two phases. Every opening is tried once, which is cheap and
        // says roughly what each is worth; after that the restarts only draw from the best few, and
        // those get the rest of the window between them. A shallow answer is enough to tell a
        // hopeless opening from a promising one - it is not enough to tell the best chain from the
        // second best, which is why the concentration comes afterwards rather than instead.
        var worthOf = new double[openings.Count];
        var tried = 0;

        for (var i = 0; i < worthOf.Length; i++)
            worthOf[i] = double.NegativeInfinity;

        int Which()
        {
            if (tried < openings.Count)
                return tried++;

            // Among the best few, at random, so each gets repeated attempts with different greedy
            // choices behind it rather than the same chain over and over.
            var order = new List<int>();

            for (var i = 0; i < worthOf.Length; i++)
                order.Add(i);

            order.Sort((a, b) => worthOf[b].CompareTo(worthOf[a]));

            return order[random.Next(Math.Min(Deeply, order.Count))];
        }

        // How much of the chain the endgame has committed, and which window it last did it in.
        var pins = 0;
        var stage = DateTime.MinValue;

        Committed = 0;

        // Start again from somewhere else, repeatedly, and keep the best answer.
        //
        // This replaces a loop that nudged the single best chain it had - which cannot work, and
        // the reason is worth stating because it is the whole shape of the problem. The chain is a
        // PATH out of the detonator, so the first link decides which way the whole thing goes. Any
        // search that only ever changes one link at a time is trapped in whichever direction its
        // first answer happened to take: reversing a chain means every link moving at once, and
        // every partial step of that is illegal or far worse than where it started.
        //
        // Measured against a chain laid by hand, that trap was worth seventy eight points - the
        // hand-laid chain simply set off the other way round the dig site. No amount of local
        // polishing finds that. Starting over from a different first link does.
        //
        // The restarts are greedy with a bit of randomness in the choice - the best few candidates
        // at each step rather than always the single best - so each one commits to a different
        // direction, and then gets the same local search the first answer got.
        // What this site could pay at the very most, so a search that has it can stop.
        //
        // See RelaxedCeiling. Nought means there is nothing worth bounding, which is every ordinary site.
        var round = 0;

        // The sparse case, answered rather than searched. See Exact.


        while (Waiting())
        {
            // Nothing left to find, provably.
            //
            // **A sparse site is solved in the first second and searched for eight more.** Nine
            // things to catch and twenty explosives has one right answer and the search reaches it
            // almost at once - then spends the rest of the window re-rolling openings that cannot
            // beat a total nothing can beat. Stopping needs no cleverness, only a number that
            // cannot be exceeded and a comparison against it.
            if (perfect > 0d && score >= perfect - 0.0001d)
            {
                StoppedAtRelaxedCeiling = true;

                break;
            }

            round++;
            Rounds = round;

            // Asked again only while the answer was "not yet" - a site that is genuinely too
            // complicated, or genuinely has no legal order, is not going to change its mind.
            if (RouterNotReady)
                Outright();

            // The generic restart, occasionally rather than every time.
            //
            // **It is the weakest thing in this loop and it was taking the largest share of it.** A
            // five link chain built greedily and polished is what the search already did before the
            // window opened; measured against it, treating one link as placed and enumerating the
            // remaining four reaches 1,311 from cold in under half a second, where the whole five
            // link problem reaches 1,261 in four seconds. Spending most of the window re-rolling
            // the harder problem is spending it on the version of the question nothing has ever
            // answered well.
            //
            // Go and get whatever the chain has been TOLD to take and has not.
            //
            // First in the round, because everything else in it is a local move and this is not a
            // local problem - see Insisting.
            var insisted = Insisting(env, candidates, best);
            var gained = insisted == null ? 0d : Score(env, insisted);

            if (insisted != null && gained > score + 0.0001d)
            {
                _fetches++;
                _fetchGain += gained - score;

                Keep(Improve(env, candidates, insisted, Waiting));
            }

            // The same move the must-take key makes, made on the plugin's own account. Every
            // fourth round, offset from the generic restart so the two do not share a tick.
            if (round % 4 == 3)
            {
                var chased = Chasing(env, candidates, best);
                var worth = chased == null ? 0d : Score(env, chased);

                if (chased != null && worth > score + 0.0001d)
                {
                    _fetches++;
                    _fetchGain += worth - score;

                    Keep(Improve(env, candidates, chased, Waiting));
                }
            }

            // Kept at one in four rather than dropped, because it is the only operator here that
            // owes nothing to the family spots - if that set is missing the site's real answer,
            // this is what finds it anyway.
            if (round % 4 == 1)
            {
                var chain = Improve(env, candidates, Greedy(env, candidates, random, Among), Waiting);

                if (Score(env, chain) > score + 0.0001d)
                    Keep(chain);
            }

            // And the cheap version of the same idea: the chain it already has, walked the other
            // way. Legal only when the far end is within reach of the detonator, which is often
            // enough to be worth the one test it costs.
            var back = Reverse(env, best);

            if (back != null && Score(env, back) > score + 0.0001d)
                Keep(back);

            // Pin an opening nobody chose, and solve the rest around it.
            //
            // **Measured in game: place the first explosive by hand and re-solve, and the chain comes
            // out worth 1,227 where solving the whole thing from scratch found 1,157.** Fixing the
            // first link and searching four is a smaller problem than searching five, and the search
            // does markedly better at it - which says the full problem is under-explored rather than
            // that the shorter one is easier.
            //
            // Compass already forces a first link, but only the best one in each of eight sectors,
            // so a good opening that is not the pick of its sector is never tried. This tries any
            // candidate at all, which is the same move the player made by hand.
            if (openings.Count > 0)
            {
                var pick = Which();
                var opening = openings[pick];

                if (Reaches(env, env.Origin, opening))
                {
                    // EnumeratedSolveOutcome as though the first explosive were already down.
                    //
                    // **Measured, repeatedly: the same site with one explosive placed reaches 1,311
                    // from cold in under half a second, where the whole five link problem reaches
                    // 1,261 in four seconds.** Committing a link and solving the remainder properly
                    // is not a shortcut, it is a smaller problem - a dozen times fewer arrangements,
                    // a fifth of the routing - and it is what the player does by hand.
                    //
                    // Enumerated against the real environment with the opening held, so the chain is
                    // scored by the objective it will finally be judged by. An earlier version built
                    // a reduced environment for this and got the propagation wrong; see Narrowed.
                    var whole = new List<Vector2>();
                    var most = double.NegativeInfinity;

                    foreach (var solved in Narrowed(env, candidates, Waiting,
                                 new List<Vector2> { opening }, bands))
                    {
                        var worthHere = Score(env, solved);

                        if (worthHere > most)
                        {
                            most = worthHere;
                            whole = solved;
                        }
                    }

                    // Without bands there is nothing to enumerate, so this falls back to what it
                    // did before they existed: greedy from the committed opening, pinned so the
                    // polish cannot move it. Restarts has to keep the operator when it is run as the
                    // baseline - dropping it silently would make the comparison a different search
                    // rather than an unaided one.
                    if (whole.Count == 0)
                    {
                        whole = Improve(env, candidates,
                            Greedy(env, candidates, random, Among, opening), Waiting, 1);
                    }


                    var worth = Walkable(env, whole) ? Score(env, whole) : double.NegativeInfinity;

                    _asPlaced++;

                    if (worth > _asPlacedBest)
                    {
                        _asPlacedBest = worth;
                        _asPlacedChain = Copied(whole);
                    }

                    if (whole.Count > 0 && whole[0] != opening)
                        _asPlacedSlipped++;

                    if (worth > worthOf[pick])
                        worthOf[pick] = worth;

                    if (worth > score + 0.0001d)
                        Keep(whole);
                }
            }

            // The best restart, filled out to its full length - once, not once each.
            //
            // **A short chain is not an answer, and on a Grand site every restart is short.** The
            // enumeration walks bands and stops when it runs out of legal band moves: on five links
            // that is usually the whole chain, on fifteen it is a third of one. Measured: 182
            // restarts, best 483.0, where fifteen explosives were available. Those stubs then lost
            // to every full length answer, so the restart path - the operator that reproduces what
            // a player does by hand, and the strongest one here - contributed nothing at all on
            // exactly the sites that need it most.
            //
            // **Filling all of them was worse than filling none.** Tried: 179 restarts each
            // completed and improved, which did make them whole - best 483.0 became best 1,493.9 -
            // and the site scored LOWER, 2,099 against 2,280. Completion is greedy over every
            // candidate for every remaining link, so it costs thousands of scorings and thousands of
            // reach questions each time; the routing starved harder (79.5% refused against 77%) and
            // the sweep, which earns an order of magnitude more, lost a fifth of its moves to pay
            // for it.
            //
            // So they are compared as they come. Restarts are all short in the same way, so ranking
            // them against each other is fair, and only the winner is made whole - one completion a
            // round instead of a hundred and seventy nine.
            if (_asPlacedChain is { Count: > 0 } && _asPlacedChain.Count < env.Explosives)
            {
                var filled = Improve(env, candidates,
                    Complete(env, Copied(_asPlacedChain), candidates), Waiting, 1);

                if (Walkable(env, filled) && Score(env, filled) is var made && made > score + 0.0001d)
                    Keep(filled);
            }

            // Keep the opening, rebuild the rest. Every fourth pass, for the same reason as above:
            // it rebuilds greedily, which the as-placed enumeration above does properly.
            //
            // **This is the move a player makes by hand and the search could not.** Sweep replaces
            // one link at a time and every replacement has to reach both its neighbours, so a
            // better chain that needs three links to move TOGETHER is unreachable from here: drop
            // the third link where it belongs and it no longer reaches the fourth, which makes
            // every step of the journey illegal even though the destination is legal and better.
            //
            // Measured, on a Sinkhole dig site: the search settled on 1,156 and a chain sharing its
            // first two links scored 1,174. Placing those two by hand and solving again found it
            // immediately - because solving again IS this operator, with the prefix fixed by the
            // explosives already down. Doing it in the search costs one greedy pass.
            //
            // The restarts above cannot substitute. They commit to a different FIRST link, so they
            // explore other directions out of the detonator; this explores other endings to the
            // direction already chosen, which is a different half of the same problem.
            if (best.Count > 1 && round % 4 == 2)
            {
                var keep = best.GetRange(0, 1 + random.Next(best.Count - 1));

                // The kept prefix is the point of this, so it is pinned for the same reason - and
                // pinning the whole prefix, not just the opening, because what is being asked is
                // what a different ENDING to this beginning is worth.
                var tail = Improve(env, candidates,
                    Greedy(env, candidates, random, Among, null, keep), Waiting, keep.Count);

                if (Score(env, tail) > score + 0.0001d)
                    Keep(tail);
            }

            // The endgame: commit the chain a link at a time as the window runs out.
            //
            // **Committing early was measured and did nothing three times over; this commits late,
            // which is a different bet.** Early on a pinned link is a guess, and the search has
            // better uses for the time than exploring the consequences of a guess. In the last
            // tenth of the window the chain is as good as this run is going to make it, and the
            // only question left is whether its opening is worth keeping - which is answered by
            // fixing that opening and rebuilding everything after it, the same move as placing an
            // explosive by hand and pressing again.
            //
            // One link per window, not one per iteration. The stage only advances after something
            // has improved since the last one, so a commitment that leads nowhere ends the solve
            // exactly as it would have ended anyway, and a commitment that pays buys a fresh window
            // in which to commit the next link. That makes the whole endgame free: it can extend a
            // run only by finding something better, which is the same rule every other operator
            // here plays by.
            //
            // The last link is never pinned. Pinning every link is not a search, it is the chain it
            // already had.
            if (settle > TimeSpan.Zero && best.Count > 2 && pins < best.Count - 1 &&
                stage != improved && DateTime.UtcNow - improved >= settle * Endgame)
            {
                stage = improved;
                pins++;

                var head = best.GetRange(0, pins);
                var rest = Improve(env, candidates,
                    Greedy(env, candidates, random, Among, null, head), Waiting, pins);

                _endgameBest = Math.Max(_endgameBest, Score(env, rest));

                // Whether the pin actually held. If a committed link is not where it was committed,
                // something downstream is moving it and the whole idea is being tested in name only.
                for (var i = 0; i < pins && i < rest.Count; i++)
                    if (rest[i] != head[i])
                        _endgameSlipped++;

                if (Score(env, rest) > score + 0.0001d)
                    Keep(rest);

                Committed = pins;
            }
        }

        return Describe(env, best);
    }

    /// <summary>
    /// The most promising first link in each direction out of the chain's origin.
    ///
    /// Sectors rather than a sample, so the openings are spread by construction: whatever the dig
    /// site looks like, there is one candidate offered per eighth of the compass, and the chain gets
    /// a chance to set off each way. Sectors with nothing reachable in them are simply absent.
    /// </summary>
    private static List<Vector2> Compass(PlanEnvironment env, List<Vector2> candidates)
    {
        const int sectors = 8;

        var bestAt = new Vector2[sectors];
        var bestGain = new double[sectors];
        var taken = new HashSet<int>();

        for (var i = 0; i < sectors; i++)
            bestGain[i] = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            var away = candidate - env.Origin;
            var far = away.Length();

            // Reaches, not distance. An opening is a FORCED first link - whatever comes back from
            // here is committed to without the greedy pass getting a say - so a sector whose best
            // candidate has a wall in front of it must offer its next best instead of offering a
            // link the game will refuse. This was the bug: three dig sites in a row planned an
            // opening through a rock, because the only test it ever faced was how far away it was.
            if (far < 0.01f || !Reaches(env, env.Origin, candidate))
                continue;

            var angle = MathF.Atan2(away.Y, away.X) + MathF.Tau;
            var sector = (int)(angle / MathF.Tau * sectors) % sectors;
            var gain = NewWeight(env, candidate, taken, env.Explosives - 1);

            if (gain > bestGain[sector])
            {
                bestGain[sector] = gain;
                bestAt[sector] = candidate;
            }
        }

        var found = new List<Vector2>();

        for (var i = 0; i < sectors; i++)
        {
            if (bestGain[i] > 0d)
                found.Add(bestAt[i]);
        }

        return found;
    }

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
    /// The richest spots on the site as of the last search, for drawing.
    ///
    /// The same figures the anchor seeds are built from, published rather than recomputed: working
    /// them out costs a pass over every candidate against every marker, which is fine once a solve
    /// and absurd once a frame. So the overlay shows what the search actually reasoned about, and
    /// shows it only after a solve - which is the honest scope anyway, since the numbers describe
    /// the site as the search understood it.
    /// </summary>
    /// <summary>
    /// How many of the best spots to draw, set by the debug button. Nought draws nothing.
    /// </summary>
    public static int Drawn { get; set; }

    /// <summary>
    /// A count the button has asked for and nobody has worked out yet, or -1 for nothing pending.
    ///
    /// The ranking needs an environment - the markers, the reach, the ground - and the settings
    /// window has none of that in scope. So the button leaves the question here and the next tick
    /// answers it, which is the same arrangement the cache button uses.
    /// </summary>
    public static int Wanted { get; set; } = -1;

    /// <summary>
    /// Which content the pending request wants the best spots around, or None for the site overall.
    /// </summary>
    public static TargetKind? PerKind { get; set; }

    /// <summary>
    /// Whether the pending request is for the paired view rather than a ranking. See Pairs.
    /// </summary>
    public static bool Paired { get; set; }

    /// <summary>
    /// Whether the pending request is for every family at once. See Families.
    /// </summary>
    public static bool Family { get; set; }

    /// <summary>Whether the pending request is for the bands themselves, drawn as shapes.</summary>
    public static bool Shaped { get; set; }

    /// <summary>
    /// The bands the last SEARCH used, as opposed to any the drawing works out for itself.
    ///
    /// **The drawing and the search were computing their own, and that made every disagreement
    /// unanswerable.** They build from separate environments - the search's knows what has already
    /// been caught and what the obstacles are at that moment - so a cell on screen might or might
    /// not have been one the search could see, and there was no way to tell which. Published here,
    /// the picture is the evidence: what is drawn is what was walked, and a chain the eye can build
    /// out of the rings is a chain the search had the parts for.
    /// </summary>
    public static IReadOnlyList<(string Name, List<Vector2> Cells, List<Vector2> All)> Searched
    {
        get;
        set;
    } = new List<(string, List<Vector2>, List<Vector2>)>();

    /// <summary>
    /// The bands as last worked out for drawing: every cell, and the corners the search uses.
    ///
    /// Kept apart from the search's own call so that watching the shapes cannot change what the
    /// search does, and so a solve running in the background cannot rewrite what is on screen
    /// halfway through a frame.
    /// </summary>
    public static IReadOnlyList<(string Name, List<Vector2> Cells, List<Vector2> All)> Shapes
    {
        get;
        private set;
    } = new List<(string, List<Vector2>, List<Vector2>)>();

    /// <summary>How many spots per family the pending request wants, as pair / rare / remnant.</summary>
    public static (int Pairs, int Rares, int Remnants) Counts { get; set; } = (3, 3, 0);

    /// <summary>The richest spread-out spots for an environment, worked out on demand.</summary>
    public static void Rank(PlanEnvironment env, int most, float spread = 0f, TargetKind? each = null,
        bool paired = false, float slack = 0f, bool heavy = true)
    {
        if (env == null || most <= 0)
        {
            Spots = new List<(Vector2, double, string)>();
            Drawn = 0;

            return;
        }

        var candidates = Candidates(env, out _, out _);

        Positions = candidates.Count;
        Drawn = most;

        if (Shaped)
        {
            var (pairs, rares, _) = Counts;

            // What the search used, when it has run. Recomputing here would answer a different
            // question than the one being asked.
            Shapes = Searched.Count > 0
                ? Searched
                : Regions(env, candidates, pairs, rares, slack, heavy);

            Spots = new List<(Vector2, double, string)>();
            Drawn = Shapes.Count;
            Fanned = Fanout(env, Shapes);

            return;
        }

        if (Family)
        {
            var (pairs, rares, remnants) = Counts;

            Spots = Families(env, candidates, pairs, rares, remnants, spread, slack, heavy);
            Drawn = Spots.Count;
            Linked = Adjacency(env, Spots);

            return;
        }

        if (paired)
        {
            Spots = Pairs(env, candidates, most, spread, slack, heavy);

            return;
        }

        if (each == null)
        {
            var flat = new List<(Vector2, double, string)>();
            var rank = 0;

            foreach (var (at, worth) in Ranked(env, candidates, most, spread, false, slack, heavy))
                flat.Add((at, worth, $"{++rank}x{Heavy(env, at)}"));

            Spots = flat;

            return;
        }

        Spots = PerEach(env, candidates, most, spread, each.Value, slack, heavy);
    }

    /// <summary>
    /// The best few places to catch each thing of one kind, one at a time.
    ///
    /// A different question from the best spots overall, and a more useful one for arguing with the
    /// planner: a remnant is going to be caught by SOMETHING, so what is worth knowing is which of
    /// the positions that catch it also sweep up the most beside it. The overall ranking answers
    /// "where is the value on this site" and buries the third remnant behind five spots around the
    /// first.
    ///
    /// By kind, because the same question is worth asking of rare monsters: they are the other
    /// content heavy enough to build a link around, and where a blast catching one also catches its
    /// neighbours is not something the eye judges well.
    ///
    /// Lettered by the thing and numbered within it, so a ring says what it belongs to and how it
    /// placed among that thing's options.
    /// </summary>
    private static List<(Vector2 At, double Worth, string Note)> PerEach(PlanEnvironment env,
        List<Vector2> candidates, int most, float spread, TargetKind kind, float slack = 0f,
        bool heavy = true)
    {
        var found = new List<(Vector2 At, double Worth, string Note)>();
        var none = new HashSet<int>();

        // Two markers sharing one best spot drew two rings on the same cell, one hidden under the
        // other, and the pair read as a single ring belonging to whichever was drawn last. Folded
        // instead, carrying both names - a spot that answers for two rares is a fact worth seeing,
        // not a collision to hide.
        var where = new Dictionary<(int, int), int>();
        var letter = 'A';

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var target = env.Targets[i];

            if (target.Kind != kind)
                continue;

            // **A seed is a spot chosen FOR a marker, so there is no such thing as a good one for a
            // marker nothing may catch.** The objective already prices these below anything a legal
            // chain can score, which settles which plan wins - it does not stop the search opening
            // every pass from spots built around them, testing chains that were worthless before the
            // first link was placed. See PlanTarget.Shunned.
            if (target.Shunned)
                continue;

            var worth = new List<(Vector2 At, double Solo)>();

            foreach (var candidate in candidates)
            {
                if (Catches(env, candidate, target))
                    worth.Add((candidate, NewWeight(env, candidate, none, env.Explosives - 1)));
            }

            if (worth.Count == 0)
                continue;

            worth.Sort(Better(Pull(env, heavy), Best(worth), slack));

            var apart = spread > 0f ? spread : MathF.Max(1f, env.Apart);
            var kept = new List<Vector2>();

            foreach (var (at, solo) in worth)
            {
                if (kept.Count >= most)
                    break;

                var clear = true;

                foreach (var already in kept)
                    clear &= Vector2.Distance(already, at) > apart;

                if (!clear)
                    continue;

                kept.Add(at);

                var cell = ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));
                var note = $"{letter}{kept.Count}";

                if (where.TryGetValue(cell, out var shared))
                {
                    var was = found[shared];

                    found[shared] = (was.At, was.Worth, $"{was.Note} {note}");

                    continue;
                }

                where[cell] = found.Count;
                found.Add((at, solo, $"{note}x{Heavy(env, at)}"));
            }

            letter++;
        }

        return found;
    }

    /// <summary>
    /// One link at a time, with the bands worked out again after each.
    ///
    /// **Bands go stale the moment a link is chosen.** They are drawn against the content still to
    /// be caught, so as soon as the first blast takes its markers every remaining family's best
    /// ground moves - the pair that was worth standing east of is now half collected, and the area
    /// that catches what is left is somewhere else. Enumerating orderings against one frozen picture
    /// asks where to stand for a site that stops existing after the first link.
    ///
    /// Measured by hand: reading the bands off the screen, placing one explosive, and recomputing
    /// reaches the best chain on this site easily - at one percent slack, where the frozen
    /// enumeration needs a wider band and still falls twenty points short.
    ///
    /// So this is that method. Each step looks ahead over the current bands, commits only the next
    /// link, and recomputes - a receding horizon, which is the standard answer when the world
    /// changes underneath a plan and re-planning is cheap. Here it costs one scan of a disc per
    /// family per link, which on the sites measured is tens of milliseconds.
    ///
    /// Scored throughout against the real environment, never a reduced one: the bands are only a way
    /// of proposing places, so narrowing what they are drawn from cannot mis-price a rune.
    /// </summary>
    private static List<Vector2> Rolling(PlanEnvironment env, List<Vector2> candidates,
        Func<bool> waiting)
    {
        if (env.Seeding == null)
            return new List<Vector2>();

        var (pairs, rares, _, _, slack, heavy, _, _, _, _) = env.Seeding;
        var chain = new List<Vector2>();

        for (var step = 0; step < env.Explosives; step++)
        {
            if (waiting?.Invoke() == false)
                break;

            // What the bands are drawn against: the content this chain has not already taken. Given
            // as a set of indices rather than a trimmed environment, so the coverage index survives
            // - see Regions.
            var gone = new HashSet<int>();

            for (var t = 0; t < env.Targets.Count; t++)
            {
                var taken = false;

                foreach (var link in chain)
                    taken |= Catches(env, link, env.Targets[t]);

                if (taken)
                    gone.Add(t);
            }

            if (gone.Count >= env.Targets.Count)
                break;

            var bands = Regions(env, candidates, pairs, rares, slack, heavy, gone);

            if (bands.Count == 0)
                break;

            var best = new List<Vector2>();
            var most = double.NegativeInfinity;

            // A share of one slice between all the steps, not a slice each. Five links at three
            // hundred and fifty milliseconds apiece is most of a four second window spent looking
            // ahead down chains that are thrown away after their first link.
            foreach (var found in Narrowed(env, candidates, waiting, Copied(chain), bands,
                         Slice / Math.Max(1, env.Explosives)))
            {
                var worth = Score(env, found);

                if (worth > most)
                {
                    most = worth;
                    best = found;
                }
            }

            // Only the next link is kept. The rest of that chain was worked out against bands that
            // are about to be redrawn, so it is a look ahead rather than a decision.
            if (best.Count <= chain.Count)
                break;

            chain.Add(best[chain.Count]);
        }

        return chain;
    }

    /// <summary>
    /// Chains built from the family spots alone, each then polished against the full candidate set.
    ///
    /// One greedy pass and one per compass direction, which on a set this small is close to trying
    /// everything - and every chain that comes out is finished on the real candidates, so a link
    /// that wants to sit two grid off a family spot still can. Nothing here is kept unless it beats
    /// what the search already has.
    ///
    /// Empty when the site has no families - no remnant sharing a blast with anything, no rares -
    /// and then this costs one pass over the targets and contributes nothing, which is correct.
    ///
    /// **Only the best few are polished, and that is the difference between this helping and this
    /// costing.** The narrow passes are nearly free - a greedy over twenty seven candidates - but
    /// the polish that follows each one is a full local search over a thousand, and nine of those
    /// run before the main loop has started. On a four second budget that is felt: the search looks
    /// slow off the mark for chains that were mostly going to be thrown away. So every direction is
    /// tried narrowly, scored narrowly, and only the best two are handed the expensive pass.
    /// </summary>
    /// <param name="head">
    /// Links already committed, which the enumeration builds on rather than replaces.
    ///
    /// **Scored as one chain with them, which is the entire point and was got wrong once already.**
    /// The first attempt at this built a smaller environment - origin at the committed link, one
    /// fewer explosive, and the content that link catches removed from the targets - and solved
    /// that. It is wrong in a way that is invisible from outside: on this site the committed link
    /// catches a remnant, and a remnant's rune multiplies every monster unearthed after it. Delete
    /// the remnant from the target list and the remainder is chosen by a search that cannot see the
    /// twenty percent riding on everything it picks up, so it picks up the wrong things - the same
    /// wrong things every time, which is why that path returned 1,294.3 however much of the window
    /// it was given.
    ///
    /// Keeping the real environment and scoring the head and the tail together has none of that: the
    /// objective is the one the plan is finally judged by, so nothing has to be added back.
    /// </param>
    /// <summary>
    /// Narrowed, with what it allocates attributed.
    ///
    /// **An iterator cannot be measured by wrapping the call.** Calling it only builds the state
    /// machine; every byte it spends is spent inside MoveNext, between one yield and the next, and
    /// a using block round the call would have reported it as free. So the stepping is measured
    /// instead. See Phases.
    /// </summary>
    private static IEnumerable<List<Vector2>> Narrowed(PlanEnvironment env, List<Vector2> candidates,
        Func<bool> waiting, List<Vector2> head = null,
        List<(string Name, List<Vector2> Cells, List<Vector2> All)> bands = null,
        TimeSpan? slice = null)
    {
        using var steps = NarrowedInner(env, candidates, waiting, head, bands, slice).GetEnumerator();

        while (true)
        {
            bool more;

            using (new Phase(PhaseNarrowed))
                more = steps.MoveNext();

            if (!more)
                yield break;

            yield return steps.Current;
        }
    }

    private static IEnumerable<List<Vector2>> NarrowedInner(PlanEnvironment env,
        List<Vector2> candidates, Func<bool> waiting, List<Vector2> head = null,
        List<(string Name, List<Vector2> Cells, List<Vector2> All)> bands = null,
        TimeSpan? slice = null)
    {
        if (env.Seeding == null)
            yield break;

        var clock = Stopwatch.StartNew();
        var (pairs, rares, remnants, spread, slack, heavy, _, _, _, _) = env.Seeding;
        var narrow = new List<Vector2>();

        foreach (var (at, _, _) in Families(env, candidates, pairs, rares, remnants, spread, slack,
                     heavy))
            narrow.Add(at);

        if (head == null || head.Count == 0)
        {
            Seeded = narrow.Count;
            SeededMs = clock.Elapsed.TotalMilliseconds;
        }

        if (narrow.Count == 0)
            yield break;

        // The bands rather than their representatives, and the cell chosen when the chain arrives.
        //
        // See Regions for why. The branching is over families now - a dozen or so - rather than over
        // three near-copies of each, and the cell is whichever member of the band the previous link
        // can actually reach. That is the difference between offering the chain a place it cannot
        // get to and offering it the same place from a step to the side.
        // Handed in wherever there is a caller to hand them in, because working them out means
        // walking a disc of ground per family and the restarts ask for them dozens of times a solve.
        var regions = bands ?? Regions(env, candidates, pairs, rares, slack, heavy);

        Banded = regions.Count;

        if (regions.Count == 0)
            yield break;

        // The ground between the edges, learned before the walk rather than during it.
        //
        // **The enumeration was not running out of time, it was running out of legal moves.** On a
        // cold site the router refuses anything it has not flooded yet, so most edge to edge links
        // read as impossible and the tree collapses - four thousand chains walked in seventy
        // milliseconds of a twelve hundred millisecond allowance, out of a space measured at half a
        // million. The search then chose the best of a fraction of a percent of the site and looked
        // for all the world as though it had finished.
        //
        // Asking for every edge pair first is at most one flood per edge, because a flood answers
        // every question from that point at once: forty odd floods, something under two hundred
        // milliseconds, and then all nineteen hundred pairs are cached and the walk is free to see
        // the whole tree. That fits inside the router's opening allowance, so it costs the solve
        // nothing it was not already going to spend.
        var edges = 0;

        foreach (var (_, corners, _) in regions)
            foreach (var corner in corners)
            {
                if (waiting?.Invoke() == false)
                    break;

                edges++;
                Reaches(env, env.Origin, corner);

                foreach (var (_, others, _) in regions)
                    foreach (var other in others)
                        Reaches(env, corner, other);
            }


        // States already explored, so a second route into one is not walked again.
        //
        // **The same position, the same bands spent, the same score - the same future.** Chains reach
        // a place by many routes: two openings that both lead to the third band arrive at the same
        // cell with the same two bands used, and everything they could do next is identical. Walking
        // it twice explores nothing new and, on a five link chain over a dozen bands, most of the
        // tree is that.
        //
        // The score is part of the key and has to be. Coverage does not care what order a chain was
        // laid in, but propagation cares about nothing else: a rune carried by the first link pays
        // over four blasts and by the third over two, so the same bands in a different order are a
        // different position to be in. Keying on place and bands alone would prune arrangements that
        // are genuinely worth more. Keying on the score as well means only true repeats are cut.
        var walked = new HashSet<(long Cell, ulong Spent, long Worth)>();
        var mask = 0UL;
        var spared = 0;

        var deep = new int[env.Explosives + 1];
        var tried = 0;
        var passed = 0;
        var bailed = "ran out of moves";
        var rough = new List<(List<Vector2> Chain, double Worth)>();
        var fixedHead = head ?? new List<Vector2>();
        var chain = Copied(fixedHead);
        var used = new bool[regions.Count];
        var seen = 0;
        var until = Stopwatch.StartNew();
        var spare = slice ?? Slice;

        // How far into the walk the current opening may run. Raised by each opening's share as it
        // is reached, so an opening that finishes early leaves its remainder to the ones after it.
        var stopAt = TimeSpan.Zero;

        // The opening the best chain of this walk began at. See where it is set.
        var bestFrom = (Band: "", At: Vector2.Zero, Worth: double.NegativeInfinity);

        void Walk()
        {
            if (chain.Count == env.Explosives)
            {
                rough.Add((Copied(chain), Score(env, chain)));

                return;
            }

            var from = chain.Count == 0 ? env.Origin : chain[^1];

            for (var r = 0; r < regions.Count; r++)
            {
                if (used[r] || seen >= Most || until.Elapsed > stopAt)
                    continue;

                // Every corner of the band, not the first that connects.
                //
                // **Which cell of a band to stand in cannot be decided on the way in.** The corners
                // are its extremes towards each of the other bands, so the right one is the one
                // facing wherever the chain goes next - and at the moment of arrival that is not
                // known yet. Taking the first cell that connects decides it backwards, on where the
                // chain came from, and the edge that mattered is never tried.
                //
                // Measured the hard way: reading these bands off the screen and picking, for each,
                // the edge facing the next one reproduces the best chain on this site easily, where
                // the search settled twenty points short. Branching over the corners is that method
                // - the choice is left open until the next band is chosen, and the enumeration is
                // what tries the combinations.
                used[r] = true;

                foreach (var cell in regions[r].Cells)
                {
                    if (seen >= Most || until.Elapsed > stopAt)
                    {
                        bailed = seen >= Most ? "hit the chain cap" : "ran out of time";

                        break;
                    }

                    var top = chain.Count == fixedHead.Count;

                    if (top)
                        tried++;

                    if (!Reaches(env, from, cell) || !Spaced(env, chain, cell, chain.Count))
                        continue;

                    if (top)
                        passed++;

                    seen++;
                    chain.Add(cell);
                    deep[Math.Min(chain.Count, deep.Length - 1)]++;

                    var worth = Score(env, chain);

                    if (chain.Count < env.Explosives)
                        rough.Add((Copied(chain), worth));

                    mask |= 1UL << (r & 63);

                    // Everything reachable from here has been reached from here already, and it is
                    // all still in rough from that first visit.
                    if (walked.Add((Key(cell), mask, (long)Math.Round(worth * 10d))))
                        Walk();
                    else
                        spared++;

                    mask &= ~(1UL << (r & 63));

                    chain.RemoveAt(chain.Count - 1);

                    if (waiting?.Invoke() == false)
                    {
                        bailed = "the search said stop";
                        used[r] = false;

                        return;
                    }
                }

                used[r] = false;
            }
        }

        // Every opening gets a share of the time, rather than the first one getting all of it.
        //
        // **Depth first spends the whole allowance on whichever opening it happens to try first.**
        // Measured: twenty three legal openings on this site, the walk tried one, enumerated its
        // four and a half thousand descendants to the end, and ran out - so the enumeration was
        // choosing the best chain that begins in one particular place and calling it the best chain.
        // Widening the slice only enumerated that same subtree more finely, which is why every
        // change to the budget did nothing.
        //
        // The fix is not breadth first, which would have to rank partial chains and cannot - see
        // Narrowed's note on why prefixes cannot be judged. It is to divide the time between the
        // openings and let each have a proper depth first look inside its own share.
        var starts = new List<(int Band, Vector2 At, double Worth)>();

        for (var r = 0; r < regions.Count; r++)
            foreach (var cell in regions[r].Cells)
                if (Reaches(env, chain.Count == 0 ? env.Origin : chain[^1], cell) &&
                    Spaced(env, chain, cell, chain.Count))
                {
                    chain.Add(cell);
                    starts.Add((r, cell, Score(env, chain)));
                    chain.RemoveAt(chain.Count - 1);
                }

        // **The richest openings first, and with more of the clock.**
        //
        // Measured once the bridges let the walk cross the site: a hundred and twenty five openings
        // sharing seventy milliseconds is half a millisecond of depth-first each, so every subtree
        // gets a glance and none gets a look - and the order they were tried in was the order the
        // bands happened to come out of Regions. The site then scored 2,718 on the first press and
        // 2,766 on the second, which is a search finding the good opening by luck rather than by
        // looking.
        //
        // Sorted by what the first link is worth on its own, which is the only thing that can be
        // known about an opening before its subtree is walked.
        starts.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        // Divided only when the opening is what is being chosen.
        //
        // A call with a head is not choosing an opening - the chain is already going where it is
        // going, and the branches here are its next link. Splitting a seventy millisecond lookahead
        // between twenty of those leaves each a few milliseconds and the rolling pass collapsed from
        // 1,266 to 783 when it was.
        var each = fixedHead.Count == 0 && starts.Count > 0 ? spare / starts.Count : spare;

        // Half the window split evenly, half split by worth.
        //
        // **A bridge opening is worth almost nothing by itself and is the reason this site is
        // solvable at all**, so an allocation purely by worth would starve exactly the openings
        // that were added to make the far side of the map reachable - a place whose whole value is
        // what can be reached FROM it scores as empty ground when asked what it catches. The even
        // half is the floor that protects them; the weighted half is what stops a rich opening
        // getting the same glance as a barren one.
        var total = 0d;

        foreach (var (_, _, worth) in starts)
            total += Math.Max(0d, worth);

        var weighted = fixedHead.Count == 0 && starts.Count > 0 && total > 0d;

        foreach (var (band, at, worth) in starts)
        {
            stopAt += weighted
                ? each / 2d + spare * (Math.Max(0d, worth) / total) / 2d
                : each;
            tried++;
            passed++;
            seen++;
            used[band] = true;
            chain.Add(at);
            deep[Math.Min(chain.Count, deep.Length - 1)]++;

            var opened = Score(env, chain);

            if (chain.Count < env.Explosives)
                rough.Add((Copied(chain), opened));

            mask |= 1UL << (band & 63);

            // Which opening the best chain came out of, for the dump.
            //
            // The one question the enumeration could not answer about itself: it reports how many
            // openings it tried and how deep it got, and said nothing about WHICH of them produced
            // the answer - so whether the bridges are earning their branches, or whether the rich
            // openings were winning all along, was a matter of opinion. The chains an opening
            // produces are the ones it appends to rough, so the winner is read off the tail.
            var was = rough.Count;

            if (walked.Add((Key(at), mask, (long)Math.Round(opened * 10d))))
                Walk();
            else
                spared++;

            for (var i = was; i < rough.Count; i++)
            {
                if (rough[i].Worth <= bestFrom.Worth)
                    continue;

                bestFrom = (regions[band].Name, at, rough[i].Worth);
            }

            mask &= ~(1UL << (band & 63));

            chain.RemoveAt(chain.Count - 1);
            used[band] = false;

            if (waiting?.Invoke() == false)
            {
                bailed = "the search said stop";

                break;
            }
        }

        // Only the main pass is reported.
        //
        // The rolling pass and the as-placed restarts call this too - dozens of times a solve, each
        // with a head and a fraction of the time - so whichever ran last was overwriting the figures
        // and the dump described a small late lookahead while appearing to describe the enumeration
        // the solve is built on.
        if (fixedHead.Count == 0)
        {
            Enumerated = seen;
            EnumeratedMs = until.Elapsed.TotalMilliseconds;
            var opens = new List<string>();

            foreach (var (name, corners, _) in regions)
            {
                var can = 0;
                var nearest = float.MaxValue;

                foreach (var corner in corners)
                {
                    nearest = MathF.Min(nearest, Vector2.Distance(env.Origin, corner));

                    if (Reaches(env, env.Origin, corner) && Spaced(env, new List<Vector2>(), corner))
                        can++;
                }

                opens.Add($"{name}:{can}/{corners.Count}@{nearest:0}");
            }

            Walked = $"stopped because it {bailed}; openings {passed} of {tried} tried; " +
                     $"{spared} repeats skipped, {walked.Count} states; " +
                     $"{edges} edges, " +
                     $"by depth {string.Join("/", deep[1..])}" + Environment.NewLine +
                     $"      reach {env.Reach:0}, apart {env.Apart:0}, placed {env.Placed?.Count ?? 0}, " +
                     $"origin ({env.Origin.X:0},{env.Origin.Y:0})" + Environment.NewLine +
                     "      openable per band (reachable/corners@nearest): " + string.Join(" ", opens) +
                     Environment.NewLine +
                     (bestFrom.Band.Length > 0
                         ? $"      best chain opened from {bestFrom.Band} at " +
                           $"({bestFrom.At.X:0},{bestFrom.At.Y:0}) worth {bestFrom.Worth:N1}"
                         : "      no opening produced a chain");
        }

        rough.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        SeededBest = rough.Count > 0 ? rough[0].Worth : 0d;
        SeededMs = clock.Elapsed.TotalMilliseconds;

        // Polished with the head held, so a committed link stays committed through the local search
        // that follows - and at least the opening in the ordinary case, which is what every other
        // seed here does.
        for (var i = 0; i < rough.Count && i < Polished; i++)
            yield return Improve(env, candidates, rough[i].Chain, waiting,
                Math.Max(1, fixedHead.Count));
    }

    /// <summary>
    /// How many of the narrow chains get the full local search. See Narrowed.
    /// </summary>
    private const int Polished = 2;

    /// <summary>
    /// How many partial chains the narrow sweep carries forward at each depth.
    ///
    /// Wide enough that a link which is worth less now but keeps the site open survives to be
    /// judged on the chain it leads to - which is the entire point of sweeping rather than picking.
    /// A few hundred over a few dozen spots is most of the space anyway.
    /// </summary>
    /// **Measured, and cut back hard.** At four hundred this was thirty three thousand full chain
    /// scorings - every marker weighed and every remnant's combination re-chosen for each - and the
    /// plugin showed nothing at all for the first three seconds of a four second window. A hundred
    /// and twenty is a quarter of the work and still carries every partial that a greedy pick would
    /// have thrown away, which is the whole reason for sweeping.
    /// <summary>
    /// How many chains the narrow enumeration may examine before it settles for what it has.
    ///
    /// Reach and spacing keep the real count far below this on the sites measured so far. It exists
    /// so that a site with a great many family spots close together degrades into a partial search
    /// rather than into a hang.
    /// </summary>
    private const int Most = 600000;

    /// <summary>
    /// How long the narrow enumeration may take before the rest of the search gets the window back.
    /// </summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Which drawn spots can follow which, as the search sees it.
    ///
    /// A chain is a path through these, so a spot the others cannot reach is a spot no chain can
    /// use however much it is worth - and a hop inside the straight line reach can still be refused
    /// when the walk round the terrain is longer than the chain is. That is invisible from the
    /// drawing, where two rings a comfortable distance apart look like a link.
    /// </summary>
    public static string Linked { get; private set; } = "not worked out";

    private static string Adjacency(PlanEnvironment env,
        IReadOnlyList<(Vector2 At, double Worth, string Note)> spots)
    {
        var lines = new List<string>();

        for (var i = 0; i < spots.Count; i++)
        {
            var to = new List<string>();

            for (var j = 0; j < spots.Count; j++)
            {
                if (i == j)
                    continue;

                if (Reaches(env, spots[i].At, spots[j].At) && Spaced(env, new List<Vector2>(), spots[j].At))
                    to.Add(Name(spots[j].Note));
            }

            var from = Reaches(env, env.Origin, spots[i].At) ? "opens" : "     ";

            lines.Add($"    {Name(spots[i].Note),-10} {from}  reaches {to.Count,2}: {string.Join(" ", to)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The first label on a spot, which is enough to recognise it by.</summary>
    private static string Name(string note)
    {
        var space = note.IndexOf(' ');

        return space < 0 ? note : note[..space];
    }

    /// <summary>How many narrow chains the last search examined, for the dump.</summary>
    public static int Enumerated { get; private set; }

    public static double EnumeratedMs { get; private set; }

    /// <summary>What the main enumeration actually walked, for the dump.</summary>
    public static string Walked { get; private set; } = "has not run";

    /// <summary>
    /// How the edges-only tree grows: legal chains of each length, and how many places each one
    /// could go next on average.
    ///
    /// The size of the space the search is actually working in, which is a different number from
    /// the count of places in it. Every link must be within reach of the last, must keep its
    /// distance from all of them, and must come from a band nothing else in the chain has used - so
    /// the tree is far narrower than the places suggest, and how much narrower is the thing worth
    /// knowing before deciding whether it can be searched outright.
    /// </summary>
    public static string Fanned { get; private set; } = "not worked out";

    private static string Fanout(PlanEnvironment env,
        IReadOnlyList<(string Name, List<Vector2> Cells, List<Vector2> All)> bands)
    {
        var edges = new List<(int Band, Vector2 At)>();

        for (var i = 0; i < bands.Count; i++)
            foreach (var corner in bands[i].Cells)
                edges.Add((i, corner));

        var live = new List<(List<Vector2> Chain, bool[] Used)>
        {
            (new List<Vector2>(), new bool[bands.Count]),
        };

        var lines = new List<string>
        {
            $"    {edges.Count} edges over {bands.Count} bands, {env.Explosives} explosives",
        };

        for (var depth = 1; depth <= env.Explosives; depth++)
        {
            var next = new List<(List<Vector2> Chain, bool[] Used)>();
            var dead = 0;

            foreach (var (chain, used) in live)
            {
                var from = chain.Count == 0 ? env.Origin : chain[^1];
                var grew = false;

                foreach (var (band, at) in edges)
                {
                    if (used[band] || !Reaches(env, from, at) || !Spaced(env, chain, at, chain.Count))
                        continue;

                    grew = true;

                    if (next.Count >= Most)
                        continue;

                    var made = Copied(chain);

                    made.Add(at);
                    var mark = (bool[])used.Clone();

                    mark[band] = true;
                    next.Add((made, mark));
                }

                if (!grew)
                    dead++;
            }

            var fan = live.Count > 0 ? (double)next.Count / live.Count : 0d;

            lines.Add($"    {depth} placed: {next.Count,8:N0} chains, " +
                      $"{fan,6:N1} ways on from each, {dead} dead end{(dead == 1 ? "" : "s")}");

            if (next.Count == 0)
                break;

            live = next;
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>How many bands the last narrow enumeration branched over, for the dump.</summary>
    public static int Banded { get; private set; }

    /// <summary>What the rolling, recomputing pass reached, for the dump.</summary>
    public static double RolledBest { get; private set; }

    /// <summary>How many family spots the last search seeded from, for the dump.</summary>
    public static int Seeded { get; private set; }

    /// <summary>How many openings the restarts drew from, for the dump.</summary>
    public static int Openings { get; private set; }

    /// <summary>Which random stream the last search drew from. See the seed in Search.</summary>
    public static int Stream { get; private set; }

    /// <summary>How many restart rounds the last search completed, for the dump.</summary>
    public static int Rounds { get; private set; }

    private static int _streams;

    /// <summary>
    /// How many of the openings get the rest of the window once every one has been tried once.
    ///
    /// Three rather than one, because a single shallow pass is a noisy way to rank them and
    /// committing everything to the leader of one attempt would throw away the site's real answer
    /// whenever that attempt was unlucky.
    /// </summary>
    private const int Deeply = 3;

    /// <summary>
    /// What share of the improvement window the deterministic openings may spend before the restart
    /// loop begins whatever they have got through. See Search.
    /// </summary>
    private const double Sown = 0.5d;

    /// <summary>How long the openings actually took, and how long they were allowed. For the dump.</summary>
    public static string Sowing { get; private set; } = "not run";

    /// <summary>
    /// How far into the improvement window the endgame starts, as a fraction of it.
    ///
    /// Nine tenths: late enough that the free search has had the window it was given, early enough
    /// that a commitment has a tenth of it to prove itself in - four hundred milliseconds at the
    /// usual four second setting.
    /// </summary>
    private const double Endgame = 0.9;

    /// <summary>How many links the endgame committed before the search ended, for the dump.</summary>
    public static int Committed { get; private set; }

    /// <summary>What the narrow pass scored before any polishing, and how long it took.</summary>
    public static double SeededBest { get; private set; }

    public static double SeededMs { get; private set; }

    /// <summary>
    /// A family as the band of places that satisfy it, rather than as one place.
    ///
    /// **A spot is a point and a family is an area, and collapsing the second into the first threw
    /// away the only thing that decided the chain.** Measured on one site: the winning chain's third
    /// link is at (1015,482), the family set held (1014,483) instead - one and a half grid away,
    /// worth the same to a tenth of a point - and the difference is that the walk from the previous
    /// link routes under the reach to one and over it to the other. No amount of searching over the
    /// wrong representative finds the right chain, because the two are the same score and only one
    /// is connectable.
    ///
    /// The blast has a radius, so catching a remnant and a rare together is satisfied by a band of
    /// cells, not a cell. Which cell in the band costs nothing and decides what the next link can
    /// reach. So the band is carried whole and the choice is deferred to the moment a chain actually
    /// arrives, when the previous link is known and reachability can be tested rather than guessed.
    /// </summary>
    /// <param name="slack">
    /// How much worth may separate a cell from the best in its band and still count as the same
    /// answer. The default tenth of a point is there because these are sums over dozens of markers
    /// and two cells catching the identical set differ in the last bits.
    /// </param>
    /// <param name="caught">
    /// Markers already taken, by their index in the real environment's target list.
    ///
    /// **Passed rather than removed, because the coverage index is keyed on the environment.** The
    /// caller used to hand in a copy of the environment with the caught markers stripped out, which
    /// meant a new object every redraw - so the index rebuilt every time, and the indices in it
    /// meant something different on each call. Here the environment is always the real one and what
    /// is gone is a set of numbers against it.
    /// </param>
    public static List<(string Name, List<Vector2> Cells, List<Vector2> All)> Regions(
        PlanEnvironment env, List<Vector2> candidates, int pairs, int rares, float slack, bool heavy,
        HashSet<int> caught = null)
    {
        using var phase = new Phase(PhaseRegions);

        return RegionsInner(env, candidates, pairs, rares, slack, heavy, caught);
    }

    /// <summary>The body of Regions, wrapped so its allocation is attributed. See Phases.</summary>
    private static List<(string Name, List<Vector2> Cells, List<Vector2> All)> RegionsInner(
        PlanEnvironment env, List<Vector2> candidates, int pairs, int rares, float slack, bool heavy,
        HashSet<int> caught = null)
    {
        var found = new List<(string Name, List<Vector2> Cells, List<Vector2> All)>();
        var gone = caught ?? new HashSet<int>();
        var none = gone;
        var pull = Pull(env, heavy);
        var covers = CoverageOfEnvironment(env);

        // The ground itself, a grid unit at a time - not the candidate list.
        //
        // **A band cannot be found among the candidates, because the candidates are not a sampling
        // of the ground.** They are marker centres and the points where two markers' blast circles
        // cross: a sparse set of geometrically interesting places, chosen so that greedy has
        // something worth trying. Asking which of them tie with the best returns the two or three
        // intersections that happen to lie nearby, which is why every band came back one to three
        // cells across nought to four grid, and why drawing them as shapes looked wrong. It was
        // wrong. There is no region in a list of isolated points.
        //
        // So a band is measured where it lives: every placeable cell within reach of the family's
        // best, stepped one grid at a time, kept when it catches the same content for near enough
        // the same score. That is a real area with a real shape, and its extremes are real places
        // the chain can stand - including the ones that are not candidates at all, which is where
        // the best chain on this site puts its third link.
        void Add(string name, List<int> required, Vector2 around)
        {
            var cells = new List<(Vector2 At, double Solo)>();
            var reach = (int)MathF.Ceiling(Spill);

            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var at = new Vector2(MathF.Round(around.X) + dx, MathF.Round(around.Y) + dy);

                    if (dx * dx + dy * dy > reach * reach || !env.CanPlace(at))
                        continue;

                    var all = true;

                    foreach (var which in required)
                        all &= Catches(env, at, env.Targets[which]);

                    if (all)
                        cells.Add((at, Adds(env, covers, at, none, env.Explosives - 1)));
                }
            }

            if (cells.Count == 0)
                return;

            var best = Best(cells);
            var band = Math.Max(0.1d, Math.Abs(best) * slack);

            // Strictly by worth, and emphatically not by Better.
            //
            // **Better is not a valid ordering once the band is wide.** It calls two cells equal
            // when they are within the band of each other, so with a five percent band A ties B, B
            // ties C, and A beats C - which is not an order at all, and List.Sort given an
            // inconsistent comparison returns an arbitrary one. The gathering loop then met an
            // out-of-band cell early, stopped, and whole families came back missing or truncated.
            // Here the band is a filter, so the sort only has to be a sort.
            cells.Sort((a, b) => b.Solo.CompareTo(a.Solo));

            var kept = new List<Vector2>();

            foreach (var (at, solo) in cells)
            {
                if (kept.Count >= Band)
                    break;

                if (best - solo <= band)
                    kept.Add(at);
            }

            if (kept.Count == 0)
                return;

            // One place, one band, whatever number of families want it.
            //
            // A rare's own band and the pair band that contains that rare are frequently the same
            // ground - and three rares close together give three identical bands. Branching over
            // each of them separately is the same chain explored twice over, and it crowds out the
            // breadth that would have gone somewhere new. Merged on the best cell, which is what
            // makes them the same answer, and the names are joined so the drawing still says which
            // families a place serves.
            for (var i = 0; i < found.Count; i++)
            {
                if (found[i].All.Count > 0 && found[i].All[0] == kept[0])
                {
                    found[i] = ($"{found[i].Name} {name}", found[i].Cells, found[i].All);

                    return;
                }
            }

            found.Add((name, kept, kept));
        }

        /// <summary>
        /// Where a family's band sits: the candidate that catches its content best, or failing that
        /// the middle of the content itself.
        ///
        /// **A family with no candidate to sit on was dropped silently, and it cost four remnants
        /// out of five.** Measured on a site whose remnants sit in its four corners: the band list
        /// came back holding R1+E8 and R1+E9 and nothing else remnant-shaped, because a candidate is
        /// a marker centre or the crossing of two blast circles, and a remnant with no rare close
        /// enough for one point to catch both has no such crossing. The ground between them was
        /// perfectly placeable and caught both; nothing ever looked at it.
        ///
        /// Add already sweeps real ground around this point and keeps only cells that catch every
        /// required target, so a fallback that is merely in the right area costs one sweep when it
        /// is wrong and finds the family when it is right. The middle of the required content is in
        /// the right area by construction: a cell catching all of them is within a blast of each,
        /// so it cannot be far from their centre.
        /// </summary>
        Vector2 Around(List<int> required)
        {
            var at = Vector2.Zero;
            var most = double.NegativeInfinity;

            foreach (var candidate in candidates)
            {
                var all = true;

                foreach (var which in required)
                    all &= Catches(env, candidate, env.Targets[which]);

                if (!all)
                    continue;

                var worth = Adds(env, covers, candidate, none, env.Explosives - 1);

                if (worth > most)
                {
                    most = worth;
                    at = candidate;
                }
            }

            if (at != Vector2.Zero)
                return at;

            var middle = Vector2.Zero;

            foreach (var which in required)
                middle += env.Targets[which].Grid;

            return required.Count > 0 ? middle / required.Count : Vector2.Zero;
        }

        // A band of cells that can throw to both of two places, and whether there was any ground
        // that could do it. See where it is called.
        bool Bridge(string name, Vector2 one, Vector2 two)
        {
            var middle = (one + two) / 2f;
            var far = env.Reach;

            // The lens of cells in range of both ends sits about the midpoint, and its half height
            // is what is left of the reach once half the gap is spent. Stepped two grid at a time:
            // this is a region rather than a lattice of distinct answers, and Corners reduces it to
            // a handful of cells afterwards regardless.
            var span = (int)MathF.Ceiling(MathF.Sqrt(MathF.Max(1f,
                far * far - Vector2.DistanceSquared(one, two) / 4f)));
            var cells = new List<(Vector2 At, double Solo)>();

            for (var dy = -span; dy <= span; dy += 2)
            {
                for (var dx = -span; dx <= span; dx += 2)
                {
                    var at = new Vector2(MathF.Round(middle.X) + dx, MathF.Round(middle.Y) + dy);

                    if (Vector2.DistanceSquared(at, one) > far * far ||
                        Vector2.DistanceSquared(at, two) > far * far ||
                        !env.CanPlace(at))
                        continue;

                    cells.Add((at, Adds(env, covers, at, none, env.Explosives - 1)));
                }
            }

            if (cells.Count == 0)
                return false;

            // By worth, and every cell kept up to the usual ceiling rather than filtered to a band
            // of near-equal ones. A bridge is chosen for WHERE it is, so narrowing it to the cells
            // that score alike would throw away exactly the geometry it exists to provide. Corners
            // then keeps the ones nearest each other band, which is the right summary of a place
            // whose job is to be reachable from two directions.
            cells.Sort((a, b) => b.Solo.CompareTo(a.Solo));

            // Two far bands lying the same way from here want the same ground in between, and a
            // second copy of a place is a second branch at every level of the walk that leads
            // somewhere already reachable. Add merges content families on exactly this test; a
            // bridge is added directly, so it has to make the test itself.
            foreach (var (_, _, all) in found)
            {
                if (all.Count > 0 && Vector2.DistanceSquared(all[0], cells[0].At) <= Same * Same)
                    return false;
            }

            var kept = new List<Vector2>();

            foreach (var (at, _) in cells)
            {
                if (kept.Count >= Band)
                    break;

                kept.Add(at);
            }

            found.Add((name, kept, kept));

            return true;
        }

        var heavies = new List<(int Which, string Name)>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var kind = env.Targets[i].Kind;

            // Never a band's anchor. See PlanTarget.Shunned.
            if (kind is TargetKind.Remnant or TargetKind.Elite && !gone.Contains(i) &&
                !env.Targets[i].Shunned)
                heavies.Add((i, $"{(kind == TargetKind.Remnant ? "R" : "E")}{heavies.Count + 1}"));
        }

        if (pairs > 0)
        {
            foreach (var (which, name) in heavies)
            {
                if (env.Targets[which].Kind != TargetKind.Remnant)
                    continue;

                foreach (var (other, label) in heavies)
                {
                    if (other == which ||
                        (env.Targets[other].Kind == TargetKind.Remnant && other < which))
                        continue;

                    var pair = new List<int> { which, other };
                    var near = Around(pair);

                    if (near != Vector2.Zero)
                        Add($"{name}+{label}", pair, near);
                }
            }
        }

        if (rares > 0)
        {
            foreach (var (which, name) in heavies)
            {
                if (env.Targets[which].Kind != TargetKind.Elite)
                    continue;

                var one = new List<int> { which };
                var near = Around(one);

                if (near != Vector2.Zero)
                    Add(name, one, near);
            }
        }

        // **And one band for each remnant on its own, unconditionally.**
        //
        // The pairs above only reach a remnant that has a heavy neighbour close enough to share a
        // blast with. On the site this was found on, four remnants of five had no band at all -
        // which means the search could not commit to taking them, could not open from them, and
        // could not be told about the places a player picks in a second by eye. A remnant is the
        // heaviest thing on the site and it is going to be caught by SOMETHING; where from is the
        // first question anyone asks of a layout, and it was the one question the band set could
        // not answer.
        //
        // Not behind a count the way pairs and rares are. Those two gates exist so the families can
        // be turned off and compared; this one would be off for every player who already has a
        // config, because a saved setting beats a changed default and "Spots per remnant" ships at
        // nought. A band per remnant is not an experiment, it is the floor.
        foreach (var (which, name) in heavies)
        {
            if (env.Targets[which].Kind != TargetKind.Remnant)
                continue;

            var alone = new List<int> { which };
            var sits = Around(alone);

            if (sits != Vector2.Zero)
                Add(name, alone, sits);
        }

        // **Places whose worth is that the chain can get from here to there.**
        //
        // Measured, and it is the whole reason a site of four cornered remnants scored 1,701 where
        // a player scored 2,756 by eye: reach 91, and six of nine bands with their nearest corner
        // at 96 to 124. The enumeration walks band corner to band corner, so it explored 18 states
        // at depth one, 144 at depth two, and NOTHING at depth three - 162 chains, in no measurable
        // time, because from any two link state there was no third band in reach. A player looks at
        // that layout and says the chain has to path through the middle; the middle catches little,
        // belongs to no family, and was therefore not a place the search could put a link at all.
        //
        // So a bridge is a family whose requirement is geometric rather than content: cells within
        // reach of both bands, ranked by whatever they do happen to catch. It does not decide that
        // the chain should go that way - it only makes going that way expressible, which it was not.
        //
        // Only between bands further apart than one throw and closer than two, since nearer needs no
        // bridge and further cannot be bridged by a single link. Capped, because every band is
        // another branch at every level of the walk, and the point is to make the walk reach the far
        // side of the site rather than to make it wider.
        // **The detonator is the first endpoint, and the most important one.** Every chain starts
        // there, and on the site this was measured on only three of nine bands were within a throw
        // of it - so the first link could reach no remnant at all, whatever the rest of the walk
        // might have managed afterwards. A bridge from the start is the answer to "where do I put
        // the first explosive so that the second can reach the thing I actually want", which is
        // precisely the question a player answers by eye before placing anything.
        //
        // It comes first in the list so that, with the cap reached, the bridges that exist are the
        // ones leaving the origin rather than an arbitrary pair in the middle of the site.
        var ends = new List<(string Name, Vector2 At)> { ("start", env.Origin) };

        foreach (var (name, _, all) in new List<(string, List<Vector2>, List<Vector2>)>(found))
        {
            if (all.Count > 0)
                ends.Add((name, all[0]));
        }

        var bridged = 0;

        for (var i = 0; i < ends.Count && bridged < Bridges; i++)
        {
            for (var j = i + 1; j < ends.Count && bridged < Bridges; j++)
            {
                var apart = Vector2.Distance(ends[i].At, ends[j].At);

                if (apart <= env.Reach || apart > env.Reach * 2f)
                    continue;

                if (Bridge(ends[i].Name + "~" + ends[j].Name, ends[i].At, ends[j].At))
                    bridged++;
            }
        }

        // Corners last, once every band is known.
        //
        // **A corner is only worth keeping if something might be reached from it**, and what might
        // be reached is the other bands - so the directions that matter are the directions they lie
        // in, not the eight points of the compass. A band at the eastern edge of the site has every
        // other band to its west; its eastern extremes lean towards nothing and can never be the
        // right cell to stand in. Fixed compass directions kept them anyway, which is why a band out
        // there came back as an eight sided shape when half of it could not matter.
        var whole = new List<(string Name, List<Vector2> Cells, List<Vector2> All)>(found);
        var spread2 = env.Seeding?.PerBand ?? 6;

        for (var i = 0; i < found.Count; i++)
        {
            var ways = new List<Vector2>();

            // The detonator counts as a direction.
            //
            // **It is where every chain starts, and it is not a band.** Corners lean towards the
            // other bands, so the cell of a band nearest the chain's origin was never kept - and a
            // band whose reachable side faces the detonator then had no corner the first link could
            // use. Measured: the enumeration found one legal opening where the site has twenty
            // three, explored that single subtree to the end in seventy milliseconds, and reported
            // it as the answer.
            ways.Add(env.Origin);

            for (var j = 0; j < whole.Count; j++)
            {
                if (i == j || whole[j].All.Count == 0)
                    continue;

                ways.Add(Middle(whole[j].All));
            }

            found[i] = (found[i].Name, Corners(found[i].All, ways, spread2), found[i].All);
        }

        return found;
    }

    /// <summary>A cell as one number, for keying a state on where the chain stands.</summary>
    internal static long Key(Vector2 at) =>
        ((long)MathF.Round(at.X) << 20) | (long)(MathF.Round(at.Y) + 1000f);

    /// <summary>The middle of a band, which is what a direction between two of them is measured from.</summary>
    private static Vector2 Middle(List<Vector2> cells)
    {
        var sum = Vector2.Zero;

        foreach (var cell in cells)
            sum += cell;

        return cells.Count > 0 ? sum / cells.Count : Vector2.Zero;
    }

    /// <summary>How many cells of a band are gathered before they are reduced to its corners.</summary>
    private const int Band = 400;

    /// <summary>
    /// How many bridging bands one site may have. See where Bridge is called.
    ///
    /// Six. Every band is another branch at every level of the enumeration, and the walk already
    /// explores 144 states at depth two on a nine band site - so this is deliberately enough to
    /// connect the far corners of a layout and not enough to turn the walk into a search over empty
    /// ground.
    /// </summary>
    private const int Bridges = 6;

    /// <summary>How close two bands' best cells have to be to count as the same place, in grid.</summary>
    private const float Same = 6f;

    /// <summary>
    /// A band reduced to the cells that can actually gain something: its best, and its furthest in
    /// each direction.
    ///
    /// **Every other cell in a band is dominated.** They all score the same - that is what makes it
    /// a band - so the only thing one cell has over another is how far it stands in some direction,
    /// and a cell that is furthest in no direction is strictly worse than the one that is, whichever
    /// way the chain goes next. Sixty cells collapse to nine or fewer without losing a single option
    /// the chain could have used.
    ///
    /// **Nearest to each other band, not furthest along the direction of it.** Those are the same
    /// thing for a round band and different for a real one, and the difference is the whole game: a
    /// link is legal when the walk to the next place fits inside the reach, so the cell that matters
    /// is the one that shortens that walk most. Measured on one site, the band holding the best
    /// chain's third link spans four grid, and the cell the chain needs is one and a half grid
    /// nearer the previous link than the cell the direction rule kept - which is the difference
    /// between a link the router allows and one it refuses. An exhaustive walk of every chain
    /// through the direction-picked edges topped out at 1,287.8 against 1,319 by hand, because the
    /// cell was in the band and not in the edges.
    ///
    /// One per other band, plus the detonator, because those are the only places a chain leaves here
    /// for. A band on the edge of the site therefore keeps the cells on its inward side and none on
    /// the outward one, where leaning gains nothing that anything can use.
    ///
    /// Extremes of the MEMBERS, not corners of the area, so a band shaped like a crescent yields
    /// real cells rather than a point in the hollow that nothing can be placed on.
    /// </summary>
    /// <param name="most">
    /// How many cells to end up with, filled out by spreading over the band when the reasoned picks
    /// do not reach it.
    ///
    /// **One cell per neighbouring band is far too coarse a summary of an area.** Measured: a band
    /// of three hundred and thirty three cells spanning thirty grid was reduced to five, and the
    /// cell the best known chain uses was not among them - it was on the screen as a dot the player
    /// could see and pick, and the search never had it. Whichever rule chooses the representatives,
    /// a rule that keeps five of three hundred will keep the wrong five sometimes; covering the band
    /// evenly as well is what stops a whole region of it being invisible.
    /// </param>
    private static List<Vector2> Corners(List<Vector2> cells, List<Vector2> ways, int most)
    {
        if (cells.Count <= 3 || ways.Count == 0)
            return cells;

        // Sorted by worth already, so the first is the band's best and stays its first answer: when
        // reach is not binding, the chain should stand where the score is highest.
        var keep = new List<Vector2> { cells[0] };
        var middle = Middle(cells);

        foreach (var way in ways)
        {
            var closest = Vector2.Zero;
            var least = float.MaxValue;

            foreach (var cell in cells)
            {
                var gap = Vector2.DistanceSquared(cell, way);

                if (gap < least)
                {
                    least = gap;
                    closest = cell;
                }
            }

            if (!keep.Contains(closest))
                keep.Add(closest);

            // And the far side of the band along the same bearing.
            //
            // **Both rules are right about different things and neither is right about both.**
            // Nearest to the next band shortens the link that has to reach it; furthest along the
            // bearing keeps distance from the link just laid, which is what leaves the chain room
            // to arrive here at all. They coincide on a round band and diverge on a real one -
            // and swapping one for the other silently dropped the cells a player had been picking
            // off the screen to build the best chain on this site by hand.
            var bearing = way - middle;

            if (bearing.LengthSquared() > 0.01f)
            {
                bearing = Vector2.Normalize(bearing);

                var furthest = Vector2.Zero;
                var most2 = float.NegativeInfinity;

                foreach (var cell in cells)
                {
                    var along = Vector2.Dot(cell - middle, bearing);

                    if (along > most2)
                    {
                        most2 = along;
                        furthest = cell;
                    }
                }

                if (!keep.Contains(furthest))
                    keep.Add(furthest);
            }
        }

        // Then spread over whatever the band still has, furthest first.
        //
        // Each addition is the cell furthest from everything kept so far, so the covering gets
        // coarser as it goes and every part of the band is within some distance of a kept cell -
        // which is the property the reasoned picks cannot promise on their own.
        while (keep.Count < most)
        {
            var furthest = Vector2.Zero;
            var best = -1f;

            foreach (var cell in cells)
            {
                var nearest = float.MaxValue;

                foreach (var already in keep)
                    nearest = MathF.Min(nearest, Vector2.DistanceSquared(cell, already));

                if (nearest > best)
                {
                    best = nearest;
                    furthest = cell;
                }
            }

            if (best <= 0f || keep.Contains(furthest))
                break;

            keep.Add(furthest);
        }

        return keep;
    }

    /// <summary>
    /// How far from a family's best the ground is searched for the rest of its band.
    ///
    /// **A band has to stay one place.** At thirty a band came back three hundred and thirty cells
    /// across thirty three grid by thirty, which is not a way of catching one thing from various
    /// angles - it is a quarter of the dig site whose totals happen to land within the slack. The
    /// blast is under thirty across, so eighteen still lets a band run most of the way round its
    /// markers while keeping the far side of them out of it.
    /// </summary>
    private const float Spill = 18f;

    /// <summary>
    /// The families together: the pair spots, the per-rare spots and the per-remnant spots, folded
    /// into one set with nothing counted twice.
    ///
    /// **One builder, because the rings drawn and the spots searched have to be the same thing.**
    /// The whole value of the drawn views is that a dump can be held against a chain that actually
    /// scored; compute the search's candidates by a second route and the dumps stop being evidence
    /// about the search and become evidence about the drawing.
    ///
    /// Measured against six recorded chains on one site, at three per pair and three per rare, this
    /// set held 18 of the 30 links exactly and 27 within 20 grid, out of 1,082 candidates. The best
    /// chain of the six had four of its five links exactly on it.
    ///
    /// The families are complements rather than alternatives, which is why this is one set with
    /// counts rather than a choice between three modes. Pair spots cover the early links, where the
    /// remnants are, and are blind by construction to a link that catches no remnant - which is what
    /// the last two links of every recorded chain are. Rare spots cover exactly those. Neither
    /// reaches four of five on its own.
    /// </summary>
    /// <summary>
    /// The family spots, worked out once per solve and handed to everyone who asks for them again.
    ///
    /// **It was computed twice, identically, and it is the most expensive thing in the search.**
    /// The band enumeration builds it at the top of Narrowed and the restart openings build it again
    /// afterwards, with the same environment, the same candidates and the same five settings - and
    /// on a Grand site each pass is five and a half seconds of scanning every candidate against
    /// every one of three hundred and seventy six markers. Measured: 5,463ms reported for the first,
    /// against a sixteen second window, before a single chain had been examined.
    ///
    /// Reference equality on the environment and the candidate list, which is the same test CoverageOfEnvironment
    /// uses and is exact for the thing being asked: both are rebuilt per solve and neither is
    /// mutated during one. The settings are carried on the environment, so two calls that agree on
    /// those two references agree on everything.
    ///
    /// Per thread, because a solve runs off the main one and the debug spots button asks from the
    /// render thread.
    /// </summary>
    public static List<(Vector2 At, double Worth, string Note)> Families(PlanEnvironment env,
        List<Vector2> candidates, int pairs, int rares, int remnants, float spread, float slack,
        bool heavy)
    {
        if (ReferenceEquals(_kinEnv, env) && ReferenceEquals(_kinFrom, candidates) && _kin != null)
            return _kin;

        var made = FamiliesOfCandidates(env, candidates, pairs, rares, remnants, spread, slack, heavy);

        _kinEnv = env;
        _kinFrom = candidates;
        _kin = made;

        return made;
    }

    [ThreadStatic] private static PlanEnvironment _kinEnv;
    [ThreadStatic] private static List<Vector2> _kinFrom;
    [ThreadStatic] private static List<(Vector2 At, double Worth, string Note)> _kin;

    /// <summary>The work itself. See Families, which is what everything calls.</summary>
    private static List<(Vector2 At, double Worth, string Note)> FamiliesOfCandidates(PlanEnvironment env,
        List<Vector2> candidates, int pairs, int rares, int remnants, float spread, float slack,
        bool heavy)
    {
        var found = new List<(Vector2 At, double Worth, string Note)>();
        var where = new Dictionary<(int, int), int>();

        void Fold(List<(Vector2 At, double Worth, string Note)> from)
        {
            foreach (var (at, worth, note) in from)
            {
                var cell = ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));

                if (where.TryGetValue(cell, out var shared))
                {
                    var was = found[shared];

                    found[shared] = (was.At, was.Worth, $"{was.Note} {note}");

                    continue;
                }

                where[cell] = found.Count;
                found.Add((at, worth, note));
            }
        }

        if (pairs > 0)
            Fold(Pairs(env, candidates, pairs, spread, slack, heavy));

        if (rares > 0)
            Fold(PerEach(env, candidates, rares, spread, TargetKind.Elite, slack, heavy));

        if (remnants > 0)
            Fold(PerEach(env, candidates, remnants, spread, TargetKind.Remnant, slack, heavy));

        return found;
    }

    /// <summary>
    /// One spot per remnant and neighbour that a single blast can take together.
    ///
    /// The ranked views answer "where is the value", and they are right about that and unhelpful
    /// about what follows it. A chain is a sequence, so the question at each step is not only which
    /// spot is richest but which of the near-equal spots leaves the next link possible - and the
    /// strongest recorded chains are built out of links that do two jobs, catching a remnant and a
    /// rare at once rather than spending a link on each.
    ///
    /// So this asks one question per remnant per thing it can share a blast with. A neighbour counts
    /// only if some candidate actually catches **both** - not if it is merely nearby, which is the
    /// difference between a handful of rings and one per pair of markers on the site. A remnant with
    /// nothing it can double up with contributes nothing, which is the intended answer rather than a
    /// gap: the per-kind views already cover where its own best spots are.
    ///
    /// Other remnants count as neighbours too, not only rares. Two remnants inside one blast is the
    /// best link on the site when it exists, and it is not a thing the eye picks out.
    ///
    /// The obvious failure is drawing the same point several times over - one spot catching a
    /// remnant and two rares answers two pairs - so positions are folded together and carry every
    /// pair they answer in one label.
    /// </summary>
    private static List<(Vector2 At, double Worth, string Note)> Pairs(PlanEnvironment env,
        List<Vector2> candidates, int most, float spread, float slack = 0f, bool heavy = true)
    {
        var markers = new List<(int Which, string Name)>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var kind = env.Targets[i].Kind;

            // Half a pair that must not be caught is not half a pair. See PlanTarget.Shunned.
            if (kind is TargetKind.Remnant or TargetKind.Elite && !env.Targets[i].Shunned)
                markers.Add((i, $"{(kind == TargetKind.Remnant ? "R" : "E")}{markers.Count + 1}"));
        }

        var none = new HashSet<int>();
        var found = new List<(Vector2 At, double Worth, string Note)>();
        var where = new Dictionary<(int, int), int>();

        foreach (var (which, name) in markers)
        {
            // Remnants anchor, everything heavy neighbours. Anchoring on rares as well drew the
            // same pair twice from both ends and turned a readable handful of rings into forty.
            if (env.Targets[which].Kind != TargetKind.Remnant)
                continue;

            var anchor = env.Targets[which];

            foreach (var (other, label) in markers)
            {
                if (other == which)
                    continue;

                // Each unordered pair once. Two remnants would otherwise be asked about from both
                // ends and answered identically.
                if (env.Targets[other].Kind == TargetKind.Remnant && other < which)
                    continue;

                var both = new List<(Vector2 At, double Solo)>();

                foreach (var candidate in candidates)
                {
                    if (Catches(env, candidate, anchor) && Catches(env, candidate, env.Targets[other]))
                        both.Add((candidate, NewWeight(env, candidate, none, env.Explosives - 1)));
                }

                // Nothing can take the two together, so there is no such spot to draw. This is the
                // "no rares in range" case and drawing the anchor's own best spot instead would be
                // answering a question nobody asked.
                if (both.Count == 0)
                    continue;

                // Among the spots that catch both, the same lean as everywhere else: the one nearest
                // the weight of the site, within the slack allowed.
                both.Sort(Better(Pull(env, heavy), Best(both), slack));

                // **The whole family, not its best member.** Measured against six recorded chains,
                // the best chain's decisive link was an R2+E4 spot sitting 18 grid from the R2+E4
                // spot this drew - same pair, lower standalone worth, chosen by the chain for the
                // reach it left the next link. Keeping one per pair threw the winner away and kept
                // the runner-up, and the spots that survived predicted the WORST chain of the six
                // better than the best one.
                //
                // So a pair is a family of options spread out across the band that catches both,
                // and how many of them to show is the same count and spacing the other views use.
                var apart = spread > 0f ? spread : MathF.Max(1f, env.Apart);
                var kept = new List<Vector2>();

                foreach (var (at, solo) in both)
                {
                    if (kept.Count >= most)
                        break;

                    var clear = true;

                    foreach (var already in kept)
                        clear &= Vector2.Distance(already, at) > apart;

                    if (!clear)
                        continue;

                    kept.Add(at);

                    var cell = ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));
                    var note = kept.Count > 1 ? $"{name}+{label}#{kept.Count}" : $"{name}+{label}";

                    if (where.TryGetValue(cell, out var shared))
                    {
                        var was = found[shared];

                        found[shared] = (was.At, was.Worth, $"{was.Note} {note}");

                        continue;
                    }

                    where[cell] = found.Count;
                    found.Add((at, solo, $"{note}x{Heavy(env, at)}"));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Puts a set of places on screen from outside the ranking machinery.
    ///
    /// The edge-only mode works out its own options and wants them drawn; this is how, without it
    /// having to pretend to be one of the debug buttons.
    /// </summary>
    public static void Show(List<(string Name, List<Vector2> Cells, List<Vector2> All)> these)
    {
        Family = false;
        Paired = false;
        PerKind = null;
        Spots = new List<(Vector2, double, string)>();
        Shapes = these;
        Shaped = true;
        Drawn = these.Count;
    }

    public static IReadOnlyList<(Vector2 At, double Worth, string Note)> Spots { get; private set; } =
        new List<(Vector2, double, string)>();

    private static int _sweeps;
    private static double _sweepGain;
    /// <summary>Stretches reversed, and what they were worth. See Reverse.</summary>
    private static int _reversals;

    private static double _reverseGain;

    private static int _orders;
    private static double _orderGain;

    /// <summary>
    /// The same chain in the other order, or null when the game could not place it.
    ///
    /// Every link is re-tested rather than only measured, for the same reason the openings are: a
    /// chain walked backwards is a different set of segments over the same points, so a reversal
    /// can be perfectly well spaced and still put its first link through a wall.
    /// </summary>
    private static List<Vector2> Reverse(PlanEnvironment env, List<Vector2> chain)
    {
        if (chain.Count < 2)
            return null;

        var back = Copied(chain);
        back.Reverse();

        var from = env.Origin;

        foreach (var point in back)
        {
            if (!Reaches(env, from, point))
                return null;

            from = point;
        }

        return back;
    }

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

        var wanted = env.Targets.Where(t => t.Weight > 0f).ToList();

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
    /// every candidate for every link, the beam expands every candidate at every step, and PerEach
    /// tests every candidate against every marker. Nine thousand candidates on a Grand site, so a
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

    /// <summary>The body of Greedy, wrapped so its allocation is attributed. See Phases.</summary>
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

            foreach (var candidate in candidates)
            {
                if (!Reaches(env, from, candidate) || !Spaced(env, chain, candidate))
                    continue;

                // Priced with the deferral in mind, because this is the one place with a chain
                // context: what has been taken already, and how many links are left to take
                // anything with. See NewWeight's defer.
                var gain = NewWeight(env, candidate, taken, env.Explosives - step - 1, true);

                // Kept in order, best first, over a list of four. An insertion sort over four
                // entries beats sorting a few thousand candidates to read the top of the list.
                for (var i = 0; i < pick.Length; i++)
                {
                    if (gain <= pick[i].Gain)
                        continue;

                    for (var j = pick.Length - 1; j > i; j--)
                        pick[j] = pick[j - 1];

                    pick[i] = (candidate, gain);

                    if (held < pick.Length)
                        held++;

                    break;
                }
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
    /// Chains that go and fetch every marker the player insisted on, one per order to fetch them in.
    ///
    /// **This is the only thing in the search that can build a route to a distant requirement, and
    /// without it the marks were decoration on a Grand site.** Nothing else can: greedy picks the
    /// next link by what it adds now, and a marker six hundred grid away adds nothing to any link
    /// within reach of the detonator, so it produces no pull at all until a chain happens to end up
    /// near it. Remnants commits to an ORDER but demands each stop be one hop from the last, so it
    /// silently skips anything that needs bridging. And Insisting moves a single link onto a missed
    /// marker, which cannot be legal when the marker is two or three links past the end of the chain.
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
    /// <summary>
    /// A chain that visits more of the must-takes than the one handed in, or null when there is no
    /// better one to be had.
    ///
    /// **The destroy-and-repair search cannot reach a distant must-take on its own, and this is
    /// what it calls to get there.** Demanded and Insisting both live inside Search, which is
    /// reached only from the score card - so the live path had the 43,255 charge for dropping a
    /// requirement and no operator able to satisfy one. A Craggy Peninsula site sat 335 grid west
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
    internal static List<Vector2> MustTakeTour(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, int worker = 0)
    {
        if (env == null || env.Musts <= 0 || candidates == null)
            return null;

        var had = chain == null ? 0 : Rate(env, chain).Held;

        if (had >= env.Musts)
            return null;

        var found = new List<(List<Vector2> Tour, int Held, double Worth)>();

        foreach (var tour in Demanded(env, candidates))
            found.Add((tour, Rate(env, tour).Held, Score(env, tour)));

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

        return equal[worker <= 0 ? 0 : worker % equal.Count].Tour;
    }

    private static IEnumerable<List<Vector2>> Demanded(PlanEnvironment env, List<Vector2> candidates)
    {
        var found = new List<int>();

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

            orders = new List<List<int>> { found };
        }

        foreach (var order in orders)
        {
            var chain = new List<Vector2>();
            var taken = new HashSet<int>();
            var from = env.Origin;

            foreach (var want in order)
            {
                // Already caught by a link laid on the way to an earlier one, which is common when
                // two of them sit near each other.
                if (taken.Contains(want))
                    continue;

                Fetch(env, candidates, chain, taken, ref from, want, asker: Touring);
            }

            if (chain.Count == 0)
                continue;

            // The rest greedily, which is now only being asked where the monsters are.
            yield return Greedy(env, candidates, null, 1, null, chain);
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

    internal const int Stepping = 1;

    internal const int Bridging = 2;

    private static readonly int[] _fetchWon = new int[3];
    private static readonly int[] _fetchStuck = new int[3];
    private static readonly int[] _fetchSpent = new int[3];
    private static readonly int[] _fetchStuckHops = new int[3];
    private static readonly int[] _fetchSpentHops = new int[3];

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
            var names = new[] { "tour", "step", "reach" };
            var said = new List<string>();

            for (var i = 0; i < 3; i++)
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
        for (var i = 0; i < 3; i++)
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
    /// <summary>
    /// The best the chain can do once one marker has changed, without searching the whole site.
    ///
    /// **Improve answers "any spot, any order" and that is what makes the reroll advice unusable on a
    /// Grand site.** It is six rounds of Sweep, and Sweep is every link against every candidate -
    /// fifteen against several hundred, six times, per sampled outcome, per candidate remnant: ten to
    /// twenty seconds for one remnant's advice with the player standing still.
    ///
    /// Almost all of what a roll is worth needs neither of those freedoms. Propagation dominates the
    /// objective - content 1,690 against propagation 12,630 on one site - and propagation flows
    /// forward from the link that carries it, so a rune that has just become valuable is realised by
    /// moving its remnant EARLIER. That is Order, at O(links squared) with a folded prefix tally.
    ///
    /// What Order cannot do is bring in a marker the chain does not catch, which is the case a roll
    /// most often creates. So the one marker that changed is offered a place: the spots that catch it
    /// are tried against each position, the illegal ones are dropped, and the survivor is re-ordered.
    ///
    /// Roughly two orders of magnitude under Improve. What it gives up is a DIFFERENT spot becoming
    /// worth taking for reasons unrelated to this marker, which the next full solve finds anyway.
    /// </summary>
    /// <param name="marker">
    /// Which target changed. Its own spots are the only ones offered a place, which is the whole of
    /// the saving: the question is not "what is the best chain now" but "what does this marker
    /// change".
    /// </param>
    /// <param name="links">
    /// How many links a substitution may disturb. One replaces a single link, which usually fails
    /// because a marker off the route is off it precisely because the reach cannot get there; two
    /// lets the following link move as well, which is what recovers most of those. See
    /// SolverSettings.RollSubstitutionLinks.
    /// </param>
    internal static List<Vector2> Restitched(PlanEnvironment env, List<Vector2> chain, int marker,
        List<Vector2> candidates, int links)
    {
        if (env == null || chain is not { Count: > 0 })
            return chain;

        var best = Order(env, Copied(chain));
        var score = Score(env, best);

        if (marker < 0 || marker >= env.Targets.Count || candidates == null)
            return best;

        var covers = CoverageOfEnvironment(env);

        // Already caught, so there is nothing to bring in and the ordering above is the whole answer.
        foreach (var at in chain)
        {
            foreach (var index in covers.Of(at))
            {
                if (index == marker)
                    return best;
            }
        }

        // The spots that would catch it, richest first. A handful, because they differ from each
        // other only in what ELSE they take and the best few cover that.
        var offers = new List<(Vector2 At, double Worth)>();

        foreach (var candidate in candidates)
        {
            var takes = false;
            var worth = 0d;

            foreach (var index in covers.Of(candidate))
            {
                takes |= index == marker;
                worth += WorthOfTarget(env.Targets[index]);
            }

            if (takes)
                offers.Add((candidate, worth));
        }

        if (offers.Count == 0)
            return best;

        offers.Sort(static (a, b) => b.Worth.CompareTo(a.Worth));

        var tried = Math.Min(offers.Count, Offers);
        var work = new List<Vector2>(chain.Count);

        for (var o = 0; o < tried; o++)
        {
            var offer = offers[o].At;

            for (var k = 0; k < chain.Count; k++)
            {
                work.Clear();
                work.AddRange(chain);
                work[k] = offer;

                // One link. Cheap, and enough whenever the marker sits within reach of the link
                // before the one it replaces.
                if (Legal(env, work) && Keep(env, work, ref best, ref score))
                    continue;

                if (links < 2)
                    continue;

                // Two. The link AFTER the substitution is what the new spot most often cannot reach,
                // so that is the one allowed to move - to the best few spots that restore the chain
                // rather than to every candidate in the site, which is the loop this exists to avoid.
                var next = k + 1;

                if (next >= chain.Count)
                    continue;

                var reached = 0;

                foreach (var candidate in candidates)
                {
                    if (reached >= Offers)
                        break;

                    if (!Reaches(env, offer, candidate))
                        continue;

                    work.Clear();
                    work.AddRange(chain);
                    work[k] = offer;
                    work[next] = candidate;

                    if (!Legal(env, work))
                        continue;

                    reached++;
                    Keep(env, work, ref best, ref score);
                }
            }
        }

        return best;
    }

    /// <summary>How many spots and how many repairs a substitution tries. See Restitched.</summary>
    private const int Offers = 3;

    /// <summary>
    /// Whether every link of a chain is reachable from the one before it and spaced from the rest.
    ///
    /// The same two questions Repair.Sound asks, here because Restitched asks them per trial and a
    /// substitution that breaks either is not a chain. See Planner.Reaches and Planner.Spaced.
    /// </summary>
    private static bool Legal(PlanEnvironment env, List<Vector2> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (!Reaches(env, i == 0 ? env.Origin : chain[i - 1], chain[i]))
                return false;

            if (!Spaced(env, chain, chain[i], i))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Orders a trial and keeps it if it beats what is held. Says whether it did, so a caller can
    /// stop trying to repair something that already worked. See Restitched.
    /// </summary>
    private static bool Keep(PlanEnvironment env, List<Vector2> trial, ref List<Vector2> best,
        ref double score)
    {
        // Ordered before it is judged, because the substitution is what changed which runes
        // propagate from where - an unordered trial is the same chain scored in the wrong order.
        var ordered = Order(env, Copied(trial));
        var worth = Score(env, ordered);

        if (worth <= score)
            return false;

        best = ordered;
        score = worth;

        return true;
    }

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
    private static int _asPlaced;
    private static double _asPlacedBest;
    private static List<Vector2> _asPlacedChain;
    private static int _asPlacedSlipped;
    private static double _endgameBest;
    private static int _endgameSlipped;

    /// <summary>What the two "as if placed" paths actually managed, for the dump.</summary>
    public static string Committing =>
        $"as-placed restarts {_asPlaced} best {_asPlacedBest:N1}" +
        (_asPlacedSlipped > 0 ? $" ({_asPlacedSlipped} LOST THE OPENING)" : "") +
        (_asPlacedChain == null
            ? " (no chain)"
            : " [" + string.Join(" ", _asPlacedChain.ConvertAll(p => $"({p.X:0},{p.Y:0})")) + "]") +
        $"; endgame best {_endgameBest:N1}" +
        (_endgameSlipped > 0 ? $" ({_endgameSlipped} PINS SLIPPED)" : "");

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
    /// Moves one link of a chain onto a marker the player insisted on and the chain is missing.
    ///
    /// **A must-take is a cliff in the objective and hill climbing does not climb cliffs.** The
    /// weight put on an insisted marker is larger than the whole site, so a chain that reaches it
    /// beats one that does not by more than everything else combined - but getting there usually
    /// means moving a link a long way, and every intermediate position is worse than where it
    /// started. Sweep, Order, Reverse and Shift all move one link and keep the result only if it
    /// improves, so none of them can cross the valley. The chain sits one adjustment away from a
    /// twelve thousand point gain, which a person looking at it can see and the search cannot.
    ///
    /// Observed exactly that: run four missed a marked henge and scored 75,122, run five found it
    /// and scored 87,425 with a chain no harder to walk. The difference was which random restart
    /// happened to open near it - which is to say, luck.
    ///
    /// So it is asked directly rather than waited for. For each insisted marker the chain misses,
    /// every spot that would catch it is tried in place of every existing link, and the best legal
    /// swap is kept. That is one pass over the candidates per missing marker, which is nothing next
    /// to a restart, and it only runs at all on a site where something has been insisted on.
    ///
    /// Null when there is nothing insisted, nothing missing, or no legal swap - and the caller
    /// scores what comes back rather than trusting it, because a swap that takes the marker and
    /// strands the rest of the chain is not an improvement.
    /// </summary>
    private static List<Vector2> Insisting(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain) =>
        Onto(env, candidates, chain, Missing(env, chain, insisted: true, keep: int.MaxValue),
            int.MaxValue);

    /// <summary>
    /// The same move, made on the plugin's own initiative rather than on being told.
    ///
    /// **The must-take key is a person doing an operator the search does not have, and it works.**
    /// Marked by hand, the score went from about fourteen and a half thousand to fifteen and a half
    /// - and that is the PLAIN score, with the insisted bonus taken out, so the chain really is
    /// seven per cent better by the plugin's own objective. The weights were never the problem. The
    /// search was losing it, and a person could see the move at a glance.
    ///
    /// What they were doing is not mysterious: pick something valuable the chain walks past, and
    /// make one link go and get it. That is a relocate-to-cover, and it is the one move none of the
    /// local operators can make - see Insisting - because the link has to cross ground where every
    /// intermediate position scores worse.
    ///
    /// So it is done without being asked, for the richest few things the chain is missing. The score
    /// still decides: a swap that grabs a chest and strands the chain behind it is refused like any
    /// other. All this changes is which moves get TRIED, which is exactly what the key was
    /// supplying by hand.
    ///
    /// Bounded, because it is not free: a few targets, a few spots each, and not every round. The
    /// restarts are still what finds a different shape of chain; this is what stops a good shape
    /// being abandoned one link short of a better one.
    /// </summary>
    private static List<Vector2> Chasing(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain) =>
        Onto(env, candidates, chain, Missing(env, chain, insisted: false, keep: Chased), Reaching);

    /// <summary>How many of the richest missed targets to go after at once. See Chasing.</summary>
    private const int Chased = 3;

    /// <summary>How many spots per missed target to try. See Chasing.</summary>
    private const int Reaching = 10;

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

    /// <summary>
    /// Moves one link of the chain onto each of a set of targets it is missing, keeping the best.
    /// </summary>
    /// <param name="cap">How many covering spots to try per target, or everything.</param>
    private static List<Vector2> Onto(PlanEnvironment env, List<Vector2> candidates,
        List<Vector2> chain, List<int> wanted, int cap)
    {
        if (chain.Count == 0 || wanted.Count == 0)
            return null;

        var covers = CoverageOfEnvironment(env);

        List<Vector2> best = null;
        var most = Score(env, chain);

        // The links before the one being moved are untouched by moving it, and the loop below walks
        // those positions in order - so the prefix is wound forward rather than rebuilt for each.
        // Reset per spot, because each spot starts again from the top of the chain. See Wind.
        var tally = Begin(env, chain.Count);
        var work = new List<Vector2>(chain.Count);

        foreach (var t in wanted)
        {
            var tried = 0;

            foreach (var spot in candidates)
            {
                if (tried >= cap)
                    break;

                var catches = false;

                foreach (var index in covers.Of(spot))
                {
                    if (index != t)
                        continue;

                    catches = true;

                    break;
                }

                if (!catches)
                    continue;

                tried++;

                work.Clear();
                Unfold(env, tally);

                for (var i = 0; i < chain.Count; i++)
                {
                    if (chain[i] == spot)
                        continue;

                    var was = chain[i];

                    chain[i] = spot;

                    // The head up to this link is the same whichever spot is being tried into it.
                    Wind(env, tally, work, chain, i);

                    // Legal all the way through: the link before it has to reach it, it has to
                    // reach the link after, and it must not sit on top of another.
                    if (Walkable(env, chain))
                    {
                        var worth = WorthOfChainWithTail(env, tally, work, chain, i);

                        if (worth > most)
                        {
                            most = worth;
                            best = Copied(chain);
                        }
                    }

                    chain[i] = was;
                }
            }
        }

        return best;
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
            foreach (var candidate in Near(candidates, before, env.Reach))
            {
                if (candidate == here)
                    continue;

                // The cheap tests first, so a candidate on the far side of the site costs two
                // distance checks rather than a walk of every marker in the dig site - and the
                // ground between is only walked for one that survives them.
                if (Vector2.DistanceSquared(before, candidate) > env.Reach * env.Reach)
                    continue;

                if (after != null &&
                    Vector2.DistanceSquared(candidate, after.Value) > env.Reach * env.Reach)
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
                // **A barrel takes everything its own blast reaches, and so does every barrel that
                // reaches.** So what a link catches is not the coverage index alone: anything in it
                // that detonates adds its closure to the same step, and a barrel inside that closure
                // adds its own. Walked with a worklist rather than another loop over the site, since
                // nearly every link catches no barrel at all and pays nothing for the possibility.
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

                    if (target.Sets is { Length: > 0 } sets)
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
                    musts++;

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
        var count = env.Targets.Count;
        var wide = Math.Max(links, 1);
        var shape = (count, wide, env.Relevant?.Length ?? -1, env.Scoped?.Length ?? -1);

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
        var count = env.Targets.Count;

        return new Running
        {
            Step = new int[count],
            Seen = new int[count],
            Touched = new int[count],
            Added = new int[Math.Max(links, 1) + 1],
            Monsters = new float[Math.Max(links, 1)],
            Tagged = Fresh(env, links),
            Classed = Owning(env, links),
            Counted = Owning(env, links),
            Visit = 1,
        };
    }

    /// <summary>
    /// Adds one link, and returns what the chain is worth with it.
    ///
    /// The coverage is folded in; the settling is redone, because it depends on the whole chain.
    /// </summary>
    internal static double Push(PlanEnvironment env, Running tally, List<Vector2> chain, Vector2 at)
    {
        Fold(env, tally, chain, at);

        var running = Price(env, tally, chain);

        return running;
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
    /// ask once. Push is that pair, kept for the callers that add one link and want its answer, and
    /// it is the audited path - see Audit, which is why the pair rather than the halves carries the
    /// check.
    ///
    /// The step a link is folded at is its position in the chain, so a run must be folded in
    /// increasing order: the coverage index records which link was FIRST to catch each marker, and
    /// first means the lowest step rather than the earliest call.
    /// </summary>
    internal static void Fold(PlanEnvironment env, Running tally, List<Vector2> chain, Vector2 at)
    {
        var covers = CoverageOfEnvironment(env);
        var s = chain.Count - 1;

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

        foreach (var i in covers.Of(at))
        {
            if (tally.Seen[i] == tally.Visit)
                continue;

            var target = env.Targets[i];

            tally.Seen[i] = tally.Visit;
            tally.Step[i] = s;
            tally.Content += target.Weight;
            tally.Covered++;

            if (target.Must)
                tally.Musts++;

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

        tally.Travel += Vector2.Distance(s == 0 ? env.Origin : chain[s - 1], at);
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
    }

    /// <summary>What the chain in the tally is worth, without changing it. See Fold.</summary>
    internal static double Price(PlanEnvironment env, Running tally, List<Vector2> chain) =>
        Settle(env, chain, tally.Step, tally.Seen, tally.Visit, tally.Monsters, tally.Tagged,
            tally.Classed, tally.Counted, tally.Touched, tally.Found, tally.Content, tally.Covered,
            tally.Musts, tally.Travel, false).Total;

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
        var covers = CoverageOfEnvironment(env);
        var s = chain.Count;

        foreach (var i in covers.Of(at))
        {
            if (tally.Seen[i] != tally.Visit || tally.Step[i] != s)
                continue;

            var target = env.Targets[i];

            tally.Seen[i] = 0;
            tally.Content -= target.Weight;
            tally.Covered--;

            if (target.Must)
                tally.Musts--;
        }

        tally.Found = tally.Added[s];
        tally.Travel -= Vector2.Distance(s == 0 ? env.Origin : chain[s - 1], at);
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
            (int X, int Y) owner = default, bool rune = false, bool flat = false)
        {
            if (weight <= 0f || string.IsNullOrEmpty(id))
                return;

            // A flat effect always lands on something named - it comes from an effect's target - so
            // it never takes the untagged route, and the key below can assume a tag.
            if (flat && tag < 0)
                tag = Tags.Monsters;

            var band = GroupIndexOfEffect(env, id);

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
            if (_credited == null || _credited.Length < chain.Count)
                _credited = new double[Math.Max(chain.Count, 16)];

            for (var k = 0; k < chain.Count; k++)
                _credited[k] = 0d;

            RuneTallyByRemnant.Clear();
            Chosen.Clear();
            Rankings.Clear();
            _ratedAside?.Clear();
            PropagationTrailByRemnant.Clear();
            _lifting.Clear();
        }

        // How many remnants have ordinary runes worth counting, for the pass after the loop.
        var locals = 0;

        var switches = 0;
        var repeated = 0d;

        void Credit(string id, float weight)
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

        for (var i = 0; i < groups * width * links; i++)
            rates[i] = 0f;

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
        var lifts = Grow(_lifts, Math.Max(links, 1));

        _lifts = lifts;

        for (var i = 0; i < links; i++)
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
        void Rate(int band, int tag, int at, float percent, string what = null)
        {
            if (percent <= 0f || at < 0 || at >= links)
                return;

            // **An empowering rate never enters a group.** It is not a share of anything landing on
            // a monster - it is a multiplier on the whole of what does - so it is held aside and
            // applied once, after the groups have compounded. See PlanEnvironment.Empowering.
            if (band == env.Empowering)
            {
                lifts[at] += percent / 100f;

                return;
            }

            var k = Indexed(env, tag < 0 ? Tags.Monsters : tag);

            if (k < 0)
                return;

            rates[(band * width + k) * links + at] += percent / 100f;

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
                    Rate(band, Tags.Monsters, 0, percent, $"banked from ({from.X},{from.Y})");

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

        // **Cleared, because this is per-thread scratch reused on every score.** A marker with no
        // ordinary runes reads whatever the last chain left at its index otherwise, and attaches
        // somebody else's runes to its waves.
        _localsOf = Grow(_localsOf, found);

        for (var t = 0; t < found; t++)
            _localsOf[t] = null;

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

            var choice = env.Targets[i].Best(after[step[i]], monsters[step[i]],
                env, taggedAfter, step[i], distinct, out var chose);

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
                    (choice.Locals?.Length ?? 0) + (choice.Runes?.Length ?? 0),
                    choice.Carries,
                    DiscountedForDuplicates(env, target, choice.Runes, choice.Carries, step[i], distinct),
                    choice.Local,
                    DiscountedForDuplicates(env, target, choice.Locals, choice.Local, step[i], distinct),
                    choice.Runes ?? [],
                    target.Recipes is { Length: > 0 } ids && chose >= 0 && chose < ids.Length
                        ? ids[chose] ?? ""
                        : "",
                    choice.Reward);
            }

            content += choice.Reward;

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
                _localCount[locals] = choice.Locals?.Length ?? 0;
                _localReach[locals] = after[step[i]];
                _localAt = Grow(_localAt, locals + 1);
                _localAt[locals] = step[i];
                locals++;
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
                Book(rune, percent, got, tag, step[i], CellKeyOf(env.Targets[i].Grid), true, flat);
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
                    if (weight > 0f && id != null)
                        Book(id, weight, after[step[i]], -1, step[i],
                            CellKeyOf(env.Targets[i].Grid), true);
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
                        $"({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0}) carries");
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

                Credit(id, weight);
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
        for (var r = 0; r < distinct; r++)
        {
            if (_carriedFlat != null && r < _carriedFlat.Length && _carriedFlat[r])
                Flat(_carriedTag[r], _carriedAt[r], _carriedWeights[r]);
            else
                Rate(_carriedGroup[r], _carriedTag[r], _carriedAt[r], _carriedWeights[r]);
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

        for (var i = 0; i < groups * width; i++)
            prefix[i] = 0f;

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
        var lifted = 0d;

        if (detail)
            _liftedFrom = -1;

        for (var s = 0; s < links; s++)
        {
            for (var i = 0; i < groups * width; i++)
                prefix[i] += rates[i * links + s];

            for (var k = 0; k < width; k++)
                flatly[k] += flats[k * links + s];

            lifted += lifts[s];

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

                RecordFactorsAtLink(env, s, prefix, groups, width, lifted, distinct);
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
                var paid = weight * (Multiplied(prefix, null, groups, width, c, lifted) - 1d) +
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

                var own = LocalSharesOfMarker(env, _localsOf, t, groups, s, distinct, out var mineLift);

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

                foreach (var (mask, part, many) in waving)
                {
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
                    // **The lift still reaches relic shares as well as rune ones**, which it always
                    // did and which is not obviously right - "Runes gain" names runes. Confining it
                    // would need per-share provenance beside rates, since a relic and a rune can be
                    // typed into the same stat group, so it is left as it was rather than changed
                    // twice in one pass.
                    var paid = part * (Multiplied(prefix, own, groups, width, cls,
                                  Math.Max(lifted, mineLift)) - 1d) +
                               many * Flatly(flatly, width, cls);

                    propagation += paid;
                    CreditToLink(detail, s, paid);

                    if (detail)
                        pooled += part * Added(prefix, own, groups, width, cls);
                }
            }
        }

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

                var mine = already.Best(after[hit], monsters[hit], env, taggedAfter, hit, 0, out _);

                _localCount = Grow(_localCount, locals + 1);
                _localReach = Grow(_localReach, locals + 1);
                _localRunes = Grow(_localRunes, locals + 1);
                _localGrid = Grow(_localGrid, locals + 1);
                _localOwn = Grow(_localOwn, locals + 1);

                _localCarry = Grow(_localCarry, locals + 1);
                _localCarry[locals] = mine.Runes;
                _localRunes[locals] = mine.Locals;
                _localGrid[locals] = ((int)MathF.Round(already.Grid.X),
                    (int)MathF.Round(already.Grid.Y));
                _localOwn[locals] = mine.Runes?.Length ?? 0;
                _localCount[locals] = mine.Locals?.Length ?? 0;
                _localReach[locals] = after[hit];
                _localAt = Grow(_localAt, locals + 1);
                _localAt[locals] = hit;
                locals++;
            }
        }

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

            var factors = new string[chain.Count];

            for (var k = 0; k < chain.Count && _factored != null && k < _factored.Length; k++)
                factors[k] = _factored[k] ?? "";

            Factors = factors;
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

        return new Verdict(content, propagation, travel, covered, steps, each,
            Math.Max(0, env.Musts - musts), env.Refused, musts);
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

        var found = new (string, float)[chosen.Length];
        var count = 0;

        foreach (var id in chosen)
        {
            foreach (var (known, weight) in table)
            {
                if (!string.Equals(known, id, StringComparison.OrdinalIgnoreCase))
                    continue;

                found[count++] = (known, weight);

                break;
            }
        }

        return count == found.Length ? found : found[..count];
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
            if (id == null || !IsRuneAlreadySent(env, id, cell, at, distinct))
                continue;

            foreach (var (known, weight) in held)
            {
                if (!string.Equals(known, id, StringComparison.OrdinalIgnoreCase))
                    continue;

                struck += weight;

                break;
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
            if (from != cell && string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        if (detail && _credited != null && at >= 0 && at < _credited.Length)
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
        double lifted, int distinct)
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

            var factor = 1d + add * (1d + lifted);

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

            if (lifted > 0d)
            {
                said.Append(" empowered x")
                    .Append((1d + lifted).ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" -> +")
                    .Append((add * (1d + lifted) * 100d).ToString("0.##", CultureInfo.InvariantCulture))
                    .Append('%');
            }

                said.Append("  -> x")
                    .Append(factor.ToString("0.####", CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            var mult = Multiplied(prefix, null, groups, width, cls, lifted);

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

                if (_carriedRune[r])
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
            var wasted = 0;

            foreach (var (id, worth) in _localRunes[n] ?? [])
            {
                if (id == null || !IsRuneBookedUpstream(env, id, mineAt, distinct))
                    continue;

                wasted++;
            }

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
                    if (!_carriedRune[r] || _carriedRich[r] <= 0f || _carriedWeights[r] <= 0f ||
                        _carriedFrom[r] > mineAt)
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
                    if (from != _localGrid[n] &&
                        !arriving.Contains(name, StringComparer.OrdinalIgnoreCase))
                        arriving.Add(name);
                }

                // **And of what it BANKED**, which is booked nowhere and so appears in no Firsts. A
                // remnant under a placed explosive sends its runes forward as plain rates - see
                // PlanEnvironment.BankedRunes - so without this it read as the source of nothing while
                // every later remnant inherited from it.
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
                    Sockets: (_localRunes[n]?.Length ?? 0) + _localOwn[n],

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
                    Inherited: Math.Max(0, runes - adds.Length) + BankedRunesReaching(env, _localGrid[n]),

                    // Plus a local rune the banked set is already sending here, which the booked
                    // check above cannot see. See PlanEnvironment.BankedRunes.
                    Wasted: wasted + spare + IsAlreadyArriving(env, _localRunes[n], _localGrid[n]),
                    FirstSourced: adds,
                    Arriving: arriving.ToArray());
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
            if (from != cell)
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
                if (from != cell && string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
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
    internal readonly record struct RuneTally(int Sockets, int Inherited, int Wasted,
        string[] FirstSourced,
        string[] Arriving = null)
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
    internal static RuneTally RuneTallyOfOption(RuneTally remnant,
        (string Id, float Worth)[] optionLocals, string[] optionPropagates)
    {
        var arriving = remnant.Arriving ?? [];
        var wasted = 0;
        var adds = new List<string>();

        foreach (var (id, _) in optionLocals ?? [])
        {
            if (id != null && arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                wasted++;
        }

        foreach (var id in optionPropagates ?? [])
        {
            if (id == null)
                continue;

            if (arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                wasted++;
            else
                adds.Add(id);
        }

        return new RuneTally((optionLocals?.Length ?? 0) + (optionPropagates?.Length ?? 0), remnant.Inherited,
            wasted, adds.ToArray(), arriving);
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
    internal readonly record struct Picked(string Reward, int Sockets, float Carries, float Kept,
        float Local, float Held, string[] Carrying, string Recipe = "", float Worth = 0f);

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
            if (_carriedBy[r] != cell || _carriedNames[r] == null || _carriedWeights[r] <= 0f)
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

    /// <summary>
    /// Shares that were rated straight into the product instead of being booked, with what each
    /// one is. Detailed passes only. See Rate and RecordFactorsAtLink.
    /// </summary>
    [ThreadStatic]
    private static List<(int Band, int Tag, int At, float Percent, string What)> _ratedAside;

    /// <summary>The ordinary runes of each covered marker, kept until its waves are paid.</summary>
    [ThreadStatic] private static (string Id, float Worth)[][] _localsOf;
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
            var weight = 0d;
            var kinds = new SortedDictionary<string, int>();

            for (var i = 0; i < env.Targets.Count; i++)
            {
                // The stamp as well as the step: the step array is no longer blanked between
                // scorings, so a stale entry from an earlier chain would otherwise read as a marker
                // this link catches.
                if (seen[i] != visit || step[i] != s)
                    continue;

                var target = env.Targets[i];
                var choice = target.Best(after[s], monsters[s], env, sums, s, 0, out _);

                // Worth, not Weight: the number written in the middle of a blast circle is read by
                // a person, and a link that happens to catch the required marker would otherwise
                // claim the whole must-have bonus. See Verdict's Bonus.
                weight += WorthOfTarget(target) + choice.Reward;
                // Content only. What this link passes on is taken from _credited below, which the
            // scoring loop filled as it went - the same arithmetic rather than a second copy of it.

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
                _phaseBytes[i] = 0;
                _phaseCalls[i] = 0;
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
            "candidates", "regions", "greedy", "improve", "narrowed", "evaluate", "reaches",
            "whole (planner)", "whole (repair)", "whole (grasp)", "whole (beam)",
            "repair/tear", "repair/rebuild",
            "improve/sweep", "improve/order", "improve/reverse", "improve/shift",
            "improve/polish",
        };

    internal const int PhaseCandidates = 0;
    internal const int PhaseRegions = 1;
    internal const int PhaseGreedy = 2;
    internal const int PhaseImprove = 3;
    internal const int PhaseNarrowed = 4;
    internal const int PhaseEvaluate = 5;
    internal const int PhaseReaches = 6;

    internal const int PhaseWhole = 7;

    internal const int PhaseRepair = 8;

    internal const int PhaseGrasp = 9;

    internal const int PhaseBeam = 10;

    internal const int PhaseTear = 11;

    internal const int PhaseRebuild = 12;

    internal const int PhaseSweep = 13;

    internal const int PhaseOrder = 14;

    internal const int PhaseReverse = 15;

    internal const int PhaseShift = 16;

    internal const int PhasePolish = 17;

    [ThreadStatic] private static long[] _phaseBytes;

    [ThreadStatic] private static long[] _phaseCalls;

    private static readonly long[] AllPhaseBytes = new long[18];

    private static readonly long[] AllPhaseCalls = new long[18];

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

        public Phase(int id)
        {
            _id = id;

            // What the enclosing phase has banked so far, put aside so this one starts from nothing
            // and can measure its own children.
            _enclosing = _phaseChildBytes;
            _phaseChildBytes = 0L;

            _had = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            var spent = GC.GetAllocatedBytesForCurrentThread() - _had;

            // Everything under this phase was banked into _phaseChildBytes as each child closed, so
            // what is left is what this phase allocated itself.
            (_phaseBytes ??= new long[Phases.Length])[_id] += spent - _phaseChildBytes;
            (_phaseCalls ??= new long[Phases.Length])[_id]++;

            // And this phase, in full, is a child of whatever encloses it.
            _phaseChildBytes = _enclosing + spent;
        }
    }

    /// <summary>What each phase of the search has allocated, and how often it ran. See Phases.</summary>
    public static IEnumerable<(string Name, long Bytes, long Calls)> PhaseTotals()
    {
        for (var i = 0; i < Phases.Length; i++)
            yield return (Phases[i], Volatile.Read(ref AllPhaseBytes[i]),
                Volatile.Read(ref AllPhaseCalls[i]));
    }

    internal static double Score(PlanEnvironment env, List<Vector2> chain) => Evaluate(env, chain).Total;

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
    /// What a blast here would add, counting nothing already covered by an earlier one.
    ///
    /// The greedy pass cannot know how many remnants come after this link, because it has not
    /// chosen them yet - so propagation is valued at the explosives still in hand, which is the
    /// most remnants that could possibly follow. Deliberately optimistic: it makes greedy reach for
    /// a propagating remnant early, which is the right instinct, and the exact objective in
    /// <see cref="Evaluate"/> is what decides whether the chain it built is actually better.
    /// </summary>
    /// <summary>
    /// Chains that visit the remnants first, one for each order they could be visited in.
    ///
    /// The spot chosen for a remnant is the best one that catches it and can be reached from the
    /// link before - "best" by what it adds overall, so a spot that catches the remnant AND a chest
    /// beside it wins over one that catches the remnant alone. What is left of the chain is filled
    /// greedily, which is the right way round: by then the remnants are placed and greedy is only
    /// being asked to find monsters, which is a question it can answer.
    ///
    /// Four remnants at most, so the orderings stay at twenty four. A site with more of them is a
    /// site where the richest four are what the chain will be built around anyway.
    /// </summary>
    private static IEnumerable<List<Vector2>> Remnants(PlanEnvironment env, List<Vector2> candidates)
    {
        var found = new List<int>();

        // **Every one of them, and the cut comes after the ranking.** This used to stop scanning at
        // the first eight that qualified and only then sort by weight, so which remnants a site's
        // seeds were built around was decided by the order the scan happened to file them in. On a
        // site with eleven, three were never candidates for the sort at all - and on this one the
        // three that mattered were the far cluster.
        for (var i = 0; i < env.Targets.Count; i++)
        {
            var target = env.Targets[i];

            // A seed is a route built to reach something, so there is no sense in building one
            // towards a marker no chain may catch. See PlanTarget.Shunned.
            if (target.Shunned)
                continue;

            // The same four tests the other two carrier scans apply. This one asked about
            // combinations and a flat carry only, so a relic whose magnitude lives in its scoped
            // effects - which is now every relic - was not counted as something worth ordering for.
            if (target.Choices is { Length: > 0 } || target.Carries > 0f ||
                target.NonStacking is { Length: > 0 } || target.Spread is { Length: > 0 })
                found.Add(i);
        }

        if (found.Count == 0)
            yield break;

        // **Ranked by what the remnant is actually worth, which is not its Weight.** A remnant's
        // reward lives in its combinations, because which one wins depends on where the chain puts
        // it - so Weight is only what the marker is worth for BEING a remnant, and sorting on it
        // made a six socket monster of a stone rank level with an empty one. Rough(0) adds back the
        // best combination's reward and its local rune, which is the same measure Pull leans on.
        //
        // Then cut to four: the orderings are factorial and the tail of a long remnant list is not
        // what a chain gets built around.
        found.Sort((a, b) => (WorthOfTarget(env.Targets[b]) + env.Targets[b].Rough(0f))
            .CompareTo(WorthOfTarget(env.Targets[a]) + env.Targets[a].Rough(0f)));

        if (found.Count > 4)
            found.RemoveRange(4, found.Count - 4);

        foreach (var order in Orders(found))
        {
            var chain = new List<Vector2>();
            var taken = new HashSet<int>();
            var from = env.Origin;

            foreach (var want in order)
            {
                if (chain.Count >= env.Explosives)
                    break;

                // Already caught on the way to an earlier stop, which is common in a cluster.
                if (taken.Contains(want))
                    continue;

                // **Bridged rather than skipped.** This used to demand a covering spot within one
                // link of the last stop and drop the remnant when there was none - which is every
                // remnant more than one explosive away, so the tour could only ever string together
                // stones that were already neighbours. See Fetch.
                Fetch(env, candidates, chain, taken, ref from, want, asker: Stepping);
            }

            if (chain.Count == 0)
                continue;

            // The rest greedily, which is now only being asked where the monsters are.
            yield return Greedy(env, candidates, null, 1, null, chain);
        }
    }

    /// <summary>
    /// Where the weight of the site lies, for breaking ties between equally good spots.
    ///
    /// **Two spots that catch the same things are not equal, and the code used to treat them as
    /// such** - it kept whichever came first out of the candidate list, which is an accident of
    /// generation order. A blast has a radius, so a piece of content can usually be caught from
    /// anywhere in a band several grid units wide, and every unit of that band spent in the wrong
    /// direction is a unit of reach the next link does not have.
    ///
    /// The right direction is where the rest of the content is. Weighted, so a remnant pulls harder
    /// than a chest, and over everything rather than what is left - the chain has to get to all of
    /// it eventually.
    ///
    /// **A remnant's reward is not in its Weight** - it is one of the choices the search makes,
    /// because which combination wins depends on where the chain puts it. Pulling on Weight alone
    /// therefore valued a remnant at its bare kind weight and made a rich one pull no harder than a
    /// poor one, which is the wrong direction on exactly the sites where the direction matters.
    /// Rough(0) adds back what the remnant is worth where it stands - the best combination's reward
    /// and its local rune, without the propagation, which depends on a chain that does not exist
    /// yet.
    ///
    /// A required remnant carries a weight larger than the rest of the site put together, so it
    /// takes the pull nearly to itself. That is intended: when one marker has to be caught, leaning
    /// every tie towards it is the right lean.
    ///
    /// **Which content pulls is a real choice, and the two answers are far apart** - measured on one
    /// site, 22 grid, which is the same order as the distance this lean is trying to make up. Over
    /// everything, 58 monster markers outnumber the 13 remnants and rares four to one and drag the
    /// centre towards where the filler is, even at low weight each. Over heavy content only, it
    /// points at the skeleton the chain is obliged to reach.
    ///
    /// Heavy by default, because that is the question being asked. Monsters are what propagation
    /// multiplies and that is worth real points - but those points are already counted in each
    /// spot's worth. The pull exists to answer "which way does the chain still owe a visit", and
    /// monsters are what it sweeps up on the way rather than what it is going for.
    /// </summary>
    private static Vector2 Pull(PlanEnvironment env, bool heavy = true)
    {
        var sum = Vector2.Zero;
        var weight = 0f;

        foreach (var target in env.Targets)
        {
            if (heavy && target.Kind is not (TargetKind.Remnant or TargetKind.Elite))
                continue;

            var of = MathF.Max(0f, target.Weight + target.Rough(0f));

            sum += target.Grid * of;
            weight += of;
        }

        // A site with no heavy content at all would otherwise pull towards the origin of the grid,
        // which is not a direction so much as a corner of the map.
        return weight > 0f ? sum / weight : heavy ? Pull(env, false) : Vector2.Zero;
    }

    /// <summary>
    /// Best first, and among near-equals the one nearest the weight of the site.
    ///
    /// **How near counts as equal is the whole question.** Exact equality answers it badly: measured
    /// on a real site, comparing at a tenth of a point moved six of nine rings and moved them 1 to 5
    /// grid, because neighbouring cells differ by fractions as a chest or a monster slips in and out
    /// of the blast. Nothing ties, so nothing leans, and the reach the lean was meant to save is
    /// still spent. The chains that actually score best put their links 13 to 27 grid from these
    /// spots, so a lean of 5 is not a small version of the right answer.
    ///
    /// So the tie is a band rather than a point, and the band is a share of the best worth on offer
    /// rather than a fixed number of points - a tenth of a point means something quite different
    /// against 368 than against 77. Inside the band the nearest to the pull wins outright: the loss
    /// is bounded by the share, and what is bought with it is reach for the link that follows.
    /// </summary>
    /// <param name="best">The best worth in the set being sorted, which sets the size of the band.</param>
    /// <param name="slack">How much of that worth may be given up to lean, as a fraction.</param>
    private static Comparison<(Vector2 At, double Solo)> Better(Vector2 pull, double best = 0d,
        float slack = 0f)
    {
        // A tenth of a point is the floor even with no slack asked for: these are sums over dozens
        // of markers, and two spots catching the identical set can differ in the last bits.
        var band = Math.Max(0.1d, Math.Abs(best) * slack);

        return (a, b) =>
        {
            var by = Math.Abs(a.Solo - b.Solo) <= band ? 0 : b.Solo.CompareTo(a.Solo);

            return by != 0
                ? by
                : Vector2.DistanceSquared(a.At, pull).CompareTo(Vector2.DistanceSquared(b.At, pull));
        };
    }

    /// <summary>
    /// The best worth in a list, for sizing the tie band. Empty lists give nought, which floors it.
    /// </summary>
    private static double Best(List<(Vector2 At, double Solo)> worth)
    {
        var most = 0d;

        foreach (var (_, solo) in worth)
            most = Math.Max(most, solo);

        return most;
    }

    /// <summary>
    /// How many pieces of heavy content one blast would catch: remnants and rares.
    ///
    /// A different measure from what a spot is worth, and the evidence says a more telling one. On
    /// one site the position (1035,552) was the best catch for TWO rares at once, scored a middling
    /// 163.8 against a best of 368.6 - and was used by the three highest scoring chains ever
    /// recorded there. A spot that serves two pieces of heavy content lets a chain spend one link
    /// where it would otherwise need two, and total weight does not say that.
    /// </summary>
    public static int Heavy(PlanEnvironment env, Vector2 at)
    {
        var count = 0;

        foreach (var target in env.Targets)
        {
            if (target.Kind is TargetKind.Remnant or TargetKind.Elite && Catches(env, at, target))
                count++;
        }

        return count;
    }

    /// <summary>The richest spread-out spots, with what each is worth on its own.</summary>
    /// <param name="opening">
    /// Whether the spot has to be reachable in one throw from where the chain starts.
    ///
    /// True for anchors, which are forced as the FIRST link and are worthless if they cannot be
    /// thrown to. False for the drawing, and that distinction was a real bug: with the detonator as
    /// the origin, requiring one throw ranked only the ground within ninety grid of it, so every
    /// ring clustered around the start and the remnants a hundred grid out - perfectly reachable as
    /// a second or third link - were not considered at all.
    /// </param>
    private static List<(Vector2 At, double Worth)> Ranked(PlanEnvironment env,
        List<Vector2> candidates, int most, float spread = 0f, bool opening = true, float slack = 0f, bool heavy = true)
    {
        var worth = new List<(Vector2 At, double Solo)>();
        var none = new HashSet<int>();

        // What the whole chain could span, as a sanity bound on the ones that need no single throw.
        var span = env.Reach * MathF.Max(1, env.Explosives);

        foreach (var candidate in candidates)
        {
            var near = opening
                ? Reaches(env, env.Origin, candidate)
                : Vector2.Distance(env.Origin, candidate) <= span;

            if (near)
                worth.Add((candidate, NewWeight(env, candidate, none, env.Explosives - 1)));
        }

        worth.Sort(Better(Pull(env, heavy), Best(worth), slack));

        var kept = new List<(Vector2, double)>();

        if (worth.Count == 0)
            return kept;

        // Spread by the game's own minimum spacing, not by a blast radius.
        //
        // Two explosives closer than this cannot both exist, so it is the real reason to treat two
        // positions as alternatives rather than as separate options. A blast radius was the first
        // guess and it is far too wide: it thinned the list to "the best spot in each AREA", so once
        // the good areas were used the remaining entries were the best of whatever was left, which
        // on a worked site is almost nothing.
        var apart = spread > 0f ? spread : MathF.Max(1f, env.Apart);

        // And a floor, so the count is a maximum rather than a quota.
        //
        // Asking for eight on a site with three spots worth having produced eight rings, five of
        // them on ground worth sixteen against a best of three hundred and eighty. A list padded to
        // length reads as though the padding is also an answer.
        var floor = worth[0].Solo * 0.1d;

        foreach (var (at, solo) in worth)
        {
            if (kept.Count >= most || solo < floor)
                break;

            var clear = true;

            foreach (var (already, _) in kept)
                clear &= Vector2.Distance(already, at) > apart;

            if (clear)
                kept.Add((at, solo));
        }

        return kept;
    }

    /// <summary>
    /// The spots worth the most on their own, as openings to build a chain around.
    ///
    /// Standalone value, not marginal: what this blast catches with nothing else placed. That is the
    /// right measure for an anchor, because the question being asked is "if the chain must include
    /// this, what does it look like" rather than "what should come next".
    ///
    /// Spread out on purpose. The richest dozen positions on a site are usually the same blast
    /// nudged a grid unit at a time, and twelve seeds that are one seed tell you nothing - so an
    /// anchor has to sit a blast away from every anchor already taken.
    /// </summary>
    private static IEnumerable<Vector2> Anchors(PlanEnvironment env, List<Vector2> candidates)
    {
        foreach (var (at, _) in Ranked(env, candidates, 8))
            yield return at;
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
    /// **A cell's coverage never changes; only which of the markers are still standing does.** The
    /// band scan asks, for every cell of every family's disc, what a blast there would be worth -
    /// and answering that from scratch means testing the cell against all eighty odd markers, about
    /// one and a half million distance tests per redraw. Measured on one site: three hundred and
    /// seventy nine redraws at twenty six milliseconds apiece, ten of the twelve seconds a complete
    /// search took.
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
    /// next site. It used to be reset inside Planner.Search, which Destroy and Repair never enters.
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

                content += MathF.Max(0f, target.Weight);
                monsters += target.MonstersUnearthed;

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
        if (env == null || env.Targets.Count == 0)
            return 0d;

        var content = 0d;
        var monsters = 0d;

        foreach (var target in env.Targets)
        {
            content += MathF.Max(0f, target.Weight);
            monsters += target.MonstersUnearthed;

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
                foreach (var choice in target.Choices)
                {
                    one.Clear();

                    Into(one, env, null, choice.Carries);
                    Into(one, env, null, choice.Local);

                    foreach (var (id, _, percent, _) in choice.Spread ?? [])
                        Into(one, env, id, percent);

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
    /// **Lifted out of Planner.Search, where it was reachable only by the fallback strategy.** The
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
    private static double Multiplied(float[] prefix, float[] extra, int groups, int width, int cls,
        double lift = 0d)
    {
        var mult = 1d;

        for (var g = 0; g < groups; g++)
        {
            var add = extra != null && (cls & 1) != 0 ? extra[g] : 0f;
            var at = g * width;

            for (var k = 0; k < width; k++)
            {
                if ((cls & (1 << k)) != 0)
                    add += prefix[at + k];
            }

            if (add != 0f)
                mult *= 1d + add * (1d + lift);
        }

        return mult;
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
        int groups, int mineAt, int distinct, out double lift)
    {
        lift = 0d;

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
                lift = Math.Max(lift, worth / 100d);

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
        if (!ReferenceEquals(_covered, env))
        {
            _covered = env;
            _covers = new Coverage(env);
        }

        return _covers;
    }

    /// <summary>
    /// What a blast at one cell adds, over the markers not already taken.
    ///
    /// The indexed form of NewWeight: same answer, read from the cell's own coverage list rather
    /// than by testing every marker on the site.
    /// </summary>
    private static double Adds(PlanEnvironment env, Coverage covers, Vector2 at, HashSet<int> taken,
        int downstream)
    {
        var weight = 0d;

        foreach (var i in covers.Of(at))
        {
            if (taken.Contains(i))
                continue;

            var target = env.Targets[i];

            weight += target.Weight + target.Rough(downstream);
        }

        return weight;
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

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (taken.Contains(i))
                continue;

            var target = env.Targets[i];

            if (!target.Wanted)
                continue;

            if (!Catches(env, at, target))
                continue;

            var worth = target.Weight + target.Rough(downstream);

            if (defer && downstream > 0 && target.Weight > 0f)
            {
                var rate = Waiting(env, target, taken);

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

            weight += worth;
        }

        return weight;
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
    private static float Waiting(PlanEnvironment env, PlanTarget target, HashSet<int> taken)
    {
        if (target.Kind is not (TargetKind.Monster or TargetKind.Elite))
            return 0f;

        var best = 0f;

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (taken.Contains(i))
                continue;

            var other = env.Targets[i];

            if (other.Kind != TargetKind.Remnant)
                continue;

            var carries = other.Carries;

            foreach (var choice in other.Choices ?? [])
                carries = MathF.Max(carries, choice.Carries);

            if (carries <= best)
                continue;

            var span = env.Reach + 2f * env.Blast + target.Radius + other.Radius;

            if (Vector2.DistanceSquared(target.Grid, other.Grid) <= span * span)
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
                    $"relocate took {_shifts} moves worth {_shiftGain:N1}, " +
                    $"fetch took {_fetches} moves worth {_fetchGain:N1}";

    /// <summary>
    /// How often the fetch operator paid, and by how much. See Chasing.
    ///
    /// Worth its own pair of numbers rather than folding into relocate: it is the one operator here
    /// that came from watching a person work, and whether it earns its place is a question somebody
    /// will ask. A zero on a site with content left over says it is not finding anything and can be
    /// run less often; a large gain says the search was leaving that much on the table.
    /// </summary>
    private static int _fetches;

    private static double _fetchGain;

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
        var note = verdict.Missed > 0
            ? $"INVALID: {verdict.Missed} of {env.Musts} must-take marker" +
              $"{(env.Musts == 1 ? "" : "s")} cannot be reached by any chain found"
            : "";

        return new Plan(chain, verdict.Total, verdict.Covered, note, catches, verdict.Plain,
            verdict.Missed);
    }
}
