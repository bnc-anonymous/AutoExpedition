using ExileCore2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// How far from the player the game loads and unloads things, measured rather than assumed.
///
/// **The scouting layer rests on one number and that number was a guess.** How near you have to
/// come before a marker exists to the client decides how much of a Grand site one lap teaches the
/// planner, and the first answer - ninety grid - came from the placement reach, which has nothing to
/// do with it. A dump put it nearer two hundred. A dump is one frame, though, and what it shows is
/// which things happen to be loaded now, not the distance at which they arrived.
///
/// The distance at which they arrive is directly observable and nobody was watching for it: every
/// sweep, a marker that had no entity last time and has one now has just been loaded, and the
/// player was exactly this far away when it happened. The same in reverse for one that has just
/// been dropped. So both are recorded, with the extremes that matter kept:
///
/// - the FURTHEST a thing has been seen to load, which is a floor on the streaming radius
/// - the NEAREST a thing has been seen to unload, which is a ceiling on what can be relied on
///
/// Those two bracket it, in the same shape as the blast radius and the marker extents - and for the
/// same reason, which is that a bracket says how well it is known where a single number does not.
///
/// **Zoning in is where the evidence is.** Standing in a site that is already loaded produces
/// nothing; walking into a fresh one loads a few hundred markers in a couple of minutes, each one a
/// sample. Written to streaming.csv as it goes so the run can be looked at afterwards.
/// </summary>
internal sealed class Streaming
{
    private readonly List<string> _pending = new();

    private uint _area;
    private string _path;
    private DateTime _wrote;

    /// <summary>The furthest anything has been seen to load. A floor on the radius.</summary>
    public float Loaded { get; private set; }

    /// <summary>The nearest anything has been seen to unload. A ceiling on what can be relied on.</summary>
    public float Dropped { get; private set; } = float.MaxValue;

    public int Loads { get; private set; }

    public int Drops { get; private set; }

    public bool Known => Loaded > 0f || Drops > 0;

    /// <summary>What has been worked out so far, for the dump and the readout.</summary>
    public string Describe()
    {
        if (!Known)
            return "nothing seen load or unload yet - walk into a site rather than standing in one";

        var loaded = Loaded > 0f ? $"loads out to {Loaded:0.#} ({Loads})" : "no loads seen";
        var dropped = Dropped < float.MaxValue
            ? $"drops from {Dropped:0.#} ({Drops})"
            : "no drops seen";

        return $"{loaded}, {dropped}";
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash == _area)
            return;

        _area = areaHash;
        Loaded = 0f;
        Dropped = float.MaxValue;
        Loads = 0;
        Drops = 0;
    }

    /// <summary>
    /// One sweep's worth of arrivals and departures.
    ///
    /// Called after the sweep has reattached entities, so Live means "the game has it right now" and
    /// the flag on the target means "it had it last time".
    /// </summary>
    public void Observe(BaseSettingsPlugin<AutoExpeditionSettings> plugin, Vector2 player,
        IReadOnlyList<Target> targets)
    {
        if (player == Vector2.Zero)
            return;

        foreach (var target in targets)
        {
            var live = target.Live;

            if (live == target.WasLive)
                continue;

            target.WasLive = live;

            var distance = Vector2.Distance(player, target.Grid);

            // Nought is not a reading. A target whose position has not been established yet would
            // otherwise report the player's own distance from the origin.
            if (distance <= 0f)
                continue;

            if (live)
            {
                Loads++;
                Loaded = MathF.Max(Loaded, distance);
            }
            else
            {
                Drops++;
                Dropped = MathF.Min(Dropped, distance);
            }

            _pending.Add(string.Join(',',
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                live ? "load" : "drop",
                target.Kind,
                distance.ToString("0.###", CultureInfo.InvariantCulture),
                target.Art));
        }

        Flush(plugin);
    }

    /// <summary>
    /// Appended as it goes rather than written at the end.
    ///
    /// The interesting run is the one where something unexpected happens and the HUD is closed
    /// before anybody thinks to save it - the same reasoning as the crossing log next to it.
    /// </summary>
    private void Flush(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        if (_pending.Count == 0 || DateTime.UtcNow - _wrote < TimeSpan.FromSeconds(5))
            return;

        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");

                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "streaming.csv");

                if (!File.Exists(_path))
                    File.WriteAllText(_path, "when,event,kind,distanceGrid,art\n");
            }

            File.AppendAllLines(_path, _pending);
            _pending.Clear();
            _wrote = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the streaming log: {ex.Message}", 5f);
            _pending.Clear();
        }
    }
}
