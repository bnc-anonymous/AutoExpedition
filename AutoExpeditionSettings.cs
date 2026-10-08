using System.Text.RegularExpressions;
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
    public const int Current = 21;

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
                rewards?.LineThicknessInWorld, "the reward line thickness"));
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
        // circles nor what it does to them. See PlanDisplaySettings.FlatCircles.
        if (was < 9)
            said.AddRange(CopiedFromPath(saved, "Display.ThePlan.AvoidHugging",
                settings?.Display?.ThePlan?.FlatCircles, "the flat blast circle switch"));

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


        // **13 - each line to a remnant got a thickness per surface.** One thickness served the world
        // and the minimap, and a line thick enough to read in the world covers the markers around it on
        // the map. The saved value was the world's, so it goes there; the minimap starts at its default.
        if (was < 13)
        {
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Rewards.LineThickness",
                settings?.Display?.Remnants?.Rewards?.LineThicknessInWorld, "the reward line thickness"));
            said.AddRange(CopiedFromPath(saved, "Display.Remnants.Rerolls.RollLineThickness",
                settings?.Display?.Remnants?.Rerolls?.RollLineThicknessInWorld, "the reroll line thickness"));
        }

        // **14 - "hold=" in the thread roles became "keep-opening=".** The word did not say what was held, or
        // that it only means anything to a worker given an enumerated opening. Rewritten in place rather than
        // accepted as a second spelling, so the line has one word for it. See ThreadRoles.
        if (was < 14 && settings?.Solver?.Advanced?.DestroyAndRepair?.ThreadRoles is { } roles &&
            roles.Value is { } line && Regex.IsMatch(line, @"\bhold="))
        {
            roles.Value = Regex.Replace(line, @"\bhold=", "keep-opening=");
            said.Add("renamed hold= to keep-opening= in the thread roles");
        }

        // **15 - the wave radius default went from 25 to 120 grid.** Measured over 7242 wave monsters tied to one
        // remnant by their runes: 2.8% spawned within 25 grid of it, the median 56, 92% within 120. A file still at the
        // old default moves; a value somebody chose stays.
        if (was < 15 && settings?.Recording?.WaveRadius is { } radius && Math.Abs(radius.Value - 25f) < 0.01f)
        {
            radius.Value = 120f;
            said.Add("raised the wave radius from the old default 25 to 120 grid");
        }

        // **16 - the flat blast circle switch became the flat circle switch.** It flattens the must take, must avoid and
        // take last rings too, so FlatBlastCircles named only half of it. See PlanDisplaySettings.FlatCircles.
        if (was < 16)
            said.AddRange(CopiedFromPath(saved, "Display.ThePlan.FlatBlastCircles",
                settings?.Display?.ThePlan?.FlatCircles, "the flat circle switch"));

        // **17 - the last remnant order worker holds remnants descending.** Added to a line where no worker holds remnants
        // yet and the last worker opens on remnant orders, which is the shipped line and the reorderings of it; a line
        // built otherwise is left as it was. See ThreadRoles.RemnantsHeld.Descending.
        if (was < 17 && settings?.Solver?.Advanced?.DestroyAndRepair?.ThreadRoles is { } line17 &&
            line17.Value is { } typed17 && !typed17.Contains("remnants=", StringComparison.OrdinalIgnoreCase))
        {
            var workers17 = typed17.Split(';');

            if (workers17.Length > 0 && workers17[^1].Contains("opening=remnant-order", StringComparison.OrdinalIgnoreCase))
            {
                workers17[^1] = workers17[^1].TrimEnd() + " remnants=descending";
                line17.Value = string.Join(";", workers17);
                said.Add("the last remnant order worker now holds remnants descending");
            }
        }

        // **18 - the edge point step default went from 7 to 0 grid.** A file still at the old default moves; a value
        // somebody chose stays. See CandidateSpotSettings.EdgePointStepGrid.
        if (was < 18 && settings?.Solver?.Advanced?.CandidateSpots?.EdgePointStepGrid is { } edgeStep && edgeStep.Value == 7)
        {
            edgeStep.Value = 0;
            said.Add("lowered the edge point step from the old default 7 to 0 grid");
        }

        // **19 - defaults moved to the player's (2026-10-06).** Numbers and text still at the old default move; a value
        // somebody chose stays. Switches do not move, since one left at its default cannot be told from one chosen.
        if (was < 19)
        {
            const string rolesBefore19 =
                "even opening=continue; even opening=refine; even opening=remnant-order; reach opening=remnant-order; " +
                "seg opening=remnant-order; even opening=remnant-order; reach opening=remnant-order; seg opening=remnant-order remnants=descending";
            var fresh = new AutoExpeditionSettings();

            if (settings?.Solver?.Advanced?.DestroyAndRepair?.ThreadRoles is { } roles19 &&
                string.Equals(roles19.Value?.Trim(), rolesBefore19, StringComparison.Ordinal))
            {
                roles19.Value = fresh.Solver.Advanced.DestroyAndRepair.ThreadRoles.Value;
                said.Add("reordered the thread roles to the new default");
            }

            if (settings?.Rewards?.MustTakeAbove is { } mustTake && Math.Abs(mustTake.Value - 400f) < 0.01f)
            {
                mustTake.Value = 2000f;
                said.Add("raised must take above from the old default 400 to 2000 exalts");
            }

            if (settings?.Rewards?.PointWorth is { } pointWorth && Math.Abs(pointWorth.Value - 0.5f) < 0.001f)
            {
                pointWorth.Value = 0.67f;
                said.Add("raised the point worth from the old default 0.5 to 0.67");
            }

            if (settings?.Rewards?.Overrides is { } overrides &&
                string.Equals(overrides.Value?.Trim(), "Unique Belt=1", StringComparison.Ordinal))
            {
                overrides.Value = fresh.Rewards.Overrides.Value;
                said.Add("replaced the manual price overrides, still at the old default, with the new list");
            }

            if (settings?.Display?.Remnants?.Rewards?.LineAbove is { } lineAbove && Math.Abs(lineAbove.Value - 400f) < 0.01f)
            {
                lineAbove.Value = 600f;
                said.Add("raised the reward line threshold from the old default 400 to 600 exalts");
            }

            if (settings?.Recording?.WaveRadius is { } waveRadius && Math.Abs(waveRadius.Value - 120f) < 0.01f)
            {
                waveRadius.Value = 300f;
                said.Add("raised the wave radius from the old default 120 to 300 grid");
            }
        }

        // **20 - the take last hold time moved under Debug.** See DebugSettings.TakeLastHoldMs.
        if (was < 20)
            said.AddRange(CopiedFromPath(saved, "TakeLastHoldMs", settings?.Debug?.TakeLastHoldMs, "the take last hold time"));

        // **21 - remnant drops collected where currency drops are.** A new switch, set as the currency drops one is, so a
        // file already collecting drops collects the remnants' whole drops too. See RecordingSettings.CollectRemnantDrops.
        if (was < 21 && settings?.Recording is { } recording && recording.CollectCurrencyDrops.Value)
        {
            recording.CollectRemnantDrops.Value = true;
            said.Add("turned on Collect remnant drops, as Collect currency drops is on");
        }

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
/// <summary>
/// The manual price overrides as a box of one entry a line, growing with the entries, in place of the host's single-line
/// field. Stored one entry a line; a value saved comma separated is shown split, and saved back one a line on the first
/// edit. Blank lines are dropped. See RewardSettings.Overrides.
/// </summary>
internal static class PriceOverrideEditor
{
    public static void Draw()
    {
        var settings = Dump.Settings;

        if (settings == null)
            return;

        var entries = (settings.Rewards.Overrides.Value ?? "")
            .Split(new[] { ',', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var box = string.Join("\n", entries);

        ImGui.TextUnformatted("Manual price overrides: one per line, reward name=exalts.");

        // A line more than the entries, so there is always room to type the next one.
        var height = ImGui.GetTextLineHeightWithSpacing() * (Math.Max(3, entries.Length) + 1) + 6f;

        if (ImGui.InputTextMultiline("###aeOverrides", ref box, 8192,
                new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X - 20f, height)))
        {
            var back = box.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            settings.Rewards.Overrides.Value = string.Join("\n", back);
        }
    }
}

internal static class RoleEditor
{
    /// <summary>
    /// Draws the thread roles as a box of several lines, one per worker, instead of the host's single-line
    /// field.
    ///
    /// **The host's field has a fixed buffer and the line outgrew it.** Eight workers described in full is
    /// over two hundred characters and it was being cut off, which silently changes the pool - the parser
    /// then reads a truncated last entry and reports it, but the setting is already lost. Any grammar
    /// eventually outgrows a fixed box, so the box is the thing to replace.
    ///
    /// One worker per line here, joined back with semicolons on the way in and split on the way out, so what
    /// is stored is still the one line the parser and the dump expect. Blank lines are dropped, so a stray
    /// newline is not a worker with no role.
    /// </summary>
    public static void Draw()
    {
        var settings = Dump.Settings;

        if (settings == null)
            return;

        var said = settings.Solver.Advanced.DestroyAndRepair.ThreadRoles.Value ?? "";
        var lines = said.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var box = string.Join("\n", lines);

        ImGui.TextUnformatted("One worker per line: a tearing bias, then opening= and keep-opening=.");

        // Roomy on purpose: the buffer is the thing that broke, so it is far larger than any pool needs.
        if (ImGui.InputTextMultiline("###aeRoles", ref box, 4096,
                new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X - 20f,
                    ImGui.GetTextLineHeight() * 10f)))
        {
            var back = box.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            settings.Solver.Advanced.DestroyAndRepair.ThreadRoles.Value = string.Join("; ", back);
        }

        RoleWords.Draw();
    }
}

internal static class RoleWords
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
        "it. Can be used to force the solver to path a certain way.\n" +
        "\n" +
        "Hold it (Debug > Take last hold time) to make the thing the chain's\n" +
        "last: the final explosive is the one that catches it, and nothing\n" +
        "is placed after. Hold it again to clear that. It is also a must take.")]
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
    /// The Data collection section: every switch for what the plugin writes to its dumps folder as evidence.
    /// Independent of Debug, so a tester can gather readings without playing with debug drawing on.
    ///
    /// **Named Data collection on screen, Recording in code.** Settings are saved under the property's name, so
    /// renaming the property would drop every saved switch; the label and the text moved first, and the property is
    /// left for a change that migrates the saved file. See CLAUDE.md, rule 3.
    /// </summary>
    [Menu("Data collection")]
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
    /// <summary>
    /// **Eight is a ceiling by decision, not by measurement, and it is not to be raised.**
    ///
    /// A press scores the BEST of its workers, and they are not close. Measured over five cold presses
    /// of one site in an eight second window: the winning worker reached 9,043 / 7,861 / 10,400 / 9,970
    /// / 9,496 while the MEDIAN worker of the same press sat at 7,811 / 7,657 / 7,367 / 7,452 / 7,367.
    /// Seven workers reliably find about 7,400 and one occasionally finds ten thousand, so a press is
    /// worth whatever the luckiest of eight found - and the press that scored 7,861 is the one where
    /// none of them found it.
    ///
    /// That makes more workers the most direct lever on the median, and it is deliberately refused: the
    /// game wants these cores, a HUD plugin taking every one of them is not a trade a player should
    /// have to make, and the search has to earn its result inside the budget it is given. So the lever
    /// is **more attempts per worker** instead - see Repair's restart on stagnation, which is what that
    /// decision points at.
    /// </summary>
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
    /// In this mode the presolve does not stop when a pass fails to improve: it keeps solving, each
    /// pass on a fresh draw, until the action key stops it or every remnant at the site has been
    /// rolled - and then it ends when the pass in flight ends. A roll after the key has stopped it
    /// starts it again. Needs Presolve switched on. See Rehearsal.Continuing.
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
    /// Compared on the enumerated gain: a challenger moves the advice when its gain is more than the
    /// advised remnant's by this share of it. Only remnants with a gain are candidates. See Rolling.Divert.
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
    /// How little the site score may rise over one improvement window before the first reroll advice at a site
    /// starts, as a percentage. Applies to presolve plans only, and only until the first advice at each site.
    ///
    /// The advice waits for the site's content to stop arriving first, and then for the score to stop climbing
    /// fast: a presolve on a site whose content is all known already - a benchmark on a semi-cold start - is still
    /// improving quickly, and advice weighed against its early chains is about chains that will not last. The
    /// score is the site's own, without the must-take bonus, which is a constant and would swamp a percentage.
    /// The window is the site's "Time to improve" setting. 5 is chosen; the dump reports the rise the advice
    /// started at, so it can be checked.
    /// </summary>
    [Menu("Start advice below improvement (%)")]
    public RangeNode<int> StartAdviceBelowImprovement { get; set; } = new RangeNode<int>(5, 0, 100);

    /// <summary>
    /// The longest the first reroll advice at a site waits for the score to stop climbing, in improvement windows,
    /// counted from when the site's content stopped arriving. See StartAdviceBelowImprovement.
    /// </summary>
    [Menu("Start advice after at most (windows)")]
    public RangeNode<int> StartAdviceAfterWindows { get; set; } = new RangeNode<int>(3, 1, 10);

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
    /// A second, with the reroll advice after it, is about a second's round trip.
    /// </summary>
    [Menu("Time to improve after a reroll (ms)")]
    public RangeNode<int> LoopSolveMs { get; set; } = new RangeNode<int>(1000, 100, 10000);

    /// <summary>
    /// How long each pass of the continuous reroll mode runs, or nought for the length of an ordinary presolve
    /// pass. A pass still ends early when a remnant is rolled or the site changes.
    ///
    /// Each pass starts on a new draw and keeps the standing plan as its floor, so a longer pass trades draws
    /// for search depth. Within one pass the pool's best has been within 0.7-2.3% of its end by half way in
    /// every 8s batch on Scorched Cay, which favours more draws; whether a longer pass does better has not been
    /// measured, and this is the setting to measure it with. Three times the presolve window is chosen, not
    /// measured. The kick, keep-opening and refine-after stay shares of the ordinary window, so a
    /// long pass still kicks and releases openings on the usual clock.
    /// </summary>
    [Menu("Continuous pass length (ms, 0 = as a presolve pass)")]
    public RangeNode<int> ContinuousPassMs { get; set; } = new RangeNode<int>(12000, 0, 600000);

    /// <summary>
    /// The same on a Grand Expedition. See ContinuousPassMs.
    ///
    /// Pass length was not what held continuous mode back. Measured offline on one Frigid Bluffs layout (2026-10-03),
    /// seeded with a 39,179 east-first plan: six 24s passes stalled at 42,205 while each worker but the first restarted
    /// every pass, and reached 45,403 by the fourth pass once each worker carried its own chain over. A 48s pass also
    /// got there, by giving the restarted workers enough time in one pass, which the carry makes unnecessary. See
    /// Repair.Search's own.
    /// </summary>
    [Menu("Continuous pass length in a Grand Expedition (ms, 0 = as a presolve pass)")]
    public RangeNode<int> ContinuousPassMsGrand { get; set; } = new RangeNode<int>(24000, 0, 600000);

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
    /// The beam survives as a component rather than a mode: the score card traces a chain through
    /// it to show where it is lost. See Beam.Trace.
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
}

/// <summary>
/// How much of a chain a repair tears out, which operator tears it, and how it is rebuilt.
/// See Repair.
/// </summary>
public class DestroyAndRepairSettings
{
    /// <summary>
    /// What each worker in the pool does, one entry per worker: its tearing bias, the opening it starts from,
    /// how long it keeps that opening, and whether it takes the pool's best.
    ///
    /// **One line, because three settings used to describe a worker without knowing about each other.** A
    /// tearing mix named an operator per worker, an independent-threads list named the workers that never
    /// adopt, and a modulo in the search decided which drew their own opening - three rules indexing one
    /// number. What came out was nobody's design: one worker was both the only segment-biased one and a hedge,
    /// one operator was named twice, and three workers had no bias at all.
    ///
    /// See ThreadRoles for the grammar. Unreadable words are reported rather than ignored, and the dump prints
    /// the table it understood, so a typo cannot quietly change the pool.
    ///
    /// **The shipped line, from the batches of 2026-09-29 on Scorched Cay** (Grand, 8s, draws 11-30 and 31-50).
    /// Two workers each biased seg, reach and even, three touring, three on enumerated openings held for 30%
    /// of the window. It scored a mean press maximum of 15,197 on draws 11-30, the highest of the day, with
    /// no rel workers at all: taking rel out cost nothing measurable, while four reach-biased workers did
    /// (14,991). The line it replaced held five openings for the whole window (keep-opening=100), and a pool
    /// of those measured far worse (14,326). One Grand site; not yet measured on a regular one. That line was
    /// "even opening=continue; even opening=tour; even opening=enumerated keep-opening=30; reach opening=tour;
    /// reach opening=enumerated keep-opening=30; seg opening=fresh; seg opening=tour; seg opening=enumerated
    /// keep-opening=30".
    ///
    /// **Replaced on 2026-10-04 by two workers on the pool's best and six on remnant orders.** On a Frigid Bluffs site
    /// every chain above 70,000 came from a remnant order or from the pool's best searched exhaustively, and the
    /// touring and enumerated workers ended at 55-58,000. Not yet measured on the benchmark set.
    ///
    /// **The last holds remnants descending since 2026-10-06** - every remnant must take, then every one but one, then
    /// but two - for the sites whose best chain holds them all. Four such workers lost on Stagnant Basin, where the best
    /// chain leaves remnants out (8,465 at 10 s against 8,820), so it is one, chosen by the player and being measured.
    /// See Repair.SearchDescendingRemnants.
    ///
    /// **The order is the player's since 2026-10-06**: the two even order workers first, so the two order workers that
    /// wait for the orders (Repair.OrderWorkersWaiting) both open even, then the reach pair and the seg pair. Not
    /// measured against the order before it, which interleaved even, reach and seg.
    /// </summary>
    [Menu("Thread roles")]
    public TextNode ThreadRoles { get; set; } = new TextNode(
        "even opening=continue; even opening=refine; even opening=remnant-order; even opening=remnant-order; " +
        "reach opening=remnant-order; reach opening=remnant-order; seg opening=remnant-order; seg opening=remnant-order remnants=descending");

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
    public CustomNode ThreadRolesHelp { get; set; } = new CustomNode(RoleEditor.Draw);

    /// <summary>
    /// Builds the remnant orders on the threads of the first two workers that open on them, which wait for them, when
    /// there are none for the ground yet; the other order workers start at once on their own openings and take orders
    /// as they are published. A worker given an order searches its chain with windows of two and three stops before
    /// its usual moves. Off, the orders are built on one thread in the background and every worker takes them as they
    /// arrive. On by default. See Repair.OrderWorkersWaiting.
    ///
    /// After a reset on a Frigid Bluffs site (2026-10-04) the background build took 19.4 s and the workers climbed on
    /// fallback openings meanwhile; split between the threads of the waiting workers, the same build took 2.3 s
    /// offline where one thread took 3.2 s. See RemnantOrder.SearchAcrossThreads and ExhaustiveSpotSearch.
    /// </summary>
    [Menu("Remnant orders first")]
    public ToggleNode RemnantOrdersFirst { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The smallest run of links Destroy and repair tears out at once.
    ///
    /// One is cheap and finds the obvious rearrangement. It is also the only size that can be tried
    /// thousands of times in a window, which is why it stays the floor rather than being raised when
    /// the larger tears go in.
    /// </summary>
    /// <summary>
    /// Stop each worker after this many rounds instead of on the clock. Nought uses the clock, which is what a
    /// real press does.
    ///
    /// **For measurement, because a press bounded by time is not reproducible and the whole pairing depends on
    /// it.** Every seed in the search is a constant plus the worker's number plus the draw, so two batches on
    /// draw five ought to build the identical chain - that is what common random numbers are for, and it is why
    /// a batch walks the same draws every time. It does not happen: a press does however many rounds fit in its
    /// window, and that count moves with everything else on the machine. Measured on one site, two batches at an
    /// identical configuration over the same draws returned medians of 8,634 and 10,025, which is three times
    /// the error a ten press median should have.
    ///
    /// With a round budget the work is the same every time, so a draw reproduces and a difference between two
    /// configurations is the configuration.
    ///
    /// **Per worker, which is not the figure the press table prints.** That column is the pool's total, so an
    /// eight second press reading 10,700 rounds did about 1,340 apiece; the per-worker count is the one in the
    /// destroy and repair line, measured at 1,173 to 2,515 on a Grand site. A budget of 1,500 is about an eight
    /// second press at the pace recently measured, and 2,500 is the pace of a faster session - setting it to the
    /// pool total would be a press seven times longer than anybody is waiting for.
    ///
    /// **It does not make a press deterministic outright.** What it fixes is the improvement loop, which is where
    /// the window goes; the opening's own cap is lifted while it is in force (see Repair.Search), and anything else
    /// bounded by wall clock can still make two presses differ. Leave it at nought for play - a round budget makes a press take as long
    /// as it takes, which on a slow frame is longer than the window a player is waiting for.
    /// </summary>
    [Menu("(MEASUREMENT) Rounds per worker, 0 for the clock")]
    public RangeNode<int> RoundsPerWorker { get; set; } = new RangeNode<int>(0, 0, 20000);

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
    /// <summary>
    /// **The property is the odd name out and has been left alone deliberately.** The label is
    /// "At a kick, restart if behind the pool by (%)", the environment carries it as RestartThreshold and
    /// the dump prints it in the label's words; this property still says RescueBelow because renaming it
    /// abandons the value people have saved, where the label and the doc cost nothing to move. Rename
    /// it in a change that says so and migrates the stored key.
    ///
    /// **Measured in the units of what the site pays, not of the score.** Solving.Behind takes the
    /// margin against the plain figure - see _sharedPlain - so ten per cent here means ten per cent of
    /// the content on offer. On a site with a held must-take that is far stronger than it used to be,
    /// because the score is mostly the insistence ceiling: at 9,506 plain of 34,908 total, ten per
    /// cent went from asking 3,490 points to asking 951, and thirteen restarts fired in one press
    /// where every earlier press had none.
    ///
    /// **One of three settings that work as one mechanism.** The two kick settings decide when a worker is
    /// interrupted; at each kick, this and "At a kick, restart if no progress for (ms)" decide whether the
    /// interruption is a shake of its best chain or a restart from nothing, and either one being true restarts.
    /// </summary>
    [Menu("At a kick, restart if behind the pool by (%)")]
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
    /// **Four on the strongest result measured here; shipped at eight with the tuned defaults of 2026-09-24
    /// (795ea5c), with the measurement for eight not recorded here.** Seven cold presses with the pair
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
    /// How long a worker may go without progress before it is kicked, as a percentage of the improvement
    /// window. Nought kicks on the round count alone. See StagnationKickRounds. A kick shakes the worker's best
    /// chain, unless one of the two restart settings says to start again instead.
    ///
    /// The round count on its own ties the kick to how fast a worker runs. Measured on Craggy Peninsula, twenty
    /// explosives: most workers ran 25 to 30ms a round, so 500 rounds was 12 to 15 seconds, longer than the eight
    /// second window, and they took one or two kicks a press where the fastest took seventeen. The tear size
    /// climbs towards the kick too, so those workers also rarely tried the larger tears.
    ///
    /// Whichever comes first kicks. 50 is chosen, not measured. Ignored while measuring with a round budget.
    /// </summary>
    [Menu("Kick after no progress (% of window)")]
    public RangeNode<int> StagnationKickPercent { get; set; } = new RangeNode<int>(50, 0, 100);

    /// <summary>
    /// How many rounds a worker may run without progress before it is kicked, or nought to kick on the clock
    /// alone. With both this and StagnationKickPercent at nought a worker is never kicked.
    ///
    /// A round is a different length of time on every site. Measured on Scorched Cay, fifteen explosives, 8s
    /// fixed: workers ran 220 to 950 rounds a press, so 500 was half a press for one and never reached by
    /// another. Whichever of this and the clock comes first kicks. 500 is the value the constant had; it was
    /// chosen, not measured.
    /// </summary>
    [Menu("Kick after no progress (rounds, 0 = clock only)")]
    public RangeNode<int> StagnationKickRounds { get; set; } = new RangeNode<int>(500, 0, 5000);

    /// <summary>
    /// How long a worker's current chain may go without improving before it is thrown away and the worker
    /// starts again from a fresh construction, or nought for never on this account. Checked at the worker's
    /// kicks, so it acts at the first kick after the time has passed. Its record is kept either way.
    ///
    /// For long solves - the continuous reroll mode with a long pass - where a worker that has settled would
    /// otherwise be shaken and polished back to the same chain until the solve ends. The behind-the-pool
    /// restart covers a worker that is far below the rest; this covers one that has simply stopped. The
    /// worker continuing from the standing plan and refining workers are exempt, since staying on their chain
    /// is what they are for. Not measured.
    ///
    /// **Close kin to a setting removed for not paying.** "Restart after barren kicks" restarted a worker after
    /// a number of kicks without a record, and on ordinary 8s presses it did not pay; this is the same idea on
    /// the clock, kept for long solves, which that measurement did not cover. Set it above the kick interval,
    /// or every kick restarts and no worker is ever shaken.
    /// </summary>
    [Menu("At a kick, restart if no progress for (ms, 0 = off)")]
    public RangeNode<int> StallRestartMs { get; set; } = new RangeNode<int>(0, 0, 60000);

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
    /// one.** It builds a chain three ways - the last solve's answer, four greedy starts, then a
    /// polish - and returns when it has finished, not when it has spent a share of
    /// anything. Measured on a Grand site: four to eight seconds, against an improvement window of
    /// eight.
    ///
    /// That is worth capping only if the tearing is worth more than the construction, which is a
    /// measurement rather than an opinion and has not been taken. On one site the opening produced
    /// the whole score and 290 rounds of tearing added 0.1%; on another the threads gained 4 to 6%
    /// after opening. It shipped at 2,000ms with the tuned defaults of 2026-09-24 (795ea5c); the
    /// measurement that chose that figure is not recorded here.
    /// </summary>
    [Menu("Opening timeout (ms, 0 = unlimited)")]
    public RangeNode<int> OpeningMs { get; set; } = new RangeNode<int>(2000, 0, 20000);

    [Menu("Opening shakes")]
    public RangeNode<int> OpeningShakes { get; set; } = new RangeNode<int>(2, 1, 12);

    /// <summary>
    /// How many near-best spots a randomised opening may choose among at each link. One is greedy.
    ///
    /// **This is GRASP's restricted candidate list, and it is what the draw actually controls.** An
    /// exploring worker builds its opening by taking the best `n` spots at each step and picking one at
    /// random, so this number is the whole of the difference between eight workers opening in eight
    /// places and eight workers opening in one.
    ///
    /// **Why it is the lever worth measuring.** Paired over the same ten draws, turning the barren
    /// restart on left seven of ten presses bit-identical and moved the median by nothing - while the
    /// draws themselves ranged from 7,879 to 10,493. The configuration is worth about nought and the
    /// draw is worth 2,600, and what the draw feeds is this. The two best presses of that batch were
    /// also the only two where the MEDIAN worker was high, 9,043 and 9,458 against the usual 7,400 - so
    /// a good press is several workers opening in a good region rather than one getting lucky.
    ///
    /// Five is the figure this has always used, as `1 + random.Next(5)`, so five changes nothing.
    /// Larger means openings that differ more and are individually worse; the window is worth about
    /// +217% over an opening, so worse-but-different has been the winning trade every time it has been
    /// measured here. See Repair.Opening and expedition_solve_plan.md 7.
    /// </summary>
    [Menu("Opening spots to choose among")]
    public RangeNode<int> OpeningChoices { get; set; } = new RangeNode<int>(5, 1, 24);

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
    /// <summary>
    /// Whether the pool's best chain is walked towards the other workers' chains once they have all
    /// finished, keeping the best chain found on the way.
    ///
    /// **Seven of eight results are thrown away every press.** Measured on this site, the winner and the
    /// median worker had NOUGHT links in common out of fifteen, and the median collected 230 points more
    /// content while scoring 2,438 less - so the discarded chains are not weak versions of the answer,
    /// they are different answers. Path relinking is what combines two of those into a third that
    /// neither worker would have found.
    ///
    /// With ALNS - which this plugin has - it is one of the two families that lead on the team
    /// orienteering problem, which is the closest named form of this puzzle. The GRASP half of it was
    /// already here in the randomised openings; this is the half that was missing.
    ///
    /// Runs after every worker has stopped, so it takes nothing from the search: about 120 scored chains
    /// per pair at a measured 42us each, three pairs, a few tens of milliseconds on the end of a press.
    ///
    /// **On by default, measured on Scorched Cay (Grand, fifteen explosives, 8s, draws 1-10, 2026-09-29).**
    /// It improved 6 of 10 presses by 1,836 in total, by its own count, with presses no longer: +813 on one
    /// draw, taking it to 16,428, and 209 to 376 on four more. Its own count is the measurement rather than a
    /// comparison of two batches, because it runs after the search and keeps its answer only when it scores
    /// higher, so it cannot lower a press; on the draws where both batches' searches reproduced, the pool's
    /// best before relinking matched the batch without it exactly. Not yet measured on a regular site.
    /// </summary>
    [Menu("Relink the best chain with the others")]
    public ToggleNode Relink { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How many links of the opening are enumerated.
    ///
    /// Two: a direction and the corridor out of it. The depth the pool actually agrees on is measured per
    /// press - it has read 2 on one and 15 on another - and two is shallow enough that a wrong enumeration
    /// costs little, since both links stay movable. Deeper commits more of the chain to a judgement made
    /// without knowing the tail, and the cost grows with each level.
    /// </summary>
    [Menu("Enumerated opening links")]
    public RangeNode<int> OpeningLinks { get; set; } = new RangeNode<int>(2, 1, 5);

    /// <summary>
    /// Whether a chain may be re-ordered by reversing a run of its links.
    ///
    /// **The chain's order is worth thousands and nothing in the search could change it.** Measured on
    /// one press: the winning chain and the median worker's had nought links in common, the median
    /// collected 230 points MORE content over 62 markers the winner missed, and scored 2,438 LESS. The
    /// score is content plus propagation, propagation pays a rune over the monsters unearthed AFTER the
    /// remnant carrying it, so a chain can take everything and arrange it so that nothing multiplies.
    ///
    /// The exhaustive setting below cannot answer this on a Grand site - it refuses any chain longer
    /// than its limit, and fifteen links has 1.3 trillion orderings - so this is the 2-opt version: 105
    /// reversals on a fifteen link chain, against a measured 42us a score.
    ///
    /// **Applied once, to the chain a worker hands back, and nowhere else.** It was first applied at the
    /// opening and on every record, which is a different thing from refining an answer: a better chain
    /// mid-search descends into a different basin afterwards, sometimes a worse one. Measured over ten
    /// matched draws it produced the best result of the batch on two of them and cost 2,692 on a third,
    /// net -1,130. As the last thing a worker does it can only add to what it returns.
    ///
    /// **On by default, on ten matched draws.** Net +1,347 against the same ten with it off, the median
    /// press 9,459 to 9,764, presses reaching 10,000 three of ten to five of ten, and no time cost -
    /// 8.2 to 8.4 seconds against 8.2 to 8.7. Two draws gained 663 and 1,333 and the rest were inside
    /// the wall-clock jitter of a few hundred points. It is the only change measured this way that has
    /// cleared that floor.
    ///
    /// Compared with PressHistory, which walks the same ten draws whatever the setting says, so the
    /// comparison is press for press rather than distribution against distribution. Anything under about
    /// 700 net over ten is jitter. See expedition_solve_plan.md section 7.
    /// </summary>
    [Menu("Reverse runs of links")]
    public ToggleNode ReverseRuns { get; set; } = new ToggleNode(true);

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

}

/// <summary>
/// Which edge points toward neighbours the search's shortlist is offered, on top of the richest and most spread
/// spots it takes by score. SeedFamilies is the record these fill in. See Repair.Shortlist and
/// Planner.EdgePointsTowardNeighbours.
/// </summary>
public class CandidateSpotSettings
{
    /// <summary>
    /// Whether points on the catch edge of each heavy marker, leading towards each heavy marker or rare in reach, are
    /// offered. Heavy is the openings' test - worth ten ordinary markers, counting what it passes on and its switches -
    /// so it is usually the remnants and relics, can be a rich chest, and late in a chain can be a rare. Measured on
    /// one site (2026-09-30): of the ten links of the two best chains found there before these were offered, seven
    /// sat exactly on one of its 79 points and the rest within 7 grid, and offering them took the median press from
    /// 9,131 to 9,895. See Planner.EdgePointsTowardNeighbours.
    /// </summary>
    [Menu("Heavy edge points")]
    public ToggleNode HeavyEdgePoints { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether points on the catch edge of each rare, leading towards each heavy marker or rare in reach, are offered,
    /// so a rare standing far from heavy content still gets points leading to what is in reach. Independent of Heavy
    /// edge points. Not yet measured. See Planner.EdgePointsTowardNeighbours.
    /// </summary>
    [Menu("Rare edge points")]
    public ToggleNode RareEdgePoints { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How much nearer a neighbour an edge point must end to be worth a point of its own, in grid. Within one direction,
    /// a point catching less is kept only if it ends this much nearer than the richer point before it; across
    /// directions, a point is folded into another catching exactly the same markers if that one ends within this of it
    /// towards every neighbour it leads to. Nought keeps every trade of content for distance. Nought is the default:
    /// against seven it was level or slightly ahead at 10 s over four Grand sites and two draws (2026-10-06, offline -
    /// Frigid 76,791/74,964 against 73,543/74,160, Grazed 11,917/11,849 against 11,821/11,833), for more points (719
    /// against 298 on Stagnant Basin). Seven had been chosen, not measured. It was a percentage of the blast radius,
    /// under another name so a saved percentage is not read as grid. See Planner.EdgePointsTowardNeighbours.
    /// </summary>
    [Menu("Edge point step (grid)")]
    public RangeNode<int> EdgePointStepGrid { get; set; } = new RangeNode<int>(0, 0, 10);
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

    /// <summary>
    /// Cuts the plan and the debug drawing off at the edge of an open side panel, such as the inventory, the character
    /// sheet or the stash, so nothing is painted over them, and draws nothing on the screen while the Escape menu is
    /// open, since where the menu sits cannot be read.
    ///
    /// The HUD draws on top of the game and cannot go behind a panel, so this clips to the largest band of the screen
    /// the panels leave clear; see Panels.ClearOfPanels. The score area is not clipped: it sits low enough that no panel
    /// reaches it. Neither are the Runeshape Combinations prices, the warning under the cursor or the reference windows.
    /// </summary>
    [Menu("Hide behind panels")]
    public ToggleNode HideBehindPanels { get; set; } = new ToggleNode(true);

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
    /// Whether the circles drawn in the world - the blast circles, and the must take, must avoid and take last rings -
    /// lie flat at their middle's height rather than following the ground. The argument below is the blast circles';
    /// the marks follow the same switch so the two always look alike.
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
    [Menu("Draw circles without terrain elevation")]
    public ToggleNode FlatCircles { get; set; } = new ToggleNode(true);
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
    /// Placement warnings - Walk closer, Refused, Choose and the rest - written under the mouse cursor instead of on the
    /// score area's last line, where they went unnoticed. Off puts them back in the score area. See CursorWarning.
    /// </summary>
    [Menu("Cursor warnings", "Show placement warnings under the mouse cursor\n" +
        "instead of in the score area.")]
    public ToggleNode CursorWarnings { get; set; } = new ToggleNode(true);

    /// <summary>How long a cursor warning stays up, in seconds. A new placement run clears it sooner. See CursorWarnings.</summary>
    [Menu("Cursor warning duration (s)")]
    public RangeNode<int> CursorWarningSeconds { get; set; } = new RangeNode<int>(4, 1, 30);

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
    /// On by default since 2026-10-06, the player's choice. It is how the content taxonomy was worked out - chestmarker3 is rare,
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
    public ToggleNode ShowArtNames { get; set; } = new ToggleNode(true);

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
    public RangeNode<float> LineAbove { get; set; } = new RangeNode<float>(600f, 0f, 5000f);

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
    public RangeNode<int> LineWithin { get; set; } = new RangeNode<int>(1000, 0, 2000);

    /// <summary>
    /// Stops the line to a remnant once an explosive that is already down covers it, on both surfaces. The
    /// line is there to say a remnant is worth walking to; once a bomb takes it, the reward is decided and the
    /// line only points at ground the chain has already dealt with, for as long as the site lasts.
    ///
    /// Covered by the same test the barrels use - blast plus the remnant's own extent, from each explosive
    /// down. See Overlay.RemnantsUnderPlaced.
    /// </summary>
    [Menu("Stop the line once a placed explosive covers it")]
    public ToggleNode HideLineOnceCaught { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Draws the line towards each rich remnant in the world, from the player's feet, clipped to the
    /// screen edge when the remnant is off screen. Which remnants get one is decided by the two settings
    /// above. See Overlay.WantsRewardLine.
    /// </summary>
    [Menu("Draw line in world")]
    public ToggleNode LineInWorld { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How thick the reward lines are drawn in the world, in whole pixels.
    ///
    /// Two settings rather than one because the two surfaces are different scales: a line thick enough
    /// to read against a lit dig site covers the markers around it on the minimap.
    /// </summary>
    [Menu("Line thickness in world")]
    public RangeNode<int> LineThicknessInWorld { get; set; } = new RangeNode<int>(3, 1, 5);

    /// <summary>
    /// Draws the same lines on the minimap and the large map, from the player to each rich remnant.
    /// The same remnants as the world lines. See Minimap.RewardLines.
    /// </summary>
    [Menu("Draw line on minimap")]
    public ToggleNode LineOnMinimap { get; set; } = new ToggleNode(false);

    /// <summary>How thick the reward lines are drawn on the map, in whole pixels. See LineThicknessInWorld.</summary>
    [Menu("Line thickness on minimap")]
    public RangeNode<int> LineThicknessOnMinimap { get; set; } = new RangeNode<int>(1, 1, 5);

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
    [Menu("Highlight reroll button")]
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
    /// Draws a line towards the remnant worth rolling, in the world. Off by default: the minimap line
    /// answers "which way" without crossing the play area. See RollLineOnMinimap.
    ///
    /// A new property rather than the old RollLine with a new default, because a saved value outlives a
    /// changed default - every settings file that had the old one on would have kept it on.
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
    [Menu("Draw line in world")]
    public ToggleNode RollLineInWorld { get; set; } = new ToggleNode(true);

    /// <summary>
    /// How thick the line to the next planned reroll is drawn in the world.
    ///
    /// Three pixels by default, because a thinner line gets lost against a lit dig site. Separate from
    /// the minimap's, because a line thick enough to read in the world covers the markers around it on
    /// the map. See RollLineThicknessOnMinimap.
    ///
    /// Whole pixels, like every other line thickness here bar the plan's three. A line is drawn a
    /// pixel at a time, so a fractional setting offers a choice the screen cannot show.
    /// </summary>
    [Menu("Line thickness in world")]
    public RangeNode<int> RollLineThicknessInWorld { get; set; } = new RangeNode<int>(3, 1, 5);

    /// <summary>
    /// Draws the line towards the remnant worth rolling on the minimap and the large map, from the player
    /// to the remnant, in the highlight colour. See Minimap.Rolls.
    /// </summary>
    [Menu("Draw line on minimap")]
    public ToggleNode RollLineOnMinimap { get; set; } = new ToggleNode(false);

    /// <summary>How thick the line to the next planned reroll is drawn on the map. See RollLineThicknessInWorld.</summary>
    [Menu("Line thickness on minimap")]
    public RangeNode<int> RollLineThicknessOnMinimap { get; set; } = new RangeNode<int>(1, 1, 5);

    /// <summary>
    /// The reroll expected value - what a roll is worth, as one figure beside the Liquid Verisium button
    /// of the remnant the advice picks.
    ///
    /// In the yellow the game borders a propagating slot in, because runes are all the comparison
    /// measures: both sides of it are scored with the reward set aside. See Rolling.ScoreRollOutcomes.
    ///
    /// It was three figures - a total, a rune half and a reward half - which said one thing three
    /// times once the reward left the comparison.
    ///
    /// A display setting rather than a debug one. It was under Debug behind "Explain reroll
    /// decisions" when it was a sentence, and it stayed there after it became a number - so it was
    /// both misnamed and in the one group a player turns off wholesale.
    /// </summary>

    [Menu("Show reroll expected value")]
    public ToggleNode RollFigures { get; set; } = new ToggleNode(true);

    /// <summary>
    /// The reroll expected value on every remnant that has a verdict, rather than on the advised one only.
    ///
    /// **Off, because the advice is one remnant at a time and this is not advice.** Rolling changes
    /// the site, so which remnant is worth rolling next is not knowable until this one has been
    /// rolled and the chain solved again - see RollLineInWorld, which draws one line for the same reason.
    /// A number on every label is a reading of the site rather than a recommendation, and it is
    /// worth having while deciding whether the advice is sane.
    ///
    /// Unlike the line and the border, this does not stop at the remnants worth rolling: a remnant
    /// the advice says to KEEP has an expected value too, and it is usually negative, which is the
    /// half of the argument the other setting never shows.
    /// </summary>
    [Menu("Show reroll expected value on all remnants")]
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

    /// <summary>
    /// A rune this remnant propagates that an earlier remnant in the chain already propagates, in the line under the
    /// remnant and in each row of the Runeshape Combinations window: its socket adds nothing to the waves after it. See
    /// Overlay.RuneLinePieces.
    /// </summary>
    [Menu("Upstream duplicate colour")]
    public ColorNode UpstreamDuplicateColour { get; set; } = new ColorNode(Color.FromArgb(255, 235, 90, 90));

    /// <summary>
    /// A rune this remnant is the first to propagate that a later remnant in the chain propagates as well, with the
    /// combination the plan takes there, in the line under the remnant and in each row of the Runeshape Combinations
    /// window. See Overlay.RuneLinePieces.
    /// </summary>
    [Menu("Downstream duplicate colour")]
    public ColorNode DownstreamDuplicateColour { get; set; } = new ColorNode(Color.FromArgb(255, 255, 150, 50));

    /// <summary>
    /// A ring around each rare monster carrying the Bond rune, at the range within which it can pass a modifier on
    /// to, and heal, another rare when it dies. 80, from the stat's own name, drawn as grid cells until the unit
    /// is measured. Purple while another rare is inside it, the warning colour while none is. See BondTransfer.
    ///
    /// Moved here from Display and renamed from BondTransferRadius, so a value saved under the old name is not
    /// carried over. Off by default since 2026-10-06.
    /// </summary>
    [Menu("Draw Bond Rune radius (Rare Monsters may transfer a Mod on death, 80 units)")]
    public ToggleNode BondRuneRadius { get; set; } = new ToggleNode(false);
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
    /// How far above the ground the unscouted layer is drawn on the large map, in world units, so its edges meet the
    /// walls the map outlines rather than the ground at their foot.
    ///
    /// The map draws a wall at its top, which on screen stands above the ground it encloses, so a layer drawn at ground
    /// height ran past the outline at the bottom of each area and stopped short of it at the top. Placed at ground height
    /// two ways - through GridToMap and as Radar places its map - the layer agreed with both to the pixel and still sat
    /// about 5 px low on an Exhumed Ruins site (2026-10-05). A hundred lined it up with the outlines by eye there. The
    /// small map is not moved. See Wash.CornersAboutPlayer.
    /// </summary>
    [Menu("Unscouted height offset (world units)")]
    public RangeNode<int> UnscoutedHeightOffset { get; set; } = new RangeNode<int>(100, -300, 300);

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
    public RangeNode<float> PointWorth { get; set; } = new RangeNode<float>(0.67f, 0f, 5f);

    /// <summary>
    /// A remnant reward worth less than this counts for nothing in the plan, the reward choice and the reroll advice,
    /// so those choices are made on runes and the chain alone.
    ///
    /// Most of an expedition's value is in what the monsters drop: chains on Grand sites drop several divines, at
    /// about 700 exalts each, against rewards of a few exalts that decided between two recipes with the same
    /// propagating rune - Ancient Rune of Retaliation at 14.8ex was taken over Ancient Rune of Dueling at 6.7ex, which
    /// put one more distinct rune on the remnant's waves. A reward worth building the chain around is above this and
    /// usually a must take as well. In exalts. 100 is chosen, not measured; nought counts every reward.
    /// </summary>
    [Menu("Ignore rewards below (exalts)")]
    public RangeNode<float> IgnoreRewardsBelow { get; set; } = new RangeNode<float>(100f, 0f, 2000f);

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
    public RangeNode<float> MustTakeAbove { get; set; } = new RangeNode<float>(2000f, 0f, 5000f);

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
    /// Prices the plugin cannot look up, written down by hand. "name=value", one a line.
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
    /// The generic unique rewards are worth very little and the belt is the best of them, so naming the
    /// belt alone says the whole of them: the others stay at nothing and the belt wins wherever it is
    /// offered against them. An exalt is enough to stop it reading as EMPTY without pulling a chain
    /// towards it. The other shipped entries are the player's own prices (2026-10-06), chosen, not
    /// looked up.
    ///
    /// This replaced a preference list that named unpriced rewards in order. An order says the belt
    /// beats the ring and cannot say by how much, so it could break a tie and never weigh against
    /// anything else. A price can.
    ///
    /// A saved file keeps whatever it already holds, except one still at the belt alone, the default
    /// before these, which migration 19 moves to them. See ConfigVersion.
    ///
    /// **One entry a line**, edited in a box that grows with them (PriceOverrideEditor); the single-line field is hidden.
    /// Commas still separate entries, so a value saved comma separated reads the same. Shipped with the player's list
    /// since 2026-10-06.
    /// </summary>
    [IgnoreMenu]
    public TextNode Overrides { get; set; } = new TextNode(
        "Unique Belt=1\nAldur's Legacy=350001\nPerfect Flux=25001\nAldur's Saga=200001\nWarding Rune of Disintegration=20");

    /// <summary>The overrides as a box of one entry a line. See Overrides and PriceOverrideEditor.</summary>
    [JsonIgnore]
    public CustomNode OverridesUi { get; set; } = new CustomNode(PriceOverrideEditor.Draw);

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
    [Menu("Collect dig site spawns",
        "Writes, in the dumps folder: spawns.csv and arrivals.csv - what each\n" +
        "dig site's markers and remnants unearth, monster by monster. The\n" +
        "switches below add to it, and need it on.")]
    public ToggleNode RecordSpawns { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Whether the spawn census writes every counted monster's death to dumps/deaths.csv - what a drop rate is out of.
    /// Deaths are still watched while drops or Bond transfers are collected, since those are credited to them; this only
    /// decides whether the file is written. Needs Collect dig site spawns. See Spawns.Deaths.
    /// </summary>
    [Menu("Collect monster deaths",
        "Writes, in the dumps folder: deaths.csv - each monster's death, where\n" +
        "and when. Needs \"Collect dig site spawns\".")]
    public ToggleNode CollectMonsterDeaths { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Whether the spawn census records drops of the currencies named below to dumps/drops.csv. Needs Collect dig site
    /// spawns. See Spawns.Drops.
    /// </summary>
    [Menu("Collect currency drops",
        "Writes, in the dumps folder: drops.csv - each drop of the currencies\n" +
        "named below, with the monster or remnant that dropped it - and\n" +
        "chests.csv - each chest opened, so a chest's drops can be told from\n" +
        "a remnant's. Needs \"Collect dig site spawns\".")]
    public ToggleNode CollectCurrencyDrops { get; set; } = new ToggleNode(false);

    /// <summary>
    /// The currencies whose drops the spawn census collects while Collect currency drops is on, by base name, separated
    /// by ";". Empty collects none.
    ///
    /// Each drop goes to dumps/drops.csv with where and when it appeared and the monster most likely to have dropped
    /// it: the nearest death within a few grid in the seconds before. One that lands at a remnant as it completes - its
    /// reward, or what it drops when it shatters - is credited to that remnant and to no monster. Every death of a monster the census counted goes to dumps/deaths.csv, which is what
    /// a drop rate is out of, and arrivals.csv carries each monster's id so its rarity and modifiers can be joined to
    /// both. See Spawns.Drops and Spawns.Deaths.
    ///
    /// A few currencies rather than everything: their worth is stable and their names are unambiguous.
    ///
    /// **Verisium as a measure of what a remnant's waves brought.** It drops only from a remnant, in stacks of up to
    /// 1000, more of it the more monsters were killed in that remnant's waves (reported from play, not yet measured);
    /// three or four full stacks mark a remnant whose waves were big. So per remnant it checks how many monsters the
    /// plan expected there against how many came, and whether rares count for more than the rest.
    /// </summary>
    [Menu("Currencies to collect",
        "The currencies Collect currency drops records, by base name,\n" +
        "separated by ;. Writes no file of its own.")]
    public TextNode DropsToRecord { get; set; } = new TextNode("Chaos Orb; Orb of Annulment; Divine Orb; Verisium");

    /// <summary>
    /// Whether the spawn census records every item a remnant drops as it completes - its reward and what it drops when it
    /// shatters, whatever the item - to dumps/remnant_items.csv, with each item's base name, class, rarity, item level and
    /// stack. Off by default with the rest of data collection. See Spawns.RemnantItem.
    ///
    /// Apart from Collect currency drops because it measures something different: what a remnant's whole drop is made of,
    /// where that one follows four named currencies to the monster or remnant that dropped them. Four currencies gave too
    /// few loot events to tell one rune's effect on loot from luck (2026-10-07).
    /// </summary>
    [Menu("Collect remnant drops",
        "Writes, in the dumps folder: remnant_items.csv - every item each remnant\n" +
        "drops as it completes, with its name, class, rarity and stack.\n" +
        "Needs \"Collect dig site spawns\".")]
    public ToggleNode CollectRemnantDrops { get; set; } = new ToggleNode(false);

    /// <summary>
    /// What the spawn census has seen at this dig site - monsters unearthed, seen dying, vanished, and drops recorded -
    /// live, the same line the dump prints. See Spawns.Describe.
    /// </summary>
    [JsonIgnore]
    public CustomNode SpawnCensusStatusUi { get; set; } = new CustomNode(() =>
        ImGui.TextWrapped("Spawn census: " + (Safe.Read(() => Dump.Spawns?.Describe(), null) ?? "not running")));

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
    [Menu("Collect remnants seen",
        "Writes, in the dumps folder: remnants.csv and remnant_offers.csv -\n" +
        "every remnant seen and the combinations it offered.")]
    public ToggleNode Census { get; set; } = new ToggleNode(false);

    /// <summary>
    /// How far from a remnant a monster still counts as one of its waves, in grid units.
    ///
    /// A remnant does not unearth its monsters all at once - it holds the next wave back until the
    /// last one is dead - so a time window cannot bound it. Space bounds it, loosely: waves arrive
    /// around the remnant, not at it.
    ///
    /// **300 by default since 2026-10-06, the player's choice; 120 was the measured one.** It widens what is recorded,
    /// and a monster between two remnants goes to the nearer.
    ///
    /// **120, measured** (2026-10-01): of 7242 wave monsters tied to one remnant by their rune modifiers over 16
    /// runs, 2.8% spawned within 25 grid of it - the old default - the median 56, 90% within 101 and 92% within
    /// 120. Remnants on one site sit 50 to 85 grid from their nearest neighbour, so radii overlap; a monster is
    /// credited to the nearer remnant. dumps/arrivals.csv keeps every arrival's position and time either way, so the
    /// attribution can be redone afterwards at any radius. This radius also widens what the census records at all:
    /// a monster this near a remnant is kept even when it is further than the site limit from every explosive. See
    /// Spawns.NearRemnant.
    /// </summary>
    [Menu("Count remnant waves within (grid)",
        "How far from a remnant a monster counts as one of its waves in\n" +
        "spawns.csv and arrivals.csv. Writes no file of its own.")]
    public RangeNode<float> WaveRadius { get; set; } = new RangeNode<float>(300f, 5f, 400f);

    /// <summary>
    /// Whether each finished solve writes a line to dumps/trials.csv - strategy, site, score and time - so strategies
    /// can be compared on the same site afterwards. Nothing in the plugin reads the file. Off by default: it is for
    /// comparing solver strategies, not something a tester needs. See Trials.
    /// </summary>
    [Menu("Collect solve trials", "Writes, in the dumps folder: trials.csv - a line per finished solve.")]
    public ToggleNode CollectSolveTrials { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Whether the plugin notes how far from the placement circle each marker lights up and goes dark, writing
    /// dumps/marker_extent.csv and its per-art summary. Nothing in the scoring reads them. Off by default: it is for
    /// tuning the blast circle, not something a tester needs. See Boundary.
    /// </summary>
    [Menu("Collect marker edge readings",
        "Writes, in the dumps folder: marker_extent.csv and marker_extent.txt -\n" +
        "how far from the placement circle each marker lights up and goes dark.")]
    public ToggleNode CollectMarkerEdges { get; set; } = new ToggleNode(false);



    /// <summary>
    /// Writes when each marker's entity loads and unloads to dumps/streaming.csv.
    ///
    /// **This ran under no setting at all.** It is on the sweep's rhythm rather than every frame,
    /// which is why it never showed up as a cost worth chasing, but it is a file being appended to
    /// while you play and there was no way to stop it short of unloading the plugin.
    /// </summary>
    [Menu("Collect marker loading",
        "Writes, in the dumps folder: streaming.csv - when marker entities\n" +
        "load and unload.")]
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
    [Menu("Watch for the expedition complete banner",
        "Writes no file. Warns in the log when more Expedition Complete\n" +
        "banners show than dig sites were started.")]
    public ToggleNode WatchFinished { get; set; } = new ToggleNode(false);

    /// <summary>
    /// Whether the spawn census watches what Bond moves, writing dumps/bond_deaths.csv and dumps/bond_gains.csv. It re-reads
    /// every counted monster's affixes four times a second while a Bond monster lives, which is its only cost. Needs
    /// Collect dig site spawns. See Spawns.BondTransfers.
    /// </summary>
    [Menu("Collect Bond transfers",
        "Writes, in the dumps folder: bond_deaths.csv and bond_gains.csv - each\n" +
        "Bond monster's death and every modifier a monster gains near one.\n" +
        "Needs \"Collect dig site spawns\".")]
    public ToggleNode CollectBondTransfers { get; set; } = new ToggleNode(false);

    /// <summary>
    /// How often, in milliseconds, Collect Bond transfers re-reads every counted monster's affixes while a Bond monster
    /// lives. A gain is credited to the nearest Bond death in the 1.5 seconds before the read that saw it, so when
    /// several Bond monsters die together a slower read leaves more deaths able to claim one gain; the candidates
    /// column of bond_gains.csv counts them. 60 is about two server ticks. See Spawns.BondTransfers.
    /// </summary>
    [Menu("Bond transfer read interval (ms)",
        "How often affixes are re-read while a Bond monster lives. Shorter tells\n" +
        "apart transfers from deaths close together in time, at the cost of more reads.")]
    public RangeNode<int> BondTransferReadMs { get; set; } = new RangeNode<int>(60, 16, 500);
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


        // **One button carrying its own state, and inert unless ctrl is held.**
        //
        // Two buttons meant the row changed shape depending on whether anything had been written, so what was
        // there to click moved. One button that reads ON or OFF says what the setting is and what pressing it
        // does, and greying it out without ctrl shows the guard rather than swallowing the click: every other
        // control here changes something the plugin owns and can undo, while this writes DOTNET_gcServer to
        // the Windows account, where it outlives ExileCore2 and applies to every .NET program started after.
        var ctrl = ImGui.GetIO().KeyCtrl;

        ImGui.BeginDisabled(!ctrl);

        if (ImGui.Button($"Enable GC Server: {(asked ? "ON" : "OFF")}###gcServer"))
        {
            if (asked)
                HostCollection.Restore();
            else
                HostCollection.UseServer();
        }

        ImGui.EndDisabled();

        // Hover has to be allowed while disabled, or the one tooltip that explains the guard is the one
        // tooltip that never shows.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                (ctrl ? "" : "HOLD CTRL to use this." + "\n\n") +
                (asked
                    ? "Set for your account now. Pressing this removes DOTNET_gcServer, so the" + "\n" +
                      "runtime chooses as it did before this was ever set." + "\n\n"
                    : "Pressing this writes DOTNET_gcServer=1 to your account's environment" + "\n" +
                      "variables." + "\n\n" +
                      "Highly recommended: it is the single largest thing outside this plugin" + "\n" +
                      "that decides what its allocation costs in frame time." + "\n\n") +
                "Takes effect next time ExileCore2 starts, and applies to every .NET" + "\n" +
                "program you start afterwards.");
        }

        // Beside the collector because it is the same kind of fact: how the host runs this plugin, read from the
        // running copy rather than from a file. See CompiledBuild.
        ImGui.TextUnformatted(CompiledBuild.Optimised
            ? "Compiled optimised: ON."
            : "Compiled optimised: OFF - the solver runs several times slower.");

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ExileCore2 compiles this plugin itself when it loads it. Unless the project\n" +
                "asks for optimisation, the JIT optimiser is left off, and the solver runs\n" +
                "several times slower - measured at about five times on one Grand site.\n\n" +
                "The project asks for it, so this should read ON. OFF means the plugin was\n" +
                "built some other way; reloading it from the source folder should fix it.");
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
    /// **Three buttons, each clearing less than the one above it.** "Delete all site data" is the
    /// whole of it, every site file for every area included; "Delete plan and ground" keeps the
    /// markers, the scouting layer and the saved ground, so a site can be re-solved from cold without
    /// walking it again; "Delete plan" drops only the plan and the chain on file. See Caches.
    /// </summary>
    [JsonIgnore]
    public CustomNode ForgetRoutingUi { get; set; } = new CustomNode(() =>
    {

        // **Three buttons, because "start again" means three different things and one of them was missing.**
        //
        // The narrow one exists for the ordinary case: solve that again without the last answer in hand. The
        // middle one is for a fair second measurement - everything the plugin worked out goes, so the next
        // solve begins with what this one began with, but the markers and the scouting layer stay because
        // those took a lap of the site to collect and nothing about the search is learnt from them. The wide
        // one is for arriving fresh.
        //
        // One per line, from the one that clears the most to the one that clears the least.
        if (ImGui.Button("Delete all site data (true cold start)###deleteCache"))
            Caches.Wanted = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("TRUE COLD START: everything the plugin worked out by standing here,\n" +
                             "including\n" +
                             "every site file on disk - the markers, the walkable ground,\n" +
                             "the filed chains and the scouting layer - for every area,\n" +
                             "not just this one.\n" +
                             "\n" +
                             "The reference table is NOT touched: your weights, your\n" +
                             "edited rows and every object the plugin has filed all stay.");
        }

        if (ImGui.Button("Delete plan and ground (cold start)###forgetWorkingOut"))
            Caches.WantedKeepingScan = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("COLD START: everything the plugin worked out standing here - the" + "\n" +
                             "plan, the chain on" + "\n" +
                             "file, the chain each worker carries to the next solve, the" + "\n" +
                             "remnant orders and the ground they were built on, the" + "\n" +
                             "routed ground held in memory, the snapped aims and every" + "\n" +
                             "reading taken." + "\n\n" +
                             "NOT the markers, the scouting layer, or the walkable ground saved to" + "\n" +
                             "disk - those took a lap of the site to collect, or minutes of flooding," + "\n" +
                             "and the terrain does not move, so the search learns" + "\n" +
                             "nothing from having them." + "\n\n" +
                             "So the next solve searches the same site as though it had never been" + "\n" +
                             "solved, without another lap. Use this between two solves you mean to" + "\n" +
                             "compare fairly." + "\n\n" +
                             "Nothing solves after it - no presolve, no continuous solving, no" + "\n" +
                             "re-solve for a roll - until the action key is pressed, so that press" + "\n" +
                             "is the first solve.");
        }

        if (ImGui.Button("Delete plan (warm start)###forgetPlanOnly"))
            Caches.WantedPlanOnly = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("WARM START: the plan in hand, the chain filed on disk, the" + "\n" +
                             "chain each worker carries to the next solve and the remnant" + "\n" +
                             "orders, and nothing else." + "\n\n" +
                             "Everything worked out about the ground stays - the routed answers," + "\n" +
                             "the snapped aims, the ground model, the readings taken here - so" + "\n" +
                             "the next solve starts knowing what this one knew." + "\n\n" +
                             "This is the one to use between two solves you mean to compare.");
        }

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
    /// The button that draws the edge points toward neighbours, and the edge point step beside it. Worked out on
    /// the tick after the press, from the markers, the reach and the ground, so it needs no plan. See
    /// Planner.ComputeEdgePointsForDrawing.
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

    /// <summary>
    /// Spots added to every worker's shortlist whatever they score, as "x,y; x,y" or pasted from a dump as
    /// "(x,y) (x,y)". For testing whether the search finds a known chain once its spots are on offer. A spot the
    /// ground refuses is left out and named in the dump's shortlist line. Empty for none. See Repair.Shortlist.
    /// </summary>
    [Menu("(DEBUG) Always shortlist these spots, as \"x,y; x,y\"")]
    public TextNode ForcedShortlistSpots { get; set; } = new TextNode("");

    [Menu("Flag unknown blast targets")]
    public ToggleNode ShowUnexpected { get; set; } = new ToggleNode(true);

    /// <summary>
    /// A red line from the player to each unknown blast target, as well as its name. Off by default: the name written
    /// on the object is enough to find it, and the lines cross the screen on a site with several. Needs Flag unknown
    /// blast targets on. See Overlay.Unknown.
    /// </summary>
    [Menu("Draw lines to unknown blast targets")]
    public ToggleNode ShowUnexpectedLines { get; set; } = new ToggleNode(false);

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
    /// Ticked, the prices stay exactly as the stored prices have them - the last prices read before it was ticked, kept
    /// in last_prices.tsv, which is not written while this is on. Everything downstream - the reward list, the
    /// must-take threshold, the reroll advice - goes on reading them and cannot tell the difference, which is the
    /// point: it freezes the question rather than the answer. See Valuation.StorePrices.
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
    /// Whether the atlas notable "Gaining Traction" is allocated: a remnant's waves carry more magic and rare packs for
    /// each remnant completed in the map before it. On, the scoring grows a remnant's magic and rare waves with its
    /// place in the chain, at the rates in the table's Gaining Traction row (fitted to recorded waves per rarity, where
    /// the node text says 50 for both); see Weighing.GainingTractionRates and Planner.TractionScales.
    ///
    /// **Asked rather than detected, though it can be read.** AtlasStats reads the node's stat from the atlas, and a
    /// read that silently stopped working would change every plan with nothing to say why. So the scoring follows this
    /// checkbox, and the dump sets what the atlas reads beside it and says when the two disagree.
    /// </summary>
    [Menu("Gaining Traction (Atlas node)",
        "Verisium Remnants have 50%% increased Monster Rarity per Remnant\n" +
        "Completed in Area\n" +
        "\n" +
        "On: a remnant's magic and rare waves are valued higher the more\n" +
        "remnants the chain has completed before it, at the rates in the\n" +
        "table's Gaining Traction row - what recorded waves fit best. The\n" +
        "dump says whether your atlas has the node, to check this against.")]
    public ToggleNode GainingTraction { get; set; } = new ToggleNode(true);

    /// <summary>
    /// Whether the map's own increased number of rare and magic monsters - its modifiers, tablets and atlas, as
    /// AtlasStats.MapMonsterIncreases reads them - is where a row's "increased number of rare monsters" starts. On, a
    /// relic's +50% adds to the map's total, so on a map at 186% it counts as 50 / 286 more rather than half as many
    /// again: the game adds increases to one stat together. Off, each row's increase counts alone.
    ///
    /// Rests on the map's increases reaching expedition monsters; over 13 recorded maps, rares per remnant wave rose in
    /// proportion (slope 1.07 +- 0.45 on a log scale). The remnant wave row is scaled by the map whatever this says,
    /// because its counts are written for a map with no modifiers. See NOTES.md, "The wave row and the map",
    /// PlanEnvironment.IncreaseBaseOfGroup and Weighing.RemnantWaveScales.
    /// </summary>
    [Menu("Map increases to rare and magic monsters",
        "On: a relic's or rune's increased number of rare or magic monsters\n" +
        "adds to the map's own (modifiers, tablets, atlas), so it is worth\n" +
        "less on a map that already has many. Off: each counts alone.")]
    public ToggleNode MapMonsterIncreases { get; set; } = new ToggleNode(true);

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
    /// Presses the CURRENT search over the same cold site several times, and reports the spread.
    ///
    /// **Repeated presses of the action key are not repeated samples.** A solve inherits the previous
    /// plan as a floor and the site's best chain off disk, so three presses measure one search and two
    /// continuations of it - measured on a Basin site as 9,506, then 9,506, then 9,506, every repeat
    /// adding nothing at all. This clears the plan and the best chain between presses and keeps the
    /// markers and the routed ground, which is the same fairness the bake-off uses between strategies.
    ///
    /// Where the comparison key above runs each ticked STRATEGY once, this runs one strategy several
    /// times. They answer different questions: which search is better, against how reliable a search
    /// is. The second is the one that says whether a change helped, because this search is randomised
    /// and a single press of it is a sample from a wide distribution - measured on one site as 7,875 to
    /// 10,701 with nothing changed but the seed.
    ///
    /// Read the answer in the dump under "every solve here, as a distribution". Unbound, like every
    /// other investigation key: a batch is several windows of standing still.
    /// </summary>
    /// <summary>
    /// Steps through the chains every worker of the last press produced, drawing each one as the plan.
    ///
    /// **Because "eight distinct chains" is not the same claim as "eight different ideas".** The distinct
    /// count compares chains for exact equality, so two routes differing in one link are two - and on this
    /// site it reads 7.8 of 8 while the scores cluster at about 7,400, which is what a set of variations on
    /// one route looks like. Whether they share a pattern is a question the eye answers in a second and a
    /// coordinate list never does.
    ///
    /// **Browsing adopts.** The chain shown becomes the plan, so it draws with its links, its blast
    /// circles and the markers it catches, exactly as the solver's own answer does - and a placement run
    /// started while browsing would place what is on screen. The next solve replaces it either way.
    ///
    /// Unbound by default, like the other investigation keys.
    /// </summary>
    [Menu("Worker chains window key")]
    public HotkeyNodeV2 BrowseHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// Whether the worker chains window is open. Toggled by the key above, and by its own cross.
    ///
    /// Kept in the settings like the reference table's switch, so the window survives a reload and the key
    /// and the cross are one statement rather than two. See ChainPanel.
    /// </summary>
    [Menu("Show the worker chains window")]
    public ToggleNode ShowChains { get; set; } = new ToggleNode(false);

    [Menu("Repeat the same solve key")]
    public HotkeyNodeV2 RepeatHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    /// <summary>
    /// How many presses the key above takes.
    ///
    /// Three is enough to see a spread and not enough to measure a median. Ten is the figure the solver
    /// plan asks for before a change may be called an improvement, and on a Grand site at eight seconds
    /// that is about a minute and a half of standing still.
    /// </summary>
    [Menu("Presses per batch")]
    public RangeNode<int> RepeatCount { get; set; } = new RangeNode<int>(10, 1, 20);

    /// <summary>
    /// Which draw of the random numbers a batch starts from. Nought walks 1 upwards.
    ///
    /// **Because tuning on ten draws and then judging on the same ten is how a search gets overfitted to
    /// them.** Every change measured on this site was accepted or rejected on draws 1 to 10, and the two
    /// that were kept were kept because they helped THOSE ten. A hold-out batch on draws nobody optimised
    /// against is the check on that, and it is the same check any fitted model needs.
    ///
    /// Set it to 10 for a batch of draws 11 to 20, compare the distribution with the tuned one, and a
    /// median that holds up says the gain is a property of the site rather than of the ten draws.
    /// </summary>
    [Menu("First draw of a batch")]
    public RangeNode<int> RepeatFrom { get; set; } = new RangeNode<int>(0, 0, 200);

    /// <summary>
    /// The plain score at which a press of the batch is stopped and the next one started, or nought to run every
    /// press its full window.
    ///
    /// For asking how often, and how fast, a search reaches a chain already known to exist - a batch against a
    /// target measures that in the time the hits take rather than ten full windows. The dump says how many
    /// presses reached it and after how long. Plain, as the score area shows it, without must-take insistence.
    /// </summary>
    [Menu("Stop a press at this score (0 for never)")]
    public RangeNode<int> RepeatStopAtScore { get; set; } = new RangeNode<int>(0, 0, 100000);

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

    /// <summary>
    /// How long the must take key is held on a marker to make the chain take it last, rather than cycle its mark. A tap
    /// cycles the mark when the key comes up, so the hold can be told apart from it; a bar under the cursor empties
    /// over the hold. See Insisted.ToggleTakenLast.
    ///
    /// Under Debug since 2026-10-06, as a timing few need to change; it was at the root of the settings, and
    /// migration 20 carries a saved value over.
    /// </summary>
    [Menu("Take last hold time (ms)")]
    public RangeNode<int> TakeLastHoldMs { get; set; } = new RangeNode<int>(750, 300, 3000);

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
            if (ImGui.Button("edges toward neighbours###drawTowardNeighbours"))
            {
                Planner.TowardNeighbours = true;
                Planner.EdgePointsPending = true;
            }

            // The Candidate spots setting itself, not a copy, so what is drawn is what the search is offered. Moving
            // it while the edge points are drawn draws them again at the new step.
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140f);

            var step = Root.Solver.Advanced.CandidateSpots.EdgePointStepGrid.Value;

            if (ImGui.SliderInt("edge point step (grid)###drawEdgePointStep", ref step, 0, 10))
            {
                Root.Solver.Advanced.CandidateSpots.EdgePointStepGrid.Value = step;

                if (Planner.TowardNeighbours)
                    Planner.EdgePointsPending = true;
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
