using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExileCore2;
using ExileCore2.PoEMemory.FilesInMemory;

namespace AutoExpedition;

/// <summary>
/// Writes the recipes a remnant's Runeshape Combinations window offers, whenever it is open, to
/// dumps/remnant_offers.csv - one row per remnant, per rolled state, per distinct list.
///
/// remnants.csv cannot hold this for a rerolled remnant: the plugin pins a rolled remnant to its chosen combination,
/// so its census row carries the one reward chosen and never the list offered. Every row here comes from the
/// window itself, fresh and rerolled alike, with each option's rune count and recipe id, so which rune counts a
/// fixed rune and slot admit can be checked against the game rather than against the chosen reward. See NOTES,
/// "Which recipes a remnant offers".
///
/// Off with the census: Data collection > Collect remnants seen.
/// </summary>
internal static class RemnantOffers
{
    private const string Header = "when,area,areaHash,x,y,origin,sockets,fixedRune,fixedSlot,offered";

    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);
    private static readonly List<string> Pending = new();
    private static readonly object Lock = new();
    private static string _path;

    /// <summary>Notes the list the window is showing for this remnant, if it is a list not written before.</summary>
    public static void Note(GameController gc, Target remnant, IEnumerable<Expedition2Recipe> recipes)
    {
        if (remnant == null || recipes == null)
            return;

        var offered = new List<string>();

        foreach (var recipe in recipes)
        {
            if (recipe == null)
                continue;

            offered.Add(Clean(Valuation.Name(recipe)) + ":" +
                        Safe.Read(() => recipe.RuneCountRequired, -1).ToString(CultureInfo.InvariantCulture) + ":" +
                        Clean(Safe.Read(() => recipe.Id, "") ?? ""));
        }

        if (offered.Count == 0)
            return;

        var hash = Safe.Read(() => gc.Area.CurrentArea.Hash, 0u);
        var x = (int)MathF.Round(remnant.Grid.X);
        var y = (int)MathF.Round(remnant.Grid.Y);
        var origin = remnant.Rerolled ? "rerolled" : "fresh";
        var list = string.Join("|", offered);

        lock (Lock)
        {
            if (!Written.Add($"{hash}:{x},{y}:{origin}:{list}"))
                return;

            Pending.Add(string.Join(",",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Clean(Safe.Read(() => gc.Area.CurrentArea.Name, "") ?? ""),
                hash.ToString("X8", CultureInfo.InvariantCulture),
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture),
                origin,
                remnant.Sockets.ToString(CultureInfo.InvariantCulture),
                Clean(remnant.FixedRune),
                remnant.FixedSlot.ToString(CultureInfo.InvariantCulture),
                list));
        }
    }

    /// <summary>Appends what has been noted. Called from the plugin, which knows where the files go.</summary>
    public static void Flush(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        List<string> rows;

        lock (Lock)
        {
            if (Pending.Count == 0)
                return;

            rows = new List<string>(Pending);
            Pending.Clear();
        }

        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "remnant_offers.csv");

                if (!File.Exists(_path))
                    File.WriteAllText(_path, Header + "\n");
            }

            File.AppendAllLines(_path, rows);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write remnant_offers.csv: {ex.Message}", 5f);
        }
    }

    private static string Clean(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace(',', ';').Replace('|', '/').Replace(':', ';').Trim();
}
