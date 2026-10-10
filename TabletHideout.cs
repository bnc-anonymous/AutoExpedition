using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;

namespace AutoExpedition;

/// <summary>
/// What full-auto reforging will click in the hideout, read for the dump before any of it is automated: the hideout
/// objects and NPCs near the player with where their models land on screen, and the stash's elements under the
/// cursor, for the fragment tab's numbered tabs. See Describe.
/// </summary>
internal static partial class Tablets
{
    /// <summary>How far, in grid units, the hideout objects are listed from the player.</summary>
    private const float HideoutListRange = 250f;

    /// <summary>
    /// The objects and NPCs near the player: metadata, name, distance, whether targetable and targeted, and where the
    /// model's interaction point and its bounds' corners project on screen. A model is clicked at its interaction
    /// point (Render.InteractCenter) rather than at its name label.
    /// </summary>
    private static string HideoutObjectsSaid(GameController gc)
    {
        var b = new StringBuilder();
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);

        if (camera == null || entities == null)
            return "  hideout objects: unreadable\n";

        var near = entities
            .Where(e => Safe.Read(e, static x => x.DistancePlayer, float.MaxValue) < HideoutListRange)
            .Where(e => Safe.Read(e, static x => x.Metadata, "") is { } path &&
                        (path.Contains("/NPC/", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("MiscellaneousObjects", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Stash", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Reforg", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Bench", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Hideout", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => Safe.Read(e, static x => x.DistancePlayer, float.MaxValue))
            .Take(40)
            .ToList();

        b.AppendLine($"  hideout objects and NPCs within {HideoutListRange:0} of the player: {near.Count}");

        foreach (var entity in near)
        {
            var render = Safe.Read(entity, static e => e.GetComponent<Render>(), null);
            var interact = Safe.Read(render, static r => r.InteractCenter, Vector3.Zero);
            var bounds = Safe.Read(render, static r => r.Bounds, Vector3.Zero);
            var at = interact == Vector3.Zero ? Safe.Read(entity, static e => e.BoundsCenterPos, Vector3.Zero) : interact;
            var screen = Safe.Read(() => camera.WorldToScreen(at), Vector2.Zero);
            var corners = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0),
                    new Vector3(0, 0, 1), new Vector3(0, 0, -1) }
                .Select(c => Safe.Read(() => camera.WorldToScreen(at + c * bounds), Vector2.Zero))
                .Where(p => p != Vector2.Zero)
                .ToList();
            var box = corners.Count == 0
                ? "no box"
                : $"box ({corners.Min(p => p.X):0},{corners.Min(p => p.Y):0})-({corners.Max(p => p.X):0},{corners.Max(p => p.Y):0})";

            b.AppendLine($"    {Safe.Read(entity, static e => e.Metadata, "?")} \"{Safe.Read(entity, static e => e.RenderName, "")}\" " +
                         $"distance {Safe.Read(entity, static e => e.DistancePlayer, 0f):0}, " +
                         $"targetable {Safe.Read(entity, static e => e.IsTargetable, false)}, " +
                         $"targeted {Safe.Read(entity, static e => e.GetComponent<Targetable>()?.isTargeted.ToString(), "-")}, " +
                         $"interact point {(interact == Vector3.Zero ? "none (bounds centre used)" : "read")} on screen ({screen.X:0},{screen.Y:0}), " +
                         $"bounds {bounds.X:0}x{bounds.Y:0}x{bounds.Z:0}, {box}");
        }

        return b.ToString();
    }

    /// <summary>
    /// Every stash tab as ExileCore2 offers it, for telling the fragment and currency tabs apart: its index, name, the
    /// inventory type read for it (the fragment tab read BreachStash, 2026-10-09, so these names may not mean what they
    /// say in Path of Exile 2), and its tab-list button's rectangle with every texture below it - the tab list shows a
    /// small icon for the fragment and currency tabs.
    /// </summary>
    private static string StashTabsSaid(GameController gc)
    {
        var b = new StringBuilder();
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);

        if (stash == null || !Safe.Read(stash, static s => s.IsVisible, false))
            return "  stash tabs: the stash is closed\n";

        var names = Safe.Read(stash, static s => s.AllStashNames, null);
        var inventories = Safe.Read(stash, static s => s.AllInventories, null);
        var buttons = Safe.Read(stash, static s => s.TabListButtons, null);
        var count = Math.Max(names?.Count ?? 0, Math.Max(inventories?.Count ?? 0, buttons?.Count ?? 0));

        b.AppendLine($"  stash tabs: {Safe.Read(stash, static s => s.TotalStashes, 0L)} in all, visible {Safe.Read(stash, static s => s.IndexVisibleStash, -1)}; " +
                     $"{names?.Count ?? -1} names, {inventories?.Count ?? -1} inventories, {buttons?.Count ?? -1} tab-list buttons");

        for (var i = 0; i < count && i < 60; i++)
        {
            var name = names != null && i < names.Count ? names[i] : "?";
            var inventory = inventories != null && i < inventories.Count ? inventories[i] : null;
            var button = buttons != null && i < buttons.Count ? buttons[i] : null;
            var rect = Safe.Read(button, static x => x.GetClientRectCache, default);
            var textures = new List<string>();
            var stack = new Stack<(Element, int)>();

            if (button != null)
                stack.Push((button, 0));

            while (stack.Count > 0 && textures.Count < 8)
            {
                var (at, depth) = stack.Pop();

                if (Safe.Read(at, static e => e.TextureName, "") is { Length: > 0 } texture)
                    textures.Add($"{(Safe.Read(at, static e => e.IsVisible, false) ? "" : "hidden ")}{texture.Split('/').Last()}");

                if (depth < 4)
                    foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                        stack.Push((child, depth + 1));
            }

            b.AppendLine($"    [{i}] \"{name}\" type {Safe.Read(inventory, static x => x.InvType.ToString(), "unread")}, button " +
                         $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) visible {Safe.Read(button, static x => x.IsVisible, false)}, " +
                         $"textures [{string.Join(", ", textures)}]");
        }

        // **The tabs as the server keeps them, with the colour each was given.** For outlining the action zones in the
        // fragment tab's own colour: how Color and Color2 relate to the two shades the game draws the tab's outline in is
        // what this is for finding out.
        var tabs = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerStashTabs, null);

        b.AppendLine($"  stash tabs as the server keeps them: {tabs?.Count ?? -1}");

        for (var i = 0; tabs != null && i < tabs.Count && i < 60; i++)
        {
            var tab = tabs[i];
            var colour2 = Safe.Read(tab, static t => t.Color2, default);

            b.AppendLine($"    [{i}] \"{Safe.Read(tab, static t => t.Name, "?")}\" type {Safe.Read(tab, static t => t.TabType.ToString(), "?")}, " +
                         $"visible index {Safe.Read(tab, static t => (int)t.VisibleIndex, -1)}, hidden {Safe.Read(tab, static t => t.IsHidden, false)}, " +
                         $"Color 0x{Safe.Read(tab, static t => t.Color, 0u):X8}, " +
                         $"Color2 #{colour2.R:X2}{colour2.G:X2}{colour2.B:X2} alpha {colour2.A}, flags {Safe.Read(tab, static t => t.Flags.ToString(), "?")}");
        }

        return b.ToString();
    }

    /// <summary>
    /// The fragment tab's controls, by their place under the visible tab's element as read off dumps of 2026-10-09
    /// 16:22-16:24: child 1 the page buttons (Fragments, Tablets, Trials), child 2 the tablet type icons (Precursor
    /// first, Expedition third), child 0, 0, 0, 0 the numbered sub-tab bar. Each child with its rectangle and every text
    /// under it, so the sub-tabs can be told apart by their numbers.
    /// </summary>
    private static string FragmentTabControlsSaid(GameController gc)
    {
        var b = new StringBuilder();
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var panel = Safe.Read(stash, static s => s.StashInventoryPanel, null);
        var index = Safe.Read(stash, static s => s.IndexVisibleStash, -1);
        var tab = panel != null && index >= 0 ? Safe.Read(() => panel.Children[index], null) : null;
        var content = Safe.Read(tab, static t => t.Children[0], null);

        if (content == null || !Safe.Read(stash, static s => s.IsVisible, false))
            return "  fragment tab controls: the stash is closed or its visible tab unreadable\n";

        static string Texts(Element element)
        {
            var found = new List<string>();
            var stack = new Stack<(Element, int)>();

            stack.Push((element, 0));

            while (stack.Count > 0 && found.Count < 6)
            {
                var (at, depth) = stack.Pop();

                if (Safe.Read(at, static e => e.Text, "") is { Length: > 0 } text)
                    found.Add(text);

                if (depth < 5)
                    foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                        stack.Push((child, depth + 1));
            }

            return string.Join("|", found);
        }

        void Row(string name, Element row)
        {
            var children = Safe.Read(row, static r => r.Children, null);

            if (children == null)
            {
                b.AppendLine($"    {name}: unreadable");
                return;
            }

            b.AppendLine($"    {name}: {children.Count} children");

            for (var i = 0; i < children.Count; i++)
            {
                var rect = Safe.Read(children[i], static c => c.GetClientRectCache, default);

                b.AppendLine($"      [{i}] ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) visible " +
                             $"{Safe.Read(children[i], static c => c.IsVisible, false)} texts \"{Texts(children[i])}\"");
                b.Append(LitFlagsSaid(children[i], "        "));
            }
        }

        b.AppendLine($"  fragment tab controls (visible tab {index}):");
        Row("page buttons (child 1)", Safe.Read(content, static c => c.Children[1], null));
        Row("tablet type icons (child 2)", Safe.Read(content, static c => c.Children[2], null));
        var subTabs = NumberedSubTabs(content);

        b.AppendLine($"    numbered sub-tabs found by their numbers: {subTabs.Count}");

        foreach (var (number, element) in subTabs)
        {
            var rect = Safe.Read(element, static e => e.GetClientRectCache, default);

            var parent = Safe.Read(element, static e => e.Parent, null);

            b.AppendLine($"      {number} ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) child " +
                         $"{Safe.Read(element, static e => e.IndexInParent, -1)} of {Safe.Read(parent, static p => p.ChildCount, -1)}");
            b.Append(LitFlagsSaid(element, "        "));
        }

        return b.ToString();
    }

    /// <summary>
    /// An element's and its children's lit and selected flags, two levels down, for telling which page button, tablet
    /// type or sub-tab is the selected one: the full-auto run clicked the Expedition tablets with them already selected
    /// (2026-10-09), so what marks the selected one is looked for here.
    /// </summary>
    private static string LitFlagsSaid(Element element, string indent)
    {
        var b = new StringBuilder();
        var stack = new Stack<(Element, string, int)>();

        stack.Push((element, "", 0));

        while (stack.Count > 0)
        {
            var (at, path, depth) = stack.Pop();

            b.AppendLine($"{indent}{(path.Length == 0 ? "self" : path)}: visible {Safe.Read(at, static e => e.IsVisible, false)} " +
                         $"local {Safe.Read(at, static e => e.IsVisibleLocal, false)} highlighted {Safe.Read(at, static e => e.isHighlighted, false)} " +
                         $"shiny {Safe.Read(at, static e => e.HasShinyHighlight, false)} active {Safe.Read(at, static e => e.IsActive, false)} " +
                         $"saturated {Safe.Read(at, static e => e.IsSaturated, false)} border {Safe.Read(at, static e => e.BordColor.ToString(), "?")} " +
                         $"texture \"{Safe.Read(at, static e => e.TextureName, "")}\"");

            if (depth >= 2)
                continue;

            var children = Safe.Read(at, static e => e.Children, null) ?? [];

            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push((children[i], path.Length == 0 ? $"{i}" : $"{path}->{i}", depth + 1));
        }

        return b.ToString();
    }

    /// <summary>
    /// The fragment tab's numbered sub-tabs, by number: each element of a sub-tab's size (80x38 in the dumps of
    /// 2026-10-09, taken as 60-100 by 28-48) whose only text below it is a number from 1 to 9. Found by number rather
    /// than by place, because the selected sub-tab moved to the bar's last child in those dumps.
    /// </summary>
    internal static List<(int Number, Element Element)> NumberedSubTabs(Element content)
    {
        var found = new List<(int, Element)>();
        var stack = new Stack<(Element, int)>();
        var visited = 0;

        stack.Push((content, 0));

        while (stack.Count > 0 && visited++ < 3000)
        {
            var (element, depth) = stack.Pop();

            if (!Safe.Read(element, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(element, static e => e.GetClientRectCache, default);

            if (rect.Width is >= 60f and <= 100f && rect.Height is >= 28f and <= 48f &&
                TextsUnder(element) is [var only] && only.Length == 1 && char.IsDigit(only[0]) && only[0] != '0')
            {
                found.Add((only[0] - '0', element));
                continue;
            }

            if (depth < 12)
                foreach (var child in Safe.Read(element, static e => e.Children, null) ?? [])
                    stack.Push((child, depth + 1));
        }

        return found.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Every non-empty text an element and its descendants carry, a few levels down.</summary>
    private static List<string> TextsUnder(Element element)
    {
        var texts = new List<string>();
        var stack = new Stack<(Element, int)>();

        stack.Push((element, 0));

        while (stack.Count > 0 && texts.Count < 4)
        {
            var (at, depth) = stack.Pop();

            if (Safe.Read(at, static e => e.Text, "") is { Length: > 0 } text)
                texts.Add(text.Trim());

            if (depth < 4)
                foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                    stack.Push((child, depth + 1));
        }

        return texts;
    }

    /// <summary>
    /// Every visible element of the stash panel whose rectangle holds the cursor, deepest last, with its path, rectangle,
    /// children and text - the fragment tab's numbered tabs read no text and the game's hover names nothing over them
    /// (dumps of 2026-10-09 16:19:17 and 16:19:18), so they are found by position.
    /// </summary>
    private static string StashElementsUnderCursorSaid(GameController gc)
    {
        var b = new StringBuilder();
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var mouse = ImGuiNET.ImGui.GetMousePos();

        if (stash == null || !Safe.Read(stash, static s => s.IsVisible, false))
            return "  stash elements under the cursor: the stash is closed\n";

        b.AppendLine($"  stash elements under the cursor ({mouse.X:0},{mouse.Y:0}):");

        var stack = new Stack<(Element Element, string Path, int Depth)>();
        var visited = 0;
        var listed = 0;

        stack.Push((stash, "StashElement", 0));

        while (stack.Count > 0 && visited++ < 4000 && listed < 60)
        {
            var (element, path, depth) = stack.Pop();

            if (!Safe.Read(element, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(element, static e => e.GetClientRectCache, default);
            var children = Safe.Read(element, static e => e.Children, null) ?? [];

            if (rect.Width > 0f && mouse.X >= rect.Left && mouse.X <= rect.Right && mouse.Y >= rect.Top && mouse.Y <= rect.Bottom)
            {
                listed++;
                b.AppendLine($"    {path} ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) kids {children.Count} " +
                             $"text \"{Safe.Read(element, static e => e.Text, "")}\"" +
                             $"{(Safe.Read(element, static e => e.Entity, null) is { } entity ? $" entity {Safe.Read(entity, static x => x.Metadata, "?")}" : "")}");
            }

            if (depth >= 16)
                continue;

            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push((children[i], $"{path}->{i}", depth + 1));
        }

        return b.ToString();
    }
}
