using ExileCore2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The plan and the explosives actually placed, scored the same way, written to a file.
///
/// What it is for: settling whether a disappointing chain is the search's fault or the objective's.
/// Place a chain by hand, press the key, and both routes come out through the identical objective
/// with the three terms kept apart.
///
/// <code>
/// yours scores HIGHER  -> the search is at fault. It had a better chain available and did not
///                         find it, which is a bug in the search or too little time to run.
/// yours scores LOWER
///   and looks better   -> the objective is at fault. The weights, or the price on a propagating
///                         rune, or the price on walking, do not match what the map is worth to a
///                         player. No amount of searching fixes that; the numbers have to change.
///   and looks worse    -> the plugin is right and the eye was wrong, which happens.
/// </code>
///
/// Appended rather than replaced, so two attempts at the same dig site sit next to each other and
/// can be read against one another.
/// </summary>
internal static class Scorecard
{
    /// <summary>
    /// The whole chain in the whole world against the remaining chain in the reduced one.
    ///
    /// Both halves are printed in full - content and propagation apart, because they fail
    /// differently. Content drifts when a caught marker is removed from one side and not added back
    /// to the other; propagation drifts when a modifier the prefix carries is lost, mis-grouped, or
    /// booked at the wrong reach. Which of the two moved says which half to look at.
    ///
    /// The banked modifiers are listed by name for the same reason: a missing relic and a collapsed
    /// remnant look identical in a total and nothing alike in a list.
    /// </summary>
    private static void Divergence(List<string> text, PlanEnvironment whole, PlanEnvironment reduced,
        Plan plan, List<Vector2> placed)
    {
        text.Add("");
        text.Add("  THE SAME SITE, SCORED BOTH WAYS");

        if (placed.Count == 0)
        {
            text.Add("    nothing placed yet, so there is only one way to score it - place a link " +
                     "and press again to compare");

            return;
        }

        if (reduced == null)
        {
            text.Add("    the reduced environment could not be built, so there is nothing to " +
                     "compare against");

            return;
        }

        // The full chain is what was placed, followed by whatever the plan still has to lay.
        var full = new List<Vector2>(placed);

        foreach (var at in plan?.Points ?? new List<Vector2>())
        {
            if (!full.Contains(at))
                full.Add(at);
        }

        var left = new List<Vector2>(plan?.Points ?? new List<Vector2>());

        var a = Planner.Judge(whole, full);
        var b = Planner.Judge(reduced, left);

        text.Add($"    whole world, whole chain ({full.Count} links over {whole.Targets.Count} markers):");
        text.Add($"      content {a.Content,10:N1}   carried {a.Propagation,10:N1}   =  {a.Plain,10:N1}");
        text.Add($"    reduced world, {left.Count} remaining ({reduced.Targets.Count} markers, " +
                 $"origin moved to ({reduced.Origin.X:0},{reduced.Origin.Y:0})):");
        text.Add($"      content {b.Content,10:N1}   carried {b.Propagation,10:N1}   =  {b.Plain,10:N1}");
        text.Add($"    DIFFERENCE  content {b.Content - a.Content,+10:N1}   " +
                 $"carried {b.Propagation - a.Propagation,+10:N1}   " +
                 $"=  {b.Plain - a.Plain,+10:N1}   (nought if the decomposition is exact)");

        if (reduced.Secured is { Length: > 0 })
        {
            var carried = new List<string>();

            foreach (var (id, tag, percent, band, _) in reduced.Secured)
                carried.Add($"{(id.Length > 0 ? id : "(flat carry)")}[tag {tag}, group {band}] {percent:0.#}%");

            text.Add("    banked from the placed links: " + string.Join(", ", carried));
        }
        else
        {
            text.Add("    banked from the placed links: NOTHING - every modifier the placed blasts " +
                     "carry has been lost");
        }

        text.Add("    what each caught marker gave:");

        foreach (var line in (reduced.Banked ?? "").Split('\n'))
            text.Add(line);
    }

    public static string Write(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc,
        AutoExpeditionSettings settings, Scan scan, Blast blast, Valuation valuation,
        Plan plan, int before)
    {
        // WITH the terrain, because a card that judges chains against a world with no walls in it
        // is not judging the chains the planner has to build.
        //
        // It was built without, and the numbers it produced were badly wrong in a way that read as
        // progress: a search given no reach constraints scored 1,282.8 where the same search in game
        // scored 1,157, and the legality check happily called a chain legal without ever asking
        // whether its links could be thrown. Both were compared against a hand-laid chain and drew
        // conclusions about the search that belonged to the environment.
        var env = Planning.Build(gc, settings, scan, blast, valuation, true, false, out var why, out _);

        if (env == null)
            return why;

        // Both routes judged from the detonator, so neither is let off the walk it made to get to
        // where it starts. See Scoring for why that matters.
        var fair = env with { Origin = Detonator.DetonatorGridPosition(gc) };
        var placed = new List<Vector2>(Detonator.PlacedExplosiveGridPositions(gc));

        // **The same site scored both ways, because the two are supposed to agree and do not.**
        //
        // While explosives are going down the plugin scores a REDUCED problem: the origin moves to
        // the last one placed, the markers already caught are taken out of the world, and what the
        // placed blasts carry forward is re-derived and booked at the first remaining link. That
        // decomposition is sound in principle - propagation only runs forwards, so every rune the
        // prefix booked applies to all of what is left, and the prefix's own payout is a constant
        // nothing later can change.
        //
        // In practice the total drifts down as links are placed on a chain that is being followed
        // exactly, and the reduced problem has ranked two candidate last links the opposite way
        // round from the whole-chain objective. One of the two is wrong and neither says so, and
        // arguing about which from a single number is what several sessions have now been spent on.
        //
        // So both are computed here, over the same ground, and printed beside each other: the whole
        // chain in the whole world, and the remaining chain in the reduced one. The line that
        // matters is the difference - if the decomposition were exact it would be nought.
        var reduced = placed.Count > 0
            ? Planning.Build(gc, settings, scan, blast, valuation, true, true, out _, out _)
            : null;

        try
        {
            var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "score.txt");

            var text = new List<string>
            {
                new string('-', 96),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}   area {Safe.Read(() => gc.Area.CurrentArea.Name, "?")}" +
                $"   level {Safe.Read(() => gc.IngameState.Data.CurrentAreaLevel, 0)}",
                "",
                $"detonator ({fair.Origin.X:0},{fair.Origin.Y:0})   blast {fair.Blast:0.##} grid   " +
                $"reach {fair.Reach:0.#} grid   explosives {Detonator.ExplosivesInHand(gc)} left of " +
                $"{Safe.Read(() => Detonator.Info(gc).TotalExplosiveCount, 0)}",
                $"markers {fair.Targets.Count} in this site, of which {Wanted(fair)} are worth something",
                "",
                "weights: " + Weights(settings),
                "",
            };

            Remnants(text, gc, settings, scan, valuation, plan);

            // The plan as a WHOLE chain: what is already down, then what it says to do next.
            //
            // A plan re-solved part way through holds only the links that are left, so printing it
            // as-is put three explosives' worth of plan beside five explosives' worth of placement
            // and declared the placement better by six hundred points. The note underneath said the
            // two were not comparable, which is true and no use - the comparison is the whole
            // reason the card exists. Same fix as the status line's fraction.
            // The chain the plan was solved FOR: whatever was already down at the moment it solved,
            // then the plan itself.
            //
            // From Planning's own record rather than worked out from whether the plan happens to
            // list a placed spot. That guess was wrong in both directions and this card showed the
            // worse one: a plan re-solved with three links left, against five explosives already
            // down, printed an EIGHT link route on a five explosive map and declared it better by a
            // hundred and eighty two points. The same fix was already made for the status line.
            var whole = new List<Vector2>(placed.GetRange(0, Math.Min(before, placed.Count)));

            whole.AddRange(plan.Points);

            Route(text, "THE PLAN", fair, whole);
            Route(text, "WHAT IS PLACED", fair, placed);
            Legal(text, fair, placed, gc);
            Rivals(text, fair, gc, plan.Points.Count > 0 ? plan.Points.Count : placed.Count);

            if (whole.Count > 0 && placed.Count > 0)
            {
                var planned = Planner.Judge(fair, whole).Total;
                var yours = Planner.Judge(fair, placed).Total;
                var gap = yours - planned;

                text.Add("");
                text.Add(gap > 0.5d
                    ? $"YOURS IS BETTER by {gap:N1}. The search had this available and did not find it."
                    : gap < -0.5d
                        ? $"THE PLAN IS BETTER by {-gap:N1}. If yours still looks better, the weights are wrong, not the search."
                        : $"LEVEL, within {MathF.Abs((float)gap):N1}.");

                if (placed.Count != whole.Count)
                {
                    text.Add($"  note: {placed.Count} placed against {whole.Count} planned, so the two " +
                             "routes are not the same length and the totals are not directly comparable.");
                }

                if (whole.Count != plan.Points.Count)
                {
                    text.Add($"  note: the plan held {plan.Points.Count} links - it was solved part way " +
                             $"through - so the {whole.Count - plan.Points.Count} already placed are " +
                             "counted in front of it to make the two routes the same chain.");
                }
            }

            Divergence(text, fair, reduced, plan, placed);

            text.Add("");
            File.AppendAllLines(path, text);

            return $"score card written to dumps/score.txt";
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the score card: {ex.Message}", 5f);

            return "could not write the score card";
        }
    }

    /// <summary>
    /// Every remnant in the site, and what the plugin believes about it.
    ///
    /// Here because the propagation term has been reading zero on every link of every score card
    /// written so far, and zero has two very different causes. Either these remnants really do pass
    /// nothing worth having - which is a fact about the map - or the slots were never read and the
    /// term that is supposed to make an early remnant worth more than a late one is quietly doing
    /// nothing at all. From the total those look identical. From this they do not.
    ///
    /// "not read" against a remnant the game still has loaded is the bad case, and means the fault
    /// is in here. Against one it has unloaded it means only that nobody has stood near it yet.
    /// </summary>
    private static void Remnants(List<string> text, GameController gc, AutoExpeditionSettings settings,
        Scan scan, Valuation valuation, Plan plan)
    {
        var here = scan.At(Detonator.DetonatorGridPosition(gc));
        var found = 0;

        text.Add("REMNANTS");

        foreach (var target in here)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            found++;

            var carries = Weighing.Carries(target);
            var reward = target.Rewards.Count > 0
                ? $"{target.Rewards[0].Name} {target.Rewards[0].Value:0.##}ex"
                : "not priced";

            var passes = target.Passing == null
                ? target.Live ? "propagation NOT READ (and it is loaded, so that is a fault)" : "propagation not read (not loaded)"
                : target.Passing.Count == 0
                    ? "passes nothing on"
                    : Describe(target.Passing, settings);

            var advice = Reroll.Word(Reroll.For(target, settings, valuation, gc, scan, plan));
            var inherit = Reroll.Downstream(target, settings, gc, scan, plan);

            text.Add($"  ({target.Grid.X,4:0},{target.Grid.Y,4:0})  {(advice.Length > 0 ? advice : "    "),-5} " +
                     $"sockets {target.Sockets}  " +
                     $"weight {Weighing.WeightOfTarget(target, settings),7:N1}  " +
                     $"carries {carries,4:N0}% of {inherit,7:N0} monster weight = {carries / 100f * inherit,7:N1}  " +
                     $"{reward}");
            text.Add($"              {passes}");

            // Which combination the objective would actually take here, and the two halves of why.
            // The planner works in weight and this in exalts, but the two differ only by a positive
            // scale factor, so they cannot pick different combinations - which is the point.
            if (target.Rewards.Count > 0)
            {
                var rate = Options.Carried(settings, valuation, target, gc, scan, plan);
                var near = Options.Locally(settings, valuation, target, gc, scan, plan);
                var money = Options.Money(settings);
                var solved = Options.Solved(target);

                var pick = target.Rewards[solved >= 0
                    ? solved
                    : Options.Take(target.Rewards, rate, near, money)];

                text.Add($"              takes {pick.Name}: {pick.Value * money:N2}ex reward" +
                         $" + {pick.Carries:0.#}% carried worth {pick.Carries * rate:N2}ex + " +
                         $"{pick.Local:0.#}% local worth {pick.Local * near:N2}ex = " +
                         $"{pick.Value * money + pick.Carries * rate + pick.Local * near:N2}ex");
            }

            if (target.Spread.Options > 0)
            {
                text.Add($"              {target.Spread.Options} options, best {target.Spread.Best:0.##}ex, " +
                         $"average {target.Spread.Mean:0.##}ex (unweighted - the game does not expose the odds)");
            }

            // What a roll would have to return to pay for itself. Zero on a remnant that cannot be
            // rolled again, and then there is nothing to say about it.
            if (Reroll.Bar(target, settings, valuation, plan) is var bar and > 0d)
                text.Add($"              a roll must beat {bar:0.##}ex to pay for itself");
        }

        if (found == 0)
            text.Add("  none in this site");

        text.Add("");
    }

    private static string Describe(List<Passes> passing, AutoExpeditionSettings settings)
    {
        var parts = new List<string>();

        foreach (var slot in passing)
        {
            var runes = new List<string>();

            // Every rune, whatever the display threshold: the card is where you check what the
            // threshold is hiding.
            foreach (var rune in Propagation.Ranked(slot))
                runes.Add($"{rune} {Runes.Weight(rune):0.#}");

            parts.Add($"slot {slot.Slot} {(slot.Settled ? "is" : "could be")} {string.Join(" / ", runes)}");
        }

        return string.Join("; ", parts);
    }

    private static void Route(List<string> text, string title, PlanEnvironment env, List<Vector2> chain)
    {
        text.Add(title);

        if (chain.Count == 0)
        {
            text.Add("  nothing");
            text.Add("");

            return;
        }

        var verdict = Planner.Judge(env, chain);

        foreach (var line in verdict.Steps ?? new List<string>())
            text.Add(line);

        if (env.Secured is { Length: > 0 })
        {
            // Named rather than summed. They are separate modifiers now - see Planning.Secured -
            // and what matters about them is which stats they raise, because that is what decides
            // whether the chain's own runes add to them or multiply with them. A single percentage
            // here would be the same collapse the scoring used to do.
            var carried = new List<string>();

            foreach (var (id, _, percent, _, _) in env.Secured)
                carried.Add($"{(id.Length > 0 ? id : "(flat carry)")} {percent:0.#}%");

            text.Add("      (already in hand from what the placed explosives caught, applying to " +
                     "everything this chain unearths: " + string.Join(", ", carried) + ")");
        }

        text.Add($"      content {verdict.Content,10:N1}   carried {verdict.Propagation,10:N1}   " +
                 $"=  {verdict.Total,10:N1}   over {verdict.Covered} markers, " +
                 $"walking {verdict.Walked:N0} grid");
        text.Add("");
    }


    /// <summary>
    /// Runs the other search and traces its answer through the beam.
    ///
    /// **So a diagnosis costs a keypress rather than a hand-laid chain.** The trace needs a chain
    /// known to be good, and the only one available used to be one the player placed themselves -
    /// which meant every reading cost a dig site. The two searches disagree by tens of points on
    /// their own, so the loser's answer is a perfectly good specimen to ask about, and it is free.
    ///
    /// The environment is the site with nothing placed and the full complement of explosives: the
    /// question both searches were originally given.
    /// </summary>
    private static void Rivals(List<string> text, PlanEnvironment env, GameController gc, int links)
    {
        if (links <= 0)
            return;

        var asked = env with
        {
            Explosives = links,
            Origin = Detonator.DetonatorGridPosition(gc),
            Placed = null,
        };

        var restarts = Planner.Search(asked, TimeSpan.FromMilliseconds(1500), TimeSpan.Zero, 0d,
            System.Threading.CancellationToken.None, null);

        if (restarts.Points.Count == 0)
            return;

        var beam = Beam.Search(asked, TimeSpan.FromMilliseconds(1500), TimeSpan.Zero,
            System.Threading.CancellationToken.None, null);

        text.Add("");
        text.Add("THE TWO SEARCHES, ON THE SAME QUESTION");
        text.Add($"  restarts {Planner.Judge(asked, restarts.Points).Total,10:N1}   " +
                 $"over {restarts.Points.Count} links");
        text.Add($"  beam     {(beam.Points.Count > 0 ? Planner.Judge(asked, beam.Points).Total : 0d),10:N1}   " +
                 $"over {beam.Points.Count} links");

        // Whichever did better is the specimen: follow it through the beam and see where it goes.
        var better = beam.Points.Count > 0 &&
                     Planner.Judge(asked, beam.Points).Total >= Planner.Judge(asked, restarts.Points).Total
            ? null
            : restarts.Points;

        if (better == null)
        {
            text.Add("  the beam already matches or beats the restarts, so there is nothing to trace");

            return;
        }

        text.Add("");
        text.Add("WHERE THE BEAM LOSES THE RESTARTS ANSWER");
        text.AddRange(Beam.Trace(asked, better));
    }

    /// <summary>
    /// Whether the planner could have produced this chain at all, link by link.
    ///
    /// **The question that should have been asked first.** A chain laid by hand scored a hundred
    /// points above anything the search found, and every explanation tried was about the SEARCH -
    /// the beam was too narrow, the fan too tight, the reach model too strict. Fixing the reach
    /// model unlocked seventy seven links and moved the answer by one point, which says the gap is
    /// not about what is legal.
    ///
    /// The remaining possibility is that the good chain is not in the space being searched: a spot
    /// that is not a candidate cannot be chosen however long the search runs, and candidates come
    /// from the content geometry rather than from the whole map. This prints the three tests a link
    /// has to pass, so a chain that beats the planner says WHY it was unavailable - or says it was
    /// available all along, which would mean the search is at fault after all.
    /// </summary>
    private static void Legal(List<string> text, PlanEnvironment env, List<Vector2> chain,
        GameController gc)
    {
        if (chain == null || chain.Count == 0)
            return;

        // Judged against the chain ITSELF, with the explosives in the ground taken out of the
        // environment. Spacing is measured against the other links being tested, and every one of
        // these is already down - so left in, each link was "too close" to itself and the whole
        // readout said nothing.
        var loose = env with { Placed = null };
        var candidates = Planner.Candidates(loose, out _, out _);
        var from = loose.Origin;

        text.Add("");
        text.Add("COULD THE PLANNER HAVE CHOSEN THIS?");

        var offered = true;

        for (var i = 0; i < chain.Count; i++)
        {
            var at = chain[i];

            // How far to the nearest position the search was allowed to consider, rather than
            // whether one sits exactly here.
            //
            // Candidates are generated where a blast touches content or where two catch circles
            // cross, and they are snapped to whole grid units. A spot placed by hand lands near one
            // of those, not on it, so an exact test says "not a candidate" about a chain the search
            // could have matched within a grid unit or two - which is a different complaint
            // entirely. The distance is the thing worth reading: two grid is the same chain, fifteen
            // is a part of the site the search was never offered.
            var nearest = float.MaxValue;

            foreach (var candidate in candidates)
                nearest = MathF.Min(nearest, Vector2.Distance(candidate, at));

            var placeable = loose.CanPlace == null || loose.CanPlace(at);
            var reaches = Planner.Reaches(loose, from, at);
            var spaced = Planner.Spaced(loose, chain.GetRange(0, i), at);
            var faults = new List<string>();

            if (!placeable)
                faults.Add("the ground refuses it");

            if (!reaches)
                faults.Add($"out of reach of ({from.X:0},{from.Y:0})");

            if (!spaced)
                faults.Add("too close to an earlier link");

            if (nearest > 1f)
                offered = false;

            text.Add($"  {i + 1,2}  ({at.X,4:0},{at.Y,4:0})  nearest candidate {nearest,5:N1} grid" +
                     (faults.Count > 0 ? "  -  " + string.Join(", ", faults) : "  -  legal"));

            from = at;
        }

        text.Add(offered
            ? "  every link is a candidate the search was free to pick - the gap is the search."
            : "  links sitting away from any candidate are spots the search was never offered, " +
              "however long it runs. A couple of grid is nothing; a dozen is the candidate set.");

        // And if it WAS available, follow it through the search and find out where it is lost.
        //
        // Asked of the site as it stood before any of this was placed: the same number of
        // explosives, thrown from the detonator, with nothing already in the ground. That is the
        // question the search was actually given, and tracing it against a half-spent site would be
        // tracing a different one.
        var asked = loose with { Explosives = chain.Count, Origin = Detonator.DetonatorGridPosition(gc) };
        var trace = Beam.Trace(asked, chain);

        if (trace.Count == 0)
            return;

        text.Add("");
        text.Add("WHERE THE SEARCH LOSES IT");
        text.AddRange(trace);
    }

    private static int Wanted(PlanEnvironment env)
    {
        var wanted = 0;

        foreach (var target in env.Targets)
        {
            if (target.Weight > 0f || target.Carries > 0f)
                wanted++;
        }

        return wanted;
    }

    private static string Weights(AutoExpeditionSettings settings)
    {
        // Straight off the table, which is where every weight lives now. See Wrt.
        static float Of(string id) => Wrt.Of(id)?.Weight ?? 0f;

        return $"remnant {Of("remnant"):0.#}, " +
               $"chest {Of("chest/magic"):0.#}/{Of("chest/normal"):0.#} magic/normal, " +
               $"rare monster {Of("monster/rare"):0.#}, monster {Of("monster/normal"):0.#}, " +
               $"sentry {Of(Wrt.RowOfKind(TargetKind.Sentry)):0.#}";
    }
}
