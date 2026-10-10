using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;
using ExileCore2.Shared.Cache;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// What a remnant could be turned into, and what that is worth.
///
/// <paramref name="Carries"/> is the second half of that: the propagation percentage this
/// particular combination would put into the slots that pass forward. Recorded here because it can
/// only be worked out where the recipe is - by the time this list reaches the overlay the recipes
/// are gone - and because without it the ground and the combinations window answer the same
/// question two different ways, one of them counting propagation and the other not.
/// </summary>
/// <param name="Local">What the ordinary slots are worth to this remnant's own waves.</param>
/// <param name="Locals">
/// Which runes make up that figure, and what each is worth on these waves.
///
/// Named rather than counted, for two reasons. Concentration needs the COUNT - waves wearing five
/// modifiers at once are worth more than waves wearing one of equal size - and the scoring loop
/// needs the NAMES, so it can strike out any the chain is already propagating into those same
/// waves. A summed percentage answers neither. See Propagation.Locally.
/// </param>
/// <param name="Held">
/// Every rune the recipe puts in a slot, priced or not, which is what its own effects are read from. See
/// Propagation.HeldRunes and Weighing.OwnEffectsOfChoices.
/// </param>
/// <param name="CarryingWaveShares">
/// The share of this remnant's own waves each rune in Carrying is on, same order. See Propagation.WaveSharesOfIds.
/// </param>
/// <param name="SlotRunes">The rune in each slot of the recipe, by id, in slot order. See Propagation.RunesPerWave.</param>
internal sealed record Reward(string Name, double Value, int RuneCount, float Carries = 0f,
    string[] Carrying = null, float Local = 0f, (string Id, float Worth)[] Locals = null,
    string Recipe = "", string[] Held = null, float[] CarryingWaveShares = null, float[] HeldWaveShares = null,
    string[] SlotRunes = null);

/// <summary>
/// One rune slot that carries forward, and which runes could end up in it.
///
/// A remnant passes some of its runes on to every remnant after it in the chain - the game draws a
/// yellow border round those slots. Which slot carries forward is fixed when the site is generated;
/// WHICH RUNE ends up in it is not, because the runes come from whichever combination you pick. So
/// this is the slot plus the candidates, and the choice is only already made when there is one.
/// </summary>
internal sealed record Passes(int Slot, List<string> Runes)
{
    /// <summary>True when every reachable combination puts the same rune here: nothing to choose.</summary>
    public bool Settled => Runes.Count == 1;
}

/// <summary>
/// Pricing the remnants.
///
/// A remnant is a socketed object that turns into one of a set of recipes, and which recipes are
/// available is fixed the moment the site is generated: one rune is already placed, in a known slot,
/// and that plus the socket count and the area level narrows the list. So the best outcome a remnant
/// can reach is knowable before anything is dug up, which is the number worth putting on screen.
///
/// Prices come from NinjaPricer over the plugin bridge, so there is no network here and no second
/// copy of a price list. Without NinjaPricer installed every recipe is worth zero and the remnants
/// fall back to being ranked by socket count, which is how they were ranked anyway.
///
/// The filtering rule is the game's, worked out from what Expedition2Good does with the same tables
/// - that plugin ships no licence, so this is a reimplementation from the rule rather than a copy of
/// its code.
/// </summary>
internal sealed class Valuation
{
    /// <summary>
    /// The Expedition2 encounter-data component hash.
    ///
    /// A magic number, and the only one in this plugin. It is how the encounter data is reached
    /// without the ground label being on screen, which matters because the label is only there when
    /// you are stood next to the remnant and the value wants knowing from across the site. If a
    /// patch moves it this returns null and remnants quietly fall back to socket count.
    /// </summary>
    private const ushort EncounterDataHash = 0x87B2;

    private readonly GameController _gc;
    private readonly AutoExpeditionSettings _settings;
    private readonly TimeCache<Dictionary<Expedition2Recipe, double>> _prices;

    /// <summary>Every recipe, bucketed by RuneCountRequired. See where it is built.</summary>
    private readonly TimeCache<ILookup<int, Expedition2Recipe>> _recipesByRuneCount;

    /// <summary>What one Divine, one Chaos and one Vaal Orb are worth in exalts, or zero when unknown.</summary>
    private double _divine;
    private double _chaos;
    private double _vaal;

    public Valuation(GameController gc, AutoExpeditionSettings settings)
    {
        _gc = gc;
        _settings = settings;

        // Rebuilt on a timer because NinjaPricer's own data arrives asynchronously and updates as
        // it goes; a price read once at load would be zero for every recipe.
        _prices = new TimeCache<Dictionary<Expedition2Recipe, double>>(BuildPrices, 5000);

        // **Recipes grouped by how many runes they want, so a remnant's shape picks its bucket.**
        //
        // RecipesFittingRemnant rejects on rune count before anything else and that term throws out
        // most of the table - 191 of 318 rejections at one 3-socket remnant - so walking all of
        // them to find the few dozen that fit is work done to be told no. The reroll advisor makes
        // this matter: it asks the same question for every shape a roll could produce rather than
        // once per remnant on the ground.
        //
        // A timer rather than a one-shot Lazy even though the table is static: EntriesList reads
        // empty until the game's dat files are up, and a Lazy would keep that empty answer for the
        // life of the process.
        _recipesByRuneCount = new TimeCache<ILookup<int, Expedition2Recipe>>(
            () => (Safe.Read(() => _gc.Files.Expedition2Recipes.EntriesList, null)
                   ?? new List<Expedition2Recipe>())
                .ToLookup(r => Safe.Read(() => r.RuneCountRequired, -1)), 5000);
    }

    /// <summary>Whether NinjaPricer answered, so the UI can say why everything is worth nothing.</summary>
    public bool Priced { get; private set; }

    /// <summary>
    /// How many exalts one of the chosen currency is worth.
    ///
    /// Everything is priced in exalts throughout - NinjaPricer's bridge returns
    /// PrimaryValue * PrimaryToExaltedRate, whatever the legacy "chaos value" name on it suggests -
    /// so this only converts at the point of display and nothing downstream has to care.
    ///
    /// The rates are not asked for specially: a Divine Orb is an item with a price like any other,
    /// so it is looked up in the same table by the same route. Returns one when the rate is unknown,
    /// which shows exalt figures under a divine label rather than dividing by zero - wrong, but
    /// visibly and recoverably so.
    /// </summary>
    public double PerExalt(string unit) => unit switch
    {
        "Divine" => _divine > 0d ? _divine : 1d,
        "Chaos" => _chaos > 0d ? _chaos : 1d,
        "Vaal" => _vaal > 0d ? _vaal : 1d,
        _ => 1d,
    };

    /// <summary>Whether the chosen unit has a known rate, so the UI can fall back honestly.</summary>
    public bool Converts(string unit) => unit switch
    {
        "Divine" => _divine > 0d,
        "Chaos" => _chaos > 0d,
        "Vaal" => _vaal > 0d,
        _ => true,
    };

    /// <summary>The short form each unit is printed with, one letter apiece.</summary>
    public static string Suffix(string unit) => unit switch
    {
        "Divine" => "d",
        "Chaos" => "c",
        _ => "ex",
    };

    /// <summary>
    /// How many recipes currently carry a price above nothing, and how many there are.
    ///
    /// **Because a plugin with no prices looks confident and is not.** NinjaPricer fetches from
    /// poe.ninja on a background thread and takes seconds; ask it anything before that lands and
    /// every recipe is worth nought. Nothing breaks - the reward lists simply fall back to the
    /// preference order, the take line disagrees with the top of the list on every remnant, and no
    /// remnant is ever rich enough to mark must take.
    ///
    /// All of which reads as a fault rather than as "the prices have not arrived". Said out loud in
    /// the dump so the next person to see it spends no time on it.
    /// </summary>
    /// <summary>
    /// Why the prices cannot be trusted right now, or null when they can.
    ///
    /// **A plugin with no prices looks exactly as confident as one with them, and that is dangerous.**
    /// Priced only says the NinjaPricer bridge answered; it says nothing about whether the bridge had
    /// any data. Caught in game with the bridge present and four of three hundred and twenty two
    /// recipes priced above nothing: every reward ranked equal at zero, the solver picked among them
    /// arbitrarily, the score read as usual and nothing on screen suggested anything was wrong.
    ///
    /// The partial case is the dangerous one precisely because it is not the empty case - a handful
    /// of priced recipes is enough to look alive while being useless. So the test is a SHARE, not a
    /// presence, and it is deliberately generous: a real list prices most of what it is asked about.
    ///
    /// Transient by nature - the fetch takes seconds on entering a zone - so this is worded as a
    /// state rather than an error, and it clears itself.
    /// </summary>
    public string Unpriced()
    {
        var (priced, total) = Pricing();

        // **One sentence, and it names the thing that went wrong.** This said "PRICES INCOMPLETE -
        // only 4 of 322 recipes priced", which describes a symptom and leaves the reader to work out
        // that NinjaPricer is the component at fault and fetching is the thing that failed. The
        // counts belong in the log and the dump, where there is room to read them.
        return total == 0 || priced == 0 || priced * 100d / total < Trusted
            ? "Failed to fetch prices from NinjaPricer"
            : null;
    }

    /// <summary>The counts behind Unpriced, for the log line and the dump. See Unpriced.</summary>
    public string Priceless()
    {
        var (priced, total) = Pricing();

        return total == 0
            ? "no recipe list at all"
            : priced == 0
                ? $"0 of {total:N0} recipes carry a price"
                : $"only {priced:N0} of {total:N0} recipes carry a price";
    }

    /// <summary>
    /// The share of recipes that must carry a price before the list is worth ranking on.
    ///
    /// Not tuned - a working fetch prices nearly everything, and the failure seen was four of three
    /// hundred and twenty two. Anything between those is equally untrustworthy, so the line is drawn
    /// where it plainly separates them rather than where a measurement put it.
    /// </summary>
    private const double Trusted = 25d;

    public (int Priced, int Total) Pricing()
    {
        var prices = _prices.Value;

        if (prices == null || prices.Count == 0)
            return (0, 0);

        var priced = 0;

        foreach (var (_, value) in prices)
        {
            if (value > 0d)
                priced++;
        }

        return (priced, prices.Count);
    }

    private Dictionary<Expedition2Recipe, double> BuildPrices()
    {
        // **Frozen where the setting says so - see DebugSettings.FreezePrices.**
        //
        // Held here rather than by stopping the cache from expiring, because the cache is what every
        // reader goes through: left to expire it would hand back an empty dictionary and every reward
        // would read as worthless. Returning the previous answer keeps the shape of everything
        // downstream exactly as it was and only stops the numbers moving.
        //
        // Nothing to hand back on the first build, so the first one always happens: freezing before
        // NinjaPricer has answered would freeze "no prices", which is not a state worth preserving.
        var frozen = Safe.Read(() => _settings.Debug.FreezePrices.Value, false);

        // **A held table belongs to the area it was built in.**
        //
        // The dictionary is keyed on Expedition2Recipe objects, and Everything does not read the
        // game's recipe table - it enumerates THIS dictionary and filters it, comparing each
        // recipe's rune against the one the remnant states now. So a table whose keys were read in
        // one area and compared against another area's readings can reject every recipe, and a
        // remnant whose option list comes back empty is never priced at all: no reward list, no
        // choices, and a propagation priced off every rune its slots could hold.
        //
        // Freezing is meant to hold the PRICES still so two runs can be compared without the feed
        // moving underneath them. Holding object identities across a zone change is not part of
        // that and cannot be what anybody wanted, so the area is what the hold is keyed on.
        var here = Safe.Read(() => _gc.Area.CurrentArea.Hash, 0u);

        if (here != _heldIn)
        {
            _heldIn = here;
            _held = null;
            _stored = null;
        }

        // The stored prices, read once an area: what the freeze holds, and what stands in for live prices that are
        // missing or incomplete. See StoredPrices.
        _stored ??= StoredPrices();

        if (frozen && _stored != null)
        {
            // Whatever answered before still answers, or nothing downstream would believe the table. The rates too, as
            // last stored: the freeze returned before any was read, and no tablet could be priced in chaos under it.
            Priced = true;
            UseRates(0d, 0d, 0d);

            return _held = _stored;
        }

        var prices = new Dictionary<Expedition2Recipe, double>();

        var value = Safe.Read(
            () => _gc.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue"),
            null);

        Priced = value != null;

        UseRates(Rate(value, "Divine Orb"), Rate(value, "Chaos Orb"), Rate(value, "Vaal Orb"));

        var recipes = Safe.Read(() => _gc.Files.Expedition2Recipes.EntriesList, null);

        foreach (var recipe in recipes ?? new List<Expedition2Recipe>())
        {
            var reward = Safe.Read(() => recipe.Reward, null);
            var count = Safe.Read(() => recipe.RewardCount, 0);

            // A hand-written price first, because the cases it covers are the ones the price list
            // cannot answer rather than ones it answers badly.
            var named = Override(recipe);

            prices[recipe] = named >= 0d
                ? named * Math.Max(1, count)
                : reward == null || value == null
                    ? 0d
                    : Safe.Read(() => value(reward), 0d) * Math.Max(1, count);
        }

        // **Live prices when they are whole, the stored ones when they are not, and the store kept up to date.**
        //
        // Prices came only from NinjaPricer, so until it answered every reward was worth nothing, and the store was
        // written only for the freeze. Now the store holds the last prices read: written whenever live prices change
        // and the freeze is off, read whenever live prices are missing - no pricer, or one still loading that prices
        // fewer than WholeShareOfStored of the rewards the store does. A table of noughts never overwrites one that
        // says something. See StoredPrices and StorePrices.
        var positive = prices.Values.Count(x => x > 0d);
        var storedPositive = _stored?.Values.Count(x => x > 0d) ?? 0;

        if (_stored != null && (!Priced || positive < storedPositive * WholeShareOfStored))
        {
            Priced = true;
            PricesFileSaid = $"live prices {(positive == 0 ? "not read yet" : $"price {positive:N0} rewards against {storedPositive:N0} stored")} - " +
                             "the stored prices used";

            return _held = _stored;
        }

        if (Priced)
        {
            _held = prices;

            if (!frozen || _stored == null)
                StorePrices(prices, recipes);
        }

        return prices;
    }

    /// <summary>
    /// The share of the rewards the stored prices price that live prices must price before they are used and stored.
    /// Chosen: high enough that a pricer still loading, answering nought for most rewards, is not taken for a market
    /// that moved. See BuildPrices.
    /// </summary>
    private const double WholeShareOfStored = 0.8;

    /// <summary>The stored prices as read this area, or null when there are none that fit. See StoredPrices.</summary>
    private Dictionary<Expedition2Recipe, double> _stored;

    /// <summary>The last prices that were actually read. See BuildPrices and FreezePrices.</summary>
    private Dictionary<Expedition2Recipe, double> _held;

    /// <summary>Which area the held table was built in. See BuildPrices.</summary>
    private uint _heldIn;

    /// <summary>
    /// The Divine, Chaos and Vaal Orb rates in use: those read live, each stored when it changes; a stored one where the
    /// live one is missing. Vaal for tablets, which are often listed in it: 442 of 1587 cached listings on 2026-10-10. With NinjaPricer failing to fetch, there was no rate at all (every dump 2026-10-09 23:13 to
    /// 23:45), so no tablet listed in chaos or divine could be priced - and before that was handled, one listed only in
    /// chaos was taken as worthless and reforged. Rates move slowly enough that the last read is a fair stand-in.
    /// </summary>
    private void UseRates(double liveDivine, double liveChaos, double liveVaal)
    {
        var stored = StoredRates();

        _divine = liveDivine > 0d ? liveDivine : stored.Divine;
        _chaos = liveChaos > 0d ? liveChaos : stored.Chaos;
        _vaal = liveVaal > 0d ? liveVaal : stored.Vaal;

        // Each live rate replaces its stored one; a missing one keeps what was stored.
        if (liveDivine > 0d || liveChaos > 0d || liveVaal > 0d)
        {
            var keep = (Divine: _divine, Chaos: _chaos, Vaal: _vaal);

            if (keep != stored)
                StoreRates(keep.Divine, keep.Chaos, keep.Vaal);
        }

        RatesSaid = liveDivine > 0d && liveChaos > 0d && liveVaal > 0d
            ? "live"
            : _divine > 0d || _chaos > 0d || _vaal > 0d
                ? $"stored, from {_ratesStoredAt:yyyy-MM-dd HH:mm} (live: divine {(liveDivine > 0d ? "read" : "missing")}, " +
                  $"chaos {(liveChaos > 0d ? "read" : "missing")}, vaal {(liveVaal > 0d ? "read" : "missing")})"
                : "none: no live rates and none stored";
    }

    /// <summary>Where the last rates read are stored. See UseRates.</summary>
    private static string RatesFile => Path.Combine(Kept.Home, "last_rates.tsv");

    /// <summary>Which rates are in use, for the dump. See UseRates.</summary>
    public static string RatesSaid { get; private set; } = "not read yet";

    /// <summary>The rates as stored, read once and kept; noughts when there are none.</summary>
    private (double Divine, double Chaos, double Vaal)? _ratesStored;

    private DateTime _ratesStoredAt;

    private (double Divine, double Chaos, double Vaal) StoredRates()
    {
        if (_ratesStored is { } kept)
            return kept;

        if (Kept.Home.Length == 0)
            return (0d, 0d, 0d);

        try
        {
            if (File.Exists(RatesFile))
            {
                var lines = File.ReadAllLines(RatesFile);
                var divine = double.Parse(lines[0].Split('\t')[1], CultureInfo.InvariantCulture);
                var chaos = double.Parse(lines[1].Split('\t')[1], CultureInfo.InvariantCulture);
                // A file written before vaal was read has two lines.
                var vaal = lines.Length > 2 ? double.Parse(lines[2].Split('\t')[1], CultureInfo.InvariantCulture) : 0d;

                _ratesStoredAt = File.GetLastWriteTime(RatesFile);
                return (_ratesStored = (divine, chaos, vaal)).Value;
            }
        }
        catch (Exception)
        {
            // An unreadable file is taken as none: the live rates write a fresh one.
        }

        return (_ratesStored = (0d, 0d, 0d)).Value;
    }

    private void StoreRates(double divine, double chaos, double vaal)
    {
        if (Kept.Home.Length == 0)
            return;

        try
        {
            File.WriteAllLines(RatesFile,
            [
                "Divine Orb\t" + divine.ToString("R", CultureInfo.InvariantCulture),
                "Chaos Orb\t" + chaos.ToString("R", CultureInfo.InvariantCulture),
                "Vaal Orb\t" + vaal.ToString("R", CultureInfo.InvariantCulture),
            ]);

            _ratesStored = (divine, chaos, vaal);
            _ratesStoredAt = DateTime.Now;
        }
        catch (Exception)
        {
            // Tried again when the rates next change.
        }
    }

    /// <summary>Where the last prices read are stored. See StoredPrices.</summary>
    private static string PricesFile => Path.Combine(Kept.Home, "last_prices.tsv");

    /// <summary>
    /// Where they were stored when they were written only for the freeze, moved to PricesFile the first time it is
    /// read. See StoredPrices.
    /// </summary>
    private static string FrozenPricesFile => Path.Combine(Kept.Home, "frozen_prices.tsv");

    /// <summary>What the stored prices last did, for the dump. See StoredPrices.</summary>
    public static string PricesFileSaid { get; private set; } = "not read yet";

    /// <summary>
    /// Writes the prices read to PricesFile when they differ from what is stored there, so the last prices read
    /// outlive the plugin: they stand in for live ones while NinjaPricer is still loading, and the freeze holds them.
    /// Not while the freeze is on and something is stored. See BuildPrices.
    ///
    /// **The freeze was held in a field and the field is rebuilt on every reload.** Valuation is
    /// constructed once in Initialise, so a reset could not shake it loose - but reloading the
    /// plugin, which is what you do between two builds you are trying to compare, runs Initialise
    /// again and starts it at null. The next build then took whatever NinjaPricer says NOW and froze
    /// that, silently, with the box still ticked.
    ///
    /// It cost a verdict. Two runs meant to differ by one search switch were taken either side of a
    /// reload: 246 recipes priced against 247, the site's ceiling down 403, and the whole 390 point
    /// difference read as the switch. It was the prices.
    ///
    /// Written by index into the recipe list rather than by name, because a recipe has no stable
    /// identifier this side of the game files and the list is the same list every load. The count is
    /// written with it so a game patch that changes the list is noticed rather than silently
    /// mismatched.
    /// </summary>
    private void StorePrices(Dictionary<Expedition2Recipe, double> prices,
        List<Expedition2Recipe> recipes)
    {
        if (recipes == null || recipes.Count == 0 || Kept.Home.Length == 0)
            return;

        // Unchanged since last written: nothing to do. Most builds, since the market moves far more slowly than every
        // five seconds.
        if (_stored != null && _stored.Count == recipes.Count &&
            recipes.All(r => prices.TryGetValue(r, out var now) && _stored.TryGetValue(r, out var was) && now == was))
            return;

        // **Never under the freeze when something is stored.** The caller asks this only with the freeze off, or with
        // nothing stored yet - and a file that exists but did not fit (a reload's first build landing before the config
        // folder is known reads nothing) is checked again here, so a failed read does not become a fresh snapshot
        // under a ticked box. See DebugSettings.FreezePrices.
        if (Safe.Read(() => _settings.Debug.FreezePrices.Value, false) && File.Exists(PricesFile))
            return;

        try
        {
            var lines = new List<string> { recipes.Count.ToString(CultureInfo.InvariantCulture) };

            for (var i = 0; i < recipes.Count; i++)
            {
                lines.Add(prices.TryGetValue(recipes[i], out var worth)
                    ? worth.ToString("R", CultureInfo.InvariantCulture)
                    : "0");
            }

            File.WriteAllLines(PricesFile, lines);

            _stored = new Dictionary<Expedition2Recipe, double>(prices);
            PricesFileSaid = $"stored {recipes.Count:N0} recipes' prices, {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            PricesFileSaid = $"could not store the prices: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads the stored prices back, or null when there are none that fit this game build. See StorePrices.
    /// </summary>
    private Dictionary<Expedition2Recipe, double> StoredPrices()
    {
        var recipes = Safe.Read(() => _gc.Files.Expedition2Recipes.EntriesList, null);

        // **Say which of these it was.** Silence here reads in the dump as "not frozen" beside a
        // setting that is plainly on, which is the state this was reported in: the freeze looked
        // ignored when it had in fact given up before it started. The home is set by the plugin as
        // it loads, so the first build of a reload can arrive before it.
        if (recipes == null || recipes.Count == 0)
        {
            PricesFileSaid = "the game's recipe list has not loaded yet - no stored prices";

            return null;
        }

        if (Kept.Home.Length == 0)
        {
            PricesFileSaid = "no config folder yet, so the stored prices could not be read";

            return null;
        }

        // The file the freeze wrote, moved to its new name once. See FrozenPricesFile.
        try
        {
            if (!File.Exists(PricesFile) && File.Exists(FrozenPricesFile))
                File.Move(FrozenPricesFile, PricesFile);
        }
        catch (Exception ex)
        {
            PricesFileSaid = $"could not move {Path.GetFileName(FrozenPricesFile)}: {ex.Message}";
        }

        if (!File.Exists(PricesFile))
        {
            PricesFileSaid = "no prices stored yet - the first prices read will be";

            return null;
        }

        try
        {
            var lines = File.ReadAllLines(PricesFile);

            // The first line is how many recipes the game had when this was written. A different
            // number means a different game build and the indices no longer line up, so the file is
            // worth nothing - and using it would be worse than not freezing, because every price
            // would be attached to the wrong reward.
            if (lines.Length != recipes.Count + 1 ||
                !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var many) || many != recipes.Count)
            {
                PricesFileSaid = "the stored prices are from a different game build - ignored";

                return null;
            }

            var held = new Dictionary<Expedition2Recipe, double>();

            for (var i = 0; i < recipes.Count; i++)
            {
                held[recipes[i]] = double.TryParse(lines[i + 1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var worth)
                    ? worth
                    : 0d;
            }

            PricesFileSaid = $"read {recipes.Count:N0} recipes' prices from disk";

            return held;
        }
        catch (Exception ex)
        {
            PricesFileSaid = $"could not read the stored prices: {ex.Message}";

            return null;
        }
    }

    /// <summary>
    /// The best this remnant can still become, or null when it cannot be read.
    ///
    /// The best rather than the average, because a remnant is a choice: you are shown the options
    /// and take one, so what it is worth walking to is the ceiling rather than the mean of things
    /// you would not pick.
    /// </summary>
    public Reward Best(Entity entity) => Top(entity, 1).FirstOrDefault();

    /// <summary>
    /// The most valuable outcomes this remnant can still reach, best first.
    ///
    /// More than one because the top line alone can mislead: a remnant whose best is a Divine Orb
    /// and whose next four are worth nothing is a different proposition from one where all five are
    /// close, and only the first is really a gamble.
    ///
    /// **Kept per recipe, where they used to be collapsed per reward name.** The note that stood
    /// here said three routes to the same orb are one outcome, not three - true of the orb and
    /// false of everything else a route carries. Seen on a live remnant: "Skill Level 20: Leylines"
    /// uses six slots and propagates Rebirth AND Life, "Skill: Leylines" uses four and propagates
    /// Rebirth alone, and both are named "Leylines". Collapsing them kept whichever priced higher -
    /// and on equal prices whichever came first, since the test is a strict greater-than - so the
    /// planner never saw the six slot version and could not choose it. The same dump had two rows
    /// both named "Skyfall".
    ///
    /// A name is not an identity. The recipe is, and it rides along on the Reward now so that
    /// nothing downstream has to join on what a combination yields.
    /// </summary>
    public List<Reward> Top(Entity entity, int keep)
    {
        var found = new List<Reward>();
        var data = Data(entity);

        if (data == null || keep <= 0)
            return found;

        var best = new Dictionary<string, Reward>(StringComparer.Ordinal);

        // Which slots carry forward, read once for the whole list rather than per recipe.
        var passing = Passing(entity);

        foreach (var (recipe, value) in Reachable(data, entity))
        {
            var name = Name(recipe);

            // A recipe Name could not read anything out of. It has no business in a reward list:
            // shown, it is a question mark on the ground; chosen, it is a chain built around
            // nothing. See Name, which returns "?" only when the reward, the description and the
            // id were all empty.
            if (string.IsNullOrWhiteSpace(name) || name == Unnamed)
                continue;

            var needs = Safe.Read(() => recipe.RuneCountRequired, 0);
            var carries = Propagation.Carried(recipe, passing);

            // Its own id, falling back to the name only where the game states none - two recipes
            // with no id and one name are indistinguishable to anything, and collapsing them is
            // then the honest answer rather than a choice.
            var id = Safe.Read(() => recipe.Id, null);

            if (string.IsNullOrWhiteSpace(id))
                id = name;

            if (!best.TryGetValue(id, out var already) || value > already.Value)
            {
                var local = Propagation.Locally(recipe, passing);

                best[id] = new Reward(name, value, needs, carries,
                    Propagation.Ids(recipe, passing), local.Total, local.Runes, id, Propagation.HeldRunes(recipe),
                    Propagation.WaveSharesOfIds(recipe, passing), Propagation.HeldRuneWaveShares(recipe),
                    Propagation.SlotRunes(recipe));
            }
        }

        // Price first, then the preference list. The second half matters only when the first
        // cannot decide - which is exactly the unpriceable window, where every option is worth
        // zero and the order would otherwise be however the dictionary happened to enumerate.
        // That was showing the amulet as "best" on the ground while the window bordered the belt:
        // two answers to one question, from two places that each had half the rule.
        return best.Values
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(keep)
            .ToList();
    }

    /// <summary>
    /// Which rune slots this remnant passes on, and what could be sitting in them.
    ///
    /// The positions are stated by the game. The runes are not: a position only says "whatever ends
    /// up in slot two carries forward", and what ends up there is decided by the combination picked
    /// at the remnant. So the candidates are the runes the reachable combinations put in that slot,
    /// and a slot with one candidate is one where the choice is already made - which is what the
    /// slot holding the fixed rune always is.
    ///
    /// This is the thing pricing cannot see and the reason to visit an early remnant at all: its own
    /// reward is spent once, where a rune it passes on applies to every remnant left in the chain.
    /// </summary>
    public List<Passes> Passing(Entity entity)
    {
        var found = new List<Passes>();
        var data = Data(entity);

        // **Null means "could not read", empty means "passes nothing", and they are not the same.**
        // Both used to come back as an empty list, and Scan caches this with ??= - so a read that
        // caught the encounter data mid-flight was stored as a remnant that propagates nothing, and
        // never asked again for the life of the site. Every propagation term built on it then agreed
        // that the remnant carried nothing forward. The same half-read that gave nine remnants one
        // Divine Orb between them. See Scan's re-pricing.
        if (data == null)
            return null;

        var positions = Safe.Read(() => data.PassedOnRunePositions, null);

        if (positions == null)
            return null;

        if (positions.Count == 0)
            return found;

        var fixedAt = Safe.Read(() => data.FixedRunePosition, -1);
        var fixedRune = Safe.Read(() => data.FixedRune?.Id, null);

        // Read once for all the slots. Walking three hundred recipes per propagating slot would do
        // the same work twice for the remnants that pass on two.
        //
        // **Everything, not Reachable: the pin must not reach this.** A must take remnant has its
        // option list collapsed to the single richest reward, and running the propagating slots
        // through that answers "what will this remnant propagate given the reward somebody insisted
        // on" - where the question here is "what CAN it propagate". The difference is not academic:
        // Scan caches this list on first sight, so a remnant that streamed in after it was marked
        // kept the collapsed answer for the life of the site, and every readout built on it agreed.
        //
        // It is what made the reroll advice claim "only source of Volcanic" beside a must take
        // remnant plainly carrying Volcanic - that remnant's candidates had been narrowed to one
        // combination, so as far as the search could see it carried nothing else.
        //
        // The plan is still pinned where pinning belongs, in Weighing.Choices. See Everything.
        var reachable = Everything(data, entity);

        foreach (var slot in positions)
        {
            if (slot < 0)
                continue;

            var runes = new List<string>();

            // The slot holding the rune already in the ground needs no search: every reachable
            // combination has that rune there, because matching it is how they were filtered.
            if (slot == fixedAt && !string.IsNullOrWhiteSpace(fixedRune))
            {
                runes.Add(fixedRune);
            }
            else
            {
                foreach (var (recipe, _) in reachable)
                {
                    var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(slot)?.Id, null);

                    if (!string.IsNullOrWhiteSpace(id) && !runes.Contains(id))
                        runes.Add(id);
                }
            }

            if (runes.Count > 0)
                found.Add(new Passes(slot, runes));
        }

        return found;
    }

    /// <summary>
    /// What the CLIENT says about one remnant's slots, recipes and runes, for reading by eye.
    ///
    /// Everything the planner's propagation is built on comes from three reads on the encounter
    /// data - which slots pass a rune on, which rune is already in the ground and where, and which
    /// recipes are still reachable - and none of them reached a readout. So every disagreement
    /// between what the game shows and what the plugin scored had to be chased through four layers
    /// of derived arrays to find out which of the three was being read wrongly. This prints the
    /// three directly.
    ///
    /// **Slot numbering is the client's, which is zero based.** The rune weights table in the dump
    /// numbers from one, and the two sit a few hundred lines apart; the offset is stated on each
    /// line rather than left for the reader to remember.
    ///
    /// **A slot is propagating only if PassedOnRunePositions names it.** That is the game's own
    /// answer to local-versus-propagating, and Passing returns entries for those slots alone - so a
    /// slot absent from the propagating list is local by definition, not by inference.
    ///
    /// Reads only, and nothing scores by it.
    /// </summary>
    public string SlotsAndRecipesOfRemnant(Entity entity)
    {
        var data = Data(entity);

        if (data == null)
            return "encounter data unreadable";

        var text = new StringBuilder();
        var sockets = Safe.Read(() => data.RuneCount, 0);
        var fixedAt = Safe.Read(() => data.FixedRunePosition, -1);
        var fixedRune = Safe.Read(() => data.FixedRune?.Id, null);
        var passing = Safe.Read(() => data.PassedOnRunePositions, null);
        var level = Safe.Read(() => _gc.IngameState.Data.CurrentAreaLevel, 0);

        text.Append($"{sockets} sockets, area level {level}, fixed rune ")
            .Append(string.IsNullOrWhiteSpace(fixedRune) ? "none" : fixedRune)
            .Append(fixedAt >= 0 ? $" at client slot {fixedAt} (weights-table slot {fixedAt + 1})" : " at no slot")
            .AppendLine();

        text.Append("      propagating slots (PassedOnRunePositions): ")
            .AppendLine(passing is { Count: > 0 }
                ? string.Join(", ", passing) + "  - every other slot is local"
                : passing == null ? "UNREADABLE" : "none - this remnant passes nothing on");

        var reachable = Everything(data, entity);

        text.Append("      ").AppendLine(FilterAccounting ?? "no filter accounting");
        text.Append("      ").Append(reachable.Count)
            .AppendLine(" reachable recipes, each with the rune it puts in every slot:");

        foreach (var (recipe, worth) in reachable.OrderByDescending(r => r.Value))
        {
            var id = Safe.Read(() => recipe.Id, null) ?? "(unreadable)";
            var needs = Safe.Read(() => recipe.RuneCountRequired, 0);
            var runes = new List<string>();

            for (var slot = 0; slot < sockets; slot++)
            {
                var at = slot;
                var rune = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                runes.Add($"{at}={rune ?? "-"}" +
                          (passing != null && passing.Contains(at) ? "*" : ""));
            }

            text.Append("        ").Append(id).Append(" [").Append(Name(recipe) ?? "?").Append("] ")
                .Append(needs).Append(" runes, worth ").Append(worth.ToString("0.#", CultureInfo.InvariantCulture))
                .Append("  ").AppendLine(string.Join(" ", runes));
        }

        // The same list Passing hands the planner, so a fault can be placed on one side of it.
        text.AppendLine("      what Passing derives from the above, per propagating slot:");

        foreach (var slot in Passing(entity) ?? [])
            text.Append("        slot ").Append(slot.Slot).Append(": ")
                .Append(slot.Runes.Count).Append(slot.Settled ? " (settled) " : " candidates ")
                .AppendLine(string.Join(", ", slot.Runes));

        return text.ToString();
    }

    /// <summary>
    /// Every recipe this remnant can still become, with what it is worth.
    ///
    /// The filter is the game's own, worked out from the tables: the socket count has to fit, the
    /// count has to be one the fixed rune appears with at this area level, the area level has to sit
    /// inside the recipe's window, and the rune already in the ground has to be the one the recipe
    /// wants in that slot.
    ///
    /// Shared between pricing and propagation because they are two questions about the same list,
    /// and a list built twice by two copies of one filter is a list that will eventually disagree
    /// with itself.
    /// </summary>
    /// <param name="entity">
    /// The remnant itself, for the one thing its data does not say: whether it has been rolled.
    /// </param>
    private List<KeyValuePair<Expedition2Recipe, double>> Reachable(Expedition2EncounterData data,
        Entity entity) =>
        Pinned(Everything(data, entity), entity, data);

    /// <summary>
    /// The same list before a must take collapses it. What the remnant IS, rather than what to click.
    ///
    /// **Statistics must not see the pin.** Pinning a must-take remnant to its single best reward is
    /// a decision about the click, and three readouts agreeing on that is why it exists - but the
    /// census and the reroll averages are asking what this remnant could have been, and a pinned
    /// list answers "one option" to that however many it really had. A remnant recorded as offering
    /// one thing is a row that quietly says the game generates far less variety than it does.
    /// </summary>
    private List<KeyValuePair<Expedition2Recipe, double>> Everything(Expedition2EncounterData data,
        Entity entity)
    {
        var found = new List<KeyValuePair<Expedition2Recipe, double>>();

        // A chosen combination narrows the list to one thing, but only when the player has said it
        // should. The choice stays open until the chain is detonated, so treating it as final is a
        // preference about whose decision wins rather than a fact about the game - which is why it
        // is a setting and why the default is the other way.
        // **A rolled remnant is not a preference, it is a fact, and it was being treated as the
        // first.** Liquid Verisium fixes the combination for good: the remnant is rerolled, what it
        // landed on is what it pays, and nothing can move it. Read as open, the whole option list
        // came back - so the overlay wrote "can propagate opulent" under a remnant with no opulent
        // in it, and named a combination it cannot reach as the pick. Neither was a bad guess; both
        // were answers to a question that is no longer being asked.
        //
        // **Overruling is NOT collapsed here, and that is the difference between the two.** Turning
        // it off means the plan routes around a decision already made - it does not mean the plan
        // stops having an opinion, and the opinion is worth seeing. Collapsing the list here threw
        // it away before anything could show it, which left the player's own pick looking like the
        // planner's. The honouring lives where it belongs instead: the planner is pinned to the
        // chosen combination in Weighing.Choices, while the list stays whole so the overlay can draw
        // both sides.
        if (Rolled(entity) && Chosen(data) is { } settled)
        {
            found.Add(new KeyValuePair<Expedition2Recipe, double>(settled, Value(settled)));

            // Said here too, because the filter below does not run: left alone, the dump printed the
            // accounting of whichever remnant was filtered last under this one.
            FilterAccounting = $"rolled, so fixed to {Safe.Read(() => settled.Id, "?")} - the recipe filter is not run";

            return found;
        }

        var level = Safe.Read(() => _gc.IngameState.Data.CurrentAreaLevel, 0);
        var sockets = Safe.Read(() => data.RuneCount, 0);
        var fixedRune = Safe.Read(() => data.FixedRune, null);
        var fixedAt = Safe.Read(() => data.FixedRunePosition, -1);

        if (sockets <= 0)
            return found;

        return RecipesFittingRemnant(sockets, fixedRune, fixedAt, level);
    }

    /// <summary>
    /// The highest area level a rune weights row still applies at, read from the row's bytes.
    ///
    /// **The row states a RANGE and ExileCore2 exposes only its start.** Expedition2RunesWeight
    /// carries Id, SlotCount, RuneSlot, Rune and Level; the row is 57 bytes and those five fill
    /// about half of it. The four bytes at offset 36, straight after Level, are the other end of
    /// the range, and nothing was reading them - so every filter in this plugin, and the same
    /// filter in the two other plugins that read this table, admitted rows that expired levels ago.
    ///
    /// **Established rather than assumed.** Over all 175 rows the value is never below Level, and
    /// the eleven runes that hold two rows for one (slot, rune count) tile the range exactly:
    /// 44-77 then 78-100, 17-74 then 75-100, 15-74 then 75-100. Eleven of eleven contiguous, no
    /// gaps and no overlaps, which a magnitude or a weight would have no reason to do.
    ///
    /// Measured at area level 79: of 172 rows the old test admitted, 113 were really in range. The
    /// 59 it let through are why Protective was offered as a possible fixed rune there when its
    /// brackets end at 30 and 40 - and why NOTES 8z recorded it as never observed among 1,180 fixed
    /// runes. That was the filter being wrong, not the observation.
    ///
    /// **The offset is patch-fragile and is re-derivable.** Match SlotCount, RuneSlot and Level
    /// into a row's raw bytes to find the layout again: eight bytes of string reference, those two
    /// ints, the rune's key, a pointer identical on every row, Level, then this. The dump prints
    /// the whole table for exactly that purpose.
    /// </summary>
    private const int MaxLevelOffset = 36;

    private static int MaxLevelOf(Expedition2RunesWeight row)
    {
        var raw = Safe.Read(() => row.M.ReadBytes(row.Address, MaxLevelOffset + 4), null);

        // No reading is not a reason to drop a row: without the upper bound this behaves exactly as
        // it did before the bound was found, which is wrong in the admitting direction rather than
        // the refusing one. A refusing default would empty a remnant's option list on a bad read.
        return raw is { Length: >= MaxLevelOffset + 4 }
            ? BitConverter.ToInt32(raw, MaxLevelOffset)
            : int.MaxValue;
    }

    /// <summary>
    /// Every rune a roll could leave fixed on a remnant of this many sockets, and the slot it would
    /// sit in.
    ///
    /// **The support a roll draws from.** A roll randomises everything, so what it can produce is
    /// not narrowed by the remnant standing there now - it is whatever the game's own table allows
    /// at this socket count and area level. SlotCount on that table is the RECIPE's rune count, not
    /// the remnant's socket count, and a recipe may want fewer runes than the remnant has sockets -
    /// so the bound is `SlotCount &lt;= sockets` rather than equality. Reading it as equality is the
    /// mistake that made a two-rune recipe look impossible on a four-socket remnant.
    ///
    /// Slots come back zero based, as the client numbers them; the table numbers from one.
    ///
    /// **Support only - the table states no odds.** Expedition2RunesWeight carries Id, SlotCount,
    /// RuneSlot, Rune and Level and nothing else, despite the name, and neither of the other two
    /// plugins reading it finds a weight either. How LIKELY each of these is has to come from
    /// somewhere else; see Rolls.Runes, whose shares are chosen rather than measured.
    /// </summary>
    public List<(Expedition2Rune Rune, int Slot)> FixedRunesPossibleAt(int sockets)
    {
        var found = new List<(Expedition2Rune Rune, int Slot)>();

        if (sockets <= 0)
            return found;

        var level = Safe.Read(() => _gc.IngameState.Data.CurrentAreaLevel, 0);
        var rows = Safe.Read(() => _gc.Files.Expedition2RunesWeights.EntriesList, null);

        if (rows == null)
            return found;

        var seen = new HashSet<(string, int)>();

        foreach (var row in rows)
        {
            var count = Safe.Read(() => row.SlotCount, -1);
            var slot = Safe.Read(() => row.RuneSlot, -1) - 1;
            var rune = Safe.Read(() => row.Rune, null);
            var from = Safe.Read(() => row.Level, int.MaxValue);

            // **The arrangement's rune count must EQUAL the socket count, not fit inside it.**
            //
            // This read `count > sockets`, which admits a rune pinnable in a two rune arrangement on
            // a six socket remnant. Measured against 787 fresh readings in remnants.csv: at four
            // sockets 5 of 23 admitted pairs never appeared, at five 17 of 41, at six 31 of 56 - and
            // every one of those 53 had been seen at a SMALLER socket count. Not one was absent
            // everywhere, which is what a rare-but-possible pair would look like.
            //
            // So the pin comes from an arrangement of exactly this many runes. What the remnant may
            // then be turned INTO is a separate and looser question - a six socket remnant reaching
            // a five rune recipe is confirmed, 25 readings of reachable against offered - so
            // RecipesFittingRemnant keeps its own rule and this one tightens alone.
            if (count != sockets || slot < 0 || slot >= sockets ||
                rune == null || from > level || MaxLevelOf(row) < level)
                continue;

            if (seen.Add((Safe.Read(() => rune.Id, "") ?? "", slot)))
                found.Add((rune, slot));
        }

        return found;
    }

    /// <summary>
    /// The recipes a rolled remnant of this shape could still be turned into, with their prices.
    ///
    /// The same filter every real remnant goes through - see RecipesFittingRemnant - asked about a
    /// shape rather than about something on the ground. The area level is read here so a caller
    /// enumerating hundreds of shapes does not have to carry it.
    /// </summary>
    public List<KeyValuePair<Expedition2Recipe, double>> RecipesForShape(
        int sockets, Expedition2Rune fixedRune, int fixedSlot) =>
        RecipesFittingRemnant(sockets, fixedRune, fixedSlot,
            Safe.Read(() => _gc.IngameState.Data.CurrentAreaLevel, 0));

    /// <summary>
    /// Every recipe a remnant of this shape can produce, with what each is worth.
    ///
    /// **Takes the shape rather than the remnant, so a remnant that does not exist can be asked
    /// about.** A roll randomises everything - socket count, which rune is fixed and where - so
    /// pricing one means asking this of shapes no remnant on the ground has. Everything above reads
    /// those three off the encounter data and hands them here; the reroll advisor enumerates them.
    ///
    /// The filter is the game's own, worked out from its tables: the recipe cannot want more runes
    /// than the remnant has sockets, its rune count has to be one the fixed rune is ever found with
    /// in that slot at this level, the area level has to sit inside the recipe's window, and the
    /// fixed rune has to be the one the recipe wants in that slot.
    /// </summary>
    /// <param name="fixedRune">Null for a remnant with nothing in the ground - see the note below.</param>
    private List<KeyValuePair<Expedition2Recipe, double>> RecipesFittingRemnant(
        int sockets, Expedition2Rune fixedRune, int fixedAt, int level)
    {
        var found = new List<KeyValuePair<Expedition2Recipe, double>>();

        // **A remnant does not have to have a rune in the ground.** Every one seen until now did,
        // so the filter was written around it: the socket counts came from the weights table keyed
        // on that rune and that slot, and a recipe had to want that rune in that slot to survive.
        //
        // The Heath site is built the other way round. Its remnant starts empty and the runic
        // henges add the markers - "Blow up Runic Henges to add Markers to this Remnant" - so after
        // the chain goes off it reads eight sockets with FixedRunePosition -1 and a rune whose id
        // is blank. Both constraints then matched nothing, the reachable list came back empty, and
        // an eight-socket remnant showed no reward at all: three hundred and eight recipes fit its
        // sockets and zero survived a filter keyed on a rune that is not there.
        //
        // So the two fixed-rune constraints are skipped when there is no fixed rune, which leaves
        // the ones that are still facts about the remnant: the recipe cannot want more runes than
        // it has sockets, and the area level has to sit inside the recipe's window.
        var pinned = fixedAt >= 0 && fixedRune != null &&
                     !string.IsNullOrWhiteSpace(Safe.Read(() => fixedRune.Id, null));

        // Which socket counts this fixed rune can appear in, at this area level, in this slot. The
        // weights table is what ties a rune to the layouts it belongs to.
        var allowed = !pinned
            ? null
            : Safe.Read(() => _gc.Files.Expedition2RunesWeights.EntriesList, null)
                ?.Where(w => Safe.Read(() => w.RuneSlot - 1 == fixedAt, false))
                .Where(w => Safe.Read(() => w.Rune?.Equals(fixedRune) == true, false))
                .Where(w => Safe.Read(() => w.Level <= level, false))

                // The other end of the range, which nothing was reading. See MaxLevelOf.
                .Where(w => MaxLevelOf(w) >= level)
                .Select(w => Safe.Read(() => w.SlotCount, -1))
                .ToHashSet() ?? new HashSet<int>();

        // **Counted per term, because an empty option list has four possible causes and looked
        // like one.** A remnant whose list comes back empty is never priced - Scan stores nothing
        // when Top returns nothing - so it reads "rewards 0" for ever, has no choices, and has its
        // propagation priced off every rune its slots could hold. Which of these terms emptied it
        // is the whole question, and no readout carried it.
        var enumerated = 0;
        var whole = (_prices.Value?.Count ?? 0);
        var byLevel = 0;
        var byFixed = 0;
        var byLeague = 0;
        var league = Safe.Read(() => _gc.IngameState.ServerData.League, "") ?? "";

        var prices = _prices.Value ?? new Dictionary<Expedition2Recipe, double>();
        var buckets = _recipesByRuneCount.Value;

        // **Only the rune counts this remnant could take**, which is the rejection that throws out
        // most of the table. The accounting still reports what the count term cost, worked out
        // rather than counted: every recipe outside these buckets failed on it.
        var takeable = Enumerable.Range(1, Math.Max(0, sockets))
            .Where(n => allowed == null || allowed.Contains(n))
            .ToList();

        foreach (var needs in takeable)
        foreach (var recipe in buckets?[needs] ?? Enumerable.Empty<Expedition2Recipe>())
        {
            enumerated++;

            if (!prices.TryGetValue(recipe, out var value))
                value = 0d;

            if (!Safe.Read(() => recipe.MinLevelReq <= level && recipe.MaxLevelReq >= level, false))
            {
                byLevel++;

                continue;
            }

            if (WithheldInLeague(recipe, league))
            {
                byLeague++;

                continue;
            }

            // The rune already in the ground has to be the one this recipe wants in that slot.
            if (pinned &&
                !Safe.Read(() => recipe.Runes.ElementAtOrDefault(fixedAt)?.Equals(fixedRune) == true, false))
            {
                byFixed++;

                continue;
            }

            found.Add(new KeyValuePair<Expedition2Recipe, double>(recipe, value));
        }

        // **"of N in the game" rather than "N recipes", because the remnant never had N.** The old
        // wording read as though this remnant started with the whole table and was narrowed, and it
        // was taken that way. 322 is the size of the game's recipe list, shared by every remnant.
        //
        // The rune-count term is now a subtraction rather than a tally. Only the buckets a remnant
        // of this shape can take are walked at all, so nothing inside the loop fails on count - and
        // every recipe outside those buckets failed on exactly that.
        FilterAccounting = $"of {whole} recipes in the game, {found.Count} fit this remnant - " +
                 $"{whole - enumerated} ruled out by sockets/slot " +
                 $"({sockets} sockets, rune counts [{(allowed == null ? "any" : string.Join(",", allowed))}]), " +
                 $"{byLevel} by level {level}, {byLeague} withheld in league \"{league}\", {byFixed} by the fixed rune " +
                 $"({(pinned ? Safe.Read(() => fixedRune.Id, "?") + "@" + fixedAt : "none in the ground")})";

        return found;
    }

    /// <summary>
    /// Whether the game keeps this recipe's reward out of the combinations window in this league. The 0.5.5 patch notes
    /// (forum thread 4000864): "The Aldur's Saga cannot be obtained outside of the Runes of Aldur League". The recipe is
    /// still in the game's recipe table and on a remnant's own list, so a remnant read it as an option - priced at a
    /// hand-set 200,001 on 2026-10-01 - and the window then had no row for it. Matched on the reward's metadata
    /// rather than its name.
    /// </summary>
    private static bool WithheldInLeague(Expedition2Recipe recipe, string league) =>
        league.IndexOf("Aldur", StringComparison.OrdinalIgnoreCase) < 0 &&
        string.Equals(Safe.Read(() => recipe.Reward?.Metadata, "") ?? "", AldursSaga, StringComparison.OrdinalIgnoreCase);

    /// <summary>Aldur's Saga's base item. See WithheldInLeague.</summary>
    private const string AldursSaga = "Metadata/Items/Expedition/Expedition2LogbookSpecial";

    /// <summary>
    /// Why the last option list came out the size it did, term by term. See Everything.
    ///
    /// Written on every build and read by the dump, which asks for one remnant immediately before
    /// printing it - so it describes that remnant rather than whichever was priced last.
    /// </summary>
    public static string FilterAccounting { get; private set; } = "nothing asked yet";

    /// <summary>
    /// Runs the option filter for one remnant and reports what it did, storing nothing.
    ///
    /// For the dump. Pricing a remnant has side effects - Scan caches the list and what it was
    /// priced under - and a diagnostic must not have them, so this asks the question and throws the
    /// answer away, keeping only the count of what survived each term.
    /// </summary>
    public string Sieve(Entity entity)
    {
        var data = Data(entity);

        if (data == null)
            return "no encounter data - the remnant cannot be read at all";

        Everything(data, entity);

        return FilterAccounting;
    }

    /// <summary>
    /// Collapses the list to the one reward that is worth taking, when one of them is.
    ///
    /// **A reward over the take-above floor is not a choice, and three readouts were still offering
    /// it as one.** That floor is what makes the planner build the whole chain around a remnant -
    /// it is the strongest statement the settings can make about a reward - and having made it, the
    /// overlay went on naming a different combination in green because its carried rune scored
    /// better, and the propagation line went on listing runes from combinations the plan has no
    /// intention of picking.
    ///
    /// Done here rather than in each of them because they all read this list: the reward lines, the
    /// green pick, the propagating slots and the planner's own option set. Pinned in one place, they
    /// cannot disagree; pinned in four, they will.
    ///
    /// The floor being zero turns this off, which is what a threshold of zero already means
    /// everywhere else.
    /// </summary>
    private List<KeyValuePair<Expedition2Recipe, double>> Pinned(
        List<KeyValuePair<Expedition2Recipe, double>> found, Entity entity, Expedition2EncounterData data = null)
    {
        // **Not once the player has chosen a reward on it.** A must take with a reward the player chose is fixed to that
        // reward (see Planning.Pinned), and the whole list is kept so the chosen one is in it and a richer one can be
        // shown beside it. One the placement run set is not the player's. See ChosenByPlayer.
        if (data != null && Safe.Read(() => ChosenByPlayer(entity), null) != null)
            return found;

        // **Asked of the marker, not of the threshold.** Collapsing the list is the consequence of a
        // remnant being must take, so it has to follow the marker: un-mark one and its other
        // combinations come back, which is the whole reason somebody would un-mark it. Reading the
        // threshold here instead made the collapse outlive the decision that caused it.
        //
        // **And only a must take the threshold earned.** A must take set by hand says "take this remnant", not "take its
        // richest reward": asked with Wants, every remnant marked by hand lost all but one combination, and a remnant
        // offering five on a Grazed Prairie site (2026-10-06) was planned on Uhtred's Saga alone, its Power-bearing 10x
        // Chaos Orb gone. See Insisted.MarkedForRewardValue.
        if (found.Count <= 1 ||
            !Insisted.Here.MarkedForRewardValue(Safe.Read(() => entity.GridPos, System.Numerics.Vector2.Zero)))
            return found;

        var best = -1;

        for (var i = 0; i < found.Count; i++)
        {
            if (best < 0 || found[i].Value > found[best].Value)
                best = i;
        }

        if (best < 0)
            return found;

        return new List<KeyValuePair<Expedition2Recipe, double>> { found[best] };
    }

    /// <summary>
    /// The shape of what this remnant could be: the best of its options, the average, and how many
    /// there are.
    ///
    /// The input to any judgement about rerolling. What a reroll is worth depends on what the
    /// alternatives are worth, and that is this - the spread of the reachable recipes rather than
    /// the single number the display shows.
    ///
    /// **Unweighted, because the game does not tell us the weights.** Expedition2RunesWeight is
    /// named for them and carries none: ExileCore2 exposes the slot, the socket count, the rune and
    /// the level, and no probability. So the average here is the average over the possibilities,
    /// which is the average outcome only if they are equally likely - and the table being called a
    /// weights table is fair warning that they are not. Treat the number as an indication rather
    /// than an expectation.
    ///
    /// Duplicated reward names are NOT collapsed here, unlike in Top: two routes to the same orb
    /// are two chances of that orb, and for an average that is the point.
    /// </summary>
    public (double Best, double Mean, int Options) Spread(Entity entity)
    {
        var data = Data(entity);

        if (data == null)
            return (0d, 0d, 0);

        var best = 0d;
        var total = 0d;
        var count = 0;

        // Everything rather than Reachable: this is the shape of what the remnant could be, and a
        // must take pin is a statement about what to click. See Everything.
        foreach (var (_, value) in Everything(data, entity))
        {
            best = Math.Max(best, value);
            total += value;
            count++;
        }

        return (best, count > 0 ? total / count : 0d, count);
    }

    /// <summary>The chosen combination when it is to be treated as final, or null.</summary>
    /// <summary>
    /// Whether Liquid Verisium has been used on this remnant, which fixes its combination for good.
    ///
    /// The same state Target.Rerolled reads. Here as well because the valuation is handed entities
    /// rather than targets, and this is the one thing about a remnant that changes what its options
    /// ARE rather than what they are worth.
    /// </summary>
    private static bool Rolled(Entity entity)
    {
        if (entity == null)
            return false;

        var states = Safe.Read(() => entity.GetComponent<StateMachine>()?.States, null);

        if (states == null)
            return false;

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i]?.Name == "is_rerolled")
                return Safe.Read(states[i], static x => x.Value, 0L) > 0L;
        }

        return false;
    }

    private Expedition2Recipe Settled(Expedition2EncounterData data) =>
        Safe.Read(() => _settings.Rewards.Overrule.Value, true) ? null : Chosen(data);

    /// <summary>Whether this remnant's options have collapsed to the one it is set to.</summary>
    public bool Settled(Entity entity)
    {
        var data = Data(entity);

        return data != null && Settled(data) != null;
    }

    /// <summary>
    /// The combination this remnant is currently set to, or null when nothing has been picked yet.
    ///
    /// Currently, not finally: the choice can be changed any time before the remnant is detonated,
    /// so this is what it would give as things stand rather than what it is committed to. Nothing
    /// that decides where to route may read it - see the note in Reachable.
    ///
    /// An unset pointer reads as an object at address zero rather than as a null reference, so the
    /// address is the test - and the name is checked too, because a recipe with no name is one this
    /// read did not really succeed at and treating it as the answer would silently reduce a
    /// remnant's whole option list to nothing.
    /// </summary>
    public Expedition2Recipe Chosen(Expedition2EncounterData data) => ChosenRecipe(data);

    /// <summary>
    /// Whether nothing has been picked on this remnant yet, read straight off the encounter data.
    ///
    /// Static because it needs nothing but the entity, for a caller that has no Valuation to ask. False when the
    /// data cannot be read, so an unreadable remnant is never taken for one waiting on a choice. See
    /// Options.Offering, where a window opened by a blast is matched to the remnant still waiting.
    /// </summary>
    public static bool NothingChosen(Entity entity) =>
        Data(entity) is { } data && ChosenRecipe(data) == null;

    /// <summary>The body of Chosen, which reads no state of its own. See Chosen.</summary>
    private static Expedition2Recipe ChosenRecipe(Expedition2EncounterData data)
    {
        var recipe = Safe.Read(() => data.SelectedRecipe, null);

        if (recipe == null || Safe.Read(() => recipe.Address, 0L) == 0L)
            return null;

        var name = Name(recipe);

        // **"?" is not a name, it is Name giving up**, and it reached the screen as a red line
        // saying the remnant was set to "?" when nothing had been picked at all. Name falls back
        // through the reward, the description and the id before it gets there, so a question mark
        // means every one of those was empty - which is what an unset pointer that still passed the
        // address test looks like.
        return string.IsNullOrWhiteSpace(name) || name == Unnamed ? null : recipe;
    }

    /// <summary>The combination this remnant has been set to, by name, or null while it is open.</summary>
    public string ChosenName(Entity entity)
    {
        var data = Data(entity);

        return data == null ? null : Name(Chosen(data));
    }

    /// <summary>
    /// What identifies the combination this remnant is set to, rather than what it yields.
    ///
    /// **A rolled remnant is fixed in place**: the roll randomises the combination and there is no
    /// choosing afterwards, so whatever this names IS the remnant. Pinning it by reward name puts
    /// two different combinations within reach of one string - "Skill Level 20: Leylines" and
    /// "Skill: Leylines" are both named "Leylines" - and the wrong one cannot be recovered from,
    /// because there is no alternative for the search to fall back on.
    /// </summary>
    /// <summary>
    /// The recipe set on this remnant when the player set it, or null: nothing set, or set by the placement run. See
    /// Placement.SetByPlacement.
    /// </summary>
    public string ChosenByPlayer(Entity entity)
    {
        var recipe = ChosenRecipeId(entity);

        if (string.IsNullOrEmpty(recipe))
            return null;

        var grid = Safe.Read(entity, static e => e.GridPos, System.Numerics.Vector2.Zero);

        return Placement.SetByPlacement(grid, recipe) ? null : recipe;
    }

    public string ChosenRecipeId(Entity entity)
    {
        var data = Data(entity);

        return data == null ? null : Safe.Read(() => Chosen(data)?.Id, null);
    }

    /// <summary>
    /// Whether this remnant is already set to that exact combination.
    ///
    /// **By recipe, because a name can cover two of them.** One remnant seen in a dump offered two
    /// combinations called "Refutation", two called "Leylines" and two called "Animus Exchange" -
    /// different rune counts and different propagation behind one string each. Every comparison
    /// that asked "is the right thing already chosen" by name could answer yes about the wrong one,
    /// and the callers act on that: the automation skips a remnant it should rewrite, walks past a
    /// window it should open, and reports a click as having taken when it set something else.
    ///
    /// The name is the fallback for a recipe the game states no id for - see Valuation.Top, which
    /// files a reward under its name in exactly that case, so the two really are one combination
    /// there rather than two being confused.
    /// </summary>
    public bool AlreadySetTo(Entity entity, Reward reward)
    {
        if (reward == null)
            return false;

        var recipe = ChosenRecipeId(entity);

        if (!string.IsNullOrWhiteSpace(recipe) && reward.Recipe.Length > 0)
            return string.Equals(reward.Recipe, recipe, StringComparison.Ordinal);

        var name = ChosenName(entity);

        return !string.IsNullOrWhiteSpace(name) &&
               string.Equals(reward.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How many of the remnant's sockets the chosen combination actually uses, or zero.
    ///
    /// Not the same as the socket count: a four socket remnant set to a three rune combination is a
    /// different thing from a three socket one, and if waves scale with runes rather than with
    /// sockets that difference is the whole answer.
    /// </summary>
    public int ChosenRunes(Entity entity)
    {
        var data = Data(entity);
        var recipe = data == null ? null : Chosen(data);

        return recipe == null ? 0 : Safe.Read(() => recipe.RuneCountRequired, 0);
    }

    /// <summary>
    /// The chosen combination's runes in slot order, space separated, a propagating slot marked with a trailing
    /// *, or empty when nothing is chosen or it cannot be read. For the spawn census, which asks whether
    /// particular runes - Oath summoning allies, Time respawning the slain - change how many monsters come.
    /// </summary>
    public string ChosenRuneNames(Entity entity)
    {
        var data = Data(entity);
        var recipe = data == null ? null : Chosen(data);

        if (recipe == null)
            return "";

        var passing = Safe.Read(() => data.PassedOnRunePositions, null);
        var count = Safe.Read(() => recipe.Runes?.Count ?? 0, 0);
        var names = new List<string>();

        for (var slot = 0; slot < count; slot++)
        {
            var at = slot;
            var rune = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

            if (string.IsNullOrEmpty(rune))
                continue;

            names.Add(rune + (passing != null && passing.Contains(at) ? "*" : ""));
        }

        return string.Join(" ", names);
    }

    /// <summary>The rune already placed in this remnant, by id, or null when unreadable.</summary>
    public string FixedRune(Entity entity)
    {
        var data = Data(entity);

        return data == null ? null : Safe.Read(() => data.FixedRune?.Id, null);
    }

    /// <summary>Which slot that rune sits in, or -1 when unreadable.</summary>
    public int FixedSlot(Entity entity)
    {
        var data = Data(entity);

        return data == null ? -1 : Safe.Read(() => data.FixedRunePosition, -1);
    }

    /// <summary>
    /// Every reachable recipe that displays under one reward name, with what each is made of.
    ///
    /// **Because a reward list keyed on the NAME hides however many recipes share it.** The list a
    /// remnant shows is grouped by name and keeps the dearest - see the Reward build - so a name
    /// carrying two recipes shows the better one on the ground while the window offers whichever the
    /// game picked. Seen on Flotsam: Adaptive Alloy at 179.51 over the remnant, 79.20 in the window,
    /// with the other eight rewards agreeing to within a couple of exalts.
    ///
    /// Three explanations were proposed for that and all three were wrong - a stale cache, a
    /// hand-written override, and two different display names. None survived reading the code, so
    /// this prints what the recipes actually are and lets the next dump settle it: the reward item
    /// each one pays out, how many of it, and what that came to.
    /// </summary>
    public IEnumerable<(string Name, string Reward, int Count, double Value, bool Hand)> Variants(
        Entity entity, string name)
    {
        var data = Data(entity);

        if (data == null || string.IsNullOrWhiteSpace(name))
            yield break;

        foreach (var (recipe, value) in Reachable(data, entity))
        {
            if (!string.Equals(Name(recipe), name, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return (Name(recipe),
                Safe.Read(() => recipe.Reward?.BaseName, "(no reward)") ?? "(no reward)",
                Safe.Read(() => recipe.RewardCount, 0),
                value,
                Override(recipe) >= 0d);
        }
    }

    /// <summary>What one recipe is worth, in exalts. Zero for one that is not priced.</summary>
    public double Value(Expedition2Recipe recipe)
    {
        if (recipe == null)
            return 0d;

        return (_prices.Value ?? new Dictionary<Expedition2Recipe, double>())
            .TryGetValue(recipe, out var value)
            ? value
            : 0d;
    }

    /// <summary>What one of a named currency is worth in exalts, via the ordinary price lookup.</summary>
    private double Rate(Func<BaseItemType, double> value, string baseName)
    {
        if (value == null)
            return 0d;

        var item = Safe.Read(
            () => _gc.Files.BaseItemTypes.Contents.Values.FirstOrDefault(x => x.BaseName == baseName),
            null);

        return item == null ? 0d : Safe.Read(() => value(item), 0d);
    }

    /// <summary>
    /// Why a remnant priced the way it did, for the dump.
    ///
    /// Four things can each independently produce nothing - the component, the weights table, the
    /// level window, the fixed rune - and from the outside all four look identical: no number over
    /// the remnant. This says which.
    /// </summary>
    public string Describe(Entity entity)
    {
        var data = Data(entity);

        if (data == null)
            return $"no encounter data (component {EncounterDataHash:X} absent)";

        var level = Safe.Read(() => _gc.IngameState.Data.CurrentAreaLevel, 0);
        var sockets = Safe.Read(() => data.RuneCount, 0);
        var fixedRune = Safe.Read(() => data.FixedRune, null);
        var fixedAt = Safe.Read(() => data.FixedRunePosition, 0);
        var runeId = Safe.Read(() => fixedRune?.Id, null);
        var pinned = fixedAt >= 0 && !string.IsNullOrWhiteSpace(runeId);

        var weights = Safe.Read(() => _gc.Files.Expedition2RunesWeights.EntriesList, null);
        var bySlot = weights?.Count(w => Safe.Read(() => w.RuneSlot - 1 == fixedAt, false)) ?? -1;
        var byRune = weights?.Count(w => Safe.Read(() => w.RuneSlot - 1 == fixedAt, false) &&
                                         Safe.Read(() => w.Rune?.Equals(fixedRune) == true, false)) ?? -1;

        var allowed = weights
            ?.Where(w => Safe.Read(() => w.RuneSlot - 1 == fixedAt, false))
            .Where(w => fixedRune != null && Safe.Read(() => w.Rune?.Equals(fixedRune) == true, false))
            .Where(w => Safe.Read(() => w.Level <= level, false))
            .Select(w => Safe.Read(() => w.SlotCount, -1))
            .Distinct()
            .OrderBy(x => x)
            .ToList() ?? new List<int>();

        var prices = _prices.Value ?? new Dictionary<Expedition2Recipe, double>();
        var bySockets = prices.Keys.Count(r => Safe.Read(() => r.RuneCountRequired <= sockets, false));
        var byAllowed = prices.Keys.Count(r => allowed.Contains(Safe.Read(() => r.RuneCountRequired, -1)));
        var byLevel = prices.Keys.Count(r => Safe.Read(() => r.MinLevelReq <= level && r.MaxLevelReq >= level, false));
        var byFixed = prices.Keys.Count(r =>
            Safe.Read(() => r.Runes.ElementAtOrDefault(fixedAt)?.Equals(fixedRune) == true, false));

        var best = Best(entity);

        return $"sockets {sockets}, " +
               (pinned ? $"fixed rune {runeId} in slot {fixedAt}" : "no fixed rune") +
               $", area level {level}; " +
               // The two rune-keyed counts are left out when there is no rune to key on, because a
               // zero beside a filter that is not being applied reads as the cause and is not.
               (pinned
                   ? $"weights: {bySlot} in that slot, {byRune} of them this rune, " +
                     $"allowed counts [{string.Join(",", allowed)}]; "
                   : "") +
               $"recipes {prices.Count} priced, {bySockets} fit sockets, " +
               (pinned ? $"{byAllowed} fit allowed, " : "") +
               $"{byLevel} fit level" + (pinned ? $", {byFixed} match the fixed rune" : "") + "; " +
               $"priced={Priced}; best={(best == null ? "none" : $"{best.Name} at {best.Value:0.##}")}";
    }

    /// <summary>
    /// A hand-written price for this reward in exalts, or -1 when nobody has written one.
    ///
    /// Longest match wins, so a specific name beats a general one - "Unique Belt" is consulted
    /// before "Unique", and putting both in the list is how a catch-all is written without it
    /// swallowing the exceptions.
    ///
    /// Minus one rather than zero for "no entry", because zero is a price somebody might genuinely
    /// want to set: a reward they consider worthless should be able to be said to be worthless.
    /// </summary>
    /// <summary>
    /// Whether a hand-written price is what this recipe is worth, rather than the price list.
    ///
    /// For the dump, which has to be able to show a reward's fields WHILE a custom price is covering
    /// for the list - otherwise the one reward somebody worked around is the one that cannot be
    /// looked at. See Dump.RewardsWithNoPrice.
    /// </summary>
    public bool Overridden(Expedition2Recipe recipe) => Override(recipe) >= 0d;

    private double Override(Expedition2Recipe recipe)
    {
        var text = Safe.Read(() => _settings.Rewards.Overrides.Value, "") ?? "";

        if (text.Trim().Length == 0)
            return -1d;

        var name = Name(recipe) ?? "";

        if (name.Length == 0)
            return -1d;

        var best = -1d;
        var longest = 0;

        // One a line, or comma separated as they were saved before. See RewardSettings.Overrides.
        foreach (var entry in text.Split(new[] { ',', '\n' }))
        {
            var parts = entry.Split('=');

            if (parts.Length != 2)
                continue;

            var key = parts[0].Trim();

            if (key.Length <= longest || !Matches(name, key) ||
                !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var worth))
                continue;

            longest = key.Length;
            best = worth;
        }

        // Written in exalts, like every other number on the Rewards tab. It used to be converted
        // through the display unit, which made a hand price two hundred times larger the moment
        // somebody switched the readout to Divine. See RewardSettings.
        return best;
    }

    /// <summary>
    /// Whether every word of a key appears in a reward name, in any order.
    ///
    /// Word by word rather than as one string, because the names are not written the way anybody
    /// would type them. The generic unique rewards come through as "[Rarity|Unique] Belt" - the
    /// rarity is markup sitting in the middle of the phrase - so "Unique Belt" as a contiguous
    /// match finds nothing and only a bare "Unique" fires, which is how three different uniques
    /// ended up all priced at the catch-all value.
    ///
    /// Matching the words instead lets a key be written as the thing reads on screen, and still
    /// lets a more specific key win, because the ranking is on how much of it had to match.
    /// </summary>
    private static bool Matches(string name, string key)
    {
        var plain = Plain(name);

        foreach (var word in key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (plain.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A reward name with the parts that vary between otherwise identical rewards taken off.
    ///
    /// Two of them, and both make an entry that looks right match nothing:
    ///
    /// The leading count. Name() returns "3x Gemcutter's Prism", and the count is already applied
    /// separately by multiplying the price - so an entry is a price per ITEM, and leaving "3x" in
    /// the name invites one written as "3x Gemcutter" that prices the three-orb variant and
    /// silently misses the two-orb one.
    ///
    /// And the gem level. "Uncut Skill Gem (Level 19)" and "(Level 20)" are the same reward for
    /// this purpose, and without stripping it a player needs an entry per level.
    ///
    /// Deliberately NOT stripping the rarity markup that "[Rarity|Unique] Belt" carries: matching
    /// word by word already steps over it, and a substitution rule is one more thing to be wrong
    /// about the next time the game words something differently.
    /// </summary>
    private static string Plain(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var at = 0;

        // "12x " off the front, by hand - a regex here would be read once and run for every reward
        // on every price rebuild.
        while (at < name.Length && char.IsDigit(name[at]))
            at++;

        if (at > 0 && at < name.Length && (name[at] == 'x' || name[at] == 'X'))
        {
            at++;

            while (at < name.Length && name[at] == ' ')
                at++;

            name = name[at..];
        }

        var level = name.IndexOf("(Level", StringComparison.OrdinalIgnoreCase);

        return level > 0 ? name[..level].TrimEnd() : name;
    }

    /// <summary>
    /// What a recipe gives, as text. Public because the reward NAME is sometimes all there is.
    ///
    /// A recipe offering a generic unique - "Unique Belt", "Unique Ring" - has no price, because
    /// there is no such item to price: poe.ninja knows what a specific unique belt sells for and
    /// nothing about the category. So the name is the only thing left to choose on, and choosing on
    /// it needs it readable from outside.
    /// </summary>
    /// <summary>What Name returns when it could read nothing at all. Never a reward, never a pick.</summary>
    internal const string Unnamed = "?";

    public static string Name(Expedition2Recipe recipe)
    {
        // **No recipe is not an unnamed recipe, and the fallback below cannot tell them apart.**
        //
        // Every read here goes through Safe.Read, so on a null recipe each lambda throws, each is
        // swallowed, and the last one hands back its default - which is Unnamed, "?". A caller
        // asking "what is this remnant set to" then gets a question mark rather than nothing, and
        // "?" is a name as far as IsNullOrWhiteSpace is concerned.
        //
        // That is how a red "set to ?" line reached the screen for remnants nobody had chosen a
        // recipe for. Chosen already guards it - it returns null rather than a recipe whose name
        // comes out as "?" - and ChosenName undid the guard on the next line by calling this with
        // the null. Fixed here instead, because every caller that asks for a name can be handed one
        // this way and only this function knows the difference.
        //
        // "?" stays for a recipe that EXISTS and cannot be named, which is a different fact and
        // worth saying out loud.
        if (recipe == null)
            return null;

        var reward = Safe.Read(() => recipe.Reward?.BaseName, null);

        if (!string.IsNullOrWhiteSpace(reward))
        {
            var count = Safe.Read(() => recipe.RewardCount, 1);

            return count > 1 ? $"{count}x {reward}" : reward;
        }

        return Safe.Read(() => recipe.Description, null) ?? Safe.Read(() => recipe.Id, Unnamed);
    }

    /// <summary>
    /// The encounter data hanging off the remnant entity.
    ///
    /// Read from the component directly rather than through the ground label. The label route needs
    /// the label on screen, which is only true when you are stood beside the remnant - and the
    /// whole point of the readout is to tell you which remnant is worth walking to.
    /// </summary>
    /// <summary>
    /// How many rune sockets a remnant really has.
    ///
    /// **Not the "sockets" state on the entity, which lies after a roll.** Observed on a rerolled
    /// remnant: the state machine read four where the remnant had three, and on another it read four
    /// where the remnant had six - while the encounter data, which is what the recipe list is built
    /// from, was right both times. Anything that counts sockets should count them from the same
    /// place the rewards come from, or the weight and the rewards describe different remnants.
    /// </summary>
    public static int SocketsOf(Entity entity)
    {
        var data = Data(entity);

        return data == null ? 0 : Math.Max(0, Safe.Read(() => data.RuneCount, 0));
    }

    private static Expedition2EncounterData Data(Entity entity)
    {
        if (entity == null)
            return null;

        var address = Safe.Read(() => entity.HashComponents.GetValueOrDefault(EncounterDataHash), 0L);

        return address == 0
            ? null
            : Safe.Read(() => RemoteMemoryObject.GetObjectStatic<Expedition2EncounterData>(address), null);
    }
}
