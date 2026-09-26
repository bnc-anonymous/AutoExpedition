using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AutoExpedition;

/// <summary>
/// Putting every setting back the way it shipped.
///
/// By reflection rather than by a written list of assignments. A hand-written reset has to name
/// every setting, and the one it forgets is silent - it looks like a reset that worked and leaves
/// one value behind. Walking the properties cannot forget one, and a setting added later is covered
/// without anybody remembering to come back here.
///
/// It works by building a fresh settings object and copying values across, so "default" always
/// means whatever the property initialisers currently say. There is no second copy of the numbers
/// to drift out of step.
/// </summary>
internal static class ResetAll
{
    /// <summary>
    /// The properties on the root that a reset must not touch.
    ///
    /// Enable would switch the plugin off, which reads as a crash rather than as a reset.
    /// ConfigVersion is not a setting at all - it records which migrations a saved file has had,
    /// and putting it back to nought would run every one of them again on the next load.
    /// </summary>
    private static readonly string[] Untouched = [nameof(AutoExpeditionSettings.Enable), "ConfigVersion"];

    public static void Apply(AutoExpeditionSettings settings)
    {
        if (settings == null)
            return;

        var shipped = new AutoExpeditionSettings();

        // **Walked rather than listed, for the same reason the copy below is.** This named five
        // sections and a hotkey by hand, and had fallen four behind: Bugs, and the insist, toggle
        // and highlight keys, were all added afterwards and none of them was reset. A list that has
        // to be kept up to date silently stops being a reset, which is the exact failure this class
        // was written to avoid and then reproduced one level up.
        //
        // Seeded with the real settings object, so the Root back-reference every section carries
        // stops here instead of walking back up into the whole tree. See Copy.
        Copy(shipped, settings, new HashSet<object>(ReferenceEqualityComparer.Instance));

        // **The must-avoid list is a store and is reset anyway, because it is a setting in every
        // sense that matters.** It is a flat list of rules somebody turned on, it lives outside the
        // settings file only because it discovers its own rows, and leaving it standing meant
        // "reset everything" quietly left the solver refusing relics.
        MustAvoidMods.Reset();

        // **The weight reference table is NOT reset, and that is a different case.** A weight is a
        // row with an edit history, and putting one back is Wrt.Forget on that row - which the
        // table's own reset does, per row, where the row being reset is in front of you. Doing it
        // from here would be a second way to undo an edit, reaching a store this cannot show.
    }

    /// <summary>
    /// Copies the Value of every settings node from one object to another.
    ///
    /// Value rather than the node itself, because the menu and the plugin both hold references to
    /// the nodes already there - swapping a node out leaves the UI editing an object nothing reads.
    ///
    /// **Recursive, and the cycle is handled by identity rather than by refusing to recurse.** An
    /// earlier version followed "anything in our namespace whose name ends in Settings", and
    /// DebugSettings holds a Root back-reference to AutoExpeditionSettings, so the walk went root
    /// -> Debug -> root -> Debug until the stack ran out. A StackOverflowException cannot be caught
    /// and is not logged: the HUD simply vanished, which is about the worst way for a settings
    /// button to fail.
    ///
    /// The fix then was to stop recursing, which was right when a section held nodes and nothing
    /// else and wrong the moment one held a subsection. Display alone now has six, and under them
    /// four more; nothing in any of them was being reset, because a subsection has no Value
    /// property and was skipped exactly as an int would be. Twenty one sections in total.
    ///
    /// So it recurses, and remembers every object it has already written to. Root resolves to an
    /// object already in that set and stops on the spot, which also keeps Enable and ConfigVersion
    /// out of reach of the back door.
    /// </summary>
    private static void Copy(object from, object to, HashSet<object> seen,
        string path = "", List<string> missed = null)
    {
        if (from == null || to == null || !seen.Add(to))
            return;

        var root = to is AutoExpeditionSettings;

        foreach (var property in to.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var where = path + "." + property.Name;

            if (root && Array.IndexOf(Untouched, property.Name) >= 0)
                continue;

            var source = Safe.Read(() => property.GetValue(from), null);
            var target = Safe.Read(() => property.GetValue(to), null);

            if (source == null || target == null)
                continue;

            var value = target.GetType().GetProperty("Value");

            if (value is { CanWrite: true, CanRead: true })
            {
                if (missed == null)
                    Safe.Try(() => value.SetValue(target, value.GetValue(source)));

                continue;
            }

            // A subsection. Anything outside this plugin's own namespace is left alone - an int, a
            // string, a list, or one of ExileCore2's own types that carries no Value.
            if (source.GetType() == target.GetType() &&
                target.GetType().Namespace == typeof(ResetAll).Namespace)
            {
                Copy(source, target, seen, where, missed);

                continue;
            }

            // Reached, not a node, and not something to walk into. Only interesting when auditing:
            // a settings type that ends up here is one the reset cannot see.
            if (missed != null && value != null)
                missed.Add(where + " [" + target.GetType().Name + "]");
        }
    }

    /// <summary>
    /// What the reset would leave behind, for the dump to report.
    ///
    /// **The reset's failure is silent by nature**, which is why this exists. It named five sections
    /// and a hotkey by hand and did not recurse, and the settings grew twenty one subsections
    /// underneath it: the button went on looking exactly as it always had while reaching 61 of 180
    /// nodes. Nothing about pressing it says which it reached.
    ///
    /// Runs the same walk with the writing turned off, so there is no second description of what
    /// the reset covers - the thing being audited is the thing doing the auditing.
    /// </summary>
    public static string Audit(AutoExpeditionSettings settings)
    {
        if (settings == null)
            return "no settings to check";

        var missed = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Copy(settings, settings, seen, "", missed);

        // Everything the walk stepped into, minus the root itself.
        var reached = seen.Count - 1;

        return missed.Count == 0
            ? $"reaches every setting under {reached} section(s); " +
              $"{string.Join(" and ", Untouched)} are held back on purpose"
            : $"**{missed.Count} SETTING(S) THE RESET CANNOT SEE** - {string.Join(", ", missed)}";
    }
}
