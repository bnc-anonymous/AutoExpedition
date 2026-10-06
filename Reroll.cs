using ExileCore2;
using System;
using System.Drawing;
using System.Numerics;

namespace AutoExpedition;

/// <summary>What to do with a remnant, in one word.</summary>
internal enum Advice
{
    /// <summary>Nothing to say - not a remnant, or nothing readable about it yet.</summary>
    None,

    /// <summary>Worth a Liquid Verisium: the best it offers is below what you would accept.</summary>
    Roll,

    /// <summary>
    /// The reward is under your floor, and the rune it carries forward is what makes up the
    /// difference. Rolling changes which combinations exist, so that rune may not come back.
    /// </summary>
    Risk,

    /// <summary>
    /// Good enough. Take it and move on.
    ///
    /// Either the reward is worth keeping, or the rune it carries forward is - and with several
    /// remnants still to come, a rune can be worth more than the reward being rerolled.
    /// </summary>
    Keep,

    /// <summary>Do not roll this under any circumstances.</summary>
    Lock,

    /// <summary>Rich enough that the chain should be built to reach it.</summary>
    Take,

    /// <summary>
    /// Already rolled, and a remnant can only be rolled once. Nothing to decide.
    ///
    /// Its own verdict rather than silence, because the absence of advice looks like the plugin
    /// having nothing to say about a remnant rather than there being nothing to say.
    /// </summary>
    Rolled,
}

/// <summary>
/// Whether to reroll a remnant, and whether the chain has to reach it.
///
/// The decision made most often in a dig site and the one the plugin was silent about. Every input
/// was already here - the best reward and what it is worth, how many sockets, which runes carry
/// forward and how good they are - and none of it was turned into an answer.
///
/// **Thresholds are in exalts, like everything else inside the plugin.** They used to be converted
/// through the display unit so a number typed in a box meant what the price beside it on screen
/// meant. That traded one kind of confusion for a worse one: switching the readout to Divine moved
/// every threshold on the tab by a factor of two hundred and changed what the plugin decided, which
/// a display setting must never do.
///
/// The propagation guard is the part that is not just a number comparison. A remnant offering
/// nothing but carrying Opulent forward is not a remnant to reroll, because the roll changes which
/// combinations exist and the rune may not survive it.
///
/// **A roll costs a Liquid Verisium, and replaces the whole remnant.** Both halves matter. The cost
/// means nothing here is a comparison against zero; the replacement being a whole new remnant -
/// new rune layout, new sockets, a different set of options - means the value of a roll is a
/// question about what the site generates rather than about the remnant in front of you. That
/// number is not available yet, so <see cref="Bar"/> reports what a roll must beat instead of
/// pretending to know what it will return.
///
/// **How much that is worth depends on how many remnants come after it, and that is knowable.** The
/// rune weight is defined per remnant it carries forward TO, so the value of keeping it is the
/// weight times the number still to come - which the plan says outright when there is one, and the
/// unspent remnants in the site bounded by the explosives left says well enough when there is not.
/// So the guard multiplies rather than shrugging: a good rune on the first of five remnants says
/// KEEP, and the same rune on the last says ROLL, because by then it carries forward to nothing.
/// </summary>
internal static class Reroll
{
    /// <summary>
    /// Whether the player has set this remnant's combination and asked for it to be kept: a combination is chosen and
    /// Overrule already chosen rewards is off. Such a remnant is never advised for a roll, since a roll replaces the
    /// choice the setting says to keep. Not a rolled remnant: Liquid Verisium fixes its combination, so what it is set
    /// to is the game's outcome rather than the player's choice, and it is the plan's pick like any other.
    /// </summary>
    public static bool HoldsPlayerChosenCombination(Target target, AutoExpeditionSettings settings) =>
        target is { Chose.Length: > 0, Rerolled: false } && !Safe.Read(() => settings.Rewards.Overrule.Value, true);

    public static Advice For(Target target, AutoExpeditionSettings settings, Valuation valuation,
        GameController gc, Scan scan, Plan plan)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return Advice.None;

        var worth = target.Rewards[0].Value;

        if (worth <= 0d)
            return Advice.None;

        var rewards = settings.Rewards;

        // The route instruction comes first: "the chain should reach this" is still true of a
        // remnant that has been rolled, because it is about where the explosives go.
        //
        // **Read off the marker rather than re-derived from the threshold.** The threshold marks a
        // rich remnant must take and then stops being consulted, so this asks the one store that
        // knows - which means a remnant somebody has un-marked stops reading TAKE, instead of the
        // word and the plan disagreeing about the same remnant. See Insisted.Automatic.
        if (Insisted.Here.Wants(target.Grid))
            return Advice.Take;

        // Then: a remnant can only be rolled once, so on one that already has been, every verdict
        // below this is advice about a decision that is not available. Without this the advisor
        // cheerfully recommended ROLL on remnants that could not be rolled.
        if (target.Rerolled)
            return Advice.Rolled;

        if (HoldsPlayerChosenCombination(target, settings))
            return Advice.Keep;

        // **Everything below this was four thresholds and is now one calculation.**
        //
        // The old shape asked, in order: is the reward above a lock figure, is it below a roll
        // figure, is the rune worth protecting, is the carried value over a keep figure. Four
        // numbers, each tuned by feel, none of them able to see the chain - so the same remnant got
        // the same verdict whether it sat first in a five link chain with everything downstream of
        // it or last with nothing.
        //
        // Rolling re-scores the whole chain against sampled outcomes instead, so the answer already
        // knows where this remnant sits, which runes the rest of the chain carries, and what a
        // different socket count would do to the route. There is nothing left for a threshold to
        // add. See Rolling.
        //
        // Only the best remnant on the chain reads ROLL: rolling changes the site, so a list of
        // them would be several answers to a question that stops being asked after the first.
        // **Asked of the advice rather than of this remnant's own verdict.** A verdict says what
        // rolling here would be worth; which remnant is being advised is one answer for the whole
        // site, and reading it off a per-remnant flag let the border and the line disagree. See
        // Rolling.Advising.
        var said = Rolling.Here.Of(target.Grid);

        return said == null ? Advice.None
            : Rolling.Here.Advising(target.Grid) ? Advice.Roll : Advice.Keep;
    }

    /// <summary>
    /// What a roll has to return to pay for itself, in exalts. Not a verdict - a bar.
    ///
    /// **The expected value of a roll cannot be computed yet, and the reason is worth recording.**
    /// A roll replaces the WHOLE remnant: new rune layout, new socket count, therefore a different
    /// set of options entirely. So the question "what will I get" is a question about every remnant
    /// the site could have generated, not about the one in front of you - and the table that would
    /// answer it, Expedition2RunesWeight, exposes no weights.
    ///
    /// This was briefly modelled as an average over the current remnant's own other options, which
    /// is a confident answer to the wrong question: those options belong to a rune layout the roll
    /// is about to throw away. Removed rather than left in, because advice computed off the wrong
    /// population is worse than no advice - it cannot be sanity-checked by eye.
    ///
    /// What CAN be said without any distribution is what the roll must beat: whatever you are
    /// holding, plus the cost of the orb. Nothing on this side of the trade is a guess.
    ///
    /// Note this bar is in currency and covers the reward only. A roll also changes the socket
    /// count, which is worth something to the ROUTE rather than to your stash - so a low bar on a
    /// three socket remnant is a better roll than the same bar on a six, and that part is not in
    /// the number.
    ///
    /// Census is what closes the gap - enough observed remnants and the distribution is measured
    /// rather than assumed.
    /// </summary>
    public static double Bar(Target target, AutoExpeditionSettings settings, Valuation valuation,
        Plan plan)
    {
        // Nothing to beat on a remnant that cannot be rolled again.
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0 || target.Rerolled)
            return 0d;

        var have = Missed(target, plan) ? 0d : target.Rewards[0].Value;

        return have + settings.Rewards.RollCost.Value;
    }

    /// <summary>
    /// The weight of the MONSTERS a rune taken here would reach.
    ///
    /// What propagation actually applies to: "added to all Monsters unearthed after this Remnant".
    /// Not remnants, which is what this counted before - a rune is worth what the rest of the chain
    /// kills, so a remnant early in a monster-rich chain carries far more than one at the end.
    ///
    /// From the plan when there is one and this remnant is in it: the monsters its later links
    /// catch, which is the same accounting the planner scores propagation with, so the advice and
    /// the route cannot disagree.
    ///
    /// Without a plan, half the monster weight in the site - a rough stand-in for "some of them are
    /// behind you", and honest about being a guess until a route exists.
    /// </summary>
    /// <summary>
    /// The monster weight the ONE link that catches this remnant unearths.
    ///
    /// What a non-propagating rune reaches. A remnant's runes are modifiers on the monsters its own
    /// explosive turns up - the pack in that blast plus the remnant's own waves - and only the
    /// propagating slot carries past that. So this is the same accounting as
    /// <see cref="Downstream"/> stopped after one link, and the two are used together: a
    /// combination is worth its reward, plus its carried runes over the rest of the chain, plus its
    /// remaining runes over this blast.
    ///
    /// Zero when there is no plan, rather than a site-wide guess. Downstream can guess because half
    /// the site is a defensible stand-in for "the part you have not reached"; there is no such
    /// stand-in for one blast, and a made-up number here would move the reward choice.
    /// </summary>
    public static float Local(Target target, AutoExpeditionSettings settings,
        GameController gc, Scan scan, Plan plan)
    {
        if (plan == null || plan.Points.Count == 0)
            return 0f;

        var here = scan.At(Detonator.DetonatorGridPosition(gc));

        for (var i = 0; i < plan.Points.Count; i++)
        {
            var mine = false;

            foreach (var caught in plan.CaughtBy(i))
            {
                if (Vector2.Distance(caught, target.Grid) < 1f)
                {
                    mine = true;

                    break;
                }
            }

            if (!mine)
                continue;

            var weight = 0f;

            foreach (var caught in plan.CaughtBy(i))
                weight += MonsterWeight(here, caught, settings);

            return weight;
        }

        return 0f;
    }

    public static float Downstream(Target target, AutoExpeditionSettings settings,
        GameController gc, Scan scan, Plan plan)
    {
        var here = scan.At(Detonator.DetonatorGridPosition(gc));
        var explosives = Detonator.ExplosivesInHand(gc);

        if (plan != null && plan.Points.Count > 0)
        {
            var mine = -1;

            for (var i = 0; i < plan.Points.Count && mine < 0; i++)
            {
                foreach (var caught in plan.CaughtBy(i))
                {
                    if (Vector2.Distance(caught, target.Grid) < 1f)
                    {
                        mine = i;

                        break;
                    }
                }
            }

            if (mine >= 0)
            {
                // From this link onwards, not after it: the blast that sets the remnant off
                // unearths monsters too, and they are affected.
                var later = 0f;

                for (var i = mine; i < plan.Points.Count; i++)
                {
                    foreach (var caught in plan.CaughtBy(i))
                        later += MonsterWeight(here, caught, settings);
                }

                return later;
            }

            // In a plan that does not include it. A remnant the chain never sets off is never
            // activated, so whatever it carries is carried to nobody - not the site-wide estimate
            // below, which would have it protecting a rune it will never get the chance to pass on.
            if (plan.Catches != null)
                return 0f;
        }

        // No plan, or this remnant is not in it. Half of what is in the site, standing in for the
        // half a chain has not reached yet.
        var loose = 0f;

        foreach (var other in here)
        {
            loose += other.Kind is TargetKind.Monster or TargetKind.Elite
                ? Weighing.WeightOfTarget(other, settings)
                : Weighing.Waves(other);
        }

        return explosives <= 0 ? 0f : loose / 2f;
    }

    /// <summary>
    /// Whether there is a plan and it goes somewhere else.
    ///
    /// False when there is no plan at all, because then nothing is known about where the chain
    /// will go and every remnant is still a candidate. Only a plan that exists and leaves this
    /// remnant out says anything.
    /// </summary>
    private static bool Missed(Target target, Plan plan)
    {
        if (plan == null || plan.Points.Count == 0 || plan.Catches == null)
            return false;

        for (var i = 0; i < plan.Points.Count; i++)
        {
            foreach (var caught in plan.CaughtBy(i))
            {
                if (Vector2.Distance(caught, target.Grid) < 1f)
                    return false;
            }
        }

        return true;
    }

    /// <summary>The weight of the monster at this spot, or zero when what is there is not one.</summary>
    private static float MonsterWeight(System.Collections.Generic.List<Target> here, Vector2 at,
        AutoExpeditionSettings settings)
    {
        foreach (var target in here)
        {
            if (Vector2.Distance(target.Grid, at) >= 1f)
                continue;

            // A remnant counts for the waves its own detonation brings, which are monsters like
            // any other - and often more of them than the markers around it.
            return target.Kind is TargetKind.Monster or TargetKind.Elite
                ? Weighing.WeightOfTarget(target, settings)
                : Weighing.Waves(target);
        }

        return 0f;
    }

    /// <summary>
    /// What this remnant is worth right now, in exalts. The figure the must take threshold reads.
    ///
    /// Was Required, which answered the threshold question itself - and that made it a second place
    /// deciding what the chain must reach. It now returns the number and lets Insisted.Automatic
    /// decide, so the comparison happens once and its answer is written down. See Insisted.
    /// </summary>
    public static double Worth(Target target, Valuation valuation)
    {
        if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0)
            return 0d;

        // What this remnant is actually worth, which on a rolled one is not the best of its list.
        //
        // **A rolled remnant is fixed**: its runes were rerolled, its sockets may have changed, and
        // the combination it ended on cannot be changed again. Reading the top of the reward list
        // for one of those asks what it could have been worth - and building the whole chain around
        // a reward that is no longer available is the most expensive way to be wrong here, since
        // this is the flag that weights a remnant above everything else in the site put together.
        var worth = target.Rewards[0].Value;

        if (target.Rerolled)
        {
            worth = 0d;

            // Asked by recipe rather than by name: a remnant can offer two combinations under one
            // name and they are not worth the same, so the first match is not the right answer.
            // See Valuation.AlreadySetTo.
            foreach (var reward in target.Rewards)
            {
                if (!Safe.Read(() => valuation?.AlreadySetTo(target.Entity, reward) ?? false, false))
                    continue;

                worth = reward.Value;

                break;
            }
        }

        return worth;
    }

    /// <summary>A threshold of zero is off, not "everything qualifies".</summary>
    private static bool Above(double exalts, float threshold) =>
        threshold > 0f && exalts >= threshold;

    /// <summary>
    /// The verdict as a word, for the score card. NOT for the overlay.
    ///
    /// Nothing is written over a remnant any more. The only thing the HUD says about rolling is a
    /// border round the game's own Liquid Verisium button - which is where the click is, needs no
    /// reading, and is absent exactly when a roll is impossible. A word floating in the world has
    /// to be read, matched to a remnant, and then acted on somewhere else.
    ///
    /// The words remain because the score card is a file read at leisure, and there the difference
    /// between RISK and ROLLED is worth having in a column.
    /// </summary>
    public static string Word(Advice advice) => advice switch
    {
        Advice.Roll => "ROLL",
        Advice.Risk => "RISK",
        Advice.Keep => "KEEP",
        Advice.Lock => "LOCK",
        Advice.Take => "TAKE",
        Advice.Rolled => "ROLLED",
        _ => "",
    };


}
