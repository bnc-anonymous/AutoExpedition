using ExileCore2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Tags somebody has written down for a kind of thing, overriding what the plugin derives.
///
/// **Derivation is a reading of the game, and the game changes.** Tags.Of works out what a thing is
/// from its kind, its tier and the art the scan read - which is right until a patch moves something,
/// adds a rarity, or introduces a container that is a chest in every way except the one the
/// classifier looks at. When that happens the plugin is wrong about a thing in front of you and
/// there is nothing you can do about it, because the answer is compiled in.
///
/// So every row can be told. Empty means derive, which is what almost every row will always say.
///
/// **Keyed by kind and tier rather than by object**, because that is the grain the derivation works
/// at: saying a reward chest is a chest is a statement about reward chests, not about the one in
/// front of you, and having to say it once per site would be worse than not being able to say it.
/// An object the plugin could not classify is the exception and does not come here - it has a row of
/// its own on the discovered list, and its tags live on it. See Unknowns.Priced.Marks.
///
/// Its own file for the reason the discovered list has one: it is a short list that grows as the
/// game does, it is legible, and it can be handed to somebody else without handing over a settings
/// file full of things that are nobody else's business.
/// </summary>
internal static class Marks
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal);
    private static bool _dirty;

    /// <summary>Where the file lives. Set once by the plugin; empty turns remembering off.</summary>
    public static string Home { get; set; } = "";

    /// <summary>What identifies the kind of thing a row is about. See the class summary.</summary>
    public static string Key(TargetKind kind, ChestTier tier) =>
        tier == ChestTier.Unknown ? kind.ToString() : $"{kind}/{tier}";

    /// <summary>The same, for a target.</summary>
    public static string Key(Target target) =>
        target == null ? "" : Key(target.Kind, target.Tier);

    /// <summary>What somebody has said this kind is, or empty to derive it.</summary>
    public static string Of(string key) =>
        key != null && Table.TryGetValue(key, out var said) ? said : "";

    /// <summary>Writes one down, or clears it when the text is empty.</summary>
    public static void Set(string key, string said)
    {
        if (string.IsNullOrEmpty(key))
            return;

        said = (said ?? "").Trim();

        if (said.Length == 0)
        {
            if (Table.Remove(key))
                _dirty = true;

            return;
        }

        if (Table.TryGetValue(key, out var had) && string.Equals(had, said, StringComparison.Ordinal))
            return;

        Table[key] = said;
        _dirty = true;
    }

    /// <summary>Everything written down, for the dump.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All => Table;

    public static int Count => Table.Count;

    /// <summary>Forgets the lot. They are overrides, so this puts every row back to derived.</summary>
    public static void ForgetAll()
    {
        if (Table.Count == 0)
            return;

        Table.Clear();
        _dirty = true;
    }

    /// <summary>Writes the file, when there is anything to write.</summary>
    public static void Keep()
    {
        if (!_dirty || Home.Length == 0)
            return;

        _dirty = false;

        try
        {
            var text = new StringBuilder();

            text.AppendLine("{");
            text.AppendLine("  \"marks\": [");

            var first = true;

            foreach (var (key, said) in Table)
            {
                if (!first)
                    text.AppendLine(",");

                first = false;

                text.Append("    { \"kind\": \"").Append(Safely(key))
                    .Append("\", \"tags\": \"").Append(Safely(said)).Append("\" }");
            }

            if (!first)
                text.AppendLine();

            text.AppendLine("  ]");
            text.AppendLine("}");

            File.WriteAllText(Path.Combine(Folder(), "marks.json"), text.ToString());
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the tag overrides: {ex.Message}", 5f);
        }
    }

    /// <summary>Reads it back. Parsed by hand for the reason Unknowns.Load is.</summary>
    public static void Load()
    {
        if (Home.Length == 0)
            return;

        var path = Path.Combine(Folder(), "marks.json");

        if (!File.Exists(path))
            return;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var key = Between(line, "\"kind\": \"", "\"");
                var said = Between(line, "\"tags\": \"", "\"");

                if (key.Length > 0 && said.Length > 0)
                    Table[key] = said;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the tag overrides: {ex.Message}", 5f);
        }
    }

    private static string Between(string line, string after, string before)
    {
        var from = line.IndexOf(after, StringComparison.Ordinal);

        if (from < 0)
            return "";

        from += after.Length;

        var to = line.IndexOf(before, from, StringComparison.Ordinal);

        return to < 0 ? line[from..] : line[from..to];
    }

    private static string Safely(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "").Replace("\"", "");

    private static string Folder()
    {
        Directory.CreateDirectory(Home);

        return Home;
    }
}
