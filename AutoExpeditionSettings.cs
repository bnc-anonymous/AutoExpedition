using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime;
using System.IO;
using System.Text.Json;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AutoExpedition;

/// <summary>
/// A heading inside a settings tab, drawn as `--- Runes ------------`.
///
/// The tabs themselves are the attribute menu's own collapsing submenus - one arrow per tab, which
/// is what the plugin has always had. What it has never had is any structure INSIDE a tab: Display
/// ran to thirty odd rows in one unbroken column, where the only way to find the reward colour was
/// to read every row above it.
///
/// So the sections are separators rather than more arrows. A tab is a place you go; a section is
/// something you scan past, and nesting a second arrow for four toggles makes you click to find out
/// there was nothing worth clicking for. This is the pattern RitualRollRanges uses and it reads
/// well there.
///
/// Used in Debug only. Display had thirteen of these and now has collapsed sections instead: at
/// sixty two settings the separators stopped being something to scan past and became a list to read
/// in full, which is the point at which an arrow earns its click. Debug is shorter and its headings
/// still do their job.
///
/// So the rule is about length, not about tabs. A heading suits a tab long enough to have two
/// subjects and short enough to take in at once; past that it wants sections. Adding one over the
/// only three things in a tab adds a line to read and separates nothing.
///
/// Declared as CustomNode properties because that is the only way to put something of your own into
/// the attribute menu, and marked JsonIgnore because a heading has nothing to save. They draw in
/// declaration order along with everything else, so a section heading is simply the property above
/// the first setting it covers.
/// </summary>
/// <summary>
/// Brings a saved settings file up to the current built-in configuration, once per change.
///
/// See AutoExpeditionSettings.ConfigVersion for why this exists rather than a changed default.
/// </summary>
internal static class Migrated
{
    /// <summary>Raise this and add a case below whenever a shipped value must reach saved files.</summary>
    public const int Current = 12;

    /// <summary>
    /// The weights whose number the plan actually took from the settings file, and the table row
    /// each answers to now.
    ///
    /// **Not every weight, because not every slider was in charge.** Weighing.Of has always asked
    /// the table for a thing's own weight, so a chest or a normal monster was already priced by its
    /// kind: row and the slider beside it was decoration - carrying those over would overwrite the
    /// table with a number that was never in effect. These are the ones where the opposite was
    /// true: a setting: id, which nothing ever looked up, or a kind: id the shipped table had no
    /// weight for, which fell through to the slider. See Migrated.Apply.
    /// </summary>
    private static readonly (string Property, string Id)[] Ruled =
    {
        // BossCave, EncasedShrine and KaruiGate are deliberately absent. Their rows are gone - all
        // three matched metadata fragments that appear nowhere in the game, so none of them ever
        // priced anything - and there is no honest target to carry three different numbers to now
        // that a sub-area entrance is one row. See Weighing.Entrance.
        ("BeastSkinRelic", "setting:BeastSkinRelic"),
        ("CagedRares", "setting:CagedRares"),
        ("GoblinRelic", "setting:GoblinRelic"),
        ("Hatch", "path:*SirenEgg*"),
        ("Henge", "setting:Henge"),
        ("KaruiTotemRelic", "setting:KaruiTotemRelic"),
        ("MagicMonster", "monster/magic"),
        ("MarkerMagic", "setting:MarkerMagic"),
        ("Sentry", "path:Metadata/MiscellaneousObjects/Sentinel/SentinelRandomEncounterObject"),
        // **One slider stood for six bases, so it migrates to all six.** The old "Strongbox" weight
        // was what every base except a Researcher's was priced at, so writing it to each of them
        // preserves exactly what it meant - it is not a guess about how to split a number, it is the
        // number it already was, applied where it already applied. See Weighing.RowOfStrongbox.
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/MartialStrongboxExpedition"),
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/ArmourerStrongboxExpedition"),
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/JewellerStrongboxExpedition"),
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/OrnateStrongboxExpedition"),
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/LargeStrongboxExpedition"),
        ("Strongbox", "path:Metadata/Chests/StrongBoxes/CasterStrongboxExpedition"),
        ("StrongboxExplicitMagicPack", "setting:StrongboxExplicitMagicPack"),
        ("StrongboxExplicitPack", "setting:StrongboxExplicitPack"),
        ("StrongboxExplicitRarePack", "setting:StrongboxExplicitRarePack"),
        ("StrongboxMagicPack", "setting:StrongboxMagicPack"),
        ("StrongboxPack", "setting:StrongboxPack"),
        ("StrongboxRarePack", "setting:StrongboxRarePack"),
        ("StrongboxResearcher", "path:Metadata/Chests/StrongBoxes/ResearchStrongboxExpedition"),

        // **StrongboxUnique is deliberately absent.** It carried onto the four *High bases, and
        // those rows are gone: a High strongbox is ordinary map content a dig site's blast
        // sometimes happens to reach, not content the chain should be routed towards. Carrying a
        // tuned number onto an id the table no longer ships would put the row straight back.
        ("SulphiteRelic", "setting:SulphiteRelic"),
        // A row each again. The three traps do share one relic modifier, which is why they were
        // collapsed into one row, but the tileset names them apart and the game labels them apart -
        // see RelicWeightFromMods - so each slider goes back to its own.
        ("WispPrimal", "setting:WispTrapPrimal"),
        ("WispVivid", "setting:WispTrapVivid"),
        ("WispWild", "setting:WispTrapWild"),
        ("WaveCount", "setting:WaveCount"),
        ("WaveMagic", "setting:WaveMagic"),
        ("WaveNormal", "setting:WaveNormal"),
        ("WaveRares", "setting:WaveRares"),
    };

    /// <summary>The companion values, which live on the same row rather than one of their own.</summary>
    private static readonly (string Property, string Id)[] Carried =
    {
        ("GoblinRelicCarries", "setting:GoblinRelic"),
        ("SulphiteRelicCarries", "setting:SulphiteRelic"),
        ("KaruiTotemRelicCarries", "setting:KaruiTotemRelic"),
        ("HengeCarries", "setting:Henge"),
    };

    /// <returns>What was changed, for the log. Empty when the file was already current.</returns>
    /// <param name="saved">
    /// The settings file this run was loaded from, so a value can be read after the property that
    /// held it has gone. Empty skips the weight migration rather than guessing at defaults.
    /// </param>
    public static string Apply(AutoExpeditionSettings settings, string saved = "")
    {
        if (settings == null || settings.ConfigVersion >= Current)
            return "";

        // **The file's own version outranks the one in memory, and this is not belt and braces.**
        //
        // A settings object that failed to load comes back as defaults, ConfigVersion included -
        // so it reads as 0 against a file that says 8, every step runs a second time, and the
        // result is written back over the file. That is not a migration failing to help, it is a
        // migration destroying the thing it exists to protect: steps that subtract a constant
        // subtracted it twice, turning a migrated nought into minus forty five.
        //
        // It happened. A settings node's type changed from float to int while a saved file held
        // 3.0, the load threw, and every value in the file was replaced by a default. The load is
        // the host's to get right; not making it worse is this method's.
        //
        // So a file claiming to be current is believed over the object, and nothing runs. The
        // settings in memory are wrong either way, and the file is the only copy of what is right.
        if (Already(saved) >= Current)
            return "";

        var was = settings.ConfigVersion;
        var said = new List<string>();

        // **1 - the two implicit pack counts stopped being probabilities**, and were lifted off
        // nought so a Researcher's six implicit RARE packs were not priced at nothing. It edited the
        // settings nodes, which no longer exist: the table ships both at one now, and 2 below is
        // what carries a value somebody actually chose. Kept as a numbered step so a file that
        // recorded version 1 is not run through 2 twice.

        // **2 - the weights moved into the reference table, and a tuned number must move with
        // them.** The table now carries every weight, its name and its built-in value, and it
        // answers ahead of the settings file - so a weight somebody had changed would be quietly
        // overruled by the shipped number on the next load. Anything that differs is written out as
        // a row of their own, which is what a disagreement has always been.
        //
        // Read out of the settings FILE rather than off the properties, because the properties are
        // going: a migration that needs them cannot outlive them. See Ruled.
        if (was < 2)
            said.AddRange(Moved(saved));

        // **3 - the solver's settings went into folders, and a saved value must follow them.**
        //
        // Forty-two of them moved from Solver into Solver.Timing, Solver.Presolve,
        // Solver.Advanced and the three folders under it, so every tuned number is at a path the
        // saved file has never heard of. Left alone, a file written before this would quietly
        // reset each one to its shipped default - the exact failure ConfigVersion exists to
        // prevent, and the one nobody notices, because a default is a plausible number.
        if (was < 3)
            said.AddRange(Refolded(settings.Solver, "Solver", saved, "solver"));

        // **4 - the display settings went into folders too, and the same rescue applies.**
        //
        // Sixty two of them moved from Display into Display.ThePlan, Display.ScoreArea,
        // Display.PlacementCircle, Display.Remnants and the sections under those, so every colour,
        // offset and thickness somebody had chosen is at a path the saved file has never heard of.
        //
        // Refolded is the version 3 step with the tab it reads passed in rather than written in,
        // which is what made this one entry rather than a second copy of it. Two of the settings
        // changed name in the same move; see Renamed.
        if (was < 4)
            said.AddRange(Refolded(settings.Display, "Display", saved, "display"));

        // **5 - the score area's Y offset stopped carrying the constant that positions it.**
        //
        // It shipped at 45, which was not a preference but the drop that puts the block above the
        // placement button - so the slider read as a position rather than as an adjustment, and
        // setting it back to nought moved the readout instead of resetting it. The 45 is in the
        // drawing now, and a value that came out of a saved file has it taken off so the block
        // stays exactly where its owner put it. See Overlay.ScoreAreaDrop.
        if (was < 5)
            said.AddRange(Lowered(settings, saved));

        // **6 - one Display setting was renamed after Display had already been nested.**
        //
        // Renamed above covers a file old enough to still hold its settings flat, because the flat
        // pass is what consults it. A file written since then has the old name at its new depth,
        // where nothing looks for it - so the value has to be fetched from the path it is actually
        // at. See CopiedFromPath.
        if (was < 6)
            said.AddRange(CopiedFromPath(saved, "Display.UnscoutedGround.ScoutNear",
                settings?.Display?.UnscoutedGround?.MarkerRadius, "the marker radius"));

        // **7 - the remnant lines joined the rewards they point at, and one colour was renamed.**
        //
        // Lines was its own section for four settings, beside a Rerolls section that also draws a
        // line - so "Line thickness" appeared twice under Remnants meaning two different marks.
        // They are the reward's own lines and they now sit with it.
        if (was < 7)
        {
            var rewards = settings?.Display?.Remnants?.Rewards;

            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Lines.LineAbove",
                rewards?.LineAbove, "the reward line threshold"));
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Lines.LineWithin",
                rewards?.LineWithin, "the reward line range"));
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Lines.LineColour",
                rewards?.LineColour, "the reward line colour"));
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Lines.LineThickness",
                rewards?.LineThickness, "the reward line thickness"));
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Rewards.ChosenColour",
                rewards?.OverruledColour, "the warning colour"));
        }

        // **8 - the reward label's offsets stopped carrying the position they were holding.**
        //
        // They shipped at 45 and -13, which put the text clear of the runes the game draws inside a
        // remnant's label. That is where the text belongs, not a preference - so the sliders read
        // as a position, and setting either back to nought moved the text instead of resetting it.
        // Both are Overlay constants now, and a value that came out of a saved file has them taken
        // off so nothing moves. Same shape as step 5, and for the same reason.
        if (was < 8)
            said.AddRange(Unanchored(settings, saved));

        // 9 - the flat blast circle switch was called AvoidHugging, which named neither the
        // circles nor what it does to them. See PlanDisplaySettings.FlatBlastCircles.
        if (was < 9)
            said.AddRange(CopiedFromPath(saved, "Display.ThePlan.AvoidHugging",
                settings?.Display?.ThePlan?.FlatBlastCircles, "the flat blast circle switch"));

        // 10 - colouring the reachable links green moved from the placement circle to the plan,
        // which is what it is about. See PlanDisplaySettings.ColourInRangeAsNext.
        if (was < 10)
            said.AddRange(CopiedFromPath(saved, "Display.PlacementCircle.ReadyEverywhere",
                settings?.Display?.ThePlan?.ColourInRangeAsNext, "the reachable colouring switch"));

        // 11 - the panel rectangle cache was called SnapshotMs behind a label describing what it
        // re-reads. It is a cache expiry. See PlacementCircleSettings.PanelCacheMs.
        if (was < 11)
            said.AddRange(CopiedFromPath(saved, "Display.PlacementCircle.SnapshotMs",
                settings?.Display?.PlacementCircle?.PanelCacheMs, "the panel cache interval"));


        settings.ConfigVersion = Current;

        return said.Count == 0 ? "" : string.Join("; ", said);
    }

    /// <summary>
    /// Copies a saved value onto the property it has since moved to.
    ///
    /// **By name, over the whole Solver tree, rather than from a hand-written table of paths.** A
    /// table would have forty-two entries, each of which is a chance to write one down wrong, and
    /// nothing would say which entry was the wrong one - a setting would simply be at its default
    /// and look as though nobody had changed it.
    ///
    /// Every node in the old file sat directly under Solver and every one of them still has that
    /// name somewhere beneath it, so the name is enough to find its new home. A name that no
    /// longer exists anywhere is a setting that was deleted rather than moved, and is skipped.
    /// </summary>
    /// <summary>
    /// The version recorded in the saved file, or nought when there is no readable file.
    ///
    /// Read straight out of the JSON rather than off the settings object, because the whole point
    /// is the case where the two disagree. See Apply.
    /// </summary>
    private static int Already(string saved)
    {
        if (string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return 0;

        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(saved));

            return file.RootElement.TryGetProperty("ConfigVersion", out var version) &&
                   version.TryGetInt32(out var number)
                ? number
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Copies one saved value onto a node that has since been renamed, naming where it used to be.
    /// </summary>
    /// <param name="was">
    /// Dotted path to the old node inside the saved file, ending at the node rather than at its
    /// Value - "Display.UnscoutedGround.ScoutNear". A path that is not there is a file that never
    /// held the setting, which is not an error.
    /// </param>
    /// <remarks>
    /// By exact path rather than by name, unlike Refolded. A name is enough when every candidate
    /// sat at one known depth; once the tree has sections, the same name can appear in more than
    /// one of them - Enable does, three times over - and a by-name search would write a value into
    /// whichever it reached first. A path cannot be ambiguous.
    /// </remarks>
    private static IEnumerable<string> CopiedFromPath(string saved, string was, object node, string what)
    {
        var said = new List<string>();

        if (node == null || string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return said;

        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(saved));

            var at = file.RootElement;

            foreach (var step in was.Split('.'))
            {
                if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(step, out var next))
                    return said;

                at = next;
            }

            // **A node is written as an object with a Value, EXCEPT a colour.** ColorNode
            // serialises as a bare "rrggbbaa" string with no wrapper, so asking every node for a
            // Value property finds nothing on a colour and carries it over as silence. Either
            // shape is accepted here and Assigned decides what it can do with it.
            var value = at.ValueKind == JsonValueKind.Object &&
                        at.TryGetProperty("Value", out var held) ? held : at;

            if (Assigned(node, value))
                said.Add($"carried {what} over from {was}");
        }
        catch (Exception)
        {
            // A file that cannot be read is one the plugin is about to overwrite with defaults
            // anyway. Nothing here is worth stopping a load for.
        }

        return said;
    }

    /// <summary>
    /// Takes the reward label's built-in position off its two saved offsets.
    /// </summary>
    /// <remarks>
    /// Only when there is a file to have read them from, for the reason Lowered gives: on a first
    /// run the noughts already mean the new thing, and subtracting would move the text on an
    /// install that had never chosen anything.
    /// </remarks>
    private static IEnumerable<string> Unanchored(AutoExpeditionSettings settings, string saved)
    {
        var said = new List<string>();
        var rewards = settings?.Display?.Remnants?.Rewards;

        if (rewards == null || string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return said;

        foreach (var (node, built, what) in new[]
                 {
                     (rewards.RewardOffsetX, Overlay.RewardLabelX, "X"),
                     (rewards.RewardOffsetY, Overlay.RewardLabelY, "Y"),
                 })
        {
            if (node == null)
                continue;

            var before = node.Value;

            node.Value = Math.Max(-500, Math.Min(500, before - built));

            if (node.Value != before)
                said.Add($"took the reward label's built-in {what} off its offset " +
                         $"({before} to {node.Value})");
        }

        return said;
    }

    /// <summary>
    /// Takes the score area's layout constant off a saved Y offset.
    /// </summary>
    /// <remarks>
    /// Only when there is a file to have read it from. A first run has no saved value, so its
    /// nought is already the new meaning of nought and subtracting would push the readout up by 45
    /// on an install that had never chosen anything - which is the shape of mistake every step
    /// here guards against by asking for the file first.
    /// </remarks>
    private static IEnumerable<string> Lowered(AutoExpeditionSettings settings, string saved)
    {
        var said = new List<string>();
        var offset = settings?.Display?.ScoreArea?.ScoreAreaY;

        if (offset == null || string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return said;

        var was = offset.Value;

        offset.Value = Math.Max(-400, Math.Min(400, was - Overlay.ScoreAreaDrop));

        if (offset.Value != was)
            said.Add($"took the score area's built-in drop off its Y offset ({was} to {offset.Value})");

        return said;
    }

    /// <summary>Settings that were renamed in the same move that nested them, old name to new.</summary>
    private static readonly Dictionary<string, string> Renamed = new()
    {
        // The score area's offsets were called Status X and Y, which named neither the element they
        // move nor the setting - and the status LINE is a different thing entirely, drawn under the
        // debug counts column. See ScoreAreaSettings.
        ["StatusX"] = "ScoreAreaX",
        ["StatusY"] = "ScoreAreaY",

        // "Search" named a verb the solver had just stopped using, and said nothing
        // about a radius. See UnscoutedDisplaySettings.MarkerRadius.
        ["ScoutNear"] = "MarkerRadius",
    };

    private static IEnumerable<string> Refolded(object into, string root, string saved, string what)
    {
        var said = new List<string>();

        if (into == null || string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return said;

        JsonElement tab;

        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(saved));

            if (!file.RootElement.TryGetProperty(root, out var found))
                return said;

            tab = found.Clone();
        }
        catch (Exception)
        {
            return said;
        }

        var moved = 0;

        foreach (var node in tab.EnumerateObject())
        {
            // Only what sat directly under the tab as a node with a Value. Anything already nested
            // is a file written after the move and needs nothing doing to it.
            if (node.Value.ValueKind != JsonValueKind.Object ||
                !node.Value.TryGetProperty("Value", out var value))
                continue;

            if (Rehomed(into, Renamed.TryGetValue(node.Name, out var now) ? now : node.Name, value))
                moved++;
        }

        if (moved > 0)
            said.Add($"moved {moved} {what} setting(s) into their new sections");

        return said;
    }

    /// <summary>
    /// Finds a property of this name anywhere under <paramref name="within"/> and sets its Value.
    ///
    /// Breadth first, so a name that exists at two depths takes the shallower one - which is the
    /// answer a person would give, and no name in this tree is ambiguous anyway.
    /// </summary>
    private static bool Rehomed(object within, string name, JsonElement value)
    {
        var queue = new Queue<object>();

        queue.Enqueue(within);

        for (var seen = 0; queue.Count > 0 && seen < 64; seen++)
        {
            var at = queue.Dequeue();

            foreach (var property in at.GetType().GetProperties())
            {
                object held;

                try
                {
                    held = property.GetValue(at);
                }
                catch (Exception)
                {
                    continue;
                }

                if (held == null)
                    continue;

                if (string.Equals(property.Name, name, StringComparison.Ordinal) &&
                    Assigned(held, value))
                    return true;

                // Only this plugin's own settings classes are walked. Nodes carry a Value and are
                // the leaves; anything from the host or the framework is not ours to recurse into.
                if (held.GetType().Namespace == typeof(SolverSettings).Namespace &&
                    held.GetType().IsClass)
                    queue.Enqueue(held);
            }
        }

        return false;
    }

    /// <summary>Sets a node's Value from the saved json, where the two agree about type.</summary>
    private static bool Assigned(object node, JsonElement value)
    {
        var slot = node.GetType().GetProperty("Value");

        if (slot == null || !slot.CanWrite)
            return false;

        try
        {
            object set = slot.PropertyType switch
            {
                var t when t == typeof(bool) && value.ValueKind is JsonValueKind.True
                    or JsonValueKind.False => value.GetBoolean(),
                var t when t == typeof(int) && value.TryGetInt32(out var i) => i,
                var t when t == typeof(float) && value.TryGetDouble(out var f) => (float)f,
                var t when t == typeof(string) && value.ValueKind == JsonValueKind.String
                    => value.GetString(),

                // Colours are stored as eight hex digits in RRGGBBAA order - alpha LAST, which is
                // the opposite of the argument order Color.FromArgb takes, and the easiest thing
                // in this method to get quietly backwards.
                var t when t == typeof(Color) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: 8 } text &&
                    uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out var packed)
                    => Color.FromArgb((byte)packed, (byte)(packed >> 24),
                        (byte)(packed >> 16), (byte)(packed >> 8)),

                _ => null,
            };

            if (set == null)
                return false;

            slot.SetValue(node, set);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes out every weight in the settings file that disagrees with the shipped table.
    ///
    /// The file is read as text rather than deserialised: the properties it names are being deleted,
    /// so there is nothing left to bind it to, and all that is wanted is one number per name.
    /// </summary>
    private static IEnumerable<string> Moved(string saved)
    {
        var said = new List<string>();

        if (string.IsNullOrEmpty(saved) || !File.Exists(saved))
            return said;

        string text;

        try
        {
            text = File.ReadAllText(saved);
        }
        catch (Exception)
        {
            return said;
        }

        foreach (var (property, id) in Ruled)
        {
            var value = Number(text, property);

            if (value == null)
                continue;

            // **A row of their own is the later answer and outranks the slider.** Somebody who has
            // already written a weight into the table did so knowing what it was for; carrying the
            // settings number over the top would undo that edit in the name of preserving it.
            if (Wrt.Status(id) == "custom")
                continue;

            var shipped = Wrt.Of(id)?.Weight;

            if (shipped != null && Math.Abs(shipped.Value - value.Value) < 0.0001f)
                continue;

            var was = value.Value;

            Wrt.Set(id, row => row.Weight = was);
            said.Add($"{Wrt.Label(id)} {was:0.###}");
        }

        foreach (var (property, id) in Carried)
        {
            var value = Number(text, property);

            if (value == null)
                continue;

            // **Nothing to migrate into any more.** This carried a tuned percentage out of the
            // old settings node and into the row's Carries cell, which was retired with the rest
            // of v1 - what a row passes on is its effect now, and an effect states a target as
            // well as a number, which a bare percentage from a slider cannot. A figure still
            // sitting in an old settings file is reported rather than written, so it can be put
            // into the Effect column by somebody who knows what it was meant to reach.
            said.Add($"{Wrt.Label(id)} had {value.Value:0.###} in the old settings - write it into " +
                     "the row's Effect cell if it is still wanted");
        }

        if (said.Count > 0)
            Wrt.Keep();

        return said;
    }

    /// <summary>The Value of one named node in a settings file, or null when it says nothing.</summary>
    private static float? Number(string text, string property)
    {
        var at = text.IndexOf("\"" + property + "\"", StringComparison.Ordinal);

        if (at < 0)
            return null;

        var mark = text.IndexOf("\"Value\"", at, StringComparison.Ordinal);

        if (mark < 0)
            return null;

        // Only inside this node: a property with no Value of its own would otherwise pick up the
        // next node's, which is a silent wrong number rather than a missing one.
        var end = text.IndexOf('}', at);

        if (end >= 0 && mark > end)
            return null;

        var from = text.IndexOf(':', mark);

        if (from < 0)
            return null;

        var stop = from + 1;

        while (stop < text.Length && (char.IsDigit(text[stop]) || text[stop] == '.' ||
                                      text[stop] == '-' || text[stop] == '+' ||
                                      char.IsWhiteSpace(text[stop])))
            stop++;

        return float.TryParse(text[(from + 1)..stop].Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

internal static class Section
{
    public static CustomNode Of(string title) => new CustomNode(() => ImGui.SeparatorText(title));

    /// <summary>
    /// A heading with no rule drawn through it.
    ///
    /// SeparatorText puts a line either side of the words, which reads as a divider between two
    /// unrelated things. On a tab whose sections are four views of one decision - what a reward is
    /// worth, when to insist on it, when to roll it away - the lines cut up something continuous.
    /// </summary>
}

/// <summary>
/// What the five words the tearing mix accepts actually do.
///
/// **A name alone cannot carry this choice.** "worst" and "reach" are equally plausible words to
/// type and they behave nothing alike - one cuts dead weight out of a route and always improves it
/// slightly, the other gambles a router call on bridging to content the chain never reached and
/// comes back empty more than half the time. Which of those eight threads should be spent on is a
/// judgement about the site in front of you, and it cannot be made from the names.
///
/// So each gets what it is good at, what it is bad at, and what it costs, which is the shape of the
/// comparison somebody is actually making. The costs are relative on purpose: the absolute numbers
/// move with the site and the dump reports the real ones per run, under "what the last run did".
/// </summary>
internal static class Mixes
{
    public static void Draw()
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(i)");

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 54f);

        Wrapped(
            "Rated from ten runs of one site: 37,361 attempts, 48 new bests. Yours will differ - "
            + "reach in particular is worth everything or nothing depending on how spread out the "
            + "site is. Your own numbers are in the dump under \"destroy and repair\".");

        Line("even",
            "No lean. Every destroy starts equal and the adaptive weights sort them out from what "
            + "they return, which is the right answer whenever they are given enough evidence to "
            + "sort.",
            "Rarely given it. New bests land about once per 800 attempts, so for most of a window "
            + "every rate sits near the floor, the weights never separate, and eight even threads "
            + "run one experiment eight times over.",
            "Free - it is the absence of a choice.");

        Line("seg",
            "Tears out a random run of consecutive links, so it moves whole stretches of route "
            + "rather than details. The best keep rate of the four, 9.7% accepted, and the one that "
            + "changes a chain's SHAPE - which is what gets a search out of a rut.",
            "Blind about what it removes, so it wins least often per attempt: 0.70 new bests per "
            + "thousand tries, the lowest of the four.",
            "Cheapest - it picks what to remove without scoring anything.");

        Line("worst",
            "Tears out the links worth least where they stand, so the repair starts with the dead "
            + "weight already gone. Wins slightly more often than seg or rel, 0.97 per thousand.",
            "Deterministic, so it keeps proposing the same tear and converges: only 3.5% of its "
            + "attempts were accepted, a third of what seg and rel manage. It never removes anything "
            + "good, so it cannot cross a valley to a better route on the far side.",
            "Dear among the three plain ones - it scores the chain once per link, so its cost grows "
            + "with the number of explosives.");

        Line("rel",
            "Tears out links that sit near each other, so the repair re-routes a neighbourhood "
            + "instead of shuffling one bomb. Shaw's relatedness, the operator that made this family "
            + "of searches work; 9.1% accepted, near seg.",
            "Degenerates on a spread-out chain: where no two links are close, the ones nearest a "
            + "pick are just the ones beside it, and it becomes seg with extra steps. Fewest wins "
            + "per attempt on this site at 0.59 per thousand.",
            "Cheap - one sort of the chain by distance, no scoring.");

        Line("reach",
            "The only one that aims at content the chain is MISSING, bridging out to a rich target "
            + "the route never got to, so it can ADD coverage where the other three can only "
            + "rearrange. Per attempt it is far the strongest: 2.54 new bests per thousand, three to "
            + "four times any of the others.",
            "Per second it is far the weakest - 0.29 wins a second against 0.78 for the other three "
            + "together - because it costs nine times more per attempt, and a fixed window spends "
            + "seconds, not attempts. Half its attempts arrive nowhere. Left unranked it took 79% of "
            + "the tearing budget for a quarter of the wins.",
            "Dearest by far, about 8.8ms an attempt against 1ms: every one runs the router, whether "
            + "it arrives or not. Rank by time is what stops that eating a window.");

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void Line(string name, string pros, string cons, string cost)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(name);
        Wrapped($"Pros: {pros}");
        Wrapped($"Cons: {cons}");
        Wrapped($"Cost: {cost}");
        ImGui.Spacing();
    }

    /// <summary>
    /// Wrapped text that is text rather than a format string.
    ///
    /// **TextWrapped is printf and these lines are full of per cent signs.** Every rate here is
    /// written as "9.7% accepted" or "79% of the tearing budget", and ImGui read each one as a
    /// conversion directive - so the tooltip came out garbled, in a way that gets worse the more
    /// evidence you put in it.
    ///
    /// TextUnformatted takes no format string and still honours the wrap position pushed around it,
    /// which is the whole of what TextWrapped was wanted for. Use it for anything whose words are
    /// not under this file's control - which, once a number can appear in them, is all of them.
    /// </summary>
    private static void Wrapped(string says) => ImGui.TextUnformatted(says);
}

public class AutoExpeditionSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Which built-in configuration this saved file has already had applied. See Migrated.
    ///
    /// **A saved file never picks up a changed default**, because it already holds a value for every
    /// property - so shipping a new number changes nothing for anybody who has run the plugin once.
    /// That is usually right and occasionally wrong: when a setting's MEANING changes, the saved
    /// number is an answer to a question no longer being asked.
    ///
    /// Not a button the user has to find and press. It applies once on load and says what it did.
    /// </summary>
    [IgnoreMenu]
    public int ConfigVersion { get; set; }

    /// <summary>
    /// Whether the weight reference table is on screen. See Catalogue.
    ///
    /// **Not a debug tool, which is where it started.** It is the answer to "what does this thing
    /// count for and how big does the planner think it is", and asking somebody to switch on debug
    /// mode to look that up is exactly the sort of hunt the table exists to end. It sits at the top
    /// of the menu because it is a thing you open, not a thing you configure.
    ///
    /// Hidden from the menu itself: the row above is a button, and a checkbox beside it saying the
    /// same thing twice is clutter. Saved, so a window left open comes back open.
    /// </summary>
    [IgnoreMenu]
    public ToggleNode ShowCatalogue { get; set; } = new ToggleNode(false);

    /// <summary>
    /// The button that opens it, and the binding beside it.
    ///
    /// Two rows rather than the one they would ideally share: the key capture widget belongs to the
    /// settings parser and is drawn from the property below, so a hand-drawn row cannot hold it
    /// without losing the capture.
    /// </summary>
    [JsonIgnore]
    public CustomNode EntityTableUi { get; set; } = new CustomNode(() =>
    {
        if (ImGui.Button(Catalogue.Open ? "Close Weight Reference Table###aeOpenTable"
                : "Open Weight Reference Table###aeOpenTable"))
            Catalogue.Toggle();

        ImGui.SameLine();
        ImGui.TextDisabled(Catalogue.Says);
    });

    [Menu("Open Weight Reference Table key",
        "Recommended: F2, or unbound and click the above button. You\n" +
        "shouldn't need to open this very often.\n" +
        "\n" +
        "Opens the Weight Reference Table, a searchable list of everything\n" +
        "the plugin knows about expedition content. Each row lists what\n" +
        "something is, what it's worth, what it modifies, what can modify\n" +
        "it, among other things. Right click a row for additional\n" +
        "information.\n" +
        "\n" +
        "If you encounter an entity that the plugin doesn't know about, you\n" +
        "can modify its settings in the table as you see fit.")]
    public HotkeyNodeV2 TableHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// The one key for playing a dig site: it stops a search, solves, places, picks rewards and
    /// clears up afterwards, choosing by what is in front of it.
    ///
    /// Named for what it is rather than what it does, because it is a binding and the row next to
    /// it is a key.
    ///
    /// Two of its branches are not guessable and so are the ones the tooltip leads with. Pressing
    /// while a search is running stops it and keeps the best chain, because the same key starting
    /// and stopping is what a player expects of something visibly working. Pressing with the cursor
    /// on the game's own explosive placement button searches again instead of placing, because that
    /// is where the hand already is when the chain has moved on. See Detonator.OverToggle.
    ///
    /// "Solve deeper" is literal: Start seeds the new search with the standing chain when the site
    /// is the same one and every spot in it is still placeable, so a second press continues from
    /// that chain rather than beginning again. It does begin again when the chain has been dropped,
    /// which happens when explosives have gone down away from the plan or a must-take has been
    /// added that the chain misses.
    ///
    /// A reward window outranks planning, which looks wrong until the case that needs it: a remnant
    /// standing on its own belongs to no expedition, so there is no chain to plan and the key would
    /// otherwise go off to solve a dig site elsewhere on the map.
    /// </summary>
    [Menu("Action key",
        "Recommended: F4\n" +
        "\n" +
        "All in one button. Press to stop solving. If solving is stopped,\n" +
        "press while hovering the explosive placement button to solve deeper.\n" +
        "Once a plan is in place and solving is stopped, walk into range of\n" +
        "bombs and press to automatically pick rewards and place explosives.\n" +
        "Once the expedition has started, press to shatter remnants and open\n" +
        "excavated chests.")]
    public HotkeyNodeV2 ActionHotkey { get; set; } = new HotkeyNodeV2(Keys.F4);

    /// <summary>
    /// Cycles the marker under the cursor: must take, must avoid, neither.
    ///
    /// A weight applies to every marker of a kind, and the answer is sometimes about one marker on
    /// one site - a rare that spawns something this character cannot fight, a chest in a corner
    /// nobody intends to walk back to. See Insisted.
    ///
    /// Lives beside the action key rather than among the debug tools, where it started. It is
    /// pressed while playing, and behind the debug switch it did nothing at all while the marks it
    /// makes carried on being drawn, so the feature looked wired up and never fired.
    ///
    /// Takes effect on the next solve, since the chain it changes is worked out there. A mark is
    /// drawn green for take and red for avoid, chosen in Overlay rather than here - the tooltip
    /// names both colours, so changing one there means changing the tooltip too.
    ///
    /// The cursor test is in screen pixels rather than grid units, because two markers far apart
    /// on the ground can be a few pixels apart on an angled map, and the pixels are what the
    /// player is aiming with. See Insisted.Under.
    /// </summary>
    [Menu("Must take / must avoid toggle key",
        "Recommended: F3\n" +
        "\n" +
        "Press while hovering an expedition entity to cycle through three\n" +
        "states: \"Must take\" (green), \"Must avoid\" (red), and no preference.\n" +
        "This applies only to the thing you marked, not every occurrence of\n" +
        "it. Can be used to force the solver to path a certain way.")]
    public HotkeyNodeV2 InsistHotkey { get; set; } = new HotkeyNodeV2(Keys.F3);

    /// <summary>
    /// The game's own key for showing the placement circle, not a key this plugin listens to.
    ///
    /// Tapped rather than watched, so it has to match whatever the key is bound to under
    /// Options, Input, League Interface - and it is a setting because that is rebindable. A
    /// mismatch is silent: the tap goes out on the wrong key and the circle never appears, which
    /// reads as the game ignoring the plugin. See Placement.Cleared.
    /// </summary>
    [Menu("Placement key, as bound in the game",
        "Escape > Options > Input > League Interface\n" +
        "\n" +
        "The plugin presses this key itself to bring the explosive\n" +
        "placement circle up. It has to match what the game has it bound\n" +
        "to, or the wrong key goes out and nothing is placed.")]
    public HotkeyNodeV2 ToggleKey { get; set; } = new HotkeyNodeV2(Keys.V);

    /// <summary>
    /// The game's own key for hiding ground labels, not a key this plugin listens to.
    ///
    /// Tapped rather than watched, so it has to match whatever Toggle Highlighting is bound to in
    /// the game's options - and it is a setting because that is rebindable. See
    /// AutomationSettings.Unhide for when it is used.
    /// </summary>
    [Menu("Toggle Highlighting key, as bound in the game",
        "Escape > Options > Input > Toggle Highlighting\n" +
        "\n" +
        "The plugin presses this key itself to clear ground labels when\n" +
        "they are covering a button it needs to click. It has to match\n" +
        "what the game has it bound to, or the wrong key goes out and the\n" +
        "button stays covered.")]
    public HotkeyNodeV2 HighlightKey { get; set; } = new HotkeyNodeV2(Keys.Z);

    // Attributed like every other root section. Without this the class is not a section at
    // all: its contents draw inline among the hotkeys above, so there is no Automation row
    // to open and nothing saying where those switches belong.
    [Submenu(CollapsedByDefault = true)]
    public AutomationSettings Automation { get; set; } = new AutomationSettings();

    [Submenu(CollapsedByDefault = true)]
    public DisplaySettings Display { get; set; } = new DisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public RewardSettings Rewards { get; set; } = new RewardSettings();

    [Submenu(CollapsedByDefault = true)]
    public BugsSettings Bugs { get; set; } = new BugsSettings();

    /// <summary>
    /// What everything is worth. Edited in the weight reference table, not here.
    ///
    /// **The tab is gone and the data is not.** Forty six sliders in two sub-tabs, a rune table and
    /// a discovered-objects list, all describing the same question - what is this thing worth - in
    /// four different shapes, none of which could say what a thing IS or what a modifier reaches.
    /// The table answers it in one place, with a row per thing and a column per question, and it
    /// writes to these same nodes: one number, one home, two views retired down to one.
    ///
    /// **A store, not a tab.** Nothing renders from here any more: the reset button and the
    /// discovered-objects panel are gone with the rest of it, since the table carries both. What is
    /// left is the numbers themselves, which stay exactly where they are - every one of these is the
    /// property a settings file is keyed on, and moving or deleting them would hand somebody the
    /// defaults back in place of the weights they tuned.
    ///

    /// <summary>
    /// Relic modifiers this character refuses, whatever the relic is worth. See MustAvoidMods.
    /// </summary>
    /// <remarks>
    /// **A self-drawing submenu rather than a CustomNode, which is a layout fix.** The settings
    /// parser spaces a submenu differently from an ordinary row, so a tree node drawn by hand
    /// among real submenus sat closer to the arrow above it than the arrows sat to each other.
    ///
    /// RenderMethod with EnableSelfDrawCollapsing is the engine's own answer: a submenu as far as
    /// the layout is concerned, drawing its own node, which is what keeps the live count in the
    /// title. ExileCore2 uses it for the plugin folder list and the notification categories.
    /// </remarks>
    [Submenu(RenderMethod = "Render", CollapsedByDefault = true,
        EnableSelfDrawCollapsing = true)]
    public class MustAvoidModsPanel
    {
        public void Render()
        {
            // The same tree node the rest of this column uses, and the same ### identity so that
            // ticking a box does not fold the section away. See UnknownEntities.
            if (!ImGui.TreeNodeEx(
                    $"Must avoid mods ({MustAvoidMods.Banned} refused of {MustAvoidMods.Count} seen)###mustAvoidMods",
                    ImGuiTreeNodeFlags.SpanAvailWidth))
                return;

            try
            {

            if (MustAvoidMods.Count == 0)
            {
                ImGui.TextDisabled("No relic modifiers seen yet. Every one the game offers will appear");
                ImGui.TextDisabled("here on its own once you walk past a relic carrying it.");

                return;
            }

            ImGui.TextDisabled("A ticked modifier is never taken: any relic carrying it is planned");
            ImGui.TextDisabled("around exactly as if you had marked it must-avoid by hand.");

            // Drawing is off by default - see MustAvoidMods.DrawInWorld. Kept at the top because they
            // are about the whole list rather than about any row in it.
            var world = MustAvoidMods.DrawInWorld;
            var map = MustAvoidMods.DrawOnMap;

            if (ImGui.Checkbox("Draw on world", ref world))
            {
                MustAvoidMods.DrawInWorld = world;
                MustAvoidMods.Keep();
            }

            ImGui.SameLine();

            if (ImGui.Checkbox("Draw on map", ref map))
            {
                MustAvoidMods.DrawOnMap = map;
                MustAvoidMods.Keep();
            }

            ImGui.Separator();

            // Which heading has been written, so each one appears once and only above rows that
            // follow it. See MustAvoidMods.All for the order these arrive in.
            var written = -1;

            foreach (var (mod, refused, _) in MustAvoidMods.All)
            {
                var band = MustAvoidMods.Immune(mod) ? 0 : 1;

                if (band != written)
                {
                    if (written >= 0)
                        ImGui.Separator();

                    ImGui.TextDisabled(band == 0
                        ? "Immunities - what stops a build killing anything"
                        : "Other drawbacks");

                    written = band;
                }

                ImGui.PushID(mod);

                var ban = refused;

                if (ImGui.Checkbox(Unknowns.Effect(mod), ref ban))
                    MustAvoidMods.Decide(mod, ban);

                // The id itself, since the words are a tidied version of it and two mods can tidy
                // down to nearly the same phrase.
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(mod);

                ImGui.PopID();
            }

            ImGui.Separator();

            if (ImGui.SmallButton("Forget all"))
                MustAvoidMods.ForgetAll();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Clears the list and every ban on it.\n" +
                                 "It fills itself again from the next relic you walk past.");
            }

            }
            finally
            {
                ImGui.TreePop();
            }
        }
    }

    /// <summary>
    /// Relic modifiers this character refuses, whatever the relic is worth. See MustAvoidMods.
    /// </summary>
    /// <remarks>JsonIgnore because the bans live in MustAvoidMods - the panel holds nothing.</remarks>
    [JsonIgnore]
    public MustAvoidModsPanel MustAvoidModifiers { get; set; } = new MustAvoidModsPanel();

    [Submenu(CollapsedByDefault = true)]
    public SolverSettings Solver { get; set; } = new SolverSettings();

    /// <summary>
    /// Independent of Debug, so a tester can gather readings without playing with debug drawing on.
    /// See RecordingSettings.
    /// </summary>
    [Submenu(CollapsedByDefault = true)]
    public RecordingSettings Recording { get; set; } = new RecordingSettings();

    [Submenu(CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new DebugSettings();

    public AutoExpeditionSettings() => Debug.Root = this;
}

/// <summary>
/// How long the search is allowed to think, and what makes it stop.
///
/// Only that. What the plugin DOES with an answer - places it, picks the rewards, clears up
/// afterwards - is under Automation, because those are the switches with consequences and they
/// belong together. This tab decides how good the answer is before any of that happens.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class SolverSettings
{
    [JsonIgnore]
    /// <summary>
    /// **The four things anybody changes sit at the top and the rest is behind Advanced.**
    /// The solver had forty-nine settings under six rules-drawn headings, all at one depth,
    /// and a flat list of that length says every one of them is a decision somebody ought to
    /// make. They are not. Threads, the timing, the presolve and - when it lands - the reroll
    /// loop are what a player tunes; the other forty are a bench for measuring the search.
    /// </summary>
    /// <summary>
    /// How many copies of the search run at once, each from its own random stream.
    ///
    /// **This was one, and one was never a decision.** ExpeditionIcons runs five independent
    /// populations and sums their generations; RuneHighlighter runs two. On an identical window that
    /// is five times the searching, and a comparison against either of them was measuring a fifth of
    /// this plugin.
    ///
    /// Destroy and repair is a trajectory method, which is exactly the shape that wants this:
    /// independent runs from different random streams, and the best of them taken. The modes that
    /// were deterministic and ignored it - Beam and Edge only - are gone.
    ///
    /// **Five at the bottom of the range rather than one.** Below five the plugin is measurably
    /// worse than the plugins it is compared against for no reason a player would choose, and the
    /// range says so instead of letting somebody wonder.
    ///
    /// **Eight by default, and the argument against it is worth stating rather than assuming.**
    /// The search threads and the game want the same cores - Path of Exile is running on this
    /// machine too - so every thread here is one the game is not getting, and the cost of taking
    /// too many is a stutter while you are standing in the dig site. That is the whole of the
    /// case, and the previous note gave only its conclusion: "four rather than five, because the
    /// game is on the same machine", which names a fact and leaves the reader to infer the rest.
    ///
    /// It does not hold at this size. A solve runs for a few seconds and stops when it stops
    /// improving, so the threads are not resident - they take the machine in bursts while you walk
    /// in, and are gone before you place anything. Against that, a thread count set low is a worse
    /// plan on every site for ever, which is not a trade a default should make quietly.
    ///
    /// **What has NOT been measured is the game's own frame rate at eight.** The frame work this
    /// plugin has is about its own frames, and 14 over 20ms in one window was put down to an
    /// entity walk rather than to the search. If eight ever reads as stutter in the game, that is
    /// the measurement to take, and the range goes down to five. See Solving.
    /// </summary>
    [Menu("Threads")]
    public RangeNode<int> Threads { get; set; } = new RangeNode<int>(8, 5, 8);

    [Submenu(CollapsedByDefault = true)]
    public SolverTimingSettings Timing { get; set; } = new SolverTimingSettings();

    [Submenu(CollapsedByDefault = true)]
    public PresolveSettings Presolve { get; set; } = new PresolveSettings();

    [Submenu(CollapsedByDefault = true)]
    public RerollSettings Reroll { get; set; } = new RerollSettings();

    [Submenu(CollapsedByDefault = true)]
    public AdvancedSolverSettings Advanced { get; set; } = new AdvancedSolverSettings();

    /// <summary>
    /// Which searches the cold key compares, ticked one by one.
    ///
    /// **Six strategies is six windows, and most of the time the question is about two of them.** A
    /// bake-off runs each one for the whole improvement window in turn, so on a Grand site at eight
    /// seconds a full run is the better part of a minute of standing still - and four fifths of it
    /// is usually spent re-confirming modes nobody is asking about. Ticking two makes the same
    /// comparison in a third of the time, which is the difference between a measurement taken
    /// several times and one taken once.
    ///
    /// Keyed by the strategy name rather than held as one toggle per mode, so a seventh search
    /// appears in the list the day it is written and defaults to being compared. A name the
    /// dictionary has never seen reads as ticked.
    /// </summary>
    public Dictionary<string, bool> Compared = new();

    /// <summary>
    /// The searches, in the order they are offered and the order a comparison tries them.
    ///
    /// **One list, because there were two and they disagreed.** The dropdown was built from its own
    /// literal in the plugin's Initialise and this array drove the comparison, so the same six modes
    /// appeared in two orders in two files, and a seventh would have had to be added to both.
    ///
    /// Ordered by how likely a search is to be wanted rather than by when it was written, so the
    /// default sits at the top of the dropdown and a comparison reports it first.
    /// </summary>
    public static readonly string[] Comparable = { DestroyRepair };

    /// <summary>Whether <paramref name="strategy"/> is ticked. Anything unheard of is.</summary>
    public bool Compares(string strategy) =>
        !Compared.TryGetValue(strategy, out var ticked) || ticked;




    /// <summary>
    /// Tear a stretch out of the chain and rebuild it, repeatedly. See Repair.
    ///
    /// The move every other mode here lacks: a neighbourhood of chains that agree with this one
    /// everywhere outside a torn run, which is exponentially many and none of them reachable by
    /// moving one link.
    /// </summary>
    public const string DestroyRepair = "Destroy and repair";



}

/// <summary>
/// What the plugin does on its own, as opposed to what it draws or how it decides.
///
/// Its own tab because it is the part with consequences. Everything under Display and Weights
/// changes what you are told; everything under here moves the cursor and clicks - so it wants to be
/// somewhere a player can find the whole of it at once and turn any of it off, rather than one
/// toggle here and another among the placement tuning.
/// </summary>
[Submenu(CollapsedByDefault = true)]
/// <summary>
/// How long a search is given, and what counts as having stopped improving.
/// </summary>
public class SolverTimingSettings
{
    /// <summary>
    /// How long with nothing better found before the search gives up - the "n" in the mode above.
    ///
    /// The countdown over the placement button is this window, and it starts again every time a
    /// better chain turns up - so a bar that keeps refilling is the search still earning its time,
    /// and one that empties is it having run out of ideas.
    /// </summary>
    [Menu("Time to improve (ms)")]
    public RangeNode<int> SettleMs { get; set; } = new RangeNode<int>(4000, 0, 30000);

    /// <summary>
    /// The same, on a Grand Expedition, because it is not the same problem.
    ///
    /// **Fifteen links is three times the chain and far more than three times the search.** The
    /// pairs a search reasons about grow with the square of the links and the ground it has to learn
    /// grows with the area the chain can cover, so a window that settles a five bomb site leaves a
    /// fifteen bomb one still climbing. Measured: the same site and strategy scored 2,218 in a four
    /// second window and 9,127 given sixteen.
    ///
    /// Separate rather than scaled, because the two are used differently. A small site is solved
    /// while you are standing over the detonator deciding; a Grand one is solved once, after a lap
    /// that took minutes, and a few more seconds against that is nothing.
    /// </summary>
    [Menu("Time to improve in a Grand Expedition (ms)")]
    public RangeNode<int> SettleMsGrand { get; set; } = new RangeNode<int>(8000, 100, 30000);

    /// <summary>
    /// How much better a chain has to be before it counts as worth waiting for.
    ///
    /// In score, the same units as the readout - so what it means depends on the weights, and the
    /// range is wide because those can be. At the shipped weights a chest is eight and a dig site
    /// a couple of hundred; turned up, a chain runs to thousands and this wants turning up with it.
    ///
    /// It decides whether the clock RESTARTS, not whether the chain is kept - anything better is
    /// always kept, however slightly. And it measures from the last restart rather than from the
    /// last improvement, so a run of small gains that add up still counts: cumulative progress is
    /// progress, and a search grinding out a point at a time towards something real should not be
    /// cut off for taking small steps.
    ///
    /// Zero, so every improvement counts, which is how the mode behaved before this existed.
    /// </summary>
    [Menu("Minimum improvement")]
    public RangeNode<float> MinImprovement { get; set; } = new RangeNode<float>(0f, 0f, 2000f);

    /// <summary>
    /// The longest any one press may run for, whatever it is still finding. Nought is no limit.
    ///
    /// **The ceiling used to be ten times the improvement window and nobody could see it.** It was
    /// a constant multiplied into a budget in the middle of Planning, so the only way to make a
    /// search stop sooner was to shorten the window it uses to decide it has stopped improving -
    /// two different questions answered by one number.
    ///
    /// Nought by default, which means the improvement window is the only thing that stops a solve.
    /// That is the honest default for a search that runs off the frame and that nothing waits for:
    /// it stops when it has run out of ideas, which is exactly when there is nothing left to gain by
    /// waiting. Set it when you would rather have an answer by a particular moment than the best one
    /// the site has.
    /// </summary>
    [Menu("Maximum time (ms, 0 = unlimited)")]
    public RangeNode<int> MaxSolveMs { get; set; } = new RangeNode<int>(0, 0, 30000);

}

/// <summary>
/// Solving the site while you walk towards it, and what it may interrupt. See Rehearsal.
/// </summary>
public class PresolveSettings
{
    /// <summary>
    /// Whether the ground is walked and remembered on the way to the detonator.
    ///
    /// **The one thing a solve cannot make up for later.** Working out whether a link fits costs a
    /// flood of the ground around it, and a search on a cold site spends a third of every window
    /// doing that and still has most of its questions come back unanswered - measured on a Grand
    /// site, thirty thousand asked and twenty thousand refused, which is a planner reasoning about a
    /// dig it was never allowed to look at. Learning the whole of it costs about four seconds, once.
    ///
    /// There is no reason for those four seconds to be inside the first press when the walk to the
    /// detonator is minutes long. So they are not.
    ///
    /// **Its own switch rather than sharing Presolve's**, because the two cost quite different
    /// things. A presolve runs whole searches on the way in and wants the machine; this floods
    /// ground on one fewer thread than the machine has, leaving one for the game, and then stops
    /// forever - the answers are facts about terrain that does not move, so nothing is ever redone.
    ///
    /// On by default, which Presolve is not, for that reason.
    ///
    /// **Presolve waits for it, which is why this sits above Presolve in the menu.** A rehearsal on
    /// half-learnt ground is the cold router problem with a longer memory: it believes most of the
    /// site unreachable, settles on a poor chain, and files that chain as the site's best - where it
    /// is then inherited as a floor by every solve afterwards. Running them in order costs the few
    /// seconds the flood takes and removes that entirely.
    ///
    /// **This one carries a help icon**, against the rule for the rest of the menu, because it is
    /// the only setting here whose behaviour depends on another setting. Two switches that look
    /// independent and are not is exactly the case a tooltip is for.
    /// </summary>

    /// <summary>
    /// Solves the site quietly while you walk towards it, on its own thread, drawing nothing until
    /// you press the plan key.
    ///
    /// Each pass keeps the best chain found so far, so markers appearing as you approach improve on
    /// the answer rather than restarting it - and the routing every pass works out is kept, which is
    /// the part the first press otherwise pays for. Stops when the site stops revealing anything and
    /// the plan stops improving.
    ///
    /// The explanation lives here rather than in a tooltip: the menu carries no help icons.
    ///
    /// Whether to solve the site quietly on the way in, before the key is ever pressed.
    ///
    /// **This replaces warming the router, which was the same idea aimed by guesswork.** Warming
    /// flooded ground in advance so the first press would not have to; the trouble was knowing which
    /// ground, and two attempts got it wrong - marker centres first, then a ring around them -
    /// because the cells a search actually floods are its band corners and candidates. Measured on a
    /// real site: the warm-up took 54 floods of which the solve used 20, while the solve took 270 of
    /// its own.
    ///
    /// A solve floods exactly the cells it needs, by construction, so rehearsing the search cannot
    /// aim at the wrong ones. It also brings back something a warm-up never could: a plan. Press the
    /// key and the floor is already the best chain the site has given up rather than nothing.
    ///
    /// Safe to run on partial content because of the floor. Each pass seeds from the standing chain
    /// and cannot publish below it, so markers streaming in as you walk make the work compound
    /// rather than restart - which is the mistake that makes the equivalent feature elsewhere ship
    /// switched off.
    ///
    /// It draws nothing. The plan appears when you press the key, exactly as it does now.
    /// </summary>
    [Menu("Presolve")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether a presolved plan is drawn as soon as it exists, or held back until the key is pressed.
    ///
    /// **Held back was a deliberate choice and it is not obviously the right one.** The reasoning
    /// was that a presolve answers a question nobody asked: you walk towards a dig site and a chain
    /// with blast circles appears across ground you had not enquired about, and the decision to
    /// press the key stops being yours. That is a real thing to want to avoid.
    ///
    /// It is also a real thing to want the opposite of. A plan on screen while you are still walking
    /// in is the difference between arriving and pressing a key, and arriving having already decided
    /// where to stand - and somebody who turned the presolve ON has said fairly plainly which of the
    /// two they are after.
    ///
    /// So it is a switch, and shown is the default: turning the presolve on is the ask.
    ///
    /// Nothing about the SEARCH changes either way. The plan is found on the walk in regardless;
    /// this decides only whether you get to look at it before you press.
    /// </summary>
    [Menu("Show presolved plan immediately")]
    public ToggleNode ShowPresolve { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How long a rehearsal goes without improving before it stops. See Presolve.
    ///
    /// Far longer than a pressed solve's window, and deliberately: nobody is waiting for it. A press
    /// is answered while you stand over the detonator, so its window is a compromise between quality
    /// and your patience; a rehearsal has the whole walk in, and the only cost of letting it think
    /// is a core that was idle.
    /// </summary>
    [Menu("Time to improve presolve (ms)")]
    public RangeNode<int> PresolveMs { get; set; } = new RangeNode<int>(4000, 0, 30000);

    /// <summary>
    /// The same on a Grand Expedition, for the reason given on SettleMsGrand: fifteen links is far
    /// more than three times the search of five, and the approach is far longer too.
    /// </summary>
    [Menu("Time to improve presolve in a Grand Expedition (ms)")]
    public RangeNode<int> PresolveMsGrand { get; set; } = new RangeNode<int>(8000, 0, 60000);

    /// <summary>
    /// How long a presolve pass is allowed to run before a change to the site may stop it.
    ///
    /// **A pass was stopped by every single marker that arrived, and the entity work made that
    /// worse by making them arrive faster.** The rule is right in principle - a pass reasoning
    /// about a site that has since grown is answering a stale question - but it is a stop per
    /// arrival, and a site streams in a marker at a time. Measured walking into one dig site: the
    /// markers went 41, 61, 77, 83, 103, 108 in two and a half seconds, and the first five passes
    /// were killed after 26ms, 212ms, 116ms, 79ms and 87ms having published nothing at all. None
    /// of them lived long enough to build an environment, flood the routing and finish a greedy
    /// pass, so the score stayed at nothing until the site went quiet.
    ///
    /// So a change no longer stops a pass outright; it stops one that has had its turn. Anything
    /// arriving inside the window is still recorded - the question is re-opened, the settle test
    /// declines to conclude, and the next pass is dispatched against the new site the moment this
    /// one ends. Nothing is lost by waiting, because every pass is seeded from the best chain
    /// known and the routing it works out belongs to the ground rather than to the question.
    ///
    /// A second by default, which is long enough for a pass to have something to show and short
    /// enough that a marker arriving early in an approach is not waited on. Nought restores the
    /// old behaviour of stopping the instant anything moves.
    /// </summary>
    [Menu("Presolve pass timeout (ms)")]
    public RangeNode<int> RestartMs { get; set; } = new RangeNode<int>(1000, 0, 10000);
}
/// <summary>
/// Rerolling a remnant: how long the advice may take, and how much of the chain it may
/// move when you take it.
///
/// **Gathered here because they were in two places and neither was this.** Two sat under
/// Timing, which is true of them and says nothing about what they are for, and the third
/// under Advanced beside a setting about exhaustive search. A reroll is one thing a player
/// does, so it is one section.
/// </summary>
public class RerollSettings
{
    /// <summary>Advice off entirely: no pass is dispatched and nothing is drawn.</summary>
    public const string Off = "Off";

    /// <summary>One remnant advised at a time, enumerated, with a re-solve after each roll.</summary>
    public const string BestRoll = "Best roll only";

    /// <summary>Every worthwhile roll listed at once, with one solve at the end.</summary>
    public const string AllRolls = "All rolls";

    /// <summary>The advice never blanks and the solver never stops for it.</summary>
    public const string Continuous = "Continuous";

    /// <summary>
    /// The modes, in the order the dropdown offers them.
    ///
    /// **One list, because the dropdown and every test of the mode must agree.** The strategy
    /// dropdown was built from its own literal in the plugin's Initialise while an array here drove
    /// the comparison, so the same modes appeared in two orders in two files. See
    /// SolverSettings.Comparable, where that is written up.
    /// </summary>
    /// <summary>
    /// **Continuous and Off only, for now.** Best roll only and All rolls are withdrawn from the
    /// dropdown ahead of a public test: All rolls was never implemented at all - the constant appears
    /// in this file and in no reader, so choosing it behaved as Best roll only and said nothing - and
    /// Best roll only stops the solver for each roll, which is the behaviour the reroll work is in the
    /// middle of replacing.
    ///
    /// Off stays selectable although it is not a mode in the same sense, because it is the only way to
    /// turn rolling off: there is no separate enable toggle, so withdrawing it would leave a tester
    /// unable to stop the advice at all.
    ///
    /// The constants stay and so do the branches that read them. See Rolling.Mode, which coerces
    /// anything that is not Off to Continuous so a settings file saved before this cannot reach a
    /// branch the dropdown no longer offers.
    /// </summary>
    public static readonly string[] Modes = { Continuous, Off };

    /// <summary>
    /// Which reroll mode is in force.
    ///
    /// **Best roll only** is what this has always done: solve, enumerate every remnant, advise the
    /// single best, and re-solve once you have rolled it. The most careful answer and the slowest -
    /// measured at about 500ms of screening per remnant plus a solve, so a six remnant site is
    /// several seconds a roll.
    ///
    /// **All rolls** solves once, enumerates once, lists every remnant worth rolling, and re-screens
    /// only the remnants whose runes overlap the one just rolled - because that is where the "runes
    /// do not stack" interaction is and nowhere else. One solve at the end rather than between each.
    ///
    /// **Continuous** never blanks and never waits: the advice stands while a solve runs and says
    /// when the chain it was weighed against has been replaced, and the solver is not stopped for a
    /// roll. Which remnant is advised is decided by what a roll certainly destroys - the weight of
    /// the runes nothing else on the chain carries - which costs no scoring and can therefore be
    /// answered every frame. Measured on one site of six remnants, that ordering picked the same
    /// remnant the enumeration did. The enumerated figures still appear beside it and are what the
    /// display shows; they are not what chose.
    ///
    /// **Off** dispatches nothing.
    /// </summary>
    [Menu("Mode")]
    public ListNode Mode { get; set; } = new ListNode { Value = Continuous };

    /// <summary>
    /// How much better a challenger must be before the advice moves to it, as a percentage.
    ///
    /// Nought means it moves on any improvement at all, which is the default on purpose: a rule
    /// that refuses to divert is harder to notice than one that diverts too readily, so the
    /// readier behaviour is the one to try first and tighten if it reads badly.
    ///
    /// Compared on what a roll destroys, not on the enumerated gain, so both sides of the
    /// comparison are the same quantity. A challenger that has not been enumerated yet has no gain
    /// to compare. See Rolling.
    /// </summary>
    [Menu("Diversion margin (%)")]
    public RangeNode<int> DiversionMargin { get; set; } = new RangeNode<int>(0, 0, 100);

    /// <summary>
    /// How close to the advised remnant counts as committed, in grid, after which the advice holds.
    ///
    /// Nought switches it off, so the advice may move however near you are. See DiversionMargin for
    /// why that is the starting point rather than a committed radius.
    /// </summary>
    [Menu("Committed within (grid)")]
    public RangeNode<int> CommittedWithin { get; set; } = new RangeNode<int>(0, 0, 200);

    /// <summary>
    /// The improvement window for the re-solve that follows a reroll, and for nothing else.
    ///
    /// **The loop is solve, suggest a roll, roll it, solve again - and its length is the whole of
    /// how this feels to use.** That re-solve is answering a question you asked a second ago: it is
    /// seeded with the previous chain and has one remnant's worth of difference to absorb, so it is
    /// a far smaller problem than the first solve and should not be given the first one's clock.
    ///
    /// **A press of the key is never this.** Pressing is asking for the best answer the site has,
    /// and it uses the window above whichever press it is - this used to apply to every solve after
    /// the first at a site, so the key stopped honouring its own setting after you had pressed it
    /// once.
    ///
    /// A second, with a second for the reroll advice after it, is a two second round trip. See
    /// RollSolveMs.
    /// </summary>
    [Menu("Time to improve after a reroll (ms)")]
    public RangeNode<int> LoopSolveMs { get; set; } = new RangeNode<int>(1000, 100, 10000);

    /// <summary>
    /// The longest the reroll advice may take before it reports what it has.
    ///
    /// **It was bounded by how much work it had, not by how long that took.** The deep pass
    /// re-solves the chain once per sampled outcome for every remnant on its shortlist, so its cost
    /// follows the size of the site - which is exactly when you least want to wait for it. The
    /// shortlist is worked through best-first, so stopping early costs the least promising
    /// candidates and the line says how many it reached.
    /// </summary>
    [Menu("Maximum time for reroll advice (ms)")]
    public RangeNode<int> RollSolveMs { get; set; } = new RangeNode<int>(1000, 100, 15000);

    /// <summary>
    /// Works a simple site out exactly before searching it, and hands the answer over as a floor.
    ///
    /// **Nine or ten things of two kinds is not a search problem.** What matters on such a site is
    /// which kind goes first - content does not care what order it is taken in, propagation cares
    /// about nothing else - and once that is decided the chain follows: cover each kind as cheaply
    /// as you can, in that order. So the kinds are permuted, each order is built greedily, and the
    /// best is kept. Three kinds at most, because the work is a permutation of them and a dig with
    /// four is not the sparse case this is for.
    ///
    /// **It was written for one map and then could not be reached.** The solve lived inside the
    /// restart search, so with Destroy and Repair selected - the default, and the one that wins the
    /// bake-offs - it never ran: every dump on that strategy reports "working it out rather than
    /// searching: has not run". It runs before the search now, whichever search that is.
    ///
    /// A floor and not an answer. Reaching the right chain and proving it is the right chain are
    /// different things, so the search carries on from it rather than stopping - which costs a pass
    /// over a handful of candidates and can only start it higher.
    ///
    /// Here as a switch so the two can be compared on the map that prompted it: turn it off and see
    /// what the search makes of the same site on its own.
    /// </summary>
    /// <summary>
    /// How many links a reroll's worth may disturb when it is priced, from one to three.
    ///
    /// **The one knob that decides whether reroll advice is usable on a Grand site.** Pricing a roll
    /// means asking what the chain could do with the rolled remnant in it, and the old answer was
    /// Planner.Improve - any spot, any order, six rounds of every link against every candidate. Per
    /// sampled outcome, per candidate remnant, that is ten to twenty seconds for one remnant's advice
    /// with the player standing still.
    ///
    /// One link replaces a single spot with one that catches the rolled remnant and re-orders the
    /// chain. That covers the case the roll usually creates and costs about forty five trials.
    ///
    /// Two lets the following link move as well, which is what recovers a remnant the reach cannot
    /// otherwise get to - and a remnant off the route is usually off it for exactly that reason. It
    /// costs one link's worth of candidates on top, so a few thousand trials.
    ///
    /// Three is offered so the cost can be seen rather than guessed at, and should not be used: the
    /// trials multiply again, which is the loop this setting exists to escape.
    ///
    /// See Planner.Restitched.
    /// </summary>
    [Menu("Reroll restructure depth (links)")]
    public RangeNode<int> RollSubstitutionLinks { get; set; } = new RangeNode<int>(1, 1, 3);
}


/// <summary>
/// The bench: the search's own knobs, and the two narrow questions it answers exactly.
///
/// Nothing here needs touching to play. It exists so a measurement can be taken without a
/// rebuild, and behind a fold so the list somebody does read stays four items long.
/// </summary>
public class AdvancedSolverSettings
{
    /// <summary>
    /// Which search looks for the chain. See the consts below for what each one is.
    ///
    /// **Destroy and repair, because it is the one that wins.** Measured against the others on the
    /// same cold sites: 16,644 on a Grand site where Mixed was the best of the rest, and 1,677
    /// against 1,575 on a small one. What it has and the others lack is a move that changes several
    /// links at once - the rearrangement a chain needs to route round a different part of the dig,
    /// which is unreachable when links may only move one at a time.
    ///
    /// **Five others were offered and are gone.** Whole site, GRASP, Edge only, Beam and Mixed were
    /// written to be measured against each other, and the measuring is done: destroy and repair
    /// wins on every site they were run on, because it is the only one with a move the others lack
    /// - a neighbourhood of chains that agree everywhere outside a torn run, which is exponentially
    /// many and none of them reachable by shifting one link. Keeping the losers as choices offered
    /// a player five ways to get a worse answer and cost every reader of this code the question of
    /// which one mattered.
    ///
    /// The band search survives as a component rather than a mode - destroy and repair opens from
    /// it - and so does the beam, which the score card still runs to show its working. See Edges
    /// and Beam.
    ///
    /// Every mode scores with the identical objective, so a score from one means the same as a
    /// score from another.
    /// </summary>
    [Menu("Optimisation Strategy")]
    public ListNode Strategy { get; set; } =
        new ListNode { Value = SolverSettings.DestroyRepair };


    [Menu("Exhaustive search on simple sites")]
    public ToggleNode UseEnumeratedSolve { get; set; } = new ToggleNode(true);

    [Submenu(CollapsedByDefault = true)]
    public DestroyAndRepairSettings DestroyAndRepair { get; set; } = new DestroyAndRepairSettings();

    [Submenu(CollapsedByDefault = true)]
    public CandidateSpotSettings CandidateSpots { get; set; } = new CandidateSpotSettings();

    [Submenu(CollapsedByDefault = true)]
    public BandSearchSettings BandSearch { get; set; } = new BandSearchSettings();
}

/// <summary>
/// How much of a chain a repair tears out, which operator tears it, and how it is rebuilt.
/// See Repair.
/// </summary>
public class DestroyAndRepairSettings
{
    /// <summary>
    /// The smallest run of links Destroy and repair tears out at once.
    ///
    /// One is cheap and finds the obvious rearrangement. It is also the only size that can be tried
    /// thousands of times in a window, which is why it stays the floor rather than being raised when
    /// the larger tears go in.
    /// </summary>
    [Menu("Smallest tear (links)")]
    public RangeNode<int> TearLeast { get; set; } = new RangeNode<int>(1, 1, 8);

    /// <summary>
    /// The largest, once the search has stopped finding anything with the small ones.
    ///
    /// **A chain gets stuck where the way forward needs several links to move together.** Watched on
    /// a Grand site: fifteen and a half thousand, holding, where the next step up would have meant
    /// routing round a different part of the dig - which is four or five links changing at once and
    /// unreachable from any tear of one or two. The gap is not a scoring problem or a time problem,
    /// it is a neighbourhood problem, and the answer is a bigger neighbourhood.
    ///
    /// Not simply "tear six every time", because a big tear is expensive and usually worse: six
    /// links rebuilt from a shortlist rarely beats a chain that was already polished. What makes it
    /// pay is WHEN. See SolverSettings.TearLeast and Repair's escalation - the ceiling climbs with
    /// consecutive failures and drops back the moment something works, so the cheap tears do the
    /// ordinary work and the expensive ones only run where the cheap ones have stopped earning.
    /// </summary>
    [Menu("Largest tear (links)")]
    public RangeNode<int> TearMost { get; set; } = new RangeNode<int>(8, 1, 20);

    /// <summary>
    /// Give each thread a different appetite for the four ways of tearing.
    ///
    /// **The adaptive weights need wins to learn from, and cannot learn when there are none.** Each
    /// worker ranks the destroys by what they return and starts them all equal - so with records
    /// rare, every rate sits near the floor, the weights never separate, and eight workers
    /// independently fail to find the same evidence. That is one non-experiment run eight times.
    ///
    /// Seeded instead: one worker in five stays balanced, and the rest lean hard on a single destroy
    /// - seventy per cent to it and ten to each of the others - so the question "does this operator
    /// suit this site" is asked in parallel rather than discovered slowly and separately. A parallel
    /// portfolio, which is how solvers that cannot know the right configuration in advance usually
    /// handle not knowing it.
    ///
    /// The bias is a prior, not a cage: Reweigh still runs, so a specialist that finds its operator
    /// genuinely useless drifts back towards the others.
    /// </summary>
    [Menu("Vary the tearing mix per thread")]
    public ToggleNode VaryOperators { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether each worker seeds its own bands, or they all take the first one's.
    ///
    /// **The pool's openings are where its diversity has to come from, and they were shared.**
    /// Off, every worker asks for worker nought's seeding and comes out with the same band
    /// chain, so eight threads descend from one place - measured on a Grand site, all eight
    /// built and finished on the identical score with nought improvements between them.
    ///
    /// **On, and measured worse, because the shake undoes it before anything reads it.** Seven
    /// of eight workers each ran their own band search at about 1,770ms, so the openings really
    /// did differ - and every worker still BUILT the identical 4,402, because a shaken opening
    /// tears a third of the chain twice and eight different band chains arrive as eight equally
    /// wrecked ones. The 1.3s a worker bought nothing and halved the rounds, 3,838 to 1,975, and
    /// the site scored 20,092 against 23,517.
    ///
    /// So diversity wants to come from the shake or from the bands, not both, and the shake is
    /// free. Off by default on that measurement rather than on the older one about window cost.
    ///
    /// Kept switchable because it is a property of the site rather than of the plugin: somewhere
    /// the bands differ enough to survive a shake, this is the switch. See Repair.Banded.
    /// </summary>
    [Menu("Give each worker its own opening",
        "Each search thread works out its own starting chain instead of\n" +
        "sharing one. Finds more, and spends longer before it starts.")]
    public ToggleNode VaryOpenings { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Which destroy each thread favours, one entry per thread, in order.
    ///
    /// **The mix was four fixed variations on a cycle and there is no reason to think those are the
    /// right four.** The four destroys suit different sites - reach finds most of the records on
    /// some and none at all on others - and which of them is worth eight threads' attention is
    /// exactly the question nobody can answer in advance. So it is written here rather than decided
    /// in code.
    ///
    /// One entry per thread, comma separated, read in order; a thread with no entry of its own is
    /// even. The names are <c>even</c>, <c>seg</c>, <c>worst</c>, <c>rel</c> and <c>reach</c> -
    /// a random contiguous run, the links worth least, the links nearest a chosen one, and the one
    /// that bridges to something the chain is missing.
    ///
    /// A bare name leans seventy per cent on it and spreads ten to each of the others. Follow it
    /// with a colon and a number to say how hard: <c>reach:90</c> leans ninety, <c>seg:40</c> only
    /// forty. The rest is always shared equally among the other three.
    ///
    /// <code>even, seg, worst, rel, reach, even, seg, worst</code>
    ///
    /// **A lean, not a rule.** Reweigh runs from the first round and moves these towards whatever is
    /// actually returning, so a thread told to favour something useless drifts off it within a few
    /// hundred rounds. Setting every thread to <c>even</c> is the same as switching the box above
    /// off.
    /// </summary>
    [Menu("Tearing mix per thread")]
    public TextNode TearingMix { get; set; } =
        new TextNode("even, reach, rel, seg, reach");

    /// <summary>
    /// What the five names mean, behind a marker beside the field.
    ///
    /// **The second place in this plugin where a help icon earns itself.** The field takes five
    /// words that mean nothing on sight, and the choice between them is a judgement about this site
    /// - whether it is spread out enough for reach to have anywhere to go, whether worst is going to
    /// find any dead weight to cut. That is five short comparisons, which is a table rather than a
    /// sentence, and putting it in the field's own tooltip would make one settings row hold a page.
    ///
    /// Drawn SameLine so it sits against the field rather than under it: the settings parser gives
    /// every property its own row, and a marker on its own row reads as another setting.
    /// </summary>
    [JsonIgnore]
    public CustomNode TearingMixHelp { get; set; } = new CustomNode(Mixes.Draw);

    /// <summary>
    /// Let a stuck thread carry on from the best chain any thread has found.
    ///
    /// **Seven of eight threads spend the window arriving somewhere that is thrown away.** Measured
    /// on one press from nothing: 3,422, 3,600, 4,198, 4,282, 4,336, 4,451, 4,488 and 5,222. The
    /// answer is the best of them, so seven full eight second searches bought nothing, and two
    /// finished below where a greedy opening starts. That is what independent multi-start costs -
    /// it is the safest parallelism there is, and it is eight samples of one search rather than one
    /// search eight times the size.
    ///
    /// Pressing again already fixes it, at the price of a press: the second solve inherits the
    /// first's chain and improves on it, measured at +207 where a press from nothing gained nothing.
    /// This is the same exchange without the wait - the thread at 3,422, at the moment it gives up
    /// and kicks, starts again from the 5,222 instead of from its own.
    ///
    /// **Only at a kick**, where the chain was going to be thrown away regardless, so it costs no
    /// search time. One thread in four never adopts, and nobody moves for a lead smaller than half a
    /// per cent - between them that keeps the pool from collapsing onto a single answer, which would
    /// lose exactly the diversity that found the good chain.
    ///
    /// **Off by default, because the answer is the MAX and sharing trades the tail for the median.**
    ///
    /// The argument above is sound and points the wrong way for this objective. Adopting rescues a
    /// thread that has fallen into a bad chain - 3,071.9 is a trap two threads landed in on one press
    /// and never left - and lifts it to about the leader's number. That raises the pool's mean and
    /// its floor, both of which are discarded: only the best thread's chain is kept.
    ///
    /// What it costs is the chance of an outlier. Four of five threads abandon their own trajectory
    /// at their first kick, so only the held-out one can still produce a surprise. Measured, an
    /// adopter that went on to beat what it adopted was worth +30. The best press of the day was a
    /// thread left alone reaching 4,936 against a baseline of 4,197 - worth +739, and it happened
    /// once in two presses with sharing off and never in seven with it on.
    ///
    /// Two presses is not a proof and this is a judgement rather than a measurement: for an
    /// objective that keeps the maximum, five independent samples beat one consensus and four
    /// spectators. Turn it on for a steadier average - if a use ever appears that wants one.
    /// </summary>
    [Menu("Share the best chain between threads")]
    public ToggleNode ShareBest { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Threads that never adopt, comma separated, counted from nought.
    ///
    /// **A pool where everybody adopts is one search on several threads.** The answer kept is the
    /// best of the workers, so what the pool needs is not the leader's chain repeated but a spread
    /// of answers around it - and a worker that moves onto the incumbent every time it stalls stops
    /// contributing a second opinion. Keeping some threads out is the hedge against that: if the
    /// incumbent is a good-looking trap, somebody is still elsewhere, and when one of them finds
    /// something better it publishes and the adopters move to IT.
    ///
    /// <code>0</code>
    ///
    /// The default is thread nought alone, which is the smallest honest hedge and the same rule the
    /// tearing mix uses for its balanced thread - there is always one worker doing the ordinary
    /// thing to compare against. Empty means everybody adopts. A number past the end of the pool is
    /// ignored rather than an error.
    ///
    /// **It was a modulus and the modulus was an accident.** Every fourth thread was held out, which
    /// reserves a quarter of an eight thread pool and two fifths of a five thread one - a fraction
    /// that moved with the thread count for no reason anybody chose. Written down instead, so the
    /// hedge is whatever it says and not whatever the arithmetic lands on.
    ///
    /// Honest note: the hedge has not paid yet. On the press where sharing first fired, the held-out
    /// threads finished at 4,282.5 and 4,439.9 against a shared 4,490.8 - two workers that could
    /// have been lifted and were not. It is insurance, and this is the field for deciding how much
    /// of it to buy.
    /// </summary>
    [Menu("Independent threads")]
    public TextNode ShareNot { get; set; } = new TextNode("0, 1");

    /// <summary>
    /// How hard a thread shakes a chain it has just taken from another thread.
    ///
    /// **A migration is only worth making if the worker ends up somewhere the leader is not.** The
    /// pool already has the leader's answer; a second copy of it is worth nothing. What a stuck
    /// worker can add is a DIFFERENT answer near a good one - and to find one it has to be thrown
    /// far enough from the adopted chain that the local search cannot simply walk back to it.
    ///
    /// The ordinary kick shakes one to three times, each tearing about a third of the chain, and
    /// that is tuned for perturbing a chain the worker found itself and knows the region of.
    /// Measured on the first press where sharing fired, three adopters all finished on exactly
    /// 4,490.8 - the adoption paid, since none of them reached that alone, but all three landed on
    /// the same point rather than spreading around it.
    ///
    /// Three shakes is roughly the whole chain rebuilt once over. Lower is a cheaper move that stays
    /// nearer what was adopted; higher approaches throwing the chain away, which is what kicking
    /// from the record was written to avoid. Only has any effect with sharing on.
    /// </summary>
    [Menu("Shake strength after adopting")]
    public RangeNode<int> AdoptedShake { get; set; } = new RangeNode<int>(2, 0, 10);

    /// <summary>
    /// How far below the best any thread may fall before it abandons its chain and starts again, as
    /// a percentage. Nought never gives up.
    ///
    /// **Half of every pool dies in a trap.** Across every measured press with threads independent,
    /// 38 of 77 finished below 4,000 while their neighbours reached 4,130 - and they pile onto the
    /// same three chains: 3,072 sixteen times, 3,846 or 3,847 eight more. A thread down there is
    /// judged against its own record, so its acceptance window is sixty points wide around a chain a
    /// quarter below what the site offers; the kick tears it, the polish puts it back, and it spends
    /// the remaining seven seconds confirming a dead answer.
    ///
    /// So the pool is not eight samples and four corpses by accident - it is that every press, and
    /// recovering those threads is worth more than any thread count this machine can run.
    ///
    /// **It starts again from its OWN construction, not from the leader's chain.** That distinction
    /// is the lesson of sharing, which rescued exactly these threads and cost every rare good answer
    /// in doing it: a thread moved onto the leader explores where the leader already is, and the
    /// answers worth having only ever came from somewhere nobody was looking. This throws the dead
    /// trajectory away and leaves the thread independent.
    ///
    /// Ten per cent sits well below the traps (a quarter down) and well above ordinary disagreement
    /// between healthy threads, which runs to about two.
    /// </summary>
    [Menu("Restart threshold (% below best)")]
    public RangeNode<float> RescueBelow { get; set; } = new RangeNode<float>(10f, 0f, 50f);

    /// <summary>
    /// How hard to shake a restarted thread's new chain, so the restart is not a copy of what the
    /// others already have. Nought rebuilds plainly.
    ///
    /// **Restarting fixed the corpses and produced clones instead.** Recovering the trapped threads
    /// worked exactly as intended - the 3,072 trap that swallowed sixteen thread-runs stopped
    /// appearing, and seven or eight of eight threads finished healthy. But the rebuild is only
    /// mildly random, so a restarted worker constructs roughly the chain everybody else has and
    /// polishes into the same basin: three presses with restarts on gave pool spreads of 105, 132 and
    /// 334 where presses without them ran to 840, and none of the three found a rare answer.
    ///
    /// For an objective that keeps the best of eight, a copy is worth no more than a corpse. The
    /// shake is the one thing measured to widen a pool - the worker given seven of them produced both
    /// the worst chain of its press and the best of the day - and a restart is where it costs least,
    /// because an opening is worth about thirty per cent of a finish. A restarted thread does not
    /// need a good chain. It needs one nobody else is standing on.
    ///
    /// **Four by default, on the strongest result measured here.** Seven cold presses with the pair
    /// in force reached the site's better chain four times - 4,874, 4,874, 4,874 and 4,936 against
    /// 4,121, 4,130 and 4,151 - where every other configuration tried managed three in eighteen.
    /// Fisher's exact on that is about 0.04.
    ///
    /// **Neither half works alone, which is why they are defaulted together.** Restarting without the
    /// shake made clones: three presses, pool spreads of 105 to 334, no better chain among them.
    /// Shaking without restarting managed one press in five. Together they take a thread that has
    /// fallen into a trap and put it somewhere nobody else is standing, which is the only move that
    /// has produced the good chain repeatably.
    ///
    /// Nought keeps the plain rebuild, which is what the first three restart presses measured.
    /// </summary>
    [Menu("Shake strength after restart")]
    public RangeNode<int> RestartShakes { get; set; } = new RangeNode<int>(8, 0, 20);

    /// <summary>
    /// The most times a thread may shake its opening before searching, which is how different the
    /// threads are from each other.
    ///
    /// **Eight threads were not eight samples.** A worker shakes the shared band opening as many
    /// times as its own number, capped here - so at the old fixed cap of two, every worker from the
    /// third onwards got the same two shakes and was then polished onto the same local optimum.
    /// Measured on eight threads: three opened on the identical chain, twice running, and several
    /// finished on identical numbers. Raising the cap to the thread count gives worker seven seven
    /// shakes and worker three three, so no two start alike.
    ///
    /// **The old cap was tuned against something that turns out not to matter.** Two was chosen
    /// because three looked like demolition - a worker came back at 11,454 where the others reached
    /// 14,735 - which treats a weak opening as a loss. The opening instrument says otherwise:
    /// openings run about 1,160 to 1,300 and finish above 4,100, a gain of 217%, so what a thread
    /// opens with barely touches what it ends with. What it does decide is whether that thread is
    /// exploring ground another thread has already covered.
    ///
    /// This matters because the good chains are tail events. Five independent threads find the
    /// better basin about two presses in seven; duplicated threads are samples that cannot
    /// contribute one.
    /// </summary>
    /// <summary>
    /// The longest the opening may take, in milliseconds. Nought lets it finish.
    ///
    /// **The opening had no clock at all, which is why this is a new cap rather than an exposed
    /// one.** It builds a chain four ways - the last solve's answer, the band search, four greedy
    /// starts, then a polish - and returns when it has finished, not when it has spent a share of
    /// anything. Measured on a Grand site: four to eight seconds, against an improvement window of
    /// eight.
    ///
    /// That is worth capping only if the tearing is worth more than the construction, which is a
    /// measurement rather than an opinion and has not been taken. On one site the opening produced
    /// the whole score and 290 rounds of tearing added 0.1%; on another the threads gained 4 to 6%
    /// after opening. So the default is nought - behave as before - and this exists to find out.
    ///
    /// **The band search cannot be interrupted by it.** That pass takes a cancellation token rather
    /// than a clock, so a cap below its cost simply lands after it. The dump reports it separately
    /// as the shared band time.
    /// </summary>
    [Menu("Opening timeout (ms, 0 = unlimited)")]
    public RangeNode<int> OpeningMs { get; set; } = new RangeNode<int>(2000, 0, 20000);

    [Menu("Opening shakes")]
    public RangeNode<int> OpeningShakes { get; set; } = new RangeNode<int>(2, 1, 12);

    /// <summary>
    /// How far a slide nudges each link along the route, in grid units. Nought never slides.
    ///
    /// **A compound move, because its parts are not worth making.** Two chains 96 points apart were
    /// compared by hand: the last four links had each shifted a little further along the route, toward
    /// where the next one used to be, and only the final link gained anything. Every step of that is
    /// neutral or slightly worse alone, so single-link relocation refuses the first and never reaches
    /// the fourth - and tear-and-rebuild misses it too, since the rebuild ranks a few candidates in a
    /// window around the hole rather than displacing everything after it together.
    ///
    /// So the whole displacement is offered as ONE move and judged on what it is worth as a whole,
    /// which is the ejection-chain idea in its simplest useful form. Each link steps toward the one
    /// that follows it and snaps to the nearest spot the site actually offers.
    ///
    /// Eight grid is roughly a quarter of a blast radius - far enough to change what a link catches,
    /// near enough that the chain stays legal. Four multiples of it are tried, from every starting
    /// point in the tail, so the run length and the distance are both swept.
    ///
    /// **Nought by default, because it was measured and the move is simply not better.** Four presses
    /// offered 1,232 slides and improved the chain NOUGHT times - and the implementation was doing what
    /// it claimed: 241 of 272 slides in one press moved more than one link, three links on average, so
    /// these were genuine compound displacements rather than single relocations dressed up as them.
    ///
    /// Which is worth knowing, because the move was not invented - it was OBSERVED, by eye, between two
    /// chains 96 points apart. The conclusion is that the observation was of a difference rather than of
    /// a cause: those four links ended up further along because the better chain happened to be built
    /// that way, not because sliding there from the worse one was a move anything could have made.
    ///
    /// Kept on a switch rather than deleted, as the ordering pass was, in case a longer chain behaves
    /// differently - a six link tail has little room to slide along. Sliding says whether it ever pays.
    /// </summary>
    [Menu("Slide distance")]
    public RangeNode<float> SlideBy { get; set; } = new RangeNode<float>(0f, 0f, 30f);

    /// <summary>
    /// The fewest links a bridge may spend reaching for content the chain is missing.
    ///
    /// **Half the remaining tail is nothing when the tail is short.** The reach operator cuts the chain
    /// and bridges out to something rich it does not have, and it was given half of whatever links
    /// remained - seven on a Grand site of fifteen, and ONE OR TWO on an ordinary expedition of six.
    /// So on a short site the one operator built to reach missed content could not reach any.
    ///
    /// Measured on a five link site, two presses: 3,432 bridges of 7,410 ran out of explosives after
    /// 1.5 hops, NOUGHT were walled off by the ground, and reach won nothing whatsoever - while what it
    /// was failing to reach was the furthest remnant on the dig, worth about 1,270 to any chain that
    /// caught it. Presses that happened to catch it scored 6,833 to 6,864; presses that did not scored
    /// 5,562.
    ///
    /// Three links is enough to be a bridge and still leave something to catch with. Raising it further
    /// lets a bridge eat the whole tail and arrive with nothing left to place, which is the failure the
    /// half rule was guarding against - so this is a floor under that rule, not a replacement for it,
    /// and it never promises links the chain has not got.
    ///
    /// **Three was reasoned, and a sweep on a Grand site says two.** The figure above was settled on a
    /// FIVE link dig, where the floor is what the operator runs on; on fifteen links the half rule
    /// covers the whole chain until three to five links remain, so this only decides the end of it.
    /// There, three lets a bridge see 359 grid and afford about 251 of it - the "out of explosives"
    /// count is what that looks like - while two sees only what it can pay for.
    ///
    /// Measured from scratch on one Grand site: one scored worse than two, two scored about 23,500
    /// where three scored 21,800. One is too few because a single hop reaches reach plus blast, which
    /// is an ordinary placement rather than a bridge, so the operator stops existing in the last few
    /// links - which is this rule's own argument arriving from the other side.
    ///
    /// **Two, on the Grand sweep.** The five link measurement is what produced the rule and it
    /// argues for a floor rather than for three specifically; the only figure measured against
    /// three is this one, and two beat it. A short dig is the case to watch if this ever looks
    /// wrong - there the floor is what the operator runs on the whole way, rather than only at the
    /// end of the chain.
    /// </summary>
    [Menu("Minimum bridge links")]
    public RangeNode<int> BridgeLinks { get; set; } = new RangeNode<int>(2, 1, 8);

    /// <summary>
    /// Try every ordering of a planned chain this long or shorter. Nought leaves it to the operators.
    ///
    /// **An expedition takes six explosives, and six things have 720 orderings.** That is nothing
    /// beside the eight hundred thousand trials a window already runs, and it settles the one question
    /// the heuristics demonstrably cannot. Propagation pays a rune against the waves it reaches, so
    /// the same spots catching the same markers are worth wildly different amounts depending on which
    /// remnant comes first.
    ///
    /// Measured on one site, two presses that found the same six spots: content identical to four
    /// figures, the same six remnants caught, total rune landings of 21 against 20 - and propagation
    /// of 5,298.6 against 3,785.1. A forty per cent swing from ordering alone. The reordering
    /// operators were working hard in both, and the WORSE press made more swaps and banked more
    /// cumulative worth; adjacent swapping cannot cross from one permutation basin to another.
    ///
    /// Only the planned links move - anything on the ground stays where it is - and the bound matters
    /// because the factorial is the point: six is 720, eight is 40,320, and a Grand site's fifteen is
    /// 1.3 trillion. Above the bound the heuristics keep charge, which is the honest division of
    /// labour.
    ///
    /// **Nought by default, because it was measured and it wins nothing.** Five cold presses of a
    /// six-link site enumerated about 14,160 orderings across 118 calls and improved the chain
    /// exactly NOUGHT times, at 43 to 239ms a press. The reordering and reversing operators already
    /// find the best sequence at this length, which the swing that prompted this made look unlikely -
    /// two presses on the same spot set, the same markers, the same six remnants, and propagation of
    /// 5,298.6 against 3,785.1.
    ///
    /// So the forty per cent is not the order the blasts are visited in. It is which spots are chosen
    /// and how the markers fall across them: a remnant's reach is the monsters unearthed after it, and
    /// two chains catching the identical set can distribute it very differently - one blast taking
    /// three remnants against three blasts taking one each. That is destroy-and-repair's problem and
    /// no enumeration shortcuts it.
    ///
    /// Left here rather than deleted because the cost is small and a differently shaped site may yet
    /// pay for it. Watch "every ordering tried" in the dump: if it keeps reporting nought improved,
    /// this is dead weight and should go.
    /// </summary>
    [Menu("Exhaustive ordering limit (links)")]
    public RangeNode<int> PermuteUpTo { get; set; } = new RangeNode<int>(0, 0, 9);

    /// <summary>
    /// How far below its own record a search will follow a chain, as a percentage.
    ///
    /// **The narrowest thing in the search.** The rule is record-to-record travel: a chain is
    /// followed if it beats the last one, or if it is within this much of the best ever seen. At two
    /// per cent of a 4,092 chain that is a window 82 points wide - and the best press of the day sat
    /// 411 points above it, the all-time record about 950. Crossing to either means following chains
    /// well below the record for a stretch, and two per cent refuses them.
    ///
    /// It is the constraint left standing after everything else was ruled out: doubling the rounds
    /// changed nothing, and a candidate set swept from 291 spots to 1,063 changed nothing.
    ///
    /// **Wider is not simply better.** The window is what keeps a search anchored to something worth
    /// having; opened far enough it stops searching and wanders, ending wherever it happens to be.
    /// Somewhere between is a setting that can cross a valley and still climb the far side, and
    /// where that sits depends on the site.
    ///
    /// **Measure it with thread sharing OFF.** Sharing moves stalled workers onto the leader's
    /// chain, so the pool converges whatever this is set to - which hides both the wandering and the
    /// escape. Independent threads show the spread this actually produces.
    /// </summary>
    [Menu("Acceptance slack (%)")]
    public RangeNode<float> AcceptSlack { get; set; } = new RangeNode<float>(2f, 0.5f, 25f);

    /// <summary>
    /// How many of the site's best spots a repair may rebuild from.
    ///
    /// **Every tear rebuilds from this list, so nothing outside it can ever enter a chain.** The
    /// site offers about 2,800 placeable spots and the repair sees 400 of them - the richest, scored
    /// alone - plus the spread below. That is a cost decision: a rebuild ranks the whole list at
    /// every slot, so doubling it roughly doubles what a repair costs.
    ///
    /// It became worth exposing when the ceiling turned out to be flat. Five cold presses of one
    /// site returned 4,083, 4,094, 4,097, 4,209 and 4,503 - mean 4,197, standard deviation 178 -
    /// while the rounds per press doubled underneath them from other work. A search that twice the
    /// rounds cannot move is not short of time, and what it is allowed to look at is the next thing
    /// to suspect.
    /// </summary>
    [Menu("Shortlist: richest spots")]
    public RangeNode<int> ShortlistRich { get; set; } = new RangeNode<int>(400, 50, 2000);

    /// <summary>
    /// How many more spots it keeps purely to cover ground, and how far apart they must sit.
    ///
    /// **A shortlist by worth alone is disconnected.** Spots exist where content is, so the ground
    /// between two clusters offers nothing worth anything - and those are exactly the cells a chain
    /// needs in order to cross from one cluster to the other. Rank by value and cut, and the repair
    /// can rebuild a stretch inside a cluster and can never rebuild one that leaves it.
    ///
    /// **This half is the one being squeezed, and the distance is why.** At the defaults it asks for
    /// 200 and gets 87: the separation test runs against everything kept so far, including all 400
    /// rich picks, and those blanket the clusters. So the connective ground the spread exists for
    /// loses out to the ground that is merely valuable. Loosening the distance buys spread picks
    /// that raising the count alone will not - the dump prints asked against got, under "what the
    /// shortlist kept".
    /// </summary>
    [Menu("Shortlist: spread spots")]
    public RangeNode<int> ShortlistSpread { get; set; } = new RangeNode<int>(200, 0, 1000);

    /// <summary>
    /// How far apart the spread picks must START by. It relaxes from here until the quota is filled.
    ///
    /// **A starting point, not an answer, because one distance cannot fit two sizes of site.** The
    /// spread is chosen from what the rich picks leave, at least this far from everything already
    /// kept - so what it yields depends entirely on how much open ground there is. On a Grand site of
    /// 2,788 candidates, 45 grid gives 87 of the 200 asked for. On an ordinary expedition of 871 the
    /// rich picks blanket the map and 45 gives FIVE of a hundred: the half of the shortlist that
    /// exists so a chain can leave a cluster was effectively missing, and nothing said so.
    ///
    /// Measured on that site by walking the distance down: 5 picks at 45, 35 at 20, 90 at 10 - and
    /// the answers moved with it, from 7,221 flat to a mode of 7,317 with no press below 7,221. A dose
    /// response across three settings with the count moving as predicted, which is the same shape of
    /// evidence as the bridge floor and stronger than any single comparison.
    ///
    /// So the quota is honoured instead: the spacing halves while the spread is underfilled and stops
    /// the moment it is not, with a floor at 5 grid because picks that close cover the same ground at
    /// far higher ranking cost. A Grand site is untouched - 45 fills there first time - and a small one
    /// finds its own distance without anybody choosing a preset per site size.
    ///
    /// Sixty four, because the spacing halves and 64, 32, 16, 8 is an exact sequence that ends on the
    /// floor - four legible values in the log rather than 45, 22.5, 11.25, 5.6. Starting wider than
    /// necessary costs a pass over a sorted list per step and nothing else.
    ///
    /// The dump says what it settled on and whether it had to move, under "what the shortlist kept",
    /// and whether the result is enough under "the connective half of it".
    /// </summary>
    [Menu("Shortlist: initial spread spacing (grid)")]
    public RangeNode<float> ShortlistSparse { get; set; } = new RangeNode<float>(64f, 8f, 256f);

    /// <summary>
    /// Divide a reach's allowance by how much further this site's routes run than the straight line.
    ///
    /// **Two reach attempts in five are aimed somewhere the chain cannot get to, and it is knowable
    /// before a hop is spent.** The operator admits a target when the straight line to it fits
    /// inside the links it has left, then hands the job to the router, which walks a real route -
    /// never shorter than the line and usually a good deal longer. Measured over one press: 4,494
    /// bridges arrived, 535 were genuinely walled off by the ground, and 3,580 simply ran out of
    /// explosives after 3.2 hops. Only the 535 are the site refusing; the rest are the test being
    /// optimistic, each paying about 8.9ms - nearly a full attempt - to discover it.
    ///
    /// So the site is asked. Every bridge that arrives reports the links it really used against the
    /// links the straight line predicted, and the running ratio tightens the admission test for
    /// everything after it. Learnt rather than written down because it is a fact about the dig site:
    /// open ground is near enough 1, a site of corridors nearer 2, and no constant suits both.
    ///
    /// Bounded to between 1 and 2.5, since below 1 it would admit more than a straight line can
    /// justify and far above 2 it would refuse most of the site on a handful of awkward bridges.
    ///
    /// **Off, because a saving in search time is worth less than a target it refuses.** The estimate
    /// is one average for the whole site applied to every pair, so a reach across open ground is
    /// charged for the corridors somewhere else on the map, and a target the chain could genuinely
    /// have reached is turned away before the router ever looks at it. What it buys back is time
    /// inside a search that already stops on its own improvement window - so the time saved goes
    /// into more attempts rather than a better answer, while a refused target is gone for that press.
    ///
    /// The note above called that risk theoretical on the grounds that this "cannot make a chain
    /// worse". That holds only for attempts that were going to fail, and the estimate cannot tell
    /// those apart from the ones that were not. A ratio measured per pair, from the routes already
    /// walked between those two markers rather than from a site-wide average, would answer the
    /// objection; until that exists this stays off, and the dump still prints the ratio it learnt.
    /// </summary>
    [Menu("Estimate route detour",
        "Before spending hops to reach a distant marker, the search asks whether\n" +
        "the chain could get there at all with the links it has left. The cheap\n" +
        "answer is the straight line, and real routes bend around terrain, so the\n" +
        "line says yes too often - 3,580 of 4,494 attempts in one measured press\n" +
        "were doomed by distance, at 8.9ms each to find out. This divides the\n" +
        "allowance by how much further this site's routes ran than the line\n" +
        "predicted: open ground is near 1x, a site of corridors nearer 2x.\n" +
        "\n" +
        "Off by default. The figure is one average for the whole site, so it also\n" +
        "refuses reaches that would have worked, and the time it saves goes into\n" +
        "more attempts rather than a better chain.")]
    public ToggleNode EstimateDetour { get; set; } = new ToggleNode(false);
}

/// <summary>
/// Which cells the search may consider putting an explosive on.
///
/// **Called "spot families" before, which named the code and not the thing.** SeedFamilies is
/// the record these fill in; what they decide is how many candidate spots each pair of markers,
/// each rare and each remnant contributes, and how far apart they have to be. See
/// Planner.Families.
/// </summary>
public class CandidateSpotSettings
{
    [Menu("High-value markers only")]
    public ToggleNode LeanHeavy { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How many candidate spots each remnant contributes on its own account.
    ///
    /// Nought means a remnant is only ever caught from a spot some other marker offered, which is
    /// most of them on a busy site and none of them on a sparse one. Three gives the search
    /// somewhere to stand for each remnant whatever else is nearby.
    /// </summary>
    [Menu("Spots per remnant (0 = none)")]
    public RangeNode<int> SpotsPerRemnant { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Spots per rare")]
    public RangeNode<int> SpotsPerRare { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Spots per pair")]
    public RangeNode<int> SpotsPerPair { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Spot worth tolerance (%)")]
    public RangeNode<float> SpotSlack { get; set; } = new RangeNode<float>(2f, 0f, 20f);

    [Menu("Band width (cells)")]
    public RangeNode<int> CellsPerBand { get; set; } = new RangeNode<int>(6, 2, 24);

    /// <summary>
    /// How far apart two drawn spots must be, in grid units, before they count as different answers.
    ///
    /// Nought means the game's own minimum spacing, which is the honest default: two explosives
    /// closer than that cannot both exist, so anything nearer is an alternative to a spot already
    /// listed rather than a spot of its own. Without spreading at all, the top dozen positions on a
    /// site are the same blast nudged a unit at a time.
    ///
    /// Worth turning up to see the shape of the site in fewer, further-apart rings, and down to see
    /// how much better the exact position is than its neighbours. It was a blast radius, hard-coded,
    /// which was wide enough that the list became "the best spot in each area" and padded itself out
    /// with ground worth nothing.
    /// </summary>
    [Menu("Minimum spot spacing (grid)")]
    public RangeNode<float> SpotSpread { get; set; } = new RangeNode<float>(0f, 0f, 90f);
}

/// <summary>
/// The band search's own settings.
///
/// **It was a mode and is now a component.** Edge only offered it as a way to play; what it is
/// good for is opening, which is where destroy and repair uses it. These shape that opening and
/// nothing here picks a search any more.
/// </summary>
public class BandSearchSettings
{
    /// <summary>
    /// How many links the band search plans, or nought for the whole chain.
    ///
    /// **Nought, because there is no upper limit to write down.** It shipped at one - plan the next
    /// bomb and stop - which is a diagnostic rather than a way to play, and it could not be raised
    /// past fifteen because fifteen was the top of its slider. A dig site's explosive count is not
    /// fixed: map and atlas modifiers add to it, so any number typed here is a cap that a modifier
    /// can walk past. Nought asks for however many the detonator says are left.
    ///
    /// One still means the next bomb only, and is still the quickest way to see what a single link
    /// is worth. Anything between is a cap for a site whose tree is too wide to walk to the end.
    ///
    /// Renamed from EdgeLinks so a saved one is discarded rather than quietly keeping the old
    /// diagnostic behaviour under a setting that now reads as unlimited.
    /// </summary>
    [Menu("Links to plan (0 = all)")]
    public RangeNode<int> EdgeChainLinks { get; set; } = new RangeNode<int>(0, 0, 30);

    [Menu("Branches per step (0 = every option)")]
    public RangeNode<int> EdgeBranches { get; set; } = new RangeNode<int>(0, 0, 12);

    [Menu("Lookahead links (0 = all)")]
    public RangeNode<int> EdgeHorizon { get; set; } = new RangeNode<int>(0, 0, 8);
}

public class AutomationSettings
{
    /// <summary>
    /// One switch over everything in this tab.
    ///
    /// **The tab is the part of the plugin with consequences, and it had no off.** Turning the
    /// automation off meant finding each switch below and knowing which of them clicked; there was
    /// no way to say "plan and draw, but keep your hands off my mouse" in one move, which is the
    /// first thing anybody trying the plugin wants to be able to say.
    ///
    /// On, because placing the chain is what the plugin is for and a tester who has to find two
    /// switches before anything happens reports that it does not work. Off, every key still plans,
    /// draws and prices; nothing moves the cursor.
    ///
    /// Drawing is not automation and is not gated by this - the line to loot still points the way.
    /// </summary>
    [Menu("Enable automation")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether to hide ground labels when one is sitting over a button that needs clicking.
    ///
    /// **A label blocks a click and does not block an explosive**, so the plugin can be certain the
    /// spot is good and still have the click land on somebody else's name. A Verisium Sentry beside
    /// a remnant is the case that turns up: its label lies across the Runeshape Combinations button,
    /// the ring stays green because the placement indicator does not care, and the run stops with a
    /// refusal that reads as though the button were missing.
    ///
    /// The game already has the answer - Toggle Highlighting takes every label off the screen - so
    /// this taps it, clicks, and taps it back. Nothing about the world changes; only what is drawn
    /// over it.
    ///
    /// On, because the alternative when it happens is a run that stops for a reason nobody can see.
    /// Off for anybody who would rather the plugin never pressed a key they did not ask it to.
    /// </summary>
    [Menu("Use the Toggle Highlighting key when buttons are obscured")]
    public ToggleNode Unhide { get; set; } = new ToggleNode(true);

    /// <summary>Whether a switch below is on AND automation is on at all.</summary>
    public bool On(ToggleNode switched) => Enable.Value && switched.Value;

    [Menu("Pre-expedition")]
    public PreExpeditionSettings PreExpedition { get; set; } = new PreExpeditionSettings();

    [Menu("Post-expedition")]
    public PostExpeditionSettings PostExpedition { get; set; } = new PostExpeditionSettings();
}

/// <summary>
/// Laying the chain: everything the plugin does between the plan existing and the last explosive
/// going down.
///
/// The safety model is not in here, and deliberately so: the player moving the mouse is what stops
/// an automated placement, and that is ExileInput2's job rather than a setting.
/// </summary>
[Submenu(CollapsedByDefault = false)]
public class PreExpeditionSettings
{
    /// <summary>
    /// Takes the richest reward when the combinations window opens during a run.
    ///
    /// Placing an explosive over a remnant that has not been decided yet makes the game put the
    /// window up and wait, which stops the run dead until somebody clicks. This clicks the option
    /// the overlay already draws a border round - the same answer, so there is no second opinion to
    /// disagree with.
    ///
    /// Richest by reward price, which is not always the right pick: early in a chain a propagating
    /// rune is worth more than the reward, because it applies to every remnant after it. Turn this
    /// off for a chain where that matters and the run will wait for you instead.
    ///
    /// It does nothing when nothing is priced - without NinjaPricer every option is worth zero, and
    /// clicking the first of several identical zeroes is picking at random.
    /// </summary>
    [Menu("Choose rewards")]
    public ToggleNode ChooseRewards { get; set; } = new ToggleNode(true);

    [Menu("Place explosives")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

}

/// <summary>
/// Tidying up once the chain has gone off and the fight is over.
///
/// One pass, nearest first, with no preference for one kind of job over another: whatever is
/// closest is what gets done, so the run works outwards from where the player is standing
/// instead of finishing all of one kind and then walking the site again for the other. A dig
/// site whose remnants and chests are interleaved is cleared in the order you would walk it.
/// </summary>
[Submenu(CollapsedByDefault = false)]
public class PostExpeditionSettings
{
    /// <summary>When the loot line starts being drawn. See LineFrom.</summary>
    public static readonly string[] WhenToLine =
    {
        "Expedition start", "Expedition end",
    };

    /// <summary>The loot line from the moment the site is live. See WhenToLine.</summary>
    public const string FromStart = "Expedition start";

    /// <summary>
    /// When the loot line starts being drawn.
    ///
    /// **Expedition start by default, which is what it shipped as before there was a choice.** The
    /// line was gated to after detonation on the reasoning that nothing it points at is useful
    /// earlier - everything is either still to be blown or not yet worth walking to. That holds for
    /// the planning stretch and is not worth enforcing: a line one pixel wide that says only which
    /// way is not what makes a dig site hard to read, and being able to see where the currency is
    /// while the chain is still going down is worth more than the tidiness.
    ///
    /// Expedition end is the stricter behaviour, kept for anyone who wants the ground clear until
    /// the blasts are done. See Ending.ProbablyOver for what "end" means - the chain set off, no
    /// explosives left on the ground, and every remnant in the chain blown.
    /// </summary>
    [Menu("Draw the loot line from")]
    public ListNode LineFrom { get; set; } = new ListNode { Value = FromStart };

    /// <summary>
    /// A hairline to whatever is nearest still worth picking up.
    ///
    /// White and one pixel, because it is a pointer rather than a statement: everything else drawn
    /// on the ground says what a thing is, and this only says which way. Off-screen loot gets the
    /// line clipped to the edge of the window, which is the case it is really for - by the time
    /// looting starts the interesting things are behind you as often as in front.
    ///
    /// **When it starts is LineFrom's to say**, and it defaults to the moment the site is live. It
    /// was briefly fixed to after detonation, on the reasoning that everything it can point at
    /// earlier is either still to be blown or not yet worth walking to - true, and not worth
    /// refusing the line over, so the choice is a setting rather than a rule.
    /// </summary>
    [Menu("Draw thin white line towards nearest loot")]
    public ToggleNode Line { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How far away that loot may be and still get a line, in grid units. Zero means any distance.
    ///
    /// **Because loot you walked past is loot you decided about.** The line goes to the nearest thing
    /// still worth picking up anywhere on the site, and a dig site is several screens across - so
    /// after you skip a low-value shatter it keeps pointing back at it from the far side of the
    /// encounter, which reads as the line being broken rather than as you having already said no.
    ///
    /// Measured from the player, not the detonator: the line is about what is worth a few steps.
    /// </summary>
    [Menu("Only towards loot within (grid, 0 = any)")]
    public RangeNode<int> LineWithin { get; set; } = new RangeNode<int>(0, 0, 1000);

    /// <summary>
    /// Click the shatter button on remnants that have been blown and cleared.
    ///
    /// The button only works up close, which is why distance decides the order.
    /// </summary>
    [Menu("Shatter remnants")]
    public ToggleNode Shatter { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Click the ground label of an excavated chest that has not been opened.
    ///
    /// This one walks the character, unlike every other click the plugin makes - a chest is opened
    /// by going to it. That is inherent to the job rather than a choice, and it is why this is a
    /// switch rather than something that simply happens.
    /// </summary>
    [Menu("Open excavated chests")]
    public ToggleNode Chests { get; set; } = new ToggleNode(true);
}

/// <summary>
/// What the plugin draws for its own sake, as opposed to what it draws to explain itself.
///
/// Everything that exists to show the working - the content rings, the blast circle, the counts,
/// the measurements - sits under Debug behind one switch instead. This is the part that would still
/// be wanted with all of that turned off.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class DisplaySettings
{
    /// <summary>
    /// The chain the planner worked out, drawn on the ground: circles, numbers and the line between.
    ///
    /// Its own group above the rest because the rest exists to get you to this - the reward text
    /// tells you which remnant is worth having and this tells you how to reach it.
    /// </summary>
    [Menu("Draw plan in world")]
    public ToggleNode DrawInWorld { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The same chain on the minimap, which is the only surface that can hold all of it.
    ///
    /// **A fifteen explosive chain does not fit on the screen.** On a Grand site it spans most of
    /// the map, so the world overlay can only show the links you are standing among - and the thing
    /// worth seeing about a chain that long is its shape.
    ///
    /// Works on both the corner minimap and the full screen one; on the corner one a ring is drawn
    /// only where the frame holds it.
    ///
    /// Beside the world switch rather than forty lines below it, because the question they answer is
    /// the same question asked of two surfaces.
    /// </summary>
    [Menu("Draw plan on minimap")]
    public ToggleNode DrawOnMinimap { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The column over the placement button: the score, the status line, the bars, the tally.
    ///
    /// **Its own switch, because it was sharing the plan's and they are not one thing.** Turning
    /// the drawn chain off to look at a dig site took the score, the countdown and everything the
    /// plugin says about itself with it - so the only way to see the ground was to make the plugin
    /// go silent. They answer different questions and now they have different switches.
    /// </summary>
    [Menu("Draw score area")]
    public ToggleNode DrawScore { get; set; } = new ToggleNode(true);

    [Submenu(CollapsedByDefault = true)]
    public PlanDisplaySettings ThePlan { get; set; } = new PlanDisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public ScoreAreaSettings ScoreArea { get; set; } = new ScoreAreaSettings();

    [Submenu(CollapsedByDefault = true)]
    public PlacementCircleSettings PlacementCircle { get; set; } = new PlacementCircleSettings();

    [Submenu(CollapsedByDefault = true)]
    public RemnantDisplaySettings Remnants { get; set; } = new RemnantDisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public UnscoutedDisplaySettings UnscoutedGround { get; set; } = new UnscoutedDisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public PriceDisplaySettings Prices { get; set; } = new PriceDisplaySettings();
}

/// <summary>
/// How the planned chain is drawn, once Draw plan in world or Draw plan on
/// minimap has turned it on.
/// </summary>
public class PlanDisplaySettings
{
    /// <summary>
    /// Draws the best chain found so far while the search is still running.
    ///
    /// The search publishes every improvement as it goes and nothing was drawing them, so a solve
    /// was a second and a half of an empty site followed by an answer. With this on you watch the
    /// chain form, which is worth more than it sounds: a route that settles instantly and one that
    /// is still rearranging itself when the clock runs out are different situations, and only one
    /// of them wants a longer solve time.
    ///
    /// The in-progress chain is drawn the same way as the finished one but knows nothing about what
    /// each link was chosen for - those are recorded only when the search commits to an answer - so
    /// the content rings appear when it settles.
    /// </summary>
    [Menu("Draw plan while solving")]
    public ToggleNode ShowProgress { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The explosive to place now: its circle, the line into it, and its number.
    ///
    /// Green is the one you act on, yellow is everything still to come - the same scheme the rest
    /// of the readout uses, where green is ready and yellow is pending. It reads at a glance
    /// without a legend, which a two-colour scheme has to if it is to be worth having.
    ///
    /// It doubles as the colour of everything the plugin SAYS - the prompt, the score, the status
    /// word once a plan is ready - because those are all the same "here, now" that the next
    /// explosive is, and a third colour would be a third thing to learn for no gain.
    ///
    /// Renamed from NextColour so the swap actually reaches a saved config: settings already on
    /// disk beat changed defaults, so a colour scheme cannot be revised in place.
    /// </summary>
    [Menu("Next explosive")]
    public ColorNode StepColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 255, 120));

    /// <summary>
    /// Whether the links beyond the one you could place now are drawn at all.
    ///
    /// Above the colour rather than beside it, because it decides whether that colour is ever
    /// used. Off leaves the next explosive and anything else within reach, which is the picture
    /// somebody wants when the chain is long enough that the rest of it is clutter.
    ///
    /// The numbers and the line joining the links are untouched: this is the circle the colour
    /// paints, not the plan. Turning off the plan is what Draw plan in world is for.
    /// </summary>
    [Menu("Draw later explosives")]
    public ToggleNode ShowLater { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether a later spot turns green too when the key could reach it right now.
    ///
    /// **Green stopped meaning "next" and started meaning "this would work", so it applies to every
    /// spot rather than one.** A chain runs off the edge of the screen; knowing which of its links
    /// are actually in front of you - and which are behind a panel or a buff icon - is the same
    /// question at every link, and answering it only for the next one threw the answer away for the
    /// rest.
    ///
    /// Kept thinner than the next spot, because "you could" and "you should" are different claims
    /// and the eye still has to find the one to press.
    /// </summary>
    [Menu("Colour later explosives in range as next explosives")]
    public ToggleNode ColourInRangeAsNext { get; set; } = new ToggleNode(true);

    [Menu("Later explosives")]
    public ColorNode LaterColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 210, 80));

    /// <summary>
    /// Whether the blast radius of an exploding barrel is drawn.
    ///
    /// A barrel the chain sets off covers ground of its own, and the circle says how much. It is
    /// worth seeing while a chain is being judged and is noise once it is trusted.
    /// </summary>
    [Menu("Draw exploding barrels")]
    public ToggleNode ShowBarrels { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The ring round a barrel's own blast, which is not one of yours.
    ///
    /// **A different colour because it is a different thing.** The plan's circles are where an
    /// explosive will be put; this is where one already is, waiting to be set off - the chain does
    /// not choose it, pay for it or place it, and drawing the two alike invites reading a barrel as
    /// a link. Orange against the plan's green and yellow, and darker, since it is the site's
    /// furniture rather than the answer.
    /// </summary>
    [Menu("Exploding barrels")]
    public ColorNode BarrelColour { get; set; } = new ColorNode(Color.FromArgb(220, 255, 130, 40));

    /// <summary>
    /// How heavily the chain is drawn, in pixels. The next link is drawn one heavier.
    ///
    /// One control rather than two, because the next link is already told apart by its colour and a
    /// second slider would be a second way of saying the same thing. Kept as one step heavier so
    /// the emphasis survives somebody turning the whole thing down to a hairline.
    /// </summary>
    [Menu("Plan thickness")]
    public RangeNode<float> PlanThickness { get; set; } = new RangeNode<float>(1f, 1f, 5f);

    /// <summary>The next spot's ring, which is the one being looked for. See ColourInRangeAsNext.</summary>
    [Menu("Next explosive thickness")]
    public RangeNode<float> NextThickness { get; set; } = new RangeNode<float>(4f, 1f, 5f);

    /// <summary>A later spot that the key could reach, drawn lighter than the next one.</summary>
    [Menu("Reachable later thickness")]
    public RangeNode<float> ReadyThickness { get; set; } = new RangeNode<float>(1f, 1f, 5f);

    /// <summary>
    /// Whether a blast circle is drawn flat at the middle's height rather than following the ground.
    ///
    /// **A blast is not a shape on the ground, and hugging it is a pretty lie.** Planner.Catches is
    /// a distance between two grid positions - height plays no part in it at all - so the terrain
    /// under a circle is decoration. Next to a cliff it is misleading decoration: the segments over
    /// the drop fall away with it and the ring sprawls down the cliff face, showing a blast reaching
    /// somewhere the test it draws never considered.
    ///
    /// **On, because flat is what the test means.** Following the ground reads a little better on an
    /// ordinary slope and there is nothing wrong with turning this off to get that; but the default
    /// should be the picture that cannot mislead, and the one that sprawls down a cliff can.
    ///
    /// A switch rather than a rule that decides for itself. That wanted a number for how much drop
    /// is too much, nothing has measured what a player would call steep, and a guessed threshold
    /// that flattens the wrong circles is worse than either picture on its own.
    /// </summary>
    [Menu("Draw blast circles without terrain elevation")]
    public ToggleNode FlatBlastCircles { get; set; } = new ToggleNode(true);
}

/// <summary>
/// What the readout above the game's placement button shows, and where it sits.
///
/// Draw score area switches the whole block on. The two offsets move it; they were called
/// Status X and Y, which named neither the element nor the setting - the status LINE is a
/// different thing, drawn under the debug counts column. See Overlay.Status.
/// </summary>
public class ScoreAreaSettings
{
    [Menu("Draw presolve in score area")]
    public ToggleNode DrawPresolve { get; set; } = new ToggleNode(true);

    [Menu("Draw reroll in score area")]
    public ToggleNode DrawReroll { get; set; } = new ToggleNode(true);

    [Menu("Draw bombs in score area")]
    public ToggleNode DrawBombs { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How far to move the score area from where it sits by default, relative to the middle of the
    /// game's placement button.
    ///
    /// Everything the plugin has to say goes there and nowhere else. It used to hang off the
    /// encounter out in the world, which put it wherever the camera happened to leave it and
    /// underneath the game's own labels as often as not; the button never moves, and it is where
    /// the cursor already is whenever any of this is being asked for.
    ///
    /// Both nought, so the slider reads as an adjustment rather than as a position. Y shipped at 45
    /// and that 45 was not a preference, it was where the block belongs - a layout constant sitting
    /// in a default, where it looked like a number somebody had chosen and where resetting the
    /// setting was indistinguishable from moving the readout. It is Overlay.ScoreAreaDrop now, and
    /// Migrated step 5 takes it off any saved value so no screen moves.
    /// </summary>
    [Menu("Score area X offset")]
    public RangeNode<int> ScoreAreaX { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("Score area Y offset")]
    public RangeNode<int> ScoreAreaY { get; set; } = new RangeNode<int>(0, -400, 400);
}

/// <summary>
/// What is drawn inside and around the game's green placement circle.
/// </summary>
public class PlacementCircleSettings
{
    /// <summary>The three answers to when the red marks are drawn.</summary>
    public static readonly string[] WhenUnreachable =
    {
        "Always", "In placement mode only", "Never",
    };

    /// <summary>
    /// Ring whatever the game is currently lighting up under the placement cursor.
    ///
    /// The game's own answer, not ours: a marker is ringed when its glow_epk state is set, which is
    /// the client saying this explosive would catch it. So it needs no radius, no disc model and no
    /// assumption about how the blast is shaped, and it stays right if any of those are wrong.
    ///
    /// It is the check you would otherwise be making by eye - "is that one inside the circle or
    /// just near it" - answered by the thing that decides.
    /// </summary>
    [Menu("Show entity rings")]
    public ToggleNode RingsInCircle { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How solid the entity rings are drawn, as a percentage of each colour's own alpha.
    ///
    /// One number for all of them, because the alternative is eleven. The ring colours say which
    /// KIND a thing is - remnant, rare, chest, sentry - and that is the part worth choosing; how
    /// much of the dig site shows through them is a separate question, asked once and answered for
    /// the whole set. Editing eleven alphas to answer it loses the colours in the process.
    ///
    /// A hundred means the colour exactly as set, which is why this is opacity rather than
    /// transparency: the number that changes nothing is the top of the range, not the bottom.
    ///
    /// It scales the alpha rather than replacing it, so a colour deliberately set part-transparent
    /// stays relatively fainter than the rest at every setting. See Overlay.WithRingOpacity.
    /// </summary>
    [Menu("Ring opacity (%)")]
    public RangeNode<int> RingOpacity { get; set; } = new RangeNode<int>(100, 0, 100);

    /// <summary>
    /// The art file each of those entities is drawn from, written beside it.
    ///
    /// Off by default: it is how the content taxonomy was worked out - chestmarker3 is rare,
    /// chestmarker2 is magic, elitemarker is a rare monster - and once that is settled it is a
    /// wall of file names over the dig site. It stays because the taxonomy is the sort of thing a
    /// patch quietly changes, and the way to notice is to be able to look.
    /// </summary>
    /// <summary>
    /// Writes each lit entity's art name under it.
    ///
    /// **"Art name", the same words the debug pass uses**, because it is the same string: the .ao the
    /// model is loaded from. This said "animation names" while Debug said "marker art names", so one
    /// feature had two vocabularies and neither pointed at the other. See DebugSettings.ShowArtNames.
    /// </summary>
    [Menu("Show entity art names")]
    public ToggleNode ShowArtNames { get; set; } = new ToggleNode(false);

    /// <summary>
    /// When to mark ground the chain cannot reach, and content a blast did not get.
    ///
    /// Three marks, one idea: the plan wanted something here and the game said no. A red cross on a
    /// spot the chain cannot stretch to, a triangle round each marker a blast failed to collect, and
    /// the same cross painted live on everywhere the cursor finds out of range while the placement
    /// circle is up.
    ///
    /// Always, because the thing it explains is otherwise invisible: a chain bending round an object
    /// costs range, the planner's model does not know it, and without the marks the only symptom is
    /// a run that stops for no stated reason.
    ///
    /// **One list rather than the two switches this replaces.** There were three states and two
    /// booleans, so one of the four combinations said nothing - "hide outside placement mode" is
    /// meaningless once the marks are off - and the two rows had to be read together to work out
    /// what either did. The names are the states.
    ///
    /// In placement mode only is for somebody who wants clean ground except while actually placing,
    /// where the marks are directly about the spot under the cursor.
    /// </summary>
    [Menu("Draw red x on problematic terrain and range")]
    public ListNode Unreachable { get; set; } = new ListNode { Value = "Always" };

    /// <summary>
    /// How long the panel rectangles are cached before being read again, in milliseconds.
    ///
    /// The drawing asks "is this spot under a panel" for every link of the chain, every frame, and
    /// answering it means reading where the interface is painting. That reading costs 1.67MB of
    /// throwaway objects, because reaching a written-down path through the tree reads the whole
    /// child-pointer array at every level it passes - a hundred megabytes a second of garbage at
    /// sixty frames, for rectangles that change when somebody opens a panel.
    ///
    /// A tenth of a second is imperceptible for a colour. Raise it if the frame time still bothers
    /// you and you can live with a dot keeping its colour for a moment after a panel covers it;
    /// drop it to nought to gather them once a frame, which is what it did before this was a
    /// setting.
    ///
    /// **It never decides a click.** The run reads the interface at the moment it presses, and
    /// asks the game itself besides. See Panels.Blocked and Panels.UnderCursor.
    /// </summary>
    [Menu("Panel position cache (ms, 0 = every frame)")]
    public RangeNode<int> PanelCacheMs { get; set; } = new RangeNode<int>(100, 0, 2000);

    [Submenu(CollapsedByDefault = true)]
    public BlastValueSettings BlastValue { get; set; } = new BlastValueSettings();
}

/// <summary>
/// The number drawn on a spot saying what an explosive there would be worth.
/// </summary>
public class BlastValueSettings
{
    /// <summary>
    /// Writes what each blast is worth beside its number, as "#12 +1234".
    ///
    /// The content that spot was chosen for, on the same scale as the weights and matching the score
    /// card's content column. Off by default: fifteen numbers is already a lot of text over a dig
    /// site, and this is for judging a chain rather than for following one.
    /// </summary>
    [Menu("Show blast value")]
    public ToggleNode ShowSpotWorth { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Drops the figure from a spot once its explosive is down.
    ///
    /// **Off, because the number is worth more after the fact than before it.** While the chain is
    /// being laid the figures rank spots that are still choices; once the explosives are down they
    /// are the only account of what this chain is actually worth, link by link, and that is the
    /// thing you read when deciding whether to detonate or to go and find something better. The
    /// game draws its own explosive there, so the label is additional rather than duplicated.
    ///
    /// On for somebody who wants clean ground behind the cursor as the chain fills in.
    /// </summary>
    [Menu("Hide blast value for placed bombs")]
    public ToggleNode HideWorthPlaced { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Drops the figures once the chain has been set off.
    ///
    /// **On, because after detonation they are about a decision nobody can make any more.** The
    /// ground is then a fight rather than a plan, and fifteen numbers over it are fifteen things
    /// between you and the monsters. Read from the detonator's own state rather than from anything
    /// remembered - see Detonator.ExplosivesDetonated.
    /// </summary>
    [Menu("Hide blast value when expedition starts")]
    public ToggleNode HideWorthStarted { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The same figure for the spot under the cursor, while the placement circle is up.
    ///
    /// **The plan says what its own spots are worth and says nothing about anywhere else.** Standing
    /// with the circle up and moving it about is the moment somebody is deciding between two pieces
    /// of ground - and until now the only way to price the one under the cursor was to place there
    /// and read the score afterwards.
    ///
    /// Scored the same way a planned link is, against the same environment, so the number under the
    /// cursor and the number in a circle mean the same thing and can be compared directly.
    /// </summary>
    [Menu("Show blast value in placement mode")]
    public ToggleNode ShowCursorWorth { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How far below the indicator that figure sits, in pixels.
    ///
    /// A setting rather than a constant because the right number depends on the cursor art, the
    /// interface scale and how far the camera is tilted - none of which this can read, and all of
    /// which decide whether the text lands under the pointer or behind it.
    /// </summary>
    [Menu("Blast value height in placement mode")]
    public RangeNode<int> CursorWorthDrop { get; set; } = new RangeNode<int>(50, -100, 300);
}

/// <summary>
/// Everything drawn on or about a remnant: its reward, the line to it, its reroll
/// advice, the runes it passes on, and the Runeshape Combinations window it opens.
/// </summary>
public class RemnantDisplaySettings
{
    [Submenu(CollapsedByDefault = true)]
    public RemnantRewardSettings Rewards { get; set; } = new RemnantRewardSettings();

    [Submenu(CollapsedByDefault = true)]
    public RerollDisplaySettings Rerolls { get; set; } = new RerollDisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public PropagationDisplaySettings Propagation { get; set; } = new PropagationDisplaySettings();

    [Submenu(CollapsedByDefault = true)]
    public CombinationsWindowSettings RuneshapeCombinationsWindow { get; set; } = new CombinationsWindowSettings();
}

/// <summary>
/// The reward text drawn beside a remnant, and the line drawn to one worth walking to.
/// </summary>
public class RemnantRewardSettings
{
    /// <summary>
    /// The same switch as Rewards, Overrule already chosen rewards - not a copy of it.
    ///
    /// Shown here because it decides what the reward lines beside a remnant MEAN: with it on they
    /// are what the plan will make the remnant into, and with it off they are what the remnant
    /// already is and will stay. It used to decide whether an overruled line drew at all; that line
    /// is gone, and this stays because the reading of everything around it still turns on the
    /// switch.
    ///
    /// **One node, shown twice, rather than two nodes kept in step.** A second ToggleNode would be
    /// a second saved value, and two places holding one fact is how they come apart. Initialise
    /// points this at the real one after the file is read, so both rows edit the same object;
    /// JsonIgnore keeps it from being written a second time and read back as a stranger.
    /// </summary>
    [JsonIgnore]
    [Menu("Overrule already chosen rewards")]
    public ToggleNode Overrule { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The warning colour for anything the planner would not do, or cannot vouch for.
    ///
    /// **It no longer colours an overruled reward**, which is what it was named for. That line is
    /// gone - see Overlay, which explains why the comparison it drew could not be made - and the
    /// colour stayed because four other readouts had come to mean the same thing by it: the missing
    /// price-list warning, the must-avoid rings, and the combinations window's prices.
    ///
    /// The property keeps its name so a saved colour is not lost. See CLAUDE.md rule 3: the label
    /// and this comment move first, and the property follows in a change that says so.
    /// </summary>
    [Menu("Warning colour")]
    public ColorNode OverruledColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 140, 140));

    [Menu("Show rewards")]
    public ToggleNode ShowRewards { get; set; } = new ToggleNode(true);

    [Menu("Reward text")]
    public ColorNode RewardColour { get; set; } = new ColorNode(Color.FromArgb(255, 190, 120, 255));

    /// <summary>
    /// Draws a line towards any remnant worth more than this, in the unit chosen above.
    ///
    /// Gated on value because a line to every remnant is five lines across the screen saying
    /// nothing - the ones worth pointing at are the ones you would walk out of your way for, which
    /// is a number rather than a category. Zero draws none.
    ///
    /// The case it earns its keep in is a remnant OFF screen: the line clamps to the edge and
    /// points the way, with the price at the end of it, because that is the one place none of the
    /// other drawing can say anything at all.
    /// </summary>
    [Menu("Draw a line to rewards above (exalts)")]
    public RangeNode<float> LineAbove { get; set; } = new RangeNode<float>(400f, 0f, 500f);

    /// <summary>
    /// How far away a remnant may be and still get a line, in grid units. Zero means any distance.
    ///
    /// **Because the map loads its remnants before you reach the dig site.** Every remnant in the
    /// area exists from the moment you arrive, so without a limit the screen carries lines to ground
    /// hundreds of grid away that has nothing to do with the site in front of you - long, edge-pinned
    /// and pointing at somewhere you are not going.
    ///
    /// Measured from the player rather than from the detonator, because the question the line answers
    /// is "is that one worth walking to", and walking starts from where you are.
    ///
    /// The default is about the width of a large dig site, so a remnant on the far side of the one you
    /// are working still gets a line and the rest of the map does not.
    /// </summary>
    [Menu("Only to rewards within (grid, 0 = any)")]
    public RangeNode<int> LineWithin { get; set; } = new RangeNode<int>(500, 0, 2000);

    [Menu("Line thickness")]
    public RangeNode<int> LineThickness { get; set; } = new RangeNode<int>(3, 1, 5);

    [Menu("Line colour")]
    public ColorNode LineColour { get; set; } = new ColorNode(Color.FromArgb(255, 190, 120, 255));

    [Menu("Label X offset")]
    public RangeNode<int> RewardOffsetX { get; set; } = new RangeNode<int>(0, -600, 600);

    [Menu("Label Y offset")]
    public RangeNode<int> RewardOffsetY { get; set; } = new RangeNode<int>(0, -600, 600);
}


/// <summary>
/// How the remnant the planner would reroll next is picked out.
/// </summary>
public class RerollDisplaySettings
{
    /// <summary>
    /// Rings the game's own Liquid Verisium button when a remnant is worth rolling.
    ///
    /// The whole of the roll advice on screen. Everything else the adviser decides - KEEP, LOCK,
    /// TAKE, RISK - means leave the button alone, and an unringed button says that already; those
    /// verdicts are written to the score card instead, where there is room to explain them.
    ///
    /// The border is absent when you are not carrying a Liquid Verisium, because the button it
    /// draws on is, and there is nothing to advise about a roll you cannot make.
    ///
    /// Named for what it does. It was "Show advice" under Rewards, which promised a good deal more
    /// than one border and sat in the menu next to the thresholds rather than next to the drawing.
    /// </summary>
    [Menu("Highlight the remnant to reroll")]
    public ToggleNode HighlightRerolls { get; set; } = new ToggleNode(true);

    /// <summary>
    /// One blue for everything the roll advice draws, and nothing else on screen uses it.
    ///
    /// **It was the planner's green, which is the colour of "this is the one to take".** The
    /// reroll advice is a different kind of statement - it is about spending a Verisium rather than
    /// about where the chain goes - and sharing a colour with the chain, the covered markers and
    /// the chosen reward meant reading position to tell them apart.
    ///
    /// One setting drives the border, the line that leads to it, and the two score-card lines that
    /// say a roll is outstanding, so they are one mark in four places rather than four marks.
    /// </summary>
    [Menu("Highlight colour")]
    public ColorNode RollColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 160, 255));

    [Menu("Border thickness")]
    public RangeNode<int> RollBorder { get; set; } = new RangeNode<int>(3, 1, 5);

    /// <summary>
    /// Draws a thin line towards the remnant worth rolling.
    ///
    /// **The border only helps once you are standing at the remnant.** It is drawn on the game's own
    /// Liquid Verisium button, which exists on a remnant's label and so appears only when you are
    /// near enough for that label to be up - and the advice is about which of a dozen remnants to
    /// walk to. A mark you can only see after arriving cannot answer that.
    ///
    /// One at a time, because that is what the advice is: rolling changes the site, so the next
    /// remnant worth rolling is not knowable until this one has been rolled and the chain solved
    /// again. A line to each of five would be five answers to a question that stops being asked
    /// after the first. See Rolling.
    ///
    /// Clipped to the edge of the screen when the remnant is behind you, like the loot line, so it
    /// says which way to walk rather than vanishing.
    /// </summary>
    /// <summary>
    /// How thick the line to the next planned reroll is drawn.
    ///
    /// It was one pixel and not a setting, on the reasoning that the line is a hint about where to
    /// go rather than a route. True, and not a reason to fix it: one pixel disappears against a lit
    /// dig site, and the border it leads to has had a thickness of its own all along.
    ///
    /// Whole pixels, like every other line thickness here bar the plan's three. A line is drawn a
    /// pixel at a time, so a fractional setting offers a choice the screen cannot show.
    /// </summary>
    [Menu("Line thickness")]
    public RangeNode<int> RollLineThickness { get; set; } = new RangeNode<int>(3, 1, 5);

    [Menu("Draw a line to it")]
    public ToggleNode RollLine { get; set; } = new ToggleNode(true);

    /// <summary>
    /// What a roll is worth, as one figure beside the Liquid Verisium button.
    ///
    /// In the yellow the game borders a propagating slot in, because runes are all the comparison
    /// measures: both sides of it are scored with the reward set aside. See Rolling.Enumerated.
    ///
    /// It was three figures - a total, a rune half and a reward half - which said one thing three
    /// times once the reward left the comparison.
    ///
    /// A display setting rather than a debug one. It was under Debug behind "Explain reroll
    /// decisions" when it was a sentence, and it stayed there after it became a number - so it was
    /// both misnamed and in the one group a player turns off wholesale.
    /// </summary>

    [Menu("Show its expected value")]
    public ToggleNode RollFigures { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The same figure on every remnant that has a verdict, rather than on the next one.
    ///
    /// **Off, because the advice is one remnant at a time and this is not advice.** Rolling changes
    /// the site, so which remnant is worth rolling next is not knowable until this one has been
    /// rolled and the chain solved again - see RollLine, which draws one line for the same reason.
    /// A number on every label is a reading of the site rather than a recommendation, and it is
    /// worth having while deciding whether the advice is sane.
    ///
    /// Unlike the line and the border, this does not stop at the remnants worth rolling: a remnant
    /// the advice says to KEEP has an expected value too, and it is usually negative, which is the
    /// half of the argument the other setting never shows.
    /// </summary>
    [Menu("Show expected value on every remnant")]
    public ToggleNode RollFiguresAll { get; set; } = new ToggleNode(true);
}

/// <summary>
/// The runes a remnant passes on to the ones after it.
/// </summary>
public class PropagationDisplaySettings
{
    /// <summary>
    /// Says what is landing on a remnant's waves, as "7 (5+4-2) Volcanic".
    ///
    /// Seven distinct runes reach the monsters this remnant will send up: five sit in its own
    /// sockets, four propagated in from earlier links, and two were struck out as duplicates of
    /// runes already arriving. Volcanic is the one it sends onward.
    ///
    /// **The total leads because it is the thing worth scanning for**, and the bracket reconciles to
    /// it so nobody has to do the sum. Drawn only for a remnant the chain reaches, since "inherited"
    /// has no meaning without a position in one. See Propagation.Waves.
    /// </summary>
    [Menu("Show runes and propagation in world")]
    public ToggleNode ShowWaves { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Amber, to agree with the game.
    ///
    /// The game borders a propagating rune slot in yellow, so a yellow line of text beside it reads
    /// as the same fact rather than as a second one - which is worth more than any colour chosen
    /// for contrast on its own.
    ///
    /// No background of its own: it sits directly under the reward text and shares that one, so
    /// the two lines read as one block rather than as two boxes that happen to be touching.
    /// </summary>
    [Menu("Propagating rune text")]
    public ColorNode PassColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 205, 90));
}

/// <summary>
/// What is drawn over the game's own Runeshape Combinations window.
/// </summary>
public class CombinationsWindowSettings
{
    /// <summary>
    /// The same line, inside the game's own Runeshape Combinations window.
    ///
    /// **The window used to carry a different answer to the same question, and a worse one.** Each
    /// option row was labelled with what THAT combination would propagate, worked out from the
    /// remnant's own slots with no idea what the chain already carried - so it named runes arriving
    /// from earlier links as though this remnant were their source, and named runes worth nothing
    /// because nothing upstream had been consulted. The world line beside the remnant answers the
    /// same question from the objective, and the two disagreed on screen.
    ///
    /// So the window shows that line instead. It is a fact about the REMNANT rather than about a row,
    /// which is why it is drawn once at the head of the list rather than repeated down it.
    /// </summary>
    [Menu("Show runes and propagation in the combinations window")]
    public ToggleNode ShowWavesInWindow { get; set; } = new ToggleNode(true);

    [Menu("Price the combinations window")]
    public ToggleNode ShowWindowPrices { get; set; } = new ToggleNode(true);

    /// <summary>
    /// White on near-opaque black, matching RuneHighlighter's expedition tooltips.
    ///
    /// Its own pair rather than the ground text's, because the two surfaces are read differently:
    /// the ground text sits over open terrain where a colour distinguishes it, and this sits inside
    /// a busy window where legibility beats identity.
    /// </summary>
    [Menu("Combination price")]
    public ColorNode WindowPriceColour { get; set; } = new ColorNode(Color.White);

    [Menu("Combination price background")]
    public ColorNode WindowPriceBackground { get; set; } = new ColorNode(Color.FromArgb(210, 0, 0, 0));

    /// <summary>
    /// The combination the plugin would take: the richest once propagation is counted.
    ///
    /// Green, the same green as the next explosive, because it means the same thing in both places
    /// - this is the one to act on. The other border in this window is the plain richest, and it is
    /// drawn in the colour the price is written in, so the two never have to be told apart by
    /// position.
    /// </summary>
    [Menu("Planner's pick")]
    public ColorNode PickColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 255, 120));

    /// <summary>
    /// The plainly most expensive combination, when that is not the one to take.
    ///
    /// Only drawn when the two disagree, which is exactly when it is worth saying anything: the
    /// same purple the prices are written in, so it reads as "this is the money" against the green
    /// "this is the pick". When they agree there is one border and nothing to compare.
    /// </summary>
    [Menu("Most expensive (if not planner's pick)")]
    public ColorNode RichestColour { get; set; } = new ColorNode(Color.FromArgb(255, 190, 120, 255));

    [Menu("Pick border thickness")]
    public RangeNode<int> BestOptionThickness { get; set; } = new RangeNode<int>(3, 1, 5);

    /// <summary>
    /// Keep drawing both borders when their option is scrolled out of the panel.
    ///
    /// The list is taller than the window it sits in, so on a six socket remnant the answer is
    /// usually somewhere below the fold - and the borders are how the answer is said. Held to the
    /// panel they simply vanished for exactly the remnants where finding the pick by eye is hardest.
    ///
    /// Both of them, because they are a pair: the green pick and the purple most expensive mean
    /// something by DIFFERING, and one of them disappearing over the fold leaves the other reading
    /// as agreement when it is nothing of the kind.
    ///
    /// A border keeps its option's real rectangle, which is off the bottom of the screen, so what
    /// shows is whatever part of it the screen still reaches - most often nothing, and sometimes an
    /// edge at the lip of the panel pointing the way to scroll. Everything else drawn in this window
    /// stays clipped: a price with no option under it is a rendering fault, while a border tracking
    /// its own option is the border doing its job.
    /// </summary>
    [Menu("Draw borders off screen")]
    public ToggleNode BordersOffScreen { get; set; } = new ToggleNode(true);
}

/// <summary>
/// The ground the plugin has not seen yet, drawn on the minimap.
/// </summary>
public class UnscoutedDisplaySettings
{
    /// <summary>
    /// Paints the ground in an ordinary dig site that nothing has vouched for yet.
    ///
    /// **Separate from the Grand switch because the two answer different questions.** On a Grand
    /// site the question is which way to walk, since the site is bigger than the client will hold
    /// and the plan is being made about whatever fraction has been near the player. On an ordinary
    /// one the whole site loads at once, so walking is not the issue - the question is whether the
    /// scan has MET the whole site.
    ///
    /// **The markers decide how much ground there is to search.** Content clusters, so a radius
    /// around each known marker is where another one could still be; anything further out is not
    /// part of this site and painting it answers a question nobody asked. What covers that region is
    /// having been near it. See MarkerRadius and Scouted.Wanted.
    ///
    /// **This is what "Partial presolve" now means**, on both kinds of site: ground inside the dig
    /// site that nothing has been near and no marker vouches for, which is a claim that stays true
    /// until somebody goes and looks. It replaced "some of this site's markers are not loaded this
    /// instant", which flipped back and forth while nothing about the site had changed.
    /// </summary>
    [Menu("Draw unscouted ground on the minimap (Expedition)")]
    public ToggleNode ShowUnscoutedExpedition { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The same layer on a Grand site, where it covers the whole map rather than one dig site.
    ///
    /// **Fifteen explosives means a site bigger than the client will hold.** Everything the planner
    /// knows came from somebody standing near it, and until the lap is done the plan is being made
    /// about a fraction of the site - with no way to tell how much was left except walking and
    /// watching the marker count. This says which way to walk.
    ///
    /// Its own switch because it is a far bigger wash of colour than the ordinary one and a player
    /// may well want one and not the other. See ShowUnscoutedExpedition.
    /// </summary>
    /// <summary>
    /// Extends the ordinary layer to the dig sites you have not walked to yet.
    ///
    /// **Remnants and detonators are readable from right across the map**, so from the moment you
    /// load in, the scan knows where the expeditions are and roughly what is in them - a listing had
    /// all fourteen remnants of a map, the furthest 1,222 grid away, with their rewards and sockets.
    /// Ordinarily the layer says nothing about them, because the region is drawn from the site you
    /// are at and that is the one you are about to plan.
    ///
    /// With this on, every remnant the scan holds and every detonator the panel names contributes
    /// its radius, whichever expedition it belongs to. The result is a patch of red sitting over
    /// each dig site from the start, which shrinks as you walk into them - a map-scale answer to
    /// "where is there still something I have not been near", as against the site-scale one.
    ///
    /// Not offered for a Grand site: that layer already covers the whole map, so there is nothing
    /// this could add to it. See Scouted.Wanted.
    /// </summary>
    [Menu("Draw unscouted ground on the minimap when out of range (Expedition)")]
    public ToggleNode ShowUnscoutedFar { get; set; } = new ToggleNode(true);

    [Menu("Draw unscouted ground on the minimap (Grand Expedition)")]
    public ToggleNode ShowUnscoutedGrand { get; set; } = new ToggleNode(true);

    [Menu("Unscouted colour")]
    public ColorNode UnscoutedColour { get; set; } = new ColorNode(Color.FromArgb(70, 255, 60, 60));

    /// <summary>
    /// How far around a marker there is still ground worth searching, in grid units.
    ///
    /// **A marker points at more of the site, it does not vouch for what is beside it.** Expedition
    /// content comes in clusters and a marker is almost never on its own, so a marker with nothing
    /// within this of it to the north is the site's own statement that there is nothing further
    /// north - and a marker sitting at the edge of what has streamed in is the opposite, an arrow at
    /// ground nobody has looked at. The markers therefore set the REGION; what covers it is having
    /// been near it, which is the streaming range off the player.
    ///
    /// **Not the streaming range, and deliberately well under it.** That one is how far the game
    /// reaches from the player. This is how far apart content sits, which is smaller - a radius the
    /// size of the streaming range would claim the site extends far past anything that is in it.
    ///
    /// Above the blast radius, since a gap smaller than one explosive is not a gap worth walking to.
    ///
    /// This is what the ordinary expedition's unscouted layer is drawn from, and through it what
    /// "Partial presolve" means. Here rather than in Debug, beside the switches and the colour it
    /// belongs with: it is the size of a thing on screen, not an instrument. See Scouted.Wanted and
    /// ShowUnscoutedExpedition.
    /// </summary>
    [Menu("Radius around each marker (grid)")]
    public RangeNode<float> MarkerRadius { get; set; } = new RangeNode<float>(55f, 20f, 160f);
}

/// <summary>
/// The unit prices are shown in, and how many decimals each gets.
///
/// Display only. Every threshold inside the plugin is in exalts whatever this says, because a
/// display setting must never change what the plugin decides. See RerollSettings.
/// </summary>
public class PriceDisplaySettings
{
    [Menu("Price in")]
    public ListNode PriceIn { get; set; } = new ListNode { Value = "Exalted" };

    [Menu("Exalt decimals")]
    public RangeNode<int> ExaltedDecimals { get; set; } = new RangeNode<int>(0, 0, 4);

    [Menu("Divine decimals")]
    public RangeNode<int> DivineDecimals { get; set; } = new RangeNode<int>(2, 0, 4);

    [Menu("Chaos decimals")]
    public RangeNode<int> ChaosDecimals { get; set; } = new RangeNode<int>(2, 0, 4);
}

/// <summary>
/// The price at which a remnant stops being worth rerolling, and the price at which the chain has
/// to go and get it.
///
/// **Every price here is in exalts, whatever the readout is set to.** These used to follow the
/// display unit so that a number typed in a box meant what the price beside it on screen meant.
/// That reads well for one setting and badly for a tab: switching the readout to Divine silently
/// multiplied every threshold on it by about two hundred, and "a roll costs 10" went from a fair
/// price to one no remnant could ever justify. A display setting must not change what the plugin
/// decides, which is the same rule Exalts per weight point already followed alone.
///
/// Zero switches a threshold off rather than making everything qualify.
///
/// They are read from the top down - TAKE, then LOCK - so setting them out of order does no harm:
/// the richest test that a remnant passes is the one that applies.
///
/// There was a third, "Good enough above", saying KEEP for a reward over some figure. It is gone:
/// it expressed the same boundary as the roll floor from the other side, so a remnant you did not
/// want called poor was a matter of two knobs disagreeing rather than one being set. Its only
/// output was a word in the score card, and it shipped switched off, so nothing was using it.
///
/// Thresholds only. Whether the answer is drawn is a display question and lives under
/// Display - Remnants, with the rest of what appears on a remnant.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class RewardSettings
{
    /// <summary>
    /// Whether a reward already chosen at a remnant may be changed.
    ///
    /// On: the choice is treated as provisional, which is what the game says it is - a combination
    /// can be changed right up until the chain is detonated. The planner weighs every option the
    /// remnant could still become, and F4 walks up and changes the ones that are set wrong.
    ///
    /// Off: a chosen remnant is settled. Its option list collapses to the one thing it is set to,
    /// exactly as a rolled remnant's does, and the planner routes around a decision already made
    /// rather than around one it would like to make. F4 leaves it alone.
    ///
    /// Off is for playing with somebody else's decisions - your own from earlier, or a remnant you
    /// picked deliberately for a reason the weights do not know about. On is for letting the plugin
    /// have its way, and it is the default because a choice the game has not committed is not
    /// really a constraint.
    ///
    /// First on the tab because it decides whether anything else here gets to act at all.
    /// </summary>
    [Menu("Overrule already chosen rewards (dangerous to disable)",
        "On, which is the default and what you want almost always.\n" +
        "\n" +
        "A combination can be changed right up until the chain is\n" +
        "detonated, so a reward already chosen is not a decision the\n" +
        "game has committed to. On, the planner treats it that way:\n" +
        "it weighs every combination the remnant could still become,\n" +
        "and the placement run changes the ones that are set wrong.\n" +
        "\n" +
        "OFF MAKES EVERY REMNANT THAT ALREADY HAS A REWARD CHOSEN\n" +
        "FIXED IN PLACE. Its option list collapses to the single thing\n" +
        "it is set to, the planner routes around that decision instead\n" +
        "of making a better one, and the run will not change it. One\n" +
        "remnant set carelessly earlier then constrains the whole\n" +
        "chain, and nothing on screen will argue with it.\n" +
        "\n" +
        "Turn it off only to preserve choices you made deliberately,\n" +
        "for a reason the weights do not know about.")]
    public ToggleNode Overrule { get; set; } = new ToggleNode(true);

    /// <summary>
    /// What one point of weight is worth in currency. The exchange rate between the two halves of
    /// this plugin.
    ///
    /// Everything about a route is in weight - a magic chest is 5.5, a rare monster 33.7, and a
    /// propagating rune a percentage of the monsters after it - and a reward is in the unit
    /// chosen under Display. Those never had to be compared until the plugin started picking
    /// rewards for you, and then it did: an option paying two exalts less but carrying Opulent
    /// forward to three more remnants is the better option, and saying so out loud requires a
    /// number for how much better.
    ///
    /// **A balance, not an exchange rate, and the old label claimed otherwise.** It shipped as
    /// "approximate exalts per weight point" with a calibration behind it - a chain's content comes to
    /// about 850 points, an expedition pays something like a divine and a half, so a point is worth
    /// half an exalt. That arithmetic does not survive contact with the game, and reading the number as
    /// a market value makes it look badly wrong when the choices it produces are fine.
    ///
    /// What it is is the factor that lets two things with no common unit be compared. One is worth
    /// setting by whether the rewards it picks look right, which is how it was actually arrived at, and
    /// that is a legitimate way to choose a number - it is simply not a price.
    ///
    /// Renamed from WeightWorth, which shipped at zero: a default of nought meant the propagation
    /// half of the reward choice did nothing at all, and a saved nought would have kept it that way
    /// silently. Zero still switches it off for anybody who wants price alone.
    ///
    /// **In exalts, whatever Price in says, and that is the fix rather than an inconsistency.**
    /// Every other number under Rewards is a threshold you type next to a price on screen, so those
    /// follow the display unit and should. This one is not a threshold - it is the exchange rate
    /// between weight and money, which the planner reads on every decision. It used to be converted
    /// through the display unit like the rest, so switching the readout to Divine made a weight
    /// point worth 0.55 DIVINE, around two hundred times more, and quietly rewrote what the chain
    /// was optimising for. A display setting must not change what the plugin chooses.
    /// </summary>
    [Menu("Weight-to-price balance when choosing rewards")]
    public RangeNode<float> PointWorth { get; set; } = new RangeNode<float>(0.5f, 0f, 5f);

    /// <summary>
    /// Marks a rich remnant must take for you, exactly as the key would, and then leaves it alone.
    ///
    /// **One kind of insistence, written into one place.** This was a second path: the threshold
    /// was consulted live, every solve, beside the markers the key had set - so the plan held two
    /// sorts of requirement that behaved the same and could not be told apart, and a reward over the
    /// line could not be un-required without changing the number for the whole site. Now it does
    /// what it says: a remnant whose best reward clears this gets marked, once, in the same store
    /// the key writes to. See Insisted.Automatic.
    ///
    /// **Once per remnant, and your answer wins after that.** Press the key on an auto-marked
    /// remnant and it cycles to must avoid and then to nothing, and it stays there - the threshold
    /// does not mark it again on the next sweep. Marking something the plugin proposed and then
    /// having it reinstated is the behaviour that makes an automatic setting feel like an argument.
    ///
    /// In exalts. Two hundred and fifty is not a number anybody needs talking into: a remnant worth
    /// that much is worth building the chain around. Nought switches it off, and the key still works.
    /// </summary>
    [Menu("Automatically set Must Take on rewards >= (exalts)")]
    public RangeNode<float> MustTakeAbove { get; set; } = new RangeNode<float>(400f, 0f, 500f);

    /// <summary>
    /// What a Liquid Verisium costs you, in exalts.
    ///
    /// A roll is not free and the advice is worthless if it pretends otherwise: the currency is
    /// traded six hundred Verisium to one, so the real price is the gold and the Verisium that went
    /// into it rather than what the orb lists for. Ten exalts is a reasonable stand-in; put your own
    /// number in, because this is the figure every ROLL and KEEP below turns on.
    ///
    /// Set it to zero to be told what the rolls are worth ignoring what they cost.
    /// </summary>
    [Menu("A roll costs (exalts)")]
    public RangeNode<float> RollCost { get; set; } = new RangeNode<float>(10f, 0f, 500f);

    /// <summary>
    /// How far from the chain a remnant may sit and still be advised on, in reaches.
    ///
    /// **A candidate is a remnant one new link could catch.** The test is whether any planned
    /// explosive is within `reach * this + blast` of it, because a new link does not have to land
    /// on the remnant, only close enough for its blast to cover it. At 1.0 that reads as "one more
    /// explosive, placed from one already down, near enough to catch this" - a statement about the
    /// chain rather than a tuning figure. Higher is a dial on optimism.
    ///
    /// **Not the same as refusing a remnant the chain misses, which was a real hole once.** That
    /// refusal threw away exactly the remnants a roll exists to rescue - currently worth nothing,
    /// worth re-routing for after a good draw. This keeps those and refuses only the ones no single
    /// link could reach, which is a walk of several reaches on the chance of a good draw.
    ///
    /// **Safe because it is not permanent.** A remnant excluded at three reaches becomes a
    /// candidate the moment the route moves near it, since the gate is re-evaluated whenever the
    /// chain changes. Being excluded from one pass costs nothing; walking three reaches to a
    /// remnant the chain will never pass costs the site.
    /// </summary>
    [Menu("Only advise rolling within (reach)",
        "A remnant is only considered for rolling when a planned explosive\n" +
        "is within this many reaches of it, plus the blast radius.\n" +
        "1.0 means one more explosive could catch it.")]
    public RangeNode<float> RollWithinReach { get; set; } = new RangeNode<float>(1f, 0.5f, 4f);

    /// <summary>
    /// **Four thresholds used to live here and the calculation replaced all of them.**
    ///
    /// Roll below, Never roll above, Protect propagating runes and Keep for carried value were each
    /// a number standing in for an answer nobody could compute: what is this remnant worth against
    /// what a roll would return. Every one of them had to be tuned by feel, none of them could see
    /// the chain, and "protect propagating runes" in particular was a switch for a thing that is not
    /// a preference at all - losing a rune IS part of the price of a roll, and belongs inside the
    /// sum rather than beside it.
    ///
    /// Rolling is now decided by re-scoring the chain against sampled outcomes. See Rolling.
    /// </summary>

    /// <summary>
    /// Prices the plugin cannot look up, written down by hand. "name=value", comma separated.
    ///
    /// Some rewards have no price and never will: a recipe offering a generic "Unique Belt" has no
    /// item behind it for poe.ninja to have an opinion about, and the same goes for uncut gems,
    /// Verisium piles and the skill and support rewards. They come back as worth nothing, and worth
    /// nothing is not a number - it is the absence of one, and the difference matters. A remnant
    /// offering a unique ring currently gets no advice, no line, and no pull on the route, exactly
    /// as though it were empty.
    ///
    /// This is the short list of exceptions rather than the three hundred settings the other plugin
    /// carries: the names that need a number are the ones the price list cannot reach, and there
    /// are a dozen of them.
    ///
    /// Matched as a substring of the reward name, case insensitively, longest match winning - so
    /// "Unique Belt=1" prices belts and a bare "Unique=1" would price every unique not named more
    /// specifically. Values are in exalts, and multiply by the reward count the same way a
    /// looked-up price does.
    ///
    /// One entry is shipped. The generic unique rewards are worth very little and the belt is the
    /// best of them, so naming the belt alone says the whole of it: the others stay at nothing and
    /// the belt wins wherever it is offered against them. An exalt is enough to stop it reading as
    /// EMPTY without pulling a chain towards it, which is the job.
    ///
    /// This replaced a preference list that named unpriced rewards in order. An order says the belt
    /// beats the ring and cannot say by how much, so it could break a tie and never weigh against
    /// anything else. A price can.
    ///
    /// A saved file keeps whatever it already holds, so the four entries shipped before this stay
    /// put for anybody who has run the plugin. See ConfigVersion for when that is worth overriding,
    /// and this is not one of those: the numbers changed, the question did not.
    /// </summary>
    [Menu("Manual price overrides",
        "Comma separated.")]
    public TextNode Overrides { get; set; } = new TextNode("Unique Belt=1");

}

/// <summary>
/// What each kind of content is worth, in whatever units you like - only the ratios matter.
///
/// Set one to zero and the planner stops going out of its way for it entirely.
///
/// What each of these is keyed to, since none of it is guessable from the label. Every marker art
/// lives under Metadata/Terrain/Doodads/Leagues/Expedition/ and is matched on a substring, which is
/// why a new numbered variant classifies itself - the family name carries the meaning and the
/// number is a change of coat.
///
/// <code>
/// Remnant        Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter (an entity, not a marker)
/// Per socket     that entity's "sockets" StateMachine state
/// Rare chest     chestmarker3.ao                       -> inferred, not yet correlated
/// Magic chest    chestmarker2.ao, chestmarker2_02.ao   -> Metadata/Chests/LeaguesExpedition/LeagueFaction1Uncommon
/// Normal chest   chestmarker_signpost_01/02/03.ao      -> Metadata/Chests/LeaguesExpedition/LeagueFaction1Common
/// Rare monster   elitemarker.ao, _02, _03              -> a rare (yellow) monster
/// Normal monster monstermarker.ao, _02, _03            -> a normal (white) or magic (blue) one
/// </code>
///
/// **"Runic monster" is not a rarity and the plugin no longer uses it as one.** In game it is the
/// word for anything an expedition blast spawns, and those come up white, blue or yellow - so it
/// names the whole set, which is what the monster tag says. The three the plugin prices are Normal,
/// Magic and Rare, matching what the game calls them and what a modifier names. A monstermarker is
/// worth knowing about here: it spawns white AND blue, so it carries both tags.
///



/// <summary>
/// The working, and the tools that produced it.
///
/// Everything here is drawn to explain or to check rather than to play with, which is why it is all
/// behind one switch - the dump and correlate keys included, since a debug mode that still fired
/// hotkeys when it was off would be a switch that does not mean what it says.
///
/// The numbers it prints are how the blast radius, the placement lattice and the chain reach were
/// established, and they are the first place to look when something on screen disagrees with the
/// game.
/// </summary>
[Submenu(CollapsedByDefault = true)]
/// <summary>
/// What the plugin writes to disk while you play, so somebody helping can choose what to gather.
///
/// **A section of its own because these are the only settings whose point is somebody else.** They
/// change nothing about how the plugin plays: every file here is evidence, and nothing reads any of
/// it back. The reroll advice is the clearest case - the client does not ship the odds a roll draws
/// from, which the disassembly settles rather than suspects, so the only route to them is counting
/// remnants. One player counting is a few hundred readings; twenty players counting is a few
/// thousand.
///
/// They were under Debug, which hid them from exactly the people who would have ticked them:
/// somebody willing to gather readings is not necessarily willing to play with debug drawing on,
/// and the census was documented as silent unless debug mode was on.
///
/// **All off by default, each independent, and Recording is above all of them** - off there and
/// nothing is written whatever the rest say, so frame time can be measured in one move rather than
/// by unticking a list.
/// </summary>
public class RecordingSettings
{
    /// <summary>
    /// The same button as the one under Investigation, at the top of the section whose files people
    /// are asked to send.
    ///
    /// **A second button, not a second implementation.** Both call DumpFolder.Open, which owns the
    /// path and creates the folder - see there for why that is one place. The ImGui id differs because
    /// two controls with one id are one control: the second would be swallowed and never drawn.
    /// </summary>
    [JsonIgnore]
    public CustomNode DumpFolderUi { get; set; } = new CustomNode(() =>
    {
        if (ImGui.Button("Open dump folder###openDumpsRecording"))
            DumpFolder.Open();
    });


    /// <summary>
    /// One switch over everything that watches the game and writes a file while you play.
    ///
    /// **These are the settings whose cost is paid whether or not anything is on screen.** Each of
    /// them walks some part of the game every frame or every sweep and appends to a file, and each
    /// was reached by a different switch in a different place - the remnant census under the spawn
    /// heading, the spawn log beside it, and the streaming log under no switch at all, so there was
    /// no way to turn them all off and no way to be sure they were.
    ///
    /// They are evidence being gathered rather than anything the plugin acts on: nothing reads
    /// these files back. So they are worth having on while the evidence is wanted and worth
    /// turning off while the frame time is being measured, and until now the second was not
    /// possible in one move.
    ///
    /// Off gates all of them regardless of their own switch. On leaves each to its own.
    /// </summary>
    [Menu("Record anything to disk while playing")]
    public ToggleNode Recording { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Writes when each marker's entity loads and unloads to dumps/streaming.csv.
    ///
    /// **This ran under no setting at all.** It is on the sweep's rhythm rather than every frame,
    /// which is why it never showed up as a cost worth chasing, but it is a file being appended to
    /// while you play and there was no way to stop it short of unloading the plugin.
    /// </summary>
    [Menu("Record when marker entities load and unload")]
    public ToggleNode RecordStreaming { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Watches for the "Expedition Complete" banner and warns if sites are still unplaced.
    ///
    /// **The most expensive thing in here per look.** The banner lives at no fixed place in the
    /// interface, so when the written-down path stops reaching it the only way to find it again is
    /// to walk everything visible - measured at 29.8ms for one search, which is two dropped frames,
    /// and it landed in the worst frame the whole plugin reported.
    ///
    /// It is already held back as far as it can be: it only looks while detonators are untouched,
    /// which is the only state the warning could fire in, and it backs off to fifteen seconds while
    /// searches come up empty. What it buys is one warning about a game bug, so it is worth being
    /// able to say no to it outright. See Finished.Showing.
    /// </summary>
    [Menu("Watch for the expedition complete banner")]
    public ToggleNode WatchFinished { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Writes every remnant seen to dumps/remnants.csv.
    ///
    /// The only route to knowing what a reroll is worth. A roll replaces the whole remnant, so its
    /// value depends on the distribution of remnants the game generates - and the game does not
    /// expose the weights, so the only way to have that number is to count them.
    ///
    /// Passive and cheap: a handful of rows per dig site, appended on the sweep it already does,
    /// no reading the game has not already done. Each row says whether the remnant came with the
    /// map or came out of a roll, so the two can be compared rather than assumed equal.
    ///
    /// Its own switch in its own section rather than behind debug mode, because the people whose
    /// readings are wanted are not the people who play with debug drawing on. See RecordingSettings.
    /// </summary>
    [Menu("Record remnants seen")]
    public ToggleNode Census { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Count what each marker unearths, and write it to dumps/spawns.csv.
    ///
    /// The weights in this plugin are judgement over most of their range - an elite marker is worth
    /// six runic ones because that seemed about right - and the questions underneath them are all
    /// answerable by counting. Does an elite marker always bring one rare, or sometimes two? What
    /// does a plain monster marker bring, and at what rarities? Do a remnant's waves really scale
    /// with its sockets?
    ///
    /// One row per marker per dig site, including the markers that produced nothing: a table built
    /// only from markers that spawned something cannot tell "always brings a rare" from "sometimes
    /// brings a rare and sometimes nothing", and those are different answers.
    ///
    /// Monsters are attributed by where they appear, filtered to the expedition league's own
    /// monster metadata so the map's own packs cannot get in, and anything near two markers goes in
    /// a "contested" column rather than being assigned to the nearer one.
    ///
    /// On by default, because it costs a walk of the monster list and answers questions that are
    /// otherwise unanswerable. Nothing reads the file yet - it is evidence being gathered, and the
    /// weights stay as they are until there is enough of it to say something.
    /// </summary>
    [Menu("Record what markers spawn")]
    public ToggleNode RecordSpawns { get; set; } = new ToggleNode(false);

    /// <summary>
    /// How far from a remnant a monster still counts as one of its waves, in grid units.
    ///
    /// A remnant does not unearth its monsters all at once - as far as anyone has watched, it holds
    /// the next wave back until the last one is dead, so a time window cannot bound it and any
    /// number chosen for one would be wrong in both directions. Space can bound it: waves arrive at
    /// the remnant, and remnants are far apart compared with markers - the three in a recent site
    /// were fifty four, a hundred and sixty eight and a hundred and seventy three grid from each
    /// other, where markers sit ten apart.
    ///
    /// Twenty five is well inside the nearest of those and well outside the remnant itself. It is
    /// deliberately generous: a wave that spawns a little wide is a wave, and the cost of counting
    /// a passing map monster is one row that the names column will expose.
    /// </summary>
    [Menu("Count remnant waves within (grid)")]
    public RangeNode<float> WaveRadius { get; set; } = new RangeNode<float>(25f, 5f, 80f);

}

public class DebugSettings
{
    /// <summary>
    /// The settings object this belongs to, so the reset below can reach the rest of it.
    ///
    /// Set by the root's constructor. Not serialised - it is a back-reference rather than a
    /// setting, and a settings tree that saved a pointer to itself would not load.
    /// </summary>
    [IgnoreMenu]
    [JsonIgnore]
    public AutoExpeditionSettings Root { get; set; }

    /// <summary>
    /// Whether the debug DRAWING is on. See DebugSettings.
    ///
    /// **It gates what this tab paints, and not everything in it.** The rings, the art labels, the
    /// counts column, the measurements and the placement layers are all behind it; the recording
    /// group, the dump, score card, cold solve and correlate keys, the scan
    /// cadence, the planner's own numbers and the price freeze are not, and run with it off.
    ///
    /// It said "off gates all of them regardless of their own switch", which was not true of any of
    /// those and had never been. A switch that claims more reach than it has is worse than one that
    /// claims none, because the settings it does not reach look disabled while they run.
    /// </summary>
    /// <remarks>
    /// **Off, so a fresh install draws nothing it was not asked to.**
    ///
    /// Everything behind it defaults on, which is the pairing that makes the mode worth having:
    /// switching it on shows the whole picture at once rather than handing somebody a tab of
    /// toggles to find. Switching it off returns the screen to the game.
    ///
    /// The keys are deliberately outside it, and that is the argument that kept this switch honest.
    /// Somebody who hits something odd should be able to press the dump key and send the file;
    /// requiring a mode to be switched on first means the report arrives without the one thing that
    /// would explain it, or does not arrive. Requiring a debug mode in order to record what the
    /// plugin does with it off is a measurement that cannot be taken.
    /// </remarks>
    /// <summary>
    /// Whether the plugin tries to find an offset again when the one baked in stops resolving.
    ///
    /// **Because a game patch moves these and the symptom is silent.** The client's no-placement volumes are
    /// found through a static type descriptor, and after the 28 September update the descriptor had moved: the
    /// lookup returned nothing, every authored no-placement rectangle read as ordinary ground, and the planner
    /// routed onto ground the game refuses. Nothing said so - the readouts showed a site with no forbidden
    /// volumes, which is indistinguishable from a site that has none.
    ///
    /// The repair is self-validating rather than a guess. The tag it is looking for is known independently and
    /// confirmed two ways, so each candidate the client's own table offers is tried and only one whose
    /// component carries that tag is adopted. A wrong guess cannot be accepted; it can only fail and say so.
    ///
    /// On by default: a stale offset that reads as "this site has none" is worse than a repair attempt that
    /// reports failure. Either way the dump says which offset was used and where it came from.
    /// </summary>
    [Menu("Attempt to repair broken offsets")]
    public ToggleNode RepairOffsets { get; set; } = new ToggleNode(true);

    [Menu("Enable debug mode")]
    public ToggleNode ShowOverlay { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Puts every setting in the plugin back, not only the ones in this section.
    ///
    /// Behind Ctrl because it is the one genuinely destructive control here: a misclick would take
    /// every colour, offset and tuned number in the plugin with it. The reference table is not
    /// touched - a row is put back from the table itself, where the row being reset is visible.
    ///
    /// **At the top because it is not a debug tool.** It sat at the bottom of this section behind
    /// the recording group and the keys, which is where somebody looking for it would give up. The
    /// menu draws properties in declaration order, so the position IS this line.
    /// </summary>
    [JsonIgnore]
    public CustomNode ResetEverythingUi { get; set; }

    /// <summary>
    /// Turns on server garbage collection for the host, by setting DOTNET_gcServer for your account.
    ///
    /// **How the host collects is not this plugin's to set at runtime, and it decides what its
    /// allocation costs.** Workstation collection suspends every thread for a gen0 and the search
    /// allocates from several at once, so its garbage arrives as frame spikes; server collection gives
    /// each core its own heap and a far larger budget before it stops anybody. Measured together with
    /// the allocation work - see NOTES - the two matter more in combination than either alone.
    ///
    /// **A persistent variable for the account, because the setting is read once when a process
    /// starts.** Setting it inside this process would do nothing at all: the collector is chosen
    /// before any plugin loads. So it is written to the environment and takes effect the next time the
    /// ExileCore2 is started, which the button says. The host is the .NET process, not the game, so
    /// Path of Exile itself does not need restarting.
    ///
    /// It affects every .NET program you launch afterwards, which is worth knowing before clicking and
    /// is why the tooltip says so and why there is a button to put it back.
    /// </summary>
    [JsonIgnore]
    public CustomNode ServerCollectionUi { get; set; } = new CustomNode(() =>
    {
        var on = GCSettings.IsServerGC;
        var asked = HostCollection.Asked;

        ImGui.TextUnformatted(on
            ? "Server collection: ON for this process."
            : "Server collection: OFF for this process - HIGHLY RECOMMENDED to turn on.");

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "The .NET runtime collects garbage in one of two modes, and ExileCore2 picks\n" +
                "one when it starts.\n\n" +
                "Workstation mode stops EVERY thread to collect, and this plugin's search\n" +
                "allocates from several at once - so its garbage arrives as frame stutters\n" +
                "rather than as steady cost.\n\n" +
                "Server mode gives each core its own heap and a far larger budget before it\n" +
                "pauses anything. On a solve this is the difference between a smooth frame\n" +
                "and a visible hitch.\n\n" +
                "The button writes DOTNET_gcServer=1 to your Windows account, because the\n" +
                "mode is read once at startup and cannot be changed in a running process.\n" +
                "Restart ExileCore2 afterwards - not the game itself. It applies to\n" +
                "every .NET program you start, not only this one, which is what\n" +
                "Undo is for.");
        }

        if (asked && !on)
        {
            ImGui.TextUnformatted("Set for your account - RESTART EXILECORE2 for it to take effect.");
        }

        if (ImGui.Button("Turn on server collection###gcServerOn"))
            HostCollection.UseServer();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Writes DOTNET_gcServer=1 to your account's environment variables.\n\n" +
                "Highly recommended: it is the single largest thing outside this plugin\n" +
                "that decides what its allocation costs in frame time.\n\n" +
                "Takes effect next time ExileCore2 starts, and applies to every .NET\n" +
                "program you start afterwards.");
        }

        if (!HostCollection.Written)
            return;

        ImGui.SameLine();

        if (ImGui.Button("Undo###gcServerOff"))
            HostCollection.Restore();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Removes DOTNET_gcServer from your account, so the runtime chooses as it\n" +
                "did before this was ever set. Takes effect next time ExileCore2 starts.");
        }
    });

    [JsonIgnore]
    public CustomNode CacheGroup { get; set; } = Section.Of("Cache");

    /// <summary>
    /// Throws away everything the plugin has learned about this dig site.
    ///
    /// Almost everything here gets better the longer you stand in one place - the routed ground, the
    /// markers the scan remembers after the game unloads them, the blast radius and the reach read
    /// once and reused, the last plan handed to the next solve as a starting point. That is right
    /// for playing and useless for measuring: a change cannot be compared against a warm state it
    /// did not have to build.
    ///
    /// This is how to get back to a cold site without reloading the plugin.
    ///
    /// **It is not a fresh install and must not be described as one.** Nothing here touches the
    /// reference table, so every weight, every row edited by hand and every object the discovery
    /// path has filed survives - as do the settings, the must-avoid list and the runes. Saying
    /// otherwise sends somebody looking for an answer they gave in a place it was never kept, and
    /// invites the opposite mistake too: pressing this to clear a weight it will not clear. What
    /// goes is what the plugin worked out by standing here.
    ///
    /// **Two buttons, one nested inside the other.** "Delete caches and plan" is the whole of it,
    /// every site file for every area included; "Delete plan" is the same clear with the markers
    /// and the saved ground kept, so a site can be re-solved from cold without walking it again.
    /// The second is a strict subset of the first - the same call, with two flags set. See Caches.
    /// </summary>
    [JsonIgnore]
    public CustomNode ForgetRoutingUi { get; set; } = new CustomNode(() =>
    {
        if (ImGui.Button("Delete caches and plan###deleteCache"))
            Caches.Wanted = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Everything the plugin worked out by standing here, including\n" +
                             "every site file on disk - the markers, the walkable ground,\n" +
                             "the filed chains and the scouting layer - for every area,\n" +
                             "not just this one.\n" +
                             "\n" +
                             "The reference table is NOT touched: your weights, your\n" +
                             "edited rows and every object the plugin has filed all stay.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Delete plan###forgetWorkingOut"))
            Caches.WantedKeepingScan = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The plan, the best chain on file and every reading taken here -\n" +
                             "but NOT the markers, the walkable ground saved on disk, or the\n" +
                             "routed ground held in memory, so the next solve searches the\n" +
                             "same site from cold without another lap of it AND without\n" +
                             "paying to learn the terrain over again.\n" +
                             "Everything this does, the button to the left also does.");
        }


        ImGui.SameLine();
        ImGui.TextDisabled(Caches.Last);
    });

    [JsonIgnore]
    public CustomNode ScanningGroup { get; set; } = Section.Of("Scanning");

    /// <summary>
    /// How often the dig site is swept for new markers, and remnants priced.
    ///
    /// The only real cost knob in the plugin. Everything else per frame is cached for exactly one
    /// frame; this is the work that genuinely repeats - walking the entity bucket and, for a
    /// remnant not yet priced, reading its encounter data and filtering three hundred recipes.
    ///
    /// Markers never move and none appear after the site is generated, so this only governs how
    /// quickly a newly loaded one is noticed as you walk. Half a second is imperceptible; a couple
    /// of seconds would be fine too.
    /// </summary>
    [Menu("Poll rate (ms)")]
    public RangeNode<int> SweepMs { get; set; } = new RangeNode<int>(250, 100, 5000);

    /// <summary>
    /// How near counts as having seen it.
    ///
    /// **Measured from the game watching things load and unload, not guessed.** Ninety grid was a
    /// guess off the placement reach; a hundred and ninety came from a still frame, which shows what
    /// happens to be loaded rather than the distance at which it arrived. Neither was evidence.
    ///
    /// Watching a lap instead - 960 loads and 837 drops in a hundred seconds - separates the two
    /// radii that matter, and they are not the same number:
    ///
    /// - Nothing was EVER dropped closer than 168 grid. That is the guarantee: anything within it
    ///   is loaded, so walking past a point records everything inside that circle.
    /// - Things LOAD out to 330 routinely and were seen to 727. That is not a guarantee, because a
    ///   thing that never loaded leaves no record of having failed to, so the far tail says only
    ///   that the game sometimes reaches that far.
    ///
    /// This is the guarantee, a little under the closest drop to leave room for the sweep: the
    /// sweep runs once a second and the player moves, so a drop is noticed somewhere after it
    /// happened. A tile painted unseen that you did see costs a walk nobody needed; the other
    /// mistake costs content the plan never hears about, and the plan cannot tell you it is missing.
    ///
    /// The dump reports the live bracket under "how far the game streams entities in", and every
    /// transition goes to streaming.csv. If the floor there drops below this, lower it.
    /// </summary>
    [Menu("Entity streaming range (grid)")]
    public RangeNode<float> ScoutReach { get; set; } = new RangeNode<float>(160f, 100f, 340f);


    [JsonIgnore]
    public CustomNode OverlayGroup { get; set; } = Section.Of("Overlay");

    [Menu("(DEBUG) Draw where the game believes placement locations are valid")]
    public ToggleNode ShowPlacement { get; set; } = new ToggleNode(true);


    /// <summary>
    /// A ring round each piece of content, at the distance an explosive still catches it.
    ///
    /// It works two ways, and only one of them is a debug tool. Under debug mode every marker in
    /// the dig site gets one, which is how the ring size was calibrated in the first place. Outside
    /// it, only the content the NEXT explosive is counted as catching gets one - so the ring says
    /// "this is what that circle is for" at exactly the moment that question is being asked, and
    /// says nothing the rest of the time.
    ///
    /// Which content that is comes from the plan's own record of what each link was chosen for,
    /// not from re-deriving it against the drawn circle. Those two can disagree, and when they do
    /// the plan is the one that decided where to put the explosive.
    /// </summary>
    [Menu("(DEBUG) Draw marker rings")]
    public ToggleNode ShowMarkers { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Moves every drawn marker up or down in world units, to find an offset by eye.
    ///
    /// **For measuring, not for correcting.** A marker is projected from the terrain height under its
    /// cell, and on some objects that is visibly not where the art is - a sub-area cap being the one
    /// this was added for. Whether the gap is vertical at all is the question: Camera.WorldToScreen
    /// takes all three axes, so a height error and a horizontal one both read as the marker sitting
    /// beside the art, and the two are told apart by whether a height can be found that lines them up
    /// at every camera angle. One that works from one angle and not another was never a height.
    ///
    /// World units, which is what the position is in - divide by Detonator.GridToWorld for grid. The
    /// measured gap between the catacomb cap's entity and the terrain above it is 196.1 world, about
    /// 18 grid, so the interesting range is wider than it looks.
    ///
    /// Nought by default, and nothing reads it but the drawing: every caller of Target.Where is a
    /// draw path, and the planner works in grid, which has no height. So this cannot move a plan.
    /// </summary>
    [Menu("Marker height offset (world units)")]
    public RangeNode<float> MarkerHeightOffset { get; set; } = new RangeNode<float>(0f, -500f, 500f);

    /// <summary>
    /// Moves every drawn marker along the object's own facing, in world units.
    ///
    /// **Rotated per object, which is the point.** The gap left on a sub-area cap once its height is
    /// right changes with how the cap is turned, so a fixed X or Y cannot fit two caps facing
    /// different ways while one distance along the facing can. Positioned.Rotation is the angle, the
    /// same field ExpeditionIcons turns the detonator's exclusion rectangle by.
    ///
    /// **What a value here would mean.** These caps have a 15x15 footprint, so if GridPos is an anchor
    /// corner rather than a centre the art sits about 7.5 grid - near 81 world - away along the
    /// facing. A number close to that fitting every cap is the corner-anchor answer; one that fits a
    /// single cap and not its neighbour is not.
    ///
    /// Nought by default, and like the height offset it reaches only the drawing.
    /// </summary>
    [Menu("Marker facing offset (world units)")]
    public RangeNode<float> MarkerFacingOffset { get; set; } = new RangeNode<float>(0f, -300f, 300f);

    /// <summary>
    /// Moves every drawn marker across the object's facing, in world units.
    ///
    /// **The companion of the facing offset, because one axis could not fit the gap.** The offset that
    /// is left on a sub-area cap has a sideways part, which is what an anchor at a CORNER of a square
    /// footprint gives: the centre lies along the diagonal, so the two components are about equal.
    ///
    /// A 15x15 footprint puts that centre near 7.5 grid - about 81 world - along each axis. Two
    /// sliders settling near 81 and 81 on every cap is the corner-anchor answer and becomes a rule
    /// keyed on Positioned.Size; a pair that fits one cap and not another is not, and should not be
    /// kept as a per-object correction.
    /// </summary>
    /// <summary>
    /// Draws every marker at its object's interact centre instead of the position it reports.
    ///
    /// Render.InteractCenter is exactly Render.Pos plus half the bounds - measured on 170 entities
    /// with no deviation - so this is the claim that a reported position is a CORNER of the bounding
    /// box. Half the bounds is 20.7 world on an ordinary marker and 96.7 on a sub-area cap, which is
    /// why it is a switch and not one of the sliders above: the correction is per object.
    /// </summary>
    [Menu("Draw markers at the interact centre")]
    public ToggleNode MarkersAtInteractCentre { get; set; } = new ToggleNode(false);

    [Menu("Marker sideways offset (world units)")]
    public RangeNode<float> MarkerSidewaysOffset { get; set; } = new RangeNode<float>(0f, -300f, 300f);

    /// <summary>
    /// Writes each marker's art name under it. The same string and the same words as
    /// PlacementCircleSettings.ShowArtNames, which is the point of both being called art names.
    /// </summary>
    [Menu("(DEBUG) Draw marker art names")]
    public ToggleNode ShowArtNames { get; set; } = new ToggleNode(true);

    [Menu("(DEBUG) Show blast circle")]
    public ToggleNode ShowBlastRadius { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Rings the richest spots on the site, as the search ranks them.
    ///
    /// What a blast at each position would catch with nothing else placed, which is the measure the
    /// anchor seeds are built from. Useful for arguing with the planner: if a spot you would have
    /// used is not ringed, the disagreement is about what things are worth rather than about the
    /// search, and the weights are the place to look.
    ///
    /// A button rather than a switch because it is a question you ask, not a mode you sit in - and
    /// the number beside it is the answer's length. Nought turns it off, which is why there is no
    /// second button.
    ///
    /// Worked out when you press it, not when you solve. It needs no plan and no search - only the
    /// markers, the reach and the ground - so asking about a site you have just walked into is a
    /// fair question and it now gets an answer. It costs a pass over every candidate against every
    /// marker, which is why it happens on a press rather than on a frame.
    /// </summary>
    [Menu("Spots to draw")]
    public RangeNode<int> SpotCount { get; set; } = new RangeNode<int>(3, 0, 40);

    /// <summary>
    /// Reads the count from the setting above rather than keeping its own.
    ///
    /// It kept its own, as a plain ImGui field behind the button, and the value never moved off
    /// eight - so the button always asked for eight however the box was edited. A RangeNode is the
    /// framework's own control, it persists between sessions, and it cannot get out of step with
    /// what the button sends.
    /// </summary>
    [JsonIgnore]
    public CustomNode DrawSpotsUi { get; set; }

    /// <summary>
    /// Draws a red line to anything the game treats as blast-activated that the scan does not know.
    ///
    /// Every expedition object a blast can set off carries a `glow_epk` state, which is how the
    /// game lights up what the explosive under the cursor would catch. The scan recognises the ones
    /// it has been taught - markers, remnants, sentries - and silently walks past anything else,
    /// which is exactly the failure that hid the Verisium Sentry for weeks: content standing in the
    /// dig site, lighting up for every placement, and worth nothing to the planner because nothing
    /// was looking for it.
    ///
    /// So this looks for the state rather than for the metadata. Anything carrying it and missing
    /// from the scan gets a line and its name, which is a standing invitation to go and read what
    /// it is. Grand Expeditions will have their own objects and this is how they turn up.
    /// </summary>
    /// <summary>
    /// Writes under a marker that the plugin is valuing it on a placeholder nobody agreed to.
    ///
    /// **The other red mark, and the one that is about something you can fix.** Flag unknown blast
    /// targets draws a line to an object the scan cannot see at all; this labels one it CAN see and
    /// cannot value - it has a row on the entity table sitting at a nominal weight, and a chain is
    /// being planned around a number nobody chose.
    ///
    /// On, because the label is the whole of how anybody finds out. An object nobody has priced is
    /// silent everywhere else: it draws like any other marker, scores like a rounding error, and the
    /// only symptom is a chain that walks past something worth having.
    ///
    /// **A switch of its own, which it did not have.** It used to draw whenever the marker rings did
    /// - true until the rings were defaulted off for a quiet debug mode, and then the one thing a
    /// tester most needs to see stopped appearing, with nothing saying it had.
    ///
    /// **And it does not need Show debug overlay.** The same fault a second time: the pass that draws
    /// it is the debug pass, so the master switch suppressed it however this was set. A tester should
    /// not have to open Debug to be told the plugin is guessing at something's worth. See Overlay.Draw,
    /// where the pass runs for this alone and every other feature in it keeps the master switch.
    /// </summary>
    [Menu("Flag entities with no weight set")]
    public ToggleNode ShowUnpriced { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Draws where a regular explosive may and may not go, from the game's own rule.
    ///
    /// **Green is placeable, red is not, in the planner's own colours.** Placement is decided in
    /// `0x141F5A6B0` and its three terms are all known exactly: the coarse routing grid's byte is
    /// non-zero, the spot is more than the spacing from every placed explosive, and it is within
    /// reach of one. Scored against 2,972 readings where the chain state was known, that agrees with
    /// the game on every one.
    ///
    /// Replaces the cyan block-edge layer and the red routing layer, each of which showed one term
    /// </summary>
    /// <summary>
    /// Sweeps the boundary with the cursor and asks the game about every spot on it.
    ///
    /// **The only honest way to know whether the model matches.** Everything else compares our
    /// answer against readings the player happened to produce; this goes and gets the readings, at
    /// the one place a model is ever wrong - where placeable meets unplaceable.
    ///
    /// Press to start, press again to stop, and taking the mouse back stops it too. A dig site is
    /// several screens wide, so it sweeps what is in front of you and remembers what it has done:
    /// walk, press again, and it carries on. Tested spots draw solid, untested ones faint, and
    /// anything the game disagreed with draws white.
    /// </summary>
    [Menu("Sweep the boundary and check it against the game")]
    public HotkeyNodeV2 FrontierHotkey { get; set; } = new HotkeyNodeV2(Keys.None);


    /// <summary>
    /// On, and not behind the debug switch, which is deliberate and is the exception here.
    ///
    /// The numbers say which explosive is which, and that is worth having while playing rather than
    /// only while measuring - it is the one layer in this section that answers a question the game
    /// itself does not, since the detonator panel gives a count and never says which cross is the
    /// third. Its neighbour ShowPlacement ships on and invisible for the opposite reason: the
    /// placement field is a screen of dots that means nothing without the rest of the tab.
    ///
    /// The cost is a few crosses drawn per frame over the explosives already down, which is bounded
    /// by the chain length.
    /// </summary>
    [Menu("(DEBUG) Draw a numbered cross on each placed explosive (cyan)")]
    public ToggleNode ShowPlacedExplosives { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Spots to mark instead of the explosives currently down, as "x,y; x,y".
    ///
    /// **Fixed coordinates, because the test needs the bombs GONE.** Marking what is on the ground
    /// tells you where you already are; the question here is whether a pair that links one way links
    /// the other, and answering it means taking both explosives back and laying them in the opposite
    /// order. A marker that follows the placements disappears exactly when it is needed.
    ///
    /// Empty falls back to the explosives currently down, which is the quick sanity check.
    /// </summary>
    [Menu("(DEBUG) ...or at these fixed spots, as \"x,y; x,y\"")]
    public TextNode MarkSpots { get; set; } = new TextNode("");

    [Menu("Flag unknown blast targets")]
    public ToggleNode ShowUnexpected { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Writes under each marker what the planner thinks it is worth, and why.
    ///
    /// **The one question the plugin could never answer on the ground.** A chain bends towards
    /// something and there was no way to ask what that thing was scoring - the weight is on one tab,
    /// what it passes on is on another, its tags are worked out in code, and none of it is beside
    /// the object itself. This puts the arithmetic under the marker: its weight, its tags, and what
    /// it passes on and to what.
    ///
    /// **The marker under the cursor, and only that one.** It wrote under every marker to begin
    /// with, which is unreadable on anything but an empty site, and capping the lines made it
    /// unreadable and incomplete at once. Pointing at a thing is already how this plugin is asked a
    /// question about one - it is the same test the must-take key makes - so the marker you are
    /// pointing at is the marker that explains itself, in full.
    ///
    /// Behind the debug switch with the other explanations, and off by default.
    /// </summary>
    [Menu("(DEBUG) Explain marker scores")]
    public ToggleNode ExplainScores { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public CustomNode CountsGroup { get; set; } = Section.Of("Counts");

    /// <summary>
    /// Stop the reward prices moving, so two runs can be compared.
    ///
    /// **A search is not repeatable while what it is searching for keeps changing.** Prices come
    /// from NinjaPricer and are rebuilt every five seconds, so a fetch landing mid-session moves
    /// every remnant's worth - and with it the score, the chain, and whether a reward clears the
    /// automatic must-take threshold. Measured tonight: a site sitting at about 4,090 across seven
    /// runs moved to 4,459 on a price update, which is ten times the run-to-run noise and had
    /// nothing to do with the change being tested at the time.
    ///
    /// Ticked, the prices stay exactly as they were when it was ticked. Everything downstream - the
    /// reward list, the must-take threshold, the reroll advice - goes on reading them and cannot
    /// tell the difference, which is the point: it freezes the question rather than the answer.
    ///
    /// For measuring only. A frozen price is a stale price, and the plan built on it is worth what
    /// the prices said an hour ago.
    /// </summary>
    [Menu("Freeze reward prices")]
    public ToggleNode FreezePrices { get; set; } = new ToggleNode(false);

    /// <summary>
    /// The column of what the dig site holds: so many monsters, so many chests, explosives left.
    ///
    /// **Named for what it draws.** It was "Count entities", which reads as an instruction to go and
    /// count something rather than as a picture being drawn - and the counting happens either way,
    /// since the same lists feed the plan. What this switch decides is whether the column appears.
    /// </summary>
    [Menu("(DEBUG) Draw the counts column")]
    public ToggleNode ShowTally { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The plugin's own one line: what it is doing, or why it is not doing anything.
    ///
    /// **Its own switch, because it is not part of the column it sits on top of.** It is anchored
    /// to the counts position so the two read as one block, and it is drawn whether or not the
    /// column is - so turning the column off left a line hanging in the air where the column had
    /// been, which reads as a setting that does not work.
    ///
    /// On by default and worth leaving on: every refusal comes out here. "No router", "Worthless",
    /// "no dig site here" and every placement refusal are this line, and a plugin that has stopped
    /// doing something without saying why is the thing all of those exist to prevent.
    /// </summary>
    [Menu("(DEBUG) Draw the status line")]
    public ToggleNode ShowStatus { get; set; } = new ToggleNode(true);

    [Menu("(DEBUG) Counts X")]
    public RangeNode<int> TallyX { get; set; } = new RangeNode<int>(15, 0, 4000);

    [Menu("(DEBUG) Counts Y")]
    public RangeNode<int> TallyY { get; set; } = new RangeNode<int>(401, 0, 2400);

    [Menu("(DEBUG) Show measurements")]
    public ToggleNode ShowMeasurements { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public CustomNode OverlayColoursGroup { get; set; } = Section.Of("Overlay colours");

    [Menu("Verisium Sentry")]
    public ColorNode SentryColour { get; set; } = new ColorNode(Color.FromArgb(255, 120, 220, 255));

    [Menu("Remnants")]
    public ColorNode RemnantColour { get; set; } = new ColorNode(Color.FromArgb(255, 190, 120, 255));

    /// <summary>
    /// Relics, which used to be painted with RemnantColour and so could not be told from a remnant.
    ///
    /// Pink against the remnant's purple: the two sit next to each other on the ground and answer
    /// different questions - a remnant is a reward to choose, a relic is a modifier already in
    /// force - so reading one as the other reads the site wrongly.
    /// </summary>
    [Menu("Relics")]
    public ColorNode RelicColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 110, 185));

    /// <summary>
    /// "Rare" rather than "elite", because that is what an elitemarker turns into.
    ///
    /// The internal name stays Elite: the art file is elitemarker.ao and matching the game's own
    /// naming in the code is worth more than matching the menu.
    /// </summary>
    [Menu("Rare monsters")]
    public ColorNode EliteColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 255, 119));

    [Menu("Normal monsters")]
    public ColorNode MonsterColour { get; set; } = new ColorNode(Color.FromArgb(255, 220, 90, 90));

    [Menu("Rare chests")]
    public ColorNode RareChestColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 210, 80));

    [Menu("Magic chests")]
    public ColorNode UncommonChestColour { get; set; } = new ColorNode(Color.FromArgb(255, 136, 136, 255));

    [Menu("Normal chests")]
    public ColorNode CommonChestColour { get; set; } = new ColorNode(Color.FromArgb(255, 245, 245, 245));

    [Menu("Unidentified chests")]
    public ColorNode UnknownChestColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 255, 119));

    [Menu("Sub-area entrance colour")]
    public ColorNode EntranceColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 230, 200));

    [Menu("Blast circle")]
    public ColorNode BlastCircleColour { get; set; } = new ColorNode(Color.FromArgb(220, 190, 190, 190));

    [Menu("Covered")]
    public ColorNode CoveredColour { get; set; } = new ColorNode(Color.FromArgb(255, 90, 255, 120));

    [JsonIgnore]
    public CustomNode ThePlannerGroup { get; set; } = Section.Of("The planner");

    /// <summary>
    /// Whether the atlas node's "Double" choice is taken, which is what makes a rolled remnant
    /// sometimes carry a second propagating rune.
    ///
    /// **A fact about the player's atlas, which nothing can read.** The node is allocated visibly
    /// enough - it appears as AtlasExpeditionNotable2_ in IngameUi.AtlasTreePanel.Passives - but its
    /// only stat is dummy_display_expedition_remnant_modifier_selector at 1, a marker saying the
    /// node opens a chooser rather than which of the three was chosen. Every selector node on the
    /// tree reads the same way. So it is asked rather than detected.
    ///
    /// On means the 25% the node states; off means a roll never adds one. It feeds the reroll
    /// advisor, which cannot know whether a roll will produce one propagating rune or two - only
    /// how often. See Rolls.TwoSlots.
    ///
    /// **It replaced a sampled 0.283.** That came from counting remnants, was flat across socket
    /// counts, and is within noise of 25% at that sample size; eight remnants on one site later came
    /// out at exactly 2 of 8. A figure the game prints beats one counted off a handful.
    /// </summary>
    [Menu("Double or Nothing: Double (Atlas node)",
        "Adjusts how Verisium Remnants within Expeditions and Grand\n" +
        "Expeditions add Runic Modifiers to future Remnants\n" +
        "\n" +
        "25%% chance to add an additional Runic Modifier")]
    public ToggleNode DoubleOrNothingDouble { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Plan as though the rewards were not there, so the route is decided by runes and monsters.
    ///
    /// The reward term and the rune term pull in different directions and the reward term is the
    /// louder of the two, because a price is a real number of exalts and a rune's worth is a
    /// judgement about a chain that has not happened yet. A remnant offering three divines will
    /// take the chain to itself whatever it propagates, which is right if you want the three
    /// divines and wrong if you are trying to see what the propagation model would do left to
    /// itself.
    ///
    /// So this cuts the reward term to a hundredth of itself - both where it weights a remnant and
    /// where it sets the "must take" threshold - and leaves remnants worth being a remnant, their
    /// sockets, and what they carry forward.
    ///
    /// A hundredth rather than nothing, because zero throws away a free tie-breaker. Where two
    /// spots are equal on runes and content there is nothing else to separate them, and at one per
    /// cent a three hundred exalt remnant is worth about five points against a rare monster's
    /// thirty four: enough to settle a draw, not enough to win an argument. It does not touch the reward TEXT, the reroll advice or the
    /// auto-click: those answer "which option do I take once I am here", which is a different
    /// question from "where do I go".
    ///
    /// Chests come down with it, to a flat 1 / 0.5 / 0.25 for rare, magic and normal. Their tuned
    /// weights exist to trade off against reward value on the same scale, and with that scale gone
    /// they would be the loudest thing left for no good reason - a dig site holds a dozen chests
    /// and three remnants. Flat and small keeps them as a tie-breaker between two otherwise equal
    /// spots, which is the only job left for them here, and keeps the ORDER right: rare over magic
    /// over normal, by the same halving the tuned weights use.
    ///
    /// Under debug because it is a way of looking at the planner rather than a way of playing:
    /// ignoring a priced reward is throwing money away unless you are studying what the rest of the
    /// objective believes.
    /// </summary>
    /// <summary>
    /// What each rune beyond the first is worth, as a percentage, on monsters that already carry
    /// one.
    ///
    /// **The propagation term is additive and the game is not.** Every rune contributes its share
    /// of the monsters it reaches, and the shares are summed - so four runes spread over four
    /// separate packs and four runes landing together on one pack score identically. In the game
    /// they do not: quantity, rarity and the rest compound on the same monster, and a rare wearing
    /// four modifiers at once is worth more than four rares wearing one each. An objective that
    /// cannot say so will happily spread runes thin.
    ///
    /// So the monsters are cut into layers by how many distinct runes reach them - a rune booked at
    /// an early link covers everything a late one does and more - and each rune beyond the first
    /// adds this much again to that layer. At fifteen per cent, monsters under two runes are worth
    /// 1.15x their additive share, under four 1.45x, under eight 2.05x, under twelve 2.65x.
    ///
    /// **Linear in the count rather than compounding, which is deliberate.** Written as a repeated
    /// multiplier it needed a ceiling to stay sane, and a ceiling binds at eleven runes on this
    /// default and at three at the top of the range - so turning the setting up would have made it
    /// stop distinguishing anything. Linear is bounded on its own, monotone everywhere, and the
    /// more conservative claim about a compounding nobody has measured.
    ///
    /// Nought turns it off exactly, restoring the additive objective to the decimal. That matters:
    /// it is how a chain scored with this on can be compared with one scored without.
    ///
    /// **Monsters only.** Several runes reaching one chest is worth the sum of them - a chest has
    /// no defences to compound - so a scoped rune only joins the layering when its scope names
    /// monsters. See Tags.Monsterly.
    ///
    /// **A remnant's own waves are where this bites hardest.** Its ordinary slots all reach them and
    /// nothing else, so a six socket remnant puts five modifiers on one pack - and those monsters
    /// are also wearing every rune propagating from this link and the ones before it. Seven deep is
    /// ordinary there, where the general pools rarely pass three. See Planner.Locally.
    ///
    /// **Five, lowered from fifteen after watching what it did.** At fifteen it came to 9,549 of a
    /// 15,800 propagation term on an ordinary five link chain - sixty per cent of the propagation
    /// and over half the whole score. A guess nobody has measured must not be the largest term in
    /// the objective: at that size the plan is chosen by this number rather than by the weights.
    ///
    /// The arithmetic was doing what it was told. Each layer is multiplied by the count of runes
    /// reaching it, a five link chain carries a dozen distinct runes between its remnants and
    /// relics, and the remnant waves stack their own ordinary slots on top - so the multiplier at
    /// the deepest layer was approaching two and applying to almost everything.
    ///
    /// Five makes it a tilt that breaks ties between chains of similar content, which is what it
    /// should be until the effect is measured. Raise it if a chain built for concentration is shown
    /// to beat one built without; the dump prints the share it is taking so that stays visible.
    /// </summary>
    /// <summary>
    /// Says so if the Bait rune ever turns up.
    ///
    /// **A rune the game knows about and has never been seen placing.** It sits in the rune table
    /// with its own id and its own art, and its mod is Power's - so a remnant carrying one would
    /// print "Power Rune" and pass unremarked. Nothing in 476 deduplicated fresh remnants, where an
    /// ordinary rune would have appeared about sixteen times.
    ///
    /// Either cut before release and left behind, or placed under conditions nobody has met. The
    /// name is what makes it worth a watch: every other rune is named for what it does, and this one
    /// is named for what it is for.
    ///
    /// On, because it costs a string comparison per remnant and the thing it would catch is
    /// otherwise invisible. Off for anybody who does not want the interruption. See Curio.
    /// </summary>
    [Menu("Announce the Bait rune if it appears")]
    public ToggleNode WatchBait { get; set; } = new ToggleNode(false);


    /// <summary>
    /// How close together two explosives may be placed, in grid units.
    ///
    /// The game has a minimum and does not state it anywhere, so it was measured: five explosives
    /// put down by hand as close together as the game would accept came out 20.81, 20.81, 20.62 and
    /// 20.52 grid units apart, which put the limit at or just under 20.5 and had the default sitting
    /// a little above it on purpose.
    ///
    /// **It is exactly 20, and the comparison is strict.** Measured directly against the game from
    /// one origin:
    ///
    /// <code>
    /// (1020,1816)  d = 20.000  d^2 = 400  refused
    /// (1026,1817)  d = 19.925  d^2 = 397  refused
    /// (1021,1816)  d = 20.025  d^2 = 401  ACCEPTED
    /// (1020,1815)  d = 21.000  d^2 = 441  ACCEPTED
    /// </code>
    ///
    /// 400 refuses and 401 accepts, so the game holds squared integer distance and asks for more than
    /// 400 - `d^2 > 400`. Nothing straddles it. Checked across every capture: of 6,499 corroborated
    /// readings taken with an explosive on the ground, **not one accepted cell lies within 20 of an
    /// explosive**, and the closest accepted distance anywhere is 20.025, which is sqrt(401) - the
    /// smallest step past the limit that an integer grid can express.
    ///
    /// The old 21 therefore refused a whole ring of legal ground between 20.025 and 21.
    ///
    /// **The detonator does not count**, and that is measured rather than assumed: cells were accepted
    /// at 0.000, 1.000 and 1.414 from it, well corroborated. The first explosive may go down on top of
    /// the detonator.
    ///
    /// Still a setting rather than a constant, because a map modifier could plausibly move it - but
    /// the default is now read off the game instead of inferred from five placements. **A saved
    /// configuration keeps its old 21 and has to be changed by hand**, since nothing here migrates
    /// settings forward.
    /// </summary>
    [Menu("Explosives no closer than (grid)")]
    public RangeNode<float> ApartAtLeast { get; set; } = new RangeNode<float>(20f, 0f, 60f);

    /// <summary>
    /// What to add to the explosion radius before drawing and solving with it, in grid units.
    ///
    /// **The default is not a fudge factor, it is Detonator.MinimumExtent.** The game paints its
    /// circle at the explosion radius less the smallest an entity can be, so subtracting the same
    /// floor puts the circle drawn here exactly on the game's own - and the catch test adds each
    /// object's extent back on top, which returns the full radius for an ordinary marker.
    ///
    /// A setting rather than a constant only because a map modifier could plausibly move the
    /// circle in a way the two explosion stats do not describe. Moving it disagrees with the game's
    /// own drawing, which is the thing to check before deciding it is wrong.
    /// </summary>
    [Menu("Blast radius adjustment (grid)")]
    public RangeNode<float> CircleCorrection { get; set; } =
        new RangeNode<float>(-Detonator.MinimumExtent, -15f, 15f);



    /// <summary>
    /// The cold solve comparison: one key, and the list of searches it runs.
    ///
    /// **Its own section because the two halves lived in different tabs.** The key was here and the
    /// checkboxes deciding what it runs were in Solver, so whichever tab you were looking at, half
    /// the control was somewhere else - and the Solver tab carried six checkboxes a player has no
    /// use for. It is a measurement tool, and measurement tools live behind the debug switch.
    /// </summary>
    [JsonIgnore]
    public CustomNode ColdSolveGroup { get; set; } = Section.Of("Cold solve comparison");

    /// <summary>
    /// Clears everything learned here, then runs each ticked search over the same cold site.
    ///
    /// **A measurement needs a cold start, and a warm site is the normal case.** Everything in this
    /// plugin gets better the longer you stand in a dig site - the routed ground, the remembered
    /// markers, the last plan handed to the next solve - so two presses of the action key are two
    /// different experiments. This forgets the site, floods the router so no search is crippled by
    /// one that is only half built, waits for a complete sweep, and then gives each ticked search
    /// the whole improvement window in turn.
    ///
    /// It used to say it solved "with Restarts", which stopped being true when the bake-off went in
    /// and was never a good description afterwards: it runs whatever is ticked below, and the action
    /// key is left on whatever the Solver tab is set to, so the two halves of a comparison stay a
    /// comparison.
    ///
    /// **Unbound, like every other investigation key.** It shipped on F3, which is the one key here
    /// that costs something to press by accident: six ticked searches on a Grand site is the better
    /// part of a minute standing still. The other three are unbound for exactly that reason.
    /// </summary>
    [Menu("Cold solve comparison key")]
    public HotkeyNodeV2 ColdHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// Which searches the key above compares, ticked one by one.
    ///
    /// **Six strategies is six windows, and most of the time the question is about two of them.** A
    /// comparison runs each one for the whole improvement window in turn, so on a Grand site at
    /// eight seconds a full run is the better part of a minute of standing still - and four fifths
    /// of it is usually spent re-confirming modes nobody is asking about. Ticking two makes the same
    /// comparison in a third of the time, which is the difference between a measurement taken
    /// several times and one taken once.
    ///
    /// The ticks themselves live on the Solver tab's settings object, where the searches are named.
    /// Only the drawing is here, next to the key that acts on it.
    /// </summary>
    [JsonIgnore]
    public CustomNode ComparedUi { get; set; }

    [JsonIgnore]
    public CustomNode InvestigationGroup { get; set; } = Section.Of("Investigation");


    /// <summary>
    /// Writes the plan and the explosives actually placed, scored the same way, to a file.
    ///
    /// Unbound, like the other debug keys. Bind it, lay a chain by hand, and press it: the file says
    /// which of the two routes the objective prefers and by how much, which is the only way to tell
    /// a search that is failing from weights that are wrong.
    /// </summary>
    [Menu("Write a score card")]
    public HotkeyNodeV2 ScoreHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// How many timestamped dumps to keep, oldest deleted first.
    ///
    /// **Every F6 wrote a new file and nothing ever removed one.** Five hundred and forty one of
    /// them, a hundred and ninety five megabytes - a hundred and sixty times the size of everything
    /// the plugin actually remembers about the sites it has walked. A dump is read once, minutes
    /// after it is written, and is worthless the moment the next one exists.
    ///
    /// Two by default, which is the working number: the one just taken, and the one before it to
    /// compare against.
    /// </summary>
    [Menu("Dump key")]
    public HotkeyNodeV2 DumpHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// Takes a dump now, for when the key above is not bound.
    ///
    /// The dump is the first thing asked for when the plugin is behaving oddly, and requiring a
    /// binding before one can be taken puts a step between the question and the answer - on a key
    /// that ships unbound, so the ordinary case is that there is no key to press.
    ///
    /// It raises a flag rather than writing anything: a CustomNode is a lambda the settings parser
    /// owns and is handed neither the plugin nor the GameController, both of which Dump.Write
    /// needs. The tick that answers the key answers this too, so there is one path and not two.
    /// </summary>
    [JsonIgnore]
    public CustomNode DumpNowUi { get; set; } = new CustomNode(() =>
    {
        if (ImGui.Button("Write a dump now###aeDumpNow"))
            Dump.AskedForInSettings = true;

        if (Dump.LastWritten.Length == 0)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled($"last: {Dump.LastWritten}");
    });

    [Menu("Dump range (grid)")]
    public RangeNode<int> DumpRange { get; set; } = new RangeNode<int>(400, 50, 2000);

    /// <summary>
    /// Opens the folder the dump key writes into.
    ///
    /// **A file nobody can find is a file nobody sends.** The dump goes to a config directory three
    /// levels inside the HUD install, and the plugin knew the path all along while the person who
    /// needed it did not. One button beats a sentence explaining where to look.
    ///
    /// Made if it is not there, so the button works before the first dump rather than failing at
    /// whoever pressed it.
    /// </summary>
    [JsonIgnore]
    public CustomNode DumpFolderUi { get; set; } = new CustomNode(() =>
    {
        if (ImGui.Button("Open dump folder###openDumps"))
            DumpFolder.Open();
    });

    [Menu("Max dump files")]
    public RangeNode<int> MaxDumps { get; set; } = new RangeNode<int>(10, 1, 100);

    /// <summary>
    /// One of the three rows in the settings that earns a tooltip.
    ///
    /// Almost everything else says what it is in its own label. This cannot: the key does a
    /// different thing on each press and the order matters, and "Correlate key" gives no hint that
    /// there are three presses at three different moments in an encounter.
    /// </summary>
    [Menu("Correlate key",
        "Press once in the dig site to remember every marker, again after\n" +
        "detonating for the monsters, and again once the encounter is cleared for\n" +
        "the chests - the two arrive at different times. It is what identifies an\n" +
        "unfamiliar marker art.")]
    public HotkeyNodeV2 CorrelateHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    public DebugSettings()
    {
        ComparedUi = new CustomNode(() =>
        {
            var solver = Root?.Solver;

            if (solver == null || !ImGui.TreeNodeEx("Searches compared###comparedByCold"))
                return;

            try
            {
                ImGui.TextDisabled("Each ticked search gets the whole improvement window in turn.");
                ImGui.Separator();

                foreach (var name in SolverSettings.Comparable)
                {
                    var ticked = solver.Compares(name);

                    ImGui.PushID(name);

                    if (ImGui.Checkbox(name, ref ticked))
                        solver.Compared[name] = ticked;

                    ImGui.PopID();
                }
            }
            finally
            {
                ImGui.TreePop();
            }
        });

        DrawSpotsUi = new CustomNode(() =>
        {
            if (ImGui.Button($"Draw {SpotCount.Value} best spots###drawSpots"))
            {
                Planner.PerKind = null;
                Planner.Paired = false;
                Planner.Family = false;
                Planner.Shaped = false;
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();

            if (ImGui.Button($"per remnant###drawPerRemnant"))
            {
                Planner.PerKind = TargetKind.Remnant;
                Planner.Paired = false;
                Planner.Family = false;
                Planner.Shaped = false;
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();

            if (ImGui.Button($"per rare###drawPerRare"))
            {
                Planner.PerKind = TargetKind.Elite;
                Planner.Paired = false;
                Planner.Family = false;
                Planner.Shaped = false;
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();

            // No count in the label: this view's size is the site's, one ring per pair of heavy
            // markers, and a number in front of it would only suggest otherwise.
            if (ImGui.Button("between pairs###drawPairs"))
            {
                Planner.PerKind = null;
                Planner.Family = false;
                Planner.Shaped = false;
                Planner.Paired = true;
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();

            if (ImGui.Button("all families###drawFamilies"))
            {
                Planner.PerKind = null;
                Planner.Paired = false;
                Planner.Shaped = false;
                Planner.Family = true;
                Planner.Counts = (Root.Solver.Advanced.CandidateSpots.SpotsPerPair.Value, Root.Solver.Advanced.CandidateSpots.SpotsPerRare.Value,
                    Root.Solver.Advanced.CandidateSpots.SpotsPerRemnant.Value);
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();

            if (ImGui.Button("regions###drawRegions"))
            {
                Planner.PerKind = null;
                Planner.Paired = false;
                Planner.Family = false;
                Planner.Shaped = true;
                Planner.Counts = (Root.Solver.Advanced.CandidateSpots.SpotsPerPair.Value, Root.Solver.Advanced.CandidateSpots.SpotsPerRare.Value,
                    Root.Solver.Advanced.CandidateSpots.SpotsPerRemnant.Value);
                Planner.Wanted = SpotCount.Value;
            }

            ImGui.SameLine();
            ImGui.TextDisabled(Planner.Drawn > 0 ? $"drawing {Planner.Drawn}" : "not drawing");
        });

        Build();
    }

    private void Build() =>
        ResetEverythingUi = new CustomNode(() =>
        {
            var armed = ImGui.GetIO().KeyCtrl;

            ImGui.BeginDisabled(!armed);

            if (ImGui.Button("Reset EVERYTHING to defaults###resetEverything") && armed && Root != null)
                ResetAll.Apply(Root);

            ImGui.EndDisabled();

            if (!armed)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("hold Ctrl");
            }
        });
}

/// <summary>
/// Working around faults in the game rather than in this plugin.
///
/// **Separate because the reason is different.** Everything else here tunes what the plugin should
/// do; these exist because the client does something wrong and the cheapest answer is to route
/// around it. Each one should name the fault it works around, so that when the game is fixed the
/// setting can be deleted rather than inherited forever.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class BugsSettings
{
    /// <summary>
    /// Keep the chain off Tetzcatl while another expedition in this map is still unopened.
    ///
    /// **The "Expedition Complete" bug.** Killing this boss can raise the banner for the whole map
    /// rather than for the encounter, and a site that has not been started yet is then unable to
    /// accept explosives at all - rejoining the instance does not clear it. The warning in the log
    /// reports the mismatch when it happens; this tries to avoid causing it.
    ///
    /// Only while another site is still untouched, because on the last expedition of a map there is
    /// nothing left to brick and the boss is worth taking.
    ///
    /// It marks the marker must-avoid, which is the same thing the avoid key does by hand - so the
    /// planner routes around it and says why, rather than the chain silently losing content.
    /// Marked ONCE per site: the must-take/must-avoid key takes it straight back off and it stays
    /// off, the same offer-and-leave-it rule the value threshold follows. See Insisted.Bugged.
    /// </summary>
    [Menu("Avoid Tetzcatl while another site is unopened",
        "Killing Tetzcatl can fire \"Expedition Complete\" for the whole map, leaving a\n" +
        "site you have not started yet unable to accept explosives. Rejoining does not\n" +
        "fix it. Off on the last expedition of a map, where there is nothing left to lose.\n" +
        "Marks it once - the must-take/must-avoid key takes the mark off again for good.")]
    public ToggleNode AvoidTetzcatl { get; set; } = new ToggleNode(true);
}
