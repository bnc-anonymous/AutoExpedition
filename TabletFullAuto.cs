using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The full-auto reforging run: one press of the action key on the stash's Reforge zone reforges every junk Expedition
/// Tablet the stash and inventory hold, round after round, until fewer than three junk tablets are left. See
/// TabletRerollingSettings.EnableTabletAutomation.
///
/// A round: in the fragment tab, open Tablets and the Expedition tablets, and withdraw the junk from sub-tabs 1 to 6
/// until they are empty or the inventory is full; Space to close; click the reforging bench's model and reforge; Space;
/// ctrl+click Doryani's model, which identifies the inventory; click the stash's model; deposit every tablet that is not
/// junk; and round again. Each step is confirmed from the game before the next, as reforging's are, and the same things
/// stop it: the mouse moved, the key again, a step that does not show.
///
/// Read off dumps of 2026-10-09: the stash, the reforging bench and Doryani by metadata path, each with the game's
/// interaction point for its model; Targetable.isTargeted true on the hovered model alone (16:25:39, 16:25:43); the
/// fragment tab's page buttons, tablet type icons and numbered sub-tabs (16:22-16:27). Space closing every panel and one
/// ctrl+click on Doryani identifying everything, with no window, are the player's account, not yet seen in a dump.
/// </summary>
internal static partial class Tablets
{
    private const string StashPath = "Metadata/MiscellaneousObjects/Stash";

    private const string ReforgingBenchPath = "Metadata/MiscellaneousObjects/Hideout/ReforgingBench";

    private const string DoryaniPath = "Metadata/NPC/Hideout/Doryani";

    private enum FullAutoPhase
    {
        /// <summary>In the stash: the fragment tab's Tablets page and its Expedition tablets.</summary>
        Navigate,

        /// <summary>Junk from sub-tabs 1 to 6 into the inventory.</summary>
        Withdraw,

        CloseStash,

        OpenBench,

        Reforge,

        CloseBench,

        /// <summary>Ctrl+click Doryani, who identifies the inventory.</summary>
        Identify,

        OpenStash,

        /// <summary>Every tablet that is not junk back into the stash.</summary>
        Deposit,

        /// <summary>At the end, the one or two junk tablets too few to reforge back into the stash as well.</summary>
        DepositLeftovers,
    }

    private static FullAutoPhase _phase;

    /// <summary>Where Navigate has got to: 0 the Tablets page, 1 the Expedition tablets, 2 done.</summary>
    private static int _navigateStep;

    /// <summary>Whether Navigate is followed by a deposit, as on a return to the stash, rather than a withdraw.</summary>
    private static bool _depositNext;

    /// <summary>
    /// The numbered sub-tabs this phase has finished with: emptied of junk by a withdraw, found full by a deposit. The
    /// next one is the lowest number not among them. Cleared when a phase begins.
    /// </summary>
    private static readonly HashSet<int> SubTabsDone = [];

    /// <summary>
    /// Junk tablets seen in each sub-tab a withdraw has passed without taking them, by sub-tab number. A withdraw takes
    /// nothing until these and the junk carried make three, so one or two junk tablets are left in the stash rather
    /// than taken out and deposited again at the end. See EnoughJunkToWithdraw.
    /// </summary>
    private static readonly Dictionary<int, int> JunkPassedBySubTab = [];

    /// <summary>Whether this withdraw has seen three junk tablets, carried and in the stash, and so takes them.</summary>
    private static bool _enoughJunkToWithdraw;

    /// <summary>
    /// Junk tablets drawn in the inventory that the bench will take: those that would not load this run are left out, as
    /// ReforgeNext leaves them out. Counted with them, the run judged three left, went back to a bench that found fewer,
    /// and went round until the click guard stopped it (2026-10-09 21:10).
    /// </summary>
    private static int ReforgeableJunkDrawn(GameController gc, TabletRerollingSettings settings) =>
        InventoryTablets().Count(t => IsJunk(gc, t, settings) && !Unloadable.Contains(AddressOf(t)));

    /// <summary>A withdraw begins: nothing passed over yet.</summary>
    private static void BeginWithdraw()
    {
        SubTabsDone.Clear();
        JunkPassedBySubTab.Clear();
        _enoughJunkToWithdraw = false;
        _moved = 0;
        _phase = FullAutoPhase.Withdraw;
        ReforgingSent.Clear();
    }

    /// <summary>
    /// Whether the withdraw should take junk now: once the junk carried and the junk seen in the sub-tabs visited reach
    /// three. The sub-tabs passed over before then are marked not done, so NextSubTab goes back to them.
    /// </summary>
    private static bool EnoughJunkToWithdraw(GameController gc, TabletRerollingSettings settings)
    {
        if (_enoughJunkToWithdraw)
            return true;

        var showing = SelectedSubTab(gc);
        var here = StashTablets().Count(t => IsJunk(gc, t, settings));

        // Keyed 0 when the sub-tab showing cannot be read, so its junk still counts.
        if (here > 0)
            JunkPassedBySubTab[Math.Max(0, showing)] = here;

        if (ReforgeableJunkDrawn(gc, settings) + JunkPassedBySubTab.Values.Sum() < 3)
            return false;

        _enoughJunkToWithdraw = true;

        foreach (var passed in JunkPassedBySubTab.Keys)
        {
            if (passed != showing)
                SubTabsDone.Remove(passed);
        }

        return true;
    }

    /// <summary>Rounds finished, for the last word.</summary>
    private static int _rounds;

    /// <summary>The hideout object whose model a move is aimed at, or null for an element on screen.</summary>
    private static Entity _worldTarget;

    /// <summary>Whether Space has already been pressed for the model click in flight. See the Moving step.</summary>
    private static bool _spacedForTarget;

    /// <summary>When the cursor arrived on a model the game did not yet count as targeted, or null.</summary>
    private static DateTime? _arrivedAt;

    private static int _hoverTries;

    /// <summary>How long a model may take to read as targeted after the cursor arrives on it.</summary>
    private static readonly TimeSpan HoverWait = TimeSpan.FromMilliseconds(400);

    /// <summary>How many points of a model are tried before the run stops.</summary>
    private const int HoverTries = 3;

    /// <summary>When the click in flight went out. A wait timed from the click, such as a sub-tab's, reads it.</summary>
    private static DateTime _clickedAt;

    /// <summary>How long the click in flight may take to show. See Send.</summary>
    private static TimeSpan _clickTimeout = StepTimeout;

    /// <summary>How long a click on a model may take to show: the character walks to it first.</summary>
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long a click on a page, a tablet type or a sub-tab is given to redraw the tab.</summary>
    private static readonly TimeSpan TabSettle = TimeSpan.FromMilliseconds(450);

    private static int _spaceTries;

    /// <summary>What the run says when it ends, once the leftovers are deposited. See FinishWithLeftovers.</summary>
    private static string _finishSaid = "";

    /// <summary>
    /// Ends a full-auto run that is in the stash: the one or two junk tablets left in the inventory, too few to reforge,
    /// go back into the stash first, then the run stops saying why.
    /// </summary>
    private static void FinishWithLeftovers(string said)
    {
        _finishSaid = said;
        _moved = 0;
        ReforgingSent.Clear();
        SubTabsDone.Clear();
        _phase = FullAutoPhase.DepositLeftovers;
    }

    /// <summary>Where the run goes once the stash is closed: the bench, or Doryani to identify first.</summary>
    private static FullAutoPhase _closeStashThen = FullAutoPhase.OpenBench;

    /// <summary>When the reforging bench was found open this time. See the Reforge phase.</summary>
    private static DateTime _benchOpenedAt;

    /// <summary>How long after the bench opens before its inventory is read: the reading runs every 150 ms.</summary>
    private static readonly TimeSpan BenchSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after the bench opens the inventory is given to show what the game says is carried.</summary>
    private static readonly TimeSpan BenchReadFor = TimeSpan.FromSeconds(2);

    /// <summary>Clicks on each hideout object, by metadata path, since the run last did something. See SendWorld.</summary>
    private static readonly Dictionary<string, int> ClicksSinceProgress = new(StringComparer.Ordinal);

    /// <summary>How many times an object may be clicked with nothing done between: once, and once again.</summary>
    private const int MostClicksSinceProgress = 2;

    /// <summary>Something was done - a tablet moved, a reforge, tablets identified - so objects may be clicked afresh.</summary>
    private static void Progressed()
    {
        ClicksSinceProgress.Clear();
        NotFoundSince.Clear();
        _worldRetried = false;
    }

    /// <summary>When each thing a run looked for was first not found, since it last got anywhere. See NotFoundYet.</summary>
    private static readonly Dictionary<string, DateTime> NotFoundSince = new(StringComparer.Ordinal);

    /// <summary>How long a run looks for a thing not found - a panel's control not drawn yet, an object not streamed in -
    /// before it stops.</summary>
    private static readonly TimeSpan NotFoundFor = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Whether to go on looking for a thing not found: true for NotFoundFor after it was first missed, saying so; false
    /// after, when the caller stops. A sub-tab, the reforge button or a hideout object missing for a frame stopped runs
    /// that a moment later would have found it.
    /// </summary>
    private static bool NotFoundYet(string what)
    {
        var now = DateTime.UtcNow;

        if (!NotFoundSince.TryGetValue(what, out var since))
            NotFoundSince[what] = since = now;

        if (now - since >= NotFoundFor)
            return false;

        Say($"looking for {what}");
        return true;
    }

    /// <summary>
    /// Whether a withdraw this run went through all six sub-tabs, so the stash has no junk left, rather than stopping
    /// for a full inventory. See the Identify phase.
    /// </summary>
    private static bool _stashEmptied;

    /// <summary>
    /// The Expedition Tablets in the player's inventory, read from the game's own record of it so it holds with the
    /// inventory panel closed: enough to tell junk by, with no element or cell. See UnidentifiedTabletsCarried.
    /// </summary>
    private static List<TabletOnScreen> CarriedTablets(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var tablets = new List<TabletOnScreen>();

        foreach (var entity in Safe.Read(main, static m => m.Inventory.Items, null) ?? [])
        {
            if (!(Safe.Read(entity, static x => x.Metadata, "") ?? "").StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var mods = Safe.Read(entity, static e => e.GetComponent<Mods>(), null);

            tablets.Add(new TabletOnScreen("carried", null, default, Safe.Read(mods, static m => m.ItemRarity, ItemRarity.Unknown),
                ModifiersOf(mods), entity, UsesOf(mods), Safe.Read(mods, static m => m.Identified, true)));
        }

        return tablets;
    }

    /// <summary>Whether the click in flight was on a model, so its effect is waited for after the walk. See SettleAfterWalk.</summary>
    private static bool _worldClick;

    /// <summary>
    /// How long the character stands still after a click on a model before its effect counts. About 200 ms is what the
    /// bench and Doryani take, by the player's watching (2026-10-09).
    /// </summary>
    private static readonly TimeSpan SettleAfterWalk = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after the character stops a click on a model may take to show before the run stops.</summary>
    private static readonly TimeSpan SettledFor = TimeSpan.FromSeconds(2);

    private static Vector2 _playerAt;

    private static DateTime _playerMovedAt = DateTime.MinValue;

    /// <summary>Since when the player's grid position has not changed, as read each step.</summary>
    private static DateTime PlayerStillSince(GameController gc)
    {
        var at = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (Vector2.DistanceSquared(at, _playerAt) > 0.01f)
        {
            _playerAt = at;
            _playerMovedAt = DateTime.UtcNow;
        }

        return _playerMovedAt;
    }

    private static DateTime _spaceAt;

    /// <summary>One full-auto step, chosen by phase from what the game shows now.</summary>
    private static void StepFullAuto(GameController gc, TabletRerollingSettings settings)
    {
        switch (_phase)
        {
            case FullAutoPhase.Navigate:
                NavigateNext(gc);
                return;

            case FullAutoPhase.Withdraw:
            {
                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                // A full inventory ends the withdraw at once, rather than after a tablet that cannot move times out.
                if (InventoryFreeCells(gc) == 0 && StashTablets().Any(t => IsJunk(gc, t, settings)))
                {
                    _stashEmptied = false;
                    _closeStashThen = FullAutoPhase.OpenBench;
                    _phase = FullAutoPhase.CloseStash;
                    _spaceTries = 0;
                    return;
                }

                if (EnoughJunkToWithdraw(gc, settings) &&
                    MoveNext(gc, StashTablets().Where(t => IsJunk(gc, t, settings)), JunkTablet, true))
                    return;

                if (NextSubTab(gc, SubTabPurpose.Junk) is { } next)
                {
                    ClickSubTab(gc, next);
                    return;
                }

                var carried = ReforgeableJunkDrawn(gc, settings);

                // Every sub-tab gone through: the stash holds no more junk, so later rounds need not come back for it.
                _stashEmptied = true;

                // **Too few junk tablets, but some carried are unidentified**: they may be junk once identified, so to
                // Doryani before calling it done. After him nothing is unidentified, so this comes round once.
                if (carried < 3 && UnidentifiedTabletsCarried(gc) > 0)
                {
                    _closeStashThen = FullAutoPhase.Identify;
                    _phase = FullAutoPhase.CloseStash;
                    _spaceTries = 0;
                    return;
                }

                // Every sub-tab visited with a full scan due: the next is FullScanEvery away. See TabletSubTabs.
                if (FullScanDue && Enumerable.Range(1, 6).All(SubTabsDone.Contains))
                    FullScanDone();

                if (carried < 3)
                {
                    var left = carried + JunkPassedBySubTab.Values.Sum();

                    FinishWithLeftovers(left == 0 ? "done: no junk left" : $"done: {left} junk tablet(s) left, fewer than three");
                    return;
                }

                _closeStashThen = FullAutoPhase.OpenBench;
                _phase = FullAutoPhase.CloseStash;
                _spaceTries = 0;
                return;
            }

            case FullAutoPhase.CloseStash:
                ClosePanels(gc, _closeStashThen, _closeStashThen == FullAutoPhase.Identify ? DoryaniPath : ReforgingBenchPath);
                return;

            case FullAutoPhase.OpenBench:
                if (ReforgingBenchOpen(gc))
                {
                    _phase = FullAutoPhase.Reforge;
                    _benchOpenedAt = DateTime.UtcNow;
                    ReforgingSent.Clear();
                    return;
                }

                SendWorld(gc, ReforgingBenchPath, false, "the reforging bench", () => ReforgingBenchOpen(gc));
                return;

            case FullAutoPhase.Reforge:
                if (!ReforgingBenchOpen(gc))
                {
                    StopReforging("the reforging bench closed");
                    return;
                }

                // **The inventory beside the bench is read before anything is decided from it.** Reforging was called done
                // on an inventory not yet drawn, and the run went bench, Space, bench, round and round (2026-10-09). So:
                // BenchSettle after the bench opens, and the inventory panel open.
                var sinceOpened = DateTime.UtcNow - _benchOpenedAt;

                if (sinceOpened < BenchSettle ||
                    !Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false) && sinceOpened < BenchReadFor)
                {
                    Say("waiting for the inventory");
                    return;
                }

                if (!ReforgeNext(gc, settings, out _))
                {
                    // Not done while the game's own record carries three junk tablets the drawn inventory does not show:
                    // given BenchReadFor to agree, then the run stops rather than leaving to come back.
                    var drawn = InventoryTablets().Count(t => IsJunk(gc, t, settings));
                    var carried = CarriedTablets(gc).Count(t => IsJunk(gc, t, settings));

                    if (carried >= 3 && drawn < 3)
                    {
                        if (sinceOpened < BenchReadFor)
                        {
                            Say("waiting for the inventory");
                            return;
                        }

                        StopReforging($"the inventory shows {drawn} junk tablet(s) but {carried} are carried - stopping");
                        return;
                    }

                    _phase = FullAutoPhase.CloseBench;
                    _spaceTries = 0;
                }

                return;

            case FullAutoPhase.CloseBench:
                // Doryani next when there is anything to identify; else whatever follows him, which closes the panels first.
                ClosePanels(gc, FullAutoPhase.Identify, UnidentifiedTabletsCarried(gc) > 0 ? DoryaniPath : null);
                return;

            case FullAutoPhase.Identify:
                if (UnidentifiedTabletsCarried(gc) == 0)
                {
                    // Identifying is progress only when Doryani was clicked for it this time.
                    if (_reforgingDoing == "Doryani")
                        Progressed();

                    // **Straight back to the bench when the stash has no junk left to give.** With three junk tablets in
                    // the inventory there is another reforge to do, and a trip to the stash would only deposit and find
                    // nothing to withdraw. The deposit waits for the last round.
                    // Less those set aside, as the bench counts them. See ReforgeableJunkDrawn.
                    if (_stashEmptied && CarriedTablets(gc).Count(t => IsJunk(gc, t, settings)) - Unloadable.Count >= 3)
                    {
                        _rounds++;
                        _phase = FullAutoPhase.OpenBench;
                        return;
                    }

                    _phase = FullAutoPhase.OpenStash;
                    return;
                }

                SendWorld(gc, DoryaniPath, true, "Doryani", () => UnidentifiedTabletsCarried(gc) == 0);
                return;

            case FullAutoPhase.OpenStash:
                if (_stashRect.Width > 0f)
                {
                    _phase = FullAutoPhase.Navigate;
                    _navigateStep = 0;
                    _depositNext = true;
                    return;
                }

                SendWorld(gc, StashPath, false, "the stash", () => _stashRect.Width > 0f);
                return;

            case FullAutoPhase.DepositLeftovers:
                // In a craft and reforge run the leftovers stay: the next crafting pass may make the junk to go with
                // them, and the put-away at the end deposits whatever is left. See PutAway.
                if (_craftAndReforge)
                {
                    FinishRun(gc, _finishSaid);
                    return;
                }

                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                if (StashTablets().Count >= SubTabHolds && InventoryTablets().Any(t => IsJunk(gc, t, settings)) &&
                    NextSubTab(gc, SubTabPurpose.Room) is { } roomForLeftovers)
                {
                    ClickSubTab(gc, roomForLeftovers);
                    return;
                }

                if (MoveNext(gc, InventoryTablets().Where(t => IsJunk(gc, t, settings)), DepositedTablet, false))
                    return;

                FinishRun(gc, _finishSaid);
                return;

            case FullAutoPhase.Deposit:
                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                // A full sub-tab showing: to one with room first, rather than waiting out a tablet that cannot go in.
                if (StashTablets().Count >= SubTabHolds && Depositable(gc, settings).Count > 0 &&
                    NextSubTab(gc, SubTabPurpose.Room) is { } room)
                {
                    ClickSubTab(gc, room);
                    return;
                }

                if (MoveNext(gc, Depositable(gc, settings), DepositedTablet, false))
                    return;

                // **In a craft and reforge run, tablets wanting a currency are crafted before more junk comes out.** Kept in
                // the inventory round after round, they took the room the junk would have, and each round reforged less
                // (2026-10-10). So the reforging pass ends here and the crafting pass works on them where they are; any
                // it makes junk are then in place for the next reforging pass.
                if (_craftAndReforge && _reforged > 0 &&
                    InventoryTablets().Any(t => t.Uses == FullUses && CraftingActions.Contains(ActionOf(t, settings))))
                {
                    FinishWithLeftovers($"{_reforged} reforged, crafting what reforging made");
                    return;
                }

                // Nothing left in the stash to withdraw, and too few junk tablets carried to reforge: done once deposited.
                if (_stashEmptied)
                {
                    // Junk left in the stash for being too few may make three with what Doryani has just identified.
                    if (JunkPassedBySubTab.Count > 0 &&
                        ReforgeableJunkDrawn(gc, settings) + JunkPassedBySubTab.Values.Sum() >= 3)
                    {
                        _stashEmptied = false;
                        BeginWithdraw();
                        return;
                    }

                    var carriedJunk = InventoryTablets().Count(t => IsJunk(gc, t, settings));

                    FinishWithLeftovers(carriedJunk == 0 ? "done: no junk left" : $"done: {carriedJunk} junk tablet(s) left, fewer than three");
                    return;
                }

                _rounds++;
                BeginWithdraw();
                return;
        }
    }

    /// <summary>
    /// The visible stash tab's content element, its children the fragment tab's controls, or null when the stash is
    /// closed or the tab is not one with page buttons Fragments, Tablets and Trials.
    /// </summary>
    private static Element FragmentTabContent(GameController gc)
    {
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var panel = Safe.Read(stash, static s => s.StashInventoryPanel, null);
        var index = Safe.Read(stash, static s => s.IndexVisibleStash, -1);
        var tab = panel != null && index >= 0 ? Safe.Read(() => panel.Children[index], null) : null;
        var content = Safe.Read(tab, static t => t.Children[0], null);
        var pages = Safe.Read(content, static c => c.Children[1], null);

        return pages != null && TextsUnder(pages).Contains("Tablets") ? content : null;
    }

    /// <summary>
    /// The fragment tab brought to its Expedition tablets: the Tablets page button when the numbered sub-tabs are not
    /// showing, then the third tablet type icon - Expedition, by place alone, the icons reading only their counts.
    /// </summary>
    private static void NavigateNext(GameController gc)
    {
        // The fragment tab first, found by its icon in the tab list. See OnStashTab.
        if (_navigateStep == 0 && !OnStashTab(gc, FragmentTabIcon, "the fragment tab"))
            return;

        if (FragmentTabContent(gc) is not { } content)
        {
            StopReforging("open the stash on the fragment tab");
            return;
        }

        switch (_navigateStep)
        {
            case 0:
                _navigateStep = 1;

                if (NumberedSubTabs(content).Count >= 6)
                    return;

                var tablets = Safe.Read(content, static c => c.Children[1].Children[1], null);
                var page = Safe.Read(tablets, static t => t.GetClientRectCache, default);

                Send(page, false, "the Tablets page", () => FragmentTabContent(gc) is { } now && NumberedSubTabs(now).Count >= 6,
                    null);
                return;

            case 1:
                _navigateStep = 2;

                // Already showing Expedition Tablets: no click. Nothing on the icons tells which type is selected - every
                // flag and texture read the same with Expedition and with Precursor selected (dumps of 2026-10-09
                // 16:39:57 and 16:40:02) - so the tab's items are asked. An empty tab cannot say, and is clicked.
                if (VisibleTabItemPaths(gc) is { Count: > 0 } shown &&
                    shown.All(x => x.StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase)))
                    return;

                var expedition = Safe.Read(content, static c => c.Children[2].Children[2], null);
                var icon = Safe.Read(expedition, static e => e.GetClientRectCache, default);

                Send(icon, false, "the Expedition tablets", () => DateTime.UtcNow - _clickedAt > TabSettle, null);
                return;

            default:
                // A crafting run comes here at its start, and again carrying an orb from the currency tab: with an orb, on to
                // its sub-tab; without, to choosing one.
                // The tidy comes here before its scan. See TabletTidy.
                if (_run == TabletRun.Tidy)
                {
                    _tidyPhase = TidyPhase.Scan;
                    return;
                }

                // So does the withdraw by worth. See TabletWithdrawValuable.
                if (_run == TabletRun.WithdrawValuable)
                {
                    _withdrawPhase = WithdrawPhase.Scan;
                    return;
                }

                // The identification step comes here to withdraw and to deposit. See TabletCraftIdentify.
                if (_run == TabletRun.Craft && _craftPhase is CraftPhase.IdentifyNavigate or CraftPhase.IdentifyDepositNavigate)
                {
                    _craftPhase = _craftPhase == CraftPhase.IdentifyNavigate ? CraftPhase.IdentifyWithdraw : CraftPhase.IdentifyDeposit;
                    return;
                }

                if (_run == TabletRun.Craft)
                {
                    _craftPhase = HeldOnCursor(gc) == CraftingActions[Math.Min(_craftCurrency, CraftingActions.Length - 1)]
                        ? CraftPhase.PickUp
                        : CraftPhase.Scout;
                    return;
                }

                if (_depositNext)
                {
                    SubTabsDone.Clear();
                    _moved = 0;
                    _phase = FullAutoPhase.Deposit;
                    ReforgingSent.Clear();
                }
                else
                {
                    BeginWithdraw();
                }

                _depositNext = false;
                return;
        }
    }

    /// <summary>The metadata of every item the visible stash tab shows, found by walking its elements.</summary>
    private static List<string> VisibleTabItemPaths(GameController gc)
    {
        var paths = new List<string>();
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var panel = Safe.Read(stash, static s => s.StashInventoryPanel, null);
        var index = Safe.Read(stash, static s => s.IndexVisibleStash, -1);
        var tab = panel != null && index >= 0 ? Safe.Read(() => panel.Children[index], null) : null;
        var stack = new Stack<(Element, int)>();
        var visited = 0;

        if (tab != null)
            stack.Push((tab, 0));

        while (stack.Count > 0 && visited++ < MostElements)
        {
            var (element, depth) = stack.Pop();

            if (!Safe.Read(element, static e => e.IsVisible, false))
                continue;

            if (Safe.Read(element, static e => e.Entity?.Metadata, null) is { Length: > 0 } path)
                paths.Add(path);

            if (depth < DeepestElement)
                foreach (var child in Safe.Read(element, static e => e.Children, null) ?? [])
                    stack.Push((child, depth + 1));
        }

        return paths;
    }

    /// <summary>
    /// The numbered sub-tab showing, or 0 when it cannot be told. The selected one is moved to the last of its parent's
    /// children: child 6 of 7 with sub-tab 1, 2, 3 and 4 selected in turn, the others below it in reverse (dumps of
    /// 2026-10-09 16:22-16:43). Nothing on the sub-tab itself reads differently - HasShinyHighlight did not hold.
    /// </summary>
    private static int SelectedSubTab(GameController gc)
    {
        using var timing = Spent.On("Tablets/SelectedSubTab");

        if (FragmentTabContent(gc) is not { } content)
            return 0;

        // **The bar reached by its remembered route from the live tab, its last child read by its number.** Searched for
        // each time, the sub-tabs cost about 10ms a time - NumberedSubTabs walks the whole tab, tablets included - three
        // times a read, up to 99ms (dump 2026-10-09 22:39).
        //
        // **Not the bar element itself, and not its children's addresses.** Kept, the bar went stale when the game rebuilt
        // it: after a trip to the currency tab and back, a click on sub-tab 2 was never read as made, though a search
        // found 2 selected (dump 2026-10-09 23:24), and the run stopped. Following the route from the live tab each time
        // reads whatever is there now, and the number is read off the child, as the search reads it. Any step that does
        // not hold falls back to the search, which finds the route again.
        if (_subTabBarRoute is { Length: > 0 } route &&
            Safe.Read(() => content.GetChildFromIndices(route), null) is { } bar &&
            Safe.Read(bar, static b => b.IsVisible, false) &&
            Safe.Read(bar, static b => b.ChildCount, 0L) is > 0 and var count &&
            Safe.Read(() => bar.GetChildAtIndex((int)count - 1), null) is { } last &&
            SubTabNumberOf(last) is > 0 and var number)
            return number;

        FindSubTabBarRoute(content);
        return SelectedSubTabBySearch(content);
    }

    /// <summary>
    /// The child indices from the fragment tab's content element down to the element its numbered sub-tabs sit in, or
    /// null when they do not all sit in one. See SelectedSubTab.
    /// </summary>
    private static int[] _subTabBarRoute;

    /// <summary>Finds the numbered sub-tabs and remembers the route to their bar, when they share one. See SelectedSubTab.</summary>
    private static void FindSubTabBarRoute(Element content)
    {
        _subTabBarRoute = null;

        var tabs = NumberedSubTabs(content);
        var parents = tabs.Select(t => Safe.Read(t.Element, static e => e.Parent, null)).ToList();

        if (tabs.Count == 0 || parents.Select(p => Safe.Read(p, static e => e.Address, 0L)).Distinct().ToList() is not [not 0L])
            return;

        var contentAddress = Safe.Read(content, static c => c.Address, 0L);
        var route = new List<int>();

        for (var at = parents[0]; at != null && route.Count < 16; at = Safe.Read(at, static e => e.Parent, null))
        {
            if (Safe.Read(at, static e => e.Address, 0L) == contentAddress)
            {
                route.Reverse();
                _subTabBarRoute = [.. route];
                return;
            }

            if (Safe.Read(at, static e => e.IndexInParent, null) is not { } index)
                return;

            route.Add(index);
        }
    }

    /// <summary>
    /// A numbered sub-tab's number, read as NumberedSubTabs reads it: an element of a sub-tab's size whose only text
    /// below it is a digit from 1 to 9. 0 when it is not one.
    /// </summary>
    private static int SubTabNumberOf(Element element)
    {
        var rect = Safe.Read(element, static e => e.GetClientRectCache, default);

        return rect.Width is >= 60f and <= 100f && rect.Height is >= 28f and <= 48f &&
               TextsUnder(element) is [var only] && only.Length == 1 && char.IsDigit(only[0]) && only[0] != '0'
            ? only[0] - '0'
            : 0;
    }

    /// <summary>The selected sub-tab by a search of the whole tab: the one that is its parent's last child.</summary>
    private static int SelectedSubTabBySearch(Element content)
    {
        foreach (var (number, element) in NumberedSubTabs(content))
        {
            var parent = Safe.Read(element, static e => e.Parent, null);

            if (Safe.Read(element, static e => e.IndexInParent, -1) == Safe.Read(parent, static p => p.ChildCount, 0) - 1)
                return number;
        }

        return 0;
    }

    /// <summary>
    /// The next sub-tab to go to: the one showing counts as done, and the next is the lowest number not done. Null when
    /// all six are.
    /// </summary>
    private static int? NextSubTab(GameController gc, SubTabPurpose purpose)
    {
        if (SelectedSubTab(gc) is > 0 and var showing)
            SubTabsDone.Add(showing);

        for (var number = 1; number <= 6; number++)
        {
            if (SubTabsDone.Contains(number))
                continue;

            // Not worth a visit by what it held when last seen: passed by, and counted as done. See SubTabWorthVisiting.
            if (!SubTabWorthVisiting(number, purpose))
            {
                SubTabsDone.Add(number);
                continue;
            }

            return number;
        }

        return null;
    }

    /// <summary>
    /// Clicks a numbered sub-tab of the Expedition tablets, confirmed by it becoming the selected one and given a moment
    /// to show its tablets.
    /// </summary>
    private static void ClickSubTab(GameController gc, int number)
    {
        var tab = FragmentTabContent(gc) is { } content ? NumberedSubTabs(content).FirstOrDefault(x => x.Number == number).Element : null;
        var rect = Safe.Read(tab, static t => t.GetClientRectCache, default);

        if (rect.Width <= 0f)
        {
            if (NotFoundYet($"sub-tab {number}"))
                return;

            StopReforging($"sub-tab {number} was not found");
            return;
        }

        Send(rect, false, $"sub-tab {number}", () => SelectedSubTab(gc) == number && DateTime.UtcNow - _clickedAt > SubTabSettle,
            null);
    }

    /// <summary>How long after a sub-tab reads selected its tablets are given to be read: the reading runs every 150 ms.</summary>
    private static readonly TimeSpan SubTabSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the stash, the reforging bench or the inventory is open.</summary>
    private static bool AnyPanelOpen(GameController gc) =>
        _stashRect.Width > 0f || ReforgingBenchOpen(gc) ||
        Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false);

    /// <summary>
    /// Whether a hideout object's model is on screen and clear of every open panel - the stash, the inventory, the
    /// reforging bench - by its interaction point and a margin around it.
    /// </summary>
    private static bool ObjectInView(GameController gc, string path)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (camera == null || HideoutObject(gc, path) is not { } entity)
            return false;

        var render = Safe.Read(entity, static e => e.GetComponent<Render>(), null);
        var interact = Safe.Read(render, static r => r.InteractCenter, Vector3.Zero);
        var at = interact == Vector3.Zero ? Safe.Read(entity, static e => e.BoundsCenterPos, Vector3.Zero) : interact;
        var point = Safe.Read(() => camera.WorldToScreen(at), Vector2.Zero);
        const float margin = 40f;

        if (point == Vector2.Zero || point.X < margin || point.Y < margin || point.X > window.Width - margin ||
            point.Y > window.Height - margin)
            return false;

        var panels = new List<RectangleF>
        {
            _stashRect,
            Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible ? g.IngameState.IngameUi.InventoryPanel.GetClientRectCache : default,
                default),
            Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench.IsVisible ? g.IngameState.IngameUi.ReforgingBench.GetClientRectCache : default,
                default),
        };

        // **The stash's tab list, when shown, reaches past the stash panel's own rectangle**: its buttons ran from x 917 to
        // 1101 with the panel ending at 887, and covered Doryani at 947 (dump of 2026-10-09 19:15:20).
        if (_stashRect.Width > 0f && TabListShown(gc))
        {
            foreach (var button in Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.TabListButtons, null) ?? [])
            {
                if (Safe.Read(button, static x => x.IsVisible, false))
                    panels.Add(Safe.Read(button, static x => x.GetClientRectCache, default));
            }
        }

        return panels.All(p => p.Width <= 0f || point.X < p.Left - margin || point.X > p.Right + margin ||
                               point.Y < p.Top - margin || point.Y > p.Bottom + margin);
    }

    /// <summary>Space until every panel is closed, a few tries apart, then the next phase.</summary>
    /// <param name="nextPath">The hideout object the next phase clicks, or null to close the panels regardless.</param>
    private static void ClosePanels(GameController gc, FullAutoPhase next, string nextPath)
    {
        if (PanelsClosedFor(gc, nextPath))
            _phase = next;
    }

    /// <summary>
    /// Space until every panel is closed, a few tries apart: true once the next object can be clicked. Stops the run when
    /// Space does not close them.
    /// </summary>
    /// <param name="nextPath">The hideout object clicked next, or null to close the panels regardless.</param>
    private static bool PanelsClosedFor(GameController gc, string nextPath)
    {
        // **Space only when the next object cannot be seen.** Clicking a hideout object closes the panels anyway, so
        // when its model is on screen and clear of every open panel, the click goes straight there. The click still
        // waits for the game to count the model as targeted, so a panel over it after all is caught there.
        if (!AnyPanelOpen(gc) || nextPath != null && ObjectInView(gc, nextPath))
        {
            _spaceTries = 0;
            return true;
        }

        if (_spaceTries > 0 && DateTime.UtcNow - _spaceAt < TimeSpan.FromMilliseconds(400))
            return false;

        if (++_spaceTries > 3)
        {
            StopReforging("Space did not close the panels");
            return false;
        }

        if (!Input.Tap(Keys.Space))
        {
            StopReforging("the Space key did not go");
            return false;
        }

        _spaceAt = DateTime.UtcNow;
        Say("closing the panels");
        return false;
    }

    /// <summary>The nearest hideout object at this metadata path, or null.</summary>
    private static Entity HideoutObject(GameController gc, string path) =>
        (Safe.Read(gc, static g => g.EntityListWrapper.Entities, null) ?? [])
        .Where(e => string.Equals(Safe.Read(e, static x => x.Metadata, ""), path, StringComparison.OrdinalIgnoreCase))
        .MinBy(e => Safe.Read(e, static x => x.DistancePlayer, float.MaxValue));

    /// <summary>Whether the game counts an object as the one under the cursor.</summary>
    private static bool IsTargeted(Entity entity) =>
        Safe.Read(entity, static e => e.GetComponent<Targetable>()?.isTargeted ?? false, false);

    /// <summary>How far from a model's interaction point a click may land, as a share of its projected box.</summary>
    private const float ModelSpread = 0.15f;

    /// <summary>
    /// A point on a model to aim at: the game's interaction point for it (Render.InteractCenter, else the bounds' centre)
    /// projected to the screen, moved a random way within ModelSpread of its projected box. Null when it does not land
    /// inside the window.
    /// </summary>
    private static Vector2? WorldPointOf(GameController gc, Entity entity)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var render = Safe.Read(entity, static e => e.GetComponent<Render>(), null);
        var interact = Safe.Read(render, static r => r.InteractCenter, Vector3.Zero);
        var at = interact == Vector3.Zero ? Safe.Read(entity, static e => e.BoundsCenterPos, Vector3.Zero) : interact;
        var bounds = Safe.Read(render, static r => r.Bounds, Vector3.Zero);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (camera == null || at == Vector3.Zero)
            return null;

        var centre = Safe.Read(() => camera.WorldToScreen(at), Vector2.Zero);
        var across = Safe.Read(() => camera.WorldToScreen(at + new Vector3(bounds.X, 0f, 0f)), centre) - centre;
        var up = Safe.Read(() => camera.WorldToScreen(at + new Vector3(0f, 0f, -bounds.Z)), centre) - centre;
        var width = Math.Max(Math.Abs(across.X), 8f);
        var height = Math.Max(Math.Abs(up.Y), 8f);
        var point = centre + new Vector2(((float)Aim.NextDouble() * 2f - 1f) * ModelSpread * width,
            ((float)Aim.NextDouble() * 2f - 1f) * ModelSpread * height);

        return point.X > 8f && point.Y > 8f && point.X < window.Width - 8f && point.Y < window.Height - 8f ? point : null;
    }

    /// <summary>
    /// Aims at a hideout object's model, clicks it once the game counts it as targeted, and waits for done, the walk to it
    /// included. See StepReforging's Moving step.
    /// </summary>
    private static void SendWorld(GameController gc, string path, bool ctrl, string doing, Func<bool> done)
    {
        // **No object is clicked a third time with nothing done in between.** The run went back and forth to the
        // reforging bench on its own (2026-10-09); whatever the cause, a loop of clicks must not be possible. Once again
        // is allowed - a click can miss - and then the run stops, saying what it read.
        // Not yet in sight: looked for a moment before it counts as a click, or stops the run. See NotFoundYet.
        var found = HideoutObject(gc, path);

        if (found == null || WorldPointOf(gc, found) == null)
        {
            if (NotFoundYet(doing))
                return;

            StopReforging(found == null ? $"{doing} was not found nearby" : $"{doing} is not on screen");
            return;
        }

        var clicks = ClicksSinceProgress.TryGetValue(path, out var before) ? before + 1 : 1;

        if (clicks > MostClicksSinceProgress)
        {
            StopReforging($"{doing} was clicked {before} times with nothing done between - stopping " +
                          $"({InventoryTablets().Count(t => t.Uses == FullUses && t.Rarity == ItemRarity.Rare && t.Identified)} identified " +
                          $"tablets drawn in the inventory, {CarriedTablets(gc).Count(t => t.Identified)} carried)");
            return;
        }

        ClicksSinceProgress[path] = clicks;

        if (HideoutObject(gc, path) is not { } entity)
        {
            StopReforging($"{doing} was not found nearby");
            return;
        }

        // A hideout object is never a hop between tablets. See UseShortestMove.
        UseShortestMove(false);
        _previousSent = doing;
        UseTolerance(OtherTolerancePx);
        _clickCell = null;

        if (WorldPointOf(gc, entity) is not { } point || !Input.MoveTo(point, ctrl: ctrl))
        {
            // Seen a moment ago: the camera or the cursor in between. Its phase asks again, which the click guard bounds.
            LogReforging($"{doing} could not be aimed at: trying once more");
            return;
        }

        _reforgingDoing = doing;
        _reforgingCtrl = ctrl;
        _reforgingRight = false;
        _beforeClick = null;
        _reforgingDone = done;
        _clickTimeout = WalkTimeout;
        _worldTarget = entity;
        _worldClick = true;
        _arrivedAt = null;
        _hoverTries = 0;
        _spacedForTarget = false;
        _reforgingStep = ReforgingStep.Moving;
        _reforgingDeadline = DateTime.UtcNow + StepTimeout;
        Say($"{(ctrl ? "ctrl+clicking" : "clicking")} {doing}");
    }

    /// <summary>
    /// Free cells in the player's inventory: its columns times rows, less every item's own cells, from the game's own
    /// record of it (ServerData). -1 when it cannot be read.
    /// </summary>
    private static int InventoryFreeCells(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var inventory = Safe.Read(main, static m => m.Inventory, null);
        var cells = Safe.Read(inventory, static i => i.Columns * i.Rows, 0);

        if (cells <= 0)
            return -1;

        var taken = (Safe.Read(inventory, static i => i.InventorySlotItems, null) ?? [])
            .Sum(x => Math.Max(1, Safe.Read(x, static s => s.SizeX * s.SizeY, 1)));

        return Math.Max(0, cells - taken);
    }

    /// <summary>
    /// Unidentified Expedition Tablets in the player's inventory, read from the game's own record of it
    /// (ServerData.PlayerInventories, MainInventory1) so it holds with the inventory panel closed.
    /// </summary>
    private static int UnidentifiedTabletsCarried(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var items = Safe.Read(main, static m => m.Inventory.Items, null);

        return items?.Count(e =>
            (Safe.Read(e, static x => x.Metadata, "") ?? "").StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase) &&
            !Safe.Read(e, static x => x.GetComponent<Mods>()?.Identified ?? true, true)) ?? 0;
    }
}
