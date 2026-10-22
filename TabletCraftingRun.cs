using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The crafting run: the action key on the stash's Craft zone applies the inventory's currency to every 10-use Expedition
/// Tablet whose next step it is, in the chain's order - Transmutation, Augmentation, Regal, Alchemy, Exalt - across the
/// fragment tab's Expedition sub-tabs and the inventory. See TabletRerollingSettings.EnableTabletAutomation.
///
/// First, every unidentified tablet in the sub-tabs is taken to Doryani and put back, so the currencies see what each
/// needs. See TabletCraftIdentify.
///
/// For each currency and each sub-tab that needs it: right-click a stack of it - in the inventory when there is one, else
/// in the currency tab, found by its icon, and then back to the Expedition tablets by left clicks - confirmed by the
/// cursor reading as holding it (HeldOnCursor); left-click the sub-tab when it is not the one showing; press shift, which
/// keeps the orb on the cursor between uses; left-click each tablet that needs it, each confirmed by that tablet changing
/// before the next; release shift, which drops the orb. Shift goes down only once the orb is over the tablets, and a
/// right-click drops an orb carried to a sub-tab that turns out to need none - both the player's account of the game
/// (2026-10-09).
///
/// Which sub-tabs need a currency comes from the tablets remembered per cell (StashNextActions), and any sub-tab not
/// seen this session is visited in case. The same things stop it as stop reforging: the mouse moved, the key again, a
/// step that does not show, and no click repeated with nothing done between.
/// </summary>
internal static partial class Tablets
{
    private enum CraftPhase
    {
        /// <summary>
        /// One stack of each currency the tablets need and the inventory holds none of, ctrl+clicked out of the currency
        /// tab first, so the run does not go back and forth between tabs for each. One stack only: when it runs out the
        /// run fetches more from the tab anyway. Only with two currencies or more needed: with one, it is picked up where
        /// it lies. See StepCrafting.
        /// </summary>
        Supply,

        /// <summary>The fragment tab's Expedition tablets. See NavigateNext.</summary>
        Navigate,

        /// <summary>
        /// Every Expedition sub-tab not seen this session clicked once, with nothing on the cursor, so its tablets are
        /// remembered before any orb is fetched. An orb carried to a sub-tab "in case" found nothing there and had to
        /// be dropped, and the right-click that dropped it opened the sub-tab's own menu (2026-10-09).
        /// </summary>
        Scout,

        /// <summary>The next sub-tab for the current currency, or the next currency.</summary>
        Choose,

        /// <summary>Right-click a stack of the current currency in the inventory.</summary>
        PickUp,

        /// <summary>Left-click the chosen sub-tab, the orb on the cursor.</summary>
        GoToSubTab,

        /// <summary>Shift down, then the orb onto each tablet that needs it.</summary>
        Apply,

        /// <summary>The first step: the fragment tab's Expedition tablets, to take the unidentified ones out. See TabletCraftIdentify.</summary>
        IdentifyNavigate,

        /// <summary>Every unidentified tablet ctrl+clicked out of each sub-tab that may hold one, or until the inventory is full.</summary>
        IdentifyWithdraw,

        /// <summary>The panels closed for Doryani.</summary>
        IdentifyClose,

        /// <summary>Doryani ctrl+clicked, until nothing carried is unidentified.</summary>
        IdentifyDoryani,

        /// <summary>The stash opened again.</summary>
        IdentifyOpenStash,

        /// <summary>The fragment tab's Expedition tablets, to put the identified ones back.</summary>
        IdentifyDepositNavigate,

        /// <summary>Every tablet brought out ctrl+clicked back into the stash.</summary>
        IdentifyDeposit,

        /// <summary>The last step: the currency stacks the run took out ctrl+clicked back to the currency tab.</summary>
        ReturnSupply,
    }

    private static CraftPhase _craftPhase;

    /// <summary>Which of CraftingCurrencies is being applied.</summary>
    private static int _craftCurrency;

    /// <summary>The sub-tab the current currency is going to or applied on, 0 for the inventory alone.</summary>
    private static int _craftSubTab;

    /// <summary>Sub-tabs the current currency is finished with.</summary>
    private static readonly HashSet<int> CraftSubTabsDone = [];

    /// <summary>
    /// The stash tabs and sub-tabs seen this session, by "tab index:sub-tab", empty ones included - the remembered cells
    /// alone cannot tell an empty sub-tab from one never seen. See NoteCraftingNeeds.
    /// </summary>
    private static readonly HashSet<string> SeenSubTabs = new(StringComparer.Ordinal);

    /// <summary>The currencies the supply step has dealt with this run, withdrawn or found missing.</summary>
    private static readonly HashSet<string> CraftSupplied = new(StringComparer.Ordinal);

    /// <summary>
    /// The currencies found in neither the inventory nor the currency tab this run, by base name: passed over from then
    /// on, and named when the run finishes. A run out of one stopped there, leaving tablets that wanted another currency
    /// untouched and a reforge to follow unstarted (2026-10-20 22:19:35, no Orb of Augmentation). See StepCrafting.
    /// </summary>
    private static readonly HashSet<string> CraftCurrenciesRunOut = new(StringComparer.Ordinal);

    /// <summary>Whether this run is holding shift.</summary>
    private static bool _shiftHeld;

    private static DateTime _shiftAt;

    /// <summary>How long shift is held before the first click it is for.</summary>
    private static readonly TimeSpan ShiftSettle = TimeSpan.FromMilliseconds(120);

    /// <summary>Currency used by the crafting run going, or the last one.</summary>
    private static int _crafted;

    /// <summary>
    /// _crafted when the current pass through the currencies began. A pass that used none ends the run; one that used
    /// some is followed by another, since a currency can leave a tablet needing an earlier one in the order.
    /// </summary>
    private static int _craftedAtPassStart;

    /// <summary>Passes through the currencies this run, for the last word.</summary>
    private static int _craftPasses;

    /// <summary>What the run says when it ends, once the currency it took out is back. See StepReturnSupply.</summary>
    private static string _craftFinishSaid = "";

    /// <summary>
    /// The crafting currency stacks the run took out of the currency tab: every stack of one in the inventory in a cell
    /// that was empty when the run started, so the player's own stay where they were. A stack the supply topped up in
    /// a cell already taken is not told apart, and stays. See InventoryCellsAtStart.
    /// </summary>
    private static List<(RectangleF Rect, string BaseName)> SuppliedStacksCarried(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var names = CraftingCurrencies.Select(c => c.BaseName).ToHashSet(StringComparer.Ordinal);
        var found = new List<(RectangleF, string)>();

        foreach (var slot in Safe.Read(main, static m => m.Inventory.InventorySlotItems, null) ?? [])
        {
            var name = Safe.Read(slot, static s => s.Item?.GetComponent<Base>()?.Name, null);
            var cell = (Safe.Read(slot, static s => s.PosX, -1), Safe.Read(slot, static s => s.PosY, -1));

            if (name != null && names.Contains(name) && !InventoryCellsAtStart.Contains(cell))
                found.Add((Safe.Read(slot, static s => s.GetClientRect(), default), name));
        }

        return found;
    }

    /// <summary>
    /// The crafting run's last step: each currency stack the run took out ctrl+clicked back, from whichever tab shows, one at
    /// a time and seen to leave; then the run ends - or, in a craft and reforge run, goes on to reforging with the
    /// inventory clear of them. Without it the stacks stayed in the inventory, and through a craft and reforge run's
    /// reforging (2026-10-09).
    /// </summary>
    private static void StepReturnSupply(GameController gc)
    {
        var stacks = SuppliedStacksCarried(gc).Where(s => s.Rect.Width > 0f).ToList();

        // In a craft and reforge run the stacks stay for the next pass, and go back at the end. See PutAway.
        if (stacks.Count == 0 || _craftAndReforge)
        {
            FinishRun(gc, _craftFinishSaid);
            return;
        }

        // From whichever tab is showing: a ctrl+click sends a currency stack to the currency tab with the stash open,
        // by the player's account (2026-10-09).
        SendStackBack(gc, stacks);
    }

    /// <summary>
    /// Ctrl+clicks the next of these inventory currency stacks back to the stash, in the order planned for them all, so
    /// the clicks are planned and drawn ahead as one batch. See StackBatchNext.
    /// </summary>
    private static void SendStackBack(GameController gc, IReadOnlyList<(RectangleF Rect, string Name)> stacks)
    {
        var ((rect, name), point) = StackBatchNext("currency back", stacks, ImGuiNET.ImGui.GetMousePos(), ctrl: true);
        var before = InventoryCurrencyHeld(gc).GetValueOrDefault(name);

        Send(rect, true, $"the {name} back", () => InventoryCurrencyHeld(gc).GetValueOrDefault(name) < before, point);
    }

    /// <summary>A currency use clicked and not yet read as made: the tablet as it was, its fingerprint, and when clicked.</summary>
    private sealed record PendingCurrencyUse(TabletOnScreen Before, string Fingerprint, DateTime Clicked);

    /// <summary>Currency uses clicked and not yet read as made, by the tablet's cell. See SettleCurrencyUses.</summary>
    private static readonly Dictionary<(int X, int Y), PendingCurrencyUse> CurrencyUsesPending = [];

    /// <summary>How many times each cell's use has been clicked again. See SettleCurrencyUses.</summary>
    private static readonly Dictionary<(int X, int Y), int> CurrencyUsesRetried = [];

    /// <summary>
    /// How many times a currency use is clicked again before the run stops: two, three clicks in all. The game refuses
    /// item operations that come too quickly - "Failed to apply item: The operation could not be completed." - and a
    /// refused use is clicked again rather than the run slowed down to avoid it (2026-10-09). A tablet refused three times
    /// is taken for something else being wrong.
    /// </summary>
    private const int CurrencyUseRetries = 2;

    /// <summary>A cell's key, its top left corner to the pixel.</summary>
    private static (int X, int Y) CellKeyOf(RectangleF cell) => ((int)MathF.Round(cell.X), (int)MathF.Round(cell.Y));

    /// <summary>
    /// Settles the currency uses clicked: one is made once a read shows its tablet changed, and written then. One still
    /// unchanged MoveSettle after its click goes back to the tablets to click - chosen again only if its next step is
    /// still this currency, so a use that did go through is never repeated - up to CurrencyUseRetries times, and then the
    /// run stops.
    /// False when the run was stopped.
    /// </summary>
    private static bool SettleCurrencyUses(string line)
    {
        foreach (var (key, pending) in CurrencyUsesPending.ToList())
        {
            var now = Seen.FirstOrDefault(t => CellKeyOf(t.Rect) == key);

            if (now != null && FingerprintOf(now) != pending.Fingerprint)
            {
                RollsCrafted(line, pending.Before, now);
                CurrencyUsesPending.Remove(key);
                continue;
            }

            if (_read - pending.Clicked < MoveSettle)
                continue;

            CurrencyUsesPending.Remove(key);

            // Which tablet, as it was, and what the read finds in its cell now, for telling a click the game refused
            // from a use read in the wrong place.
            var said = $"cell {key.X},{key.Y} {pending.Before.Where}, was {FingerprintOf(pending.Before)}, now " +
                       (now == null ? "nothing read in the cell" : FingerprintOf(now));

            var retried = CurrencyUsesRetried.GetValueOrDefault(key);

            if (retried >= CurrencyUseRetries)
            {
                LogReforging($"a {line} use was not seen after {retried + 1} clicks - {said}");
                StopReforging($"a {line} did not change a tablet after {retried + 1} clicks");
                return false;
            }

            CurrencyUsesRetried[key] = retried + 1;
            // **The orb is not counted back.** A use read late may have been made all the same, and counted back the last
            // orb of a stack: the cursor still reads as holding the currency after its stack runs out with shift held, so
            // the retry went out with nothing on the cursor and a shift+click picked the tablet up (dump 2026-10-13
            // 21:36:13). Counted low, the stack is let go and picked up again, which reads its real size. See _orbsLeft.
            LogReforging($"a {line} use was not seen {MoveSettle.TotalMilliseconds:0}ms after its click: the tablet is taken again - {said}");
            ReforgingSent.Remove(AddressOf(pending.Before));
        }

        return true;
    }

    /// <summary>When the cursor was first read holding a currency other than the one wanted next. See Choose.</summary>
    private static DateTime? _otherOrbSince;

    /// <summary>How long an orb let go of is given to leave the cursor before the run stops on it. Chosen.</summary>
    private static readonly TimeSpan OrbDropWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Each remembered stash cell's tablet's next step, by the same "tab index:sub-tab:x,y" as StashCraftingNeeds, for
    /// which sub-tabs a currency is wanted on. See NoteCraftingNeeds.
    /// </summary>
    private static readonly Dictionary<string, TabletAction> StashNextActions = new(StringComparer.Ordinal);

    /// <summary>The tablet action each crafting currency is, in CraftingCurrencies' order.</summary>
    private static readonly TabletAction[] CraftingActions =
        [TabletAction.Transmutation, TabletAction.Augmentation, TabletAction.Regal, TabletAction.Alchemy, TabletAction.Exalt];

    /// <summary>Starts a crafting run's own state. Called as any run starts.</summary>
    private static void StartCrafting(GameController gc)
    {
        _craftPhase = CraftPhase.Supply;
        CraftSupplied.Clear();
        CraftCurrenciesRunOut.Clear();
        _craftCurrency = 0;
        _craftSubTab = 0;
        _crafted = 0;
        _craftedAtPassStart = 0;
        _otherOrbSince = null;
        CurrencyUsesPending.Clear();
        CurrencyUsesRetried.Clear();
        _craftPasses = 1;
        _shiftHeld = false;
        CraftSubTabsDone.Clear();

        // Identifying every unidentified tablet comes first, so the currencies see what each needs. See TabletCraftIdentify.
        if (_run == TabletRun.Craft)
            StartCraftIdentifying(gc);
    }

    /// <summary>Whether shift reads as down on the keyboard, either side.</summary>
    private static bool ShiftDown() =>
        ExileCore2.Input.IsKeyDown(Keys.ShiftKey) || ExileCore2.Input.IsKeyDown(Keys.LShiftKey) || ExileCore2.Input.IsKeyDown(Keys.RShiftKey);

    /// <summary>Releases shift, which drops an orb kept on the cursor.</summary>
    private static void LetShiftGo()
    {
        if (_shiftHeld)
            Input?.Let(Keys.ShiftKey);

        _shiftHeld = false;

        // The orb goes back to its stack; the next pick-up reads it again.
        _orbsLeft = null;
    }

    /// <summary>The tablets on screen - the sub-tab showing and the inventory - whose next step is this action.</summary>
    private static List<TabletOnScreen> CraftTargets(TabletRerollingSettings settings, TabletAction action) =>
        Seen.Where(t => t.Where is "stash" or "stash tab elements" or "inventory")
            .Where(t => t.Uses == FullUses && (t.Rarity == ItemRarity.Normal || t.Identified) && ActionOf(t, settings) == action &&
                        !ReforgingSent.Contains(AddressOf(t)))
            .ToList();

    /// <summary>
    /// The next sub-tab the current currency is wanted on: one with a remembered tablet whose next step it is, the lowest
    /// number not done. Null when none. Every sub-tab has been seen by then: see the Scout phase.
    /// </summary>
    private static int? NextCraftSubTab(GameController gc, TabletAction action)
    {
        var fragment = StashTabWithIcon(gc, FragmentTabIcon);

        for (var number = 1; number <= 6; number++)
        {
            if (CraftSubTabsDone.Contains(number))
                continue;

            var prefix = $"{fragment}:{number}:";

            if (StashNextActions.Any(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && x.Value == action))
                return number;
        }

        return null;
    }

    /// <summary>A stack of this currency in the inventory, by base name, and its cell, or null.</summary>
    private static (RectangleF Rect, int Count)? InventoryStackOf(GameController gc, string baseName)
    {
        foreach (var item in Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].VisibleInventoryItems,
                     null) ?? [])
        {
            var entity = Safe.Read(item, static i => i.Item, null);

            if (Safe.Read(entity, static e => e.GetComponent<Base>()?.Name, null) == baseName)
                return (Safe.Read(item, static i => i.GetClientRectCache, default), StackSizeOf(entity));
        }

        return null;
    }

    /// <summary>How many a stack holds, from its Stack component; 0 when it cannot be read, which counts nothing down.</summary>
    private static int StackSizeOf(ExileCore2.PoEMemory.MemoryObjects.Entity entity) =>
        Safe.Read(entity, static e => e.GetComponent<Stack>()?.Size ?? 0, 0);

    /// <summary>A stack of this currency in the currency tab, by base name, and its cell, or null - the tab showing.</summary>
    private static (RectangleF Rect, int Count)? CurrencyTabStackOf(GameController gc, string baseName)
    {
        foreach (var item in Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.VisibleStash.VisibleInventoryItems, null) ?? [])
        {
            var entity = Safe.Read(item, static i => i.Item, null);

            if (Safe.Read(entity, static e => e.GetComponent<Base>()?.Name, null) == baseName)
                return (Safe.Read(item, static i => i.GetClientRectCache, default), StackSizeOf(entity));
        }

        return null;
    }

    /// <summary>
    /// How many orbs the stack picked up has left, counted down a use at a time; null when not known. With shift held the
    /// cursor still read as holding an Alchemy after its stack ran out, and the run clicked on for 4.5 seconds, 16
    /// clicks, until one tablet had missed three times (dump 2026-10-10 17:25:06). At nought it is taken as empty. Read
    /// afresh at each pick-up, and forgotten when the orb is let go. See StackSizeOf.
    /// </summary>
    private static int? _orbsLeft;

    /// <summary>Whether the stash shows the fragment tab's Expedition tablets: its numbered sub-tabs are up.</summary>
    private static bool OnExpeditionTablets(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IndexVisibleStash, -1) == StashTabWithIcon(gc, FragmentTabIcon) &&
        FragmentTabContent(gc) is { } content && NumberedSubTabs(content).Count >= 6;

    /// <summary>One step of the crafting run, chosen by phase from what the game shows now.</summary>
    private static void StepCrafting(GameController gc, TabletRerollingSettings settings)
    {
        // The stash is closed for part of it, so before the check below. See TabletCraftIdentify.
        if (CraftIdentifying)
        {
            StepCraftIdentifying(gc, settings);
            return;
        }

        if (_stashRect.Width <= 0f)
        {
            StopReforging("the stash closed");
            return;
        }

        if (_craftPhase == CraftPhase.ReturnSupply)
        {
            StepReturnSupply(gc);
            return;
        }

        if (_craftCurrency >= CraftingCurrencies.Length)
        {
            // **Again while the last pass did anything**: an Augmentation leaves a tablet wanting a Regal or an
            // Alchemy, and a pass in order can still miss one whose next step was read late. A pass that used nothing
            // means nothing is left to do, so this cannot go round without progress.
            if (_crafted > _craftedAtPassStart)
            {
                _craftedAtPassStart = _crafted;
                _craftPasses++;
                _craftCurrency = 0;
                CraftSubTabsDone.Clear();
                ReforgingSent.Clear();
                _craftPhase = CraftPhase.Choose;
                return;
            }

            _craftFinishSaid = $"done: nothing left to craft, {_crafted} currency used over {_craftPasses} pass(es)" +
                               (CraftCurrenciesRunOut.Count == 0 ? "" : $"; ran out of {string.Join(", ", CraftCurrenciesRunOut)}");
            _craftPhase = CraftPhase.ReturnSupply;
            _craftCurrency = CraftingCurrencies.Length;
            return;
        }

        var (line, baseName) = CraftingCurrencies[_craftCurrency];
        var action = CraftingActions[_craftCurrency];
        var holding = HeldOnCursor(gc) == action;

        // A currency run out of is passed over for the rest of the run, as finishing it everywhere is. See CraftCurrenciesRunOut.
        if (CraftCurrenciesRunOut.Contains(baseName) && !holding)
        {
            _craftCurrency++;
            CraftSubTabsDone.Clear();
            ReforgingSent.Clear();
            _craftPhase = CraftPhase.Choose;
            return;
        }

        switch (_craftPhase)
        {
            case CraftPhase.Supply:
            {
                // **Stacks only for the stash's tablets.** A stack saves going back and forth between the currency tab
                // and the sub-tabs for each currency; the inventory's tablets are crafted from the currency tab itself,
                // so they need none taken out. See PickUp.
                var need = StashCraftingNeedTotals();
                var held = InventoryCurrencyHeld(gc);
                var lacking = CraftingCurrencies
                    .Where(c => need.GetValueOrDefault(c.Line) > 0 && held.GetValueOrDefault(c.BaseName) == 0 && !CraftSupplied.Contains(c.BaseName))
                    .ToList();

                if (CraftingCurrencies.Count(c => need.GetValueOrDefault(c.Line) > 0) < 2 || lacking.Count == 0)
                {
                    _navigateStep = 0;
                    _craftPhase = CraftPhase.Navigate;
                    return;
                }

                if (!OnStashTab(gc, CurrencyTabIcon, "the currency tab"))
                    return;

                // None in the tab: the run goes on with what there is, and stops when it runs out.
                var stacks = new List<(RectangleF Rect, string Name)>();

                foreach (var (_, name) in lacking)
                {
                    if (CurrencyTabStackOf(gc, name) is { } tabStack)
                        stacks.Add((tabStack.Rect, name));
                    else
                        CraftSupplied.Add(name);
                }

                if (stacks.Count == 0)
                    return;

                // One batch of every stack wanted, so the clicks are planned and drawn ahead together. See StackBatchNext.
                var ((supplyRect, supplyName), point) = StackBatchNext("currency supply", stacks, ImGuiNET.ImGui.GetMousePos(), ctrl: true);
                var supplyLine = CraftingCurrencies.First(c => c.BaseName == supplyName).Line;
                var before = held.GetValueOrDefault(supplyName);

                CraftSupplied.Add(supplyName);
                Send(supplyRect, true, $"a stack of {supplyLine}", () => InventoryCurrencyHeld(gc).GetValueOrDefault(supplyName) > before,
                    point);
                return;
            }

            case CraftPhase.Navigate:
                NavigateNext(gc);
                return;

            case CraftPhase.Scout:
            {
                var fragment = StashTabWithIcon(gc, FragmentTabIcon);

                // Only those worth a look by what they held when last seen, even if not seen this session. See
                // SubTabWorthVisiting.
                var unseen = Enumerable.Range(1, 6)
                    .Where(n => !SeenSubTabs.Contains($"{fragment}:{n}") && SubTabWorthVisiting(gc, n, SubTabPurpose.Craftable)).ToList();

                if (unseen.Count == 0)
                {
                    if (FullScanDue && Enumerable.Range(1, 6).All(n => SeenSubTabs.Contains($"{fragment}:{n}")))
                        FullScanDone();

                    _craftPhase = CraftPhase.Choose;
                    return;
                }

                ClickSubTab(gc, unseen[0]);
                return;
            }

            case CraftPhase.Choose:
            {
                var showing = SelectedSubTab(gc);

                // **A new currency is never picked up over an orb still on the cursor** - but the last one is given
                // OrbDropWait to go. Letting go of shift drops it, and the cursor still read as holding it when the next
                // currency was chosen: the run stopped here straight after its last Transmutation, and the dump a moment
                // later read the cursor free (2026-10-09 23:27). Inferred from that, not seen frame by frame.
                if (HeldOnCursor(gc) is not TabletAction.None and var other && other != action)
                {
                    _otherOrbSince ??= DateTime.UtcNow;

                    if (DateTime.UtcNow - _otherOrbSince < OrbDropWait)
                    {
                        Say("waiting for the last orb to drop");
                        return;
                    }

                    StopReforging("an orb is still on the cursor: right-click it away and start again");
                    return;
                }

                _otherOrbSince = null;

                if (!CraftSubTabsDone.Contains(showing) && CraftTargets(settings, action).Count > 0)
                {
                    _craftSubTab = showing;
                    _craftPhase = CraftPhase.PickUp;
                    return;
                }

                CraftSubTabsDone.Add(showing);

                if (NextCraftSubTab(gc, action) is { } next)
                {
                    _craftSubTab = next;
                    _craftPhase = CraftPhase.PickUp;
                    return;
                }

                // This currency is finished everywhere: the next one, from the sub-tab showing.
                _craftCurrency++;
                CraftSubTabsDone.Clear();
                ReforgingSent.Clear();
                return;
            }

            case CraftPhase.PickUp:
                if (holding)
                {
                    // **Only the inventory wants it: used from where it was picked up.** The inventory shows beside any
                    // stash tab, so an orb right-clicked in the currency tab goes straight onto its tablets; it went back
                    // to the Expedition tablets first, and with no stash tablet needing it that was two tab changes for
                    // nothing (2026-10-10).
                    if (!CraftTargets(settings, action).Any(t => t.Where != "inventory") && NextCraftSubTab(gc, action) == null)
                    {
                        _craftPhase = CraftPhase.Apply;
                        return;
                    }

                    // Picked up in the currency tab: back to the Expedition tablets with the orb on the cursor, by left
                    // clicks only, then here again to go on to the sub-tab.
                    if (!OnExpeditionTablets(gc))
                    {
                        _navigateStep = 0;
                        _craftPhase = CraftPhase.Navigate;
                        return;
                    }

                    _craftPhase = SelectedSubTab(gc) == _craftSubTab ? CraftPhase.Apply : CraftPhase.GoToSubTab;
                    return;
                }

                // The inventory's stack first, as it needs no change of tab; else the currency tab's.
                if (InventoryStackOf(gc, baseName) is { } stack)
                {
                    // **The currency tab's stack counts too.** With the stash open, an orb picked up from the inventory
                    // goes on being used from the currency tab once the inventory's stack runs out, by the player's
                    // account; counted from the inventory's stack alone, a run of 20 Alchemies went back to the currency
                    // tab for more with one still on the cursor (2026-10-11 22:43:36). The tab's count is as last seen.
                    _orbsLeft = stack.Count > 0 ? stack.Count + (_currencyTabHeld?.GetValueOrDefault(baseName) ?? 0) : null;
                    LogReforging($"the {line} stack: {(_orbsLeft is { } inStack ? $"{inStack} orb(s)" : "count not read")}");
                    Send(stack.Rect, false, $"the {line} stack",() => HeldOnCursor(gc) == action, null, right: true);
                    return;
                }

                if (!OnStashTab(gc, CurrencyTabIcon, "the currency tab"))
                {
                    // Stopped, rather than on its way to the tab: there is no currency tab, and none in the inventory.
                    if (_reforgingStep == ReforgingStep.Idle)
                        CursorWarning.Show("Missing currency");

                    return;
                }

                // **None left: the next currency, not the end of the run.** See CraftCurrenciesRunOut.
                if (CurrencyTabStackOf(gc, baseName) is not { } kept)
                {
                    LogReforging($"no {baseName} in the inventory or the currency tab: going on to the next currency");
                    CraftCurrenciesRunOut.Add(baseName);
                    _craftCurrency++;
                    CraftSubTabsDone.Clear();
                    ReforgingSent.Clear();
                    _craftPhase = CraftPhase.Choose;
                    return;
                }

                _orbsLeft = kept.Count > 0 ? kept.Count : null;
                LogReforging($"the {line} stack in the currency tab: {(_orbsLeft is { } inTab ? $"{inTab} orb(s)" : "count not read")}");
                Send(kept.Rect, false, $"the {line} stack in the currency tab", () => HeldOnCursor(gc) == action, null, right: true);
                return;

            case CraftPhase.GoToSubTab:
                _craftPhase = CraftPhase.Apply;
                ClickSubTab(gc, _craftSubTab);
                return;

            case CraftPhase.Apply:
            {
                // Uses clicked and not yet read as made, settled before anything here is decided. See SettleCurrencyUses.
                if (!SettleCurrencyUses(line))
                    return;

                var targets = CraftTargets(settings, action);

                // Nothing past choosing the next tablet - leaving the sub-tab, dropping the orb, fetching more - while a
                // use is still to be read.
                if ((targets.Count == 0 || !holding) && CurrencyUsesPending.Count > 0)
                {
                    Say("waiting for the last uses to show");
                    return;
                }

                // **Nothing left here: shift up drops the orb.** Shift was never pressed when the orb came to a sub-tab that
                // needed none - the remembered tablets were out of date - so it goes down now and up on the next step.
                // Never a right-click to drop it: on a sub-tab that opens the sub-tab's own menu (2026-10-09).
                if (targets.Count == 0)
                {
                    CraftSubTabsDone.Add(_craftSubTab);

                    // **Another sub-tab wants this currency: straight there, the orb still on the cursor.** Shift stays
                    // down, as letting it go drops the orb; the run used to drop it here and fetch a fresh one from the
                    // currency tab for the next sub-tab (2026-10-09).
                    if (holding && NextCraftSubTab(gc, action) is { } onward)
                    {
                        _craftSubTab = onward;
                        _craftPhase = CraftPhase.GoToSubTab;
                        return;
                    }

                    if (holding && !_shiftHeld)
                    {
                        if (!Input.Hold(Keys.ShiftKey))
                        {
                            StopReforging("shift did not go down");
                            return;
                        }

                        _shiftHeld = true;
                        _shiftAt = DateTime.UtcNow;
                        return;
                    }

                    LetShiftGo();
                    _craftPhase = CraftPhase.Choose;
                    return;
                }

                // The stack ran out, or the orb was dropped: shift up, and pick it up again. Out by the count as well as by
                // the cursor, which went on reading as holding it. See _orbsLeft.
                if (!holding || _orbsLeft <= 0)
                {
                    if (CurrencyUsesPending.Count > 0)
                    {
                        Say("waiting for the last uses to show");
                        return;
                    }

                    LogReforging($"the {line} stack {(holding ? "is used up by the count" : "has left the cursor")}");
                    LetShiftGo();
                    _craftPhase = CraftPhase.PickUp;
                    return;
                }

                if (!_shiftHeld)
                {
                    if (!Input.Hold(Keys.ShiftKey))
                    {
                        StopReforging("shift did not go down");
                        return;
                    }

                    _shiftHeld = true;
                    _shiftAt = DateTime.UtcNow;
                    return;
                }

                if (DateTime.UtcNow - _shiftAt < ShiftSettle)
                    return;

                var mouse = ImGuiNET.ImGui.GetMousePos();
                // The first by the batch rule among the stash's tablets, before the inventory's; every later one by
                // distance from the cursor. See ItemClicks.NextItemOf.
                var pool = targets.Where(t => t.Where != "inventory").ToList() is { Count: > 0 } stashed ? stashed : targets;
                // From the batch as planned for this currency. See BatchNext.
                var (next, point) = BatchNext($"craft {line}", targets, mouse, _crafted == 0, _crafted == 0, ctrl: false,
                    firstPool: pool);
                var before = FingerprintOf(next);
                var cell = next.Rect;

                var key = CellKeyOf(cell);

                ReforgingSent.Add(AddressOf(next));
                MarkTabletChanging(AddressOf(next));
                _crafted++;
                _orbsLeft--;
                Progressed();
                CurrencyUsesPending[key] = new PendingCurrencyUse(next, before, DateTime.UtcNow + StepTimeout);

                // **Done at the click: the cursor goes straight on to the next tablet**, as the moves between stash and
                // inventory do. Waiting to see each use made a stop of 160 to 250ms on every tablet, the read of the
                // screen catching it or not (dump 2026-10-09 23:40). The use is pending until a read shows the tablet
                // changed. See SettleCurrencyUses.
                Send(cell, false, $"{line} on a tablet",
                    () =>
                    {
                        if (CurrencyUsesPending.TryGetValue(key, out var pending))
                            CurrencyUsesPending[key] = pending with { Clicked = _clickedAt };

                        return true;
                    },
                    point,
                    beforeClick: () =>
                    {
                        // **The stack ran out on the way here: this click is let go, not the run.** With clicks going
                        // straight on, the last orb can be used while the cursor is on its way to the next tablet. The
                        // tablet is not clicked, and the next step finds the cursor empty and fetches more.
                        if (HeldOnCursor(gc) != action)
                        {
                            ReforgingSent.Remove(AddressOf(next));
                            CurrencyUsesPending.Remove(key);
                            _crafted--;
                            _orbsLeft++;
                            return "";
                        }

                        return !ShiftDown() ? "shift was not down - the tablet was not clicked" : null;
                    });
                return;
            }
        }
    }
}
