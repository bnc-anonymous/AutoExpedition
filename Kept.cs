using ExileCore2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The best chain ever found at a dig site, kept across undos, presses and reloads.
///
/// **The search's memory of a site is one press long, and everything else about the plugin is not.**
/// The markers survive a reload, the walked ground survives it, the refused cells and the measured
/// range survive it - and the one thing the player actually cares about, the route, was held in a
/// field that the next solve overwrote. Undo every explosive and the chain that scored 2,780 was
/// gone; the next press started from a greedy answer and had to find its way back, which it does
/// not reliably do.
///
/// So the best chain is written down. It is a floor that cannot be lost, which is what makes
/// pressing the key safe: a re-solve can improve on the best this site has ever seen, and it can
/// never leave you worse off than you already were.
///
/// **One canonical form, and it matters.** What is stored is the whole route from the detonator -
/// the explosives already down at the time plus the links that were still planned - because that is
/// the only shape two presses can be compared in. A plan made half way along a chain is a plan for
/// the remainder, scored against an origin that has moved and a pool of content an earlier blast has
/// already taken, and filing that beside a full route would be filing two different questions under
/// one answer.
///
/// **The score is a hint; the chain is the fact.** Weights are settings, remnant choices change, and
/// content streams in - so a number written down last session says nothing reliable about this one.
/// It is kept for the dump, and every use of the chain re-scores it against the environment in hand.
/// </summary>
internal static class Kept
{
    /// <summary>Where the files live. Set once by the plugin; empty turns remembering off.</summary>
    public static string Home { get; set; } = "";

    /// <summary>Bumped when the columns change, so an older file is ignored rather than misread.</summary>
    private const int Version = 1;

    /// <summary>The best route known here, from the detonator, or null when there is none.</summary>
    public static List<Vector2> Chain { get; private set; }

    /// <summary>What it scored when it was filed, for the dump only. See the class summary.</summary>
    public static double Worth { get; private set; }

    /// <summary>
    /// Whether the chain on file can no longer be placed, so it must not block a replacement.
    ///
    /// Set by the solve that discovered it - see Planning.Start. It is not a reason to delete
    /// anything: the route is still the best this site has ever given up, and the cell that went bad
    /// may come back.
    /// </summary>
    public static bool Stale { get; set; }

    /// <summary>Which press filed it, so the dump can say whether it is this session's.</summary>
    private static DateTime _when;

    private static uint _area;
    private static Vector2 _site;

    /// <summary>
    /// Reads back the best chain for a site, if one was ever filed.
    ///
    /// Per site rather than per area, because a map holds two expeditions and their routes have
    /// nothing to do with each other.
    /// </summary>
    public static void Load(uint area, Vector2 site, string name, Vector2 size)
    {
        if (area == _area && site == _site)
            return;

        _area = area;
        _site = site;
        Chain = null;
        Worth = 0d;
        _when = DateTime.MinValue;

        if (Home.Length == 0 || area == 0 || site == Vector2.Zero)
            return;

        var path = Path.Combine(Folder(), File(area, site));

        if (!System.IO.File.Exists(path))
            return;

        try
        {
            var chain = new List<Vector2>();

            foreach (var line in System.IO.File.ReadLines(path))
            {
                var parts = line.Split('\t');

                switch (parts[0])
                {
                    // A hash is only as unique as the game makes it, so the name and the size are
                    // checked on the way back exactly as Remembered and Learnt check them.
                    case "version" when parts.Length < 2 || parts[1] != Version.ToString():
                    case "name" when parts.Length < 2 || parts[1] != name:
                        return;

                    case "size" when parts.Length < 3 ||
                                     (int)Number(parts[1]) != (int)size.X ||
                                     (int)Number(parts[2]) != (int)size.Y:
                        return;

                    case "worth" when parts.Length >= 2:
                        Worth = Number(parts[1]);

                        break;

                    case "when" when parts.Length >= 2:
                        _when = DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal, out var stamp)
                            ? stamp
                            : DateTime.MinValue;

                        break;

                    case "spot" when parts.Length >= 3:
                        chain.Add(new Vector2(Number(parts[1]), Number(parts[2])));

                        break;
                }
            }

            Chain = chain.Count > 0 ? chain : null;
            Stale = false;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the best chain: {ex.Message}", 5f);
        }
    }

    /// <summary>
    /// Files a route if it beats what is already known here.
    ///
    /// The caller has scored both against the same environment, which is the only sound comparison -
    /// see the class summary on why the stored number cannot be trusted for it.
    /// </summary>
    /// <returns>Whether this became the new best.</returns>
    public static bool Offer(uint area, Vector2 site, string name, Vector2 size,
        List<Vector2> chain, double worth, double standing)
    {
        if (chain is not { Count: > 0 } || area == 0 || site == Vector2.Zero)
            return false;

        // Ties do not displace. The chain already filed has been proved on the ground at least as
        // often, and swapping one for another of identical worth churns the file for nothing.
        if (Chain is { Count: > 0 } && !Stale && worth <= standing)
            return false;

        Stale = false;

        _area = area;
        _site = site;
        Chain = new List<Vector2>(chain);
        Worth = worth;
        _when = DateTime.UtcNow;

        if (Home.Length == 0)
            return true;

        try
        {
            var lines = new List<string>
            {
                $"version\t{Version}",
                $"area\t{area}",
                $"name\t{name}",
                $"size\t{size.X:0}\t{size.Y:0}",
                $"site\t{site.X:0}\t{site.Y:0}",
                $"worth\t{worth.ToString("0.0", CultureInfo.InvariantCulture)}",
                $"when\t{_when:O}",
            };

            foreach (var at in Chain)
                lines.Add($"spot\t{at.X:0}\t{at.Y:0}");

            System.IO.File.WriteAllLines(Path.Combine(Folder(), File(area, site)), lines);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the best chain: {ex.Message}", 5f);
        }

        return true;
    }

    /// <summary>
    /// Throws away the best chain for a site, for the cache reset.
    ///
    /// **A cache clear that leaves the file behind clears nothing**, which is the same reasoning
    /// Learnt.Forget carries: the next area change would read it straight back, and a player who
    /// pressed the button to measure a cold solve would get a warm one with no sign of it.
    /// </summary>
    /// <summary>
    /// Throws away every chain filed in this area, which is what the cache reset can actually ask
    /// for: it knows the area it is clearing and not which dig site of the two you are standing in.
    /// </summary>
    public static void ForgetAll(uint area)
    {
        Chain = null;
        Worth = 0d;
        _when = DateTime.MinValue;
        _area = 0;
        _site = Vector2.Zero;

        if (Home.Length == 0 || area == 0)
            return;

        try
        {
            foreach (var path in Directory.GetFiles(Folder(), $"plan_{area}_*.tsv"))
                System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not forget the best chains: {ex.Message}", 5f);
        }
    }

    public static void Forget(uint area, Vector2 site)
    {
        Chain = null;
        Worth = 0d;
        _when = DateTime.MinValue;
        _area = 0;
        _site = Vector2.Zero;

        if (Home.Length == 0 || area == 0 || site == Vector2.Zero)
            return;

        try
        {
            var path = Path.Combine(Folder(), File(area, site));

            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not forget the best chain: {ex.Message}", 5f);
        }
    }

    /// <summary>What is on file here, for the dump.</summary>
    public static string Describe() =>
        Chain is not { Count: > 0 }
            ? "nothing filed for this site yet"
            : $"{Chain.Count} spots worth {Worth:N1} when it was filed" +
              (_when == DateTime.MinValue
                  ? ""
                  : $", {(DateTime.UtcNow - _when).TotalMinutes:0} minutes ago");

    private static string File(uint area, Vector2 site) =>
        $"plan_{area}_{site.X:0}_{site.Y:0}.tsv";

    private static string Folder()
    {
        var directory = Path.Combine(Home, "sites");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static float Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0f;
}
