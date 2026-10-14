using System;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Whether an item on the ground has room in the player's inventory, read from the game's own record of it
/// (ServerData.PlayerInventories, MainInventory1), which holds with the inventory panel closed. For looting: an item with
/// no room is not clicked, rather than clicked, walked to and missed.
///
/// A stackable item fits while a stack of the same item carried is short of its most, whatever cells are free: four
/// Chaos Orbs carried take two more with the inventory otherwise full. Any other item needs a free place its size, with
/// the cells asked to be kept free still free after it. See Fits.
/// </summary>
internal static class InventorySpace
{
    /// <summary>
    /// The inventory as last read: which cells are taken, and each carried stack's metadata, size, most and top left
    /// cell.
    /// </summary>
    private sealed record Snapshot(bool[,] Taken, int Columns, int Rows, int Free,
        (string Metadata, int Size, int Most, int X, int Y)[] Stacks);

    private static Snapshot _snapshot;
    private static DateTime _readAt;

    /// <summary>How long one read of the inventory is used for. Chosen: a frame or so, as a loot run asks once an item.</summary>
    private static readonly TimeSpan ReadFor = TimeSpan.FromMilliseconds(100);

    /// <summary>The inventory now, read at most every ReadFor; null when the game's record cannot be read.</summary>
    private static Snapshot Read(GameController gc)
    {
        if (_snapshot != null && DateTime.UtcNow - _readAt < ReadFor)
            return _snapshot;

        _readAt = DateTime.UtcNow;
        _snapshot = null;

        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var inventory = Safe.Read(main, static m => m.Inventory, null);
        var columns = Safe.Read(inventory, static i => i.Columns, 0);
        var rows = Safe.Read(inventory, static i => i.Rows, 0);

        if (columns <= 0 || rows <= 0)
            return null;

        var taken = new bool[columns, rows];
        var slots = Safe.Read(inventory, static i => i.InventorySlotItems, null) ?? [];
        var stacks = new System.Collections.Generic.List<(string, int, int, int, int)>();

        foreach (var slot in slots)
        {
            var x = Safe.Read(slot, static s => s.PosX, -1);
            var y = Safe.Read(slot, static s => s.PosY, -1);
            var w = Math.Max(1, Safe.Read(slot, static s => s.SizeX, 1));
            var h = Math.Max(1, Safe.Read(slot, static s => s.SizeY, 1));

            for (var i = Math.Max(0, x); i < Math.Min(columns, x + w); i++)
            for (var j = Math.Max(0, y); j < Math.Min(rows, y + h); j++)
                taken[i, j] = true;

            var item = Safe.Read(slot, static s => s.Item, null);
            var most = Safe.Read(item, static e => e.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0, 0);

            if (most > 1)
                stacks.Add((Safe.Read(item, static e => e.Metadata, "") ?? "", Safe.Read(item, static e => e.GetComponent<Stack>()?.Size ?? 0, 0),
                    most, x, y));
        }

        var free = 0;

        foreach (var cell in taken)
            if (!cell)
                free++;

        _snapshot = new Snapshot(taken, columns, rows, free, stacks.ToArray());
        return _snapshot;
    }

    /// <summary>
    /// Whether an item lying on the ground can be picked up with keepFree inventory cells left free after it: true when a
    /// carried stack of it has room, or a free place its size leaves keepFree cells. True when the inventory or the item
    /// cannot be read, so a failed read never holds a pickup back.
    /// </summary>
    public static bool Fits(GameController gc, Entity onGround, int keepFree)
    {
        if (Read(gc) is not { } inventory)
            return true;

        var item = Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);

        if (item == null)
            return true;

        var most = Safe.Read(item, static e => e.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0, 0);

        if (most > 1)
        {
            var metadata = Safe.Read(item, static e => e.Metadata, "") ?? "";

            if (metadata.Length > 0 && inventory.Stacks.Any(s => s.Size < s.Most && string.Equals(s.Metadata, metadata, StringComparison.Ordinal)))
                return true;
        }

        var w = Math.Max(1, Safe.Read(item, static e => e.GetComponent<Base>()?.ItemCellsSizeX ?? 1, 1));
        var h = Math.Max(1, Safe.Read(item, static e => e.GetComponent<Base>()?.ItemCellsSizeY ?? 1, 1));

        return inventory.Free - w * h >= Math.Max(0, keepFree) && PlaceFor(inventory, w, h);
    }

    /// <summary>
    /// The screen rectangle of a free place w by h cells in the inventory, the first by column from the top left, from the
    /// inventory panel's rectangle divided by the game's columns and rows; null when the panel is not showing or no place
    /// is free.
    /// </summary>
    public static RectangleF? FreePlaceOnScreen(GameController gc, int w, int h)
    {
        if (Read(gc) is not { } inventory || GridOnScreen(gc) is not { } grid)
            return null;

        var cellWide = grid.Width / inventory.Columns;
        var cellHigh = grid.Height / inventory.Rows;

        for (var x = 0; x + w <= inventory.Columns; x++)
        for (var y = 0; y + h <= inventory.Rows; y++)
        {
            var clear = true;

            for (var i = x; i < x + w && clear; i++)
            for (var j = y; j < y + h && clear; j++)
                clear = !inventory.Taken[i, j];

            if (clear)
                return new RectangleF(grid.Left + x * cellWide, grid.Top + y * cellHigh, w * cellWide, h * cellHigh);
        }

        return null;
    }

    /// <summary>
    /// The screen rectangle of the top left cell of a carried stack of metadata with room for count more, the first as
    /// the game lists them; null when the panel is not showing or no such stack is carried. A stack of the same item
    /// clicked onto it joins it.
    /// </summary>
    public static RectangleF? StackWithRoomOnScreen(GameController gc, string metadata, int count)
    {
        if (string.IsNullOrEmpty(metadata) || Read(gc) is not { } inventory || GridOnScreen(gc) is not { } grid)
            return null;

        foreach (var stack in inventory.Stacks)
        {
            if (stack.Size + count > stack.Most || stack.X < 0 || stack.Y < 0 || !string.Equals(stack.Metadata, metadata, StringComparison.Ordinal))
                continue;

            var cellWide = grid.Width / inventory.Columns;
            var cellHigh = grid.Height / inventory.Rows;

            return new RectangleF(grid.Left + stack.X * cellWide, grid.Top + stack.Y * cellHigh, cellWide, cellHigh);
        }

        return null;
    }

    /// <summary>The inventory grid's rectangle on screen, or null when the panel is not showing.</summary>
    private static RectangleF? GridOnScreen(GameController gc)
    {
        var grid = Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].GetClientRect(), default);

        return grid.Width <= 0f || !Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false) ? null : grid;
    }

    /// <summary>Whether a free place w by h cells is anywhere in the inventory.</summary>
    private static bool PlaceFor(Snapshot inventory, int w, int h)
    {
        for (var x = 0; x + w <= inventory.Columns; x++)
        for (var y = 0; y + h <= inventory.Rows; y++)
        {
            var clear = true;

            for (var i = x; i < x + w && clear; i++)
            for (var j = y; j < y + h && clear; j++)
                clear = !inventory.Taken[i, j];

            if (clear)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The inventory cells a loot run that is not instant loot leaves free for instant loot: Inventory slots kept for
    /// instant loot while instant loot is on and a party member is in the area, else none. See
    /// AutolootSettings.InventorySlotsKeptForInstantLoot.
    /// </summary>
    public static int KeptForInstantLoot(GameController gc, AutoExpeditionSettings settings) =>
        settings.Automation.Enable.Value && settings.Automation.Autoloot.EnableInstantAutoloot && Autoloot.PartyInArea(gc)
            ? settings.Automation.Autoloot.InventorySlotsKeptForInstantLoot.Value
            : 0;

    /// <summary>The inventory's space, for the dump.</summary>
    public static string Said(GameController gc) =>
        Read(gc) is { } inventory
            ? $"{inventory.Free} of {inventory.Columns * inventory.Rows} cells free, {inventory.Stacks.Count(s => s.Size < s.Most)} " +
              $"stack(s) carried with room (read from the game's record of the inventory, panel open or not)"
            : "unreadable";
}
