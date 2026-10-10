using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The tidy at the end of a craft and reforge run: the fragment tab's Expedition tablets brought together at the front,
/// so the T tablets there fill the first T cells with no gap - sub-tab 1 first, then 2, and so on.
///
/// A deposit lands in the earliest sub-tab with room, at its first free cell, filling a column top to bottom and then
/// the next column to the right, whichever sub-tab is showing, and the stash then shows that sub-tab - the player's
/// account (2026-10-10). So the tablets that
/// sit past the first T cells in that order are withdrawn, as many as there are gaps before them, and deposited again,
/// which puts each in a gap. A round withdraws as many as the inventory holds; rounds go on until nothing sits past the
/// first T cells, at most MostTidyRounds, and a round that moves nothing ends it.
///
/// The grid is 12 columns of 8 rows, 96 cells: tablets read at 12 x-positions from 59 to 771 and 8 y-positions from
/// 429 to 883 on one sub-tab (dump 2026-10-09 23:45). A tablet's cell is worked out from its place against the tablets
/// furthest up and left across every sub-tab; one that does not fall on the grid stops the tidy rather than move the
/// wrong tablet.
/// </summary>
internal static partial class Tablets
{
    private enum TidyPhase
    {
        /// <summary>
        /// The tablets and currency stacks a craft and reforge run kept in the inventory between its passes deposited:
        /// everything in a cell that was empty when the run started. See PutAway.
        /// </summary>
        PutAway,

        /// <summary>The fragment tab's Expedition tablets. See NavigateNext.</summary>
        Navigate,

        /// <summary>Each sub-tab shown in turn and its tablets' places noted.</summary>
        Scan,

        /// <summary>The tablets past the first T cells ctrl+clicked out, highest sub-tab first.</summary>
        Withdraw,

        /// <summary>Every tablet withdrawn ctrl+clicked back, into the earliest gaps.</summary>
        Deposit,

        /// <summary>The last sub-tab with tablets in it shown, then the run ends. See FinishTidy.</summary>
        Finish,
    }

    private static TidyPhase _tidyPhase;

    /// <summary>Settings.TabletRerolling.TidyAfterCraftAndReforge, as last read. See Draw.</summary>
    private static bool _tidyAfterCraftAndReforge = true;

    /// <summary>What the craft and reforge run said as it finished, said again once the tidy is done.</summary>
    private static string _tidyAfter = "";

    private static int _tidyRounds;

    /// <summary>Whether this round's withdraw stopped on a full inventory with tablets still to take out.</summary>
    private static bool _tidyCutShort;


    /// <summary>Tablets moved by the tidy, in all.</summary>
    private static int _tidyMoved;

    /// <summary>The sub-tab being scanned.</summary>
    private static int _tidyScanSubTab;

    /// <summary>Each sub-tab's tablets as scanned: the rectangle each was read at.</summary>
    /// <summary>
    /// Each sub-tab's tablets as scanned, with whether each is used - under FullUses uses - and so stays where it is: a
    /// player keeps those where they put them, and no tablet run moves one. See PlanTidy.
    /// </summary>
    private static readonly Dictionary<int, List<(RectangleF Rect, bool Used)>> TidyScanned = [];

    /// <summary>The cells to empty in each sub-tab this round, by column and row.</summary>
    private static readonly Dictionary<int, HashSet<(int Column, int Row)>> TidyToWithdraw = [];

    /// <summary>How many tablets sat past the first T cells at the last round's scan. See PlanTidy.</summary>
    private static int _tidyPastLastRound;

    /// <summary>Tablets withdrawn this round, for a round that moved nothing to end the tidy.</summary>
    private static int _tidyWithdrawnThisRound;

    /// <summary>
    /// Whether the screen has been read since the tidy's last sub-tab click, so the tablets read are the new sub-tab's:
    /// always, when the last click was not on a sub-tab. Asked of every click, it held each tablet withdrawn for the next
    /// read too, about 150 to 250ms, and the tidy moved at half the pace of a withdraw (dump 2026-10-10 00:40).
    /// </summary>
    private static bool ReadSinceSubTabClick() =>
        _previousSent?.StartsWith("sub-tab ", StringComparison.Ordinal) != true || _read - _clickedAt >= TimeSpan.FromMilliseconds(100);

    private const int TidyColumns = 12;
    private const int TidyRows = 8;

    /// <summary>The most rounds a tidy makes, however much each moves.</summary>
    private const int MostTidyRounds = 10;

    /// <summary>What withdrawing a tablet for the tidy is called, in the status line.</summary>
    private const string TidyTablet = "a tablet to tidy";

    /// <summary>
    /// The end of a craft and reforge run, from the stash as it left it: what it kept in the inventory put away, then the
    /// tablets tidied when asked.
    /// </summary>
    private static void StartTidy(GameController gc, string after, bool tidy)
    {
        _run = TabletRun.Tidy;
        _tidyAfter = after;
        _tidyWanted = tidy;
        _tidyRounds = 0;
        _tidyMoved = 0;
        BeginTidyRound();
        _tidyPhase = TidyPhase.PutAway;

        // The put-away deposits what is outside the cells taken when the craft and reforge run started. See
        // TabletsBroughtOut and SuppliedStacksCarried, which read InventoryCellsAtStart.
        InventoryCellsAtStart.Clear();

        foreach (var cell in CombinedCellsAtStart)
            InventoryCellsAtStart.Add(cell);

        Progressed();
        Say("putting away");
    }

    /// <summary>Whether the tidy itself follows the put-away. See StartTidy.</summary>
    private static bool _tidyWanted;

    /// <summary>One round: to the fragment tab, then a scan from sub-tab 1.</summary>
    private static void BeginTidyRound()
    {
        _tidyCutShort = false;
        _tidyPhase = TidyPhase.Navigate;
        _navigateStep = 0;
        _tidyScanSubTab = 1;
        _tidyWithdrawnThisRound = 0;
        TidyScanned.Clear();
        TidyToWithdraw.Clear();
        ReforgingSent.Clear();
        MovesPending.Clear();
        MovesRetried.Clear();
        _moved = 0;
    }

    private static void StepTidy(GameController gc)
    {
        switch (_tidyPhase)
        {
            case TidyPhase.PutAway:
            {
                // **One put-away at the end, of everything kept between passes**: the tablets into the earliest gaps,
                // then the currency stacks to the currency tab, a ctrl+click sending each from whichever tab shows.
                if (MoveNext(gc, TabletsBroughtOut(gc).Where(t => t.Uses == FullUses), DepositedTablet, false, room: int.MaxValue))
                    return;

                if (SuppliedStacksCarried(gc).Where(s => s.Rect.Width > 0f).ToList() is [var (rect, name), ..])
                {
                    var before = InventoryCurrencyHeld(gc).GetValueOrDefault(name);

                    Send(rect, true, $"the {name} back", () => InventoryCurrencyHeld(gc).GetValueOrDefault(name) < before, null);
                    return;
                }

                if (!_tidyWanted)
                {
                    StopReforging(_tidyAfter);
                    return;
                }

                _tidyPhase = TidyPhase.Navigate;
                _navigateStep = 0;
                Say("tidying the tablets");
                return;
            }

            case TidyPhase.Navigate:
                NavigateNext(gc);
                return;

            case TidyPhase.Scan:
            {
                // A sub-tab seen lately is taken as it was seen, not visited. See RememberedContents.
                if (_tidyScanSubTab <= 6 && RememberedContents(gc, _tidyScanSubTab) is { } remembered)
                {
                    TidyScanned[_tidyScanSubTab] = remembered.Select(t => (t.Rect, t.Uses != FullUses)).ToList();

                    if (++_tidyScanSubTab > 6)
                        PlanTidy(gc);

                    return;
                }

                if (SelectedSubTab(gc) != _tidyScanSubTab)
                {
                    ClickSubTab(gc, _tidyScanSubTab);
                    return;
                }

                if (!ReadSinceSubTabClick())
                    return;

                TidyScanned[_tidyScanSubTab] = StashTablets().Select(t => (t.Rect, t.Uses != FullUses)).ToList();

                if (++_tidyScanSubTab <= 6)
                    return;

                PlanTidy(gc);
                return;
            }

            case TidyPhase.Withdraw:
            {
                var subTab = TidyToWithdraw.Where(x => x.Value.Count > 0).Select(x => x.Key).DefaultIfEmpty(0).Max();

                if (subTab == 0)
                {
                    _tidyPhase = TidyPhase.Deposit;
                    ReforgingSent.Clear();
                    _moved = 0;
                    return;
                }

                if (SelectedSubTab(gc) != subTab)
                {
                    ClickSubTab(gc, subTab);
                    return;
                }

                if (!ReadSinceSubTabClick())
                    return;

                var cells = TidyToWithdraw[subTab];
                var wanted = StashTablets().Where(t => t.Uses == FullUses && GridCellOf(t.Rect) is { } cell && cells.Contains(cell)).ToList();

                if (MoveNext(gc, wanted, TidyTablet, true))
                {
                    _tidyWithdrawnThisRound = Math.Max(_tidyWithdrawnThisRound, _moved);
                    return;
                }

                // The inventory full, or this sub-tab's done: to depositing, or the next sub-tab down.
                if (InventoryFreeCells(gc) == 0)
                {
                    _tidyCutShort = TidyToWithdraw.Any(x => x.Value.Count > 0);
                    TidyToWithdraw.Clear();
                    return;
                }

                cells.Clear();
                return;
            }

            case TidyPhase.Finish:
                if (_tidyLastSubTab > 0 && SelectedSubTab(gc) != _tidyLastSubTab)
                {
                    ClickSubTab(gc, _tidyLastSubTab);
                    return;
                }

                StopReforging(_tidyFinishSaid);
                return;

            case TidyPhase.Deposit:
            {
                // Into the earliest gaps, whichever sub-tab is showing. See the class summary.
                if (MoveNext(gc, TabletsBroughtOut(gc).Where(t => t.Uses == FullUses), DepositedTablet, false, room: int.MaxValue))
                    return;

                _tidyMoved += _tidyWithdrawnThisRound;

                if (_tidyWithdrawnThisRound == 0 || ++_tidyRounds >= MostTidyRounds)
                {
                    FinishTidy(_tidyWithdrawnThisRound == 0 ? _tidyAfter : $"{_tidyAfter}; tidy stopped at the most rounds a tidy makes");
                    return;
                }

                // **Every tablet past the first T cells out and back in: compact, with no scan to say so.** Deposits
                // fill the earliest gaps, and there were exactly as many gaps as tablets taken out, so a scan after
                // would only find nothing to move. Another round only when a full inventory cut the withdraw short.
                if (!_tidyCutShort)
                {
                    FinishTidy(_tidyAfter);
                    return;
                }

                BeginTidyRound();
                return;
            }
        }
    }

    /// <summary>
    /// The scan done: the grid worked out from the tablets furthest up and left, every tablet given its place in fill
    /// order, and those past the first T cells marked to withdraw. Done when there are none.
    /// </summary>
    private static void PlanTidy(GameController gc)
    {
        var all = TidyScanned.SelectMany(x => x.Value).ToList();

        if (all.Count == 0)
        {
            StopReforging(_tidyAfter);
            return;
        }

        LearnGrid(all.Select(x => x.Rect).ToList());

        var placed = new List<(int SubTab, int Column, int Row)>();
        var usedAt = new List<int>();

        foreach (var (subTab, tablets) in TidyScanned)
        {
            foreach (var (rect, used) in tablets)
            {
                if (GridCellOf(rect) is not { } cell)
                {
                    StopReforging($"{_tidyAfter}; not tidied: a tablet at {rect.X:0},{rect.Y:0} in sub-tab {subTab} is off the grid");
                    return;
                }

                if (used)
                    usedAt.Add(FillIndexOf(subTab, cell.Column, cell.Row));
                else
                    placed.Add((subTab, cell.Column, cell.Row));
            }
        }

        // **Used tablets stay; full-use ones fill around them.** The first T cells in fill order that no used tablet
        // holds are where the T full-use tablets belong, as the game's deposits fill the earliest free cell. One past
        // them is taken out and put back.
        var total = placed.Count;
        var past = placed.Where(p => FreeCellsBefore(FillIndexOf(p.SubTab, p.Column, p.Row), usedAt) >= total).ToList();

        if (past.Count == 0)
        {
            FinishTidy(_tidyAfter);
            return;
        }

        // **No tidier than the round before: stop.** A round deposits into the gaps before the tablets it took out, so
        // each leaves fewer past the first T cells; one that does not means deposits do not land as taken here, and
        // going on would move the same tablets round after round.
        if (_tidyRounds > 0 && past.Count >= _tidyPastLastRound)
        {
            FinishTidy($"{_tidyAfter}; tidy stopped: {past.Count} tablet(s) still past the first {total} cells, no fewer than the round before");
            return;
        }

        _tidyPastLastRound = past.Count;
        LogReforging($"tidy round {_tidyRounds + 1}: {total} tablets, {past.Count} past the first {total} cells");

        foreach (var (subTab, column, row) in past)
        {
            if (!TidyToWithdraw.TryGetValue(subTab, out var cells))
                TidyToWithdraw[subTab] = cells = [];

            cells.Add((column, row));
        }

        // The inventory's cells now, so the deposit takes back only the tablets the tidy brought out.
        InventoryCellsAtStart.Clear();

        foreach (var cell in InventoryCellsTaken(gc))
            InventoryCellsAtStart.Add(cell);

        _tidyPhase = TidyPhase.Withdraw;
        ReforgingSent.Clear();
        _moved = 0;
    }

    /// <summary>What the run says once the tidy has shown the last sub-tab with tablets. See FinishTidy.</summary>
    private static string _tidyFinishSaid = "";

    /// <summary>The sub-tab the tablets end in once compact, or 0 for none. See FinishTidy.</summary>
    private static int _tidyLastSubTab;

    /// <summary>
    /// Ends the tidy on the sub-tab where the tablets end once compact, so the stash is left showing it, then the run
    /// stops saying why.
    /// </summary>
    private static void FinishTidy(string said)
    {
        _tidyFinishSaid = said;
        LogReforging($"tidied, {_tidyMoved} tablet(s) moved over {_tidyRounds + 1} round(s)");
        // Where the tablets end once compact - the sub-tab holding the T-th - not the last the scan saw tablets in, which a
        // round finishing without a scan after it has just emptied.
        var last = LastFilledIndex();

        _tidyLastSubTab = last < 0 ? 0 : last / (TidyColumns * TidyRows) + 1;
        _tidyPhase = TidyPhase.Finish;
    }

    /// <summary>The grid as the last scan found it: its top left cell's corner and the size of a cell.</summary>
    private static float _gridLeft, _gridTop, _gridPitch;

    /// <summary>
    /// The grid from the tablets read across the sub-tabs: its corner from those furthest up and left, a cell's size the
    /// median tablet width. For the tidy and the most valuable withdraw. See GridCellOf.
    /// </summary>
    private static void LearnGrid(List<RectangleF> all)
    {
        _gridLeft = all.Min(r => r.X);
        _gridTop = all.Min(r => r.Y);
        _gridPitch = all.Select(r => r.Width).OrderBy(w => w).ElementAt(all.Count / 2);
    }

    /// <summary>A tablet's column and row from its rectangle, or null when it does not fall on the grid.</summary>
    private static (int Column, int Row)? GridCellOf(RectangleF rect)
    {
        if (_gridPitch <= 0f)
            return null;

        var column = (rect.X - _gridLeft) / _gridPitch;
        var row = (rect.Y - _gridTop) / _gridPitch;
        var (c, r) = ((int)MathF.Round(column), (int)MathF.Round(row));

        return c is >= 0 and < TidyColumns && r is >= 0 and < TidyRows && MathF.Abs(column - c) < 0.25f && MathF.Abs(row - r) < 0.25f
            ? (c, r)
            : null;
    }

    /// <summary>A cell's place in the order deposits fill: sub-tab, then column, then row.</summary>
    private static int FillIndexOf(int subTab, int column, int row) => (subTab - 1) * TidyColumns * TidyRows + column * TidyRows + row;

    /// <summary>How many cells before this place in fill order no used tablet holds: its rank among the cells free to fill.</summary>
    private static int FreeCellsBefore(int index, List<int> usedAt) => index - usedAt.Count(u => u < index);

    /// <summary>
    /// The last place in fill order a tablet holds once compact: the last used tablet, or the last of the cells the
    /// full-use ones fill around them, whichever is later. -1 with none.
    /// </summary>
    private static int LastFilledIndex()
    {
        var usedAt = new List<int>();
        var fullUse = 0;

        foreach (var (subTab, tablets) in TidyScanned)
        {
            foreach (var (rect, used) in tablets)
            {
                if (!used)
                    fullUse++;
                else if (GridCellOf(rect) is { } cell)
                    usedAt.Add(FillIndexOf(subTab, cell.Column, cell.Row));
            }
        }

        var last = usedAt.DefaultIfEmpty(-1).Max();

        // The fullUse-th cell no used tablet holds.
        for (int index = 0, free = 0; fullUse > 0; index++)
        {
            if (usedAt.Contains(index))
                continue;

            if (++free == fullUse)
                return Math.Max(last, index);
        }

        return last;
    }
}
