using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace AutoExpedition;

/// <summary>
/// One modifier an Expedition Tablet can carry, as the Expedition Tablet Rerolling section's modifier table shows it.
///
/// Id is the mod's name without its tier digits (Tablets.StemOf). Affix is "Prefix", "Suffix", or blank for a row not
/// yet seen on a tablet, which then matches either. Weight is what it adds towards a full magic tablet being worth
/// a Regal: the tablet is when its two modifiers' weights reach TabletRerollingSettings.RegalThreshold, so one strong
/// modifier can carry it alone and two weaker ones together. Search includes it in the trade search that prices a finished
/// tablet. TradeStat is the trade site's id for it ("explicit.stat_..."), without which it cannot be searched for;
/// it is filled in from the listings the plugin fetches, matched by affix name, or typed. Slack is how far below the
/// tablet's own roll, in per cent, a listing's roll may be and still count. IgnoreOtherModRangesBelow, when above nought, marks a
/// modifier whose value below it means the tablet will be rerolled with a Divine Orb, so the other modifiers' values do
/// not matter and are searched for without them. ShortName heads its column in the hover table of listings. Seen is set
/// the first time a tablet carrying it is read.
/// </summary>
public class TabletModifierRow
{
    public string Id { get; set; } = "";
    public string Affix { get; set; } = "";
    public string Text { get; set; } = "";
    public int Weight { get; set; }

    /// <summary>The saved name Weight had until 2026-10-09, read once. See ShouldRegalSaved.</summary>
    [JsonProperty("RegalWeight")]
    private int RegalWeightSaved
    {
        set => Weight = value;
    }

    /// <summary>
    /// A table saved before Regal weights: a modifier marked to Regal on its own (ShouldRegal, earlier Good) weighs the
    /// default threshold of 100, and one in a Pair weighs 50, so a prefix and suffix pair still makes 100. Read once.
    /// </summary>
    [JsonProperty("ShouldRegal")]
    private bool ShouldRegalSaved
    {
        set => Weight = value ? Math.Max(Weight, 100) : Weight;
    }

    /// <summary>See ShouldRegalSaved.</summary>
    [JsonProperty("Good")]
    private bool GoodSaved
    {
        set => Weight = value ? Math.Max(Weight, 100) : Weight;
    }

    /// <summary>See ShouldRegalSaved.</summary>
    [JsonProperty("Pair")]
    private string PairSaved
    {
        set => Weight = string.IsNullOrWhiteSpace(value) ? Weight : Math.Max(Weight, 50);
    }
    public bool Search { get; set; }
    public string TradeStat { get; set; } = "";
    public string ShortName { get; set; } = "";
    /// <summary>
    /// What value the trade search asks for: blank works it out from the mod's roll range (see SearchMinimum), "any"
    /// searches for the modifier without a value, "exact" for at least its own roll, and a list such as "25,30,34" for
    /// the highest step at or below its roll.
    /// </summary>
    public string ValueSteps { get; set; } = "";

    /// <summary>
    /// A table saved before value steps: a row searched at its exact roll (Slack 0) stays exact; any other slack gives
    /// way to the automatic steps. Read once.
    /// </summary>
    [JsonProperty("Slack")]
    private int SlackSaved
    {
        set => ValueSteps = value == 0 ? "exact" : ValueSteps;
    }

    public int IgnoreOtherModRangesBelow { get; set; }

    /// <summary>The saved name IgnoreOtherModRangesBelow had until 2026-10-09, read once. See GoodSaved.</summary>
    [JsonProperty("DivineBelow")]
    private int DivineBelowSaved
    {
        set => IgnoreOtherModRangesBelow = value;
    }
    public bool Seen { get; set; }

    /// <summary>
    /// The rows a new settings file starts with, and what Restore default rows goes back to: every Expedition Tablet
    /// modifier read off tablets up to 2026-10-09, with the choices the author's own table held then - weights,
    /// Search, Value steps and Ignore other mod ranges below. Those choices are the author's, not measured. Highest
    /// weight first.
    ///
    /// Ids, affixes, texts and trade stats were read: the ids and affixes off tablets in the stash, the texts as the
    /// game writes them (see KnownTexts), the trade stats from fetched listings and the site's stat list (see
    /// KnownTradeStats). The short names of the searched rows were chosen for the hover table's columns.
    ///
    /// The additional random map modifier is searched at its exact value and ignores the other modifiers' ranges below
    /// 2: a 1 is rerolled with a Divine Orb hoping for 2, so below 2 the other modifiers' values do not matter.
    /// </summary>
    public static List<TabletModifierRow> Defaults() => WithGameText(
    [
        new() { Id = "TowerMapAdditionalModifier", Affix = "Suffix", Text = "Map has # additional random Modifier", Weight = 102, Search = true, TradeStat = "explicit.stat_588512487", ValueSteps = "exact", IgnoreOtherModRangesBelow = 2, ShortName = "random modifiers" },
        new() { Id = "TowerExpeditionChanceForVerisiumRemnant", Affix = "Suffix", Text = "Expeditions have +#% Surpassing chance to contain an additional Verisium Remnant", Weight = 101, Search = true, TradeStat = "explicit.stat_3653794255", ShortName = "additional remnant" },
        new() { Id = "TowerExpedition2RunicModPassoverChance", Affix = "Suffix", Text = "Verisium Remnants have +#% chance to add an additional Runic Modifier in Map", Weight = 100, Search = true, TradeStat = "explicit.stat_3871299443", ShortName = "runic modifiers" },
        new() { Id = "TowerMonsterEffectiveness", Affix = "Prefix", Text = "Monsters have #% increased Effectiveness", Weight = 51, Search = true, TradeStat = "explicit.stat_2065500219", ShortName = "effectiveness" },
        new() { Id = "TowerRarePackIncrease", Affix = "Prefix", Text = "Map has #% increased number of Rare Monsters", Weight = 52, Search = true, TradeStat = "explicit.stat_3793155082", ShortName = "rare monsters" },
        new() { Id = "TowerExpeditionIncreasedMonsterRarity", Affix = "Suffix", Text = "#% increased Expedition Monster Rarity in Map", Weight = 50, Search = true, TradeStat = "explicit.stat_2905096233", ShortName = "expedition monster rarity" },
        new() { Id = "TowerDroppedItemRarityIncrease", Affix = "Prefix", Text = "#% increased Rarity of Items found in Map", Weight = 5, Search = true, TradeStat = "explicit.stat_2306002879", ShortName = "item rarity" },
        new() { Id = "TowerPackSizeIncrease", Affix = "Prefix", Text = "#% increased Pack Size in Map", Weight = 4, Search = true, TradeStat = "explicit.stat_2017682521", ShortName = "pack size" },
        new() { Id = "TowerMonsterRarityIncrease", Affix = "Prefix", Text = "Map has #% increased Monster Rarity", Weight = 3, Search = true, TradeStat = "explicit.stat_4142653832", ShortName = "monster rarity" },
        new() { Id = "TowerExpeditionChanceForTwinnedElites", Affix = "Suffix", Text = "Expeditions have +#% Surpassing chance to Duplicate Runic Monsters in Map", Weight = 3, Search = true, TradeStat = "explicit.stat_779964546", ShortName = "duplicate runics" },
        new() { Id = "TowerMagicPackIncrease", Affix = "Prefix", Text = "Map has #% increased Magic Monsters", Weight = 1, Search = true, TradeStat = "explicit.stat_3873704640", ShortName = "magic monsters" },
        new() { Id = "TowerExpeditionRunicMonsters", Affix = "Suffix", Text = "Map contains #% increased number of Runic Monster Markers", Weight = 1, Search = true, TradeStat = "explicit.stat_1640965354", ShortName = "runic markers" },
        new() { Id = "TowerExpeditionExplosionRadius", Affix = "Suffix", Text = "#% increased Expedition Explosive Area of Effect in Map", Search = true, TradeStat = "explicit.stat_3039133122", ShortName = "explosive area" },
        new() { Id = "TowerExpeditionVaalRemnants", Affix = "Suffix", Text = "Expeditions contain 1 Vaal Relic in Map", Search = true, TradeStat = "explicit.stat_2852112245", ValueSteps = "exact", ShortName = "vaal relic" },
        new() { Id = "TowerExpeditionBuriedStrongboxes", Affix = "Suffix", Text = "Expeditions contain 1 buried Strongbox in Map", Search = true, TradeStat = "explicit.stat_181823691", ShortName = "buried strongbox" },
        new() { Id = "TowerAdditionalAzmeriWisp", Affix = "Prefix", Text = "Map contains # additional Azmeri Spirit", Search = true, TradeStat = "explicit.stat_358129101", ShortName = "azmeri spirits" },
        new() { Id = "TowerDroppedGoldIncrease", Affix = "Prefix", Text = "#% increased Gold found in Map (Gold Piles)", TradeStat = "explicit.stat_1276056105", ShortName = "gold" },
        new() { Id = "TowerStoneCircleChance", Affix = "Suffix", Text = "Map has #% increased chance to contain a Summoning Circle", TradeStat = "explicit.stat_267210597", ShortName = "summoning circle %" },
        new() { Id = "TowerExperienceGainIncrease", Affix = "Prefix", Text = "#% increased Experience gain in Map", TradeStat = "explicit.stat_57434274", ShortName = "experience" },
        new() { Id = "TowerExpeditionIncreasedVerisium", Affix = "Suffix", Text = "Monsters from Verisium Remnants drop #% increased Verisium", TradeStat = "explicit.stat_3520418269", ShortName = "verisium" },
        new() { Id = "TowerExpeditionUnearthedRares", Affix = "Suffix", Text = "The first unearthed Runic Monster will be a Rare Monster in Map", TradeStat = "explicit.stat_3963944561", ShortName = "first runic rare" },
        new() { Id = "TowerExpeditionFrozenBosses", Affix = "Suffix", Text = "Expeditions contain 1 Additional Boss encased in ice in Map", TradeStat = "explicit.stat_1183698646", ShortName = "frozen boss" },
        new() { Id = "TowerAdditionalStrongboxChance", Affix = "Suffix", Text = "Map has #% increased chance to contain Strongboxes", TradeStat = "explicit.stat_4279535856", ShortName = "strongbox %" },
        new() { Id = "TowerAdditionalShrine", Affix = "Suffix", Text = "Map contains an additional Shrine", TradeStat = "explicit.stat_1468737867", ShortName = "shrine" },
        new() { Id = "TowerAdditionalExile", Affix = "Prefix", Text = "Map is inhabited by # additional Rogue Exile", TradeStat = "explicit.stat_3550168289", ShortName = "rogue exiles" },
        new() { Id = "TowerAdditionalShrineChance", Affix = "Suffix", Text = "Map has #% increased chance to contain Shrines", TradeStat = "explicit.stat_689816330", ShortName = "shrine %" },
        new() { Id = "TowerRareChestCount", Affix = "Prefix", Text = "Map contains an additional Rare Chest", TradeStat = "explicit.stat_231864447", ShortName = "rare chest" },
        new() { Id = "TowerAdditionalStrongbox", Affix = "Suffix", Text = "Map contains # additional Strongboxes", TradeStat = "explicit.stat_3240183538", ShortName = "strongboxes" },
        new() { Id = "TowerExpeditionLogbookIncrease", Affix = "Suffix", Text = "#% increased Quantity of Expedition Logbooks dropped by Runic Monsters in Map", TradeStat = "explicit.stat_1083387327", ShortName = "logbooks" },
        new() { Id = "TowerAdditionalEssence", Affix = "Prefix", Text = "Map contains an additional Essence", TradeStat = "explicit.stat_395808938", ShortName = "essence" },
        new() { Id = "TowerMapDroppedMapsIncrease", Affix = "Suffix", Text = "#% increased Quantity of Waystones found in Map", TradeStat = "explicit.stat_2777224821", ShortName = "waystones" },
        new() { Id = "TowerAdditionalStoneCircle", Affix = "Prefix", Text = "Map contains an additional Summoning Circle", TradeStat = "explicit.stat_2839545956", ShortName = "summoning circle" },
        new() { Id = "TowerMapAdditionalUniqueMonsterModifier", Affix = "Suffix", Text = "Unique Monsters have # additional Rare Modifier", TradeStat = "explicit.stat_3371085671", ShortName = "unique rare mods" },
        new() { Id = "TowerExpeditionAdditionalSentinels", Affix = "Suffix", Text = "Expeditions contain 1 Additional Verisium Sentry in Map", TradeStat = "explicit.stat_1109460697", ShortName = "sentry" },
        new() { Id = "TowerAdditionalEssenceChance", Affix = "Suffix", Text = "Map has #% increased chance to contain Essences", TradeStat = "explicit.stat_1825943485", ShortName = "essence %" },
        new() { Id = "TowerAdditionalSpiritChance", Affix = "Suffix", Text = "Map has #% increased chance to contain Azmeri Spirits", TradeStat = "explicit.stat_3815617979", ShortName = "azmeri spirit %" },
        new() { Id = "TowerAdditionalExileChance", Affix = "Suffix", Text = "Map has #% increased chance to contain Rogue Exiles", TradeStat = "explicit.stat_1352729973", ShortName = "rogue exile %" },
    ]);

    /// <summary>Each row's text as the game writes it, where TabletRerollingSettings.KnownTexts has it.</summary>
    private static List<TabletModifierRow> WithGameText(List<TabletModifierRow> rows)
    {
        foreach (var row in rows)
        {
            if (TabletRerollingSettings.KnownTexts.TryGetValue(row.Id, out var text))
                row.Text = text;
        }

        return rows;
    }
}

/// <summary>
/// The Prices section of Expedition Tablet Rerolling: the price tiers, their colours, and which tablets are priced.
/// </summary>
public class TabletPriceSettings
{
    /// <summary>
    /// Below this price, in chaos, a tablet is junk: it shows no price, Withdraw leaves it, and the reforging bench takes
    /// it. Its own search's price, or a ceiling from a search at higher rolls; one with no price, or only a floor, is
    /// never reforged. Tablets under Minimum weight to keep are always reforged. Took the place of the reforging limit
    /// and the bad price threshold, two settings for one line (2026-10-10). See Tablets.IsJunk.
    /// </summary>
    [Menu("Reforge under (chaos)",
        "Tablets priced under this:\n" +
        "- show no price\n" +
        "- are not withdrawn\n" +
        "- are reforged at the bench")]
    public RangeNode<float> ReforgeUnderChaos { get; set; } = new RangeNode<float>(2f, 0f, 10f);

    /// <summary>The top of the low tier: prices from Reforge under up to this are drawn in the low tier colour.</summary>
    [Menu("Low tier under (chaos)", "Prices from Reforge under up to this use Low tier colour.")]
    public RangeNode<float> LowTierUnderChaos { get; set; } = new RangeNode<float>(10f, 0f, 50f);

    /// <summary>Prices in the low tier. The plugin's red, as the upstream duplicate colour.</summary>
    [Menu("Low tier colour")]
    public ColorNode LowTierColour { get; set; } = new ColorNode(Color.FromArgb(255, 235, 90, 90));

    /// <summary>The top of the mid tier: prices from Low tier under up to this are drawn in the mid tier colour, higher ones in the high tier colour.</summary>
    [Menu("Mid tier under (chaos)",
        "Prices from Low tier under up to this use Mid tier colour.\n" +
        "Higher prices use High tier colour.")]
    public RangeNode<float> MidTierUnderChaos { get; set; } = new RangeNode<float>(100f, 0f, 500f);

    /// <summary>Prices in the mid tier. The plugin's green, as the pick colour.</summary>
    [Menu("Mid tier colour")]
    public ColorNode MidTierColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 255, 120));

    /// <summary>Prices at or above Mid tier under. The plugin's purple, as the reward colour.</summary>
    [Menu("High tier colour", "Prices at or above Mid tier under.")]
    public ColorNode HighTierColour { get; set; } = new ColorNode(Color.FromArgb(255, 190, 120, 255));

    /// <summary>
    /// The weight a tablet's modifiers must add up to for it to be kept. A tablet below it carries nothing the modifier
    /// table values: it is not searched for on the trade site, shows no price, and is reforged whatever it would sell
    /// for. A rare is marked for an Exalt only if one more modifier could take it here. See Tablets.IsJunk and
    /// ReachesWeightToKeep. Named Minimum weight to price until 2026-10-10, which said only the first of the three.
    ///
    /// 101 against the default rows' weights: the additional remnant (101) or the random modifiers (102) clears it on
    /// its own, and rare monsters (52) with effectiveness (51) together - the player's own scheme, made the default
    /// (2026-10-10).
    /// </summary>
    [Menu("Minimum weight to keep",
        "Tablets whose modifiers weigh less than this:\n" +
        "- are not priced\n" +
        "- are reforged\n" +
        "A rare is only exalted if one more modifier could reach the minimum weight.")]
    public RangeNode<int> MinimumWeightToKeep { get; set; } = new RangeNode<int>(101, 0, 400);

    /// <summary>The saved name MinimumWeightToKeep had until 2026-10-10, read once. Raw JSON, so the node is not rebuilt.</summary>
    [JsonProperty("MinimumWeightToPrice")]
    private Newtonsoft.Json.Linq.JToken MinimumWeightToPriceSaved
    {
        set
        {
            if (value?["Value"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                MinimumWeightToKeep.Value = value["Value"].ToObject<int>();
        }
    }

    /// <summary>
    /// Whether a finished tablet - rare, four modifiers - is priced on the trade site against other rare tablets, and
    /// labelled with the price in its band's colour. See TabletPricing and Tablets.IsPriced.
    /// </summary>
    [Menu("Show prices for rare 4 mod tablets",
        "Searches the trade site for each finished rare tablet\n" +
        "(instant buyout only) and shows its price on it.")]
    public ToggleNode ShowRareTabletPrices { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether a magic tablet is priced the same way against other magic tablets, its price shown beside its action
    /// border. Off by default: every distinct magic tablet in view is one more search.
    /// </summary>
    [Menu("Show prices for magic tablets",
        "The same, for magic tablets.\n" +
        "Each different tablet is one more trade search, so pricing\n" +
        "hits the trade site's rate limits much sooner.")]
    public ToggleNode ShowMagicTabletPrices { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Prices on the tablets listed in the merchant window (IngameUi.OfflineMerchantPanel), as in the stash. No borders
    /// there. See IncludeMerchantTabletsInPricingRuns for whether a pricing run searches them.
    /// </summary>
    [Menu("Show prices in the merchant window")]
    public ToggleNode ShowMerchantPrices { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether a tablet already used - under Tablets.FullUses uses remaining - is left alone: no border, no price, no
    /// search. Its uses are read from its implicit. See Tablets.UsesOf.
    /// </summary>
    [Menu("Ignore tablets with < 10 uses remaining", "Used tablets get no border and no price.")]
    public ToggleNode IgnoreUsedTablets { get; set; } = new ToggleNode(true);

    /// <summary>
    /// While a tablet currency is right-clicked for use, no price labels are drawn. Off, prices stay up beside the
    /// borders of the tablets the currency is for. See Tablets.Draw.
    /// </summary>
    [Menu("Hide prices when currency held", "While a tablet currency is right-clicked for use, draw no prices.")]
    public ToggleNode HidePricesWhenCurrencyHeld { get; set; } = new ToggleNode(false);
}

/// <summary>
/// The Automation section of Expedition Tablet Rerolling: the tablet runs and their action zones.
/// </summary>
public class TabletAutomationSettings
{
    /// <summary>
    /// The tablet runs, with Automation > Enable automation on as well: five zones along the bottom of the fragment
    /// tab - Craft, Reforge, Craft and Reforge, Withdraw over Deposit - each starting its run when the action key is pressed over it, and the
    /// action key at the open reforging bench. Off, no zone is drawn and the key prices the tablet under the cursor.
    /// One switch for all of them: a player wanting one run and not another presses only that one's zone. It took the
    /// place of Automation's "Reforge full auto" and "Expedition Tablet Crafting" (2026-10-10). See Tablets.StartZone.
    /// </summary>
    [Menu("Enable tablet automation",
        "Adds Craft, Reforge, Craft and Reforge, Withdraw and Deposit zones under the fragment tab.\n" +
        "Hover a zone and press the action key to start that run.\n" +
        "Needs Automation > Enable automation on too.")]
    public ToggleNode EnableTabletAutomation { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether a craft and reforge run ends by tidying the fragment tab's Expedition tablets to the front, with no gaps
    /// between them. See Tablets.StartTidy.
    /// </summary>
    [Menu("Tidy tablets after Craft and Reforge", "When Craft and Reforge finishes, moves tablets to the front of the sub-tabs, leaving no gaps.")]
    public ToggleNode TidyAfterCraftAndReforge { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The least time between two ctrl+clicks moving tablets between the stash and the inventory. Each click is taken to
    /// have gone through and the cursor moves straight on to the next tablet; this slows the batch if the game drops
    /// clicks that come too fast. See Tablets.MoveNext.
    /// </summary>
    [Menu("Pause between clicks (ms)",
        "The least time between clicks when moving tablets.\n" +
        "Raise it if tablets get missed.")]
    public RangeNode<int> PauseBetweenClicksMs { get; set; } = new RangeNode<int>(0, 0, 200);

    /// <summary>
    /// ExileInput2's Shortest Move for a hop from one tablet to the next in a batch - a withdraw after a withdraw, a
    /// bench load after a load, a currency use after a use - in place of ExileInput2's own 40ms. ExileInput2 adds it to
    /// every move's time, so every other move of a run, the first to a tablet included, keeps ExileInput2's own. 0 uses
    /// ExileInput2's own for the hops too. Needs an ExileInput2 with SetShortestMove. See Tablets.UseShortestMove.
    /// </summary>
    [Menu("Shortest move between tablets (ms)",
        "The least time the mouse takes to move from one tablet to the next.\n" +
        "Lower is faster. 0 uses ExileInput2's own setting.")]
    public RangeNode<int> ShortestMoveBetweenTabletsMs { get; set; } = new RangeNode<int>(200, 0, 400);

    /// <summary>
    /// ExileInput2's Shortest Move for a hop from one tablet to the next in the Deposit zone's run, in place of Shortest
    /// move between tablets: a deposit only empties the inventory, so it can go faster. 0 uses ExileInput2's own. See
    /// Tablets.UseShortestMove.
    /// </summary>
    [Menu("Shortest move when depositing (ms)",
        "The same, for the Deposit zone only.\n" +
        "0 uses ExileInput2's own setting.")]
    public RangeNode<int> ShortestMoveWhenDepositingMs { get; set; } = new RangeNode<int>(67, 0, 400);

    /// <summary>Moves the action zones right. See EnableTabletAutomation.</summary>
    [Menu("Action zones X offset")]
    public RangeNode<int> ActionZonesXOffset { get; set; } = new RangeNode<int>(0, -500, 500);

    /// <summary>Moves the action zones down. See EnableTabletAutomation.</summary>
    [Menu("Action zones Y offset")]
    public RangeNode<int> ActionZonesYOffset { get; set; } = new RangeNode<int>(0, -500, 500);

    /// <summary>How tall the action zones are. See EnableTabletAutomation.</summary>
    [Menu("Action zones height")]
    public RangeNode<int> ActionZonesHeight { get; set; } = new RangeNode<int>(100, 40, 200);

    /// <summary>How wide the action zones are together, the gaps between them included. See EnableTabletAutomation.</summary>
    [Menu("Action zones width", "All zones together, including the gaps.")]
    public RangeNode<int> ActionZonesWidth { get; set; } = new RangeNode<int>(802, 400, 1200);
}

/// <summary>
/// The Trade site section of Expedition Tablet Rerolling: what is searched, how often, and how a price is made.
/// </summary>
public class TabletTradeSiteSettings
{
    /// <summary>The trade league searched. Blank searches the league the game is in (ServerData.League).</summary>
    [Menu("Trade league", "Blank uses the league you are playing in.")]
    public TextNode TradeLeague { get; set; } = new TextNode("");

    /// <summary>How a price is made from the cheapest listings. See TabletRerollingSettings.PriceMethods.</summary>
    [Menu("Price from", "Which of the cheapest listings make up the price.")]
    public ListNode PriceFrom { get; set; } = new ListNode { Value = TabletRerollingSettings.MedianOfCheapestFive };

    /// <summary>
    /// How long a price stays fresh. A stale price stays drawn and is searched again only by the action key.
    /// Cheap prices use CheapPriceFreshHours instead.
    /// </summary>
    [Menu("Price fresh for (minutes)",
        "Older prices stay shown, and are searched again\n" +
        "the next time you press the action key.")]
    public RangeNode<int> PriceFreshMinutes { get; set; } = new RangeNode<int>(180, 5, 1440);

    /// <summary>
    /// The highest price, in divines, that counts as cheap. A cheap price stays fresh for CheapPriceFreshHours, and a
    /// tablet with the same modifiers at lower rolls is not searched: it is shown as at most that price.
    /// </summary>
    [Menu("Cheap price up to (divine)",
        "Prices at or under this:\n" +
        "- stay fresh for Cheap price fresh for\n" +
        "- also cover the same modifiers at lower rolls, shown as at most this price without a search")]
    public RangeNode<float> CheapPriceUpToDivine { get; set; } = new RangeNode<float>(2f, 0f, 10f);

    /// <summary>How long a cheap price stays fresh. See CheapPriceUpToDivine.</summary>
    [Menu("Cheap price fresh for (hours)")]
    public RangeNode<int> CheapPriceFreshHours { get; set; } = new RangeNode<int>(24, 1, 72);

    /// <summary>
    /// The action key over a tablet searches it again whatever its freshness, but not when its own price is at most
    /// this many minutes old. See Tablets.ActOnActionKey.
    /// </summary>
    [Menu("Hovered refresh minimum age (minutes)",
        "The action key over a tablet searches it again,\n" +
        "unless its price is newer than this.")]
    public RangeNode<int> HoveredRefreshMinimumMinutes { get; set; } = new RangeNode<int>(15, 0, 120);

    /// <summary>
    /// Whether a pricing run searches tablets seen only in the merchant window. Off, they show what other searches
    /// already say about them, and the action key over one still prices it.
    /// </summary>
    [Menu("Include merchant tablets in pricing runs",
        "Off: tablets you have listed are only searched\n" +
        "when you press the action key over them.")]
    public ToggleNode IncludeMerchantTabletsInPricingRuns { get; set; } = new ToggleNode(false);

    /// <summary>
    /// The least time between two requests to the trade site, searches and fetches alike. The site's own limits are
    /// always kept as well; this can only make the plugin slower. See TabletPricing.
    /// </summary>
    [Menu("Seconds between trade requests", "Never faster than the site's own limits, whatever this says.")]
    public RangeNode<float> SecondsBetweenRequests { get; set; } = new RangeNode<float>(3f, 0.5f, 10f);

    /// <summary>
    /// The most searches in any five minutes a pricing run sends, counting the player's own on the website. The site
    /// allowed 30 per 300 seconds, locking out for 30 minutes past that (2026-10-09). The default leaves 2 for the
    /// action key over a tablet and 3 for searches made by hand. See HoveredSearchesPerFiveMinutes.
    /// </summary>
    [Menu("Sweep searches per 5 minutes",
        "Pricing everything stops at this many searches in 5 minutes.\n" +
        "The trade site allows 30, including your own browser searches.")]
    public RangeNode<int> SweepSearchesPerFiveMinutes { get; set; } = new RangeNode<int>(25, 1, 29);

    /// <summary>
    /// The most searches in any five minutes the action key over a tablet may take its search to, counting the
    /// player's own: above SweepSearchesPerFiveMinutes, so that tablet is searched while a pricing run waits. The
    /// default leaves 3 of the site's 30 for searches made by hand.
    /// </summary>
    [Menu("Hovered searches per 5 minutes",
        "Pricing a hovered tablet may go up to this many, so it isn't stuck behind a full run.\n" +
        "The trade site allows 30, including your own browser searches.")]
    public RangeNode<int> HoveredSearchesPerFiveMinutes { get; set; } = new RangeNode<int>(27, 1, 29);

    /// <summary>
    /// Moves the pricing progress line right of where it is drawn by default, under the inventory's gold.
    /// See Tablets.DrawPricingProgress.
    /// </summary>
    [Menu("Pricing progress X offset", "Pixels right of its built-in place.")]
    public RangeNode<int> PricingProgressXOffset { get; set; } = new RangeNode<int>(0, -1000, 1000);

    /// <summary>Moves the pricing progress line down. See PricingProgressXOffset.</summary>
    [Menu("Pricing progress Y offset", "Pixels down from its built-in place.")]
    public RangeNode<int> PricingProgressYOffset { get; set; } = new RangeNode<int>(0, -1000, 1000);

    [JsonIgnore]
    public CustomNode PricingStatusUi { get; set; } = new CustomNode(() =>
    {
        ImGuiNET.ImGui.TextWrapped($"Trade site: {TabletPricing.Status}, {TabletPricing.Pending} waiting. {TabletPricing.Limits}");

        if (ImGuiNET.ImGui.Button("Clear tablet price cache###aeTabletPriceClear"))
            TabletPricing.Clear();
    });
}

/// <summary>
/// The Crafting by hand section of Expedition Tablet Rerolling: borders by the currency each tablet takes next, and
/// the currency requirements line.
/// </summary>
public class TabletCraftingByHandSettings
{
    /// <summary>
    /// Whether tablets are bordered by the currency to use on each next. Off by default: the borders are for crafting by
    /// hand, the runs need none, and drawing them as the cursor moves over the stash made it feel slow (2026-10-09).
    /// Prices are drawn either way. See Tablets.Draw.
    /// </summary>
    [Menu("Draw tablet borders",
        "Outlines each tablet in the colour of the currency to use on it next.\n" +
        "Only needed for crafting by hand; prices show either way.")]
    public ToggleNode DrawTabletBorders { get; set; } = new ToggleNode(false);

    [Menu("Draw tablet borders in inventory", "The same borders on tablets in your inventory.")]
    public ToggleNode DrawInInventory { get; set; } = new ToggleNode(false);

    [Menu("Border thickness (px)")]
    public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(3, 1, 3);

    /// <summary>A full magic tablet whose modifiers are not worth keeping. The plugin's yellow, as the rare chest colour.</summary>
    [Menu("Alchemy colour")]
    public ColorNode AlchemyColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 210, 80));

    /// <summary>A full magic tablet worth keeping. The plugin's blue, as the reroll colour.</summary>
    [Menu("Regal colour")]
    public ColorNode RegalColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 160, 255));

    /// <summary>A rare tablet with fewer than four modifiers. Brown; the plugin had no brown before this.</summary>
    [Menu("Exalt colour")]
    public ColorNode ExaltColour { get; set; } = new ColorNode(Color.FromArgb(255, 160, 105, 55));

    /// <summary>
    /// The border on each tablet a right-clicked Transmutation or Augmentation is for: a normal tablet, or a magic one
    /// short of a prefix or a suffix. White. The game outlines these itself, too faintly to pick out in a full tab. They
    /// get no border otherwise. See Tablets.HeldOnCursor.
    /// </summary>
    [Menu("Transmutation and Augmentation colour (held)",
        "While a Transmutation or Augmentation is right-clicked,\n" +
        "outlines the tablets it can be used on. Fully transparent draws none.")]
    public ColorNode TransmutationAndAugmentationColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 255, 255));

    /// <summary>
    /// While a Transmutation, Augmentation, Alchemy, Regal or Exalt is right-clicked for use, only the tablets whose next
    /// action it is keep a border, so they are all there is to pick out. See Tablets.HeldOnCursor.
    /// </summary>
    [Menu("Hide other borders when currency held",
        "While a tablet currency is right-clicked for use, draw only the\n" +
        "borders of the tablets it is for.")]
    public ToggleNode HideOtherBordersWhenCurrencyHeld { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The weight a full magic tablet's two modifiers must reach between them for it to be worth a Regal rather
    /// than an Alchemy, and that a magic tablet with one modifier must be able to reach with the heaviest modifier of
    /// its open affix for it to be worth an Augmentation. Each modifier's weight is set in the modifier table. See
    /// TabletModifierRow.Weight and Tablets.ActionOf.
    /// </summary>
    [Menu("Regal threshold",
        "A magic tablet with two modifiers weighing this or more together:\n" +
        "- is regaled, otherwise alchemied\n" +
        "A magic tablet with one modifier:\n" +
        "- is augmented if the heaviest modifier it could gain would reach this\n" +
        "- is alchemied otherwise")]
    public RangeNode<int> RegalThreshold { get; set; } = new RangeNode<int>(100, 1, 300);

    /// <summary>
    /// The searched modifiers - rows with Search ticked and a trade stat - a rare tablet with only a prefix open needs
    /// before it is marked for an Exalt: three modifiers nobody searches for gain nothing from a fourth prefix. One with
    /// a suffix open is always marked, since that suffix can be the additional random map modifier. See
    /// Tablets.ActionOf.
    /// </summary>
    [Menu("Minimum searched modifiers for exalting",
        "A rare with only a prefix open is exalted only if it has this many searched modifiers\n" +
        "(rows ticked Search in the modifier table).\n" +
        "Every rare also needs one more modifier to be able to reach Minimum weight to keep.")]
    public RangeNode<int> MinimumSearchedModifiersForExalting { get; set; } = new RangeNode<int>(1, 0, 3);

    /// <summary>
    /// Under the pricing progress line, each crafting currency whose stock covers what the tablets seen need, in green
    /// colour: "Augmentation: 5/2", have over need. See Tablets.DrawCraftingRequirements.
    /// </summary>
    [Menu("Show met currency requirements for crafting tablets",
        "Lists each crafting currency you have enough of, as have/need,\n" +
        "under the pricing progress line.")]
    public ToggleNode ShowMetCraftingRequirements { get; set; } = new ToggleNode(false);

    /// <summary>The same for each crafting currency short of what the tablets need, in red.</summary>
    [Menu("Show unmet currency requirements for crafting tablets",
        "Lists each crafting currency you don't have enough of, as have/need,\n" +
        "under the pricing progress line.")]
    public ToggleNode ShowUnmetCraftingRequirements { get; set; } = new ToggleNode(true);
}
/// <summary>
/// The Expedition Tablet Rerolling section: borders on Expedition Tablets in the stash, and optionally the inventory,
/// in the colour of the currency each takes next, and the table of modifiers that decides whether a magic tablet is
/// worth a Regal. See Tablets.
/// </summary>
public class TabletRerollingSettings
{
    public TabletRerollingSettings()
    {
        ModifierTableUi = new CustomNode(() => TabletModifierTable.Draw(this));
    }

    /// <summary>
    /// The whole section: off, no tablet is read, bordered or priced, and nothing is sent to the trade site. See
    /// Tablets.Draw, the one place every part of it starts from.
    /// </summary>
    [Menu("Enable Expedition Tablet Rerolling",
        "Prices Expedition Tablets, and crafts and reforges them for you.\n" +
        "In a hideout or town, with the action key (F4 by default):\n" +
        "- press over a tablet to price it, if it meets Minimum weight to keep\n" +
        "- hold over a tablet to open its search on the trade site\n" +
        "- press anywhere else to price every tablet seen\n" +
        "- press over a zone under the fragment tab to start that run\n" +
        "Everything in this section needs this on.")]
    public ToggleNode EnableTabletRerolling { get; set; } = new ToggleNode(true);

    /// <summary>The price tiers and which tablets are priced. Open by default: the settings most often changed.</summary>
    [Menu("Prices")]
    [Submenu(CollapsedByDefault = false)]
    public TabletPriceSettings Prices { get; set; } = new TabletPriceSettings();

    [Menu("Automation")]
    [Submenu(CollapsedByDefault = true)]
    public TabletAutomationSettings TabletAutomation { get; set; } = new TabletAutomationSettings();

    [Menu("Trade site")]
    [Submenu(CollapsedByDefault = true)]
    public TabletTradeSiteSettings TradeSite { get; set; } = new TabletTradeSiteSettings();

    [Menu("Crafting by hand")]
    [Submenu(CollapsedByDefault = true)]
    public TabletCraftingByHandSettings CraftingByHand { get; set; } = new TabletCraftingByHandSettings();

    /// <summary>
    /// Whether one more modifier could take these to Minimum weight to keep: their weight now and the heaviest row of an
    /// open affix - a prefix row with a prefix open, a suffix row with a suffix open, a row of either with either - that
    /// is not already among them.
    /// </summary>
    internal bool ReachesWeightToKeep(Tablets.TabletModifier[] modifiers, bool prefixOpen, bool suffixOpen) =>
        ReachesWeight(modifiers, prefixOpen, suffixOpen, Prices.MinimumWeightToKeep.Value);

    /// <summary>
    /// Whether one more modifier could take these to the given weight: their weight now and the heaviest row of an open
    /// affix not already among them. See ReachesWeightToKeep.
    /// </summary>
    internal bool ReachesWeight(Tablets.TabletModifier[] modifiers, bool prefixOpen, bool suffixOpen, int weight)
    {
        var present = modifiers.Select(m => m.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var best = Modifiers
            .Where(r => !present.Contains(r.Id))
            .Where(r => r.Affix.Equals("Prefix", StringComparison.OrdinalIgnoreCase) ? prefixOpen
                : r.Affix.Equals("Suffix", StringComparison.OrdinalIgnoreCase) ? suffixOpen
                : prefixOpen || suffixOpen)
            .Select(r => r.Weight)
            .DefaultIfEmpty(0)
            .Max();

        return WeightOf(modifiers) + best >= weight;
    }

    /// <summary>How many of these modifiers are searched: their row has Search ticked and a trade stat.</summary>
    internal int SearchedCountOf(Tablets.TabletModifier[] modifiers) =>
        modifiers.Count(m => RowOf(m) is { Search: true, TradeStat.Length: > 0 });

    /// <summary>
    /// The saved name MinimumWeightToKeep had until 2026-10-09, read once. Taken as raw JSON, not a RangeNode, so the
    /// value is not clamped by a node built without this one's range.
    /// </summary>
    [JsonProperty("MinimumRegalWeightToPrice")]
    private Newtonsoft.Json.Linq.JToken MinimumRegalWeightToPriceSaved
    {
        set
        {
            if (value?["Value"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                Prices.MinimumWeightToKeep.Value = value["Value"].ToObject<int>();
        }
    }

    /// <summary>
    /// The saved name HideOtherBordersWhenCurrencyHeld had until 2026-10-09, when it hid prices as well, read once.
    /// Raw JSON, so the node is not rebuilt.
    /// </summary>
    [JsonProperty("DisableOtherDrawingsWhenCurrencyHeld")]
    private Newtonsoft.Json.Linq.JToken DisableOtherDrawingsWhenCurrencyHeldSaved
    {
        set
        {
            if (value?["Value"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                CraftingByHand.HideOtherBordersWhenCurrencyHeld.Value = value["Value"].ToObject<bool>();
        }
    }

    /// <summary>
    /// How long a price stays fresh: CheapPriceFreshHours at or under CheapPriceUpToDivine, else PriceFreshMinutes. A
    /// search with no usable listing has no price and takes PriceFreshMinutes.
    /// </summary>
    internal TimeSpan FreshFor(double divines) => !double.IsNaN(divines) && divines <= TradeSite.CheapPriceUpToDivine.Value
        ? TimeSpan.FromHours(TradeSite.CheapPriceFreshHours.Value)
        : TimeSpan.FromMinutes(TradeSite.PriceFreshMinutes.Value);

    [JsonIgnore]
    public CustomNode ModifierTableUi { get; set; }

    /// <summary>The modifier table. Replaced, not appended to, when the settings file is read. See TabletModifierRow.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<TabletModifierRow> Modifiers = TabletModifierRow.Defaults();

    internal const string MedianOfCheapestFive = "Median of cheapest 5";

    /// <summary>The choices of Price from, each with how many listings it fetches.</summary>
    internal static readonly (string Name, int Listings)[] PriceMethods =
    [
        (MedianOfCheapestFive, 5),
        ("Median of cheapest 10", 10),
        ("Average of cheapest 5", 5),
        ("Cheapest", 1),
    ];

    /// <summary>
    /// The price of a set of listings, already in one unit and sorted cheapest first, by the chosen method. NaN for none.
    /// </summary>
    internal double PriceOf(IReadOnlyList<double> cheapestFirst)
    {
        if (cheapestFirst.Count == 0)
            return double.NaN;

        double Median(int most)
        {
            var take = cheapestFirst.Take(most).ToList();
            var middle = take.Count / 2;

            return take.Count % 2 == 1 ? take[middle] : (take[middle - 1] + take[middle]) / 2d;
        }

        return TradeSite.PriceFrom.Value switch
        {
            "Median of cheapest 10" => Median(10),
            "Average of cheapest 5" => cheapestFirst.Take(5).Average(),
            "Cheapest" => cheapestFirst[0],
            _ => Median(5),
        };
    }

    /// <summary>How many listings the chosen method fetches.</summary>
    internal int ListingsWanted =>
        PriceMethods.FirstOrDefault(x => x.Name == TradeSite.PriceFrom.Value) is { Listings: > 0 } m ? m.Listings : 5;

    /// <summary>
    /// Fills in a row's blank TradeStat from a fetched listing's modifier, and its text where none is shipped: the
    /// listing names the affix ("of Remnants"), its stat hash and the game's wording; a tablet modifier read with that
    /// affix name gives the row. See TabletPricing.Learned.
    /// </summary>
    /// <summary>
    /// The trade site's stat for every Expedition Tablet modifier read so far, by mod id stem, so a row can be searched
    /// for as soon as it exists rather than once the plugin has fetched a listing carrying it.
    ///
    /// Read on 2026-10-09. Confirmed: seen on fetched Expedition Tablet listings beside the affix's name - every entry
    /// down to TowerAdditionalExile. Matched by text against /api/trade2/data/stats only: the rest, taking the plain
    /// entry where an "Areas with Powerful Map Bosses" twin exists. Least certain of those: twinned elites
    /// (Surpassing chance to Duplicate Runic Monsters), remnants passing two runic modifiers (Verisium Remnants have a
    /// chance to add an additional Runic Modifier) and sentinels (Additional Verisium Sentry).
    /// </summary>
    internal static readonly Dictionary<string, string> KnownTradeStats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TowerExpeditionChanceForVerisiumRemnant"] = "explicit.stat_3653794255",
        ["TowerMonsterEffectiveness"] = "explicit.stat_2065500219",
        ["TowerRarePackIncrease"] = "explicit.stat_3793155082",
        ["TowerMonsterRarityIncrease"] = "explicit.stat_4142653832",
        ["TowerDroppedGoldIncrease"] = "explicit.stat_1276056105",
        ["TowerMagicPackIncrease"] = "explicit.stat_3873704640",
        ["TowerExperienceGainIncrease"] = "explicit.stat_57434274",
        ["TowerDroppedItemRarityIncrease"] = "explicit.stat_2306002879",
        ["TowerExpeditionExplosionRadius"] = "explicit.stat_3039133122",
        ["TowerExpeditionRunicMonsters"] = "explicit.stat_1640965354",
        ["TowerPackSizeIncrease"] = "explicit.stat_2017682521",
        ["TowerExpeditionIncreasedVerisium"] = "explicit.stat_3520418269",
        ["TowerExpeditionUnearthedRares"] = "explicit.stat_3963944561",
        ["TowerExpeditionFrozenBosses"] = "explicit.stat_1183698646",
        ["TowerAdditionalStrongboxChance"] = "explicit.stat_4279535856",
        ["TowerExpeditionVaalRemnants"] = "explicit.stat_2852112245",
        ["TowerAdditionalShrine"] = "explicit.stat_1468737867",
        ["TowerAdditionalShrineChance"] = "explicit.stat_689816330",
        ["TowerExpeditionIncreasedMonsterRarity"] = "explicit.stat_2905096233",
        ["TowerExpeditionLogbookIncrease"] = "explicit.stat_1083387327",
        ["TowerAdditionalAzmeriWisp"] = "explicit.stat_358129101",
        ["TowerRareChestCount"] = "explicit.stat_231864447",
        ["TowerAdditionalStrongbox"] = "explicit.stat_3240183538",
        ["TowerAdditionalExile"] = "explicit.stat_3550168289",
        ["TowerMapAdditionalModifier"] = "explicit.stat_588512487",
        ["TowerStoneCircleChance"] = "explicit.stat_267210597",
        ["TowerExpeditionBuriedStrongboxes"] = "explicit.stat_181823691",
        ["TowerAdditionalEssence"] = "explicit.stat_395808938",
        ["TowerMapDroppedMapsIncrease"] = "explicit.stat_2777224821",
        ["TowerAdditionalStoneCircle"] = "explicit.stat_2839545956",
        ["TowerExpeditionChanceForTwinnedElites"] = "explicit.stat_779964546",
        ["TowerMapAdditionalUniqueMonsterModifier"] = "explicit.stat_3371085671",
        ["TowerExpedition2RunicModPassoverChance"] = "explicit.stat_3871299443",
        ["TowerExpeditionAdditionalSentinels"] = "explicit.stat_1109460697",
        ["TowerAdditionalEssenceChance"] = "explicit.stat_1825943485",
        ["TowerAdditionalSpiritChance"] = "explicit.stat_3815617979",
        ["TowerAdditionalExileChance"] = "explicit.stat_1352729973",
    };

    /// <summary>
    /// Each known modifier's text as the game writes it on an Expedition Tablet, "#" for the roll: the trade site's
    /// wording for its stat (/api/trade2/data/stats, 2026-10-09), the "in Map" form where the site also lists an "Area"
    /// one, as fetched tablet listings read. The HUD's own translation of these mods was mostly unknown, and the stat
    /// keys it fell back to ("map number of magic packs +%") did not read as the game does. See KnownTradeStats.
    /// </summary>
    internal static readonly Dictionary<string, string> KnownTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TowerExpeditionChanceForVerisiumRemnant"] = "Expeditions have +#% Surpassing chance to contain an additional Verisium Remnant",
        ["TowerMonsterEffectiveness"] = "Monsters have #% increased Effectiveness",
        ["TowerRarePackIncrease"] = "Map has #% increased number of Rare Monsters",
        ["TowerMonsterRarityIncrease"] = "Map has #% increased Monster Rarity",
        ["TowerDroppedGoldIncrease"] = "#% increased Gold found in Map (Gold Piles)",
        ["TowerMagicPackIncrease"] = "Map has #% increased Magic Monsters",
        ["TowerExperienceGainIncrease"] = "#% increased Experience gain in Map",
        ["TowerDroppedItemRarityIncrease"] = "#% increased Rarity of Items found in Map",
        ["TowerExpeditionExplosionRadius"] = "#% increased Expedition Explosive Area of Effect in Map",
        ["TowerExpeditionRunicMonsters"] = "Map contains #% increased number of Runic Monster Markers",
        ["TowerPackSizeIncrease"] = "#% increased Pack Size in Map",
        ["TowerExpeditionIncreasedVerisium"] = "Monsters from Verisium Remnants drop #% increased Verisium",
        ["TowerExpeditionUnearthedRares"] = "The first unearthed Runic Monster will be a Rare Monster in Map",
        ["TowerExpeditionFrozenBosses"] = "Expeditions contain 1 Additional Boss encased in ice in Map",
        ["TowerAdditionalStrongboxChance"] = "Map has #% increased chance to contain Strongboxes",
        ["TowerExpeditionVaalRemnants"] = "Expeditions contain 1 Vaal Relic in Map",
        ["TowerAdditionalShrine"] = "Map contains an additional Shrine",
        ["TowerAdditionalShrineChance"] = "Map has #% increased chance to contain Shrines",
        ["TowerExpeditionIncreasedMonsterRarity"] = "#% increased Expedition Monster Rarity in Map",
        ["TowerExpeditionLogbookIncrease"] = "#% increased Quantity of Expedition Logbooks dropped by Runic Monsters in Map",
        ["TowerAdditionalAzmeriWisp"] = "Map contains # additional Azmeri Spirit",
        ["TowerRareChestCount"] = "Map contains an additional Rare Chest",
        ["TowerAdditionalStrongbox"] = "Map contains # additional Strongboxes",
        ["TowerAdditionalExile"] = "Map is inhabited by # additional Rogue Exile",
        ["TowerMapAdditionalModifier"] = "Map has # additional random Modifier",
        ["TowerStoneCircleChance"] = "Map has #% increased chance to contain a Summoning Circle",
        ["TowerExpeditionBuriedStrongboxes"] = "Expeditions contain 1 buried Strongbox in Map",
        ["TowerAdditionalEssence"] = "Map contains an additional Essence",
        ["TowerMapDroppedMapsIncrease"] = "#% increased Quantity of Waystones found in Map",
        ["TowerAdditionalStoneCircle"] = "Map contains an additional Summoning Circle",
        ["TowerExpeditionChanceForTwinnedElites"] = "Expeditions have +#% Surpassing chance to Duplicate Runic Monsters in Map",
        ["TowerMapAdditionalUniqueMonsterModifier"] = "Unique Monsters have # additional Rare Modifier",
        ["TowerExpedition2RunicModPassoverChance"] = "Verisium Remnants have +#% chance to add an additional Runic Modifier in Map",
        ["TowerExpeditionAdditionalSentinels"] = "Expeditions contain 1 Additional Verisium Sentry in Map",
        ["TowerAdditionalEssenceChance"] = "Map has #% increased chance to contain Essences",
        ["TowerAdditionalSpiritChance"] = "Map has #% increased chance to contain Azmeri Spirits",
        ["TowerAdditionalExileChance"] = "Map has #% increased chance to contain Rogue Exiles",
    };

    /// <summary>
    /// The increased Expedition Monster Rarity suffix's weight of 50 - the other half of monster rarity - on a
    /// table that read it off a tablet before it was a default row; added when missing. Only that row: every other
    /// row's weight comes from what was saved for it, so a choice made before weights is not undone. Migration 28.
    /// </summary>
    internal string SetDefaultRegalWeights()
    {
        var fresh = TabletModifierRow.Defaults().First(r => r.Id == "TowerExpeditionIncreasedMonsterRarity");
        var rows = Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        if (rows.Count == 0)
            Modifiers.Add(fresh);

        foreach (var row in rows.Where(r => r.Weight == 0))
            row.Weight = fresh.Weight;

        return "weighted increased Expedition Monster Rarity at 50 towards a Regal, with monster rarity";
    }

    /// <summary>Each row's text as the game writes it, where KnownTexts has it. Migration 27.</summary>
    internal string UseGameTexts()
    {
        var changed = 0;

        foreach (var row in Modifiers)
        {
            if (KnownTexts.TryGetValue(row.Id, out var text) && row.Text != text)
            {
                row.Text = text;
                changed++;
            }
        }

        return changed == 0 ? "" : $"worded {changed} tablet modifier row(s) as the game writes them";
    }

    /// <summary>Fills each row's blank trade stat from KnownTradeStats. Migration 26.</summary>
    internal string FillKnownTradeStats()
    {
        var filled = 0;

        foreach (var row in Modifiers.Where(r => r.TradeStat.Length == 0))
        {
            if (KnownTradeStats.TryGetValue(row.Id, out var stat))
            {
                row.TradeStat = stat;
                filled++;
            }
        }

        return filled == 0 ? "" : $"set the trade site's stat on {filled} more tablet modifier row(s)";
    }

    /// <summary>
    /// Every row's choices back to where a new table starts: the default rows as TabletModifierRow.Defaults has them,
    /// every other row with weight nought, Search cleared, Slack 10 and no ignore value. Trade stats, texts and
    /// whether a row was seen are kept. Missing default rows are added. The table's Restore default rows button.
    /// </summary>
    internal void RestoreDefaultChoices()
    {
        var defaults = TabletModifierRow.Defaults();

        foreach (var row in Modifiers)
        {
            var fresh = defaults.FirstOrDefault(d => string.Equals(d.Id, row.Id, StringComparison.OrdinalIgnoreCase) &&
                                                     (d.Affix.Length == 0 || string.Equals(d.Affix, row.Affix, StringComparison.OrdinalIgnoreCase)));

            row.Weight = fresh?.Weight ?? 0;
            row.Search = fresh?.Search ?? false;
            row.ValueSteps = fresh?.ValueSteps ?? "";
            row.IgnoreOtherModRangesBelow = fresh?.IgnoreOtherModRangesBelow ?? 0;
        }

        foreach (var fresh in defaults.Where(d => !Modifiers.Any(r => string.Equals(r.Id, d.Id, StringComparison.OrdinalIgnoreCase))))
            Modifiers.Add(fresh);
    }

    /// <summary>
    /// The least value a trade search asks of this modifier, or null to ask only that it is there.
    ///
    /// **Steps rather than the exact roll, so tablets share searches.** Searching each tablet at its own roll made
    /// nearly every one a new search: 67 answers in the cache, all different, with Twinned elites alone asked at 27, 28,
    /// 29, 30, 32 and 36 (2026-10-09). Automatic steps come from the mod's roll range, low to high:
    /// - a count whose top is 3 or less (1-2, 2-3) is asked exactly - one more random modifier, relic or chest is the
    ///   difference that prices it;
    /// - a range four wide or less (pack size 5-7, item rarity 8-12) is asked only to be present;
    /// - otherwise the highest of low, low + half the width, and high - a tenth of the width (at least 1) at or below
    ///   the roll: 25-35 asks 25, 30 or 34; 10-15 asks 10, 12 or 14; 70-100 asks 70, 85 or 97.
    /// A range that did not read is asked at the roll itself.
    /// </summary>
    /// <summary>
    /// The search minimums below this one, one step at a time and at most three, for searching lower when a tablet's
    /// own search finds too few listings: SearchMinimum again with the roll just under the last step. None for a
    /// modifier asked exactly or by count (top 3 or less), whose one less is a different tablet, nor for one asked only
    /// to be present.
    /// </summary>
    internal static IReadOnlyList<int?> LowerSearchMinimums(TabletModifierRow row, Tablets.TabletModifier modifier, int? min)
    {
        var lower = new List<int?>();

        if (row.ValueSteps.Trim().Equals("exact", StringComparison.OrdinalIgnoreCase) || modifier.High is > 0 and <= 3)
            return lower;

        for (var at = min; at is { } step && lower.Count < 3;)
        {
            var next = SearchMinimum(row, modifier with { Values = [step - 1] });

            if (next == at)
                break;

            lower.Add(next);
            at = next;
        }

        return lower;
    }

    internal static int? SearchMinimum(TabletModifierRow row, Tablets.TabletModifier modifier)
    {
        if (modifier.Values.Length == 0)
            return null;

        var roll = modifier.Values[0];
        var steps = row.ValueSteps.Trim();

        if (steps.Equals("any", StringComparison.OrdinalIgnoreCase))
            return null;

        if (steps.Equals("exact", StringComparison.OrdinalIgnoreCase))
            return roll;

        if (steps.Length > 0)
        {
            var listed = steps.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), out var v) ? v : (int?)null)
                .Where(x => x != null && x <= roll)
                .ToList();

            return listed.Count == 0 ? null : listed.Max();
        }

        var (low, high) = (modifier.Low, modifier.High);

        if (high <= 0 || high < low)
            return roll;

        if (high <= 3)
            return roll;

        var width = high - low;

        if (width <= 4)
            return null;

        int[] automatic = [low, low + width / 2, high - Math.Max(1, (int)Math.Round(width / 10d))];

        return automatic.Where(x => x <= roll).DefaultIfEmpty(low).Max();
    }

    internal void Learn(string affixName, string tradeStat, string gameText, IEnumerable<Tablets.TabletModifier> seen)
    {
        foreach (var modifier in seen.Where(m => string.Equals(m.AffixName, affixName, StringComparison.OrdinalIgnoreCase)))
        {
            if (RowOf(modifier) is not { } row)
                continue;

            if (row.TradeStat.Length == 0)
                row.TradeStat = tradeStat;

            // A modifier with no shipped text takes the listing's, which is the game's own wording.
            if (!KnownTexts.ContainsKey(row.Id) && gameText.Length > 0)
                row.Text = gameText;
        }
    }

    /// <summary>
    /// The additional random map modifier's row searched at its exact value and marked as rerolled by a Divine Orb below
    /// 2, on a table saved before rows had their own slack. Migration 24.
    /// </summary>
    internal string SetRandomModifierRule()
    {
        var fresh = TabletModifierRow.Defaults().First(r => r.IgnoreOtherModRangesBelow > 0);
        var changed = 0;

        foreach (var row in Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase)))
        {
            row.ValueSteps = fresh.ValueSteps;
            row.IgnoreOtherModRangesBelow = fresh.IgnoreOtherModRangesBelow;
            changed++;
        }

        return changed == 0 ? "" : "searched the additional random map modifier at its exact value, other modifiers without values below 2";
    }

    /// <summary>
    /// The default rows' short names on a table saved without them, and the price tiers moved from the first
    /// placeholders, 5 and 20, to Low tier under 10 and Mid tier under 100 where they were left at those. Migration 25.
    /// </summary>
    internal string SetShortNamesAndThresholds()
    {
        foreach (var fresh in TabletModifierRow.Defaults())
        {
            foreach (var row in Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase) && r.ShortName.Length == 0))
                row.ShortName = fresh.ShortName;
        }

        if (Math.Abs(Prices.LowTierUnderChaos.Value - 5f) < 0.01f)
            Prices.LowTierUnderChaos.Value = 10f;

        if (Math.Abs(Prices.MidTierUnderChaos.Value - 20f) < 0.01f)
            Prices.MidTierUnderChaos.Value = 100f;

        return "named the tablet modifiers for the hover table and set tablet price thresholds to 1, 10 and 100 chaos";
    }

    /// <summary>
    /// The default short name on every saved row without one, so the hover table's columns are named rather than cut
    /// from the modifier's text. A name already set is kept. Migration 29.
    /// </summary>
    internal string FillShortNames()
    {
        var filled = 0;

        foreach (var fresh in TabletModifierRow.Defaults())
        {
            foreach (var row in Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase) && r.ShortName.Length == 0))
            {
                row.ShortName = fresh.ShortName;
                filled++;
            }
        }

        return filled == 0 ? "" : $"named {filled} tablet modifier(s) for the hover table";
    }

    /// <summary>Sets the default trade stats on rows saved without one. Migration 23.</summary>
    internal string FillTradeStats()
    {
        var filled = 0;

        foreach (var fresh in TabletModifierRow.Defaults())
        {
            foreach (var row in Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase) && r.TradeStat.Length == 0))
            {
                row.TradeStat = fresh.TradeStat;
                filled++;
            }
        }

        return filled == 0 ? "" : $"set the trade site's stat on {filled} tablet modifier row(s)";
    }

    internal Color ColourOf(Tablets.TabletAction action) => action switch
    {
        Tablets.TabletAction.Alchemy => CraftingByHand.AlchemyColour,
        Tablets.TabletAction.Regal => CraftingByHand.RegalColour,
        Tablets.TabletAction.Exalt => CraftingByHand.ExaltColour,
        _ => Color.Transparent,
    };

    /// <summary>The row naming this modifier: the same id stem, and the same affix, else a row with none.</summary>
    internal TabletModifierRow RowOf(Tablets.TabletModifier modifier)
    {
        var affix = AffixOf(modifier);

        return Modifiers.FirstOrDefault(r => string.Equals(r.Id, modifier.Stem, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(r.Affix, affix, StringComparison.OrdinalIgnoreCase)) ??
               Modifiers.FirstOrDefault(r => string.Equals(r.Id, modifier.Stem, StringComparison.OrdinalIgnoreCase) &&
                                             r.Affix.Length == 0);
    }

    /// <summary>What a tablet's modifiers weigh towards a Regal: the sum of their rows' Weight.</summary>
    internal int WeightOf(Tablets.TabletModifier[] modifiers) => modifiers.Sum(m => RowOf(m)?.Weight ?? 0);

    /// <summary>Whether a tablet's modifiers are worth a Regal: their weights reach RegalThreshold.</summary>
    internal bool IsGood(Tablets.TabletModifier[] modifiers) => WeightOf(modifiers) >= Math.Max(1, CraftingByHand.RegalThreshold.Value);

    /// <summary>
    /// Records the modifiers read off a tablet: a new row for one with none, its affix and text on a default row seen
    /// for the first time, and a second row copying the first's choices when the same mod turns up with the other affix.
    /// </summary>
    internal void Note(Tablets.TabletModifier[] modifiers)
    {
        foreach (var modifier in modifiers)
        {
            // A modifier read before its id was: no row for it. One was added once, blank in every column and marked
            // seen, so it could not be removed from the table (2026-10-10). See RemoveBlankRows.
            if (string.IsNullOrWhiteSpace(modifier.Stem))
                continue;

            var affix = AffixOf(modifier);
            var row = RowOf(modifier);

            if (row != null && (row.Affix.Length > 0 || affix.Length == 0))
            {
                row.Seen = true;

                if (row.Text.Length == 0)
                    row.Text = KnownTexts.GetValueOrDefault(modifier.Stem, modifier.Text);

                continue;
            }

            if (row != null)
            {
                row.Affix = affix;
                row.Seen = true;

                if (KnownTexts.TryGetValue(modifier.Stem, out var shipped))
                    row.Text = shipped;
                else if (modifier.Text.Length > 0)
                    row.Text = modifier.Text;

                continue;
            }

            var sibling = Modifiers.FirstOrDefault(r => string.Equals(r.Id, modifier.Stem, StringComparison.OrdinalIgnoreCase));

            Modifiers.Add(new TabletModifierRow
            {
                Id = modifier.Stem, Affix = affix,
                Text = KnownTexts.TryGetValue(modifier.Stem, out var known) ? known
                    : modifier.Text.Length > 0 ? modifier.Text : sibling?.Text ?? "",
                Weight = sibling?.Weight ?? 0, Search = sibling?.Search ?? false, Seen = true,
                TradeStat = sibling?.TradeStat is { Length: > 0 } stat ? stat : KnownTradeStats.GetValueOrDefault(modifier.Stem, ""),
            });
        }
    }

    /// <summary>Removes rows with no modifier id, which Note once added for a modifier read before its id. Migration 32.</summary>
    internal string RemoveBlankRows()
    {
        var removed = Modifiers.RemoveAll(r => string.IsNullOrWhiteSpace(r.Id));

        return removed == 0 ? "" : $"removed {removed} blank row(s) from the tablet modifier table";
    }

    /// <summary>
    /// Brings a table saved by the first version of this section up to the defaults set from the first dump of real
    /// Expedition Tablets (2026-10-09): each default row's text, affix and choices restored, the Verisium Remnant row
    /// added or weighted towards a Regal, and every other row's text cleared so the next read fills it from the translation rather
    /// than the affix name. Choices on other rows are kept. Migration 22.
    /// </summary>
    internal string UpdateFromFirstDump()
    {
        foreach (var fresh in TabletModifierRow.Defaults())
        {
            var existing = Modifiers.Where(r => string.Equals(r.Id, fresh.Id, StringComparison.OrdinalIgnoreCase)).ToList();

            if (existing.Count == 0)
            {
                Modifiers.Add(fresh);

                continue;
            }

            foreach (var row in existing)
            {
                row.Text = fresh.Text;
                row.Weight = Math.Max(row.Weight, fresh.Weight);
                row.Search |= fresh.Search;


                if (row.Affix.Length == 0)
                    row.Affix = fresh.Affix;
            }
        }

        var defaults = TabletModifierRow.Defaults().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Modifiers.Where(r => !defaults.Contains(r.Id)))
            row.Text = "";

        return "updated the tablet modifier table from the first read of Expedition Tablets (Verisium Remnant chance marked to Regal)";
    }

    private static string AffixOf(Tablets.TabletModifier modifier) =>
        modifier.IsPrefix ? "Prefix" : modifier.IsSuffix ? "Suffix" : "";
}
