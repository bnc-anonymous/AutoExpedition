using System;
using System.Collections.Generic;
using Color = System.Drawing.Color;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;

namespace AutoExpedition;

/// <summary>
/// Draws the range around each rare monster carrying the Bond rune within which it can pass a modifier on when it
/// dies. See PropagationDisplaySettings.BondRuneRadius.
///
/// Bond's monster modifier holds one stat, read from the game's files (2026-09-30):
/// ChancePctToTransferRareModAndHealRareMonsterWithin80UnitsOnDeath, 25 and 50 when empowered. So a rare dying
/// can hand a modifier to, and heal, another rare within 80 units of it - and only then, which is worth seeing
/// while choosing which rare to kill first. The 80 comes from the stat's own name and is grid cells. Recorded on one
/// Grand site (2026-09-30), deaths of empowered Bond rares passed modifiers to rares 24.8 to 40.8 grid away, which
/// rules out world units (80 of those is about 7.4 grid); nothing was seen between 45 and 95 grid, so 80 grid was
/// taken as settled rather than measured to its edge. Two apparent transfers at 97 and 114 grid each had a closer
/// Bond death within the same three seconds.
///
/// Purple when another living rare is inside the ring, so a death there can pass a modifier on; the warning
/// colour when none is, so killing it now passes nothing.
/// </summary>
internal static class BondTransfer
{
    /// <summary>The transfer range in world units: 80 grid cells. See the class summary.</summary>
    private const float TransferRangeWorld = 80f * Detonator.GridToWorld;

    /// <summary>The purple the plugin draws exploits and remnant rewards in, for a ring with another rare inside.</summary>
    private static readonly Color InRange = Color.FromArgb(255, 190, 120, 255);

    /// <summary>The modifier id prefix, which covers the empowered variant as well.</summary>
    private const string BondModifier = "ExpeditionMonsterModRuneBond";

    /// <summary>
    /// Whether each rare seen carries Bond, by entity id, so its modifiers are read once. A rare whose modifiers
    /// could not be read yet is not cached, and is asked again next frame. Cleared on an area change.
    /// </summary>
    private static readonly Dictionary<uint, bool> Bonded = new();

    private static uint _area;

    public static void Draw(Graphics graphics, GameController gc, Color alone)
    {
        var area = Safe.Read(gc, static g => g.Area.CurrentArea.Hash, 0u);

        if (area != _area)
        {
            Bonded.Clear();
            _area = area;
        }

        var monsters = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var of) ? of : null, null);

        if (monsters == null)
            return;

        var rares = new List<(Vector3 At, bool Bonded)>();

        foreach (var monster in monsters)
        {
            if (!Safe.Read(monster, static e => e.IsAlive, false))
                continue;

            var magic = Safe.Read(monster, static e => e.GetComponent<ObjectMagicProperties>(), null);

            if (magic == null || Safe.Read(magic, static m => m.Rarity, MonsterRarity.White) != MonsterRarity.Rare)
                continue;

            var id = Safe.Read(monster, static e => e.Id, 0u);

            if (!Bonded.TryGetValue(id, out var bonded))
            {
                var mods = Safe.Read(magic, static m => m.Mods, null);

                // Unread modifiers are not a verdict: asked again next frame, and meanwhile the rare still counts
                // as one another ring may hold.
                if (mods is not { Count: > 0 })
                {
                    var unread = Safe.Read(monster, static e => e.Pos, Vector3.Zero);

                    if (unread != Vector3.Zero)
                        rares.Add((unread, false));

                    continue;
                }

                bonded = false;

                foreach (var mod in mods)
                {
                    if (mod != null && mod.StartsWith(BondModifier, StringComparison.Ordinal))
                    {
                        bonded = true;

                        break;
                    }
                }

                Bonded[id] = bonded;
            }

            var at = Safe.Read(monster, static e => e.Pos, Vector3.Zero);

            if (at != Vector3.Zero)
                rares.Add((at, bonded));
        }

        for (var i = 0; i < rares.Count; i++)
        {
            if (!rares[i].Bonded)
                continue;

            var shared = false;

            for (var j = 0; j < rares.Count && !shared; j++)
            {
                shared = j != i && Vector2.Distance(new Vector2(rares[i].At.X, rares[i].At.Y),
                    new Vector2(rares[j].At.X, rares[j].At.Y)) <= TransferRangeWorld;
            }

            graphics.DrawCircleInWorld(rares[i].At, TransferRangeWorld, shared ? InRange : alone, 2f, 32, true);
        }
    }
}
