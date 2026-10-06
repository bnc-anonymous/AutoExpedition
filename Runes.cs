using ExileCore2;
using ExileCore2.PoEMemory.FilesInMemory;
using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// How good a rune is to take, independent of what this particular remnant could become.
///
/// It matters because a rune is not consumed the way a reward is - a good one taken early carries
/// forward, so a remnant offering it is worth more than its recipe price says. Which is the one
/// thing pure pricing cannot see.
/// </summary>
internal enum RuneTier
{
    /// <summary>A rune this plugin has never heard of. Scored as C rather than assumed good.</summary>
    Unknown,

    C,
    B,
    A,
    S,
    SS,
}

/// <summary>
/// The rune tiers.
///
/// Player knowledge rather than anything readable from the game, so it is a table and it is going
/// to need editing when the meta moves. It is here rather than in settings because a list of
/// twenty rune names in a menu is not a setting anybody wants to maintain - the weights per tier
/// are the part worth adjusting, and those are.
///
/// The plugin this replaces has its own version of this table and disagrees: it puts "oath" in its
/// bottom tier where this has it in S, and has no tier above S at all. Worth knowing if its route
/// suggestions have ever looked wrong around oath remnants.
///
/// B and C are "the rest of the purple runes" and "the rest of the blue runes" - a distinction the
/// game draws and this cannot yet, since nothing readable off a rune says which colour it is. Until
/// that is found, anything unlisted is C: the safe direction, since over-valuing a rune sends the
/// chain somewhere it should not go, and B-tier is explicitly the tier that does not matter much.
/// </summary>
internal static class Runes
{
    public static RuneTier Tier(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return RuneTier.Unknown;

        return id.ToLowerInvariant() switch
        {
            "opulent" => RuneTier.SS,

            // Power is S in the published list and weighted above it here - see the shipped rune rows,
            // and weight_reference_table.md for why.
            "power" or "death" or "bond" or "oath" => RuneTier.S,

            "time" or "rebirth" => RuneTier.A,

            _ => RuneTier.Unknown,
        };
    }

    /// <summary>
    /// What this rune reaches, in the shape the propagation reader expects, or empty for monsters.
    ///
    /// **Read off the row's effect, which is now the only place a rune says anything.** It used to
    /// come from a Scope cell, with the magnitude in a Weight cell beside it and a fallback into a
    /// settings list behind both - three stores for one rune, and the two cells were in different
    /// units. See Wrt.Runed.
    ///
    /// Empty where the target is monsters, because that is what an empty scope has always meant and
    /// what every caller still tests for. See Tags.Monsterly, where minus one is that case.
    ///
    /// **Every chain-wide effect, joined by commas**, as Tags.Scope reads them. This returned the first and
    /// stopped, so a row reaching rare and magic monsters - Oath's "rare_monster.count *= +100%,
    /// magic_monster.count *= +100%" - propagated its rare half and silently dropped the magic one.
    /// </summary>
    public static string Scope(string id)
    {
        var parts = new List<string>(2);

        foreach (var effect in TableGrammar.EffectsOfRow(Wrt.Id.Rune(id), out _))
        {
            // An 'own' effect reaches the holding remnant's waves only, per combination. See Weighing.OwnEffectsOfRunes.
            if (effect.Own)
                continue;

            // An effect on other runes' magnitude is a lift, not a carry reaching things of its target tag, whichever
            // tag names the runes it lifts. See Weighing.Lift and EmpowerableRune.
            if (string.Equals(effect.Attribute, TableGrammar.Magnitude, StringComparison.Ordinal) ||
                string.Equals(effect.Target, Tags.Known[Tags.Runes],
                    StringComparison.OrdinalIgnoreCase))
                continue;

            // One on every monster makes the rune unscoped, which is what an empty scope means.
            if (string.Equals(effect.Target, Tags.Known[Tags.Monsters], StringComparison.OrdinalIgnoreCase))
                return "";

            parts.Add(effect.Target + "=" + TableGrammar.Precise(effect.Share * 100f));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// What this rune is worth as a share of every monster it reaches - its Weight - or nought where it is scoped, since
    /// a scoped rune is paid through its scope alone. What the propagated-rune lookups read, so the planner's unscoped
    /// booking cannot pay a scoped rune again on every monster. Bond, Oath and Time were paid both ways once their rows
    /// were scoped to rare monsters (2026-10-01). See Propagation.Carried, which already skipped them for the total.
    /// </summary>
    public static float UnscopedWeight(string id) =>
        Scoped(id) ? 0f : Weight(id);

    /// <summary>Whether this rune reaches something other than monsters. See Scope.</summary>
    public static bool Scoped(string id) =>
        Scope(id).Trim().Length > 0;

    /// <summary>
    /// What a rune is worth, as a PERCENTAGE uplift to the things it reaches.
    ///
    /// **One store now, and it is the table.** This read a Weight cell, then a settings list, then a
    /// ranking compiled into this file - three answers to one question, with the first two in a column
    /// whose meaning depended on the row's id prefix. The ranking's numbers are shipped rows now and
    /// its reasoning is in weight_reference_table.md, so the argument survives without being a second
    /// source.
    ///
    /// Not a weight on the same scale as a chest or a remnant. The game says what propagation does:
    /// "the Runic Modifier in this slot will be added to all Monsters unearthed after this Remnant". It
    /// is a modifier ON monsters, so what it is worth depends on how many there are - which is why it
    /// is a percentage and why TableGrammar.Content refuses to count it as content.
    /// </summary>
    public static float Weight(string id)
    {
        var effects = TableGrammar.EffectsOfRow(Wrt.Id.Rune(id), out _);

        // **Its share before its lift**, so a rune that adds and lifts - Rebirth - is weighed by what it adds and lifts
        // through its effect number. A rune that only lifts - Power - answers with its lift, which is what booking it has
        // always carried into the amplifiers' group. See Weighing.LiftsOfRune.
        foreach (var effect in effects)
        {
            // An 'own' effect reaches the holding remnant's waves only, per combination. See Weighing.OwnEffectsOfRunes.
            if (!effect.Own && !Weighing.IsLiftEffect(effect))
                return effect.Share * 100f;
        }

        foreach (var effect in effects)
        {
            if (!effect.Own)
                return effect.Share * 100f;
        }

        return 0f;
    }



    /// <summary>
    /// What a rune nobody has an opinion about is worth: the value most of the tail carries.
    ///
    /// Two rather than zero, and that is the whole lesson of the first version of this table. A
    /// rune weighted zero is not "unimportant", it is ABSENT - the objective cannot tell it apart
    /// from an empty socket, so twenty seven of the thirty four runes were invisible to the route
    /// and a remnant full of them scored as a remnant full of nothing. It is also what a rune added
    /// by a patch gets before anybody has looked at it, and being slightly wrong about a new rune
    /// beats not seeing it.
    /// </summary>
    private const float Ordinary = 2f;

    /// <summary>Every rune the game knows about, for seeding the per-rune list.</summary>
    public static List<string> All(GameController gc)
    {
        var runes = Safe.Read(() => gc.Files.Expedition2Runes.EntriesList, null);
        var found = new List<string>();

        foreach (var rune in runes ?? new List<Expedition2Rune>())
        {
            var id = Safe.Read(() => rune.Id, null);

            if (!string.IsNullOrWhiteSpace(id))
                found.Add(id);
        }

        return found;
    }

    public static string Name(RuneTier tier) => tier switch
    {
        RuneTier.SS => "SS",
        RuneTier.S => "S",
        RuneTier.A => "A",
        RuneTier.B => "B",
        RuneTier.C => "C",
        _ => "?",
    };
}
