using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Watches for the Bait rune, which the game knows about and has never been seen placing.
///
/// **Thirty four runes exist and one of them behaves oddly.** Bait is in Expedition2Runes with its
/// own id, its own art - TomeRuneBait, RemnantRuneBait - and a mod shared with Power, so the window
/// would call it "Power Rune" if it ever turned up. It appears nowhere in 476 deduplicated fresh
/// remnants, where a rune of ordinary frequency would have shown up about sixteen times.
///
/// That is either a rune cut before release and left in the table, or one that appears under
/// conditions nobody here has met. The name is the interesting part: everything else in the table is
/// named for what it does - Fire, Rebirth, Opulent - and "Bait" is named for what it is FOR.
///
/// So this says so if it ever appears, because the alternative is it turning up in a dig site and
/// going unremarked as a second Power rune. Nothing depends on the answer; it is a curiosity with a
/// cheap enough watch to be worth keeping.
///
/// Costs one string comparison per remnant per tick against the rune in the ground, plus one per
/// propagating candidate. It stops looking once it has seen one. See DebugSettings.WatchBait.
/// </summary>
internal static class Curio
{
    /// <summary>The rune this is about. Spelt once.</summary>
    private const string Bait = "Bait";

    /// <summary>Whether it has ever been seen.</summary>
    public static bool Seen { get; private set; }

    /// <summary>Where, so the overlay can mark it. Cleared on a zone change; Seen is not.</summary>
    public static Vector2 Where { get; private set; }

    /// <summary>What to draw over that remnant.</summary>
    public static string Says { get; private set; } = "";

    private static string _what = "";

    /// <summary>Whether it still needs announcing once in the log.</summary>
    public static bool Fresh { get; private set; }

    public static void AreaChange()
    {
        Where = Vector2.Zero;
        Says = "";
    }

    /// <summary>
    /// Looks at one remnant: the rune already in the ground, and the ones its slots could pass on.
    ///
    /// Not every combination's every slot, which would be thousands of comparisons a frame for a
    /// rune that has never existed. These two are where a rune is actually visible on a remnant, and
    /// a Bait nobody can see is not an encounter with one.
    /// </summary>
    public static void Notice(Target target)
    {
        if (Seen || target == null || target.Kind != TargetKind.Remnant)
            return;

        if (Is(Safe.Read(() => target.FixedRune, null)))
        {
            Found(target, "in the ground");

            return;
        }

        foreach (var slot in target.Passing ?? new List<Passes>())
        {
            foreach (var rune in slot.Runes ?? new List<string>())
            {
                if (!Is(rune))
                    continue;

                Found(target, $"as a candidate for propagating slot {slot.Slot + 1}");

                return;
            }
        }
    }

    private static bool Is(string id) =>
        !string.IsNullOrWhiteSpace(id) && string.Equals(id, Bait, StringComparison.OrdinalIgnoreCase);

    private static void Found(Target target, string how)
    {
        Seen = true;
        Fresh = true;
        Where = Safe.Read(() => target.Grid, Vector2.Zero);
        Says = "BAIT rune - " + how;
        _what = $"{how}, on the remnant at ({Where.X:0},{Where.Y:0}). The window will call it " +
                "\"Power Rune\" - it shares Power's mod - so the id is the only thing that tells " +
                "them apart.";
    }

    /// <summary>Says it once, in the log, so a sighting is not missed while looking elsewhere.</summary>
    public static void Announce(bool wanted)
    {
        if (!Fresh)
            return;

        Fresh = false;

        if (wanted)
            DebugWindow.LogMsg("[AutoExpedition] The Bait rune has appeared: " + _what, 30f);
    }

    /// <summary>What a dump should say, seen or not.</summary>
    public static string Describe() =>
        Seen
            ? "YES - " + _what
            : "not seen - it is in the rune table with its own art and a mod shared with Power, and " +
              "has appeared on no remnant yet";
}
