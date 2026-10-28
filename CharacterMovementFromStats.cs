using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// The character's dodge roll and walking speed, worked out from its stats when asked, for instant loot's choice between
/// walking to an item and rolling toward it. See Autoloot.RollIsFaster.
///
/// **The roll, by the wiki's formula** (poe2wiki, Dodge roll, read 2026-10-25), with M the movement speed multiplier,
/// R one plus the roll speed bonus, S the action speed multiplier and A the added roll distance in metres:
/// distance = floor((4.6 + A) x sqrt(M) x 10) / 10 metres, duration = base / (sqrt(M x R) x S). It fits the player's
/// rolls at M 1.48: 5.5 metres measured 49.2 grid with a staff out, 8.0 metres measured 72.2 grid with a crossbow's +2
/// metres - 8.95 and 9.03 grid a metre (dumps 2026-10-25 01:48:20, 01:48:58).
///
/// The stats read: DodgeRollTravelDistance, A in tenths of a metre (20 for +2 metres, absent with the staff out);
/// MovementVelocityPermyriad, M - 1 in ten-thousandths (4800, unmoved on chilled ground); DodgeRollSpeedPct, R - 1 in
/// percent; VirtualActionSpeedPct, S - 1 in percent, which carries slows (-22 with IsChilled 1, dump 01:49:03).
/// </summary>
internal static class CharacterMovementFromStats
{
    /// <summary>The roll's base distance in metres, from the wiki's formula. See the class summary.</summary>
    private const double BaseRollMetres = 4.6;

    /// <summary>
    /// Grid per metre. Measured: 49.2 grid for 5.5 metres and 72.2 grid for 8.0 metres, 8.95 and 9.03 (dumps 2026-10-25
    /// 01:48:20, 01:48:58).
    /// </summary>
    private const double GridPerMetre = 9.0;

    /// <summary>
    /// The roll's base animation time, still tail included. Measured: 16 rolls at M 1.48 and no roll or action speed
    /// bonus took 769 to 827ms, about 790, which is 961 x 1/sqrt(1.48) (dumps 2026-10-25 01:48:20, 01:48:58).
    /// </summary>
    private const double BaseRollMs = 961;

    /// <summary>
    /// Walking speed with no bonus, in grid per second. Measured: one straight walk off chilled ground at +48% movement
    /// speed, 93.4 grid between two dumps begun 1.70s apart, 55.0 grid/s, so 37.2 at no bonus (dumps 2026-10-25 01:55:58
    /// and 01:56:00, their start times from each file's write time less its build time); a path not quite straight
    /// would make it faster. Seven earlier walks, some likely chilled or bent, had a median of 52 grid/s. The wiki has a
    /// roll cover over its whole time what walking does, which would put it at 62 grid/s - the 49.2-grid roll in 790ms;
    /// the walk measured is slower than that.
    /// </summary>
    private const double BaseWalkGridPerSecond = 37.2;

    /// <summary>The roll, grid and ms, from the player's stats now.</summary>
    public static (float Grid, double Ms) Roll(GameController gc)
    {
        var stats = StatsOf(gc);
        var rootOfMovement = Math.Sqrt(MovementOf(stats));
        var addedMetres = StatOf(stats, GameStat.DodgeRollTravelDistance) / 10d;
        var metres = Math.Floor((BaseRollMetres + addedMetres) * rootOfMovement * 10d) / 10d;
        var rollSpeed = 1d + StatOf(stats, GameStat.DodgeRollSpeedPct) / 100d;
        var ms = BaseRollMs / (Math.Sqrt(MovementOf(stats) * Math.Max(0.1d, rollSpeed)) * ActionSpeedOf(stats));

        return ((float)(metres * GridPerMetre), ms);
    }

    /// <summary>The walking speed in grid per second, from the player's stats now.</summary>
    public static double WalkGridPerSecond(GameController gc)
    {
        var stats = StatsOf(gc);

        return BaseWalkGridPerSecond * MovementOf(stats) * ActionSpeedOf(stats);
    }

    /// <summary>For the dump: the stats read and what they come to.</summary>
    public static string Said(GameController gc)
    {
        var stats = StatsOf(gc);
        var roll = Roll(gc);

        return $"roll {roll.Grid:0.0} grid in {roll.Ms:0}ms, walking {WalkGridPerSecond(gc):0.0} grid/s, from " +
               $"DodgeRollTravelDistance {StatOf(stats, GameStat.DodgeRollTravelDistance)}, " +
               $"MovementVelocityPermyriad {StatOf(stats, GameStat.MovementVelocityPermyriad)}, " +
               $"DodgeRollSpeedPct {StatOf(stats, GameStat.DodgeRollSpeedPct)}, " +
               $"VirtualActionSpeedPct {StatOf(stats, GameStat.VirtualActionSpeedPct)}";
    }

    /// <summary>M, the movement speed multiplier: 1.48 for MovementVelocityPermyriad 4800; never below a tenth.</summary>
    private static double MovementOf(IDictionary<GameStat, int> stats) =>
        Math.Max(0.1d, 1d + StatOf(stats, GameStat.MovementVelocityPermyriad) / 10000d);

    /// <summary>S, the action speed multiplier: 0.78 for VirtualActionSpeedPct -22; never below a tenth.</summary>
    private static double ActionSpeedOf(IDictionary<GameStat, int> stats) =>
        Math.Max(0.1d, 1d + StatOf(stats, GameStat.VirtualActionSpeedPct) / 100d);

    private static IDictionary<GameStat, int> StatsOf(GameController gc) =>
        Safe.Read(gc, static g => g.Player?.GetComponent<Stats>()?.StatDictionary, null);

    private static int StatOf(IDictionary<GameStat, int> stats, GameStat stat) =>
        stats == null ? 0 : Safe.Read(stats, s => s.TryGetValue(stat, out var v) ? v : 0, 0);
}
