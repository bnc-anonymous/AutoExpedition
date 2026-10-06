using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// One line per solve, so a comparison between strategies can be made at all.
///
/// **A dig site is used once and never seen again.** It is generated with the map, it does not
/// change while you stand in it, and when the map is done it is gone - so a strategy cannot be
/// re-tested on a site that made it look good, and two runs on different sites say nothing about
/// each other. The only sound comparison is between strategies on the SAME site in the same visit,
/// and the only way to build confidence is to collect many such pairs.
///
/// Which makes recording them by hand exactly the wrong approach: it happened twice, the second time
/// on a site whose scan had quietly lost thirteen markers between the two runs, and that was not
/// visible until the numbers were held side by side afterwards. So every solve writes a line, with
/// the site's shape beside its score - and a pair, or a hundred pairs, can be read out later.
/// </summary>
internal static class Trials
{
    private static string _path;
    private static bool _tried;

    /// <summary>Where the log went, for the dump to say.</summary>
    public static string Last { get; private set; } = "nothing recorded yet";

    /// <param name="site">
    /// Which dig site within the map, as its detonator's position.
    ///
    /// **The area hash names the map, not the site.** A map can hold more than one, and the first
    /// version of this log recorded only the hash - so two sites solved in the same map came back
    /// as one, and forty one rows turned out to describe a single identifier. The detonator does not
    /// move, so where it stands is the site's name.
    /// </param>
    public static void Record(AutoExpedition plugin, string strategy, uint area, int level,
        Vector2 site, Plan plan, int markers, int remnants, int rares, int explosives,
        double seconds, string detail)
    {
        // Behind Data collection's switch. See RecordingSettings.CollectSolveTrials.
        if (plugin == null || plan == null || !Safe.Read(() => plugin.Settings.Recording.CollectSolveTrials.Value, true))
            return;

        try
        {
            if (!_tried)
            {
                _tried = true;

                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");

                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "trials.csv");

                if (!File.Exists(_path))
                {
                    File.WriteAllText(_path,
                        "when,area,site,level,strategy,score,links,covered,markers,remnants," +
                        "rares,explosives,seconds,chain,detail" + Environment.NewLine);
                }
            }

            if (_path == null)
                return;

            var where = new List<string>();

            foreach (var at in plan.Points)
                where.Add($"{at.X:0} {at.Y:0}");

            var line = string.Join(",",
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                area.ToString(CultureInfo.InvariantCulture),
                $"{site.X:0} {site.Y:0}",
                level.ToString(CultureInfo.InvariantCulture),
                Clean(strategy),
                plan.Weight.ToString("0.0", CultureInfo.InvariantCulture),
                plan.Points.Count.ToString(CultureInfo.InvariantCulture),
                plan.Covered.ToString(CultureInfo.InvariantCulture),
                markers.ToString(CultureInfo.InvariantCulture),
                remnants.ToString(CultureInfo.InvariantCulture),
                rares.ToString(CultureInfo.InvariantCulture),
                explosives.ToString(CultureInfo.InvariantCulture),
                seconds.ToString("0.0", CultureInfo.InvariantCulture),
                string.Join(" | ", where),
                Clean(detail));

            File.AppendAllText(_path, line + Environment.NewLine);
            Last = $"{Clean(strategy)} {plan.Weight:N1} over {plan.Points.Count} links, written to trials.csv";
        }
        catch (Exception error)
        {
            // A log that takes the plugin down with it is worse than no log.
            Last = $"could not record: {error.Message}";
            _path = null;
        }
    }

    /// <summary>Commas and newlines out, since this is a csv and the detail is free text.</summary>
    private static string Clean(string text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : text.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
}
