using ExileCore2;
using ExileCore2.PoEMemory.FilesInMemory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoExpedition;

/// <summary>
/// Which single remnant to roll next, and what it is worth.
///
/// **A roll is only answerable against a chain, which is why nothing here runs before a solve.** A
/// propagating rune is worth its percentage of the monsters downstream of its own link, and
/// "downstream" has no meaning until the links exist. The same roll is excellent on the first
/// remnant of a five link chain and worthless on the last.
///
/// So this runs on a solved plan and asks, per remnant: what would the chain be worth if this one
/// were rolled? The answer is an expectation over the population of remnants the game generates -
/// see Rolls - and it is taken by sampling rather than by arithmetic, because the thing being
/// averaged is a whole re-scored chain and not a number.
///
/// **One at a time, then solve again.** Rolling changes socket counts, which changes which spots
/// are worth taking, which changes what every other remnant's runes reach. A list of five rolls
/// computed against one chain would be five answers to a question that stopped being asked after
/// the first. So the advice is always a single remnant; take it, roll it, and the next solve asks
/// again.
///
/// **A partly placed chain is still worth advising on, and the environment already handles it.**
/// This first refused once anything was down, on the reasoning that a roll changes socket counts and
/// the placed prefix was chosen against the old ones. True, and not a reason to go quiet: the prefix
/// is fixed whether or not anything is rolled, and every remnant still ahead of it is as live a
/// decision as it was before the first explosive landed.
///
/// The placed explosives are taken into account rather than ignored. Planning.Remaining drops the
/// content they already cover, Secured banks the propagation they already bought, and the origin
/// moves to the last one down - so the chain being scored here is the chain still to come, judged
/// from where it will actually start.
///
/// The one thing it cannot advise on is a remnant a placed explosive already covers. Those leave the
/// target list entirely, and what survives of them is a single banked percentage with no rune names
/// in it, so there is nothing left to compare a roll against. Said out loud rather than passed over
/// in silence - see the count in Telling.
///
/// What it deliberately does NOT do: click anything. The advice appears over the remnant's own
/// Liquid Verisium button and the player presses it, because a roll is irreversible, costs currency
/// and rests on a sample of forty two outcomes. See DisplaySettings.RollFigures.
/// </summary>
internal sealed class Rolling
{
    /// <summary>The one the plugin uses. Static, because it is one table per session and every caller wants the same one.</summary>
    public static readonly Rolling Here = new();

    /// <summary>What to do about one remnant, and why.</summary>
    /// <param name="Exact">The enumerated half - a roll's worth with the chain held still.</param>
    /// <param name="Extra">
    /// The sampled half - what re-solving adds beyond what it would have added anyway.
    ///
    /// **Kept apart from Exact because they fail differently.** One is arithmetic over the rune
    /// table and is either right or wrong; the other is an average over eight re-solves and can be
    /// noisy, or can quietly carry a gain that had nothing to do with the roll. A single total hides
    /// which of the two is speaking, and two rounds of diagnosis have been spent guessing at it.
    /// </param>
    /// <param name="Error">
    /// The standard error of the sampled half, which says how much of Extra to believe.
    ///
    /// Nought when the remnant was screened out before the deep pass ran, which is not the same as
    /// a precise nought and reads as a blank. See Premium.
    /// </param>
    /// <param name="Landed">
    /// How many draws came back above nought, out of the samples taken.
    ///
    /// Read beside Error rather than instead of it. Most draws are exactly nought, so a mean can
    /// rest on one draw in twenty while its standard error looks small.
    /// </param>
    internal sealed record Verdict(Vector2 Grid, double Gain, string Why,
        double Exact = 0d, double Extra = 0d,
        double Error = 0d, int Landed = 0, int Samples = 0, bool Refused = false);

    /// <summary>
    /// The advice as it stands, keyed by the remnant's cell so the overlay can find it.
    ///
    /// **Replaced whole rather than edited in place.** It is written by a background pass and read
    /// by the frame that draws the labels, and a dictionary being rebuilt underneath an enumeration
    /// is the one way this could take the HUD down. A finished copy swapped into the field is a
    /// single reference assignment, which the reader either sees or does not.
    /// </summary>
    private volatile Dictionary<(int X, int Y), Verdict> _said = new();

    /// <summary>The one remnant worth rolling, or null when none is.</summary>
    public Verdict Best { get; private set; }

    /// <summary>
    /// How far through the expensive pass it is, nought to one, or nought when nothing is running.
    ///
    /// **Said out loud because it is the only part of this that takes visible time.** Screening is
    /// milliseconds; the re-solves are most of a second, and a plugin quietly using a core for that
    /// long with nothing on screen is indistinguishable from one that has hung. The solver already
    /// counts itself down for the same reason.
    /// </summary>
    public float Through { get; private set; }

    /// <summary>Whether a pass is running now, for the readout.</summary>
    public bool Working => _working is { IsCompleted: false };

    /// <summary>
    /// How many passes have run at this site, so the readout can keep saying what the last one
    /// found. Nought means the question has not been asked yet, which is a different thing from
    /// having been asked and answered "nothing worth rolling". See Overlay.Status.
    /// </summary>
    public int Runs => _runs;

    /// <summary>
    /// How many remnants the last pass said are worth rolling.
    ///
    /// Nought is an answer, not an absence - it means the chain cannot be improved by rolling
    /// anything on this site, which is what tells you to get on with placing. See Overlay.Status.
    /// </summary>
    public int Rollable
    {
        get
        {
            var said = _said;
            var count = 0;

            foreach (var verdict in said.Values)
            {
                if (Advising(verdict.Grid))
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Whether the advice is about the chain currently on screen.
    ///
    /// **Two things have to have finished, and marking a remnant before both of them is a lie.** A
    /// verdict is computed against one solved chain; start another solve and the chain it argued
    /// about no longer exists, but the verdict stays published because dropping it would make the
    /// border flicker off and on every replan. So it stays in the dump, where it is dated, and goes
    /// off the screen, where it is not.
    ///
    /// The second wait is this pass itself: the figures are the previous answer until it finishes,
    /// and a border that says "roll this" while the arithmetic behind it is still running is the
    /// one case where being a second late costs nothing and being wrong costs a Verisium.
    /// </summary>
    public bool Fresh { get; private set; }

    /// <summary>
    /// Whether advice was abandoned because the placing started, rather than answered.
    ///
    /// **Abandoned and "nothing worth rolling" are opposite answers and looked identical.** Both ended
    /// with no roll being advised, so both drew the settled colour - and one of them means "go ahead"
    /// while the other means "you never found out". A pass cut off mid-run, or one that did advise a
    /// roll before a bomb landed on the chain it was weighed over, is a question left open, and the
    /// readout says so in the colour it uses for anything else wanting attention.
    ///
    /// Latched on the transition, because Nothing swaps the verdicts away and Rollable is read off
    /// them - asked afterwards, what the last pass found is already gone. See Consider.
    /// </summary>
    public bool Skipped { get; private set; }

    /// <summary>Whether the ground has been latched already, so the answer is captured once.</summary>
    private bool _latched;

    /// <summary>Why there is no advice, when there is none. For the dump and the overlay.</summary>
    public string Quiet { get; private set; } = "no plan solved yet";

    /// <summary>
    /// How many blocks of arrangements the least-advanced remnant has had walked.
    ///
    /// **The least, not the average.** The figure reads as how far the site has got, and a site is
    /// only as settled as its worst-covered remnant - one that has had a single round can still move
    /// by more than the advice's margin. Named for the walk rather than for the pass count, because
    /// a remnant whose enumeration is finished is skipped and its rounds stop climbing. Not
    /// Planner.Rounds, which counts the solver's restarts. See Enumerated.
    /// </summary>
    public int RoundsWalked { get; private set; }

    /// <summary>
    /// How many blocks it takes to walk every arrangement, which is the same for every remnant here.
    ///
    /// Every remnant on a site enumerates the same shapes - they are the shapes a roll can produce,
    /// which the site decides and the remnant does not - so one bound serves all of them. It is the
    /// largest arrangement count of any shape above ExactToSockets, over ArrangementsPerRound.
    ///
    /// One when nothing needs more than a single block, in which case the first answer is already
    /// exact and there is no progress to report.
    /// </summary>
    public int RoundsNeeded { get; private set; } = 1;

    /// <summary>
    /// Whether the last pass failed to walk anything new, so deepening has stopped.
    ///
    /// **A backstop, not a mechanism.** RoundsNeeded and what the walk actually reaches are worked
    /// out in two places and agree only while both apply the same skips; if they ever drift, the
    /// dispatch that runs another round while they differ would run for ever. A pass that did not
    /// raise RoundsWalked cannot be making progress whatever the bound claims, so it ends there and
    /// the dump says so. Cleared when the totals are, because a new chain deserves the attempt.
    /// </summary>
    public bool Stalled { get; private set; }

    /// <summary>
    /// Whether this is the remnant the advice is pointing at.
    ///
    /// **One answer, because there were two and they disagreed on screen.** The blue line was drawn
    /// from Best while the border round the Verisium button was drawn from a per-verdict Roll flag,
    /// and in the continuous mode those are different quantities: Best is re-picked every frame by
    /// Divert on what a roll destroys, and the flag was set once a pass by the enumerated ranking.
    /// So the line pointed at one remnant and the border ringed another.
    ///
    /// Best is the advice - it is what Divert sets and what the other modes set at the end of a
    /// pass - so everything that marks the advised remnant asks this.
    /// </summary>
    public bool Advising(Vector2 grid) =>
        Best is { } best && Vector2.Distance(best.Grid, grid) < 1f;

    /// <summary>
    /// Which reroll mode is in force. See RerollSettings.Mode.
    ///
    /// **Anything that is not Off reads as Continuous, because the other two modes are withdrawn.**
    /// The dropdown offers Continuous and Off only - see RerollSettings.Modes for why - and this is
    /// the other half of that: a settings file saved before they were withdrawn still holds "Best roll
    /// only" or "All rolls", and a value the list does not offer would otherwise reach a branch nobody
    /// can select and nobody is testing.
    ///
    /// One place, so the dropdown offers two and every test of the mode sees two. The branches for the
    /// withdrawn modes are left in Consider and its callers rather than deleted: they are the work in
    /// progress, not dead code, and nothing can reach them while this stands.
    /// </summary>
    public static string Mode(AutoExpeditionSettings settings)
    {
        var said = Safe.Read(() => settings?.Solver?.Reroll?.Mode?.Value, RerollSettings.Continuous);

        return said == RerollSettings.Off ? RerollSettings.Off : RerollSettings.Continuous;
    }

    /// <summary>
    /// Whether the chain the standing advice was weighed against has since been replaced.
    ///
    /// **Only the continuous mode can be in this state.** Every other mode blanks instead, which
    /// says the same thing by saying nothing. Here the advice is deliberately kept - a stale number
    /// about a chain one solve old beats an empty label while you are walking - so something has to
    /// say so out loud. See RerollSettings.Mode and Fresh.
    /// </summary>
    public bool Stale { get; private set; }

    /// <summary>
    /// Which remnant the continuous mode is pointing at, and why that one.
    ///
    /// For the dump. The enumerated gain on screen is not what chose - see Divert.
    /// </summary>
    public string Diverted { get; private set; } = "";


    /// <summary>
    /// How long the last pass took, which run it was, and whether one is running now.
    ///
    /// **A pass takes seconds and publishes nothing until it finishes**, so a dump taken during one
    /// shows the PREVIOUS answer - complete, plausible, and about a chain that has been replaced.
    /// That is fine behaviour and a trap for anybody comparing two dumps, so the run number and the
    /// age say which answer is being read.
    /// </summary>
    public string Telling => _telling +
                             (_working is { IsCompleted: false }
                                 ? " [A PASS IS RUNNING - the figures below are the previous one]"
                                 : _finished == default
                                     ? ""
                                     : $"; finished {(DateTime.UtcNow - _finished).TotalSeconds:N0}s ago");

    private string _telling = "has not run";
    private DateTime _finished;

    private Vector2 _for;
    private int _runs;

    /// <summary>
    /// Every verdict, for the dump.
    ///
    /// **The summary line could not be argued with.** It said which remnant won and how long the
    /// pass took, so a verdict that looked wrong - "only source of Volcanic" on a site where
    /// something else plainly carries Volcanic - had nothing behind it to check. The reasoning is
    /// already computed for the overlay; printing it costs a string.
    /// </summary>
    public string Verdicts()
    {
        var said = _said;

        if (said.Count == 0)
            return "        nothing weighed";

        var text = new List<string>(said.Count);

        foreach (var (cell, verdict) in said)
        {
            // **The re-route figure carries its own precision, because a margin uses it.** See
            // Premium: the 10% diversion margin is compared against this mean, and a margin under
            // the mean's own standard error decides nothing. Printed as the error and the number of
            // draws that landed, since most draws are nought and the count is what says whether the
            // mean rests on one of them.
            var spread = verdict.Samples == 0
                ? ""
                : $" +-{verdict.Error:0.0} over {verdict.Landed}/{verdict.Samples} draws" +
                  (verdict.Extra > 0d
                      ? $", {verdict.Error / verdict.Extra:0%} of it"
                      : "");

            text.Add($"        ({cell.X},{cell.Y}) {(Advising(verdict.Grid) ? "ROLL" : "keep")} " +
                     $"{verdict.Gain:+#,##0.0;-#,##0.0;0} [runes " +
                     $"layout {verdict.Exact:+#,##0.0;-#,##0.0;0} + " +
                     $"re-route {verdict.Extra:+#,##0.0;-#,##0.0;0}{spread}] - {verdict.Why}");
        }

        text.Sort(StringComparer.Ordinal);

        return string.Join("\n", text);
    }

    /// <summary>What was said about the remnant standing on this cell, if anything.</summary>
    public Verdict Of(Vector2 grid) =>
        _said.TryGetValue(((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var said)
            ? said
            : null;

    public void Forget()
    {
        _asked++;
        _said = new Dictionary<(int X, int Y), Verdict>();
        Best = null;
        Quiet = "no plan solved yet";
        _for = Vector2.Zero;

        // The totals are scores of a chain, and there is no longer a chain. See Gathering.
        _gathered = new Dictionary<(int X, int Y), (double Total, double Weight, int Round, bool More)>();
        _standing = new Dictionary<(int X, int Y), double>();
        _gatheredFor = "";
    }

    /// <summary>
    /// Forget the site as well as the advice, which is what a new dig site means.
    ///
    /// **Separate from Forget because Forget runs on every roll.** A roll drops the advice
    /// deliberately - it was weighed against the combination the roll has just replaced - and the
    /// record of a pass that threw must outlive that, or the one thing anybody is going to look for
    /// is destroyed by the next thing they do. See Failure.
    /// </summary>
    public void ForgetTheSite()
    {
        Forget();

        _failure = "";
        _failed = default;
    }

    /// <summary>
    /// A local rune list with no names: the expected worth spread over the expected distinct count.
    ///
    /// The scoring loop strikes out local runes the chain already propagates, which needs names it
    /// does not have here. A null id says "cannot be struck" rather than leaving the array empty,
    /// which would lose the count that concentration depends on.
    /// </summary>
    private static (string Id, float Worth)[] Anonymous(int count, float total)
    {
        if (count <= 0)
            return System.Array.Empty<(string, float)>();

        var each = total / count;
        var found = new (string, float)[count];

        for (var i = 0; i < count; i++)
            found[i] = (null, each);

        return found;
    }

    /// <summary>
    /// What one ordinary slot is worth on average, over the whole rune table.
    ///
    /// Exact for the purpose it is put to: the ordinary slots contribute by being added up, and the
    /// expectation of a sum is the sum of the expectations however the draws land.
    /// </summary>
    private static float Locally(Dictionary<string, (float Weight, string Scope, float Local)> known)
    {
        var total = 0f;

        foreach (var (rune, share) in Rolls.Runes)
        {
            if (known.TryGetValue(rune, out var said))
                total += share * said.Local;
        }

        return total;
    }

    /// <summary>
    /// How many DIFFERENT runes fill that many ordinary slots, on average.
    ///
    /// Concentration counts distinct runes rather than adding their values, so the count is the
    /// figure it needs and the count is not the slot total - runes do not stack, and the same one
    /// twice is one modifier. A rune is missing from k draws with probability (1-p) to the k, so the
    /// expected number present is the sum over the table of one minus that.
    ///
    /// Rounded, because a slot count has to be a whole number for the layering to use it, and the
    /// fraction it discards is worth less than the approximation it replaces.
    /// </summary>
    private static int Distinct(int slots)
    {
        if (slots <= 1)
            return slots;

        var total = 0d;

        foreach (var (_, share) in Rolls.Runes)
            total += 1d - Math.Pow(1d - share, slots);

        return Math.Max(1, (int)Math.Round(total));
    }

    /// <summary>
    /// How many draws the re-solve premium is averaged over.
    ///
    /// **Eight, and that is enough because of what is being averaged.** It is no longer the whole
    /// value of a roll - Enumerated answers that exactly, by adding up the rune table rather than
    /// drawing from it. What is left to sample is only what RE-SOLVING adds on top: nought on most
    /// draws, because most rolls do not make some other spot worth taking, and occasionally large.
    ///
    /// A quantity that is usually zero settles in a handful of draws. The same count applied to the
    /// whole score was moving by a hundred and fifty points between passes; applied to a difference
    /// that is mostly nothing, it barely moves at all.
    ///
    /// Eight also keeps the expensive half affordable: each draw is a full re-solve, and five
    /// remnants at eight is forty of them.
    /// </summary>
    private const int Deep = 8;

    /// <summary>How many remnants the chain already takes go through to the expensive pass.</summary>
    private const int Shortlist = 2;

    /// <summary>The least a roll must be worth before it is advised at all. See where it is used.</summary>
    private const double Worthwhile = 1d;

    /// <summary>How many the chain misses go through beside them. See Chosen.</summary>
    private const int Detoured = 3;

    /// <summary>Which solve the standing advice was worked out after. See Consider.</summary>
    private int _after = -1;

    /// <summary>
    /// Works out what to advise, once per solved plan.
    ///
    /// Gated on the plan object rather than on a timer: Planning hands back a new one on every
    /// solve, so a reference that has not moved is a question that has already been answered.
    /// </summary>
    /// <param name="settling">
    /// Whether the ground is still moving - a placement run under way, or explosives coming back
    /// off. See the note on dispatching below.
    /// </param>
    /// <param name="down">
    /// How many explosives are on the ground, read once by the tick and handed in.
    ///
    /// Passed rather than fetched so the caller's settling test and this cannot disagree about the
    /// same ground - they are one question and were being asked twice. See the call site.
    /// </param>
    public void Consider(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Planning planning, Valuation valuation, bool settling, int down)
    {
        var env = planning?.Env;
        var chain = planning?.Plan?.Points;

        // Whether anything may be drawn from this, worked out before the early returns so it is
        // answered on every frame rather than only on the frames that dispatch a pass. See Fresh.
        var mode = Mode(settings);

        if (mode == RerollSettings.Off)
        {
            Fresh = false;
            Stale = false;
            Diverted = "";
            Nothing("rolling advice is off");

            return;
        }

        // **The continuous mode does not blank while a solve runs, and that is its whole point.**
        //
        // Every other mode requires the standing plan to be the one the advice was weighed against,
        // so a solve in flight or a freshly published chain hides everything. That is the honest
        // readout when the advice is one careful answer per solve. It is the wrong one when the
        // solver never stops: the screen would be empty most of the time. So the advice is kept and
        // Stale says the chain has moved under it.
        Stale = _plan != null && planning.Plan != null && !ReferenceEquals(_plan, planning.Plan);

        // **And not on a pass being in flight either, which is the same mistake one level down.**
        //
        // Dropping the Searching term was not enough: a pass takes about 500ms per remnant, so
        // gating on Working blanked everything for the two seconds it ran and the advice flickered
        // off once per solve anyway. The answer from the last pass is exactly what this mode exists
        // to keep showing while the next one is worked out.
        //
        // What it does need is an answer to show at all, which is why _said rather than _plan: _plan
        // is set when a pass is DISPATCHED, so it is non-null before the first one has produced
        // anything.
        Fresh = mode == RerollSettings.Continuous
            ? _said.Count > 0
            : planning is { Searching: false } && !Working && _plan != null &&
              ReferenceEquals(_plan, planning.Plan);

        if (env == null || chain is not { Count: > 0 })
        {
            Nothing("no plan solved yet");

            return;
        }

        var site = Detonator.DetonatorGridPosition(gc);

        // **Nothing to say once an explosive is down, and that is a decision rather than a gap.**
        //
        // The order this is used in is: solve, then roll what is worth rolling, then place. Advice
        // during the placing half is advice about a decision already being acted on, and it was worse
        // than useless - it was stale. The environment every figure rests on is rebuilt only by a
        // SOLVE: a placed blast's remnants move from env.Targets to env.Shown - never scored, never
        // credited - at that moment and not before. So between a bomb landing and the next solve the
        // standing answer describes a site where that bomb had not landed, and "Reroll: true" outlived
        // the chain it was weighed over.
        //
        // The honest alternative to that is silence, not a better number. Getting the number right
        // mid-placement means weighing a roll against a partly committed route, which is a real
        // question with a fiddly answer that nobody asks: a roll is bought before the bombs go down or
        // not at all. Refused explicitly so the readout says why rather than showing a figure whose
        // basis has gone.
        if (down > 0)
        {
            // Captured on the way in, once, while there is still something to read. A finished pass
            // that advised nothing is the one answer placing cannot invalidate - a placed blast only
            // takes content out of what is left to win, so a roll that was not worth buying then is
            // not worth buying now. That verdict is left standing and goes on being drawn; anything
            // else is an open question. See Skipped.
            if (!_latched)
            {
                _latched = true;
                Skipped = !(Runs > 0 && Fresh) || Rollable > 0;
            }

            if (Skipped)
            {
                // **Withheld, not discarded, and the difference is an undo.**
                //
                // This called Nothing, which swaps the verdicts away for an empty map. Combined with
                // the gate below - same site, same solve, so no pass is dispatched - the advice was
                // gone for good: place one explosive and take it back off, and a standing ROLL came
                // back as "false", because Rollable was reading an empty map that nothing would ever
                // refill. The remnant it had named was lost with it.
                //
                // Nothing has to be destroyed to hide it. Every consumer - the readout, the named
                // marker, the per-remnant labels, the figures, the dump - is gated on Fresh, so
                // dropping that one flag withholds the lot. And withholding is reversible: an undo
                // back to clear ground restores advice that is once again exactly about the chain on
                // screen, which is the same chain it was computed from.
                Fresh = false;

                Quiet = "explosives are already down - rolls are worth advising before placing, " +
                        "so this one was left unanswered";
            }

            return;
        }

        // The ground is clear again, so whatever was latched about it no longer describes anything.
        // Fresh is recomputed from the plan at the top of every frame, so the advice held through the
        // placing comes back on its own from here.
        _latched = false;
        Skipped = false;

        // **Every frame, before the dispatch gates, because the gates return on most of them.**
        // The continuous mode's choice of remnant is not a product of the pass - the pass produces
        // the figures beside it - so it cannot be made where the pass is dispatched. See Divert.
        if (mode == RerollSettings.Continuous)
            Divert(gc, env, settings, scan, site);

        // **Gated on a solve having finished, not on the plan object having changed.**
        //
        // The two are not the same event. A plan is handed back fresh whenever the chain is
        // re-described - an undo walking the cursor back over its own links, a bake-off adopting a
        // winner - and none of those asked a new question. Gated on the reference, a run down a
        // five link chain dispatched a pass per link, each costing what a solve costs and each one
        // stale before it finished.
        //
        // The advice belongs directly after a solve, which is the moment there is a new chain to
        // advise on. After that it stands until something asks for another one: content changing,
        // or the key being pressed. See Planning.Solves.
        //
        // **Except while there are arrangements left to walk, which is another round rather than
        // another question.** The enumeration is progressive: a pass takes the next block of each
        // large shape's arrangements and adds it to the last, so the answer improves until every
        // arrangement has been walked and then cannot move. Gating rounds on solves made the
        // deepening wait for something unrelated to it - stand still with the figures at one round
        // of two and nothing would finish them, because a solve is what a changed site asks for and
        // the site had not changed.
        //
        // **Self-limiting, which is why it needs no clock.** RoundsWalked reaches RoundsNeeded and
        // the dispatches stop on their own; a remnant whose walk is finished is skipped rather than
        // re-walked, so the last rounds are cheaper than the first. A roll or a new chain resets the
        // totals - see Gathering - and the deepening starts again, which is the loop this mode is
        // for.
        //
        // Continuous only. The other modes are one careful answer per solve by definition, and
        // spending the machine between solves is the thing they are not for.
        var deepening = mode == RerollSettings.Continuous && Runs > 0 && !Stalled &&
                        RoundsWalked < RoundsNeeded;

        if (!deepening && Vector2.Distance(_for, site) < 1f && _after == Planning.Solves)
            return;

        // **Not while the ground is still moving.**
        //
        // The gate below is the plan object, which is exactly right for "has the question changed"
        // and exactly wrong for "is now the time to ask it": every explosive that lands triggers a
        // re-solve, every re-solve hands back a new plan, so a five link run dispatched five passes
        // - each one costing what a solve costs, each one answering a question about a chain that
        // was about to be a link shorter, and none of them drawn, because the run was still going
        // when the next one replaced it.
        //
        // Deferred rather than dropped. _plan is deliberately left where it is, so the moment the
        // run finishes the reference is still stale and the next tick asks once, about the state
        // the player is actually looking at. Nothing is drawn in the meantime - Fresh is false for
        // the same reason - which is the honest readout for advice that is no longer about the
        // chain in front of you.
        // Not in the continuous mode, which is defined by not waiting for the ground to settle.
        // The advice there is about the chain it names and says when that has been replaced.
        if (settling && mode != RerollSettings.Continuous)
            return;

        // **Off the frame, because this costs what a solve costs.** The shortlist pass re-solves
        // the chain once per sampled outcome - two remnants at a couple of dozen samples each - and
        // a hundred milliseconds spent here is a hundred milliseconds the HUD is not drawing. The
        // answer is advice about a decision nobody is making this instant, so it can arrive late.
        if (_working is { IsCompleted: false })
            return;

        _for = site;
        _plan = planning.Plan;
        _after = Planning.Solves;

        var asking = ++_asked;
        _floored = "";

        // **Everything the pass needs, read here on the frame, and that is a rule rather than a
        // tidiness.** What follows runs on a background thread, and two of the things it wants are
        // plain dictionaries the frame writes to: Insisted, which the must-take marker and the
        // hotkey both edit, and the weight reference table behind every rune's weight. A dictionary
        // being read while it is rehashed throws, or worse does not.
        // **This dig site's content, not the map's.**
        //
        // The planner has always built its environment from At(site), which is what keeps one
        // expedition's markers out of the other's and leaves loose remnants - the wild ones that
        // belong to no site and need no explosive - out of both. The reroll pass took the whole
        // scan instead, so it weighed remnants that are not part of this chain, cannot be part of
        // it, and have nothing to do with the plan it is advising on. Standing beside a wild
        // remnant it would tell you to roll that, which is advice about a different object
        // entirely: a loose remnant is shattered where it stands and no bomb is ever placed on it.
        //
        // The same list the plan was built from is the only defensible one to advise about.
        var targets = new List<Target>(scan?.At(site) ?? new List<Target>());
        var links = new List<Vector2>(chain);
        var marked = Marked(env);
        var known = Known(settings);

        // Only for the line that reports it. The environment has already accounted for these -
        // their content is out of the target list and their propagation is banked in Secured - so
        // this is the count of remnants the advice has nothing left to say about.
        var settled = Settled(gc, targets);

        // **Read here, on the thread that owns the reads.** Valuation goes to game memory and two
        // TimeCaches, and the advice runs on a task; gathering the shapes up front means the task
        // walks an immutable list instead. See ShapesARollCouldProduce.
        var shapes = ShapesARollCouldProduce(valuation);

        _working = Task.Run(() => BackgroundWork.Record("reroll advice", () =>
        {
            try
            {
                Weigh(settings, targets, env, links, settled, marked, known, asking, shapes);
            }
            catch (Exception ex)
            {
                // **The whole exception, not its message.** "Index was outside the bounds of the
                // array" names neither the array nor the method, and the pass runs on a task whose
                // stack reaches no log. One line on the overlay for the player, the full trace in
                // the dump for whoever has to find it.
                _failure = ex.ToString();
                _failed = DateTime.UtcNow;

                Quiet = "the reroll pass failed: " + ex.Message;
            }
        }));
    }

    private object _plan;
    private Task _working;

    /// <summary>
    /// What every pass at this chain has enumerated so far, per remnant cell.
    ///
    /// **Why the totals rather than the gain.** The gain is a weighted mean minus a baseline, and
    /// means do not add. The sum and the weight do, so those are what accumulate and the mean is
    /// taken at the end of each pass. See Enumerated.
    /// </summary>
    private Dictionary<(int X, int Y), (double Total, double Weight, int Round, bool More)> _gathered = new();

    /// <summary>
    /// Which chain and content the totals in _gathered are about.
    ///
    /// **They are scores of a particular chain, so a different chain makes them meaningless.** Not
    /// the plan's identity, which changes on every solve: most solves publish nothing new, and
    /// throwing the accumulation away each time would stop it ever converging. The links themselves,
    /// and what the remnants hold, are what the scores depend on. See Gathering.
    /// </summary>
    private string _gatheredFor = "";

    /// <summary>
    /// The score of each remnant as it stands, from the last pass that computed it.
    ///
    /// Kept beside the totals because the gain is the accumulated mean less this, and a remnant
    /// whose enumeration is finished is never enumerated again - so the figure has to survive
    /// without it. One score, so recomputing it would cost little; it is here to keep the exact
    /// case from having to call Enumerated at all.
    /// </summary>
    private Dictionary<(int X, int Y), double> _standing = new();

    /// <summary>The two halves of each gain, kept apart for the dump. See Verdict.</summary>
    private Dictionary<int, double> _exact = new();
    private Dictionary<int, double> _extra = new();

    /// <summary>The standard error of the sampled half, per remnant. See Premium.</summary>
    private Dictionary<int, double> _error = new();

    /// <summary>How many of the draws came back above nought, per remnant. See Premium.</summary>
    private Dictionary<int, int> _landed = new();

    /// <summary>An impossible figure the pass had to floor, for the dump. Empty when none.</summary>
    private string _floored = "";

    /// <summary>What the last pass had to correct, if anything. See Weigh.</summary>
    public string Floored => _floored;

    /// <summary>The last exception a pass threw, with its stack. Empty when none has.</summary>
    private string _failure = "";

    /// <summary>When that was, so the dump can say how long ago it happened.</summary>
    private DateTime _failed;

    /// <summary>
    /// Where a pass last threw, if one has. Printed by the dump in full.
    ///
    /// **Kept until the zone changes, not cleared by the next pass.** A throw here is intermittent
    /// - the seed moves with the run number - so clearing it on the next dispatch meant one
    /// successful pass erased the only record of it, and an F6 twenty seconds later showed nothing.
    /// The failure outlives the pass that caused it because that is when somebody comes looking.
    /// </summary>
    public string Failure => _failure;

    /// <summary>How long ago that was. Meaningless while Failure is empty.</summary>
    public TimeSpan SinceFailure => DateTime.UtcNow - _failed;

    /// <summary>
    /// Which question the running pass is answering, so a late answer to an old one is dropped.
    ///
    /// A pass takes long enough to outlive a zone change, and the task that finishes afterwards
    /// would publish advice about remnants in a dig site nobody is standing in. Bumped by Forget
    /// and by every dispatch; the pass checks it before it publishes anything.
    /// </summary>
    private int _asked;

    private void Nothing(string why)
    {
        Fresh = false;
        // Swapped rather than cleared: the frame may be walking it to draw the labels.
        _said = new Dictionary<(int X, int Y), Verdict>();
        Best = null;
        Quiet = why;
    }

    /// <summary>
    /// How many remnants a placed explosive already covers, and so how many are past advising on.
    ///
    /// The same reach test Planning.Remaining uses, because the two have to agree about which side
    /// of the line a remnant falls on - one saying it is in the environment while the other reports
    /// it as settled would be worse than either answer alone.
    /// </summary>
    private static int Settled(GameController gc, List<Target> scan)
    {
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed is not { Length: > 0 })
            return 0;

        var radius = Safe.Read(() => Detonator.BlastRadius(gc, 0f), null) ?? 0f;
        var count = 0;

        foreach (var target in scan)
        {
            if (target.Kind != TargetKind.Remnant || target.Spent)
                continue;

            var reach = radius + Extents.Of(target);

            foreach (var at in placed)
            {
                if (Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                {
                    count++;

                    break;
                }
            }
        }

        return count;
    }

    /// <summary>What has been insisted on, per target, taken on the frame. See Consider.</summary>
    private static Insisted.Said[] Marked(PlanEnvironment env)
    {
        var said = new Insisted.Said[env.Targets.Count];

        for (var i = 0; i < said.Length; i++)
            said[i] = Insisted.Here.Of(env.Targets[i].Grid);

        return said;
    }

    /// <summary>
    /// Every rune this pass might draw, with what it is worth and what it reaches, read on the frame.
    ///
    /// The whole table rather than the ones actually drawn, because which are drawn is decided by a
    /// random number on the other thread - and reading one more row than needed costs nothing next
    /// to reading any of them from the wrong thread.
    /// </summary>
    private static Dictionary<string, (float Weight, string Scope, float Local)> Known(
        AutoExpeditionSettings settings)
    {
        var found = new Dictionary<string, (float, string, float)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rune, _) in Rolls.Runes)
        {
            found[rune] = (Safe.Read(() => Runes.Weight(rune), 0f),
                Safe.Read(() => Runes.Scope(rune), "") ?? "",
                Safe.Read(() => Propagation.Locally(rune), 0f));
        }

        return found;
    }

    /// <summary>
    /// One shape a roll could produce, with everything it could then become.
    ///
    /// A roll randomises everything, so what comes out is a socket count, a rune, and the slot that
    /// rune is fixed in - and the recipes follow from those three. Which slots PROPAGATE is drawn
    /// separately and is not part of the shape; see where these are walked.
    /// </summary>
    /// <param name="Share">
    /// How likely this shape is: the socket count's share times the rune's. Chosen numbers, not
    /// measured ones - the game's table states which shapes are legal and nothing about how often.
    /// See Rolls.Runes.
    /// </param>
    /// <param name="Recipes">
    /// Every recipe this shape can produce, with the rune it puts in each slot and what its reward
    /// is worth. After a roll the player picks one of these, so the value of the shape is the best
    /// of them rather than an average over them.
    /// </param>
    /// <param name="Kept">
    /// The shape's distinct arrangements, worked out on first use and kept as long as the shape is.
    ///
    /// **They were rebuilt per remnant per pass and depend on neither.** A shape's arrangements
    /// follow from its socket count and its recipes, so every remnant on the site asked the same
    /// question and got a fresh answer, and progressive deepening multiplied that by the round
    /// count. Measured before this: a pass allocated 62MB, and five rounds back to back kept the
    /// process in gen0 collections - which stop every thread, the drawing one included, for 370ms
    /// out of a 240 frame window.
    ///
    /// Held here rather than in a cache beside the enumeration, so its lifetime is the shape's. The
    /// shapes are re-gathered from live game data every pass and a cache outliving them would answer
    /// about a site that has changed. Indexed by slot count, which is one or two.
    ///
    /// A reference inside a readonly struct: the field cannot be reassigned and the array it names
    /// can be filled, which is what a lazily built cache needs. See OutcomesOf.
    /// </param>
    private readonly record struct RolledShape(int Sockets, double Share,
        List<KeyValuePair<Expedition2Recipe, double>> Recipes,
        List<(int[] Positions, int Stands)>[] Kept);

    /// <summary>
    /// Every shape a roll of this site could produce, read BEFORE the advice goes to a background
    /// thread.
    ///
    /// **Valuation reads game memory and two TimeCaches, and neither is safe to touch from the
    /// advice task.** The enumeration needs the recipes for hundreds of shapes, so they are all
    /// gathered here, on the thread that already owns those reads, and the task walks an immutable
    /// list. The alternative - asking Valuation from inside the task - is a race the current code
    /// does not have and would be a poor thing to introduce for a readout.
    ///
    /// It is a per-site answer rather than a per-remnant one: every remnant on the site rolls from
    /// the same table at the same area level, so this is gathered once and walked once per remnant.
    /// </summary>
    private static List<RolledShape> ShapesARollCouldProduce(Valuation valuation)
    {
        var shapes = new List<RolledShape>();

        if (valuation == null)
            return shapes;

        foreach (var (sockets, socketShare) in Rolls.Sockets)
        {
            if (socketShare <= 0f)
                continue;

            // **Every pin the game admits at this socket count, and how many there are.**
            //
            // The count is needed as well as the list: the share of a pin is its observed count
            // against the readings at that socket count, smoothed by one imagined observation per
            // admitted pin - so the divisor is a property of this level's admitted set rather than
            // of the census. See Rolls.ShareOfPin.
            var pins = Safe.Read(() => valuation.FixedRunesPossibleAt(sockets), null)
                       ?? new List<(Expedition2Rune Rune, int Slot)>();

            foreach (var (rune, slot) in pins)
            {
                var id = Safe.Read(() => rune.Id, null);

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                // **Per socket count, not one share for the rune everywhere.**
                //
                // This took the rune's global share out of Rolls.Runes, which is its share of every
                // fresh remnant at any size. Power is 1.75% of all of them and seven of the ten pins
                // a nine socket remnant admits, so a marginal number misprices the large shapes by
                // an order of magnitude - and the large shapes are the ones holding the nine rune
                // recipes. It also counted a rune once per admitted slot, so a rune pinnable in two
                // slots took twice its share.
                var runeShare = Rolls.ShareOfPin(sockets, id, slot, pins.Count);

                if (runeShare <= 0f)
                    continue;

                var recipes = Safe.Read(() => valuation.RecipesForShape(sockets, rune, slot), null);

                if (recipes is not { Count: > 0 })
                    continue;

                // Three, so slot counts of one and two index straight in.
                shapes.Add(new RolledShape(sockets, socketShare * runeShare, recipes,
                    new List<(int[] Positions, int Stands)>[3]));
            }
        }

        return shapes;
    }

    /// <summary>
    /// How wide the reroll enumeration is, and how much of it is the same question twice.
    ///
    /// **Two shapes reaching the same recipes from the same socket count are one evaluation.** A
    /// rune fixed at one slot and a different rune fixed at another can admit exactly the same
    /// recipes, and a shape is worth whatever its best recipe is worth - so the pair score
    /// identically and only their shares differ. Collapsing them changes no arithmetic; it stops
    /// the same score being computed twice.
    ///
    /// The socket count stays in the key because it is not implied by the recipes: it decides how
    /// many ordinary slots are left over, and those carry local runes.
    ///
    /// For the dump. Nothing routes or scores by it.
    /// </summary>
    internal static string EnumerationWidth(Valuation valuation, AutoExpeditionSettings settings)
    {
        var shapes = ShapesARollCouldProduce(valuation);

        if (shapes.Count == 0)
            return "no shapes - the recipe table or the weights table is not readable yet";

        var distinct = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var shape in shapes)
        {
            var ids = new List<string>(shape.Recipes.Count);

            foreach (var (recipe, _) in shape.Recipes)
                ids.Add(Safe.Read(() => recipe.Id, null) ?? "?");

            ids.Sort(StringComparer.Ordinal);

            var key = shape.Sockets + "|" + string.Join(",", ids);

            distinct[key] = distinct.TryGetValue(key, out var had) ? had + 1 : 1;
        }

        // A cell is one score: a shape crossed with one set of propagating positions. Positions are
        // drawn rather than chosen, so they cannot be folded into the recipe choice - see where the
        // enumeration walks them.
        var raw = 0;
        var collapsed = 0;

        foreach (var shape in shapes)
            raw += PositionSets(shape.Sockets);

        foreach (var key in distinct.Keys)
            collapsed += PositionSets(int.Parse(key.Split('|')[0], CultureInfo.InvariantCulture));

        // **How many of those cells ask the same question.**
        //
        // A cell is a shape crossed with one set of propagating positions, and a position past the
        // end of a recipe exposes nothing - so on a shape whose recipes are all shorter than its
        // socket count, many position sets produce the identical set of propagating runes. Scoring
        // them separately is the same arithmetic several times over; collapsing is exact, with each
        // survivor carrying the combined weight.
        //
        // The saving only exists where recipes are shorter than the remnant. A shape holding a
        // recipe as long as its socket count collapses to nothing, because then every position
        // exposes a different rune. Measured rather than assumed - the last dedup tried here saved
        // 0%.
        //
        // Keyed on the ordered runes per recipe, which is conservative: two orderings of one set
        // count as different, so this understates the collapse rather than overstating it.
        var cells = 0;
        var outcomes = 0;

        foreach (var shape in shapes)
        {
            for (var slots = 1; slots <= 2; slots++)
            {
                var sets = PositionSetsOf(shape.Sockets, slots);

                if (sets.Count == 0)
                    continue;

                cells += sets.Count;

                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var positions in sets)
                {
                    var key = new StringBuilder();

                    foreach (var (recipe, _) in shape.Recipes)
                    {
                        var needs = Safe.Read(() => recipe.RuneCountRequired, 0);

                        key.Append('|');

                        foreach (var slot in positions)
                        {
                            if (slot >= needs)
                                continue;

                            var at = slot;

                            key.Append(Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null) ?? "-")
                               .Append(',');
                        }
                    }

                    seen.Add(key.ToString());
                }

                outcomes += seen.Count;
            }
        }

        // **How much of the work is on shapes that can only ever expose a weak rune?**
        //
        // Seven runes carry a real magnitude - bond and opulent at 40%, time, power, death and
        // rebirth at 32%, oath at 27% - and the other twenty-six sit within two hundredths of 4%.
        // A shape whose recipes can put a strong one in a propagating slot is worth scoring
        // properly; one that can only ever expose a 4% rune has a nearly constant propagation
        // value, and those could share a score weighted by their combined probability rather than
        // taking one each.
        //
        // The worry is that it inverts: the expensive shapes are the seven to nine socket ones, and
        // long recipes are where the strong runes live - Mirror of Kalandra and Hinekora's Lock are
        // made of Power, Opulent, Time, Death and Bond. If nearly every big shape can reach a
        // strong rune the collapse only applies to small shapes that are already cheap.
        //
        // Ten per cent separates the bands cleanly and nothing sits near it.
        var strongCells = 0;
        var weakCells = 0;
        var strongShapes = 0;

        foreach (var shape in shapes)
        {
            var strong = false;

            foreach (var (recipe, _) in shape.Recipes)
            {
                var needs = Safe.Read(() => recipe.RuneCountRequired, 0);

                for (var slot = 0; slot < needs && !strong; slot++)
                {
                    var at = slot;
                    var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                    if (!string.IsNullOrWhiteSpace(id) && Runes.Weight(id) > 10f)
                        strong = true;
                }

                if (strong)
                    break;
            }

            var mine = PositionSetsOf(shape.Sockets, 1).Count + PositionSetsOf(shape.Sockets, 2).Count;

            if (strong)
            {
                strongShapes++;
                strongCells += mine;
            }
            else
            {
                weakCells += mine;
            }
        }

        // What is actually walked, after collapsing identical outcomes and sampling the
        // arrangements inside the rare shapes. The three figures before it are why it is shaped
        // that way - see Enumerated. Every shape is walked; only arrangements are thinned.
        var scored = 0;

        foreach (var shape in shapes)
        {
            var big = shape.Sockets > ExactToSockets;

            for (var slots = 1; slots <= 2; slots++)
            {
                var sets = OutcomesOf(shape, slots);

                scored += big && sets.Count > ArrangementsPerRound ? ArrangementsPerRound : sets.Count;
            }
        }

        return $"{shapes.Count} shapes over {cells} position sets. " +
               $"{strongShapes} can expose a rune above 10% ({strongCells} cells); the other " +
               $"{shapes.Count - strongShapes} only reach weak ones ({weakCells}, " +
               $"{(strongCells + weakCells > 0 ? weakCells * 100 / (strongCells + weakCells) : 0)}%). " +
               $"Shapes reaching the same recipes: {cells - collapsed} saved. " +
               $"Identical propagating outcomes: {cells - outcomes} saved " +
               $"({(cells > 0 ? 100 - outcomes * 100 / cells : 0)}%). " +
               $"ACTUALLY SCORED {scored} " +
               $"({(cells > 0 ? scored * 100 / cells : 0)}% of the full enumeration), " +
               $"walking every arrangement to {ExactToSockets} sockets, and above it " +
               $"{ArrangementsPerRound} arrangements of each per pass. " +
               // **The bound, not the average.** This divided the cells by the cells per pass, which
               // is how many passes it would take if every shape were the same size - it said "about
               // 2" on a site the walk finished in 5, because one shape's arrangements set the bound
               // and the mean hides it. RoundsToExhaust is the figure the mode line reports, so both
               // now come from one place and cannot disagree.
               $"{RoundsToExhaust(shapes, settings)} passes covers the lot - the largest shape " +
               "decides that, not the average one - and each pass adds to what the last one found " +
               "rather than replacing it";
    }

    /// <summary>
    /// How many ways the propagating slots can fall on a remnant of this many sockets.
    ///
    /// One slot always propagates and a second sometimes does - see Debug.DoubleOrNothingDouble -
    /// and which are which is drawn. So it is every single slot plus every unordered pair.
    /// </summary>
    private static int PositionSets(int sockets) =>
        sockets <= 0 ? 0 : sockets + sockets * (sockets - 1) / 2;

    private void Weigh(AutoExpeditionSettings settings, List<Target> scan, PlanEnvironment env,
        List<Vector2> chain, int settled, Insisted.Said[] marked,
        Dictionary<string, (float Weight, string Scope, float Local)> known, int asking,
        List<RolledShape> shapes)
    {
        var began = DateTime.UtcNow;

        // **What it may spend, rather than what it happens to cost.**
        //
        // The deep pass re-solves the chain once per sampled outcome for every remnant on its
        // shortlist, so its cost follows the size of the site - biggest exactly where waiting is
        // least welcome. The shortlist is already best-first, so a deadline costs the least
        // promising candidates and nothing else, and the line below says how many it reached.
        var until = began.AddMilliseconds(
            Math.Max(100, Safe.Read(() => settings.Solver.Reroll.RollSolveMs.Value, 1000)));
        var cut = 0;

        var said = new Dictionary<(int X, int Y), Verdict>();

        Best = null;
        Quiet = "";
        _runs++;

        // **The accumulation only survives while the thing it is about does.** See Gathering.
        var gathering = Gathering(env, chain);

        if (gathering != _gatheredFor)
        {
            _gathered = new Dictionary<(int X, int Y), (double Total, double Weight, int Round, bool More)>();
            _standing = new Dictionary<(int X, int Y), double>();
            _gatheredFor = gathering;
            RoundsWalked = 0;
            Stalled = false;
        }

        var rounds = 0;
        var unfinished = 0;
        var settledCells = 0;
        var fewest = int.MaxValue;
        var walkedBefore = RoundsWalked;

        var baseline = Planner.Score(env, chain);

        // **The same rolls offered to every remnant, which is the whole of the fix.**
        //
        // One shared stream meant remnant A was judged against draws one to sixteen and remnant B
        // against seventeen to thirty two - different worlds, compared as though they were the same
        // one. A rune the rest of the chain does not carry is worth a percentage of everything
        // downstream and a duplicate is worth nothing, so one lucky draw moves a remnant's average
        // by more than the decision is worth: the dump showed one remnant at +274 from screening
        // and the whole pass concluding that nothing was worth rolling, because the deep pass had
        // drawn a different set.
        //
        // Seeding per remnant from one number makes sample k the same imagined remnant for all of
        // them, so what is left in the comparison is the thing being compared. It is also why the
        // screening and the deep pass share the seed: the deep pass then re-solves the very rolls
        // the screen scored, and the two can no longer contradict each other.
        //
        // Seeded on the site and the run so the same site advises the same thing twice rather than
        // wobbling each time the plan is redrawn.
        var stream = ((int)site(env) * 31) ^ _runs;

        var worth = new List<(int Index, double Gain, bool Reached)>();

        _exact = new Dictionary<int, double>();
        _extra = new Dictionary<int, double>();
        _error = new Dictionary<int, double>();
        _landed = new Dictionary<int, int>();

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var target = env.Targets[i];

            if (target.Kind != TargetKind.Remnant)
                continue;

            var why = Refuses(scan, env, chain, target, i, marked[i], settings);

            if (why != null)
            {
                Say(said, target.Grid, 0d, why, refused: true);

                continue;
            }

            var reached = Caught(env, chain, i);

            // **What earlier passes at this same chain already enumerated.**
            //
            // Each pass walks the next block of arrangements and adds to these, so the figure
            // improves every pass and settles on the exhaustive one. Thrown away when the chain or
            // the content moves, because a score of one chain says nothing about another - see
            // Gathering, and the reset above this loop.
            var cell = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));
            var had = _gathered.GetValueOrDefault(cell);

            // **Nothing left to walk means the figure is exact, so no pass touches it again.**
            //
            // Every arrangement has been scored and the mean cannot move, so another round would
            // cost what a round costs and change nothing. The floor below still applies: it is a
            // check on the answer rather than a step in producing it, and an exact figure that
            // cannot happen is exactly what it is watching for.
            var everyArrangementWalked = had.Round > 0 && !had.More;
            double gain;

            if (everyArrangementWalked)
            {
                gain = had.Weight > 0d
                    ? had.Total / had.Weight - _standing.GetValueOrDefault(cell)
                    : 0d;

                settledCells++;
                rounds += had.Round;
                fewest = Math.Min(fewest, had.Round);
            }
            else
            {
                var at = i;
                var round = had.Round;
                var walked = ResultOfStage($"screening the remnant at ({target.Grid.X:0},{target.Grid.Y:0})",
                    () => Enumerated(env, chain, at, settings, known, shapes, round));

                _gathered[cell] = (had.Total + walked.Total, had.Weight + walked.Weight,
                    had.Round + 1, walked.More);
                _standing[cell] = walked.Standing;

                rounds += had.Round + 1;
                unfinished += walked.More ? 1 : 0;
                fewest = Math.Min(fewest, had.Round + 1);

                var gathered = _gathered[cell];

                gain = gathered.Weight > 0d
                    ? gathered.Total / gathered.Weight - walked.Standing
                    : 0d;
            }

            // **A remnant the chain misses cannot be made worse by rolling it, and the arithmetic
            // has to agree.** It contributes nothing as things stand - no reward collected, no rune
            // propagated - and the re-solve may always keep the chain it was given, so the gain is
            // nought at worst. A negative here is not advice, it is a fault: the two halves
            // disagreeing about whether the chain reaches it.
            //
            // Floored, and the raw figure kept so the dump says a floor was applied rather than
            // quietly reading nought.
            if (!reached && gain < 0d)
            {
                // Half a point, because floating point arithmetic over thirty two re-scored chains
                // lands a hair under nought routinely and that is not the fault this is watching
                // for. Anything larger is the two halves disagreeing about reach.
                if (gain < -0.5d)
                {
                    _floored = $"({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0}) screened " +
                               $"{gain:N0} while unreached, which cannot happen - floored to 0";
                }

                gain = 0d;
            }

            _exact[i] = gain;
            worth.Add((i, gain, reached));
        }

        if (worth.Count == 0)
        {
            if (asking != _asked)
                return;

            _said = said;
            Best = null;
            Quiet = "no remnant on this chain can be rolled";

            // Nothing was walked and nothing will be, so the deepening must not keep asking. This
            // path leaves before the figures below are published, which would otherwise hold
            // RoundsWalked at whatever the last chain left it and dispatch a pass every tick.
            Stalled = true;

            _telling = $"run {_runs}: {(DateTime.UtcNow - began).TotalMilliseconds:N0}ms, nothing to weigh";
            _finished = DateTime.UtcNow;

            return;
        }

        worth.Sort((a, b) => b.Gain.CompareTo(a.Gain));

        var screened = (DateTime.UtcNow - began).TotalMilliseconds;

        // The expensive pass, on the few that screening liked. It re-solves rather than re-scoring,
        // which is what lets a roll be credited for a route that only becomes worth taking once the
        // socket count has moved.
        var best = -1;
        var top = 0d;

        // **Worked out once, because a roll cannot move them.** Candidates are built from where
        // the content stands and how big it is; a roll changes a remnant's runes and sockets and
        // none of its geometry. Rebuilding them per sample was the bulk of the cost of the deep
        // pass and every rebuild returned the same list.
        var spots = Planner.Candidates(env, out _, out _);

        // What a re-solve is worth on this chain with NOTHING rolled - the floor every premium is
        // measured against. See Premium.
        // **Measured the same way the premiums are**, or the subtraction is between two different
        // questions. It was Improve against Improve; it is Restitched against Restitched now, with
        // no marker named - which is Order alone, the re-ordering available without rolling anything.
        var links = Math.Clamp(Safe.Read(() => settings.Solver.Reroll.RollSubstitutionLinks.Value, 1), 1, 3);

        var house = Math.Max(0d,
            Planner.Score(env, Planner.Restitched(env, chain, -1, spots, links)) - baseline);

        // What the deep pass said, per remnant, so the line printed is the line that decided.
        var deep = new Dictionary<int, double>();

        var shortlist = Chosen(env, chain, worth);
        var done = 0;

        foreach (var i in shortlist)
        {
            // Never before the first: an answer built on nothing is worse than a late one, and a
            // single candidate is the case the loop exists to serve.
            if (done > 0 && DateTime.UtcNow >= until)
            {
                cut = shortlist.Count - done;

                break;
            }

            Through = (float)done++ / shortlist.Count;

            // The exact fixed-chain figure screening already found, plus what re-solving adds.
            // Only the second part is sampled, and it is nought on most draws. See Premium.
            var reached = Caught(env, chain, i);
            var at = i;
            var (extra, error, landed) = ResultOfStage(
                $"re-solving the remnant at ({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0})",
                () => Premium(env, chain, at, settings, known, stream, Deep, spots, house, links));
            var gain = _exact.GetValueOrDefault(i) + extra;

            _extra[i] = extra;
            _error[i] = error;
            _landed[i] = landed;
            deep[i] = gain;

            if (best < 0 || gain > top)
            {
                best = i;
                top = gain;
            }
        }

        // Everything screening looked at gets a line, so the overlay can explain a remnant it is
        // advising against as readily as one it is advising for.
        foreach (var (i, gain, _) in worth)
        {
            // **The number that decided, not the one that nominated.** Screening ranks; the deep
            // pass rules. Printing the screening figure for a remnant the deep pass then rejected
            // put a positive gain beside a verdict of keep, with nothing to say which was which.
            var shown = deep.TryGetValue(i, out var ruled) ? ruled : gain;

            var at = i;

            Say(said, env.Targets[i].Grid, shown,
                ResultOfStage($"explaining the remnant at ({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0})",
                    () => Because(env, chain, at, shown, deep.ContainsKey(at), settings)),
                _exact.GetValueOrDefault(i), _extra.GetValueOrDefault(i),
                _error.GetValueOrDefault(i),
                _landed.GetValueOrDefault(i), deep.ContainsKey(i) ? Deep : 0);
        }

        // Dropped if the question has moved on - a zone change, or another solve - because
        // publishing now would put advice about the last dig site over the top of this one.
        if (asking != _asked)
            return;

        Through = 0f;

        // Published together so the readout cannot show a count from one pass against a bound from
        // another. See RoundsWalked.
        RoundsNeeded = RoundsToExhaust(shapes, settings);
        RoundsWalked = fewest == int.MaxValue ? 0 : fewest;
        Stalled = RoundsWalked <= walkedBefore;

        // Published as one finished object, so the frame never sees a half-built answer.
        _said = said;

        // Measured beside the answer rather than instead of it. See RankingWithoutScoring.
        _withoutScoring = RankedWithoutScoring(env, worth);

        // **More than nothing is not a reason to spend a Liquid Verisium.**
        //
        // The gain is a difference between two sampled sums of thousands, so its last fraction is
        // arithmetic noise rather than signal. Anything above nought counted, which meant a site
        // where every roll was worthless still produced a recommendation - the one that happened to
        // sort first, printed as "ROLL ... Worth about 0 to the chain", because the readout rounds
        // to whole points and there was nothing there to round.
        //
        // A point is the smallest amount the readout can show. Below that the advice is claiming a
        // difference the player cannot see, about a consumable they cannot get back.
        var won = best >= 0 && top >= Worthwhile;

        // **The continuous mode picks its own, on the frame, and would only be overwritten here.**
        // Its choice moves between passes and this one cannot; setting it would make the advice
        // jump to the pass's pick for a frame and then back. See Divert, which runs every frame.
        if (Mode(settings) != RerollSettings.Continuous)
        {
            Best = won ? Of(env.Targets[best].Grid) : null;
            Quiet = won ? "" : "every roll on this chain is worth less than what it would replace";
        }

        _finished = DateTime.UtcNow;
        _telling = $"run {_runs}: {(DateTime.UtcNow - began).TotalMilliseconds:N0}ms over {worth.Count} remnants " +
                  $"({screened:N0}ms screening, {deep.Count} re-solved" +
                  (house > 0.5d
                      ? $", {house:N0} of re-solve gain was available without rolling and has been " +
                        "taken off every premium"
                      : "") + ")" +
                  (cut > 0
                      ? $"; STOPPED at {Safe.Read(() => settings.Solver.Reroll.RollSolveMs.Value, 1000)}ms " +
                        $"with {cut} less promising remnants unexamined"
                      : "") +
                  (settled > 0
                      ? $"; {settled} more already under a placed explosive, which is past advising on"
                      : "") +
                  // **How far the enumeration has got, because it is progressive now.** Each pass
                  // walks the next block of arrangements and adds to the last, so a figure is exact
                  // once nothing is left to walk and approximate until then. Without this the two
                  // are indistinguishable on screen. See Enumerated.
                  $"; {rounds} rounds gathered, {unfinished} remnants still have arrangements left" +
                  (settledCells > 0 ? $", {settledCells} already exact and re-used unchanged" : "");
    }


    /// <summary>
    /// The enumerated ranking beside one that costs nothing, so a dump says whether the enumeration
    /// is buying anything.
    ///
    /// **A reading, not a decision: nothing consults this.** Screening is 2,387ms of a 2,389ms pass
    /// and scales at about 500ms per remnant, which is the whole of the reroll latency and gets
    /// worse on a site with more remnants. The question this answers is whether the answer could
    /// have been had for free.
    ///
    /// The free ranking is the one thing a roll certainly costs: the weight of the runes this
    /// remnant is the chain's only source of, which vanish when it is replaced. Least destroyed
    /// first. It uses the same BankedRunesReaching test the explanation uses, so the two cannot
    /// drift apart.
    ///
    /// **What it deliberately ignores** is everything that makes the enumeration expensive: where
    /// the remnant sits in the chain, so a rune that reaches one link counts the same as one that
    /// reaches five; its socket count; and its ordinary slots. If the orders agree anyway then none
    /// of that was deciding anything. If they do not, the disagreement says which of those to add.
    ///
    /// Two guesses at savings in this file have come back at 0% and 5%, so this is measured rather
    /// than reasoned about.
    /// </summary>
    public string RankingWithoutScoring => _withoutScoring;

    private string _withoutScoring = "";

    /// <summary>What a roll here certainly destroys: the weight of the runes nothing else carries.</summary>
    private static float SolelySourcedWeight(PlanEnvironment env, int index)
    {
        var destroyed = 0f;

        foreach (var (id, weight) in env.Targets[index].Runes ?? [])
        {
            if (weight <= 0f || id == null)
                continue;

            if (!BankedRunesReaching(env, index, id))
                destroyed += weight;
        }

        return destroyed;
    }

    /// <summary>See RankingWithoutScoring.</summary>
    private static string RankedWithoutScoring(PlanEnvironment env,
        List<(int Index, double Gain, bool Reached)> worth)
    {
        if (worth is not { Count: > 0 })
            return "nothing screened, so nothing to compare";

        var rows = new List<(int Index, double Gain, float Destroyed)>(worth.Count);

        foreach (var (i, gain, _) in worth)
            rows.Add((i, gain, SolelySourcedWeight(env, i)));

        var byGain = new List<(int Index, double Gain, float Destroyed)>(rows);
        var byCost = new List<(int Index, double Gain, float Destroyed)>(rows);

        byGain.Sort((a, b) => b.Gain.CompareTo(a.Gain));
        byCost.Sort((a, b) => a.Destroyed.CompareTo(b.Destroyed));

        // Where the enumeration's winner comes in the free order. First means the free order would
        // have advised the same remnant, which is the only thing a mode built on it has to get right.
        var placed = byCost.FindIndex(x => x.Index == byGain[0].Index) + 1;

        // How far apart the two orders are: adjacent pairs the free order puts the wrong way round
        // against the enumerated one. Zero is the same order throughout.
        var wrong = 0;

        for (var a = 0; a < rows.Count; a++)
        for (var b = a + 1; b < rows.Count; b++)
        {
            var one = rows[a];
            var two = rows[b];

            if (Math.Abs(one.Gain - two.Gain) < 0.5d)
                continue;

            if (one.Gain > two.Gain != one.Destroyed < two.Destroyed)
                wrong++;
        }

        var pairs = rows.Count * (rows.Count - 1) / 2;
        var text = new List<string>(rows.Count + 1)
        {
            $"the enumerated pick is {placed} of {byCost.Count} in the free order; " +
            $"{wrong} of {pairs} pairs ordered the other way",
        };

        foreach (var (i, gain, destroyed) in byCost)
        {
            text.Add($"        ({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0}) " +
                     $"destroys {destroyed:N1} weight of runes nothing else carries, " +
                     $"enumerated {gain:+#,##0.0;-#,##0.0;0}" +
                     (i == byGain[0].Index ? "   <- the enumerated pick" : ""));
        }

        return string.Join("\n", text);
    }


    /// <summary>
    /// Pick the remnant to advise from what a roll certainly destroys, every frame.
    ///
    /// **The continuous mode cannot rank on the enumerated gain, because the gain lags.** Screening
    /// costs about 500ms a remnant and runs once per solve, so between solves the newest figures are
    /// seconds old and a remnant rolled in the meantime has none at all. What a roll destroys - the
    /// weight of the runes nothing else on the chain carries - is arithmetic over target.Runes and
    /// can be answered on any frame.
    ///
    /// Measured on one site of six remnants, that ordering put the enumeration's own pick first, and
    /// the two disagreed on 2 of 15 pairs - both inside a group where three remnants destroy the same
    /// weight and it has nothing left to separate them with. So it is trusted to choose the
    /// candidate and not to order a tie.
    ///
    /// **Only remnants that already have a verdict are eligible.** A remnant the pass has not
    /// reached has no figure to show, and a blank beside an advised remnant reads as a nought rather
    /// than as an absence - a readout in this file has made exactly that mistake before. So the
    /// choice is over what has been scored, ordered by what has not.
    ///
    /// **The comparison is between two destroyed weights, never between a weight and a gain.** Those
    /// are different quantities and a margin over the pair of them would mean nothing.
    /// </summary>
    private void Divert(GameController gc, PlanEnvironment env, AutoExpeditionSettings settings,
        Scan scan, Vector2 site)
    {
        var said = _said;

        if (env?.Targets == null || said.Count == 0)
            return;

        // **A remnant already rolled is off the list however good its verdict still looks.**
        //
        // A remnant takes one Liquid Verisium and no more, and the verdicts here outlive the roll -
        // that is the point of this mode - so the one just rolled still carries the figure that
        // advised rolling it. Refuses makes the same check when a pass runs; this is the same check
        // between passes, against the same reading.
        var live = Safe.Read(() => scan?.At(site), null);

        var margin = Math.Clamp(Safe.Read(() => settings.Solver.Reroll.DiversionMargin.Value, 0), 0, 100) / 100f;
        var committed = Math.Max(0, Safe.Read(() => settings.Solver.Reroll.CommittedWithin.Value, 0));
        var here = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        var bestAt = Vector2.Zero;
        var bestCost = float.MaxValue;
        var standing = float.MaxValue;

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var target = env.Targets[i];

            if (target.Kind != TargetKind.Remnant)
                continue;

            var cell = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));

            if (!said.TryGetValue(cell, out var verdict) || verdict.Refused)
                continue;

            if (Nearest(live, target.Grid) is { Rerolled: true })
                continue;

            // **The prior chooses between rolls worth making; it does not decide that one is.**
            //
            // Ranking by what a roll destroys says which remnant is the cheapest to replace. It says
            // nothing about whether replacing it gains anything, and a remnant that destroys nothing
            // at all sorts first whatever its gain is - so this advised a roll the enumeration scored
            // at -32, on a site where the same pass had better candidates and every other mode would
            // have said nothing at all. Measured at (1449,1517): "0.0 destroyed" and "screening put a
            // roll here at about -32, behind the ones that were".
            //
            // So the two quantities do what each is good for: the enumerated gain decides whether a
            // roll is worth a Verisium, and the prior picks between the ones that are. Same floor the
            // other modes use, so Continuous cannot recommend what they would refuse.
            if (verdict.Gain < Worthwhile)
                continue;

            var cost = SolelySourcedWeight(env, i);

            if (Best is { } was && Vector2.Distance(was.Grid, target.Grid) < 1f)
                standing = cost;

            if (cost >= bestCost)
                continue;

            bestCost = cost;
            bestAt = target.Grid;
        }

        if (bestAt == Vector2.Zero)
        {
            Best = null;
            Diverted = said.Count == 0
                ? "nothing on this chain has been scored yet"
                : $"no roll on this chain clears {Worthwhile:0.#}, so none is advised";

            return;
        }

        // **Committed, so the advice holds.** Off by default - see CommittedWithin - because a rule
        // that refuses to move is harder to spot than one that moves too readily.
        if (Best is { } held && standing < float.MaxValue && committed > 0 && here != Vector2.Zero &&
            Vector2.Distance(here, held.Grid) <= committed)
        {
            Diverted = $"holding ({held.Grid.X:0},{held.Grid.Y:0}) - within {committed} grid of it";

            return;
        }

        // A challenger has to beat what is advised by the margin. At nought any improvement moves it.
        if (Best != null && standing < float.MaxValue && bestCost >= standing * (1f - margin))
        {
            Diverted = $"holding ({Best.Grid.X:0},{Best.Grid.Y:0}) at {standing:N1} destroyed - " +
                       $"best challenger {bestCost:N1} does not beat it by {margin:0%}";

            return;
        }

        var moved = Best == null || Vector2.Distance(Best.Grid, bestAt) >= 1f;

        Best = Of(bestAt);
        Quiet = Best == null ? "the best candidate has no verdict yet" : "";
        Diverted = $"{(moved ? "moved to" : "still")} ({bestAt.X:0},{bestAt.Y:0}) at " +
                   $"{bestCost:N1} destroyed" +
                   (standing < float.MaxValue && moved ? $", from {standing:N1}" : "");
    }

    /// <summary>
    /// What the accumulated totals are only valid for: these links, and these remnants holding this.
    ///
    /// The links because every accumulated figure is a score of a chain. The runes because a roll
    /// changes what a remnant holds, which changes what every other remnant's roll is worth -
    /// runes do not stack, so one remnant's contents decide whether another's are duplicates.
    ///
    /// Cheap enough to build once a pass: a few dozen links and a rune list per remnant.
    /// </summary>
    private static string Gathering(PlanEnvironment env, List<Vector2> chain)
    {
        var said = new StringBuilder(256);

        foreach (var at in chain ?? new List<Vector2>())
            said.Append((int)MathF.Round(at.X)).Append(',').Append((int)MathF.Round(at.Y)).Append(';');

        said.Append('|');

        foreach (var target in env?.Targets ?? new List<PlanTarget>())
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            said.Append((int)MathF.Round(target.Grid.X)).Append(',')
                .Append((int)MathF.Round(target.Grid.Y)).Append(':')
                .Append(target.Weight.ToString("0.##")).Append('=');

            foreach (var (id, weight) in target.Runes ?? [])
                said.Append(id).Append('/').Append(weight.ToString("0.###")).Append('+');

            // **What the remnant OFFERS, and whether it is insisted, because the standing side depends
            // on both and neither was here.**
            //
            // The accumulation holds two halves: a mean over rolled arrangements, which does not
            // depend on the remnant's current offers because the rolled side prices every recipe at
            // nought, and a STANDING - the chain as it is - which depends on them entirely. Fingerprint
            // one half and the cache survives a change that moved the other.
            //
            // Turning a must take on or off does exactly that: Planning.Pinned collapses Choices to the
            // single insisted reward, which changes the standing and nothing else here. So a cached
            // standing from the pinned chain was subtracted from a mean belonging to the unpinned one,
            // and a remnant read as worth rolling by tens of thousands. Reported as a bogus +32,220 on
            // a first propagator, and it went away on a plan reset - which is this cache being dropped.
            said.Append(target.Must ? "M" : "m");

            foreach (var choice in target.Choices ?? [])
            {
                said.Append(choice.Reward.ToString("0.#")).Append('/')
                    .Append(choice.Carries.ToString("0.##")).Append('/')
                    .Append(choice.Local.ToString("0.##")).Append(',');
            }

            said.Append(';');
        }

        return said.ToString();
    }

    /// <summary>
    /// How many passes it takes before every arrangement has been walked. See RoundsNeeded.
    ///
    /// Combinatorics only - it builds the position sets and counts them, and scores nothing - so it
    /// costs a small fraction of one remnant's screening and is worked out once a pass rather than
    /// once a remnant.
    /// </summary>
    private static int RoundsToExhaust(List<RolledShape> shapes, AutoExpeditionSettings settings)
    {
        var most = 1;

        // **The same shapes and the same slot counts the walk takes, or the bound is unreachable.**
        // A bound above what any remnant can reach would leave RoundsWalked short of it for ever,
        // and the dispatch that deepens while they differ would never stop. Two skips matter: a
        // shape with no share is never walked, and with "Double or Nothing" off the two-slot case
        // has no share either - and its arrangements are the pairs, so it is the larger count and
        // would have set the bound on its own.
        var twoSlots = Rolls.TwoSlots(settings);

        foreach (var shape in shapes ?? new List<RolledShape>())
        {
            if (shape.Sockets <= ExactToSockets || shape.Share <= 0d ||
                shape.Recipes is not { Count: > 0 })
                continue;

            for (var slots = 1; slots <= 2; slots++)
            {
                if (slots > shape.Sockets)
                    continue;

                if ((slots == 2 ? twoSlots : 1f - twoSlots) <= 0f)
                    continue;

                var sets = OutcomesOf(shape, slots).Count;

                if (sets <= 0)
                    continue;

                most = Math.Max(most, (sets + ArrangementsPerRound - 1) / ArrangementsPerRound);
            }
        }

        return most;
    }

    private static float site(PlanEnvironment env) => env.Origin.X + env.Origin.Y;

    /// <summary>
    /// What repositioning a remnant costs, and whether the position depends on the socket count.
    ///
    /// **Two readings, both needed before the pricing can value a roll at its best position.**
    /// reroll_plan.md prices a roll as `worth(rolled) - worth(current)` with `worth` taken at the
    /// remnant's best position rather than where it sits. The enumeration is already 1,856 scores
    /// per remnant, so whether that is affordable turns on what a reposition costs against a score;
    /// and caching the position per rune set - the only thing that makes it affordable - is sound
    /// only if the position does not also depend on the socket count.
    ///
    /// Measured here rather than reasoned about, because the last design that was reasoned about
    /// cost ten to twenty seconds a remnant.
    ///
    /// A reading, not a decision: nothing consults this.
    /// </summary>
    internal static string Positioning(PlanEnvironment env, List<Vector2> chain,
        AutoExpeditionSettings settings)
    {
        if (env == null || chain is not { Count: > 0 })
            return "no solved plan to measure against";

        var index = -1;

        for (var i = 0; i < env.Targets.Count; i++)
            if (env.Targets[i].Kind == TargetKind.Remnant)
            {
                index = i;

                break;
            }

        if (index < 0)
            return "no remnant on this site";

        var spots = Planner.Candidates(env, out _, out _);
        var links = Math.Max(1, Safe.Read(() => settings.Solver.Reroll.RollSubstitutionLinks.Value, 1));
        var known = new Dictionary<string, (float Weight, string Scope, float Local)>();
        var random = new Random(1);
        var was = env.Targets[index];

        // Warm, so the first call's jitting is not the measurement.
        Planner.Score(env, chain);
        Planner.Restitched(env, chain, index, spots, links);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < Trials; i++)
            Planner.Score(env, chain);

        var scoring = clock.Elapsed.TotalMilliseconds * 1000d / Trials;

        clock.Restart();

        for (var i = 0; i < Trials; i++)
            Planner.Restitched(env, chain, index, spots, links);

        var placing = clock.Elapsed.TotalMilliseconds * 1000d / Trials;

        // **Does the best order depend on how many sockets the remnant has?** Same runes, four
        // socket counts, and the ordered chain compared. A difference means the position cannot be
        // cached per rune set, and the cheap version of the plan's pricing is not available.
        var runes = Rolls.Runes;
        var differed = 0;
        var tested = 0;
        var targets = new List<PlanTarget>(env.Targets);

        for (var r = 0; r < runes.Length && tested < Sampled; r++)
        {
            List<Vector2> first = null;

            foreach (var (sockets, _) in Rolls.Sockets)
            {
                targets[index] = Built(was, settings, known, sockets, new[] { runes[r].Rune });

                var order = Planner.Restitched(env with { Targets = targets }, chain, index, spots,
                    links);

                if (first == null)
                {
                    first = order;

                    continue;
                }

                if (!Same(first, order))
                {
                    differed++;

                    break;
                }
            }

            tested++;
        }

        var many = 1856;
        var cost = many * scoring / 1000d;

        return $"one score {scoring:0.#}us, one reposition {placing:0.#}us " +
               $"({(scoring > 0d ? placing / scoring : 0d):0.0}x a score) over {Trials} calls" +
               $"\n    the enumeration is {many} scores a remnant = {cost:0.##}ms; " +
               $"repositioning per rune set adds 464 x {placing:0.#}us = {464 * placing / 1000d:0.##}ms" +
               $"\n    socket count changed the best order on {differed} of {tested} runes " +
               (differed == 0
                   ? "- the position can be cached per rune set"
                   : "- the position CANNOT be cached per rune set");
    }

    /// <summary>Whether two chains are the same links in the same order.</summary>
    private static bool Same(List<Vector2> a, List<Vector2> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;

        return true;
    }

    /// <summary>How many calls each timing averages over. See Positioning.</summary>
    private const int Trials = 40;

    /// <summary>How many runes the socket-count comparison walks. See Positioning.</summary>
    private const int Sampled = 12;

    /// <summary>
    /// Which remnants get the expensive pass: the best few, plus one the chain does not reach.
    ///
    /// **Screening cannot rank a remnant the chain misses.** Holding the chain still, rolling one
    /// changes nothing the objective can see, so every unreached remnant screens at exactly nought
    /// and they are indistinguishable from each other and from a roll worth nothing. Ranking alone
    /// would either bury them below anything positive or flood the shortlist with a dozen ties.
    ///
    /// So one is carried in by hand, and the one chosen is the NEAREST to the chain rather than the
    /// richest: what stops the chain taking it is the detour, and a remnant twenty grid off the
    /// route is a question worth asking where one across the site is not. What it might become is
    /// the same draw for all of them - that is what the sampling decides - so distance is the only
    /// thing separating them beforehand.
    ///
    /// **Three, not one, now that a sample is a tenth of what it was.** It was one on the grounds
    /// that each costs a re-solve - true when a remnant meant thirty two of them. At ten, the whole
    /// unreached side of an ordinary site fits inside the budget the reached side used to need, and
    /// a missed remnant is the case where a roll can only help: its gain is nought at worst, so
    /// asking about more of them can only find upside.
    ///
    /// Still bounded, because a Grand site can strand a dozen. The nearest three are the ones whose
    /// detour is plausible; a remnant across the site is not going to be worth re-routing for
    /// whatever it rolls into.
    /// </summary>
    private static List<int> Chosen(PlanEnvironment env, List<Vector2> chain,
        List<(int Index, double Gain, bool Reached)> worth)
    {
        // **Ranked among the remnants the chain actually takes, because the others all tie.**
        //
        // An unreached remnant screens at exactly nought - a fixed chain sees nothing from it - and
        // nought beats every reached remnant whose roll screens negative, which on an ordinary site
        // is all of them. So the first version of this handed both deep passes to unreached
        // remnants and left the two with real signal un-re-solved, which is the exact crowding it
        // was written to prevent, arrived at from the other side.
        //
        // The reached ones compete on their own numbers; one unreached remnant is carried in
        // beside them, always.
        var found = new List<int>(Shortlist + 1);

        foreach (var (index, _, reached) in worth)
        {
            if (found.Count >= Shortlist)
                break;

            if (reached)
                found.Add(index);
        }

        // The missed ones, nearest first, up to the allowance.
        var missed = new List<(int Index, float Gap)>();

        // **Only one that could actually be reached by moving a link.**
        //
        // The missed ones were sorted nearest-first and the nearest carried in whatever the
        // distance, so on a site where nothing unreached is close the advice spent a deep pass -
        // eight full re-solves - on a remnant the chain could never touch, and then reported a
        // re-route worth nought as though that were news.
        //
        // What re-routing can actually do is move ONE link. A link can travel at most a link's
        // reach, and it has to come within a blast radius to catch anything, so a remnant further
        // than the two combined cannot be caught by any single move. That is a generous bound - a
        // link rarely has its whole reach free, since both its neighbours still have to chain - but
        // generous is the right direction for a filter whose job is to exclude the impossible
        // rather than to judge the marginal.
        var arm = env.Reach + env.Blast;

        foreach (var (index, _, reached) in worth)
        {
            if (reached || found.Contains(index))
                continue;

            var gap = Near(env, chain, index);

            if (gap <= arm)
                missed.Add((index, gap));
        }

        missed.Sort((a, b) => a.Gap.CompareTo(b.Gap));

        for (var k = 0; k < missed.Count && k < Detoured; k++)
            found.Add(missed[k].Index);

        return found;
    }

    /// <summary>How far this marker sits from the nearest link, in grid. See Chosen.</summary>
    private static float Near(PlanEnvironment env, List<Vector2> chain, int index)
    {
        var grid = env.Targets[index].Grid;
        var closest = float.MaxValue;

        foreach (var at in chain)
            closest = MathF.Min(closest, Vector2.Distance(at, grid));

        return closest;
    }

    /// <summary>
    /// Runs one step of the pass and says where it was if it throws.
    ///
    /// The pass runs on a task, so a throw reaches no log and arrives as one line of Quiet. When
    /// that line was "Index was outside the bounds of the array" it named neither the array, the
    /// method nor the remnant, and the pass is three stages over every remnant on the site.
    ///
    /// The original is kept as the inner exception, so the dump still prints its stack. See
    /// Rolling.Failure and Dump's THE REROLL PASS THREW.
    /// </summary>
    private static T ResultOfStage<T>(string stage, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{stage}: {ex.Message}", ex);
        }
    }

    private static void Say(Dictionary<(int X, int Y), Verdict> said, Vector2 grid, double gain,
        string why, double exact = 0d, double extra = 0d,
        double error = 0d, int landed = 0, int samples = 0, bool refused = false) =>
        said[((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y))] =
            new Verdict(grid, gain, why, exact, extra, error, landed, samples, refused);

    /// <summary>
    /// Why this remnant is not a candidate, or null when it is one.
    ///
    /// Read in the order a person would: the things that make rolling impossible first, then the
    /// one that makes it wrong.
    /// </summary>
    private static string Refuses(List<Target> scan, PlanEnvironment env,
        List<Vector2> chain, PlanTarget target, int index, Insisted.Said marked,
        AutoExpeditionSettings settings)
    {
        var live = Nearest(scan, target.Grid);

        if (live != null && live.Rerolled)
            return "already rolled - a remnant takes one Liquid Verisium and no more";

        // **Must take vetoes the roll, and it is a veto rather than a term.** The threshold has
        // already said this reward is worth building the whole chain around; replacing it with an
        // average one cannot be an improvement whatever the runes do. See Insisted.Automatic.
        if (target.Must || marked == Insisted.Said.Take)
            return "must take - the reward is worth more than any roll could return";

        if (marked == Insisted.Said.Avoid)
            return "marked must avoid";

        // **Being unreached is NOT a refusal, and calling it one was a real hole.** The note here
        // used to say a remnant no blast reaches contributes nothing either way. That is true of
        // screening, which holds the chain still - and false of the pass that matters, which
        // re-solves. A remnant the chain currently misses is precisely the one a roll might turn
        // into something worth re-routing to take, and refusing it meant that question was never
        // asked.
        //
        // It is still ranked last by screening, because a fixed chain sees no gain from it at all.
        // Weigh carries one of them into the deep pass regardless. See Detoured.

        // **Being out of every link's range is a different question, and is a refusal.** The gate
        // asks whether ONE more explosive could catch this remnant - placed from a bomb already
        // down, near enough for its blast to cover it - not whether the chain catches it now. So a
        // remnant the route misses by a link stays in, and one several reaches away goes, because
        // advising a walk there is advising a walk on the chance of a good draw.
        //
        // Re-evaluated every pass, so exclusion is never permanent: the moment the route moves
        // near a remnant it becomes a candidate again. See RewardSettings.RollWithinReach.
        // From the detonator when nothing is down yet, because that is where the first link is
        // placed from. Near answers with float.MaxValue on an empty chain, which would refuse the
        // whole site before a plan exists.
        var away = chain.Count > 0
            ? Near(env, chain, index)
            : Vector2.Distance(env.Origin, target.Grid);

        var reaches = MathF.Max(0f, Safe.Read(() => settings.Rewards.RollWithinReach.Value, 1f));
        var within = env.Reach * reaches + env.Blast;

        if (away > within)
        {
            return $"{away:0} grid from the chain, past the {within:0} that one more explosive " +
                   "could cover";
        }

        return null;
    }

    /// <summary>
    /// Whether any link of the chain catches this marker.
    ///
    /// **Through Planner.Catches rather than a distance test written again here.** The copy this
    /// replaced compared squared distances against blast plus radius and stopped there - which is
    /// most of the rule and not all of it. Catches also consults Missed, the record of markers the
    /// game was watched NOT to catch from a spot, so a marker the plugin has learnt is out of reach
    /// counted as reached here and as unreached everywhere else.
    ///
    /// The same mistake as the blast circles: a second copy of a rule that had already drifted from
    /// the first. There is one coverage test and this is not it.
    /// </summary>
    private static bool Caught(PlanEnvironment env, List<Vector2> chain, int index)
    {
        foreach (var at in chain)
        {
            if (Planner.Catches(env, at, env.Targets[index]))
                return true;
        }

        return false;
    }

    private static Target Nearest(List<Target> scan, Vector2 grid)
    {
        foreach (var target in scan ?? new List<Target>())
        {
            if (target.Kind == TargetKind.Remnant && Vector2.Distance(target.Grid, grid) < 1f)
                return target;
        }

        return null;
    }

    /// <summary>
    /// What a roll here is worth, holding the chain still: an exact expectation, not an average.
    ///
    /// **The rune is enumerated rather than drawn, and that is where the noise was.** A rune already
    /// carried elsewhere on the chain contributes nothing at all, because runes do not stack, while
    /// a new one contributes its whole percentage of everything downstream plus a layer of
    /// concentration. So each drawn rune was close to a coin flip between two very different worlds,
    /// and the mean of ten flips moved by a hundred and fifty points between passes - enough to turn
    /// a roll into a keep on nothing but the seed.
    ///
    /// Twenty nine runes with known frequencies is a small enough space to add up instead of
    /// sampling. Socket count and slot count are enumerated beside it for the same reason - four
    /// values and two, both with measured probabilities - so the whole of this is arithmetic and
    /// returns the same number every time it is asked.
    ///
    /// Both propagating slots are enumerated, all four socket counts, both slot counts. The
    /// ordinary slots are computed rather than drawn - their value is a sum, so the expectation of
    /// the sum is exact, and their distinct count comes from the standard "different faces in k
    /// rolls". Nothing in here consults a random stream, which was not true until the arrangements
    /// stopped being sampled - the summary above claimed it anyway for some time.
    ///
    /// **One pass is one round, not the whole answer.** A shape above ExactToSockets has more
    /// arrangements of its propagating slots than a pass walks, so each pass takes the next block
    /// and returns what it found; Weigh adds that to the earlier rounds and takes the mean. Total
    /// and Weight are returned rather than a gain because means do not add and sums do. More says
    /// whether anything is left, and when it is false the figure is exact and will not be
    /// recomputed.
    ///
    /// No reward enters this at all, on either side of the comparison - see FromRecipes.
    /// </summary>
    private static (double Total, double Weight, bool More, double Standing) Enumerated(
        PlanEnvironment env, List<Vector2> chain, int index,
        AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known,
        List<RolledShape> shapes, int round)
    {
        var targets = new List<PlanTarget>(env.Targets);
        var more = false;

        // **The environment every cell is scored against, built once.** It names `targets` by
        // reference, so replacing the entry at `index` is visible through it and a fresh copy per
        // cell says nothing new - it was one record allocation per scored cell, and there are
        // thousands of those a pass. See the walk below.
        var scoring = env with { Targets = targets };
        var was = env.Targets[index];
        var total = 0d;

        // **Divided by the weight actually accumulated, not assumed to be one.**
        //
        // The rune table's shares sum to 1.002 - three decimal places, written down and dismissed as
        // rounding. In a weighted average it is not rounding, it is a systematic 0.25% inflation of
        // every score this walks, and a quarter of a percent of a 25,000 point chain is 65 points of
        // gain that is not there. It showed as three unrelated remnants all worth exactly +65.4,
        // including ones the chain does not reach - where the answer has to be nought, because every
        // score being averaged IS the baseline.
        //
        // Normalising is the fix rather than correcting the table, because the table is measured
        // data and will be re-measured; an average that only works when its weights happen to total
        // one is a trap set for the next person who edits them.
        var weight = 0d;

        // **A roll produces a remnant, and a remnant is one recipe.**
        //
        // This used to draw a socket count, how many slots propagate, and then two runes from a
        // global table INDEPENDENTLY - assembling a remnant out of four unrelated variables. Two
        // faults followed.
        //
        // The rune pairs it priced need not exist. A remnant becomes one recipe and the recipe
        // fixes every slot together: measured at (602,901), whose propagating slots offer Fire,
        // Stone, Life and Tempest, Cold, the only pairings that exist are Life+Tempest, Life+Cold,
        // Fire alone and Stone alone. The old loop priced Fire with Tempest, which is not a remnant.
        //
        // And the reward was drawn apart from the runes - a flat Rolls.Average bolted onto whatever
        // pair came up - when both come from the same recipe and cannot be had separately.
        //
        // So the draw is over SHAPES: a socket count, and which rune is fixed in which slot. The
        // recipes follow from those. See ShapesARollCouldProduce, which reads them off the game's
        // own tables before the advice leaves the calling thread.
        //
        // **Recipes are chosen, positions are drawn, and the two cannot be folded together.** After
        // a roll the player takes the best recipe on offer, which is a max - and Best already does
        // that inside one Score when the target carries them all as Choices, so every recipe of a
        // shape costs one score between them. Which slots propagate is the game's dice rather than
        // the player's choice, so it is an average and stays outside the score. Putting positions
        // in as Choices too would let Best pick the best of those as well, which is assuming the
        // player chooses them.
        for (var n = 0; n < (shapes?.Count ?? 0); n++)
        {
            var shape = shapes[n];

            if (shape.Share <= 0d || shape.Recipes is not { Count: > 0 })
                continue;

            var twoSlots = Rolls.TwoSlots(settings);

            for (var slots = 1; slots <= 2; slots++)
            {
                var slotShare = slots == 2 ? twoSlots : 1f - twoSlots;

                if (slotShare <= 0f || slots > shape.Sockets)
                    continue;

                // **Every set of that many positions, each equally likely.** Which slots the game
                // marks as propagating is stated nowhere: PassedOnRunePositions is read off a
                // remnant that already exists, and a roll makes a new one. Uniform over the sets is
                // the least-assumption reading rather than a number invented to fill the gap.
                //
                // **Sets exposing the same runes are scored once.** A position past the end of a
                // recipe exposes nothing, so on a shape whose recipes are shorter than its socket
                // count many sets ask an identical question. Collapsing them is exact - each
                // survivor carries the weight of every set that folded into it - and measured at
                // 21% of the whole enumeration.
                var sets = OutcomesOf(shape, slots);

                if (sets.Count == 0)
                    continue;

                // **The rare shapes are sampled rather than walked, because they are most of the
                // work and almost none of the probability.**
                //
                // Measured: seven, eight and nine socket shapes are 7,748 of 9,597 cells - four
                // fifths of the enumeration - for 1.9% of the distribution, because the number of
                // position sets grows as the square of the socket count while their share does
                // not. Walking them exhaustively spent the budget on one-in-fifty outcomes and
                // overran it: 2,224ms against a 1,000ms cap, which then abandoned whole remnants
                // unexamined. An unbounded silent omission is worse than a bounded known one.
                //
                // Three other ways were tried and measured first: collapsing shapes that reach the
                // same recipes saved 0%, collapsing shapes that can only expose a weak rune saved
                // 5%, and the outcome dedup above saves 21%. Together they do not reach.
                //
                // The sample is drawn without replacement and each draw carries the weight of the
                // ones it stands for, so the expectation is unbiased - what it loses is the
                // certainty that no arrangement was missed, over the 1.9% where that is cheapest
                // to give up. Everything at six sockets and below is still walked in full.
                // **One block of a fixed walk, not a fresh handful of dice.**
                //
                // This drew ArrangementsPerRound of them at random every pass. Every pass therefore
                // recomputed the same approximation with a different seed instead of getting any
                // better at it, and the same remnant on one site read +104.3 on one pass and +99.3
                // on the next. Passes are cheap and frequent - one per solve - so the work wants
                // accumulating rather than repeating.
                //
                // So each arrangement has a fixed place in a walk that covers all of them exactly
                // once, this pass takes the block at `round`, and Weigh adds it to what earlier
                // rounds found. The answer converges on the exhaustive one and then stops moving,
                // and it is the same answer every time it is asked - which is what the summary of
                // this method has always claimed.
                //
                // **Spread rather than in order**, because a partial answer is read long before the
                // last round arrives. Walking indices 0,1,2... would make every early answer a
                // reading of the lowest-numbered slots; stepping by a stride coprime to the count
                // still visits each exactly once but spreads the block across the range. The stride
                // comes from the count alone, so it does not move between passes or between runs.
                var whole = 0;

                foreach (var (_, stands) in sets)
                    whole += stands;

                if (whole <= 0)
                    continue;

                var block = shape.Sockets > ExactToSockets ? ArrangementsPerRound : sets.Count;
                var from = round * block;

                if (from >= sets.Count)
                    continue;

                if (from + block < sets.Count)
                    more = true;

                var stride = SpreadStride(sets.Count);

                for (var k = from; k < from + block && k < sets.Count; k++)
                {
                    var (positions, stands) = sets[(int)((long)k * stride % sets.Count)];

                    // Its own weight, not scaled up to stand for arrangements this pass skipped.
                    // The skipped ones are coming in a later round, and counting them twice is what
                    // scaling would do.
                    var each = shape.Share * slotShare * stands / whole;

                    targets[index] = FromRecipes(was, settings, known, shape, positions);

                    total += each * Planner.Score(scoring, chain);
                    weight += each;
                }
            }
        }

        // **The remnant as it stands, scored without its reward too.**
        //
        // The rolled side prices every recipe at nought - see FromRecipes - so comparing it against
        // the plan's ordinary score would charge the roll for a reward it was never credited with,
        // and every remnant holding anything at all would read as a keep. Both sides are scored on
        // runes and sockets, and the reward is decided elsewhere, by the must-take threshold.
        //
        // Every combination is kept rather than locked to the richest one, matching the rolled side
        // again: there the planner picks between the recipes a roll could produce, so here it picks
        // between the ones the remnant already offers. Holding it to one would charge the roll for
        // a choice it did not take away.
        targets[index] = was with { Choices = ChoicesWithoutReward(was) };

        var standing = Planner.Score(scoring, chain);

        return (total, weight, more, standing);
    }

    /// <summary>
    /// A step that walks every index of a list of this length exactly once, spread out.
    ///
    /// Stepping by a value coprime to the length visits each index once before repeating, which is
    /// what makes a block of the walk a spread sample rather than a run of neighbours. Any coprime
    /// does that; a large one spreads the early blocks widest.
    ///
    /// One, when the length is small enough that spreading means nothing.
    /// </summary>
    private static int SpreadStride(int count)
    {
        if (count <= 2)
            return 1;

        for (var stride = Math.Min(count - 1, 9973); stride > 1; stride--)
        {
            if (Coprime(stride, count))
                return stride;
        }

        return 1;
    }

    /// <summary>Whether these share no factor but one. See SpreadStride.</summary>
    private static bool Coprime(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a == 1;
    }

    /// <summary>
    /// The remnant's own combinations with every reward set to nought and nothing else touched.
    ///
    /// The runes, the local worth and the modifiers each combination spreads are left as they are,
    /// so what the planner sees is the same remnant valued on its contents alone.
    /// </summary>
    private static (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals,
        string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[]
        ChoicesWithoutReward(PlanTarget was)
    {
        if (was.Choices is not { Length: > 0 })
            return was.Choices;

        var bare = new (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals,
            string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[was.Choices.Length];

        for (var n = 0; n < was.Choices.Length; n++)
        {
            var choice = was.Choices[n];

            bare[n] = (0f, choice.Carries, choice.Local, choice.Locals, choice.Runes, choice.Spread);
        }

        return bare;
    }

    /// <summary>
    /// What re-solving adds on top of that, which is the only part still worth sampling.
    ///
    /// **A difference, not a total, and that is the whole point.** Enumerated already answers what
    /// a roll is worth with the chain held still, exactly. What a re-solve adds is the chance that
    /// the roll makes some OTHER spot worth taking - nought on most samples, occasionally large.
    /// Sampling that difference rather than the whole score means the quantity being averaged is
    /// usually zero, so a handful of draws settles it where hundreds would have been needed to pin
    /// down the total.
    /// </summary>
    private static (double Mean, double Error, int Landed) Premium(PlanEnvironment env,
        List<Vector2> chain, int index,
        AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known,
        int stream, int samples, List<Vector2> spots, double house, int links)
    {
        var random = new Random(stream);
        var targets = new List<PlanTarget>(env.Targets);
        var total = 0d;
        var squares = 0d;
        var landed = 0;

        for (var s = 0; s < samples; s++)
        {
            targets[index] = Rolled(env.Targets[index], settings, known, random);

            var after = env with { Targets = targets };
            var still = Planner.Score(after, chain);

            // **The one marker that changed, offered a place, and then the chain re-ordered.**
            //
            // This was Planner.Improve - any spot, any order - which is six rounds of Sweep, and
            // Sweep is every link against every candidate in the site. Per sample, per remnant, that
            // is ten to twenty seconds of advice for one remnant on a Grand site, with the player
            // standing still. It is not slow there, it is unusable.
            //
            // Restitched asks the narrower question the roll actually raises: propagation dominates
            // the objective and flows forward, so a rune that has just become valuable is realised by
            // moving its remnant earlier - and a remnant the chain does not catch has to be let in.
            // Ordering and one substitution answer both, about two orders of magnitude cheaper. See
            // Planner.Restitched and SolverSettings.RollSubstitutionLinks.
            var moved = Planner.Score(after,
                Planner.Restitched(after, chain, index, spots, links));

            // **Less what re-solving was worth anyway, which is the correction this was missing.**
            // Improve starts from the published chain and that chain is not necessarily a local
            // optimum of it - the solver stops on a time budget, not on a proof - so re-solving
            // finds a gain whether or not anything was rolled. Without taking that off, every
            // remnant the chain does not reach was credited with it: two quite different ones came
            // back at +64.1 apiece, which is the house improvement wearing a roll's clothes.
            //
            // What is wanted is only the re-routing the ROLL unlocks, so the same measurement on the
            // unrolled environment is subtracted. Floored at nought: a roll that makes the chain
            // harder to improve has not cost anything, it has simply unlocked nothing.
            var drawn = Math.Max(0d, moved - still - house);

            total += drawn;
            squares += drawn * drawn;

            if (drawn > 0d)
                landed++;
        }

        var mean = total / samples;

        // **The spread, because the margin that uses this mean has never been checked against it.**
        // Diverting the player wants a new candidate to beat the advised one by 10%, and 10% was
        // chosen to sit above this estimate's own noise without anybody measuring the noise. The
        // standard error of the mean is the quantity that settles it: a margin under it is a
        // threshold that flips on nothing. See reroll_plan.md.
        //
        // **Read it beside the count, not on its own.** Most draws are exactly nought - the floor
        // above sees to that - so this is the spread of a spike at zero with a tail, and the
        // standard error falls as the tail gets rarer while the estimate gets WORSE. The count of
        // draws that landed is what says whether a mean rests on one sample or on twenty.
        var variance = Math.Max(0d, squares / samples - mean * mean);
        var error = samples > 1 ? Math.Sqrt(variance / samples) : 0d;

        return (mean, error, landed);
    }

    /// <summary>
    /// The socket count up to which every arrangement is walked rather than sampled.
    ///
    /// Six and below is 19% of the enumeration's cells and 98.1% of its probability; seven and
    /// above is the other way round. See where this is used.
    /// </summary>
    private const int ExactToSockets = 6;

    /// <summary>
    /// How many arrangements of a large shape one pass walks.
    ///
    /// Not a sample size: a pass takes the next block of this many and the rounds together cover
    /// every arrangement. It sets how long the first answer takes and how many passes the exact one
    /// needs, and nothing else. See Enumerated.
    /// </summary>
    private const int ArrangementsPerRound = 8;

    /// <summary>
    /// The distinct propagating-rune outcomes of a shape, with how many position sets each stands
    /// for.
    ///
    /// **A position past the end of a recipe exposes nothing**, so on a shape whose recipes are
    /// shorter than its socket count several sets of positions expose an identical set of runes -
    /// on a three rune recipe over nine sockets, all thirty-six pairs collapse to seven answers.
    /// Scoring them separately is the same arithmetic repeated, and folding them is exact so long
    /// as each survivor carries the weight of the ones it absorbed.
    ///
    /// Keyed on the runes each recipe would expose, in slot order, which is the only thing about a
    /// position set the scoring can see.
    /// </summary>
    /// <summary>This shape's arrangements for this slot count, worked out once. See RolledShape.Kept.</summary>
    private static List<(int[] Positions, int Stands)> OutcomesOf(RolledShape shape, int slots)
    {
        if (shape.Kept is not { } kept || slots < 0 || slots >= kept.Length)
            return DistinctPositionOutcomes(shape, slots);

        return kept[slots] ??= DistinctPositionOutcomes(shape, slots);
    }

    private static List<(int[] Positions, int Stands)> DistinctPositionOutcomes(
        RolledShape shape, int slots)
    {
        var found = new List<(int[] Positions, int Stands)>();
        var where = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var positions in PositionSetsOf(shape.Sockets, slots))
        {
            var key = new StringBuilder();

            foreach (var (recipe, _) in shape.Recipes)
            {
                var needs = Safe.Read(() => recipe.RuneCountRequired, 0);

                key.Append('|');

                foreach (var slot in positions)
                {
                    if (slot >= needs)
                        continue;

                    var at = slot;

                    key.Append(Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null) ?? "-")
                       .Append(',');
                }
            }

            var said = key.ToString();

            if (where.TryGetValue(said, out var already))
            {
                found[already] = (found[already].Positions, found[already].Stands + 1);

                continue;
            }

            where[said] = found.Count;
            found.Add((positions, 1));
        }

        return found;
    }

    /// <summary>
    /// Every set of this many slot positions on a remnant of this many sockets.
    ///
    /// One or two, because a remnant carries one propagating slot and sometimes a second - see
    /// Debug.DoubleOrNothingDouble. Which of them propagate is drawn rather than chosen and the
    /// odds are stated nowhere, so the caller treats every set as equally likely.
    /// </summary>
    private static List<int[]> PositionSetsOf(int sockets, int howMany)
    {
        var found = new List<int[]>();

        if (sockets <= 0 || howMany <= 0 || howMany > sockets)
            return found;

        if (howMany == 1)
        {
            for (var at = 0; at < sockets; at++)
                found.Add(new[] { at });

            return found;
        }

        for (var first = 0; first < sockets; first++)
        for (var second = first + 1; second < sockets; second++)
            found.Add(new[] { first, second });

        return found;
    }

    /// <summary>
    /// A remnant of this shape, carrying every recipe it could become as a choice.
    ///
    /// **One choice per recipe, so the scoring picks between them as it does for a real remnant.**
    /// A recipe fixes every slot together, so the rune pairs a remnant can hold are the pairs some
    /// recipe lists and no others. The older draw took runes from a global table independently and
    /// priced pairs that cannot exist.
    ///
    /// The propagating runes are the recipe's runes at the drawn positions; everything else it
    /// holds is local. Slots past the recipe's length hold nothing, which is why a two rune recipe
    /// on a six socket remnant propagates nothing when the drawn positions fall past it.
    ///
    /// Every choice carries a reward of nought - see the comment on the loop for why.
    /// </summary>
    /// <param name="positions">Which slots propagate, drawn by the caller. See PositionSetsOf.</param>
    private static PlanTarget FromRecipes(PlanTarget was, AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known,
        RolledShape shape, int[] positions)
    {
        var choices = new List<(float Reward, float Carries, float Local, (string Id, float Worth)[] Locals,
            string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)>(shape.Recipes.Count);

        var weights = new List<(string Id, float Weight)>();

        foreach (var (recipe, _) in shape.Recipes)
        {
            var needs = Safe.Read(() => recipe.RuneCountRequired, 0);
            var propagating = new List<string>(positions.Length);
            var locals = new List<(string Id, float Worth)>();

            for (var slot = 0; slot < needs && slot < shape.Sockets; slot++)
            {
                var at = slot;
                var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (positions.Contains(slot))
                {
                    propagating.Add(id);

                    var known2 = false;

                    foreach (var (had, _) in weights)
                    {
                        if (string.Equals(had, id, StringComparison.OrdinalIgnoreCase))
                        {
                            known2 = true;

                            break;
                        }
                    }

                    if (!known2)
                        weights.Add((id, Runes.Weight(id)));
                }
                else
                {
                    locals.Add((Id: id, Worth: Locally(known)));
                }
            }

            // **No reward on either side, because this question is about runes.**
            //
            // Pricing the reward a roll might return drags in an odds problem the client cannot
            // answer: its tables say which recipes a shape can reach and nothing about how often
            // each is offered, so a Mirror of Kalandra was priced as freely available as a
            // Glassblower's Bauble. Measured, 60% of the expected reward came from shapes that can
            // reach something dear - the advice was a bet on numbers nobody has.
            //
            // It is also unnecessary. A remnant holding a reward worth more than a roll could
            // return is already a must-take, and a must-take is never advised for rolling. The
            // reward side is guarded there, where it can be decided on a price rather than on a
            // distribution.
            //
            // So both sides are scored with the reward set aside and what remains is the runes,
            // which is what the advice was ever useful for. Three faults went with it: the flat
            // Rolls.Average constant, the floor that applied to one side only, and the jackpot.
            choices.Add((0f, 0f, locals.Sum(x => x.Worth),
                locals.ToArray(), propagating.ToArray(), null));
        }

        var own = Safe.Read(() => Weighing.WeightOfTarget(new Target
        {
            Kind = TargetKind.Remnant,
            Sockets = shape.Sockets,
        }, settings), 0f);

        return was with
        {
            Weight = own,
            Runes = weights.ToArray(),
            PropagatingRuneWeights = weights.ToArray(),
            Choices = choices.ToArray(),
        };
    }

    /// <summary>
    /// One possible remnant on the far side of the orb.
    ///
    /// Everything a roll decides is redrawn: the socket count, how many slots propagate and which
    /// runes are in them. The reward is not, because nothing in the reroll comparison is priced on
    /// a reward - see Built.
    /// </summary>
    private static PlanTarget Rolled(PlanTarget was, AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known, Random random)
    {
        var sockets = Rolls.Socketed(random);
        var slots = Rolls.Slots(random, settings);
        var drawn = new string[slots];

        for (var n = 0; n < slots; n++)
            drawn[n] = Rolls.Rune(random);

        return Built(was, settings, known, sockets, drawn);
    }

    /// <summary>
    /// The same remnant, from a layout it is handed rather than one it draws.
    ///
    /// Split out so Enumerated can walk the rune table deliberately while Rolled goes on drawing.
    /// One assembler either way, because the two passes must be pricing the same object - a second
    /// copy of this is a second answer waiting to disagree.
    /// </summary>
    private static PlanTarget Built(PlanTarget was, AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known, int sockets,
        string[] drawn)
    {
        var runes = new List<(string Id, float Weight)>(drawn.Length);
        var ids = new List<string>(drawn.Length);
        List<(string Id, int Tag, float Percent, bool Flat)> spread = null;

        foreach (var id in drawn)
        {

            // **Drawn twice is carried once, and a combination really can repeat a rune.**
            //
            // Confirmed in game rather than argued: an Explosive Transmutation combination was
            // opened and found holding the same rune in two of its sockets. So a combination is not
            // a set of distinct runes, and since runes do not stack that remnant propagates one
            // modifier rather than two - which is what this does by skipping the duplicate instead
            // of drawing again.
            //
            // What is NOT claimed is the rate. The game fills each socket from its own weight table;
            // this draws from the pooled marginal frequencies, and there is no reason the two
            // collide equally often. It is the right treatment of a collision on a distribution
            // that is admittedly the wrong shape - see Rolls, where the same substitution is made
            // for the rune table as a whole.
            //
            // So the slot is not redrawn. **What this does NOT claim is that the rate is right.**
            // The game fills each slot from its own weight table; this draws twice from the pooled
            // marginal frequencies, which collide about once in twenty. Those are different
            // processes and there is no reason for their collision rates to match. It is the right
            // TREATMENT of a collision on a distribution that is admittedly the wrong shape - see
            // Rolls, where the same substitution is made for the rune table as a whole.
            if (ids.Contains(id))
                continue;

            known.TryGetValue(id, out var said);

            ids.Add(id);
            runes.Add((id, said.Weight));

            var scope = said.Scope ?? "";

            if (scope.Trim().Length == 0)
                continue;

            foreach (var (tag, percent) in Tags.Scope(scope, out _))
            {
                if (percent <= 0f)
                    continue;

                spread ??= new List<(string, int, float, bool)>(2);

                // A sampled rune is a share, never a flat addition: it is a percentage the roll
                // might land on. See Weighing.ScopedEffectsOfReward.
                spread.Add((id, tag, percent, false));
            }
        }

        // **The ordinary slots, which were being left empty.** Every slot that does not propagate
        // holds a rune reaching this remnant's own waves, and the sampled remnant had none of them -
        // so every roll was scored as replacing a real remnant with one carrying no local runes at
        // all. That is a loss on every sample, it grows with the concentration term, and it read on
        // screen as "KEEP, a roll costs 269" on remnants whose propagating runes were all duplicated
        // elsewhere and therefore worth nothing to lose.
        //
        // Drawn from the same table and deduplicated the same way Propagation.Local does it, because
        // the comparison being made is against a number that function produced.
        // **The ordinary slots are worked out, not drawn, and that is what makes Enumerated
        // exact.** They were being filled by pulling runes out of the table, so every "enumerated"
        // combination still carried a random handful of them - the arithmetic looked deterministic
        // and was not.
        //
        // Both figures it needs have closed forms. What they are WORTH is a sum over the slots, so
        // the expectation is the slot count times the average rune's local value - exact, because a
        // sum of expectations is the expectation of the sum. How MANY distinct runes there are
        // feeds concentration, which counts rather than adds, so that one is the standard "how many
        // different faces in k rolls": one minus the chance each rune is missed every time.
        var ordinary = Math.Max(0, sockets - drawn.Length);
        var local = ordinary * Locally(known);
        var distinct = Distinct(ordinary);

        // **A rolled remnant is priced at nought, like every other side of this comparison.**
        //
        // The client's tables say which recipes a shape can reach and nothing about how often each
        // is offered, so any reward put here is drawn from a distribution nobody has. The remnant
        // as it stands is scored the same way - see ChoicesWithoutReward - and what the reroll
        // advice compares is runes and sockets. Whether a reward is too good to roll over is
        // decided by the must-take threshold instead, on a price rather than on a distribution.
        const float paid = 0f;

        // The remnant's own weight comes from the table exactly as a scanned one's does - the reward
        // rides on the combination rather than on the marker. The bonus and per-socket figures it
        // used to add are gone; see Weighing.WeightOfTargetByKind.
        var own = Safe.Read(() => Weighing.WeightOfTarget(new Target
        {
            Kind = TargetKind.Remnant,
            Sockets = sockets,
        }, settings), 0f);

        return was with
        {
            Weight = own,
            Runes = runes.ToArray(),

            // **The same set, as the lookup too.** A sampled remnant's runes ARE its candidates -
            // there is no recipe behind it to be narrower than the draw - and leaving this inherited
            // from the real remnant would weigh the sampled ids against a table that does not hold
            // them, dropping every one. See Planner.WeightsOfChosenRunes.
            PropagatingRuneWeights = runes.ToArray(),
            Choices = new[]
            {
                // **Nameless on purpose.** A sampled remnant's ordinary runes are an expectation
                // rather than a draw - see the note above - so there is no identity to strike out
                // against the chain, and a null id tells the scoring loop exactly that. It leaves
                // an imagined remnant slightly better off than a real one whose duplicates are
                // struck, which is a known bias and a smaller one than sampling the identities
                // would reintroduce.
                (Reward: paid, Carries: 0f, Local: local, Locals: Anonymous(distinct, local),
                    Runes: ids.ToArray(), Spread: spread?.ToArray()),
            },
        };
    }

    /// <summary>
    /// The reasoning, in the words a person deciding at the remnant would want.
    ///
    /// **Said out loud because the arithmetic disagrees with instinct about half the time.** The
    /// two cases that surprise people are a rich-looking remnant that should be rolled because
    /// every rune on it is already carried by something earlier, and a poor one that should be kept
    /// because it holds the chain's only copy of something. Neither is visible from the remnant.
    /// </summary>
    private static string Because(PlanEnvironment env, List<Vector2> chain, int index, double gain,
        bool settled, AutoExpeditionSettings settings)
    {
        var target = env.Targets[index];
        var sole = new List<string>();
        var shared = new List<string>();

        foreach (var (id, weight) in target.Runes ?? [])
        {
            if (weight <= 0f || id == null)
                continue;

            (BankedRunesReaching(env, index, id) ? shared : sole).Add(id);
        }

        var said = new List<string>();

        // **Says how many remnants that claim was checked against.** "Only source of Volcanic" is
        // a statement about every other remnant in the environment, and when it looks wrong the
        // first question is which remnants it actually looked at - a remnant the chain cannot reach
        // is still in the list, one streamed out is not.
        if (sole.Count > 0)
            said.Add($"only source of {Names(sole)} among {Remnants(env)} remnants");

        if (shared.Count > 0)
            said.Add($"{Names(shared)} already carried elsewhere");

        // **Which duplicate to roll is not the obvious one.** Where two remnants carry the same
        // rune, rolling the EARLIER one keeps the rune - the later copy still supplies it - and puts
        // the new one on the link with the longest reach. Rolling the later one gains a rune that
        // reaches almost nothing.
        if (shared.Count > 0 && Earliest(env, chain, index, shared))
            said.Add("and is the earlier copy, so a new rune here reaches furthest");

        // **What it is actually holding, which the rune list alone does not say.** The first
        // version listed only the propagating runes, so a remnant whose runes were all duplicated
        // read "already carried elsewhere" - which sounds like a reason to roll - beside a number
        // saying a roll costs 269. Both were true and neither explained the other: what the roll
        // gives up is the reward and the ordinary slots, and neither was mentioned.
        var reward = 0f;

        foreach (var choice in target.Choices ?? [])
            reward = MathF.Max(reward, choice.Reward);

        // **In exalts, which a choice's Reward is not.** It is in WEIGHT - the price divided by
        // exalts per weight point - and printing it as exalts once set sixty weight against sixty
        // five exalts and read as a near tie, when the remnant was holding 33ex.
        //
        // **Stated rather than charged.** Neither side of the comparison is priced on a reward any
        // more - see Enumerated - so this is not a term in the number beside it. It is here because
        // a reader deciding whether to follow the advice wants to know what is being replaced, and
        // because the threshold that does guard the reward is a separate setting. See
        // RewardSettings.MustTakeAbove.
        if (reward > 0f)
        {
            var worth = reward * MathF.Max(0.0001f,
                Safe.Read(() => settings.Rewards.PointWorth.Value, 1f));

            said.Add($"holds {worth:N0}ex of reward, which a roll replaces and the figure beside " +
                     "this does not count");
        }

        var ordinary = 0;

        foreach (var choice in target.Choices ?? [])
            ordinary = Math.Max(ordinary, choice.Locals?.Length ?? 0);

        if (ordinary > 0)
            said.Add($"{ordinary} ordinary slot{(ordinary == 1 ? "" : "s")} on its own waves");

        // Worth saying, because the number means something different here: the chain gets nothing
        // from this remnant as it stands, so the whole figure is what re-routing to it would be
        // worth once it had been rolled into something else.
        if (!Caught(env, chain, index))
            said.Add("the chain does not reach it as planned, so this is what re-routing would buy");

        var why = said.Count > 0 ? string.Join("; ", said) : "nothing carried forward";

        // A remnant screening rejected never had its chain re-solved, so its figure is an
        // estimate that ignores re-routing - said plainly rather than dressed up as a verdict.
        if (!settled)
            return $"not re-solved: screening put a roll here at about {gain:N0}, behind the ones " +
                   $"that were. {why}";

        // **No verdict word here, and none of the ranking either.** This said "ROLL:" or "KEEP:"
        // from the pass's own pick, and both readers of it state the verdict themselves from
        // Advising - so one line came out as "ROLL -358.3 ... - KEEP: only source of Rebirth", both
        // verdicts at once about one remnant, because in the continuous mode the pass ranks on the
        // enumerated gain while Divert picks on what a roll destroys.
        //
        // Nor can it be fixed by asking Advising here: this runs on the background pass, where Best
        // is still the previous answer. The reason is what is true of this remnant; which remnant is
        // advised is the reader's to say, at the moment of reading. See Advising.
        return gain > 0d
            ? $"worth about {gain:N0} to the chain. {why}"
            : $"a roll here costs about {-gain:N0}. {why}";
    }

    /// <summary>How many remnants the sole-source claim was tested against. See Because.</summary>
    private static int Remnants(PlanEnvironment env)
    {
        var count = 0;

        foreach (var target in env.Targets)
        {
            if (target.Kind == TargetKind.Remnant)
                count++;
        }

        return count;
    }

    /// <summary>The runes as the game names them, for a sentence somebody reads. See RuneInfo.</summary>
    private static string Names(List<string> ids)
    {
        var said = new List<string>(ids.Count);

        foreach (var id in ids)
            said.Add(RuneInfo.Called(id));

        return string.Join(", ", said);
    }

    private static bool BankedRunesReaching(PlanEnvironment env, int index, string id)
    {
        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (i == index || env.Targets[i].Kind != TargetKind.Remnant)
                continue;

            foreach (var (other, weight) in env.Targets[i].Runes ?? [])
            {
                if (weight > 0f && string.Equals(other, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether this remnant is the first on the chain to offer any of these runes.</summary>
    private static bool Earliest(PlanEnvironment env, List<Vector2> chain, int index,
        List<string> runes)
    {
        var mine = At(env, chain, index);

        if (mine < 0)
            return false;

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (i == index || env.Targets[i].Kind != TargetKind.Remnant)
                continue;

            var theirs = At(env, chain, i);

            if (theirs < 0 || theirs >= mine)
                continue;

            foreach (var (other, _) in env.Targets[i].Runes ?? [])
            {
                if (runes.Contains(other))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Which link first catches this marker, or minus one when none does.</summary>
    private static int At(PlanEnvironment env, List<Vector2> chain, int index)
    {
        var target = env.Targets[index];
        var reach = env.Blast + target.Radius;

        for (var i = 0; i < chain.Count; i++)
        {
            if (Vector2.DistanceSquared(chain[i], target.Grid) <= reach * reach)
                return i;
        }

        return -1;
    }
}
