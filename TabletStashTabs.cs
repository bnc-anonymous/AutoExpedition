using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory;

namespace AutoExpedition;

/// <summary>
/// Which stash tab is which, and going to one. Each tab's button in the tab list carries a small icon for its kind -
/// FragmentTabIcon.dds on the fragment tab, CurrencyTabIcon.dds on the currency tab - and the texture is there to read
/// with the list shown or hidden (dumps of 2026-10-09 17:00:15 to 17:00:32). The inventory type ExileCore2 reads for a
/// tab is not used: most tabs read none until visited, and the fragment tab read BreachStash. A tab is clicked in the
/// list, so the list is shown first when it is hidden.
/// </summary>
internal static partial class Tablets
{
    /// <summary>The fragment tab's icon in the tab list.</summary>
    internal const string FragmentTabIcon = "FragmentTabIcon.dds";

    /// <summary>The currency tab's icon in the tab list.</summary>
    internal const string CurrencyTabIcon = "CurrencyTabIcon.dds";

    /// <summary>Whether a texture under an element ends with this file name, a few levels down.</summary>
    private static bool CarriesTexture(Element element, string file)
    {
        var stack = new Stack<(Element, int)>();

        stack.Push((element, 0));

        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();

            if ((Safe.Read(at, static e => e.TextureName, "") ?? "").EndsWith(file, StringComparison.OrdinalIgnoreCase))
                return true;

            if (depth < 4)
                foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                    stack.Push((child, depth + 1));
        }

        return false;
    }

    /// <summary>
    /// The index of the first usable stash tab whose tab-list button carries this icon, or -1. Not one named
    /// "(Remove-only)" or "(Unavailable)": the game keeps those buttons hidden even with the list shown.
    /// </summary>
    private static int StashTabWithIcon(GameController gc, string icon)
    {
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var names = Safe.Read(stash, static s => s.AllStashNames, null);
        var buttons = Safe.Read(stash, static s => s.TabListButtons, null);

        for (var i = 0; buttons != null && i < buttons.Count; i++)
        {
            var name = names != null && i < names.Count ? names[i] ?? "" : "";

            if (name.EndsWith("(Remove-only)", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("(Unavailable)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (CarriesTexture(buttons[i], icon))
                return i;
        }

        return -1;
    }

    /// <summary>Whether the stash's tab list is showing: any of its buttons visible.</summary>
    private static bool TabListShown(GameController gc) =>
        (Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.TabListButtons, null) ?? [])
        .Any(b => Safe.Read(b, static x => x.IsVisible, false));

    /// <summary>
    /// The button that shows the tab list, "Click to show the tab list": StashElement child 2, 0, 0, 0, 1, 2, at
    /// (840,119 39x47) in a dump of 2026-10-09 17:00:32, read by its place.
    /// </summary>
    private static Element TabListToggle(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.Children[2].Children[0].Children[0].Children[0].Children[1].Children[2],
            null);

    /// <summary>
    /// Brings the stash to the tab carrying this icon. True when it is showing; otherwise a step goes - the tab list
    /// shown, or the tab's button clicked, confirmed by the stash's visible tab - and false. Stops the run when no tab
    /// carries the icon.
    /// </summary>
    private static bool OnStashTab(GameController gc, string icon, string doing)
    {
        var target = StashTabWithIcon(gc, icon);

        if (target < 0)
        {
            StopReforging($"no {doing} in the stash");
            return false;
        }

        if (Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IndexVisibleStash, -1) == target)
            return true;

        if (!TabListShown(gc))
        {
            var toggle = Safe.Read(TabListToggle(gc), static t => t.GetClientRectCache, default);

            Send(toggle, false, "the tab list", () => TabListShown(gc), null);
            return false;
        }

        var button = Safe.Read(gc, g => g.IngameState.IngameUi.StashElement.TabListButtons[target], null);
        var rect = Safe.Read(button, static b => b.GetClientRectCache, default);

        Send(rect, false, doing,
            () => Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IndexVisibleStash, -1) == target &&
                  DateTime.UtcNow - _clickedAt > TabSettle,
            null);
        return false;
    }
}
