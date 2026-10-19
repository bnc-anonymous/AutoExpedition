using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Color = System.Drawing.Color;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Autodeposit: a "Deposit" zone to the right of the inventory's gold amount, drawn while the stash and the inventory are
/// both open. Hovered, it outlines the inventory cells it would empty - every cell but those kept in
/// AutodepositSettings.KeptInventoryCells; the action key there moves their items to the open stash tab.
///
/// **One ctrl+right click per kind of stackable, one ctrl+left click per other item.** A ctrl+right click on a stack
/// sends every stack of that kind in the inventory, so it is used only when no stack of the same kind (by metadata) lies
/// in a kept cell, which it would take too; otherwise each stack is ctrl+left clicked alone. An item counts as kept
/// when any cell it covers is kept. Uses the plugin's shared ExileInput2 claim, Tablets.Input.
///
/// **Run as the tablet Deposit zone runs (Tablets.MoveNext, Tablets.Tick)**:
/// - the clicks are planned ahead as a queue, by the rule every batch of clicks follows (ItemClicks.NextItemOf), each
///   aimed just inside its item's edge facing the last click (ItemClicks.NearEdgeOf), the first anywhere in its item;
///   ExileInput2 is given the queue, draws it while Show planned automation in hideout is on, and makes each move as drawn. Planned
///   again from where the run is when something unexpected happens. See PlanQueue
/// - a 3px arrival tolerance, and Shortest move between items for every move of the run
/// - an item is clicked only with the cursor inside it, aimed again once anywhere in it, then the run stops
/// - each click is taken to have gone through and the cursor moves straight on; an item still there MoveSettle after its
///   click is clicked once more, and still there after that, the run stops
/// - a move that never landed, or a click that did not go, is tried once more after RetryWait
/// - the game taking typed input pauses the run, the cursor handed back, for up to PausedFor
/// - the claim is renewed every tick, and every move and decision is logged for the dump
/// </summary>
internal static class InventoryDeposit
{
    /// <summary>The inventory's columns and rows.</summary>
    public const int Columns = 12;

    public const int Rows = 5;

    private enum Phase
    {
        Idle,
        Choosing,
        Moving,
        Paused,
    }

    /// <summary>One click of a run: where, which button, and the items it is expected to take away.</summary>
    private sealed record PlannedClick(RectangleF Rect, bool Right, string Name, long[] Takes);

    private static Phase _phase = Phase.Idle;
    private static PlannedClick _current;

    /// <summary>Items chosen for a click this run, by entity address; they are not chosen again unless sent back. See Choose.</summary>
    private static readonly HashSet<long> Sent = new();

    /// <summary>Items clicked and still shown, by entity address, with when their click went. See Choose.</summary>
    private static readonly Dictionary<long, DateTime> Pending = new();

    /// <summary>Items clicked a second time for still being there, by entity address. A third is not made.</summary>
    private static readonly HashSet<long> Retried = new();

    /// <summary>
    /// The clicks still to make, in order, each with the point it lands on: planned from the run's start, or from where
    /// the run is when something unexpected sends it back - an item clicked again, the inventory changed under it. Given
    /// to ExileInput2 to draw and to make as drawn. See PlanQueue.
    /// </summary>
    private static readonly List<(PlannedClick Click, Vector2 Point, bool FirstOfRun)> Queue = new();

    /// <summary>Whether ExileInput2 has dropped the queue it was given, after a move it did not plan: given again.</summary>
    private static bool _queueDropped;

    /// <summary>When the run last ticked; DateTime.MinValue when idle. See Tick.</summary>
    private static DateTime _lastTickAt = DateTime.MinValue;

    /// <summary>A gap between ticks this long or longer is logged. Chosen: a frame is 6 to 16ms.</summary>
    private static readonly TimeSpan LongTick = TimeSpan.FromMilliseconds(40);

    /// <summary>When the run began, for the time it took. See Choose.</summary>
    private static DateTime _runStartedAt;

    /// <summary>Whether the click in hand is the queue's own, rather than a retry anywhere in its item.</summary>
    private static bool _currentPlanned;

    /// <summary>When the click in hand's move was seen arrived; null until it is. For the log.</summary>
    private static DateTime? _arrivedAt;

    /// <summary>The click in hand's move's wait for the button and its own length, as ExileInput2 gave them. For the log.</summary>
    private static (float WaitMs, float TravelMs, bool Swooped, float OvershotPx)? _moveTimes;

    /// <summary>Moves made this run, so the first is told from the hops after it.</summary>
    private static int _moves;

    private static int _clicks;

    /// <summary>Whether the click in hand has been aimed again for a cursor short of the item.</summary>
    private static bool _reaimed;

    /// <summary>Whether the click in hand is already its second try. See RetryOrStop.</summary>
    private static bool _sendRetried;

    /// <summary>When the click in hand is tried again; null when none is waiting. See RetryOrStop.</summary>
    private static DateTime? _retryAt;

    /// <summary>When the run paused for the game taking typed input. See Tick.</summary>
    private static DateTime _pausedSince;

    /// <summary>Shortest move between items, read when the run began.</summary>
    private static int _shortestMs;

    private static DateTime _deadline;
    private static DateTime _sentAt;

    /// <summary>How long a clicked item may still show before it is clicked again, as the tablet Deposit zone's.</summary>
    private static readonly TimeSpan MoveSettle = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a move to an item may take before it is tried again, as the tablet runs' StepTimeout.</summary>
    private static readonly TimeSpan MoveFor = TimeSpan.FromSeconds(3);

    /// <summary>The wait before a failed move or click is tried once more, as the tablet runs'.</summary>
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the run waits for the game to stop taking typed input, as the tablet runs'.</summary>
    private static readonly TimeSpan PausedFor = TimeSpan.FromSeconds(10);

    /// <summary>The run's decisions and moves, newest last, for the dump. See Describe.</summary>
    private static readonly List<string> Log = new();

    private const int LogKept = 80;

    /// <summary>What the last run said, and when, shown under the zone for NoteFor.</summary>
    private static (string Text, bool Failed, DateTime At) _note = ("", false, DateTime.MinValue);

    private static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(4);

    public static bool Running => _phase != Phase.Idle;

    /// <summary>Whether the stash and the inventory are both open.</summary>
    private static bool StashAndInventoryOpen(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IsVisible, false) &&
        Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false);

    /// <summary>
    /// The Deposit zone: just right of the gold amount, as wide as the word "Deposit" and a little padding, a little taller
    /// than the gold's text. Empty when the stash and inventory are not both open, the gold amount cannot be read, or the
    /// zone would run past the inventory panel's right edge.
    /// </summary>
    private static RectangleF ZoneRect(GameController gc)
    {
        if (!StashAndInventoryOpen(gc) || Tablets.GoldAmountRect(gc) is not { Height: > 0f } gold ||
            Tablets.InventoryRect(gc) is not { Width: > 0f } panel)
            return default;

        var left = gold.Right + gold.Height;
        var wide = (_wordWidth > 0f ? _wordWidth : gold.Height * 3.5f) + gold.Height * 0.8f;

        return left + wide > panel.Right
            ? default
            : new RectangleF(left, gold.Top - gold.Height * 0.25f, wide, gold.Height * 1.5f);
    }

    /// <summary>The word "Deposit" as last measured by Draw, which ZoneRect sizes the zone by; 0 before the first draw.</summary>
    private static float _wordWidth;

    private static Vector2 Cursor(GameController gc) =>
        Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

    private static bool Contains(RectangleF rect, Vector2 at) =>
        rect.Width > 0f && at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom;

    /// <summary>
    /// Whether the action key is Autodeposit's now: a run going, which the press stops, or the cursor on the Deposit zone
    /// with Autodeposit on and Enable automation on.
    /// </summary>
    public static bool TakesActionKey(GameController gc, AutoExpeditionSettings settings) =>
        Running ||
        settings.Automation.Enable.Value && settings.Automation.Autodeposit.EnableAutodeposit.Value &&
        Contains(ZoneRect(gc), Cursor(gc));

    /// <summary>The action key on the zone starts a run; during one, anywhere, it stops it.</summary>
    public static void ActOnActionKey(GameController gc, AutoExpeditionSettings settings)
    {
        if (Running)
        {
            Stop("Deposit stopped", true);
            return;
        }

        var input = Tablets.Input;

        if (input is not { Available: true })
        {
            Say("ExileInput2 is not installed", true);
            return;
        }

        if (!InventoryItems(gc, settings).Any(i => !i.Kept))
        {
            Say("Nothing to deposit", false);
            return;
        }

        input.Release();

        if (!input.Take())
        {
            Say("Another plugin is using the cursor", true);
            return;
        }

        _shortestMs = settings.Automation.Autodeposit.ShortestMoveBetweenItemsMs.Value;
        Tablets.UseTolerance(Tablets.TabletTolerancePx);

        // For the moves the queue did not plan - a retry, a re-aim. The planned ones carry their own. See GiveQueue.
        input.SetShortestMove(_shortestMs);

        // The part of every move that grows with its length, for the whole run, planned moves included: set before the
        // queue is planned, which times each move with it. See AutodepositSettings.MoveTimePerRootPixelBetweenItemsMs.
        input.SetMoveTimePerRootPixel(settings.Automation.Autodeposit.MoveTimePerRootPixelBetweenItemsMs.Value);
        Queue.Clear();

        Sent.Clear();
        Pending.Clear();
        Retried.Clear();
        Log.Clear();
        _clicks = 0;
        _moves = 0;
        _retryAt = null;
        _phase = Phase.Choosing;
        _runStartedAt = DateTime.UtcNow;
        LogLine("run started");
        Choose(gc, settings);
    }

    /// <summary>
    /// The inventory's items, each with its rectangle, its cells and whether it is kept, read from the inventory panel's
    /// items and placed on the 12 by 5 grid by their rectangles against the grid's own.
    /// </summary>
    private static List<(Entity Item, long Key, RectangleF Rect, string Metadata, bool Stackable, bool Kept)> InventoryItems(
        GameController gc, AutoExpeditionSettings settings)
    {
        var found = new List<(Entity, long, RectangleF, string, bool, bool)>();
        var inventory = Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory], null);
        var grid = Safe.Read(inventory, static i => i.GetClientRect(), default);

        if (grid.Width <= 0f)
            return found;

        var cellWide = grid.Width / Columns;
        var cellHigh = grid.Height / Rows;
        var kept = KeptCells(settings);

        foreach (var slot in Safe.Read(inventory, static i => i.VisibleInventoryItems, null) ?? [])
        {
            var item = Safe.Read(slot, static s => s.Item, null);
            var rect = Safe.Read(slot, static s => s.GetClientRect(), default);

            if (item == null || rect.Width <= 0f)
                continue;

            var column = (int)MathF.Round((rect.Left - grid.Left) / cellWide);
            var row = (int)MathF.Round((rect.Top - grid.Top) / cellHigh);
            var wide = Math.Max(1, (int)MathF.Round(rect.Width / cellWide));
            var high = Math.Max(1, (int)MathF.Round(rect.Height / cellHigh));
            var isKept = false;

            for (var r = row; r < row + high && !isKept; r++)
            for (var c = column; c < column + wide && !isKept; c++)
                isKept = kept.Contains(r * Columns + c);

            found.Add((item, Safe.Read(item, static e => e.Address, 0L), rect,
                Safe.Read(item, static e => e.Metadata, "") ?? "",
                Safe.Read(item, static e => e.HasComponent<Stack>(), false), isKept));
        }

        return found;
    }

    /// <summary>The kept cells, as row * Columns + column. See AutodepositSettings.KeptInventoryCells.</summary>
    public static HashSet<int> KeptCells(AutoExpeditionSettings settings) =>
        (settings.Automation.Autodeposit.KeptInventoryCells.Value ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(word => int.TryParse(word, out var index) ? index : -1)
        .Where(index => index is >= 0 and < Columns * Rows)
        .ToHashSet();

    /// <summary>
    /// The next click, chosen from what the inventory shows now, as Tablets.MoveNext chooses: clicked items gone are let
    /// go of, one still shown MoveSettle after its click is sent back to be clicked again - once, then the run stops - and
    /// the nearest item not yet sent is sent. With none left to send, the run ends once nothing is pending.
    /// </summary>
    private static void Choose(GameController gc, AutoExpeditionSettings settings)
    {
        var items = InventoryItems(gc, settings);
        var shown = items.Select(i => i.Key).ToHashSet();
        var now = DateTime.UtcNow;

        foreach (var (key, clicked) in Pending.ToList())
        {
            if (!shown.Contains(key))
            {
                Pending.Remove(key);
                continue;
            }

            if (now - clicked < MoveSettle)
                continue;

            if (!Retried.Add(key))
            {
                Stop("Deposit stopped: an item did not move after two clicks - is the stash tab full, or one that does " +
                     "not take it?", true);
                return;
            }

            LogLine($"an item was still there {MoveSettle.TotalMilliseconds:0}ms after its click: clicking it again");
            Pending.Remove(key);
            Sent.Remove(key);
        }

        var left = items.Where(i => !i.Kept && !Sent.Contains(i.Key)).ToList();

        if (left.Count == 0)
        {
            if (Pending.Count == 0)
                Stop($"Deposited, {_clicks} click{(_clicks == 1 ? "" : "s")} in " +
                     $"{(DateTime.UtcNow - _runStartedAt).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s", false);

            return;
        }

        // The queue, planned again when it is empty or no longer fits what the inventory shows. An item to click that it
        // does not hold - one clicked again for still being there - is fitted into it where it adds least to the way,
        // rather than the whole being planned again from the cursor, which left it last and far off. See FitIntoQueue.
        var leftKeys = left.Select(i => i.Key).ToHashSet();

        if (Queue.Count == 0 || !Queue[0].Click.Takes.All(leftKeys.Contains))
            PlanQueue(gc, settings, items, left);
        else if (FitIntoQueue(gc, items, left))
            GiveQueue(settings);
        else if (_queueDropped)
            GiveQueue(settings);

        var (next, point, _) = Queue[0];
        Queue.RemoveAt(0);

        foreach (var key in next.Takes)
            Sent.Add(key);

        _sendRetried = false;
        Send(gc, next, point);
    }

    /// <summary>
    /// Plans every click still to make, from the cursor, by the rule every batch of clicks follows (ItemClicks.NextItemOf):
    /// each from the last one's landing point, the first of the run anywhere in its item and every later one just inside
    /// its edge facing the last. A stackable kind goes in one ctrl+right click, taking every stack of it, unless a kept
    /// cell holds one too, which the click would take as well; then each stack is its own ctrl+left click.
    /// </summary>
    private static void PlanQueue(GameController gc, AutoExpeditionSettings settings,
        List<(Entity Item, long Key, RectangleF Rect, string Metadata, bool Stackable, bool Kept)> items,
        List<(Entity Item, long Key, RectangleF Rect, string Metadata, bool Stackable, bool Kept)> left)
    {
        Queue.Clear();

        var keptKinds = items.Where(i => i.Kept && i.Stackable).Select(i => i.Metadata).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = left.ToList();
        var from = Cursor(gc);
        var first = _moves == 0;

        while (remaining.Count > 0)
        {
            var next = ItemClicks.NextItemOf(remaining, i => i.Rect, from, first);
            var name = Safe.Read(next.Item, static e => e.GetComponent<Base>()?.Name, "") ?? "an item";
            var right = next.Stackable && next.Metadata.Length > 0 && !keptKinds.Contains(next.Metadata);
            var taken = right
                ? remaining.Where(i => string.Equals(i.Metadata, next.Metadata, StringComparison.OrdinalIgnoreCase)).ToList()
                : [next];
            var point = first ? ItemClicks.AnywhereIn(next.Rect) : ItemClicks.NearEdgeOf(next.Rect, from);

            Queue.Add((new PlannedClick(next.Rect, right, name, taken.Select(i => i.Key).ToArray()), point, first));

            foreach (var item in taken)
                remaining.Remove(item);

            from = point;
            first = false;
        }

        LogLine($"planned {Queue.Count} click(s)");
        GiveQueue(settings);
    }

    /// <summary>
    /// Fits each item to click that the queue does not hold into the queue where it adds least to the way: between the two
    /// clicks it detours least from, or at the end. Its point is just inside its edge facing the click before it. True
    /// when anything was fitted, for the queue to be given to ExileInput2 again.
    /// </summary>
    private static bool FitIntoQueue(GameController gc,
        List<(Entity Item, long Key, RectangleF Rect, string Metadata, bool Stackable, bool Kept)> items,
        List<(Entity Item, long Key, RectangleF Rect, string Metadata, bool Stackable, bool Kept)> left)
    {
        var held = Queue.SelectMany(q => q.Click.Takes).ToHashSet();
        var missing = left.Where(i => !held.Contains(i.Key)).ToList();

        if (missing.Count == 0)
            return false;

        var keptKinds = items.Where(i => i.Kept && i.Stackable).Select(i => i.Metadata).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cursor = Cursor(gc);

        foreach (var item in missing)
        {
            if (held.Contains(item.Key))
                continue;

            var centre = new Vector2(item.Rect.Center.X, item.Rect.Center.Y);
            var at = Queue.Count;
            var least = float.MaxValue;

            for (var k = 0; k <= Queue.Count; k++)
            {
                var before = k == 0 ? cursor : Queue[k - 1].Point;
                var cost = k == Queue.Count
                    ? Vector2.Distance(before, centre)
                    : Vector2.Distance(before, centre) + Vector2.Distance(centre, Queue[k].Point) - Vector2.Distance(before, Queue[k].Point);

                if (cost < least)
                {
                    least = cost;
                    at = k;
                }
            }

            var right = item.Stackable && item.Metadata.Length > 0 && !keptKinds.Contains(item.Metadata);
            var takes = right
                ? missing.Where(i => string.Equals(i.Metadata, item.Metadata, StringComparison.OrdinalIgnoreCase) && !held.Contains(i.Key))
                    .Select(i => i.Key).ToArray()
                : [item.Key];
            var from = at == 0 ? cursor : Queue[at - 1].Point;
            var name = Safe.Read(item.Item, static e => e.GetComponent<Base>()?.Name, "") ?? "an item";

            Queue.Insert(at, (new PlannedClick(item.Rect, right, name, takes), ItemClicks.NearEdgeOf(item.Rect, from), false));

            foreach (var key in takes)
                held.Add(key);

            LogLine($"fitted {name} into the queue at {at + 1} of {Queue.Count}");
        }

        return true;
    }

    /// <summary>
    /// Gives ExileInput2 the queue, to draw while Show planned automation in hideout is on and to make as drawn: the run's first move at
    /// ExileInput2's own shortest move, every later one at Shortest move between items, as the tablet runs.
    /// </summary>
    private static void GiveQueue(AutoExpeditionSettings settings)
    {
        var between = _shortestMs;

        Tablets.Input.PlanClicks(Queue.Select(q => q.Click.Rect).ToArray(), Queue.Select(q => q.Point).ToArray(),
            Queue.Select(q => q.Click.Right).ToArray(), Queue.Select(_ => new[] { System.Windows.Forms.Keys.LControlKey }).ToArray(),
            Queue.Select(q => q.FirstOfRun ? -1 : between).ToArray(), settings.Automation.ShowPlannedAutomation.Value);
        _queueDropped = false;
    }

    /// <summary>
    /// Sends the cursor to a click's planned point, ctrl held, at Shortest move between items. A point that is not the
    /// queue's next - a retry anywhere in the item - is a move ExileInput2 did not plan, and it drops the queue it was
    /// given; the next choice gives it again.
    /// </summary>
    private static void Send(GameController gc, PlannedClick click, Vector2 point, bool planned = true)
    {
        var input = Tablets.Input;

        _current = click;
        _currentPlanned = planned;
        _arrivedAt = null;

        var aimed = input.MoveTo(point, ctrl: true);

        // The move's own times, set when it was asked for, for the log. See ExileInput2's LastMoveTimes.
        _moveTimes = input.LastMoveTimes();

        if (!aimed)
        {
            RetryOrStop(gc, $"could not aim at {click.Name}");
            return;
        }

        _moves++;
        _reaimed = false;
        _sentAt = DateTime.UtcNow;
        _phase = Phase.Moving;
        _deadline = _sentAt + MoveFor;
    }

    /// <summary>The click in hand tried once more after RetryWait, or the run stopped when it already was. See Tick.</summary>
    private static void RetryOrStop(GameController gc, string why)
    {
        if (_current == null || _sendRetried)
        {
            Stop($"Deposit stopped: {why}{(_sendRetried ? ", after one retry" : "")}", true);
            return;
        }

        _sendRetried = true;
        _retryAt = DateTime.UtcNow + RetryWait;
        _phase = Phase.Choosing;
        LogLine($"{why}: trying once more");
    }

    /// <summary>Steps a run. Called every tick.</summary>
    public static void Tick(GameController gc, AutoExpeditionSettings settings)
    {
        if (_phase == Phase.Idle)
        {
            _lastTickAt = DateTime.MinValue;
            return;
        }

        // A long gap between ticks during a run, for the log: a stall the player may see as the cursor jumping.
        var tickAt = DateTime.UtcNow;

        if (_lastTickAt != DateTime.MinValue && tickAt - _lastTickAt >= LongTick)
            LogLine($"a frame took {(tickAt - _lastTickAt).TotalMilliseconds:0}ms" + (_current != null ? $", moving to {_current.Name}" : ""));

        _lastTickAt = tickAt;

        var input = Tablets.Input;

        // **The game taking typed input pauses the run rather than ending it**, as the tablet runs: the cursor is handed
        // back until the focus clears, then taken again, and the run carries on from what the inventory shows.
        if (_phase == Phase.Paused)
        {
            if (Tablets.FocusedInput(gc) == null && input.Take())
            {
                Sent.Clear();
                Pending.Clear();
                Queue.Clear();
                _retryAt = null;
                _phase = Phase.Choosing;
                LogLine("carrying on");
            }
            else if (DateTime.UtcNow - _pausedSince > PausedFor)
                Stop("Deposit stopped: the game kept taking typed input", true);

            return;
        }

        if (!StashAndInventoryOpen(gc))
        {
            Stop("Deposit stopped: the stash or inventory closed", true);
            return;
        }

        if (input.Stopped() is { Length: > 0 } stopped)
        {
            LogLine($"ExileInput2 stopped ({stopped}) moving to {_current?.Name ?? "nothing"}, " +
                    $"{(DateTime.UtcNow - _sentAt).TotalMilliseconds:0}ms after it was sent" +
                    (input.StopDetail() is { Length: > 0 } detail ? $"; ExileInput2: {detail}" : ""));

            if (stopped == "GameIsTakingInput")
            {
                input.Release();
                _pausedSince = DateTime.UtcNow;
                _phase = Phase.Paused;
                return;
            }

            // A move that never landed is tried once more, as a failed click is; releasing clears ExileInput2's stop.
            if (stopped == "MoveNeverLanded" && _current != null && !_sendRetried)
            {
                input.Release();
                RetryOrStop(gc, $"the cursor did not land on {_current.Name}");
                return;
            }

            Stop("Deposit stopped: " + stopped switch
            {
                "PlayerMovedTheMouse" => "you moved the mouse",
                "TakenOver" => "an instant loot pickup took the cursor - press again",
                "PlayerPressedAKey" => "you pressed a key",
                "MoveNeverLanded" => "the cursor did not land, after one retry",
                _ => $"stopped: {stopped}",
            }, true);
            return;
        }

        // **The claim renewed every step**, as the tablet runs: ExileInput2 drops a claim nothing refreshes for its expiry,
        // and a wait for pending items calls nothing else.
        if (!input.Take())
        {
            if (input.Stopped() is not { Length: > 0 })
                Stop("Deposit stopped: another plugin took the cursor", true);

            return;
        }

        var now = DateTime.UtcNow;

        // A failed move or click waiting its turn to be tried once more, anywhere in its item. See RetryOrStop.
        if (_retryAt is { } retryAt)
        {
            if (now < retryAt)
                return;

            _retryAt = null;

            if (_current is { } again)
            {
                _queueDropped = true;
                Send(gc, again, ItemClicks.AnywhereIn(again.Rect), planned: false);
                return;
            }
        }

        if (_phase == Phase.Choosing)
        {
            Choose(gc, settings);
            return;
        }

        if (!input.Arrived())
        {
            if (now >= _deadline)
                RetryOrStop(gc, $"the cursor did not reach {_current.Name}");

            return;
        }

        _arrivedAt ??= now;

        // Clicked only with the cursor inside the item: arrived within the tolerance of a point near its edge, the cursor
        // may sit on the neighbour. Aimed again once anywhere in it, then the run stops. Not for a planned click: its
        // point is inside the item by construction, and ExileInput2 counts it arrived once the cursor is on it, before
        // the game's report of the cursor - which this check reads - has caught up.
        if (!_currentPlanned && !ItemClicks.CursorInside(gc, _current.Rect))
        {
            if (_reaimed || !input.MoveTo(ItemClicks.AnywhereIn(_current.Rect), ctrl: true))
            {
                Stop($"Deposit stopped: the cursor did not reach {_current.Name}", true);
                return;
            }

            _queueDropped = true;
            _reaimed = true;
            _deadline = now + MoveFor;
            return;
        }

        if (!input.Click(ctrl: true, right: _current.Right))
        {
            RetryOrStop(gc, $"the click on {_current.Name} did not go");
            return;
        }

        _clicks++;

        foreach (var key in _current.Takes)
            Pending[key] = now;

        var cursor = Cursor(gc);

        LogLine($"{(_current.Right ? "ctrl+right" : "ctrl+left")} {_current.Name}{(_current.Takes.Length > 1 ? $" ({_current.Takes.Length} stacks)" : "")}" +
                $"{(_currentPlanned ? "" : " (not planned)")}: " +
                (_moveTimes is { } times ? $"waited {times.WaitMs:0}ms for the button, moved {times.TravelMs:0}ms{ItemClicks.FlourishSaid(times)}, " : "") +
                $"arrived {((_arrivedAt ?? now) - _sentAt).TotalMilliseconds:0}ms and clicked {(now - _sentAt).TotalMilliseconds:0}ms " +
                $"after it was asked for, at ({cursor.X:0},{cursor.Y:0}) in " +
                $"({_current.Rect.X:0},{_current.Rect.Y:0} {_current.Rect.Width:0}x{_current.Rect.Height:0})");

        _phase = Phase.Choosing;
        Choose(gc, settings);
    }

    private static void Stop(string text, bool failed)
    {
        _phase = Phase.Idle;
        _current = null;
        _retryAt = null;
        Queue.Clear();

        if (Tablets.Input is { } input)
        {
            input.SetShortestMove(-1);
            input.SetMoveTimePerRootPixel(-1);
            Tablets.UseTolerance(Tablets.OtherTolerancePx);
            input.Release();
        }

        LogLine(text);
        Say(text, failed);
    }

    private static void Say(string text, bool failed) => _note = (text, failed, DateTime.UtcNow);

    private static void LogLine(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");

        if (Log.Count > LogKept)
            Log.RemoveAt(0);
    }

    /// <summary>The run's state and its log, for the dump.</summary>
    public static void Describe(System.Text.StringBuilder b, GameController gc, AutoExpeditionSettings settings)
    {
        b.AppendLine("=== autodeposit ===");
        b.AppendLine($"  enabled {settings.Automation.Autodeposit.EnableAutodeposit.Value}, phase {_phase}, zone {ZoneRect(gc)}, " +
                     $"stash and inventory open {StashAndInventoryOpen(gc)}; kept cells " +
                     $"{(KeptCells(settings) is { Count: > 0 } kept ? string.Join(",", kept.OrderBy(i => i)) : "none")}");
        b.AppendLine($"  last said: {(_note.Text.Length > 0 ? $"\"{_note.Text}\"" : "nothing since the plugin loaded")}");
        b.AppendLine($"  the last run's {Log.Count} decisions and moves, newest last:");

        foreach (var line in Log)
            b.AppendLine($"    {line}");

        b.AppendLine();
    }

    /// <summary>
    /// The Deposit zone and its word, the cells it would empty outlined while it is hovered, and the run's progress or last
    /// outcome under it.
    /// </summary>
    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        if (!settings.Automation.Enable.Value || !settings.Automation.Autodeposit.EnableAutodeposit.Value ||
            ZoneRect(gc) is not { Width: > 0f } zone)
            return;

        var hovered = Contains(zone, Cursor(gc));

        graphics.DrawBox(zone, Color.FromArgb(hovered ? 120 : 70, 0, 0, 0));
        graphics.DrawFrame(zone, Color.FromArgb(hovered ? 200 : 90, 200, 200, 200), 1);

        const string word = "Deposit";
        var size = graphics.MeasureText(word);

        _wordWidth = size.X;

        // Centred by the font renderer itself, both ways: placing the top-left by MeasureText's height set the word off
        // centre vertically, the measured height not being the drawn glyphs'.
        graphics.DrawText(word, new Vector2(zone.Center.X, zone.Center.Y), Color.FromArgb(hovered ? 255 : 200, 230, 230, 230),
            ExileCore2.Shared.Enums.FontAlign.Center | ExileCore2.Shared.Enums.FontAlign.VerticalCenter);

        if (hovered && !Running)
            OutlineDepositedCells(graphics, gc, settings);

        var text = Running ? $"Depositing{(_phase == Phase.Paused ? " - paused while the game takes typed input" : _clicks > 0 ? $", {_clicks} clicked" : "")}"
            : DateTime.UtcNow - _note.At < NoteFor ? _note.Text : "";

        if (text.Length == 0)
            return;

        var noteSize = graphics.MeasureText(text);

        graphics.DrawTextWithBackground(text, new Vector2(zone.Right - noteSize.X, zone.Bottom + 4f),
            !Running && _note.Failed ? CursorWarning.Warning : Color.White, Color.FromArgb(200, 0, 0, 0));
    }

    /// <summary>Faint outlines over the inventory cells the Deposit zone would empty: every cell not kept.</summary>
    private static void OutlineDepositedCells(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        var grid = Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].GetClientRect(), default);

        if (grid.Width <= 0f)
            return;

        var cellWide = grid.Width / Columns;
        var cellHigh = grid.Height / Rows;
        var kept = KeptCells(settings);

        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            if (kept.Contains(row * Columns + column))
                continue;

            graphics.DrawFrame(new RectangleF(grid.Left + column * cellWide + 2f, grid.Top + row * cellHigh + 2f, cellWide - 4f, cellHigh - 4f),
                Color.FromArgb(110, 255, 255, 255), 1);
        }
    }
}
