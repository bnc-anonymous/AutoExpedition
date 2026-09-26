using ExileCore2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace AutoExpedition;

/// <summary>
/// Relic modifiers the character cannot fight, and the markers that carry them.
///
/// **A weight cannot say this, because the objection is not to the value.** A relic granting forty
/// per cent rarity is worth taking by every number the plugin has, and "monsters are immune to fire
/// damage" makes the whole expedition unkillable for an ignite build - so the correct weight is not
/// a smaller number, it is "not that one, ever". Pricing it low enough to lose would also price it
/// low enough to take whenever the alternative was thin, which is exactly the run where being
/// unable to kill anything hurts most.
///
/// So it reuses what the player already has for that statement rather than inventing a second one:
/// a marker carrying a banned mod is an Insisted.Said.Avoid, with the same large negative weight,
/// the same red rings, and the same wording. The one difference is that it applies itself - the
/// point is never having to notice the relic in the first place.
///
/// **Still avoid rather than refuse**, for the reason written on Insisted.Said.Avoid: a chain whose
/// only route runs past the relic should be the best chain that does, not no chain at all. A ban
/// makes it cost more than the site is worth, which settles every case except the one where there
/// was no alternative - and there, being told is more use than being given nothing.
///
/// The list fills itself from what the game shows, like the unknown weights do. Nobody should have
/// to type a mod id, and the ones worth banning are build-specific - there is no sensible default
/// beyond "none of them".
/// </summary>
internal static class MustAvoidMods
{
    /// <summary>
    /// Which spelling of this file's contents is on disk.
    ///
    /// Same reasoning as Unknowns.Keyed: the entries are mod ids, and if what counts as an entry
    /// ever changes, rows written under the old rule survive beside the new ones and read as bans
    /// nobody set. Bump it when Notice changes what it files.
    /// </summary>
    private const int Keyed = 1;

    /// <summary>
    /// Every relic mod seen, and whether the chain refuses it.
    ///
    /// **Concurrent because the search reads it.** Planning asks Bans for every marker on every
    /// candidate chain, off the frame, while the sweep is still filing newly seen mods on it - which
    /// is the plain dictionary crash already paid for twice, on Reached and Refused.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> Seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Every drawback the game rolls, so the list is complete before you meet one.
    ///
    /// **A list that fills itself is empty exactly when it is needed.** Discovery works - walk past a
    /// relic and its modifiers appear - but the moment you want a modifier banned is the moment you
    /// are standing in front of one, which is one relic too late. Seeding means the decision is made
    /// once, in the menu, before it costs anything.
    ///
    /// Everything here arrives unticked. What is worth refusing depends entirely on the character, and
    /// a shipped opinion about somebody else's build is worse than no opinion - the seeding makes the
    /// choice available, not the choice.
    ///
    /// **Read off the game rather than ported.** This was forty-one ids taken from the set another
    /// plugin warns on, and nine of them do not exist in Path of Exile 2 - they are Path of Exile 1
    /// names, near-misses of the real ones. We seeded ElitesRegenerateLifeEveryFourSeconds against a
    /// game that says RegenerateLifeEveryFourSeconds, and ElitesPetrifyOnHit against
    /// ElitesRandomCurseOnHit. Ticking one of those refused nothing, for ever, and said nothing about
    /// it - which is the same silent failure as a matcher that never fires.
    ///
    /// These are the thirty-two the expedition_relic domain actually lists, less the dummy. Every
    /// downside seen in a dump here is among them, and none of the nine has ever been seen.
    ///
    /// Discovery still runs, and now says so - see Unheard. This is a head start, not a replacement.
    /// </summary>
    private static readonly string[] Known =
    {
        "ExpeditionRelicDownsideAllDamagePoisonsPoisonDuration",
        "ExpeditionRelicDownsideAlwaysCrit",
        "ExpeditionRelicDownsideArmourBreak",
        "ExpeditionRelicDownsideAvoidDamage",
        "ExpeditionRelicDownsideBleedOnHitBleedDuration",
        "ExpeditionRelicDownsideCannotBeCrit",
        "ExpeditionRelicDownsideCannotBeLeechedFrom",
        "ExpeditionRelicDownsideChaosPenetration",
        "ExpeditionRelicDownsideColdPenetration",
        "ExpeditionRelicDownsideCriticalAgainstFullLife",
        "ExpeditionRelicDownsideDamageAsChaos",
        "ExpeditionRelicDownsideDamageAsCold",
        "ExpeditionRelicDownsideDamageAsFire",
        "ExpeditionRelicDownsideDamageAsLightning",
        "ExpeditionRelicDownsideDamageAttackCastMovementSpeedLowLife",
        "ExpeditionRelicDownsideElementalAilmentChance",
        "ExpeditionRelicDownsideElitesRandomCurseOnHit",
        "ExpeditionRelicDownsideFirePenetration",
        "ExpeditionRelicDownsideGrantNoFlaskCharges",
        "ExpeditionRelicDownsideHitsCannotBeEvaded",
        "ExpeditionRelicDownsideImmuneChaosDamage",
        "ExpeditionRelicDownsideImmuneColdDamage",
        "ExpeditionRelicDownsideImmuneFireDamage",
        "ExpeditionRelicDownsideImmuneLightningDamage",
        "ExpeditionRelicDownsideImmunePhysicalDamage",
        "ExpeditionRelicDownsideImmuneToCurses",
        "ExpeditionRelicDownsideIncreasedDamage",
        "ExpeditionRelicDownsideIncreasedLife",
        "ExpeditionRelicDownsideIncreasedSpeed",
        "ExpeditionRelicDownsideLightningPenetration",
        "ExpeditionRelicDownsideRegenerateLifeEveryFourSeconds",
        "ExpeditionRelicDownsideResistancesAndMaxResistances",
    };

    /// <summary>Puts the known drawbacks on the list, without disturbing anything already decided.</summary>
    private static void Preseed()
    {
        foreach (var mod in Known)
            Seen.TryAdd(mod, false);
    }

    /// <summary>
    /// Says so when the game shows a drawback the seed list has never heard of.
    ///
    /// **The seed was wrong for a year and nothing said so.** Nine of its ids were Path of Exile 1
    /// names that this game never rolls, and the only way that could have been noticed was somebody
    /// ticking one and wondering why nothing was ever refused. The reverse - a real drawback missing
    /// from the seed - is just as quiet: it turns up in the list only after you have already walked
    /// past the relic carrying it, which is the case the seed exists to prevent.
    ///
    /// So a modifier arriving from the game that the list did not predict is worth one line. Once per
    /// id per session, because this runs off the scan.
    /// </summary>
    private static void Unheard(string mod)
    {
        if (string.IsNullOrEmpty(mod) || !Told.Add(mod))
            return;

        DebugWindow.LogMsg(
            $"[AutoExpedition] {mod} is a relic drawback the seed list does not know about. It is on " +
            "the list now, but it was not there before you met it - which is what the seed is for. " +
            "Worth adding to MustAvoidMods.Known.", 10f);
    }

    /// <summary>Which unheard-of drawbacks have already been reported. See Unheard.</summary>
    private static readonly HashSet<string> Told = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a relic refused for its modifiers is ringed in the world, and on the map.
    ///
    /// **Off by default, and separate from the marks made by hand.** A mark placed with the key is
    /// worth drawing because somebody made a decision and wants to see it honoured. A ban is a
    /// standing rule that may apply to half the relics on a site, and ringing all of them in red
    /// turns a useful signal into wallpaper - the chain already plans around them, which is the
    /// whole point of setting the rule once instead of pointing at each relic.
    ///
    /// Two switches rather than one, because the two surfaces answer different questions: the world
    /// says what is in front of you, the map says what the chain is shaped around.
    /// </summary>
    public static bool DrawInWorld;

    /// <inheritdoc cref="DrawInWorld"/>
    public static bool DrawOnMap;

    /// <summary>Where the bans live. Empty turns the remembering off.</summary>
    public static string Home { get; set; } = "";

    public static int Count => Seen.Count;

    /// <summary>How many are actually banned, which is the number worth showing.</summary>
    public static int Banned
    {
        get
        {
            var count = 0;

            foreach (var pair in Seen)
            {
                if (pair.Value)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Every drawback seen, worst first: the immunities, then the rest, alphabetical within each.
    ///
    /// **Immunities lead because they are the ones that get ticked.** A penetration or a life
    /// modifier makes a fight harder; an immunity makes it impossible for the build it names, and a
    /// character has usually met every one of them before it has met much else. Sorting the list
    /// strictly by family buried the three rows anybody actually comes here for.
    ///
    /// </summary>
    public static List<(string Mod, bool Refused, bool Downside)> All
    {
        get
        {
            var all = new List<(string Mod, bool Refused, bool Downside)>();

            foreach (var pair in Seen)
            {
                // A file written while the gifts were still listed still holds them. Dropped on
                // the way out rather than migrated, since an unticked row is worth nothing.
                if (!Downside(pair.Key))
                    continue;

                all.Add((pair.Key, pair.Value, true));
            }

            all.Sort((a, b) =>
            {
                int Band((string Mod, bool Refused, bool Downside) x) =>
                    Immune(x.Mod) ? 0 : x.Downside ? 1 : 2;

                var bands = Band(a).CompareTo(Band(b));

                return bands != 0
                    ? bands
                    : string.Compare(Unknowns.Effect(a.Mod), Unknowns.Effect(b.Mod),
                        StringComparison.OrdinalIgnoreCase);
            });

            return all;
        }
    }

    /// <summary>
    /// Whether the mod makes monsters immune to something.
    ///
    /// Matched on the word rather than on a list of ids, so a league that adds a fourth immunity
    /// sorts into the right band without anybody editing this. See All.
    /// </summary>
    public static bool Immune(string mod) =>
        mod != null && mod.Contains("Immune", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a mod id is one of the drawbacks rather than one of the gifts.</summary>
    public static bool Downside(string mod) =>
        mod != null && mod.Contains("Downside", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files whatever this marker carries, so the player has something to tick.
    ///
    /// Only the relic modifiers. Every other mod in the game would arrive here too - monster
    /// affixes by the hundred - and a list nobody can find anything in is a list nobody uses.
    /// </summary>
    public static void Notice(Target target)
    {
        var mods = target?.Mods;

        if (string.IsNullOrEmpty(mods))
            return;

        foreach (var piece in mods.Split(',', ';', ' '))
        {
            var mod = piece.Trim();

            // **Drawbacks only.** The gifts were listed too, on the reasoning that a build might
            // not be able to handle one - but nobody refuses a relic for what it gives, and a list
            // twice as long as it needs to be is a list the three rows that matter hide inside.
            if (!Downside(mod) || mod.EndsWith("DummyStat", StringComparison.Ordinal))
                continue;

            // Before the add, because the add is what makes it known. See Unheard.
            if (!Seen.ContainsKey(mod))
                Unheard(mod);

            Seen.TryAdd(mod, false);
        }
    }

    /// <summary>Whether the chain refuses this marker outright. See the class summary.</summary>
    public static bool Bans(Target target)
    {
        var mods = target?.Mods;

        if (string.IsNullOrEmpty(mods) || Seen.IsEmpty)
            return false;

        foreach (var piece in mods.Split(',', ';', ' '))
        {
            if (Seen.TryGetValue(piece.Trim(), out var refused) && refused)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Which banned mod this marker carries, as something a person can read.
    ///
    /// Named rather than merely flagged: "must avoid" on a relic the player never marked is a
    /// mystery, and the whole value of an automatic rule is lost if you cannot see what it fired on.
    /// </summary>
    public static string Why(Target target)
    {
        var mods = target?.Mods;

        if (string.IsNullOrEmpty(mods) || Seen.IsEmpty)
            return "";

        var found = new List<string>();

        foreach (var piece in mods.Split(',', ';', ' '))
        {
            var mod = piece.Trim();

            if (Seen.TryGetValue(mod, out var refused) && refused && !found.Contains(mod))
                found.Add(mod);
        }

        for (var i = 0; i < found.Count; i++)
            found[i] = Unknowns.Effect(found[i]);

        return string.Join(", ", found);
    }

    /// <summary>Bans a mod, or lifts the ban, and writes it down.</summary>
    public static void Decide(string mod, bool refused)
    {
        if (string.IsNullOrEmpty(mod))
            return;

        Seen[mod] = refused;

        Keep();
    }

    /// <summary>Drops every ban and everything seen. The list fills itself again from the ground.</summary>
    public static void ForgetAll()
    {
        Seen.Clear();

        Keep();
    }

    /// <summary>
    /// Puts the whole of this back the way it ships, for the reset button.
    ///
    /// **Not the same as ForgetAll**, which empties the list and lets the ground refill it. What
    /// ships is the seeded drawbacks with nothing banned, so this clears and re-seeds - otherwise
    /// a reset would leave somebody with a shorter list than a fresh install has, and the
    /// difference would only show up the next time they walked past a relic.
    ///
    /// The two drawing switches go back as well. They are plain statics rather than settings nodes,
    /// so nothing else reaches them. See ResetAll.
    /// </summary>
    public static void Reset()
    {
        Seen.Clear();
        Preseed();

        DrawInWorld = false;
        DrawOnMap = false;

        Keep();
    }

    public static void Keep()
    {
        if (string.IsNullOrEmpty(Home))
            return;

        try
        {
            var text = new StringBuilder();

            text.AppendLine("{");
            text.AppendLine($"  \"keyed\": {Keyed},");
            text.AppendLine($"  \"drawInWorld\": {(DrawInWorld ? "true" : "false")},");
            text.AppendLine($"  \"drawOnMap\": {(DrawOnMap ? "true" : "false")},");
            text.AppendLine("  \"mods\": [");

            var first = true;

            foreach (var (mod, refused, _) in All)
            {
                if (!first)
                    text.AppendLine(",");

                first = false;

                text.Append($"    {{ \"mod\": \"{mod}\", \"refused\": {(refused ? "true" : "false")} }}");
            }

            if (!first)
                text.AppendLine();

            text.AppendLine("  ]");
            text.AppendLine("}");

            File.WriteAllText(Path.Combine(Home, "mustavoidmods.json"), text.ToString());
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not remember the must-avoid mods: {ex.Message}", 5f);
        }
    }

    public static void Load()
    {
        // First, and outside every early return below: a missing file, an unreadable one or a
        // version bump must all still leave the known drawbacks on the list. See Known.
        Preseed();

        if (string.IsNullOrEmpty(Home))
            return;

        try
        {
            var path = Path.Combine(Home, "mustavoidmods.json");

            if (!File.Exists(path))
                return;

            var lines = File.ReadAllLines(path);
            var keyed = 0;

            foreach (var line in lines)
            {
                var said = Between(line, "\"keyed\": ", ",");

                if (said.Trim().Length > 0 && int.TryParse(said.Trim(), out var wrote))
                {
                    keyed = wrote;

                    break;
                }
            }

            // Written under a rule that no longer means anything. See Keyed.
            if (keyed != Keyed)
                return;

            foreach (var line in lines)
            {
                if (line.Contains("\"drawInWorld\":", StringComparison.Ordinal))
                    DrawInWorld = line.Contains("true", StringComparison.Ordinal);

                if (line.Contains("\"drawOnMap\":", StringComparison.Ordinal))
                    DrawOnMap = line.Contains("true", StringComparison.Ordinal);

                var mod = Between(line, "\"mod\": \"", "\"");

                if (mod.Length > 0)
                    Seen[mod] = line.Contains("\"refused\": true", StringComparison.Ordinal);
            }

            Stale();
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the must-avoid mods: {ex.Message}", 5f);
        }
    }

    /// <summary>
    /// Drops saved entries the game has no such modifier for.
    ///
    /// **Trimming the seed was not enough, because the saved file keeps what the seed once put in
    /// it.** Nine Path of Exile 1 names had been written out and would have stayed for ever - a list
    /// of forty-one against a game with thirty-two, eight of which could be ticked to no effect at
    /// all.
    ///
    /// Safe to drop where nobody has decided anything: a modifier this removes is one no relic
    /// carries, and if that turns out to be wrong, the first relic carrying it puts it straight back
    /// and Unheard says so.
    ///
    /// **A ticked one goes too, and is named when it does.** A tick against a modifier the game
    /// never rolls refuses nothing - it is a decision that has never once been acted on, and keeping
    /// it only preserves the belief that something is being avoided. The nine Path of Exile 1 names
    /// were ticked exactly like real ones and looked exactly like real ones, which is the whole
    /// problem. The removal is announced by id so a modifier that really does exist can be ticked
    /// again, and discovery puts it back on the list the first time a relic carries it.
    ///
    /// Written back, or this is a cleanup that never finishes. The file is otherwise rewritten only
    /// when something else asks for it - a box ticked, or the scan meeting a modifier it has not
    /// saved - and neither need happen, so the stale entries were read again on the next load,
    /// dropped again, and announced again: forty-one saved against a game with thirty-two, reported
    /// every session and never once repaired.
    /// </summary>
    private static void Stale()
    {
        var known = new HashSet<string>(Known, StringComparer.Ordinal);
        var loose = Seen.Keys.Where(x => !known.Contains(x)).OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (loose.Count == 0)
            return;

        var ticked = loose.Where(x => Seen[x]).ToList();

        foreach (var mod in loose)
            Seen.TryRemove(mod, out _);

        DebugWindow.LogMsg(
            $"[AutoExpedition] dropped {loose.Count} relic drawback(s) the game has no modifier for: " +
            string.Join(", ", loose.Select(x => x.Replace("ExpeditionRelicDownside", ""))) +
            (ticked.Count > 0
                ? $". {ticked.Count} of those had been set to must-avoid, refusing nothing: " +
                  string.Join(", ", ticked.Select(x => x.Replace("ExpeditionRelicDownside", "")))
                : "") +
            " Any that turns out to be real comes back the first time a relic carries it.", 15f);

        Keep();
    }

    private static string Between(string line, string open, string close)
    {
        var start = line.IndexOf(open, StringComparison.Ordinal);

        if (start < 0)
            return "";

        start += open.Length;

        var end = line.IndexOf(close, start, StringComparison.Ordinal);

        return end < 0 ? "" : line[start..end];
    }
}
