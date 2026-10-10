using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using Graphics = ExileCore2.Graphics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Expedition Tablets in the open stash tab and, optionally, the inventory: which currency each takes next, drawn as a
/// border inside its cell. See TabletRerollingSettings.
///
/// The sequence a tablet is taken through: a normal one is made magic (Transmutation); a magic one without both a
/// prefix and a suffix is filled (Augmentation); a full magic one is then either made rare keeping its modifiers
/// (Regal, when they are worth keeping) or rerolled to rare with new ones (Alchemy, when they are not); a rare one with
/// fewer than four modifiers takes an Exalt; a rare one with four is finished.
/// </summary>
internal static partial class Tablets
{
    /// <summary>The Expedition Tablet's metadata path, as the census recorded it on 2026-10-07 (remnant_items.csv).</summary>
    internal const string ExpeditionTabletPath = "Metadata/Items/TowerAugment/ExpeditionAugment";

    internal enum TabletAction
    {
        None,
        Transmutation,
        Augmentation,
        Alchemy,
        Regal,
        Exalt,
        Finished,
    }

    /// <summary>
    /// One explicit modifier on a tablet. Id is the mod's own name (RawName, else ModRecord.Key, else Name); Stem is Id
    /// without its trailing tier digits, which is what a TabletModifierRow names. AffixName is the affix's own name
    /// ("of Remnants"), which is what a trade listing calls it. See TabletRerollingSettings.Learn.
    /// </summary>
    internal sealed record TabletModifier(string Id, string Stem, bool IsPrefix, bool IsSuffix, string Group,
        int[] Values, string Text, string AffixName, int Low, int High);

    /// <summary>
    /// One tablet on screen: where it was read, its element and cell, its rarity, its explicit modifiers, and whether it
    /// is identified - an unidentified one reads no modifiers.
    /// </summary>
    internal sealed record TabletOnScreen(string Where, Element Element, RectangleF Rect, ItemRarity Rarity,
        TabletModifier[] Modifiers, Entity Entity, int Uses, bool Identified);

    /// <summary>How often the stash and inventory are read again. The walk of a tab's elements is bounded, not free.</summary>
    private static readonly TimeSpan ReadEvery = TimeSpan.FromMilliseconds(150);

    /// <summary>Which part of a read comes next: 0 the read itself, 1 crafting needs, 2 sub-tab records. See Draw.</summary>
    private static int _readStage;

    /// <summary>
    /// The metadata of every item the visible stash tab showed at the last read, from the read's own walk of its elements,
    /// so the sub-tab records need not walk them again. See NoteSubTab and VisibleTabItemPaths, which walks the same way.
    /// </summary>
    private static List<string> _stashTabItemPathsAtRead = [];

    /// <summary>
    /// The most elements visited under the visible stash tab looking for tablets. The Fragment tab's tablet section does
    /// not list its items in VisibleInventoryItems reliably, so its element tree is walked as TabletHelper does
    /// (StashElement.StashInventoryPanel, the child at IndexVisibleStash, down), within these bounds.
    /// </summary>
    private const int MostElements = 900;

    private const int DeepestElement = 12;

    /// <summary>Space between a border and the edge of its cell, so neighbouring borders never touch.</summary>
    private const float BorderGap = 1f;

    private static DateTime _read = DateTime.MinValue;

    /// <summary>The currency right-clicked for use at the last read, as the action it is; None for none. See HeldOnCursor.</summary>
    private static TabletAction _held;

    /// <summary>The tablets found on the last read. For drawing and for the dump.</summary>
    internal static IReadOnlyList<TabletOnScreen> Seen { get; private set; } = [];

    /// <summary>The open stash's rectangle at the last read, empty when it is closed. The progress line sits above it.</summary>
    private static RectangleF _stashRect;

    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings, Valuation valuation)
    {
        var tablets = settings.TabletRerolling;

        if (!tablets.EnableTabletRerolling)
            return;

        _valuation = valuation;
        _automationOn = settings.Automation.Enable.Value;
        _tabletAutomation = tablets.TabletAutomation.EnableTabletAutomation.Value;
        _tidyAfterCraftAndReforge = tablets.TabletAutomation.TidyAfterCraftAndReforge.Value;
        _pauseBetweenClicksMs = tablets.TabletAutomation.PauseBetweenClicksMs.Value;
        _shortestMoveMs = tablets.TabletAutomation.ShortestMoveBetweenTabletsMs.Value;
        _shortestMoveDepositingMs = tablets.TabletAutomation.ShortestMoveWhenDepositingMs.Value;
        _recordReforges = settings.Recording.CollectTabletReforges.Value;
        _recordCrafts = settings.Recording.CollectTabletCrafts.Value;
        _recordIdentifications = settings.Recording.CollectTabletIdentifications.Value;
        _actionKeyName = Safe.Read(() => settings.ActionHotkey.Value.Key.ToString(), "F4") is { Length: > 0 } key ? key : "F4";

        // **The read of the screen and what is worked out from it, over three frames.** Done in one, it made a frame of
        // about 36ms every 150ms (15 read, 14 sub-tab records, 7 crafting needs, dump 2026-10-09 22:09), and ExileInput2,
        // which puts the cursor once a frame, jumped across each one. Each part still runs once a read.
        if (_readStage == 1)
        {
            _readStage = 2;

            using (Spent.On("Tablets/NoteCraftingNeeds"))
                NoteCraftingNeeds(gc, tablets);
        }
        else if (_readStage == 2)
        {
            _readStage = 0;

            using (Spent.On("Tablets/NoteSubTab"))
            {
                NoteSubTab(gc, tablets);

                using (Spent.On("Tablets/NoteSubTab/Save"))
                    SaveSubTabBook();
            }

            using (Spent.On("Tablets/CountForZones"))
                CountForZones(gc, tablets);
        }
        else if (DateTime.UtcNow - _read >= ReadEvery)
        {
            _readStage = 1;

            // Each part timed apart, for "where a frame goes" in the dump: the section as a whole averaged 4.9ms a
            // frame with frames of 181ms (dump 2026-10-09 21:40), and the parts said nothing.
            using (Spent.On("Tablets/Read"))
            {
                PruneModifierCache();
                Seen = Read(gc, true, tablets.Prices.ShowMerchantPrices);
            }

            using (Spent.On("Tablets/HeldOnCursor"))
                _held = HeldOnCursor(gc);

            using (Spent.On("Tablets/NoteFragmentTab"))
                NoteFragmentTab(gc);

            _read = DateTime.UtcNow;

            using (Spent.On("Tablets/Note"))
            {
                foreach (var tablet in Seen)
                    tablets.Note(tablet.Modifiers);
            }

            using (Spent.On("Tablets/CollectTablets"))
                CollectTablets(gc, tablets, valuation);

            using (Spent.On("Tablets/RollsWatch"))
            {
                // One read of the inventory for both, and none when neither needs it.
                var carried = new Lazy<List<((int X, int Y) Cell, bool Identified, ItemRarity Rarity, Mods Mods, RectangleF Rect, int ItemLevel)>>(() => CarriedByCell(gc));

                RollsWatch(gc, () => carried.Value);
                RollsWatchIdentified(gc, () => carried.Value);
            }
        }

        using (Spent.On("Tablets/LearnTradeStats"))
            LearnTradeStats(tablets);

        // **The hovered tablet's own tooltip, found from the cursor.** A hovered item's tooltip is drawn over its
        // neighbours, and a border painted there covers the tooltip's text, so tablets under it are left out for that
        // frame. IngameState.UIHoverTooltip was the first source tried: with nothing hovered it still read visible and
        // covered the stash, so no border showed until a tablet was hovered, and with one hovered borders still crossed
        // the tooltip (2026-10-09). The cursor test and the element's own Tooltip are what Ritual Roll Ranges uses.
        var mouse = ImGuiNET.ImGui.GetMousePos();
        var hovered = Seen.FirstOrDefault(t => mouse.X >= t.Rect.Left && mouse.X <= t.Rect.Right &&
                                               mouse.Y >= t.Rect.Top && mouse.Y <= t.Rect.Bottom);

        _hoveredAtDraw = hovered;

        if (_actionKeyPressed)
        {
            _actionKeyPressed = false;

            // A run going is stopped by the key anywhere. With the reforging bench open the key starts reforging junk
            // tablets - but not over a tablet, which it prices as anywhere else: pressed to price a 6c tablet with the
            // bench open, it started a run that reforged that tablet (2026-10-10). Over an action zone it starts that
            // zone's run. See TabletActionZones.
            if (_reforgingHasCursor)
                ToggleReforging(gc, tablets, _run);
            else if (ReforgingBenchOpen(gc) && _tabletAutomation && hovered == null)
                ToggleReforging(gc, tablets);
            else if (ZoneUnderCursor(gc, tablets) is { } zone)
                StartZone(gc, tablets, zone);
            else
                ActOnActionKey(gc, hovered, tablets, valuation);
        }

        using (Spent.On("Tablets/StepReforging"))
            StepReforging(gc, tablets);

        using (Spent.On("Tablets/Status"))
        {
            DrawReforgingStatus(graphics, gc);
            DrawActionZones(graphics, gc, tablets);
            DrawKeyNote(graphics, gc);
        }

        using (Spent.On("Tablets/PlanPricingRun"))
        {
            if (valuation != null && PricingRun.Count > 0 && DateTime.UtcNow - _planned >= PlanEvery)
                PlanPricingRun(tablets, valuation);
        }

        using (Spent.On("Tablets/Progress"))
        {
            DrawPricingProgress(graphics, gc, tablets);
            DrawCraftingRequirements(graphics, gc, tablets);
        }

        if (Seen.Count == 0)
            return;

        var tooltip = TooltipOf(hovered);
        var thickness = Math.Clamp(tablets.CraftingByHand.BorderThickness.Value, 1, 3);

        // The hovered tablet's price table, laid out first so that what lies under it can be left out like what lies
        // under the tooltip. Its background is translucent, and a label under it shows through its text.
        DetailLayout detail;

        using (Spent.On("Tablets/Detail"))
            // Not while a run holds the cursor: it would open over every tablet the run passes. Prices are still drawn.
            detail = !_reforgingHasCursor && hovered != null && !(tablets.Prices.IgnoreUsedTablets && hovered.Uses < FullUses) &&
                     IsPriced(hovered, ActionOf(hovered, tablets), tablets, valuation)
                ? LayOutDetail(graphics, hovered.Rect, tooltip, Priced(gc, hovered, tablets, valuation))
                : null;

        using var labels = Spent.On("Tablets/Labels");

        foreach (var tablet in Seen)
        {
            var underDetail = detail != null && Overlaps(tablet.Rect, detail.Rect);

            if (tablet != hovered && (tooltip.Width > 0f && Overlaps(tablet.Rect, tooltip) || underDetail))
                continue;

            // A used tablet is left alone entirely when asked. See TabletRerollingSettings.IgnoreUsedTablets.
            if (tablets.Prices.IgnoreUsedTablets && tablet.Uses < FullUses)
                continue;

            var action = ActionOf(tablet, tablets);

            // **A priced tablet carries its price in the corner, coloured by band** - in the stash and in the inventory
            // alike, where it is taken to be sold - and while a currency is in use only when not hidden then. See Priced
            // and TabletRerollingSettings.HidePricesWhenCurrencyHeld.
            if ((_held == TabletAction.None || !tablets.Prices.HidePricesWhenCurrencyHeld) && !underDetail &&
                IsPriced(tablet, action, tablets, valuation))
            {
                var priced = Priced(gc, tablet, tablets, valuation);

                if (priced.Label.Length > 0)
                    DrawLabel(graphics, tablet.Rect, priced.Label, priced.Colour.A > 0 ? priced.Colour : Color.White);
            }

            // A finished tablet has no border: its price is all there is to say about it. None at all unless asked for.
            if (action == TabletAction.Finished || !tablets.CraftingByHand.DrawTabletBorders)
                continue;

            // The inventory is read for prices; the other borders show there only when asked for.
            if (tablet.Where == "merchant" || tablet.Where == "inventory" && !tablets.CraftingByHand.DrawInInventory)
                continue;

            // **While a currency is in use, the tablets it is for.** A tablet whose next action is the held currency is
            // drawn - in TransmutationAndAugmentationColour for a Transmutation or Augmentation, else its own action
            // colour - and every other tablet is left off when HideOtherBordersWhenCurrencyHeld is on. See
            // HeldOnCursor.
            var forHeld = _held != TabletAction.None && action == _held;

            if (_held != TabletAction.None && !forHeld && tablets.CraftingByHand.HideOtherBordersWhenCurrencyHeld)
                continue;

            var colour = forHeld && action is TabletAction.Transmutation or TabletAction.Augmentation
                ? tablets.CraftingByHand.TransmutationAndAugmentationColour.Value
                : tablets.ColourOf(action);

            if (colour.A > 0)
                DrawInsetBorder(graphics, tablet.Rect, thickness, colour);
        }

        if (detail != null)
            DrawDetail(graphics, detail);
    }

    /// <summary>
    /// Whether a tablet is priced: a finished one (rare, four modifiers) with ShowRareTabletPrices, any magic one with
    /// ShowMagicTabletPrices. Never without the reward valuation, whose rates the prices are shown at.
    /// </summary>
    private static bool IsPriced(TabletOnScreen tablet, TabletAction action, TabletRerollingSettings settings,
        Valuation valuation) =>
        valuation != null && (action == TabletAction.Finished && settings.Prices.ShowRareTabletPrices ||
                              tablet.Rarity == ItemRarity.Magic && settings.Prices.ShowMagicTabletPrices);

    /// <summary>
    /// The hovered tablet's tooltip rectangle, or empty. **Not one that overlaps the tablet itself**: the game draws a
    /// tooltip beside the item it describes. With an Orb of Transmutation in use, the white borders of the hovered tablet
    /// and its neighbours vanished though no tooltip text was over them (2026-10-09); a rectangle read over the tablet
    /// itself is the suspected cause, not yet seen in a dump - the dump's hovered line prints it. The hovered tablet is
    /// never skipped either way.
    /// </summary>
    private static RectangleF TooltipOf(TabletOnScreen hovered)
    {
        if (hovered == null)
            return default;

        var rect = Safe.Read(hovered.Element, static e =>
            e.Tooltip is { IsVisible: true } shown ? shown.GetClientRectCache : default, default);

        return rect.Width > 0f && Overlaps(rect, hovered.Rect) ? default : rect;
    }

    /// <summary>
    /// Whether two rectangles share more than an edge. RectangleF.Intersects counts a shared edge: the game draws a
    /// tooltip with its bottom exactly on the hovered tablet's top (y 624 and 624 in the dump of 04:02:42 on
    /// 2026-10-09), and Intersects then threw the tooltip away, so prices were drawn over its text.
    /// </summary>
    private static bool Overlaps(RectangleF a, RectangleF b, float edge = 2f) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > edge &&
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > edge;

    /// <summary>The uses a fresh Expedition Tablet has: 10 on every one of 74 read on 2026-10-09.</summary>
    internal const int FullUses = 10;

    // ------------------------------------------------------------------ prices

    /// <summary>
    /// The trade site's "# uses remaining (Tablets)", searched at exactly the tablet's own uses: a used tablet sells for
    /// less, and a search without it priced a fresh one against them (2026-10-09).
    /// </summary>
    private const string UsesStat = "pseudo.pseudo_number_of_uses_remaining";

    /// <summary>The trade site's "# Modifiers", explicit modifiers on the item. See Priced.</summary>
    private const string ModifierCountStat = "pseudo.pseudo_number_of_affix_mods";

    /// <summary>
    /// A finished tablet's price as drawn: the label's colour and text, what was searched, its own search's age and
    /// count (Status, the hover's first line), the listings shown, and what else the price rests on (Detail, the line
    /// under it, empty when there is nothing more).
    /// </summary>
    private sealed record PricedTablet(Color Colour, string Label, string Searched, string Status,
        List<(string Stat, string Text)> Filters, List<TabletPricing.Listing> Listings, string Detail = "");

    /// <summary>
    /// What a finished tablet's price reads as now: nothing, with the reason, when it is not searched at all; else what
    /// the answers so far say. Never queues a search: see ActOnActionKey and PlanPricingRun.
    /// </summary>
    private static PricedTablet Priced(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings,
        Valuation valuation) =>
        SearchOfTablet(gc, tablet, settings, out var reason) is { } search
            ? PricedFromAnswers(search, FingerprintOf(tablet), settings, valuation) is var priced
                ? priced with { Status = $"{priced.Status}, {search.WeightSaid}" }
                : null
            : new PricedTablet(Color.Transparent, "", "", reason, [], []);

    /// <summary>
    /// Whether a listing's currency is one the valuation prices, given its rates: exalted, chaos, divine and vaal. A
    /// listing in one of these with no rate known is of unknown price, not of a currency taken as worth under a chaos.
    /// See ExaltsForReforging.
    /// </summary>
    private static bool IsRatedCurrency(string currency) => currency is "exalted" or "chaos" or "divine" or "vaal";

    /// <summary>
    /// A listing's price in exalts at the valuation's rates; NaN for a currency other than exalted, chaos, divine or
    /// vaal, or one whose rate is unknown. Vaal was taken as under a chaos until 2026-10-10, but at about 8 vaal to the
    /// chaos a tablet listed at 80 vaal is worth about 10c.
    /// </summary>
    private static double ExaltsOf(TabletPricing.Listing listing, Valuation valuation) => listing.Currency switch
    {
        "exalted" => listing.Amount,
        "chaos" when valuation.Converts("Chaos") => listing.Amount * valuation.PerExalt("Chaos"),
        "divine" when valuation.Converts("Divine") => listing.Amount * valuation.PerExalt("Divine"),
        "vaal" when valuation.Converts("Vaal") => listing.Amount * valuation.PerExalt("Vaal"),
        _ => double.NaN,
    };

    /// <summary>
    /// The colour of the price tier a price in chaos falls in, from Reforge under up. White when the chaos rate is not
    /// known, since the bands are in chaos and cannot be judged. See TabletChaosOf.
    /// </summary>
    private static Color BandColour(double? chaos, TabletRerollingSettings settings) =>
        chaos is not { } known ? Color.White
        : known >= settings.Prices.MidTierUnderChaos.Value ? settings.Prices.HighTierColour.Value
        : known >= settings.Prices.LowTierUnderChaos.Value ? settings.Prices.MidTierColour.Value
        : settings.Prices.LowTierColour.Value;

    /// <summary>
    /// A tablet price in exalts as chaos, or null when the valuation has no chaos rate and the price is not nought.
    /// Valuation.PerExalt answers 1 for an unknown rate, which read 85 exalts as 85 chaos and coloured the tablet as Good
    /// while NinjaPricer had failed to fetch (dump 2026-10-09 22:03).
    /// </summary>
    private static double? TabletChaosOf(double exalts, Valuation valuation) =>
        exalts == 0d ? 0d : valuation.Converts("Chaos") ? exalts / valuation.PerExalt("Chaos") : null;

    /// <summary>An answer's price in exalts by the Price from setting, or NaN when it has no usable listing.</summary>
    private static double PriceOfAnswer(TabletPricing.Answer answer, TabletRerollingSettings settings, Valuation valuation) =>
        settings.PriceOf(answer.Listings.Select(l => ExaltsOf(l, valuation)).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList());

    /// <summary>
    /// A price in whole chaos below a divine, in whole divines from one up - "17c", "1d": tablets trade in those two,
    /// and a fraction of either claims more precision than the listings have.
    /// </summary>
    private static string Shown(double exalts, Valuation valuation)
    {
        if (valuation.Converts("Divine") && exalts >= valuation.PerExalt("Divine"))
            return Whole(exalts / valuation.PerExalt("Divine")) + "d";

        return valuation.Converts("Chaos") ? Whole(exalts / valuation.PerExalt("Chaos")) + "c" : Whole(exalts) + "ex";
    }

    /// <summary>A listing's price as listed, whole: "12c", "1d", "5ex".</summary>
    private static string Listed(TabletPricing.Listing listing) => Whole(listing.Amount) + listing.Currency switch
    {
        "chaos" => "c",
        "divine" => "d",
        "exalted" => "ex",
        _ => " " + listing.Currency,
    };

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Ago(TimeSpan age) =>
        age.TotalMinutes < 1 ? $"{age.TotalSeconds:0}s" : age.TotalHours < 1 ? $"{age.TotalMinutes:0}m"
        : age.TotalDays < 1 ? $"{age.TotalHours:0}h" : $"{age.TotalDays:0}d";

    /// <summary>Takes the trade stats named by fetched listings' affixes into the modifier table. See TabletRerollingSettings.Learn.</summary>
    private static void LearnTradeStats(TabletRerollingSettings settings)
    {
        var seen = Seen.SelectMany(t => t.Modifiers).ToList();

        while (TabletPricing.Learned.TryDequeue(out var mods))
        {
            foreach (var mod in mods ?? [])
            {
                var hash = mod?.Value<string>("hash") ?? "";
                var name = mod?["mods"]?.FirstOrDefault()?.Value<string>("name") ?? "";

                if (hash.StartsWith("stat.", StringComparison.Ordinal) && name.Length > 0)
                    settings.Learn(name, hash["stat.".Length..], StripNumbers(Clean(mod?.Value<string>("description") ?? "")), seen);
            }
        }
    }

    /// <summary>Black behind the price label and the hover table, as Ritual Roll Ranges draws its percentage.</summary>
    private static readonly Color LabelBackground = Color.FromArgb(190, 0, 0, 0);

    private static readonly Color Dim = Color.FromArgb(255, 160, 160, 160);

    /// <summary>The price in the bottom left corner of the cell, in its band's colour.</summary>
    private static void DrawLabel(Graphics graphics, RectangleF cell, string label, Color colour)
    {
        var size = graphics.MeasureText(label);

        graphics.DrawTextWithBackground(label, new Vector2(cell.Left + BorderGap + 1f, cell.Bottom - BorderGap - 1f - size.Y),
            colour, ExileCore2.Shared.Enums.FontAlign.Left, LabelBackground);
    }

    /// <summary>
    /// A hovered finished tablet's search and its cheapest listings as a small table - price as listed, how long ago it
    /// was listed, and each searched modifier's roll - under the game's tooltip, or under the tablet when there is none.
    /// </summary>
    private static DetailLayout LayOutDetail(Graphics graphics, RectangleF cell, RectangleF tooltip, PricedTablet priced)
    {
        // No listings, no table: its headings alone say nothing.
        var rows = new List<(string[] Cells, Color Colour)>();

        if (priced.Listings.Count > 0)
            rows.Add((["Price", "Listed", .. priced.Filters.Select(f => f.Text)], Dim));

        // Cheapest first at the valuation's rates, those it cannot price last: the site's own order is by its rates. See
        // TabletPricing.ListingsFetched.
        var cheapestFirst = _valuation == null
            ? priced.Listings
            : priced.Listings.OrderBy(l => ExaltsOf(l, _valuation) is var exalts && double.IsNaN(exalts) ? double.MaxValue : exalts).ToList();

        foreach (var listing in cheapestFirst.Take(5))
        {
            rows.Add(([
                Listed(listing),
                listing.Indexed == DateTime.MinValue ? "?" : Ago(DateTime.UtcNow - listing.Indexed),
                .. priced.Filters.Select(f => listing.Rolls != null && listing.Rolls.TryGetValue(f.Stat, out var roll)
                    ? roll
                    : "-"),
            ], Color.White));
        }

        var lines = new List<(string Text, Color Colour)>
        {
            // Its own search first, the same on every tablet; what else the price rests on; what was searched.
            (priced.Status, Color.White),
            (priced.Detail, Color.White),
            (priced.Searched, Dim),
        };

        lines.RemoveAll(l => l.Text.Length == 0);

        var height = graphics.MeasureText("Hg").Y + 2f;
        var widths = new float[rows.Count > 0 ? rows[0].Cells.Length : 0];

        foreach (var (cells, _) in rows)
        {
            for (var c = 0; c < cells.Length && c < widths.Length; c++)
                widths[c] = Math.Max(widths[c], graphics.MeasureText(cells[c]).X + 12f);
        }

        var width = Math.Max(widths.Sum(), lines.Select(l => graphics.MeasureText(l.Text).X).DefaultIfEmpty(0f).Max()) + 8f;
        var at = tooltip.Width > 0f ? new Vector2(tooltip.Left, tooltip.Bottom + 4f) : new Vector2(cell.Left, cell.Bottom + 4f);

        return new DetailLayout(new RectangleF(at.X, at.Y, width, height * (lines.Count + rows.Count) + 8f), rows, lines,
            widths, height);
    }

    /// <summary>The hover table's place on screen and its contents, measured. See LayOutDetail.</summary>
    private sealed record DetailLayout(RectangleF Rect, List<(string[] Cells, Color Colour)> Rows,
        List<(string Text, Color Colour)> Lines, float[] Widths, float Height);

    private static void DrawDetail(Graphics graphics, DetailLayout detail)
    {
        var (rect, rows, lines, widths, height) = detail;
        var at = new Vector2(rect.Left, rect.Top);

        graphics.DrawBox(at, at + new Vector2(rect.Width, rect.Height), LabelBackground);

        var y = at.Y + 4f;

        foreach (var (text, colour) in lines)
        {
            graphics.DrawText(text, new Vector2(at.X + 4f, y), colour);
            y += height;
        }

        foreach (var (cells, colour) in rows)
        {
            var x = at.X + 4f;

            for (var c = 0; c < cells.Length && c < widths.Length; c++)
            {
                graphics.DrawText(cells[c], new Vector2(x, y), colour);
                x += widths[c];
            }

            y += height;
        }
    }


    /// <summary>
    /// The currency right-clicked for use, as the tablet action it is, from IngameUi.Cursor's action string, each read
    /// off the cursor in an F6 dump (2026-10-09): "transmute_to_magic" Transmutation, "add_mod_to_magic" Augmentation,
    /// "transmute_to_rare" Alchemy, "upgrade_magic_to_rare" Regal, "add_mod_to_rare" Exalt.
    ///
    /// **Not the Cursor1 inventory, which was the first source tried.** A right-clicked currency stays where it is and
    /// the cursor changes mode, so Cursor1 read empty. Cursor.Action read 264 and 261, outside ExileCore2's
    /// MouseActionType, so the string is the field to trust.
    /// </summary>
    internal static TabletAction HeldOnCursor(GameController gc) => CursorAction(gc) switch
    {
        "transmute_to_magic" => TabletAction.Transmutation,
        "add_mod_to_magic" => TabletAction.Augmentation,
        "transmute_to_rare" => TabletAction.Alchemy,
        "upgrade_magic_to_rare" => TabletAction.Regal,
        "add_mod_to_rare" => TabletAction.Exalt,
        _ => TabletAction.None,
    };

    /// <summary>IngameUi.Cursor's action string, or blank.</summary>
    private static string CursorAction(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.Cursor?.ActionString, "") ?? "";

    /// <summary>
    /// The currency a tablet takes next. Unique and unreadable tablets take none. See the class summary for the order.
    /// </summary>
    /// <summary>
    /// A tablet's next action and junk verdict, worked out once for each read's record of it. Crafting needs, the sub-tab
    /// records, the labels and a run's steps each asked again for every tablet, every time: about 10ms and 7ms of a read
    /// for the first two alone, the junk verdict building the tablet's trade search to find its price (dump 2026-10-09
    /// 22:28). A record is a snapshot of one read, so what it is judged on cannot change under it; the next read's
    /// records are judged afresh, settings and prices then included.
    /// </summary>
    private sealed class TabletJudgement
    {
        public TabletAction? Action;
        public bool? Junk;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabletOnScreen, TabletJudgement> Judgements = new();

    /// <summary>What a tablet is to have done next. Worked out once per read's record of it. See TabletJudgement.</summary>
    internal static TabletAction ActionOf(TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var judged = Judgements.GetOrCreateValue(tablet);

        return judged.Action ??= ActionOfUnjudged(tablet, settings);
    }

    private static TabletAction ActionOfUnjudged(TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var prefixes = tablet.Modifiers.Count(x => x.IsPrefix);
        var suffixes = tablet.Modifiers.Count(x => x.IsSuffix);

        return tablet.Rarity switch
        {
            ItemRarity.Normal => TabletAction.Transmutation,
            // **An Augmentation only where it could make the tablet worth a Regal**: its one modifier and the heaviest row
            // of the missing affix must reach Regal threshold. A bad suffix with no prefix able to make up the rest is
            // rerolled with an Alchemy instead (2026-10-09).
            ItemRarity.Magic when prefixes == 0 || suffixes == 0 =>
                settings.ReachesWeight(tablet.Modifiers, prefixes == 0, suffixes == 0, Math.Max(1, settings.CraftingByHand.RegalThreshold.Value))
                    ? TabletAction.Augmentation
                    : TabletAction.Alchemy,
            ItemRarity.Magic => settings.IsGood(tablet.Modifiers) ? TabletAction.Regal : TabletAction.Alchemy,
            // **An Exalt only where it could make the tablet worth pricing**: its weight now, and the heaviest modifier
            // that could still roll into an open slot, must reach Minimum weight to keep. A tablet with item rarity, runic
            // markers and first runic rare, suffixes full, was marked though no prefix could take it past 58 of 101
            // (2026-10-09 19:36). With a suffix open the additional random map modifier, alone worth about 85c on a tablet
            // of unsearched modifiers, reaches it by its own weight. With only a prefix open, enough searched modifiers
            // must already be on it too.
            ItemRarity.Rare when tablet.Modifiers.Length < 4 =>
                settings.ReachesWeightToKeep(tablet.Modifiers, prefixes < 2, suffixes < 2) &&
                (suffixes < 2 || settings.SearchedCountOf(tablet.Modifiers) >= settings.CraftingByHand.MinimumSearchedModifiersForExalting.Value)
                    ? TabletAction.Exalt
                    : TabletAction.None,
            ItemRarity.Rare => TabletAction.Finished,
            _ => TabletAction.None,
        };
    }

    /// <summary>
    /// Every Expedition Tablet in the open stash tab, and in the inventory when asked. One entry per item entity: the
    /// stash's item list and the walk of its elements find the same tablet twice on an ordinary tab.
    /// </summary>
    private static List<TabletOnScreen> Read(GameController gc, bool inventory, bool merchant)
    {
        var found = new List<TabletOnScreen>();
        var entities = new HashSet<long>();
        var ui = Safe.Read(gc, static g => g.IngameState.IngameUi, null);

        if (ui == null)
            return found;

        var stash = Safe.Read(ui, static u => u.StashElement, null);

        _stashRect = default;
        _stashTabItemPathsAtRead = [];

        if (stash != null && Safe.Read(stash, static s => s.IsVisible, false))
        {
            _stashRect = Safe.Read(stash, static s => s.GetClientRectCache, default);

            foreach (var item in Safe.Read(stash, static s => s.VisibleStash?.VisibleInventoryItems, null) ?? [])
                Add(found, entities, "stash", item, Safe.Read(item, static i => i.Item, null));

            var panel = Safe.Read(stash, static s => s.StashInventoryPanel, null);
            var index = Safe.Read(stash, static s => s.IndexVisibleStash, -1);
            var tab = panel != null && index >= 0 ? Safe.Read(() => panel.Children[index], null) : null;

            if (tab != null)
            {
                using (Spent.On("Tablets/Read/Walk"))
                    Walk(found, entities, tab, paths: _stashTabItemPathsAtRead);
            }
        }

        // The merchant window is a StashElement too, its listed items in its visible tab.
        var shop = merchant ? Safe.Read(ui, static u => u.OfflineMerchantPanel, null) : null;

        if (shop != null && Safe.Read(shop, static s => s.IsVisible, false))
        {
            foreach (var item in Safe.Read(shop, static s => s.VisibleStash?.VisibleInventoryItems, null) ?? [])
                Add(found, entities, "merchant", item, Safe.Read(item, static i => i.Item, null));
        }

        if (inventory)
        {
            var panel = Safe.Read(ui, static u => u.InventoryPanel, null);

            if (panel != null && Safe.Read(panel, static p => p.IsVisible, false))
            {
                foreach (var item in Safe.Read(panel, static p => p[InventoryIndex.PlayerInventory]?.VisibleInventoryItems,
                             null) ?? [])
                    Add(found, entities, "inventory", item, Safe.Read(item, static i => i.Item, null));
            }
        }

        return found;
    }

    /// <summary>The visible stash tab's elements, depth first and bounded, for tablets the item list missed.</summary>
    /// <param name="paths">Where to add the metadata of every item met, when wanted.</param>
    private static void Walk(List<TabletOnScreen> found, HashSet<long> entities, Element tab, string where = "stash tab elements",
        List<string> paths = null)
    {
        var stack = new Stack<(Element Element, int Depth)>();
        var visited = 0;

        stack.Push((tab, 0));

        while (stack.Count > 0 && visited < MostElements)
        {
            var (element, depth) = stack.Pop();

            visited++;

            if (element == null || !Safe.Read(element, static e => e.IsVisible, false))
                continue;

            var entity = Safe.Read(element, static e => e.Entity, null);

            if (entity != null)
            {
                if (paths != null && Safe.Read(entity, static e => e.Metadata, null) is { Length: > 0 } path)
                    paths.Add(path);

                Add(found, entities, where, element, entity);
            }

            if (depth >= DeepestElement)
                continue;

            foreach (var child in Safe.Read(element, static e => e.Children, null) ?? [])
                stack.Push((child, depth + 1));
        }
    }


    private static void Add(List<TabletOnScreen> found, HashSet<long> entities, string where, Element element,
        Entity entity)
    {
        if (entity == null || element == null || !Safe.Read(element, static e => e.IsVisible, false))
            return;

        var path = Safe.Read(entity, static e => e.Metadata, "") ?? "";

        if (!path.StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase))
            return;

        var address = Safe.Read(entity, static e => e.Address, 0L);

        if (address == 0L || !entities.Add(address))
            return;

        var rect = Safe.Read(element, static e => e.GetClientRectCache, default);

        if (rect.Width <= 4f || rect.Height <= 4f)
            return;

        using var timing = Spent.On("Tablets/Read/Modifiers");
        var mods = Safe.Read(entity, static e => e.GetComponent<Mods>(), null);
        var rarity = Safe.Read(mods, static m => m.ItemRarity, ItemRarity.Unknown);
        var identified = Safe.Read(mods, static m => m.Identified, true);
        var hash = Safe.Read(mods, static m => m.Hash, 0L);
        var now = DateTime.UtcNow;

        CachedModifiers kept;

        // The dump reads the screen from Tick, the section from Render.
        lock (ModifierCache)
        {
            if (!ModifierCache.TryGetValue(address, out kept) || kept.Hash != hash || kept.Rarity != rarity ||
                kept.Identified != identified || now - kept.At > ModifiersKeptFor ||
                TabletsChanging.TryGetValue(address, out var until) && now < until)
            {
                kept = new CachedModifiers(hash, rarity, identified, ModifiersOf(mods), UsesOf(mods), now);

                // A modifier read without its id or values is not kept, so the next read tries again rather than use the
                // half-read tablet for five seconds. See FullyRead.
                if (kept.Modifiers.All(m => !string.IsNullOrWhiteSpace(m.Id) && m.Values.Length > 0))
                    ModifierCache[address] = kept;
                else
                    ModifierCache.Remove(address);
            }
        }

        found.Add(new TabletOnScreen(where, element, rect, rarity, kept.Modifiers, entity, kept.Uses, identified));
    }

    /// <summary>A tablet's modifiers and uses as last read, with what says whether they may have changed since.</summary>
    private sealed record CachedModifiers(long Hash, ItemRarity Rarity, bool Identified, TabletModifier[] Modifiers, int Uses,
        DateTime At);

    /// <summary>
    /// Each tablet's modifiers and uses by entity address, so a read of the screen does not build them again for every
    /// tablet. Building them was about 40ms of every 150ms read with a sub-tab of 95 tablets showing (dump 2026-10-09
    /// 22:03), and the frames it stalled made the cursor stutter during a run.
    ///
    /// Read again when the Mods component's hash, the rarity or the identified flag differ, when the entry is older than
    /// ModifiersKeptFor, and while a run has just clicked the tablet (TabletsChanging). Whether the hash changes when a
    /// currency adds a modifier has not been checked, so it is not relied on alone.
    /// </summary>
    private static readonly Dictionary<long, CachedModifiers> ModifierCache = [];

    /// <summary>How long a tablet's modifiers are used from ModifierCache before they are read again regardless.</summary>
    private static readonly TimeSpan ModifiersKeptFor = TimeSpan.FromSeconds(5);

    /// <summary>Tablets a run has just used a currency on, by address, read fresh until the time given. See ModifierCache.</summary>
    private static readonly Dictionary<long, DateTime> TabletsChanging = [];

    /// <summary>A tablet a run is about to change: its modifiers are read fresh for the next two seconds.</summary>
    private static void MarkTabletChanging(long address)
    {
        var now = DateTime.UtcNow;

        lock (ModifierCache)
        {
            foreach (var gone in TabletsChanging.Where(x => x.Value < now).Select(x => x.Key).ToList())
                TabletsChanging.Remove(gone);

            TabletsChanging[address] = now + TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>Drops cached modifiers not used for a minute. Called once per read of the screen.</summary>
    private static void PruneModifierCache()
    {
        lock (ModifierCache)
        {
            if (ModifierCache.Count < 1000)
                return;

            var old = DateTime.UtcNow - TimeSpan.FromMinutes(1);

            foreach (var gone in ModifierCache.Where(x => x.Value.At < old).Select(x => x.Key).ToList())
                ModifierCache.Remove(gone);
        }
    }

    /// <summary>
    /// A tablet's explicit modifiers. Prefix or suffix from ModRecord.AffixType. Text is the translation where the HUD
    /// can translate it, else the stat keys spelled out: on the first dump of Expedition Tablets (2026-10-09)
    /// DisplayName was the affix's name ("Brimming", "of Remnants") and most translations read "&lt;unknown ...&gt;".
    /// </summary>
    private static TabletModifier[] ModifiersOf(Mods mods)
    {
        var explicits = Safe.Read(mods, static m => m.ExplicitMods, null);

        if (explicits == null)
            return [];

        var result = new List<TabletModifier>(explicits.Count);

        foreach (var mod in explicits)
        {
            if (mod == null)
                continue;

            var id = Safe.Read(mod, static m => m.RawName, null);

            if (string.IsNullOrWhiteSpace(id))
                id = Safe.Read(mod, static m => m.ModRecord?.Key, null);

            if (string.IsNullOrWhiteSpace(id))
                id = Safe.Read(mod, static m => m.Name, "") ?? "";

            var affix = Safe.Read(mod, static m => m.ModRecord?.AffixType, null);
            var range = RangeOf(mod);

            result.Add(new TabletModifier(id, StemOf(id), affix == ModType.Prefix, affix == ModType.Suffix,
                Safe.Read(mod, static m => m.ModRecord?.Group ?? m.Group, "") ?? "",
                Safe.Read(mod, static m => m.Values?.ToArray(), null) ?? [],
                TextOf(mod), Clean(Safe.Read(mod, static m => m.DisplayName, "") ?? ""),
                range.Low, range.High));
        }

        return result.ToArray();
    }

    /// <summary>
    /// The range the mod's first value rolls in: ItemMod.ValuesMinMax, else the mod record's StatRange. (0, 0) when
    /// neither reads. The trade search's value steps are worked out from it. See TabletModifierRow.ValueSteps.
    /// </summary>
    private static (int Low, int High) RangeOf(ItemMod mod)
    {
        var range = Safe.Read(mod, static m => m.ValuesMinMax is { Length: > 0 } r ? (r[0].Min, r[0].Max) : (0, 0), (0, 0));

        if (range.Item1 == 0 && range.Item2 == 0)
            range = Safe.Read(mod, static m => m.ModRecord?.StatRange is { Length: > 0 } r ? (r[0].Min, r[0].Max) : (0, 0), (0, 0));

        return (Math.Min(range.Item1, range.Item2), Math.Max(range.Item1, range.Item2));
    }

    private static string TextOf(ItemMod mod)
    {
        var translated = Clean(Safe.Read(mod, static m => m.Translation, "") ?? "");

        if (translated.Length > 0 && !translated.Contains("<unknown", StringComparison.OrdinalIgnoreCase))
            return StripNumbers(translated);

        var stats = Safe.Read(mod, static m => m.ModRecord?.StatNames?.Select(x => x?.Key).Where(x => x != null).ToArray(),
            null) ?? [];

        return string.Join(", ", stats.Select(x => x.Replace('_', ' ')));
    }

    /// <summary>A translated line with its rolled numbers taken out, so one row reads the same for every roll of it.</summary>
    private static string StripNumbers(string text)
    {
        var b = new StringBuilder(text.Length);

        foreach (var c in text)
            b.Append(char.IsDigit(c) ? '#' : c);

        return System.Text.RegularExpressions.Regex.Replace(b.ToString(), "#+", "#");
    }

    /// <summary>
    /// A tablet's uses remaining: the value of its implicit TowerAddExpeditionToMapsImplicit, 10 on every tablet read on
    /// 2026-10-09. -1 when it cannot be read.
    /// </summary>
    private static int UsesOf(Mods mods)
    {
        foreach (var mod in Safe.Read(mods, static m => m.ImplicitMods, null) ?? [])
        {
            var name = Safe.Read(mod, static m => m.RawName, "") ?? "";

            if (name.StartsWith("TowerAdd", StringComparison.OrdinalIgnoreCase) &&
                Safe.Read(mod, static m => m.Values, null) is { Count: > 0 } values)
                return values[0];
        }

        return -1;
    }

    /// <summary>A mod id without its trailing tier digits: TowerMonsterEffectiveness3 is TowerMonsterEffectiveness.</summary>
    internal static string StemOf(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";

        var end = id.Length;

        while (end > 0 && char.IsDigit(id[end - 1]))
            end--;

        return id[..end];
    }

    /// <summary>Text with the game's [Tag|Shown] and &lt;style&gt;{text} markup reduced to what is shown.</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var b = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);

                if (close > i)
                {
                    var inner = text[(i + 1)..close];
                    var bar = inner.LastIndexOf('|');

                    b.Append(bar >= 0 ? inner[(bar + 1)..] : inner);
                    i = close + 1;

                    continue;
                }
            }

            if (text[i] == '<')
            {
                var open = text.IndexOf('{', i);
                var close = open > i ? text.IndexOf('}', open) : -1;

                if (open > i && close > open)
                {
                    b.Append(text, open + 1, close - open - 1);
                    i = close + 1;

                    continue;
                }
            }

            b.Append(text[i]);
            i++;
        }

        return b.ToString().Trim();
    }

    /// <summary>
    /// Four filled bands growing inwards from a gap inside the cell, as the Ritual Roll Ranges plugin draws them: a
    /// stroked rectangle is centred on its path, so half its thickness would spill onto the neighbouring cell.
    /// </summary>
    private static void DrawInsetBorder(Graphics graphics, RectangleF cell, int thickness, Color colour)
    {
        var (left, top) = (cell.Left + BorderGap, cell.Top + BorderGap);
        var (right, bottom) = (cell.Right - BorderGap, cell.Bottom - BorderGap);

        if (right - left <= thickness * 2 || bottom - top <= thickness * 2)
            return;

        graphics.DrawBox(new Vector2(left, top), new Vector2(right, top + thickness), colour);
        graphics.DrawBox(new Vector2(left, bottom - thickness), new Vector2(right, bottom), colour);
        graphics.DrawBox(new Vector2(left, top + thickness), new Vector2(left + thickness, bottom - thickness), colour);
        graphics.DrawBox(new Vector2(right - thickness, top + thickness), new Vector2(right, bottom - thickness), colour);
    }

    /// <summary>
    /// IngameUi.Cursor and what hangs off it, and the visible stash's Highlights. Kept for the currencies not yet read
    /// from the cursor's action string - a Regal, Alchemy or Exalt - should borders be wanted for them. See HeldOnCursor.
    /// </summary>
    private static string CursorElement(GameController gc)
    {
        var b = new StringBuilder();
        var cursor = Safe.Read(gc, static g => g.IngameState.IngameUi.Cursor, null);

        b.AppendLine($"  IngameUi.Cursor: {(cursor == null ? "null" : $"action {Safe.Read(cursor, static c => c.Action.ToString(), "?")}, " +
                                                                    $"action string \"{Safe.Read(cursor, static c => c.ActionString, "")}\", " +
                                                                    $"clicks {Safe.Read(cursor, static c => c.Clicks, -1)}, " +
                                                                    $"entity {Safe.Read(cursor, static c => c.Entity?.Metadata, null) ?? "none"}")}");

        if (cursor != null)
            DescribeChildren(b, cursor, 1, 4);

        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement?.VisibleStash, null);
        var highlights = Safe.Read(stash, static s => s.Highlights, null);

        b.AppendLine($"  visible stash: {Safe.Read(stash, static s => s.InvType.ToString(), "none")}, nested " +
                     $"{Safe.Read(stash, static s => s.IsNestedInventory, false)}, highlights " +
                     (highlights == null ? "unreadable" : $"{highlights.Count}: " +
                         string.Join(" ", highlights.Take(20).Select(h => $"({h.Position.X},{h.Position.Y}) {h.Color}"))));

        return b.ToString().TrimEnd();
    }

    private static void DescribeChildren(StringBuilder b, Element element, int depth, int deepest)
    {
        var children = Safe.Read(element, static e => e.Children, null) ?? [];

        foreach (var child in children.Take(12))
        {
            var rect = Safe.Read(child, static c => c.GetClientRectCache, default);

            b.AppendLine($"  {new string(' ', depth * 2)}child: visible {Safe.Read(child, static c => c.IsVisible, false)}, " +
                         $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}), " +
                         $"entity {Safe.Read(child, static c => c.Entity?.Metadata, null) ?? "none"}, " +
                         $"text \"{Safe.Read(child, static c => c.Text, "")}\"");

            if (depth < deepest)
                DescribeChildren(b, child, depth + 1, deepest);
        }
    }

    /// <summary>
    /// Every tablet the last read found, with each modifier's id, prefix or suffix, group, values and the three texts
    /// the game offers for it, for building the modifier table's defaults from real tablets. See Dump.
    /// </summary>
    internal static string Describe(GameController gc, TabletRerollingSettings settings)
    {
        var b = new StringBuilder();
        var seen = Read(gc, true, true);

        b.AppendLine($"  {seen.Count} Expedition Tablet(s) on screen (stash tab, its elements, and the inventory when open)");
        b.AppendLine($"  cursor action: \"{CursorAction(gc)}\" - read as {HeldOnCursor(gc)}");
        b.AppendLine(CursorElement(gc));

        var mouse = ImGuiNET.ImGui.GetMousePos();
        var hovered = seen.FirstOrDefault(t => mouse.X >= t.Rect.Left && mouse.X <= t.Rect.Right &&
                                               mouse.Y >= t.Rect.Top && mouse.Y <= t.Rect.Bottom);
        var raw = hovered == null
            ? default
            : Safe.Read(hovered.Element, static e => e.Tooltip is { } t ? t.GetClientRectCache : default, default);

        b.AppendLine(hovered == null
            ? $"  hovered tablet: none (cursor {mouse.X:0},{mouse.Y:0})"
            : $"  hovered tablet ({hovered.Rect.X:0},{hovered.Rect.Y:0} {hovered.Rect.Width:0}x{hovered.Rect.Height:0}); its tooltip " +
              $"visible {Safe.Read(hovered.Element, static e => e.Tooltip?.IsVisible ?? false, false)} at " +
              $"({raw.X:0},{raw.Y:0} {raw.Width:0}x{raw.Height:0}), used as ({TooltipOf(hovered).X:0},{TooltipOf(hovered).Y:0} " +
              $"{TooltipOf(hovered).Width:0}x{TooltipOf(hovered).Height:0})");
        if (hovered != null)
            b.Append(HoveredTabletTierSaid(hovered));

        b.AppendLine($"  trade site: {TabletPricing.Status}, {TabletPricing.Pending} waiting; {TabletPricing.Limits}");
        b.AppendLine($"  {PricingRunSaid}");
        b.AppendLine($"  {MerchantSaid(gc)}");
        b.Append(ReforgingSaid(gc, settings));
        b.Append(StashElementsUnderCursorSaid(gc));
        b.Append(StashTabsSaid(gc));
        b.Append(FragmentTabControlsSaid(gc));
        b.Append(HideoutObjectsSaid(gc));

        // Each inventory tablet's cell, matched by the slot's own screen rectangle, as the identification recorder and the
        // crafting run's deposit match them. "no cell" means the two rectangles do not agree. See CentreWithin.
        var slots = CarriedByCell(gc);

        foreach (var tablet in seen)
        {
            var cell = tablet.Where != "inventory" ? ""
                : slots.FirstOrDefault(s => CentreWithin(tablet.Rect, s.Rect)) is { Rect.Width: > 0f } slot
                    ? $" cell ({slot.Cell.X},{slot.Cell.Y})"
                    : " no cell";

            b.AppendLine($"  {tablet.Where}{cell} ({tablet.Rect.X:0},{tablet.Rect.Y:0} {tablet.Rect.Width:0}x{tablet.Rect.Height:0}) " +
                         $"{tablet.Rarity}, {tablet.Uses} uses, {tablet.Modifiers.Length} explicit(s), next: {ActionOf(tablet, settings)}" +
                         $"{(tablet.Rarity == ItemRarity.Magic && tablet.Modifiers.Length == 2 ? $" (weight {settings.WeightOf(tablet.Modifiers)} of {settings.CraftingByHand.RegalThreshold.Value})" : "")}");

            var mods = Safe.Read(tablet.Entity, static e => e.GetComponent<Mods>(), null);
            var (metadata, baseName, itemLevel) = ItemDetailsOfTablet(tablet.Entity);
            var tier = tablet.Rarity != ItemRarity.Normal && !Safe.Read(mods, static m => m.Identified, true)
                ? $", tooltip tier {TierOfTabletTooltip(tablet.Element)}"
                : "";

            b.AppendLine($"    metadata {metadata}, base \"{baseName}\", item level {itemLevel}{tier}, " +
                         $"render name \"{Safe.Read(tablet.Entity, static e => e.RenderName, "")}\"");

            foreach (var mod in Safe.Read(mods, static m => m.ExplicitMods, null) ?? [])
            {
                var record = Safe.Read(mod, static m => m.ModRecord, null);
                var stats = Safe.Read(record, static r => string.Join(", ", r.StatNames?.Select(x => x?.Key) ?? []), "");

                b.AppendLine($"    {Safe.Read(mod, static m => m.RawName, "?")} [key {Safe.Read(record, static r => r.Key, "?")}] " +
                             $"{Safe.Read(record, static r => r.AffixType.ToString(), "?")}, group {Safe.Read(record, static r => r.Group, "?")}, " +
                             $"values [{string.Join(", ", Safe.Read(mod, static m => m.Values, null) ?? [])}] range " +
                             $"{RangeOf(mod).Low}-{RangeOf(mod).High}, " +
                             $"display \"{Safe.Read(mod, static m => m.DisplayName, "")}\", " +
                             $"translation \"{Safe.Read(mod, static m => m.Translation, "")}\", stats [{stats}]");
            }

            foreach (var mod in Safe.Read(mods, static m => m.ImplicitMods, null) ?? [])
                b.AppendLine($"    implicit {Safe.Read(mod, static m => m.RawName, "?")} values " +
                             $"[{string.Join(", ", Safe.Read(mod, static m => m.Values, null) ?? [])}]");
        }

        return b.ToString().TrimEnd();
    }

    /// <summary>
    /// For finding where an unidentified tablet's tier ("Expedition Tablet (Tier 4)") is kept: the hovered tablet's
    /// tooltip text, every modifier the item holds (ItemMods, which may include those hidden until identified) with its
    /// level, and the rest of its Mods component. Tier 2 and tier 4 tablets read the same base, item level and implicit
    /// (dumps 2026-10-09 20:38:29 and 20:38:35), so none of those is it.
    /// </summary>
    private static string HoveredTabletTierSaid(TabletOnScreen hovered)
    {
        var b = new StringBuilder();
        var mods = Safe.Read(hovered.Entity, static e => e.GetComponent<Mods>(), null);

        b.AppendLine($"    mods: identified {Safe.Read(mods, static m => m.Identified, false)}, item level " +
                     $"{Safe.Read(mods, static m => m.ItemLevel, -1)}, required level {Safe.Read(mods, static m => m.RequiredLevel, -1)}, " +
                     $"unique name \"{Safe.Read(mods, static m => m.UniqueName, "")}\", hash {Safe.Read(mods, static m => m.Hash, 0L)}");

        foreach (var mod in Safe.Read(mods, static m => m.ItemMods, null) ?? [])
            b.AppendLine($"    item mod {Safe.Read(mod, static m => m.RawName, "?")} level {Safe.Read(mod, static m => m.Level, -1)}, " +
                         $"group {Safe.Read(mod, static m => m.Group, "?")}, values [{string.Join(", ", Safe.Read(mod, static m => m.Values, null) ?? [])}], " +
                         $"affix {Safe.Read(mod, static m => m.ModRecord?.AffixType.ToString(), "?")}");

        foreach (var stat in Safe.Read(mods, static m => m.HumanStats, null) ?? [])
            b.AppendLine($"    human stat \"{stat}\"");

        var tooltip = Safe.Read(hovered.Element, static e => e.Tooltip, null);
        var texts = new List<string>();
        var stack = new Stack<(ExileCore2.PoEMemory.Element Element, int Depth)>();

        if (tooltip != null)
            stack.Push((tooltip, 0));

        while (stack.Count > 0 && texts.Count < 80)
        {
            var (element, depth) = stack.Pop();
            var text = (Safe.Read(element, static e => e.Text, null) ?? "").Trim();

            if (text.Length > 0)
                texts.Add($"{new string(' ', depth)}\"{text.Replace("\n", " / ")}\"");

            var children = Safe.Read(element, static e => e.Children, null) ?? [];

            for (var i = children.Count - 1; i >= 0 && depth < 12; i--)
                stack.Push((children[i], depth + 1));
        }

        b.AppendLine(tooltip == null ? "    tooltip: none" : $"    tooltip text ({texts.Count} piece(s), depth-first):");

        foreach (var text in texts)
            b.AppendLine($"      {text}");

        return b.ToString();
    }
}
