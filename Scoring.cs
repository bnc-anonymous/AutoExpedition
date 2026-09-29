using ExileCore2;
using System.Globalization;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// What the plan is worth, and what the explosives actually down are worth, side by side.
///
/// On screen permanently rather than behind a key, because it is the only honest answer to "is this
/// plan any good". A route drawn on the ground looks reasonable or unreasonable by eye and the eye
/// is not to be trusted about it - a chain that skips a chest to reach two runic monsters is either
/// right or wrong depending on numbers nobody can see. So both numbers are shown, in the same units,
/// from the same objective.
///
/// **Both are judged from the detonator**, not from wherever the chain has got to. The point is to
/// compare two complete routes, and a route scored from its own far end would be charged nothing
/// for the walking it did to get there.
///
/// The caveat worth knowing: a plan made part way through a chain contains only the links that were
/// left, so its number is for the remainder while yours is for the whole thing. Re-plan at the
/// detonator with nothing placed if the comparison is meant to be fair.
/// </summary>
internal sealed class Scoring
{
    private Plan _of;

    /// <summary>The chain the last reading was taken from. See Update's cache.</summary>
    private List<Vector2> _chain;
    private int _targets = -1;
    private int _placed = -1;
    private int _found = -1;
    private int _runes;

    /// <summary>Whether there is anything to say. False outside a dig site.</summary>
    public bool Known { get; private set; }

    /// <summary>What the planner's chain scores.</summary>
    public double Planned { get; private set; }

    /// <summary>What the explosives actually placed score, by the same objective.</summary>
    public double Yours { get; private set; }

    /// <summary>
    /// The environment and the chain the number on screen was last worked out from. See Update.
    ///
    /// Written every time the readout recomputes and read only by the dump, so the composition can
    /// be compared between two presses taken at different distances from the site.
    /// </summary>
    internal static PlanEnvironment LastEnv { get; private set; }

    internal static List<Vector2> LastChain { get; private set; }

    /// <summary>How many explosives are down, so a partial chain is not read as a bad one.</summary>
    public int Down { get; private set; }

    /// <summary>
    /// How much must-have bonus each of the two numbers had taken out of it.
    ///
    /// **The number on screen has to be explainable from a dump, and it was not.** The running score
    /// was the one figure the player looks at all the time and the only one F6 said nothing about -
    /// so a drop of seven thousand after a reload could not be told apart from lost markers, a lost
    /// plan, or a re-scored chain without guessing. These are what the drop was.
    /// </summary>
    /// <summary>
    /// Recomputes when something has changed, which is rarely.
    ///
    /// Cached on the plan, the number of markers known and the number of explosives down, because
    /// none of the three changes between two frames in the ordinary case - and building the
    /// environment walks every marker in the site.
    /// </summary>
    /// <summary>How much there was to loot when the looting started, and how much is left.</summary>
    private int _total;
    private int _left;

    /// <summary>The site those two numbers belong to, so the next dig site starts over.</summary>
    private Vector2 _lootingAt;

    /// <summary>
    /// Counts what is left to pick up, and what there was to begin with.
    ///
    /// The two halves are counted differently, because only one of them leaves a trace.
    ///
    /// **Chests are exact, and survive a reload.** An opened chest is still standing there with its
    /// IsOpened set, so the total is every expedition chest in the site and the number done is the
    /// ones already opened. Nothing has to be remembered between frames, and a plugin loaded half
    /// way through a looting run reads the right fraction immediately.
    ///
    /// **Remnants can only be remembered.** Shattering one destroys it, so a count taken fresh each
    /// frame would shrink in step with the work and read n/n the whole way through - hence the
    /// high-water mark. That mark is the one thing a reload cannot recover: it restarts from the
    /// remnants still standing, so the fraction picks up from there rather than from the start. The
    /// chest half stays right, which is most of it.
    ///
    /// It only counts once the explosives are gone, which is what makes "of n" mean the site rather
    /// than whatever happens to be lying about mid-chain.
    /// </summary>
    private void Looting(GameController gc, Scan scan)
    {
        var site = Detonator.DetonatorGridPosition(gc);

        if (site != _lootingAt)
        {
            _lootingAt = site;
            _total = 0;
            _left = 0;
            _remnants = 0;
        }

        if (Detonator.ExplosivesInHand(gc) > 0)
        {
            _total = 0;

            return;
        }

        var standing = 0;

        // THIS dig site. A map holds two, and scan.Targets holds both of them - so the other
        // expedition's spent remnants were being counted here, and its chests with them, which is
        // why the count read higher than the number of things anybody could see.
        foreach (var target in scan.At(site))
        {
            if (target.Shatterable)
                standing++;
        }

        // Never falls, so shattering one moves the fraction rather than the goalposts.
        if (standing > _remnants)
            _remnants = standing;

        var chests = 0;
        var opened = 0;

        var found = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(
                ExileCore2.Shared.Enums.EntityType.Chest, out var of)
                ? of
                : null, null);

        foreach (var chest in found ?? new List<ExileCore2.PoEMemory.MemoryObjects.Entity>())
        {
            var metadata = Safe.Read(chest, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("LeaguesExpedition", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var grid = Safe.Read(chest, static e => e.GridPos, Vector2.Zero);

            if (site == Vector2.Zero || grid == Vector2.Zero ||
                Vector2.Distance(grid, site) > Detonator.SiteReach(gc))
                continue;

            var isOpen = Safe.Read(chest, static e =>
                e.GetComponent<ExileCore2.PoEMemory.Components.Chest>()?.IsOpened ?? false, false);

            // Only things that can actually be opened are counted. GenericShatterable shares the
            // reward chests' path, so a tileset full of barrels inflated the denominator and the
            // readout said there was far more left to collect than there was.
            //
            // Or already open, because opening one can take its targetable flag away - and dropping
            // it from the total the moment it is collected would make the count go backwards.
            if (!isOpen && !Safe.Read(chest, static e => e.IsTargetable, false))
                continue;

            chests++;

            if (isOpen)
                opened++;
        }

        _total = chests + _remnants;
        _left = chests - opened + standing;

        // Nothing left means the line has said what it had to say.
        if (_left <= 0)
            _total = 0;
    }

    /// <summary>What the right hand number is made of, for the dump. See Update.</summary>
    public string Breakdown { get; private set; } = "nothing scored yet";

    /// <summary>The most spent remnants seen standing here at once, which is how many there were.</summary>
    private int _remnants;

    /// <summary>
    /// The lowest the score went during the last re-solve, and what it was made of down there.
    ///
    /// **A dip lasts about a second and F6 is pressed afterwards, so the dump kept missing it.** The
    /// breakdown is recomputed every time the number moves, which means by the time anybody asks for
    /// it the search has climbed back and it describes the recovery rather than the fall. Watched
    /// here instead: the score before the press is remembered, the low water mark during the search
    /// is kept, and both are still there to be read when the solve has finished.
    ///
    /// Before the cache gate, because this has to see every frame - the gate skips the frames where
    /// nothing it keys on has changed, and the start of a search is exactly such a frame.
    /// </summary>
    public string Dip { get; private set; } = "no re-solve seen yet";

    /// <summary>
    /// The live chain completed to the full explosive count, or null while that is still being worked out.
    ///
    /// **Completing a chain is not the same kind of question as valuing one**, so it is asked of the search's own
    /// environment, which has the terrain and obstacle predicates; the scoring environment is built without them,
    /// and Complete CHOOSES spots, so run against that it could credit links the game would never place. The origin
    /// is the detonator, so the journey is charged as it is when the chain is scored. The environment is copied
    /// only when the origin differs, because the planner caches coverage per environment object and a copy starts
    /// those caches from nothing.
    ///
    /// One completion at a time, on a low-priority thread. A chain published while one is running waits for the
    /// next tick after it finishes; the screen keeps the last figure meanwhile.
    /// </summary>
    private List<Vector2> CompletedLiveChain(List<Vector2> live, PlanEnvironment solid, Vector2 origin)
    {
        lock (_completionGate)
        {
            if (ReferenceEquals(_completedFrom, live))
                return _completed;

            if (_completingFrom != null)
                return null;

            _completingFrom = live;
        }

        var source = new List<Vector2>(live);
        var from = solid.Origin == origin ? solid : solid with { Origin = origin };

        BackgroundWork.StartAtLowPriority(() =>
        {
            var done = source;

            try
            {
                done = BackgroundWork.Record("score completion", () => Planner.Complete(from, new List<Vector2>(source)));
            }
            catch (System.Exception)
            {
                // A completion that fails leaves the chain scored as it stands, which is what it was before this
                // was done at all.
            }
            finally
            {
                lock (_completionGate)
                {
                    _completedFrom = live;
                    _completed = done;
                    _completingFrom = null;
                }
            }

            return 0;
        });

        return null;
    }

    private readonly object _completionGate = new();

    /// <summary>The live chain the last completion was for, and what it came to. See CompletedLiveChain.</summary>
    private List<Vector2> _completedFrom;

    private List<Vector2> _completed;

    /// <summary>The live chain a completion is running for, or null when none is.</summary>
    private List<Vector2> _completingFrom;

    private bool _searching;
    private double _before;
    private double _low;
    private string _lowOn = "";

    public void Update(GameController gc, AutoExpeditionSettings settings, Scan scan,
        Blast blast, Valuation valuation, Planning planning)
    {
        Looting(gc, scan);

        if (planning.Searching != _searching)
        {
            _searching = planning.Searching;

            if (_searching)
            {
                // The number as it stood the instant the key went down, which is what a fall is a
                // fall FROM.
                _before = Planned;
                _low = Planned;
                _lowOn = Breakdown;
            }
            else
            {
                Dip = _low < _before - 0.5d
                    ? $"the last solve started at {_before:N0}, fell to {_low:N0} " +
                      $"(-{_before - _low:N0}) and finished at {Planned:N0}; at the bottom it was " +
                      _lowOn
                    : $"the last solve started at {_before:N0} and never fell below it, " +
                      $"finishing at {Planned:N0}";
            }
        }

        var plan = planning.Plan;
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        // While a search is running the number tracks its best chain so far, which is what makes it
        // climb on screen rather than appear at the end. Recomputed when the search publishes a
        // better one, which is a handful of times per solve.
        // The finished plan is the fallback rather than nothing: a search with no published chain
        // yet is a search that has not beaten what is already drawn, and the answer to that is to
        // keep drawing it.
        var chain = planning.Searching && planning.Live is { Count: > 0 }
            ? planning.Live
            : plan.Points;

        // What the remnants are currently set to, folded into the cache key.
        //
        // Without it the score was frozen against the combinations: change the rune on a remnant and
        // the number on screen did not move, because nothing else in the key had changed - not the
        // plan, not the marker count, not the explosives down. The reading was stale rather than
        // wrong, which is the worse kind: it looked like the choice made no difference.
        // **Nothing solved yet is not nothing known.**
        //
        // A rehearsal shows no number until its first pass publishes something - and with the wait
        // for prices in front of that, the score can be blank for ten seconds at a site the plugin
        // has a perfectly good route for on disk. The filed chain is what the next press would take
        // as its floor, so it is the honest answer to "what is this site worth" while the search is
        // still getting started.
        //
        // Only with nothing placed. Kept holds a whole route from the detonator and the line below
        // prepends the explosives already down, so using it mid-chain would count the front of the
        // route twice.
        if (chain is not { Count: > 0 } && placed.Length == 0 && Kept.Chain is { Count: > 0 })
            chain = Kept.Chain;

        // **A short live chain is scored as it will be finished, and the finishing is done off the frame.** See
        // the note further down on why a live chain is scored completed. The completion is a greedy build over every
        // candidate, and done here it stopped the drawing for 150 to 920ms at a time on a cold Grand site. Until the
        // completed chain is ready this returns early, so the score on screen stays at the last figure rather than
        // the frame waiting for the next one.
        if (planning.Searching && chain is { Count: > 0 } && planning.Env is { } solid &&
            chain.Count < solid.Explosives)
        {
            var completed = CompletedLiveChain(chain, solid, Detonator.DetonatorGridPosition(gc));

            if (completed == null)
                return;

            chain = completed;
        }

        var runes = Combinations(scan, valuation, gc);

        // **The chain itself is part of the key, and leaving it out is why a score stayed blank.**
        //
        // The key was a list of the things the chain is DERIVED from - the plan, the marker count,
        // what is placed, how many improvements the search has drawn - on the reasoning that the
        // chain cannot change unless one of them does. That stopped being true the moment a fallback
        // was added: with no plan and no live chain the filed route is used instead, and it becomes
        // available when Kept loads, which none of those five things notices. So the first frame
        // computed nothing, every frame after it hit the cache, and the readout sat empty at a site
        // with a perfectly good route on file.
        if (ReferenceEquals(plan, _of) && ReferenceEquals(chain, _chain) &&
            scan.Targets.Count == _targets && placed.Length == _placed &&
            planning.Found == _found && runes == _runes)
            return;

        _chain = chain;
        _of = plan;
        _targets = scan.Targets.Count;
        _placed = placed.Length;
        _found = planning.Found;
        _runes = runes;
        Down = placed.Length;

        // Each costly step timed on its own, because the whole of Update was measured at 270ms on a Grand site and
        // the table could not say which part. See Spent.
        PlanEnvironment env;

        using (Spent.On("Tick/Scoring.Update/Build"))
            env = Planning.Build(gc, settings, scan, blast, valuation, false, false, out _, out _);

        if (env == null)
        {
            Known = false;

            return;
        }

        // From the detonator, for both, so the two routes are charged for the same journey.
        var fair = env with { Origin = Detonator.DetonatorGridPosition(gc) };

        // **Kept for the dump, because the number on screen is worked out here and nowhere else.**
        //
        // The search's own figure and this one disagree - a chain the solve finished at 5,823 is
        // drawn at 2,858 - and the gap tracks how far the player has walked from the site. The
        // pool cannot be the cause: Build reads scan.At(site), which keeps a marker for ever once
        // seen, and the marker count in the line below holds steady while the score falls. So what
        // is wanted is the environment this figure was computed against, target by target, at two
        // distances. See Dump's composition line.
        LastEnv = fair;

        // **A live chain is scored short, and the jump at the end of a pass is that being fixed.**
        //
        // The search publishes whatever beats the floor, and a great deal of what it finds is a
        // PARTIAL chain - the band walk stops when it runs out of legal band moves, so a five link
        // problem is routinely answered with three. Only when the pass finishes does Poll spend the
        // explosives that were left over, and the number then rises by whatever those two blasts
        // were worth.
        //
        // Watching that from the outside, the score sits still for the whole window and then jumps
        // the instant the countdown expires, which reads as a readout that is behind rather than a
        // plan that is incomplete. So the same completion is applied here, to the chain being shown:
        // what is displayed is then what this chain is going to be worth, rather than what it is
        // worth half-built.
        //
        // Only while searching, and only when it is actually short. A finished plan has already been
        // completed by Poll and running it again would cost a greedy pass over every candidate for
        // nothing. The completion itself is done off the frame; see CompletedLiveChain, called above.

        // The right hand number: the chain the plan was solved FOR, scored with the combinations the
        // planner would take. Static, which is the point of it - it is the thing being compared
        // against, so it must not move when you deviate from it or change a rune.
        //
        // **The head comes from the chain, not from the ground.**
        //
        // It used to be the explosives actually down, cut to however many the plan was solved
        // behind - which is two accounts of the same route being reconciled every frame, and they
        // disagree exactly when it matters. Undo drops the game's count a frame before its position
        // list, so for that frame the cut was one short and what got scored, drawn and FILED was the
        // plan with a link missing off its front: a route nobody planned, worth 2,197 where the
        // chain it came from was worth 5,322. The next undo then walked down from that.
        //
        // Planning holds the whole route and says how far along it the chain has got, so there is
        // nothing to reconcile: the head is the chain's own first links. While a search runs the
        // tail is its best so far rather than the filed plan, which is the one case where this is
        // not simply Chain - and it is the same head either way. See Planning.Chain.
        var before = System.Math.Min(planning.Laid, planning.Chain.Count);
        var intended = new List<Vector2>(before + (chain?.Count ?? 0));

        for (var i = 0; i < before; i++)
            intended.Add(planning.Chain[i]);

        intended.AddRange(chain ?? new List<Vector2>());

        // Plain, not Total: a chain made to reach a required marker carries a term bigger than
        // the whole site, and a number on screen that triples because something was marked says
        // nothing about the chain. See Verdict.Held.
        Verdict ahead;

        using (Spent.On("Tick/Scoring.Update/Rate"))
            ahead = intended.Count > 0 ? Planner.Rate(fair, intended) : null;

        LastChain = intended;
        Planned = ahead?.Plain ?? 0d;

        // **The number on screen is not the number the search is maximising, and that has to be
        // visible.** The search ranks chains by Verdict.Total against the environment it solved
        // with; this line shows Plain against an environment rebuilt this frame from the detonator,
        // over the placed prefix AND the chain. Four differences, any of which can move the figure
        // while the search is doing exactly what it was asked - so when the score dips on a
        // re-solve, this says which of the four did it rather than leaving it to be guessed at.
        // Which environment produced the number, because two of them are in play and they have
        // disagreed: the planner builds one with terrain and with the caught content removed, this
        // builds its own with neither, and a chain the search scored at 893 was drawn at 3,535 with
        // the whole gap in propagation. Nothing said which env either number came from.
        Breakdown =
            $"env at ({fair.Origin.X:0},{fair.Origin.Y:0}) planner at " +
            $"({planning.Site.X:0},{planning.Site.Y:0}), {fair.Targets.Count} markers, " +
            $"{fair.Explosives} explosives; " +
            $"{(planning.Searching ? "live chain" : "finished plan")} of {chain?.Count ?? 0} spots " +
            $"behind {before} laid of a {planning.Chain.Count} link chain, {placed.Length} " +
            "explosives down; " +
            // **Plain and the insistence apart, rather than plain and a total that hides it.** Total
            // is plain plus a synthetic bonus per must-take, sized larger than the site so the
            // objective ranks them lexicographically - so printing it beside plain invited the two
            // to be compared, and they were, by the plugin's author and by its readouts. What the
            // bonus is worth is the one honest way to show it. See Verdict.Plain.
            $"plain {ahead?.Plain ?? 0d:N0}" +
            ((ahead?.Total ?? 0d) - (ahead?.Plain ?? 0d) > 0.5d
                ? $" (+{(ahead?.Total ?? 0d) - (ahead?.Plain ?? 0d):N0} insistence, not loot)"
                : "") + ", " +
            $"held {ahead?.Held ?? 0} of {fair.Musts}, content {ahead?.Content ?? 0d:N0}, " +
            $"propagation {ahead?.Propagation ?? 0d:N0}, walked {ahead?.Walked ?? 0d:N0}";

        // The left hand number: what is actually down, scored with the combinations actually SET on
        // the remnants rather than the ones the planner would pick. That is what makes it move when
        // you change a rune - the whole reason to show two numbers is that they can disagree, and a
        // left hand side that quietly re-picked your rewards for you could not.
        Verdict down;

        using (Spent.On("Tick/Scoring.Update/Placed"))
            down = placed.Length > 0
                ? Planner.Rate(Chosen(fair, scan, valuation, gc), new List<Vector2>(placed))
                : null;

        Yours = down?.Plain ?? 0d;
        Known = true;

        // **The best route this site has ever given up, filed from here because this is where a
        // whole route is scored.**
        //
        // It was first put in Planning, which meant it could only file a solve that began with
        // nothing placed - a mid-chain solve plans the remainder, against an origin that has moved
        // and a content pool with everything the placed blasts caught taken out of it, and that
        // number is not on the same scale as a whole route's. But the restriction was unnecessary:
        // this line already answers the question properly. `fair` is built from the detonator with
        // the complete pool, and `intended` is the explosives already down followed by the links
        // still planned - which IS the whole route, scored exactly as a route planned from scratch
        // would be. So a plan made half way along is filed on equal terms with one made at the
        // start, which is what it deserves.
        //
        // Plain rather than Total, for the same reason the readout shows Plain: a must-have bonus is
        // bigger than the site and would file whichever chain happened to be marked at the time.
        //
        // Only a finished plan. While a search runs `chain` is its best so far, and filing that
        // would record the search's working rather than its answer. See Kept.
        // The truncation this used to have to guard against cannot happen any more: the head is
        // the chain's own, so a route with a link missing off its front is not a shape that can be
        // built here to be offered. See the assembly above.
        if (!planning.Searching && Planned > 0d && intended.Count > 0)
        {
            var site = Detonator.DetonatorGridPosition(gc);

            Kept.Load(Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u), site,
                Safe.Read(gc, static g => g.Area.CurrentArea.Name, "") ?? "",
                Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, Vector2.Zero));

            // **Rated with nothing on the ground, for both chains.**
            //
            // The file answers "what is the best route this site has ever given up", which is a
            // claim about the site and not about what happens to be laid this minute. Rated against
            // the live environment it stops being that: the spacing rule applies to the explosives
            // actually down, and a link is exempt from it only where it sits ON one - which is true
            // of the route being walked and false of any other. So a filed route that DIVERGES from
            // the one on the ground has several links within the separation of somebody else's
            // explosives, is judged unplaceable, and scores at minus infinity.
            //
            // Which lets anything at all replace it. Measured across one session on a fifteen link
            // site: the file went 4,700, then 4,140, then 4,218, then 4,130 - each solve made while
            // part way along a diverging chain overwriting the best answer the site had produced
            // with the one currently on screen. The site forgot its own record by being asked about
            // it at the wrong moment.
            //
            // Clearing Placed puts both chains on the same clean ground, which is the only footing
            // on which "which of these two routes is better" means anything. The number on screen is
            // deliberately not rated this way - that one IS about the live situation.
            var filing = fair with { Placed = null };

            var standing = Kept.Chain is { Count: > 0 }
                ? Planner.Rate(filing, Kept.Chain).Plain
                : double.NegativeInfinity;

            // The candidate on the same terms, so what is written down is comparable with what a
            // later session rates it at rather than with this minute's obstacles.
            var offered = Planner.Rate(filing, intended).Plain;

            Kept.Offer(Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u), site,
                Safe.Read(gc, static g => g.Area.CurrentArea.Name, "") ?? "",
                Safe.Read(gc, static g => g.IngameState.Data.AreaDimensions, Vector2.Zero),
                intended, offered, standing);
        }

        if (_searching && Planned < _low)
        {
            _low = Planned;
            _lowOn = Breakdown;
        }
    }

    /// <summary>
    /// The same environment, with every remnant pinned to the combination it is actually set to.
    ///
    /// The objective normally lets the search pick a remnant's combination, because that is the
    /// decision it is making. For scoring what is ALREADY DOWN that is the wrong question: those
    /// remnants have been decided, by you or by the run, and a number that re-picked them would be
    /// reporting a chain nobody placed.
    ///
    /// A remnant with no choice made yet is left alone - it is still a decision, so the planner's
    /// pick is the honest valuation of it.
    ///
    /// The index is the link between the two halves: Weighing.Choices walks a remnant's rewards in
    /// order, so choice i belongs to reward i, and the chosen combination is found among them.
    ///
    /// **By recipe, because a name can cover two of them.** A remnant seen here offered two
    /// combinations both called "Refutation" - different rune counts, different propagation - and
    /// the first one a name search meets is not necessarily the one that is set. That pinned the
    /// placed chain to a combination nobody had chosen and left the two numbers over the button
    /// 2,094 apart on five explosives sitting exactly where the plan asked for them, with the
    /// readout underneath saying the reward picks agreed. They did agree; the pin did not.
    /// </summary>
    private static PlanEnvironment Chosen(PlanEnvironment env, Scan scan, Valuation valuation,
        GameController gc)
    {
        var site = Detonator.DetonatorGridPosition(gc);
        var remnants = site == Vector2.Zero ? scan.Targets : scan.At(site);
        var targets = new List<PlanTarget>(env.Targets.Count);
        var pinned = false;

        foreach (var target in env.Targets)
        {
            var forced = target;

            if (target.Kind == TargetKind.Remnant && target.Choices is { Length: > 0 })
            {
                foreach (var remnant in remnants)
                {
                    if (remnant.Kind != TargetKind.Remnant ||
                        Vector2.Distance(remnant.Grid, target.Grid) >= 1f)
                        continue;

                    var recipe = Safe.Read(() => valuation?.ChosenRecipeId(remnant.Entity), null);

                    // The name is the fallback, not the answer. Valuation.Top files a reward under
                    // the recipe's own id and falls back to its name only where the game states no
                    // id - and where that happens the two really are indistinguishable, so matching
                    // on the name is the honest answer rather than a second-best one.
                    var chosen = Safe.Read(() => valuation?.ChosenName(remnant.Entity), null);

                    if (string.IsNullOrWhiteSpace(recipe) && string.IsNullOrWhiteSpace(chosen))
                        break;

                    for (var i = 0; i < remnant.Rewards.Count && i < target.Choices.Length; i++)
                    {
                        var reward = remnant.Rewards[i];
                        var same = !string.IsNullOrWhiteSpace(recipe)
                            ? string.Equals(reward.Recipe, recipe, System.StringComparison.Ordinal)
                            : string.Equals(reward.Name, chosen, System.StringComparison.Ordinal);

                        if (!same)
                            continue;

                        forced = target with { Choices = [target.Choices[i]] };
                        pinned = true;

                        break;
                    }

                    break;
                }
            }

            targets.Add(forced);
        }

        return pinned ? env with { Targets = targets } : env;
    }

    /// <summary>
    /// A fingerprint of every remnant's chosen combination in this dig site.
    ///
    /// Cheap on purpose: a handful of remnants, one string each, folded into an int. It exists only
    /// to answer "has anything about the rewards changed since the last reading", so it has to
    /// differ when a choice differs and nothing more.
    ///
    /// Note what it does NOT do. The planner still values a remnant by the combination IT would
    /// take rather than the one currently set, because with "Overrule already chosen rewards" on it
    /// intends to change it back. The score moves when you change a rune because the propagation and
    /// the monsters downstream are re-read, not because the objective has adopted your choice.
    /// </summary>
    private static int Combinations(Scan scan, Valuation valuation, GameController gc)
    {
        var site = Detonator.DetonatorGridPosition(gc);
        var hash = 17;

        foreach (var target in site == Vector2.Zero ? scan.Targets : scan.At(site))
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            // The recipe rather than the reward name, for the reason given on Chosen: two
            // combinations can share a name, and switching between them changed nothing here - so
            // the scores kept the reading taken before the switch.
            var chosen = Safe.Read(() => valuation?.ChosenRecipeId(target.Entity), null);

            if (string.IsNullOrWhiteSpace(chosen))
                chosen = Safe.Read(() => valuation?.ChosenName(target.Entity), null) ?? "";

            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(chosen);
        }

        return hash;
    }


    /// <summary>
    /// The line for the status area: what is down over what the plan is worth.
    ///
    /// One fraction rather than two labelled numbers, because it is the same shape as every other
    /// progress readout - it starts at 0/208, climbs as each explosive lands, and reads 208/208
    /// when the plan has been placed. Two numbers with words in front of them made the reader work
    /// out which was which and what the gap meant.
    ///
    /// The left can exceed the right, and that is not a fault: place better than the plan and the
    /// fraction says so. While a search is running the right-hand number is the best chain found so
    /// far, so it climbs too.
    /// </summary>
    public string Line()
    {
        // Once the chain has gone off, the score is answering a question nobody is asking any more.
        // What is left to do is picking things up, so that is what the line says instead - and it
        // goes away entirely when there is nothing left to pick up.
        //
        // What is LEFT over what there was, not what has been done. It reads as a job list rather
        // than as progress, which is the useful way round here: the number counts down to nothing
        // and the line disappears at the same moment it reaches zero, so the two agree. Shown the
        // other way it said 3/10 while four things were visible on screen, which is a fraction
        // nobody can act on.
        if (_total > 0)
            return $"Loot: {_left}/{_total}";

        if (!Known || (Planned <= 0d && Down == 0))
            return "";

        // Grouped, because a dig site scores in the thousands once the weights are turned up and
        // "1234/5678" is two numbers nobody can compare at a glance. Invariant so it is always a
        // comma: the readout is not the place to discover what the machine's locale thinks.
        return "Score: " + Yours.ToString("N0", CultureInfo.InvariantCulture) +
               "/" + Planned.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The score line in three pieces, so the left hand number can be coloured on its own.
    ///
    /// The comparison is the whole content of this readout and it was being left to the reader to
    /// make. Colour says it at a glance: behind the plan, ahead of it, or level. Split here rather
    /// than in the drawing because the pieces have to agree with Line() exactly - two places
    /// formatting the same numbers is two places to get them different.
    ///
    /// Empty head means there is nothing to colour and the caller should draw Line() plainly, which
    /// is the loot count and the blank case.
    /// </summary>
    public (string Head, string Yours, string Tail, int Against) Parts()
    {
        if (_total > 0 || !Known || (Planned <= 0d && Down == 0))
            return ("", "", "", 0);

        // A tolerance rather than an equality test: these are doubles summed over dozens of terms,
        // so two chains that are the same chain can differ in the last bit. Half a point is far
        // below anything the readout shows and far above the arithmetic's noise.
        var against = Yours > Planned + 0.5d ? 1 : Yours < Planned - 0.5d ? -1 : 0;

        return ("Score: ",
            Yours.ToString("N0", CultureInfo.InvariantCulture),
            "/" + Planned.ToString("N0", CultureInfo.InvariantCulture),
            against);
    }
}
