using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore2;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The Withdraw zone's run: the most valuable Expedition tablets in the fragment tab's sub-tabs taken out, as many as the
/// inventory has room for, most valuable first. Each sub-tab is looked at once and every tablet's worth noted; then the
/// most valuable are withdrawn in that order across all of them, changing sub-tab as the next one needs.
///
/// A tablet's worth is its own search's price; else what a search at lower rolls says it is worth at least; else what a
/// search at higher rolls says at most. In exalts, so the order holds without NinjaPricer's rates. A tablet with none of
/// these is not withdrawn, nor one worth under Reforge under. See WorthForWithdraw.
/// </summary>
internal static partial class Tablets
{
    private enum WithdrawPhase
    {
        /// <summary>The fragment tab's Expedition tablets. See NavigateNext.</summary>
        Navigate,

        /// <summary>Each sub-tab shown in turn and its tablets' places and worth noted.</summary>
        Scan,

        /// <summary>The chosen tablets ctrl+clicked out, most valuable first.</summary>
        Withdraw,
    }

    private static WithdrawPhase _withdrawPhase;

    /// <summary>The sub-tab being scanned.</summary>
    private static int _withdrawScanSubTab;

    /// <summary>
    /// Each sub-tab's tablets as scanned: where each was read, its worth in exalts, its fingerprint, and where the worth
    /// came from (see WorthAndSourceOfSearch).
    /// </summary>
    private static readonly Dictionary<int, List<(RectangleF Rect, double Worth, string Fingerprint, string Source)>> WithdrawScanned = [];

    /// <summary>The tablets chosen to withdraw and not yet taken, most valuable first.</summary>
    private static readonly List<(int SubTab, (int Column, int Row) Cell, double Worth)> WithdrawPlan = [];

    /// <summary>The planned tablets clicked this run, by sub-tab and cell, so one seen gone is done rather than stale.</summary>
    private static readonly HashSet<(int SubTab, (int Column, int Row) Cell)> WithdrawSent = [];

    /// <summary>What withdrawing a tablet by its worth is called, in the status line.</summary>
    private const string ValuableTablet = "a tablet to withdraw";

    /// <summary>Starts the run, from the stash as it is.</summary>
    private static void StartWithdrawValuable()
    {
        _withdrawPhase = WithdrawPhase.Navigate;
        _navigateStep = 0;
        _withdrawScanSubTab = 1;
        WithdrawScanned.Clear();
        WithdrawPlan.Clear();
    }

    /// <summary>
    /// The Deposit zone's run: every 10-use Expedition tablet in the inventory ctrl+clicked into the stash, one straight
    /// after another. A used one stays, as every run leaves it: players carry those for their own maps. The game puts each
    /// in the earliest sub-tab with room. Done once none is left to send and every one sent is seen to have gone.
    /// </summary>
    private static void StepDepositAll(GameController gc)
    {
        if (MoveNext(gc, InventoryTablets().Where(t => t.Uses == FullUses), DepositedTablet, false, room: int.MaxValue))
            return;

        FinishQuietly($"done: {_moved} tablet(s) deposited");
    }

    /// <summary>
    /// A tablet's worth in exalts for ordering a withdraw: the price its label is drawn from (PricedFromAnswers) - its own
    /// search's price; else the lowest ceiling from searches at higher rolls with as many listings as Price from takes,
    /// when there is no floor or the ceiling is within Cheap price up to; else the highest floor from searches at lower
    /// rolls; else the estimate from searches one modifier short. Null with none. A tablet its own search takes as under
    /// a chaos is worth nought, as the reforger takes it.
    /// </summary>
    private static double? WorthForWithdraw(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings) =>
        WorthAndSourceForWithdraw(gc, tablet, settings).Worth;

    /// <summary>WorthForWithdraw, with where it came from. See WorthAndSourceOfSearch.</summary>
    private static (double? Worth, string Source) WorthAndSourceForWithdraw(GameController gc, TabletOnScreen tablet,
        TabletRerollingSettings settings)
    {
        if (_valuation == null)
            return (null, "no valuation");

        return SearchOfTablet(gc, tablet, settings, out var reason) is { } search
            ? WorthAndSourceOfSearch(search, settings)
            : (null, $"no search: {reason}");
    }

    /// <summary>WorthForWithdraw from a tablet's search, for a tablet known only by its record. See RememberedWorth.</summary>
    private static double? WorthOfSearch(TabletSearch search, TabletRerollingSettings settings) =>
        WorthAndSourceOfSearch(search, settings).Worth;

    /// <summary>
    /// WorthOfSearch, with where it came from - its own search, a floor from lower rolls, a ceiling from higher rolls or an
    /// estimate - for the run's log, since the label drawn on a tablet can take a different one of these (a ceiling, "<",
    /// where this takes the floor). See PricedFromAnswers.
    /// </summary>
    private static (double? Worth, string Source) WorthAndSourceOfSearch(TabletSearch search, TabletRerollingSettings settings)
    {
        if (_valuation == null)
            return (null, "no valuation");

        if (TabletPricing.Cached(search.Key) is { Error.Length: 0 } own && OwnPriceStands(search, own, settings, _valuation) &&
            PriceOfAnswer(own, settings, _valuation) is var price && !double.IsNaN(price))
            return (price, "own search");

        if (TabletPricing.Cached(search.Key) is { Error.Length: 0 } unconverted && OwnPriceStands(search, unconverted, settings, _valuation) &&
            ListedOnlyUnconverted(unconverted, settings, _valuation))
            return (0d, "listed only under a chaos");

        double? floor = null, ceiling = null;

        foreach (var (_, answer, tighter, looser) in TabletPricing.RelatedAnswers(search.Key))
        {
            if (answer.Error.Length > 0 || PriceOfAnswer(answer, settings, _valuation) is var bound && double.IsNaN(bound))
                continue;

            if (looser && (floor == null || bound > floor))
                floor = bound;

            if (tighter && answer.Listings.Count >= settings.ListingsWanted && (ceiling == null || bound < ceiling))
                ceiling = bound;
        }

        // **The bound the label is drawn from, so the order follows the prices shown.** The label, and the listings shown
        // on hovering, are the ceiling while there is no floor or the ceiling is cheap, else the floor (PricedFromAnswers).
        // Taking the floor first, as this did, ordered Planar Terraform at its 2d floor from lower rolls while its label
        // and hover showed its higher rolls' listings at 14-16c (dump 2026-10-18 01:23:49).
        if (ceiling is { } top && (floor == null || top / _valuation.PerExalt("Divine") <= settings.TradeSite.CheapPriceUpToDivine.Value))
            return (top, floor is { } beside ? $"ceiling from higher rolls, floor {Shown(beside, _valuation)}" : "ceiling from higher rolls");

        if (floor is { } low)
            return (low, ceiling is { } high ? $"floor from lower rolls, ceiling {Shown(high, _valuation)}" : "floor from lower rolls");

        return EstimateFromFewerModifiers(search, settings, _valuation)?.Price is { } estimate
            ? (estimate, "estimate one modifier short")
            : (null, "unpriced");
    }

    /// <summary>
    /// A remembered tablet's worth from the prices known now, through the search collected for its fingerprint; the
    /// worth its record was written with when none is collected. The record's worth is as of when its sub-tab last
    /// showed, so a tablet priced since read as unpriced and was left out of the plan: the run withdrew from sub-tab 1
    /// while more valuable tablets sat in sub-tab 2 (2026-10-18, the user's account).
    /// </summary>
    private static (double? Worth, string Source) RememberedWorth(RememberedTablet tablet, TabletRerollingSettings settings) =>
        Collected.TryGetValue(tablet.Fingerprint, out var search) && WorthAndSourceOfSearch(search, settings) is { Worth: not null } now
            ? (now.Worth, "remembered: " + now.Source)
            : (tablet.Worth, "remembered: the record's worth");

    /// <summary>
    /// Whether every full-uses tablet a sub-tab's record priced has its search collected, so RememberedWorth prices it from
    /// the prices known now. Collected is kept in memory only, so after the plugin loads every remembered tablet fell back
    /// to the record's worth, as of when its sub-tab last showed: a tablet searched again since was ordered by its old
    /// price, behind cheaper ones (dump 2026-10-18 01:21:51, sub-tabs 1 and 2 all "the record's worth"). A sub-tab failing
    /// this is visited, which reads it fresh and collects it.
    /// </summary>
    private static bool PricedNow(List<RememberedTablet> remembered) =>
        remembered.All(t => t.Uses != FullUses || t.Worth == null || Collected.ContainsKey(t.Fingerprint));

    private static void StepWithdrawValuable(GameController gc, TabletRerollingSettings settings)
    {
        switch (_withdrawPhase)
        {
            case WithdrawPhase.Navigate:
                NavigateNext(gc);
                return;

            case WithdrawPhase.Scan:
            {
                // A sub-tab seen lately is taken as it was seen, not visited - while every priced tablet in it can be priced
                // now. See RememberedContents and PricedNow.
                if (_withdrawScanSubTab <= 6 && RememberedContents(gc, _withdrawScanSubTab) is { } remembered && PricedNow(remembered))
                {
                    WithdrawScanned[_withdrawScanSubTab] = remembered
                        .Where(t => t.Uses == FullUses)
                        .Select(t => RememberedWorth(t, settings) is var worth
                            ? (t.Rect, worth.Worth ?? double.NaN, t.Fingerprint, worth.Source)
                            : default)
                        .ToList();
                    _withdrawScanSubTab++;
                }
                else if (SelectedSubTab(gc) != _withdrawScanSubTab)
                {
                    ClickSubTab(gc, _withdrawScanSubTab);
                    return;
                }
                else
                {
                    if (!ReadSinceSubTabClick())
                        return;

                    WithdrawScanned[_withdrawScanSubTab] = StashTablets()
                        .Where(t => t.Uses == FullUses)
                        .Select(t => WorthAndSourceForWithdraw(gc, t, settings) is var worth
                            ? (t.Rect, worth.Worth ?? double.NaN, FingerprintOf(t), worth.Source)
                            : default)
                        .ToList();
                    _withdrawScanSubTab++;
                }

                if (_withdrawScanSubTab <= 6)
                    return;

                PlanWithdrawValuable(gc, settings);
                return;
            }

            case WithdrawPhase.Withdraw:
            {
                if (WithdrawPlan.Count == 0)
                {
                    if (MoveNext(gc, [], ValuableTablet, true))
                        return;

                    FinishQuietly($"done: {_moved} tablet(s) withdrawn, most valuable first");
                    return;
                }

                var showing = SelectedSubTab(gc);

                if (!ReadSinceSubTabClick())
                    return;

                // **A plan entry leaves the plan when its cell is seen empty, not when it is clicked.** Taken off at the
                // click, a tablet still there 150ms after it was clicked again by MoveNext's retry and found nothing to
                // retry: its entry had gone, and the run withdrew one fewer than planned (dump 2026-10-18 01:29:04). An
                // entry clicked and now empty is done; one never clicked and empty was planned from a stale record, and
                // every such entry of the sub-tab goes at once, not one a visit: planned from a stale record, the run went
                // back and forth between sub-tabs for each one gone (2026-10-10).
                var shown = StashTablets();
                var held = shown.Select(t => GridCellOf(t.Rect)).Where(c => c != null).Select(c => c!.Value).ToHashSet();

                bool InFlight((int SubTab, (int Column, int Row) Cell, double Worth) p) =>
                    p.SubTab == showing && shown.FirstOrDefault(t => GridCellOf(t.Rect) == p.Cell) is { } t &&
                    ReforgingSent.Contains(AddressOf(t));

                foreach (var p in WithdrawPlan.Where(InFlight))
                    WithdrawSent.Add((p.SubTab, p.Cell));

                var gone = 0;

                WithdrawPlan.RemoveAll(p =>
                {
                    if (p.SubTab != showing || held.Contains(p.Cell))
                        return false;

                    if (!WithdrawSent.Contains((p.SubTab, p.Cell)))
                        gone++;

                    return true;
                });

                if (gone > 0)
                    LogReforging($"sub-tab {showing}: {gone} planned tablet(s) no longer there");

                // The most valuable not clicked yet, so the inventory fills in that order. One clicked and not yet moved
                // stays on the plan, so MoveNext's retry has it to click again. See MoveNext.
                var head = WithdrawPlan.FindIndex(p => !InFlight(p));

                if (head < 0)
                {
                    // Everything left is on its way: settled, and retried where one did not move, before calling it done.
                    MoveNext(gc, [], ValuableTablet, true);
                    return;
                }

                var subTab = WithdrawPlan[head].SubTab;

                if (showing != subTab)
                {
                    // Every move on the sub-tab showing settled before leaving it.
                    if (MoveNext(gc, [], ValuableTablet, true))
                        return;

                    ClickSubTab(gc, subTab);
                    return;
                }

                // **With the rest of this sub-tab's in the plan, in the same order, as one batch**, so ExileInput2 plans and
                // draws them ahead and the cursor goes straight on from one to the next, as every other withdraw does. Each
                // was a batch of its own, planned and drawn alone. Only those before the plan next needs another sub-tab.
                var run = WithdrawPlan.Skip(head).TakeWhile(p => p.SubTab == subTab).ToList();
                var batch = run
                    .Select(p => shown.FirstOrDefault(t => GridCellOf(t.Rect) == p.Cell))
                    .Where(t => t != null && !ReforgingSent.Contains(AddressOf(t)))
                    .ToList();

                var thisSubTab = head + run.Count;

                MoveNext(gc, batch, ValuableTablet, true, inOrder: true,
                    thenClicks: (_, from) => PlannedValuableAfter(gc, thisSubTab, from));
                return;
            }
        }
    }

    /// <summary>
    /// The rest of the most valuable withdraw after the sub-tab showing, as clicks for MoveNext to plan ahead: every tablet
    /// still in WithdrawPlan in its order, with the sub-tab click before each change of sub-tab, each tablet where the scan
    /// read it and its point just inside its cell's edge facing the last. Stops at a sub-tab or tablet it cannot place.
    /// Changes nothing: each click is still sent only once the run decides on it. See PlannedSubTabsAfter, its junk
    /// counterpart, and ExpectedAfterSubTab.
    /// </summary>
    /// <param name="skip">How many of WithdrawPlan's first entries are the sub-tab showing's, planned by MoveNext itself.</param>
    /// <param name="from">Where the cursor will be when the sub-tab showing is done.</param>
    private static List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)> PlannedValuableAfter(GameController gc, int skip,
        Vector2 from)
    {
        var planned = new List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)>();
        var at = SelectedSubTab(gc);
        var firstOfVisit = false;

        ExpectedAfterSubTab.Clear();

        foreach (var (subTab, cell, _) in WithdrawPlan.Skip(skip))
        {
            if (subTab != at)
            {
                if (SubTabRect(gc, subTab) is not { Width: > 0f } tab)
                    break;

                planned.Add((tab, null, false, false));
                from = new Vector2(tab.X + tab.Width / 2f, tab.Y + tab.Height / 2f);
                at = subTab;
                firstOfVisit = true;
            }

            if (!WithdrawScanned.TryGetValue(subTab, out var scanned) ||
                scanned.FirstOrDefault(x => GridCellOf(x.Rect) == cell) is not { Rect.Width: > 0f } tablet)
                break;

            var point = ItemClicks.NearEdgeOf(tablet.Rect, from);

            if (firstOfVisit)
                ExpectedAfterSubTab.Add((subTab, tablet.Rect, tablet.Fingerprint));

            firstOfVisit = false;
            planned.Add((tablet.Rect, point, false, true));
            from = point;
        }

        return planned;
    }

    /// <summary>
    /// Whether a worth in exalts is under Reforge under, in chaos: such a tablet is not withdrawn, as its price is not
    /// even drawn. False without a chaos rate, which cannot say: the tablet is kept in rather than dropped. See
    /// TabletChaosOf.
    /// </summary>
    private static bool UnderReforgePrice(double exalts, TabletRerollingSettings settings) =>
        TabletChaosOf(exalts, _valuation) is { } chaos && chaos < settings.Prices.ReforgeUnderChaos.Value;

    /// <summary>
    /// The scan done: the grid learned, and the most valuable tablets chosen, as many as the inventory has free cells,
    /// most valuable first across all sub-tabs.
    /// </summary>
    private static void PlanWithdrawValuable(GameController gc, TabletRerollingSettings settings)
    {
        var all = WithdrawScanned.SelectMany(x => x.Value).Select(x => x.Rect).ToList();
        var room = InventoryFreeCells(gc);

        if (all.Count == 0 || room <= 0)
        {
            StopReforging(all.Count == 0 ? "done: no tablets in the sub-tabs" : "the inventory is full");
            return;
        }

        LearnGrid(all);

        var priced = WithdrawScanned
            .SelectMany(x => x.Value.Where(t => !double.IsNaN(t.Worth) && !UnderReforgePrice(t.Worth, settings))
                .Select(t => (SubTab: x.Key, Cell: GridCellOf(t.Rect), t.Worth, t.Source)))
            .Where(t => t.Cell != null)
            .OrderByDescending(t => t.Worth)
            .Take(room)
            .ToList();

        if (priced.Count == 0)
        {
            StopReforging($"done: no tablet in the sub-tabs priced at {settings.Prices.ReforgeUnderChaos.Value:0.##}c or more");
            return;
        }

        // **Most valuable first across every sub-tab**, changing sub-tab whenever the next is elsewhere: taken a sub-tab at
        // a time, the inventory filled most valuable first within each sub-tab, mixed overall (2026-10-10).
        WithdrawPlan.Clear();
        WithdrawPlan.AddRange(priced.Select(t => (t.SubTab, t.Cell!.Value, t.Worth)));

        LogReforging($"withdraw by worth: {priced.Count} of {all.Count} tablets, from {priced[0].Worth:0.##}ex down to {priced[^1].Worth:0.##}ex");

        // Each in the order taken, with its worth and where that came from, so an order that looks wrong against the labels
        // can be read rather than guessed at.
        foreach (var (subTab, cell, worth, source) in priced)
            LogReforging($"  sub-tab {subTab} ({cell!.Value.Column},{cell.Value.Row}): {Shown(worth, _valuation)} ({worth:0.#}ex), {source}");
        _withdrawPhase = WithdrawPhase.Withdraw;
        ReforgingSent.Clear();
        WithdrawSent.Clear();
        _moved = 0;
    }
}
