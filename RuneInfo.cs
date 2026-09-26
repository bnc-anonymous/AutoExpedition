using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.Shared.Enums;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace AutoExpedition;

/// <summary>What a rune does, in the game's own words.</summary>
internal sealed record RuneEffect(string Does, string AtPower);

/// <summary>
/// The rune descriptions, read from the game and kept.
///
/// Built once and held, because it is a file table rather than anything about the current map - and
/// because translating thirty four mods through the stat description files is not work to do per
/// frame behind a tooltip.
///
/// These runes are MONSTER modifiers, which is worth saying plainly since the name suggests
/// otherwise: a rune makes the dig site harder in a specific way rather than granting anything.
///
/// The written lines in RuneEffects are preferred over anything read from the files. The files hold
/// a stat list rather than a sentence, so a successful translation still reads like markup with an
/// internal id in it, and nine of twenty four did not translate at all. Reading from the game is
/// kept for a rune the table does not cover - one added by a patch, most likely - where a rough
/// answer beats none.
/// </summary>
internal static class RuneInfo
{
    /// <summary>
    /// Swapped in whole when the build finishes, rather than filled in place.
    ///
    /// The build runs off the drawing thread, so a reader must never see it half done. A reference
    /// assignment is atomic, so a reader gets either the empty table it had or the complete one -
    /// and a rune missing for the frames the build takes reads the same as a rune not yet loaded,
    /// which is what every caller here already handles.
    /// </summary>
    private static Dictionary<string, RuneEffect> Known = new(StringComparer.OrdinalIgnoreCase);
    private static bool _built;

    /// <summary>
    /// What a rune does, from the cache. No GameController, so a settings delegate can ask.
    ///
    /// Returns null for a rune the game does not describe, which is most of the ones that matter -
    /// see the note on Describable.
    /// </summary>
    public static RuneEffect For(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        // Straight to the written table when it has the rune, so the menu reads correctly in the
        // hideout as well - the game's files are not loaded there and the cache is empty.
        if (RuneEffects.For(id) is { } written)
            return new RuneEffect(written, null);

        return Known.GetValueOrDefault(id);
    }

    /// <summary>
    /// Whether a translated line is worth showing a player.
    ///
    /// It often is not. Several of the runes people rank highest are implemented as bare flags -
    /// Opulent is "HasOpulentRuneMod: 1", Power is "DummyExpeditionRunePower: 1" - and Death, Oath
    /// and Time carry no stats at all, their behaviour living somewhere the stat tables do not
    /// reach. Printing the raw stat name in a tooltip is worse than printing nothing: it looks like
    /// an answer and is not one.
    /// </summary>
    public static bool Describable(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.Contains("<unknown", StringComparison.Ordinal) &&
        !text.Contains("(no stats)", StringComparison.Ordinal);

    /// <summary>
    /// What the game calls this rune on screen, or the id when that cannot be read.
    ///
    /// **The plugin names runes in a language the player's screen does not use.** Expedition2Rune
    /// carries an Id and no name at all - "Gasp" - while the remnant's own window says "Volcanic
    /// Rune". Thirty two of the thirty four match closely enough not to notice; two do not, and a
    /// tester told to look for Gasp will not find it.
    ///
    /// **The name alone, with the id left out.** It was written as "Volcanic (Gasp)" so that the
    /// id stayed findable - the reference table, the census columns and the logs all key on it - and
    /// that is noise on every line for the sake of two runes in thirty four. What somebody reads on
    /// screen should match what the game wrote on the remnant.
    ///
    /// The cost is real and small: Bait and Power share a mod, so both read "Power" and a line
    /// naming one cannot say which. Bait has never been placed - nothing in 476 remnants - and if it
    /// ever is, Curio rings the remnant and names it by id. That is the case covered, rather than
    /// every line paying for it.
    /// </summary>
    public static string Called(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "";

        return Shown.TryGetValue(id, out var name) && name.Length > 0 ? name : Titled(id);
    }

    /// <summary>
    /// A rune id made to read like a name, for one the game has not told us the name of.
    ///
    /// **The fallback was the id verbatim, which is lower case**, so a rune whose UserFriendlyName
    /// had not been read rendered as "Rune: power" among thirty-three that read "Rune: Power". It
    /// looks like a different sort of row rather than the same sort with a gap in it, which is the
    /// opposite of what a fallback should do.
    ///
    /// Only the shape is invented here, never the word: the id is the game's, and this capitalises
    /// it. See Called, and Naming for the real name when the file has one.
    /// </summary>
    private static string Titled(string id)
    {
        var parts = id.Split(' ', '_', '-');

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }

        return string.Join(" ", parts);
    }

    private static Dictionary<string, string> Shown =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The displayed name out of a mod's UserFriendlyName.
    ///
    /// The field is a marked-up block - an icon tag, a colour, the name in braces, then the lines
    /// the tooltip prints under it - so the name is the first braced group and nothing else. The
    /// trailing "Rune" comes off because every one of them has it and a table of things called
    /// "Rune: Volcanic Rune" reads worse than one called "Rune: Volcanic".
    /// </summary>
    private static string Naming(string friendly)
    {
        if (string.IsNullOrWhiteSpace(friendly))
            return "";

        var open = friendly.IndexOf('{');
        var close = open < 0 ? -1 : friendly.IndexOf('}', open + 1);

        if (open < 0 || close < 0)
            return "";

        var name = friendly[(open + 1)..close].Trim();

        return name.EndsWith(" Rune", StringComparison.OrdinalIgnoreCase)
            ? name[..^5].Trim()
            : name;
    }

    public static void Build(GameController gc)
    {
        if (_built || _building)
            return;

        var runes = Safe.Read(() => gc.Files.Expedition2Runes.EntriesList, null);

        // Not an error, just too early: the file tables are not loaded until a character is in a
        // world, so this is retried until it is.
        if (runes == null || runes.Count == 0)
            return;

        // **Off the drawing thread, because it is a 190ms frame otherwise.**
        //
        // This runs once and latches, so its cost is a single frame - but that frame measured
        // 189.6ms and 49MB, which is a third of a second of the game standing still on whichever
        // frame the file tables happen to become readable. Nothing waits on the result: every
        // caller already copes with a rune it has no entry for, because until this runs there are
        // no entries at all.
        //
        // Reads only, like everything else here. See Known for why a reader cannot catch it half
        // built.
        _building = true;

        Task.Run(() => BackgroundWork.Record("rune tables", () =>
        {
            try
            {
                Built(gc);
            }
            finally
            {
                _building = false;
            }
        }));
    }

    /// <summary>Whether the build is in flight, so it is not started twice. See Build.</summary>
    private static volatile bool _building;

    /// <summary>The build itself, off the drawing thread. See Build.</summary>
    private static void Built(GameController gc)
    {
        // Re-read here rather than handed in, so this file needs no name for the record type the
        // host's file table holds.
        var runes = Safe.Read(() => gc.Files.Expedition2Runes.EntriesList, null);

        if (runes == null || runes.Count == 0)
            return;


        var relic = Safe.Read(() => gc.Files.ExpeditionRelicStatDescriptions, null);
        var general = Safe.Read(() => gc.Files.StatDescriptions, null);
        var known = new Dictionary<string, RuneEffect>(StringComparer.OrdinalIgnoreCase);
        var shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rune in runes)
        {
            var id = Safe.Read(() => rune.Id, null);

            if (string.IsNullOrWhiteSpace(id))
                continue;

            // The written table first, the game's own translation only for a rune it does not
            // cover. That is the opposite of the obvious ordering and it is deliberate: the files
            // give a stat list rather than a sentence, so even where the translation succeeds it
            // reads like "1% to gain [UndeadArchon|Archon of Undeath] when you create an
            // [Offering]" - markup, internal ids and a number that is not the one on the remnant.
            // The written lines are the wording the game actually shows a player.
            // Read whether or not the effect text is written down, because the two are separate
            // questions: RuneEffects covers what a rune DOES in the game's own wording, and this is
            // what the game CALLS it. See Called.
            shown[id] = Naming(Safe.Read(() => rune.Mod?.UserFriendlyName, null));

            if (RuneEffects.For(id) is { } written)
            {
                known[id] = new RuneEffect(written, null);
                continue;
            }

            var does = Describe(Safe.Read(() => rune.Mod, null), relic, general);
            var atPower = Describe(Safe.Read(() => rune.ModPower, null), relic, general);

            known[id] = new RuneEffect(does, atPower);
        }

        if (known.Count == 0)
            return;

        Known = known;
        Shown = shown;
        _built = true;
    }

    /// <summary>
    /// A mod as the game words it.
    ///
    /// Built from the mod's own stats and their ranges, then run through the description tables -
    /// the relic one first, since where it has a line that line is the expedition-specific wording,
    /// and the general one behind it because these are monster stats and that is where they live.
    /// The high end of each range is used, since a rune's ceiling is what people rank them by.
    /// </summary>
    public static string Describe(ModsDat.ModRecord mod, StatDescriptionWrapper relic,
        StatDescriptionWrapper general)
    {
        if (mod == null)
            return "-";

        var values = new Dictionary<GameStat, int>();

        Safe.Try(() =>
        {
            var names = mod.StatNames;
            var ranges = mod.StatRange;

            for (var i = 0; i < names.Length && i < ranges.Length; i++)
            {
                if (names[i] == null || ranges[i] == null)
                    continue;

                values[names[i].MatchingStat] = ranges[i].Max;
            }
        });

        if (values.Count == 0)
            return $"{Safe.Read(() => mod.Key, "?")} (no stats)";

        var text = Safe.Read(() => relic?.TranslateMod(values), null);

        if (string.IsNullOrWhiteSpace(text) || text.Contains("<unknown", StringComparison.Ordinal))
            text = Safe.Read(() => general?.TranslateMod(values), null) ?? text;

        return string.IsNullOrWhiteSpace(text)
            ? $"{Safe.Read(() => mod.Key, "?")}: {string.Join(", ", values.Select(x => $"{x.Key}={x.Value}"))}"
            : text.Replace("\n", " / ");
    }
}
