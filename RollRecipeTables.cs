using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoExpedition;

/// <summary>
/// What a roll can produce at one area level: for every socket count, every rune the game lets a roll fix and in which
/// slot, and for each of those the recipes the remnant could then be turned into, with their prices.
///
/// **A snapshot of Valuation's answers, not a second copy of its rules.** FixedRunesPossibleAt and RecipesForShape read
/// the game's tables and apply the filters; this asks them once per socket count and keeps what they said in plain
/// types. The reroll advice builds its shapes from this in game, and the offline tool builds them from the same data
/// saved to a file, so a saved site can have its rolls scored without the game - by the same code.
///
/// Level-specific: a recipe has a level window and a pin has a level range, so a snapshot taken at area level 81
/// answers for 81. The level is kept with it so a reader can see what it answers for.
/// </summary>
internal sealed class RollRecipeTables
{
    /// <summary>A recipe as the roll calculation reads it: its id, how many runes it uses, and the rune in each slot.</summary>
    internal sealed record RollRecipe(string Id, int RuneCount, string[] Runes);

    /// <summary>One rune a roll can fix, in one slot, and what a remnant so fixed can become, each with its price in exalts.</summary>
    internal sealed record RollPin(string RuneId, int Slot, List<(RollRecipe Recipe, double Price)> Recipes);

    /// <summary>Every pin the game admits at each socket count, recipes or none - their count is part of ShareOfPin.</summary>
    public Dictionary<int, List<RollPin>> PinsBySockets { get; init; } = new();

    /// <summary>The area level the snapshot answers for.</summary>
    public int AreaLevel { get; init; }

    /// <summary>The league it was taken in, which decides recipes the game withholds. See Valuation.WithheldInLeague.</summary>
    public string League { get; init; } = "";

    /// <summary>When it was taken, local time.</summary>
    public DateTime TakenAt { get; init; }

    /// <summary>The socket counts a roll is asked about: every count either site kind's roll can come up with.</summary>
    private static IEnumerable<int> SocketCounts() =>
        Rolls.SocketsFor(true).Select(x => x.Sockets).Concat(Rolls.SocketsFor(false).Select(x => x.Sockets)).Distinct()
            .OrderBy(x => x);

    /// <summary>Asks Valuation about every socket count. On the frame, since Valuation reads the game.</summary>
    public static RollRecipeTables FromValuation(Valuation valuation, int areaLevel, string league)
    {
        var bySockets = new Dictionary<int, List<RollPin>>();

        if (valuation == null)
            return new RollRecipeTables { AreaLevel = areaLevel, League = league ?? "", TakenAt = DateTime.Now };

        foreach (var sockets in SocketCounts())
        {
            var pins = new List<RollPin>();

            foreach (var (rune, slot) in Safe.Read(() => valuation.FixedRunesPossibleAt(sockets), null) ?? [])
            {
                // A pin with no rune id is kept, with no recipes: the shape builder skips it, but it is counted among the
                // pins the game admits, which is the divisor in Rolls.ShareOfPin.
                var runeId = Safe.Read(() => rune.Id, null) ?? "";
                var recipes = new List<(RollRecipe, double)>();

                if (string.IsNullOrWhiteSpace(runeId))
                {
                    pins.Add(new RollPin("", slot, recipes));

                    continue;
                }

                foreach (var (recipe, price) in Safe.Read(() => valuation.RecipesForShape(sockets, rune, slot), null) ?? [])
                {
                    var count = Safe.Read(() => recipe.RuneCountRequired, 0);
                    var runes = new string[Math.Max(0, count)];

                    for (var at = 0; at < runes.Length; at++)
                        runes[at] = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                    recipes.Add((new RollRecipe(Safe.Read(() => recipe.Id, null) ?? "?", count, runes), price));
                }

                pins.Add(new RollPin(runeId, slot, recipes));
            }

            bySockets[sockets] = pins;
        }

        return new RollRecipeTables
        {
            PinsBySockets = bySockets, AreaLevel = areaLevel, League = league ?? "", TakenAt = DateTime.Now,
        };
    }

    /// <summary>How many pins and recipes it holds, for a log line.</summary>
    public string Said() =>
        $"area level {AreaLevel}, league \"{League}\", {PinsBySockets.Sum(x => x.Value.Count)} pins over " +
        $"{PinsBySockets.Count} socket counts, {PinsBySockets.Sum(x => x.Value.Sum(p => p.Recipes.Count))} pin recipes";

    // ------------------------------------------------------------------ file

    /// <summary>
    /// The file name for one area level, in the plugin's config folder. One file per level, because the recipes and pins
    /// a roll can produce depend on it, and a dump taken in another area must not replace a map level's tables.
    /// </summary>
    public static string FileNameAt(int areaLevel) => $"roll_recipe_tables_{areaLevel}.json";

    /// <summary>The lowest area level saved: maps start at 65, and a town's or the campaign's tables answer for no map.</summary>
    public const int LowestSavedLevel = 65;

    private sealed record Saved(int AreaLevel, string League, DateTime TakenAt, List<SavedPin> Pins);

    private sealed record SavedPin(int Sockets, string Rune, int Slot, List<SavedRecipe> Recipes);

    private sealed record SavedRecipe(string Id, int RuneCount, string[] Runes, double Price);

    public void Save(string path)
    {
        var pins = PinsBySockets
            .SelectMany(bySockets => bySockets.Value.Select(pin => new SavedPin(bySockets.Key, pin.RuneId, pin.Slot,
                pin.Recipes.Select(x => new SavedRecipe(x.Recipe.Id, x.Recipe.RuneCount, x.Recipe.Runes, x.Price))
                    .ToList())))
            .ToList();

        var writing = path + ".tmp";

        File.WriteAllText(writing, Newtonsoft.Json.JsonConvert.SerializeObject(
            new Saved(AreaLevel, League, TakenAt, pins), Newtonsoft.Json.Formatting.Indented));
        File.Move(writing, path, true);
    }

    public static RollRecipeTables Load(string path)
    {
        var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<Saved>(File.ReadAllText(path));
        var bySockets = new Dictionary<int, List<RollPin>>();

        foreach (var pin in saved?.Pins ?? [])
        {
            if (!bySockets.TryGetValue(pin.Sockets, out var pins))
                bySockets[pin.Sockets] = pins = new List<RollPin>();

            pins.Add(new RollPin(pin.Rune, pin.Slot,
                (pin.Recipes ?? []).Select(x => (new RollRecipe(x.Id, x.RuneCount, x.Runes ?? []), x.Price)).ToList()));
        }

        return new RollRecipeTables
        {
            PinsBySockets = bySockets, AreaLevel = saved?.AreaLevel ?? 0, League = saved?.League ?? "",
            TakenAt = saved?.TakenAt ?? DateTime.MinValue,
        };
    }
}
