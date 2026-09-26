using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// What each marker turned into.
///
/// This settled the chest tiers, and the answer was not the one the art suggested. The three
/// chestmarker_signpost variants each have their own height where the three monstermarker variants
/// share one, which read like a tier - and it is not. All three signposts spawn the same
/// LeagueFaction1Common chest; the tier lives in a different model entirely, chestmarker2, which
/// spawns LeagueFaction1Uncommon. Weighting the tall signpost above the short one would have been
/// wrong all season and nothing in game would ever have said so.
///
/// So it is measured instead. Remember where every marker stood and what art it wore, clear the
/// encounter - the chests appear once it is cleared, not when the explosives go off - and then
/// match what actually spawned back to the position it came from.
///
/// Chests do not move, and every one of them matched its marker at a distance of exactly zero, so
/// that half is unambiguous. Monsters are near worthless here by comparison: they walk, they die,
/// and the first run matched a RuneEncounterController to a chest signpost nineteen units away,
/// which plainly did not spawn there. Their distance is reported rather than hidden for exactly
/// that reason.
/// </summary>
internal sealed class Correlate
{
    private readonly record struct Remembered(Vector2 Grid, string Art, TargetKind Kind);

    private readonly List<Remembered> _before = new();
    private uint _area;
    private int _reports;

    public bool Ready => _before.Count > 0;

    public int Count => _before.Count;

    /// <summary>A dig site is per-area, so a snapshot does not survive leaving one.</summary>
    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _before.Clear();
            _reports = 0;
        }

        _area = areaHash;
    }

    /// <summary>
    /// One key, two meanings: remember first, correlate after. Returns what to tell the player.
    /// </summary>
    public string Press(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc, Scan scan)
    {
        if (!Ready)
        {
            Remember(scan);

            return $"remembered {_before.Count} marker(s) - press again after detonating for the " +
                   "monsters, and again after clearing for the chests";
        }

        var path = Report(plugin, gc);
        _reports++;

        // The snapshot deliberately survives the report. The two halves of an encounter arrive at
        // different times - monsters when the explosives go off, chests only once it is cleared -
        // so one snapshot wants reporting against twice, and clearing it after the first threw
        // away the half that had not happened yet. It is dropped on leaving the area instead.
        return path == null
            ? "could not write the report"
            : $"wrote {Path.GetFileName(path)} (report {_reports}; markers kept, press again later)";
    }

    private void Remember(Scan scan)
    {
        _before.Clear();

        foreach (var target in scan.Targets.Where(t => t.Kind != TargetKind.Remnant))
            _before.Add(new Remembered(target.Grid, target.Art, target.Kind));
    }

    private string Report(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc)
    {
        var b = new StringBuilder();
        b.AppendLine($"AutoExpedition marker correlation - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        b.AppendLine($"{_before.Count} marker(s) remembered before the encounter was cleared");
        b.AppendLine();

        var spawned = Spawned(gc);
        b.AppendLine($"{spawned.Count} chest(s) and monster(s) present now");

        var matches = new List<(Remembered From, Entity What, float Distance)>();
        var unmatched = new List<Entity>();

        foreach (var entity in spawned)
        {
            var at = Safe.Read(() => entity.GridPos, Vector2.Zero);
            var nearest = _before
                .Select(m => (Marker: m, Distance: Vector2.Distance(m.Grid, at)))
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            // Twenty grid units is about two thirds of a blast radius: near enough that nothing
            // else plausibly spawned it, far enough to allow for a monster taking a step.
            if (nearest.Marker.Art != null && nearest.Distance <= 20f)
                matches.Add((nearest.Marker, entity, nearest.Distance));
            else
                unmatched.Add(entity);
        }

        b.AppendLine();
        b.AppendLine("=== what each art turned into ===");

        foreach (var group in matches.GroupBy(m => m.From.Art).OrderBy(g => g.Key))
        {
            b.AppendLine();
            b.AppendLine($"--- {group.Key} ({group.Count()} matched) ---");

            foreach (var kinds in group
                         .GroupBy(m => $"{Describe(m.What)}")
                         .OrderByDescending(g => g.Count()))
            {
                var far = kinds.Max(k => k.Distance);
                b.AppendLine($"  {kinds.Count(),3}x  {kinds.Key}   (nearest match up to {far:0.#} away)");
            }
        }

        b.AppendLine();
        b.AppendLine($"=== {unmatched.Count} with no marker within 20 units (ordinary map content) ===");

        foreach (var group in unmatched
                     .GroupBy(x => Safe.Read(() => x.Metadata, "?"))
                     .OrderByDescending(g => g.Count())
                     .Take(30))
            b.AppendLine($"  {group.Count(),3}x  {group.Key}");

        // And the other direction: a marker with nothing near it is one whose content was never
        // unearthed, which is the normal case for anything outside the chain.
        var used = matches.Select(m => m.From).ToHashSet();
        b.AppendLine();
        b.AppendLine("=== markers that produced nothing (not covered by the chain) ===");

        foreach (var group in _before.Where(m => !used.Contains(m)).GroupBy(m => m.Art).OrderBy(g => g.Key))
            b.AppendLine($"  {group.Count(),3}x  {group.Key}");

        try
        {
            var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"autoexpedition_correlation_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, b.ToString());
            DebugWindow.LogMsg($"[AutoExpedition] Wrote {path}", 10f);

            return path;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the correlation: {ex.Message}", 10f);
            return null;
        }
    }

    private static List<Entity> Spawned(GameController gc)
    {
        var by = Safe.Read(() => gc?.EntityListWrapper?.ValidEntitiesByType, null);

        if (by == null)
            return new List<Entity>();

        return new[] { EntityType.Chest, EntityType.Monster }
            .SelectMany(type => by.GetValueOrDefault(type) ?? new List<Entity>())
            .ToList();
    }

    /// <summary>
    /// Enough about a spawned thing to tell one tier from another.
    ///
    /// The metadata is what actually answered it - LeagueFaction1Common against
    /// LeagueFaction1Uncommon - with rarity agreeing. MonsterMinimapIcon was the field expected to
    /// carry the tier, since that is how MinimapIcons tells expedition chests apart, and on these
    /// chests it was not set at all. Kept anyway: a faction or tier this encounter did not roll may
    /// well use it.
    /// </summary>
    private static string Describe(Entity entity)
    {
        var metadata = Safe.Read(() => entity.Metadata, "?");
        var rarity = Safe.Read(() => entity.Rarity.ToString(), "?");

        var icon = "";
        var stats = Safe.Read(() => entity.Stats, null);

        if (stats != null && stats.TryGetValue(GameStat.MonsterMinimapIcon, out var index))
        {
            icon = Enum.IsDefined(typeof(MapIconsIndex), index)
                ? $" icon={(MapIconsIndex)index}"
                : $" icon={index}";
        }

        var large = Safe.Read(() => entity.GetComponent<Chest>()?.IsLarge.ToString(), null);

        return $"{metadata}  rarity={rarity}{icon}{(large == null ? "" : $" large={large}")}";
    }
}
