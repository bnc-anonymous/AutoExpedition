using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// The chain, searched backwards, a link at a time, keeping the best few partial answers.
///
/// **Why backwards.** Propagation goes forwards, which is exactly the reason to build against it: a
/// remnant at link i pays its carried percentage over the monsters unearthed at links i..N, and that
/// is a SUFFIX quantity - it cannot depend on anything earlier, because propagation never reaches
/// back. So building the last link first means that when a link is placed, everything after it is
/// already decided: its remnant's carried value is exact, its combination is decided once and never
/// revisited, and the partial chain carries a true score rather than an estimate.
///
/// That invariant - every term is final the moment its link is placed - is what a beam needs, since
/// ranking partial chains is the only thing a beam does. Built forwards, a half-finished chain has to
/// be ranked on a number blind to most of what a remnant is worth.
///
/// **Why a beam rather than restarts.** The search this sits beside is greedy plus a one-link local
/// sweep plus randomised restarts, and it has a measured hole: a better chain that needs three links
/// to move TOGETHER is unreachable, because moving any one of them alone breaks the reach to its
/// neighbour. On a Sinkhole dig site that was worth eighteen points, and the restarts could not find
/// it because they only ever vary the FIRST link - they explore other directions out of the
/// detonator, where the improvement was a different ending to the same direction.
///
/// A beam explores endings by construction. It also costs N x width x branching, which is LINEAR in
/// the number of explosives - the property that matters for Grand Expeditions at fifteen or more,
/// where the space grows exponentially and a sample of whole chains does not keep up.
///
/// **Deterministic.** No randomness anywhere, so the same dig site gives the same chain every time.
/// The old search cannot promise that, and since the travel term was removed nothing breaks ties
/// between two routes covering the same content.
///
/// See expedition_solve_plan.md for the reasoning in full, including what this does NOT yet do:
/// there is no branch and bound here, and dominance pruning is the cheap version rather than a
/// comparison of covered sets.
/// </summary>
internal static class Beam
{
    /// <summary>
    /// How many partial chains survive each step.
    ///
    /// Where it starts, not where it stays: the search doubles the width and runs again until the
    /// budget is spent, so this only has to be narrow enough that the first answer arrives quickly.
    /// </summary>
    private const int Width = 240;

    /// <summary>
    /// How many candidates are tried as an extension of any one partial chain.
    ///
    /// Ordered by what they would add in coverage, which is cheap to work out, so this is "the best
    /// few next links" rather than an arbitrary subset. Only these get the real objective run on
    /// them - see Extend for why that separation is what makes the search affordable at all.
    /// </summary>
    private const int Fan = 24;

    /// <summary>The most extensions a partial will ever be allowed to offer.</summary>
    private const int Widest = 400;

    /// <summary>
    /// For each candidate, which targets its blast would catch and what they are worth together.
    ///
    /// Worked out once for the whole search. It is the input to the bound: to know whether a partial
    /// chain has left anything worth having for the links still to come, you have to be able to ask
    /// "what is the best blast available that does not repeat what is already covered", and that is
    /// this table intersected with what the partial has taken.
    /// </summary>
    private static (int[] Targets, float Solo)[] Reach(PlanEnvironment env, List<Vector2> candidates)
    {
        var table = new (int[], float)[candidates.Count];

        for (var c = 0; c < candidates.Count; c++)
        {
            var hits = new List<int>();
            var solo = 0f;

            for (var i = 0; i < env.Targets.Count; i++)
            {
                if (!Planner.Catches(env, candidates[c], env.Targets[i]))
                    continue;

                hits.Add(i);
                solo += MathF.Max(0f, env.Targets[i].Weight);
            }

            table[c] = (hits.ToArray(), solo);
        }

        return table;
    }

    /// <summary>
    /// Whether one candidate can be thrown to another, remembered.
    ///
    /// **The single most expensive thing in the search, and it was being recomputed thousands of
    /// times for the same pair.** Reaches is not a distance test when terrain checking is on: it
    /// walks the ground between the two points cell by cell and asks the scenery list as well. The
    /// sift asks it for every candidate of every partial at every step, which on a real dig site is
    /// a quarter of a million grid walks a pass - measured at two narrow passes in two and a half
    /// seconds, against an older search that finishes hundreds of restarts in the same time.
    ///
    /// The candidate list does not change for the life of the search, so the answer for a pair does
    /// not either. Two bits per pair in a flat array: one saying whether it has been asked, one
    /// holding the answer. Six hundred candidates is under half a megabyte and the walk happens
    /// once.
    /// </summary>
    private sealed class Links
    {
        private readonly PlanEnvironment _env;
        private readonly List<Vector2> _candidates;
        private readonly bool[] _asked;
        private readonly bool[] _yes;
        private readonly int _size;

        public Links(PlanEnvironment env, List<Vector2> candidates)
        {
            _env = env;
            _candidates = candidates;
            _size = candidates.Count;
            _asked = new bool[_size * _size];
            _yes = new bool[_size * _size];
        }

        public bool Between(int from, int to)
        {
            var at = from * _size + to;

            if (_asked[at])
                return _yes[at];

            var answer = Planner.Reaches(_env, _candidates[from], _candidates[to]);

            _asked[at] = true;
            _yes[at] = answer;

            return answer;
        }
    }

    /// <summary>
    /// An optimistic guess at what the links still to come could add, used to RANK partial chains.
    ///
    /// **This is the piece whose absence made the first version lose.** Propagation front-loads
    /// value - a remnant pays over everything unearthed after it - so an optimal chain puts its rich
    /// links early and its weakest link last. A beam built backwards judges a partial by its tail,
    /// which is exactly the part an optimal chain has least of: on a Sinkhole site the winning
    /// chain's last two links were worth 77 and 122 against 234 and 246 at the front, so its tail
    /// ranked nowhere and was thrown away long before the front that justified it could exist.
    ///
    /// Ranking by score alone therefore asks the wrong question. The right one is "what could this
    /// chain still become", and the answer has to credit a weak tail that has left the rich part of
    /// the site untouched for its prefix.
    ///
    /// Optimistic on purpose - it ignores reach, spacing and overlap between the remaining links,
    /// so it can only over-promise. That is what an admissible bound is for: it may keep a partial
    /// that turns out not to deliver, but it will not throw away one that would have.
    ///
    /// **Cheap on purpose too, and that is a correction rather than a preference.** The first
    /// version scanned every candidate to find the best few still available, for every candidate
    /// extension of every partial - quadratic in the candidate list, hundreds of millions of
    /// operations a step, and the search could not finish a single pass inside the time budget. It
    /// answered "no route" not because there was none but because it never got to the end of a
    /// step. This walks the target list instead, which is one pass over eighty-odd flags.
    /// </summary>
    private static double Ahead(PlanEnvironment env, bool[] covered, int left, double most)
    {
        if (left <= 0 || most <= 0d)
            return 0d;

        // What is left in the site at all. A suffix that has already taken everything rich has
        // nothing to promise a prefix however many links it has left, and that is the whole of the
        // discrimination this needs to make.
        var spare = 0d;

        for (var i = 0; i < covered.Length; i++)
        {
            if (!covered[i])
                spare += MathF.Max(0f, env.Targets[i].Weight);
        }

        // The best any one blast could be worth, times the links remaining, capped by what actually
        // remains. Still optimistic - it ignores reach, spacing and overlap between those links -
        // which is what keeps it admissible.
        var ceiling = left * most;

        return spare < ceiling ? spare : ceiling;
    }

    /// <summary>
    /// Searches, publishing each improvement as it is found.
    /// </summary>
    /// <param name="found">Called with every chain better than the last, for the live drawing.</param>
    /// <summary>
    /// What the last search actually did, for the dump.
    ///
    /// Written because the alternative is guessing. Four rounds of this search were tuned by
    /// reasoning about where the time must be going, and about half of those guesses were wrong -
    /// the quadratic bound, the premature widening guard, the sift that could not see propagation.
    /// A line saying how many passes ran, how wide they got and why it stopped settles in one
    /// reading what an afternoon of reasoning did not.
    /// </summary>
    public static string Last { get; private set; } = "has not run";

    /// <param name="settle">
    /// How long to carry on after the last improvement, or zero for "use the whole budget". The same
    /// rule the other search answers to, because the setting has to mean one thing: the player asked
    /// for a search that stops when it stops getting better, not for a search that stops when a
    /// particular algorithm has run out of its own ideas.
    /// </param>
    public static Plan Search(PlanEnvironment env, TimeSpan budget, TimeSpan settle,
        CancellationToken token, Action<List<Vector2>> found)
    {
        var candidates = Planner.Candidates(env, out var offered, out _);

        if (candidates.Count == 0)
        {
            return Plan.Empty with
            {
                Note = offered == 0
                    ? "no candidate positions could be generated from the content here"
                    : "every candidate position was refused as unplaceable",
            };
        }

        Planner.Positions = candidates.Count;

        var reach = Reach(env, candidates);
        var links = new Links(env, candidates);

        // The richest single blast in the site, which is the per-link ceiling the bound leans on.
        var most = 0d;

        foreach (var (_, solo) in reach)
            most = solo > most ? solo : most;
        var deadline = DateTime.UtcNow + budget;
        var best = new List<Vector2>();
        var score = double.NegativeInfinity;
        var shown = double.NegativeInfinity;

        // What the live drawing is told, as the beam goes rather than only when a pass ends.
        //
        // A pass at the widest setting is most of a solve, so publishing only at the end of one left
        // the overlay with almost nothing to show - and the point of drawing the search is watching
        // the chain take shape. Tracked apart from the authoritative best so the picture only ever
        // moves forwards: these are unpolished chains from the middle of a pass, and a later pass
        // starting over from one link must not rewind what the player is looking at.
        void Offer(List<Vector2> chain, double worth)
        {
            if (worth <= shown)
                return;

            shown = worth;
            found?.Invoke(new List<Vector2>(chain));
        }

        // Wider and wider until the budget is spent.
        //
        // A first pass at 240 finishes in a fraction of a solve and the rest of the time was being
        // thrown away. Width is the one knob that decides whether the step which commits the chain
        // to a part of the site was decided on evidence or on the first few greedy-looking suffixes,
        // so spending the budget on it is spending it in the right place.
        //
        // Each pass is independent and deterministic, and only a better chain is kept, so a wider
        // pass can never make the answer worse.
        var improved = DateTime.UtcNow;
        var began = DateTime.UtcNow;
        var passes = 0;
        var reached = 0;
        var why = "it ran out of fan";

        // The FAN grows between passes, not the width.
        //
        // Measured, and it settles an argument this search had been losing for several rounds: three
        // passes at widths 240, 480 and 960 returned the identical chain and the identical 1,125.5.
        // Keeping more partial chains cannot help when every one of them is offered the same narrow
        // set of next links - the beam was exploring a cone, and a wider beam explores the same cone
        // with more of the same in it.
        //
        // The fan is what decides how much of the site a partial is ever allowed to consider, so it
        // is the knob that changes the shape of the search rather than its bookkeeping. Width stays
        // where it is, since the evidence says it was never binding.
        for (var fan = Fan; fan <= Widest; fan *= 2)
        {
            // When this pass has to be over by: the budget, or the improvement window, whichever
            // comes first.
            //
            // ONE deadline, computed here and handed down, because every check that used a different
            // one was a way for the search to overrun. The settle window was tested between passes
            // while the passes themselves ran to the budget - fifteen seconds against the one and a
            // half the player asked for - and the "is there time to widen" guard was measured
            // against the budget too, so it waved through passes that could never finish inside the
            // window. Recomputed each time round, since an improvement moves the window.
            var stop = settle > TimeSpan.Zero
                ? deadline < improved + settle ? deadline : improved + settle
                : deadline;

            if (token.IsCancellationRequested || DateTime.UtcNow > stop)
            {
                why = token.IsCancellationRequested ? "it was cancelled" : "the clock ran out";

                break;
            }

            passes++;
            reached = fan;

            // No guard on whether the next pass can FINISH, and that is deliberate.
            //
            // There was one, refusing to start a pass whose estimated cost ran past the window. It
            // stopped the search with a third of the budget unspent: once a pass took half a second
            // the estimate for the next was a full one, which never fit, so the widening stopped
            // while the clock ran. The premise was wrong - an abandoned pass is not wasted. Once
            // hands back the best complete chain it reached before the clock hit, and a wider pass
            // shares its early steps with the narrower one, so what comes back is usually at least
            // as good. Starting one that cannot finish costs nothing but the time that was going
            // spare anyway.

            var chain = Once(env, candidates, reach, most, links, fan, stop, token, Offer, out var bit);

            if (chain.Count == 0)
                continue;

            // Polished before it is judged.
            //
            // The beam's business is the SHAPE of the chain - which part of the site it works and in
            // what order - and it is bad at the last few percent, because a suffix that looks rich on
            // its own can leave a link stranded once the chain has to reach the detonator. Measured:
            // a beam chain covered 37 markers where the old search covered 44, with one link doubling
            // back over ground an earlier one had already taken.
            //
            // The one-link sweep is exactly the thing that fixes that and exactly the thing that
            // cannot find the shape on its own - it was the hole in the old search. So the two are
            // complementary rather than rivals, and running both costs one sweep.
            // Skipped when the clock has already run out, because it is not free: the sweep walks
            // every candidate for every link and can run to thousands of scoring passes on a big
            // site. Unguarded it was the last place the search could overrun the window after
            // everything else had been made to respect it.
            if (DateTime.UtcNow < stop)
                chain = Planner.Improve(env, candidates, chain);

            var worth = Planner.Evaluate(env, chain).Total;

            if (worth > score)
            {
                score = worth;
                best = chain;
                improved = DateTime.UtcNow;
                Offer(best, worth);
            }

            // The width never bit, so the beam held every partial there was and a wider one would
            // search the same space to the same answer. Nothing left to try.
            if (!bit)
            {
                why = "a wider fan stopped changing anything";

                break;
            }
        }

        Last = $"{passes} pass{(passes == 1 ? "" : "es")}, widest fan {reached}, " +
               $"{(DateTime.UtcNow - began).TotalMilliseconds:0} ms, stopped because {why}" +
               (best.Count == 0 ? ", found nothing" : $", best {score:N1} over {best.Count} links");

        // Nothing at all means the beam never finished a step, not that the site has no chain in
        // it. A greedy pass costs a fraction of a solve and always produces something legal, so the
        // answer to running out of time is a worse plan rather than no plan - "no route" on a dig
        // site full of content reads as the plugin being broken, and it would be right to.
        if (best.Count == 0)
            best = Planner.Greedy(env, candidates, null, 1);

        return best.Count == 0
            ? Plan.Empty with { Note = "nothing reachable from the detonator is worth placing on" }
            : Planner.Describe(env, best);
    }

    /// <summary>
    /// Follows a chain somebody else built through the search, and says where it is lost.
    ///
    /// **Built because eight rounds of tuning this search produced nothing and three measurements
    /// each produced a fact.** A chain laid by hand scored a hundred points above anything the
    /// search found, every link of it legal and every link within three grid of a position the
    /// search was free to pick - so the good answer is inside the space and the search walks past
    /// it. This says at which step, and what the search thought of it at the time.
    ///
    /// Three outcomes, and they point in different directions:
    ///
    /// - **Never offered.** The sift cut it before the objective saw it, so the fan is the ceiling
    ///   and no amount of beam width helps.
    /// - **Offered and ranked poorly.** The ordering is wrong about it - the bound, or the sift's
    ///   idea of what a link is worth.
    /// - **Offered, ranked well, trimmed anyway.** The beam is simply too narrow, which is the only
    ///   one of the three that widening fixes.
    /// - **Survives to the end and loses on score.** Then the objective prefers the search's chain
    ///   and the disagreement is about the weights, not the search.
    ///
    /// Matched with a tolerance, because a chain placed by hand lands near the generated candidates
    /// rather than on them, and a trace that demanded exactness would report the truthful but
    /// useless answer that none of it was ever in the beam.
    /// </summary>
    public static List<string> Trace(PlanEnvironment env, List<Vector2> want)
    {
        var text = new List<string>();

        if (want == null || want.Count == 0)
            return text;

        var candidates = Planner.Candidates(env, out _, out _);

        if (candidates.Count == 0)
        {
            text.Add("  no candidates were generated, so there is nothing to trace through");

            return text;
        }

        var reach = Reach(env, candidates);
        var most = 0d;

        foreach (var (_, solo) in reach)
            most = solo > most ? solo : most;

        var links = new Links(env, candidates);
        var beam = new List<Partial>
        {
            new(new List<Vector2>(), 0d, new bool[env.Targets.Count], 0d, 0d, -1),
        };

        for (var step = 0; step < want.Count; step++)
        {
            var next = new List<Partial>();

            var spread = Spread(Fan, beam.Count, candidates.Count);

            foreach (var partial in beam)
                Extend(env, candidates, reach, most, links, spread, DateTime.MaxValue, partial, next);

            if (next.Count == 0)
            {
                text.Add($"  step {step + 1}: the search had nothing legal to extend with");

                break;
            }

            next.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));

            // The last step+1 links of the chain being traced: what a suffix of this length would
            // have to look like to be on the way to it.
            var tail = want.GetRange(want.Count - (step + 1), step + 1);
            var rank = -1;

            for (var i = 0; i < next.Count; i++)
            {
                if (!Same(next[i].Chain, tail))
                    continue;

                rank = i;

                break;
            }

            if (rank < 0)
            {
                text.Add($"  step {step + 1}: NEVER OFFERED - no extension the sift allowed leads " +
                         "to this chain, so the fan is the ceiling and width cannot help");

                break;
            }

            var kept = Trim(next, Width);
            var survived = false;

            foreach (var partial in kept)
                survived |= Same(partial.Chain, tail);

            text.Add($"  step {step + 1}: offered at rank {rank + 1} of {next.Count}, " +
                     $"scoring {next[rank].Score:N1} against the best {next[0].Score:N1} - " +
                     (survived
                         ? $"kept (beam holds {kept.Count})"
                         : $"TRIMMED AWAY, the beam keeps {kept.Count} - widen and it survives"));

            if (!survived)
                break;

            beam = kept;
        }

        return text;
    }

    /// <summary>
    /// How many extensions each partial may offer at this step.
    ///
    /// **The first step takes every candidate, and that is not an optimisation but a correction.**
    /// The beam begins with one empty suffix, so a fan of any size is the entire search: it decides
    /// the LAST link of the chain, which in a backward construction is the decision everything else
    /// follows from. Traced on a real site, the last link of a chain worth a hundred and seventeen
    /// points more than the search found was never offered at step one - first at a fan of 24, then
    /// again at 240, because it covers a hundred and twenty of content and a couple of hundred spots
    /// cover more while making worse chains.
    ///
    /// The sift exists to keep the real objective off candidates that cannot survive, which is worth
    /// it when there are hundreds of parents. With one parent it saves six hundred scoring passes -
    /// nothing - and costs the answer. So it does not run.
    ///
    /// After that the fan applies, but never so tightly that the beam cannot be filled: enough
    /// children to fill the width, however few parents there are.
    /// </summary>
    private static int Spread(int fan, int parents, int candidates) =>
        parents <= 1 ? candidates : Math.Max(fan, Width / Math.Max(1, parents));

    /// <summary>Whether a suffix is the one being traced, allowing for hand placement.</summary>
    private static bool Same(List<Vector2> chain, List<Vector2> want)
    {
        if (chain.Count != want.Count)
            return false;

        for (var i = 0; i < chain.Count; i++)
        {
            if (Vector2.Distance(chain[i], want[i]) > 3.5f)
                return false;
        }

        return true;
    }

    /// <summary>One pass of the beam at a given width, returning the best chain it reached.</summary>
    /// <param name="stop">
    /// When to abandon, already narrowed to the sooner of the budget and the improvement window.
    /// This method does not know which of the two it is looking at, and must not: one clock.
    /// </param>
    private static List<Vector2> Once(PlanEnvironment env, List<Vector2> candidates,
        (int[] Targets, float Solo)[] reach, double most, Links links, int fan, DateTime stop,
        CancellationToken token, Action<List<Vector2>, double> offer, out bool bit)
    {
        bit = false;

        var best = new List<Vector2>();
        var score = double.NegativeInfinity;

        // Suffixes, shortest first. A step prepends one link to every entry, so after k steps the
        // beam holds the best k-link endings the search has seen.
        var beam = new List<Partial>
        {
            new(new List<Vector2>(), 0d, new bool[env.Targets.Count], 0d, 0d, -1),
        };

        for (var step = 0; step < env.Explosives; step++)
        {
            var next = new List<Partial>();

            // Enough children to fill the beam, however few parents there are.
            //
            // **The fan is per partial, so with one partial it was the whole search.** At the first
            // step the beam holds a single empty suffix, so a fan of twenty four meant twenty four
            // possible LAST LINKS out of six hundred - and in a chain built backwards the last link
            // decides everything that follows. The width of two hundred and forty went unused at the
            // one step where it mattered most, which is why widening the beam never changed the
            // answer and doubling the fan to ninety six did not either.
            //
            // Traced on a real site: the last link of a chain worth a hundred points more than the
            // search found was NEVER OFFERED at step one. It was not ranked badly or trimmed away -
            // it was never generated.
            var spread = Spread(fan, beam.Count, candidates.Count);

            foreach (var partial in beam)
            {
                // Abandoned outright, not broken out of.
                //
                // Breaking out of this loop alone left a half-built step that the rest of the pass
                // carried on refining, so a wide pass overran the budget by however long the steps
                // after it took. The clock has to end the PASS, and a pass abandoned part way is
                // worth nothing anyway - its answer is a chain built from a fraction of the beam.
                if (token.IsCancellationRequested || DateTime.UtcNow > stop)
                    return best;

                Extend(env, candidates, reach, most, links, spread, stop, partial, next);
            }

            if (next.Count == 0)
                break;

            next.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));
            beam = Trim(next, Width);

            // Whether the width actually threw anything away at this step.
            bit |= beam.Count < next.Count;

            // The best chain of this length that actually starts at the detonator. A shorter chain
            // is a real answer - the site may not have N reachable spots worth using - so every
            // length is offered rather than only the last.
            foreach (var partial in beam)
            {
                if (!Planner.Reaches(env, env.Origin, partial.Chain[0]) || partial.Score <= score)
                    continue;

                score = partial.Score;
                best = new List<Vector2>(partial.Chain);
                offer?.Invoke(best, score);

                break;
            }
        }

        return best;
    }

    /// <summary>
    /// One partial chain: a suffix, and what it is worth on its own.
    ///
    /// The score is the objective applied to the suffix as if it were the whole chain, which is the
    /// honest partial score - every term in it is already final, and prepending links only ever adds
    /// to it. See the note on the class.
    /// </summary>
    /// <param name="Covered">
    /// Which targets this suffix already catches, so the bound can ask what is left without walking
    /// the chain again. One flag per target, copied on each extension - a few dozen bytes per entry
    /// against a coverage recount per candidate per step.
    /// </param>
    /// <param name="Rank">
    /// Score plus what the remaining links could optimistically add. What the beam SORTS on, while
    /// Score remains what the chain is actually worth. Keeping the two apart matters: the answer is
    /// judged on Score and only the pruning uses Rank.
    /// </param>
    /// <param name="Monsters">
    /// Monster weight this suffix unearths. Carried so the sift can value a remnant by what it would
    /// actually propagate over, which is the thing a coverage-only number cannot see.
    /// </param>
    /// <param name="Head">
    /// Which candidate the suffix begins at, or -1 for the empty suffix. An index rather than a
    /// position so reachability can be looked up in a table instead of walked over the terrain.
    /// </param>
    private readonly record struct Partial(List<Vector2> Chain, double Score, bool[] Covered,
        double Rank, double Monsters, int Head);

    /// <summary>
    /// Every worthwhile link that could come immediately before this suffix.
    ///
    /// Feasibility first and cheaply: within reach of the suffix's head, far enough from every link
    /// already in it, and not so far from the detonator that the links still to come could never
    /// bridge the gap. That last one is the admissible filter that makes a long chain affordable -
    /// with m links left the open end has to be within m x reach of the origin, and a suffix that
    /// fails it is dead however good it looks.
    /// </summary>
    private static void Extend(PlanEnvironment env, List<Vector2> candidates,
        (int[] Targets, float Solo)[] reach, double most, Links links, int fan, DateTime stop,
        Partial partial, List<Partial> into)
    {
        var left = env.Explosives - partial.Chain.Count;
        var bridge = left * env.Reach;

        // Sifted cheaply first, scored properly second.
        //
        // **This is what makes the search affordable, and its absence is what made it lose.** The
        // objective walks the whole chain against every marker in the site, and it was being run for
        // all six hundred candidates of every partial at every step - hundreds of thousands of full
        // scoring passes, most of them on candidates with no chance of surviving the fan. At one and
        // a half seconds the search got through one narrow pass and scored 955 where the older
        // search scored 1,156.
        //
        // What a candidate ADDS in coverage is computable in a few operations from the table of what
        // each blast catches, and it orders candidates well enough to choose which two dozen deserve
        // the real objective. The cheap number decides who gets measured; the real one decides who
        // wins. It is never used to rank a partial against another partial.
        var sifted = new List<(int At, double Gain)>();

        for (var at = 0; at < candidates.Count; at++)
        {
            // The clock, inside the candidate loop rather than only around it.
            //
            // Reachability stopped being a distance test when links started being routed round
            // terrain, so one pass of this loop can now cost a path search. Checked once per partial
            // that was enough to hang the plugin outright - the task never finished and the readout
            // sat on "Solving 0.0s".
            if ((at & 63) == 0 && DateTime.UtcNow > stop)
                break;

            var candidate = candidates[at];

            if (partial.Head >= 0 && !links.Between(at, partial.Head))
                continue;

            if (!Planner.Spaced(env, partial.Chain, candidate))
                continue;

            // Could the rest of the chain still get here from the detonator?
            if (Vector2.DistanceSquared(env.Origin, candidate) > bridge * bridge)
                continue;

            // What this blast would unearth, then what that is worth.
            //
            // Two passes over a handful of markers, because the second needs the first: a remnant
            // propagates over the monsters this blast digs up as well as everything after it, so the
            // monster weight has to be known before the remnant can be priced. Counting coverage
            // alone valued a remnant at its base weight and ignored the carried half - which on
            // these weights is the larger half - so the sift was cutting exactly the candidates the
            // objective cares most about.
            var dug = 0d;

            foreach (var i in reach[at].Targets)
            {
                if (partial.Covered[i])
                    continue;

                var target = env.Targets[i];

                if (target.Kind is TargetKind.Monster or TargetKind.Elite)
                    dug += MathF.Max(0f, target.Weight);
                else if (target.Waves > 0f)
                    dug += target.Waves;
            }

            var downstream = partial.Monsters + dug;
            var gain = 0d;

            foreach (var i in reach[at].Targets)
            {
                if (partial.Covered[i])
                    continue;

                var target = env.Targets[i];

                gain += MathF.Max(0f, target.Weight);

                if (target.Choices is { Length: > 0 })
                    gain += target.Rough((float)downstream);
            }

            sifted.Add((at, gain));
        }

        if (sifted.Count == 0)
            return;

        sifted.Sort(static (a, b) => b.Gain.CompareTo(a.Gain));

        var picks = new List<Partial>();

        for (var n = 0; n < sifted.Count && n < fan; n++)
        {
            var at = sifted[n].At;
            var grown = new List<Vector2>(partial.Chain.Count + 1) { candidates[at] };

            grown.AddRange(partial.Chain);

            var covered = (bool[])partial.Covered.Clone();
            var dug = 0d;

            foreach (var i in reach[at].Targets)
            {
                if (!covered[i])
                {
                    var target = env.Targets[i];

                    if (target.Kind is TargetKind.Monster or TargetKind.Elite)
                        dug += MathF.Max(0f, target.Weight);
                    else if (target.Waves > 0f)
                        dug += target.Waves;
                }

                covered[i] = true;
            }

            var worth = Planner.Evaluate(env, grown).Total;

            picks.Add(new Partial(grown, worth, covered,
                worth + Ahead(env, covered, env.Explosives - grown.Count, most),
                partial.Monsters + dug, at));
        }

        picks.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));
        into.AddRange(picks);
    }

    /// <summary>
    /// The beam, cut to width, without letting one good opening crowd out every other.
    ///
    /// Kept at most twice per head position. Two suffixes starting at the same link are competing to
    /// be the same thing, and the better of them nearly always wins later too - so keeping dozens of
    /// near-identical variants spends the whole width on one corner of the site. This is the cheap
    /// half of dominance pruning; the real version compares covered sets and is worth adding when
    /// there is a measurement saying the width is the limit.
    /// </summary>
    private static List<Partial> Trim(List<Partial> sorted, int width)
    {
        var kept = new List<Partial>(width);
        var heads = new Dictionary<(int X, int Y), int>();

        foreach (var partial in sorted)
        {
            if (kept.Count >= width)
                break;

            var head = partial.Chain[0];
            var cell = ((int)MathF.Round(head.X), (int)MathF.Round(head.Y));

            heads.TryGetValue(cell, out var already);

            if (already >= 2)
                continue;

            heads[cell] = already + 1;
            kept.Add(partial);
        }

        return kept;
    }
}
