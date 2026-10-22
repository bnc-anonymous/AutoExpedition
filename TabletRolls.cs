using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;

namespace AutoExpedition;

/// <summary>
/// What the plugin's own reforges and crafts roll, written down for working out modifier weights: dumps/tablet_reforges.csv
/// and dumps/tablet_crafts.csv. See RecordingSettings.CollectTabletReforges and CollectTabletCrafts.
///
/// A reforge row is the three tablets that went in and the one that came out. The one that comes out is unidentified,
/// so the row waits: the inventory cell it was taken out to is noted, and the row is written once the tablet in that cell
/// reads identified - by Doryani in a full-auto run or by hand later. A tablet moved from that cell first is dropped.
/// A craft row is the currency and the tablet before and after, written as the crafting run confirms the change.
///
/// A tablet is written as its rarity and each explicit modifier as id (tier digits and all), P or S, and values:
/// "Rare|TowerRarePackIncrease2 P 31|TowerExpeditionUnearthedRares S 2".
/// </summary>
internal static partial class Tablets
{
    private const string ReforgeHeader = "when,input1,input2,input3,result";

    private const string CraftHeader = "when,currency,before,after";

    private const string IdentifyHeader = "when,where,itemLevel,tier,result";

    /// <summary>Whether each recording is on, as at the last draw. See Draw.</summary>
    private static bool _recordReforges;

    private static bool _recordCrafts;

    private static bool _recordIdentifications;

    /// <summary>
    /// Unidentified tablets in the inventory, by cell, waiting to be seen identified, with the tier read off each one's
    /// tooltip: -1 not read yet, TierNotOnTooltip read and not there, 0 read as having none. See RollsWatchIdentified and
    /// TierOfTabletTooltip.
    /// </summary>
    private static readonly Dictionary<(int X, int Y), int> UnidentifiedByCell = [];

    /// <summary>
    /// A tablet whose tooltip was walked and held no tier: one not hovered since login has none. Walked again only while
    /// hovered. Walking every such tablet's tooltip on every read cost about 28ms a read with 41 tablets carried, a
    /// stalled frame each time during a reforge run (dump 2026-10-10 15:19:58, Tablets/RollsWatch worst 82ms).
    /// </summary>
    private const int TierNotOnTooltip = -2;

    private static readonly List<string> IdentifyRows = [];

    /// <summary>A reforge waiting for its result: the inputs, and the inventory cell the result went to, once known.</summary>
    private sealed class PendingReforge
    {
        public string When = "";
        public string[] Inputs = [];
        public (int X, int Y)? Cell;
    }

    private static readonly List<PendingReforge> PendingReforges = [];

    /// <summary>Inventory cells already holding an unidentified tablet when the run looked, so a new one can be told apart.</summary>
    private static readonly HashSet<(int, int)> KnownUnidentifiedCells = [];

    private static readonly List<string> ReforgeRows = [];

    private static readonly List<string> CraftRows = [];

    private static readonly object RollsLock = new();

    /// <summary>A tablet as written: rarity, then each explicit modifier's id, affix and values.</summary>
    private static string TabletWritten(ItemRarity rarity, IEnumerable<TabletModifier> modifiers) =>
        string.Join("|", new[] { rarity.ToString() }.Concat(modifiers.Select(m =>
            $"{m.Id} {(m.IsPrefix ? "P" : m.IsSuffix ? "S" : "?")} {string.Join("/", m.Values)}"))).Replace(',', ';');

    /// <summary>
    /// The Expedition Tablets in the inventory by cell, from the game's own record of it, with each one's Mods component
    /// for ModifiersOf. Not the modifiers themselves: parsing every carried tablet's, twice a read, cost up to 80ms a
    /// read with 47 carried during a reforge run (dump 2026-10-10 15:25:56, Tablets/RollsWatch), where a row needs one.
    /// </summary>
    private static List<((int X, int Y) Cell, bool Identified, ItemRarity Rarity, Mods Mods, ExileCore2.Shared.RectangleF Rect, int ItemLevel)> CarriedByCell(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var found = new List<((int, int), bool, ItemRarity, Mods, ExileCore2.Shared.RectangleF, int)>();

        foreach (var slot in Safe.Read(main, static m => m.Inventory.InventorySlotItems, null) ?? [])
        {
            var entity = Safe.Read(slot, static s => s.Item, null);

            if (!(Safe.Read(entity, static e => e.Metadata, "") ?? "").StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var mods = Safe.Read(entity, static e => e.GetComponent<Mods>(), null);

            found.Add(((Safe.Read(slot, static s => s.PosX, -1), Safe.Read(slot, static s => s.PosY, -1)),
                Safe.Read(mods, static m => m.Identified, false), Safe.Read(mods, static m => m.ItemRarity, ItemRarity.Unknown),
                mods, Safe.Read(slot, static s => s.GetClientRect(), default), Safe.Read(mods, static m => m.ItemLevel, 0)));
        }

        return found;
    }

    /// <summary>A reforging run starts: the unidentified tablets already carried are not this run's results.</summary>
    private static void RollsBegin(GameController gc)
    {
        if (!_recordReforges)
            return;

        lock (RollsLock)
        {
            KnownUnidentifiedCells.Clear();

            foreach (var carried in CarriedByCell(gc).Where(c => !c.Identified))
                KnownUnidentifiedCells.Add(carried.Cell);
        }
    }

    /// <summary>A reforge was clicked with these three tablets in the bench.</summary>
    private static void RollsReforged(IReadOnlyList<TabletOnScreen> inputs)
    {
        if (!_recordReforges)
            return;

        lock (RollsLock)
            PendingReforges.Add(new PendingReforge
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Inputs = inputs.Select(t => TabletWritten(t.Rarity, t.Modifiers)).ToArray(),
            });
    }

    /// <summary>
    /// A reforged tablet was taken out to the inventory: the unidentified tablet in a cell not seen before goes to the
    /// oldest reforge waiting for its result.
    /// </summary>
    private static void RollsTakenOut(GameController gc)
    {
        if (!_recordReforges)
            return;

        lock (RollsLock)
        {
            var waiting = PendingReforges.FirstOrDefault(p => p.Cell == null);

            foreach (var carried in CarriedByCell(gc).Where(c => !c.Identified))
            {
                if (!KnownUnidentifiedCells.Add(carried.Cell))
                    continue;

                if (waiting != null)
                {
                    waiting.Cell = carried.Cell;
                    waiting = PendingReforges.FirstOrDefault(p => p.Cell == null);
                }
            }
        }
    }

    /// <summary>
    /// Writes each waiting reforge whose result now reads identified, and drops one whose cell no longer holds an
    /// unidentified or identified tablet. Called on each read of the screen.
    /// </summary>
    private static void RollsWatch(GameController gc, Func<List<((int X, int Y) Cell, bool Identified, ItemRarity Rarity, Mods Mods, ExileCore2.Shared.RectangleF Rect, int ItemLevel)>> carriedNow)
    {
        lock (RollsLock)
        {
            if (!PendingReforges.Any(p => p.Cell != null))
                return;
        }

        var carried = carriedNow();

        lock (RollsLock)
        {
            foreach (var pending in PendingReforges.Where(p => p.Cell != null).ToList())
            {
                var there = carried.FirstOrDefault(c => c.Cell == pending.Cell);

                if (there.Cell != pending.Cell)
                {
                    PendingReforges.Remove(pending);
                    KnownUnidentifiedCells.Remove(pending.Cell!.Value);
                    continue;
                }

                if (!there.Identified || ModifiersOf(there.Mods) is not { Length: > 0 } result)
                    continue;

                ReforgeRows.Add(string.Join(",", pending.When, pending.Inputs.ElementAtOrDefault(0) ?? "", pending.Inputs.ElementAtOrDefault(1) ?? "",
                    pending.Inputs.ElementAtOrDefault(2) ?? "", TabletWritten(there.Rarity, result)));
                PendingReforges.Remove(pending);
                KnownUnidentifiedCells.Remove(pending.Cell!.Value);
            }
        }
    }

    /// <summary>A tablet's metadata path, base name and item level, for the dump.</summary>
    private static (string Metadata, string BaseName, string ItemLevel) ItemDetailsOfTablet(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
    {
        var mods = Safe.Read(entity, static e => e.GetComponent<Mods>(), null);

        return ((Safe.Read(entity, static e => e.Metadata, "") ?? "").Replace(',', ';'),
            (Safe.Read(entity, static e => e.GetComponent<Base>()?.Name, "") ?? "").Replace(',', ';'),
            Safe.Read(mods, static m => m.ItemLevel, 0).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// An unidentified tablet's tier, read off its tooltip: "Unidentified (Tier 4)" is 4, "Unidentified" alone is 0, and
    /// -1 when the line was not found. The tooltip is the only place it was found: the Mods component of an unidentified
    /// tablet holds the implicit alone, and tier 2 and tier 4 tablets read the same base and item level (dumps
    /// 2026-10-09 20:41:42 to 20:41:45).
    /// </summary>
    private static int TierOfTabletTooltip(Element element)
    {
        var stack = new Stack<(Element Element, int Depth)>();

        if (Safe.Read(element, static e => e.Tooltip, null) is { } tooltip)
            stack.Push((tooltip, 0));

        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            var text = (Safe.Read(at, static e => e.Text, null) ?? "").Trim();

            if (text.StartsWith("Unidentified", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, @"\(Tier (\d+)\)");

                return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }

            if (depth >= 8)
                continue;

            foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                stack.Push((child, depth + 1));
        }

        return -1;
    }

    /// <summary>
    /// Writes every inventory tablet seen unidentified and now seen identified - by Doryani, by hand, or by anything
    /// else - with its tier and its modifiers. Called on each read of the screen.
    ///
    /// Tracked by inventory cell, as the reforge recorder is, not by entity address: keyed by address, an inventory
    /// identified at Doryani (2026-10-09) wrote no row at all. Whether identifying gives the item a new address was not
    /// checked; the cell holds either way. A tablet moved to another cell before it is identified is lost.
    ///
    /// The tier is read off the tooltip of the tablet on screen in that cell, found by the slot's own screen
    /// rectangle, until it reads. Only a tablet hovered since login has a tooltip to read (dumps 2026-10-09 20:45:02 and,
    /// after a relog, 20:46:13).
    /// </summary>
    /// <summary>Whether an on-screen rectangle's centre lies inside an inventory slot's rectangle.</summary>
    private static bool CentreWithin(ExileCore2.Shared.RectangleF shown, ExileCore2.Shared.RectangleF slot)
    {
        var (x, y) = (shown.X + shown.Width / 2f, shown.Y + shown.Height / 2f);

        return slot.Width > 0f && x >= slot.Left && x <= slot.Right && y >= slot.Top && y <= slot.Bottom;
    }

    private static void RollsWatchIdentified(GameController gc, Func<List<((int X, int Y) Cell, bool Identified, ItemRarity Rarity, Mods Mods, ExileCore2.Shared.RectangleF Rect, int ItemLevel)>> carriedNow)
    {
        if (!_recordIdentifications)
            return;

        var carried = carriedNow();

        lock (RollsLock)
        {
            foreach (var tablet in carried)
            {
                if (!tablet.Identified)
                {
                    var tier = UnidentifiedByCell.TryGetValue(tablet.Cell, out var had) ? had : -1;
                    var hovered = _hoveredAtDraw is { Where: "inventory" } over && CentreWithin(over.Rect, tablet.Rect);

                    if ((tier == -1 || tier == TierNotOnTooltip && hovered) &&
                        Seen.FirstOrDefault(t => t.Where == "inventory" && CentreWithin(t.Rect, tablet.Rect)) is { } shown)
                    {
                        var read = TierOfTabletTooltip(shown.Element);

                        tier = read >= 0 ? read : TierNotOnTooltip;
                    }

                    UnidentifiedByCell[tablet.Cell] = tier;
                    continue;
                }

                // Only a cell seen unidentified is written; its modifiers are parsed then, not for every identified tablet.
                if (!UnidentifiedByCell.ContainsKey(tablet.Cell) || ModifiersOf(tablet.Mods) is not { Length: > 0 } identified ||
                    !UnidentifiedByCell.Remove(tablet.Cell, out var before))
                    continue;

                IdentifyRows.Add(string.Join(",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "inventory",
                    tablet.ItemLevel.ToString(CultureInfo.InvariantCulture),
                    before >= 0 ? before.ToString(CultureInfo.InvariantCulture) : "",
                    TabletWritten(tablet.Rarity, identified)));
            }

            // A cell no longer holding a tablet: it was moved, used or sold, and is not waited for.
            foreach (var cell in UnidentifiedByCell.Keys.Where(c => carried.All(t => t.Cell != c)).ToList())
                UnidentifiedByCell.Remove(cell);
        }
    }

    /// <summary>The crafting run saw a currency change a tablet.</summary>
    private static void RollsCrafted(string currency, TabletOnScreen before, TabletOnScreen after)
    {
        if (!_recordCrafts)
            return;

        lock (RollsLock)
            CraftRows.Add(string.Join(",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), currency,
                TabletWritten(before.Rarity, before.Modifiers), TabletWritten(after.Rarity, after.Modifiers)));
    }

    /// <summary>
    /// **Currency used by hand on a tablet**, written as the crafting run's crafts are: each tablet on screen is kept by
    /// its cell with its modifiers as written, and a change to them while a currency is on the cursor, or was
    /// at the last read, is a craft by that currency. Not while a tablet run holds the cursor, as the run writes its own;
    /// both readings whole and identified, so a tablet half read or an identification is not taken for one. Chaos Orbs by
    /// hand were not recorded, the run never using them, though they are what tests what a Chaos Orb does
    /// (2026-10-20). A currency the plugin does not name is written as the game's cursor action, "by hand: " and its
    /// string. See RollsCrafted and ChaosOrbPredictionRows.
    /// </summary>
    private static readonly Dictionary<string, (string Written, TabletOnScreen Tablet)> HandCraftWatch = new(StringComparer.Ordinal);

    /// <summary>The game's cursor action at the last read. See HandCraftWatch.</summary>
    private static string _cursorActionBefore = "";

    /// <summary>
    /// For each tablet crafted by hand that Chaos Orb odds can be worked out for, what they said before and what it came
    /// to: its worth now and expected after in exalts, the chance it would come out ahead of the orb, and its worth after as
    /// the same listings value it. Written to dumps/tablet_chaos_predictions.csv. See TabletChaosOrb.
    /// </summary>
    private static readonly List<string> ChaosOrbPredictionRows = [];

    private const string ChaosOrbPredictionHeader = "when,currency,before,after,worthNowExalts,expectedAfterExalts,chanceAhead,worthAfterExalts";

    /// <summary>
    /// For each tablet on screen whose modifiers changed under a held currency: searches its own price (SearchAfterHandCraft)
    /// and, with Collect tablet crafts on, writes the craft. While a currency is held, the hovered tablet's modifiers are read
    /// fresh at every read, not from ModifierCache: whether a currency changes the Mods hash the cache watches is not known,
    /// and a tablet showed the same price and Chaos Orb odds after an orb by hand as before it (the player, 2026-10-21).
    /// See HandCraftWatch.
    /// </summary>
    private static void WatchHandCrafts(GameController gc, TabletRerollingSettings settings)
    {
        if (_reforgingHasCursor)
        {
            HandCraftWatch.Clear();
            _cursorActionBefore = "";
            return;
        }

        var action = CursorAction(gc);
        var held = action.Length > 0 ? action : _cursorActionBefore;
        var seenNow = new HashSet<string>(StringComparer.Ordinal);

        if (action.Length > 0 && _hoveredAtDraw is { } hovered && AddressOf(hovered) is var hoveredAddress and not 0L)
            MarkTabletChanging(hoveredAddress);

        // **By the tablet's cell, not its item's address**: a Chaos Orb by hand was not recorded, the tablet read as
        // another item after the use, as the crafting run finds it by cell too (CurrencyUsesPending; 2026-10-20). A stash
        // cell with the tab and sub-tab showing, so a tab changed under a held currency is not taken for a craft.
        var stashTab = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IndexVisibleStash, -1);
        var subTab = SelectedSubTab(gc);

        foreach (var tablet in Seen)
        {
            var address = tablet.Where == "inventory"
                ? $"inventory:{tablet.Rect.X:0},{tablet.Rect.Y:0}"
                : $"{tablet.Where}:{stashTab}:{subTab}:{tablet.Rect.X:0},{tablet.Rect.Y:0}";

            if (!tablet.Identified || !FullyRead(tablet))
                continue;

            seenNow.Add(address);

            var written = TabletWritten(tablet.Rarity, tablet.Modifiers);

            if (HandCraftWatch.TryGetValue(address, out var before) && before.Written != written && held.Length > 0)
            {
                SearchAfterHandCraft(gc, tablet, settings);

                if (!_recordCrafts)
                {
                    HandCraftWatch[address] = (written, tablet);
                    continue;
                }

                var currency = CurrencyOfCursorAction(held);

                RollsCrafted(currency, before.Tablet, tablet);

                if (ChaosOrbOddsOf(gc, before.Tablet, settings) is { } odds)
                {
                    // The outcome priced as ChaosOrbOddsUnkept prices each outcome: its set's price at any roll.
                    var worthAfter = ChaosOrbWorthOfModifiers(gc, tablet.Modifiers, settings);

                    lock (RollsLock)
                        ChaosOrbPredictionRows.Add(string.Join(",",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), currency,
                            before.Written, written,
                            odds.NowExalts.ToString("0.#", CultureInfo.InvariantCulture),
                            odds.AfterExalts.ToString("0.#", CultureInfo.InvariantCulture),
                            odds.ChanceAhead.ToString("0.###", CultureInfo.InvariantCulture),
                            worthAfter?.ToString("0.#", CultureInfo.InvariantCulture) ?? ""));
                }
            }

            HandCraftWatch[address] = (written, tablet);
        }

        foreach (var gone in HandCraftWatch.Keys.Where(k => !seenNow.Contains(k)).ToList())
            HandCraftWatch.Remove(gone);

        _cursorActionBefore = action;
    }

    /// <summary>A cursor action as the crafting run names its currency, or "by hand: " and the game's string for one it does not name.</summary>
    private static string CurrencyOfCursorAction(string action) => action switch
    {
        "transmute_to_magic" => "Transmutation",
        "add_mod_to_magic" => "Augmentation",
        "transmute_to_rare" => "Alchemy",
        "upgrade_magic_to_rare" => "Regal",
        "add_mod_to_rare" => "Exalt",
        // A Chaos Orb's, read on the player's first one by hand (2026-10-20 23:15:38).
        "reroll" => "Chaos",
        _ => "by hand: " + action,
    };

    /// <summary>Appends the rows written since the last time to their files in the dumps folder.</summary>
    internal static void RollsFlush(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        List<string> reforges, crafts, identified, predictions;

        lock (RollsLock)
        {
            if (ReforgeRows.Count == 0 && CraftRows.Count == 0 && IdentifyRows.Count == 0 && ChaosOrbPredictionRows.Count == 0)
                return;

            reforges = [.. ReforgeRows];
            crafts = [.. CraftRows];
            identified = [.. IdentifyRows];
            predictions = [.. ChaosOrbPredictionRows];
            ReforgeRows.Clear();
            CraftRows.Clear();
            IdentifyRows.Clear();
            ChaosOrbPredictionRows.Clear();
        }

        void Append(string file, string header, List<string> rows)
        {
            if (rows.Count == 0)
                return;

            try
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                var path = Path.Combine(directory, file);

                Directory.CreateDirectory(directory);

                if (!File.Exists(path))
                    File.WriteAllText(path, header + "\n");

                File.AppendAllLines(path, rows);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[AutoExpedition] Could not write {file}: {ex.Message}", 5f);
            }
        }

        Append("tablet_reforges.csv", ReforgeHeader, reforges);
        Append("tablet_crafts.csv", CraftHeader, crafts);
        Append("tablet_identifications.csv", IdentifyHeader, identified);
        Append("tablet_chaos_predictions.csv", ChaosOrbPredictionHeader, predictions);
    }
}
