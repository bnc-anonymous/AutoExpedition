using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace AutoExpedition;

/// <summary>
/// The next explosive only, chosen from the bands' edges, with every option listed.
///
/// **A mode that answers one question, because the full search cannot be asked one.** Searching
/// whole chains reports a score, and a score says nothing about whether a particular place was ever
/// on the table - which is exactly what is in doubt. A player reading the regions view can build the
/// best chain known on this site by picking rings off the screen; the search, given the same bands,
/// lands twenty points short. Either it does not have those rings or it does not use them, and no
/// amount of looking at chain scores separates the two.
///
/// So this scores every edge as the NEXT link from wherever the chain now stands, and lists them all
/// with their worth, their distance, and whether they are legal at all. Place one explosive, press
/// again, and the list becomes the following step's options. Step by step, the drawn rings and the
/// search's choices can be held against each other directly.
/// </summary>
internal static class Edges
{
    /// <summary>
    /// How many of the site's own spots a dead end may fall back on.
    ///
    /// Enough to get the chain moving again, few enough that it stays a way out rather than a
    /// second search over the whole site.
    /// </summary>
    private const int Spare = 16;

    /// <summary>What the last pass found, for the dump.</summary>
    public static string Last { get; private set; } = "has not run";

    public static Plan Search(PlanEnvironment env, TimeSpan budget, TimeSpan settle,
        CancellationToken token, Action<List<Vector2>> found = null)
    {
        var clock = Stopwatch.StartNew();
        var candidates = Planner.Candidates(env, out var offered, out _);

        if (candidates.Count == 0)
        {
            Last = $"no candidates at all, of {offered} offered";

            return Plan.Empty with { Note = "the terrain check refused every spot" };
        }

        var seeding = env.Seeding ?? new SeedFamilies(3, 3, 0, 0f, 0.02f, true);
        var bands = Planner.Regions(env, candidates, seeding.Pairs, seeding.Rares, seeding.Slack,
            seeding.Heavy);

        // Published so the regions view draws what was actually used rather than working out its
        // own, which is what made every disagreement between the two unanswerable.
        Planner.Searched = bands;

        // One flat list. Which band a cell belongs to constrains nothing here.
        var places = new List<Vector2>();

        foreach (var (_, corners, _) in bands)
            foreach (var corner in corners)
                if (!places.Contains(corner))
                    places.Add(corner);

        if (places.Count == 0)
        {
            Last = $"{bands.Count} bands but no edges";

            return Plan.Empty with { Note = "no band edges to build a chain from" };
        }

        var chain = new List<Vector2>();
        var options = new List<(Vector2 At, double Worth, bool Legal)>();
        var best = new List<Vector2>();
        var top = double.NegativeInfinity;

        // The whole chain, when asked for.
        //
        // Plain depth first, memoised, with a time cap - and it says whether it finished. Exhausting
        // is the point: a complete walk that does not find a chain proves the chain is not there,
        // which is the one thing a sampler can never tell you. At thirty two edges it finished in
        // 1.9s; at forty nine the space is roughly five times that, so it wants ten seconds or so.
        //
        // Nought is every explosive the detonator has left, which is the ordinary case - the number
        // is not fixed, since modifiers add to it, so there is no cap worth writing down. One is the
        // next bomb only and anything between is a cap for a tree too wide to walk to the end.
        if (seeding.Links != 1)
        {
            var whole = Deep(env, places, budget, settle, token, found, clock, bands, seeding);

            return whole;
        }

        foreach (var place in places)
        {
            if (token.IsCancellationRequested)
                break;

            var legal = Planner.Reaches(env, env.Origin, place) &&
                        Planner.Spaced(env, chain, place, 0);

            chain.Add(place);

            var worth = Planner.Score(env, chain);

            chain.RemoveAt(chain.Count - 1);
            options.Add((place, worth, legal));

            if (!legal || worth <= top)
                continue;

            top = worth;
            best = new List<Vector2> { place };
            found?.Invoke(new List<Vector2>(best));
        }

        options.Sort((a, b) => b.Worth.CompareTo(a.Worth));

        // On screen the way the regions view draws: a dot per grid cell, a ring on the ones the
        // chain could actually use, one colour per band.
        //
        // Not blast circles. A radius ring at every option covers the site in overlapping discs and
        // says nothing about which cell is which - and the question here is precisely which cells
        // are on offer, one grid square at a time.
        // **This bomb's options only.** Drawing every edge on the site answers "where are the bands"
        // - which the regions view already answers - and buries the question being asked here, which
        // is where THIS explosive may go. An edge out of reach of the chain's current end is not an
        // option for it and is left off entirely.
        var usable = new HashSet<(int, int)>();

        foreach (var (at, _, ok) in options)
            if (ok)
                usable.Add(((int)MathF.Round(at.X), (int)MathF.Round(at.Y)));

        var drawn = new List<(string Name, List<Vector2> Cells, List<Vector2> All)>();

        foreach (var (name, corners, _) in bands)
        {
            var here = new List<Vector2>();

            foreach (var corner in corners)
                if (usable.Contains(((int)MathF.Round(corner.X), (int)MathF.Round(corner.Y))))
                    here.Add(corner);

            if (here.Count > 0)
                drawn.Add((name, here, here));
        }

        Planner.Show(drawn);

        var listed = new List<string>();

        foreach (var (name, corners, all) in bands)
        {
            var where = new List<string>();

            foreach (var corner in corners)
                where.Add($"({corner.X:0},{corner.Y:0})");

            listed.Add($"      {name,-14} {all.Count,4} cells -> {corners.Count} edges: " +
                       string.Join(" ", where));
        }

        var ranked = new List<string>();
        var legals = 0;

        foreach (var (at, worth, legal) in options)
        {
            if (legal)
                legals++;

            ranked.Add($"      {(legal ? "  " : "XX")} ({at.X:0},{at.Y:0}) {worth,9:N1}  " +
                       $"{Vector2.Distance(env.Origin, at),5:0} grid out");
        }

        Last = $"NEXT LINK ONLY. {places.Count} edges over {bands.Count} bands, " +
               $"{options.Count} options of which {legals} legal, {clock.ElapsedMilliseconds}ms, " +
               $"best {top:N1}" + Environment.NewLine +
               $"      slack {seeding.Slack:P1}, per pair {seeding.Pairs}, per rare {seeding.Rares}, " +
               $"cells per band {seeding.PerBand}, reach {env.Reach:0}, apart {env.Apart:0}, " +
               $"placed {env.Placed?.Count ?? 0}, origin ({env.Origin.X:0},{env.Origin.Y:0})" +
               Environment.NewLine + string.Join(Environment.NewLine, listed) +
               Environment.NewLine +
               "      every edge as the next link, best first (XX = unreachable or too close):" +
               Environment.NewLine + string.Join(Environment.NewLine, ranked);

        return best.Count == 0
            ? Plan.Empty with { Note = "no band edge is reachable from here" }
            : Planner.Describe(env, best);
    }

    /// <summary>
    /// A link at a time, with the bands redrawn after each - which is the only way the best chain
    /// can be built at all.
    ///
    /// **The edges are state dependent, and that is not a detail.** A band is the ground that catches
    /// some content for near enough the best score, so the moment a blast takes that content the
    /// band moves. Measured on one site: the cell the best known chain uses for its second link is
    /// not an edge at the start - its band is five cells a good way off - and it becomes one only
    /// after the first explosive is placed. An exhaustive walk of the starting edges therefore
    /// cannot produce that chain however long it runs, and did not: 840,328 chains, every one of
    /// them, topping out twenty two points short.
    ///
    /// So the bands are worked out again for every partial chain at every step, against the content
    /// that is still there. That is the expensive way round and there is no cheap one: the picture
    /// the choice is made from has to be the picture that will be true when the choice is taken.
    ///
    /// A beam rather than a single line, because ranking partial chains is unreliable here - a
    /// remnant caught early is worth its rune over everything after it, which a prefix cannot see -
    /// so several are carried and the full chain decides between them at the end.
    /// </summary>
    private static Plan Deep(PlanEnvironment env, List<Vector2> places, TimeSpan budget,
        TimeSpan settle, CancellationToken token, Action<List<Vector2>> found, Stopwatch clock,
        List<(string Name, List<Vector2> Cells, List<Vector2> All)> bands, SeedFamilies seeding)
    {
        var candidates = Planner.Candidates(env, out _, out _);
        var stop = settle > TimeSpan.Zero ? settle : budget;
        var chain = new List<Vector2>();
        var best = new List<Vector2>();
        var top = double.NegativeInfinity;
        var drawn = 0;
        var reused = 0;
        var grown = 0;
        var kept = new Dictionary<(ulong, ulong, ulong, ulong),
            List<(string Name, List<Vector2> Cells, List<Vector2> All)>>();
        var deep = new int[env.Explosives + 1];

        // How long a chain this is allowed to build. See the note on the call above: nought asks for
        // the whole chain, and a cap above what is left is simply what is left.
        var cap = seeding.Links > 0 ? Math.Min(seeding.Links, env.Explosives) : env.Explosives;

        // Why nodes give up: nothing left to catch, no bands to draw, nothing legal to move to.
        var stopped = new int[3];
        // Nought is every option, not one.
        //
        // **Trimming is what costs score here.** Measured on this site: twenty branches lost twenty
        // points against no limit at all, because the link that matters is ranked by its immediate
        // worth and that ranking has been wrong at every turn. So the honest default is no trim, and
        // the setting exists for sites where the tree turns out too wide to walk.
        var wide = seeding.Branches > 0 ? seeding.Branches : int.MaxValue;
        var tally = Planner.Begin(env, env.Explosives);

        // How deep one lookahead goes before the chain commits to a link.
        //
        // **This decides whether the idea survives a Grand Expedition.** Searching every chain is
        // exponential in links: nine branches a node is half a million placements over five links
        // and two hundred million million over fifteen. A horizon makes it linear - look k ahead,
        // keep the first, look again - so fifteen explosives cost fifteen small searches instead of
        // one impossible one.
        //
        // Nought means no horizon, which is the exhaustive search that is affordable at five links
        // and is the yardstick a horizon must be held to: this site's best chain is worth 1,319.8,
        // so a horizon that reaches it here is one worth trusting there.
        var horizon = seeding.Horizon > 0 ? seeding.Horizon : env.Explosives;
        var settled = 0;

        // Recurse: place, redraw, look, place again.
        //
        // Every node works its bands out afresh against what its own chain has already taken, which
        // is the only picture the next choice can honestly be made from. Branching on the best few
        // rather than on all of them is the one concession to arithmetic: eleven edges over five
        // links is a hundred and sixty thousand redraws, which is an hour, and four is a thousand,
        // which is half a minute.
        void Step()
        {
            if (chain.Count == cap || token.IsCancellationRequested ||
                clock.Elapsed > stop)
                return;

            // Past the horizon this lookahead is done. The chain commits its next link below and
            // looks again from there, which is what makes the cost linear in links rather than
            // exponential.
            if (chain.Count - settled >= horizon)
                return;

            var gone = new HashSet<int>();
            var caught = (0UL, 0UL, 0UL, 0UL);

            for (var t = 0; t < env.Targets.Count; t++)
            {
                var taken = false;

                foreach (var link in chain)
                    taken |= Planner.Catches(env, link, env.Targets[t]);

                if (!taken)
                    continue;

                gone.Add(t);

                // Which markers are gone, as four words - the whole of what a redraw depends on.
                var bit = 1UL << (t & 63);

                caught = (t >> 6) switch
                {
                    0 => (caught.Item1 | bit, caught.Item2, caught.Item3, caught.Item4),
                    1 => (caught.Item1, caught.Item2 | bit, caught.Item3, caught.Item4),
                    2 => (caught.Item1, caught.Item2, caught.Item3 | bit, caught.Item4),
                    _ => (caught.Item1, caught.Item2, caught.Item3, caught.Item4 | bit),
                };
            }

            if (gone.Count >= env.Targets.Count)
            {
                stopped[0]++;

                return;
            }

            // **Remembered by what is left, not by how the chain got there.** Bands depend only on
            // the content still standing, so two chains that have swept up the same markers - in
            // any order, from any cells - want the identical redraw. On a five link chain most of
            // the tree is permutations of the same few sets, so this is the difference between a
            // recomputation per node and one per distinct situation.
            if (!kept.TryGetValue(caught, out var here))
            {
                here = Planner.Regions(env, candidates, seeding.Pairs, seeding.Rares,
                    seeding.Slack, seeding.Heavy, gone);

                kept[caught] = here;
                drawn++;
            }
            else
            {
                reused++;
            }

            var from = chain.Count == 0 ? env.Origin : chain[^1];
            var options = new List<(Vector2 At, double Worth)>();
            var cells = new List<Vector2>();

            foreach (var (_, corners, _) in here)
                foreach (var corner in corners)
                    cells.Add(corner);

            // **When the heavy content runs out, the bands do too - and the chain still has links.**
            // A band is built around a remnant or a rare, so once those are all caught there are no
            // families to draw and no places to offer, even though chests and monsters worth
            // hundreds of points are still standing. Measured: the walk died at three links every
            // time and scored 683, because the last two explosives had nowhere the mode would name.
            //
            // So when there are no bands left, every candidate is a place: the site is only chests
            // and monsters by then, nothing is worth building a family around, and the ordinary
            // ranking by what a blast catches is exactly the right way to choose.
            if (cells.Count == 0)
            {
                stopped[1]++;
                cells = candidates;
            }

            foreach (var corner in cells)
                {
                    if (chain.Contains(corner) || !Planner.Reaches(env, from, corner) ||
                        !Planner.Spaced(env, chain, corner, chain.Count))
                        continue;

                    chain.Add(corner);

                    // Carried, not recomputed. See Planner.Running: the four links already in the
                    // chain caught what they caught and cannot catch anything else, so only this
                    // link's coverage is new.
                    var worth = Planner.Push(env, tally, chain, corner);

                    // Removed first: Pop reads the position from the chain's length, and undoing a
                    // link while it is still on the end aims the undo at the wrong step - so it
                    // matched nothing and every candidate tried stayed folded into the tally.
                    chain.RemoveAt(chain.Count - 1);
                    Planner.Pop(env, tally, chain, corner);
                    options.Add((corner, worth));
                }

            // Nothing the bands offer can be reached from here - so use the site.
            //
            // **Having no legal edge is not the same as having nothing to do.** The bands can be
            // full of places the chain cannot get to from where it stands: all used, all too close,
            // all out of reach. The fallback only fired when there were no bands at all, so a chain
            // that ran out of usable edges simply stopped - measured, a four link chain on a site
            // with five explosives, leaving one unplaced and a few hundred points on the ground.
            if (options.Count == 0 && cells != candidates)
            {
                stopped[2]++;

                // The best few of the site, not all of it.
                //
                // **Opening the whole candidate set at a dead end explodes the tree.** Measured: a
                // thousand candidates at depth four turned six thousand chains into three hundred
                // and seventy thousand, the first lookahead ate the entire window, and the chain
                // never committed past its first link - so the fallback meant to add a fifth link
                // cost the chain everything after the first. A dead end wants a way out, not a
                // second search.
                var spare = new List<(Vector2 At, double Worth)>();

                foreach (var corner in candidates)
                {
                    if (chain.Contains(corner) || !Planner.Reaches(env, from, corner) ||
                        !Planner.Spaced(env, chain, corner, chain.Count))
                        continue;

                    chain.Add(corner);

                    var worth = Planner.Push(env, tally, chain, corner);

                    chain.RemoveAt(chain.Count - 1);
                    Planner.Pop(env, tally, chain, corner);
                    spare.Add((corner, worth));
                }

                spare.Sort((a, b) => b.Worth.CompareTo(a.Worth));

                for (var i = 0; i < spare.Count && i < Spare; i++)
                    options.Add(spare[i]);
            }

            options.Sort((a, b) => b.Worth.CompareTo(a.Worth));

            for (var i = 0; i < options.Count && i < wide; i++)
            {
                if (clock.Elapsed > stop)
                    return;

                chain.Add(options[i].At);
                Planner.Push(env, tally, chain, options[i].At);
                grown++;
                deep[Math.Min(chain.Count, deep.Length - 1)]++;

                if (options[i].Worth > top)
                {
                    top = options[i].Worth;
                    best = new List<Vector2>(chain);
                    found?.Invoke(new List<Vector2>(best));
                }

                Step();
                chain.RemoveAt(chain.Count - 1);
                Planner.Pop(env, tally, chain, options[i].At);
            }
        }

        // Commit a link, look again - and with no horizon this runs once and is the exhaustive
        // search unchanged.
        while (settled < env.Explosives && !token.IsCancellationRequested && clock.Elapsed < stop)
        {
            top = double.NegativeInfinity;
            best = new List<Vector2>(chain);

            Step();

            if (best.Count <= chain.Count)
                break;

            // Only the next link is kept: everything past it was chosen against bands that are about
            // to be redrawn from a site with one more blast taken out of it.
            var take = best[chain.Count];

            chain.Add(take);
            Planner.Push(env, tally, chain, take);
            settled++;

            if (horizon >= env.Explosives)
                break;
        }

        if (chain.Count >= best.Count && chain.Count > 0)
        {
            best = new List<Vector2>(chain);
            top = Planner.Score(env, best);
            found?.Invoke(new List<Vector2>(best));
        }

        // An explosive in hand is a link the chain has not used.
        //
        // **A short chain is a bug however it happens**, and it can happen several ways here - the
        // window running out mid-lookahead, a dead end with nothing to fall back on, a horizon that
        // never got a second pass. On this site a four link answer where five were available left a
        // few hundred points on the ground. So whatever the search returns is filled out greedily
        // from the site's own spots before it is handed back.
        //
        // **And it is why this pass overran its budget by three times.** It had no clock at all -
        // only cancellation - while scoring every candidate against every remaining link, which on
        // a Grand site is fourteen hundred spots times fifteen links of full chain scoring.
        // Measured: 2,255ms and 2,319ms spent against a budget of 800, taken out of the search that
        // follows it. A grace rather than a hard stop, because finishing the chain is worth real
        // points and abandoning it half filled is the bug this loop exists to fix.
        // **A quarter was far too mean, and it cost the whole answer.** This loop is not a tidy-up:
        // with the stepwise expansion finding nothing - measured, nought nodes at every depth - it
        // is where the edge search's chain actually gets built. Unbounded it laid fifteen links in
        // about 1,455ms; capped at a two hundred millisecond grace it laid ONE, and the mode went
        // from scoring 62,307 to scoring 89.2 while still taking 1,195ms to do it.
        //
        // So the grace is as long again as the search itself had, floored at a second and a quarter.
        // That still bounds a pass that used to run on cancellation alone, and it leaves the edge
        // mode taking a little less than the 2,255ms it was measured at rather than a tenth of the
        // chain.
        //
        // The real fix is underneath: each candidate is priced by rescoring the WHOLE chain, where
        // Planner's Push and Pop price one added link against a running tally. That is the same
        // saving the stepwise search already takes and it would make this loop cheap enough that
        // the grace stopped mattering.
        var filling = stop + TimeSpan.FromMilliseconds(
            Math.Max(1250d, stop.TotalMilliseconds));

        // **Carried, the way the stepwise search above already carries it.**
        //
        // This loop appends one link at a time and priced every candidate by rescoring the whole
        // chain - which is what made it slow, and being slow is the only reason it needs a grace of
        // a second and a quarter on top of its budget. The note above says so and names the fix. It
        // is the append-only case Push and Pop were written for, and the audit has it exact over
        // tens of thousands of trials. See Planner.Fold.
        var filler = Planner.Begin(env, env.Explosives);

        foreach (var at in best)
            Planner.Fold(env, filler, best, at);

        while (best.Count < env.Explosives && !token.IsCancellationRequested &&
               clock.Elapsed < filling)
        {
            var from = best.Count == 0 ? env.Origin : best[^1];
            var pick = Vector2.Zero;
            var most = double.NegativeInfinity;

            foreach (var spot in candidates)
            {
                if (best.Contains(spot) || !Planner.Reaches(env, from, spot) ||
                    !Planner.Spaced(env, best, spot, best.Count))
                    continue;

                best.Add(spot);

                var worth = Planner.Push(env, filler, best, spot);

                // Removed before the undo, because Pop reads the step off the chain's length. See
                // the same pairing in the stepwise loop above.
                best.RemoveAt(best.Count - 1);
                Planner.Pop(env, filler, best, spot);

                if (worth <= most)
                    continue;

                most = worth;
                pick = spot;
            }

            if (most <= double.NegativeInfinity || pick == Vector2.Zero)
                break;

            best.Add(pick);

            // Folded rather than pushed: the worth is already in hand as `most`, and pushing would
            // settle the whole tally again to tell us something we know.
            Planner.Fold(env, filler, best, pick);

            top = most;
        }

        found?.Invoke(new List<Vector2>(best));

        Last = $"STEP BY STEP, bands redrawn at every node, lookahead {horizon}. " +
               $"{bands.Count} bands at the start, " +
               $"{drawn} redraws ({reused:N0} reused), {grown:N0} placements tried, " +
               $"{(wide == int.MaxValue ? "all" : wide.ToString())} branches a step, " +
               $"{clock.ElapsedMilliseconds}ms of {stop.TotalMilliseconds:N0}, best {top:N1}" +
               Environment.NewLine +
               $"      slack {seeding.Slack:P1}, cells per band {seeding.PerBand}, " +
               $"by depth {string.Join("/", deep[1..])}; dead ends: " +
               $"{stopped[0]} nothing left to catch, {stopped[1]} no bands (fell back to " +
               $"{candidates.Count} candidates), {stopped[2]} nothing legal in reach" +
               Environment.NewLine +
               "      best chain: " +
               string.Join(" ", best.ConvertAll(c => $"({c.X:0},{c.Y:0})"));

        return best.Count == 0
            ? Plan.Empty with { Note = "no legal chain through the band edges" }
            : Planner.Describe(env, best);
    }
}
