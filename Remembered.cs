using ExileCore2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// What has been scouted, kept on disk so a plugin reload does not undo the walking.
///
/// **Scouting a Grand site takes minutes and a plugin reload takes seconds.** The game unloads what
/// is behind you, so the only way the scan learns a 2370 by 1449 site is for somebody to walk its
/// edge - and every reload after a code change threw that away and asked for the lap again. That is
/// the single most expensive thing about working on this plugin in a live dig site, and none of it
/// is information the game will not still be true about in a minute's time.
///
/// So the marker records are written beside the dumps, keyed on the area hash, and read back when
/// the same area comes round again. What is stored is only what the scan worked out for itself -
/// where a thing is, what kind it is, what art it wears. Anything read live off an entity, a
/// remnant's activated state or its prices, is deliberately not stored: those change, and a stale
/// answer that looks fresh is worse than no answer.
///
/// **A restored target has no entity**, which is the state the scan already handles for a marker the
/// game has unloaded - it draws, it counts, it plans, and the next sweep that walks past hands it
/// its entity back. So nothing downstream needs to know this happened.
/// </summary>
internal static class Remembered
{
    /// <summary>Bumped when the columns change, so an old file is ignored rather than misread.</summary>
    /// <summary>
    /// Bumped when the columns change, so an older file is ignored rather than misread.
    ///
    /// **Version 2 is the first that does not write the kind as a number.** An enum's ordinal is a
    /// storage format the moment something persists it, and nothing here said so - adding a kind in
    /// the middle of the list silently re-labelled every marker after it in every file already
    /// written, and a site came back holding eighty one exploding barrels that were nothing of the
    /// sort. Names do not renumber.
    /// </summary>
    private const int Version = 2;

    /// <summary>
    /// The marker format, for anything that records the same lap and has to be thrown away with it.
    ///
    /// Only Scouted needs this, and the reason is worth stating: the two files are two halves of one
    /// record. This one holds what was found; Scouted holds where the finding happened. Discarding
    /// this one alone leaves the other claiming ground has been covered by a sweep whose results no
    /// longer exist - so the map reads as walked, nothing is painted red, and the one layer that
    /// exists to say "go back over this" says the opposite. See Scouted.Restore.
    /// </summary>
    public static int Format => Version;

    /// <summary>How long a site is worth keeping. A map is gone long before this.</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    /// <summary>How many sites to keep at most, oldest dropped first.</summary>
    private const int Most = 60;

    /// <summary>
    /// Writes the scouted markers for an area.
    ///
    /// Named for the area hash, which is the game's own identity for this instance of this map - two
    /// runs of the same map are two hashes, so nothing is ever restored across a map you have left
    /// and come back to. The name and the dimensions go in the file as well and are checked on the
    /// way back, because a hash is only as unique as the game makes it and restoring another map's
    /// markers would be a very confusing kind of wrong.
    /// </summary>
    public static void Save(string home, uint area, string name, Vector2 size, ICollection<Target> targets)
    {
        if (string.IsNullOrEmpty(home) || area == 0 || targets.Count == 0)
            return;

        try
        {
            var directory = Folder(home);
            var lines = new List<string>
            {
                $"version\t{Version}",
                $"area\t{area}",
                $"name\t{name}",
                $"size\t{size.X:0}\t{size.Y:0}",
                "grid\tkind\ttier\tradius\tsiteX\tsiteY\tsockets\tfixedSlot\trolled\tart\ticon\trarity\tmeta",
            };

            foreach (var target in targets)
            {
                lines.Add(string.Join('\t',
                    $"{target.Grid.X:0.###},{target.Grid.Y:0.###}",
                    target.Kind.ToString(),
                    (int)target.Tier,
                    target.Radius.ToString("0.###", CultureInfo.InvariantCulture),
                    target.Site.X.ToString("0.###", CultureInfo.InvariantCulture),
                    target.Site.Y.ToString("0.###", CultureInfo.InvariantCulture),
                    target.Sockets,
                    target.FixedSlot,
                    target.WasRolled ? 1 : 0,
                    target.Art,
                    target.Icon,
                    // Before Meta, which stays the last column: a strongbox restored from memory
                    // without its rarity drops to the plainest weight, and the whole point of the
                    // file is that walking away does not change what the site is worth.
                    target.Rarity,
                    target.Packs,
                    target.Explodes ? 1 : 0,
                    target.Holds,
                    // How far this thing's own explosion reaches, which is read from a live entity
                    // and therefore unavailable to a marker restored from here. Without it a site
                    // came back with its barrels at no radius, setting nothing off, and only the
                    // ones walked past again recovered. See Target.Sets.
                    target.Sets.ToString("0.###", CultureInfo.InvariantCulture),
                    // The guarding packs by rarity, appended here for the same reason Rarity was
                    // slotted in above: a strongbox restored from memory without them drops back to
                    // a bare pack count and the site quietly loses what the rarities were worth.
                    // Packs above stays the total, which is what an older file has instead of these.
                    target.ImplicitPacks,
                    target.ImplicitMagicPacks,
                    target.ImplicitRarePacks,
                    target.ExplicitPacks,
                    target.ExplicitMagicPacks,
                    target.ExplicitRarePacks,
                    // The object's own modifiers, which say what a relic grants. Read from a live entity and
                    // latched for the map, but a site restored from here came back without them, so after a
                    // plugin reload every relic was worth its flat weight alone until walked past again: two
                    // "Runic Monsters are Duplicated" relics on one site read 3 and 0 to the heavy test. Tabs
                    // separate the columns, so the commas inside are safe. See Target.Mods.
                    target.Mods,
                    // Whether a remnant was found to belong to no dig site, which is settled only by picking a
                    // recipe beside it and is not something to have to do twice. See Target.Lone.
                    target.Lone switch { true => "1", false => "0", _ => "" },
                    target.Meta));
            }

            var path = Path.Combine(directory, $"site_{area}.tsv");

            // Never replace a fuller memory with a thinner one.
            //
            // Within one visit the marker count only ever grows, so a smaller set means something
            // has forgotten rather than learned - a cache clear that wiped the scan, most likely -
            // and writing it would turn a debugging keystroke into the loss of a lap round the site.
            if (File.Exists(path) && Rows(path) > targets.Count)
                return;

            File.WriteAllLines(path, lines);
            Prune(directory);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not remember the site: {ex.Message}", 5f);
        }
    }

    /// <summary>Reads back what was scouted here, or nothing if this area has not been seen.</summary>
    public static List<Target> Load(string home, uint area, string name, Vector2 size)
    {
        var found = new List<Target>();

        if (string.IsNullOrEmpty(home) || area == 0)
            return found;

        try
        {
            var path = Path.Combine(Folder(home), $"site_{area}.tsv");

            if (!File.Exists(path))
                return found;

            var lines = File.ReadAllLines(path);
            var body = false;

            foreach (var line in lines)
            {
                var parts = line.Split('\t');

                if (!body)
                {
                    switch (parts[0])
                    {
                        // Any disagreement means this file is not about the place being stood in.
                        case "version" when parts.Length < 2 || parts[1] != Version.ToString():
                        case "area" when parts.Length < 2 || parts[1] != area.ToString():
                        case "name" when parts.Length < 2 || parts[1] != name:
                            return new List<Target>();

                        case "size" when parts.Length < 3 ||
                                         parts[1] != size.X.ToString("0") ||
                                         parts[2] != size.Y.ToString("0"):
                            return new List<Target>();

                        case "grid":
                            body = true;

                            break;
                    }

                    continue;
                }

                if (parts.Length < 12)
                    continue;

                var at = parts[0].Split(',');

                if (at.Length < 2)
                    continue;

                var restored = new Target
                {
                    Grid = new Vector2(Number(at[0]), Number(at[1])),
                    // Names only. TryParse takes "9" as happily as "Barrel" and answers with
                    // whatever is ninth today, which is the very mistake the version bump exists to
                    // prevent - and a file that slipped through by any other route would walk
                    // straight back into it.
                    Kind = !char.IsDigit(parts[1].FirstOrDefault()) &&
                           Enum.TryParse<TargetKind>(parts[1], out var kind)
                        ? kind
                        : TargetKind.Scenery,
                    Tier = (ChestTier)(int)Number(parts[2]),
                    Radius = Number(parts[3]),
                    Site = new Vector2(Number(parts[4]), Number(parts[5])),
                    Sockets = (int)Number(parts[6]),
                    FixedSlot = (int)Number(parts[7]),
                    WasRolled = parts[8] == "1",
                    Art = parts[9],
                    Icon = parts[10],
                    // A file written before rarity existed has twelve columns and this one has
                    // thirteen, with the metadata last in both. Read from the end rather than
                    // bumping the version and throwing the old file away: the only field it is
                    // missing is one that did not exist, and a lap already walked is worth more
                    // than a tidy format change.
                    Rarity = parts.Length > 12 ? parts[11] : "",
                    Packs = parts.Length > 13 ? (int)Number(parts[12]) : 0,
                    Explodes = parts.Length > 15 && parts[13] == "1",
                    Holds = parts.Length > 15 ? parts[14] : parts.Length > 14 ? parts[13] : "",
                    Sets = parts.Length > 16 ? Number(parts[15]) : 0f,

                    // Six more columns, read the same length-gated way. Metadata stays last in
                    // both, and a file written before the rarities were split has only the total -
                    // see the fixup below.
                    ImplicitPacks = parts.Length > 22 ? (int)Number(parts[16]) : 0,
                    ImplicitMagicPacks = parts.Length > 22 ? (int)Number(parts[17]) : 0,
                    ImplicitRarePacks = parts.Length > 22 ? (int)Number(parts[18]) : 0,
                    ExplicitPacks = parts.Length > 22 ? (int)Number(parts[19]) : 0,
                    ExplicitMagicPacks = parts.Length > 22 ? (int)Number(parts[20]) : 0,
                    ExplicitRarePacks = parts.Length > 22 ? (int)Number(parts[21]) : 0,

                    // One more, the modifiers, read the same way; a file written before it has none.
                    Mods = parts.Length > 23 ? parts[22] : "",
                    Lone = parts.Length > 24 ? parts[23] switch { "1" => true, "0" => false, _ => null } : null,
                    Meta = parts[^1],
                };

                // **An older file has the total and not the rarities.** Read as it stands that box
                // would be guarded by six packs of nothing, so the total is taken as normal packs:
                // it is what the weighting assumed before the split existed, and it is the likelier
                // rarity. The next walk past the label replaces it with the real split.
                if (restored.Kind == TargetKind.Strongbox && restored.Packs > 0 &&
                    restored.ImplicitPacks + restored.ImplicitMagicPacks +
                    restored.ImplicitRarePacks == 0)
                    restored.ImplicitPacks = restored.Packs;

                found.Add(restored);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the remembered site: {ex.Message}", 5f);

            return new List<Target>();
        }

        return found;
    }

    /// <summary>Drops the file for an area, for the cache-clearing key that means "really forget".</summary>
    public static void Drop(string home, uint area)
    {
        if (string.IsNullOrEmpty(home) || area == 0)
            return;

        try
        {
            var path = Path.Combine(Folder(home), $"site_{area}.tsv");

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Nothing useful to do about it, and a failed delete is not worth a line on screen.
        }
    }

    /// <summary>How many markers a stored file holds, without parsing any of them.</summary>
    private static int Rows(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var body = false;
            var rows = 0;

            foreach (var line in lines)
            {
                if (body)
                    rows++;
                else if (line.StartsWith("grid	", StringComparison.Ordinal))
                    body = true;
            }

            return rows;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string Folder(string home)
    {
        var directory = Path.Combine(home, "sites");

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>
    /// Keeps the folder from growing without end.
    ///
    /// Every dig site ever walked would otherwise sit there for good, and they are of no use after
    /// the map closes - a map is one visit and its hash never comes round again. Both an age and a
    /// count, because a heavy session makes sixty files in a day and a quiet month makes none.
    /// </summary>
    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("site_*.tsv")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        for (var i = 0; i < files.Count; i++)
        {
            if (i >= Most || DateTime.UtcNow - files[i].LastWriteTimeUtc > Keep)
                files[i].Delete();
        }
    }

    private static float Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0f;
}
