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
/// and rests on measured shares of what rolls return. See Rolls and DisplaySettings.RollFigures.
/// </summary>
internal sealed class Rolling
{
    /// <summary>The one the plugin uses. Static, because it is one table per session and every caller wants the same one.</summary>
    public static readonly Rolling Here = new();

    /// <summary>What to do about one remnant, and why.</summary>
    /// <param name="Gain">What a roll is worth to the chain as it stands, exactly. See ScoreRollOutcomes.</param>
    internal sealed record RollAdvice(Vector2 Grid, double Gain, string Why, bool Refused = false);

    /// <summary>
    /// The advice as it stands, keyed by the remnant's cell so the overlay can find it.
    ///
    /// **Replaced whole rather than edited in place.** It is written by a background pass and read
    /// by the frame that draws the labels, and a dictionary being rebuilt underneath an enumeration
    /// is the one way this could take the HUD down. A finished copy swapped into the field is a
    /// single reference assignment, which the reader either sees or does not.
    /// </summary>
    private volatile Dictionary<(int X, int Y), RollAdvice> _adviceByCell = new();

    /// <summary>The one remnant worth rolling, or null when none is.</summary>
    public RollAdvice Advised { get; private set; }

    /// <summary>How far through the current round the pass is, nought to one; nought when nothing is running.</summary>
    public float RoundProgress { get; private set; }

    /// <summary>
    /// How far the whole enumeration has got, nought to one: the rounds already walked plus the share of
    /// the current round done, over the rounds it takes to walk every arrangement.
    ///
    /// RoundProgress restarts at nought every round, so on a site needing five rounds a readout of RoundProgress
    /// alone climbs to three quarters and falls back five times. This climbs once. The bound is taken
    /// at the start of the pass rather than read from RoundsNeeded, which is only published when a pass
    /// ends and so would belong to the previous chain during the first round of a new one.
    /// See RoundsWalked and RoundsNeeded.
    /// </summary>
    public float Progress =>
        Math.Clamp((_progressWalked + RoundProgress) / Math.Max(1, _progressNeeded), 0f, 1f);

    /// <summary>The rounds walked when the running pass began. See Progress.</summary>
    private int _progressWalked;

    /// <summary>The rounds the running pass's chain needs in all. See Progress.</summary>
    private int _progressNeeded = 1;

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
            var said = _adviceByCell;
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
    public string NoAdviceReason { get; private set; } = "no plan solved yet";

    /// <summary>
    /// What the first reroll advice at this site is waiting for, or empty when it is not waiting. For the score
    /// area's Reroll line. See HoldingFirstAdvice.
    /// </summary>
    public string Waiting { get; private set; } = "";

    /// <summary>How far through the longest wait the first advice is, nought to one. See HoldingFirstAdvice.</summary>
    public float WaitProgress { get; private set; }

    /// <summary>What the score was doing when the first advice at this site started, for the dump.</summary>
    public string StartedAt { get; private set; } = "not started at this site";

    /// <summary>The site the first advice has started for, after which it is not held again there.</summary>
    private Vector2? _adviceStartedFor;

    /// <summary>When the site's content was first seen settled in the current stretch of it being so.</summary>
    private DateTime _contentSettledSince = DateTime.MinValue;

    /// <summary>What would be said about the score if the advice started now. See StartedAt.</summary>
    private string _startedAtRise = "";

    /// <summary>
    /// Why the first reroll advice at this site should wait, or null when it may start.
    ///
    /// Waits first for the site's content to stop arriving, then for the site score to rise less than
    /// StartAdviceBelowImprovement over one improvement window - or for StartAdviceAfterWindows windows to pass
    /// since the content settled, whichever comes first, so a site whose score keeps climbing still gets advice.
    /// </summary>
    private string HoldingFirstAdvice(GameController gc, AutoExpeditionSettings settings)
    {
        if (!Rehearsal.ContentSettled)
        {
            _contentSettledSince = DateTime.MinValue;
            WaitProgress = 0f;

            return "the site's content is still arriving";
        }

        if (_contentSettledSince == DateTime.MinValue)
            _contentSettledSince = DateTime.UtcNow;

        var window = TimeSpan.FromMilliseconds(
            Planning.ImprovementWindowMs(settings, Detonator.ExplosiveCount(gc)));
        var windows = Math.Max(1, Safe.Read(() => settings.Solver.Reroll.StartAdviceAfterWindows.Value, 3));
        var longest = window * windows;
        var waited = DateTime.UtcNow - _contentSettledSince;

        WaitProgress = (float)Math.Clamp(waited.TotalMilliseconds / Math.Max(1d, longest.TotalMilliseconds), 0d, 1d);

        var rise = Planning.SiteScoreRise(window);
        var below = Math.Max(0, Safe.Read(() => settings.Solver.Reroll.StartAdviceBelowImprovement.Value, 5)) / 100d;
        var risen = double.IsPositiveInfinity(rise)
            ? $"no whole {window.TotalSeconds:0.#}s window of scores yet"
            : $"+{rise * 100d:0.#}% over the last {window.TotalSeconds:0.#}s";

        if (rise < below)
        {
            _startedAtRise = $"started with the score at {risen}, under the {below * 100d:0.#}% asked for";

            return null;
        }

        if (waited >= longest)
        {
            _startedAtRise = $"started after the longest wait of {windows} window(s), with the score at {risen}";

            return null;
        }

        return $"the score is still rising, {risen}";
    }

    /// <summary>
    /// How many blocks of arrangements the least-advanced remnant has had walked.
    ///
    /// **The least, not the average.** The figure reads as how far the site has got, and a site is
    /// only as settled as its worst-covered remnant - one that has had a single round can still move
    /// by more than the advice's margin. Named for the walk rather than for the pass count, because
    /// a remnant whose enumeration is finished is skipped and its rounds stop climbing. Not
    /// Planner.Rounds, which counts the solver's restarts. See ScoreRollOutcomes.
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
    /// from Advised while the border round the Verisium button was drawn from a per-verdict Roll flag,
    /// and in the continuous mode those are different quantities: Advised is re-picked every frame by
    /// Divert on what a roll destroys, and the flag was set once a pass by the enumerated ranking.
    /// So the line pointed at one remnant and the border ringed another.
    ///
    /// Advised is the advice - it is what Divert sets and what the other modes set at the end of a
    /// pass - so everything that marks the advised remnant asks this.
    /// </summary>
    public bool Advising(Vector2 grid) =>
        Advised is { } best && Vector2.Distance(best.Grid, grid) < 1f;

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
        var said = _adviceByCell;

        if (said.Count == 0)
            return "        nothing weighed";

        var text = new List<string>(said.Count);

        foreach (var (cell, verdict) in said)
        {
            // A positive gain that is not the advised roll is a roll worth making later, not a keep. See Divert.
            text.Add($"        ({cell.X},{cell.Y}) {(Advising(verdict.Grid) ? "ROLL" : verdict.Gain >= MinimumAdvisedGain && !verdict.Refused ? "roll later" : "keep")} " +
                     $"{verdict.Gain:+#,##0.0;-#,##0.0;0} - {verdict.Why}");
        }

        text.Sort(StringComparer.Ordinal);

        return string.Join("\n", text);
    }

    /// <summary>What was said about the remnant standing on this cell, if anything.</summary>
    public RollAdvice Of(Vector2 grid) =>
        _adviceByCell.TryGetValue(((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var said)
            ? said
            : null;

    public void Forget()
    {
        _asked++;
        _adviceByCell = new Dictionary<(int X, int Y), RollAdvice>();
        Advised = null;
        NoAdviceReason = "no plan solved yet";
        _for = Vector2.Zero;

        // The totals are scores of a chain, and there is no longer a chain. See Gathering.
        _gathered = new Dictionary<(int X, int Y), (double Total, double Weight, int Round, bool More)>();
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

        _adviceStartedFor = null;
        _contentSettledSince = DateTime.MinValue;
        Waiting = "";
        WaitProgress = 0f;

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

    /// <summary>The lifts each rolled combination holds, per amplifier class, in order, or null for none. See RolledRemnant.</summary>
    private static float[][] HeldLiftOfRolledChoices(
        IReadOnlyList<(float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes,
            (string Id, int Tag, float Percent, bool Flat)[] Spread)> choices)
    {
        var found = new float[choices.Count][];
        var any = false;

        for (var c = 0; c < choices.Count; c++)
        {
            var held = new List<string>(choices[c].Runes ?? []);

            foreach (var (id, _) in choices[c].Locals ?? [])
                held.Add(id);

            found[c] = Weighing.HeldLiftsOfRunes(held);
            any |= found[c] != null;
        }

        return any ? found : null;
    }

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


    /// <summary>The least a roll must be worth before it is advised at all. See where it is used.</summary>
    private const double MinimumAdvisedGain = 1d;



    /// <summary>Which solve the standing advice was worked out after. See Consider.</summary>
    private int _after = -1;

    /// <summary>How many remnants on this site had been rolled when the last pass was dispatched.</summary>
    private int _rolledWhen = -1;

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

        // Cleared every frame and set again only by the gate that is holding, so a return anywhere above the gate -
        // the mode switched off, no plan, an explosive down - cannot leave "waiting" on the score area. It did: the
        // dump read "rolling advice is off" beside a first advice still WAITING. See HoldingFirstAdvice.
        Waiting = "";

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
        // What it does need is an answer to show at all, which is why _adviceByCell rather than _plan: _plan
        // is set when a pass is DISPATCHED, so it is non-null before the first one has produced
        // anything.
        Fresh = mode == RerollSettings.Continuous
            ? _adviceByCell.Count > 0
            : planning is { Searching: false } && !Working && _plan != null &&
              ReferenceEquals(_plan, planning.Plan);

        if (env == null || chain is not { Count: > 0 })
        {
            Nothing("no plan solved yet");

            return;
        }

        var site = Detonator.DetonatorGridPosition(gc);

        // **The first advice at a site waits for the site and the score to settle.** A presolve plan made while
        // markers are still arriving is a plan for part of the site, and one made while the score is still
        // climbing fast is a plan that will not last - so advice weighed against either is about a chain about to
        // be replaced. Once it has started it is not held again at this site: a roll makes the next solve jump,
        // and that jump is the roll paying off. A plan the player asked for is advised on at once. See
        // RerollSettings.StartAdviceBelowImprovement and Rehearsal.ContentSettled.
        if (Planning.Rehearsing && !(_adviceStartedFor is { } started && Vector2.Distance(started, site) < 1f))
        {
            var holding = HoldingFirstAdvice(gc, settings);

            if (holding != null)
            {
                Waiting = holding;
                Nothing("waiting: " + holding);

                return;
            }

            _adviceStartedFor = site;
            StartedAt = _startedAtRise;
        }

        Waiting = "";

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

                NoAdviceReason = "explosives are already down - rolls are worth advising before placing, " +
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

        // **A roll dispatches a pass at once, rather than waiting for a solve to finish.**
        //
        // Reported from a Grand site: roll a remnant, the readout says nothing is worth rolling, an
        // eight second solve runs, and only when it ends does the advice name the next remnant. That
        // is the gate below. It dispatches on a COMPLETED solve, so between the roll and the end of
        // the search in flight the verdicts are the pre-roll ones - and Divert drops the remnant just
        // rolled, so if it was the only one clearing the floor the answer becomes "none is advised"
        // for the length of a solve.
        //
        // A roll changes what every other remnant's roll is worth, because runes do not stack, so the
        // standing verdicts are the wrong answer rather than an old one. It also invalidates the
        // accumulated rounds on its own - Gathering holds each remnant's offers - so refreshing now
        // costs no deepening that the roll had not already spent. Measured cost of a pass on this
        // site: 23ms over three remnants, with the arrangements re-used.
        //
        // Continuous only. The other modes drop the verdicts wholesale on a roll and re-solve - see
        // the call site - so they have nothing to refresh.
        var rolled = mode == RerollSettings.Continuous ? Rolled(scan, site) : 0;

        if (!deepening && rolled == _rolledWhen && Vector2.Distance(_for, site) < 1f &&
            _after == Planning.Solves)
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

        // **Off the frame.** The pass scores the chain once per outcome of a roll, thousands for each
        // remnant, and time spent here is time the HUD is not drawing. The answer is advice about a
        // decision nobody is making this instant, so it can arrive late.
        if (_working is { IsCompleted: false })
        {
            // **A roll stops the pass in flight**, which is answering about the remnants before it: runes do not stack,
            // so every verdict it is working out is the wrong answer now. It stops at its next remnant - see the check in
            // WeighRemnants - and the pass the roll asks for is dispatched on the first frame after. Before this a pass
            // walking its rounds ran to the end, seconds on a Grand site, and the roll's own pass waited behind it with
            // the readout at nought.
            if (rolled != _rolledWhen && !_abandonedFor.Equals(rolled))
            {
                _abandonedFor = rolled;
                Interlocked.Increment(ref _asked);
            }

            return;
        }

        _for = site;
        _plan = planning.Plan;
        _after = Planning.Solves;
        _rolledWhen = rolled;

        var asking = Interlocked.Increment(ref _asked);
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
        var markedForReward = MarkedForRewardValue(env);
        var known = Known(settings);

        // Only for the line that reports it. The environment has already accounted for these -
        // their content is out of the target list and their propagation is banked in Secured - so
        // this is the count of remnants the advice has nothing left to say about.
        var settled = Settled(gc, targets);

        // **Read here, on the thread that owns the reads.** Valuation goes to game memory and two
        // TimeCaches, and the advice runs on a task; gathering the shapes up front means the task
        // walks an immutable list instead. See ShapesARollCouldProduce.
        var shapes = ShapesARollCouldProduce(valuation, Rolls.MapSlotFloor(gc), Detonator.Grand(gc));

        // **Its own thread, at normal priority - ahead of the solver's workers.** A pass is usually asked for when the
        // chain has reached a strong point and the player is deciding what to roll, so it is what they are waiting on.
        // At the workers' below-normal priority it shared the processors with eight of them: a pass on a Grazed Prairie
        // site (2026-10-06) took 12,959 ms during a cold solve against 338 ms once the solve had settled. Normal and not
        // above, so it never outranks the game's own threads. See BackgroundWork.StartAtNormalPriority.
        _working = BackgroundWork.StartAtNormalPriority(() => BackgroundWork.Record("reroll advice", () =>
        {
            try
            {
                WeighRemnants(settings, targets, env, links, settled, marked, markedForReward, known, asking, shapes);
            }
            catch (Exception ex)
            {
                // **The whole exception, not its message.** "Index was outside the bounds of the
                // array" names neither the array nor the method, and the pass runs on a task whose
                // stack reaches no log. One line on the overlay for the player, the full trace in
                // the dump for whoever has to find it.
                _failure = ex.ToString();
                _failed = DateTime.UtcNow;

                NoAdviceReason = "the reroll pass failed: " + ex.Message;
            }

            return 0;
        }));
    }

    private object _plan;
    private Task _working;

    /// <summary>
    /// What every pass at this chain has enumerated so far, per remnant cell.
    ///
    /// **Why the totals rather than the gain.** The gain is a weighted mean, and means do not add.
    /// The sum and the weight do, so those are what accumulate and the mean is taken at the end of
    /// each pass. See ScoreRollOutcomes.
    ///
    /// **Total is a sum of differences, each against the standing score of its own round**, not a
    /// sum of rolled scores to be set against one standing later. Scores of the whole chain are
    /// tens of thousands and anything Gathering does not fingerprint can move them between rounds;
    /// subtracting the latest round's standing from a mean mostly gathered earlier then put that
    /// whole move into the gain. Measured: two remnants read +45,884 and +41,912 on one pass and
    /// -2,977 and -6,956 on the next, both shifted by the same 48,860.
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


    /// <summary>An impossible figure the pass had to floor, for the dump. Empty when none.</summary>
    private string _floored = "";

    /// <summary>What the last pass had to correct, if anything. See WeighRemnants.</summary>
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

    /// <summary>The roll count a running pass was last told to stop for, so one roll stops it once. See Consider.</summary>
    private int? _abandonedFor;

    private void Nothing(string why)
    {
        Fresh = false;
        // Swapped rather than cleared: the frame may be walking it to draw the labels.
        _adviceByCell = new Dictionary<(int X, int Y), RollAdvice>();
        Advised = null;
        NoAdviceReason = why;
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

    /// <summary>Whether each target is must take because of its reward's value, taken on the frame. See Insisted.MarkedForRewardValue.</summary>
    private static bool[] MarkedForRewardValue(PlanEnvironment env)
    {
        var said = new bool[env.Targets.Count];

        for (var i = 0; i < said.Length; i++)
            said[i] = Insisted.Here.MarkedForRewardValue(env.Targets[i].Grid);

        return said;
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
            // Local worth with any split-off share added, since a local slot pays both on the same waves. See
            // Weighing.SplitShareKeyOf.
            found[rune] = (Safe.Read(() => Runes.Weight(rune), 0f),
                Safe.Read(() => Runes.Scope(rune), "") ?? "",
                Safe.Read(() => Propagation.Locally(rune) +
                                Weighing.SplitShareKeysOfRune(rune).Sum(Propagation.Locally), 0f));
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
    /// <param name="floor">
    /// The map's floor on rune slots: no roll lands below it, so socket counts under it are left out, and ScoreRollOutcomes's
    /// division by the weight it gathers scales the rest up. See Rolls.MapSlotFloor.
    /// </param>
    /// <param name="grand">
    /// Whether the site is a Grand Expedition, whose rolls come up seven sockets far more often than any other site's.
    /// See Rolls.SocketsFor.
    /// </param>
    private static List<RolledShape> ShapesARollCouldProduce(Valuation valuation, int floor = 0, bool grand = true)
    {
        var shapes = new List<RolledShape>();

        if (valuation == null)
            return shapes;

        var socketShares = Rolls.SocketsFor(grand);

        foreach (var (sockets, socketShare) in socketShares)
        {
            if (socketShare <= 0f || sockets < floor)
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

        // **Each socket count carries the census's share of a roll, whatever its pins came to.** A socket count's
        // shapes are its pins with recipes, each at the count's share times the pin's; pins without recipes drop out
        // and duplicate or unmatched pins can push a count's total above or below its share. On a Craggy Peninsula site
        // (2026-10-05) the outcomes came to 7 sockets 23.3% and 8 13.5% of a roll, against 2.75% and 1.47% in
        // Rolls.SocketsFor, and those two counts carried most of every remnant's expected gain. So the shapes of each count
        // are rescaled to its share, keeping the pins' proportions within it; what they summed to before is kept for
        // the dump. See ShapeSharesSaid.
        var said = new StringBuilder();

        foreach (var bySockets in shapes.GroupBy(x => x.Sockets).OrderBy(g => g.Key))
        {
            var socketShare = socketShares.FirstOrDefault(x => x.Sockets == bySockets.Key).Share;
            var summed = bySockets.Sum(x => x.Share);

            said.Append(string.Create(CultureInfo.InvariantCulture,
                $" {bySockets.Key}: {bySockets.Count()} shape(s) summing to {summed:P2} against {socketShare:P2};"));

            if (summed <= 0d)
                continue;

            for (var i = 0; i < shapes.Count; i++)
            {
                if (shapes[i].Sockets == bySockets.Key)
                    shapes[i] = shapes[i] with { Share = shapes[i].Share * socketShare / summed };
            }
        }

        ShapeSharesSaid = said.Length == 0 ? "no shapes" : said.ToString().Trim();

        return shapes;
    }

    /// <summary>
    /// What the shapes of each socket count summed to before being rescaled to its share of a roll, for the dump. See
    /// ShapesARollCouldProduce.
    /// </summary>
    internal static string ShapeSharesSaid { get; private set; } = "not gathered yet";

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
        // Six runes carry a real magnitude - bond and opulent at 40%, time, power and death at 32%,
        // oath at 27% - and the other twenty-seven sit within two hundredths of 4%.
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
        // that way - see ScoreRollOutcomes. Every shape is walked; only arrangements are thinned.
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

    private void WeighRemnants(AutoExpeditionSettings settings, List<Target> scan, PlanEnvironment env,
        List<Vector2> chain, int settled, Insisted.Said[] marked, bool[] markedForReward,
        Dictionary<string, (float Weight, string Scope, float Local)> known, int asking,
        List<RolledShape> shapes)
    {
        var began = DateTime.UtcNow;

        var said = new Dictionary<(int X, int Y), RollAdvice>();

        Advised = null;
        NoAdviceReason = "";
        _runs++;

        // **The accumulation only survives while the thing it is about does.** See Gathering.
        var gathering = Gathering(env, chain);

        if (gathering != _gatheredFor)
        {
            _gathered = new Dictionary<(int X, int Y), (double Total, double Weight, int Round, bool More)>();
            _gatheredFor = gathering;
            RoundsWalked = 0;
            Stalled = false;
        }

        var rounds = 0;
        var unfinished = 0;
        var settledCells = 0;
        var fewest = int.MaxValue;
        var walkedBefore = RoundsWalked;

        _progressWalked = walkedBefore;
        _progressNeeded = RoundsToExhaust(shapes, settings);

        // **Plain worth, never Total, on every side of every roll comparison.** Total carries the synthetic weight for
        // holding a must take, which is not loot, and a roll is about loot. When a remnant's must take was removed with
        // the key, the two sides of one comparison disagreed about that weight and the whole of it - about 21,900 on a
        // Craggy Peninsula site, 2026-10-03 - was advised as a roll worth 21,987. See Planner.Plainly.
        var baseline = Planner.Plainly(env, chain);

        var worth = new List<(int Index, double Gain, bool Reached)>();

        // What else was running when this pass began, for the line that reports it: the passes run on one thread at
        // below-normal priority beside the solver's workers and the remnant order search. See _telling.
        var workersAtStart = Planning.WorkersRunning;
        var orderingAtStart = RemnantOrder.Searching;
        var remnantsToScreen = 0;
        var remnantsScreened = 0;

        for (var i = 0; i < env.Targets.Count; i++)
        {
            if (env.Targets[i].Kind == TargetKind.Remnant)
                remnantsToScreen++;
        }

        for (var i = 0; i < env.Targets.Count; i++)
        {
            var target = env.Targets[i];

            if (target.Kind != TargetKind.Remnant)
                continue;

            // Stopped by a roll, or by a newer question, at a remnant boundary: nothing is published, and the next pass
            // starts from the roll's state. See Consider.
            if (asking != Volatile.Read(ref _asked))
                return;

            // Through this round, by remnant, so the readout moves while a pass runs rather than only between them.
            // See Progress.
            RoundProgress = remnantsToScreen > 0 ? (float)remnantsScreened++ / remnantsToScreen : 0f;

            var why = RefusalReason(scan, env, chain, target, i, marked[i], markedForReward[i], settings);

            if (why != null)
            {
                RecordAdvice(said, target.Grid, 0d, why, refused: true);

                continue;
            }

            var reached = ChainCatches(env, chain, i);

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
                gain = had.Weight > 0d ? had.Total / had.Weight : 0d;

                settledCells++;
                rounds += had.Round;
                fewest = Math.Min(fewest, had.Round);
            }
            else
            {
                var at = i;
                var round = had.Round;
                var walked = ResultOfStage($"screening the remnant at ({target.Grid.X:0},{target.Grid.Y:0})",
                    () => ScoreRollOutcomes(env, chain, at, settings, known, shapes, round));

                _gathered[cell] = (had.Total + walked.Total - walked.Weight * walked.Standing,
                    had.Weight + walked.Weight, had.Round + 1, walked.More);

                rounds += had.Round + 1;
                unfinished += walked.More ? 1 : 0;
                fewest = Math.Min(fewest, had.Round + 1);

                var gathered = _gathered[cell];

                gain = gathered.Weight > 0d ? gathered.Total / gathered.Weight : 0d;
            }

            // **A remnant the chain misses cannot be made worse by rolling it, and the arithmetic
            // has to agree.** It contributes nothing as things stand - no reward collected, no rune
            // propagated - so with the chain held still the gain is nought. A negative here is not
            // advice, it is a fault: the two sides of the comparison disagreeing about whether the
            // chain reaches it.
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

            worth.Add((i, gain, reached));
        }

        if (worth.Count == 0)
        {
            if (asking != _asked)
                return;

            _adviceByCell = said;
            Advised = null;
            NoAdviceReason = "no remnant on this chain can be rolled";

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

        // **The exact figure decides, with nothing sampled on top.** A second pass re-solved the
        // chain under eight sampled rolls of the best few remnants and added the mean, crediting a
        // roll for a route it would make worth taking. Eight draws moved between passes, and the rolls
        // it drew were assembled from independent runes no recipe holds; it added nought in every dump
        // read (2026-10-05). So a roll that only pays by re-routing the chain is not credited.
        var best = worth[0].Index;
        var top = worth[0].Gain;

        // Everything screening looked at gets a line, so the overlay can explain a remnant it is
        // advising against as readily as one it is advising for.
        foreach (var (i, gain, _) in worth)
        {
            var at = i;

            RecordAdvice(said, env.Targets[i].Grid, gain,
                ResultOfStage($"explaining the remnant at ({env.Targets[i].Grid.X:0},{env.Targets[i].Grid.Y:0})",
                    () => AdviceReason(env, chain, at, gain, settings)));
        }

        // Dropped if the question has moved on - a zone change, or another solve - because
        // publishing now would put advice about the last dig site over the top of this one.
        if (asking != _asked)
            return;

        RoundProgress = 0f;

        // Published together so the readout cannot show a count from one pass against a bound from
        // another. See RoundsWalked.
        RoundsNeeded = RoundsToExhaust(shapes, settings);
        RoundsWalked = fewest == int.MaxValue ? 0 : fewest;
        Stalled = RoundsWalked <= walkedBefore;

        // Published as one finished object, so the frame never sees a half-built answer.
        _adviceByCell = said;

        // Measured beside the answer rather than instead of it. See RankingWithoutScoring.
        _withoutScoring = RankedWithoutScoring(env, worth);

        // **More than nothing is not a reason to spend a Liquid Verisium.**
        //
        // The gain is a difference between two sums over thousands of outcomes, so its last fraction
        // is arithmetic noise rather than signal. Anything above nought counted, which meant a site
        // where every roll was worthless still produced a recommendation - the one that happened to
        // sort first, printed as "ROLL ... Worth about 0 to the chain", because the readout rounds
        // to whole points and there was nothing there to round.
        //
        // A point is the smallest amount the readout can show. Below that the advice is claiming a
        // difference the player cannot see, about a consumable they cannot get back.
        var won = best >= 0 && top >= MinimumAdvisedGain;

        // **The continuous mode picks its own, on the frame, and would only be overwritten here.**
        // Its choice moves between passes and this one cannot; setting it would make the advice
        // jump to the pass's pick for a frame and then back. See Divert, which runs every frame.
        if (Mode(settings) != RerollSettings.Continuous)
        {
            Advised = won ? Of(env.Targets[best].Grid) : null;
            NoAdviceReason = won ? "" : "every roll on this chain is worth less than what it would replace";
        }

        _finished = DateTime.UtcNow;
        _telling = $"run {_runs}: {(DateTime.UtcNow - began).TotalMilliseconds:N0}ms over {worth.Count} remnants " +
                  $"({screened:N0}ms screening)" +
                  (settled > 0
                      ? $"; {settled} more already under a placed explosive, which is past advising on"
                      : "") +
                  // **How far the enumeration has got, because it is progressive now.** Each pass
                  // walks the next block of arrangements and adds to the last, so a figure is exact
                  // once nothing is left to walk and approximate until then. Without this the two
                  // are indistinguishable on screen. See ScoreRollOutcomes.
                  $"; {rounds} rounds gathered, {unfinished} remnants still have arrangements left" +
                  (settledCells > 0 ? $", {settledCells} already exact and re-used unchanged" : "") +
                  $"; beside it: {workersAtStart} solver worker(s) at the start and {Planning.WorkersRunning} at the end, " +
                  $"the remnant order search {(orderingAtStart ? "running" : "idle")} at the start and " +
                  $"{(RemnantOrder.Searching ? "running" : "idle")} at the end";
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
    /// The free ranking is the one thing a roll certainly costs: the weight of the runes the chain
    /// credits to this remnant, which vanish when it is replaced. Least destroyed first. It calls the
    /// same SolelySourcedWeight the advice is picked with and the explanation is worded from, so the
    /// three cannot drift apart.
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

    /// <summary>
    /// What a roll here certainly destroys: the weight of the runes this remnant is the chain's
    /// credited source of.
    ///
    /// The runes walked are the chosen combination's propagating runes - see ChosenPropagatingRunes -
    /// and each counts when the booking credits it to this remnant.
    ///
    /// **Asked of the booking, not of the other remnants' rune lists.** Walking the other remnants
    /// for a matching id answers "could something else supply this", and the question is "does
    /// anything else actually supply it". The scoring has already decided: only the first source of a
    /// rune is credited, and RuneTally.FirstSourced is that decision per remnant.
    ///
    /// The two come apart on a duplicate. Measured at (978,908): its own propagation is Opulent at 40
    /// on a reach of 1,351.9, and two later remnants list Opulent among their candidates while neither
    /// propagates it - (1138,984) takes Protective and (944,1139) takes Arcane. So the old test read
    /// "destroys 0.0", the advice read "Opulent already carried elsewhere", and rolling it would have
    /// thrown away the chain's only 40% rune.
    ///
    /// Falls back to the old test when no detailed pass has published a booking yet, or when a roll
    /// has invalidated it - see Planner.RuneTallyOutOfDate. It is the weaker answer, and the
    /// alternative is ranking every remnant at nought destroyed.
    /// </summary>
    private static float SolelySourcedWeight(PlanEnvironment env, int index)
    {
        var target = env.Targets[index];
        var booked = Credited(target.Grid);
        var destroyed = 0f;

        foreach (var (id, weight) in ChosenPropagatingRunes(target))
        {
            if (weight <= 0f || id == null)
                continue;

            if (booked == null
                    ? !BankedRunesReaching(env, index, id)
                    : booked.Contains(id, StringComparer.OrdinalIgnoreCase))
                destroyed += weight;
        }

        // **And what its held runes do to its own waves, which a roll loses just as surely.** A rune valued only on
        // its own remnant - Time, after 2026-10-02 - carries nothing forward, so it added nothing here, and a remnant
        // holding Time read as destroying 0 and was the first one advised for a roll. Its own effects are points; the
        // credited runes are percentages of the pool they pay on - Picked.Downstream, the pool PlanTarget.Best prices a
        // carried rune on at this link - so the own worth goes in as the percentage of that pool it equals.
        if (!Planner.RuneTallyOutOfDate &&
            Planner.Chosen.TryGetValue(((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y)), out var picked) &&
            picked.Own > 0f)
            destroyed += 100f * picked.Own / Math.Max(picked.Downstream, 1f);

        return destroyed;
    }

    /// <summary>
    /// The propagating runes of the combination the last detailed pass took at this remnant, with their
    /// weights, or PlanTarget.Runes when there is no such pass to read.
    ///
    /// PlanTarget.Runes is not the chosen combination's runes. It is the strongest candidate per
    /// propagating slot (Weighing.StrongestRunePerPropagatingSlot), the stand-in for a remnant whose
    /// rewards are unread. Where the chain takes a combination whose rune is not its slot's strongest,
    /// the two differ: at (1005,1035) the chain took Stone and Protective while the stand-in held Stone
    /// and Lightning, so a roll there was said to destroy 4.0 rather than 8.0 (2026-10-01).
    /// </summary>
    private static (string Id, float Weight)[] ChosenPropagatingRunes(PlanTarget target)
    {
        if (!Planner.RuneTallyOutOfDate &&
            Planner.Chosen.TryGetValue(((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y)),
                out var picked) &&
            picked.Carrying != null)
            return Planner.WeightsOfChosenRunes(target, picked.Carrying) ?? [];

        return target.Runes ?? [];
    }

    /// <summary>
    /// Every rune the last detailed pass had reaching this remnant from elsewhere, or empty when there
    /// is none to read. See Planner.RuneTally.Arriving.
    /// </summary>
    private static string[] Arriving(Vector2 grid)
    {
        if (Planner.RuneTallyOutOfDate)
            return [];

        return Planner.RuneTallyByRemnant.TryGetValue(
            ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var tally)
            ? tally.Arriving ?? []
            : [];
    }

    /// <summary>
    /// The runes the last detailed pass credited to the remnant on this cell, or null when there is no
    /// booking to read. See Planner.RuneTally.FirstSourced.
    /// </summary>
    private static string[] Credited(Vector2 grid)
    {
        if (Planner.RuneTallyOutOfDate)
            return null;

        return Planner.RuneTallyByRemnant.TryGetValue(
            ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var tally)
            ? tally.FirstSourced ?? []
            : null;
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
                     $"destroys {destroyed:N1} weight of runes credited to it and of its held runes' own effects, " +
                     $"enumerated {gain:+#,##0.0;-#,##0.0;0}" +
                     (i == byGain[0].Index ? "   <- the enumerated pick" : ""));
        }

        return string.Join("\n", text);
    }


    /// <summary>
    /// Pick the remnant to advise, every frame: the largest enumerated gain among the remnants that have a verdict and
    /// have not been rolled.
    ///
    /// **Ranked on the gain, not on what a roll destroys.** Only remnants with a verdict are eligible, and a verdict
    /// carries its gain, so the gain is there to rank on; it is seconds old between solves, which is no worse than the
    /// verdict that made the remnant eligible. Ranking on the destroyed rune weight instead (SolelySourcedWeight) stopped
    /// separating anything once most rune rows became the 1% quantity share: on a Craggy Peninsula site (2026-10-05) the
    /// seven remnants destroyed 1.0 to 1.9, and the advice rolled the one at 1.1 worth +121 while the one at 1.7 was
    /// worth +465. The destroyed weight is still worked out, for the dump.
    ///
    /// **Only remnants that already have a verdict are eligible.** A remnant the pass has not
    /// reached has no figure to show, and a blank beside an advised remnant reads as a nought rather
    /// than as an absence - a readout in this file has made exactly that mistake before.
    /// </summary>
    private void Divert(GameController gc, PlanEnvironment env, AutoExpeditionSettings settings,
        Scan scan, Vector2 site)
    {
        var said = _adviceByCell;

        if (env?.Targets == null || said.Count == 0)
            return;

        // **A remnant already rolled is off the list however good its verdict still looks.**
        //
        // A remnant takes one Liquid Verisium and no more, and the verdicts here outlive the roll -
        // that is the point of this mode - so the one just rolled still carries the figure that
        // advised rolling it. RefusalReason makes the same check when a pass runs; this is the same check
        // between passes, against the same reading.
        var live = Safe.Read(() => scan?.At(site), null);

        var margin = Math.Clamp(Safe.Read(() => settings.Solver.Reroll.DiversionMargin.Value, 0), 0, 100) / 100f;
        var committed = Math.Max(0, Safe.Read(() => settings.Solver.Reroll.CommittedWithin.Value, 0));
        var here = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        var bestAt = Vector2.Zero;
        var bestCost = float.MaxValue;
        var bestGain = double.MinValue;
        var standing = float.MaxValue;
        var standingGain = double.MinValue;

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

            // **Marked must take since the pass ran.** The pass refuses a must take, but its verdicts stand until the
            // next pass finishes, so a remnant marked with the key kept its roll line for that long. Asked here, every
            // tick, so the advice moves to the next remnant at once. See RefusalReason.
            if (Insisted.Here.Wants(target.Grid) || Insisted.Here.Avoids(target.Grid))
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
            if (verdict.Gain < MinimumAdvisedGain)
                continue;

            var cost = SolelySourcedWeight(env, i);

            if (Advised is { } was && Vector2.Distance(was.Grid, target.Grid) < 1f)
            {
                standing = cost;
                standingGain = verdict.Gain;
            }

            if (verdict.Gain <= bestGain)
                continue;

            bestCost = cost;
            bestGain = verdict.Gain;
            bestAt = target.Grid;
        }

        if (bestAt == Vector2.Zero)
        {
            Advised = null;
            Diverted = said.Count == 0
                ? "nothing on this chain has been scored yet"
                : $"no roll on this chain clears {MinimumAdvisedGain:0.#}, so none is advised";

            return;
        }

        // **Committed, so the advice holds.** Off by default - see CommittedWithin - because a rule
        // that refuses to move is harder to spot than one that moves too readily.
        if (Advised is { } held && standing < float.MaxValue && committed > 0 && here != Vector2.Zero &&
            Vector2.Distance(here, held.Grid) <= committed)
        {
            Diverted = $"holding ({held.Grid.X:0},{held.Grid.Y:0}) - within {committed} grid of it";

            return;
        }

        // A challenger has to beat the advised remnant's gain by the margin. At nought any larger gain moves it.
        if (Advised != null && standing < float.MaxValue && bestGain <= standingGain * (1d + margin))
        {
            Diverted = $"holding ({Advised.Grid.X:0},{Advised.Grid.Y:0}) at {standingGain:+#,##0.0;-#,##0.0;0} - " +
                       $"best challenger {bestGain:+#,##0.0;-#,##0.0;0} does not beat it by {margin:0%}";

            return;
        }

        var moved = Advised == null || Vector2.Distance(Advised.Grid, bestAt) >= 1f;

        Advised = Of(bestAt);
        NoAdviceReason = Advised == null ? "the best candidate has no verdict yet" : "";
        Diverted = $"{(moved ? "moved to" : "still")} ({bestAt.X:0},{bestAt.Y:0}) at " +
                   $"{bestGain:+#,##0.0;-#,##0.0;0}, destroying {bestCost:N1}" +
                   (standing < float.MaxValue && moved ? $", from {standingGain:+#,##0.0;-#,##0.0;0}" : "");
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

    /// <summary>How far this marker sits from the nearest link, in grid.</summary>
    private static float DistanceToChain(PlanEnvironment env, List<Vector2> chain, int index)
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
    /// The pass runs on a task, so a throw reaches no log and arrives as one line of NoAdviceReason. When
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

    private static void RecordAdvice(Dictionary<(int X, int Y), RollAdvice> said, Vector2 grid, double gain,
        string why, bool refused = false) =>
        said[((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y))] = new RollAdvice(grid, gain, why, refused);

    /// <summary>
    /// Why this remnant is not a candidate, or null when it is one.
    ///
    /// Read in the order a person would: the things that make rolling impossible first, then the
    /// one that makes it wrong.
    /// </summary>
    private static string RefusalReason(List<Target> scan, PlanEnvironment env,
        List<Vector2> chain, PlanTarget target, int index, Insisted.Said marked, bool markedForReward,
        AutoExpeditionSettings settings)
    {
        var live = Nearest(scan, target.Grid);

        if (live != null && live.Rerolled)
            return "already rolled - a remnant takes one Liquid Verisium and no more";

        if (Reroll.HoldsPlayerChosenCombination(live, settings))
            return "its combination is chosen and Overrule already chosen rewards is off";

        // **Must take vetoes the roll, and it is a veto rather than a term.** Marked for its reward's value, the
        // threshold has already said this reward is worth building the whole chain around, and replacing it with an
        // average one cannot be an improvement whatever the runes do. See Insisted.Automatic. Marked by hand, the
        // remnant is kept as it was marked, whatever a roll might be worth.
        if (target.Must || marked == Insisted.Said.Take)
            return markedForReward
                ? "must take - the reward is worth more than any roll could return"
                : "marked must take by hand - not rolled";

        if (marked == Insisted.Said.Avoid)
            return "marked must avoid";

        // **The environment's word for it, which can disagree with the mark.** A solve begun while the remnant was marked
        // avoid weighs it at its weight less a ceiling above every weight on the site - below nought, since a remnant's
        // own weight is floored at nought (Weighing.WeightOfTargetByKind) - until the next solve, whatever the mark says
        // meanwhile. A rolled outcome is weighed afresh without that, so the penalty read as the roll's gain: 20,000 and
        // more on a 7 socket remnant whose mark had been cycled past avoid, on a Grazed Prairie site (2026-10-06).
        if (target.Weight < 0f)
            return "avoided when the solve began";

        // **Being unreached is not a refusal.** The remnant is still weighed and listed; with the chain
        // held still a roll there is worth nought, so it is never advised.

        // **Being out of every link's range is a different question, and is a refusal.** The gate
        // asks whether ONE more explosive could catch this remnant - placed from a bomb already
        // down, near enough for its blast to cover it - not whether the chain catches it now. So a
        // remnant the route misses by a link stays in, and one several reaches away goes, because
        // advising a walk there is advising a walk on the chance of a good draw.
        //
        // Re-evaluated every pass, so exclusion is never permanent: the moment the route moves
        // near a remnant it becomes a candidate again. See RewardSettings.RollWithinReach.
        // From the detonator when nothing is down yet, because that is where the first link is
        // placed from. DistanceToChain answers with float.MaxValue on an empty chain, which would refuse the
        // whole site before a plan exists.
        var away = chain.Count > 0
            ? DistanceToChain(env, chain, index)
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
    private static bool ChainCatches(PlanEnvironment env, List<Vector2> chain, int index)
    {
        foreach (var at in chain)
        {
            if (Planner.Catches(env, at, env.Targets[index]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// How many of this site's remnants have been rolled, which is the cheap signal that a roll has
    /// happened since the last pass was dispatched. See Consider.
    ///
    /// Counted off the scan rather than taken from Scan.RollPending, which the tick consumes: in the
    /// continuous mode it is consumed only once a search is not in flight, so a flag is true for as
    /// long as nothing has looked and then false whether or not this had its turn. A count cannot be
    /// missed and cannot be double-counted.
    /// </summary>
    private static int Rolled(Scan scan, Vector2 site)
    {
        var count = 0;

        foreach (var target in Safe.Read(() => scan?.At(site), null) ?? new List<Target>())
        {
            if (target.Kind == TargetKind.Remnant && target.Rerolled)
                count++;
        }

        return count;
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
    /// and returns what it found; WeighRemnants adds that to the earlier rounds and takes the mean. Total
    /// and Weight are returned rather than a gain because means do not add and sums do. More says
    /// whether anything is left, and when it is false the figure is exact and will not be
    /// recomputed.
    ///
    /// No reward enters this at all, on either side of the comparison - see RolledRemnant.
    /// </summary>
    private static (double Total, double Weight, bool More, double Standing) ScoreRollOutcomes(
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

        // Every outcome this pass scored, for the dump's breakdown. See OutcomesSaid.
        var outcomes = new List<(double Score, double Each, int Sockets, int[] Positions, (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices)>();

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
        // **Recipes are drawn, not chosen, as positions are.** A roll lands on one of the recipes its
        // shape can reach and the player cannot pick another, so each outcome is the average over
        // those recipes, evenly, since the client's tables say which a shape reaches and not how
        // often. This scored them all as Choices of one remnant, which let the scoring take the best
        // one - crediting every outcome with its richest runes: on an Exhumed Ruins site (2026-10-05)
        // a roll of the chain's only source of Power read as even and was advised at +26. Recipes
        // passing the same runes score the same, so they are scored once, weighted by how many.
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
                // once, this pass takes the block at `round`, and WeighRemnants adds it to what earlier
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

                    var rolled = RolledRemnant(was, settings, known, shape, positions);
                    var offers = rolled.Choices ?? [];

                    foreach (var (first, alike) in RecipeGroupsWithSameRunes(offers))
                    {
                        targets[index] = LockedToRecipe(rolled, first);

                        var scored = Planner.Plainly(scoring, chain);
                        var share = each * alike / offers.Length;

                        total += share * scored;
                        weight += share;

                        outcomes.Add((scored, share, shape.Sockets, positions, targets[index].Choices));
                    }
                }
            }
        }

        // **The remnant as it stands, scored without its reward too.**
        //
        // The rolled side prices every recipe at nought - see RolledRemnant - so comparing it against
        // the plan's ordinary score would charge the roll for a reward it was never credited with,
        // and every remnant holding anything at all would read as a keep. Both sides are scored on
        // runes and sockets, and the reward is decided elsewhere, by the must-take threshold.
        //
        // Every combination is kept rather than locked to the richest one, matching the rolled side
        // again: there the planner picks between the recipes a roll could produce, so here it picks
        // between the ones the remnant already offers. Holding it to one would charge the roll for
        // a choice it did not take away.
        targets[index] = was with { Choices = ChoicesWithoutReward(was) };

        var standing = Planner.Plainly(scoring, chain);

        // Every round's outcomes together, since each round walks a different block of them: the last round alone
        // was all nine-socket shapes on an Exhumed Ruins site, the rarest of them. Started again at round nought.
        var cell = ((int)was.Grid.X, (int)was.Grid.Y);
        List<(double Score, double Each, int Sockets, int[] Positions, (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices)> all;

        lock (OutcomesGate)
        {
            if (round == 0 || !OutcomesAcrossRounds.TryGetValue(cell, out all))
                OutcomesAcrossRounds[cell] = all = new();

            all.AddRange(outcomes);
            OutcomesOfRemnant[cell] = OutcomesSaid(all, standing, round);
        }

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
    /// What the last pass of ScoreRollOutcomes found for each remnant, by its cell: the outcomes that scored highest against the
    /// remnant as it stands, and how much of the expectation came from outcomes above it and from those below. For the
    /// dump, which is the only place the outcomes behind one average can be seen. See Verdicts.
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), string> OutcomesOfRemnant = new();

    /// <summary>The outcomes of every round so far, by the remnant's cell. See OutcomesOfRemnant.</summary>
    private static readonly Dictionary<(int X, int Y), List<(double Score, double Each, int Sockets, int[] Positions, (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices)>>
        OutcomesAcrossRounds = new();

    private static readonly object OutcomesGate = new();

    /// <summary>The breakdown OutcomesOfRemnant holds, from one pass's outcomes. See ScoreRollOutcomes.</summary>
    private static string OutcomesSaid(List<(double Score, double Each, int Sockets, int[] Positions, (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)[] Choices)> outcomes,
        double standing, int round)
    {
        if (outcomes.Count == 0)
            return $"round {round}: no outcome scored";

        var weight = outcomes.Sum(x => x.Each);
        var above = outcomes.Where(x => x.Score > standing).ToList();
        var gained = above.Sum(x => x.Each * (x.Score - standing)) / Math.Max(1e-12, weight);
        var lost = outcomes.Where(x => x.Score < standing).Sum(x => x.Each * (x.Score - standing)) / Math.Max(1e-12, weight);
        var said = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"rounds 0 to {round}: {outcomes.Count} outcome(s) against {standing:N1} as it stands; {above.Sum(x => x.Each) / Math.Max(1e-12, weight):P1} of the chance scores above it, " +
            $"adding {gained:+#,##0.0;-#,##0.0;0} to the expectation, the rest {lost:+#,##0.0;-#,##0.0;0}"));

        // **Where the expectation comes from, by socket count**: each count's chance and what it adds to the expected
        // gain. The outcomes listed below are the extremes, each a fraction of a per cent, and say nothing about which
        // part of the distribution carries the average.
        said.Append("\n            by sockets:");

        foreach (var bySockets in outcomes.GroupBy(x => x.Sockets).OrderBy(g => g.Key))
            said.Append(string.Create(CultureInfo.InvariantCulture,
                $" {bySockets.Key}: {bySockets.Sum(x => x.Each) / Math.Max(1e-12, weight):P1} chance, " +
                $"{bySockets.Sum(x => x.Each * (x.Score - standing)) / Math.Max(1e-12, weight):+#,##0.0;-#,##0.0;0};"));

        said.Append("\n            highest - 'passing' lists the runes of the recipes the outcome offers, not the one taken:");

        foreach (var x in outcomes.OrderByDescending(x => x.Score).Take(6))
            said.Append(string.Create(CultureInfo.InvariantCulture,
                $"\n            {x.Score - standing:+#,##0.0;-#,##0.0;0} at {x.Each / Math.Max(1e-12, weight):P2}: {x.Sockets} sockets, passing slot(s) {string.Join(",", x.Positions)}, passing {string.Join("/", (x.Choices ?? []).Select(c => c.Runes is { Length: > 0 } r ? string.Join("+", r) : "-").Distinct().Take(6))}"));

        return said.ToString();
    }

    /// <summary>
    /// The recipes of a rolled remnant grouped by what they hold - the runes passed on and the runes kept - each group
    /// as its first recipe's index and how many there are. Recipes holding the same runes score the same, so a group is
    /// scored once. See ScoreRollOutcomes.
    /// </summary>
    private static List<(int First, int Alike)> RecipeGroupsWithSameRunes(
        (float Reward, float Carries, float Local, (string Id, float Worth)[] Locals, string[] Runes,
            (string Id, int Tag, float Percent, bool Flat)[] Spread)[] offers)
    {
        var groups = new List<(int First, int Alike)>();
        var keys = new Dictionary<int, int>();

        for (var c = 0; c < offers.Length; c++)
        {
            var hash = new HashCode();

            foreach (var rune in offers[c].Runes ?? [])
                hash.Add(rune, StringComparer.OrdinalIgnoreCase);

            hash.Add('|');

            foreach (var (id, worth) in offers[c].Locals ?? [])
            {
                hash.Add(id, StringComparer.OrdinalIgnoreCase);
                hash.Add(worth);
            }

            var key = hash.ToHashCode();

            if (keys.TryGetValue(key, out var at))
                groups[at] = (groups[at].First, groups[at].Alike + 1);
            else
            {
                keys[key] = groups.Count;
                groups.Add((c, 1));
            }
        }

        return groups;
    }

    /// <summary>
    /// A rolled remnant locked to one of its recipes, as a roll leaves it: that recipe the only choice, and its own
    /// effects and held lift with it. See ScoreRollOutcomes.
    /// </summary>
    private static PlanTarget LockedToRecipe(PlanTarget rolled, int c)
    {
        static T[] Only<T>(T[] all, int at) => all is { Length: > 0 } && at < all.Length ? [all[at]] : null;

        return rolled with
        {
            Choices = Only(rolled.Choices, c),
            OwnEffectsOfChoices = Only(rolled.OwnEffectsOfChoices, c),
            HeldLiftOfChoices = Only(rolled.HeldLiftOfChoices, c),
        };
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
    /// needs, and nothing else. See ScoreRollOutcomes.
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
    /// **One choice per recipe, each scored on its own by ScoreRollOutcomes**, since a roll lands on one and the player cannot
    /// pick between them. See LockedToRecipe.
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
    private static PlanTarget RolledRemnant(PlanTarget was, AutoExpeditionSettings settings,
        Dictionary<string, (float Weight, string Scope, float Local)> known,
        RolledShape shape, int[] positions)
    {
        var choices = new List<(float Reward, float Carries, float Local, (string Id, float Worth)[] Locals,
            string[] Runes, (string Id, int Tag, float Percent, bool Flat)[] Spread)>(shape.Recipes.Count);

        var weights = new List<(string Id, float Weight)>();

        // Each recipe's own effects, its held runes on their slots' waves. See Weighing.OwnEffectsOfRunes.
        var ownOfRecipes = new List<(int Tag, bool Count, float Factor, float WaveShare)[]>(shape.Recipes.Count);
        var createdOfRecipes = new List<(int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment)[]>(shape.Recipes.Count);

        foreach (var (recipe, _) in shape.Recipes)
        {
            var needs = Safe.Read(() => recipe.RuneCountRequired, 0);
            var propagating = new List<string>(positions.Length);
            var locals = new List<(string Id, float Worth)>();

            // Every rune the recipe holds, each once at its first slot, with that slot's share of the waves, for its own
            // effects. See Weighing.OwnEffectsOfRunes.
            var heldIds = new List<string>();
            var heldShares = new List<float>();

            for (var slot = 0; slot < needs && slot < shape.Sockets; slot++)
            {
                var at = slot;
                var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (!heldIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    heldIds.Add(id);
                    heldShares.Add(Propagation.WaveShareOfSlot(slot, needs));
                }

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
                    {
                        weights.Add((id, Runes.UnscopedWeight(id)));

                        // Its split-off shares with it, as Weighing.PropagatingRuneWeights lists them.
                        foreach (var split in Weighing.SplitShareKeysOfRune(id))
                            weights.Add((split, Runes.UnscopedWeight(split)));
                    }
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
            ownOfRecipes.Add(Weighing.OwnEffectsOfRunes(heldIds, heldShares));
            createdOfRecipes.Add(Weighing.CreatedOfRunes(heldIds, heldShares));
        }

        var rolled = new Target
        {
            Kind = TargetKind.Remnant,
            Sockets = shape.Sockets,
        };
        var own = Safe.Read(() => Weighing.WeightOfTarget(rolled, settings), 0f);

        return was with
        {
            Weight = own,

            // **The rolled remnant's waves, not the one it replaces.** Built as Planning builds a remnant's, from the
            // rolled socket count. Inherited, a three socket outcome was paid as a seven socket remnant on everything
            // that lands on its own waves - the runes reaching them, its own effects, the monsters Time and Death
            // add, Gaining Traction - and only its Weight shrank, so on a Craggy Peninsula site (2026-10-05) rolling
            // a seven socket remnant at the chain's end came out better than keeping it in 49% of outcomes, where
            // 4.4% of rolls reach seven sockets or more. See Weighing.PartsOfTarget and TractionByBeforeOfTarget.
            Waves = Safe.Read(() => Weighing.Waves(rolled), 0f),
            Parts = Safe.Read(() => Weighing.PartsOfTarget(rolled), null),
            TractionByBefore = Safe.Read(() => Weighing.TractionByBeforeOfTarget(rolled,
                Safe.Read(() => settings.Debug.GainingTraction.Value, true)), null),
            WaveCount = shape.Sockets,
            Runes = weights.ToArray(),
            PropagatingRuneWeights = weights.ToArray(),
            Choices = choices.ToArray(),

            // Per combination, so it belongs to the real remnant's combinations and not to these. Left inherited it
            // indexed another recipe's wave change for each of these. Nought here: the rolled side prices no reward,
            // which is where a recipe's extra waves are counted. See PlanTarget.TractionOfChoice.
            MagicAndRareWavesOfChoices = null,

            // These recipes' own runes, which a roll can land on as well as away from. See PlanTarget.OwnOfChoice.
            OwnEffectsOfChoices = ownOfRecipes.Any(x => x != null) ? ownOfRecipes.ToArray() : null,

            // And the empowering lift each holds. See PlanTarget.HeldLiftOfChoice.
            HeldLiftOfChoices = HeldLiftOfRolledChoices(choices),

            // Per combination as well, and the real remnant's: inherited, it gave each of these the wave shares of
            // another recipe's runes. Null reads as every wave. See PlanTarget.WaveShareOfCarried.
            CarriedWaveSharesOfChoices = null,
            SlotRunesOfChoices = null,

            // And the monsters each adds, as its own effects. See PlanTarget.CreatedByOwnEffects.
            CreatedOfChoices = createdOfRecipes.Any(x => x != null) ? createdOfRecipes.ToArray() : null,
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
    private static string AdviceReason(PlanEnvironment env, List<Vector2> chain, int index, double gain,
        AutoExpeditionSettings settings)
    {
        var target = env.Targets[index];

        // **Three groups, not two, because "not credited here" covers two different situations.** A
        // rune the chosen combination propagates is either credited to this remnant, already arriving
        // from elsewhere, or neither - propagated and paying nothing. Splitting on the booking alone
        // called the third kind "credited to another remnant". See SolelySourcedWeight.
        var sole = new List<string>();
        var upstream = new List<string>();
        var idle = new List<string>();

        var booked = Credited(target.Grid);
        var arriving = Arriving(target.Grid);

        foreach (var (id, weight) in ChosenPropagatingRunes(target))
        {
            if (weight <= 0f || id == null)
                continue;

            if (booked == null)
            {
                // No booking to read, so the weaker test: does any other remnant offer it.
                (BankedRunesReaching(env, index, id) ? upstream : sole).Add(id);

                continue;
            }

            if (booked.Contains(id, StringComparer.OrdinalIgnoreCase))
                sole.Add(id);
            else if (arriving.Contains(id, StringComparer.OrdinalIgnoreCase))
                upstream.Add(id);
            else
                idle.Add(id);
        }

        var said = new List<string>();

        // **Says how many remnants that claim was checked against.** "Only source of Volcanic" is
        // a statement about every other remnant in the environment, and when it looks wrong the
        // first question is which remnants it actually looked at - a remnant the chain cannot reach
        // is still in the list, one streamed out is not.
        //
        // Worded as the chain crediting it once a booking has been read, because that is the weaker
        // and truer claim: another remnant may be able to offer the same rune, and the point is that
        // on the chain as planned nothing else is credited with it.
        if (sole.Count > 0)
        {
            said.Add(booked == null
                ? $"only source of {Names(sole)} among {Remnants(env)} remnants"
                : $"the chain's credited source of {Names(sole)}");
        }

        if (upstream.Count > 0)
        {
            said.Add(booked == null
                ? $"{Names(upstream)} already carried elsewhere"
                : $"{Names(upstream)} already arrives from an earlier link, so a roll does not lose it");
        }

        if (idle.Count > 0)
            said.Add($"propagates {Names(idle)}, which the chain credits to no remnant");

        // **Which duplicate to roll is not the obvious one.** Where two remnants carry the same
        // rune, rolling the EARLIER one keeps the rune - the later copy still supplies it - and puts
        // the new one on the link with the longest reach. Rolling the later one gains a rune that
        // reaches almost nothing.
        //
        // Asked only of the runes that do arrive from elsewhere. A rune nothing else sources has no
        // copy to be the earlier of, and this said so about two of them.
        if (upstream.Count > 0 && Earliest(env, chain, index, upstream))
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
        // more - see ScoreRollOutcomes - so this is not a term in the number beside it. It is here because
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
            ordinary = Math.Max(ordinary, Planner.RunesInLocals(choice.Locals));

        if (ordinary > 0)
            said.Add($"{ordinary} ordinary slot{(ordinary == 1 ? "" : "s")} on its own waves");

        // The chain gets nothing from this remnant as it stands, and the figure holds the chain still.
        if (!ChainCatches(env, chain, index))
            said.Add("the chain does not reach it as planned, so rolling it changes nothing the plan collects");

        var why = said.Count > 0 ? string.Join("; ", said) : "nothing carried forward";

        // **No verdict word here, and none of the ranking either.** This said "ROLL:" or "KEEP:"
        // from the pass's own pick, and both readers of it state the verdict themselves from
        // Advising - so one line came out as "ROLL -358.3 ... - KEEP: only source of Rebirth", both
        // verdicts at once about one remnant, because in the continuous mode the pass ranks on the
        // enumerated gain while Divert picks on what a roll destroys.
        //
        // Nor can it be fixed by asking Advising here: this runs on the background pass, where Advised
        // is still the previous answer. The reason is what is true of this remnant; which remnant is
        // advised is the reader's to say, at the moment of reading. See Advising.
        return gain > 0d
            ? $"worth about {gain:N0} to the chain. {why}"
            : $"a roll here costs about {-gain:N0}. {why}";
    }

    /// <summary>How many remnants the sole-source claim was tested against. See AdviceReason.</summary>
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
