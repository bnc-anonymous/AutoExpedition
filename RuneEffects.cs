using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// What each rune does, in the wording the game shows on a remnant.
///
/// A table rather than a read, because the game's own stat descriptions cannot answer this. Several
/// runes are implemented as bare flags - Opulent is "HasOpulentRuneMod: 1", Power is
/// "DummyExpeditionRunePower: 1" - and Death, Oath and Time carry no stats at all, their behaviour
/// living in spawn logic the stat tables never see. Of twenty four mods translated from the files,
/// nine came back as raw stat names and the misses were concentrated in exactly the runes people
/// rank highest.
///
/// So these lines come from poe2db, which lists the remnant text for each rune. They are used only
/// where the game's own translation produced nothing readable - see RuneInfo - so a future patch
/// that starts describing a rune properly takes over from this automatically.
///
/// Every rune here except Bait, whose page lists no effect text at all.
/// </summary>
internal static class RuneEffects
{
    public static string For(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : Known.GetValueOrDefault(id);

    /// <summary>
    /// Power is the odd one out, and worth reading before the rest make sense.
    ///
    /// It does nothing to the monsters itself - it upgrades every other rune on the remnant to its
    /// stronger variant. Which is why every rune in the game data carries a second mod alongside
    /// its first: that second one is what this turns on.
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Power"] = "Upgrades all other Runic Modifiers on the Remnant to a more powerful and rewarding variant",

        ["Opulent"] = "Increased Monster Rarity",
        ["Death"] = "Slain Monsters may merge into stronger Monsters",
        ["Bond"] = "Rare Monsters may transfer a Mod on death",
        ["Oath"] = "A Monster summons Allies",
        ["Time"] = "Slain Monsters may respawn as a higher Rarity",
        ["Rebirth"] = "Chance to Rebirth on death",

        ["Wisdom"] = "Increased Experience",
        ["Soul"] = "Union of Souls",
        ["Life"] = "Shared Life",
        ["Ward"] = "Protected by Runic Ward",
        ["Rage"] = "Periodically Enrage",
        ["Adaptive"] = "Adaptation",

        ["Fire"] = "Extra Fire Damage",
        ["Cold"] = "Extra Cold Damage",
        ["Lightning"] = "Extra Lightning Damage",

        ["Tempest"] = "Cannot be Shocked or Chilled / All Damage contributes to chance to Shock and Chill Magnitude",
        ["Momentum"] = "Increased Movement Speed / Movement Speed cannot be Slowed below base",
        ["Bloodletting"] = "Life Leech / Cannot have Life Leeched from / Inflicts Corrupted Blood on Hit",
        ["Stone"] = "Armoured / Increased Stun Threshold / Earthly Prison",
        ["Arcane"] = "Extra Energy Shield / Triggers a Stunning nova when Energy Shield is depleted",
        ["Toxic"] = "Chance to Poison on Hit / Chance for Toxic Volatiles on death / All Damage from Hits contributes to Poison Magnitude",
        ["Electrocuting"] = "Extra Lightning Damage / Lightning Damage Electrocutes / Shocked Ground Trails",
        ["Protective"] = "Periodically gain Verisium Proximity Shields",
        ["Cyclonic"] = "Chance to inflict Exposure on Hit / Armour Break on Hit / Wither on Hit",
        ["Prismatic"] = "All Damage can Shock / All Damage can Chill / All Damage can Ignite / Increased Elemental Resistances",
        ["Gasp"] = "Extra Fire Damage / All Damage can Ignite / Ignited Ground Trails",
        ["Vision"] = "Reflect Curses / Chance to Reflect Shock / Chance to Reflect Chill",
        ["Celestial"] = "Chance for a Fire, Cold or Lightning Explosion on Death",

        ["Sky"] = "Conjures Elemental Tornados",
        ["Earth"] = "Conjures Earthly Spires",
        ["Moon"] = "Conjures Moon Beams",
        ["Tidal"] = "Conjures Tidal Waves",
    };
}
