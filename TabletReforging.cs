using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.Shared.Enums;
using Newtonsoft.Json;
using Graphics = ExileCore2.Graphics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Reforges junk Expedition Tablets at the reforging bench, three at a time, when the action key is pressed with the
/// bench open. Junk: rare, identified, at full uses, and under Minimum weight to keep - a tablet that would never
/// be priced. Nothing else is ever put in.
///
/// One step at a time, each read off the screen before the next: ctrl+click three junk tablets from the inventory into
/// the bench; click its "reforge" button; ctrl+click the next three in; ctrl+click the unidentified tablet the last
/// reforge made back into the inventory; reforge; and so on until fewer than three junk tablets are left, when the
/// last reforged tablet is taken out and the run ends. The next three go in before the reforged tablet comes out
/// because the bench takes a moment to make the new tablet, and loading the next three covers that wait. No reforge is
/// clicked until the last one's tablet has appeared and been taken out.
///
/// A step that the screen does not confirm within StepTimeout stops the run, except taking out the reforged tablet,
/// which is tried up to ReforgedTakeTries times. The player touching the mouse (ExileInput2's Stopped), the bench
/// closing, the key again, or an identified tablet in the bench that is not junk stop it too.
///
/// The steps were read off three dumps of 2026-10-09: the bench is IngameUi.ReforgingBench, its button a descendant
/// with the text "reforge", and the new tablet appears in the bench unidentified. That the bench's tablets can be read
/// by walking its elements, as the Fragment tab's are, is assumed: ReforgingSaid prints what the walk finds.
/// </summary>
internal static partial class Tablets
{
    /// <summary>The cursor, shared with placement. Set by the plugin as it starts.</summary>
    internal static ExileInput2Client Input { get; set; }

    /// <summary>Picks where on a hideout object a click lands. See ItemClicks for clicks on items in a grid.</summary>
    private static readonly Random Aim = new();

    /// <summary>How long a step's effect may take to show before the run stops.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How many times taking out a reforged tablet is tried before the run stops. See the class summary.</summary>
    private const int ReforgedTakeTries = 4;

    /// <summary>Takes of the reforged tablet tried so far, for the one now in the bench.</summary>
    private static int _reforgedTakes;

    /// <summary>
    /// Since when a reforge has been clicked whose tablet has not yet been taken out. The next reforge waits for it: a
    /// reforge clicked before the last one's tablet appeared did nothing (dump of 2026-10-09 15:05:47).
    /// </summary>
    private static DateTime? _reforgedPendingSince;

    /// <summary>
    /// How long a reforge's tablet may take to appear, from the click on "reforge", before the run stops. About a second
    /// usually and three at most, by the player's watching (2026-10-09). The next three load meanwhile.
    /// </summary>
    private static readonly TimeSpan ReforgedWait = TimeSpan.FromSeconds(3);

    /// <summary>How long the run's last word stays drawn after it stops. The solver's run's, Placement.Linger, as well.</summary>
    private static readonly TimeSpan ReforgingEndShownFor = TimeSpan.FromSeconds(4);

    private enum ReforgingStep
    {
        Idle,

        /// <summary>The cursor is on its way to the target; the click goes on arrival.</summary>
        Moving,

        /// <summary>Clicked; waiting for the bench to show the effect.</summary>
        Clicked,
    }

    private static ReforgingStep _reforgingStep = ReforgingStep.Idle;

    /// <summary>What the click in flight is for, for the status line, and whether it is ctrl+clicked.</summary>
    private static string _reforgingDoing = "";

    private static bool _reforgingCtrl;

    /// <summary>Whether the click in flight is a right-click: picking up a currency.</summary>
    private static bool _reforgingRight;

    /// <summary>
    /// Asked on arrival, just before the click: a reason not to click, which stops the run, or null. A crafting click
    /// refuses unless its currency is on the cursor and shift is down, so a click can never pick a tablet up.
    /// </summary>
    private static Func<string> _beforeClick;

    /// <summary>Whether the game now shows what the click in flight was for. Read every step.</summary>
    private static Func<bool> _reforgingDone;

    private static DateTime _reforgingDeadline;

    /// <summary>The entities already ctrl+clicked into the bench this round, so a slow move is not clicked twice.</summary>
    private static readonly HashSet<long> ReforgingSent = [];

    private static int _reforged;

    private static string _reforgingSaid = "";

    private static DateTime _reforgingSaidAt = DateTime.MinValue;

    private static bool _reforgingHasCursor;

    /// <summary>Whether a tablet run holds the cursor now, for the action key to stop it. See AutoExpedition.ActionKeyWentDown.</summary>
    internal static bool RunHoldsCursor => _reforgingHasCursor;

    /// <summary>Since when the run has been paused for the game taking typed input, or null. See StepReforging.</summary>
    private static DateTime? _reforgingPausedSince;

    /// <summary>How long a pause for typed input may last before the run stops.</summary>
    private static readonly TimeSpan PausedFor = TimeSpan.FromSeconds(10);

    /// <summary>The element the game has given input focus to, or null: what ExileInput2 stops for.</summary>
    internal static Element FocusedInput(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.FocusedInputElement, null);

    /// <summary>Whether the reforging bench is open.</summary>
    private static bool ReforgingBenchOpen(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench?.IsVisible ?? false, false);

    /// <summary>The Expedition Tablets in the reforging bench, found by walking its elements.</summary>
    private static List<TabletOnScreen> BenchTablets(GameController gc)
    {
        var found = new List<TabletOnScreen>();
        var bench = Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench, null);

        if (bench != null && Safe.Read(bench, static b => b.IsVisible, false))
            Walk(found, [], bench, "reforging bench");

        return found;
    }

    /// <summary>The bench's "reforge" button: the element whose text it is, or null when it is not found.</summary>
    private static Element ReforgeButton(GameController gc)
    {
        var bench = Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench, null);
        var stack = new Stack<(Element Element, int Depth)>();
        var visited = 0;

        if (bench == null)
            return null;

        stack.Push((bench, 0));

        while (stack.Count > 0 && visited++ < MostElements)
        {
            var (element, depth) = stack.Pop();

            if (string.Equals(Safe.Read(element, static e => e.Text, "")?.Trim(), "reforge", StringComparison.OrdinalIgnoreCase))
                return element;

            if (depth >= DeepestElement)
                continue;

            foreach (var child in Safe.Read(element, static e => e.Children, null) ?? [])
                stack.Push((child, depth + 1));
        }

        return null;
    }

    /// <summary>The reward valuation as last drawn with, for prices read outside a draw. See Draw.</summary>
    private static Valuation _valuation;

    /// <summary>
    /// Whether a tablet is junk to reforge or withdraw: rare, identified, at full uses, not marked for an Exalt (see
    /// ActionOf - a suffix open, or enough searched modifiers), and either under Minimum weight to keep or worth at
    /// most under Reforge under by MostChaosOf.
    /// </summary>
    private static bool IsJunk(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        _junkReasonSettings = settings;
        var judged = Judgements.GetOrCreateValue(tablet);

        return judged.Junk ??= IsJunkUnjudged(gc, tablet, settings);
    }

    /// <summary>IsJunk worked out, for the first ask of each read's record. See TabletJudgement.</summary>
    private static bool IsJunkUnjudged(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings) =>
        tablet.Rarity == ItemRarity.Rare && tablet.Identified && tablet.Uses == FullUses && FullyRead(tablet) &&
        ActionOf(tablet, settings) != TabletAction.Exalt &&
        (settings.WeightOf(tablet.Modifiers) < settings.Prices.MinimumWeightToKeep.Value ||
         MostChaosOf(gc, tablet, settings) is { } most && most < settings.Prices.ReforgeUnderChaos.Value);

    /// <summary>
    /// Why a tablet is junk, for the run's log: its weight when under Minimum weight to keep, else its most worth in
    /// chaos against Reforge under, with its modifiers as read. A 6c tablet was reforged with nothing in the
    /// log to say which it had read as (2026-10-10).
    /// </summary>
    private static string JunkReasonOf(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var weight = settings.WeightOf(tablet.Modifiers);
        var read = string.Join("+", tablet.Modifiers.Select(m => m.Id.Length > 0 ? $"{m.Id}[{string.Join("/", m.Values)}]" : "(no id)"));

        return weight < settings.Prices.MinimumWeightToKeep.Value
            ? $"w{weight} under {settings.Prices.MinimumWeightToKeep.Value} - {read}"
            : $"w{weight}, worth at most {(MostChaosOf(gc, tablet, settings) is { } most ? $"{most:0.##}c" : "unknown")} against " +
              $"{settings.Prices.ReforgeUnderChaos.Value:0.##}c - {read}";
    }

    /// <summary>The settings the last junk judgement was made with, for the withdraw's log line. See MoveNext.</summary>
    private static TabletRerollingSettings _junkReasonSettings;

    /// <summary>
    /// Whether every modifier on a tablet has been read: an id and its values. One read without them weighs nothing, so a
    /// tablet worth 6c read under the minimum weight and was withdrawn as junk, then read whole in the inventory and left
    /// out of the bench (2026-10-10, inferred: the stash's reading was not caught). Not junk until it is.
    /// </summary>
    private static bool FullyRead(TabletOnScreen tablet) =>
        tablet.Modifiers.Length > 0 && tablet.Modifiers.All(m => !string.IsNullOrWhiteSpace(m.Id) && m.Values.Length > 0);

    /// <summary>
    /// The most a tablet is worth in chaos by the answers so far: its own search's price, else the lowest ceiling from
    /// a search at higher rolls with as many listings as Price from takes. Null with neither.
    /// </summary>
    private static double? MostChaosOf(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (_valuation == null || SearchOfTablet(gc, tablet, settings, out _) is not { } search)
            return null;

        // Null without a chaos rate, unless the price is nought: unknown rather than read as chaos. See TabletChaosOf.
        if (TabletPricing.Cached(search.Key) is { Error.Length: 0 } own)
        {
            var price = ExaltsForReforging(own, settings);

            return double.IsNaN(price) ? null : TabletChaosOf(price, _valuation);
        }

        var ceilings = TabletPricing.RelatedAnswers(search.Key)
            .Where(x => x.Tighter && x.Answer.Error.Length == 0 && x.Answer.Listings.Count >= settings.ListingsWanted)
            .Select(x => ExaltsForReforging(x.Answer, settings))
            .Where(x => !double.IsNaN(x))
            .ToList();

        return ceilings.Count > 0 ? TabletChaosOf(ceilings.Min(), _valuation) : null;
    }

    /// <summary>
    /// An answer's price in exalts for deciding junk: as drawn, but nought when it has listings and every one is in a
    /// currency the valuation does not convert - alchemy, regal and the like, which tablets are listed in when they are
    /// worth less than a chaos - and has as many
    /// as a price takes. NaN otherwise with no price. See ListedOnlyUnconverted.
    /// </summary>
    private static double ExaltsForReforging(TabletPricing.Answer answer, TabletRerollingSettings settings)
    {
        var price = PriceOfAnswer(answer, settings, _valuation);

        // Nought only where every listing is in a currency the valuation never prices - alchemy, regal. A chaos or divine
        // listing is NaN only for want of a rate: with NinjaPricer failing to fetch, a tablet listed at 6 chaos was taken
        // as worth nought and reforged (2026-10-09 23:13 to 23:45, every dump without rates). Unknown, then, not junk.
        return double.IsNaN(price) && ListedOnlyUnconverted(answer, settings, _valuation) ? 0d : price;
    }

    private static long AddressOf(TabletOnScreen tablet) => Safe.Read(tablet.Entity, static e => e.Address, 0L);

    /// <summary>
    /// What a run does: reforge junk tablets at the bench, withdraw junk ones from the stash tab to the inventory,
    /// deposit the inventory's other tablets back into the stash tab, or all of it in a loop. They share every step but
    /// the choosing. See StepReforging.
    /// </summary>
    private enum TabletRun
    {
        /// <summary>Reforging junk tablets at the open bench. See ReforgeNext.</summary>
        Reforge,

        /// <summary>Currency from the inventory onto every tablet that needs it. See StepCrafting.</summary>
        Craft,

        /// <summary>Withdraw, reforge, identify and deposit, round and round. See StepFullAuto.</summary>
        FullAuto,

        /// <summary>The fragment tab's Expedition tablets brought to the front, after a craft and reforge run. See StepTidy.</summary>
        Tidy,

        /// <summary>The most valuable tablets withdrawn, most valuable first. See StepWithdrawValuable.</summary>
        WithdrawValuable,

        /// <summary>Every Expedition tablet in the inventory deposited. See StepDepositAll.</summary>
        Deposit,
    }

    /// <summary>The run going, or the last one.</summary>
    private static TabletRun _run;

    /// <summary>Tablets a withdraw or deposit has moved so far.</summary>
    private static int _moved;

    /// <summary>
    /// Whether Automation > Enable automation is on, and whether the section's Enable tablet automation is, as at the
    /// last draw. Every run needs both; with either off no action zone is drawn. See TabletActionZones.
    /// </summary>
    private static bool _automationOn;

    private static bool _tabletAutomation;

    /// <summary>The stash tab's tablets as last read, those a withdraw takes from.</summary>
    private static List<TabletOnScreen> StashTablets() => Seen.Where(t => t.Where is "stash" or "stash tab elements").ToList();

    /// <summary>The inventory's tablets as last read, those a deposit takes from.</summary>
    private static List<TabletOnScreen> InventoryTablets() => Seen.Where(t => t.Where == "inventory").ToList();

    /// <summary>
    /// The inventory's tablets a deposit moves: at full uses and not junk. A used tablet is never moved: players swap
    /// tablets in and out of the inventory between their own maps.
    /// </summary>
    private static List<TabletOnScreen> Depositable(GameController gc, TabletRerollingSettings settings)
    {
        var depositable = InventoryTablets().Where(t => t.Uses == FullUses && !IsJunk(gc, t, settings)).ToList();

        // Kept only once this pass has reforged: one still wanting a currency as the pass starts is one the crafting pass
        // could not do, and keeping it would only send the run back to crafting.
        if (!_craftAndReforge || _reforged == 0)
            return depositable;

        // **In a craft and reforge run, a tablet wanting a currency stays**, up to MostKeptForCrafting, top left first:
        // the next crafting pass works on it in the inventory, and one it turns to junk is there to reforge. Deposited,
        // it was crafted in the stash and withdrawn again to reforge (2026-10-10). The rest go back, to leave the
        // inventory room for junk. See PutAway.
        var kept = depositable
            .Where(t => CraftingActions.Contains(ActionOf(t, settings)))
            .OrderBy(t => t.Rect.X).ThenBy(t => t.Rect.Y)
            .Take(MostKeptForCrafting)
            .ToHashSet();

        return depositable.Where(t => !kept.Contains(t)).ToList();
    }

    /// <summary>The most tablets a craft and reforge run keeps in the inventory to craft there: half of its 60 cells.</summary>
    private const int MostKeptForCrafting = 30;

    /// <summary>Text under and right of the pointer, kept inside the window: placed and backed as CursorWarning's words.</summary>
    private static void DrawUnderCursor(Graphics graphics, GameController gc, string text, Color colour)
    {
        var cursor = Safe.Read(() => new Vector2(gc.IngameState.MousePosX, gc.IngameState.MousePosY), Vector2.Zero);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (cursor == Vector2.Zero)
            return;

        var size = graphics.MeasureText(text);
        var at = new Vector2(
            Math.Clamp(cursor.X + 16f, 0f, Math.Max(0f, window.Width - size.X - 4f)),
            Math.Clamp(cursor.Y + 44f, 0f, Math.Max(0f, window.Height - size.Y - 4f)));

        graphics.DrawTextWithBackground(text, at, colour, Color.FromArgb(200, Color.Black));
    }

    /// <summary>
    /// The action key with the bench open, or over one of the stash's action zones: starts a run of that kind, or
    /// stops the one going. Nothing starts while Enable automation is off.
    /// </summary>
    private static void ToggleReforging(GameController gc, TabletRerollingSettings settings, TabletRun run = TabletRun.Reforge)
    {
        if (run is TabletRun.Craft or TabletRun.Reforge or TabletRun.FullAuto)
            CraftingAskedInArea = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);

        if (_reforgingHasCursor)
        {
            StopReforging("stopped by the key");
            return;
        }

        if (!_automationOn)
        {
            _run = run;
            Say("automation is off (Automation > Enable automation)");
            return;
        }

        if (Input is not { Available: true })
        {
            Say("needs ExileInput2, which is not answering");
            return;
        }

        Input.Release();

        if (!Input.Take())
        {
            Say("the cursor is in use by another plugin");
            return;
        }

        _reforgingHasCursor = true;
        _run = run;
        _craftAndReforge = false;
        _moved = 0;
        _reforgingPausedSince = null;
        _reforgingStep = ReforgingStep.Idle;
        _reforgedPendingSince = null;
        _reforgedTakes = 0;
        _reforged = 0;
        _putDowns = 0;
        Unloadable.Clear();
        ReforgingLog.Clear();
        MovesPending.Clear();
        MovesRetried.Clear();
        BenchLoadsPending.Clear();
        BenchLoadsRetried.Clear();
        RollsBegin(gc);
        _phase = FullAutoPhase.Navigate;
        _navigateStep = 0;
        _depositNext = false;
        SubTabsDone.Clear();
        _stashEmptied = false;
        _roomForBench = 0;
        Progressed();
        StartCrafting(gc);
        _rounds = 0;
        ReforgingSent.Clear();
        Say("starting");
    }

    /// <summary>
    /// Ends a run that did what it was asked, with nothing drawn: the tablets moved, crafted or withdrawn are the answer
    /// on screen. Its word is still kept for the dump. A run that stops on a problem, or finds nothing to do, uses
    /// StopReforging, which says why.
    /// </summary>
    private static void FinishQuietly(string why)
    {
        StopReforging(why);
        _reforgingSaidAt = DateTime.MinValue;
    }

    /// <summary>Whether the run that last stopped was a craft and reforge run, for its last word. See DrawReforgingStatus.</summary>
    private static bool _endedCraftAndReforge;

    private static void Say(string what)
    {
        _reforgingSaid = what;
        _reforgingSaidAt = DateTime.UtcNow;
    }

    private static void StopReforging(string why)
    {
        _endedCraftAndReforge = _craftAndReforge;
        Say(_run switch
        {
            _ when _craftAndReforge => why,
            TabletRun.Craft => $"{why}{(_crafted > 0 ? $" ({_crafted} currency used)" : "")}",
            _ => $"{why}{(_reforged > 0 ? $" ({_reforged} reforged)" : "")}",
        });
        _reforgingStep = ReforgingStep.Idle;
        _reforgingDone = null;
        _worldTarget = null;
        _worldClick = false;
        _shiftHeld = false;

        if (_reforgingHasCursor)
        {
            // Back to ExileInput2's own shortest move for the placement that shares this caller name. See
            // UseShortestMove.
            if (_shortestMoveApplied is not -1)
                Input?.SetShortestMove(-1);

            if (_toleranceApplied != OtherTolerancePx)
                Input?.SetTolerance(OtherTolerancePx);

            Input?.Release();
        }

        if (_focusAllowedUnder != 0L)
            Input?.AllowFocusUnder([]);

        _focusAllowedUnder = 0L;
        _shortestMoveApplied = null;
        _toleranceApplied = 0;

        // A click waiting to be tried once more is not tried by the next run.
        _retryAt = null;
        _lastSend = null;
        _worldRetried = false;
        _clickCell = null;
        _previousSent = null;
        _craftAndReforge = false;
        _reforgingHasCursor = false;
    }

    /// <summary>
    /// Tells ExileInput2 that focus inside the reforging bench, while it is open, is not typed input. The game reports the
    /// box around the reforge button as taking typed input after each reforge (element at 724,1127 225x52, text "", with
    /// the button at 790,1139 inside it; dump 2026-10-12 12:34:01), and ExileInput2 stopped the run for it every cycle:
    /// the run paused, the plan drawn faded out, and it carried on a moment later. Told only when the bench changes.
    /// </summary>
    private static void AllowFocusInBench(GameController gc)
    {
        var bench = ReforgingBenchOpen(gc) ? Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench?.Address ?? 0L, 0L) : 0L;

        if (bench == _focusAllowedUnder)
            return;

        if (Input?.AllowFocusUnder(bench != 0L ? [bench] : []) == true)
            _focusAllowedUnder = bench;
    }

    /// <summary>Whether the element the game has given input focus lies inside the open reforging bench. For the log.</summary>
    private static bool FocusUnderBench(GameController gc)
    {
        var bench = Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench?.Address ?? 0L, 0L);
        var at = FocusedInput(gc);

        for (var depth = 0; bench != 0L && at != null && depth < 64; depth++)
        {
            if (Safe.Read(at, static e => e.Address, 0L) == bench)
                return true;

            at = Safe.Read(at, static e => e.Parent, null);
        }

        return false;
    }

    /// <summary>The bench ExileInput2 was last told focus inside is not typed input, or 0. See AllowFocusInBench.</summary>
    private static long _focusAllowedUnder;

    /// <summary>Steps the run once. Called every draw; returns at once when no run is going.</summary>
    private static void StepReforging(GameController gc, TabletRerollingSettings settings)
    {
        if (!_reforgingHasCursor)
        {
            _lastStepAt = default;
            return;
        }

        // A long gap between steps during a run, for the log: a stall the player may see as the cursor jumping.
        var stepAt = DateTime.UtcNow;

        if (_lastStepAt != default && stepAt - _lastStepAt >= LongStep)
            LogReforging($"a frame took {(stepAt - _lastStepAt).TotalMilliseconds:0}ms, after {_previousSent}");

        _lastStepAt = stepAt;

        if (!_automationOn)
        {
            StopReforging("automation was turned off");
            return;
        }

        AllowFocusInBench(gc);

        // A full-auto run opens and closes these itself, and so does the crafting run's identification step, on its way to
        // Doryani and back - it checks the stash itself where it needs it. Every other run needs its own panel open
        // throughout. Without the second, a crafting run stopped as soon as it closed the stash for Doryani.
        if (_run != TabletRun.FullAuto && !(_run == TabletRun.Craft && CraftIdentifying) &&
            (_run == TabletRun.Reforge ? !ReforgingBenchOpen(gc) : _stashRect.Width <= 0f))
        {
            StopReforging(_run == TabletRun.Reforge ? "the reforging bench closed" : "the stash closed");
            return;
        }

        // **The game taking typed input pauses the run rather than ending it.** ExileInput2 stops on any
        // IngameState.FocusedInputElement, and one came up right after a reforge with no chat open (dump of 2026-10-09
        // 15:19:24, eight reforges in). The cursor is handed back until the focus clears, then taken again, and the run
        // carries on from what the screen shows.
        if (_reforgingPausedSince is { } paused)
        {
            if (FocusedInput(gc) == null && Input.Take())
            {
                _reforgingPausedSince = null;
                ReforgingSent.Clear();
                Say("carrying on");
            }
            else if (DateTime.UtcNow - paused > PausedFor)
                StopReforging("the game kept taking typed input");

            return;
        }

        if (Input.Stopped() is { Length: > 0 } stopped)
        {
            if (stopped == "GameIsTakingInput")
            {
                // Which element, for the dump: one came up for a moment around the bench, with no chat open, and paused
                // the run (2026-10-12 12:27:11). See ExileInput2's FocusedSaid.
                LogReforging($"paused: the game is taking typed input, {(FocusUnderBench(gc) ? "inside" : "not inside")} the " +
                             $"reforging bench; ExileInput2: {Input.StopDetail()}");
                Input.Release();
                _reforgingPausedSince = DateTime.UtcNow;
                _reforgingStep = ReforgingStep.Idle;
                _worldTarget = null;
                Say("paused: the game is taking typed input");
                return;
            }

            // The move in flight when ExileInput2 stopped, for the dump: a MoveNeverLanded stopped a reforge with nothing
            // to say which move or where the cursor was (2026-10-10 18:21:50).
            var cursor = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

            LogReforging($"ExileInput2 stopped ({stopped}) moving to {_reforgingDoing} in {RectSaid(_sentTarget)}, " +
                         $"{(DateTime.UtcNow - _sentAt).TotalMilliseconds:0}ms after it was sent, the cursor read at ({cursor.X:0},{cursor.Y:0})" +
                         (Input.StopDetail() is { Length: > 0 } detail ? $"; ExileInput2: {detail}" : ""));

            // **A move that never landed is tried once more, as a failed click is.** Releasing clears ExileInput2's stop and
            // lets go of every key it held for this run, shift included, so a currency run picks its orb up again. A
            // player taking the mouse, or anything else, still stops the run. See RetryOrStop.
            // Not a hideout object's move, which Send did not make: _lastSend would be an older click.
            if (stopped == "MoveNeverLanded" && _lastSend != null && !_sendRetried && !_worldClick)
            {
                Input.Release();
                _shiftHeld = false;
                _orbsLeft = null;
                RetryOrStop($"the cursor did not land on {_reforgingDoing}");
                return;
            }

            StopReforging(stopped switch
            {
                "PlayerMovedTheMouse" => "you moved the mouse",
                "TakenOver" => "an instant loot pickup took the cursor - press again",
                "PlayerPressedAKey" => "you pressed a key",
                _ => $"stopped: {stopped}",
            });
            return;
        }

        // **The claim kept alive every step.** ExileInput2 takes the cursor off a holder that calls nothing that
        // refreshes it for its claim expiry - five seconds by default - and Stopped does not refresh it. A pause or a slow
        // wait calls nothing else, and a lapsed claim refuses the next move.
        // Take is safe every tick. Refused with a stop recorded, the stop is handled next step, typed input as a pause.
        if (!Input.Take())
        {
            if (Input.Stopped() is not { Length: > 0 })
                StopReforging("another plugin took the cursor");

            return;
        }

        var now = DateTime.UtcNow;

        // A failed click waiting its turn to be tried once more. See RetryOrStop.
        if (_retryAt is { } retryAt)
        {
            if (now < retryAt)
                return;

            _retryAt = null;

            if (_lastSend is { } again)
            {
                _resending = true;

                try
                {
                    Send(again.Target, again.Ctrl, again.Doing, again.Done, again.Pointed ? ItemClicks.AnywhereIn(again.Target) : null, again.Timeout, again.Right,
                        again.BeforeClick);
                }
                finally
                {
                    _resending = false;
                }
            }

            return;
        }

        switch (_reforgingStep)
        {
            case ReforgingStep.Moving:
                if (now > _reforgingDeadline)
                {
                    RetryOrStop($"the cursor did not reach {_reforgingDoing}");
                    return;
                }

                if (!Input.Arrived())
                    return;

                // **A model is clicked only once the game says it is the one under the cursor.** Targetable.isTargeted
                // read true on Doryani and on the reforging bench while hovered, and false on every other object (dumps
                // of 2026-10-09 16:25:39 and 16:25:43). Not yet: wait a moment, then aim at another point of the model.
                if (_worldTarget != null && !IsTargeted(_worldTarget))
                {
                    _arrivedAt ??= now;

                    if (now - _arrivedAt < HoverWait)
                        return;

                    PlannedAhead.Clear();

                    if (++_hoverTries >= HoverTries || WorldPointOf(gc, _worldTarget) is not { } again || !Input.MoveTo(again, ctrl: _reforgingCtrl))
                    {
                        // **A panel may be over the model after all: Space once, then the same click again.** A second
                        // failure stops the run.
                        if ((_run == TabletRun.FullAuto || _run == TabletRun.Craft && CraftIdentifying) && !_spacedForTarget && AnyPanelOpen(gc) && Input.Tap(System.Windows.Forms.Keys.Space))
                        {
                            var spaced = _worldTarget;

                            _spacedForTarget = true;
                            _hoverTries = 0;
                            _arrivedAt = now;
                            Say($"closing the panels over {_reforgingDoing}");

                            if (WorldPointOf(gc, spaced) is { } after && Input.MoveTo(after, ctrl: _reforgingCtrl))
                            {
                                _reforgingDeadline = now + StepTimeout;
                                return;
                            }
                        }

                        StopReforging($"could not target {_reforgingDoing}");
                        return;
                    }

                    _arrivedAt = null;
                    _reforgingDeadline = now + StepTimeout;
                    return;
                }

                // **Never a click with an item held on the cursor: it is put down first.** Every click of a run is a
                // ctrl+click, a currency use or a button, none of which picks anything up; with a tablet held, each
                // ctrl+click swapped it with the tablet clicked, and the junk withdraw went round the same cells eight
                // times (dump 2026-10-13 21:36:13). The click it interrupts is dropped and the run chooses again; up to
                // PutDownsPerRun times a run, then it stops.
                if (_reforgingDoing != HeldItemPutDown && Tablets.ItemHeldOnCursor(gc) is { } held)
                {
                    var heldName = Safe.Read(held, static e => e.GetComponent<ExileCore2.PoEMemory.Components.Base>()?.Name, null) ?? "an item";
                    var wide = Math.Max(1, Safe.Read(held, static e => e.GetComponent<ExileCore2.PoEMemory.Components.Base>()?.ItemCellsSizeX ?? 1, 1));
                    var high = Math.Max(1, Safe.Read(held, static e => e.GetComponent<ExileCore2.PoEMemory.Components.Base>()?.ItemCellsSizeY ?? 1, 1));

                    if (_putDowns >= PutDownsPerRun || InventorySpace.FreePlaceOnScreen(gc, wide, high) is not { } place)
                    {
                        StopReforging($"{heldName} is held on the cursor and could not be put down - put it down and start again");
                        return;
                    }

                    _putDowns++;
                    LogReforging($"{heldName} held on the cursor before {_reforgingDoing}: put down in the inventory at ({place.X:0},{place.Y:0})");
                    Send(place, false, HeldItemPutDown, () => Tablets.ItemHeldOnCursor(gc) == null, ItemClicks.AnywhereIn(place));
                    return;
                }

                // A refusal stops the run; an empty one lets this click go and the run choose again. See the crafting run's
                // click on a tablet.
                if (_beforeClick?.Invoke() is { } refused)
                {
                    if (refused.Length == 0)
                    {
                        _reforgingStep = ReforgingStep.Idle;
                        _worldTarget = null;
                        return;
                    }

                    StopReforging(refused);
                    return;
                }

                // **A tablet is clicked only with the cursor in its cell.** Taken as arrived within ExileInput2's
                // tolerance of a point near the cell's edge, the cursor sat over the tablet before it, which an Alchemy
                // had just made rare, and the next Alchemy went on that: "Failed to apply item: Target is not Normal or
                // Magic" (2026-10-09 23:36). Moved again once, then the run stops rather than click a neighbour.
                if (_clickCell is { } cell && !_sentPlanned && !ItemClicks.CursorInside(gc, cell))
                {
                    PlannedAhead.Clear();

                    if (_reaimed || !Input.MoveTo(ItemClicks.AnywhereIn(cell), ctrl: _reforgingCtrl))
                    {
                        StopReforging($"the cursor did not reach {_reforgingDoing}'s cell");
                        return;
                    }

                    _reaimed = true;
                    _reforgingDeadline = now + StepTimeout;
                    return;
                }

                if (!Input.Click(ctrl: _reforgingCtrl, right: _reforgingRight))
                {
                    RetryOrStop($"the click on {_reforgingDoing} did not go");
                    return;
                }

                _clickedAt = now;
                _clickedPoint = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

                // The still-timer starts at the click: before the walk begins, standing still says nothing.
                if (_worldClick)
                {
                    _playerAt = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
                    _playerMovedAt = now;
                }

                _reforgingStep = ReforgingStep.Clicked;
                _reforgingDeadline = now + _clickTimeout;
                return;

            case ReforgingStep.Clicked:
                // **After a click on a model, nothing is believed until the character has stopped walking.** The bench
                // read open the moment its click went out, before its panel and the inventory beside it were drawn, and the
                // run took an empty inventory for no junk and walked on to the stash (2026-10-09). So: SettleAfterWalk of
                // standing still before what was asked for counts, and SettledFor from the stop for it to show.
                if (_worldClick)
                {
                    var still = now - PlayerStillSince(gc);

                    if (still < SettleAfterWalk)
                        return;

                    if (_reforgingDone?.Invoke() == true)
                    {
                        _worldClick = false;
                        _reforgingStep = ReforgingStep.Idle;
                        _worldTarget = null;
                        break;
                    }

                    if (still <= SettledFor && now <= _reforgingDeadline)
                        return;

                    _worldClick = false;
                    _reforgingStep = ReforgingStep.Idle;
                    _worldTarget = null;

                    // A hideout object is clicked again by its phase, which asks for it again; the click guard stops a
                    // third click with nothing done between. See SendWorld.
                    if (!_worldRetried)
                    {
                        _worldRetried = true;
                        LogReforging($"nothing came of {_reforgingDoing}: trying once more");
                        return;
                    }

                    StopReforging($"nothing came of {_reforgingDoing}, after one retry");
                    return;
                }

                if (_reforgingDone?.Invoke() == true)
                {
                    // Where each move's time went, for the dump: the cursor's travel to the click, then the click to
                    // the screen read showing it, which waits for the next read of the tablets.
                    LogReforging($"{_reforgingDoing}: {(_clickedAt - _sentAt).TotalMilliseconds:0}ms to the click" +
                                 (_sentMoveTimes is { } times ? ItemClicks.FlourishSaid(times) : "") + " at " +
                                 $"({_clickedPoint.X:0},{_clickedPoint.Y:0}) in {RectSaid(_sentTarget)}, " +
                                 $"{(now - _clickedAt).TotalMilliseconds:0}ms to show it");

                    if (_reforgingDoing == ReforgedTablet)
                    {
                        _reforgedPendingSince = null;
                        RollsTakenOut(gc);
                    }

                    _reforgingStep = ReforgingStep.Idle;
                    _worldTarget = null;
                    break;
                }

                if (now <= _reforgingDeadline)
                    return;

                _reforgingStep = ReforgingStep.Idle;
                _worldTarget = null;

                // The reforged tablet may not be ready to leave yet: choose again, which tries it again.
                if (_reforgingDoing == ReforgedTablet && _reforgedTakes < ReforgedTakeTries)
                    break;

                // **A junk tablet that would not go into the bench is set aside**, and the run chooses again - which takes
                // any loaded ones back out when fewer than three are left. At most MostUnloadable, then the run stops.
                if (_reforgingDoing == JunkTablet && (_run == TabletRun.Reforge || _run == TabletRun.FullAuto && _phase == FullAutoPhase.Reforge) &&
                    Unloadable.Count < MostUnloadable)
                {
                    Unloadable.Add(_loadingAddress);
                    LogReforging("a junk tablet did not go into the bench: set aside");
                    break;
                }

                // In a full-auto run a full inventory ends the withdraw, and a full sub-tab moves the deposit on to the next.
                if (_run == TabletRun.FullAuto && _phase == FullAutoPhase.Withdraw && _reforgingDoing == JunkTablet)
                {
                    _stashEmptied = false;
                    _closeStashThen = FullAutoPhase.OpenBench;
                    _phase = FullAutoPhase.CloseStash;
                    _spaceTries = 0;
                    break;
                }

                if (_run == TabletRun.FullAuto && _phase == FullAutoPhase.Deposit && _reforgingDoing == DepositedTablet &&
                    NextSubTab(gc, SubTabPurpose.Room) is { } next)
                {
                    ClickSubTab(gc, next);
                    return;
                }

                // Leftovers at the end with no room left in any sub-tab: stop as the run would have, saying so.
                if (_run == TabletRun.FullAuto && _phase == FullAutoPhase.DepositLeftovers)
                {
                    StopReforging($"{_finishSaid} - the leftovers did not fit in the stash");
                    return;
                }

                if (_run == TabletRun.FullAuto && _reforgingDoing == DepositedTablet)
                {
                    StopReforging("a tablet did not leave the inventory: the stash tab may be full");
                    return;
                }

                RetryOrStop($"nothing came of {_reforgingDoing}");
                return;
        }

        switch (_run)
        {
            case TabletRun.Craft:
                StepCrafting(gc, settings);
                return;

            case TabletRun.Reforge:
                if (!ReforgeNext(gc, settings, out var done))
                    StopReforging(done);
                return;

            case TabletRun.FullAuto:
                StepFullAuto(gc, settings);
                return;

            case TabletRun.Tidy:
                StepTidy(gc, settings);
                return;

            case TabletRun.WithdrawValuable:
                StepWithdrawValuable(gc, settings);
                return;

            case TabletRun.Deposit:
                StepDepositAll(gc);
                return;
        }
    }

    /// <summary>
    /// The next reforging step, chosen from what the bench and the inventory show now: load three junk tablets while the
    /// last reforge finishes, take the reforged tablet out, then reforge. False, with why, once fewer than three junk
    /// tablets are left and the last reforged tablet is out; true while a step goes or the run was stopped.
    /// </summary>
    private static bool ReforgeNext(GameController gc, TabletRerollingSettings settings, out string done)
    {
        done = "";

        var bench = BenchTablets(gc);

        // **A rare tablet with no modifiers is not read yet**, whatever its identified flag says: a real one has at least
        // one. The reforged tablet read so in the moment it appeared - identified by default, weight 0, and with a
        // suffix open an Exalt tablet, so not junk - and the check below stopped the run on it (2026-10-09 18:57). It is
        // taken for the reforged tablet, as an unidentified one is.
        bool Read(TabletOnScreen t) => t.Identified && (t.Rarity == ItemRarity.Normal || t.Modifiers.Length > 0);

        // **A tablet not yet fully read is not judged.** It is not junk until it is (see FullyRead), and taken here for one
        // put in by hand it stopped a run on a tablet the run had just loaded itself, weight 53 against a minimum of 101,
        // read as junk in the dump a moment later (a tester's dump, 2026-10-10 10:58). Waited on for up to 2 seconds.
        if (bench.Any(t => Read(t) && !FullyRead(t)))
        {
            _benchHalfReadSince ??= DateTime.UtcNow;

            if (DateTime.UtcNow - _benchHalfReadSince < TimeSpan.FromSeconds(2))
            {
                Say("waiting for the bench");
                return true;
            }
        }
        else
            _benchHalfReadSince = null;

        // Anything read in the bench that is not junk was put there by hand: leave it, and stop.
        if (bench.FirstOrDefault(t => Read(t) && !IsJunk(gc, t, settings)) is { } kept)
        {
            StopReforging($"the bench holds a tablet that is not junk (weight {settings.WeightOf(kept.Modifiers)})");
            return true;
        }

        // **The reforged tablet is the one in the result slot**, once that is known: a load not yet read in a slot below
        // must not be taken for it and taken back out, which matters now the reforged tablet is taken out while loads are
        // still landing. Before the slot is known, any tablet not read, as before. See InResultSlot.
        var made = _benchResultRect.Width > 0f
            ? bench.FirstOrDefault(t => !Read(t) && InResultSlot(t))
            : bench.FirstOrDefault(t => !Read(t));
        var loaded = bench.Where(Read).ToList();

        // When each tablet in the bench was first seen there. See the wait on a load landing below.
        foreach (var tablet in bench)
            BenchFirstSeen.TryAdd(AddressOf(tablet), DateTime.UtcNow);

        if (BenchFirstSeen.Count > BenchFirstSeenKept)
            BenchFirstSeen.Clear();

        if (made != null)
        {
            if (made.Rect != _benchResultRect)
                RememberBenchResult(gc, made.Rect);

            _benchResultRect = made.Rect;
        }
        else if (_benchResultRect.Width <= 0f && RememberedBenchResult(gc) is { Width: > 0f } remembered)
            _benchResultRect = remembered;

        // Loads clicked and not yet read as gone from the inventory: settled first, as the moves between stash and
        // inventory are. See BenchLoadsPending.
        if (!SettleBenchLoads())
            return true;

        var inFlight = BenchLoadsPending.Count;

        // **A reforged tablet outside the slot remembered** - the slot moved since it was saved - is taken once nothing is
        // loading and the last load landed a second ago, so no load can be mistaken for it, and the slot learnt again.
        if (made == null && inFlight == 0 && _benchResultRect.Width > 0f && DateTime.UtcNow - _benchLoadLandedAt > TimeSpan.FromSeconds(1) &&
            bench.FirstOrDefault(t => !Read(t) && !InResultSlot(t)) is { } elsewhere)
        {
            made = elsewhere;
            RememberBenchResult(gc, made.Rect);
            _benchResultRect = made.Rect;
        }
        var junk = InventoryTablets().Where(t => IsJunk(gc, t, settings) && !ReforgingSent.Contains(AddressOf(t)) &&
                                                !Unloadable.Contains(AddressOf(t))).ToList();

        // Load the next three while the last reforge finishes - but only when three can be loaded, so none is left
        // half way. A load clicked and not yet seen in the bench counts as loaded.
        if (loaded.Count + inFlight < 3 && loaded.Count + inFlight + junk.Count >= 3)
        {
            var count = loaded.Count + inFlight;

            LogReforging($"load: {loaded.Count} in the bench, {inFlight} on the way, {(made != null ? "a reforged tablet there, " : "")}" +
                         $"{junk.Count} junk counted - " +
                         string.Join(", ", junk.Select(t => $"({t.Rect.X:0},{t.Rect.Y:0}) {JunkReasonOf(gc, t, settings)}")));

            // Each of the three by distance from the cursor, the first clicked anywhere in its cell and the other two just
            // inside the edge facing it, as a hand would reach for them: a load starts where the cursor is, not from the
            // top-left or bottom-left as a batch's first does. See ItemClicks.NextItemOf.
            var mouse = ImGuiNET.ImGui.GetMousePos();
            // Each later three planned from the reforge button, where the cursor is when they are loaded, not from the last
            // of the three before. See BatchNext's groupSize.
            var buttonCentre = _reforgeButtonRect.Width > 0f
                ? new Vector2(_reforgeButtonRect.X + _reforgeButtonRect.Width / 2f, _reforgeButtonRect.Y + _reforgeButtonRect.Height / 2f)
                : (Vector2?)null;
            var (next, point) = BatchNext(BenchLoadBatch, junk, AddressOf, static t => t.Rect, mouse, false, count == 0, ctrl: true,
                restLimit: 2 - count, groupSize: 3, firstGroup: 3 - count, groupStart: buttonCentre);

            // **The rest of the cycle planned with the loads that make it three, and the next cycle's loads after it**: the
            // reforged tablet out when a reforge is going, the reforge button, then the next three. Each part was its own
            // queue, planned only when its turn came, and the cursor stopped between them (2026-10-11). The clicks are
            // still sent in turn, each when the bench is ready for it; what is already planned is added to rather than
            // planned again (see PlanAhead), and a send that does not match it, as when the bench moved, plans afresh.
            if (count + 1 + BatchRest.Count == 3)
                BatchRest.AddRange(BenchCycleAfterLoads(gc, made != null || _reforgedPendingSince != null, BatchRest.Count));

            var loading = AddressOf(next);

            ReforgingSent.Add(loading);
            _loadingAddress = loading;
            BenchLoadsPending[loading] = DateTime.UtcNow + StepTimeout;
            Progressed();

            // Done at the click, as a move between stash and inventory is: the cursor goes straight on to the next.
            Send(next.Rect, true, JunkTablet, () =>
                {
                    BenchLoadsPending[loading] = _clickedAt;
                    _benchLoadClickedAt = _clickedAt;
                    return true;
                },
                point);
            return true;
        }

        // **While loads are on their way, only what does not turn on them**: the reforged tablet out once it is in its
        // slot, or waiting over the slot for it. Everything stopped here until all three read in the bench, so the third
        // load was followed by a sharp stop and then the move to the reforged tablet, where the plan had the cursor going
        // straight on. Unloading, calling it done and the reforge still wait for the loads, below.
        if (inFlight > 0 && made == null && _reforgedPendingSince == null)
        {
            Say("waiting for the bench");
            return true;
        }

        // **A tablet just loaded may read in the bench before its modifiers do**, and would be taken for the reforged one
        // and taken back out. With no reforge pending, an unread tablet within a second of a load landing is waited on -
        // only one first seen in the bench since that load was clicked: a tablet there from before, as one left by an
        // interrupted run is, was waited on for the second as well (2026-10-12 12:27:09). Whether the bench's copy reads
        // late has not been seen either way; loads used to wait for it to read.
        if (made != null && _reforgedPendingSince == null && DateTime.UtcNow - _benchLoadLandedAt < TimeSpan.FromSeconds(1) &&
            BenchFirstSeen.TryGetValue(AddressOf(made), out var madeSeen) && madeSeen >= _benchLoadClickedAt)
        {
            Say("waiting for the bench");
            return true;
        }

        // Then the reforged tablet out, before the next reforge or at the end.
        if (made != null)
        {
            // **No room in the inventory for it**: a full-auto run goes to the stash for room and comes back. A bench left
            // holding a tablet and three loads, with the stash's junk withdrawn to a full inventory, could neither take the
            // tablet out nor load anything, and the run stuck (2026-10-12 12:21:14). See _roomForBench.
            if (_run == TabletRun.FullAuto && InventoryFreeCells(gc) == 0)
            {
                _roomForBench = 1;
                LogReforging("no room in the inventory for the tablet in the bench: to the stash to deposit one");
                done = "no room for the tablet in the bench";
                return false;
            }

            var address = AddressOf(made);

            _reforgedTakes++;

            // Then the reforge, and the next cycle's loads. See BenchCycleAfterLoads.
            if (loaded.Count == 3)
            {
                BatchRest.Clear();
                BatchRest.AddRange(BenchCycleAfterLoads(gc, false, 0));
            }

            // Where on it is ExileInput2's choice, as for the reforge button.
            Send(made.Rect, true, ReforgedTablet, () => BenchTablets(gc).All(t => AddressOf(t) != address));
            return true;
        }

        _reforgedTakes = 0;

        // The last reforge's tablet has not appeared yet: wait for it, so it is out before the next reforge or the end.
        if (_reforgedPendingSince is { } since)
        {
            if (DateTime.UtcNow - since > ReforgedWait)
                StopReforging("the reforged tablet did not appear");
            else
            {
                Say("waiting for the reforged tablet");

                // **Not idle while the bench works**: the cursor goes over where the tablet will appear and moves round
                // there, rather than resting on the reforge button until it does - ctrl down for the ctrl+click that
                // takes it out, rather than let go and pressed again. Once each reforge. See ExileInput2's Loiter.
                if (_loiteredFor != since && _benchResultRect.Width > 0f &&
                    Input.Loiter(_benchResultRect, LoiterShare, (int)ReforgedWait.TotalMilliseconds, [System.Windows.Forms.Keys.LControlKey]))
                    _loiteredFor = since;
            }

            return true;
        }

        // Unloading, calling it done and the reforge wait for every load to read in the bench. See above.
        if (inFlight > 0)
        {
            Say("waiting for the bench");
            return true;
        }

        // **Fewer than three left, but some already loaded: they come back out**, so the bench is left empty. A tablet that
        // would not load once left its partners stranded in the bench at the end of a run (2026-10-09 19:09).
        if (loaded.Count is > 0 and < 3 && loaded.Count + junk.Count < 3)
        {
            var back = loaded[0];
            var address = AddressOf(back);

            LogReforging($"unload: {loaded.Count} in the bench and {junk.Count} junk counted, fewer than three");
            Send(back.Rect, true, "a junk tablet back", () => BenchTablets(gc).All(t => AddressOf(t) != address), null);
            return true;
        }

        if (loaded.Count < 3)
        {
            done = loaded.Count + junk.Count == 0
                ? "done: no junk left"
                : $"done: {loaded.Count + junk.Count} junk tablet(s) left, fewer than three";
            return false;
        }

        if (ReforgeButton(gc) is not { } button)
        {
            if (NotFoundYet("the reforge button"))
                return true;

            StopReforging("the reforge button was not found");
            return true;
        }

        var inputs = loaded.Select(AddressOf).ToHashSet();

        // Done once the three are taken: the new tablet may show only after a moment, and is collected later.
        // ExileInput2 chooses where on the button, scattering each press about a spot of the button's own (MousePointer.PointIn).
        _reforgeButtonRect = Safe.Read(button, static x => x.GetClientRectCache, default);

        // Then the next cycle: its loads, the tablet this reforge makes, and the button again. See BenchCycleAfterLoads.
        BatchRest.Clear();

        if (NextBenchLoads(gc, 0) is { Count: 3 } coming)
        {
            BatchRest.AddRange(coming);

            if (_benchResultRect.Width > 0f)
            {
                BatchRest.Add((_benchResultRect, null, false, true));
                BatchRest.Add((_reforgeButtonRect, null, false, true));
            }
        }

        // **Ctrl+clicked, as every other click of the bench is**: the button takes ctrl+click as a click, so ctrl stays down
        // from the loads through the reforge, the wait over the reforged tablet's slot and the ctrl+click that takes it out,
        // rather than coming up for the button and going down again after it.
        Send(_reforgeButtonRect, true, "the reforge",
            () => BenchTablets(gc).All(t => !inputs.Contains(AddressOf(t))));
        _reforged++;
        RollsReforged(loaded);
        Progressed();
        _reforgedPendingSince = DateTime.UtcNow;
        ReforgingSent.Clear();
        return true;
    }

    /// <summary>The move to the click in flight: its times, and whether it swooped or overshot. See Send.</summary>
    private static (float WaitMs, float TravelMs, bool Swooped, float OvershotPx)? _sentMoveTimes;

    /// <summary>
    /// What a reforging cycle clicks after its three loads: the reforged tablet out, where one last appeared, when a
    /// reforge is going; the reforge button; then the next cycle's three loads, after skip loads of this one still to
    /// come. Stops short where it cannot say: a reforge going with no reforged tablet seen yet to say where it appears,
    /// and the loads after it, are left for later. The button is found on the bench before it is first clicked.
    /// </summary>
    private static List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)> BenchCycleAfterLoads(GameController gc, bool reforging,
        int skip)
    {
        var after = new List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)>();

        if (reforging)
        {
            if (_benchResultRect.Width <= 0f)
                return after;

            after.Add((_benchResultRect, null, false, true));
        }

        if (_reforgeButtonRect.Width <= 0f && ReforgeButton(gc) is { } button)
            _reforgeButtonRect = Safe.Read(button, static x => x.GetClientRectCache, default);

        if (_reforgeButtonRect.Width <= 0f)
            return after;

        after.Add((_reforgeButtonRect, null, false, true));

        if (NextBenchLoads(gc, skip) is { Count: 3 } coming)
            after.AddRange(coming);

        return after;
    }

    /// <summary>
    /// The next three loads in the order planned for the bench, after skip of them, each with its point; fewer when fewer
    /// are planned or still carried. See BatchNext.
    /// </summary>
    private static List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)> NextBenchLoads(GameController gc, int skip)
    {
        var loads = new List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)>();

        if (_batchOf != BenchLoadBatch)
            return loads;

        var carried = InventoryTablets().ToDictionary(AddressOf, t => t.Rect);

        foreach (var (address, point) in BatchOrder.Skip(skip).Take(3))
        {
            if (!carried.TryGetValue(address, out var rect))
                break;

            loads.Add((rect, point, false, true));
        }

        return loads;
    }

    /// <summary>The batch name the bench's loads are planned under. See BatchNext.</summary>
    private const string BenchLoadBatch = "bench load";

    /// <summary>Where the reforged tablet last appeared in the bench, and where the reforge button was last clicked. See the bench load.</summary>
    private static RectangleF _benchResultRect, _reforgeButtonRect;

    /// <summary>
    /// Where the reforged tablet appears, as last seen, kept on disk with the bench panel's rectangle then, so it is known
    /// from a run's first reforge rather than only once one has been seen since the plugin loaded: without it, a run of one
    /// reforge after a reload neither drew the tablet's click ahead nor loitered over it (2026-10-12 12:47:17). Set by
    /// the plugin from its config directory.
    /// </summary>
    internal static string BenchResultFile { get; set; } = "";

    /// <summary>The bench panel and the reforged tablet's place, both as last seen and saved. See BenchResultFile.</summary>
    private sealed class BenchResultRecord
    {
        public float[] Bench { get; set; } = [];
        public float[] Result { get; set; } = [];
    }

    /// <summary>The bench panel's rectangle now, or empty when it is not open.</summary>
    private static RectangleF BenchPanelRect(GameController gc) =>
        ReforgingBenchOpen(gc) ? Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench.GetClientRectCache, default) : default;

    /// <summary>Saves where the reforged tablet appeared, beside the bench panel's rectangle. See BenchResultFile.</summary>
    private static void RememberBenchResult(GameController gc, RectangleF result)
    {
        var bench = BenchPanelRect(gc);

        if (BenchResultFile.Length == 0 || bench.Width <= 0f || result.Width <= 0f)
            return;

        try
        {
            File.WriteAllText(BenchResultFile, JsonConvert.SerializeObject(new BenchResultRecord
            {
                Bench = [bench.X, bench.Y, bench.Width, bench.Height],
                Result = [result.X, result.Y, result.Width, result.Height],
            }));
        }
        catch (Exception)
        {
            // Learnt again from the next reforge.
        }
    }

    /// <summary>
    /// Where the reforged tablet will appear, from the saved record: as saved where the bench panel is where it was, else
    /// moved and scaled with the panel. Empty with no record, or with the bench closed.
    /// </summary>
    private static RectangleF RememberedBenchResult(GameController gc)
    {
        var bench = BenchPanelRect(gc);

        if (BenchResultFile.Length == 0 || bench.Width <= 0f || bench.Height <= 0f || !File.Exists(BenchResultFile))
            return default;

        try
        {
            var record = JsonConvert.DeserializeObject<BenchResultRecord>(File.ReadAllText(BenchResultFile));

            if (record is not { Bench.Length: 4, Result.Length: 4 } || record.Bench[2] <= 0f || record.Bench[3] <= 0f)
                return default;

            var scaleX = bench.Width / record.Bench[2];
            var scaleY = bench.Height / record.Bench[3];

            return new RectangleF(bench.X + (record.Result[0] - record.Bench[0]) * scaleX, bench.Y + (record.Result[1] - record.Bench[1]) * scaleY,
                record.Result[2] * scaleX, record.Result[3] * scaleY);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>Whether a tablet in the bench sits in the result slot: its centre inside the slot's rectangle. See ReforgeNext.</summary>
    private static bool InResultSlot(TabletOnScreen tablet)
    {
        var centre = new Vector2(tablet.Rect.X + tablet.Rect.Width / 2f, tablet.Rect.Y + tablet.Rect.Height / 2f);

        return centre.X >= _benchResultRect.Left && centre.X <= _benchResultRect.Right &&
               centre.Y >= _benchResultRect.Top && centre.Y <= _benchResultRect.Bottom;
    }

    /// <summary>The reforge whose wait the cursor was last sent to loiter through, by when it began. See ReforgeNext.</summary>
    private static DateTime _loiteredFor;

    /// <summary>How big the loiter over the reforged tablet's slot is, as a share of the slot's smaller side. Chosen.</summary>
    private const float LoiterShare = 0.3f;

    /// <summary>When the last step of a run went, and the gap past which the next is logged as a stall. See StepReforging.</summary>
    private static DateTime _lastStepAt;

    private static readonly TimeSpan LongStep = TimeSpan.FromMilliseconds(40);

    /// <summary>Items in the player's inventory by the game's own record of it, or -1 when it cannot be read.</summary>
    private static long InventoryItemCount(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);

        return Safe.Read(main, static m => m.Inventory.ItemCount, -1L);
    }

    /// <summary>What moving a junk tablet is called, in the status line and to tell a full inventory from other waits.</summary>
    private const string JunkTablet = "a junk tablet";

    /// <summary>What depositing a tablet is called. See JunkTablet.</summary>
    private const string DepositedTablet = "a tablet";

    /// <summary>A junk tablet deposited to make room for a tablet the bench holds. See TabletFullAuto's _roomForBench.</summary>
    private const string JunkForRoom = "a junk tablet, for room";

    /// <summary>Tablets clicked in the batch going and not yet read as gone, by entity address, with when. See MoveNext.</summary>
    private static readonly Dictionary<long, DateTime> MovesPending = [];

    /// <summary>Tablets clicked a second time after the first click did not move them. See MoveNext.</summary>
    private static readonly HashSet<long> MovesRetried = [];

    /// <summary>
    /// How long after its click a tablet still read where it was - a move, a load into the bench, a currency use - counts
    /// as not moved: the player's ping as the game reports it, plus MoveSettleMarginMs, and never under MoveSettleLeastMs,
    /// so it is never shorter than the game can answer; MoveSettleUnknownMs with no ping read. It was 600ms, chosen and
    /// never measured, while the run log shows a click that took on screen 6 to 28ms after it - loads, withdraws and Exalt
    /// uses alike, dozens of each over the dumps of 2026-10-12 - so a missed load held the run more than half a second
    /// before it was clicked again.
    /// </summary>
    private static TimeSpan MoveSettle => TimeSpan.FromMilliseconds(
        _latencyMs > 0 ? Math.Max(MoveSettleLeastMs, _latencyMs + MoveSettleMarginMs) : MoveSettleUnknownMs);

    /// <summary>The ping as last read, in milliseconds, or -1. Read with each draw. See MoveSettle.</summary>
    private static int _latencyMs = -1;

    /// <summary>What MoveSettle adds to the ping. Chosen: half the loot run's margin, as a tablet click shows within 30ms.</summary>
    private const int MoveSettleMarginMs = 75;

    /// <summary>The least MoveSettle is, for the next read of the screen to have come. Chosen.</summary>
    private const int MoveSettleLeastMs = 150;

    /// <summary>MoveSettle with no ping read. Chosen.</summary>
    private const int MoveSettleUnknownMs = 300;

    /// <summary>Settings.TabletRerolling.ShortestMoveWhenDepositingMs, as last read; 0 for ExileInput2's own. See Draw.</summary>
    private static int _shortestMoveDepositingMs;

    /// <summary>Settings.TabletRerolling.ShortestMoveBetweenTabletsMs, as last read; 0 for ExileInput2's own. See Draw.</summary>
    private static int _shortestMoveMs;

    /// <summary>The cell of the tablet the move in flight is for, or null for anything else. See the Moving step.</summary>
    private static RectangleF? _clickCell;

    /// <summary>Whether the move in flight has been sent again for missing its cell. See the Moving step.</summary>
    private static bool _reaimed;

    /// <summary>
    /// ExileInput2's arrival tolerance for a tablet, in pixels: arrived within this of the point aimed at. Its default of
    /// 12 let a click aimed near a cell's edge land in the cell before it (2026-10-09 23:36). Chosen.
    /// </summary>
    internal const int TabletTolerancePx = 3;

    /// <summary>The tolerance for every other move, and what a run hands back: the 12 explosive placement sets itself.</summary>
    internal const int OtherTolerancePx = 12;

    /// <summary>The tolerance last given to ExileInput2, or 0 when not known. See UseTolerance.</summary>
    private static int _toleranceApplied;

    /// <summary>Gives ExileInput2 an arrival tolerance when it differs from the last given.</summary>
    internal static void UseTolerance(int px)
    {
        if (_toleranceApplied != px && Input.SetTolerance(px))
            _toleranceApplied = px;
    }


    /// <summary>The shortest move last given to ExileInput2, -1 for its own, or null when not known. See UseShortestMove.</summary>
    private static int? _shortestMoveApplied;

    /// <summary>What the last cursor move of a run went to, for telling a hop between tablets. See IsTabletHop.</summary>
    private static string _previousSent;

    /// <summary>
    /// Whether a move is a hop from one tablet to the next of the same batch: the same kind of tablet click as the move
    /// before it - a withdraw after a withdraw, a load after a load, a currency use after a currency use. Anything else
    /// between them - a sub-tab, a stack of currency, a hideout object - makes the next move a first one.
    /// </summary>
    private static bool IsTabletHop(string doing) => doing == _previousSent && IsTabletClick(doing);

    /// <summary>Whether a click is on a tablet: moving one, loading one, using a currency on one.</summary>
    private static bool IsTabletClick(string doing) =>
        doing is JunkTablet or DepositedTablet or JunkForRoom or UnidentifiedTablet or ReforgedTablet or TidyTablet or ValuableTablet or "a junk tablet back" ||
        doing.EndsWith(" on a tablet", StringComparison.Ordinal);

    /// <summary>
    /// Gives ExileInput2 the Shortest move setting for a hop between tablets, and its own for every other move.
    /// ExileInput2 adds the shortest move to every move's time (Travel: shortest + per root pixel x root of distance),
    /// so held for a whole run it slowed the long moves - inventory to stash - as much as the hops. Kept per caller name,
    /// which explosive placement shares, so StopReforging hands back ExileInput2's own.
    /// </summary>
    private static void UseShortestMove(bool hop)
    {
        // The Deposit zone's run has its own. See TabletRerollingSettings.ShortestMoveWhenDepositingMs.
        var shortest = _run == TabletRun.Deposit ? _shortestMoveDepositingMs : _shortestMoveMs;
        var wanted = hop && shortest > 0 ? shortest : -1;

        if (_shortestMoveApplied != wanted && Input.SetShortestMove(wanted))
            _shortestMoveApplied = wanted;
    }

    /// <summary>
    /// The next move of a withdraw or a deposit: ctrl+click one of these tablets to the other side - the run's first one
    /// of the three nearest the cursor at random, anywhere in its cell, every one after it the nearest, near its facing
    /// edge. False when nothing is left to click and every click has been read as gone. Not in threes: that is
    /// reforging's, which loads the bench three at a time.
    ///
    /// **A click is taken to have gone through, and the cursor moves straight on.** Waiting for each move to show made a
    /// stop after every tablet, of about 100ms or 290ms as the 150ms read of the screen caught it or missed it (dump
    /// 2026-10-09 21:59). So each click is pending until a read no longer shows it, and the batch is checked as it goes:
    /// a tablet still shown MoveSettle after its click is clicked once more, and after that the run stops. No more clicks
    /// are pending than there is room for on the other side, and none planned past it, so a full inventory or sub-tab is
    /// not clicked into.
    /// </summary>
    /// <param name="toInventory">A withdraw, from the stash into the inventory; else a deposit, the other way.</param>
    /// <param name="room">Room on the other side in place of what the screen shows: the tidy's deposits go to the earliest
    /// sub-tab with room, not the one showing. See TabletTidy.</param>
    /// <param name="inOrder">The tablets moved in the order given, not by the batch rule: the most valuable withdraw's. See BatchNext.</param>
    /// <param name="thenClicks">
    /// The clicks planned after the batch's last tablet, given the room left and where the cursor will be: a withdraw's
    /// next sub-tabs and the tablets remembered in them (see PlannedSubTabsAfter), so the whole withdraw is one path rather
    /// than a stop at each sub-tab. Asked only when every tablet left is planned. Each click is still sent only by whoever
    /// decides on it, a tablet once read where it was planned, and a send that does not match the plan plans afresh. See
    /// PlanAhead.
    /// </param>
    private static bool MoveNext(GameController gc, IEnumerable<TabletOnScreen> movable, string what, bool toInventory, int? room = null,
        bool inOrder = false, Func<int, Vector2, List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)>> thenClicks = null)
    {
        var shown = (toInventory ? StashTablets() : InventoryTablets()).Select(AddressOf).ToHashSet();
        var now = DateTime.UtcNow;

        foreach (var (address, clicked) in MovesPending.ToList())
        {
            // Gone, or dropped by a phase that cleared what was sent.
            if (!shown.Contains(address) || !ReforgingSent.Contains(address))
            {
                MovesPending.Remove(address);
                continue;
            }

            if (_read - clicked < MoveSettle)
                continue;

            if (!MovesRetried.Add(address))
            {
                StopReforging($"{what} did not move after two clicks");
                return true;
            }

            LogReforging($"{what} was still there {MoveSettle.TotalMilliseconds:0}ms after its click: clicking it again");
            MovesPending.Remove(address);
            ReforgingSent.Remove(address);
        }

        var left = movable.Where(t => !ReforgingSent.Contains(AddressOf(t))).ToList();
        var space = room ?? (toInventory ? InventoryFreeCells(gc) : SubTabHolds - StashTablets().Count);

        if (left.Count == 0 || MovesPending.Count >= space)
        {
            if (MovesPending.Count == 0)
                return false;

            Say("waiting for the game to move them");
            return true;
        }

        var mouse = ImGuiNET.ImGui.GetMousePos();
        var first = _moved == 0;
        TabletOnScreen next;
        Vector2 point;
        // The plan's own rectangle when following it, so PlanAhead matches it exactly rather than planning afresh.
        RectangleF? plannedRect = null;

        // **The tablet the plan already has next, where a later sub-tab's was planned from its record**: taken as planned,
        // with the plan's point and the rest of the plan after it, so the path made is the one drawn. BatchNext would plan
        // the sub-tab afresh from the cursor, and a different first point would plan the whole path again. See
        // PlannedSubTabsAfter.
        if (thenClicks != null && PlannedAhead.Count > 0 && PlannedAhead[0] is { Ctrl: true, Right: false } ahead &&
            left.FirstOrDefault(t => MathF.Abs(t.Rect.X - ahead.Rect.X) < 1f && MathF.Abs(t.Rect.Y - ahead.Rect.Y) < 1f) is { } planned)
        {
            next = planned;
            plannedRect = ahead.Rect;
            point = ahead.Point ?? ItemClicks.NearEdgeOf(planned.Rect, mouse);
            BatchOrder.Clear();
            _batchOf = "";
            BatchRest.Clear();
            BatchRest.AddRange(PlannedAhead.Skip(1));
        }
        else
        {
            // From the batch as planned. See BatchNext. Planned ahead no further than the room left after this click: the
            // clicks themselves stopped at the room, and the plan drawn went on through every tablet to move.
            (next, point) = BatchNext(what, left, mouse, first, first, ctrl: true,
                restLimit: space == int.MaxValue ? int.MaxValue : space - MovesPending.Count - 1, inOrder: inOrder);

            if (thenClicks != null && BatchRest.Count == left.Count - 1)
            {
                var roomLeft = space == int.MaxValue ? int.MaxValue : space - MovesPending.Count - left.Count;

                BatchRest.AddRange(thenClicks(roomLeft, BatchRest.Count > 0 && BatchRest[^1].Point is { } last ? last : point));
            }
        }

        var chosen = AddressOf(next);

        // A junk tablet taken out says why it is junk, once. See JunkReasonOf.
        if (what == JunkTablet && _junkReasonSettings is { } judged)
            LogReforging($"withdraw ({next.Rect.X:0},{next.Rect.Y:0}): {JunkReasonOf(gc, next, judged)}");

        ReforgingSent.Add(chosen);
        MovesPending[chosen] = now + StepTimeout;
        Progressed();
        _moved++;

        // Done at the click. The time it was clicked replaces the placeholder above once the click goes. See Clicked.
        Send(plannedRect ?? next.Rect, true, what, () =>
            {
                MovesPending[chosen] = _clickedAt;
                return true;
            },
            point);
        return true;
    }

    /// <summary>
    /// A batch of tablet clicks in the order it was planned - by entity address, each with the point it lands on - and
    /// which batch it is. See BatchNext.
    /// </summary>
    private static readonly List<(long Address, Vector2 Point)> BatchOrder = [];

    private static string _batchOf = "";

    /// <summary>
    /// The next tablet of a batch, and its point, from the order planned for the batch: while the batch is the same one
    /// and its next tablet is still there, the order is followed; otherwise it is planned again from the cursor, by the
    /// rule every batch of clicks follows (ItemClicks.NextItemOf) - firstRule for the top-left and bottom-left start - with
    /// each point just inside its tablet's edge facing the last, and the first anywhere in its cell where anywhereFirst.
    /// A batch followed in a fixed order can be drawn ahead and made as drawn: the clicks after this one, restLimit at
    /// most, go to Send for PlanAhead. firstPool, where given, is where the first comes from. See PlanAhead.
    /// </summary>
    private static (TabletOnScreen Next, Vector2 Point) BatchNext(string batch, IReadOnlyList<TabletOnScreen> items, Vector2 mouse,
        bool firstRule, bool anywhereFirst, bool ctrl, int restLimit = int.MaxValue, IReadOnlyList<TabletOnScreen> firstPool = null,
        bool inOrder = false) =>
        BatchNext(batch, items, AddressOf, static t => t.Rect, mouse, firstRule, anywhereFirst, ctrl, restLimit, firstPool, inOrder);

    /// <summary>
    /// A batch of currency stacks, each known by its cell, in the order planned for it. See the tablets' BatchNext, which
    /// this follows with the same rule.
    /// </summary>
    private static ((RectangleF Rect, string Name) Next, Vector2 Point) StackBatchNext(string batch,
        IReadOnlyList<(RectangleF Rect, string Name)> stacks, Vector2 mouse, bool ctrl) =>
        BatchNext(batch, stacks, static s => CellKeyOfRect(s.Rect), static s => s.Rect, mouse, false, true, ctrl);

    /// <summary>A cell's place on screen as one key, for batches of things with no entity address. See StackBatchNext.</summary>
    private static long CellKeyOfRect(RectangleF rect) => ((long)MathF.Round(rect.X) << 32) | (uint)MathF.Round(rect.Y);

    /// <summary>BatchNext for any kind of item, known by keyOf and clicked in rectOf.</summary>
    /// <param name="inOrder">
    /// The items clicked in the order given rather than by ItemClicks.NextItemOf, each point still just inside its cell's
    /// edge facing the last: for a batch whose order is its point, as the most valuable withdraw's is.
    /// </param>
    /// <param name="groupSize">
    /// For a batch clicked in groups with a trip elsewhere between them, as the bench's loads are three at a time with the
    /// reforge button between: after firstGroup items, and every groupSize after that, the next is planned from
    /// groupStart rather than from the item before it. Without it, every three after the first were chosen by distance
    /// from the last tablet loaded, though the cursor was at the reforge button (2026-10-13).
    /// </param>
    private static (T Next, Vector2 Point) BatchNext<T>(string batch, IReadOnlyList<T> items, Func<T, long> keyOf, Func<T, RectangleF> rectOf,
        Vector2 mouse, bool firstRule, bool anywhereFirst, bool ctrl, int restLimit = int.MaxValue, IReadOnlyList<T> firstPool = null,
        bool inOrder = false, int groupSize = 0, int firstGroup = 0, Vector2? groupStart = null)
    {
        var byAddress = new Dictionary<long, T>();

        foreach (var item in items)
            byAddress[keyOf(item)] = item;

        var fits = _batchOf == batch && BatchOrder.Count > 0 && byAddress.ContainsKey(BatchOrder[0].Address) &&
                   byAddress.Keys.All(address => BatchOrder.Any(o => o.Address == address));

        if (!fits)
        {
            BatchOrder.Clear();
            _batchOf = batch;

            var remaining = items.ToList();
            var from = mouse;
            var firstOne = true;

            while (remaining.Count > 0)
            {
                var planned = BatchOrder.Count;

                if (groupSize > 0 && groupStart is { } start && planned >= firstGroup && (planned - firstGroup) % groupSize == 0 && planned > 0)
                    from = start;

                var pool = firstOne && firstPool is { Count: > 0 } ? firstPool.Where(remaining.Contains).ToList() : remaining;

                if (pool.Count == 0)
                    pool = remaining;

                var next = inOrder ? remaining[0] : ItemClicks.NextItemOf(pool, rectOf, from, firstOne && firstRule);
                var point = firstOne && anywhereFirst ? ItemClicks.AnywhereIn(rectOf(next)) : ItemClicks.NearEdgeOf(rectOf(next), from);

                BatchOrder.Add((keyOf(next), point));
                remaining.Remove(next);
                from = point;
                firstOne = false;
            }
        }

        var (address, landing) = BatchOrder[0];

        BatchOrder.RemoveAt(0);
        BatchRest.Clear();

        foreach (var (later, at) in BatchOrder.Take(Math.Max(0, restLimit)))
        {
            if (byAddress.TryGetValue(later, out var item))
                BatchRest.Add((rectOf(item), at, false, ctrl));
        }

        return (byAddress[address], landing);
    }

    /// <summary>The clicks a batch has planned after the one being sent, for PlanAhead; set by BatchNext, used once.</summary>
    private static readonly List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)> BatchRest = [];

    /// <summary>What ExileInput2 was last given to plan and draw, still to come. See PlanAhead.</summary>
    private static readonly List<(RectangleF Rect, Vector2? Point, bool Right, bool Ctrl)> PlannedAhead = [];

    /// <summary>Settings.Automation.ShowPlannedAutomation, as last read. See Draw.</summary>
    private static bool _showPlanned;

    /// <summary>
    /// Gives ExileInput2 the click being sent, and the batch's clicks after it, to plan, draw while Show planned automation
    /// in hideout is on, and make as drawn - unless this click is the next of what it was last given, which it already
    /// has. A click with no point is planned by its cell, where ExileInput2 would aim. Each move at the shortest move it
    /// would have had: Shortest move between tablets (or when depositing) for a hop between tablets, ExileInput2's own
    /// otherwise. See BatchNext and ExileInput2's PlanClicks.
    /// </summary>
    /// <summary>
    /// Whether a planned move from one click to the next is a hop between tablets, at Shortest move between tablets (or when
    /// depositing): both cells of one grid, so the same size - inventory to inventory, stash to stash. Every later click of
    /// a plan was given the hop's shortest move, so the long ways - the inventory to the bench's reforged tablet, that to
    /// the reforge button - took about 380ms where ExileInput2's own timing takes about 250 (dump 2026-10-12 14:00:49).
    /// </summary>
    private static bool IsHopBetweenCells(RectangleF from, RectangleF to) =>
        from.Width > 0f && MathF.Abs(from.Width - to.Width) < 1f && MathF.Abs(from.Height - to.Height) < 1f;

    private static bool PlanAhead(RectangleF target, Vector2? point, bool ctrl, bool right, bool hop)
    {
        var rest = BatchRest.ToList();

        BatchRest.Clear();

        if (PlannedAhead.Count > 0 && PlannedAhead[0].Rect == target && PlannedAhead[0].Point == point &&
            PlannedAhead[0].Right == right && PlannedAhead[0].Ctrl == ctrl)
        {
            PlannedAhead.RemoveAt(0);

            // **What comes after this click, where it goes on from what is planned, is added to the queue's end** rather
            // than the queue planned again, so one run of the bench is one queue. See ExileInput2's ExtendPlan.
            if (rest.Count > PlannedAhead.Count && rest.Take(PlannedAhead.Count).SequenceEqual(PlannedAhead))
            {
                var added = rest.Skip(PlannedAhead.Count).ToList();
                var hopMsAdded = (_run == TabletRun.Deposit ? _shortestMoveDepositingMs : _shortestMoveMs) is > 0 and var ms ? ms : -1;
                var before = PlannedAhead.Count > 0 ? PlannedAhead[^1].Rect : target;

                if (Input.ExtendPlan(
                        added.Select(p => p.Rect).ToArray(),
                        added.Select(p => p.Point ?? new Vector2(float.NaN, float.NaN)).ToArray(),
                        added.Select(p => p.Right).ToArray(),
                        added.Select(p => p.Ctrl ? new[] { System.Windows.Forms.Keys.LControlKey } : []).ToArray(),
                        added.Select((p, i) => IsHopBetweenCells(i == 0 ? before : added[i - 1].Rect, p.Rect) ? hopMsAdded : -1).ToArray()))
                    PlannedAhead.AddRange(added);
            }

            return true;
        }

        PlannedAhead.Clear();
        PlannedAhead.Add((target, point, right, ctrl));
        PlannedAhead.AddRange(rest);

        var shortest = _run == TabletRun.Deposit ? _shortestMoveDepositingMs : _shortestMoveMs;
        var hopMs = shortest > 0 ? shortest : -1;

        var planned = Input.PlanClicks(
            PlannedAhead.Select(p => p.Rect).ToArray(),
            PlannedAhead.Select(p => p.Point ?? new Vector2(float.NaN, float.NaN)).ToArray(),
            PlannedAhead.Select(p => p.Right).ToArray(),
            PlannedAhead.Select(p => p.Ctrl ? new[] { System.Windows.Forms.Keys.LControlKey } : []).ToArray(),
            PlannedAhead.Select((p, i) => i == 0 ? (hop ? hopMs : -1) : IsHopBetweenCells(PlannedAhead[i - 1].Rect, p.Rect) ? hopMs : -1).ToArray(),
            _showPlanned);

        PlannedAhead.RemoveAt(0);

        if (!planned)
            PlannedAhead.Clear();

        return planned;
    }

    /// <summary>
    /// Whether the click in flight was planned by ExileInput2, which then counts it arrived once the cursor is on its
    /// point - before the game reports it there - so the cell check, which reads that report, is skipped for it: its
    /// point is inside its cell by construction. See PlanAhead.
    /// </summary>
    private static bool _sentPlanned;

    /// <summary>
    /// The run's loading decisions, newest last, each with what was counted when it was made: for the dump, so a decision
    /// that went wrong can be read rather than guessed at. The last LogKept are kept.
    /// </summary>
    private static readonly List<string> ReforgingLog = [];

    private const int LogKept = 150;

    /// <summary>
    /// Junk tablets clicked into the bench and not yet read as gone from the inventory, by entity address, with when.
    /// Loading goes straight on from one click to the next, as the moves between stash and inventory do; the reforge
    /// waits until none is pending and three read in the bench. See SettleBenchLoads.
    /// </summary>
    private static readonly Dictionary<long, DateTime> BenchLoadsPending = [];

    /// <summary>When a tablet in the bench was first read not yet fully read, or null. See ReforgeNext.</summary>
    private static DateTime? _benchHalfReadSince;

    /// <summary>When each tablet read in the bench was first seen there, by entity address. See ReforgeNext.</summary>
    private static readonly Dictionary<long, DateTime> BenchFirstSeen = [];

    /// <summary>How many BenchFirstSeen holds before it starts again. Chosen: far more than a bench holds in a run.</summary>
    private const int BenchFirstSeenKept = 200;

    /// <summary>When a load was last clicked into the bench. See ReforgeNext's reforged tablet.</summary>
    private static DateTime _benchLoadClickedAt;

    /// <summary>When a load was last read as gone from the inventory. See ReforgeNext's reforged tablet.</summary>
    private static DateTime _benchLoadLandedAt;

    /// <summary>Loads clicked a second time after the first did not go in. See SettleBenchLoads.</summary>
    private static readonly HashSet<long> BenchLoadsRetried = [];

    /// <summary>
    /// Drops the pending loads now read as gone from the inventory; a load still there MoveSettle after its click is
    /// clicked once more, and after that set aside as Unloadable, as a load that timed out was before. False when the run
    /// was stopped: more than MostUnloadable set aside.
    /// </summary>
    private static bool SettleBenchLoads()
    {
        var carried = InventoryTablets().Select(AddressOf).ToHashSet();

        foreach (var (address, clicked) in BenchLoadsPending.ToList())
        {
            if (!carried.Contains(address) || !ReforgingSent.Contains(address))
            {
                if (!carried.Contains(address))
                    _benchLoadLandedAt = DateTime.UtcNow;

                BenchLoadsPending.Remove(address);
                continue;
            }

            if (_read - clicked < MoveSettle)
                continue;

            BenchLoadsPending.Remove(address);

            if (BenchLoadsRetried.Add(address))
            {
                LogReforging($"a junk tablet was still in the inventory {MoveSettle.TotalMilliseconds:0}ms after its click: clicking it again");
                ReforgingSent.Remove(address);
                continue;
            }

            if (Unloadable.Count >= MostUnloadable)
            {
                StopReforging($"{MostUnloadable} junk tablets would not go into the bench");
                return false;
            }

            Unloadable.Add(address);
            LogReforging("a junk tablet did not go into the bench after two clicks: set aside");
        }

        return true;
    }

    /// <summary>
    /// Where the game read the cursor when the click went, and the rect it was sent to, for the run's log: ExileInput2
    /// chooses the point within a rect and does not say which.
    /// </summary>
    private static Vector2 _clickedPoint;

    private static RectangleF _sentTarget;

    private static string RectSaid(RectangleF rect) => $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})";

    /// <summary>A click as Send was asked for it, kept for one more try. See RetryOrStop.</summary>
    private sealed record SentClick(RectangleF Target, bool Ctrl, string Doing, Func<bool> Done, bool Pointed, TimeSpan? Timeout, bool Right,
        Func<string> BeforeClick);

    private static SentClick _lastSend;

    /// <summary>Whether the last click has been tried once more already.</summary>
    private static bool _sendRetried;

    /// <summary>Whether Send is resending the last click, so it keeps what it was first asked.</summary>
    private static bool _resending;

    /// <summary>When the last click is to be tried once more, or null.</summary>
    private static DateTime? _retryAt;

    /// <summary>Whether a hideout object's click has come to nothing once since the run last got anywhere.</summary>
    private static bool _worldRetried;

    /// <summary>How long a failed click waits before it is tried once more.</summary>
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// A click that failed - the cursor did not reach it, it would not go, or nothing came of it - tried once more after
    /// RetryWait, at a fresh point in the same target; the second failure stops the run, saying so. A panel slow to draw
    /// or a click the game did not take stopped whole runs at the first try, where a moment later the same click works.
    /// </summary>
    private static void RetryOrStop(string why)
    {
        _reforgingStep = ReforgingStep.Idle;
        _worldTarget = null;

        if (_lastSend == null || _sendRetried)
        {
            StopReforging(_sendRetried ? $"{why}, after one retry" : why);
            return;
        }

        _sendRetried = true;
        _retryAt = DateTime.UtcNow + RetryWait;
        LogReforging($"{why}: trying once more");
        Say($"{why}: trying once more");
    }

    /// <summary>When the move in flight was sent, for the time to its click in the run's log. See Send.</summary>
    private static DateTime _sentAt;

    private static void LogReforging(string line)
    {
        ReforgingLog.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");

        if (ReforgingLog.Count > LogKept)
            ReforgingLog.RemoveAt(0);
    }

    /// <summary>The junk tablets that would not go into the bench this run, by entity address, left out of loading.</summary>
    private static readonly HashSet<long> Unloadable = [];

    /// <summary>How many tablets may fail to load before the run stops rather than setting another aside.</summary>
    private const int MostUnloadable = 3;

    /// <summary>The tablet the load in flight is for.</summary>
    private static long _loadingAddress;

    /// <summary>What putting down an item found held on the cursor is called. See the click on arrival.</summary>
    private const string HeldItemPutDown = "the item held, put down";

    /// <summary>How many items found held on the cursor a run puts down before it stops instead. Chosen.</summary>
    private const int PutDownsPerRun = 3;

    /// <summary>Items put down from the cursor this run. See PutDownsPerRun.</summary>
    private static int _putDowns;

    /// <summary>What taking the reforged tablet out is called, in the status line and to tell its retries apart.</summary>
    private const string ReforgedTablet = "the reforged tablet";

    /// <summary>Sends the cursor to a target, to be clicked on arrival, and what must then show.</summary>
    /// <param name="point">A point in the target to aim at, in place of ExileInput2's spread about its centre. See
    /// AnywhereIn and NearEdgeOf.</param>
    /// <param name="timeout">How long after the click what must show may take; StepTimeout by default.</param>
    /// <param name="right">A right-click rather than a left one.</param>
    /// <param name="beforeClick">A reason not to click, asked on arrival, which stops the run; an empty one lets this click go instead. See _beforeClick.</param>
    private static void Send(RectangleF target, bool ctrl, string doing, Func<bool> done, Vector2? point = null,
        TimeSpan? timeout = null, bool right = false, Func<string> beforeClick = null)
    {
        // What was asked, for one more try after a wait should it fail. See RetryOrStop.
        if (!_resending)
        {
            _lastSend = new SentClick(target, ctrl, doing, done, point != null, timeout, right, beforeClick);
            _sendRetried = false;
        }

        _sentTarget = target;

        var hop = IsTabletHop(doing);

        _sentPlanned = PlanAhead(target, point, ctrl, right, hop);
        UseShortestMove(hop);
        _previousSent = doing;

        var tabletClick = IsTabletClick(doing);

        UseTolerance(tabletClick ? TabletTolerancePx : OtherTolerancePx);
        _clickCell = tabletClick ? target : null;
        _reaimed = false;

        var aimed = target.Width > 0f && (point is { } at ? Input.MoveTo(at, ctrl: ctrl) : Input.MoveTo(target, ctrl: ctrl));

        // The move's own times and flourishes, set as it was asked for, for the log. See ExileInput2's LastMoveTimes.
        _sentMoveTimes = aimed ? Input.LastMoveTimes() : null;

        if (!aimed)
        {
            RetryOrStop($"could not aim at {doing}");
            return;
        }

        _reforgingDoing = doing;
        _reforgingCtrl = ctrl;
        _reforgingRight = right;
        _beforeClick = beforeClick;
        _reforgingDone = done;
        _clickTimeout = timeout ?? StepTimeout;
        _worldTarget = null;
        _worldClick = false;
        _reforgingStep = ReforgingStep.Moving;
        _reforgingDeadline = DateTime.UtcNow + StepTimeout;
        _sentAt = DateTime.UtcNow;
        Say($"{(ctrl ? "moving" : "clicking")} {doing}");
    }

    /// <summary>"Reforging: ..." above the bench while a run goes, and its last word for a moment after.</summary>
    private static void DrawReforgingStatus(Graphics graphics, GameController gc)
    {
        if (!_reforgingHasCursor && DateTime.UtcNow - _reforgingSaidAt > ReforgingEndShownFor)
            return;

        var bench = Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench, null);
        var benchRect = Safe.Read(bench, static b => b.IsVisible ? b.GetClientRectCache : default, default);
        var rect = _run switch
        {
            TabletRun.Reforge => benchRect,
            TabletRun.FullAuto => benchRect.Width > 0f ? benchRect : _stashRect,
            _ => _stashRect,
        };

        var line = _run switch
        {
            TabletRun.FullAuto => $"Reforging, full auto: {_reforgingSaid}" +
                                  $"{(_reforgingHasCursor && _reforged > 0 ? $", {_reforged} reforged" : "")}",
            TabletRun.Craft => $"Crafting: {_reforgingSaid}{(_reforgingHasCursor && _crafted > 0 ? $", {_crafted} currency used" : "")}",
            TabletRun.Tidy => $"Tidying: {_reforgingSaid}",
            TabletRun.WithdrawValuable => $"Withdrawing: {_reforgingSaid}{(_reforgingHasCursor && _moved > 0 ? $", {_moved} withdrawn" : "")}",
            TabletRun.Deposit => $"Depositing: {_reforgingSaid}{(_reforgingHasCursor && _moved > 0 ? $", {_moved} deposited" : "")}",
            _ => $"Reforging: {_reforgingSaid}{(_reforgingHasCursor && _reforged > 0 ? $", {_reforged} reforged" : "")}",
        };

        if (_craftAndReforge)
            line = $"Craft and reforge, pass {_combinedPasses}: {line}";
        else if (!_reforgingHasCursor && _endedCraftAndReforge)
            line = $"Craft and reforge: {_reforgingSaid}";
        // Above the panel in use; under the cursor between panels, while a full-auto run walks from one to the next.
        if (rect.Width <= 0f)
        {
            DrawUnderCursor(graphics, gc, line, Color.White);
            return;
        }

        var size = graphics.MeasureText(line);

        graphics.DrawTextWithBackground(line, new Vector2(rect.Left + 4f, rect.Top - size.Y - 6f), Color.White, FontAlign.Left,
            LabelBackground);
    }

    /// <summary>The bench as the run sees it, for the dump.</summary>
    private static string ReforgingSaid(GameController gc, TabletRerollingSettings settings)
    {
        var b = new StringBuilder();
        var open = ReforgingBenchOpen(gc);

        b.AppendLine($"  reforging bench: open {open}; run {(_reforgingHasCursor ? $"going, step {_reforgingStep}" : "idle")}; " +
                     $"last said \"{_reforgingSaid}\"");

        var focused = FocusedInput(gc);
        var focusedRect = Safe.Read(focused, static f => f.GetClientRectCache, default);

        b.AppendLine(focused == null
            ? "    input focus: none"
            : $"    input focus: {Safe.Read(focused, static f => f.Address, 0L):X} ({focusedRect.X:0},{focusedRect.Y:0} " +
              $"{focusedRect.Width:0}x{focusedRect.Height:0}), visible {Safe.Read(focused, static f => f.IsVisible, false)}, " +
              $"text \"{Safe.Read(focused, static f => f.Text, "")}\"");

        if (!open)
        {
            // The log is kept after the bench closes, which is when a stopped run is looked at.
            b.AppendLine($"    the run's last {ReforgingLog.Count} decisions and moves, newest last; {Unloadable.Count} tablet(s) set aside:");

            foreach (var line in ReforgingLog)
                b.AppendLine($"      {line}");

            return b.ToString();
        }

        var button = ReforgeButton(gc);
        var rect = Safe.Read(button, static x => x.GetClientRectCache, default);

        b.AppendLine($"    reforge button: {(button == null ? "not found" : $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})")}");

        foreach (var tablet in BenchTablets(gc))
            b.AppendLine($"    in the bench ({tablet.Rect.X:0},{tablet.Rect.Y:0} {tablet.Rect.Width:0}x{tablet.Rect.Height:0}): " +
                         $"{tablet.Rarity}, identified {tablet.Identified}, {tablet.Uses} uses, weight {settings.WeightOf(tablet.Modifiers)}, " +
                         $"junk {IsJunk(gc, tablet, settings)}" +
                         $"{(MostChaosOf(gc, tablet, settings) is { } most ? $", worth at most {most:0.##}c" : "")}");

        b.AppendLine($"    result slot remembered: {(_benchResultRect.Width > 0f ? RectSaid(_benchResultRect) : "none")}; " +
                     $"reforged tablet {(_reforgedPendingSince is { } pending ? $"awaited for {(DateTime.UtcNow - pending).TotalMilliseconds:0}ms" : "not awaited")}");
        BenchElementsSaid(b, gc);

        var inventory = Seen.Where(t => t.Where == "inventory").ToList();

        b.AppendLine($"    inventory: {inventory.Count} tablet(s), {inventory.Count(t => IsJunk(gc, t, settings))} junk");
        b.AppendLine($"    the run's last {ReforgingLog.Count} decisions and moves, newest last; {Unloadable.Count} tablet(s) set aside:");

        foreach (var line in ReforgingLog)
            b.AppendLine($"      {line}");

        // How ExileInput2's planned queues ended, for this plugin's name - which the loot runs share. See PlanAhead.
        var ends = Input?.PlanEndsSaid() ?? [];

        b.AppendLine($"    ExileInput2's plans for this plugin, how each ended, newest last ({ends.Length}):" +
                     (ends.Length == 0 ? " none, or an ExileInput2 too old to say" : ""));

        foreach (var line in ends)
            b.AppendLine($"      {line}");

        return b.ToString();
    }

    /// <summary>
    /// The bench's elements that carry an entity or overlap the remembered result slot, each with both visibility flags,
    /// its entity and that entity's metadata, for the dump. BenchTablets skips an element that is not visible locally,
    /// has no entity, carries something other than a tablet or is 4px or smaller, and says nothing about which. A run
    /// stopped on "the reforged tablet did not appear" with the tablet showing in the slot (dump 2026-10-17 19:33:42),
    /// the slot's element visible and no tablet read in it, and this says which of those it was.
    /// </summary>
    private static void BenchElementsSaid(StringBuilder b, GameController gc)
    {
        var bench = Safe.Read(gc, static g => g.IngameState.IngameUi.ReforgingBench, null);
        var stack = new Stack<(Element Element, string Path, int Depth)>();
        var visited = 0;
        var said = 0;

        if (bench == null)
            return;

        stack.Push((bench, "", 0));
        b.AppendLine("    bench elements carrying an entity or over the result slot (path from the bench):");

        while (stack.Count > 0 && visited++ < MostElements && said < 40)
        {
            var (element, path, depth) = stack.Pop();
            var rect = Safe.Read(element, static e => e.GetClientRectCache, default);
            var entity = Safe.Read(element, static e => e.Entity, null);
            var overSlot = _benchResultRect.Width > 0f && rect.Width > 0f &&
                           rect.Left < _benchResultRect.Right && rect.Right > _benchResultRect.Left &&
                           rect.Top < _benchResultRect.Bottom && rect.Bottom > _benchResultRect.Top;

            if (depth > 0 && (entity != null || overSlot))
            {
                said++;
                b.AppendLine($"      [{path}] {RectSaid(rect)} visible {Safe.Read(element, static e => e.IsVisible, false)}, " +
                             $"visible locally {Safe.Read(element, static e => e.IsVisibleLocal, false)}, " +
                             (entity == null
                                 ? "no entity"
                                 : $"entity {Safe.Read(entity, static e => e.Address, 0L):X}, " +
                                   $"metadata \"{Safe.Read(entity, static e => e.Metadata, null) ?? "unreadable"}\""));
            }

            if (depth >= DeepestElement)
                continue;

            var kids = Safe.Read(element, static e => e.Children, null) ?? [];

            for (var i = kids.Count - 1; i >= 0; i--)
                stack.Push((kids[i], path.Length == 0 ? $"{i}" : $"{path}->{i}", depth + 1));
        }

        if (said == 0)
            b.AppendLine("      none");
    }
}
