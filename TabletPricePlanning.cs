using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Enums;
using Newtonsoft.Json.Linq;
using Graphics = ExileCore2.Graphics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Which trade searches price the tablets, and what each tablet's price reads as from the answers so far.
///
/// **Nothing is searched until the action key is pressed.** Every finished tablet seen in any stash tab or the
/// inventory is collected. The key over a tablet searches that tablet at its own rolls; the key over anything else
/// starts a pricing run over every collected tablet, planned per modifier set so that one search can bound many
/// tablets:
///
/// - A modifier set held by one tablet is searched at that tablet's rolls.
/// - A modifier set held by several is first searched at the highest roll of each modifier among them - the ceiling.
///   Every tablet of the set is worth at most its price, so a low value ceiling settles them all with one search.
/// - Otherwise it is searched at the lowest roll of each modifier among them - the floor - and every tablet of the set
///   is worth at least that.
///
/// A tablet then shows "5d" for a price from its own search, "&lt;5d" at most, "&gt;5d" at least, "..." while its run is
/// still searching and "?" when it has not been asked for. See TabletPricing.RelatedAnswers for why the bounds hold.
/// </summary>
internal static partial class Tablets
{
    /// <summary>
    /// One searched modifier: its trade stat, the least roll searched or null for any, its column name, and its row's
    /// weight, which orders the hover table's columns.
    /// </summary>
    internal sealed record SearchFilter(string Stat, int? Min, string Text, int Weight, IReadOnlyList<int?> Lower);

    /// <summary>
    /// What a tablet is searched as: league, rarity, the most modifiers (magic only, else 0), uses, its searched
    /// modifiers sorted by stat, its weight, which orders the searches, and each of its modifiers' weights,
    /// heaviest first.
    /// </summary>
    internal sealed record TabletSearch(string League, string Rarity, int MostModifiers, int Uses,
        IReadOnlyList<SearchFilter> Filters, int Weight, IReadOnlyList<int> ModifierWeights)
    {
        internal string WeightSaid => WeightSaidOf(ModifierWeights);

        private string Prefix => $"{League}|{Rarity}{(MostModifiers > 0 ? $"|mods<={MostModifiers}" : "")}|uses={Uses}";

        /// <summary>The prefix and the stats without their minimums: tablets that one search can bound.</summary>
        internal string ModifierSet => Prefix + "|" + string.Join(";", Filters.Select(f => f.Stat));

        internal string Key => KeyOf(Filters);

        /// <summary>How many steps lower this tablet can be searched. See SearchFilter.Lower.</summary>
        internal int LowerLevels => Filters.Max(f => f.Lower.Count);

        /// <summary>The filters searched a number of steps lower, each modifier as far as it goes.</summary>
        internal List<SearchFilter> LowerFilters(int level) =>
            Filters.Select(f => f.Lower.Count == 0 ? f : f with { Min = f.Lower[Math.Min(level, f.Lower.Count) - 1] }).ToList();

        /// <summary>The cache key of a search of this tablet's modifier set at these minimums. See TabletPricing.PartsOf.</summary>
        internal string KeyOf(IEnumerable<SearchFilter> filters) =>
            Prefix + "|" + string.Join(";", filters.Select(f => f.Min is { } at ? $"{f.Stat}>={at}" : f.Stat));

        /// <summary>A search, as words for the hover table, heaviest modifier first like its columns.</summary>
        internal string DescribedAs(IEnumerable<SearchFilter> filters) =>
            $"{Uses} uses, {Rarity}{(MostModifiers > 0 ? $", at most {MostModifiers} modifier(s)" : "")}: " +
            string.Join(", ", filters.OrderByDescending(f => f.Weight).ThenBy(f => f.Stat, StringComparer.Ordinal)
                .Select(f => f.Min is { } at ? $"{f.Text} {at}+" : f.Text));

        /// <summary>The trade site's search body for this tablet's modifier set at these minimums: instant buyout only.</summary>
        internal string BodyOf(IEnumerable<SearchFilter> filters)
        {
            var stats = new JArray(new JObject
            {
                ["id"] = UsesStat,
                ["value"] = new JObject { ["min"] = Uses, ["max"] = Uses },
            });

            if (MostModifiers > 0)
                stats.Add(new JObject { ["id"] = ModifierCountStat, ["value"] = new JObject { ["max"] = MostModifiers } });

            foreach (var filter in filters)
            {
                stats.Add(filter.Min is { } at
                    ? new JObject { ["id"] = filter.Stat, ["value"] = new JObject { ["min"] = at } }
                    : new JObject { ["id"] = filter.Stat });
            }

            return new JObject
            {
                ["query"] = new JObject
                {
                    // Instant buyout only: "securable" is the site's name for it.
                    ["status"] = new JObject { ["option"] = "securable" },
                    ["type"] = "Expedition Tablet",
                    ["stats"] = new JArray(new JObject { ["type"] = "and", ["filters"] = stats }),
                    ["filters"] = JObject.Parse("{\"type_filters\":{\"filters\":{\"rarity\":{\"option\":\"" + Rarity + "\"}}}}"),
                },
                ["sort"] = new JObject { ["price"] = "asc" },
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>
    /// "Weight: 151 (100+50+1+0)": the sum of a tablet's modifier weights, then each, heaviest first like the hover
    /// table's columns. "Weight: 0" alone when every part is nought.
    /// </summary>
    private static string WeightSaidOf(IReadOnlyList<int> parts) =>
        parts.Any(x => x != 0) ? $"Weight: {parts.Sum()} ({string.Join("+", parts)})" : "Weight: 0";

    /// <summary>The label of a collected tablet no search has been asked for: press the action key to price it.</summary>
    private const string NotQueuedLabel = "?";

    /// <summary>
    /// How far right of the inventory panel's left edge and down from its top the pricing progress line and the crafting
    /// requirements under it sit before their offset sliders, under the gold. Drawn whenever the inventory is open: it
    /// sat on the stash, 1690 and 1230 from its corner, and showed only with the stash open. The same place on screen,
    /// the stash at (0,0) and the inventory at (1673,0) (dump 2026-10-10 16:14:55).
    /// </summary>
    internal const int ProgressRightOfInventory = 17;

    /// <inheritdoc cref="ProgressRightOfInventory"/>
    internal const int ProgressBelowInventoryTop = 1230;

    /// <summary>The inventory panel's rectangle, empty when it is closed. The progress line hangs from it.</summary>
    private static RectangleF InventoryRect(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel is { IsVisible: true } panel ? panel.GetClientRectCache : default,
            default);

    /// <summary>The "?" of a tablet no search found a single listing for, at its own rolls or any lower. Chosen.</summary>
    private static readonly Color NoneListedColour = Color.FromArgb(255, 190, 110, 255);

    /// <summary>The label of a tablet whose search is queued or running.</summary>
    private const string QueuedLabel = "...";

    /// <summary>How often a pricing run is planned again: often enough to follow each answer, rarely enough to be cheap.</summary>
    private static readonly TimeSpan PlanEvery = TimeSpan.FromSeconds(1);

    /// <summary>How long the progress line stays up after a run ends, saying it has. The solver's run's, Placement.Linger, as well.</summary>
    private static readonly TimeSpan RunEndedShownFor = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Every finished tablet seen since the plugin loaded, by FingerprintOf, with its search. Kept in memory only: the
    /// stash changes between sessions.
    /// </summary>
    private static readonly Dictionary<string, TabletSearch> Collected = new(StringComparer.Ordinal);

    /// <summary>
    /// The collected tablets seen only in the merchant window, by fingerprint: left out of a pricing run unless
    /// IncludeMerchantTabletsInPricingRuns. One seen anywhere else is taken off.
    /// </summary>
    private static readonly HashSet<string> MerchantOnly = new(StringComparer.Ordinal);

    /// <summary>The collected tablets the running pricing run has still to settle, by fingerprint. Empty when none runs.</summary>
    private static readonly HashSet<string> PricingRun = new(StringComparer.Ordinal);

    private static int _pricingRunTotal;

    private static DateTime _pricingRunEnded = DateTime.MinValue;

    private static DateTime _planned = DateTime.MinValue;

    /// <summary>Searches the running pricing run may still send, at most: for the progress line's minutes.</summary>
    private static int _searchesLeft;

    private static bool _actionKeyPressed;

    /// <summary>The action key's name as bound, for the hover table's text. Set each draw from the settings.</summary>
    private static string _actionKeyName = "F4";

    /// <summary>"just now" under a minute, then "1 minute ago", "5 hours ago", "2 days ago".</summary>
    private static string AgeInWords(TimeSpan age)
    {
        static string Count(double value, string unit)
        {
            var whole = Math.Max(1, (int)Math.Floor(value));

            return $"{whole} {unit}{(whole == 1 ? "" : "s")} ago";
        }

        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? Count(age.TotalMinutes, "minute")
            : age.TotalDays < 1 ? Count(age.TotalHours, "hour")
            : Count(age.TotalDays, "day");
    }

    /// <summary>
    /// Whether the action key belongs to the tablets now: rerolling is on and the player is in a hideout or a town,
    /// where there is no dig site for the key to solve or place at. Anywhere else it is the solver's.
    /// </summary>
    internal static bool ActionKeyIsForTablets(GameController gc, AutoExpeditionSettings settings) =>
        settings.TabletRerolling.EnableTabletRerolling &&
        Safe.Read(gc, static g => g.Area.CurrentArea is { } area && (area.IsHideout || area.IsTown), false);

    /// <summary>What the action key over a tablet did, said under the cursor for KeyNoteFor. See ActOnActionKey.</summary>
    private static string _keyNote = "";

    private static DateTime _keyNoteAt = DateTime.MinValue;

    private static readonly TimeSpan KeyNoteFor = TimeSpan.FromSeconds(2);

    private static void KeyNote(string note)
    {
        _keyNote = note;
        _keyNoteAt = DateTime.UtcNow;
    }

    /// <summary>The action key's note under the cursor, while fresh. See KeyNote.</summary>
    private static void DrawKeyNote(Graphics graphics, GameController gc)
    {
        if (DateTime.UtcNow - _keyNoteAt < KeyNoteFor)
            DrawUnderCursor(graphics, gc, _keyNote, Color.White);
    }

    /// <summary>The action key tapped in a hideout or town. Handled on the next draw. See ActOnActionKey.</summary>
    internal static void ActionKeyPressed() => _actionKeyPressed = true;

    /// <summary>The tablet under the cursor at the last draw, or null.</summary>
    private static TabletOnScreen _hoveredAtDraw;

    /// <summary>
    /// The action key held over a priced tablet: its own search opened on the trade site in the browser. False, doing
    /// nothing, with no such tablet under the cursor. See TabletPricing.OpenInBrowser.
    /// </summary>
    internal static bool OpenHoveredOnTradeSite(GameController gc, AutoExpeditionSettings settings)
    {
        if (HoveredSearch(gc, settings) is not { } search)
            return false;

        TabletPricing.OpenInBrowser(search.Key, search.League, search.BodyOf(search.Filters),
            settings.TabletRerolling.TradeSite.HoveredSearchesPerFiveMinutes.Value, settings.TabletRerolling);

        return true;
    }

    /// <summary>The search of the priced tablet under the cursor at the last draw, or null. See OpenHoveredOnTradeSite.</summary>
    internal static TabletSearch HoveredSearch(GameController gc, AutoExpeditionSettings settings)
    {
        var tablets = settings.TabletRerolling;

        return _hoveredAtDraw is { } hovered && _valuation != null && IsPriced(hovered, ActionOf(hovered, tablets), tablets, _valuation)
            ? SearchOfTablet(gc, hovered, tablets, out _)
            : null;
    }

    /// <summary>A tablet's identity for collecting: rarity, uses and every explicit modifier with its values.</summary>
    private static string FingerprintOf(TabletOnScreen tablet) =>
        $"{tablet.Rarity}|{tablet.Uses}|" + string.Join(";", tablet.Modifiers.Select(m => m.Id + "=" + string.Join(",", m.Values)));

    /// <summary>
    /// What a finished tablet is searched as: its uses and its searchable modifiers, those whose row is ticked Search
    /// and has a trade stat, each at SearchMinimum. A modifier under its row's IgnoreOtherModRangesBelow means the tablet
    /// will be rerolled with a Divine Orb, so the others are searched without values. Null, with the reason, for a
    /// tablet under Minimum weight to keep, one with nothing searchable, and when there is no league.
    /// </summary>
    private static TabletSearch SearchOfTablet(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings,
        out string reason)
    {
        var weight = settings.WeightOf(tablet.Modifiers);
        var parts = tablet.Modifiers.Select(m => settings.RowOf(m)?.Weight ?? 0).OrderByDescending(x => x).ToList();

        if (weight < settings.Prices.MinimumWeightToKeep.Value)
        {
            reason = $"Not priced, under minimum weight to keep: {settings.Prices.MinimumWeightToKeep.Value}. {WeightSaidOf(parts)}";
            return null;
        }

        // **A searched modifier read without its value means the tablet is not read yet.** Its weight still counts it,
        // so dropping it from the search priced the rest alone: a tablet past the weight minimum on additional remnant
        // and rare monsters was searched for rare monsters 30+ only, which no tablet weighs enough to be priced on
        // (2026-10-09 19:18:24; the tablet itself was not caught in a dump, so how it read so is inferred).
        if (tablet.Modifiers.Any(m => settings.RowOf(m) is { Search: true, TradeStat.Length: > 0 } && m.Values.Length == 0))
        {
            reason = "Not priced, modifiers not read yet";
            return null;
        }

        var searchable = tablet.Modifiers
            .Select(m => (Modifier: m, Row: settings.RowOf(m)))
            .Where(x => x.Row is { Search: true, TradeStat.Length: > 0 })
            .ToList();

        var divined = searchable.FirstOrDefault(x =>
            x.Row.IgnoreOtherModRangesBelow > 0 && x.Modifier.Values[0] < x.Row.IgnoreOtherModRangesBelow);
        var filters = searchable
            .Select(x => new SearchFilter(x.Row.TradeStat,
                divined.Row != null && !ReferenceEquals(x.Row, divined.Row) ? null : TabletRerollingSettings.SearchMinimum(x.Row, x.Modifier),
                x.Row.ShortName.Length > 0 ? x.Row.ShortName : x.Row.Id, x.Row.Weight,
                TabletRerollingSettings.LowerSearchMinimums(x.Row, x.Modifier,
                    divined.Row != null && !ReferenceEquals(x.Row, divined.Row) ? null : TabletRerollingSettings.SearchMinimum(x.Row, x.Modifier))))
            .OrderBy(f => f.Stat, StringComparer.Ordinal)
            .ToList();

        if (filters.Count == 0)
        {
            reason = "no searched modifier on it";
            return null;
        }

        var league = settings.TradeSite.TradeLeague.Value is { Length: > 0 } chosen
            ? chosen
            : Safe.Read(gc, static g => g.IngameState.ServerData.League, "") ?? "";

        if (league.Length == 0)
        {
            reason = "no league to search: set Trade league";
            return null;
        }

        reason = "";

        // Against tablets of its own rarity, and a magic one against tablets with no more modifiers than it: a
        // one-modifier tablet is a different item from a two-modifier one.
        return new TabletSearch(league, tablet.Rarity == ItemRarity.Magic ? "magic" : "rare",
            tablet.Rarity == ItemRarity.Magic ? tablet.Modifiers.Length : 0, tablet.Uses, filters, weight,
            parts);
    }

    /// <summary>Collects the priced tablets on screen. See Collected.</summary>
    private static void CollectTablets(GameController gc, TabletRerollingSettings settings, Valuation valuation)
    {
        foreach (var tablet in Seen)
        {
            if (settings.Prices.IgnoreUsedTablets && tablet.Uses < FullUses || !IsPriced(tablet, ActionOf(tablet, settings), settings, valuation))
                continue;

            if (SearchOfTablet(gc, tablet, settings, out _) is not { } search)
                continue;

            var fingerprint = FingerprintOf(tablet);

            if (tablet.Where == "merchant")
            {
                if (!Collected.ContainsKey(fingerprint))
                    MerchantOnly.Add(fingerprint);
            }
            else
                MerchantOnly.Remove(fingerprint);

            Collected[fingerprint] = search;
        }
    }

    /// <summary>
    /// The action key: over a priced tablet, searches it at its own rolls, first of all, unless its own price is younger
    /// than HoveredRefreshMinimumMinutes; over anything else, starts a pricing run over every collected tablet, which
    /// does not search again a price still fresh.
    /// </summary>
    private static void ActOnActionKey(GameController gc, TabletOnScreen hovered, TabletRerollingSettings settings,
        Valuation valuation)
    {
        if (hovered != null && !(settings.Prices.IgnoreUsedTablets && hovered.Uses < FullUses) &&
            IsPriced(hovered, ActionOf(hovered, settings), settings, valuation) &&
            SearchOfTablet(gc, hovered, settings, out _) is { } search)
        {
            // Searched again whatever its freshness, unless its own price is younger than Hovered refresh minimum age;
            // then, when its own search found too few listings, one step lower. It joins the pricing run so the steps
            // after that follow on their own.
            // Each case says what it did under the cursor, so a press that searches nothing is not taken for one that failed.
            if (TabletPricing.Cached(search.Key) is not { Error.Length: 0 } own ||
                DateTime.UtcNow - own.When > TimeSpan.FromMinutes(settings.TradeSite.HoveredRefreshMinimumMinutes.Value))
            {
                TabletPricing.Enqueue(search.Key, search.League, search.BodyOf(search.Filters),
                    static _ => false, long.MaxValue, settings.TradeSite.HoveredSearchesPerFiveMinutes.Value, settings);
                KeyNote("Searching...");
            }
            else if (NextLowerFilters(search, settings, valuation) is { } lower)
            {
                TabletPricing.Enqueue(search.KeyOf(lower), search.League, search.BodyOf(lower),
                    static _ => false, long.MaxValue, settings.TradeSite.HoveredSearchesPerFiveMinutes.Value, settings);
                KeyNote("Searching lower rolls...");
            }
            else
                KeyNote($"Priced {AgeInWords(DateTime.UtcNow - own.When)}" +
                        (own.Listings.Count < settings.ListingsWanted ? $": {(own.Total == 0 ? "none" : own.Total.ToString())} listed at these rolls" : ""));

            if (PricingRun.Add(FingerprintOf(hovered)))
            {
                _pricingRunTotal = PricingRun.Count == 1 ? 1 : _pricingRunTotal + 1;
                _planned = DateTime.MinValue;
            }

            return;
        }

        PricingRun.Clear();
        PricingRun.UnionWith(Collected.Keys.Where(x => settings.TradeSite.IncludeMerchantTabletsInPricingRuns || !MerchantOnly.Contains(x)));
        _pricingRunTotal = PricingRun.Count;
        _planned = DateTime.MinValue;
    }

    /// <summary>An answer that can be relied on now: no error, and fresh by FreshFor. Stale ones are still drawn.</summary>
    private static bool IsUsable(TabletPricing.Answer answer, TabletRerollingSettings settings, Valuation valuation) =>
        answer is { Error.Length: 0 } && DateTime.UtcNow - answer.When <= settings.FreshFor(DivinesOf(answer, settings, valuation));

    private static double DivinesOf(TabletPricing.Answer answer, TabletRerollingSettings settings, Valuation valuation) =>
        PriceOfAnswer(answer, settings, valuation) / valuation.PerExalt("Divine");

    /// <summary>
    /// Whether an answer, as a ceiling, settles a looser search: usable, with as many listings as Price from takes, and
    /// a cheap price (Cheap price up to).
    /// </summary>
    private static bool SettlesAsCeiling(TabletPricing.Answer answer, TabletRerollingSettings settings, Valuation valuation) =>
        IsUsable(answer, settings, valuation) && answer.Listings.Count >= settings.ListingsWanted &&
        DivinesOf(answer, settings, valuation) <= settings.TradeSite.CheapPriceUpToDivine.Value;

    /// <summary>Whether a tablet still wants a search: no usable answer of its own, and no settling ceiling.</summary>
    private static bool NeedsSearch(TabletSearch search, TabletRerollingSettings settings, Valuation valuation) =>
        IsUsable(TabletPricing.Cached(search.Key), settings, valuation)
            ? NextLowerFilters(search, settings, valuation) != null
            : !TabletPricing.RelatedAnswers(search.Key).Any(x => x.Tighter && SettlesAsCeiling(x.Answer, settings, valuation));

    /// <summary>
    /// The next lower search a tablet wants, or null: when its own search found fewer listings than Price from takes,
    /// the first step down whose answer is not in yet, unless a step already found enough or there are no steps left.
    /// A lower search is looser, so its price shows as a floor. See TabletSearch.LowerFilters.
    /// </summary>
    private static List<SearchFilter> NextLowerFilters(TabletSearch search, TabletRerollingSettings settings, Valuation valuation)
    {
        if (TabletPricing.Cached(search.Key) is not { Error.Length: 0 } own || own.Listings.Count >= settings.ListingsWanted)
            return null;

        for (var level = 1; level <= search.LowerLevels; level++)
        {
            var filters = search.LowerFilters(level);
            var answer = TabletPricing.Cached(search.KeyOf(filters));

            if (!IsUsable(answer, settings, valuation))
                return filters;

            if (answer.Listings.Count >= settings.ListingsWanted)
                return null;
        }

        return null;
    }

    /// <summary>
    /// Plans the running pricing run: settles what the answers so far settle, and queues the next search of each
    /// modifier set that still needs one. See the class summary for the order.
    /// </summary>
    private static void PlanPricingRun(TabletRerollingSettings settings, Valuation valuation)
    {
        _planned = DateTime.UtcNow;
        _searchesLeft = 0;

        bool Settles(TabletPricing.Answer answer) => SettlesAsCeiling(answer, settings, valuation);

        foreach (var group in PricingRun.Select(x => (Fingerprint: x, Search: Collected[x])).GroupBy(x => x.Search.ModifierSet).ToList())
        {
            var needing = new List<TabletSearch>();

            foreach (var (fingerprint, search) in group)
            {
                if (NeedsSearch(search, settings, valuation))
                    needing.Add(search);
                else
                    PricingRun.Remove(fingerprint);
            }

            if (needing.Count == 0)
                continue;

            foreach (var lowering in needing.Where(x => IsUsable(TabletPricing.Cached(x.Key), settings, valuation)).ToList())
            {
                var lower = NextLowerFilters(lowering, settings, valuation);

                TabletPricing.Enqueue(lowering.KeyOf(lower), lowering.League, lowering.BodyOf(lower),
                    Settles, lowering.Weight * 1_000_000L, settings.TradeSite.SweepSearchesPerFiveMinutes.Value, settings);
                needing.Remove(lowering);
                _searchesLeft++;
            }

            if (needing.Count == 0)
                continue;

            var first = needing[0];
            var ceiling = CombinedFilters(needing, highest: true);
            var floor = CombinedFilters(needing, highest: false);

            // Highest weight first, then higher rolls first, so a ceiling lands before what it can settle.
            long PriorityOf(IEnumerable<SearchFilter> filters) =>
                needing.Max(x => x.Weight) * 1_000_000L + Math.Clamp(filters.Sum(f => f.Min ?? 0), 0, 999_999);

            void Want(IReadOnlyList<SearchFilter> filters) =>
                TabletPricing.Enqueue(first.KeyOf(filters), first.League, first.BodyOf(filters), Settles,
                    PriorityOf(filters), settings.TradeSite.SweepSearchesPerFiveMinutes.Value, settings);

            if (needing.Count == 1)
            {
                Want(first.Filters);
                _searchesLeft++;
                continue;
            }

            if (!IsUsable(TabletPricing.Cached(first.KeyOf(ceiling)), settings, valuation))
            {
                Want(ceiling);
                _searchesLeft += 2;
                continue;
            }

            // The ceiling is in and settled nothing, or needing would be empty: the floor bounds them from below.
            if (first.KeyOf(floor) == first.KeyOf(ceiling) || IsUsable(TabletPricing.Cached(first.KeyOf(floor)), settings, valuation))
            {
                foreach (var (fingerprint, _) in group)
                    PricingRun.Remove(fingerprint);

                continue;
            }

            Want(floor);
            _searchesLeft++;
        }

        if (PricingRun.Count == 0 && _pricingRunTotal > 0)
            _pricingRunEnded = DateTime.UtcNow;
    }

    /// <summary>
    /// The filters of a modifier set at the highest or the lowest minimum of each modifier among these tablets. A
    /// minimum of "any" is the lowest of all.
    /// </summary>
    private static List<SearchFilter> CombinedFilters(List<TabletSearch> searches, bool highest)
    {
        var combined = new List<SearchFilter>();

        for (var i = 0; i < searches[0].Filters.Count; i++)
        {
            var minimums = searches.Select(s => s.Filters[i].Min).ToList();
            int? min = highest
                ? minimums.Where(x => x.HasValue).DefaultIfEmpty(null).Max()
                : minimums.Any(x => !x.HasValue) ? null : minimums.Min();

            combined.Add(searches[0].Filters[i] with { Min = min });
        }

        return combined;
    }

    /// <summary>
    /// Whether an answer has as many listings as a price takes and every one is in a currency the valuation never
    /// converts - alchemy, regal - which tablets are listed in when worth under a chaos. One listing at 10000 Scrolls of
    /// Wisdom took a tablet as under a chaos, so it had no label and counted as junk (a tester, 2026-10-10): fewer
    /// listings than that are too few to say anything.
    /// </summary>
    private static bool ListedOnlyUnconverted(TabletPricing.Answer answer, TabletRerollingSettings settings, Valuation valuation) =>
        answer.Listings.Count >= settings.ListingsWanted &&
        answer.Listings.All(l => !IsRatedCurrency(l.Currency) && double.IsNaN(ExaltsOf(l, valuation)));

    /// <summary>
    /// A guessed price for a tablet: the median price of cached searches for all of its modifiers but one, at rolls no
    /// higher than its own and with as many listings as a price takes, raised by EstimateRaiseForLastModifier for the
    /// modifier each leaves out. Null with no such search. Not a bound: drawn with "?" after it, and only where its own
    /// modifier set gives no price. See TabletPricing.AnswersForFewerModifiers.
    /// </summary>
    private static (double Price, int Searches)? EstimateFromFewerModifiers(TabletSearch search, TabletRerollingSettings settings,
        Valuation valuation)
    {
        var version = TabletPricing.AnswersVersion;
        var now = DateTime.UtcNow;

        // Worked out again when an answer changes, and now and then for the rates and the Price from setting.
        if (Estimates.TryGetValue(search.Key, out var kept) && kept.Version == version && now - kept.When < EstimateKeptFor)
            return kept.Estimate;

        var prices = TabletPricing.AnswersForFewerModifiers(search.Key)
            .Where(x => x.Stats.Count == search.Filters.Count - 1 && x.Answer.Error.Length == 0 &&
                        x.Answer.Listings.Count >= settings.ListingsWanted)
            .Select(x => PriceOfAnswer(x.Answer, settings, valuation))
            .Where(x => !double.IsNaN(x))
            .OrderBy(x => x)
            .ToList();

        (double Price, int Searches)? estimate = prices.Count > 0
            ? ((prices[(prices.Count - 1) / 2] + prices[prices.Count / 2]) / 2d * EstimateRaiseForLastModifier, prices.Count)
            : null;

        Estimates[search.Key] = (version, now, estimate);
        return estimate;
    }

    /// <summary>Each search key's last estimate, with the answers' version and when it was worked out. See EstimateFromFewerModifiers.</summary>
    private static readonly Dictionary<string, (int Version, DateTime When, (double Price, int Searches)? Estimate)> Estimates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// What the median of the searches one modifier short is multiplied by for the modifier each leaves out: half again.
    /// Chosen by the player (2026-10-10), not measured.
    /// </summary>
    private const double EstimateRaiseForLastModifier = 1.5d;

    /// <summary>How long an estimate stands while no answer changes.</summary>
    private static readonly TimeSpan EstimateKeptFor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// "Pricing 14/37 tablets, about 9 minutes" under the inventory's gold while a run is going, "Priced 37 tablets" for a
    /// moment after, whenever the inventory is open.
    /// The minutes are the searches the run may still send at the searches-per-five-minutes cap, which is stricter than
    /// the site's own.
    /// </summary>
    private static void DrawPricingProgress(Graphics graphics, GameController gc, TabletRerollingSettings settings)
    {
        string line;

        if (PricingRun.Count > 0)
        {
            var secondsPerSearch = Math.Max(300d / Math.Max(1, settings.TradeSite.SweepSearchesPerFiveMinutes.Value),
                settings.TradeSite.SecondsBetweenRequests.Value * 2d);
            var minutes = Math.Max(1, (int)Math.Ceiling(_searchesLeft * secondsPerSearch / 60d));

            line = $"Pricing {_pricingRunTotal - PricingRun.Count}/{_pricingRunTotal} tablets, about {minutes} minute{(minutes == 1 ? "" : "s")}";
        }
        else if (_pricingRunTotal > 0 && DateTime.UtcNow - _pricingRunEnded < RunEndedShownFor)
            line = $"Priced {_pricingRunTotal} tablets";
        else
            return;

        if (InventoryRect(gc) is not { Width: > 0f } inventory)
            return;

        var size = graphics.MeasureText(line);

        graphics.DrawTextWithBackground(line,
            new Vector2(inventory.Left + 4f + ProgressRightOfInventory + settings.TradeSite.PricingProgressXOffset.Value,
                inventory.Top - size.Y - 6f + ProgressBelowInventoryTop + settings.TradeSite.PricingProgressYOffset.Value), Color.White,
            FontAlign.Left, LabelBackground);
    }

    /// <summary>The merchant window as the tablet reader sees it, for the dump: whether it is open and what it lists.</summary>
    private static string MerchantSaid(GameController gc)
    {
        var shop = Safe.Read(gc, static g => g.IngameState.IngameUi.OfflineMerchantPanel, null);

        if (shop == null)
            return "merchant window: unreadable";

        var items = Safe.Read(shop, static s => s.VisibleStash?.VisibleInventoryItems, null);
        var tablets = items?.Count(i => (Safe.Read(i, static x => x.Item?.Metadata, "") ?? "")
            .StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase)) ?? 0;

        return $"merchant window: visible {Safe.Read(shop, static s => s.IsVisible, false)}, " +
               $"{(items == null ? "no visible tab read" : $"{items.Count} item(s) in its visible tab, {tablets} Expedition Tablet(s)")}; " +
               $"{MerchantOnly.Count} collected tablet(s) seen only there";
    }

    /// <summary>The pricing run's state, for the dump.</summary>
    private static string PricingRunSaid =>
        $"{Collected.Count} tablet(s) collected; pricing run {(PricingRun.Count > 0 ? $"{_pricingRunTotal - PricingRun.Count}/{_pricingRunTotal}, " + $"at most {_searchesLeft} search(es) left" : "not running")}";

    /// <summary>
    /// What a tablet's price reads as now, from the answers so far, without queuing anything: its own search's price;
    /// else a ceiling from a tighter search, shown first when it is a low value; else a floor from a looser one; else
    /// whether a search for it is coming.
    /// </summary>
    private static PricedTablet PricedFromAnswers(TabletSearch search, string fingerprint, TabletRerollingSettings settings,
        Valuation valuation)
    {
        // Heaviest modifier leftmost. The filters themselves stay in stat order, which the cache key is built in.
        var columns = search.Filters.OrderByDescending(f => f.Weight).ThenBy(f => f.Stat, StringComparer.Ordinal)
            .Select(f => (f.Stat, f.Text)).ToList();
        var searched = search.DescribedAs(search.Filters);
        var exact = TabletPricing.Cached(search.Key);
        var queued = TabletPricing.IsQueued(search.Key) || PricingRun.Contains(fingerprint);

        // The first line is the tablet's own search and reads the same on every tablet; what else is known is the line
        // under it. One line holding both ran so long that the price's source was lost in it (2026-10-10).
        var head = exact switch
        {
            { Error.Length: 0 } => $"Priced {AgeInWords(DateTime.UtcNow - exact.When)}, {exact.Total} listed{(queued ? ", searching again" : "")}",
            { } failed => $"Search failed {AgeInWords(DateTime.UtcNow - failed.When)}: {failed.Error}",
            null when queued => "Searching...",
            null => "Not priced yet",
        };

        // **A price being searched again keeps showing, with "..." after it**: "100d...". The old answer stands until the
        // new one replaces it, and the label read as settled while a search for it was queued (2026-10-10). A tablet
        // with no label, under Reforge under, stays without one.
        PricedTablet Make(Color colour, string label, string detail, List<TabletPricing.Listing> listings) =>
            new(colour, queued && label.Length > 0 && label != QueuedLabel ? label + QueuedLabel : label, searched, head, columns,
                listings, detail);

        if (exact is { Error.Length: 0 })
        {
            var price = PriceOfAnswer(exact, settings, valuation);

            // With no listing to price from at its own rolls, the bounds below say what is known.
            if (!double.IsNaN(price))
            {
                // **Three rising lower limits.** Under the bad one there is no price worth writing on the tablet.
                var chaos = TabletChaosOf(price, valuation);

                return chaos < settings.Prices.ReforgeUnderChaos.Value
                    ? Make(Color.Transparent, "", $"{Shown(price, valuation)}, under Reforge under", exact.Listings)
                    : Make(BandColour(chaos, settings), Shown(price, valuation), "", exact.Listings);
            }

            // **Listed only in currencies the valuation does not convert** - alchemy, regal - which tablets are listed in
            // when worth less than a chaos. Vaal was among them until 2026-10-10 and is now converted: at about 8 vaal to
            // the chaos, a listing at 80 vaal is about 10c.
            // Priced, then, and cheap: no label, as under Reforge under, and the reforger takes it as worth nothing. Not "?".
            // Not a chaos, divine or vaal listing whose rate is unknown: that one's price is unknown. See IsRatedCurrency.
            // Not fewer listings than a price takes either: see ListedOnlyUnconverted.
            if (ListedOnlyUnconverted(exact, settings, valuation))
            {
                var currency = exact.Listings.GroupBy(l => l.Currency).MaxBy(g => g.Count())!.Key;
                var amount = settings.PriceOf(exact.Listings.Where(l => l.Currency == currency).Select(l => l.Amount).OrderBy(x => x)
                    .ToList());

                return Make(Color.Transparent, "", $"{Whole(amount)} {currency}, taken as under a chaos", exact.Listings);
            }
        }

        (double Price, TabletPricing.Answer Answer)? ceiling = null, floor = null;

        foreach (var (_, answer, tighter, looser) in TabletPricing.RelatedAnswers(search.Key))
        {
            if (answer.Error.Length > 0 || PriceOfAnswer(answer, settings, valuation) is var price && double.IsNaN(price))
                continue;

            if (tighter && answer.Listings.Count >= settings.ListingsWanted && (ceiling == null || price < ceiling.Value.Price))
                ceiling = (price, answer);

            if (looser && (floor == null || price > floor.Value.Price))
                floor = (price, answer);
        }

        // Its own rolls not searched: the key prices them. Searched, the first line already says what they found.
        var priceExactly = exact is { Error.Length: 0 } ? "" : $", {_actionKeyName} to price exactly";

        var boundDetail = (ceiling, floor) switch
        {
            ({ } c, { } f) => $"Checked against other rolls: between {Shown(f.Price, valuation)} and {Shown(c.Price, valuation)}, priced " +
                              $"{AgeInWords(DateTime.UtcNow - (c.Answer.When > f.Answer.When ? c.Answer.When : f.Answer.When))}{priceExactly}",
            ({ } c, null) => $"Checked against higher rolls: at most {Shown(c.Price, valuation)}, priced " +
                              $"{AgeInWords(DateTime.UtcNow - c.Answer.When)}{priceExactly}",
            (null, { } f) => $"Checked against lower rolls: at least {Shown(f.Price, valuation)}, priced " +
                              $"{AgeInWords(DateTime.UtcNow - f.Answer.When)}{priceExactly}",
            _ => "",
        };

        if (ceiling is { } top && (floor == null || top.Price / valuation.PerExalt("Divine") <= settings.TradeSite.CheapPriceUpToDivine.Value))
        {
            var chaos = TabletChaosOf(top.Price, valuation);

            return chaos < settings.Prices.ReforgeUnderChaos.Value
                ? Make(Color.Transparent, "", boundDetail + ", under Reforge under", top.Answer.Listings)
                : Make(BandColour(chaos, settings), "<" + Shown(top.Price, valuation), boundDetail, top.Answer.Listings);
        }

        if (floor is { } bottom)
        {
            var chaos = TabletChaosOf(bottom.Price, valuation);

            return Make(chaos < settings.Prices.ReforgeUnderChaos.Value ? Dim : BandColour(chaos, settings), ">" + Shown(bottom.Price, valuation),
                boundDetail, bottom.Answer.Listings);
        }

        if (exact is { Error.Length: > 0 })
            return Make(Color.Transparent, "!", "", exact.Listings);

        if (queued)
            return Make(Color.Transparent, QueuedLabel, exact == null ? "" : "Searching lower rolls...", exact?.Listings ?? []);

        var estimate = EstimateFromFewerModifiers(search, settings, valuation);
        var estimateSaid = estimate is { } guess
            ? $"Estimated {Shown(guess.Price, valuation)}: the median of {guess.Searches} search(es) one modifier short, plus half"
            : "";

        // Searched, with nothing to price from at its own rolls nor any bound: unpriced still, so "?" as before a search.
        // **None listed at all: its own rolls and every lower step found nothing.** A purple "?" and a line that says so,
        // apart from a tablet with a few listings too few to price. The lowest step is the lowest rolls searched, so
        // this is none at any rolls the search reaches - on 2026-10-10 the trade site had no tablet at all with the
        // four modifiers of one that read so.
        if (exact != null && exact.Total == 0 && NextLowerFilters(search, settings, valuation) == null &&
            Enumerable.Range(1, search.LowerLevels).All(level => TabletPricing.Cached(search.KeyOf(search.LowerFilters(level))) is { Error.Length: 0, Total: 0 }))
            return Make(NoneListedColour, NotQueuedLabel,
                "None listed at any rolls searched" + (estimateSaid.Length > 0 ? $". {estimateSaid}" : ""), exact.Listings);

        if (estimate is { } estimated)
        {
            var chaos = TabletChaosOf(estimated.Price, valuation);

            return Make(chaos < settings.Prices.ReforgeUnderChaos.Value ? Dim : BandColour(chaos, settings), Shown(estimated.Price, valuation) + "?",
                (exact != null ? "Too few to price. " : "") + estimateSaid, exact?.Listings ?? []);
        }

        // The key over it is offered only while a lower search is left. With none - no lower rolls to search, or every
        // one searched and none listing enough - the line said to press it, and the press could search nothing more.
        return exact != null
            ? Make(Color.Transparent, NotQueuedLabel, "Too few to price, " +
                (NextLowerFilters(search, settings, valuation) != null
                    ? $"{_actionKeyName} over this tablet to search lower rolls"
                    : search.LowerLevels == 0
                        ? "no lower rolls to search"
                        : $"too few at lower rolls either ({search.LowerLevels} searched)"), exact.Listings)
            : Make(Color.Transparent, NotQueuedLabel, $"{_actionKeyName} to price everything, or over this tablet to price it", []);
    }
}
