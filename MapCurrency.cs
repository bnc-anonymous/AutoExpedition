using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.Shared.Enums;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoExpedition;

/// <summary>
/// The currency that has dropped in the current map, in exalts, for the score area's Currency line. See
/// ScoreAreaSettings.DrawCurrency.
///
/// Every item of the game's StackableCurrency class that appears on the ground is priced once, by entity id, with
/// NinjaPricer's price for the stack, and counted where one of the stack is worth at least Currency worth at least. From
/// entering the map, so loot from monsters killed before an expedition counts, and across both sites of an ordinary
/// map. A hideout or town in between keeps the total; another map starts it again.
///
/// An item is counted when it is seen: one that dropped out of the game's range is counted when the player comes near
/// it. Currency the player drops from their own inventory is counted too, as the game does not say who dropped an
/// item. An item NinjaPricer has no price for within PricedWithin is left out.
///
/// **What the player did not pick up** - taken by other players in a party, or left on the ground - is the total less
/// what the player's inventory gained in currency by the same rule. The inventory is valued on entering the map, once
/// NinjaPricer has its prices, and every InventoryReadEvery after; a trip to the hideout banks what was gained so far
/// and values it again on coming back, so a deposit there is not a loss. Currency spent from the inventory in the map
/// reads as not picked up.
/// </summary>
internal static class MapCurrency
{
    /// <summary>How long an item is asked about before it is left out unpriced. Chosen: NinjaPricer answers in seconds.</summary>
    private static readonly TimeSpan PricedWithin = TimeSpan.FromSeconds(30);

    /// <summary>The total so far, in exalts.</summary>
    internal static double Exalts { get; private set; }

    /// <summary>The stacks counted in the total.</summary>
    internal static int Stacks { get; private set; }

    /// <summary>How often the inventory's currency is valued. Chosen.</summary>
    private static readonly TimeSpan InventoryReadEvery = TimeSpan.FromSeconds(1);

    /// <summary>The inventory's currency on entering the map this time, in exalts; null before it has been valued.</summary>
    private static double? _inventoryOnEntering;

    /// <summary>The inventory's currency at its last valuing in the map, in exalts.</summary>
    private static double _inventoryLast;

    /// <summary>What the inventory gained in currency on earlier visits to this map, in exalts.</summary>
    private static double _pickedUpEarlierVisits;

    private static DateTime _inventoryReadAt;

    /// <summary>The currency the player has picked up in this map so far, in exalts, or null before the inventory is valued.</summary>
    internal static double? PickedUpExalts =>
        _inventoryOnEntering is { } entering ? _pickedUpEarlierVisits + _inventoryLast - entering : null;

    /// <summary>The map the total belongs to.</summary>
    private static uint _area;

    /// <summary>Every item already decided, counted or not, by entity id.</summary>
    private static readonly HashSet<uint> Decided = new();

    /// <summary>Items seen and not yet decided, with when each was first seen.</summary>
    private static readonly Dictionary<uint, (Entity OnGround, DateTime Since)> Undecided = new();

    /// <summary>An item on the ground has appeared. Called from EntityAdded for each WorldItem.</summary>
    internal static void ItemArrived(Entity onGround)
    {
        var id = Safe.Read(onGround, static e => e.Id, 0u);

        if (id != 0u && !Decided.Contains(id))
            Undecided.TryAdd(id, (onGround, DateTime.UtcNow));
    }

    /// <summary>Starts a new total on entering another map, and prices what has appeared. Called each tick.</summary>
    internal static void Tick(GameController gc, AutoExpeditionSettings settings)
    {
        var area = Safe.Read(gc, static g => g.Area.CurrentArea, null);

        if (area == null || Safe.Read(area, static a => a.IsHideout || a.IsTown, false))
        {
            Undecided.Clear();

            // Leaving the map: what the inventory gained is banked, and it is valued afresh on coming back.
            if (_inventoryOnEntering is { } entering)
            {
                _pickedUpEarlierVisits += _inventoryLast - entering;
                _inventoryOnEntering = null;
            }

            return;
        }

        var hash = Safe.Read(area, static a => a.Hash, 0u);

        if (hash != _area)
        {
            _area = hash;
            Exalts = 0d;
            Stacks = 0;
            Decided.Clear();
            Undecided.Clear();
            _inventoryOnEntering = null;
            _pickedUpEarlierVisits = 0d;
        }

        if (!settings.Display.ScoreArea.DrawCurrency)
            return;

        var chaos = Autoloot.Valuation is { } valuation && valuation.Converts("Chaos") ? valuation.PerExalt("Chaos") : 0d;
        var leastExalts = settings.Display.ScoreArea.CurrencyAtLeastChaos.Value * chaos;

        // Without a chaos rate the least cannot be worked out, and an item decided now could be decided wrongly.
        if (chaos <= 0d)
            return;

        // Valued only once NinjaPricer has its prices: a valuing at entry before then reads the inventory as worth
        // nothing, and all of it would later read as picked up.
        if (settings.Display.ScoreArea.DrawCurrencyNotPickedUp && DateTime.UtcNow - _inventoryReadAt >= InventoryReadEvery &&
            Autoloot.Valuation?.Unpriced() == null)
        {
            _inventoryReadAt = DateTime.UtcNow;

            if (InventoryCurrencyExalts(gc, leastExalts) is { } inventory)
            {
                _inventoryOnEntering ??= inventory;
                _inventoryLast = inventory;
            }
        }

        foreach (var (id, (onGround, since)) in new List<KeyValuePair<uint, (Entity, DateTime)>>(Undecided))
        {
            var itemClass = Autoloot.ItemClassOfItem(gc, onGround);

            if (itemClass.Length > 0 && itemClass != "StackableCurrency")
            {
                Decide(id);
                continue;
            }

            var price = itemClass.Length > 0 ? Autoloot.ExaltsOfItem(gc, onGround) : 0d;

            if (price <= 0d)
            {
                if (DateTime.UtcNow - since > PricedWithin)
                    Decide(id);

                continue;
            }

            var item = Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);
            var stack = Math.Max(1, Safe.Read(item, static e => e.GetComponent<Stack>()?.Size ?? 1, 1));

            if (price / stack >= leastExalts)
            {
                Exalts += price;
                Stacks++;
            }

            Decide(id);
        }
    }

    /// <summary>
    /// The currency in the player's main inventory, in exalts, by the same rule as the drops: StackableCurrency whose
    /// one item is worth at least leastExalts; null when the inventory cannot be read.
    /// </summary>
    private static double? InventoryCurrencyExalts(GameController gc, double leastExalts)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var slots = Safe.Read(Safe.Read(main, static m => m.Inventory, null), static i => i.InventorySlotItems, null);

        if (slots == null || Autoloot.Valuation is not { } valuation)
            return null;

        var total = 0d;

        foreach (var slot in slots)
        {
            var item = Safe.Read(slot, static s => s.Item, null);
            var metadata = Safe.Read(item, static e => e.Metadata, "") ?? "";

            if (metadata.Length == 0 ||
                Safe.Read(() => gc.Files.BaseItemTypes.Translate(metadata)?.ClassName, "") != "StackableCurrency")
                continue;

            var price = valuation.ExaltsOfItem(item);
            var stack = Math.Max(1, Safe.Read(item, static e => e.GetComponent<Stack>()?.Size ?? 1, 1));

            if (price > 0d && price / stack >= leastExalts)
                total += price;
        }

        return total;
    }

    private static void Decide(uint id)
    {
        Decided.Add(id);
        Undecided.Remove(id);
    }

    /// <summary>
    /// The score area's line, "Currency: 4.5d", with what the player did not pick up where notPickedUp asks -
    /// "Currency: 4.5d (-1.2d)" - once the inventory has been valued; null without a divine rate. Not picked up is never
    /// shown below 0, which the inventory gaining more than was counted dropping would give, and not shown at all while it
    /// rounds to 0.
    /// </summary>
    internal static string Line(bool notPickedUp)
    {
        var divine = Valuation.DivineInExalts;

        if (divine <= 0d)
            return null;

        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Currency: {Exalts / divine:0.#}d");

        // Left off while it would read "(-0d)": nothing missed yet, or less than shows at one decimal place.
        var missed = notPickedUp && PickedUpExalts is { } picked ? Math.Round(Math.Max(0d, Exalts - picked) / divine, 1) : 0d;

        return missed > 0d
            ? line + string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (-{missed:0.#}d)")
            : line;
    }

    /// <summary>For the dump.</summary>
    internal static string Said =>
        $"{Exalts:0.#}ex in {Stacks} stack(s) counted, {Decided.Count} item(s) decided, {Undecided.Count} waiting, area {_area}; " +
        $"inventory {(_inventoryOnEntering is { } entering ? $"{entering:0.#}ex on entering, {_inventoryLast:0.#}ex now" : "not valued yet")}, " +
        $"{_pickedUpEarlierVisits:0.#}ex gained on earlier visits";
}
