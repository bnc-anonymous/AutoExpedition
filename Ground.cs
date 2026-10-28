using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// The ground labels, read in one place.
///
/// **Twelve call sites asked the game for this list independently, several of them per frame and
/// one of them inside a loop.** It is not a cheap property - ExileCore2 rebuilds the list from
/// memory each time - and it is the read that writes "Element with index: 0 not found. Indices:
/// [1] [8] [0] [1] [0]" into the log, because the second label container this client leaves empty
/// sits on that path. So how often it is asked is worth knowing, and worth being able to change in
/// one place rather than twelve.
/// </summary>
internal static class Ground
{
    /// <summary>
    /// Marks the calling thread as the one Tick and Render run on, the only one served from the shared read. Called at
    /// the start of each. They were one thread in the dump of 2026-10-11 16:02:24.
    /// </summary>
    public static void NextFrame() => _owner = Environment.CurrentManagedThreadId;

    /// <summary>
    /// The list must be read again: an item entity arrived or went, or the area changed. Called from the plugin's
    /// EntityAdded and EntityRemoved for WorldItem entities, so a drop is in the next read - not up to RefreshEveryMs late.
    /// </summary>
    public static void Changed()
    {
        _changed = true;
        FastRefreshNow();
        ChangesSeen++;
    }

    /// <summary>An instant loot click went out: the list is read every FastRefreshEveryMs for FastRefreshForMs. See there.</summary>
    public static void FastRefreshNow() => _rereadUntil = Environment.TickCount64 + Math.Max(0, FastRefreshForMs);

    /// <summary>
    /// An item entity arrived: noted with the time, for the dump to set against its first look (see
    /// Autoloot.ListedItemTimings), and the list read again as Changed does.
    /// </summary>
    public static void ItemArrived(uint id)
    {
        lock (ArrivalsOfItems)
        {
            if (ArrivalsOfItems.Count > 2000)
                ArrivalsOfItems.Clear();

            ArrivalsOfItems[id] = DateTime.UtcNow;
        }

        Changed();
    }

    /// <summary>When each item entity arrived this area, by id, or null. See ItemArrived.</summary>
    public static DateTime? ArrivalOfItem(uint id)
    {
        lock (ArrivalsOfItems)
            return ArrivalsOfItems.TryGetValue(id, out var at) ? at : null;
    }

    private static readonly Dictionary<uint, DateTime> ArrivalsOfItems = new();

    /// <summary>
    /// An item entity left ExileCore2's entity list - picked up by anyone, or streamed out: noted with the time, for the
    /// dump to set against the label going and the inventory filling (see Autoloot.ObserveClickTargets), and the list
    /// read again as Changed does.
    /// </summary>
    public static void ItemDeparted(uint id)
    {
        lock (DeparturesOfItems)
        {
            if (DeparturesOfItems.Count > 2000)
                DeparturesOfItems.Clear();

            DeparturesOfItems[id] = DateTime.UtcNow;
        }

        Changed();
    }

    /// <summary>When each item entity left the entity list this area, by id, or null. See ItemDeparted.</summary>
    public static DateTime? DepartureOfItem(uint id)
    {
        lock (DeparturesOfItems)
            return DeparturesOfItems.TryGetValue(id, out var at) ? at : null;
    }

    private static readonly Dictionary<uint, DateTime> DeparturesOfItems = new();

    /// <summary>
    /// **Read every FastRefreshEveryMs for this long after an item arrives or goes, or an instant loot click, not only
    /// once.** An item's entity can arrive before its label is in the list, so the one read the arrival asks for can miss
    /// it, and it was then seen only at the next RefreshEveryMs: of two stacks dropped together, one was clicked at once
    /// and the other sat about half a second (the player's account, 2026-10-20; suspected, not yet seen in a dump -
    /// ItemArrived records each arrival against the item's first look to settle it). Set each tick from
    /// AutolootSettings.FastGroundLabelRefreshAfterDropOrPickupMs.
    /// </summary>
    public static int FastRefreshForMs { get; set; } = 1000;

    /// <summary>How often the list is read while FastRefreshForMs runs. Chosen: about two rendered frames.</summary>
    private const int FastRefreshEveryMs = 33;

    private static long _rereadUntil;

    /// <summary>
    /// **The list is read when it changes, not every frame.** One read builds every label afresh - 555 of them, 0.65ms
    /// and 0.95MB (dump 2026-10-11 16:05:38) - and the list itself changes only when something drops or goes, while what
    /// moves every frame is where the labels are drawn, which each label's element reads live. So the list is read on
    /// Changed, and every RefreshEveryMs regardless, in case a change came without an event. Set each tick from
    /// AutolootSettings.GroundLabelRefreshMs; 500 until 2026-10-20.
    /// </summary>
    public static int RefreshEveryMs { get; set; } = 198;

    /// <summary>
    /// How long one look at which item labels are visible serves every caller. 10ms, under a rendered frame, made the
    /// look nearly every frame - 0.62ms and 453KB a frame with 394 labels in the list (dump 2026-10-12 00:59:11) - for
    /// what changes as fast as the camera turns. Where each visible label is drawn is read live by its callers.
    /// </summary>
    private const int ViewEveryMs = 30;

    /// <summary>Counts the full reads of the list, for caches of what it holds - ids, elements - to key on.</summary>
    public static long ReadNumber { get; private set; }

    /// <summary>Counts the looks at what is visible, for caches of what is drawn where to key on. See ViewEveryMs.</summary>
    public static long ViewNumber { get; private set; }

    /// <summary>Changed calls, and full reads by cause, for the dump.</summary>
    public static long ChangesSeen { get; private set; }

    /// <inheritdoc cref="ChangesSeen"/>
    public static long ReadsOnChange { get; private set; }

    /// <inheritdoc cref="ChangesSeen"/>
    public static long ReadsOnRefresh { get; private set; }

    /// <summary>Whether the caller is the thread the shared read serves. Any other reads afresh.</summary>
    public static bool OnOwnerThread => Environment.CurrentManagedThreadId == _owner;

    private static int _owner = -1;
    private static volatile bool _changed = true;
    // Zero, not long.MinValue: now minus long.MinValue overflows to a negative number, so "ViewEveryMs since the last
    // look" never came true, ViewNumber stayed 0 and every cache keyed on it kept its first answer - the autoloot lists
    // empty and the label rectangles where they first were (dump 2026-10-11 16:10:00). Environment.TickCount64 counts
    // from boot, so 0 is always long enough ago.
    private static long _readMs;
    private static long _viewMs;
    private static IList<LabelOnGround> _labels;
    private static Dictionary<uint, LabelOnGround> _byId;
    private static List<(LabelOnGround Label, Entity Item)> _itemLabels;
    private static List<(LabelOnGround Label, Entity Item)> _visibleItems;

    /// <summary>The ground labels: read again on Changed or every RefreshEveryMs on the owner thread, afresh on any other.</summary>
    public static IList<LabelOnGround> Labels(GameController gc)
    {
        if (!OnOwnerThread)
            return Read(gc);

        var now = Environment.TickCount64;
        var stale = now - _readMs >= RefreshEveryMs ||
                    (now < _rereadUntil && now - _readMs >= FastRefreshEveryMs);

        if (_labels == null || _changed || stale)
        {
            if (_changed)
                ReadsOnChange++;
            else
                ReadsOnRefresh++;

            _changed = false;
            _labels = Read(gc);
            _readMs = now;
            _byId = null;
            _itemLabels = null;
            _visibleItems = null;
            ReadNumber++;
        }

        if (now - _viewMs >= ViewEveryMs)
        {
            _viewMs = now;
            _visibleItems = null;
            ViewNumber++;
        }

        return _labels;
    }

    /// <summary>
    /// The visible labels of items on the ground, with each item's ground entity: which labels hold items is worked out
    /// once per read, and which of those are visible once per look. Of 555 labels, 35 were visible (dump 2026-10-11
    /// 16:05:38).
    /// </summary>
    public static List<(LabelOnGround Label, Entity Item)> VisibleItems(GameController gc)
    {
        var labels = Labels(gc);

        if (OnOwnerThread && _visibleItems != null)
            return _visibleItems;

        var items = OnOwnerThread && _itemLabels != null ? _itemLabels : ItemLabelsOf(labels);

        if (OnOwnerThread)
            _itemLabels = items;

        var found = new List<(LabelOnGround, Entity)>();

        // **A label's own flag, and its parent's visibility once per parent**, rather than each label's full visibility,
        // which reads up the chain of parents and builds an object at each step: nearly every label shares one parent,
        // and the labels are its children. Taken to be the same answer - LabelOnGround.IsVisible is not readable in the
        // HUD's assembly - and the dump compares the two counts. See VisibleCountsSaid.
        var parentShown = new Dictionary<long, bool>();

        foreach (var (label, item) in items)
        {
            var element = Safe.Read(label, static l => l.Label, null);

            if (element == null || !Safe.Read(element, static e => e.IsVisibleLocal, false))
                continue;

            var parent = Safe.Read(element, static e => e.Parent, null);
            var key = Safe.Read(parent, static p => p.Address, 0L);

            if (!parentShown.TryGetValue(key, out var shown))
                parentShown[key] = shown = parent == null || Safe.Read(parent, static p => p.IsVisible, false);

            if (shown)
                found.Add((label, item));
        }

        if (OnOwnerThread)
            _visibleItems = found;

        return found;
    }

    /// <summary>
    /// The visible item labels counted both ways - the label's own full visibility, and the way VisibleItems now asks - for
    /// the dump, so a difference between them shows.
    /// </summary>
    public static string VisibleCountsSaid(GameController gc)
    {
        var labels = Labels(gc);
        var items = ItemLabelsOf(labels);
        var full = 0;

        foreach (var (label, _) in items)
        {
            if (Safe.Read(label, static l => l.IsVisible, false))
                full++;
        }

        return $"visible item labels: {full} by each label's full visibility, {VisibleItems(gc).Count} as the looting reads them";
    }

    /// <summary>The labels in a read that belong to items on the ground, with each item's entity.</summary>
    private static List<(LabelOnGround, Entity)> ItemLabelsOf(IList<LabelOnGround> labels)
    {
        var found = new List<(LabelOnGround, Entity)>();

        foreach (var label in labels ?? [])
        {
            var item = Safe.Read(label, static l => l.ItemOnGround, null);

            if (item != null && Safe.Read(item, static e => e.HasComponent<ExileCore2.PoEMemory.Components.WorldItem>(), false))
                found.Add((label, item));
        }

        return found;
    }

    /// <summary>The label whose ground entity has this id, from the shared read, or null. The first, when several.</summary>
    public static LabelOnGround LabelOf(GameController gc, uint id)
    {
        var labels = Labels(gc);

        if (labels == null || id == 0u)
            return null;

        if (!OnOwnerThread)
        {
            foreach (var label in labels)
            {
                if (Safe.Read(label, static l => l.ItemOnGround?.Id ?? 0u, 0u) == id)
                    return label;
            }

            return null;
        }

        if (_byId == null)
        {
            _byId = new Dictionary<uint, LabelOnGround>(labels.Count);

            foreach (var label in labels)
                _byId.TryAdd(Safe.Read(label, static l => l.ItemOnGround?.Id ?? 0u, 0u), label);
        }

        return _byId.GetValueOrDefault(id);
    }

    private static IList<LabelOnGround> Read(GameController gc) =>
        Safe.Read(() => gc.IngameState.IngameUi.ItemsOnGroundLabels, null);
}
