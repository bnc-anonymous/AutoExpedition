using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.Shared.Enums;
using Newtonsoft.Json;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// **Chaos Orb candidates**: rare tablets one Chaos Orb is expected to raise in worth by at least Chaos Orb minimum gain,
/// and worth no more than Chaos Orb maximum tablet value now - marked in their cell with the expected gain and the chance
/// the tablet comes out worth more, and kept from reforging, for the player to use the orb by hand. What a Chaos Orb does
/// is taken from the game's description, not measured - one of the four modifiers removed, each equally likely, and one
/// added by weight from the same affix's modifiers that the tablet can take.
///
/// Worth: now, the tablet's own price; each outcome, the price of its set - its searched modifiers, the rest filler - from
/// tools/tablet_outcome_survey.py, which searches the trade site once for every set, each exactly that set. Where an
/// outcome's set has no listing the worth is an estimate, and the mark says so (ChaosOrbPriceOfSet). Without the
/// survey's answers no tablet has odds. The survey replaced medians of the pricer's cached listings by their price
/// movers, which left out modifiers that are not movers: a 6-divine tablet whose movers' median was 0.5 divines had its
/// outcomes scaled twelvefold, a Verisium Remnant outcome to 1,494 divines (Mythical Abyss, 2026-10-21).
/// </summary>
internal static partial class Tablets
{
    /// <summary>
    /// Each modifier's spawn weight, by stem, fitted from 14,091 recorded draws - crafts and identifications - with the
    /// modifier groups applied (tools/tablet_mod_weights.py, 2026-10-20). Scaled to 1000 over all; only the ratios within
    /// an affix matter. The game's own spawn weights read 0 for every tablet modifier (dump 2026-10-20 21:44:05).
    /// </summary>
    private static readonly Dictionary<string, double> ChaosOrbModifierWeights = new(StringComparer.Ordinal)
    {
        ["TowerMonsterEffectiveness"] = 52.2, ["TowerDroppedGoldIncrease"] = 52.0, ["TowerDroppedItemRarityIncrease"] = 51.5,
        ["TowerExperienceGainIncrease"] = 48.1, ["TowerMonsterRarityIncrease"] = 40.5, ["TowerPackSizeIncrease"] = 25.3,
        ["TowerRareChestCount"] = 24.9, ["TowerMagicPackIncrease"] = 24.6, ["TowerRarePackIncrease"] = 22.8,
        ["TowerAdditionalAzmeriWisp"] = 6.2, ["TowerAdditionalStoneCircle"] = 6.1, ["TowerAdditionalEssence"] = 5.4,
        ["TowerAdditionalExile"] = 2.6,
        ["TowerExpeditionRunicMonsters"] = 103.4, ["TowerExpeditionIncreasedVerisium"] = 99.8,
        ["TowerExpeditionExplosionRadius"] = 99.3, ["TowerMapDroppedMapsIncrease"] = 42.7,
        ["TowerAdditionalShrineChance"] = 25.6, ["TowerExpeditionLogbookIncrease"] = 25.0,
        ["TowerExpeditionVaalRemnants"] = 24.6, ["TowerExpeditionChanceForTwinnedElites"] = 23.5,
        ["TowerExpeditionBuriedStrongboxes"] = 23.3, ["TowerExpeditionIncreasedMonsterRarity"] = 21.3,
        ["TowerExpeditionFrozenBosses"] = 20.4, ["TowerExpeditionUnearthedRares"] = 20.4,
        ["TowerAdditionalEssenceChance"] = 15.7, ["TowerStoneCircleChance"] = 15.1, ["TowerAdditionalSpiritChance"] = 14.2,
        ["TowerAdditionalStrongboxChance"] = 13.8, ["TowerExpeditionChanceForVerisiumRemnant"] = 8.5,
        ["TowerExpedition2RunicModPassoverChance"] = 8.0, ["TowerExpeditionAdditionalSentinels"] = 7.9,
        ["TowerAdditionalStrongbox"] = 7.9, ["TowerAdditionalShrine"] = 6.3, ["TowerAdditionalExileChance"] = 5.4,
        ["TowerMapAdditionalModifier"] = 3.2, ["TowerMapAdditionalUniqueMonsterModifier"] = 2.4,
    };

    /// <summary>
    /// Modifiers sharing a group in the game's Mods file, by stem, which block each other whatever their affix: each pair
    /// was on none of 5,700 recorded rare tablets (dump 2026-10-20 21:44:05). Every other modifier is its own group.
    /// </summary>
    private static readonly Dictionary<string, string> ChaosOrbModifierGroups = new(StringComparer.Ordinal)
    {
        ["TowerAdditionalShrine"] = "Shrine", ["TowerAdditionalShrineChance"] = "Shrine",
        ["TowerAdditionalStrongbox"] = "Strongbox", ["TowerAdditionalStrongboxChance"] = "Strongbox",
        ["TowerAdditionalEssence"] = "Essence", ["TowerAdditionalEssenceChance"] = "Essence",
        ["TowerAdditionalExile"] = "Exile", ["TowerAdditionalExileChance"] = "Exile",
        ["TowerAdditionalStoneCircle"] = "StoneCircle", ["TowerStoneCircleChance"] = "StoneCircle",
    };

    /// <summary>
    /// One Chaos Orb on a tablet: its worth now and expected after, in exalts, the chance it comes out ahead - worth more
    /// than now and the orb's price together, as the mark's gain is net of the orb - and whether any part of it is an
    /// estimate (see ChaosOrbPriceOfSet), which the mark shows as a leading "~".
    /// </summary>
    internal sealed record ChaosOrbOdds(double NowExalts, double AfterExalts, double ChanceAhead, bool Estimated, bool NowIsOwnPrice);

    /// <summary>
    /// Where tools/tablet_outcome_survey.py writes its answers: one trade search per set of searched modifiers, each
    /// exactly that set. Set by the plugin beside the pricer's cache.
    /// </summary>
    internal static string ChaosOrbSetPricesFile { get; set; } = "";

    /// <summary>
    /// The survey's answers for one league, by set - its searched modifiers' trade stats, sorted, joined by ";" - with
    /// what they were read from, so a newer file, another league or another age limit reads them again.
    /// </summary>
    private sealed record ChaosOrbSetAnswers(DateTime FileWritten, string League, int FreshDays,
        Dictionary<string, TabletPricing.Answer> BySet);

    private static volatile ChaosOrbSetAnswers _chaosOrbSetAnswers;

    /// <summary>Counts every reading of the survey's answers, so odds worked out before one are worked out again.</summary>
    private static int _chaosOrbSetAnswersVersion;

    private static DateTime _chaosOrbSetAnswersCheckedAt = DateTime.MinValue;

    private static int _chaosOrbSetAnswersLoading;

    /// <summary>
    /// Chaos Orb odds by tablet as written - rarity, uses, identified, Price from, and each modifier with its rolls - with
    /// the versions of the pricer's cache and of the survey's answers they were worked out at. By what the tablet is, not
    /// by its TabletOnScreen, which every read of the screen builds again: keyed by that, every tablet's odds were worked
    /// out again every 150ms. Emptied past ChaosOrbOddsKeptMost entries.
    /// </summary>
    private static readonly Dictionary<string, (int Answers, int SetAnswers, ChaosOrbOdds Odds)> ChaosOrbOddsOfTablets =
        new(StringComparer.Ordinal);

    private const int ChaosOrbOddsKeptMost = 2000;

    private static string ChaosOrbGroupOf(string stem) => ChaosOrbModifierGroups.GetValueOrDefault(stem, stem);

    /// <summary>
    /// The survey's answers for the league searched - Trade league, or the league the game is in - no older than Delete
    /// prices older than (days), and without an error. Read on a background task when the file is newer than the copy
    /// held, looked at every ten seconds at most; null until the first reading is done, or with no file.
    /// </summary>
    private static Dictionary<string, TabletPricing.Answer> ChaosOrbSetAnswersOf(GameController gc, TabletRerollingSettings settings)
    {
        var league = settings.TradeSite.TradeLeague.Value is { Length: > 0 } chosen
            ? chosen
            : Safe.Read(gc, static g => g.IngameState.ServerData.League, "") ?? "";
        var freshDays = settings.TradeSite.DeletePricesOlderThanDays.Value;
        var held = _chaosOrbSetAnswers;
        var now = DateTime.UtcNow;

        if (ChaosOrbSetPricesFile.Length == 0 || now - _chaosOrbSetAnswersCheckedAt < TimeSpan.FromSeconds(10) &&
            held != null && held.League == league && held.FreshDays == freshDays)
            return held?.BySet;

        _chaosOrbSetAnswersCheckedAt = now;

        var written = Safe.Read(() => File.Exists(ChaosOrbSetPricesFile) ? File.GetLastWriteTimeUtc(ChaosOrbSetPricesFile) : DateTime.MinValue,
            DateTime.MinValue);

        if (written == DateTime.MinValue)
            return null;

        if (held != null && held.FileWritten == written && held.League == league && held.FreshDays == freshDays)
            return held.BySet;

        if (Interlocked.Exchange(ref _chaosOrbSetAnswersLoading, 1) == 0)
        {
            var path = ChaosOrbSetPricesFile;

            Task.Run(() =>
            {
                try
                {
                    var read = JsonConvert.DeserializeObject<Dictionary<string, TabletPricing.Answer>>(File.ReadAllText(path),
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    var bySet = new Dictionary<string, TabletPricing.Answer>(StringComparer.Ordinal);
                    var oldest = DateTime.UtcNow - TimeSpan.FromDays(freshDays);

                    foreach (var (key, answer) in read ?? [])
                    {
                        // "league|rare|uses=10|affixes=4|stat;stat;...|no other searched", as the survey writes it.
                        var parts = key.Split('|');

                        if (parts.Length != 6 || parts[0] != league || answer?.Listings == null ||
                            answer.Error is { Length: > 0 } || answer.When.ToUniversalTime() < oldest)
                            continue;

                        bySet[parts[4]] = answer;
                    }

                    _chaosOrbSetAnswers = new ChaosOrbSetAnswers(written, league, freshDays, bySet);
                    Interlocked.Increment(ref _chaosOrbSetAnswersVersion);
                }
                catch (Exception)
                {
                    // Read again on the next look; the copy held, if any, stays.
                }
                finally
                {
                    Volatile.Write(ref _chaosOrbSetAnswersLoading, 0);
                }
            });
        }

        return held?.BySet;
    }

    /// <summary>A tablet's set: the trade stats of its searched modifiers, sorted, joined by ";", as the survey keys it.</summary>
    private static string ChaosOrbSetKeyOf(IEnumerable<string> stems, Dictionary<string, string> searchedStatOf) =>
        string.Join(";", stems.Where(searchedStatOf.ContainsKey).Select(s => searchedStatOf[s]).OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// How many of a set's listings, each listed no longer than ChaosOrbExactListingDaysMost when the survey searched,
    /// make its price exact. Chosen: 401 of 464 sets met both on 2026-10-21, against 450 with no age limit.
    /// </summary>
    private const int ChaosOrbExactListingsLeast = 5;

    /// <summary>
    /// The longest a listing may have been up when the survey searched and still count towards its set's price. Chosen.
    /// Listings left up are the ones not bought at their price: in 348 of 446 sets the dearer half of the cheapest ten
    /// was older than the cheaper half, and one set priced at 37 divines rested on two listings 2.3 and 5.8 days old
    /// against a survey median of 0.9 (2026-10-21).
    /// </summary>
    private static readonly TimeSpan ChaosOrbExactListingDaysMost = TimeSpan.FromDays(7);

    /// <summary>
    /// The worth of a tablet of these modifiers at any roll, in exalts: by the Price from setting, from its set's listings
    /// up no longer than ChaosOrbExactListingDaysMost when searched, where there are at least ChaosOrbExactListingsLeast.
    /// **An estimate otherwise**: the dearest of its sets with one searched modifier fewer that meets the same test, since
    /// a searched modifier more rarely makes a tablet cheaper. Null where none does. A set listed a few times was every
    /// listing there was, but what a few sellers ask is not what such a tablet sells for: two asks of 24 and 50 divines
    /// carried more than half the expected worth of tablets listed at one chaos (2026-10-21).
    /// </summary>
    private static (double Exalts, bool Estimated)? ChaosOrbPriceOfSet(IReadOnlyCollection<string> stems,
        Dictionary<string, TabletPricing.Answer> bySet, Dictionary<string, string> searchedStatOf, TabletRerollingSettings settings)
    {
        double PriceOf(IEnumerable<string> of)
        {
            if (!bySet.TryGetValue(ChaosOrbSetKeyOf(of, searchedStatOf), out var answer))
                return double.NaN;

            var recent = answer.Listings.Where(l => answer.When - l.Indexed <= ChaosOrbExactListingDaysMost).ToList();

            return recent.Count < ChaosOrbExactListingsLeast
                ? double.NaN
                : SetPriceCappedByNewer(ChaosOrbSetKeyOf(of, searchedStatOf), answer,
                    PriceOfAnswer(answer with { Listings = recent }, settings, _valuation), settings, _valuation);
        }

        if (PriceOf(stems) is var exact && !double.IsNaN(exact))
            return (exact, false);

        var fewer = stems.Where(searchedStatOf.ContainsKey)
            .Select(left => PriceOf(stems.Where(s => s != left)))
            .Where(p => !double.IsNaN(p))
            .ToList();

        return fewer.Count > 0 ? (fewer.Max(), true) : null;
    }

    /// <summary>
    /// The set survey's price for the set a rare tablet's search covers - its searched modifiers at any roll - and the
    /// answer it is from, in exalts; by the test ChaosOrbPriceOfSet makes for an exact price, and null where that fails,
    /// for another league, or before the survey's answers are read. For the price label, as a floor: the survey's
    /// prices are each set's cheapest listings, at any roll. See PricedFromAnswers.
    /// </summary>
    private static (double Price, TabletPricing.Answer Answer)? SetSurveyPriceOf(TabletSearch search, TabletRerollingSettings settings,
        Valuation valuation)
    {
        if (search.Rarity != "rare" || _chaosOrbSetAnswers is not { } held || held.League != search.League ||
            !held.BySet.TryGetValue(string.Join(";", search.Filters.Select(f => f.Stat).OrderBy(s => s, StringComparer.Ordinal)), out var answer))
            return null;

        var recent = answer.Listings.Where(l => answer.When - l.Indexed <= ChaosOrbExactListingDaysMost).ToList();

        if (recent.Count < ChaosOrbExactListingsLeast)
            return null;

        var price = PriceOfAnswer(answer with { Listings = recent }, settings, valuation);

        return double.IsNaN(price)
            ? null
            : (SetPriceCappedByNewer(string.Join(";", search.Filters.Select(f => f.Stat).OrderBy(s => s, StringComparer.Ordinal)),
                answer, price, settings, valuation), answer with { Listings = recent });
    }

    /// <summary>
    /// A survey answer's set price capped at the cheapest price of a newer search of the same set by the tablet pricer,
    /// whose searches are at a tablet's rolls and so find a subset of the survey's any-roll listings: a cheaper newer one
    /// means the market moved after the survey. See PriceCappedByNewer.
    /// </summary>
    private static double SetPriceCappedByNewer(string statsKey, TabletPricing.Answer surveyed, double price,
        TabletRerollingSettings settings, Valuation valuation)
    {
        if (double.IsNaN(price) || _chaosOrbSetAnswers is not { } held)
            return price;

        foreach (var newer in TabletPricing.AnswersOfModifierSet($"{held.League}|rare|uses={FullUses}|{statsKey}"))
        {
            if (newer.When <= surveyed.When || newer.Error.Length > 0 || newer.Listings.Count < settings.ListingsWanted)
                continue;

            if (PriceOfAnswer(newer, settings, valuation) is var capped && !double.IsNaN(capped) && capped < price)
                price = capped;
        }

        return price;
    }

    /// <summary>The searched modifiers' trade stats by stem: rows with Search ticked and a trade stat.</summary>
    private static Dictionary<string, string> ChaosOrbSearchedStatOf(TabletRerollingSettings settings) =>
        settings.Modifiers.Where(r => r.Search && !string.IsNullOrEmpty(r.TradeStat))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TradeStat, StringComparer.Ordinal);

    /// <summary>
    /// What one Chaos Orb is expected to do to a rare, identified, full-use tablet of two prefixes and two suffixes; null
    /// for any other, without the survey's answers, or with an outcome that cannot be priced.
    /// </summary>
    private static ChaosOrbOdds ChaosOrbOddsOf(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var key = $"{tablet.Uses}|{tablet.Identified}|{settings.TradeSite.PriceFrom.Value}|{TabletWritten(tablet.Rarity, tablet.Modifiers)}";
        var answers = TabletPricing.AnswersVersion;
        var setAnswers = Volatile.Read(ref _chaosOrbSetAnswersVersion);

        lock (ChaosOrbOddsOfTablets)
        {
            if (ChaosOrbOddsOfTablets.TryGetValue(key, out var kept) && kept.Answers == answers &&
                kept.SetAnswers == setAnswers && _chaosOrbSetAnswers != null)
                return kept.Odds;
        }

        var odds = ChaosOrbOddsUnkept(gc, tablet, settings);

        lock (ChaosOrbOddsOfTablets)
        {
            if (ChaosOrbOddsOfTablets.Count >= ChaosOrbOddsKeptMost)
                ChaosOrbOddsOfTablets.Clear();

            ChaosOrbOddsOfTablets[key] = (answers, setAnswers, odds);
        }

        return odds;
    }

    private static ChaosOrbOdds ChaosOrbOddsUnkept(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (tablet.Rarity != ItemRarity.Rare || !tablet.Identified || tablet.Uses != FullUses || tablet.Modifiers.Length != 4 ||
            tablet.Modifiers.Count(m => m.IsPrefix) != 2 || tablet.Modifiers.Count(m => m.IsSuffix) != 2 ||
            tablet.Modifiers.Any(m => !ChaosOrbModifierWeights.ContainsKey(m.Stem)) || _valuation == null)
            return null;

        if (ChaosOrbSetAnswersOf(gc, settings) is not { } bySet)
            return null;

        var searchedStatOf = ChaosOrbSearchedStatOf(settings);
        var stems = tablet.Modifiers.Select(m => m.Stem).ToList();
        var affixOf = settings.Modifiers.Where(r => ChaosOrbModifierWeights.ContainsKey(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Affix, StringComparer.Ordinal);
        var orb = _valuation.Converts("Chaos") ? _valuation.PerExalt("Chaos") : 0d;

        // **Now is its own price**, its trade search's at its rolls, and each outcome its set's at any roll: a tablet with
        // good rolls reads a little worse after the orb than it is, which errs towards keeping it - chosen over valuing
        // both sides by set, which ignores its rolls (2026-10-21). Without its own price yet, its set's price, and the
        // odds are an estimate. Not MostChaosOf, which without its own search gives a ceiling from searches at higher
        // rolls - what the tablet is worth at most, right for not reforging it, wrong for its worth: a tablet bought
        // at about 2 divines was valued at a 7-divine ceiling 3 to 11 days old (Godless Anthem, dump 2026-10-21 02:17:18).
        var ownChaos = gc != null && orb > 0d ? OwnSearchChaosOf(gc, tablet, settings) : null;
        double now;
        var estimated = false;

        if (ownChaos is { } chaos)
            now = chaos * orb;
        else if (ChaosOrbPriceOfSet(stems, bySet, searchedStatOf, settings) is { } set)
            (now, estimated) = (set.Exalts, true);
        else
            return null;

        return ChaosOrbOddsFrom(tablet, settings, bySet, searchedStatOf, now, estimated, ownChaos != null);
    }

    /// <summary>
    /// A tablet's own trade search's price in chaos - the search at its rolls - or null where it has none yet. See
    /// ChaosOrbOddsUnkept for why not MostChaosOf.
    /// </summary>
    private static double? OwnSearchChaosOf(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (_valuation == null || SearchOfTablet(gc, tablet, settings, out _) is not { } search ||
            TabletPricing.Cached(search.Key) is not { Error.Length: 0 } own)
            return null;

        var price = ExaltsForReforging(own, settings);

        return double.IsNaN(price) ? null : TabletChaosOf(PriceCappedByNewer(search.Key, own, price, settings, _valuation), _valuation);
    }

    /// <summary>
    /// The Chaos Orb odds of a rare, four-modifier tablet worth now as given, in exalts. See ChaosOrbOddsUnkept, and
    /// ChaosOrbOddsAtAskingPrice, which gives a listing's asking price as its worth now.
    /// </summary>
    private static ChaosOrbOdds ChaosOrbOddsFrom(TabletOnScreen tablet, TabletRerollingSettings settings,
        Dictionary<string, TabletPricing.Answer> bySet, Dictionary<string, string> searchedStatOf, double now,
        bool estimated, bool nowIsOwnPrice)
    {
        var stems = tablet.Modifiers.Select(m => m.Stem).ToList();
        var affixOf = settings.Modifiers.Where(r => ChaosOrbModifierWeights.ContainsKey(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Affix, StringComparer.Ordinal);
        var orb = _valuation.Converts("Chaos") ? _valuation.PerExalt("Chaos") : 0d;
        var after = 0d;
        var better = 0d;

        foreach (var removed in tablet.Modifiers)
        {
            var kept = stems.Where(s => s != removed.Stem).ToList();
            var taken = kept.Select(ChaosOrbGroupOf).ToHashSet(StringComparer.Ordinal);
            var affix = removed.IsPrefix ? "Prefix" : "Suffix";
            var pool = ChaosOrbModifierWeights
                .Where(w => affixOf.GetValueOrDefault(w.Key) == affix && !taken.Contains(ChaosOrbGroupOf(w.Key)))
                .ToList();
            var total = pool.Sum(w => w.Value);

            if (total <= 0d)
                return null;

            foreach (var (stem, weight) in pool)
            {
                var chance = weight / total / tablet.Modifiers.Length;

                if (ChaosOrbPriceOfSet(kept.Append(stem).ToList(), bySet, searchedStatOf, settings) is not { } worth)
                    return null;

                estimated |= worth.Estimated;
                after += chance * worth.Exalts;

                if (worth.Exalts > now + orb)
                    better += chance;
            }
        }

        return new ChaosOrbOdds(now, after, better, estimated, nowIsOwnPrice);
    }

    /// <summary>
    /// The Chaos Orb odds of a tablet listed at this asking price, in exalts, bought at it; null as ChaosOrbOddsUnkept
    /// gives null, or without the survey's answers.
    /// </summary>
    private static ChaosOrbOdds ChaosOrbOddsAtAskingPrice(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings,
        double askingExalts)
    {
        if (tablet.Rarity != ItemRarity.Rare || !tablet.Identified || tablet.Uses != FullUses || tablet.Modifiers.Length != 4 ||
            tablet.Modifiers.Count(m => m.IsPrefix) != 2 || tablet.Modifiers.Count(m => m.IsSuffix) != 2 ||
            tablet.Modifiers.Any(m => !ChaosOrbModifierWeights.ContainsKey(m.Stem)) || _valuation == null ||
            ChaosOrbSetAnswersOf(gc, settings) is not { } bySet)
            return null;

        return ChaosOrbOddsFrom(tablet, settings, bySet, ChaosOrbSearchedStatOf(settings), askingExalts, false, false);
    }

    /// <summary>
    /// Asking prices read from listings' tooltips in another player's merchant window, in exalts, by the listing's item
    /// entity address; emptied when the window closes. See ShowChaosOrbOddsInOtherMerchants.
    /// </summary>
    private static readonly Dictionary<long, double> OtherMerchantAskingPrices = [];

    /// <summary>The odds of each listing at its asking price, with the survey answers' version. See DrawOtherMerchantChaosOrbMarks.</summary>
    private static readonly Dictionary<(long Address, double Asking), (int SetAnswers, ChaosOrbOdds Odds)> OtherMerchantOdds = [];

    /// <summary>The other player's merchant window's tablets at the last read, and when that was.</summary>
    private static List<TabletOnScreen> _otherMerchantTablets = [];

    private static DateTime _otherMerchantReadAt = DateTime.MinValue;

    /// <summary>
    /// The asking price at the end of a listing's tooltip, in exalts: a count written "18x" and, after it, the currency's
    /// name ("Chaos Orb"), as the tooltip of a tablet in another player's merchant window ends (dump 2026-10-21 02:11:23).
    /// Null where none is found or the currency has no rate.
    /// </summary>
    private static double? AskingPriceOfTooltip(Element tooltip)
    {
        var texts = new List<string>();
        var stack = new Stack<(Element Element, int Depth)>();

        if (tooltip != null)
            stack.Push((tooltip, 0));

        while (stack.Count > 0 && texts.Count < 120)
        {
            var (element, depth) = stack.Pop();

            if ((Safe.Read(element, static e => e.Text, null) ?? "").Trim() is { Length: > 0 } text)
                texts.Add(text);

            var children = Safe.Read(element, static e => e.Children, null) ?? [];

            for (var i = children.Count - 1; i >= 0 && depth < 12; i--)
                stack.Push((children[i], depth + 1));
        }

        for (var i = texts.Count - 2; i >= 0; i--)
        {
            var count = System.Text.RegularExpressions.Regex.Match(texts[i], @"^(\d+(?:\.\d+)?)x$");

            if (!count.Success)
                continue;

            var unit = texts[i + 1] switch
            {
                "Chaos Orb" => "Chaos",
                "Divine Orb" => "Divine",
                "Vaal Orb" => "Vaal",
                "Exalted Orb" => "Exalted",
                _ => null,
            };

            if (unit == null || unit != "Exalted" && !_valuation.Converts(unit))
                return null;

            var amount = double.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            return amount * (unit == "Exalted" ? 1d : _valuation.PerExalt(unit));
        }

        return null;
    }

    /// <summary>
    /// With Show Chaos Orb odds in other players' merchant windows: reads that window's tablets every ReadEvery, the
    /// asking price of the one hovered from its tooltip, and marks each tablet whose price has been read with its Chaos
    /// Orb odds bought at it - orange at Chaos Orb minimum gain or more, grey below.
    /// </summary>
    private static void DrawOtherMerchantChaosOrbMarks(Graphics graphics, GameController gc, TabletRerollingSettings settings)
    {
        if (!settings.Prices.ShowChaosOrbOddsInOtherMerchants.Value || _valuation == null || !_valuation.Converts("Divine") ||
            !_valuation.Converts("Chaos"))
            return;

        var window = Safe.Read(gc, static g => g.IngameState.IngameUi.PurchaseWindowHideout, null);

        if (window == null || !Safe.Read(window, static w => w.IsVisible, false))
        {
            OtherMerchantAskingPrices.Clear();
            _otherMerchantTablets = [];
            OtherMerchantOdds.Clear();
            return;
        }

        if (DateTime.UtcNow - _otherMerchantReadAt >= ReadEvery)
        {
            var found = new List<TabletOnScreen>();
            var entities = new HashSet<long>();

            foreach (var item in Safe.Read(window, static w => w.TabContainer?.VisibleStash?.VisibleInventoryItems, null) ?? [])
                Add(found, entities, "other merchant", item, Safe.Read(item, static i => i.Item, null));

            _otherMerchantTablets = found;
            _otherMerchantReadAt = DateTime.UtcNow;

            var mouse = ImGuiNET.ImGui.GetMousePos();

            foreach (var tablet in found)
            {
                if (mouse.X < tablet.Rect.Left || mouse.X > tablet.Rect.Right || mouse.Y < tablet.Rect.Top || mouse.Y > tablet.Rect.Bottom)
                    continue;

                var tooltip = Safe.Read(tablet.Element, static e => e.Tooltip, null);

                if (Safe.Read(tooltip, static t => t.IsVisible, false) && AskingPriceOfTooltip(tooltip) is { } asking &&
                    AddressOf(tablet) is var address and not 0L)
                    OtherMerchantAskingPrices[address] = asking;
            }
        }

        var divine = _valuation.PerExalt("Divine");
        var orb = _valuation.PerExalt("Chaos");

        foreach (var tablet in _otherMerchantTablets)
        {
            // Not marked at all when dearer than Chaos Orb maximum tablet value, as in the player's own tabs.
            if (!OtherMerchantAskingPrices.TryGetValue(AddressOf(tablet), out var asking) ||
                asking > settings.Prices.ChaosOrbMaximumValueDivines.Value * divine)
                continue;

            // Kept by listing and price until the survey's answers change: worked out every frame, they were about 80
            // outcome prices a tablet, for every tablet in the window.
            var key = (AddressOf(tablet), asking);
            var version = Volatile.Read(ref _chaosOrbSetAnswersVersion);

            if (!OtherMerchantOdds.TryGetValue(key, out var kept) || kept.SetAnswers != version)
                OtherMerchantOdds[key] = kept = (version, ChaosOrbOddsAtAskingPrice(gc, tablet, settings, asking));

            if (kept.Odds is not { } odds)
                continue;

            var gain = (odds.AfterExalts - odds.NowExalts - orb) / divine;
            var gainText = (odds.Estimated ? "~" : "") + $"{gain:+0.#;-0.#;0}d";
            var colour = gain >= settings.Prices.ChaosOrbMinimumGainDivines.Value ? ChaosOrbMarkColour : ChaosOrbOddsColour;
            var at = new Vector2(tablet.Rect.Left + BorderGap + 1f, tablet.Rect.Top + BorderGap + 1f);

            graphics.DrawTextWithBackground(gainText, at, colour, FontAlign.Left, LabelBackground);
            graphics.DrawTextWithBackground($"{odds.ChanceAhead:0%}", at with { Y = at.Y + graphics.MeasureText(gainText).Y },
                colour, FontAlign.Left, LabelBackground);
        }
    }

    /// <summary>
    /// The worth of a tablet of these modifiers at any roll by the survey's answers, in exalts, or null; for the Chaos
    /// Orb predictions file, which sets what an orb did beside what was expected. See ChaosOrbPriceOfSet.
    /// </summary>
    internal static double? ChaosOrbWorthOfModifiers(GameController gc, IEnumerable<TabletModifier> modifiers, TabletRerollingSettings settings) =>
        ChaosOrbSetAnswersOf(gc, settings) is { } bySet &&
        ChaosOrbPriceOfSet(modifiers.Select(m => m.Stem).ToList(), bySet, ChaosOrbSearchedStatOf(settings), settings) is { } worth
            ? worth.Exalts
            : null;

    /// <summary>
    /// A rare tablet's Chaos Orb odds for the dump: worth now and whether that is its own price, expected worth after,
    /// the gain net of the orb, the chance of coming out ahead, and whether it is a candidate or which limit it misses.
    /// </summary>
    internal static string ChaosOrbOddsSaid(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (_valuation == null || !_valuation.Converts("Divine") || !_valuation.Converts("Chaos"))
            return "Chaos Orb: no odds, no divine or chaos rate";

        if (ChaosOrbOddsOf(gc, tablet, settings) is not { } odds)
            return "Chaos Orb: no odds (not 2 prefixes and 2 suffixes at full uses, no survey answers for this league " +
                   "- tools/tablet_outcome_survey.py - or an outcome whose set and every set one modifier fewer have no listing)";

        var divine = _valuation.PerExalt("Divine");
        var orb = _valuation.PerExalt("Chaos");
        var gain = (odds.AfterExalts - odds.NowExalts - orb) / divine;
        var ownPrice = odds.NowIsOwnPrice ? "its own price" : "its set's price, no own price yet";
        var verdict = !settings.Prices.MarkChaosOrbCandidates.Value ? "marking off"
            : ChaosOrbWorthForMaximum(gc, tablet, settings, odds) > settings.Prices.ChaosOrbMaximumValueDivines.Value * divine ? "not a candidate: worth over the maximum value"
            : gain < settings.Prices.ChaosOrbMinimumGainDivines.Value ? "not a candidate: gain under the minimum"
            : "candidate";

        return $"Chaos Orb: now {odds.NowExalts / divine:0.00}d ({ownPrice}), after {odds.AfterExalts / divine:0.00}d, " +
               $"gain {gain:+0.00;-0.00;0.00}d net of the orb, {odds.ChanceAhead:0%} ahead" +
               (odds.Estimated ? ", an estimate" : "") + $"; {verdict}";
    }

    private static bool IsChaosOrbCandidate(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings) =>
        ChaosOrbCandidateOdds(gc, tablet, settings) != null;

    private static ChaosOrbOdds ChaosOrbCandidateOdds(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        if (!settings.Prices.MarkChaosOrbCandidates.Value || _valuation == null || !_valuation.Converts("Divine") ||
            !_valuation.Converts("Chaos") || ChaosOrbOddsOf(gc, tablet, settings) is not { } odds)
            return null;

        var divine = _valuation.PerExalt("Divine");
        var gain = odds.AfterExalts - odds.NowExalts - _valuation.PerExalt("Chaos");

        return ChaosOrbWorthForMaximum(gc, tablet, settings, odds) <= settings.Prices.ChaosOrbMaximumValueDivines.Value * divine &&
               gain >= settings.Prices.ChaosOrbMinimumGainDivines.Value * divine
            ? odds
            : null;
    }

    /// <summary>
    /// What a tablet's worth is taken as for Chaos Orb maximum tablet value, in exalts: its own price where it has one;
    /// without, the higher of its set's price and the ceiling from searches at higher rolls (MostChaosOf), since the
    /// maximum keeps a valuable tablet from the orb and an unknown worth is not to be assumed low. Its set's cheapest
    /// listings put a tablet at 3 divines, under a 3-divine maximum, while the one search near its rolls said at most 35
    /// (Eerie Secrets, dump 2026-10-21 02:57:46). The gain is still worked out from the set's price.
    /// </summary>
    private static double ChaosOrbWorthForMaximum(GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings,
        ChaosOrbOdds odds) =>
        odds.NowIsOwnPrice || MostChaosOf(gc, tablet, settings) is not { } ceilingChaos
            ? odds.NowExalts
            : Math.Max(odds.NowExalts, ceilingChaos * _valuation.PerExalt("Chaos"));

    /// <summary>
    /// A Chaos Orb candidate's mark, in the top left of its cell: the expected gain net of the orb, with a leading "~" where
    /// any part is an estimate, over the chance it comes out ahead of the orb. With Show Chaos Orb odds on every tablet, every tablet the odds can be worked out for is marked too, whatever its
    /// gain or worth now and with marking off, in ChaosOrbOddsColour: a candidate in ChaosOrbMarkColour as ever. Only the
    /// mark: what is reforged is still decided by the candidates alone.
    /// </summary>
    private static void DrawChaosOrbMark(Graphics graphics, GameController gc, TabletOnScreen tablet, TabletRerollingSettings settings)
    {
        var candidate = ChaosOrbCandidateOdds(gc, tablet, settings);
        var withAlt = settings.Prices.ShowChaosOrbOddsWhileHoldingAlt.Value && AltHeld();
        var odds = candidate ?? ((settings.Prices.ShowChaosOrbOddsOnEveryTablet.Value || withAlt) && _valuation != null &&
                                 _valuation.Converts("Divine") && _valuation.Converts("Chaos")
            ? ChaosOrbOddsOf(gc, tablet, settings)
            : null);

        if (odds == null)
            return;

        var divine = _valuation.PerExalt("Divine");
        var gain = (odds.AfterExalts - odds.NowExalts - _valuation.PerExalt("Chaos")) / divine;
        // The gain over the chance, on two lines: "+1.6d 14%" on one is a character wider than a tablet's cell and ran onto
        // the next one.
        var gainText = (odds.Estimated ? "~" : "") + $"{gain:+0.#;-0.#;0}d";
        // With Alt held, by the gain's sign alone: orange for a gain, grey for a loss, whatever the limits.
        var colour = withAlt ? gain > 0d ? ChaosOrbMarkColour : ChaosOrbOddsColour
            : candidate != null ? ChaosOrbMarkColour : ChaosOrbOddsColour;
        var at = new Vector2(tablet.Rect.Left + BorderGap + 1f, tablet.Rect.Top + BorderGap + 1f);

        graphics.DrawTextWithBackground(gainText, at, colour, FontAlign.Left, LabelBackground);
        graphics.DrawTextWithBackground($"{odds.ChanceAhead:0%}", at with { Y = at.Y + graphics.MeasureText(gainText).Y },
            colour, FontAlign.Left, LabelBackground);
    }

    /// <summary>The colour of Chaos Orb odds on a tablet that is not a candidate: grey. See DrawChaosOrbMark.</summary>
    private static readonly Color ChaosOrbOddsColour = Color.FromArgb(255, 170, 170, 170);

    /// <summary>The Chaos Orb candidate mark's colour: orange, used by no price tier.</summary>
    private static readonly Color ChaosOrbMarkColour = Color.FromArgb(255, 255, 170, 60);
}
