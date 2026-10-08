using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using Graphics = ExileCore2.Graphics;
using RectangleF = ExileCore2.Shared.RectangleF;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Prices in the Runeshape Combinations window.
///
/// This is where the decision is actually made. Everything drawn on the ground is there to get you
/// to the right remnant; this is the moment you pick what it becomes, and the game lists the
/// options with no indication which is worth anything.
///
/// One border, on the best. Bordering several would make the reader compare borders, which is the
/// job the border was supposed to do for them - so every option gets its price and exactly one gets
/// the ring around it.
///
/// Drawn before the panel gate that suppresses the world overlay, because this window IS a panel:
/// the gate that stops rings being drawn through the atlas tree would otherwise stop this too,
/// precisely when it is open.
/// </summary>
internal static class Options
{
    /// <summary>Whether the combinations window is up, waiting for a reward to be chosen.</summary>
    public static bool Open(GameController gc)
    {
        var window = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window, null);

        return window != null && Safe.Read(() => window.IsVisible, false) &&
               Safe.Read(() => window.Options, null) is { Count: > 0 };
    }

    /// <summary>
    /// Which option to take, and why.
    ///
    /// The richest, when anything in the window has a price. The same answer the border is drawn
    /// round, so what an automated click takes is what the overlay was already pointing at.
    ///
    /// When NOTHING is priced there is still an answer, and this is the second half of it: a
    /// preference list, matched against the reward name. That case is not a failure of the price
    /// list - a recipe offering a generic "Unique Belt" has no item for poe.ninja to have an
    /// opinion about - so waiting for the player to decide was waiting for something the plugin
    /// could have been told once.
    ///
    /// Nothing at all when neither applies, because a click that takes the first of several
    /// unpriced options is picking at random with the confidence of a plugin that knows something.
    /// </summary>
    /// <returns>
    /// The option's rect, what it is worth, whether it was chosen by name, and WHICH option it is.
    ///
    /// The index is there because a rectangle is not an identity. Two calls a frame apart read the
    /// element's rect twice, and while the window is opening those two reads do not agree - so
    /// comparing "is the pick the same option as the richest" by rectangle answered no while the
    /// window animated, and drew a second border a few pixels off the first one round the very same
    /// option. Comparing which option it is cannot do that.
    /// </returns>
    public static (RectangleF Rect, double Value, string Name, string Recipe, bool ByName, int Index) Pick(
        GameController gc, AutoExpeditionSettings settings, Valuation valuation, Scan scan, Plan plan,
        Target acting = null, Reward wanted = null)
    {
        WantedAt = -1;
        WantedRect = default;

        if (!Open(gc))
            return (default, 0d, "", "", false, -1);

        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.Options, null);

        // What a carried rune is worth in currency here, and how many remnants would inherit it.
        // Zero for either leaves the choice on the reward price alone.
        // **Whose window this is, from the caller when the caller knows.** Whose identifies the
        // remnant by fingerprinting the option set and falls back to whichever is nearest, which is
        // right when nobody knows and wrong when somebody does: the placement run walked to a
        // particular remnant to open this window, and a second remnant standing closer is no part
        // of that. Seen in a dump - the run acting on a remnant 51.8 grid away while the nearest
        // was 17.5, so the pick was resolved against a list belonging to neither.
        var remnant = acting ?? Whose(gc, scan);
        var carry = Carried(settings, valuation, remnant, gc, scan, plan);

        // **The reward the run already decided on, rather than a fresh answer to the same
        // question.**
        //
        // Deciding twice is how the window came to be opened for a reward that was already ticked.
        // Take weighs price against what a combination carries forward, and the carry rate runs
        // through Reroll.Downstream, which depends on the plan and on how much of it has been
        // placed - so the answer moves between the moment the run judges a remnant set wrongly and
        // the moment the window is up and something has to be clicked. With the rate at nought the
        // richest reward wins; at sixteen exalts a point a zero-price combination that propagates
        // wins instead. Both were computed, seconds apart, and the run walked across a dig site to
        // tick the option that was already ticked.
        //
        // One decision, made where the remnant is judged and carried to where it is acted on.
        // See Placement.Choosing.
        // **The solver's own answer first, because it already made this decision.**
        //
        // The chain on screen was scored with one combination per remnant - see Planner.Chosen - and
        // Take is a second, independent attempt at the same question from different inputs: its rates
        // come from Reroll.Downstream rather than the planner's per-link sums, and it has no notion of
        // a rune the chain is already sending. While neither discounted duplicates the two usually
        // agreed; once the objective started to, they stopped, and the border sat on Expansive Alloy
        // while the plan beside it had been scored on Cyclonic Alloy. A recommendation contradicting
        // the plan it is drawn next to is worse than either answer alone.
        //
        // So the published choice wins where there is one. Take stays as the fallback for what it is
        // the only answer for: a remnant no chain reaches, a window opened before anything is solved,
        // and the placement run's own pre-decided reward, which still arrives as wanted.
        var want = wanted
                   ?? (remnant != null ? Took(remnant) : null)
                   ?? (remnant?.Rewards is { Count: > 0 } rewards
                       ? rewards[Take(rewards, carry,
                           Locally(settings, valuation, remnant, gc, scan, plan), Money(settings))]
                       : null);

        // **No remnant, no answer.** Not "no remnant, so take the most expensive thing in the
        // window" - that was a fallback and it is the reason a green border once sat on a
        // combination nobody had chosen. Which remnant this window belongs to is the first half of
        // the question; without it the second half is not a worse answer, it is an answer to
        // something else. See Unknown.
        if (want == null)
            return (default, 0d, "", "", false, Unknown);

        // The remnant's own reward list decides - the same list, the same arithmetic and the same
        // answer as the text on the ground. Only the matching back to a rectangle happens here.
        {
            var found = Named(options, valuation, want.Name);

            if (found.Index >= 0)
            {
                // Found, and not safely clickable: the list is taller than the panel it sits in, so
                // it scrolls and its coordinates no longer describe the screen. See Hidden.
                Scrollable = found.Panel;
                WantedAt = found.Index;
                WantedRect = found.Rect;

                // **A rectangle that is not there yet is a frame to wait for, not a refusal.**
                //
                // Reachable answers false for a degenerate rectangle, which is right for drawing a
                // border and wrong here: Hidden is terminal and tells the player to scroll down to
                // an option that is sitting in front of them, on a window with no scroll bar at all.
                // The option list gets its geometry a frame or two after the window opens, and a
                // press landing in that gap found the option, measured it at nothing, and gave up.
                //
                // The same race the Waiting path below already guards - it only covers the case
                // where the option is not found at all, and this is the case where it is found and
                // has not been laid out. Both are "ask again next frame".
                if (found.Rect.Width <= 0f || found.Rect.Height <= 0f)
                    return (default, 0d, want.Name, want.Recipe, false, Waiting);

                if (!Reachable(gc, found.Rect, found.Panel))
                    return (default, 0d, want.Name, want.Recipe, false, Hidden);

                return (found.Rect, want.Value + want.Carries * carry, want.Name, want.Recipe, false, found.Index);
            }

            // The answer is known and the window has not caught up. Do NOT fall through.
            //
            // Falling through was a real bug with real consequences: the window is opened by an
            // explosive landing on an undecided remnant, and on the first frames after that its
            // options have no rectangle yet. The reward to take was worked out correctly, could not
            // be pointed at for one frame, and the code below then answered a DIFFERENT question -
            // which is the most expensive - and clicked that. The overlay said take the Witchcraft
            // and the plugin took the Foundations, from one frame of missing geometry.
            //
            // Waiting is the honest answer: the pick has not changed, only the ability to reach it,
            // and the caller's own timeout ends the run if the rectangles never arrive.
            if (Offers(options, valuation, want.Name))
                return (default, 0d, want.Name, want.Recipe, false, Waiting);
        }

        // **The remnant is known, its reward is decided, and the window does not offer it.**
        //
        // There used to be a hundred lines here that answered a different question when this
        // happened: take the most expensive option, or failing that the one highest up the
        // preference list. It reads like robustness and it is the opposite - the two ways to get
        // here are a reward list belonging to a different remnant and a reward list that has gone
        // stale, and in both cases the plugin has just been shown that it does not understand this
        // window. Clicking anything at that point spends somebody's remnant on a guess.
        //
        // So it stops and says so, and the caller turns that into a refusal the player can read.
        return (default, 0d, want.Name, want.Recipe, false, Unknown);
    }

    /// <summary>
    /// The visible option offering a named reward, or index -1 when the window has no such option.
    ///
    /// Richest first among duplicates, since several combinations can reach the same reward and the
    /// reward list already collapsed them to the best one.
    /// </summary>
    private static (RectangleF Rect, int Index, ExileCore2.PoEMemory.Element Panel) Named(
        List<Expedition2WindowOption> options,
        Valuation valuation, string name)
    {
        var rect = default(RectangleF);
        var at = -1;
        var most = double.NegativeInfinity;

        // The option itself, so the clipping panel can be found by walking up from it. See Viewport.
        ExileCore2.PoEMemory.Element panel = null;

        // The same search over anything with a usable rectangle, for when NOTHING reports itself
        // visible. Individual options come back IsVisible false in every dump taken so far, even
        // with a rectangle the border draws on correctly - so treating visibility as a requirement
        // is a way of finding no option at all and falling back to plain price for no reason. It
        // stays as a PREFERENCE, since a visible option is the better answer when there is one.
        var loose = default(RectangleF);
        var looseAt = -1;
        var loosest = double.NegativeInfinity;

        var index = -1;

        foreach (var option in options)
        {
            index++;

            if (option == null)
                continue;

            // **Carved, not raw, and this is the path that actually runs.** Clickable was applied
            // on the fallback branch alone - the one used when no remnant could be matched - so
            // every ordinary pick aimed at the whole row, rune icons included, and hovering one of
            // those lights the icon rather than the option. The border showed it plainly: it
            // enclosed the runes while the "Propagates" text beside it, drawn off the carved rect,
            // sat correctly to their right. Two rectangles for one option, and the click had the
            // wrong one. See Clickable.
            var where = Clickable(option);

            if (where.Width <= 0f || where.Height <= 0f)
                continue;

            var recipe = Safe.Read(() => option.Recipe, null);

            if (!string.Equals(Valuation.Name(recipe), name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = valuation.Value(recipe);

            if (value > loosest)
            {
                loosest = value;
                loose = where;
                looseAt = index;
                panel = option;
            }

            if (!Safe.Read(() => option.IsVisible, false) || value <= most)
                continue;

            most = value;
            rect = where;
            at = index;
            panel = option;
        }

        return at >= 0 ? (rect, at, panel) : (loose, looseAt, panel);
    }

    /// <summary>
    /// Which of a remnant's rewards to take: the richest once propagation is counted.
    ///
    /// **This is the only place that decision is made.** It was briefly made twice - once over the
    /// remnant's reward list for the ground display and once over the window's recipes for the
    /// border - and the two lists are not the same list. The reward list collapses recipes by name,
    /// keeping the richest of each; the window's recipes are every combination separately. So the
    /// two could and did name different rewards, which showed up as the ground hoisting one thing
    /// and the window bordering another.
    ///
    /// The reward list wins because it is the one the player is reading, and because it is built
    /// once per remnant rather than per frame.
    /// </summary>
    /// <param name="carry">What one point of CARRIED propagation is worth here, in exalts.</param>
    /// <param name="local">
    /// What one point of non-carried propagation is worth here, in exalts.
    ///
    /// Smaller than <paramref name="carry"/> and never zero for the wrong reason: a rune in a slot
    /// that does not pass forward still buffs the monsters this remnant's own explosive unearths.
    /// Ignoring that said an Opulent in the wrong socket was worth nothing, when it is worth most
    /// of a blast - and on a last link, where nothing follows, the two terms are the same size.
    /// </param>
    /// <param name="money">
    /// What a exalt of reward counts for, against the runes. One normally; a hundredth under runes
    /// and rares mode.
    ///
    /// It has to be here and not only in the planner. The mode was cutting the reward term in the
    /// objective while this went on comparing full prices, so the route would set off for a remnant
    /// on the strength of its runes and the border would then tell you to take the currency - the
    /// two halves of the plugin disagreeing about the same decision, which is the one thing the
    /// shared Take exists to prevent.
    /// </param>
    /// <returns>An index into <paramref name="rewards"/>. Zero - the richest - whenever nothing
    /// carries, which is the usual case and needs no second thought.</returns>
    public static int Take(IReadOnlyList<Reward> rewards, double carry, double local = 0d,
        double money = 1d)
    {
        if (rewards == null || rewards.Count < 2 || (carry <= 0d && local <= 0d))
            return 0;

        var best = 0;
        var most = double.NegativeInfinity;

        for (var i = 0; i < rewards.Count; i++)
        {
            var worth = rewards[i].Value * money +
                        rewards[i].Carries * carry + rewards[i].Local * local;

            if (worth > most)
            {
                most = worth;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// What one point of carried weight is worth here, in exalts.
    ///
    /// The exchange rate the player set, times the number of remnants that would inherit the rune -
    /// so it is zero on the last remnant of a chain however good the rune, and zero whenever no
    /// rate has been given, which leaves the choice on price alone.
    /// </summary>
    public static double Carried(AutoExpeditionSettings settings, Valuation valuation, Target remnant,
        GameController gc, Scan scan, Plan plan)
    {
        var rate = Safe.Read(() => settings.Rewards.PointWorth.Value, 0f);

        if (rate <= 0f || remnant?.Passing is not { Count: > 0 })
            return 0d;

        var downstream = Reroll.Downstream(remnant, settings, gc, scan, plan);

        // No conversion. The rate is in exalts and everything internal is in exalts, so the
        // display unit has no business here - see PointWorth for why that was worth fixing.
        return downstream <= 0f ? 0d : rate / 100d * downstream;
    }

    /// <summary>What an exalt of reward counts for against the runes.</summary>
    /// <remarks>
    /// One, always. It used to be a hundredth while the runes-and-rares mode was on, and that
    /// mode is gone - the argument for it, that a rich remnant should not pull a chain away
    /// from the content, is the weights' job and was being made twice.
    /// </remarks>
    public static double Money(AutoExpeditionSettings settings) => 1d;

    /// <summary>
    /// The same rate for a rune that does NOT carry forward: one blast's worth of monsters.
    ///
    /// No Passing requirement, unlike the carried rate - a remnant that passes nothing on still has
    /// runes, and they still apply to what it unearths. That was the whole of the oversight.
    /// </summary>
    public static double Locally(AutoExpeditionSettings settings, Valuation valuation, Target remnant,
        GameController gc, Scan scan, Plan plan)
    {
        var rate = Safe.Read(() => settings.Rewards.PointWorth.Value, 0f);

        if (rate <= 0f || remnant == null)
            return 0d;

        var here = Reroll.Local(remnant, settings, gc, scan, plan);

        return here <= 0f ? 0d : rate / 100d * here;
    }

    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Valuation valuation, Scan scan, Plan plan)
    {
        var window = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window, null);

        if (window == null || !Safe.Read(() => window.IsVisible, false))
            return;

        var options = Safe.Read(() => window.Options, null);

        if (options == null || options.Count == 0)
            return;

        // **What is landing on this remnant's waves, read from the objective like the world line.**
        //

        // Planner.RuneTallyByRemnant is the table the yellow line under the remnant is read from, so the figures
        // here and on the ground are one answer. The NAMES stay per row, because that is the half a
        // row actually decides: which runes this remnant would be the source of if you took it. What
        // the old text lacked was the other half - the figures - so it named a row's runes with no
        // idea what the chain already carried, crediting this remnant as the source of runes arriving
        // from earlier links and naming runes the weights rate at nothing.
        //
        // Null when the remnant cannot be identified or no chain reaches it, and then nothing is drawn
        // rather than something guessed. See ShowWavesInWindow and Propagation.Waves.
        _drew.Clear();

        var showing = Safe.Read(() => settings.Display.Remnants.RuneshapeCombinationsWindow.ShowWavesInWindow.Value, true);
        var mine = showing ? Whose(gc, scan) : null;

        Planner.RuneTally? landing = null;
        var was = mine == null ? default : ((int)MathF.Round(mine.Grid.X), (int)MathF.Round(mine.Grid.Y));

        // Blank while the figures are a roll out of date, for the same reason the line on the ground is
        // blank: the window and the ground read one table and must not disagree about it. See
        // Planner.RuneTallyOutOfDate.
        if (mine != null && !Planner.RuneTallyOutOfDate &&
            Planner.RuneTallyByRemnant.TryGetValue(
                ((int)MathF.Round(mine.Grid.X), (int)MathF.Round(mine.Grid.Y)), out var found))
        {
            landing = found;
        }

        // Every option, visible or scrolled away, for the offers file. See RemnantOffers.
        if (Safe.Read(() => settings.Recording.Census.Value, false))
            RemnantOffers.Note(gc, mine ?? Whose(gc, scan), options.Select(o => Safe.Read(() => o?.Recipe, null)));

        var chosen = Pick(gc, settings, valuation, scan, plan);
        var rows = new List<(RectangleF Rect, double Value, List<(string Text, Color Colour)> Waves, int Index)>();
        var index = -1;

        foreach (var option in options)
        {
            index++;

            if (option == null || !Safe.Read(() => option.IsVisible, false))
                continue;

            var rect = Clickable(option);

            if (rect.Width <= 0f || rect.Height <= 0f)
                continue;

            var recipe = Safe.Read(() => option.Recipe, null);

            // **Read off the priced reward rather than recomputed from the live option.**
            //
            // Both routes ask Propagation.Ids the same question, and the answer is already worked out
            // and stored per combination when the remnant is priced - which is where the objective and
            // the dump both read it from, so it is the one that has been checked against real sites.
            // Recomputing it here from option.Recipe made a second path to one fact and it came back
            // empty for every row, so the window drew six identical lines with no names on them.
            //
            // Matched on the reward's own name, because that is what both sides agree on: the window
            // shows a combination by what it yields, and the priced list is keyed the same way.
            var named = Valuation.Name(recipe);
            var id = Safe.Read(() => recipe.Id, null);
            var priced = landing is null ? null : Priced(mine, id, named);

            // **The row's own figures, not the chosen combination's.**
            //
            // A remnant with six sockets offers combinations that use two of them and combinations
            // that use five, so "its own runes" is a fact about the ROW. Reading the chosen
            // combination's count for every row is what printed one sentence six times: 2x Armourer's
            // Scrap uses two sockets and read as though it used six.
            //
            // Wasted moves with the row too - a combination putting a rune into a slot the chain is
            // already sending that rune to has gained nothing by it - so it is the overlap between
            // what this row holds and what is arriving from earlier links. Only the inherited figure
            // belongs to the remnant, which is why that one is taken from the objective as it stands.
            string line = null;
            List<(string Text, Color Colour)> pieces = null;

            if (landing is { } runes && priced != null)
            {
                // **Every row through the planner's own rule, taken or not.** See Planner.Would, which
                // is where the arithmetic lives now - this used to carry a second version of it and the
                // two disagreed on screen. The row actually taken must come out equal to RuneTallyByRemnant, and the
                // diagnostic below says so rather than assuming it.
                var tally = Planner.RuneTallyOfOption(runes, priced.Locals, priced.Carrying, priced.SlotRunes);

                line = Propagation.RunesOnWaves(tally);

                // Coloured as the line under the remnant is - an upstream or downstream duplicate in its own colour -
                // through the same function. See Overlay.RuneLinePieces.
                pieces = Overlay.RuneLinePieces(settings, tally);

                // The row the planner took, by recipe where both sides know it. Names alone put
                // the TAKEN marker on whichever same-named row came first. See Priced.
                var taken = Planner.Chosen.TryGetValue(was, out var took) &&
                            (took.Recipe.Length > 0 && priced.Recipe.Length > 0
                                ? string.Equals(took.Recipe, priced.Recipe, StringComparison.Ordinal)
                                : string.Equals(named, took.Reward, StringComparison.OrdinalIgnoreCase));

                _drew.Add($"[{index}] \"{named ?? "(unnamed)"}\" -> " +
                          $"holds {Planner.RunesInLocals(priced.Locals)} local + " +
                          $"{priced.Carrying?.Length ?? 0} propagating, " +
                          $"arriving [{string.Join(" ", runes.Arriving ?? [])}]" +
                          (taken
                              ? $"  TAKEN - ground says \"{Propagation.RunesOnWaves(runes)}\"" +
                                (Propagation.RunesOnWaves(runes) == line ? " (agrees)" : " (DISAGREES)")
                              : "") +
                          $"  drawn \"{line}\"");
            }
            else
            {
                _drew.Add($"[{index}] \"{named ?? "(unnamed)"}\" -> " +
                          (landing is null
                              ? "no waves for this remnant, so nothing is drawn"
                              : "NO PRICED REWARD of that name, so nothing is drawn"));
            }

            rows.Add((rect, valuation.Value(recipe), pieces, index));
        }

        if (rows.Count == 0)
            return;

        // **What the list is clipped to, because an option keeps its rectangle off the page.**
        //
        // The option list is a column 1,467 tall inside a panel 720 tall, so on a six socket remnant
        // most of it is scrolled out of view at any moment - and every one of those rows reports a
        // perfectly ordinary rectangle while sitting below the bottom of the screen. Everything here
        // drew against those rectangles, so the prices for the hidden rows were painted down the
        // screen and off the end of it: numbers floating over the game with no option under them.
        //
        // The same panel Reachable uses to decide whether an option can be clicked, asked here about
        // whether it can be drawn on. Found by walking up from an option rather than by a path - see
        // Viewport.
        var view = Viewport(gc, Element(options));

        foreach (var (rect, value, waves, _) in rows)
        {
            // Per row, and against the row rather than the text, so a row half over the edge is
            // treated as hidden. Half a price at the panel's lip reads as a rendering fault, and the
            // row it belongs to cannot be clicked either - see Reachable, which draws the same line.
            if (!Within(view, rect))
                continue;

            // Top left of the row, in the world line's colours: amber, as the game borders the propagating slots in
            // yellow, with a rune an earlier remnant already sends or a later one sends too in its duplicate colour.
            if (waves != null)
            {
                Overlay.DrawRuneLinePieces(graphics, waves, new Vector2(rect.Left + 4f, rect.Top + 2f),
                    settings.Display.Remnants.RuneshapeCombinationsWindow.WindowPriceBackground);
            }

            // Bottom right, inside the option. Right-aligned means the figures line up on their
            // last digit down the window, which is what makes a column of prices comparable at a
            // glance rather than something to read one at a time.
            var text = value > 0d ? Prices.Text(value, settings, valuation) : "-";
            var size = graphics.MeasureText(text);
            var at = new Vector2(rect.Right - size.X - 4f, rect.Bottom - size.Y - 2f);

            graphics.DrawTextWithBackground(text, at, settings.Display.Remnants.RuneshapeCombinationsWindow.WindowPriceColour,
                settings.Display.Remnants.RuneshapeCombinationsWindow.WindowPriceBackground);
        }

        // The plain richest, which is a different question from the one above whenever propagation
        // is worth anything: an option paying less and carrying Opulent to three more remnants is
        // the better take, and then these two are different rectangles.
        var richest = default(RectangleF);
        var richestAt = -1;
        var most = 0d;

        foreach (var (rect, value, _, at) in rows)
        {
            if (value > most)
            {
                most = value;
                richest = rect;
                richestAt = at;
            }
        }

        // Off the page it is drawn or not on the same terms as the green one - see
        // DisplaySettings.BordersOffScreen, and note the pair only says anything by differing.
        if (richestAt >= 0 && !Within(view, richest) && !settings.Display.Remnants.RuneshapeCombinationsWindow.BordersOffScreen)
            richestAt = -1;

        // Purple on the richest, and only when it is NOT the one to take. Purple because that is
        // the colour the price is written in on the ground, so the border says "this is the money"
        // without needing a legend - and drawn first, so the green sits over it if they somehow
        // overlap. Absent when the two agree, since a second border round the same option would
        // only invite the reader to look for a difference that is not there.
        // Against which option is WANTED rather than which one is clickable, so an off-page pick
        // does not get the purple border meant for an option that is rich and not the answer. See
        // WantedAt.
        var wantedAt = chosen.Index >= 0 ? chosen.Index : WantedAt;

        // A rolled remnant's combination cannot be changed, so neither border is advice: the pick is
        // drawn in the warning colour and the richest border is left off.
        var rolled = (mine ?? Whose(gc, scan))?.Rerolled == true;

        if (!rolled && richestAt >= 0 && richestAt != wantedAt)
        {
            graphics.DrawFrame(richest, settings.Display.Remnants.RuneshapeCombinationsWindow.RichestColour,
                settings.Display.Remnants.RuneshapeCombinationsWindow.BestOptionThickness.Value);
        }

        // Round whichever option would be taken - the richest once propagation is counted, or the
        // one the preference list names when none of them is priced. Nothing when there is no
        // answer, because a border round the best of several identically worthless options says
        // something untrue.
        // **The pick's own rectangle, clipped or not.** Scrolled past the bottom of the panel the
        // green border is the only thing still saying which option to take, and dropping it took the
        // answer away on exactly the remnants whose lists are long enough to need one. Off the screen
        // there is nothing to see; at the lip of the panel there is an edge, and that edge is the
        // cue to scroll. See DisplaySettings.BordersOffScreen.
        var pick = chosen.Rect.Width > 0f
            ? chosen.Rect
            : settings.Display.Remnants.RuneshapeCombinationsWindow.BordersOffScreen && chosen.Index == Hidden
                ? WantedRect
                : default;

        if (pick.Width > 0f)
            graphics.DrawFrame(pick,
                rolled
                    ? settings.Display.Remnants.Rewards.OverruledColour
                    : settings.Display.Remnants.RuneshapeCombinationsWindow.PickColour,
                settings.Display.Remnants.RuneshapeCombinationsWindow.BestOptionThickness.Value);

        // **Why there is no green border, written where the green border would have been.**
        //
        // Drawing nothing is the right thing to do and a terrible thing to look at: an overlay that
        // silently stops having an opinion is indistinguishable from one that has broken. It said
        // nothing for a reason, so it says the reason.
        //
        // Short, because it is drawn over the game while somebody is reading a list of rewards, and
        // a sentence there is an obstacle rather than an explanation. The two cases stay separate -
        // not knowing WHICH remnant this is and not finding the reward on it are different faults
        // with different fixes - and the long version of either lives in the dump, where there is
        // room to say which of the four ways the match failed. See Explain.
        if (chosen.Index == Unknown)
        {
            // Anchored on the first row that is actually on the page, so the explanation does not
            // follow a scrolled-away option off the bottom of the screen with everything else.
            var first = rows[0].Rect;

            foreach (var (rect, _, _, _) in rows)
            {
                if (!Within(view, rect))
                    continue;

                first = rect;

                break;
            }

            graphics.DrawTextWithBackground(
                Placement.Opened == null && Whose(gc, scan) == null
                    ? "AutoExpedition: identity match failed"
                    : "AutoExpedition: reward not offered here",
                new Vector2(first.Left + 4f, first.Top - 20f),
                settings.Display.Remnants.Rewards.OverruledColour, settings.Display.Remnants.RuneshapeCombinationsWindow.WindowPriceBackground);
        }
    }

    /// <summary>
    /// How far a remnant may be from the player and still be the one whose window is open, in grid.
    ///
    /// **The interaction range, read off the game rather than reasoned about.** Clicking a remnant's
    /// combinations button walks the character to it and stops, and where it stops is the range.
    /// Four readings with a window up, every one of them the remnant it genuinely belonged to:
    ///
    ///     28.3   standing closer than needed
    ///     34.7   an earlier dump
    ///     56.6   walked in
    ///     56.9   walked in
    ///
    /// The last two are the ones that matter. Two independent walks stopping within a third of a
    /// grid unit of each other is a hard stop, not a coincidence of where somebody was standing, so
    /// the range is about 57 and the shorter readings are simply the player already being inside it.
    ///
    /// Set just above that. The two errors do not cost the same - too high lets a distant remnant
    /// stay a candidate, which the fingerprint and the dig site usually remove anyway, while too low
    /// refuses to name a window that is plainly identifiable, which is the failure this whole path
    /// exists to avoid. That is exactly what 30 did, on the reading that the 28.3 was a limit when
    /// it was a player standing close.
    ///
    /// Three grid of headroom over the widest reading. The two errors cost different things - too
    /// high leaves a candidate the fingerprint and the dig site usually remove anyway, too low
    /// refuses to name a window the player can plainly see, which is what 30 produced - but the
    /// readings cluster tightly enough at ~57 that a thin margin is worth having: it is the
    /// difference between separating two remnants on one site and refusing to.
    ///
    /// If a window ever goes unidentified with the dump's distance column reading above this, the
    /// range is wider than these readings found and this is the number to raise.
    ///
    /// **It only ever removes candidates.** See the note in Offering: nothing here ranks by
    /// distance, and a remnant the fingerprint rejected is never promoted by being near.
    /// </summary>
    private const float Reach = 60f;

    /// <summary>
    /// How far a remnant is, by the game's own reckoning where it has one.
    ///
    /// **Entity.DistancePlayer rather than the two grid positions subtracted.** They have agreed to
    /// a tenth of a grid everywhere they have been compared - 56.9 and 58.7 in the dump that made
    /// the point - so this is not a correction. It is the client answering a question about its own
    /// world instead of the plugin reconstructing the answer from two coordinates it also read, and
    /// where a bound is being drawn at three grid of margin, whose number it is starts to matter.
    ///
    /// Falls back to the subtraction when the entity cannot be read, which is the same answer.
    /// </summary>
    private static float Away(Target target, Vector2 player)
    {
        var said = Safe.Read(() => target.Entity.DistancePlayer, -1f);

        return said >= 0f
            ? said
            : player == Vector2.Zero
                ? -1f
                : Vector2.Distance(target.Grid, player);
    }

    /// <summary>An Index meaning "the pick is settled, the window is not ready to be clicked yet".</summary>
    public const int Waiting = -2;

    /// <summary>
    /// An Index meaning "this window cannot be attributed to a remnant, or offers nothing the
    /// decision named" - so nothing may be clicked and nothing may be drawn as the pick.
    ///
    /// **The whole point of naming this rather than returning a best guess.** Choosing a reward is
    /// the one action here that spends something irreversible, so a wrong answer costs a remnant
    /// and a refusal costs a keypress. Everything that cannot be established is this.
    /// </summary>
    public const int Unknown = -4;

    /// <summary>
    /// The reward to take is in the list and scrolled out of sight.
    ///
    /// **An option keeps its rectangle when the window has scrolled past it**, so geometry alone
    /// says nothing about whether it can be clicked - and the run happily walked the cursor to a
    /// point off the page and clicked whatever was underneath. Nineteen options in a window that
    /// shows a handful is the ordinary case on a six socket remnant.
    ///
    /// Told apart from Waiting on purpose. Waiting means the window has not drawn yet and the next
    /// frame will fix it; this means the window has drawn and the answer is somewhere the cursor
    /// cannot reach, which no amount of waiting changes.
    /// </summary>
    public const int Hidden = -3;

    /// <summary>
    /// The option the last Pick wanted, kept so a caller can ask how to scroll to it.
    ///
    /// Pick answers with a rectangle because that is what clicking needs; scrolling needs the
    /// element, to walk up to the panel and across to its bar. Held here rather than widened into
    /// the return, which four callers would then have to carry and three of them ignore.
    /// </summary>
    public static ExileCore2.PoEMemory.Element Scrollable { get; private set; }

    /// <summary>
    /// Which option the last Pick wanted, whether or not it could be reached - or -1.
    ///
    /// **Index says two things at once and one of them was being lost.** It carries WHICH option to
    /// take and, through its sentinels, whether the window is in a state to take it - so a pick that
    /// is merely scrolled out of sight comes back as Hidden, and its identity goes with it. The
    /// drawing then had no way to tell "the richest option is not the one to take" from "the one to
    /// take is off the page", and drew the purple richest-border on the pick's own row. Scrolling to
    /// it turned the same option green, which is the same fact reported two ways.
    ///
    /// Kept beside Scrollable and for the same reason: the callers that need it are not the callers
    /// that would have to carry it through the return.
    /// </summary>
    public static int WantedAt { get; private set; } = -1;

    /// <summary>
    /// Where that option is, whether or not it can be reached - or nothing.
    ///
    /// The rectangle is real even when the option is scrolled past the bottom of the panel; what
    /// makes it unreachable is that it describes a place off the screen, which is a reason not to
    /// CLICK it rather than a reason not to know where it is. See DisplaySettings.BorderOffScreen.
    /// </summary>
    public static RectangleF WantedRect { get; private set; }

    /// <summary>
    /// Whether a rectangle in the combinations window can actually be clicked.
    ///
    /// Inside the window and inside the screen. An option scrolled out of view reports a rectangle
    /// beyond the window's own bounds, which is what makes this answerable at all.
    /// </summary>
    /// <summary>
    /// The panel that actually clips the option list, found by walking up from an option.
    ///
    /// **A hardcoded path was the wrong approach and the numbers said so.** The path read off a UI
    /// explorer - 40, 4, 0 - could not be walked from Expedition2Window at all, because the typed
    /// window exposes six children where the explorer's tree has dozens; its indices belong to a
    /// different root. Every attempt to name the element by position was a guess about somebody
    /// else's numbering.
    ///
    /// The clipper can be recognised instead of located. A scroll area is precisely an element
    /// SHORTER than the content inside it - that is what makes it scroll - so walking up from an
    /// option, the first ancestor shorter than the list it contains is the panel doing the
    /// clipping. No indices, nothing to break when the game rearranges the window, and it reads the
    /// same whether the list scrolls or not.
    ///
    /// Bounded, because an ancestor chain from a bad read can be anything.
    /// </summary>
    /// <summary>
    /// Where in a combination row a click will actually be taken.
    ///
    /// **The rune icons are not clickable and the row is.** Confirmed from a dump: hovering child 5
    /// of an option - a 45x45 rune icon - lights the icon and not the option, and the click does
    /// nothing. Child 0 is the reward's own text and behaves as part of the row; everything from
    /// child 1 onwards is an icon and swallows the hover.
    ///
    /// So the row is aimed at in the widest stretch of itself that no icon covers. On a 666 wide
    /// row with five icons clustered in the middle that is most of the right-hand side - a target
    /// far bigger than the one it replaces, and the reason this reads as "aim better" rather than
    /// "work around a bug".
    ///
    /// The full row when the children cannot be read, because the row is the right answer whenever
    /// the icons are not in the way, and a row nobody can measure is not evidence that they are.
    /// </summary>
    internal static RectangleF Clickable(ExileCore2.PoEMemory.Element option)
    {
        var row = Safe.Read(option, static e => e.GetClientRectCache, default(RectangleF));

        if (row.Width <= 0f || row.Height <= 0f)
            return row;

        var kids = Safe.Kids(option);

        if (kids == null || kids.Count < 2)
            return row;

        var icons = new List<RectangleF>();

        // From one, not nought. Child nought is the reward text and takes a click perfectly well;
        // starting the sweep at nought would rule out the whole row on most options.
        for (var i = 1; i < kids.Count; i++)
        {
            var rect = Safe.Read(kids[i], static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width > 0f && rect.Height > 0f)
                icons.Add(rect);
        }

        var clear = Panels.Clearest(row, icons);

        return clear.Width > 0f && clear.Height > 0f ? clear : row;
    }

    private static ExileCore2.PoEMemory.Element Panel(ExileCore2.PoEMemory.Element option)
    {
        var at = Safe.Read(option, static e => e.Parent, null);
        var inner = Safe.Read(option, static e => e.GetClientRectCache, default(RectangleF));

        for (var step = 0; step < 8 && at != null; step++)
        {
            var here = Safe.Read(at, static e => e.GetClientRectCache, default(RectangleF));

            if (here.Height > 0f && inner.Height > 0f && here.Height + 1f < inner.Height)
                return at;

            if (here.Height > 0f)
                inner = here;

            at = Safe.Read(at, static e => e.Parent, null);
        }

        return null;
    }

    private static RectangleF Viewport(GameController gc, ExileCore2.PoEMemory.Element option)
    {
        var panel = Safe.Read(Panel(option), static e => e.GetClientRectCache, default(RectangleF));

        return panel.Height > 0f
            ? panel
            : Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.GetClientRectCache,
                default(RectangleF));
    }

    /// <summary>
    /// What to drag, and how far, to bring an option into view - or nothing when it is already there.
    ///
    /// **The thumb moves in proportion to the list, not with it.** The option list is 1,467 tall
    /// inside a panel 720 tall, so the whole scroll bar represents the whole list: moving the thumb
    /// one pixel moves the content by the ratio of the two. Dragging by the distance the option is
    /// out of view would overshoot it by that same factor.
    ///
    /// The thumb is found the way the panel was - by what it is rather than where. A vertical scroll
    /// bar is the child of the panel that is tall and narrow, which nothing else in a list of
    /// rewards is.
    ///
    /// Aimed a little past the edge rather than exactly at it: an option flush with the bottom of
    /// the panel is one the next frame of layout can push out again, and a few pixels of margin
    /// costs nothing.
    /// </summary>
    public static (Vector2 From, Vector2 To, bool Needed) Scrolling(GameController gc,
        ExileCore2.PoEMemory.Element option)
    {
        var at = Safe.Read(option, static e => e.GetClientRectCache, default(RectangleF));
        var panel = Viewport(gc, option);
        var list = Content(option);

        if (at.Height <= 0f || panel.Height <= 0f || list.Height <= panel.Height)
            return (Vector2.Zero, Vector2.Zero, false);

        // How far the content has to move, and which way. Positive means the content must come up,
        // which is the thumb going down.
        var shift = at.Bottom > panel.Bottom ? at.Bottom - panel.Bottom + Margin
            : at.Top < panel.Top ? at.Top - panel.Top - Margin
            : 0f;

        if (MathF.Abs(shift) < 1f)
            return (Vector2.Zero, Vector2.Zero, false);

        var track = Thumb(Panel(option));

        if (track.Height <= 0f)
            return (Vector2.Zero, Vector2.Zero, false);

        // **The thumb is an element of its own: the track's own child.**
        //
        // Confirmed against the window's tree - the panel holds the track and the track holds the
        // thumb - so where the game draws it is the game's answer rather than one worked out from
        // proportions. Used when it can be read.
        var grip = Grip(Panel(option));

        if (grip.Height > 0f)
        {
            var straight = shift * panel.Height / list.Height;
            var grabbed = new Vector2(grip.X + grip.Width / 2f, grip.Y + grip.Height / 2f);

            return (grabbed, new Vector2(grabbed.X, grabbed.Y + straight), true);
        }

        // **The bar found by shape is the TRACK, and the thumb is somewhere inside it.**
        //
        // Measured: the bar came back 27 wide and 720 tall - exactly the height of the panel, which
        // a thumb never is. Grabbing its middle on an unscrolled list lands just past the bottom of
        // a thumb about 353 tall, which is empty track, and a drag from empty track moves nothing.
        //
        // The thumb's size and position are not a mystery though: it is as tall a fraction of the
        // track as the panel is of the list, and it sits as far down the track as the list is
        // scrolled. Both of those are numbers already in hand, so it is worked out rather than
        // hunted for - and works whether or not the game models the thumb as an element of its own.
        var span = MathF.Max(1f, list.Height - panel.Height);
        var scrolled = Math.Clamp((panel.Top - list.Top) / span, 0f, 1f);
        var tall = MathF.Max(8f, track.Height * panel.Height / list.Height);
        var top = track.Top + scrolled * (track.Height - tall);

        var travel = shift * panel.Height / list.Height;
        var from = new Vector2(track.X + track.Width / 2f, top + tall / 2f);

        return (from, new Vector2(from.X, from.Y + travel), true);
    }

    /// <summary>How far past the panel's edge to bring an option, in pixels. See Scrolling.</summary>
    private const float Margin = 8f;

    /// <summary>The whole option list, which is what the thumb is proportional to.</summary>
    private static RectangleF Content(ExileCore2.PoEMemory.Element option)
    {
        var at = Safe.Read(option, static e => e.Parent, null);
        var widest = Safe.Read(option, static e => e.GetClientRectCache, default(RectangleF));

        // Up until something is shorter than what it holds, which is the panel - the last rectangle
        // before that is the list itself. See Viewport, which stops at the other side of the same
        // step.
        for (var step = 0; step < 8 && at != null; step++)
        {
            var here = Safe.Read(at, static e => e.GetClientRectCache, default(RectangleF));

            if (here.Height > 0f && widest.Height > 0f && here.Height + 1f < widest.Height)
                return widest;

            if (here.Height > 0f)
                widest = here;

            at = Safe.Read(at, static e => e.Parent, null);
        }

        return widest;
    }

    /// <summary>
    /// The scroll bar inside a panel: its tallest, narrowest child.
    ///
    /// Recognised rather than indexed, for the reason the panel is - a path read off a UI explorer
    /// could not be walked from the typed window at all. A bar is the one child of a list panel that
    /// is far taller than it is wide.
    /// </summary>
    /// <summary>
    /// The draggable thumb: the scroll track's own child.
    ///
    /// The track is recognised by shape - tall, narrow, and a child of the panel - and the thumb is
    /// what sits inside it. Nothing here is indexed, so a rearranged window changes where these are
    /// found without changing what they are; where the thumb cannot be read at all, Scrolling works
    /// its position out from the proportions instead.
    /// </summary>
    private static RectangleF Grip(ExileCore2.PoEMemory.Element panel)
    {
        ExileCore2.PoEMemory.Element track = null;
        var best = default(RectangleF);

        foreach (var kid in Safe.Kids(panel) ??
                            new List<ExileCore2.PoEMemory.Element>())
        {
            var at = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

            if (at.Width <= 0f || at.Height < at.Width * 3f || at.Height <= best.Height)
                continue;

            best = at;
            track = kid;
        }

        var found = default(RectangleF);

        foreach (var kid in Safe.Kids(track) ??
                            new List<ExileCore2.PoEMemory.Element>())
        {
            var at = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

            // Shorter than the track it sits in - that is what makes it a thumb rather than the
            // groove behind it - and the tallest of whatever qualifies.
            if (at.Height <= 0f || at.Height >= best.Height || at.Height <= found.Height)
                continue;

            found = at;
        }

        return found;
    }

    private static RectangleF Thumb(ExileCore2.PoEMemory.Element panel)
    {
        var best = default(RectangleF);

        foreach (var kid in Safe.Kids(panel) ??
                            new List<ExileCore2.PoEMemory.Element>())
        {
            var at = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

            // Tall and thin, and taller than anything else that is. A reward row is wider than it
            // is tall; a bar is the other way round by a long way.
            if (at.Width <= 0f || at.Height < at.Width * 3f || at.Height <= best.Height)
                continue;

            best = at;
        }

        return best;
    }

    /// <summary>Everything the scroll arithmetic worked out, for the dump. See Scrolling.</summary>
    public static string Scroll(GameController gc)
    {
        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.Options, null);

        if (options is not { Count: > 0 })
            return "no options";

        var option = Scrollable ?? Safe.Read(() => (ExileCore2.PoEMemory.Element)options[0], null);
        var at = Safe.Read(option, static e => e.GetClientRectCache, default(RectangleF));
        var panelAt = Viewport(gc, option);
        var list = Content(option);
        var panel = Panel(option);
        var thumb = Thumb(panel);
        var kids = Safe.Read(panel, static e => (int)e.ChildCount, -1);
        var (from, to, needed) = Scrolling(gc, option);

        return $"option ({at.X:0},{at.Y:0} {at.Width:0}x{at.Height:0})  " +
               $"panel ({panelAt.X:0},{panelAt.Y:0} {panelAt.Width:0}x{panelAt.Height:0}, " +
               $"{kids} children)  list {list.Height:0} tall  " +
               (thumb.Height > 0f
                   ? $"track ({thumb.X:0},{thumb.Y:0} {thumb.Width:0}x{thumb.Height:0})"
                   : "track NOT FOUND - no tall narrow child") +
               (Grip(panel) is { Height: > 0f } grip
                   ? $"  thumb ({grip.X:0},{grip.Y:0} {grip.Width:0}x{grip.Height:0})"
                   : "  thumb NOT FOUND - working it out from the proportions") +
               (needed
                   ? $"  drag ({from.X:0},{from.Y:0}) to ({to.X:0},{to.Y:0})"
                   : "  no drag wanted");
    }

    /// <summary>
    /// The chain from an option up to whatever clips it, for the dump.
    ///
    /// Named apart from the Panel that finds the element: one returns the thing and one describes
    /// the search for it, and an overload where the difference is the return type is a trap for
    /// whoever reads it next.
    /// </summary>
    public static string Walk(GameController gc)
    {
        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.Options, null);

        if (options is not { Count: > 0 })
            return "no options to walk up from";

        var text = new System.Text.StringBuilder();
        var at = Safe.Read(() => (ExileCore2.PoEMemory.Element)options[0], null);

        for (var step = 0; step < 6 && at != null; step++)
        {
            var here = Safe.Read(at, static e => e.GetClientRectCache, default(RectangleF));

            text.Append(step == 0 ? "option " : " <- ")
                .Append($"({here.X:0},{here.Y:0} {here.Width:0}x{here.Height:0})");

            at = Safe.Read(at, static e => e.Parent, null);
        }

        var panel = Viewport(gc, Safe.Read(() => (ExileCore2.PoEMemory.Element)options[0], null));

        text.Append("   clipping at (")
            .Append($"{panel.X:0},{panel.Y:0} {panel.Width:0}x{panel.Height:0}")
            .Append(')');

        return text.ToString();
    }

    /// <param name="content">
    /// The option list's own rectangle, kept for the dump rather than used to decide anything.
    /// </param>
    /// <remarks>
    /// **The rectangles do track the scroll, and two dumps that said otherwise were both taken
    /// unscrolled.** Measured properly: with the list scrolled, the first option moved from
    /// (63,198) to (63,-125), the list itself moved with it, and the wanted option moved from
    /// (63,909) to (63,586). So a coordinate is a real screen position and containment is exactly
    /// the right question - it was the panel being compared against that was wrong, twice: the
    /// window is the full height of the screen and the list is taller than the screen, and neither
    /// clips anything.
    /// </remarks>
    public static bool Reachable(GameController gc, RectangleF at,
        ExileCore2.PoEMemory.Element panel = null)
    {
        if (at.Width <= 0f || at.Height <= 0f)
            return false;

        var window = Viewport(gc, panel);

        var middle = new Vector2(at.X + at.Width / 2f, at.Y + at.Height / 2f);

        // **The whole option, not its middle, and against the window's real bounds.**
        //
        // Measured: the option list is a column 1,467 tall starting at y=198, on a window 1,440
        // tall - so the last seven of nineteen options hang off the bottom of the screen entirely
        // and report perfectly ordinary rectangles while doing it. A centre inside the window is
        // not enough either, since an option half over the edge is half clickable.
        if (window.Width > 0f && window.Height > 0f &&
            (at.Left < window.Left || at.Right > window.Right ||
             at.Top < window.Top || at.Bottom > window.Bottom))
            return false;

        var screen = Safe.Read(() => gc.Window.GetWindowRectangle(), default(RectangleF));

        return screen.Width <= 0f ||
               (middle.X >= screen.Left && middle.X <= screen.Right &&
                middle.Y >= screen.Top && middle.Y <= screen.Bottom);
    }

    /// <summary>
    /// Which remnant the open window was found to belong to, held until that window goes away.
    ///
    /// **The cursor answers the question once and then stops answering it.** Under reads where the
    /// cursor is, and the cursor is on the button only until the player moves it - which they do
    /// immediately, to click an option. So the identity was correct for a frame and then collapsed
    /// back to "identity match failed" while the same window sat there unchanged.
    ///
    /// An identity is a fact about a window rather than about a moment, so it is kept for as long as
    /// the window is. Set only where it was established from the cursor; the fingerprint's own
    /// answers need no holding, since they are re-derived correctly on every frame from the window
    /// itself.
    ///
    /// Checked against the window before it is handed back, by the same test that guards a
    /// remembered identity from the placement run - a window whose recipes do not all carry this
    /// remnant's fixed rune at its fixed position is not this remnant's window. See Fits.
    /// </summary>
    private static Target _window;

    /// <summary>
    /// Drops that, once there is no window for it to be about.
    ///
    /// Called once a tick, because the window closing is the only thing that can end an identity and
    /// nothing inside this file runs while the window is shut. One read of one flag - the per-remnant
    /// work stays where it belongs, in Under, which runs when an answer is actually wanted.
    /// </summary>
    public static void Closed(GameController gc)
    {
        if (Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.IsVisible, false))
            return;

        _window = null;
        _from = Vector2.Zero;
    }

    /// <summary>
    /// Where the cursor was the first time this window was asked about.
    ///
    /// **The cursor is evidence about the click that opened the window, and only until it moves.**
    /// It rests on the button it pressed, which is what makes it an answer - but the player moves it
    /// straight away, and where it goes next is about nothing. Worse, where it goes next can be
    /// another remnant's button: the list is full of them, and a cursor wandering across the dig site
    /// would have handed the open window to whichever one it passed over last.
    ///
    /// So the reading is taken while the mouse is still, and a mouse that has moved ends the
    /// question rather than re-answering it. Exact, not within a few pixels: a mouse nobody has
    /// touched reports the same two numbers, and a tolerance here would only be inventing a distance
    /// over which a player has not really moved their hand.
    ///
    /// This also leaves room for the honest retry. A button read can fail for a frame, and while the
    /// cursor has not moved the next frame may ask again - it is the same click either way.
    /// </summary>
    private static Vector2 _from;

    /// <summary>
    /// The remnant whose combinations button the cursor is resting on, among those given.
    ///
    /// **A window opens because somebody clicked one particular button, and the cursor is still on
    /// it.** Every other way of telling two remnants apart has been tried and the dumps killed them
    /// one at a time: the option fingerprint ties when two remnants share a layout; the dig site
    /// removes the other expedition and nothing more; distance separated 887 grid and then failed at
    /// 56.6 against 56.8; SelectedRecipe looked decisive until a window turned up listing both
    /// candidates' selections; the sockets state lies after a roll, and both were rolled;
    /// Targetable.isTargeted reads false on both.
    ///
    /// What is left is the click, and it needs no watching. The label and its button stay readable
    /// while the window is up, and the cursor has not moved since it pressed one - so the question
    /// can simply be asked at the moment the answer is wanted, off one screen position and a
    /// rectangle per candidate. No tick, no history, nothing to go stale.
    ///
    /// **And no tolerance.** Pressing the button leaves the cursor on the button, so the test is
    /// whether the one contains the other - there is no drift to allow for and no radius to pick.
    /// Measured with a window up and two candidates on screen: 26 pixels to the centre of the button
    /// that opened it, well inside its 54 square, and 1,391 to the other.
    ///
    /// Asked only of remnants the fingerprint already accepts, so it can confirm one of them and
    /// never introduce one of its own. Two candidates under the cursor is the honest tie it has
    /// always been; none under it is the honest refusal.
    /// </summary>
    private static Target Under(GameController gc, List<Target> fits)
    {
        var cursor = Safe.Read(gc, static g =>
            new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

        if (cursor == Vector2.Zero)
            return null;

        Target only = null;

        foreach (var target in fits)
        {
            var button = Placement.Button(gc, target, Placement.CombinationsPath).Rect;

            if (button.Width <= 0f || button.Height <= 0f)
                continue;

            if (cursor.X < button.Left || cursor.X > button.Right ||
                cursor.Y < button.Top || cursor.Y > button.Bottom)
                continue;

            if (only != null)
                return null;

            only = target;
        }

        if (only != null)
            _window = only;

        return only;
    }

    /// <summary>Where the cursor sits against each candidate's button, for the dump.</summary>
    public static string Pointing(GameController gc, List<Target> fits)
    {
        var cursor = Safe.Read(gc, static g =>
            new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

        if (cursor == Vector2.Zero || fits == null || fits.Count == 0)
            return "no cursor read";

        var said = new List<string>();

        foreach (var target in fits)
        {
            var button = Placement.Button(gc, target, Placement.CombinationsPath).Rect;

            said.Add($"({target.Grid.X:0},{target.Grid.Y:0}) " +
                     (button.Width <= 0f
                         ? "no button on screen"
                         : $"{Vector2.Distance(cursor, new Vector2(button.X + button.Width / 2f, button.Y + button.Height / 2f)):0} px"));
        }

        return $"cursor at ({cursor.X:0},{cursor.Y:0}), on the button counts: " +
               string.Join(", ", said) +
               (_window != null
                   ? $"; holding ({_window.Grid.X:0},{_window.Grid.Y:0}) for this window"
                   : _from == Vector2.Zero
                       ? "; nothing held for this window yet"
                       : cursor == _from
                           ? "; nothing held yet, cursor has not moved since this window opened"
                           : $"; nothing held, and the cursor has moved from ({_from.X:0},{_from.Y:0}) " +
                             "since - so it is no longer evidence");
    }

    /// <summary>
    /// Whether a row sits wholly inside the panel that clips the option list.
    ///
    /// No viewport means no clipping known, and then everything is drawn - a missing measurement
    /// should not blank the display. See Draw.
    /// </summary>
    /// <summary>
    /// What the last draw put on each option row, for the dump. See where it is filled.
    /// </summary>
    public static string Drew => _drew.Count == 0
        ? "nothing drawn - the window has not been drawn since the plugin loaded"
        : string.Join((char)10 + "      ", _drew);

    private static readonly List<string> _drew = new();

    /// <summary>
    /// Which runes one combination puts into a propagating slot, from the priced reward list.
    ///
    /// Null when the remnant has no reward by that name, which is an ordinary state rather than a
    /// fault: the list is priced asynchronously and deduplicated by name, so a window open before the
    /// prices land has nothing to match against. The line then carries its figures and no names.
    /// </summary>
    /// <summary>
    /// Which of a remnant's rewards the solver took, or minus one when it has not said.
    ///
    /// **The one place that answers "what was this remnant scored with".** Five callers used to decide
    /// it for themselves through Take - the ground text, the window border, the placement run's click,
    /// the score card and the dump - each with its own rates, and every one of them a second opinion
    /// about a decision the objective had already made. They agreed while nothing separated them and
    /// stopped the moment the objective learnt to discount a duplicate: the ground read Expansive Alloy
    /// beside a plan scored on Cyclonic Alloy, and the run would have clicked the first.
    ///
    /// Minus one is an ordinary answer - nothing solved yet, a remnant no chain reaches, or one under an
    /// explosive already down, which lives in env.Shown and is never scored - and the caller then falls
    /// back on Take, which is the only answer available there. See Planner.Chosen.
    /// </summary>
    public static int Solved(Target remnant)
    {
        if (remnant?.Rewards is not { Count: > 0 })
            return -1;

        var cell = ((int)MathF.Round(remnant.Grid.X), (int)MathF.Round(remnant.Grid.Y));

        if (!Planner.Chosen.TryGetValue(cell, out var picked) ||
            string.IsNullOrWhiteSpace(picked.Reward))
            return -1;

        // **The recipe identifies it; the name does not.** A remnant can offer two combinations
        // under one name - two "Leylines", one using six runes and one four - and this returns the
        // row the run will CLICK, so meeting the wrong one first clicks the wrong combination and
        // the remnant is then spent on it. The solver records which it took; this reads that.
        //
        // The name is the fallback for a recipe the game states no id for, where the two really are
        // indistinguishable rather than being confused. See Valuation.Top.
        for (var i = 0; i < remnant.Rewards.Count; i++)
        {
            if (picked.Recipe is { Length: > 0 } && remnant.Rewards[i].Recipe.Length > 0
                    ? string.Equals(remnant.Rewards[i].Recipe, picked.Recipe, StringComparison.Ordinal)
                    : string.Equals(remnant.Rewards[i].Name, picked.Reward,
                        StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The combination the last detailed scoring pass took at this remnant, or null.
    ///
    /// Null is an ordinary answer and the caller falls back on Take: nothing solved yet, or the chain
    /// does not reach this remnant, or it sits under an explosive already down - where it lives in
    /// env.Shown and is never scored. See Planner.Chosen and Solved.
    /// </summary>
    private static Reward Took(Target remnant)
    {
        var at = Solved(remnant);

        return at >= 0 ? remnant.Rewards[at] : null;
    }

    /// <summary>
    /// The priced entry for one window row, found by the recipe the row offers.
    ///
    /// **A reward name is not an identity, and this used to join on one.** It returned the FIRST
    /// entry of that name, so a remnant offering "Skill Level 20: Leylines" and "Skill: Leylines" -
    /// both named "Leylines", six propagating slots against four - painted the four slot row's
    /// runes onto whichever was asked about. The same dump held two rows named "Skyfall".
    ///
    /// The recipe id is carried on the Reward for exactly this. Falls back to the name where the
    /// game states no id, which is the one case where the two rows really are indistinguishable.
    /// </summary>
    private static Reward Priced(Target remnant, string recipe, string reward)
    {
        if (remnant == null)
            return null;

        if (!string.IsNullOrWhiteSpace(recipe))
        {
            foreach (var held in remnant.Rewards)
            {
                if (string.Equals(held.Recipe, recipe, StringComparison.Ordinal))
                    return held;
            }
        }

        if (string.IsNullOrWhiteSpace(reward))
            return null;

        foreach (var held in remnant.Rewards)
        {
            if (held.Recipe.Length == 0 &&
                string.Equals(held.Name, reward, StringComparison.OrdinalIgnoreCase))
                return held;
        }

        return null;
    }




    private static bool Within(RectangleF view, RectangleF at) =>
        view.Width <= 0f || view.Height <= 0f ||
        (at.Left >= view.Left && at.Right <= view.Right &&
         at.Top >= view.Top && at.Bottom <= view.Bottom);

    /// <summary>The first option with anything behind it, for walking up to the clipping panel.</summary>
    private static ExileCore2.PoEMemory.Element Element(List<Expedition2WindowOption> options)
    {
        foreach (var option in options)
        {
            if (option != null)
                return option;
        }

        return null;
    }

    /// <summary>Whether the window lists this reward at all, geometry or no geometry.</summary>
    private static bool Offers(List<Expedition2WindowOption> options, Valuation valuation, string name)
    {
        foreach (var option in options)
        {
            if (option != null &&
                string.Equals(Valuation.Name(Safe.Read(() => option.Recipe, null)), name,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Which remnant offers exactly what the window is showing.
    ///
    /// A remnant's reachable combinations follow from its socket count and its fixed rune, so no
    /// two remnants with different layouts offer the same list - and the plugin already reads that
    /// list for every remnant in the site, from any distance, without opening anything. Comparing
    /// the two is therefore an identity check rather than an inference.
    ///
    /// By reward name rather than by recipe object, because that is the form the reward list is
    /// kept in: collapsed by name, richest of each, which is exactly what a comparison wants.
    ///
    /// It answers only on a clear winner - a strict majority of the window's names, and no other
    /// remnant matching as many. Two remnants CAN share a layout, and then their reward lists are
    /// identical and there is nothing here to tell them apart; the distance rule below gets that
    /// case, and gets it right, because a tie here means the reward half of the answer is the same
    /// either way and only the propagation differs.
    /// </summary>
    private static Target Offering(GameController gc, Scan scan)
    {
        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window?.Options, null);

        if (options == null || options.Count == 0 || scan == null)
            return null;

        // **Every recipe on offer carries the remnant's fixed rune at its fixed position.** That is
        // what the window is: the list of combinations reachable from the rune already socketed, so
        // the rune the whole list agrees on at some position IS the fixed rune, and the position it
        // agrees at IS the fixed position. Neither is a guess and neither is a name.
        //
        // This replaced a count of overlapping reward NAMES, which was a poor proxy for the same
        // idea - it needed an arbitrary majority, it compared strings the game never compares, and
        // it could be defeated by two remnants whose reward lists happened to look alike. The
        // structure cannot be defeated that way. Credit where it is due: ExpeditionIcons identifies
        // the window this way and says so in the same words.
        var agreed = Agreed(options);

        if (agreed == null)
            return null;

        // How many runes the richest combination on offer uses, which is the remnant's socket count
        // wherever it offers anything at its full width. A remnant that offers nothing at full width
        // reads as narrower than it is, so this narrows candidates rather than admitting them.
        var width = 0;

        foreach (var option in options)
        {
            var needs = Safe.Read(() => option.Recipe.RuneCountRequired, 0);

            if (needs > width)
                width = needs;
        }

        var fits = new List<Target>();

        foreach (var target in scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant || string.IsNullOrEmpty(target.FixedRune) ||
                target.FixedSlot < 0 || target.FixedSlot >= agreed.Length)
                continue;

            if (!string.Equals(agreed[target.FixedSlot], target.FixedRune, StringComparison.Ordinal))
                continue;

            // Where the window offers a combination at full width, the socket count must agree too.
            if (width > 0 && target.Sockets > 0 && target.Sockets != width)
                continue;

            fits.Add(target);
        }

        if (fits.Count == 0)
        {
            // **A remnant with no fixed rune cannot be fingerprinted, and one site is built that
            // way.**
            //
            // The fingerprint IS the rune every offered recipe shares at some slot. The heath
            // remnant starts empty - the runic henges add its markers, see Valuation - so nothing
            // agrees at any slot and the filter above cannot admit it: it wants a FixedRune and a
            // FixedSlot and there are neither. Measured on Moor of Fallen Skies: "294 options,
            // widest combination 8 runes, every option agrees on [0:(differs) ... 7:(differs)]",
            // then "1 remnants in the scan, 1 with no fixed rune read yet, 0 matching".
            //
            // **So the answer comes from there being nothing to confuse it with.** The window belongs
            // to a remnant of this dig site, and if the site holds exactly one remnant then that is
            // the one - no evidence needed beyond counting, and none available.
            //
            // Held to both conditions rather than either. One remnant on its own is not enough: a
            // remnant WITH a fixed rune that the fingerprint rejected was rejected for a reason, and
            // naming it anyway would undo the check. It has to be the case the fingerprint cannot
            // speak to, which is the empty remnant.
            var dig = Detonator.DetonatorGridPosition(gc);
            Target lone = null;
            var remnants = 0;

            foreach (var target in scan.Targets)
            {
                if (target.Kind != TargetKind.Remnant ||
                    (dig != Vector2.Zero && Vector2.Distance(target.Site, dig) >= 1f))
                    continue;

                remnants++;
                lone = target;
            }

            return remnants == 1 && lone != null && string.IsNullOrEmpty(lone.FixedRune)
                ? lone
                : null;
        }

        // **A map holds two expeditions, and the second one's remnants were being allowed to
        // create a tie.**
        //
        // The fingerprint does its job: Sky at slot 1 over six sockets, matched by the remnant the
        // player was standing at and by one 887 grid away on the OTHER dig site. Two matches, so
        // the refusal below fired, and the overlay said it could not tell which remnant a window
        // belonging to the one under the player's feet was for.
        //
        // Narrowed to this dig site, which is a fact about the remnants and not a guess about the
        // player. Every target already carries the site it belongs to - Scan assigns it and the
        // planner has always used it to keep one expedition's markers out of the other's - and a
        // remnant on a different site did not produce a window opened at this one.
        //
        // NOT a distance rule, which is the thing that must not creep back in here. Distance was
        // tried as a fallback for when the fingerprint declines and it named a rolled remnant
        // seventeen grid away while the window belonged to one at fifty-three. This does not rank
        // by distance, does not answer when the fingerprint has already failed, and cannot promote
        // a remnant the fingerprint rejected: it only removes candidates that belong to a different
        // dig site, and then asks the same question as before.
        var site = Detonator.DetonatorGridPosition(gc);
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        // **This dig site first, because that is a fact about the remnant** rather than a guess
        // about the player. See the note above for the two expeditions that were creating ties.
        var here = new List<Target>();

        foreach (var target in fits)
        {
            if (site != Vector2.Zero && Vector2.Distance(target.Site, site) >= 1f)
                continue;

            here.Add(target);
        }

        // **One match on this dig site is the answer, however far away it is.**
        //
        // Reach is here to separate several candidates and it was also rejecting the only one. The
        // premise it rests on - that a window opens from arm's reach - does not hold when placing an
        // explosive whose blast covers a remnant opens that remnant's window from wherever the
        // player happens to be standing.
        //
        // Measured: the fingerprint named one remnant, on this dig site, and the dump read "fixed
        // rune matches but it is 109 away, past the 60 a window can be opened from". So the overlay
        // reported that it could not tell which remnant a window it had just identified belonged to,
        // and the placement run stopped to ask the player to choose.
        //
        // A unique agreement on the fixed rune, its position and the socket count, among the
        // remnants of the dig site in front of you, is the strongest evidence this path has. Being
        // far away cannot make it weaker - there is nothing left for it to be confused with. Reach
        // still does its job below, where there is more than one candidate and something has to
        // separate them.
        if (here.Count == 1)
            return here[0];

        // **The last explosive's blast first, then arm's reach.**
        //
        // Placing an explosive whose blast covers a remnant opens that remnant's window from wherever the
        // player stands, and it is the more direct evidence of the two: the window appears as the explosive
        // lands, on a remnant the blast plainly covers. Reach is about where the player stands, which a
        // window opened this way says nothing about. Measured on Scorched Cay: two remnants fitted the
        // fingerprint at 92.3 and 157.6 from the player, reach rejected both, and the run stopped to ask -
        // while the explosive just placed at (369,758) was 36.8 from the first and over 200 from the second.
        //
        // **Only a remnant still waiting on a choice.** A blast from earlier on covers remnants whose windows
        // were dealt with long ago; without this, walking up to a second remnant with the same fingerprint
        // would hand its window to the first. A window a blast has just opened is on a remnant nothing has
        // been picked for yet.
        //
        // The radius is the game's own figure, before the plugin's circle correction, plus the three grid of
        // headroom Reach uses: the remnant's extent is about 2.25 and the correction is -2.25, so the raw
        // radius is already the catch distance and the headroom covers rounding. Exactly one survivor, as
        // everywhere here, or it falls through to reach. See Detonator.ExplosionRadius.
        if (Detonator.LastExplosiveGridPosition(gc) is var last && last != Vector2.Zero)
        {
            var catches = Detonator.ExplosionRadius(gc) + 3f;
            Target blasted = null;
            var inBlast = 0;

            foreach (var target in here)
            {
                if (Vector2.Distance(target.Grid, last) > catches ||
                    !Safe.Read(() => Valuation.NothingChosen(target.Entity), false))
                    continue;

                blasted = target;
                inBlast++;
            }

            if (inBlast == 1)
                return blasted;
        }

        Target only = null;
        var matches = 0;

        foreach (var target in here)
        {

            // **And within arm's reach, because the window only opens from there.**
            //
            // This is a BOUND, not the distance rule that was tried and rejected. That one ranked
            // candidates by distance and answered whenever the fingerprint declined, which is how it
            // named a rolled remnant seventeen grid away while the window belonged to one at
            // fifty-three. This removes candidates and never promotes one: a remnant the fingerprint
            // rejected stays rejected, a tie between two remnants both in reach stays a refusal, and
            // nothing here ever picks the nearest of anything.
            //
            // The game's interaction range with headroom - see Reach, where the readings are. It
            // discriminates within a single dig site, which is the whole reason for having it: on
            // the site this was worked out against, three remnants sat inside it and four outside.
            var away = Away(target, player);

            if (away >= 0f && away > Reach)
                continue;

            only = target;
            matches++;
        }

        // **The propagation outline would break a tie and cannot be read.** Checked, so it is not
        // checked again: the game outlines the propagating runes in yellow both on the remnant and
        // in this window, and which slots propagate is a fact about the remnant - PassedOnRunePositions
        // - that does NOT follow from its area level, socket count or fixed rune. It is therefore the
        // one remaining thing that could separate two remnants offering the same list.
        //
        // There is no element behind it. Dumped three levels under an option with the window open:
        // the six rune icons have no children at all, and BordColor, BgColor, TextColor,
        // isHighlighted, HasShinyHighlight, IsSaturated, IsActive, Type, TextureName and
        // TextureNameRaw read identically on every one of them, outlined or not. The client draws
        // the outline without an element carrying it.
        //
        // **The button under the cursor, when the fingerprint has left more than one.**
        //
        // Last, because it is the only test here that asks about the player rather than about the
        // remnants - so everything that can be settled from the world is settled first, and this is
        // reached only by a tie that nothing else could break. See Under.
        //
        // The answer this window has already given comes first, because the cursor will have moved
        // on by the second frame and the window has not. See _window.
        if (matches > 1)
        {
            if (_window != null && fits.Contains(_window) && Fits(gc, _window))
                return _window;

            var cursor = Safe.Read(gc, static g =>
                new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

            if (_from == Vector2.Zero)
                _from = cursor;

            // Still where it was when this window was first asked about - so it is still the cursor
            // that pressed the button, and still worth reading. See _from.
            if (cursor != Vector2.Zero && cursor == _from && Under(gc, fits) is { } clicked)
                return clicked;
        }

        // **One, or none.**
        //
        // Two remnants ON THE SAME SITE can carry the same rune in the same slot at the same socket
        // count, and then they offer the same list and are genuinely indistinguishable from the
        // window. The only correct answer for that is nothing, and it stays nothing - the narrowing
        // above removes other sites' remnants, it does not rank the ones that are left.
        // ExpeditionIcons settles it by distance instead; that
        // answers "which remnant is nearest", which differs from "which remnant is this window"
        // exactly when it matters - the player walks past one remnant to open another's. The run
        // never needs this path, because it knows what it opened; only a hand-opened window
        // reaches here, and "I cannot tell" is a thing it is allowed to say.
        return matches == 1 ? only : null;
    }

    /// <summary>
    /// How far a remnant is from the last explosive placed, against the distance its blast catches at, for the
    /// dump line of a remnant Reach rejected. Offering takes the one remnant inside the blast with nothing chosen
    /// yet, before it asks about reach. Empty when nothing is placed.
    /// </summary>
    private static string BlastSaid(GameController gc, Target target)
    {
        var last = Detonator.LastExplosiveGridPosition(gc);

        if (last == Vector2.Zero)
            return "";

        var from = Vector2.Distance(target.Grid, last);
        var catches = Detonator.ExplosionRadius(gc) + 3f;

        var waiting = Safe.Read(() => Valuation.NothingChosen(target.Entity), false);

        return $"; {from:0.#} from the last explosive at ({last.X:0},{last.Y:0}), " +
               (from <= catches ? $"INSIDE its blast of {catches:0.#}" : $"outside its blast of {catches:0.#}") +
               (waiting ? ", nothing chosen yet" : ", a reward already chosen");
    }

    /// <summary>
    /// Whether the open window CONTRADICTS this remnant.
    ///
    /// Not an identification - two remnants can pass this for the same window. It is the negative
    /// that carries the weight: a remnant whose fixed rune is not the rune every offered recipe
    /// carries at that position certainly did not produce this window, and that is the only thing
    /// being asked. See Whose.
    ///
    /// **No evidence is not evidence against.** A remnant without a fixed rune offers nothing to
    /// contradict, and the Heath sites build remnants exactly that way - they start empty and runic
    /// henges add the markers, so the remnant reads eight sockets, FixedRunePosition -1 and a blank
    /// rune id. Failing those would have stopped the run on every one of them, on the grounds that
    /// a check with no input did not pass. See Valuation.Everything, which skips the same two
    /// constraints for the same reason.
    /// </summary>
    private static bool Fits(GameController gc, Target remnant)
    {
        if (remnant == null)
            return false;

        if (string.IsNullOrEmpty(remnant.FixedRune) || remnant.FixedSlot < 0)
            return true;

        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window?.Options, null);

        if (options == null || options.Count == 0)
            return true;

        var agreed = Agreed(options);

        if (agreed == null || remnant.FixedSlot >= agreed.Length || agreed[remnant.FixedSlot] == null)
            return true;

        return string.Equals(agreed[remnant.FixedSlot], remnant.FixedRune, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rune every offered recipe carries at each position, and null where they differ.
    ///
    /// Ids rather than objects, since two reads of the same rune are two instances. Null when the
    /// window has nothing readable to compare, which is not the same as an array of nulls: one is
    /// "no evidence", the other is "the evidence agrees on nothing". See Offering.
    /// </summary>
    /// <summary>
    /// Why the window could not be attributed, in the terms the test is actually written in.
    ///
    /// **"No remnant identified" names the outcome and not one of its causes.** There are four, and
    /// they want four different responses: the window's recipes agree on no rune at all; they agree
    /// on one no remnant in the scan carries; the scan has not read a fixed rune for the remnants
    /// yet, so nothing can match; or two remnants match and the tie is deliberately not broken. From
    /// outside those are one message, and the only way to tell them apart was to read the code and
    /// guess which branch ran.
    ///
    /// Written for the dump rather than the screen: the player needs to know it has no answer, and
    /// whoever is fixing it needs to know why.
    /// </summary>
    public static string Explain(GameController gc, Scan scan, Valuation valuation)
    {
        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window?.Options, null);

        if (options == null || options.Count == 0)
            return "the window lists no options, so there is nothing to fingerprint";

        if (scan == null)
            return "no scan";

        var agreed = Agreed(options);

        if (agreed == null)
            return $"the {options.Count} options carry no readable runes, so no fingerprint";

        var shape = new List<string>();

        for (var i = 0; i < agreed.Length; i++)
            shape.Add($"{i}:{agreed[i] ?? "(differs)"}");

        var width = 0;

        foreach (var option in options)
        {
            var needs = Safe.Read(() => option.Recipe.RuneCountRequired, 0);

            if (needs > width)
                width = needs;
        }

        var here = Detonator.DetonatorGridPosition(gc);
        var at = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        var lines = new List<string>
        {
            $"{options.Count} options, widest combination {width} runes, " +
            $"every option agrees on [{string.Join(" ", shape)}]",
            $"      player at ({at.X:0},{at.Y:0}), reach bound {Reach:0} grid",

        };

        // **What the window is actually offering, by recipe id.** Every question asked of this
        // diagnostic so far has come down to whether some remnant could have produced this list, and
        // the list itself was the one thing not in it - so the answers had to be inferred from the
        // agreed-rune row, which is a summary of it. Ids rather than reward names because the
        // identity is what matters: a recipe id is the game's own handle on a combination, while two
        // different combinations can pay out the same reward.
        foreach (var option in options)
        {
            var recipe = Safe.Read(() => option.Recipe, null);

            lines.Add($"      offers {Safe.Read(() => recipe.Id, null) ?? "(unreadable)"} " +
                      $"[{Valuation.Name(recipe) ?? "?"}] " +
                      $"{Safe.Read(() => recipe.RuneCountRequired, 0)} runes");
        }

        var remnants = 0;
        var unread = 0;
        var fits = 0;
        var matched = new List<Target>();

        foreach (var target in scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            remnants++;

            if (string.IsNullOrEmpty(target.FixedRune) || target.FixedSlot < 0)
            {
                unread++;

                continue;
            }

            var why = target.FixedSlot >= agreed.Length
                ? $"its fixed slot {target.FixedSlot} is past the window's {agreed.Length} positions"
                : !string.Equals(agreed[target.FixedSlot], target.FixedRune, StringComparison.Ordinal)
                    ? $"the window agrees on {agreed[target.FixedSlot] ?? "(differs)"} at slot " +
                      $"{target.FixedSlot}, this carries {target.FixedRune}"
                    : width > 0 && target.Sockets > 0 && target.Sockets != width
                        ? $"fixed rune matches but it has {target.Sockets} sockets against the " +
                          $"window's {width}"
                        : Away(target, at) is var gone && gone >= 0f && gone > Reach
                            ? $"fixed rune matches but it is {gone:0.#} away, past the {Reach:0} a " +
                              "window can be opened from" + BlastSaid(gc, target)
                            : "MATCHES";

            if (why == "MATCHES")
            {
                fits++;
                matched.Add(target);
            }

            // **And what this remnant says it has selected.**
            //
            // A selection is one of the combinations the remnant can reach, so it is one of the rows
            // in that remnant's own window - which makes a candidate whose selection the window does
            // not list a candidate that did not produce it. Printed rather than acted on until it is
            // seen to discriminate: the two remnants this was written for are both rolled, to
            // different rewards, and whether one window lists both selections is exactly the thing
            // nobody could answer from a dump.
            var picked = Safe.Read(() => valuation.ChosenName(target.Entity), null);

            lines.Add($"      ({target.Grid.X:0},{target.Grid.Y:0}) rune {target.FixedRune} at slot " +
                      $"{target.FixedSlot}, {target.Sockets} sockets, site " +
                      $"({target.Site.X:0},{target.Site.Y:0})" +
                      $"{(Vector2.Distance(target.Site, here) < 1f ? " THIS ONE" : "")}, " +
                      $"{Away(target, at):0.#} away (game) / " +
                      $"{(at == Vector2.Zero ? -1f : Vector2.Distance(target.Grid, at)):0.#} " +
                      $"(computed), selected " +
                      $"{(picked == null ? "UNREADABLE" : picked.Length == 0 ? "NOTHING" : picked)}" +
                      $": {why}");
        }

        if (matched.Count > 0)
            lines.Add($"      {Pointing(gc, matched)}");

        lines.Insert(1, $"      {remnants} remnants in the scan, {unread} with no fixed rune read " +
                        $"yet, {fits} matching");

        return string.Join("\n", lines);
    }

    private static string[] Agreed(List<Expedition2WindowOption> options)
    {
        var first = Safe.Read(() => options[0].Recipe?.Runes, null);

        if (first == null || first.Count == 0)
            return null;

        var agreed = new string[first.Count];

        for (var position = 0; position < first.Count; position++)
        {
            var rune = Safe.Read(() => first[position]?.Id, null);

            if (string.IsNullOrEmpty(rune))
                continue;

            var shared = true;

            foreach (var option in options)
            {
                var runes = Safe.Read(() => option.Recipe?.Runes, null);

                if (runes != null && position < runes.Count &&
                    string.Equals(Safe.Read(() => runes[position]?.Id, null), rune,
                        StringComparison.Ordinal))
                    continue;

                shared = false;

                break;
            }

            if (shared)
                agreed[position] = rune;
        }

        return agreed;
    }

    /// <summary>
    /// Which remnant this window belongs to.
    ///
    /// The window says nothing about it. It opens two ways - by interacting with a remnant, or by
    /// placing an explosive whose blast covers one - so it is identified by what the run opened, then
    /// by the fingerprint of the offered recipes, narrowed first by the last explosive's blast and
    /// then by the player's reach. See Offering.
    ///
    /// It refuses to answer rather than guess. If a second remnant is nearly as close, no rune is
    /// named - "this combination passes on Opulent" taken from the wrong remnant's slots is worse
    /// than saying nothing, because it reads exactly like the right answer.
    /// </summary>
    public static Target Whose(GameController gc, Scan scan)
    {
        // **The plugin opened it, so the plugin knows - and the window is asked to agree.**
        //
        // The identity itself is not an inference: the combinations window opens by clicking the
        // button on one remnant's own ground label, and the run clicked a particular one. But
        // remembering is not the same as being right, and the one thing that could go wrong here -
        // the memory outliving its window, or the run's target drifting - would be acted on
        // silently, which is the failure this whole path is built to avoid.
        //
        // So it is checked against the window rather than trusted. The check is cheap and it is
        // exact: the window IS the set of combinations reachable from this remnant's fixed rune at
        // its fixed position, so a window whose recipes do not all carry that rune there is not
        // this remnant's window. Disagreement is not resolved in either direction - it means one of
        // two things the plugin believes is wrong, and it stops instead.
        var ours = Placement.Opened;

        if (ours != null)
            return Fits(gc, ours) ? ours : null;

        // **Otherwise the option set, and otherwise nothing.**
        //
        // The window carries no reference to its remnant - Expedition2Window exposes a list of
        // options and each option exposes a recipe, and that is the whole of the binding. So for a
        // window somebody opened by hand there is no fact to read, only evidence, and the only
        // evidence worth acting on is a fingerprint that fits exactly one remnant.
        //
        // Two earlier answers were wrong and are worth recording rather than repeating.
        //
        // Standing next to it: the interaction range is bigger than it looks. Observed with the
        // window up - the player 34.7 grid from the remnant it belonged to, and 20 grid from a
        // different one. Any distance rule tight enough to be discriminating is too tight to be
        // right, and any rule loose enough to be right picks the wrong remnant. It was kept as a
        // fallback for a while and did exactly the damage that invites: with two remnants sharing
        // an option set the fingerprint declined to answer, the fallback answered instead, and it
        // named a ROLLED remnant seventeen grid away while the open window belonged to one at
        // fifty-three. The plugin then asked that window for a reward it does not offer and drew a
        // green border round something nobody had chosen.
        //
        // The "activated" state reading 2: it does mark exactly one remnant, but not the one being
        // looked at. It sat on the same remnant across four dumps while the open window belonged to
        // another. It means something, and it does not mean this.
        //
        // So there is no fallback. A window that cannot be attributed is a window nothing is
        // decided about - see Pick, which refuses rather than answering a different question.
        return Offering(gc, scan);
    }
}
