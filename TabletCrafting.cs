using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// The currency crafting the tablets seen would take, against the currency held: "Exalt: 80/195", have over need, in
/// green when there is enough and red when not, under the pricing progress line.
///
/// Need is each 10-use tablet taken to part-finished, by the player's account of the crafts (2026-10-09): a
/// Transmutation adds exactly one modifier and an Alchemy makes a four-modifier rare. So a normal tablet takes a
/// Transmutation and an Augmentation (what follows, a Regal and an Exalt or an Alchemy, is not counted); a magic one
/// with one modifier an Augmentation; a full magic one at the Regal threshold a Regal and an Exalt, under it an Alchemy;
/// a rare one marked for an Exalt an Exalt (see ActionOf), any other rare nothing.
///
/// Tablets are counted from every stash tab and sub-tab seen this session, cell by cell, and the inventory as it is
/// now. A tablet moved out of a stash cell still counts there until that sub-tab is seen again.
///
/// Have is the inventory's stacks, from the game's own record of it (ServerData), and the currency tab's as last seen -
/// a tab is only read while it is the one showing.
/// </summary>
internal static partial class Tablets
{
    /// <summary>The crafting currencies, as their lines read, each with the item's base name.</summary>
    private static readonly (string Line, string BaseName)[] CraftingCurrencies =
    [
        ("Transmutation", "Orb of Transmutation"),
        ("Augmentation", "Orb of Augmentation"),
        ("Regal", "Regal Orb"),
        ("Alchemy", "Orb of Alchemy"),
        ("Exalt", "Exalted Orb"),
    ];

    /// <summary>
    /// The currency each remembered stash cell's tablet needs, by "tab index:sub-tab:x,y". A stash tab's cells are
    /// replaced whenever that tab and sub-tab is the one showing. See NoteCraftingNeeds.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, int>> StashCraftingNeeds = new(StringComparer.Ordinal);

    /// <summary>The currency tab's stacks as last seen, by base name, or null before it has been seen this session.</summary>
    private static Dictionary<string, int> _currencyTabHeld;

    /// <summary>What one tablet needs to reach part-finished, by currency line. See the class summary.</summary>
    private static Dictionary<string, int> CraftingNeedsOf(TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var needs = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string line) => needs[line] = needs.GetValueOrDefault(line) + 1;

        if (tablet.Uses != FullUses)
            return needs;

        var prefixes = tablet.Modifiers.Count(x => x.IsPrefix);
        var suffixes = tablet.Modifiers.Count(x => x.IsSuffix);

        switch (tablet.Rarity)
        {
            case ItemRarity.Normal:
                Add("Transmutation");
                Add("Augmentation");
                break;

            case ItemRarity.Magic when prefixes == 0 || suffixes == 0:
                Add(ActionOf(tablet, settings) == TabletAction.Augmentation ? "Augmentation" : "Alchemy");
                break;

            case ItemRarity.Magic when settings.IsGood(tablet.Modifiers):
                Add("Regal");
                Add("Exalt");
                break;

            case ItemRarity.Magic:
                Add("Alchemy");
                break;

            case ItemRarity.Rare when tablet.Identified && ActionOf(tablet, settings) == TabletAction.Exalt:
                Add("Exalt");
                break;
        }

        return needs;
    }

    /// <summary>
    /// Records what the visible stash tab's tablets need, replacing what was remembered for that tab and sub-tab, and
    /// the currency tab's stacks when it is the one showing. Called on each read of the screen.
    /// </summary>
    private static void NoteCraftingNeeds(GameController gc, TabletRerollingSettings settings)
    {
        if (_stashRect.Width <= 0f)
            return;

        var visible = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IndexVisibleStash, -1);

        if (visible < 0)
            return;

        var subTab = SelectedSubTab(gc);
        var prefix = $"{visible}:{subTab}:";

        if (subTab > 0)
            SeenSubTabs.Add($"{visible}:{subTab}");

        foreach (var key in StashCraftingNeeds.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            StashCraftingNeeds.Remove(key);

        foreach (var key in StashNextActions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            StashNextActions.Remove(key);

        var perTablet = Spent.On("Tablets/NoteCraftingNeeds/PerTablet");

        foreach (var tablet in StashTablets())
        {
            var cell = $"{prefix}{tablet.Rect.X:0},{tablet.Rect.Y:0}";

            StashCraftingNeeds[cell] = CraftingNeedsOf(tablet, settings);

            if (tablet.Uses == FullUses && (tablet.Rarity == ItemRarity.Normal || tablet.Identified))
                StashNextActions[cell] = ActionOf(tablet, settings);
        }

        perTablet.Dispose();

        using (Spent.On("Tablets/NoteCraftingNeeds/StashTabWithIcon"))
        {
            if (visible != StashTabWithIcon(gc, CurrencyTabIcon))
                return;
        }

        var held = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.VisibleStash.VisibleInventoryItems, null) ?? [])
        {
            var entity = Safe.Read(item, static i => i.Item, null);
            var name = Safe.Read(entity, static e => e.GetComponent<Base>()?.Name, null);

            if (name != null)
                held[name] = held.GetValueOrDefault(name) + Math.Max(1, Safe.Read(entity, static e => e.GetComponent<Stack>()?.Size ?? 1, 1));
        }

        _currencyTabHeld = held;
    }

    /// <summary>What the stash's remembered tablets need, by currency line, the inventory's left out. See the Supply phase.</summary>
    private static Dictionary<string, int> StashCraftingNeedTotals()
    {
        var need = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var cell in StashCraftingNeeds.Values)
        foreach (var (line, count) in cell)
            need[line] = need.GetValueOrDefault(line) + count;

        return need;
    }

    /// <summary>What the tablets seen need in all, by currency line: the stash's remembered cells and the inventory's.</summary>
    private static Dictionary<string, int> CraftingNeedTotals(TabletRerollingSettings settings)
    {
        var need = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var cell in StashCraftingNeeds.Values)
        foreach (var (line, count) in cell)
            need[line] = need.GetValueOrDefault(line) + count;

        foreach (var tablet in InventoryTablets())
        foreach (var (line, count) in CraftingNeedsOf(tablet, settings))
            need[line] = need.GetValueOrDefault(line) + count;

        return need;
    }

    /// <summary>The inventory's stacks by base name, from the game's own record of it.</summary>
    private static Dictionary<string, int> InventoryCurrencyHeld(GameController gc)
    {
        var held = new Dictionary<string, int>(StringComparer.Ordinal);
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);

        foreach (var entity in Safe.Read(main, static m => m.Inventory.Items, null) ?? [])
        {
            if (Safe.Read(entity, static e => e.GetComponent<Base>()?.Name, null) is { } name)
                held[name] = held.GetValueOrDefault(name) + Math.Max(1, Safe.Read(entity, static e => e.GetComponent<Stack>()?.Size ?? 1, 1));
        }

        return held;
    }

    /// <summary>A crafting currency held in enough for the tablets seen. Fixed: the price tier colours mean something else.</summary>
    private static readonly Color RequirementMetColour = Color.FromArgb(255, 90, 255, 120);

    /// <summary>A crafting currency short of what the tablets seen need.</summary>
    private static readonly Color RequirementUnmetColour = Color.FromArgb(255, 235, 90, 90);

    /// <summary>
    /// "Transmutation: 0/0" and the rest under the pricing progress line, each green when the currency held covers what
    /// the tablets need and red when not, shown as ShowMetCraftingRequirements and ShowUnmetCraftingRequirements ask. With
    /// the currency tab not yet seen this session, a last line says its stacks are not counted.
    /// </summary>
    private static void DrawCraftingRequirements(Graphics graphics, GameController gc, TabletRerollingSettings settings)
    {
        if (!settings.CraftingByHand.ShowMetCraftingRequirements && !settings.CraftingByHand.ShowUnmetCraftingRequirements ||
            InventoryRect(gc) is not { Width: > 0f } panel)
            return;

        var need = CraftingNeedTotals(settings);
        var inventory = InventoryCurrencyHeld(gc);
        var lines = new List<(string Text, Color Colour)>();

        foreach (var (line, baseName) in CraftingCurrencies)
        {
            var have = inventory.GetValueOrDefault(baseName) + (_currencyTabHeld?.GetValueOrDefault(baseName) ?? 0);
            var needed = need.GetValueOrDefault(line);
            var met = have >= needed;

            if (met ? settings.CraftingByHand.ShowMetCraftingRequirements : settings.CraftingByHand.ShowUnmetCraftingRequirements)
                lines.Add(($"{line}: {have}/{needed}", met ? RequirementMetColour : RequirementUnmetColour));
        }

        if (lines.Count > 0 && _currencyTabHeld == null)
            lines.Add(("Currency tab not seen yet: its stacks are not counted", Dim));

        // Under the pricing progress line, at its offsets: its row is kept whether it shows or not.
        var height = graphics.MeasureText("Hg").Y + 4f;
        var at = new Vector2(panel.Left + 4f + ProgressRightOfInventory + settings.TradeSite.PricingProgressXOffset.Value,
            panel.Top - height - 2f + ProgressBelowInventoryTop + settings.TradeSite.PricingProgressYOffset.Value);

        foreach (var (text, colour) in lines)
        {
            at.Y += height;
            graphics.DrawTextWithBackground(text, at, colour, FontAlign.Left, LabelBackground);
        }
    }
}
