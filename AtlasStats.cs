using System;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.Shared.Enums;

namespace AutoExpedition;

/// <summary>
/// Stats the allocated atlas passives grant that bear on a dig site's worth.
///
/// **Not from the map, which does not carry them.** The rune slot floor reaches the map's visible stats (see Census),
/// but "Gaining Traction" does not: on a map with it allocated, MapStatsVisible held three expedition stats and not
/// this one (2026-09-30). Read from ServerData.AtlasStats when that holds it, which needs no panel open - not yet
/// confirmed in play - and otherwise from the atlas tree panel, which is not always loaded, so the last value read
/// is kept and the dump says where it came from and how old it is.
/// </summary>
internal static class AtlasStats
{
    /// <summary>
    /// The stat "Gaining Traction" (AtlasExpeditionNotable12) grants, which the game describes as "Verisium Remnants
    /// have 50% increased Monster Rarity per Remnant Completed in Area".
    /// </summary>
    private const string MagicAndRarePacksPerRemnantCompletedKey =
        "map_expedition2_remnant_number_of_magic_and_rare_packs_+%_and_rare_monster_modifiers_chance_%_per_remnant_completed";

    /// <summary>
    /// Percent more magic and rare packs, and more rare monster modifier chance, in a remnant's waves for each
    /// remnant already completed in the area, summed over the allocated passives; nought when none grants it, null
    /// until the tree has been read once. Only checked against DebugSettings.GainingTraction, which is what the scoring
    /// follows. NOTES.md, "Gaining Traction", has what the recorded waves say about the node.
    /// </summary>
    public static int? MagicAndRarePacksPerRemnantCompletedPct { get; private set; }

    /// <summary>
    /// Whether a stat's name concerns rare or magic monsters or packs, for the dump's section on them and the spawn
    /// recorder's atlas column. By name only: which are increased, more, or already-summed totals is not said by the
    /// name. See NOTES.md, "Map modifiers on rare and magic monsters".
    /// </summary>
    public static bool ConcernsMonsterRarity(string statName) =>
        statName != null &&
        (statName.Contains("Rare", StringComparison.Ordinal) || statName.Contains("Magic", StringComparison.Ordinal)) &&
        (statName.Contains("Pack", StringComparison.Ordinal) || statName.Contains("Monster", StringComparison.Ordinal)) &&
        !statName.Contains("Item", StringComparison.Ordinal) && !statName.Contains("Chest", StringComparison.Ordinal);

    /// <summary>
    /// The increased number of rare and magic monsters the map has from its modifiers, tablets and atlas, in per cent:
    /// MapStatsVisible's MapNumberOfRarePacksPct plus the server atlas stats', and magic likewise. Matched against the
    /// game's map summary on three maps (186 = 126 + 60, 268 = 208 + 60, 277 = 217 + 60). The biome's atlas bonus is
    /// already inside the map figure: a map with no rare modifiers on an Ocean-counts-as-Forest atlas read
    /// MapNumberOfRarePacksPct 65, the atlas's MapForestNumberOfRarePacksPct (2026-10-01), so it is not added again.
    /// The scoring starts its rows' own increases here when
    /// DebugSettings.MapMonsterIncreases is on; see PlanEnvironment.IncreaseBaseOfGroup.
    /// </summary>
    public static (int Rare, int Magic) MapMonsterIncreases(GameController gc)
    {
        var map = Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);
        var atlas = Safe.Read(() => gc.IngameState.ServerData.AtlasStats, null);

        static int Of(Dictionary<GameStat, int> stats, GameStat stat) =>
            stats != null && stats.TryGetValue(stat, out var value) ? value : 0;

        return (Of(map, GameStat.MapNumberOfRarePacksPct) + Of(atlas, GameStat.MapNumberOfRarePacksPct),
            Of(map, GameStat.MapNumberOfMagicPacksPct) + Of(atlas, GameStat.MapNumberOfMagicPacksPct));
    }

    /// <summary>
    /// The map's pack size, in per cent: MapStatsVisible's MapPackSizePctFinalFromMap, the figure the remnant pack model
    /// was fitted on (2026-10-05). See Weighing.PacksOfRemnant.
    /// </summary>
    public static int MapPackSize(GameController gc)
    {
        var map = Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);

        return map != null && map.TryGetValue(GameStat.MapPackSizePctFinalFromMap, out var value) ? value : 0;
    }

    /// <summary>
    /// The map's "more magic and rare monsters", in per cent: MapStatsVisible's
    /// MapNumberOfMagicAndRarePacksPctFinalAndRareMonsterModifiersChancePctFinalFromMap. Matched against the game's
    /// map summary, where 18 read as "18% more magic and rare monsters" (2026-10-01). A multiplier on the increased
    /// total, not part of it. See Weighing.RemnantWaveScales.
    /// </summary>
    public static int MapMoreMagicAndRareMonsters(GameController gc)
    {
        var map = Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);

        return map != null &&
               map.TryGetValue(GameStat.MapNumberOfMagicAndRarePacksPctFinalAndRareMonsterModifiersChancePctFinalFromMap,
                   out var value)
            ? value
            : 0;
    }

    /// <summary>When the tree was last read, or null for never.</summary>
    private static DateTime? _readAt;

    /// <summary>Where the value last read came from. See Refresh.</summary>
    private static string _source = "";

    /// <summary>
    /// Reads the stat again: from the server's atlas stats when they hold it, else from the allocated passives on the
    /// tree panel, keeping the last value when neither can be read.
    /// </summary>
    public static void Refresh(GameController gc)
    {
        var server = Safe.Read(() => gc.IngameState.ServerData.AtlasStats, null);

        if (server != null &&
            server.TryGetValue(GameStat.MapExpedition2RemnantNumberOfMagicAndRarePacksPctAndRareMonsterModifiersChancePctPerRemnantCompleted,
                out var fromServer))
        {
            MagicAndRarePacksPerRemnantCompletedPct = fromServer;
            _readAt = DateTime.UtcNow;
            _source = "the server's atlas stats";

            return;
        }

        var passives = Safe.Read(() => gc.IngameState.IngameUi.AtlasTreePanel?.Passives, null);

        if (passives == null)
            return;

        var total = 0;

        foreach (var passive in passives)
        {
            if (!Safe.Read(() => passive.IsAllocatedForPlan, false))
                continue;

            foreach (var (stat, value) in Safe.Read(() => passive.PassiveSkill?.Stats, null) ?? [])
            {
                if (string.Equals(Safe.Read(() => stat?.Key, null), MagicAndRarePacksPerRemnantCompletedKey,
                        StringComparison.Ordinal))
                    total += value;
            }
        }

        MagicAndRarePacksPerRemnantCompletedPct = total;
        _readAt = DateTime.UtcNow;
        _source = server == null
            ? "the atlas tree panel (the server's atlas stats unreadable)"
            : "the atlas tree panel (the server's atlas stats do not hold it)";
    }

    /// <summary>
    /// What is known of the atlas stats, in words, for the dump, against the checkbox the scoring follows. A
    /// disagreement is said in capitals, since it means the plans are scored for an atlas the player does not have.
    /// </summary>
    public static string Said(bool ticked)
    {
        var (magic, rare) = ticked ? Safe.Read(Weighing.GainingTractionRates, (0f, 0f)) : (0f, 0f);
        var scored = ticked
            ? $"the checkbox is on, so the scoring uses the table's {Weighing.GainingTractionRow} row: " +
              $"+{magic * 100f:0.#}% magic and +{rare * 100f:0.#}% rare packs per remnant"
            : "the checkbox is off, so the scoring uses nothing";

        if (MagicAndRarePacksPerRemnantCompletedPct is not { } pct)
            return $"Gaining Traction: {scored}; the atlas has not been readable yet to check it - open the atlas once";

        var read = $"the atlas reads +{pct}% (from {_source}, {(DateTime.UtcNow - _readAt!.Value).TotalMinutes:0} minute(s) ago)";
        // Agreement is about whether the node is allocated. The rate scored is chosen apart from the stat - see
        // the table's Gaining Traction row - so a stat of 50 against a scored 25 is not a disagreement.
        var agree = ticked ? pct > 0 : pct == 0;

        return $"Gaining Traction: {scored}; {read}" +
               (agree ? "" : " - THEY DISAGREE: set the Debug checkbox to match your atlas");
    }
}
