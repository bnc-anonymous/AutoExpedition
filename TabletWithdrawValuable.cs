using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Each sub-tab's tablets as scanned: where each was read, and its worth in exalts.</summary>
    private static readonly Dictionary<int, List<(RectangleF Rect, double Worth)>> WithdrawScanned = [];

    /// <summary>The tablets chosen to withdraw and not yet taken, most valuable first.</summary>
    private static readonly List<(int SubTab, (int Column, int Row) Cell, double Worth)> WithdrawPlan = [];

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
    /// A tablet's worth in exalts for ordering a withdraw: its own search's price; else the highest floor from searches
    /// at lower rolls; else the lowest ceiling from searches at higher rolls with as many listings as Price from takes;
    /// else the estimate from searches one modifier short. Null with none. A tablet its own search takes as under
    /// a chaos is worth nought, as the reforger takes it.
    /// </summary>
    private static double? WorthForWithdraw(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (_valuation == null || SearchOfTablet(gc, tablet, settings, out _) is not { } search)
            return null;

        if (TabletPricing.Cached(search.Key) is { Error.Length: 0 } own && PriceOfAnswer(own, settings, _valuation) is var price &&
            !double.IsNaN(price))
            return price;

        if (TabletPricing.Cached(search.Key) is { Error.Length: 0 } unconverted && ListedOnlyUnconverted(unconverted, settings, _valuation))
            return 0d;

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

        return floor ?? ceiling ?? EstimateFromFewerModifiers(search, settings, _valuation)?.Price;
    }

    private static void StepWithdrawValuable(GameController gc, TabletRerollingSettings settings)
    {
        switch (_withdrawPhase)
        {
            case WithdrawPhase.Navigate:
                NavigateNext(gc);
                return;

            case WithdrawPhase.Scan:
            {
                // A sub-tab seen lately is taken as it was seen, not visited. See RememberedContents.
                if (_withdrawScanSubTab <= 6 && RememberedContents(gc, _withdrawScanSubTab) is { } remembered)
                {
                    WithdrawScanned[_withdrawScanSubTab] = remembered
                        .Where(t => t.Uses == FullUses)
                        .Select(t => (t.Rect, t.Worth ?? double.NaN))
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
                        .Select(t => (t.Rect, WorthForWithdraw(gc, t, settings) ?? double.NaN))
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

                var subTab = WithdrawPlan[0].SubTab;

                if (SelectedSubTab(gc) != subTab)
                {
                    // Every move on the sub-tab showing settled before leaving it.
                    if (MoveNext(gc, [], ValuableTablet, true))
                        return;

                    ClickSubTab(gc, subTab);
                    return;
                }

                if (!ReadSinceSubTabClick())
                    return;

                // **Every planned tablet of this sub-tab no longer in its cell is off the plan at once**, not one a visit:
                // planned from a stale record, the run went back and forth between sub-tabs for each one gone (2026-10-10).
                var shown = StashTablets();
                var held = shown.Select(t => GridCellOf(t.Rect)).Where(c => c != null).Select(c => c!.Value).ToHashSet();
                var gone = WithdrawPlan.RemoveAll(p => p.SubTab == subTab && !held.Contains(p.Cell));

                if (gone > 0)
                {
                    LogReforging($"sub-tab {subTab}: {gone} planned tablet(s) no longer there");
                    return;
                }

                // The most valuable still to take, alone, so the inventory fills in that order. Sent already, it is off
                // the plan. See MoveNext.
                var cell = WithdrawPlan[0].Cell;
                var next = shown.FirstOrDefault(t => GridCellOf(t.Rect) == cell);

                if (next == null || ReforgingSent.Contains(AddressOf(next)))
                {
                    WithdrawPlan.RemoveAt(0);
                    return;
                }

                MoveNext(gc, [next], ValuableTablet, true);
                return;
            }
        }
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
                .Select(t => (SubTab: x.Key, Cell: GridCellOf(t.Rect), t.Worth)))
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
        _withdrawPhase = WithdrawPhase.Withdraw;
        ReforgingSent.Clear();
        _moved = 0;
    }
}
