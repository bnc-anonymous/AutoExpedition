using ExileCore2;
using System;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// Solves the site while you walk towards it, and draws nothing until you ask.
///
/// **This replaces warming the router, which was the right instinct aimed by guesswork.** The
/// warm-up flooded ground in advance so the first press would not have to; the difficulty was
/// knowing WHICH ground, and two attempts got it wrong - marker centres, then a ring around markers
/// - because the cells a search floods are its band corners and its candidates. Measured on a real
/// site: the warm-up took 54 floods of which a solve used 20, while that solve took 270 of its own,
/// and 27,005 of its questions went unanswered for want of routing budget.
///
/// A solve floods exactly the cells it needs, by construction. So rather than approximate the
/// search's appetite, the search is simply run. It cannot aim at the wrong cells, and it brings back
/// something the warm-up never could: press the key and the floor is already the best chain the site
/// has given up, rather than nothing.
///
/// **Started early rather than when the site stops revealing itself**, and that is deliberate. The
/// terrain is loaded from the moment the map is; only the markers stream in. So a pass on half the
/// content still floods real ground at real band corners, and those floods are permanent - the plan
/// it produces may be thrown away, the routing never is. Waiting for a quiet scan would mean waiting
/// until you had arrived, which is the whole window this exists to use.
///
/// **And repeated short passes beat one long one**, because the router's allowance is per solve:
/// Route.Renew resets the spent clock every time, so ten rehearsals on the way in is ten allowances
/// of flooding against one. On a site that refused five questions in six for want of budget, that is
/// the difference that matters.
///
/// Nothing it finds can make things worse. Each pass seeds from the standing chain and cannot
/// publish below it, and Kept holds the best across all of them - so markers appearing as you
/// approach make the work compound rather than restart. That is the failure the equivalent feature
/// in RuneHighlighter has, where a changed layout kills the search and starts it cold, and it is why
/// that one ships switched off.
/// </summary>
internal sealed class Rehearsal
{
    /// <summary>How long to leave between passes.</summary>
    private const double Between = 500d;

    /// <summary>The fewest markers worth solving for. Below this there is no problem to solve.</summary>
    private const int Enough = 2;

    private DateTime _next = DateTime.MinValue;
    private uint _area;
    private Vector2 _site;
    private int _passes;
    private int _markers;
    private int _shape;
    private DateTime _revealed = DateTime.MinValue;

    /// <summary>When this dig site was first seen, which bounds the wait for prices. See Tick.</summary>
    private DateTime _first = DateTime.MinValue;

    /// <summary>
    /// When the dig site now in front of the player was first seen, for anything that wants to
    /// date an event against the approach rather than against the clock.
    ///
    /// One definition, here, because this is the class that already decides when a site becomes a
    /// new problem - and a second answer to "when did this start" is how two timelines come to
    /// disagree about the same moment. See Planning's run timeline.
    /// </summary>
    public static DateTime Sighted { get; private set; } = DateTime.MinValue;
    private double _best;
    private bool _done;

    /// <summary>Where the key had last been pressed, as the dispatch saw it. See Describe.</summary>
    private Vector2 _pressed;

    /// <summary>Whether the last pass counted as a re-solve under a plan. See Describe.</summary>
    private bool _asked;

    /// <summary>The site the last pass ran for, to compare against where the key was pressed.</summary>
    private Vector2 _where;

    /// <summary>Whether the player has been inside this site yet. See Tick.</summary>
    private bool _inside;

    /// <summary>The most of this site whose data has ever been read. See Tick.</summary>
    private int _read;

    /// <summary>The most of this site's remnants that have ever been readable at once. See Tick.</summary>
    private int _readable;

    /// <summary>
    /// Whether the presolve has settled here, and over how many passes, for the readout.
    ///
    /// **A line that only exists while something is happening cannot say that nothing needs to.**
    /// The countdown vanishes the moment a pass ends, so the difference between "settled, and this
    /// is the site's answer" and "has not started" was invisible - and those two want completely
    /// different responses from whoever is reading. Kept for the same reason the preflood line is.
    /// </summary>
    public static bool Settled { get; private set; }

    /// <summary>
    /// Whether a presolve pass has ended with this site's content unchanged since it began - what Settled means,
    /// without the presolve stopping. The test is the settle test: the pass was not cut short or reopened by a
    /// change, and it either published no improvement or ended at least Quiet after the content last changed. An
    /// improving chain does not hold it back once the content has been quiet that long, which a whole pass
    /// almost always has; it is a statement about the content, not about the chain.
    ///
    /// **Settled is never set in the continuous reroll mode**, because there the pass that would have settled the
    /// site is followed by another instead. So nothing could tell a site still being scouted, where every pass is
    /// cut short by new markers, from one whose content has stopped arriving - and the reroll advice ran from the
    /// first rough plan on, a pass of up to 2.8 seconds and 262MB each, advising on a site still being read.
    /// Cleared wherever Settled is. See Rolling.Consider.
    /// </summary>
    public static bool ContentSettled { get; private set; }

    /// <summary>How many presolve passes this site has had. See Settled.</summary>
    public static int Passes { get; private set; }

    /// <summary>What this site's rehearsal has managed, for the dump.</summary>
    public string Describe() =>
        _passes == 0
            ? "has not rehearsed here"
            : Recounted() + $"{_passes} passes over {PassesSpanMs():N0}ms, best {_best:N1}, " +
              $"last pass drew {_drew} improvements and " +
              $"{(_moved ? "moved" : "did NOT move")} the score" +
              (_done ? " (finished - nothing left to learn)" : "") +
              (_reopened ? " (re-opened - waiting on a pass against the new site)" : "") +
              // The continuous reroll mode, which is what keeps this from finishing. See Continuing.
              (Continuing
                  ? $" (continuous reroll mode: solving on, {_rerollsLeft} remnant(s) still to roll, draw {Planning.Draws})"
                  : _stoppedByPlayer
                      ? $" (continuous reroll mode: stopped by {_stoppedBy})"
                      : "") +
              // How many passes this did not dispatch because a roll's own solve had already
              // answered the site. Two solves per roll was five seconds of it. See Tick.
              (_rollsAnswered > 0
                  ? $" ({_rollsAnswered} pass(es) not dispatched - a roll's own solve answered them)"
                  : "") +
              // **The fingerprint, because it is what a reroll has to move and nothing showed it.**
              //
              // A roll changes what a remnant offers, which changes this hash, which clears the done
              // flag and wakes the rehearsal. When a roll failed to wake it, the only way to tell
              // whether the hash had moved was to add the hash - so here it is, beside the markers and
              // the links it is mixed with. Two dumps either side of a roll say at once whether the
              // detection or the response is at fault. See Fingerprint.
              $"; site fingerprint {_shape} over {_markers} markers, " +
              $"read {_read}, readable {_readable}" +

              // **Whether the plugin thinks a plan was ASKED for here, which decides the window.**
              //
              // A site that changes under a plan somebody pressed for gets a one second re-solve,
              // because somebody is waiting; a presolve gets the full window, because nobody is. The
              // switch between them is one distance comparison against the site the key was last
              // pressed at - and when a reroll produced the long window instead of the short one,
              // nothing in the dump said which way that comparison had gone or why.
              //
              // Both positions, because "asked False" on its own does not say whether the ask was never
              // recorded or was recorded somewhere else. See Planning.Showing and ShownAt.
              //
              // **Three positions, because two of them were answers to different moments.** The ask
              // was latched when the pass was dispatched and ShownAt was read live at dump time, so a
              // line reading "asked False (pressed at 381,777, site now 381,777)" said the comparison
              // was between two identical positions and had still come out false - which cannot
              // happen, and did not: ShownAt had simply changed between the dispatch and the dump.
              // The latched one is what the comparison actually saw.
              $"; asked {_asked} (comparing {(_pressed == Vector2.Zero ? "nowhere" : $"{_pressed.X:0},{_pressed.Y:0}")} " +
              $"against {(_where == Vector2.Zero ? "nowhere" : $"{_where.X:0},{_where.Y:0}")}" +
              $", pressed at now {(Planning.ShownAt == Vector2.Zero ? "nowhere" : $"{Planning.ShownAt.X:0},{Planning.ShownAt.Y:0}")})" +
              "";

    /// <summary>
    /// Whether the timeline holds a solve that improved on a site that had not changed under it.
    ///
    /// **This is the reading somebody is going to take, so the dump takes it rather than leaving
    /// it to arithmetic across a dozen lines.** A presolve settling low and the next pass opening
    /// several times higher is the complaint; whether it is a fault depends entirely on whether
    /// the markers, the loaded count and the readable remnants moved in between, and comparing
    /// three columns across a dozen rows by eye is how a wrong answer gets read off a right dump.
    ///
    /// Read off Planning's run timeline, which is the one record of what every solve did - this
    /// kept a second copy of it and the two could disagree. Silent when there is nothing to say,
    /// so it does not become a line that is always there.
    /// </summary>
    private static string Recounted()
    {
        var journey = Planning.Journey;

        for (var i = 1; i < journey.Count; i++)
        {
            var before = journey[i - 1];
            var now = journey[i];

            // **A score that fell, and whether the table explains it.** The floor forbids a solve
            // publishing below the chain it was seeded with, so a fall can only be the scoring
            // changing - and a reader watching the number on screen has no way to tell an edited
            // weight from a search that went backwards.
            // Not when the later run had no floor at all: a deleted plan or a cold press starts from nothing on
            // purpose, and it falling below what came before is what it was asked to risk.
            if (now.After < before.After - 0.0001d && now.Before > 0.0001d)
                return now.Cause == Planning.MustTakeChangedCause
                    ? $"RE-AIMED - run {now.Number} published {now.After:N1} under run {before.Number}'s " +
                      $"{before.After:N1} after a must take was marked or cleared, which changes what the solver " +
                      "maximises - the chain before it may not hold what is now required. "
                    : now.Table != before.Table
                    ? $"RE-SCORED - run {now.Number} fell to {now.After:N1} from {before.After:N1} " +
                      $"across a reference table edit (rev {before.Table} to {now.Table}), so the " +
                      "two numbers are not comparable. "
                    : $"SCORE FELL - run {now.Number} published {now.After:N1} under run " +
                      $"{before.Number}'s {before.After:N1} with the table unmoved, which the " +
                      "floor is supposed to forbid. ";

            // A solve that was stopped concluded nothing, and one that ended at nothing measured
            // nothing - neither is a floor a later run can be said to have beaten.
            if (before.Ended.StartsWith("stopped", StringComparison.Ordinal) ||
                before.After <= 0.0001d)
                continue;

            if (now.Markers != before.Markers || now.Loaded != before.Loaded ||
                now.Readable != before.Readable || now.Links != before.Links ||
                now.Live != before.Live || now.Table != before.Table)
                continue;

            if (now.After > before.After + 0.0001d)
                return $"SEARCH GAP - run {now.Number} reached {now.After:N1} where run " +
                       $"{before.Number} ran its window out at {before.After:N1} on an identical " +
                       $"site, {100d * (now.After - before.After) / Math.Max(1d, before.After):0.#}% " +
                       "left on the table. ";
        }

        return "";
    }

    /// <summary>
    /// What the last pass managed, because a countdown draining to nothing looks identical whether
    /// the search is working or idling.
    ///
    /// A pass that draws no improvements is the floor doing its job - it is seeded with the best
    /// chain known and cannot publish below it - and it spends its whole window proving there is
    /// nothing better. That is the right behaviour and the wrong thing to watch, so the number says
    /// which of the two you are looking at.
    /// </summary>
    private int _drew;

    private bool _moved;

    /// <summary>What the plan was worth when the pass in flight was launched. See _moved.</summary>
    private double _before;

    /// <summary>Whether that pass was stopped early rather than allowed to finish. See the stop.</summary>
    private bool _cut;

    /// <summary>
    /// Whether markers that are not remnants or relics have arrived since the pass in flight began. They do not stop it
    /// at once; the pass takes them in when it ends, or is stopped for them once it has run MarkersWaitMs. See Tick.
    /// </summary>
    private bool _markersWaiting;

    /// <summary>Where the remnants and relics stood when last looked at, as one number. See Tick.</summary>
    private int _stops;

    /// <summary>
    /// How long a pass runs before markers that arrived under it stop it. Chosen: longer than the remnant order search
    /// takes in game on a Grand site, about 8 s, so a pass started while scouting can finish it. See MarkersWaiting.
    /// </summary>
    private const double MarkersWaitMs = 10000d;

    /// <summary>
    /// When the pass in flight was dispatched, and how long the last finished one took.
    ///
    /// **Because "the presolve only ran for a second" could not be checked.** The line said how many
    /// passes and what they found and nothing about how long they were given - so a pass running on
    /// the one second window meant for a press, rather than the full presolve window, looked exactly
    /// like one running on the right window and finishing early. See Describe, which prints both
    /// this and the asked flag that chooses between the two windows.
    /// </summary>
    private DateTime _launched = DateTime.MinValue;

    /// <summary>
    /// When this site's first pass began and when it settled, for the dump's span of the passes. The span used to be the
    /// last pass alone and was set only when the site settled, so in the continuous reroll mode, which never settles,
    /// it read "14 passes over 0ms". See PassesSpanMs.
    /// </summary>
    private DateTime _firstLaunched = DateTime.MinValue;

    /// <summary>See _firstLaunched.</summary>
    private DateTime _settledAt = DateTime.MinValue;

    /// <summary>From the first pass to settling, or to now while the site has not settled.</summary>
    private double PassesSpanMs() =>
        _firstLaunched == DateTime.MinValue
            ? 0d
            : ((_done && _settledAt != DateTime.MinValue ? _settledAt : DateTime.UtcNow) - _firstLaunched).TotalMilliseconds;

    /// <summary>
    /// Whether the site has changed since the pass whose result the settle test is about to read.
    ///
    /// **Without it, a settled rehearsal could never be re-opened.** Every trigger here clears
    /// _done, which is meant to start the search thinking again - but the settle test below then
    /// runs on the SAME tick, reads planning.Found, and finds the count from the pass that ended
    /// before the change, because a new one has not started to overwrite it. A pass that concluded
    /// by publishing nothing therefore concluded again, immediately, on evidence about a different
    /// site. Walking into a dig site with markers streaming in, the presolve stayed finished the
    /// whole way and nothing restarted it.
    ///
    /// So the evidence is marked stale when the question changes, and the settle test declines to
    /// read it until a pass has actually run against the new one. See Reopened.
    /// </summary>
    private bool _reopened;


    /// <summary>A solve for a roll is under way, so this does not ask for a second. See Tick.</summary>
    private bool _solvingARoll;

    /// <summary>How many passes that has saved at this dig site, for the dump.</summary>
    private int _rollsAnswered;

    /// <summary>
    /// Told that a solve has been dispatched because a remnant was rolled.
    ///
    /// Called by the caller that dispatches it, and only when it started. See Tick for why.
    ///
    /// **A roll also turns the continuous reroll mode back on after the action key stopped it.** The key
    /// means "that plan will do"; rolling another remnant afterwards changes the site that plan was
    /// for, so the reason to stop has gone. See StopContinuing.
    ///
    /// **And a roll's solve counts as asked for**, as the action key's does. Continuing needs a pass of the presolve's
    /// own or an ask, and a roll's solve takes the place of the pass: after a reload on a Frigid Bluffs site
    /// (2026-10-04) a roll 0.7 s after the site was first seen solved for 28 s, and nothing solved again until the
    /// action key was pressed nearly two minutes later. See Continuing.
    /// </summary>
    public void RemnantWasRolled()
    {
        _solvingARoll = true;
        _stoppedByPlayer = false;
        _continueAsked = true;
    }

    /// <summary>
    /// Whether continuous solving is keeping the presolve going at this site, as of the last tick with no search in
    /// flight.
    ///
    /// True while the site has had at least one pass, or the action key has asked for a solve here, the last plan is not
    /// proved optimal, no explosive is down, and the action key has not stopped it. The action key over the placement
    /// button starts it again. Needs the presolve switched on, since this is the presolve continuing rather than a
    /// solver of its own.
    ///
    /// **Asked for counts as a pass.** A roll's own solve takes the place of the presolve's pass, and outside the last
    /// roll of the continuous reroll mode it settles the site - so after a reload on a site whose remnants were rolled,
    /// the presolve had no pass of its own, and the action key over the placement button ran one press and stopped.
    ///
    /// **Not tied to rerolling any more.** It used to run only in the Continuous reroll mode and only while a remnant
    /// was still unrolled, so once the rolls were done nothing could search further except single presses. Each pass
    /// carries every worker's chain on to the next, so passes add up to one search. See WorkerChains.
    ///
    /// **Not once an explosive is down**, because a solve then interrupts placing. See todo section 0.
    /// </summary>
    public bool Continuing { get; private set; }

    /// <summary>
    /// Stops the continuous reroll mode at this site until a remnant is rolled, the site changes, or the action
    /// key starts a solve here. See ResumeContinuing.
    ///
    /// Also called by both delete buttons, so deleting the plan does not start a search by itself.
    ///
    /// Called when the action key stops a search, or is pressed between two continuous passes. A pass
    /// already in flight is the caller's to stop.
    /// </summary>
    public void StopContinuing(string by = "the action key")
    {
        _stoppedByPlayer = true;
        _stoppedBy = by;
        _continueAsked = false;
        Continuing = false;
    }

    /// <summary>
    /// Lets the continuous reroll mode run again at this site after the action key stopped it.
    ///
    /// Called when the action key starts a solve. The stop meant "that plan will do"; asking for a new plan says
    /// the opposite. Without this the stop outlived the press: a site stopped once stayed stopped through a
    /// fresh press, so the solve the press started ended, the reroll advice ran, and nothing searched while the
    /// remnants were rolled.
    /// </summary>
    public void ResumeContinuing()
    {
        _stoppedByPlayer = false;
        _continueAsked = true;
    }

    /// <summary>Whether the action key has stopped the continuous reroll mode here. See StopContinuing.</summary>
    private bool _stoppedByPlayer;

    /// <summary>
    /// Whether the action key has asked for a solve at this site since it was last stopped, which lets continuous
    /// solving run although the presolve has had no pass of its own here. See Continuing.
    /// </summary>
    private bool _continueAsked;

    /// <summary>What stopped it, for the presolve readout: the action key or a delete button.</summary>
    private string _stoppedBy = "the action key";

    /// <summary>
    /// Whether the next pass is one the presolve would not have run, so it takes a new draw. See Tick.
    /// </summary>
    private bool _keptGoing;

    /// <summary>
    /// Whether one full pass is owed after the last roll in the continuous reroll mode. See Tick, where
    /// the roll's own solve is counted.
    /// </summary>
    private bool _finalPass;

    /// <summary>How many remnants at this site were unrolled and unspent at the last count. See RerollsLeft.</summary>
    private int _rerollsLeft;

    /// <summary>Marks the next pass as one the continuous reroll mode asked for. See Continuing.</summary>
    private void KeepGoing()
    {
        if (!_keptGoing)
            Reopening(_rerollsLeft > 0
                ? $"continuous solving, {_rerollsLeft} remnant(s) still to roll"
                : "continuous solving");

        _keptGoing = true;
    }

    /// <summary>
    /// The remnants at this site that could still take a Liquid Verisium: not rolled, and not spent.
    ///
    /// Rolled is asked two ways because the live flag needs the entity loaded. WasRolled is the same
    /// flag remembered by the scan, so a remnant rolled and then streamed out does not count as
    /// rollable again. See Scan's roll detection.
    /// </summary>
    private static int RerollsLeft(System.Collections.Generic.List<Target> content)
    {
        var left = 0;

        foreach (var target in content)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            if (Safe.Read(() => target.Spent, false) || Safe.Read(() => target.Rerolled, false) ||
                target.WasRolled)
                continue;

            left++;
        }

        return left;
    }

    /// <summary>Why the question was last re-opened, and how many times since the last pass.</summary>
    private string _why;

    private int _whys;

    /// <summary>
    /// What re-opened the question, worded for the solve this is about to dispatch.
    ///
    /// **The site it ran against is no longer latched here.** Every one of those - the markers, the
    /// loaded count, the readable remnants, the link count, the table revision - is read by
    /// Planning when it opens the run, which is the same tick and the same question. Two copies of
    /// it is how the two timelines came to disagree, and this one lost: it filed a pass that
    /// published 6,928 as having ended at 1,669.6, because it read the score at whatever later tick
    /// it got round to rather than when the pass finished. See Planning.Run.
    /// </summary>
    private string _ran;

    /// <summary>
    /// Notes what has changed about the site, for the pass that will answer it.
    ///
    /// The last reason rather than all of them, with a count beside it: several can fire between
    /// two passes and a line that joined them grows without bound while you walk.
    /// </summary>
    private void Reopening(string why)
    {
        _why = why;
        _whys++;
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash == _area)
            return;

        _area = areaHash;
        _site = Vector2.Zero;
        _inside = false;
        _read = 0;
        _readable = 0;
        Settled = false;
        ContentSettled = false;
        Passes = 0;
        _passes = 0;
        _markers = 0;
        _shape = 0;
        _solvingARoll = false;
        _rollsAnswered = 0;
        _best = 0d;
        _done = false;
        _revealed = DateTime.MinValue;
        _first = DateTime.MinValue;
        Sighted = DateTime.MinValue;
        _why = null;
        _whys = 0;
        _ran = null;
        _stoppedByPlayer = false;
        _continueAsked = false;
        _keptGoing = false;
        _finalPass = false;
        Continuing = false;
    }

    public void Tick(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation, Planning planning, Placement placement,
        CancellationToken zone)
    {
        if (!settings.Solver.Presolve.Enable)
            return;

        // Never over the top of the mouse. A press takes precedence over a rehearsal in every case,
        // which is what Planning.ShownAt records.
        if (placement.Busy)
            return;

        var site = Detonator.DetonatorGridPosition(gc);

        if (site == Vector2.Zero || Detonator.ExplosivesInHand(gc) <= 0)
            return;

        // A new dig site in the same area is a new problem. The counters follow the site rather than
        // the zone, since a map holds two and finishing one says nothing about the other.
        if (Vector2.Distance(site, _site) >= 1f)
        {
            _site = site;
            _inside = false;
            _reopened = false;
            _read = 0;
            _readable = 0;
            Settled = false;
            ContentSettled = false;
            Passes = 0;
            _first = DateTime.UtcNow;
            Sighted = _first;
            _passes = 0;
            _markers = 0;
            _shape = 0;
            _best = 0d;
            _done = false;
            _revealed = DateTime.UtcNow;
            _why = null;
            _whys = 0;
            _ran = null;
            _stoppedByPlayer = false;
            _continueAsked = false;
            _keptGoing = false;
            _finalPass = false;
            Continuing = false;
            }

        // **Arriving is a change to the problem, and it is the one change nothing could see.**
        //
        // Every other trigger here is something about the site moving - a marker revealed, a remnant
        // rolled, a price arriving - and walking closer moves none of them. So a presolve that ran
        // from the edge of the site, plateaued, and declared "nothing left to learn" was never asked
        // again, however far in you then walked. Measured on an ordinary site: the plan drawn on
        // arrival was worth 1,854 and a one second re-solve, same environment and same 101 markers,
        // reached 4,311.
        //
        // What a pass from out there is actually saying is that it has nothing left to learn FROM
        // OUT THERE, which is not the same claim. Entities stream in as you approach and the scan
        // reads what has streamed, so a site solved from the boundary is solved on a subset of
        // itself - and the subset it missed here was worth more than twice everything it found.
        //
        // So crossing into the site re-opens the question once. **Only that, and not a ban on
        // settling while outside** - that was the first attempt and it meant a presolve run from
        // out of range never finished: every pass ended, none of them counted, and the next began
        // immediately, for as long as you stood there. A pass is entitled to conclude that it has
        // nothing more to learn from where it is; what it is not entitled to do is carry that
        // conclusion across a change in what it can see. See _read.
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var inside = player != Vector2.Zero &&
                     Vector2.Distance(player, site) <= Detonator.BaseReach(gc);

        if (inside && !_inside)
        {
            _inside = true;
            _done = false;
            _revealed = DateTime.UtcNow;
            _reopened = true;
            Reopening("walked into the site");

            if (planning.Searching && Planning.Rehearsing && HadItsTurn(settings) &&
                planning.Stop("the presolve, because the site changed under it"))
                _cut = true;
        }

        var content = scan.At(site);
        var here = content.Count;

        // **How many explosives the site has is part of what the site IS.**
        //
        // It is assumed to be five until the detonator panel loads - see Detonator.ExplosivesToPlanFor, which
        // is what lets the presolve begin on the approach at all - and on a Grand site that guess is
        // wrong by ten. Nothing else here would notice: the markers are the same markers and the
        // rewards are the same rewards, so a rehearsal that had settled would keep a five link plan
        // for a fifteen link site, with the small site's reach baked into it.
        //
        // Folded into the fingerprint rather than checked separately, because the correction wanted
        // is exactly the one a marker arriving already triggers: stop the pass in flight, clear
        // done, and solve the real problem.
        // **How many explosives the site has is part of what the site IS.**
        //
        // It is assumed to be five until the detonator panel loads, and on a Grand site that guess
        // is wrong by ten. Nothing else here would notice: the markers are the same markers and
        // the rewards the same rewards, so a settled rehearsal would keep a five link plan for a
        // fifteen link site with the small site's reach baked into it. Folded into the fingerprint
        // rather than checked separately, because the correction wanted is the one a marker
        // arriving already triggers.
        //
        // **And whether the site is live, because that is when the routing grid exists.** A
        // presolve made on the approach is made without it, so its links are straight lines and
        // its reach is a distance. See Detonator.PanelReady.
        var links = Detonator.ExplosiveCount(gc);
        var live = Detonator.PanelReady(gc);
        var shape = Fingerprint(content, valuation) * 31 + links * 2 + (live ? 1 : 0);

        // **How much of the site has been READ, which is not how much of it is loaded.**
        //
        // This counted targets whose entity the game currently holds, on the reasoning that a
        // remnant which has never been in view has no encounter data - no rewards, no runes, no
        // propagation - so more of the site being loaded means more of it being known. The first
        // half is true and the conclusion does not follow: Rewards is a plain list assigned once
        // and cleared only by Forget, and a strongbox's pack counts are plain fields, so the data
        // SURVIVES the entity unloading. "Remembered" therefore splits into two quite different
        // cases - never seen, and seen once and kept - and a count of what is live cannot tell
        // them apart.
        //
        // What that cost is a re-open that buys nothing: walk out of a site and back into it and
        // the live count climbs from nought again, past its own previous mark, while the plugin
        // learns not one new thing. And it missed the case it was for, since a remnant read at the
        // boundary and then unloaded took the count down with it.
        //
        // So the question is asked directly. A remnant is read when it has rewards, which is the
        // same test Priced already uses; a strongbox when its guarding packs have been counted,
        // which is what the dump means by "packs unread". Everything else is read the moment it is
        // classified, and the marker count already covers that.
        //
        // Still a high water mark, for the reason the old one was: it can only go up, so nothing
        // about where the player is standing can move it.
        var read = 0;

        foreach (var target in content)
        {
            if (HasBeenRead(target))
                read++;
        }

        if (read > _read)
        {
            Reopening($"more of the site read ({read}, was {_read})");
            _read = read;
            _done = false;
            _revealed = DateTime.UtcNow;
            _reopened = true;
        }

        // **Remnants get their own high water mark, because the general one can hide them.**
        //
        // The count above is over every target, and monsters come and go: thirty of them unloading
        // while one remnant loads leaves the total below its old maximum, so nothing re-opens -
        // and the one thing that actually changed is the one thing that matters. A remnant is where
        // the rewards and the runes are, which is where the propagation is, which is most of what a
        // chain is worth.
        //
        // So the remnants in range are counted separately and the same rule applied. Standing still
        // while the site streams in, this is what resumes the presolve; walking in, it fires several
        // times and each pass is seeded from the last. See Scan.Reach.
        // Remnants only for the trigger: they are what carries propagation, so a remnant coming
        // into range is what makes the problem materially different. The readout reports every kind
        // - see Overlay.Status - because being told about the site is a wider question than being
        // told when to solve it again.
        var (_, _, missingRemnants, remnants) = scan.Reach(site);
        var readable = remnants - missingRemnants;

        if (readable > _readable)
        {
            Reopening($"more remnants readable ({readable}, was {_readable})");
            _readable = readable;
            _done = false;
            _revealed = DateTime.UtcNow;
            _reopened = true;
        }

        // **Nothing is published from here about how complete the site is.** There were two figures
        // saying so - how many remnants were out of range, and how many there were - and they were a
        // second, quieter answer to the question the scouting layer now answers properly. Two
        // answers to one question is how they come to disagree. See Scouted.Left and Overlay.Status.

        // Something new to solve for restarts a rehearsal that had given up. Markers stream in as
        // you walk, and a site that had nothing left to teach a moment ago has something now.
        //
        // **A count is not enough, because rerolling a remnant changes none of it.** The markers
        // are the same markers; what a remnant OFFERS has changed, and that is most of what a Grand
        // site is worth - so a rehearsal that watched only the count would settle on a plan built
        // around rewards that are no longer there, declare itself finished, and never look again.
        // The rewards are folded in for the same reason the score line folds them into its cache
        // key: a reading that is stale rather than wrong is the worse kind, because it looks like
        // the choice made no difference.
        // Where the remnants and relics stand, which is what a chain is built round. See MarkersWaiting.
        var stops = 0;

        foreach (var target in content)
        {
            if (target.Kind is TargetKind.Remnant or TargetKind.Relic)
                stops = unchecked(stops * 31 + target.Grid.GetHashCode());
        }

        if (here != _markers || shape != _shape)
        {
            var markersArrived = here != _markers;
            var stopsChanged = stops != _stops || shape != _shape && here == _markers;

            _stops = stops;

            Reopening(markersArrived
                ? $"markers {here}, were {_markers}"
                : "the rewards, the link count or the routing grid changed");
            _markers = here;
            _shape = shape;
            _revealed = DateTime.UtcNow;
            _done = false;
            Settled = false;

            // Only for markers arriving, which is the site still being read. A roll changes the rewards and is
            // a reason to solve again, not a reason to withhold the advice that asked for it.
            if (markersArrived)
                ContentSettled = false;

            _reopened = true;

            // **The pass in flight is stopped, not left to finish.**
            //
            // It is reasoning about a site that no longer exists - a marker has appeared, a remnant
            // has been rolled, a price has arrived - and its window is eight seconds, sixteen on a
            // Grand site. Letting it run its course means the newest thing you walked past waits
            // that long before anything thinks about it, which is most of the approach spent
            // answering a stale question.
            //
            // Stopping keeps everything: Stop publishes the best found so far, Poll files it, and
            // the next pass is seeded from it. So a restart costs the search time since the last
            // improvement and nothing else - the routing every pass worked out is kept regardless,
            // because that is a property of the ground rather than of the question.
            //
            // Only our own. A player who has pressed the key is having their search stopped by
            // nobody.
            //
            // **Except for markers alone.** While a site is scouted they arrive every second or two, and each one cut the
            // pass: on a Frigid Bluffs site (2026-10-04) sixteen passes in a row were stopped after 1.2 to 4.5 s, the
            // workers starting over every time and the remnant order search, which needs about 8 s, never finishing
            // until the scouting did - the score stood at 52,000 for twenty seconds and then jumped to 72,000. A marker
            // that is not a remnant or a relic moves the score but not the chain's shape, so it waits for the pass to
            // end, or for it to have run MarkersWaitMs. See MarkersWaiting.
            if (!stopsChanged && markersArrived)
                _markersWaiting = true;
            else if (planning.Searching && Planning.Rehearsing && HadItsTurn(settings) &&
                     planning.Stop("the presolve, because the site changed under it"))
                _cut = true;
        }

        // Markers waiting on a pass that has run long enough for them. See MarkersWaiting.
        if (_markersWaiting && planning.Searching && Planning.Rehearsing &&
            (DateTime.UtcNow - _launched).TotalMilliseconds >= MarkersWaitMs &&
            planning.Stop("the presolve, because markers arrived while it ran"))
        {
            _cut = true;
            _markersWaiting = false;
        }

        // Ours is stopping, or theirs is running. Either way this frame does not start one.
        if (planning.Searching)
            return;

        // Nor after a cold start, until the player asks: Start would refuse, and the pass would be counted as run.
        // See Planning.HeldAfterColdStart.
        if (Planning.HeldAfterColdStart)
            return;

        // Nor on a Grand site already set off, where Start would refuse. See Detonator.GrandSiteSetOff.
        if (Detonator.GrandSiteSetOff(gc))
            return;

        // **The continuous reroll mode keeps solving until the player stops it or nothing is left to
        // roll.** Otherwise a pass that fails to beat the standing chain settles the site, and only a
        // change to the site - a marker, a price, a roll - opens it again. Between rolls the player is
        // walking to the next remnant, and that time was spent solving nothing. See Continuing.
        _rerollsLeft = RerollsLeft(content);
        Continuing = !_stoppedByPlayer && (_passes > 0 || _continueAsked) && !planning.ProvenBest &&
                     Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0) == 0;

        if (_done && Continuing)
        {
            _done = false;
            Settled = false;
            KeepGoing();
        }

        if (here < Enough || _done)
            return;

        // **A roll is answered by the solve the roll itself asks for, and not twice.**
        //
        // The fingerprint above folds in the rewards, so rolling a remnant moves it and re-opens
        // the rehearsal. AutoExpedition ALSO dispatches a solve on the roll - a short window seeded
        // from the standing chain, which is the right shape for one remnant's difference. Neither
        // knew about the other, so every roll was solved twice: measured on one site, a 1,066ms
        // press pass and then a 4,023ms rehearsal pass starting 34ms after it ended, on the same
        // floor and the same 92 markers, the second publishing nothing. Five seconds before the
        // reroll advice could start, and the advice was then dropped and redone when the second
        // pass began.
        //
        // So the roll's own pass counts as the pass this would have dispatched. Set when the solve
        // actually started, so a refused Start leaves the rehearsal to cover the site as before.
        //
        // Read here rather than where the fingerprint moves, because the two are not always the
        // same frame: rolling while a pass is in flight defers the dispatch, and the re-open is
        // recorded first. This is the gate both orders arrive at.
        if (_solvingARoll)
        {
            _solvingARoll = false;
            _reopened = false;
            _rollsAnswered++;

            // **Except after the last roll in the continuous reroll mode**, where one full pass follows.
            // The roll's own solve has the short reroll window, and with nothing left to roll there is
            // no later pass to improve on it - the player is walking back to the detonator. See
            // Continuing.
            if (Rolling.Mode(settings) == RerollSettings.Continuous && !_stoppedByPlayer &&
                _rerollsLeft == 0)
            {
                _finalPass = true;
                ContentSettled = true;
                KeepGoing();
            }
            else
            {
                _done = true;
                Settled = true;
                ContentSettled = true;

                return;
            }
        }

        // **Wait for the remnants to be priced, because an unpriced site is the wrong problem.**
        //
        // Rewards are read and priced asynchronously, and a rehearsal starts as soon as there are
        // two markers - so the first pass ran against remnants worth nothing: no reward value, no
        // carried rune, no propagation. Measured on a five explosive site: the planner scored its
        // own chain at 893 where the same chain priced is worth 3,535, and all 2,642 of the
        // difference was propagation. It was optimising a quarter of the objective and then being
        // beaten by the next pass the moment the prices arrived, which is the score jumping a
        // window later.
        //
        // Not for ever, though. A site can hold a remnant nothing can price - no economy data, an
        // unreadable recipe - and waiting on that would mean never rehearsing at all. After the
        // grace period it goes anyway, on the reasoning that a partly priced site is still a better
        // problem than no answer.
        //
        // **Measured from when the site was first seen, not from the last thing that changed.** It
        // was the latter, and that made the wait unbounded in exactly the situation it was meant to
        // help: markers stream in while you walk, each arrival is a change, each change restarted
        // the clock, and a remnant that had just appeared was unpriced until the next sweep - so the
        // grace period never expired and the rehearsal did not start until the site stopped
        // revealing itself, which is when you had arrived.
        var priced = Priced(content);

        if (priced)
            SiteArrival.Note(SiteArrival.Step.RemnantsPriced);

        if (!priced && DateTime.UtcNow - _first < TimeSpan.FromMilliseconds(Patience))
            return;

        // **Solve on what there is, from wherever you are, and say what it rests on.**
        //
        // The remnants and the detonator are enough to have an opinion, and both are available from
        // across the map - one live listing had all fourteen remnants readable, the furthest 1,222
        // grid away, with their rewards, their sockets and their chosen combinations. What is
        // missing early is the MONSTERS, which is the weight propagation pays out over, so an early
        // answer is a real answer to a smaller site rather than a wrong answer to this one.
        //
        // It improves on its own: every marker remembered, every reward that becomes readable and
        // every increase in what is loaded re-opens the question, and each pass is seeded from the
        // last. Refusing to solve until close was the previous rule, and it spent the walk in - the
        // one stretch of time nobody else wants - doing nothing.
        //
        // What it must not do is pretend, and it does not: the line reads "Partial presolve" and
        // the warning under it says the picture is incomplete. See Overlay.Status.

        if (DateTime.UtcNow < _next)
            return;

        // **Stops when the site has gone quiet AND the plan has stopped improving**, which are two
        // different things and both have to hold. A quiet scan alone means only that you have
        // stopped walking; a plan that did not improve alone means only that the last pass was
        // unlucky with a site that is still arriving.
        // **Plain, not the objective's total.** The objective adds a synthetic bonus for holding a
        // marker the player insisted on - larger than anything the site can pay, so the comparison
        // comes out lexicographic without a special case - and that makes its number unreadable: on
        // a site with one must-take this reported 21,016 beside a plan the rest of the dump called
        // 9,859, and the whole difference was the bonus. Two conventions side by side, unlabelled.
        //
        // The Climb series was moved to Plain for exactly this and this was left behind. See
        // Verdict.Plain.
        var worth = planning.Ready ? planning.Plan.Plain : 0d;

        // Measured before the best is moved, which the first version of this got wrong: it compared
        // the pass against a high water mark it had already raised, so every pass reported that it
        // had moved the score and the readout said the opposite of the truth.
        // Against what it was when this pass was launched, which is the only fair comparison. It
        // was against the running high water mark, which starts at nought - so the first pass always
        // "moved" the score, whether it had found anything or inherited it whole from the filed
        // chain, and the readout said so while the number on screen had not changed.
        _moved = _passes > 0 && worth > _before + 0.0001d;

        // **The pass that just finished, read now rather than remembered from last time.**
        //
        // Found is reset by Start, so at this moment it still holds the count from the pass that has
        // ended - and the field kept for the dump holds the one before that, because it is written
        // on the way INTO a pass. Deciding on the field meant deciding on evidence one pass out of
        // date: a barren pass was answered by starting another one, and only the pass after that
        // could see it. Measured as three passes where two were the answer.
        var drew = planning.Found;

        // **Filed before the settle test, because settling is one of the things being explained.**
        //
        // A pass that concludes returns from inside that test and a pass that does not falls
        // through to dispatch another, so there is exactly one point both of them pass through
        // with the result in hand. Filed once, since this runs every frame until the next pass
        // starts - or for ever, once the rehearsal has declared itself finished.
        // **A pass that published nothing is the answer, not a step towards it.** It was seeded with
        // the best chain known here and spent its whole window failing to beat it - that is the
        // search saying it has run out of ideas about this site, in the same words the improvement
        // window uses. Waiting for the scan to go quiet as well meant another full window spent
        // proving the same thing again.
        //
        // Nothing is lost by stopping early: a marker arriving, a remnant rolled, a reward chosen -
        // any of them clears _done and the rehearsal starts again with something new to think about.
        // **Judged on what the search published, not on whether the number changed.**
        //
        // A pass that opens with a floor of 3,534.9 and ends at 3,534.9 having drawn no improvements
        // has said everything it has to say about this site: it was handed the best chain known and
        // could not beat it. Whether the SCORE moved is a different question with a misleading
        // answer - the first pass inherits its floor from the filed chain, so the number rises from
        // nothing to that floor without the search having found a thing.
        //
        // Not a pass that was cut short. One stopped because a marker appeared drew nothing because
        // it was not allowed to finish, which says nothing about the site.
        //
        // **Nor one that ran out of ROUTING, which is not the same as running out of ideas.**
        //
        // The router floods outward from a spot to answer whether the next link can be reached, and
        // it caches what it learns against the site. A first pass on a site nobody has routed pays
        // for all of that at once, and when its budget runs out it starts refusing to answer -
        // which the search reads as "you cannot get there from here" and routes around. Measured on
        // an ordinary five link site: the first pass asked the router 176,203 times and had 159,002
        // of those REFUSED, finished at 1,854, and declared itself done. The very next solve, one
        // second long, asked 2,679 times with nothing refused because the floods were now cached,
        // and finished at 4,311.
        //
        // So a starved pass has learnt something valuable and concluded nothing. Stopping on it
        // reports the groundwork as the answer, which is what made walking into a site give a plan
        // worth less than half of what pressing the key immediately afterwards gives.
        // **Once, unless something has changed.**
        //
        // A pass that ran out of routing used to be exempted from counting as done, on the grounds
        // that it had learnt floods and deserved another go. Where the router cannot cover the site
        // at all that condition never clears, so every pass was exempt and the next began half a
        // second later - standing still at a portal, the window restarting for as long as you stood
        // there. Retries bounded at three were the same bug with a smaller number on it.
        //
        // So a pass concludes, whatever stopped it. What re-opens the question is the site being
        // different from the site that was solved: a marker remembered, a reward readable, more of
        // it loaded than ever before, or you walking into it. Every one of those is a real change
        // and none of them can fire without something having actually happened.

        // In the continuous reroll mode the pass that would have settled the site is followed by
        // another on a fresh draw instead. See Continuing.
        if ((Continuing || _finalPass) && _passes > 0 && !_cut && !_reopened && (drew == 0 ||
            DateTime.UtcNow - _revealed > TimeSpan.FromMilliseconds(Quiet)))
        {
            // The same conclusion the branch below reaches, and the next pass runs anyway. See ContentSettled.
            ContentSettled = true;
            KeepGoing();
        }
        else if (_passes > 0 && !_cut && !_reopened && (drew == 0 ||
            DateTime.UtcNow - _revealed > TimeSpan.FromMilliseconds(Quiet)))
        {
            _drew = drew;
            _settledAt = DateTime.UtcNow;
            _done = true;
            Settled = true;
            ContentSettled = true;

            return;
        }

        if (worth > _best)
            _best = worth;

        // Proved optimal, so more passes cannot help however long the walk is - unless the router
        // was refusing to answer, in which case what was proved optimal is a site with walls across
        // it that are not there. See starved.
        //
        // **And not while the question has changed under it**, which is the same fault the settle
        // test above had. ProvenBest belongs to the last plan PUBLISHED, and it survives until a new
        // pass publishes - so a re-opened rehearsal read a proof about a smaller site and concluded
        // again without running. It is also wrong on its own terms: optimal was proved against the
        // content that pass ran on, and a marker arriving means there is more of it. See _reopened.
        if (planning.ProvenBest && !_reopened)
        {
            _done = true;
            Settled = true;
            ContentSettled = true;

            return;
        }

        // A pass the presolve would not have run searches on the next draw, or it would repeat the
        // pass before it: same chain, same seeds. See Planning.Draws.
        // Captured before it is cleared: a pass the continuous reroll mode asked for runs its own length. See
        // RerollSettings.ContinuousPassMs.
        var continuousPass = _keptGoing;

        if (_keptGoing)
        {
            Planning.Draws++;
            _keptGoing = false;
        }

        _finalPass = false;

        _drew = drew;
        _before = worth;
        _cut = false;
        _reopened = false;
        _ran = _whys > 1 ? $"{_why} (+{_whys - 1} more changes)" : _why ?? "the first pass here";
        _why = null;
        _whys = 0;
        _launched = DateTime.UtcNow;
        _markersWaiting = false;

        if (_passes == 0)
            _firstLaunched = _launched;

        _passes++;
        SiteArrival.Note(SiteArrival.Step.FirstPresolvePass);
        Passes = _passes;
        Settled = false;
        _next = DateTime.UtcNow + TimeSpan.FromMilliseconds(Between);

        // **A rehearsal is only a rehearsal until the plan has been asked for.**
        //
        // The same triggers fire afterwards - a remnant rolled, a marker revealed, a price arrived -
        // and they should, because the plan on screen is out of date the moment any of them happens.
        // What must not carry over is the WINDOW: a presolve is eight seconds because nobody is
        // waiting for it, and once the chain is drawn somebody is. Rolling a remnant the adviser
        // picked and then waiting twice the usual solve for the answer is the loop this exists to
        // keep short.
        var asked = Planning.Showing(site);

        _asked = asked;
        _where = site;

        // What the comparison was against, captured with its answer rather than re-read later.
        _pressed = Planning.ShownAt;

        // **Never looped from here, which it was, and that cost the search most of its window.**
        //
        // The short window is for the reroll loop: a roll re-asks a question the player asked a
        // second ago, the chain is seeded from the previous one, and a second is plenty. Passing
        // asked for it read "a plan was asked for here and the site has changed" as that loop - and
        // that is true of every change at a site the key has ever been pressed at. A marker
        // streaming in, a price arriving, a strongbox identified: each one got one second, for the
        // rest of the map, because of a press minutes earlier.
        //
        // Measured: two passes over 1,523ms on a site where the window is four seconds, after a
        // strongbox was rolled. The reroll loop has its own call site and says so itself - see
        // AutoExpedition's "a remnant was rolled" - so nothing here has to guess at it.
        //
        // The rehearsing flag still follows asked, because it decides something else entirely:
        // whether the result is drawn without being asked for. After a press it should be, which is
        // why these two cannot be the same flag however alike they look.
        // **The reason goes with the solve rather than being kept beside it.** Planning files
        // every run and prints the timeline, so what re-opened the question belongs in the cause -
        // one record of one fact, where a copy here was a second timeline that could disagree.
        planning.Start(gc, settings, scan, blast, valuation, zone, false, !asked,
            asked
                ? $"a plan asked for here, and {_ran ?? "the site changed"}"
                : $"the presolve - {_ran ?? "rehearsing this site"}",
            continuous: continuousPass);
    }

    /// <summary>
    /// Whether the pass in flight has run long enough that a change may stop it.
    ///
    /// **Stopping is right and stopping instantly is not.** A pass reasoning about a site that has
    /// since grown is answering a stale question, so a change should re-open it - but the change
    /// is one marker arriving, and markers arrive one at a time. Every arrival killed the pass,
    /// and the entity work made it worse by making them arrive promptly: five passes in two and a
    /// half seconds, cut at 26ms, 212ms, 116ms, 79ms and 87ms, every one of them publishing
    /// nothing because none lived long enough to finish a greedy pass.
    ///
    /// Nothing is lost by letting it finish. The change still clears done and marks the question
    /// re-opened, so the settle test declines to conclude on it and the next pass is dispatched
    /// against the new site as soon as this one ends. See SolverSettings.RestartMs.
    /// </summary>
    private bool HadItsTurn(AutoExpeditionSettings settings)
    {
        var least = Math.Max(0, Safe.Read(() => settings.Solver.Presolve.RestartMs.Value, 1000));

        return _launched == DateTime.MinValue ||
               (DateTime.UtcNow - _launched).TotalMilliseconds >= least;
    }

    /// <summary>
    /// Whether this target's own data has been read off the game yet.
    ///
    /// Only two kinds carry anything that needs the entity: a remnant's rewards and a strongbox's
    /// guarding packs. Both are kept once read and survive the entity unloading, so this asks what
    /// is on the record rather than what the game currently holds. Everything else is read the
    /// moment it is classified. See Tick.
    /// </summary>
    private static bool HasBeenRead(Target target) =>
        target.Kind switch
        {
            TargetKind.Remnant => target.Spent || target.Rewards.Count > 0,
            TargetKind.Strongbox => !target.Explodes || target.Packs > 0,
            _ => true,
        };

    /// <summary>Whether every remnant here has had its rewards read and valued. See Tick.</summary>
    private static bool Priced(System.Collections.Generic.List<Target> content)
    {
        foreach (var target in content)
        {
            if (target.Kind != TargetKind.Remnant || target.Spent)
                continue;

            if (target.Rewards.Count == 0)
                return false;

            var any = false;

            foreach (var reward in target.Rewards)
                any |= reward.Value > 0d;

            if (!any)
                return false;
        }

        return true;
    }

    /// <summary>How long to wait for prices before rehearsing anyway, in milliseconds.</summary>
    private const double Patience = 10000d;
    /// <summary>
    /// What this site's remnants are currently offering, as one number.
    ///
    /// Cheap on purpose - a handful of remnants, a few strings each - because it only has to answer
    /// "has anything about the rewards changed since the last pass". Sockets as well as rewards,
    /// since a reroll can change how many there are, and the chosen name, since picking one is a
    /// decision the plan should be allowed to react to.
    /// </summary>
    private static int Fingerprint(System.Collections.Generic.List<Target> content,
        Valuation valuation)
    {
        var hash = 17;

        foreach (var target in content)
        {
            // **A strongbox changes, and nothing here could see it.** This walked remnants only, on
            // the reasoning that they are what a roll moves - and a strongbox is the other thing the
            // player can change: identifying one reveals its modifiers, a currency use rerolls its
            // rarity and the packs guarding it. Scan.Guards re-reads all three now, and without them
            // folded in here the presolve slept through it: a box identified into "guarded by 6
            // packs of monsters" was worth six packs more and the search was never told.
            //
            // The rarity and the pack counts only, not the modifier text. Those are what the weights
            // are built from - see Weighing.TierSplitOfTarget - and they move only when the player moves them,
            // where the text can be re-read into the same words and would churn the hash.
            if (target.Kind == TargetKind.Strongbox)
            {
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(target.Rarity ?? "");
                hash = hash * 31 + target.ImplicitPacks;
                hash = hash * 31 + target.ImplicitMagicPacks;
                hash = hash * 31 + target.ImplicitRarePacks;
                hash = hash * 31 + target.ExplicitPacks;
                hash = hash * 31 + target.ExplicitMagicPacks;
                hash = hash * 31 + target.ExplicitRarePacks;

                continue;
            }

            if (target.Kind != TargetKind.Remnant)
                continue;

            hash = hash * 31 + target.Sockets;

            // **The prices as well as the names, because a rehearsal can start before them.**
            //
            // Rewards are priced asynchronously and a presolve begins as soon as there are two
            // markers to solve for, so the first passes run against remnants worth nothing - no
            // reward value, therefore no carried rune, therefore no propagation. Measured: the
            // planner scored its own chain at 893 while the readout, pricing the same remnants,
            // scored it at 3,535, and every point of the gap was propagation.
            //
            // A chain chosen for a site whose remnants are worth nothing is a chain that ignores
            // the thing the site is mostly worth. Folding the values in means the prices arriving
            // counts as the site revealing something, which it is - so the rehearsal wakes and
            // solves the real problem instead of settling on the wrong one.
            foreach (var reward in target.Rewards)
            {
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(reward.Name ?? "");
                hash = hash * 31 + reward.Value.GetHashCode();
                hash = hash * 31 + reward.Carries.GetHashCode();
            }

            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(
                Safe.Read(() => valuation?.ChosenName(target.Entity), null) ?? "");
        }

        return hash;
    }

    /// <summary>
    /// How long the site has to reveal nothing before a rehearsal will call itself finished.
    ///
    /// Longer than the gap between passes, so a pass that happens to land between two markers
    /// arriving does not read as a site that has gone quiet.
    /// </summary>
    private const double Quiet = 3000d;
}
