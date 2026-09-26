using ExileCore2;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Whether this expedition is probably over, worked out from the site rather than from the banner.
///
/// **The banner is the only thing that STATES it and it cannot be relied on.** "Expedition Complete"
/// fires on the last monster's death, which is the right moment - and it also fires when it should
/// not: seen twice at one dig site, once on an ice-encased unique, and the map's other site was
/// unable to accept an explosive afterwards. Something that is both the authority and a known fault
/// is a poor thing to gate a drawing on. See Finished.
///
/// So this asks the site two questions instead, neither of which is certain on its own:
///
/// **Has every remnant the chain was built around been blown?** A remnant is hidden while its waves
/// are alive and shows again once they are dead, so a chain whose remnants are all offering their
/// shatter buttons is a chain whose waves are done. Read from `activated`, where six is "blown,
/// still standing, still offering the button" and seven is "already shattered" - either answers
/// this, because both mean the waves came and went. See Target.Shatterable.
///
/// **Have all the explosives fired?** The panel's placed count returns to nought once the chain has
/// run, so nought placed after a detonation means the last blast has happened and what is left
/// standing is the last group of monsters.
///
/// Both together is not proof and is not treated as any: nothing acts on this but a drawing. The
/// reasoning is published so a dump can say which half is holding it back.
/// </summary>
internal static class Ending
{
    /// <summary>Whether the fight is probably finished. See the class summary for what that rests on.</summary>
    public static bool ProbablyOver { get; private set; }

    /// <summary>Why it says what it says, for the dump. Never empty once it has been asked.</summary>
    public static string Says { get; private set; } = "not asked yet";

    /// <summary>
    /// Re-decides it, cheaply enough to call every frame.
    ///
    /// Every term is a panel read or a state already cached on a target, so there is no walk of the
    /// interface and no search. The remnants come from the plan rather than from the site, which is
    /// the difference between "the chain is finished" and "every remnant in the map is" - a remnant
    /// the chain never reached would otherwise hold this false for ever.
    /// </summary>
    public static void Watch(GameController gc, Scan scan, Plan plan)
    {
        // **Latched, because the detonator entity goes away and the live read then says no.**
        //
        // ExplosivesDetonated walks the entity list for this site's detonator and returns -1 when it
        // is not in it - which is below one, so a spent site whose detonator has unloaded reads as a
        // site that was never set off. That is precisely the stretch this flag exists for: looting
        // happens after the blasts, spread across a dig site, and walking away from the machine is
        // the normal way to do it.
        //
        // Detonator.SetOff is the same question already answered and remembered, recorded while the
        // walk had the entity in hand - see its own comment, which was written for a detonator that
        // will not be loaded by the time somebody asks. The live read stays in front of it as the
        // fast path for the site you are standing on.
        var site = Detonator.DetonatorGridPosition(gc);

        if (Detonator.ExplosivesDetonated(gc) < 1 && !(site != Vector2.Zero && Detonator.SetOff(site)))
        {
            ProbablyOver = false;
            Says = "the chain has not been set off";

            return;
        }

        var placed = Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, -1);

        if (placed != 0)
        {
            ProbablyOver = false;
            Says = $"{placed} explosives are still on the ground, so blasts are still to come";

            return;
        }

        var (blown, all) = Remnants(gc, scan, plan);

        if (blown < all)
        {
            ProbablyOver = false;
            Says = $"{blown} of {all} of the chain's remnants have been blown, so waves are still owed";

            return;
        }

        ProbablyOver = true;
        Says = all == 0
            ? "every explosive has fired and the chain caught no remnant"
            : $"every explosive has fired and all {all} of the chain's remnants are spent";
    }

    /// <summary>
    /// How many of the remnants the chain was built around have been blown, of how many there are.
    ///
    /// Matched by position against the scan, because the plan records the CELLS each link was chosen
    /// for rather than the targets - see Plan.CaughtBy - and a cell is the only thing the two agree
    /// on once entities have streamed in and out.
    /// </summary>
    private static (int Blown, int All) Remnants(GameController gc, Scan scan, Plan plan)
    {
        if (plan is not { Points.Count: > 0 } || scan == null)
            return (0, 0);

        var wanted = new HashSet<(int X, int Y)>();

        for (var i = 0; i < plan.Points.Count; i++)
        {
            foreach (var at in plan.CaughtBy(i))
                wanted.Add(((int)MathF.Round(at.X), (int)MathF.Round(at.Y)));
        }

        if (wanted.Count == 0)
            return (0, 0);

        int blown = 0, all = 0;

        foreach (var target in scan.At(Detonator.DetonatorGridPosition(gc)))
        {
            if (target.Kind != TargetKind.Remnant ||
                !wanted.Contains(((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y))))
                continue;

            all++;

            // Six is blown and still offering its button, seven is already shattered. Both mean the
            // waves came up and went down, which is the question. See Target.Shatterable.
            if (target.State("activated") >= 6)
                blown++;
        }

        return (blown, all);
    }

    /// <summary>Forgets it with the area, so a new map does not inherit a finished one.</summary>
    public static void AreaChange()
    {
        ProbablyOver = false;
        Says = "not asked yet";
    }
}
