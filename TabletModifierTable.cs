using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace AutoExpedition;

/// <summary>
/// The Expedition Tablet Rerolling section's modifier table: one row per modifier a tablet can carry, with what each is
/// called, whether it is a prefix or a suffix, and the choices a row holds. Its short name, which heads its column in
/// the hover table of listings, is set by the plugin and not shown here. See TabletModifierRow.
///
/// Laid out as ExileMaps' Tuning > Atlas modifiers list: a search box over the table, a checkbox column whose header
/// selects every visible row, and, while rows are selected, a bar above the table whose edits go to every selected
/// row that is visible - a row filtered out of sight never changes. Columns sort on a click, and a third click restores
/// the table's own order. Selection holds rows rather than positions, so sorting or a row being added keeps it.
/// </summary>
internal static class TabletModifierTable
{
    private static readonly Vector4 Dim = new(0.62f, 0.62f, 0.62f, 1f);

    private static string _search = "";

    private static readonly HashSet<TabletModifierRow> Selected = [];

    /// <summary>The bar's last-typed values, seeded from the first selected row when a selection begins.</summary>
    private static (string ValueSteps, int IgnoreBelow, int Weight) _bulk;

    private static TabletModifierRow _seededFrom;

    public static void Draw(TabletRerollingSettings settings)
    {
        ImGui.SeparatorText("Tablet modifiers");
        ImGui.TextWrapped("A full magic tablet is worth a Regal when its two modifiers' weights add up to the " +
                          "Regal threshold. A finished tablet is priced by its modifiers ticked Search that " +
                          "have a Trade stat, each at its Value step (blank: from its roll range); with a modifier under its Ignore " +
                          "other mod ranges below value, the others are searched for without values. Rows are added as tablets are read; a " +
                          "row not yet seen on an Expedition Tablet is shown dimmed.");

        var rows = settings.Modifiers;

        Selected.RemoveWhere(r => !rows.Contains(r));

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###aeTabletModifierSearch", "Search...", ref _search, 128);

        var visible = rows.Where(r => Matches(r, _search)).ToList();
        var picked = visible.Where(Selected.Contains).ToList();

        if (picked.Count > 0)
            BulkBar(picked);
        else
            _seededFrom = null;

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable("###aeTabletModifiers", 9, flags, new Vector2(0f, 420f)))
            return;

        ImGui.TableSetupColumn("###all", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 24f);
        ImGui.TableSetupColumn("Modifier", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Affix");
        ImGui.TableSetupColumn("Weight");
        ImGui.TableSetupColumn("Search");
        ImGui.TableSetupColumn("Value steps");
        ImGui.TableSetupColumn("Ignore other mod ranges below");
        ImGui.TableSetupColumn("Trade stat");
        ImGui.TableSetupColumn("###remove", ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupScrollFreeze(0, 1);

        // Drawn by hand: TableHeadersRow cannot hold the select-all checkbox.
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);

        var all = visible.Count > 0 && visible.All(Selected.Contains);

        if (ImGui.Checkbox("###aeTabletAll", ref all))
        {
            foreach (var row in visible)
            {
                if (all)
                    Selected.Add(row);
                else
                    Selected.Remove(row);
            }
        }

        string[] headers = ["Modifier", "Affix", "Weight", "Search", "Value steps", "Ignore other mod ranges below", "Trade stat", ""];

        for (var c = 0; c < headers.Length; c++)
        {
            ImGui.TableSetColumnIndex(c + 1);
            ImGui.TableHeader(headers[c]);
        }

        Sort(visible);

        TabletModifierRow removing = null;

        foreach (var row in visible)
        {
            ImGui.PushID(rows.IndexOf(row));
            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            var ticked = Selected.Contains(row);

            if (ImGui.Checkbox("###pick", ref ticked))
            {
                if (ticked)
                    Selected.Add(row);
                else
                    Selected.Remove(row);
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();

            var name = row.Text.Length > 0 ? row.Text : row.Id;

            if (row.Seen)
                ImGui.TextUnformatted(name);
            else
                ImGui.TextColored(Dim, name + " (not yet seen)");

            if (ImGui.IsItemHovered())
            {
                // The full text, which the column may cut short, and the mod's own name, which is what the game files
                // and the dump call it.
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
                ImGui.TextUnformatted(row.Text.Length > 0 ? row.Text : "(no text read yet)");
                ImGui.Separator();
                ImGui.TextColored(Dim, row.Id + (row.Affix.Length > 0 ? $" ({row.Affix.ToLowerInvariant()})" : ""));
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Affix.Length > 0 ? row.Affix : "-");

            ImGui.TableNextColumn();

            ImGui.SetNextItemWidth(60f);

            var weight = row.Weight;

            if (ImGui.InputInt("###regal", ref weight, 0))
                row.Weight = Math.Max(0, weight);

            ImGui.TableNextColumn();

            var search = row.Search;

            if (ImGui.Checkbox("###search", ref search))
                row.Search = search;

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(60f);

            var steps = row.ValueSteps;

            if (ImGui.InputTextWithHint("###steps", "auto", ref steps, 40))
                row.ValueSteps = steps.Trim();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Blank: steps from the roll range. \"any\": no value. \"exact\": its own roll.\n" +
                                 "A list such as 25,30,34: the highest at or below its roll.");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(50f);

            var below = row.IgnoreOtherModRangesBelow;

            if (ImGui.InputInt("###below", ref below, 0))
                row.IgnoreOtherModRangesBelow = Math.Max(0, below);

            // The trade site's id for it; filled in from fetched listings by affix name, or typed. See TabletModifierRow.
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(170f);

            var stat = row.TradeStat;

            if (ImGui.InputText("###stat", ref stat, 64))
                row.TradeStat = stat.Trim();

            ImGui.TableNextColumn();

            if (!row.Seen && ImGui.SmallButton("Remove"))
                removing = row;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (removing != null)
        {
            rows.Remove(removing);
            Selected.Remove(removing);
        }

        // Overwrites every row's choices, so it asks first.
        if (ImGui.Button("Restore default rows###aeTabletDefaults"))
            ImGui.OpenPopup("###aeTabletRestore");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every row's weight, Search, Value steps and ignore value back to the defaults.\n" +
                             "Trade stats are kept.");

        var open = true;

        if (ImGui.BeginPopupModal("Restore default rows?###aeTabletRestore", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Every row's choices go back to the defaults. Trade stats are kept.");

            if (ImGui.Button("Restore"))
            {
                settings.RestoreDefaultChoices();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The edits for every selected visible row at once: weight, Search, Value steps and Ignore other mod ranges below. The numbers and
    /// the pair start from the first selected row; each edit is written to every selected visible row.
    /// </summary>
    private static void BulkBar(List<TabletModifierRow> picked)
    {
        if (!ReferenceEquals(_seededFrom, picked[0]))
        {
            _seededFrom = picked[0];
            _bulk = (picked[0].ValueSteps, picked[0].IgnoreOtherModRangesBelow, picked[0].Weight);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{picked.Count} selected");

        Field("Weight");
        ImGui.SetNextItemWidth(60f);

        if (ImGui.InputInt("###aeBulkRegal", ref _bulk.Weight, 0))
        {
            _bulk.Weight = Math.Max(0, _bulk.Weight);
            picked.ForEach(r => r.Weight = _bulk.Weight);
        }

        Field("Search");

        var search = picked.All(r => r.Search);

        if (ImGui.Checkbox("###aeBulkSearch", ref search))
            picked.ForEach(r => r.Search = search);

        Field("Value steps");
        ImGui.SetNextItemWidth(60f);

        if (ImGui.InputTextWithHint("###aeBulkSteps", "auto", ref _bulk.ValueSteps, 40))
            picked.ForEach(r => r.ValueSteps = _bulk.ValueSteps.Trim());

        Field("Ignore other mod ranges below");
        ImGui.SetNextItemWidth(50f);

        if (ImGui.InputInt("###aeBulkBelow", ref _bulk.IgnoreBelow, 0))
        {
            _bulk.IgnoreBelow = Math.Max(0, _bulk.IgnoreBelow);
            picked.ForEach(r => r.IgnoreOtherModRangesBelow = _bulk.IgnoreBelow);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        if (ImGui.SmallButton("Clear selection###aeBulkClear"))
            Selected.Clear();
    }

    /// <summary>
    /// A separator, then a bulk-bar control's label, on the line: each control reads as "| Label [control]", so where
    /// one ends and the next begins is plain.
    /// </summary>
    private static void Field(string label)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
    }

    /// <summary>Whether a row matches the search: every word of it in the text, short name, id, affix, pair or trade stat.</summary>
    private static bool Matches(TabletModifierRow row, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        var haystack = $"{row.Text} {row.ShortName} {row.Id} {row.Affix} {row.TradeStat}";

        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Orders the visible rows by the clicked column, or leaves the table's own order when none is.</summary>
    private static void Sort(List<TabletModifierRow> visible)
    {
        var specs = ImGui.TableGetSortSpecs();

        if (specs.SpecsCount == 0)
            return;

        var spec = specs.Specs;
        Func<TabletModifierRow, IComparable> key = spec.ColumnIndex switch
        {
            1 => r => r.Text.Length > 0 ? r.Text : r.Id,
            2 => r => r.Affix,
            3 => r => r.Weight,
            4 => r => r.Search,
            5 => r => r.ValueSteps,
            6 => r => r.IgnoreOtherModRangesBelow,
            7 => r => r.TradeStat,
            _ => null,
        };

        if (key == null)
            return;

        var sorted = spec.SortDirection == ImGuiSortDirection.Descending
            ? visible.OrderByDescending(key).ToList()
            : visible.OrderBy(key).ToList();

        visible.Clear();
        visible.AddRange(sorted);
    }
}
