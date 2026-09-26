using ExileCore2.PoEMemory.FilesInMemory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoExpedition;

/// <summary>
/// The runes a remnant carries forward, put into words.
///
/// Why this is worth drawing at all: a remnant's reward is spent once, but a rune it passes on
/// applies to every remnant left in the chain. So an early remnant offering a good propagating rune
/// is worth more than its own reward says, and a late one is worth exactly what its reward says -
/// the same remnant, a different value, depending only on where in the chain you take it. The game
/// marks the propagating slots with a yellow border and says nothing about whether what is in them
/// is any good; the rune weights are the half it leaves out.
///
/// One place for the wording because there are two callers - the text over a remnant and the
/// combinations window - and the same rune described two ways in two places would be worse than
/// either description on its own.
/// </summary>
internal static class Propagation
{
    /// <summary>
    /// What a propagating slot could put in play, best first.
    ///
    /// Ordered by weight rather than by the order the recipes happened to come out of the table,
    /// because a list of five runes is read from the top and the top is the only part that gets
    /// read. Ties keep table order, so equal runes do not shuffle between frames.
    /// </summary>
    public static List<string> Ranked(Passes passes) =>
        Ranked(passes, 0f);

    /// <summary>As above, keeping only the runes worth <paramref name="above"/> or more.</summary>
    public static List<string> Ranked(Passes passes, float above) =>
        passes?.Runes?
            .Where(id => Runes.Weight(id) >= above)
            .OrderByDescending(id => Runes.Weight(id))
            .ToList() ?? new List<string>();

    /// <summary>
    /// What a remnant's waves are wearing, as one line.
    ///
    /// **"7 (5+4-2) Volcanic"** - seven distinct runes land on this remnant's waves; it holds five
    /// of them in its own sockets, four arrived from earlier links, and two were struck out as
    /// duplicates. Volcanic is what it sends forward.
    ///
    /// The bracket always reconciles to the number in front of it, so nobody does the arithmetic -
    /// the answer is printed first and the terms are there to be glanced at. Sockets rather than
    /// distinct own runes, because a remnant with two Colds holds two and is wasting one, and a
    /// bracket that had already removed it would not add up.
    ///
    /// Names are the PROPAGATING runes only. A local rune's identity changes nothing beyond these
    /// waves, and the count already says how many there are; a propagating rune is what the rest of
    /// the chain inherits, so which one it is decides whether this remnant is worth keeping.
    ///
    /// Nothing at all for a remnant no chain reaches: there is no position, so "inherited" has no
    /// answer and every figure would describe a chain that does not exist.
    /// </summary>
    /// <summary>
    /// **Every one of them, because there are never many.** This took a cap, defaulting to three, so
    /// a remnant propagating more than that had the rest silently dropped. Counted across the site:
    /// a remnant carries one or two propagating slots and no more, so the cap could only ever hide
    /// something and never shorten anything - which makes it a setting whose best value is "off".
    /// </summary>
    public static string Waves(Planner.RuneTally runes) => Waves(runes, runes.FirstSourced);

    /// <summary>
    /// The same line with the names supplied, for a combination that has not been chosen yet.
    ///
    /// The figures belong to the remnant - how many runes land on its waves, split into its own and
    /// its inherited - and they are the same whichever combination is taken. What changes is WHICH
    /// runes it would then be the source of, and in the combinations window that is a different
    /// answer on every row. So the numbers come from the objective and the names from the row being
    /// looked at. See Options and Ids.
    /// </summary>
    public static string Waves(Planner.RuneTally runes, IReadOnlyList<string> sources) =>
        Waves(runes.Sockets, runes.Inherited, runes.Wasted, sources);

    /// <summary>
    /// The same line from figures worked out elsewhere, for a combination not yet taken.
    ///
    /// **Every part of it moves with the combination except what is arriving.** A remnant with six
    /// sockets offers combinations using two of them and combinations using five, so its own count is
    /// a property of the ROW; and which of those runes the chain is already sending decides the
    /// wasted count, which is a property of the row as well. Only the inherited figure is the
    /// remnant's. Reading the chosen combination's three numbers for every row printed one sentence
    /// six times and called it per row. See Options.
    /// </summary>
    public static string Waves(int sockets, int inherited, int wasted, IReadOnlyList<string> sources)
    {
        var total = Math.Max(0, sockets + inherited - wasted);
        var said = $"{total} ({sockets}+{inherited}-{wasted})";

        if (sources is not { Count: > 0 })
            return said;

        var names = new List<string>(sources.Count);

        foreach (var id in sources)
            names.Add(RuneInfo.Called(id));

        return said + " " + string.Join(", ", names);
    }


    /// <summary>
    /// The total weight of the runes one combination would carry forward, per remnant downstream.
    ///
    /// Summed across the propagating slots, because a combination carrying two DIFFERENT good runes
    /// forward really is carrying both - but counted once per rune, because carrying the same rune
    /// in two slots is still one rune active. It used to sum blindly and pay twice for that.
    /// </summary>
    public static float Carried(Expedition2Recipe recipe, List<Passes> passing)
    {
        if (recipe == null || passing == null)
            return 0f;

        var seen = new List<string>();
        var total = 0f;

        foreach (var slot in passing)
        {
            var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(slot.Slot)?.Id, null);

            if (string.IsNullOrWhiteSpace(id) || seen.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;

            seen.Add(id);

            // **A scoped rune is spent through its scope and must not be summed here too.** This
            // total is spent against the monsters the chain unearths; a rune that says it reaches
            // chests is paid against chests in Planner.Settle, and adding it to both would pay it
            // twice. The same rule Weighing.PricedRowOfTarget applies to a discovered object's effects.
            if (Runes.Scoped(id))
                continue;

            total += Runes.Weight(id);
        }

        return total;
    }

    /// <summary>
    /// What one combination's NON-propagating runes are worth, as a percentage.
    ///
    /// The companion to <see cref="Carried"/>, and the half that was missing. A remnant's runes are
    /// modifiers on the monsters that remnant's explosive unearths - the pack in the blast and the
    /// remnant's own waves. The propagating slot is the one that ALSO reaches everything unearthed
    /// afterwards; the rest still apply, they simply stop there.
    ///
    /// Leaving them out said an Opulent in the wrong socket was worth nothing at all, when what it
    /// is worth is sixty per cent of one blast rather than sixty per cent of the rest of the chain.
    /// On a last link, where there is no rest of the chain, that is the entire difference between
    /// the two.
    ///
    /// Distinct by rune id, the same as Carried, and disjoint from it: a slot is either propagating
    /// or it is not, so nothing is counted twice.
    /// </summary>
    /// <summary>The same, as a total only. For the callers pricing rather than counting.</summary>
    public static float Local(Expedition2Recipe recipe, List<Passes> passing) =>
        Locally(recipe, passing).Total;

    /// <summary>
    /// What the ordinary slots are worth to this remnant's own waves, and HOW MANY of them there
    /// are.
    ///
    /// **The count is what concentration needs and a total cannot give.** Five ordinary runes on one
    /// remnant all land on the same waves, so the monsters there wear five modifiers at once - the
    /// most concentrated thing on a dig site. Summed into one percentage that is indistinguishable
    /// from a single rune worth as much, and the objective could not tell the two apart.
    ///
    /// Distinct, because runes do not stack: the same rune in two ordinary slots is one modifier on
    /// those waves, which is the rule the propagating half already follows.
    /// </summary>
    public static (float Total, (string Id, float Worth)[] Runes) Locally(Expedition2Recipe recipe,
        List<Passes> passing)
    {
        var runes = Safe.Read(() => recipe?.Runes, null);

        if (runes == null)
            return (0f, System.Array.Empty<(string, float)>());

        // **The identities, not just the total, because only the chain knows what is wasted.**
        // A rune in an ordinary slot reaches this remnant's own waves - and if something earlier in
        // the chain is already propagating that same rune into them, the copy is worth nothing,
        // because runes do not stack. Nothing here can know that: which runes arrive depends on
        // where this remnant sits and what the links before it chose, and this runs before there is
        // a chain at all.
        //
        // So the names travel with the number and the scoring loop, which does know, strikes out the
        // ones already in play. See Planner's Locally.
        var seen = new List<string>();
        var found = new List<(string, float)>();
        var total = 0f;

        for (var slot = 0; slot < runes.Count; slot++)
        {
            if (passing != null && passing.Any(x => x.Slot == slot))
                continue;

            var id = Safe.Read(() => runes.ElementAtOrDefault(slot)?.Id, null);

            if (string.IsNullOrWhiteSpace(id) || seen.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;

            seen.Add(id);

            // **A local slot reaches this remnant's own waves, which are monsters.**
            //
            // So a scoped rune counts here for whatever share of it reaches monsters, and for
            // nothing else: one saying it reaches excavated chests reaches none of the waves, and
            // one saying monster=30 reaches all of them at thirty percent.
            //
            // **Not skipped outright, which is what this did when scopes went in.** The propagating
            // half is spent per tag in Planner.Settle from the combination's rune list - and that
            // list is the PROPAGATING slots only, so a scoped rune sitting in an ordinary slot was
            // excluded here and never picked up there. It stopped counting at all.
            // **An empowering rune first, because it is worth nothing by the measure below.**
            //
            // Locally asks what share of a rune's scope reaches monsters. An empowering rune's scope
            // names no tag at all - it multiplies the other runes rather than pointing at anything on
            // the ground - so that share is nought, and the test below threw out the one rune whose
            // nought is its defining property. End to end: a six socket remnant read five, the payout
            // never saw it, and changing the multiplier in the table moved nothing on screen.
            //
            // Its percentage travels as its worth so the scoring loop can find it; Planner.Owned takes
            // it out of the groups and applies it as a factor on everything landing on these waves.
            // See Weighing.Empowering.
            if (string.Equals(Weighing.GroupKeyOfEffect(id), Weighing.Empowering,
                    StringComparison.OrdinalIgnoreCase))
            {
                // **From the row's effect, not from its scope cell.** That cell is where an empowering
                // rate lived while it had nowhere honest to go - a bare number pointing at nothing -
                // and Runes.Scope now correctly answers empty for a rune aimed at runes, so reading it
                // here returned nought and dropped the one rune whose nought was never its share. The
                // identical fault this comment already describes, one store later. See Weighing.Lift.
                var lift = Weighing.Lift(id);

                if (lift > 0f)
                    found.Add((id, lift));

                // Deliberately NOT added to the total. That is what the ordinary slots are worth on
                // these waves, and an empowering rune is worth none of it on its own - it multiplies
                // what the others bring. Adding it here would pay for it twice and pay for it on a
                // remnant with nothing to empower.
                continue;
            }

            var worth = Locally(id);

            // Counted only when it is worth something on these waves. A rune scoped entirely to
            // chests sits in an ordinary slot and reaches none of the monsters there, so it is not
            // one of the modifiers they are wearing.
            if (worth <= 0f)
                continue;

            total += worth;
            found.Add((id, worth));
        }

        return (total, found.ToArray());
    }

    /// <summary>What share of a scope reaches monsters, which is all a local slot can reach.</summary>
    /// <summary>
    /// What one rune in an ordinary slot is worth to its remnant's waves.
    ///
    /// The body of Local's loop, lifted out so the reroll sampler can price a rune it has invented
    /// the same way this prices one the game placed. Two copies of this rule would eventually
    /// disagree, and the whole reroll decision is a comparison between them. See Rolling.
    /// </summary>
    public static float Locally(string id) =>
        Runes.Scoped(id) ? Monsterly(Runes.Scope(id)) : Runes.Weight(id);

    private static float Monsterly(string scope)
    {
        var total = 0f;

        foreach (var (tag, percent) in Tags.Scope(scope, out _))
        {
            if (tag == Tags.Monsters && percent > 0f)
                total += percent;
        }

        return total;
    }

    /// <summary>
    /// Which runes one combination would carry forward, by id, without duplicates.
    ///
    /// The companion to <see cref="Carried"/>, which returns the same set added up. Both exist
    /// because the objective needs the total when runes are assumed to stack and the names when
    /// they are not - and the names have to belong to the SAME combination as the total, or the
    /// planner is again pricing one option and propagating another.
    /// </summary>
    public static string[] Ids(Expedition2Recipe recipe, List<Passes> passing)
    {
        if (recipe == null || passing == null)
            return [];

        var found = new List<string>();

        foreach (var slot in passing)
        {
            var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(slot.Slot)?.Id, null);

            if (!string.IsNullOrWhiteSpace(id) && !found.Contains(id, StringComparer.OrdinalIgnoreCase))
                found.Add(id);
        }

        return found.ToArray();
    }

}
