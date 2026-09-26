using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.MemoryObjects;
using RectangleF = ExileCore2.Shared.RectangleF;
using Graphics = ExileCore2.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// What is out there, drawn on the ground.
///
/// Split in two on purpose. The rewards over the remnants are the thing worth having; everything
/// else here shows the working and sits behind the debug switch, because a ring round every one of
/// a hundred markers is how you check a classifier rather than how you play.
///
/// Circles come from Graphics.DrawCircleInWorld, which projects and samples terrain height per
/// segment for free. Worth saying because the obvious alternative is to do it by hand - the plugin
/// this replaces hand-tessellates 72 segments per circle per explosive per frame, each with its own
/// WorldToScreen and terrain lookup, which is where its frame time goes.
///
/// Whether the blast circles follow that terrain or lie flat is a display setting - see
/// PlanDisplaySettings.FlatBlastCircles, which has the argument about which of the two is truthful.
/// </summary>
internal static class Overlay
{
    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings, Scan scan,
        Snap snap, Valuation valuation, Boundary boundary,
        Planning planning, Placement placement, Blast blast, Scoring scoring, Spawns spawns,
        Cleared cleared, Scouted scouted, List<RectangleF> covered)
    {
        // The diagnostic height offset, refreshed before anything projects. See
        // DebugSettings.MarkerHeightOffset and Target.Where.
        Target.MarkerHeightOffset = Safe.Read(settings, static s => s.Debug.MarkerHeightOffset.Value, 0f);
        Target.MarkerFacingOffset = Safe.Read(settings, static s => s.Debug.MarkerFacingOffset.Value, 0f);
        Target.MarkerSidewaysOffset = Safe.Read(settings, static s => s.Debug.MarkerSidewaysOffset.Value, 0f);
        Target.MarkersAtInteractCentre = Safe.Read(settings, static s => s.Debug.MarkersAtInteractCentre.Value, false);

        // Only the site being stood in. A map can hold two encounters, and merging them would
        // inflate every count and let the planner chain across the map to the other one.
        var site = Detonator.DetonatorGridPosition(gc);
        // **Plus the remnants that belong to no dig site at all**, which were drawn by nobody.
        //
        // At(site) is what keeps one expedition's markers out of the other's, and a remnant standing
        // on its own belongs to neither - so it had no marker, no socket count, no reward text and
        // no red when the player's pick differed from the planner's. It is still a remnant with a
        // decision to make, and the decision is the only thing about it worth drawing.
        List<Target> targets;

        using (Spent.On("Including"))
            targets = site == Vector2.Zero ? scan.Targets : Including(scan, site, gc);

        // Before the test below, because "nothing found yet" is one of the things it has to say and
        // a status line that vanishes exactly when there is a problem is worse than none.
        if (settings.Display.DrawScore)
            using (Spent.On("Status"))
            {
                Status(graphics, gc, settings, scan, planning, placement, scoring, valuation,
                    scouted);
            }

        // Outside that test on purpose, whatever the indentation used to suggest. The invitation is
        // how you get a plan in the first place, so hiding it with the plan would leave the key
        // undiscoverable; it has its own reasons to stay quiet and they are in the method.
        using (Spent.On("Invite"))
        {
            Invite(graphics, gc, settings, planning, placement, covered);
        }


        // **Every cross layer, before the markers are counted.** These draw ground and the game's own
        // verdicts about ground, and none of them reads the target list - but they all sat below the
        // "no markers, nothing to draw" return, so on a site with no content left they went silent
        // together. Switching the reach metric then appeared to change nothing, which is the one
        // conclusion the comparison must never hand out by accident.
        //
        // Where a regular explosive may and may not go, straight from the game's own rule. See
        // Forbidden.Placement.
        // Behind the debug switch as well as its own, so it ships on without shipping visible:
        // it is on by default, and debug mode off is what keeps it off the screen in play.
        if (settings.Debug.ShowOverlay && settings.Debug.ShowPlacement)
            using (Spent.On("Forbidden.Placement"))
            {
                Forbidden.Placement(graphics, gc, covered, settings);
            }

        // Behind the debug switch as well as its own, for the same reason as the layer above: it is
        // on by default, so its own toggle cannot keep it off the screen in play and debug mode off
        // is what does. It was the one cross layer here without the master switch, so the cyan
        // numbers drew for everybody.
        if (settings.Debug.ShowOverlay && settings.Debug.ShowPlacedExplosives)
            using (Spent.On("Border.Placements"))
            {
                Border.Placements(graphics, gc, covered, settings.Debug.MarkSpots.Value);
            }

        if (targets.Count == 0)
            return;

        // Which chain is on show, worked out once for everything that draws it.
        //
        // While the search runs, the best chain it has found so far stands in for the finished one.
        // It is a real chain rather than a sketch - every link satisfies reach, spacing and
        // placement - just not the last word, so it is drawn exactly like the answer and simply gets
        // better. Catches are not known until the search commits, so it draws with none.
        //
        // Once, and not once per surface: the world and the minimap showing different chains - the
        // one being found and the one found last time - reads as two plans, and the question the
        // minimap exists to answer is what the plan looks like.
        // **And only once the player has asked for a plan.** A rehearsal solves the site on the way
        // in and keeps what it finds; drawing it would answer a question nobody asked, halfway
        // across the map, and take the decision to press the key away from the player. See
        // Planning.ShownAt.
        // Asked for, or offered up front because the presolve was switched on and told to show its
        // working. See SolverSettings.ShowPresolve.
        var here = Vector2.Distance(planning.Site, site) < 1f &&
                   (Planning.Showing(site) ||
                    (settings.Solver.Presolve.Enable.Value && settings.Solver.Presolve.ShowPresolve.Value));
        var live = here && settings.Display.ThePlan.ShowProgress && planning.Searching ? planning.Live : null;
        var shown = live is { Count: > 0 } ? new Plan(live, 0d, 0) : planning.Plan;

        // One answer for the whole chain, used by the world drawing and the minimap alike: the
        // two pictures of one plan must never disagree about which spots are live. See Reachable.
        //
        // **Nothing is green while the search is still running.**
        //
        // A live chain is drawn without its Catches - the search does not know what each link is
        // FOR until it commits - and every readiness test that depends on them therefore answers
        // "nothing owed here". So a link sitting on a remnant whose reward has not been chosen, with
        // the combinations button nowhere on screen, was painted green: the one colour that is
        // supposed to mean the key will work if you press it now.
        //
        // The honest answer during a solve is that nothing is known yet, and the honest colour for
        // that is not green. It costs nothing: a chain that is still being found is not one to be
        // placing from.
        bool[] lit;

        using (Spent.On("Reachable"))
        {
            lit = planning.Searching
                ? new bool[shown?.Points.Count ?? 0]
                : Reachable(gc, settings, shown, placement, cleared, covered);
        }

        // The plan under the rest, so the rings and the text sit over it rather than beneath it.
        if (settings.Display.DrawInWorld && here)
        {
            // **The detailed pass runs when EITHER thing that needs it is being drawn.**
            //
            // Breakdown is the only caller of the detailed scoring pass, and that pass is what fills
            // Planner.RuneTallyByRemnant - the table the yellow line under every remnant is read from. It was
            // called only when spot worth was ticked, so turning that one setting off silently took
            // the rune text off every remnant as well, and left whatever the last pass had written
            // sitting there as a stale snapshot. Two unrelated settings, one of them quietly
            // governing the other.
            //
            // The pass runs for either; only the per-link figures are withheld when spot worth is
            // off, which is what that setting actually means. See Planning.Breakdown.
            List<(double Content, double Carried)> worth;

            using (Spent.On("Breakdown"))
            {
                worth = settings.Display.PlacementCircle.BlastValue.ShowSpotWorth || settings.Display.Remnants.Propagation.ShowWaves
                    ? planning.Breakdown(shown.Points)
                    : null;
            }

            // The explosives already down when this plan was made, so its first link is numbered
            // as the chain's next blast rather than as its first. See Numbered.
            using (Spent.On("Chain"))
            {
                Chain(graphics, gc, settings, shown, targets, blast, cleared, covered, placement,
                    lit, settings.Display.PlacementCircle.BlastValue.ShowSpotWorth ? worth : null,
                    planning.Laid);
            }
        }

        // Under the rings, so the plan reads over the wash rather than through it.
        //
        // A switch per kind of site, because the two are different amounts of colour: an ordinary
        // expedition paints a patch around the markers, a Grand one paints a map. See
        // Display.UnscoutedGround.ShowUnscoutedExpedition.
        if ((Detonator.Grand(gc)
                ? settings.Display.UnscoutedGround.ShowUnscoutedGrand
                : settings.Display.UnscoutedGround.ShowUnscoutedExpedition).Value)
            using (Spent.On("Minimap.Unscouted"))
            {
                Minimap.Unscouted(graphics, gc, settings, scouted);
            }

        // Outside the ShowPlan block, and outside the panel test the rest of the overlay lives
        // under: the minimap is drawn by the game on top of whatever else is open, so a ring on it
        // is only wrong when the minimap itself is not there - which Minimap decides for itself.
        if (settings.Display.DrawOnMinimap && here)
        {
            // Worked out once for both surfaces. See Placement.Ready.
            using (Spent.On("Minimap.Draw"))
            {
                Minimap.Draw(graphics, gc, settings, shown, blast, cleared, lit,
                    settings.Display.ThePlan.ColourInRangeAsNext);
            }
        }

        // The barrels, whether or not there is a plan yet: they are the site's own blasts and knowing
        // where they are is what shapes a chain. See Minimap.Barrels.
        if (settings.Display.DrawOnMinimap)
            using (Spent.On("Minimap.Barrels"))
            {
                Minimap.Barrels(graphics, settings, targets,
                    Blown(gc, settings, targets, blast.Radius(gc, settings) ?? 0f));
            }

        // Over the plan and under the labels, so a marker inside a blast circle still reads as
        // marked. Drawn whether or not there is a plan: the whole point of marking one is to say
        // something before the next solve.
        //
        // **Plus the bans, which nobody placed.** Gating this on marks the player made by hand meant
        // a refused modifier bent the chain with nothing on screen to say why - the one case where
        // the drawing matters most, since there is no remembered keypress to explain it. See MustAvoidMods.
        if (Insisted.Here.Count > 0 ||
            (MustAvoidMods.Banned > 0 && (MustAvoidMods.DrawInWorld || MustAvoidMods.DrawOnMap)))
        {
            using (Spent.On("Musts"))
            {
                Musts(graphics, gc, settings, targets, covered, shown, blast);
            }

            if (settings.Display.DrawOnMinimap)
                using (Spent.On("Minimap.Musts"))
                {
                    Minimap.Musts(graphics, gc, settings, targets);
                }
        }

        if (settings.Display.Remnants.Rewards.LineAbove.Value > 0f)
            using (Spent.On("Lines"))
            {
                Lines(graphics, gc, targets, settings, valuation, covered);
            }

        // **After the chain has gone off, which is what the group it sits in is named for.**
        //
        // It was drawn from the moment a site was readable, so a line to the nearest shatterable
        // thing crossed the screen while the chain was still being planned and placed - pointing at
        // a remnant the plan was about to put an explosive on, during the one stretch where the
        // ground wants to be legible. Nothing it says is useful before detonation: everything it
        // points at is either still to be blown or not yet worth walking to.
        //
        // Detonated is the plugin's own definition of "the chain has gone off" - the detonator's
        // activated state - and is the same gate Cleared uses to start tidying. See
        // Detonator.ExplosivesDetonated.
        // Which end of the expedition it starts at is the player's, and starting at the site going
        // live is the default. See PostExpeditionSettings.LineFrom.
        // Start means the site is live, not merely that the setting is on - PanelReady is the
        // plugin's own test for the detonator panel having been filled in for the encounter in front
        // of you, which is the earliest moment there is an expedition to loot.
        var fromStart = string.Equals(
            Safe.Read(settings, static s => s.Automation.PostExpedition.LineFrom.Value,
                PostExpeditionSettings.FromStart),
            PostExpeditionSettings.FromStart, StringComparison.Ordinal);

        var looting = settings.Automation.PostExpedition.Line &&
                      (fromStart ? Detonator.PanelReady(gc) : Ending.ProbablyOver);

        if (looting)
            using (Spent.On("Loot"))
            {
                Loot(graphics, gc, settings, scan, covered);
            }

        if (settings.Display.Remnants.Rerolls.RollLine)
            using (Spent.On("Rolls"))
            {
                Rolls(graphics, gc, settings, covered);
            }

        // Outside that switch on purpose: it has nothing to do with wanting a line towards loot.
        using (Spent.On("Baited"))
        {
            Baited(graphics, gc, settings, covered);
        }

        // What the spot under the cursor would collect, while the circle is up to ask about it.
        if (settings.Display.PlacementCircle.BlastValue.ShowCursorWorth && Detonator.Placing(gc))
            using (Spent.On("Worth"))
            {
                Worth(graphics, gc, settings, planning, covered);
            }

        // What the last attempt could not light, until the next explosive answers the question.
        // See DisplaySettings.Unreachable - three states, which is one more than the pair of
        // toggles this replaced could say.
        if (Safe.Read(() => settings.Display.PlacementCircle.Unreachable.Value, "Always") is var when &&
            (when == "Always" || (when == "In placement mode only" && Detonator.Placing(gc))))
            using (Spent.On("Unlit"))
            {
                Unlit(graphics, gc, placement, targets, covered);
            }

        // **Not behind the Unreachable setting, though it draws the same triangle.** That setting is
        // about an automated run's report on ground it could not use; this is about what the player
        // just did with their own cursor, and somebody with automation switched off has every reason
        // to want the second and not the first. It draws nothing at all unless an explosive is off
        // its planned spot, so there is nothing to switch off. See Astray.
        using (Spent.On("Astrayed"))
        {
            Astrayed(graphics, gc, settings, planning, targets, blast, covered);
        }

        if (settings.Display.Remnants.Rewards.ShowRewards || settings.Display.Remnants.Propagation.ShowWaves ||
            settings.Display.Remnants.Rerolls.HighlightRerolls)
            using (Spent.On("Remnants"))
            {
                Remnants(graphics, gc, targets, settings, valuation, scan, planning.Plan, covered);
            }

        // What the game is lighting up right now, whether or not the debug overlay is on.
        //
        // **The two halves of this are suppressed differently and used to be suppressed together.**
        // The rings are worth dropping when the debug pass is already ringing every marker in the
        // site - two rings on one marker say nothing the one said. The NAMES have no such duplicate:
        // the debug pass writes a label only under its own Show art toggle, so folding them into the
        // same test turned "show entity animation names" off whenever the debug overlay was on with
        // markers, which is a combination that says nothing about wanting the names hidden. Measured
        // from a live settings file: names on, art off, debug markers on, and nothing drawn.
        var rings = settings.Display.PlacementCircle.RingsInCircle &&
                    !(settings.Debug.ShowOverlay && settings.Debug.ShowMarkers);
        var names = settings.Display.PlacementCircle.ShowArtNames;

        if (rings || names)
            using (Spent.On("Lit"))
            {
                Lit(graphics, gc, targets, covered, settings, rings, names);
            }

        // **The unpriced flag is not part of the debug overlay, though it is drawn by the same pass.**
        //
        // It is a call to action about an object in front of you - the plugin is valuing something on
        // a placeholder nobody chose - and a tester cannot be expected to turn on a debug overlay to
        // find out. It said as much in its own doc comment and in the comment on the pass below, and
        // was then gated on ShowOverlay along with everything else, so it drew for nobody who had not
        // already opened Debug.
        //
        // Everything else this pass draws keeps its own master switch: `debugging` is required
        // alongside each individual toggle below, so letting the pass run for the flag's sake does
        // not turn the marker rings, the art labels or the score lines on behind anybody's back.
        var debugging = settings.Debug.ShowOverlay;

        if (!debugging && !settings.Debug.ShowUnpriced && !settings.Debug.ShowUnexpected)
            return;

        var placing = Detonator.Placing(gc);

        // One pass for both the rings and the art labels: each needs the marker projected to the
        // screen, and projecting twice for two things drawn in the same place is the sort of waste
        // that hid in having them in two files.
        // **Also when nothing under here is switched on**, because this pass carries the red label
        // that says an object in front of you is being valued on a placeholder - and that is a call
        // to action rather than a debug drawing. It used to ride on the marker rings being on, which
        // was true until those were defaulted off; the one thing a tester most needs to see then
        // silently stopped appearing. See DebugSettings.ShowUnexpected for the other red mark, which
        // is about objects the scan does not know at all and has a switch of its own.
        if ((debugging && (settings.Debug.ShowMarkers || settings.Debug.ShowArtNames ||
                          settings.Debug.ExplainScores)) || settings.Debug.ShowUnpriced)
        {
            var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

            if (camera != null)
            {
                // **One marker at a time, the one under the cursor.**
                //
                // A line per marker was unreadable on anything but an empty site - and the answer to
                // that was a cap, which made it unreadable AND incomplete. Pointing at a marker is
                // already how this plugin asks a question about one; the same test the must-take key
                // uses picks the same marker, so the thing you are pointing at is the thing that
                // explains itself. See Insisted.Under.
                Target asked;

                using (Spent.On("Insisted.Under"))
                    asked = debugging && settings.Debug.ExplainScores ? Insisted.Under(gc, targets) : null;

                using (Spent.On("Marker loop"))
                {
                    foreach (var target in targets)
                    {
                        Marker(graphics, gc, settings, planning, target, placing, camera, covered,
                            asked);
                    }
                }
            }
        }

        // **A barrel's own blast, in a pass of its own.**
        //
        // The game lights up what an EXPLOSIVE catches; a barrel's detonation has not happened while
        // the placement circle is up, so the one thing on a dig site that gives a chain free radius
        // is the one thing invisible until it goes off. Sixty grid against an explosive's thirty
        // five, so it is worth seeing before choosing where to stand.
        //
        // **Not inside the marker loop above**, which is where this was first put and never drew:
        // that loop is behind the two DEBUG switches, and a thing shown because the plan is shown
        // cannot live behind a switch for showing marker art. Its own pass, its own question.
        if (settings.Display.DrawInWorld)
        {
            using var barrels = Spent.On("Barrel blasts");

            var blown = Blown(gc, settings, targets, blast.Radius(gc, settings) ?? 0f);

            foreach (var target in targets)
            {
                if (target.Kind != TargetKind.Barrel || target.Sets <= 0f || target.Spent ||
                    blown.Contains(Cell(target.Grid)))
                    continue;

                var where = target.Where(gc);

                if (where == Vector3.Zero)
                    continue;

                if (settings.Display.ThePlan.ShowBarrels)
                {
                    graphics.DrawCircleInWorld(where, target.Sets * Detonator.GridToWorld,
                        settings.Display.ThePlan.BarrelColour, 2f, 32, true);
                }
            }
        }

        if (Planner.Drawn > 0)
            using (Spent.On("Spots"))
            {
                Spots(graphics, gc, settings, blast, covered);
            }

        // **Not behind debug mode, like the unpriced flag beside it.** Both are about an object in
        // front of you that the plugin cannot account for, and both are the only way anybody finds
        // out - one for something it can see and cannot value, one for something a blast acts on
        // that it cannot see at all. A tester should not need the debug overlay to be told either.
        if (settings.Debug.ShowUnexpected)
            using (Spent.On("Unknown"))
            {
                Unknown(graphics, gc, settings, scan, site, covered);
            }

        if (debugging && placing && settings.Debug.ShowBlastRadius)
            using (Spent.On("Blast"))
            {
                Blast(graphics, gc, settings, covered);
            }

        // Behind the master switch like everything else in this pass. It was called unconditionally,
        // which was correct while the pass itself could only run with debug mode on - and stopped
        // being correct when the pass was let through for the unpriced flag alone, since the counts
        // column and the measurements inside it are debug tools and ShowTally is on by default.
        if (debugging)
            using (Spent.On("Tally"))
            {
                Tally(graphics, gc, settings, targets, placing, snap, scan, valuation,
                    boundary, spawns, covered, planning);
            }
    }

    // ------------------------------------------------------------------ content

    private static void Marker(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Planning planning, Target target, bool placing, Camera camera, List<RectangleF> covered,
        Target asked)
    {
        // Read once and kept on the target: a marker never moves. One the game has unloaded still
        // draws, from the terrain height at where it was seen.
        var world = target.Where(gc);

        if (world == Vector3.Zero)
            return;

        var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        if (at == Vector2.Zero || Panels.Covers(covered, at))
            return;

        // ShowOverlay as well, because this pass now runs for the unpriced flag alone and the
        // rings are part of the debug overlay. See the gate in Draw.
        if (settings.Debug.ShowOverlay && settings.Debug.ShowMarkers && !target.Spent)
            Shape(graphics, gc, settings, target, world, placing && target.Glowing);

        if (settings.Debug.ShowOverlay && settings.Debug.ShowArtNames)
            Named(graphics, target.Label, at);

        if (ReferenceEquals(target, asked))
            Explained(graphics, settings, planning, target, at);

        // **An object nobody has priced says so, on the ground, every time you walk past it.**
        //
        // The red line Unexpected draws says "this exists and has no name"; it goes on saying that
        // whether or not anybody has acted on it, and there was nowhere to act. Now there is, so the
        // marker names the place: the weight it is carrying meanwhile, and where to change it.
        //
        // Two lines and centred, because one is the width of a dig site - and only while it is still
        // at the shipped guess, since an object somebody has priced is not asking for anything.
        // Relics too, and not only the objects that fell off the end of the classifier. A relic the
        // table cannot name used to take the generic "unknown relic" weight, which looks like an
        // answer and is not one - see Weighing.Relic.
        // **Nothing to price about something that is no longer there.**
        //
        // A marker outlives its entity: the scan keeps what it saw so a chain can be planned around
        // content the game has streamed out, and that is the whole reason the remembered list
        // exists. But the key an unknown object is filed under is built from things only a live
        // entity can answer - its states, its render name, its label - so a marker whose entity has
        // gone cannot be matched against the table and reads as unpriced no matter how carefully it
        // was priced.
        //
        // After a chain goes off that is most of the site. Sixteen zealots were blown up, their
        // entities destroyed, and every one of their markers went on asking to be weighed - while
        // the settings tab showed nothing new, because there was nothing left to file either.
        //
        // The red text is a call to action about a thing in front of you. No entity, no action.
        // **The switch is asked before the object is, because asking the object is not free.**
        // Unknowns.Unread builds the key this target is filed under - eight fields joined into one
        // string - then looks it up, then does the same for everything the target grants. That is
        // a handful of allocations and two long-string hashes per marker, and a dig site holds a
        // hundred of them. Behind the switch it costs nothing when the flag is off. A line above
        // this built the same key unconditionally and threw the answer away.
        if (settings.Debug.ShowUnpriced && target.Live && Unknowns.Unread(target))
        {
            // **Names the row, because "open the table" is no help with several unset objects on one
            // map.** This dropped the object's name on the reasoning that the marker above it already
            // said what the thing was - true while the flag rode on the debug overlay, and false now
            // that it draws without it: the marker label is drawn under Show markers and Show art,
            // both of which need the master switch, so in the ordinary case nothing else on screen
            // names the object at all. A map with three unset things then gave three identical lines
            // and no way to tell which row each wanted.
            //
            // The name is built the same way the table builds its own, so the string on the ground is
            // the string to look for in the Name column rather than a second description of the same
            // row. See Unknowns.Name, and Catalogue's site rows, which call it with the same pair.
            var named = Safe.Read(target, static t => Unknowns.Name(Unknowns.Key(t), t.Label ?? ""), "");

            Unpriced(graphics, at, named.Length > 0
                ? $"Unset - find \"{named}\" in the Weight Reference Table"
                : "Unset - open the Weight Reference Table");
        }
    }

    /// <summary>
    /// How far below the marker's own point the name sits, in pixels.
    ///
    /// The projected point is the base of the marker, where the art meets the ground, and text
    /// drawn from there starts at that line and runs up-right - so it read as hanging off the
    /// marker's shoulder rather than belonging to it. Down a little and centred puts it under the
    /// thing it names, clear of the ring drawn at the same point.
    /// </summary>
    /// **Twelve pixels lower than it was**, asked for after reading it in game: at ten the name sat
    /// close enough to the marker to read as part of it rather than under it. The score lines in
    /// Explained hang from this same figure, so they move with it - which is right, since the whole
    /// stack hangs off one point and it was the top of the stack that was too high.
    private const float NameDrop = 22f;

    /// <summary>A marker's name, centred under it. See NameDrop.</summary>
    /// <summary>
    /// This dig site's markers, and every loose remnant in the area beside them. See where it is
    /// called.
    /// </summary>
    private static List<Target> Including(Scan scan, Vector2 site, GameController gc)
    {
        var here = scan.At(site);
        var all = new List<Target>(here);

        foreach (var target in scan.Targets)
        {
            if (target.Kind == TargetKind.Remnant && !target.Spent &&
                Vector2.Distance(target.Site, site) >= 1f && scan.Loose(gc, target.Grid))
                all.Add(target);
        }

        return all;
    }

    /// <summary>
    /// A centred red line under a marker nobody has priced yet. See where it is called.
    /// </summary>
    private static void Unpriced(Graphics graphics, Vector2 at, string text)
    {
        var size = graphics.MeasureText(text);

        graphics.DrawText(text, new Vector2(at.X - size.X / 2f, at.Y + NameDrop + size.Y),
            Color.FromArgb(255, 235, 90, 90));
    }

    /// <summary>What to call a doubted object on screen. See where it is called.</summary>
    private static string Naming(Scan scan, Unexpected.Found one)
    {
        // Any target still waiting to be priced, not only the ones filed as Unknown. A relic is
        // TargetKind.Relic, so this found nothing for one and fell back to the tail of its metadata
        // - which is why five different Vaal relics all read "ExpeditionRelic" on the ground.
        foreach (var target in scan.Targets)
        {
            if (Vector2.Distance(target.Grid, one.Grid) < 1.5f && Unknowns.Unread(target))
                return target.Label;
        }

        return one.Name;
    }

    /// <summary>
    /// What a marker is worth where the chain puts it, and what moved it.
    ///
    /// **A weight on its own explains nothing, because nothing here is worth its weight.** A runic
    /// monster is 5.45 until a relic three links earlier passes 20% on to monsters, and then it is
    /// 6.54 - and the plugin had no way to say so on the ground. The weight was on one tab, what
    /// carried it on another, and which links reached this one nowhere at all.
    ///
    /// **Attributed to the thing that was modified rather than to the thing doing the modifying.**
    /// The objective credits a carrier with a percentage of everything downstream, which is the
    /// right way to SCORE it and the wrong way to read it: it answers "what is this relic worth"
    /// when the question in front of you is "why is the chain coming here for that". Same arithmetic,
    /// turned round, so a marker says what reached it.
    ///
    /// Inclusive of its own link, as the model is: the blast that sets a relic off also unearths
    /// what it covers, and that is affected too.
    /// </summary>
    private static void Explained(Graphics graphics, AutoExpeditionSettings settings,
        Planning planning, Target target, Vector2 at)
    {
        if (target == null)
            return;

        var drop = NameDrop + (settings.Debug.ShowArtNames ? 16f : 0f);

        void Line(string text, Color colour)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var size = graphics.MeasureText(text);

            // **Backed, because this is read against a dig site rather than against a menu.** The
            // ground under a marker is whatever the tileset put there - pale sand, lit rubble, a
            // blast circle - and a line of text on it is legible or not depending on where you are
            // standing, which is no way to read arithmetic. Its own colour rather than the reward
            // background, which is a display preference about something else.
            graphics.DrawTextWithBackground(text, new Vector2(at.X - size.X / 2f, at.Y + drop),
                colour, Behind);

            drop += 16f;
        }

        var weight = Safe.Read(() => Weighing.WeightOfTarget(target, settings), 0f);
        var link = Link(planning, target);

        if (link < 0)
        {
            // Not in the chain, so there is no place in it to be worth anything at. Its own weight
            // is still worth saying - it is what the search was offered and turned down.
            Line($"weight {weight:0.#} - not in the plan", Color.FromArgb(255, 170, 170, 170));
            Line(Tags.Line(target), Color.FromArgb(255, 130, 130, 130));

            return;
        }

        var mask = Tags.Mask(target);
        var lifted = 0f;
        var from = 0;

        // Every link up to and including this one, because a carry reaches forwards from where it
        // is picked up. See Planner.Settle.
        for (var step = 0; step <= link; step++)
        {
            foreach (var caught in planning.Plan.CaughtBy(step))
            {
                var carrier = Carrier(planning, caught);

                if (carrier == null)
                    continue;

                foreach (var (tag, percent) in Reaching(carrier, mask, target))
                {
                    if (percent <= 0f)
                        continue;

                    lifted += percent;
                    from++;

                    Line($"+{percent:0.#}% {Tags.Known[tag]} from link {step + 1}",
                        Color.FromArgb(255, 255, 205, 90));
                }
            }
        }

        Line($"weight {weight:0.#}" +
             (lifted > 0f ? $" -> {weight * (1f + lifted / 100f):0.#} at link {link + 1}"
                 : $" at link {link + 1}"),
             lifted > 0f ? Color.FromArgb(255, 150, 255, 170) : Color.White);

        Line(Tags.Line(target), Color.FromArgb(255, 130, 130, 130));
    }

    /// <summary>What the score explanation is written on, so it reads over any ground. See Explained.</summary>
    private static readonly Color Behind = Color.FromArgb(225, 0, 0, 0);

    /// <summary>Which link of the plan this marker is counted as caught by, or -1.</summary>
    private static int Link(Planning planning, Target target)
    {
        var plan = planning?.Plan;

        if (plan?.Catches == null)
            return -1;

        for (var i = 0; i < plan.Catches.Count; i++)
        {
            foreach (var caught in plan.Catches[i])
            {
                if (Vector2.Distance(caught, target.Grid) < 1f)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>The planned target standing at a grid position, or nothing.</summary>
    private static PlanTarget Carrier(Planning planning, Vector2 grid)
    {
        var targets = planning?.Env?.Targets;

        if (targets == null)
            return null;

        foreach (var planned in targets)
        {
            if (Vector2.Distance(planned.Grid, grid) < 1f)
                return planned;
        }

        return null;
    }

    /// <summary>
    /// What a carrier passes on that actually reaches this marker.
    ///
    /// A scoped carry reaches it when the marker carries the tag; the flat carry reaches whatever
    /// the propagation model counts as unearthed - monsters, and a remnant's own waves - which is
    /// the same test Planner.Unearths makes.
    /// </summary>
    private static IEnumerable<(int Tag, float Percent)> Reaching(PlanTarget carrier, long mask,
        Target target)
    {
        if (carrier.Spread != null)
        {
            foreach (var (_, tag, percent, _) in carrier.Spread)
            {
                if (tag == Tags.Monsters ? Unearthed(target) : (mask & (1L << tag)) != 0L)
                    yield return (tag, percent);
            }
        }

        if (carrier.Carries > 0f && Unearthed(target))
            yield return (Tags.Monsters, carrier.Carries);
    }

    /// <summary>Whether the propagation model counts this marker among what a chain unearths.</summary>
    private static bool Unearthed(Target target) =>
        target.Kind is TargetKind.Monster or TargetKind.Elite or TargetKind.Hatch or
            TargetKind.Caged or TargetKind.Monolith or TargetKind.Remnant;

    private static void Named(Graphics graphics, string text, Vector2 at)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var size = graphics.MeasureText(text);

        graphics.DrawText(text, new Vector2(at.X - size.X / 2f, at.Y + NameDrop), Color.White);
    }

    /// <summary>
    /// The marker itself: a circle, in world space, at the marker's own radius.
    ///
    /// Every word of that is load-bearing.
    ///
    /// **In world space**, because the camera projects a circle on the ground into a skewed
    /// ellipse. The blast circle is drawn that way, so a ring drawn as a true circle in screen
    /// space is in a different projection and overlapping it means nothing. That was briefly the
    /// case here and the rings were visibly wrong against the bomb radius.
    ///
    /// **At the size the setting says**, which is meant to be the distance at which that marker
    /// would be caught, so the ring overlaps the blast circle exactly when it would be. It was
    /// briefly taken from the marker's Render.Bounds instead - those are the extent of the ART, so
    /// a monster marker came out at 41.3 world units because that is the size of the signpost, and
    /// every ring was about twice what it should be.
    ///
    /// **A circle**, because a square touches at its corners before its edges, and overlap would
    /// stop meaning in range.
    ///
    /// followTerrain is off, which is the one corner cut. It samples the ground height per segment,
    /// and across a ring four grid units wide the ground barely moves - so the ring sits flat at
    /// the marker's own height. The blast circle keeps it: that one is thirty units across and
    /// genuinely spans slopes.
    /// </summary>
    /// <summary>
    /// A ring colour with the one opacity setting applied to its alpha.
    ///
    /// Scaled rather than replaced, so a colour set part-transparent on purpose keeps its relation
    /// to the rest. A hundred returns the colour untouched, which is the ordinary case and worth
    /// not allocating for. See PlacementCircleSettings.RingOpacity.
    /// </summary>
    private static Color WithRingOpacity(AutoExpeditionSettings settings, Color colour)
    {
        var percent = Math.Clamp(
            Safe.Read(() => settings.Display.PlacementCircle.RingOpacity.Value, 100), 0, 100);

        return percent >= 100 ? colour : Color.FromArgb(colour.A * percent / 100, colour);
    }

    private static void Shape(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Target target, Vector3 world, bool lit)
    {
        var colour = WithRingOpacity(settings, Colour(settings, target));
        var covered = WithRingOpacity(settings, (Color)settings.Debug.CoveredColour);

        // Per art. The ring is meant to overlap the blast circle exactly when the thing would be
        // caught, and a siren egg is caught from eight times further out than a marker - drawn at
        // the marker size it looked like the chain was missing eggs it was comfortably taking.
        var radius = Extents.Of(target)
                     * Detonator.GridToWorld;

        var segments = Segments(settings, radius);

        if (target.Kind == TargetKind.Chest)
        {
            Dashed(graphics, gc, world, radius, colour, 2f, segments);

            if (lit)
                Dashed(graphics, gc, world, radius * 1.25f, covered, 2f, segments);

            return;
        }

        if (lit)
        {
            graphics.DrawFilledCircleInWorld(world, radius, colour, segments, false);
            graphics.DrawCircleInWorld(world, radius * 1.25f, covered, 2f, segments, false);
        }
        else
        {
            graphics.DrawCircleInWorld(world, radius, colour, 2f, segments, false);
        }
    }

    /// <summary>
    /// How many sides to draw a ring of this size with.
    ///
    /// **A fixed count is a fixed number of corners, not a fixed amount of roundness.** Twelve sides
    /// look like a circle at a marker's 24.5 world units and like a dodecagon at a siren egg's 188,
    /// because what the eye picks up is how far the flat side sags away from the true arc - and that
    /// sag grows with the radius while the count stays put.
    ///
    /// Holding the sag constant instead makes the count grow with the SQUARE ROOT of the radius: the
    /// sag is r(1 - cos(pi/n)), which for the counts in use is near enough r(pi^2 / 2n^2), so n has
    /// to rise as the square root of r to hold it. That is a weaker growth than it feels like it
    /// should be, and it is the reason this is worth writing down rather than scaling by radius -
    /// scaling by radius would put an egg at ninety odd sides for no visible gain over thirty.
    ///
    /// The setting is the count at the ordinary marker size, so the knob keeps meaning what it
    /// meant. The cap is there because forty eggs on a Grand site are forty rings a frame, and the
    /// whole reason the setting defaults low is that the line count is the cost here.
    ///
    /// It agrees with the numbers already picked by eye elsewhere, which is some evidence it is
    /// right: the blast circle is 312 world units and was hand-set to 48, and this rule asks for 43.
    /// </summary>
    private static int Segments(AutoExpeditionSettings settings, float radius)
    {
        var at = Sides;
        var ordinary = MathF.Max(1f, Unknowns.Size);

        if (radius <= ordinary)
            return at;

        var scaled = (int)MathF.Round(at * MathF.Sqrt(radius / ordinary));

        return Math.Clamp(scaled, at, 96);
    }

    /// <summary>
    /// How many sides an ordinary-sized ring is drawn with.
    ///
    /// Was a slider, and did not deserve one: a mark twenty pixels across looks round at ten sides
    /// and no rounder at twenty four, so the only thing the knob changed was frame cost, in a
    /// direction nobody wants. Twelve is round at that size and still cheap. Larger rings get
    /// proportionally more sides from the caller, so roundness stays constant rather than the count.
    /// </summary>
    private const int Sides = 12;

    /// <summary>
    /// How far under a planned spot its label hangs, in pixels.
    ///
    /// Enough to clear the marker art and not enough to read as a separate thing. Set by eye against
    /// the game's own explosive: two was still touching it, four sits clear.
    ///
    /// A constant rather than a setting: unlike the placement cursor's figure, which sits under a
    /// pointer whose art and scale this cannot read, a spot's label is anchored to a point this
    /// projects itself.
    /// </summary>
    private const float Hangs = 4f;

    /// <summary>
    /// What to write in the middle of a blast: its place in the chain, and what it is there for.
    ///
    /// **The number alone says the order and nothing about the trade.** Fifteen circles all look
    /// equally deserved, and the question standing over a chain is which links are carrying it -
    /// whether the long reach out to link eleven is buying anything, or whether two spots are
    /// splitting content one could take. The score card answers that in a file; this answers it on
    /// the ground, where the decision is actually made.
    ///
    /// Two figures, because they are two different claims. The first is what this blast TAKES - the
    /// content credited to it, which is a fact about this circle. The bracketed one is what it
    /// PASSES ON: a remnant's carried rune, or a relic's, applied to everything the rest of the
    /// chain unearths. That second number belongs to the link in the sense that moving the link
    /// changes it, and belongs to the chain in the sense that it is paid out elsewhere - so it is
    /// shown beside the first rather than added to it.
    ///
    /// Both come from the plan's own scoring rather than from the geometry, so they agree with the
    /// score card instead of being a second opinion about the same blast.
    ///
    /// **The bracket is what dropping this blast would cost, not its share of the propagation.**
    /// A rune booked here pays out over every link that follows, so no single blast owns that
    /// number - and the question being asked of a figure drawn inside a blast circle is whether to
    /// put a bomb there. The brackets therefore add up to more than the chain's propagation, which
    /// is what superadditive means and is not an error. See Planning.Breakdown.
    /// </summary>
    /// <param name="before">
    /// How many explosives were already on the ground when this plan was made.
    ///
    /// **A re-solved plan holds only what is LEFT, and its first link is not blast one.** The chain
    /// is numbered from the plan's own list, so after three explosives a fresh solve labelled the
    /// next one "#1" while the game had three sitting beside it and the score card was calling the
    /// same blast #4. Two things counting the same chain and disagreeing by three.
    /// </param>
    private static string Numbered(bool worthy,
        List<(double Content, double Carried)> worth, int index, int before)
    {
        var number = (index + 1 + before).ToString();

        if (!worthy || worth == null || index >= worth.Count)
            return number;

        var (content, carried) = worth[index];

        if (content <= 0d && carried <= 0d)
            return number;

        return carried > 0d
            ? $"#{number} +{content:N0} ({carried:N0})"
            : $"#{number} +{content:N0}";
    }

    /// <summary>
    /// A ring of dashes, projected the same way a world circle is.
    ///
    /// Hand-drawn because Graphics has no dashed primitive, and the vertices are offset in world
    /// space before projecting so the dashes sit on the same skewed ellipse an undashed ring would.
    /// No terrain sampling, for the reason given on Shape.
    ///
    /// Chests are dashed because the rarity colours are shared with the monsters, so the dashes are
    /// what say "chest" before the colour says which one. Drawing every other segment also makes a
    /// chest cost half what a monster does.
    /// </summary>
    private static void Dashed(Graphics graphics, GameController gc, Vector3 world, float radius, Color colour,
        float thickness, int segments)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        // An even count, so dashes and gaps alternate all the way round rather than meeting as two
        // dashes at the seam.
        var steps = Math.Max(4, segments % 2 == 0 ? segments : segments + 1);

        var previous = Vector2.Zero;

        for (var i = 0; i <= steps; i++)
        {
            var angle = i / (float)steps * MathF.Tau;
            var at = world with
            {
                X = world.X + MathF.Cos(angle) * radius,
                Y = world.Y + MathF.Sin(angle) * radius,
            };

            var screen = Safe.Read((camera, at), static x => x.camera.WorldToScreen(x.at), Vector2.Zero);

            if (i > 0 && i % 2 == 1 && previous != Vector2.Zero && screen != Vector2.Zero)
                graphics.DrawLine(previous, screen, thickness, colour);

            previous = screen;
        }
    }

    private static void Blast(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<RectangleF> covered)
    {
        var radius = Detonator.BlastRadius(gc, settings.Debug.CircleCorrection.Value);
        var at = Detonator.PlacementIndicatorGridPosition(gc);

        if (radius == null || at == Vector2.Zero)
            return;

        var centre = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(at), Vector3.Zero);

        if (centre != Vector3.Zero && !Under(gc, covered, centre))
            graphics.DrawCircleInWorld(centre, radius.Value * Detonator.GridToWorld,
                settings.Debug.BlastCircleColour, 2f, 48, !settings.Display.ThePlan.FlatBlastCircles);
    }

    // -------------------------------------------------------------------- the plan

    /// <summary>
    /// The chain: where each explosive goes, in order, and what each one catches.
    ///
    /// Drawn from the origin outwards so the order is visible - the line is the sequence the
    /// explosives are placed in, which is the part that matters now that propagation makes an
    /// early remnant worth more than a late one. The next explosive is emphasised because that is
    /// the only one there is anything to do about.
    ///
    /// The circles follow the terrain. A blast is thirty grid units across and genuinely spans
    /// slopes, so a flat ring would sit visibly off the ground at the edges and there is no reading
    /// a plan against a circle that is in the wrong place. That costs terrain samples, which is
    /// affordable here in a way it was not for the eighty odd marker rings: there are at most
    /// fifteen of these and they are looked at while standing still.
    /// </summary>
    private static void Chain(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Plan plan, List<Target> targets, Blast blast, Cleared cleared, List<RectangleF> covered,
        Placement placement, bool[] lit,
        List<(double Content, double Carried)> worth = null, int before = 0)
    {
        if (plan.Points.Count == 0)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        var radius = blast.Radius(gc, settings);

        // Which spots are already done, and therefore which one is next. Read off the game's own
        // list rather than counted, so undoing an explosive puts its spot back in the queue and the
        // emphasis moves back to it without anything having to be told.
        var placed = Detonator.PlacedExplosiveGridPositions(gc);
        var queue = -1;

        for (var i = 0; i < plan.Points.Count; i++)
        {
            if (!Already(placed, plan.Points[i]))
            {
                queue = i;

                break;
            }
        }

        // The line into the chain's first spot, from wherever that first throw is made.
        //
        // Origin, not Site, and the difference is the whole of it: Origin is "where the next
        // explosive is thrown from" - the detonator on a fresh chain, the last explosive placed
        // once one has been. That is exactly the point the planner solved from, so the line says
        // what the plan actually claims.
        //
        // Drawn only when the first spot is still to be placed, because a plan comes in two shapes
        // and this handles both. Solved from scratch, it holds every spot including the ones now
        // under an explosive, and queue has moved past them - their base line is behind you and
        // drawing it from Origin would run from the far end of the chain back to its beginning,
        // which is the closed loop this used to show. Re-solved part way, it holds only the spots
        // that are left, queue is 0, and Origin is the last explosive - which is the case that
        // made a resumed chain look like it had been planned from the detonator all over again.
        var from = queue == 0 ? Where(gc, Detonator.LastExplosiveGridPosition(gc)) : Vector3.Zero;
        var fromGrid = queue == 0 ? Detonator.LastExplosiveGridPosition(gc) : Vector2.Zero;
        var behind = false;

        // How many pixels a grid unit is worth on screen right now, for the sanity check below.
        var scale = Scale(gc, camera, plan.Points[0]);

        for (var i = 0; i < plan.Points.Count; i++)
        {
            var world = Where(gc, plan.Points[i]);

            if (world == Vector3.Zero)
                continue;

            var done = Already(placed, plan.Points[i]);

            // Fought out: this link is finished with entirely - no circle, no number, no line into
            // it and none out of it. Everything below is about a blast that still has something to
            // do, so a cleared one leaves the loop having only moved the chain's origin along.
            var gone = cleared.Is(i);
            var next = i == queue;

            // **Green means "press the key and this goes down", not "this one is next".**
            //
            // The next link is next whether or not the game will accept it - a reward window open,
            // the tool not in hand, the spot behind a panel - so the old colouring invited a press
            // at moments when nothing could happen. Now the ring only turns while it would actually
            // work, and otherwise reads as one more planned spot. See Placement.Ready.
            // Worked out for the whole chain in order - see Reachable. Only the next one when
            // the setting is off.
            var ready = lit != null && i < lit.Length && lit[i] &&
                        (next || settings.Display.ThePlan.ColourInRangeAsNext);
            var colour = ready ? settings.Display.ThePlan.StepColour : settings.Display.ThePlan.LaterColour;

            // Three weights, in order of what they are asking of you: press this, you could press
            // this, this is planned. Colour says which of the first two it is; thickness keeps the
            // next one findable among however many are reachable.
            var thickness = next
                ? MathF.Max(0.5f, settings.Display.ThePlan.NextThickness.Value)
                : ready
                    ? MathF.Max(0.5f, settings.Display.ThePlan.ReadyThickness.Value)
                    : MathF.Max(0.5f, settings.Display.ThePlan.PlanThickness.Value);

            if (!gone && !Under(gc, covered, world))
            {
                // No circle on a spot that already has an explosive on it: the game draws that one
                // itself, and two rings on the same ground only make it harder to see which of them
                // is the plan.
                // A later link draws no circle when they are switched off. The number and the
                // line into it stay: this switch is about the circle the Later explosives colour
                // paints, not about the plan. See PlanDisplaySettings.ShowLater.
                if (radius != null && !done &&
                    (ready || next || settings.Display.ThePlan.ShowLater))
                {
                    graphics.DrawCircleInWorld(world, radius.Value * Detonator.GridToWorld, colour,
                        thickness, 32, !settings.Display.ThePlan.FlatBlastCircles);
                }

                // Ring what that circle is for. Only the next one, and only outside debug mode,
                // where every marker is ringed anyway and doing it twice would just thicken them.
                if (next && settings.Debug.ShowMarkers && !settings.Debug.ShowOverlay)
                {
                    Rings(graphics, gc, settings, targets, plan.CaughtBy(i), camera, covered);
                }

                // **Whether the figure is drawn here at all**, which is three questions and not one:
                // whether it is wanted, whether this spot has had its explosive, and whether the
                // chain has been set off. See Display.PlacementCircle.BlastValue.ShowSpotWorth and the two beside it.
                var worthy = settings.Display.PlacementCircle.BlastValue.ShowSpotWorth &&
                             !(done && settings.Display.PlacementCircle.BlastValue.HideWorthPlaced) &&
                             !(settings.Display.PlacementCircle.BlastValue.HideWorthStarted && Detonator.ExplosivesDetonated(gc) >= 1);

                // A number on its own is drawn only while the spot is still to be placed - the game
                // draws its own explosive on a placed one and a bare "#3" over that says nothing the
                // eye has not been told. The VALUE is different: it is the account of what the chain
                // is worth link by link, which is exactly what somebody deciding whether to detonate
                // is reading. So a placed spot gets a label when there is a figure on it and not
                // otherwise.
                if (!done || worthy)
                {
                    var at = Safe.Read((camera, world),
                        static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

                    if (at != Vector2.Zero)
                    {
                        var text = Numbered(worthy, worth, i, before);

                        // Centred on the spot and hung just under it, rather than starting at it -
                        // which put the label up and to the right of the explosive it belongs to
                        // and, on a chain that doubles back, over the link before it. Two pixels is
                        // clear of the marker art without reading as a separate thing.
                        var size = Safe.Read(() => graphics.MeasureText(text), Vector2.Zero);

                        graphics.DrawTextWithBackground(text,
                            new Vector2(at.X - size.X / 2f, at.Y + Hangs), colour, Color.Black);
                    }
                }
            }

            // The link, projected at both ends rather than drawn in world space: a straight line
            // between two points on the ground is straight on screen as well, so there is nothing
            // to be gained by sampling along it.
            //
            // Drawn in full while the chain is being laid - a spot with its explosive on it is
            // still part of the route you are building, and a line that retracted as you placed
            // would hide the shape of the thing before it was finished. It is AFTER detonation
            // that a link stops being worth drawing, and then the test is whether its blast has
            // been fought out rather than whether its explosive went down. See Cleared.
            if (from != Vector3.Zero && !gone && !behind)
            {
                var a = Safe.Read((camera, from), static x => x.camera.WorldToScreen(x.from), Vector2.Zero);
                var b = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

                if (a != Vector2.Zero && b != Vector2.Zero && !Panels.Covers(covered, a) &&
                    !Panels.Covers(covered, b) &&
                    Plausible(scale, Vector2.Distance(fromGrid, plan.Points[i]),
                        Vector2.Distance(a, b)))
                    graphics.DrawLine(a, b, thickness, colour);
            }

            from = world;
            fromGrid = plan.Points[i];
            behind = gone;
        }
    }

    /// <summary>
    /// How many pixels one grid unit covers on screen, measured rather than assumed.
    ///
    /// Two points ten grid apart on the ground, projected, and the distance between them divided by
    /// ten. The camera is near enough isometric that this is the same anywhere on screen, which is
    /// what makes it usable as a yardstick for a link drawn at the far edge of the site.
    ///
    /// Zero when it cannot be measured, which turns the check that uses it off rather than making
    /// it reject everything.
    /// </summary>
    private static float Scale(GameController gc, Camera camera, Vector2 near)
    {
        if (camera == null || near == Vector2.Zero)
            return 0f;

        var one = Where(gc, near);
        var two = Where(gc, near + new Vector2(10f, 0f));

        if (one == Vector3.Zero || two == Vector3.Zero)
            return 0f;

        var here = Safe.Read((camera, one), static x => x.camera.WorldToScreen(x.one), Vector2.Zero);
        var there = Safe.Read((camera, two), static x => x.camera.WorldToScreen(x.two), Vector2.Zero);

        if (here == Vector2.Zero || there == Vector2.Zero)
            return 0f;

        return Vector2.Distance(here, there) / 10f;
    }

    /// <summary>
    /// Whether a link's length on screen is anywhere near what its length on the ground implies.
    ///
    /// **A point behind the camera does not fail to project - it projects to the wrong place.** The
    /// transform mirrors it in front of the viewer, so the answer is a perfectly ordinary looking
    /// screen coordinate and every test for "did this work" passes. What it produces is a line from
    /// one end of the chain to somewhere across the screen that no explosive is standing on, which
    /// is the flicker seen while turning the camera on a Grand site.
    ///
    /// The ground knows better. Two spots ninety grid apart cannot be a thousand pixels apart when a
    /// grid unit is four pixels, whatever the projection says, so the world distance and the
    /// measured scale between them settle it. Generous - three times over, plus a floor for short
    /// links where a few pixels of perspective is a large fraction - because the cost of rejecting a
    /// real link is a line that blinks, and the cost of accepting a false one is the thing being
    /// fixed.
    /// </summary>
    private static bool Plausible(float scale, float grid, float pixels) =>
        scale <= 0f || pixels <= grid * scale * 3f + 80f;

    /// <summary>
    /// One reward, with its price.
    ///
    /// Green when it is the one to take and that is not simply the most expensive, purple
    /// otherwise - the same two colours the combinations window borders with, so the ground and the
    /// window say the same thing in the same language. When the take IS the most expensive there is
    /// one line and it is purple, because there is nothing to distinguish it from.
    /// </summary>
    /// <returns>How far down the next line goes.</returns>
    private static float Line(Graphics graphics, AutoExpeditionSettings settings, Valuation valuation,
        Reward reward, Vector2 at, bool take) =>
        Line(graphics, settings, valuation, reward, at,
            take ? settings.Display.ThePlan.StepColour : settings.Display.Remnants.Rewards.RewardColour);

    /// <summary>The same line in a colour of its own. See ChosenColour.</summary>
    private static float Line(Graphics graphics, AutoExpeditionSettings settings, Valuation valuation,
        Reward reward, Vector2 at, Color colour) =>
        graphics.DrawTextWithBackground(
            reward.Value > 0d
                ? $"{reward.Name}  {Prices.Text(reward.Value, settings, valuation)}"
                : reward.Name,
            at, colour, RewardBackground).Y;

    /// <summary>
    /// Which of a remnant's rewards the plugin would take, as an index into its reward list.
    ///
    /// Zero when that is simply the richest, which is the usual answer and the one that needs no
    /// saying. Above zero when propagation changes it: a combination paying less but putting a
    /// carried rune into a slot that passes forward can be worth more over the rest of the chain,
    /// and this is where the ground display finds that out.
    ///
    /// It is the same call the combinations window makes, over the same list, so the two cannot
    /// disagree - and they did disagree, because the window used to rank the window's own recipes
    /// while this ranked the collapsed reward list. Two places computing "the best option" two ways
    /// is how the amulet came to be named on the ground while the window bordered the belt.
    ///
    /// Zero also whenever the rate is unset or nothing carries, since then there is no second term
    /// and the price ordering already is the answer.
    /// </summary>
    /// <summary>
    /// The same question, asked once a second per remnant rather than once a frame.
    ///
    /// **What it costs is out of all proportion to what it decides.** It picks which line of the
    /// reward list is drawn green, and answering it walks every marker on the site and then the
    /// whole plan, per remnant - 0.512ms a frame of a 1.218ms stage. Nothing about that answer can
    /// move within a frame: the plan is replaced whole by a solve, an edit to the table moves its
    /// revision, and the price feed refreshes every five seconds.
    ///
    /// So all three are the key, and the clock is the backstop for the one of them that has no
    /// revision of its own. A second is far inside the feed's own interval and far outside a frame.
    /// </summary>
    private static int Taking(Target target, AutoExpeditionSettings settings, Valuation valuation,
        GameController gc, Scan scan, Plan plan)
    {
        var now = Environment.TickCount64;

        if (target.TakeUnder == Wrt.Revision && ReferenceEquals(target.TakePlan, plan) &&
            now - target.TakeAt < 1_000L)
            return target.TakeAnswer;

        target.TakeUnder = Wrt.Revision;
        target.TakePlan = plan;
        target.TakeAt = now;

        return target.TakeAnswer = Take(target, settings, valuation, gc, scan, plan);
    }

    private static int Take(Target target, AutoExpeditionSettings settings, Valuation valuation,
        GameController gc, Scan scan, Plan plan)
    {
        // A rolled remnant has no choice left, so the green one is whatever it landed on.
        //
        // **Naming the best option on a remnant that cannot reach it is worse than saying nothing.**
        // Rerolling with liquid virisium fixes the combination for good - the menu still opens and
        // still clicks, and none of it changes anything - so a green "take Lesser Rebirth Rune" over
        // one of those is an instruction that cannot be followed, on a remnant whose actual reward
        // is sitting in the same list ungreened.
        //
        // The planner already knows: Weighing.Choices pins a rolled remnant to its combination, so
        // the chain is built around what it really pays. This is the display catching up with it.
        if (target.Rerolled)
        {
            var locked = Safe.Read(() => valuation?.ChosenName(target.Entity), null);

            if (!string.IsNullOrWhiteSpace(locked))
            {
                for (var i = 0; i < target.Rewards.Count; i++)
                {
                    if (string.Equals(target.Rewards[i].Name, locked, StringComparison.Ordinal))
                        return i;
                }
            }

            // Rolled, but nothing readable says onto what. Better to green nothing than to green a
            // reward the remnant may not have.
            return -1;
        }

        // The objective's own choice where it has one, so the green text names what the plan was
        // scored with rather than a second answer to the same question. See Options.Solved.
        var solved = Options.Solved(target);

        if (solved >= 0)
            return solved;

        return Options.Take(target.Rewards,
            Options.Carried(settings, valuation, target, gc, scan, plan),
            Options.Locally(settings, valuation, target, gc, scan, plan),
            Options.Money(settings));
    }

    /// <summary>
    /// A red triangle round every marker a blast was chosen for and failed to light.
    ///
    /// **The plugin and the game disagree about those markers and only one of them can see the
    /// ground.** The coverage model is a disc; the client's is whatever the terrain actually does,
    /// and on a lip or a slope a marker inside the circle does not light. There is no readout that
    /// can explain that in words - "two of ten markers" says nothing about WHICH two - so they are
    /// pointed at instead, and the decision goes to somebody looking at the site.
    ///
    /// A triangle rather than another circle. Every other mark here is round - the blast radius, the
    /// catch extent, the must-take rings - and a shape nothing else uses cannot be mistaken for any
    /// of them at a glance.
    ///
    /// Cleared when the next explosive lands, which is the moment the question stops mattering.
    /// </summary>
    private static void Unlit(Graphics graphics, GameController gc, Placement placement,
        List<Target> targets, List<RectangleF> covered)
    {
        // **Nothing about a missed marker is worth drawing once the site has gone off.** Both halves
        // of this are advice for a chain still being laid: a red triangle says "you have not covered
        // that", and the cross with its reason says "the click did not take". After the detonation
        // neither can be acted on - the markers it names are spent or dying, and the run is over -
        // so all it does is litter the fight with red over things nobody can now do anything about.
        //
        // Detonated is the plugin's own test for a site having been set off, cached to a quarter
        // second because it walks the entity list. See Detonator.ExplosivesDetonated.
        if (Detonator.ExplosivesDetonated(gc) >= 1)
            return;

        // **Refused was left out of this guard and it is the commonest of the four.** The method
        // draws four things and the early-out listed three, so on a site whose only marks were
        // blocked squares it returned before drawing any of them - which reads exactly like the
        // recording not working.
        if (placement.Unlit.Count == 0 && placement.Spoiled.Count == 0)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        foreach (var at in placement.Unlit)
        {
            var world = Where(gc, at);

            if (world == Vector3.Zero)
                continue;

            var on = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
                Vector2.Zero);

            if (on == Vector2.Zero || Panels.Covers(covered, on))
                continue;

            Triangle(graphics, camera, world, TriangleSize(Astray.Nearest(targets, at)), Color.Red);

            var why = placement.Reason.Length > 0 ? placement.Reason : "Unlit";
            var size = graphics.MeasureText(why);

            graphics.DrawTextWithBackground(why,
                new Vector2(on.X - size.X / 2f, on.Y + UnlitSize * 0.5f + 2f), Color.Red,
                Color.Black);
        }

        // And the spots themselves, crossed out. Where it was missed FROM, which is the other half
        // of the same story - see Placement.Spoiled.
        //
        // The same cross, on everywhere the GAME has said the chain cannot reach from where it now
        // stands. Free while the circle is up - see Reached - so waving the cursor about paints the
        // edge of what is possible instead of finding it one failed run at a time.
        //
        // **Kept after the circle goes away, because it is knowledge rather than a live readout.**
        // It was drawn only while placing, which meant the survey vanished the moment you stopped
        // taking it - and what a sweep is FOR is to look at afterwards and decide where to stand.
        // Whether it shows outside placement mode is the Hide setting's business, not this one's.
        var origin = Detonator.LastExplosiveGridPosition(gc);

        // **Out of range is only news inside the range.** Everything past the limit is out of range
        // by definition, and crossing it out says nothing a player does not already know while
        // filling the screen with marks - the useful ones are the spots that are close enough to
        // look placeable and are not, which is what an obstruction bending the route produces.
        //
        // The game's own arithmetic, like everywhere else - see Detonator.PlacementRange.
        var limit = Detonator.PlacementRange(gc);

        foreach (var at in placement.Spoiled)
        {
            if (limit > 0f && origin != Vector2.Zero && Vector2.Distance(origin, at) > limit)
                continue;

            var world = Where(gc, at);

            if (world == Vector3.Zero)
                continue;

            var on = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
                Vector2.Zero);

            if (on == Vector2.Zero || Panels.Covers(covered, on))
                continue;

            // **Drawn on the ground rather than on the screen.** A cross made of two screen-space
            // diagonals is the same shape wherever the camera is pointing, which makes it float
            // above the site instead of lying on it - and the site is drawn at an angle, so every
            // other mark here is skewed and this one was not. Four corners taken a grid and a bit
            // out along the world axes and projected individually gives the ground's own X.
            // Along the grid's own axes, NOT its diagonals.
            //
            // **The map is drawn at forty five degrees, so the two swap over.** A cross built from
            // grid diagonals - which is what an X looks like on paper - projects to a vertical and a
            // horizontal line, and the mark turned into a plus sign the moment it was put on the
            // ground. The axes project to the screen diagonals, which is the X that was wanted.
            var corners = new[]
            {
                Where(gc, at + new Vector2(-SpoiltSize, 0f)),
                Where(gc, at + new Vector2(SpoiltSize, 0f)),
                Where(gc, at + new Vector2(0f, -SpoiltSize)),
                Where(gc, at + new Vector2(0f, SpoiltSize)),
            };

            var screen = new Vector2[corners.Length];
            var readable = true;

            for (var i = 0; i < corners.Length; i++)
            {
                screen[i] = corners[i] == Vector3.Zero
                    ? Vector2.Zero
                    : Safe.Read((camera, corners[i]),
                        static x => x.camera.WorldToScreen(x.Item2), Vector2.Zero);

                readable &= screen[i] != Vector2.Zero;
            }

            if (!readable)
                continue;

            graphics.DrawLine(screen[0], screen[1], 1f, Color.Red);
            graphics.DrawLine(screen[2], screen[3], 1f, Color.Red);
        }
    }

    /// <summary>
    /// What a blast at the indicator would be worth, written under the cursor.
    ///
    /// The indicator rather than the pointer, because the indicator is where the explosive would go
    /// - the two differ by the lattice snap and by the range clamp, and the number has to belong to
    /// the spot the game would use rather than to the pixel the mouse is over.
    ///
    /// Same two figures a planned circle carries, in the same order and the same words: what it
    /// takes, and in brackets what it passes on. See Numbered.
    /// </summary>
    private static void Worth(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Planning planning, List<RectangleF> covered)
    {
        var at = Detonator.PlacementIndicatorGridPosition(gc);

        if (at == Vector2.Zero || Detonator.PlacementIndicatorIsRed(gc))
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var world = Where(gc, at);

        if (camera == null || world == Vector3.Zero)
            return;

        var on = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
            Vector2.Zero);

        if (on == Vector2.Zero || Panels.Covers(covered, on))
            return;

        var (content, carried) = planning.Spot(at);

        if (content <= 0d && carried <= 0d)
            return;

        var text = carried > 0d ? $"+{content:N0} ({carried:N0})" : $"+{content:N0}";
        var size = graphics.MeasureText(text);

        graphics.DrawTextWithBackground(text,
            new Vector2(on.X - size.X / 2f, on.Y + settings.Display.PlacementCircle.BlastValue.CursorWorthDrop.Value),
            settings.Display.ThePlan.StepColour, Color.Black);
    }

    /// <summary>Two lists walked as one, without copying either.</summary>
    private static IEnumerable<Vector2> Both(IEnumerable<Vector2> first, IEnumerable<Vector2> second)
    {
        foreach (var at in first)
            yield return at;

        foreach (var at in second)
            yield return at;
    }

    /// <summary>
    /// The mark for a marker something went wrong with, pointed at rather than described.
    ///
    /// A triangle rather than another circle. Every other mark in this overlay is round - the blast
    /// radius, the catch extent, the must-take rings - so a shape nothing else uses cannot be
    /// mistaken for any of them at a glance. Drawn from two places now: a marker the game refused to
    /// light for an automated run, and one a hand-placed explosive fell short of. Same shape,
    /// because from the player's side it is the same news.
    /// </summary>
    private static void Triangle(Graphics graphics, Camera camera, Vector3 world, float radius,
        Color colour)
    {
        if (camera == null || radius <= 0f)
            return;

        // **Three points on the ground, projected - not three points on the screen.**
        //
        // Drawn in screen space it always pointed straight up and kept the same size however far
        // away it was, which is the one mark in this overlay that did not sit on the site. Every
        // other one - the blast circles, the marker rings, the must-take rings - is a world shape
        // put through the camera, so it leans with the ground and shrinks with distance, and a
        // triangle that ignored all that read as part of the interface rather than part of the map.
        //
        // Flat at the marker's own height rather than following the terrain under each corner,
        // which is what DrawCircleInWorld does for the rings it sits beside.
        var corners = new Vector2[3];

        for (var i = 0; i < 3; i++)
        {
            var angle = MathF.PI / 2f + i * MathF.Tau / 3f;

            var corner = new Vector3(world.X + MathF.Cos(angle) * radius,
                world.Y + MathF.Sin(angle) * radius, world.Z);

            corners[i] = Safe.Read((camera, corner),
                static x => x.camera.WorldToScreen(x.corner), Vector2.Zero);

            if (corners[i] == Vector2.Zero)
                return;
        }

        graphics.DrawLine(corners[0], corners[1], 2f, colour);
        graphics.DrawLine(corners[1], corners[2], 2f, colour);
        graphics.DrawLine(corners[2], corners[0], 2f, colour);
    }

    /// <summary>
    /// How big to draw that triangle around a marker, in world units.
    ///
    /// Twice the marker's own extent, so it sits clearly outside both the marker ring and the
    /// covered ring at 1.25 that may be drawn with it - and a marker's extent is the right thing to
    /// scale by, since a siren egg is caught from eight times further out than an ordinary one and a
    /// fixed size would swamp the first and vanish beside the second.
    /// </summary>
    private static float TriangleSize(Target target) =>
        Extents.Of(target) * Detonator.GridToWorld * 2f;

    /// <summary>
    /// What a hand-placed explosive cost, written where it stands. See <see cref="Astray"/>.
    ///
    /// Yellow "Inexact" for an explosive off its planned cell with nothing lost by it, red "Missed"
    /// where the blast no longer reaches something the link was chosen for - and in that case the
    /// markers themselves are pointed at, because which ones were given up is the whole of the
    /// decision and a count does not carry it.
    ///
    /// Both on black, like every other word drawn over the site, so they read over pale ground and
    /// over the blast circles alike.
    /// </summary>
    private static void Astrayed(Graphics graphics, GameController gc,
        AutoExpeditionSettings settings, Planning planning, List<Target> targets, Blast blast,
        List<RectangleF> covered)
    {
        // Advice for a chain still being laid, so it goes when the site does. See Unlit for the
        // reasoning and Detonator.ExplosivesDetonated for the test.
        if (Detonator.ExplosivesDetonated(gc) >= 1)
            return;

        var radius = blast.Radius(gc, settings);

        if (radius == null)
            return;

        var found = Astray.Of(gc, settings, planning, targets, radius.Value);

        if (found == null)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        foreach (var verdict in found)
        {
            foreach (var lost in verdict.Lost)
            {
                var marker = Astray.Nearest(targets, lost);

                // The marker's own position rather than the grid cell the plan recorded, so the
                // triangle sits on the thing rather than beside it where the two disagree.
                var where = marker != null ? marker.Where(gc) : Where(gc, lost);

                if (where == Vector3.Zero)
                    continue;

                var mark = Safe.Read((camera, where), static x => x.camera.WorldToScreen(x.where),
                    Vector2.Zero);

                if (mark == Vector2.Zero || Panels.Covers(covered, mark))
                    continue;

                Triangle(graphics, camera, where, TriangleSize(marker), Color.Red);

                // **The same two readouts the placement circle offers, on the markers that were
                // given up.** Somebody who has those switched on has said they want to know what a
                // marker IS rather than only where it is - and the moment that question is most
                // worth answering is when they have just missed one. Read straight off the settings
                // rather than through the pair of conditions Draw builds for Lit: those are about
                // not ringing the same marker twice while the debug pass is ringing everything, and
                // nothing here is being drawn twice.
                if (marker == null)
                    continue;

                if (settings.Display.PlacementCircle.RingsInCircle)
                    Shape(graphics, gc, settings, marker, where, true);

                if (settings.Display.PlacementCircle.ShowArtNames)
                    Named(graphics, marker.Label, mark);
            }

            var world = Where(gc, verdict.At);

            if (world == Vector3.Zero)
                continue;

            var on = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
                Vector2.Zero);

            if (on == Vector2.Zero || Panels.Covers(covered, on))
                continue;

            var text = verdict.Missed ? "Missed" : "Inexact";
            var size = graphics.MeasureText(text);

            graphics.DrawTextWithBackground(text,
                new Vector2(on.X - size.X / 2f, on.Y - size.Y / 2f),
                verdict.Missed ? Color.Red : Color.Yellow, Color.Black);
        }
    }

    /// <summary>How far the triangle's points sit from the marker, in pixels. See Unlit.</summary>
    private const float UnlitSize = 22f;

    /// <summary>
    /// Half the width of the cross on a spoilt spot, in GRID units.
    ///
    /// Small, in the way the band dots are small. It marks a CELL - a point on the ground rather
    /// than a thing standing on it - and a cross the size of a marker ring claims an area the fact
    /// does not cover. In grid rather than pixels so it lies on the ground and shrinks with
    /// distance, like everything else drawn out there.
    /// </summary>
    private const float SpoiltSize = 1.5f;

    /// <summary>
    /// A hairline from the player to the nearest thing left to pick up.
    ///
    /// The same set the F4 pass works through and the loot count counts: a spent remnant still
    /// carrying its shatter button, or an expedition chest nobody has opened. Nearest by where
    /// things are in the world rather than on screen, so it does not swap ends as the camera turns.
    ///
    /// Clipped to the window edge when the nearest thing is behind you, so the line still says
    /// which way to walk rather than disappearing at the moment it is most wanted.
    /// </summary>
    /// <summary>
    /// Rings the richest spots the search found, best first, with what each is worth.
    ///
    /// The same numbers the anchor seeds are built from - see Planner.Spots - so this is the site as
    /// the search understands it rather than a second opinion about it. Ranked one to n, because
    /// the ORDER is the interesting part: a spot ringed second that you would have used first is a
    /// disagreement worth chasing.
    /// </summary>
    /// <summary>
    /// The bands themselves, drawn as the shapes they are.
    ///
    /// A band is every place that catches the same content for the same score, so on the ground it
    /// is a blob - and its shape is the thing worth seeing. A long thin one means the chain has a
    /// real choice about how far to lean; a round one a grid across means it has none, and no amount
    /// of searching will find reach that is not there.
    ///
    /// The corners the search actually uses are ringed, the rest of the band is dotted, so it is
    /// obvious whether the eight directions caught the useful extremes or cut a lobe off.
    /// </summary>
    private static void Shapes(Graphics graphics, GameController gc, List<RectangleF> covered)
    {
        var bands = Planner.Shapes;

        if (bands.Count == 0)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        for (var i = 0; i < bands.Count; i++)
        {
            var colour = Band(i);
            var (name, corners, all) = bands[i];

            foreach (var cell in all)
            {
                var world = Where(gc, cell);

                if (world == Vector3.Zero)
                    continue;

                // One cell, one dot. Under half a grid across, so cells a grid apart read as
                // separate points and the band's shape is the shape of the dots rather than of one
                // blob they have merged into - at 1.6 grid each dot was three across and every
                // neighbour overlapped it.
                graphics.DrawCircleInWorld(world, 0.4f * Detonator.GridToWorld, colour, 1f, 6, true);
            }

            foreach (var corner in corners)
            {
                var world = Where(gc, corner);

                if (world == Vector3.Zero)
                    continue;

                // Ringed rather than filled, and only a little larger than a cell: a band can be
                // three grid across, and a corner marked at five would cover the whole of it.
                graphics.DrawCircleInWorld(world, 1.3f * Detonator.GridToWorld, colour, 2f, 12, true);
            }

            if (corners.Count == 0)
                continue;

            var at = Safe.Read((camera, Where(gc, corners[0])),
                static x => x.camera.WorldToScreen(x.Item2), Vector2.Zero);

            if (at != Vector2.Zero && !Panels.Covers(covered, at))
                graphics.DrawTextWithBackground($"{name} ({all.Count})", at, colour, Color.Black);
        }
    }

    /// <summary>
    /// A colour per band, from a fixed wheel.
    ///
    /// Bands overlap on the ground - two families can want much the same place - so they have to be
    /// told apart by colour rather than by position. Spaced round the wheel and kept light, so they
    /// read against the dig site's browns and against each other where they cross.
    /// </summary>
    private static Color Band(int which)
    {
        var hue = which * 0.618034f % 1f * 6f;
        var part = (int)hue;
        var rise = (byte)(80 + 175 * (hue - part));
        var fall = (byte)(255 - 175 * (hue - part));

        return part switch
        {
            0 => Color.FromArgb(255, 255, rise, 80),
            1 => Color.FromArgb(255, fall, 255, 80),
            2 => Color.FromArgb(255, 80, 255, rise),
            3 => Color.FromArgb(255, 80, fall, 255),
            4 => Color.FromArgb(255, rise, 80, 255),
            _ => Color.FromArgb(255, 255, 80, fall),
        };
    }

    private static void Spots(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Blast blast, List<RectangleF> covered)
    {
        if (Planner.Shaped)
        {
            Shapes(graphics, gc, covered);

            return;
        }

        var spots = Planner.Spots;

        if (spots.Count == 0)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var radius = blast.Radius(gc, settings);

        if (camera == null)
            return;

        // Every spot on the list, not the first few.
        //
        // The count means "per thing" in the per-kind views - three best spots per rare over eight
        // rares is twenty four rings, all of them wanted - and the list is already cut to that when
        // it is built. Truncating again here drew three rings in total and made the per-kind buttons
        // look broken.

        // One colour for the whole set, and never the chain's: these answer a question about the
        // site rather than about the plan, so they must not read as part of the route drawn in green
        // and yellow.
        //
        // Which colour says which question. The overall ranking is light blue, its own thing; the
        // per-remnant ranking borrows the remnant colour, because every ring in it belongs to a
        // remnant and matching the mark already on that remnant is what joins the two up by eye.
        var colour = Planner.Family
            ? Color.FromArgb(255, 140, 255, 170)
            : Planner.Paired
                ? Color.FromArgb(255, 255, 170, 80)
                : Planner.PerKind switch
        {
            TargetKind.Remnant => (Color)settings.Debug.RemnantColour,
            TargetKind.Elite => (Color)settings.Debug.EliteColour,
            _ => Color.FromArgb(255, 120, 200, 255),
        };

        for (var i = 0; i < spots.Count; i++)
        {
            var world = Where(gc, spots[i].At);

            if (world == Vector3.Zero)
                continue;

            var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

            if (at == Vector2.Zero || Panels.Covers(covered, at))
                continue;

            if (radius != null)
            {
                graphics.DrawCircleInWorld(world, radius.Value * Detonator.GridToWorld, colour,
                    1f, 24, true);
            }

            graphics.DrawTextWithBackground($"{spots[i].Note}: {spots[i].Worth:N0}", at, colour,
                Color.Black);
        }
    }

    /// <summary>
    /// A red line to anything the game would set off that the scan does not know about.
    ///
    /// Drawn from the player rather than from the site, because the point of it is to walk over and
    /// look at the thing. Lit ones get the thicker line - a blast would take that one right now,
    /// which is the strongest statement the game makes about it - and the name is written at the
    /// far end so the metadata can be read off the screen without a dump.
    ///
    /// See <see cref="Unexpected"/> for why this is worth having at all.
    /// </summary>
    private static void Unknown(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Scan scan, Vector2 site,
        List<RectangleF> covered)
    {
        var found = Unexpected.Read(gc, scan, site, Unexpected.Around(gc),
            settings.Debug.SweepMs.Value);

        if (found.Count == 0)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (camera == null || player == Vector3.Zero)
            return;

        var from = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player), Vector2.Zero);

        if (from == Vector2.Zero || Panels.Covers(covered, from))
            return;

        var here = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        foreach (var one in found)
        {
            if (one.World == Vector3.Zero)
                continue;

            var to = Safe.Read((camera, one.World),
                static x => x.camera.WorldToScreen(x.World), Vector2.Zero);

            if (to == Vector2.Zero || Panels.Covers(covered, to))
                continue;

            // Behind the camera projects to the wrong place rather than to nowhere, and a red line
            // pointing away from the thing it names is worse than no line. See Toward.
            var toward = Toward(gc, camera, here, one.Grid, from);

            if (toward != Vector2.Zero && Vector2.Dot(toward, Normal(to - from)) <= 0.5f)
                continue;

            graphics.DrawLine(from, to, one.Lit ? 2f : 1f, Color.Red);
            // **The name the settings tab gives it, and no grid reference.**
            //
            // The coordinates were here to be read off the screen and typed into a search of the
            // source, back when naming a new object meant writing a rule for it. They are an
            // address, nothing the player does with this line needs one, and the dump still carries
            // them for anybody who does.
            //
            // The name comes from the target standing at that cell rather than from the metadata,
            // because the metadata tail is not a name - "ExpeditionEncasedMonster" says nothing
            // about which row in the list prices it. The scan holds these now, so there is a target
            // to ask; where there is not, the tail is still better than nothing.
            graphics.DrawText(Naming(scan, one), to, Color.Red);
        }
    }

    /// <summary>
    /// A thin line to the one remnant worth a Liquid Verisium, while there is one.
    ///
    /// **The same shape as the loot line, and for the same reason.** What is wanted is a direction
    /// to walk in, so it is clipped to the window edge when the remnant is behind the camera rather
    /// than disappearing - a line that only exists while you are already looking at the thing is a
    /// line that never tells you anything.
    ///
    /// Drawn in the reroll highlight colour, so the line and the border it leads to are the same
    /// mark twice rather than two marks to learn. One pixel: it is a hint about where to go next,
    /// not a route, and the chain's own polyline is already on the screen. See RollLine.
    /// </summary>
    private static void Rolls(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<RectangleF> covered)
    {
        // **Nothing is drawn while either half of the answer is still being worked out.** The
        // verdict below argues against one solved chain, and a solve in flight means that chain is
        // being replaced - so the line would point at a remnant chosen for a plan that no longer
        // exists. See Rolling.Fresh.
        if (!Rolling.Here.Fresh || Rolling.Here.Best is not { } best || best.Grid == Vector2.Zero)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);
        var at = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (camera == null || player == Vector3.Zero || at == Vector2.Zero)
            return;

        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (window.Width <= 0f || window.Height <= 0f)
            return;

        var from = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player),
            Vector2.Zero);
        var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(best.Grid),
            Vector3.Zero);

        if (from == Vector2.Zero || world == Vector3.Zero)
            return;

        var to = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        if (to == Vector2.Zero)
            return;

        // **At the button, once there is one.** The line exists to answer "which remnant do I walk
        // to", and the answer stops being a direction the moment you arrive: what is wanted then is
        // the thing to click, which is the game's own Liquid Verisium button on that remnant's
        // label. It sits above the remnant rather than on it, so a line to the base points below
        // the only part of this that takes a click.
        //
        // The button exists only while the label is up and only while a Verisium is carried, so the
        // remnant's own position is what the line uses for the whole walk in - which is every frame
        // the line is doing the job it was added for. See RollButton, and Rolling for the advice.
        // **Kept before the button replaces it, because the check below needs the ground.**
        var ground = to;

        var button = ButtonFor(gc, best.Grid);

        if (button.Width > 0f)
            to = new Vector2(button.X + button.Width / 2f, button.Y + button.Height / 2f);

        // Aimed by a step of ground rather than by the far projection, which mirrors behind the
        // camera and would point the opposite way. See Toward.
        //
        // **Against the remnant's own projection, not the button's.** The button sits above the
        // label, which is well above the remnant on screen, so walking up to the remnant turns
        // `to - from` towards straight up while a step of ground still points sideways. The two
        // stop agreeing, the projection is called untrusted, and the else branch below fires a line
        // `window.Width + window.Height` long in the ground direction - which clips to the window
        // edge and points off the screen at a remnant standing a few paces away. The projection was
        // never in doubt; the two vectors were about different points.
        var toward = Toward(gc, camera, at, best.Grid, from);
        var trusted = toward == Vector2.Zero || Vector2.Dot(toward, Normal(ground - from)) > 0.5f;

        var end = trusted && Inside(window, to, Edge)
            ? to
            : Clip(window, from, trusted ? to : from + toward * (window.Width + window.Height));

        if (end != Vector2.Zero && !Panels.Covers(covered, end))
            graphics.DrawLine(from, end, settings.Display.Remnants.Rerolls.RollLineThickness.Value,
                settings.Display.Remnants.Rerolls.RollColour);
    }

    /// <summary>
    /// Rings the remnant carrying a Bait rune, if one has ever appeared.
    ///
    /// **Drawn rather than only logged because the game gives no way to tell it apart.** Bait shares
    /// Power's mod, so the remnant's own window calls it "Power Rune" - a player looking for
    /// something unusual would find nothing unusual to look at. The ring is the only thing that
    /// says which remnant it is.
    ///
    /// A ring and not a line: a rune is seen on a remnant you are standing near, so the question is
    /// never which direction. It goes on drawing until the zone changes. See Curio.
    /// </summary>
    private static void Baited(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<RectangleF> covered)
    {
        if (!settings.Debug.WatchBait || !Curio.Seen || Curio.Where == Vector2.Zero)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(Curio.Where),
            Vector3.Zero);

        if (world == Vector3.Zero)
            return;

        var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        if (at == Vector2.Zero || Panels.Covers(covered, at))
            return;

        graphics.DrawCircleInWorld(world, 3f * Detonator.GridToWorld, Color.Magenta, 2f, 24, true);

        var size = graphics.MeasureText(Curio.Says);
        var text = new Vector2(at.X - size.X / 2f, at.Y - size.Y - 10f);

        graphics.DrawBox(new RectangleF(text.X - 3f, text.Y, size.X + 6f, size.Y), Behind);
        graphics.DrawText(Curio.Says, text, Color.Magenta);
    }

    private static void Loot(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Scan scan, List<RectangleF> covered)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);
        var at = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var site = Detonator.DetonatorGridPosition(gc);

        if (camera == null || player == Vector3.Zero || at == Vector2.Zero || site == Vector2.Zero)
            return;

        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (window.Width <= 0f || window.Height <= 0f)
            return;

        var from = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player), Vector2.Zero);

        if (from == Vector2.Zero || Panels.Covers(covered, from))
            return;

        // One line, to the nearest thing left.
        var nearest = Vector3.Zero;
        var nearestGrid = Vector2.Zero;
        var closest = float.MaxValue;

        // Offered rather than drawn. Every lootable thing competes and one line is drawn at the
        // end, to the nearest of them - briefly it drew a line to each, which is how the count was
        // proved right, and is more than anybody needs once it is.
        // Squared once, so each candidate costs a subtraction and a compare. Zero means no limit.
        var cap = Safe.Read(() => settings.Automation.PostExpedition.LineWithin.Value, 0);
        var within = (float)cap * cap;

        void Offer(Vector3 world, Vector2 grid, float away)
        {
            if (away >= closest)
                return;

            // Loot you walked past is loot you decided about, so the line stops following it across
            // the site. See PostExpeditionSettings.LineWithin.
            if (within > 0f && away * away > within)
                return;

            closest = away;
            nearest = world;
            nearestGrid = grid;
        }

        foreach (var target in scan.At(site))
        {
            if (!target.Shatterable)
                continue;

            var world = target.Where(gc);

            if (world != Vector3.Zero)
                Offer(world, target.Grid, Vector2.Distance(at, target.Grid));
        }

        // The chests come from the entity list, because a scan chest is the MARKER and the blast
        // has already consumed it by the time any of this is on screen.
        var chests = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(
                ExileCore2.Shared.Enums.EntityType.Chest, out var of)
                ? of
                : null, null);

        foreach (var chest in chests ?? new List<Entity>())
        {
            var metadata = Safe.Read(chest, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("LeaguesExpedition", StringComparison.OrdinalIgnoreCase) < 0 ||
                Safe.Read(chest, static e =>
                    e.GetComponent<ExileCore2.PoEMemory.Components.Chest>()?.IsOpened ?? true, true))
                continue;

            // **Filed under Chests is not the same as something you can pick up.** GenericShatterable
            // - the barrels and pots a blast breaks - lives at Metadata/Chests/LeaguesExpedition
            // like every reward chest does, so the loot line pointed at the nearest unbroken barrel
            // and read as "there is something over there to collect". There is not: it cannot be
            // walked to, clicked, or opened, and only an explosive does anything to it.
            //
            // Targetable is the game's own answer to "can this be interacted with", which is the
            // question being asked - rather than a list of metadata names to keep up to date.
            if (!Safe.Read(chest, static e => e.IsTargetable, false))
                continue;

            var grid = Safe.Read(chest, static e => e.GridPos, Vector2.Zero);

            // **A finite bound, because SiteReach is infinite on a Grand site.** That infinity is
            // right for deciding which markers belong to a site - a Grand one has no radius - and
            // wrong as a search radius: it made every targetable chest in the map a candidate, and
            // this loop deliberately does not filter on metadata, so an ordinary chest anywhere would
            // qualify. Never observed, because the loot within the site is almost always nearer.
            if (grid == Vector2.Zero ||
                Vector2.Distance(grid, site) > MathF.Min(Detonator.SiteReach(gc), Belongs))
            {
                continue;
            }

            var world = Safe.Read(chest, static e => e.Pos, Vector3.Zero);

            if (world != Vector3.Zero)
                Offer(world, grid, Vector2.Distance(at, grid));
        }

        if (nearest == Vector3.Zero)
            return;

        var to = Safe.Read((camera, nearest), static x => x.camera.WorldToScreen(x.nearest), Vector2.Zero);

        if (to == Vector2.Zero)
            return;

        // Clipped to the edge when it is behind you, so the line still says which way to walk - and
        // aimed by a step of ground rather than by the far projection, which mirrors behind the
        // camera and would point the opposite way. See Toward.
        var toward = Toward(gc, camera, at, nearestGrid, from);
        var trusted = toward == Vector2.Zero || Vector2.Dot(toward, Normal(to - from)) > 0.5f;

        var end = trusted && Inside(window, to, Edge)
            ? to
            : Clip(window, from, trusted ? to : from + toward * (window.Width + window.Height));

        if (end != Vector2.Zero && !Panels.Covers(covered, end))
            graphics.DrawLine(from, end, 1f, Color.White);
    }

    /// <summary>
    /// How far from the site a chest may be and still be this site's, when the site has no radius.
    /// </summary>
    private const float Belongs = 300f;

    /// <summary>
    /// Whether a spot is actually on screen and not behind a panel.
    ///
    /// The line points at the nearest VISIBLE loot rather than the nearest loot, so this is part of
    /// choosing rather than a check afterwards. Clipping an off-screen one to the window edge was
    /// the first version and it is the wrong idea here: an arrow at the edge of the screen says
    /// "somewhere that way", which is not what a line to a thing you can see says, and it wins the
    /// nearest contest against loot you could have walked to.
    /// </summary>
    private static bool Showing(GameController gc, Camera camera, RectangleF window,
        List<RectangleF> covered, Vector3 world)
    {
        var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        return at != Vector2.Zero && Inside(window, at, Edge) && !Panels.Covers(covered, at);
    }

    /// <summary>
    /// Rings on whatever the game is lighting up under the placement cursor.
    ///
    /// Keyed on the marker's own glow_epk state rather than on distance to a circle, so it is the
    /// client's answer to "would this explosive catch that" and not ours. Nothing is drawn unless
    /// placement mode is actually up, because nothing is lit unless it is.
    /// </summary>
    /// <param name="rings">
    /// Whether to ring them. Off when the debug marker pass is already doing it - see the caller.
    /// </param>
    /// <param name="names">Whether to write each one's art beside it.</param>
    private static void Lit(Graphics graphics, GameController gc, List<Target> targets,
        List<RectangleF> covered, AutoExpeditionSettings settings, bool rings, bool names)
    {
        if (!Detonator.Placing(gc))
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        foreach (var target in targets)
        {
            if (!target.Glowing)
                continue;

            var world = target.Where(gc);

            if (world == Vector3.Zero)
                continue;

            var at = Safe.Read((camera, world),
                static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

            if (at == Vector2.Zero || Panels.Covers(covered, at))
                continue;

            if (rings)
                Shape(graphics, gc, settings, target, world, true);

            if (names)
                Named(graphics, target.Label, at);
        }
    }

    /// <summary>
    /// Rings on the content one link of the plan was chosen for.
    ///
    /// Matched on the grid position the plan recorded rather than on distance to the drawn circle,
    /// because those are two different questions and the plan's answer is the one that put the
    /// explosive there. A ring appearing round something the circle looks like it misses is
    /// therefore information, not a glitch - it means the plan and the drawing disagree, and the
    /// drawing is the one to doubt.
    /// </summary>
    private static void Rings(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<Target> targets, Vector2[] caught, Camera camera, List<RectangleF> covered)
    {
        if (caught.Length == 0 || camera == null)
            return;

        foreach (var target in targets)
        {
            var wanted = false;

            foreach (var at in caught)
            {
                if (Vector2.DistanceSquared(at, target.Grid) < 1f)
                {
                    wanted = true;

                    break;
                }
            }

            if (!wanted)
                continue;

            var world = target.Where(gc);

            if (world == Vector3.Zero)
                continue;

            var screen = Safe.Read((camera, world),
                static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

            if (screen == Vector2.Zero || Panels.Covers(covered, screen))
                continue;

            Shape(graphics, gc, settings, target, world, false);
        }
    }

    /// <summary>
    /// "F4 to solve", at the foot of the detonator.
    ///
    /// The one thing that does NOT belong in the status area, because it is not a status - it is an
    /// invitation, and an invitation belongs beside the thing it is inviting you to do. Standing at
    /// the detonator with nothing planned, the question is "what now", and the answer wants to be
    /// where you are looking rather than in the corner where the running commentary goes.
    ///
    /// Only when there is nothing else being said. A dig site that cannot be solved has a reason
    /// showing above the placement button, and "F4 to solve" next to "No radius" invites you to do
    /// something that will not work.
    /// </summary>
    private static void Invite(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Planning planning, Placement placement, List<RectangleF> covered)
    {

        if (planning.Ready || planning.Searching ||
            planning.Status.Length > 0 || placement.Status.Length > 0)
            return;

        // Nothing to solve with no explosives left.
        //
        // The other conditions are all about the plugin's own state - no plan, nothing being said -
        // and a reload clears every one of them. So walking back past a dig site you finished half
        // an hour ago, or simply reloading beside one, invited you to solve an expedition that is
        // over. The detonator says how many are left, and that survives a reload because it is the
        // game's number rather than ours.
        if (Detonator.ExplosivesInHand(gc) <= 0)
            return;

        var key = Safe.Read(() => settings.ActionHotkey.Value.Key.ToString(), "");

        if (key.Length == 0)
            return;

        var world = Where(gc, Detonator.DetonatorGridPosition(gc));

        if (world == Vector3.Zero)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var at = camera == null
            ? Vector2.Zero
            : Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        if (at == Vector2.Zero || Panels.Covers(covered, at))
            return;

        var text = $"{key} to solve";
        var size = graphics.MeasureText(text);

        graphics.DrawTextWithBackground(text, new Vector2(at.X - size.X / 2f, at.Y),
            settings.Display.ThePlan.StepColour, Color.Black);
    }

    /// <summary>
    /// Everything the plugin has to say, in one place: a word above the game's placement button.
    ///
    /// One place because two places is worse than either. This used to be split - a prompt floating
    /// at the encounter and a countdown over the button - and the pair of them disagreed about
    /// which was the current state while the camera moved one of them around behind the game's own
    /// labels. The button does not move, and it is where the cursor already is whenever any of this
    /// is being asked for.
    ///
    /// One word, too. It is read out of the corner of an eye in the middle of doing something else,
    /// so "Deviated" is the whole message; the sentence explaining it goes to the debug line, where
    /// there is time to read it.
    /// </summary>
    /// <remarks>
    /// **This column speaks during a rehearsal; the ground does not.**
    ///
    /// A presolve runs while you walk in, and the split that turned out to be wanted is not between
    /// finished and unfinished work but between the two places it could be shown. Over the
    /// detonator, a climbing score and a countdown are information - the plugin has a route, roughly
    /// this good, and is still looking. Drawn on the ground they would be a chain laid over a site
    /// you have not chosen to look at yet, which takes the decision to press the key away from you.
    ///
    /// So everything here draws as usual whoever asked for the search, and the world drawing waits
    /// for the key. See Planning.ShownAt, which gates the other half.
    /// </remarks>
    /// <summary>
    /// How far below the anchor the score area sits before the player's own offset is applied.
    ///
    /// A tuned number rather than a derived one: the terms above it place the block against the
    /// button's top and a row height, and this is the nudge that was settled on by looking at it.
    /// It spent its life as the default of ScoreAreaY, which made a setting anybody could reset
    /// hold a constant nobody could see. See DisplaySettings.ScoreAreaY and Migrated step 5.
    /// </summary>
    public const int ScoreAreaDrop = 45;

    /// <summary>
    /// What every line of remnant text is painted on, so it can be read over a lit dig site.
    ///
    /// Was a colour setting. It is behind four different kinds of line - the reward list, the
    /// overruled choice, the unpriced warning and the propagation - so changing it anywhere meant
    /// changing it everywhere, and a background is not a thing anybody sets: it is nearly opaque
    /// black because that is what text needs to sit on.
    /// </summary>
    private static readonly Color RewardBackground = Color.FromArgb(225, 0, 0, 0);

    /// <summary>
    /// Where a remnant's reward text sits relative to the bottom of the game's own label for it,
    /// before the player's own offset is applied.
    ///
    /// Tuned by looking at it rather than derived: the label's rect gives the anchor and these put
    /// the text clear of the runes the game draws inside it. They spent their lives as the defaults
    /// of the two offset sliders, which made a setting anybody could reset hold a position nobody
    /// could see. See RemnantRewardSettings.RewardOffsetX and Migrated step 8.
    /// </summary>
    internal const int RewardLabelX = 45;

    /// <inheritdoc cref="RewardLabelX"/>
    internal const int RewardLabelY = -13;

    private static void Status(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        Scan scan, Planning planning, Placement placement, Scoring scoring, Valuation valuation,
        Scouted scouted)
    {
        RectangleF rect;

        using (Spent.On("Status/ToggleRect"))
            rect = Detonator.ToggleRect(gc);

        if (rect.Width <= 0f || rect.Height <= 0f)
            return;

        var step = (Color)settings.Display.ThePlan.StepColour;
        var later = (Color)settings.Display.ThePlan.LaterColour;
        var reward = (Color)settings.Display.Remnants.Rewards.RewardColour;

        // The column, top to bottom, in the order it reads. Adding a line here is adding a line;
        // nothing below it needs to know. See Readout.
        var readout = new Readout(graphics, rect.Width);

        // The score, at the top, because it is the line being read.
        //
        // Two numbers in the same units from the same objective: what the plan is worth, and what
        // the explosives actually down are worth. A route looks sensible or silly by eye and the eye
        // cannot see the numbers it turns on.
        //
        // In three pieces so the left hand number can carry the comparison in its colour: yellow
        // behind the plan, the reward colour ahead of it, green level with it.
        var score = scoring.Line();

        // **The anchor row is always added, even with nothing to say.**
        //
        // Every other row is positioned relative to it, so a frame where it is missing is a frame
        // where the whole column hangs off whatever happens to be first - and the moment a score
        // arrives everything below it moves down a line. After a cache clear that is exactly what
        // happened: two empty slots, preflood and presolve beneath them, and then the score
        // appearing and shunting the lot.
        //
        // Readout already keeps an empty anchor's place in the order; it just has to be asked for.
        // A space rather than an empty string, because a row with no pieces measures no height and
        // an anchor of no height is the same drift by another route - the offset that positions the
        // block is a multiple of the anchor's own height.
        if (score.Length == 0)
            readout.Anchor((" ", step));

        if (score.Length > 0)
        {
            var (head, yours, tail, against) = scoring.Parts();

            // **The score is the ANCHOR, which is what keeps it still.**
            //
            // Rows added before the anchor stack upwards and rows added after it stack downwards -
            // see Readout - so whichever row is the anchor is the one row that does not move when
            // another appears. That has to be the score: it is the line being read, and it was
            // drifting up the screen by a row every time a stage started talking underneath it,
            // because the anchor had been moved to the bottom of the column and everything else
            // was therefore stacking up away from the button.
            if (head.Length > 0)
            {
                readout.Anchor(
                    (head, step),
                    (yours, against > 0 ? reward : against < 0 ? later : step),
                    (tail, step));
            }
            else
            {
                readout.Anchor((score, step));
            }
        }

        // The word, pinned to the button. Optional, and the line above it is not - this used to give
        // up when there was nothing to say, which took the score with it, so the loot count that
        // exists precisely for the moment the plugin has nothing left to say could only be seen
        // while F4 was mid-sentence.
        // While solving, a bar that empties as the time runs down. The number says how long is left
        // and the bar says how far through it is - two different questions.
        //
        // The room is kept whether the bar is drawn or not, so the lines under it do not jump up the
        // moment a search finishes.
        // **One line each, in the order the work happens: flood the ground, presolve it, solve it.**
        //
        // Each line stays after its stage is over, saying what it left behind, so the column reads
        // as a history of the approach rather than as whichever single thing is happening this
        // instant. A line that exists only while its stage runs cannot distinguish "finished" from
        // "never started", and those want opposite responses from whoever is reading.
        //
        // The bar follows the stage that is running, because a bar means "this is how far through
        // THAT is" and which line it sits under is the whole of its meaning.
        //
        // Green for done, amber for pending, which is what the rest of this column already uses.
        // **Asked here rather than remembered from the presolve, because the presolve does not
        // always run.** Rehearsal.Tick is skipped entirely while the ground is being flooded or a
        // flood is owed, so a figure it publishes is nought for the whole of the period it is meant
        // to describe. The scouting layer is kept up every frame and can answer at any moment.
        //
        // **Four tests have been tried here and three of them measured nothing.**
        //
        // The marker count could not: on entering a map the scan holds nothing, so "none of them
        // are missing" is trivially true and the readout called a site it had never seen whole.
        //
        // The detonator's panel could not either: it lists every encounter in the map with its grid
        // position from wherever you are standing - confirmed in a dump taken across the map, both
        // dig sites named - so "the panel can be read" is true almost always.
        //
        // Remnants-without-monsters was the third and it was the best of the bad ones, on the
        // reasoning that remnants stream map-wide while monsters do not. It still measured the
        // wrong thing: it asks what is loaded THIS INSTANT, and entities load and drop as the
        // player moves, so one untouched site read Presolve, Partial presolve and Presolve again
        // while nothing about it had changed. A readout that flickers on something the player did
        // not do teaches them to ignore it.
        //
        // **What "whole" means: no ground in this site is still unaccounted for.**
        //
        // It used to be "no marker of this site is out of range this instant", off Scan.Reach - and
        // that flips as entities stream in and out while nothing about the site changes, so the same
        // untouched site read Presolve, then Partial presolve, then Presolve again. Worse, it was
        // answering a question it could not: remnants are readable map-wide, so the count it watched
        // had almost nothing to do with whether the site had been met.
        //
        // Ground nobody has been near, and that no marker vouches for, is a claim that stays true
        // until somebody goes and looks - which is exactly what "there might be more here" should
        // mean. Same layer the unscouted wash is drawn from. See Scouted.Left.
        var (unmet, walkable) = scouted?.Left() ?? (0, 0);
        var whole = walkable > 0 && unmet == 0;
        var presolving = planning.Searching && Planning.Rehearsing;
        var solving = planning.Searching && !Planning.Rehearsing;
        var left = planning.Left > 0.05f ? $"{planning.Left:0.0}s" : "finishing";
        var along = Math.Clamp(planning.Left / MathF.Max(0.05f, planning.Window), 0f, 1f);

        var presolve = whole ? "Presolve" : "Partial presolve";

        if (presolving && settings.Display.ScoreArea.DrawPresolve)
        {
            readout.Line(($"{presolve}: {planning.Left:0.0}s", later));
            readout.Bar(along, later);
        }
        else if (Rehearsal.Passes > 0 && settings.Display.ScoreArea.DrawPresolve)
        {
            readout.Line((
                $"{presolve}: {(Rehearsal.Settled ? "done" : "ready")} ({Rehearsal.Passes})",
                whole && Rehearsal.Settled ? step : later));
        }

        if (solving)
        {
            readout.Line(($"Solving: {left}", later));
            readout.Bar(along, later);
        }

        // Last in the column because it is last in the work: the rerolls are advice about the chain
        // that has just been solved, so there is nothing for them to say until there is one.
        //
        // Kept afterwards like the stages above it, and for the same reason - "asked, and nothing
        // is worth rolling" is a useful answer that used to look identical to never having asked.
        // Green only when the advice is about the chain currently on screen: a verdict computed
        // against a chain that has since been re-solved is stale, which is what Fresh means.
        if (Rolling.Here.Skipped && settings.Display.ScoreArea.DrawReroll)
        {
            // **The roll colour, because an unanswered question is not a clean bill of health.**
            // It reads in the same colour as "true" on purpose: both say there is a roll here you
            // have not dealt with, and both lead to the same border on the same button. The settled
            // colour is reserved for a pass that finished and found nothing, which is the only one
            // of the three that means get on with placing. See Rolling.Skipped.
            //
            // Not the warning red it used to be. Red is what the unpriced markers and the refused
            // spots use, and an outstanding roll is not a fault - it is work the plugin is pointing
            // at, in the one colour that only ever means rolling. See DisplaySettings.RollColour.
            readout.Line(("Reroll: skipped", settings.Display.Remnants.Rerolls.RollColour));
        }
        else if (Rolling.Here.Working && settings.Display.ScoreArea.DrawReroll)
        {
            readout.Line(($"Reroll: {Rolling.Here.Through * 100f:0}%", later));
            readout.Bar(Math.Clamp(Rolling.Here.Through, 0f, 1f), later);
        }
        else if (Rolling.Here.Runs > 0 && Rolling.Here.Fresh && settings.Display.ScoreArea.DrawReroll)
        {
            // **A count, and the colour carries the verdict.** Nought is green because it is the
            // good outcome - nothing on this site can be improved by rolling, so get on with
            // placing. Anything above nought is the roll colour: a call to action about something
            // in front of you, in the colour of the border it will lead to.
            //
            // **Only while it is Fresh, which means the advice is about the chain now on screen.**
            // A verdict is computed against one solved chain; solve again and the chain it argued
            // about is gone, but the verdict stays published so the border does not flicker off and
            // on through every replan. Drawing it regardless left "Rerolls: 0" sitting in the
            // settled green through a presolve that had not finished - an answer about a plan that
            // did not exist yet. Absent is the honest state while the question is open.
            var roll = Rolling.Here.Rollable > 0;

            // **How far the enumeration has got, when it has somewhere left to get to.**
            //
            // The figures are accumulated over passes: each one walks the next block of a large
            // shape's arrangements and adds to the last, so an early answer is an estimate and a
            // late one is exact. Nothing on screen distinguished the two, and a number that is
            // about to move by five per cent looks identical to one that cannot move at all.
            //
            // **A percentage rather than a fraction, because a fraction reads as a count of work
            // items.** It was "(5/5)", which looks like five rerolls still to make - two small
            // numbers side by side name a quantity of things far more readily than they name
            // progress through them. A percentage can only be progress, so it says what this is.
            //
            // **Kept up at 100% rather than disappearing when the walk finishes.** It was hidden
            // once complete, on the reasoning that a figure which cannot move says nothing - which
            // made "finished" and "never started" read identically, and that is the one pair a
            // progress indicator has to tell apart.
            //
            // **Only beside a roll, though.** The figure says how settled the advice is, so with
            // no advice there is nothing for it to be about - and it read "Reroll: false (0/5)" once
            // every remnant on the site had been rolled, where the nought is not progress but the
            // pass having nothing left to weigh at all.
            //
            // Still absent on a site where one block covers everything, because there it would
            // always read 100%. See Rolling.RoundsWalked.
            //
            // Integer division, so it floors: a walk one block short of the end reads 99% and not
            // 100%, and only walking every block reads 100%. Rounding would claim completion
            // slightly early on a site with enough blocks, which is the one value here that has to
            // be exact.
            var rounds = roll && Rolling.Here.RoundsNeeded > 1
                ? $" ({Rolling.Here.RoundsWalked * 100 / Rolling.Here.RoundsNeeded}%)"
                : "";

            readout.Line(($"Reroll: {(roll ? "true" : "false")}{rounds}",
                roll ? settings.Display.Remnants.Rerolls.RollColour : step));
        }

        // **No room kept for the bar.**
        //
        // This used to reserve the bar's height so the lines below did not jump as a stage started
        // and stopped. That was written when there was one bar at the foot of the column; the bar
        // now sits under whichever stage is running, which is somewhere in the middle, so the space
        // was being kept in the wrong place - three pixels of nothing between the reroll line and
        // the bomb count, permanently, to steady a bar that never appears there.
        //
        // The cost of not keeping it is that lines below a running stage shift by three pixels when
        // its bar comes and goes. That is smaller than the gap it was buying.

        // **There is no "entities out of range" line, and there was.**
        //
        // It was meant to answer "have I been in range of all of this site's remnants yet", which is
        // the one thing that makes a plan untrustworthy on arrival. In play it answered nothing:
        // remnants stream in map-wide and are readable from anywhere, so the warning was either
        // absent or on for reasons that had nothing to do with what a player would call range.
        //
        // The question is still not answerable - nothing in the game says how many remnants a dig
        // site has until every one of them has arrived - and a line that cannot answer it is worse
        // than no line, because it reads as though it can. What the same signals still drive is the
        // presolve's own wording: "Partial presolve" where "Presolve" would overclaim. See whole.

        // **Last, because it is the answer and the rest is how it was arrived at.**
        //
        // The explosives down out of the explosives planned is the line you read while actually
        // placing them, and it used to sit above four lines about flooding and solving - stages
        // that are finished by the time it matters. Reading order follows the work: what was
        // learnt, what was planned, what was advised, and then what is on the ground.
        if (settings.Display.ScoreArea.DrawBombs)
        {
            var text = Word(gc, settings, planning, placement);

            readout.Line((text, planning.Ready ? step : later));
        }


        // The bakeoff's scores while one is running, and otherwise nothing.
        //
        // **The share of the site is gone from here entirely.** It is a figure for judging a plan
        // rather than for following one - there is nothing to do differently at 40% than at 30% -
        // and this column is read at a glance while deciding where to stand. Hiding it behind the
        // debug switch was not enough, because the debug switch is on while any of this is being
        // worked on, which is exactly when the column is being looked at.
        //
        // It lives in the dump, where it always did and where it is fuller: content taken of content
        // offered, the percentage, and the marker counts beside it. See Dump.
        var tally = Bakeoff.Showing;

        if (tally.Count > 0)
        {
            foreach (var (entry, best) in tally)
            {
                readout.Right(entry, best
                    ? Color.FromArgb(255, 120, 255, 120)
                    : Color.FromArgb(255, 255, 225, 120));
            }
        }

        // **One row higher than the anchor's own height, because the anchor changed rows.**
        //
        // The line pinned here used to be the placement count, with the score stacked above it, so
        // the score sat two rows above the button. Making the score the anchor - which is what
        // stops it drifting when a stage starts talking - moved it down into the count's old place.
        // The height of a row is the anchor's own height: every row here is one line of the same
        // text, so the score's height is the count's height, and the gap between them is Space.
        using (Spent.On("Status/Paint"))
        {
            readout.Draw(
                new Vector2(rect.Left + rect.Width / 2f + settings.Display.ScoreArea.ScoreAreaX.Value, 0f),
                rect.Top - readout.AnchorHeight * 2f - Readout.Space - 6f + ScoreAreaDrop +
                settings.Display.ScoreArea.ScoreAreaY.Value);
        }
    }

    /// <summary>
    /// A ring round every marker the player has said the chain must take.
    ///
    /// Two rings rather than one, a little apart. A single ring is what half the other overlays here
    /// draw - the blast circle, the catch extent, the lit test - and on a site already covered in
    /// circles one more says nothing. A pair reads as a deliberate mark at a glance, and it does not
    /// need a colour nobody else is using to do it.
    ///
    /// Sized in grid units taken up to world units, like everything else drawn on the ground, and
    /// followTerrain on so it sits on the slope rather than through it.
    /// </summary>
    private static void Musts(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<Target> targets, List<RectangleF> covered, Plan plan, Blast blast)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        // The planner's own colour for a must take, because it IS the plan - the mark exists to
        // bend the chain towards it. The refusal gets the other colour, since it is the same
        // statement pointing the other way and the two must never be confused at a glance.
        var green = (Color)settings.Display.ThePlan.StepColour;
        var red = (Color)settings.Display.Remnants.Rewards.OverruledColour;

        foreach (var target in targets)
        {
            var said = Insisted.Here.Of(target.Grid);

            // A banned modifier is an avoid the player did not have to make, so it is drawn
            // as one - same rings, same colour. What it does not share is the wording: a mark
            // nobody placed has to say what put it there. See MustAvoidMods.Why.
            // Only when asked for. A mark made by hand always draws; a standing rule does not,
            // or a site where half the relics carry a banned modifier is half red. See
            // MustAvoidMods.DrawInWorld.
            var banned = MustAvoidMods.DrawInWorld && said == Insisted.Said.Nothing &&
                         MustAvoidMods.Bans(target)
                ? MustAvoidMods.Why(target)
                : "";

            if (banned.Length > 0)
                said = Insisted.Said.Avoid;

            if (said == Insisted.Said.Nothing)
                continue;

            var colour = said == Insisted.Said.Take ? green : red;

            var world = target.Where(gc);

            if (world == Vector3.Zero)
                continue;

            graphics.DrawCircleInWorld(world, 3.2f * Detonator.GridToWorld, colour, 2f, 24, true);
            graphics.DrawCircleInWorld(world, 4.4f * Detonator.GridToWorld, colour, 2f, 24, true);

            var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world),
                Vector2.Zero);

            if (at == Vector2.Zero || Panels.Covers(covered, at))
                continue;

            // **A mark the chain cannot honour looks exactly like one it has.** The weight it adds
            // is excluded from every number a person reads, so the only sign either way was the ring
            // - and the ring is the same ring. On a site with more marks than explosives the search
            // has to drop one, which is a correct answer that reads as the key not working.
            //
            // Named by the blast that takes it, in the numbers the circles already carry, so the
            // claim can be checked by looking at the circle rather than taken on trust.
            var by = Insisted.TakenBy(plan, target.Grid);

            // **A marker an explosive already down has taken is not one the plan failed to reach.**
            //
            // A re-solved plan holds only what is LEFT, so a must take caught by an earlier blast
            // appears in none of its points and TakenBy answers nought - which is the same answer
            // it gives for a mark the chain genuinely could not honour. The two are opposite news
            // and were reading identically, in red.
            //
            // Asked of Planning, which owns the rule that decides what the bombs on the ground have
            // claimed, rather than measured again here. See Planning.Taken.
            if (by == 0 && blast.Radius(gc, settings) is { } reach &&
                Safe.Read(() => Planning.Taken(target, Detonator.PlacedExplosiveGridPositions(gc), reach), false))
                by = -2;

            // The same four words either way round: what was asked, and whether the plan managed
            // it. A must take the chain cannot reach and an avoided marker it cannot help catching
            // are the same kind of news - the ground would not allow what was asked - and both are
            // worth saying, because neither shows up in any number.
            var note = banned.Length > 0
                ? by > 0
                    ? $"MUST AVOID ({banned}) - TAKEN ANYWAY by blast #{by}"
                    : $"must avoid ({banned}) - avoided"
                : said == Insisted.Said.Take
                ? by switch
                {
                    > 0 => $"must take - blast #{by}",
                    -2 => "must take - already taken",
                    0 => "must take - NOT REACHED",
                    _ => "must take",
                }
                : by switch
                {
                    > 0 => $"must avoid - TAKEN ANYWAY by blast #{by}",
                    -2 => "MUST AVOID - TAKEN ALREADY",
                    0 => "must avoid - avoided",
                    _ => "must avoid",
                };

            // Red when the answer is not the one asked for, whichever way it was asked.
            // An explosive already down counts as taken, so it satisfies a must take and breaks a
            // must avoid - the same way a planned blast would. See Planning.Taken.
            var failed = said == Insisted.Said.Take ? by == 0 : by > 0 || by == -2;

            graphics.DrawTextWithBackground(note, at, failed ? red : colour, Color.Black);
        }
    }

    /// <summary>
    /// Which one thing to say, when several are true.
    ///
    /// Ordered by what the player can act on. What it is doing now beats what it worked out
    /// earlier; a chain that has gone off-plan beats a count of how much of the plan is down,
    /// because the count would be reassuring and wrong.
    /// </summary>
    /// <summary>The red the ground uses for a thing that needs attention. See Unpriced.</summary>
    private static readonly Color Warning = Color.FromArgb(255, 235, 90, 90);

    private static string Word(GameController gc, AutoExpeditionSettings settings, Planning planning,
        Placement placement)
    {
        if (placement.Status.Length > 0)
            return placement.Status;

        // **"Solving 0.0s" sat there for the best part of a second and read as a hang.** The
        // countdown is the improvement window, and the search stops checking it only between
        // operators - one pass of the polish over a three hundred marker site is hundreds of
        // milliseconds on its own, and the mixed strategy spends a fixed slice on the band search
        // before the main one starts. So the window can run out while the current piece of work is
        // still in flight, and a frozen number is the worst way to say that.
        // **The countdown is not said here any more.** It belongs in the column with the preflood
        // and the presolve, in the order the work happens, because reading it against those two is
        // the point - see Status. Said in both places it was the same fact twice, and said only
        // here it sat above the stages it comes after.
        if (planning.Searching)
            return "";

        if (Placement.Deviated(gc, planning))
            return "Deviated";

        // **Counted over the whole route rather than over the plan.**
        //
        // The plan is only the route's tail, so a solve made with two explosives already down read
        // "0/3" - the same chain described two ways depending on when it happened to be solved,
        // and a denominator that dropped as the site was worked through. Planning holds the route
        // whole now, so the honest pair is how many of its links are down over how many it has. See
        // Planning.Chain.
        var down = planning.Laid + Placement.PlacedOf(gc, planning.Plan);
        var links = planning.Chain.Count;

        // Said once the chain is laid out and before any of it is down, which is when the question
        // "should I press again" is actually being asked. See Planning.ProvenBest.
        if (planning.ProvenBest && planning.Ready && down == 0)
            return $"Bombs: 0/{links} - best possible";

        if (planning.Ready)
            return $"Bombs: {down}/{links}";

        return planning.Status;
    }




    /// <summary>
    /// Whether one of the game's placed explosives is the one laid for this planned spot.
    ///
    /// **Not "is exactly on it".** See Astray.Owns for why the two are different questions and why
    /// this one is answered at half the minimum separation: a link laid by hand lands a few grid
    /// out, and judging that as a different spot made every hand-placed chain read as a chain that
    /// had walked away from its plan. Whether it is exactly on the cell is Astray's business, and it
    /// says so in yellow.
    /// </summary>
    private static bool Already(Vector2[] placed, Vector2 point)
    {
        foreach (var at in placed)
        {
            if (Vector2.Distance(at, point) < Astray.Owned)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Barrels the chain has already set off, so their circles can stop being drawn.
    ///
    /// **A barrel's radius is a question about where to stand, and once the blast has happened there
    /// is nothing left to decide.** The circle stayed up for the rest of the run, so a site half
    /// cleared was covered in sixty-grid rings around ground that had already gone up - the one
    /// drawing that is pure noise, because it describes a decision nobody can make any more.
    ///
    /// The game's own detonated state is the first answer and the better one, and Target.Spent now
    /// reads it. This is the second: an explosive already standing within reach of a barrel has set
    /// it off whether or not the entity has caught up, or has been streamed out entirely and left
    /// its marker behind on the remembered list.
    ///
    /// **And barrels set each other off**, so this is a closure rather than a test - the far barrel
    /// of a pair five grid apart is as gone as the near one. Repeated until nothing new is added,
    /// which on any real site is two or three passes over a handful of barrels.
    /// </summary>
    internal static HashSet<(int, int)> Blown(GameController gc, AutoExpeditionSettings settings,
        List<Target> targets, float blast)
    {
        var gone = new HashSet<(int, int)>();
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed.Length == 0 || blast <= 0f)
            return gone;

        // **A blast reaches a marker's EDGE, not its centre**, which is how Planner.Catches has
        // always decided it: distance against blast plus the marker's own extent. Testing centre to
        // centre here made this stricter than the rule the chain was planned under, and a barrel two
        // grid outside the bare radius - well inside it once its own size counts - kept its circle
        // after the bomb that takes it was already down.
        //
        // The same Extents.Of the planner uses, so the two cannot drift.

        var barrels = new List<Target>();

        foreach (var target in targets)
        {
            if (target.Kind == TargetKind.Barrel && target.Sets > 0f && !target.Spent)
                barrels.Add(target);
        }

        // Reached directly by an explosive that is already down.
        foreach (var barrel in barrels)
        {
            var edge = blast + Extents.Of(barrel);

            foreach (var at in placed)
            {
                if (Vector2.Distance(at, barrel.Grid) <= edge)
                {
                    gone.Add(Cell(barrel.Grid));

                    break;
                }
            }
        }

        // Then by each other, until it settles.
        for (var pass = 0; pass < barrels.Count && gone.Count > 0; pass++)
        {
            var added = false;

            foreach (var barrel in barrels)
            {
                if (gone.Contains(Cell(barrel.Grid)))
                    continue;

                foreach (var other in barrels)
                {
                    if (!gone.Contains(Cell(other.Grid)) ||
                        Vector2.Distance(other.Grid, barrel.Grid) > other.Sets)
                        continue;

                    gone.Add(Cell(barrel.Grid));
                    added = true;

                    break;
                }
            }

            if (!added)
                break;
        }

        return gone;
    }

    /// <summary>A grid position as a key, rounded the way every other per-site fact here is.</summary>
    internal static (int, int) Cell(Vector2 grid) =>
        ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));

    /// <summary>
    /// Which links the key could reach, in order, stopping at the first it could not.
    ///
    /// **Reachability runs out; it does not come back.** The links are placed in order, so a spot
    /// six blasts away is only reachable in the sense that matters if everything between here and it
    /// is too. Answering each one independently lit up spots on the far side of a link that is
    /// behind a panel or off the screen - true of that spot on its own, and useless as a statement
    /// about what pressing the key repeatedly would do.
    ///
    /// So the first refusal closes the rest. What is green is a run from the next blast forwards,
    /// which is exactly the question being asked: how far can I get without moving.
    ///
    /// Links already placed are skipped rather than tested - they are done, and a finished blast has
    /// no opinion about whether the ones after it can be reached.
    /// </summary>
    private static bool[] Reachable(GameController gc, AutoExpeditionSettings settings, Plan plan,
        Placement placement, Cleared cleared, List<RectangleF> covered)
    {
        var ready = new bool[plan?.Points.Count ?? 0];

        if (ready.Length == 0 || placement == null)
            return ready;

        var placed = Detonator.PlacedExplosiveGridPositions(gc);
        var running = true;
        var first = true;

        // The link each one is placed from, which decides where the cursor has to point for it.
        // Zero for the first considered link, meaning "the newest explosive on the ground", and the
        // spot before it from then on - including spots already placed, since a placed link is
        // still the one the next is laid from. See Placement.Pointed.
        var from = Vector2.Zero;

        for (var i = 0; i < plan.Points.Count; i++)
        {
            if (Already(placed, plan.Points[i]) || cleared.Is(i))
            {
                from = plan.Points[i];

                continue;
            }

            if (!running)
                break;

            // Only the first unplaced link's answer is reported - see the record parameter.
            //
            // The planned spot on the grid, not its world position: Ready works out for itself
            // where the cursor would have to go for it. See Placement.Pointed.
            using (Spent.On("Reachable/Ready"))
            {
                ready[i] = placement.Ready(gc, settings, plan.Points[i], from, plan.CaughtBy(i),
                    covered, out _, first);
            }

            first = false;
            from = plan.Points[i];

            // The first one the key could not reach ends the run.
            running = ready[i];
        }

        return ready;
    }


    private static readonly Color Once = Color.FromArgb(120, 190, 190, 190);

    private static Vector3 Where(GameController gc, Vector2 grid) =>
        grid == Vector2.Zero
            ? Vector3.Zero
            : Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(grid), Vector3.Zero);

    // ------------------------------------------------------------------ rewards

    /// <summary>
    /// A line from the player towards each remnant worth going to.
    ///
    /// Only the ones over the threshold. A line to every remnant is five lines across the screen
    /// that say nothing; the point is to be told about the one that is worth walking to, and how
    /// much it takes to be worth walking to is a number the player sets.
    ///
    /// **The off-screen case is what this is for.** A remnant on screen already has its price drawn
    /// over it and needs no line. One off the edge has nothing - no marker, no text, no ring - so
    /// the line is clipped to the screen edge and the price drawn at the end of it, which turns
    /// "there is something good over there" into a direction and a figure.
    /// </summary>
    private static void Lines(Graphics graphics, GameController gc, List<Target> targets,
        AutoExpeditionSettings settings, Valuation valuation, List<RectangleF> covered)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (player == Vector3.Zero)
            return;

        var from = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player), Vector2.Zero);

        if (from == Vector2.Zero)
            return;

        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (window.Width <= 0f || window.Height <= 0f)
            return;

        var here = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        var floor = settings.Display.Remnants.Rewards.LineAbove.Value * valuation.PerExalt(settings.Display.Prices.PriceIn.Value);
        var thickness = settings.Display.Remnants.Rewards.LineThickness.Value;

        // Squared, so the test per remnant is a subtraction and a compare. Zero means no limit.
        var reach = Safe.Read(() => settings.Display.Remnants.Rewards.LineWithin.Value, 0);
        var within = (float)reach * reach;

        foreach (var target in targets)
        {
            if (target.Kind != TargetKind.Remnant || target.Spent || target.Rewards.Count == 0)
                continue;

            if (target.Rewards[0].Value < floor)
                continue;

            // The whole map's remnants exist from the moment the area loads, so without this the
            // screen fills with lines to ground that has nothing to do with the site in front of you.
            // See DisplaySettings.LineWithin.
            if (within > 0f && here != Vector2.Zero &&
                Vector2.DistanceSquared(here, target.Grid) > within)
            {
                continue;
            }

            var world = target.Where(gc);

            if (world == Vector3.Zero)
                continue;

            var to = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

            if (to == Vector2.Zero)
                continue;

            // Which way the remnant really lies, taken from a step of ground rather than from the
            // far projection. See Toward.
            var toward = Toward(gc, camera, here, target.Grid, from);
            var trusted = toward == Vector2.Zero || Vector2.Dot(toward, Normal(to - from)) > 0.5f;

            // Clipped to the edge rather than dropped, so an off-screen remnant still gets pointed
            // at. On-screen ones come back unchanged and the clip costs nothing.
            //
            // A projection the step disagrees with is off-screen whatever it says - that is what a
            // point behind the camera looks like - and the clip then runs along the step's
            // direction, which is the one that cannot be mirrored.
            var off = !trusted || !Inside(window, to, Edge);

            // **Off screen, the line is clipped along the ground bearing and never along the far
            // projection.** It used to prefer the projection whenever the two roughly agreed, and
            // that is the one that is unstable out there: a point approaching the camera plane
            // projects to a figure that swings about, so the endpoint jittered along the edge while
            // the bearing beside it sat still. On screen the projection is the answer and is used
            // unchanged.
            var end = !off
                ? to
                : Clip(window, from,
                    toward != Vector2.Zero
                        ? from + toward * (window.Width + window.Height)
                        : to);

            if (end == Vector2.Zero || Panels.Covers(covered, end) || Panels.Covers(covered, from))
                continue;

            graphics.DrawLine(from, end, thickness, settings.Display.Remnants.Rewards.LineColour);

            // The price, but only where nothing else can show it. On screen the remnant already has
            // its own text and a second copy on a line end is noise.
            if (!off)
                continue;

            var text = Prices.Text(target.Rewards[0].Value, settings, valuation);
            var size = graphics.MeasureText(text);

            graphics.DrawTextWithBackground(text,
                new Vector2(end.X - size.X / 2f, end.Y - size.Y / 2f),
                settings.Display.Remnants.Rewards.LineColour, Color.Black);
        }
    }

    /// <summary>How far inside the window a point has to be to count as on screen.</summary>
    private const float Edge = 24f;

    /// <summary>A vector of length one, or zero when there is no direction to be had.</summary>
    private static Vector2 Normal(Vector2 of) =>
        of.LengthSquared() > 0.0001f ? Vector2.Normalize(of) : Vector2.Zero;

    /// <summary>
    /// Which way something lies on screen, measured from a few steps of ground rather than from
    /// where it projects to.
    ///
    /// **A point behind the camera projects to the wrong place, not to nowhere.** The transform
    /// mirrors it round in front of the viewer, so a remnant behind you comes back as an ordinary
    /// screen coordinate on the opposite side - and a line clipped towards it points away from the
    /// thing it is naming. Observed: a purple price drawn off the wrong edge entirely.
    ///
    /// A short step from the player towards it cannot be behind the camera when the player is not,
    /// so the direction it projects to is trustworthy whatever the far end does. Ten grid, which is
    /// far enough that the projected step is longer than the rounding on it.
    ///
    /// Zero when the step cannot be read, which leaves the caller trusting the far projection - the
    /// behaviour it had before, for the case where there is nothing better.
    /// </summary>
    private static Vector2 Toward(GameController gc, Camera camera, Vector2 here, Vector2 target,
        Vector2 from)
    {
        if (here == Vector2.Zero || target == here)
            return Vector2.Zero;

        // **The furthest point along the ground that still projects sanely, not a fixed short step.**
        //
        // This took one step of ten grid, which is stable because it is near the camera and is only a
        // rough bearing to something two hundred grid away - under perspective the ground curves in
        // screen space, so a near step and a far target do not share a direction. Used to CLIP a line
        // to the window edge, a bearing that is slightly wrong puts the endpoint in slightly the wrong
        // place, and it moves as the player moves. Reported as the endpoint being jittery at a
        // distance and smooth close up, which is this: near the remnant the two bearings agree.
        //
        // Halving from the target back towards the player takes the best point available. Whatever
        // survives the sanity check is both projectable and as close to the true bearing as this can
        // get, and the search costs at most a handful of projections for a handful of remnants.
        var way = Normal(target - here);

        // **The near step decides the DIRECTION, a further one only refines it.**
        //
        // Ten grid from the player is close enough to the camera to project reliably, so its bearing
        // is trustworthy in sign and rough in precision. A point further along is more precise and
        // not trustworthy: past the camera plane it projects MIRRORED, to a point that is a plausible
        // distance from the player and on entirely the wrong side. Taking the furthest sanely-projected
        // point on its own, which is what this did for one commit, therefore pointed a line off the
        // opposite edge of the screen.
        //
        // So the near bearing is established first and every further candidate has to agree with it
        // before it is preferred. That keeps the precision that fixed the jitter and cannot reverse:
        // a mirrored projection fails the dot test by pointing backwards, which is the one thing it
        // reliably does.
        var close = Where(gc, here + way * 10f);

        if (close == Vector3.Zero)
            return Vector2.Zero;

        var near = Safe.Read((camera, close), static x => x.camera.WorldToScreen(x.close), Vector2.Zero);

        if (near == Vector2.Zero)
            return Vector2.Zero;

        var bearing = Normal(near - from);

        if (bearing == Vector2.Zero)
            return Vector2.Zero;

        // Read here rather than taken as an argument: four passes call this and only two of them have
        // a window in hand, and the figure is only a sanity bound.
        var screen = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default(RectangleF));
        var sane = 4f * MathF.Max(MathF.Max(screen.Width, screen.Height), 1000f);

        for (var reach = Vector2.Distance(target, here); reach >= 20f; reach *= 0.5f)
        {
            var step = Where(gc, here + way * reach);

            if (step == Vector3.Zero)
                continue;

            var at = Safe.Read((camera, step), static x => x.camera.WorldToScreen(x.step), Vector2.Zero);

            if (at == Vector2.Zero || Vector2.Distance(at, from) > sane)
                continue;

            var refined = Normal(at - from);

            // Half a right angle of disagreement is generous for two bearings to the same thing and
            // nowhere near enough to admit a reversal.
            if (refined == Vector2.Zero || Vector2.Dot(refined, bearing) < 0.7f)
                continue;

            return refined;
        }

        return bearing;
    }

    private static bool Inside(RectangleF window, Vector2 at, float margin) =>
        at.X > margin && at.Y > margin &&
        at.X < window.Width - margin && at.Y < window.Height - margin;

    /// <summary>
    /// Where the line from inside the window to a point outside it crosses the edge.
    ///
    /// Walked rather than solved: the four edge intersections of a segment and a rectangle are
    /// simple enough algebra and fiddly enough to get subtly wrong, and this runs for at most a
    /// handful of remnants a frame. Halving the interval twenty times puts the answer inside a
    /// thousandth of a pixel, which is further than anybody is going to look.
    /// </summary>
    private static Vector2 Clip(RectangleF window, Vector2 from, Vector2 to)
    {
        if (!Inside(window, from, Edge))
            return Vector2.Zero;

        var near = 0f;
        var far = 1f;

        for (var i = 0; i < 20; i++)
        {
            var mid = (near + far) / 2f;

            if (Inside(window, Vector2.Lerp(from, to, mid), Edge))
                near = mid;
            else
                far = mid;
        }

        return Vector2.Lerp(from, to, near);
    }

    /// <summary>
    /// The text over a remnant: what it can become, and what it carries forward.
    ///
    /// Both on one anchor and in one pass, because they are read together - the reward says whether
    /// this remnant is worth taking, and the propagating rune says whether it is worth taking
    /// EARLY. A remnant with a two-divine reward and nothing to pass on is the last stop in the
    /// chain; one passing on Opulent is the first, whatever its own reward says.
    /// </summary>
    private static void Remnants(Graphics graphics, GameController gc, List<Target> targets,
        AutoExpeditionSettings settings, Valuation valuation, Scan scan, Plan plan,
        List<RectangleF> covered)
    {
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return;

        Dictionary<uint, Element> labels;

        using (Spent.On("Remnants/Labels"))
            labels = Labels(gc);

        // **One line, which is what it always shipped as.** The count was a setting with a range
        // of ten, and the reason to raise it never arrived: the list is ordered by price, so the
        // second row is the reward you are not taking and the tenth is noise drawn over a dig site.
        // What the extra rows were for - seeing the plan disagree with the money - is the hoisted
        // pick's job, and that is drawn above the list however tight it is.
        const int listed = 1;

        var lines = settings.Display.Remnants.Rewards.ShowRewards ? listed : 0;

        // Asked once for the whole pass rather than per remnant: it is the same answer for all of
        // them and it walks the recipe table to work it out.
        string warning;

        using (Spent.On("Remnants/Unpriced"))
            warning = Safe.Read(() => valuation?.Unpriced(), null);
        var doubted = warning is { Length: > 0 };

        foreach (var target in targets)
        {
            // Spent remnants draw nothing. Everything below says what is still to be decided
            // there, and nothing is.
            if (target.Kind != TargetKind.Remnant || target.Spent)
                continue;

            // Gated on the line's own switch now the weight floor is gone: a remnant whose slots
            // are not being drawn has no reason to have them read. See ShowWaves.
            var passing = settings.Display.Remnants.Propagation.ShowWaves ? target.Passing : null;
            var rewards = lines > 0 && target.Rewards.Count > 0;

            // Fresh as well as enabled: a border saying "roll this" while the chain behind the
            // verdict is being re-solved is advice about a plan that is on its way out. See
            // Rolling.Fresh.
            Advice advice;

            // **Attributed, because Remnants is 642KB a frame and nothing said which part.**
            // Overlay.Draw allocates about a megabyte every frame and Remnants is two thirds of it,
            // which is enough garbage to keep the process in gen0 collections while a search runs.
            // Three candidates and no way to tell them apart: the advice, the reward lines and the
            // propagation line. See the frame table in the dump.
            using (Spent.On("Remnants/Advice"))
                advice = settings.Display.Remnants.Rerolls.HighlightRerolls && Rolling.Here.Fresh
                    ? Reroll.For(target, settings, valuation, gc, scan, plan)
                    : Advice.None;

            // **What the propagation line is drawn from, not a second opinion about it.** This
            // tested target.Passing while the line below comes out of Planner.RuneTallyByRemnant, so a remnant
            // the objective had scored but whose own Passing list was empty got skipped here. With
            // rewards switched on that never showed, because the rewards kept the remnant past this
            // test and the line drew anyway; switching rewards off made propagation and the reroll
            // advice disappear with them, which is what sent somebody looking. See Planner.RuneTallyByRemnant.
            var propagating = passing != null &&
                Planner.RuneTallyByRemnant.ContainsKey(((int)MathF.Round(target.Grid.X),
                    (int)MathF.Round(target.Grid.Y)));

            if (!rewards && advice == Advice.None && !propagating)
                continue;

            Vector2 at;

            using (Spent.On("Remnants/Anchor"))
                at = Anchor(gc, camera, labels, target);

            if (at == Vector2.Zero)
                continue;

            at.X += RewardLabelX + settings.Display.Remnants.Rewards.RewardOffsetX.Value;
            at.Y += RewardLabelY + settings.Display.Remnants.Rewards.RewardOffsetY.Value;

            if (Panels.Covers(covered, at))
                continue;

            // **Said on the remnant, because this is where the number it discredits appears.** With
            // no price list every reward reads as worth nothing, so the list under a remnant is an
            // arbitrary ordering of equal zeros and looks exactly like a real one. A line in the
            // corner is read once; this is read every time a reward is.
            //
            // Drawn through the same call as every other line here, in the colour that already means
            // "this is not what the planner would do" - so it sits in the column with the rewards
            // and carries their background, rather than floating beside them in a style of its own.
            if (doubted)
            {
                at.Y += graphics.DrawTextWithBackground(warning, at,
                    (Color)settings.Display.Remnants.Rewards.OverruledColour, RewardBackground).Y;
            }

            // The verdict first, because it is the thing being decided. It is a border round the
            // game's own Liquid Verisium button on the remnant's label rather than text: the button
            // is where the click happens, and a ringed button needs no reading. It is absent when
            // the player has no Verisium, and then there is nothing to advise.
            //
            // ROLL only. Every other verdict means leave the button alone, and an unringed button
            // already says that - including RISK, which was briefly drawn in amber and should not
            // have been. A border on the Verisium button reads as "click this", and RISK is not
            // that: it says the rune is what is holding the remnant above your floor and that
            // nobody knows what a roll would hand back. Marking the click for a decision the
            // plugin cannot make is worse than saying nothing, because the mark looks like advice.
            // RISK lives in the score card, where there is room to say what it means.
            // **Resolved only when something is going to use it.** RollButton costs 20,304 bytes a
            // call - see its own doc comment - and there are exactly two consumers here: the border,
            // which rings at most one remnant on the site, and the figures beside the button, which
            // ShowsRollFigures decides. Asking for every remnant's button and then finding that
            // neither wanted it is the whole of what this loop was doing twelve times a frame.
            //
            // The stage stays, because the cost is worth watching rather than assuming gone.
            var ringing = advice == Advice.Roll;
            var explaining = ShowsRollFigures(settings, target);

            RectangleF button = default;

            if (ringing || explaining)
            {
                using (Spent.On("Remnants/Button"))
                {
                    if (!target.Rerolled && target.Live &&
                        labels.TryGetValue(Safe.Read(target, static t => t.Entity.Id, 0u), out var label))
                        button = RollButton(label);
                }
            }

            if (button is { Width: > 0f })
            {

                if (ringing)
                {
                    graphics.DrawFrame(button, settings.Display.Remnants.Rerolls.RollColour,
                        settings.Display.Remnants.Rerolls.RollBorder.Value);
                }

                // **The reasoning, above the button, while the advice is young.** A border says
                // "click this" and nothing else, which is the right amount to say once a thing is
                // trusted and not before. The two cases people get wrong are invisible from the
                // remnant itself - a rich one worth rolling because every rune on it is already
                // carried earlier in the chain, and a poor one worth keeping because it holds the
                // chain's only copy of something - so the sentence is where the argument gets made.
                //
                // See DisplaySettings.RollFigures, and Rolling for where the numbers come from.
                if (explaining)
                    Explaining(graphics, settings, target, button);
            }

            // Which one the plugin would actually take. The list is ordered by price, so that is
            // the top of it unless propagation changes the answer - and when it does, the take goes
            // ABOVE the list on a line of its own. Order is the whole signal here: first line is
            // what to take, the rest is what it is worth in money.
            //
            // No border. The green border means "this is the one to click" and there is nothing to
            // click on the ground - the click happens in the combinations window, and that is where
            // the border lives. A border here would be the same mark meaning two different things
            // two screens apart.
            //
            // An extra line rather than a reordering, which is what this was first written as. The
            // reward list is one line long by default, so swapping the take into it meant the take
            // and the most valuable were competing for the same row and only one of them could be
            // seen. The whole point of saying anything here is that the two differ, which cannot be
            // shown by displaying one of them.
            //
            // It is then dropped from the list below, so the reward never appears twice - and since
            // the extra line does not come out of the line budget, the most valuable is still there
            // however tight that budget is set.
            int take;

            using (Spent.On("Remnants/Take"))
                take = Taking(target, settings, valuation, gc, scan, plan);

            // What the remnant is set to RIGHT NOW, when the plan wants something else.
            //
            // **Two rules were quietly deciding this between them and the loser was never shown.**
            // With overruling on, the plan names its own pick and a combination the player set by
            // hand disappears; with it off, the list collapses to the player's and the plan's
            // preference disappears. Either way one line appears and there is nothing to say a
            // decision was made at all - so a remnant set deliberately, for a reason the weights do
            // not know, reads exactly like one nobody has touched.
            //
            // Both, in two colours, and this one FIRST. The top line is what is true of the remnant
            // as it stands - what you get if you walk away now - and the green under it is what the
            // plan would rather. Reading downwards is then reading from the present to the proposal,
            // which is the order the decision is actually made in.
            //
            // Nothing is drawn when they agree, which is the ordinary case.
            var picked = take >= 0 && take < target.Rewards.Count ? target.Rewards[take].Name : null;

            // **The reward a remnant is set to is no longer drawn in red beside the plan's, because
            // the comparison it claimed to make cannot be made.**
            //
            // The line said "this is what will happen and the plan would rather have that". It only
            // ever appeared with overruling off - with it on the plan changes the choice, so there
            // is nothing to disagree about - and with it off Planning.Pinned collapses the remnant's
            // option list to the one recipe it is set to. The planner is then given a single choice
            // and picks it, so the two sides of the comparison are the same value by construction
            // and nothing drew.
            //
            // What could still draw was the case where the plan had not published yet: Taking falls
            // back to the pricing heuristic, and a heuristic preference is not the planner's, so the
            // line named an alternative the plan had never weighed. A readout that is silent when it
            // is right and wrong when it speaks is worth less than no readout.
            //
            // The honest version needs the planner to rank every option while still scoring with the
            // pinned one, which is a change to Weighing.Choices rather than to a colour. Until that
            // exists there is nothing true to draw here.

            // Behind the same switch as the list it sits above. It was drawn unconditionally,
            // so turning rewards off left the green pick and the red overruled line on screen -
            // which reads as a switch that does not work, and is only visible at all when the
            // remnant is being kept alive by propagation or reroll advice.
            // **Bounded, because Taking answers from a cache and the list can shrink underneath
            // it.** The answer is kept for a second against the plan and the table revision, and a
            // reward list narrowed inside that second leaves an index pointing past the end - the
            // same staleness the overruled line above is guarded against, one case later. It threw
            // ArgumentOutOfRangeException out of Render, which takes the whole overlay down rather
            // than one line of it.
            // The reward text and its layout, timed together. Each line formats a name and a price
            // into a string and hands it to DrawTextWithBackground, which measures it - so the cost
            // is per line drawn rather than per reward held, and the two differ by a lot on a site
            // where every remnant carries a full list.
            using (Spent.On("Remnants/Rewards"))
            {
                if (take > 0 && take < target.Rewards.Count && rewards)
                    at.Y += Line(graphics, settings, valuation, target.Rewards[take], at, true);

                var drawn = 0;

                for (var i = 0; i < target.Rewards.Count && drawn < lines; i++)
                {
                    // Skip the hoisted one, and ONLY when it was hoisted. Skipping on `i == take`
                    // alone dropped the richest reward whenever the pick WAS the richest, because then
                    // take is zero and zero is the richest - so the usual case, one line long, showed
                    // the second best reward and nothing else.
                    if (take > 0 && i == take)
                        continue;

                    // Green is the pick, wherever it lands. When nothing was hoisted the pick IS the
                    // top of the list, so the first line is green and purple never appears - which is
                    // the point: purple means "the money, and not what to take", so it has nothing to
                    // say when those are the same reward.
                    at.Y += Line(graphics, settings, valuation, target.Rewards[i], at, take == 0 && i == 0);
                    drawn++;
                }
            }

            if (passing == null)
                continue;

            // Under the rewards, on ONE line however many slots carry forward, and in its own
            // colour because it answers a different question. Amber for a reason: the game borders
            // the propagating slots in yellow, so the two agree on screen without anybody having to
            // be told they are the same thing.
            // **The figures come from the objective, the overlay only reads them.** What is
            // landing on these waves is worked out while the chain is scored - see Planner.RuneTallyByRemnant -
            // because a readout that recomputes a rule is a readout that will disagree with it, as
            // the blast circles and the coverage test both did.
            //
            // **Nothing at all when the objective has not spoken**, which is the case for a remnant
            // no chain reaches. There used to be a fallback line here that listed what a remnant
            // COULD propagate, worked out from its own candidates with no idea what the chain
            // already carried - so it named runes that were worth nothing and contradicted the
            // figures beside it. A remnant with no position has no answer, and no answer is the
            // honest thing to draw.
            var cell = ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));

            using var waves = Spent.On("Remnants/Waves");

            var line = settings.Display.Remnants.Propagation.ShowWaves && Planner.RuneTallyByRemnant.TryGetValue(cell, out var runes)
                ? Propagation.Waves(runes)
                : null;

            if (line != null)
            {
                graphics.DrawTextWithBackground(line, at, settings.Display.Remnants.Propagation.PassColour,
                    RewardBackground);
            }
        }
    }

    /// <summary>
    /// Where a remnant's reward line goes: under the game's own label for it.
    ///
    /// Not over the remnant. The game already draws "Remnant, 4 socket(s)" and the runes there, and
    /// anything put at the remnant's own position lands underneath that and cannot be read at all -
    /// which is exactly how this first shipped. Hanging off the bottom of the label puts the two
    /// together and keeps them together as the camera moves.
    ///
    /// Falls back to the remnant's position for one that has no label, which is any of them off
    /// screen or too far away - remembered ones, in other words, where there is nothing to sit
    /// under and nothing to collide with either.
    /// </summary>
    private static Vector2 Anchor(GameController gc, Camera camera,
        Dictionary<uint, Element> labels, Target target)
    {
        // Read through the state-passing Safe.Read rather than a capturing lambda. A lambda that
        // closes over a local allocates a closure object and a delegate every call, and this runs
        // once per remnant per frame: measured at 139,205 bytes a frame over 3,600 calls in a
        // 240-frame window, which was the largest named part of the remnant drawing.
        if (target.Live)
        {
            var id = Safe.Read(target, static t => t.Entity.Id, 0u);

            // The cached rect, which is what the other 38 rect reads in this plugin use - including
            // RollButton, three lines of this same file away, for a rect that gets a border drawn on
            // it. GetClientRect recomputes by walking back up the parent chain on every call, and
            // this stage measured 101,953 bytes a frame over 12 remnants with the closures already
            // taken out of it, so the recomputation is the remaining suspect. Unverified: it needs a
            // dump to confirm the bytes actually move.
            if (id != 0 && labels.TryGetValue(id, out var label) &&
                Safe.Read(label, static l => l.GetClientRectCache, default) is { Width: > 0f } rect)
                return new Vector2(rect.Left, rect.Bottom + 2f);
        }

        var world = target.Where(gc);

        return world == Vector3.Zero
            ? Vector2.Zero
            : Safe.Read((Camera: camera, World: world), static x => x.Camera.WorldToScreen(x.World), Vector2.Zero);
    }

    /// <summary>
    /// The on-screen label for each thing that has one, by entity id. Built once a frame.
    ///
    /// The ELEMENT rather than its rect, because the label is not only a place to hang text under:
    /// the game's own Liquid Verisium button lives inside it, so what is wanted is something that
    /// can be descended into as well as measured.
    /// </summary>
    private static Dictionary<uint, Element> Labels(GameController gc)
    {
        var found = new Dictionary<uint, Element>();
        // The full list, not the visible subset. A dump taken standing on a remnant found three
        // labels in ItemsOnGroundLabelsVisible - all of them for remnants four hundred grid units
        // away, projecting off the top of the screen - and nothing for the one underfoot. Whatever
        // "visible" means there, it is not "the one you are standing on", and the reroll border
        // never drew because of it.
        var labels = Ground.Labels(gc);

        if (labels == null)
            return found;

        foreach (var label in labels)
        {
            // Two state-passing reads rather than two capturing lambdas, for the same reason as in
            // Anchor: the loop runs over every ground label on the site, so a closure here is a
            // per-label allocation. 50,638 bytes a frame before the change.
            var id = Safe.Read(label, static l => l.ItemOnGround?.Id ?? 0u, 0u);
            var element = Safe.Read(label, static l => l.Label, (Element)null);

            if (id == 0 || element == null)
                continue;

            found[id] = element;
        }

        return found;
    }

    /// <summary>
    /// What a roll here is worth, as one figure beside the Verisium button.
    ///
    /// **In the propagating yellow, because runes are all it measures.** The reroll comparison
    /// scores both sides with the reward set aside - see Rolling.Enumerated - so the number is what
    /// the roll does to the runes the chain carries, and the yellow the game borders a propagating
    /// slot in is the colour it is already about.
    ///
    /// It was three figures: a total, the rune half and the reward half, split apart because they
    /// rested on different evidence. With the reward out of the comparison there is one thing left
    /// to say and a stack of three said it three times.
    ///
    /// On the remnant being recommended by default, and on every remnant with a verdict when asked
    /// for - a dig site with a number over every label is one nobody can read, so the second is a
    /// thing to switch on while checking the advice rather than a thing to play with. The split
    /// into a fixed-chain figure and a re-route premium is still in the dump, where there is room
    /// for it. See DisplaySettings.RollFigures and RollFiguresAll.
    /// </summary>
    /// <summary>
    /// Whether the figures beside the roll button would be drawn for this remnant.
    ///
    /// Split out of Explaining so the caller can ask before paying for the button rect, which costs
    /// 20,304 bytes to resolve - see RollButton. Every test in here is about the advice and the
    /// settings, none of them about where the button is, so asking first costs nothing.
    ///
    /// **Two settings, because these are two different things to want.**
    ///
    /// The next planned roll is the advice: one remnant, the one the line leads to and the border
    /// rings, and the figures are there to say why it is worth the Verisium. Every remnant with a
    /// verdict is a reading of the site - it includes the ones the advice says to keep, whose
    /// expected value is usually negative, which is the half of the argument the advice never shows.
    /// See DisplaySettings.RollFigures and RollFiguresAll.
    /// </summary>
    private static bool ShowsRollFigures(AutoExpeditionSettings settings, Target target)
    {
        if (!Rolling.Here.Fresh)
            return false;

        var planned = Rolling.Here.Best is { } best &&
                      Vector2.Distance(best.Grid, target.Grid) < 1f;

        return planned
            ? settings.Display.Remnants.Rerolls.RollFigures
            : settings.Display.Remnants.Rerolls.RollFiguresAll;
    }

    private static void Explaining(Graphics graphics, AutoExpeditionSettings settings, Target target,
        RectangleF button)
    {
        if (!ShowsRollFigures(settings, target))
            return;

        var said = Rolling.Here.Of(target.Grid);

        if (said == null)
            return;

        // Anchored to the right edge of the button and centred on it, so it sits beside the thing
        // it is about rather than over the label it belongs to.
        var height = graphics.MeasureText("+0").Y;
        var at = new Vector2(button.Right + 4f, button.Center.Y - height / 2f);

        // The whole gain, which is the number the verdict is made on. Its two halves - what a roll
        // is worth with the chain held still, and what re-solving adds - are in the dump.
        Figure(graphics, settings, ref at, said.Gain, settings.Display.Remnants.Propagation.PassColour);
    }

    /// <summary>One of those figures, signed, on the same dark ground the reward lines use.</summary>
    private static void Figure(Graphics graphics, AutoExpeditionSettings settings, ref Vector2 at,
        double worth, Color colour)
    {
        var said = worth.ToString("+0;-0;0");
        var size = graphics.MeasureText(said);

        graphics.DrawBox(new RectangleF(at.X - 2f, at.Y, size.X + 4f, size.Y),
            RewardBackground);
        graphics.DrawText(said, at, colour);

        at.Y += size.Y;
    }

    /// <summary>
    /// The Liquid Verisium button on the remnant standing at this cell, or an empty rect.
    ///
    /// Matched on the label's own entity position rather than on an entity handed in, because the
    /// advice carries a cell and nothing else - see Rolling.Verdict, which is keyed that way so a
    /// verdict outlives the entity it was computed from.
    /// </summary>
    private static RectangleF ButtonFor(GameController gc, Vector2 grid)
    {
        var labels = Ground.Labels(gc);

        if (labels == null)
            return default;

        foreach (var label in labels)
        {
            var at = Safe.Read(() => label.ItemOnGround.GridPos, Vector2.Zero);

            if (at == Vector2.Zero || Vector2.Distance(at, grid) > 1.5f)
                continue;

            var element = Safe.Read<Element>(() => label.Label, null);

            return element == null ? default : RollButton(element);
        }

        return default;
    }

    /// <summary>
    /// The game's Liquid Verisium button on a remnant's label, or an empty rect when there is none.
    ///
    /// There is none when the player has no Liquid Verisium, which is the game stating that a roll
    /// is not possible - so nothing is drawn, and the advice goes quiet on its own rather than
    /// recommending something that cannot be done.
    /// </summary>
    /// <summary>
    /// The resolved button, remembered for the frame it was resolved in.
    ///
    /// Keyed on the label's address rather than the Element object, because the labels dictionary is
    /// rebuilt every frame from the ground-label list and there is no promise the same wrapper comes
    /// back for the same element.
    /// </summary>
    private static readonly Dictionary<long, RectangleF> Buttons = new();

    private static int _buttonsFor = -1;

    /// <summary>
    /// The game's own Liquid Verisium button on a remnant's ground label, as a rect.
    ///
    /// **Measured at 20,304 bytes a call**, which is why it is memoised. It descends
    /// Placement.RollPath through Safe.Kid, and every step reads the node's whole child-pointer
    /// vector - so the remnant drawing spent 243,644 bytes a frame here, 61% of everything it
    /// allocated and the largest single line in Overlay.Draw. Where that figure actually goes is not
    /// yet known: three pointer vectors do not come to 20KB, so something inside is either reading a
    /// far larger array than expected or throwing and being caught. See LeafCalls.GameReadThrows,
    /// which was added to tell those apart, and do not assume it is the vectors.
    ///
    /// **For one frame only.** The rect moves with the camera, so a memo held any longer would draw
    /// a border where the button used to be. The two callers that want the same remnant's button in
    /// one frame - the remnant list and the line leading to the advised roll - share one resolution
    /// because of this, and nothing else changes.
    /// </summary>
    internal static RectangleF RollButton(Element label)
    {
        var key = Safe.Read(label, static e => (long)e.Address, 0L);

        if (_buttonsFor != Frame.Number)
        {
            Buttons.Clear();
            _buttonsFor = Frame.Number;
        }
        else if (key != 0L && Buttons.TryGetValue(key, out var had))
        {
            return had;
        }

        var rect = RollButtonUncached(label);

        if (key != 0L)
            Buttons[key] = rect;

        return rect;
    }

    private static RectangleF RollButtonUncached(Element label)
    {
        var at = label;

        foreach (var index in Placement.RollPath)
        {
            // The count first - see Panels.Resolve. Reading Children is what logs the miss, and a
            // remnant with no Liquid Verisium has no button at this index, so the ordinary case on
            // a site where you are not carrying one was a line in the log per label per frame.
            if (Safe.Read(at, static e => e.ChildCount, 0) <= index || index < 0)
                return default;

            at = Safe.Kid(at, index);

            if (at == null)
                return default;
        }

        if (!Safe.Read(at, static e => e.IsVisible, false))
            return default;

        var rect = Safe.Read(at, static e => e.GetClientRectCache, default);

        return rect.Width > 0f && rect.Height > 0f ? rect : default;
    }

    // ------------------------------------------------------------------ the working

    private static void Tally(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        List<Target> targets, bool placing, Snap snap, Scan scan,
        Valuation valuation, Boundary boundary, Spawns spawns, List<RectangleF> covered,
        Planning planning)
    {
        if (!settings.Debug.ShowTally)
            return;

        // The whole block moves together, so it is dropped as a block rather than line by line -
        // half a column of counts is worse than none.
        if (Panels.Covers(covered, new Vector2(settings.Debug.TallyX.Value, settings.Debug.TallyY.Value)))
            return;

        var line = new Vector2(settings.Debug.TallyX.Value, settings.Debug.TallyY.Value);

        // Every kind, not the four a small site has. A row with nothing in it is skipped below, so
        // the sentries, eggs and entrances a Grand site adds cost nothing anywhere else - and
        // leaving them out had forty eggs drawn on screen and counted nowhere.
        foreach (var kind in new[]
                 {
                     TargetKind.Remnant, TargetKind.Elite, TargetKind.Monster, TargetKind.Chest,
                     TargetKind.Sentry, TargetKind.Hatch, TargetKind.Entrance,
                     TargetKind.Relic, TargetKind.Scenery, TargetKind.Caged,
                     TargetKind.Monolith, TargetKind.Strongbox,
                 })
        {
            // Chests are split by tier, because that is the distinction being chosen between: a
            // blue is worth going out of the way for and a white is not.
            var groups = kind == TargetKind.Chest
                ? targets.Where(t => t.Kind == kind).GroupBy(t => t.Tier)
                    .OrderByDescending(g => g.Key)
                    .Select(g => (Label: $"{Name(g.Key)} chests", Of: g.ToList(), Tier: g.Key))
                : new[]
                {
                    (Label: Name(kind), Of: targets.Where(t => t.Kind == kind).ToList(), Tier: ChestTier.Unknown),
                }.AsEnumerable();

            foreach (var (label, of, tier) in groups)
            {
                if (of.Count == 0)
                    continue;

                var caught = placing ? of.Count(t => t.Glowing) : 0;
                var text = placing ? $"{label} {caught}/{of.Count}" : $"{label} {of.Count}";
                var colour = kind == TargetKind.Chest ? ChestColour(settings, tier) : Colour(settings, kind);

                var size = graphics.DrawText(text, line, colour);
                line.Y += size.Y;
            }
        }

        // Worth saying once, rather than leaving every remnant reading nothing with no explanation.
        if (!valuation.Priced && targets.Any(t => t.Kind == TargetKind.Remnant))
        {
            graphics.DrawText("remnant prices need NinjaPricer", line, Color.FromArgb(255, 200, 160, 90));
            line.Y += 16f;
        }

        // Name then quantity, the same shape as every content line above it, so the block reads as
        // one list rather than a list with a sentence stuck on the end.
        graphics.DrawText($"explosives {Detonator.ExplosivesInHand(gc)}", line, Color.White);

        // **The ceiling the search stops at, which is not a score anybody can reach.**
        //
        // Named on the line rather than left to be inferred: it is the site scored with four rules
        // dropped - no reach, carriers lifting monsters that came out before them, every remnant
        // taking every combination, a target caught twice - so the percentage beside it is NOT how
        // much of the site is left. A dense site reads a few per cent with an optimal plan on
        // screen. See Planner.RelaxedCeiling for the four.
        //
        // Worth showing anyway, in a debug panel: when the two meet the relaxations have stopped
        // being relaxations and the search stops, which is what PROVEN BEST says.
        //
        // Read rather than worked out: the ceiling walks every candidate spot and everything each
        // covers, which is a solve's work and not a frame's. See Planner.RelaxedCeilingNow.
        if (Planner.RelaxedCeilingNow > 0d && planning?.Plan is { Points.Count: > 0 } drawn)
        {
            line.Y += 16f;

            var share = drawn.Plain / Planner.RelaxedCeilingNow * 100d;

            graphics.DrawText(
                $"relaxed ceiling {Planner.RelaxedCeilingNow:N0} - unreachable" +
                (planning.ProvenBest ? "   PROVEN BEST" : ""),
                line, Color.FromArgb(255, 170, 170, 170));

            line.Y += 16f;

            graphics.DrawText(
                $"  plan {drawn.Plain:N0} = {share:0.#}% of it (not progress)",
                line, Color.FromArgb(255, 170, 170, 170));
        }

        var found = 0;

        // Says plainly that the count is remembered rather than seen, so an empty-looking dig site
        // across the map cannot be mistaken for an empty tally.
        if (scan.Sites > 1 || scan.LiveCount < targets.Count)
        {
            line.Y += 16f;
            graphics.DrawText(
                $"remembered {targets.Count}, in view {targets.Count(t => t.Live)}" +
                (scan.Sites > 1 ? $"   site 1 of {scan.Sites}" : ""),
                line, Color.FromArgb(255, 170, 170, 170));
        }

        // **Each search thread's best, while it is still searching.**
        //
        // The dump says what the workers scored once they have all stopped, which is the wrong
        // moment for the question this answers: are the eight of them exploring different ground, or
        // converging on one answer? Eight numbers climbing together says the diversification is not
        // working; one pulling away says it is - and that is worth seeing as it happens rather than
        // reconstructing afterwards.
        //
        // Stacked rather than run together, and the score first, because a column of figures with a
        // common left edge can be compared at a glance and a sentence per thread cannot. The lean is
        // what that worker was told to favour. See Solving.Watching.
        // **The plain figure leads, because that is the one on screen beside it.**
        //
        // These lines used to show the search's objective, which carries a synthetic bonus for holding a
        // marker the player insisted on - so on a site with one must-take a thread read 18,176 while the
        // green score read 12,096 for the same chain, and one of them looked wrong. Neither was: they
        // are different quantities, and only one of them is loot.
        //
        // The objective follows in brackets where the two differ, so the number the search is actually
        // maximising stays visible without being mistaken for what the chain is worth. See
        // Solving.Watching and Verdict.Plain.
        foreach (var (plain, best, lean) in Solving.Watching())
        {
            line.Y += 16f;

            var said = double.IsNegativeInfinity(plain)
                ? "-"
                : Math.Abs(best - plain) < 0.5d
                    ? $"{plain:N0}"
                    : $"{plain:N0} [{best:N0} held]";

            graphics.DrawText(
                said.PadRight(said.Length > 16 ? said.Length + 2 : 16) +
                $"thread {found} ({(lean.Length > 0 ? lean : "even")})",
                line, Color.FromArgb(255, 170, 170, 170));

            found++;
        }

        // What the chain has actually unearthed, live. The counts the weight table is meant to be
        // derived from, watchable while the fight is happening rather than only in a file
        // afterwards - which is also how a guard that is counting the map gets noticed.
        if (spawns != null && settings.Recording.RecordSpawns)
        {
            var (normal, magic, rare, unique, waves, far, counting) = spawns.Live;

            if (normal + magic + rare + unique + far > 0)
            {
                line.Y += 16f;
                graphics.DrawText(
                    $"unearthed {normal} normal, {magic} magic, {rare} rare" +
                    (unique > 0 ? $", {unique} unique" : "") +
                    (waves > 0 ? $"   waves {waves}" : "") +

                    // Everything seen that the range gate threw away. Without it, a count that is
                    // too low looks the same whether nothing was unearthed or everything was
                    // unearthed too far from the chain to be counted.
                    (far > 0 ? $"   {far} out of range" : "") +
                    (counting ? "" : "   (stopped)"),
                    line, Color.FromArgb(255, 255, 200, 120));
            }
        }

        if (settings.Debug.ShowMeasurements)
            Measurements(graphics, gc, settings, ref line, snap, boundary, targets);
    }

    private static void Measurements(Graphics graphics, GameController gc, AutoExpeditionSettings settings,
        ref Vector2 line, Snap snap, Boundary boundary,
        List<Target> targets)
    {
        var grey = Color.FromArgb(255, 170, 170, 170);
        var art = Detonator.ArtRadius(gc);
        var drawn = Detonator.BlastRadius(gc, settings.Debug.CircleCorrection.Value);

        // The computed radius first, since that is what everything plans and draws to, then the art
        // beside it - which is there to be disagreed with. See Blast.Disagrees.
        line.Y += 16f;
        graphics.DrawText(
            $"explosion radius {(drawn == null ? "-" : drawn.Value.ToString("0.##"))}" +
            $" = base {(Detonator.Grand(gc) ? Detonator.GrandBlast : Detonator.OrdinaryBlast):0.#}" +
            $" x {Detonator.ModifierScale(gc):0.###}" +
            $" {settings.Debug.CircleCorrection.Value:+0.##;-0.##}" +
            $" (art {(art == null ? "not drawn" : art.Value.ToString("0.##"))})", line, grey);


        // The marker extent, per art, from the crossings the game has demonstrated. Per art rather
        // than per kind because the sizes look like they differ between markers of one kind, and
        // this is what the marker size setting ought to be.
        var (tight, seen) = boundary.Progress();

        line.Y += 16f;
        graphics.DrawText($"marker extent: {tight} of {seen} arts pinned down", line, grey);

        foreach (var text in boundary.Lines())
        {
            line.Y += 16f;
            graphics.DrawText($"  {text}", line, grey);
        }

        line.Y += 16f;
        graphics.DrawText($"snaps every {snap.Describe()}", line, grey);

        line.Y += 16f;
        graphics.DrawText(
            $"range {Detonator.PlacementRange(gc):0.#} (base {Detonator.BaseReach(gc):0.#}, " +
            $"map {Detonator.PlacementRangePct(gc):+0;-0;+0}%)", line, grey);

        // Printed so the anchor can be checked at a glance rather than taken on trust: the chain
        // reaches from its own last link, never from wherever the player is standing.
        var origin = Detonator.LastExplosiveGridPosition(gc);
        var placed = Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0);
        var at = Detonator.PlacementIndicatorGridPosition(gc);
        var asked = Detonator.RequestedGridPosition(gc);

        line.Y += 16f;
        graphics.DrawText(
            $"reaching from {(placed > 0 ? $"explosive {placed}" : "the detonator")} " +
            $"({origin.X:0},{origin.Y:0}), now {Vector2.Distance(origin, at):0.#} out" +
            (Detonator.PlacementIndicatorIsRed(gc) ? "  BLOCKED" : ""), line, grey);

        // The two positions side by side. They agree wherever an explosive can go and part company
        // past the limit, which is why the entity is the one read.
        line.Y += 16f;
        graphics.DrawText(
            $"lands ({at.X:0},{at.Y:0})   cursor asks ({asked.X:0},{asked.Y:0})" +
            (Vector2.Distance(at, asked) > 1.5f ? $"  clamped by {Vector2.Distance(at, asked):0.#}" : ""),
            line, grey);

        // What the targeting grid says under ground that is placeable by definition. This is the
        // evidence for what the planner's terrain test should actually be; see Terrain.Describe.
        line.Y += 16f;
        graphics.DrawText($"targeting grid at placeable ground: {Terrain.Describe(gc, targets)}", line, grey);
    }

    // ------------------------------------------------------------------ naming and colour

    /// <summary>Whether a world position lands under one of the partial windows.</summary>
    private static bool Under(GameController gc, List<RectangleF> covered, Vector3 world)
    {
        if (covered.Count == 0)
            return false;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera == null)
            return false;

        var at = Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        return at != Vector2.Zero && Panels.Covers(covered, at);
    }

    private static string Name(TargetKind kind) => kind switch
    {
        TargetKind.Remnant => "remnants",
        TargetKind.Elite => "rare monsters",
        TargetKind.Chest => "chests",
        TargetKind.Sentry => "sentries",
        TargetKind.Hatch => "siren eggs",
        TargetKind.Caged => "gated encounters",
        TargetKind.Monolith => "monolith bosses",
        TargetKind.Strongbox => "strongboxes",
        TargetKind.Entrance => "gates and entrances",
        TargetKind.Relic => "relics",
        TargetKind.Scenery => "explodables",
        _ => "runic monsters",
    };

    private static string Name(ChestTier tier) => tier switch
    {
        ChestTier.Common => "normal",
        ChestTier.Uncommon => "magic",
        ChestTier.Rare => "rare",
        ChestTier.Gold => "gold",
        ChestTier.GrandCurrencyBright => "bright currency",
        ChestTier.GrandCurrency => "currency",
        ChestTier.GrandUnique => "unique",
        ChestTier.GrandTrinkets => "trinket",
        ChestTier.GrandMaps => "map",
        ChestTier.GrandArmour => "armour",
        ChestTier.GrandGeneric => "plain reward",
        _ => "unknown",
    };

    private static Color Colour(AutoExpeditionSettings settings, Target target) =>
        target.Kind == TargetKind.Chest
            ? ChestColour(settings, target.Tier)
            : Colour(settings, target.Kind);

    private static Color Colour(AutoExpeditionSettings settings, TargetKind kind) => kind switch
    {
        TargetKind.Remnant => settings.Debug.RemnantColour,
        TargetKind.Elite => settings.Debug.EliteColour,
        TargetKind.Sentry => settings.Debug.SentryColour,
        TargetKind.Entrance => settings.Debug.EntranceColour,
        TargetKind.Relic => settings.Debug.RelicColour,
        TargetKind.Caged => settings.Debug.EliteColour,
        TargetKind.Monolith => settings.Debug.EliteColour,
        TargetKind.Strongbox => settings.Debug.RareChestColour,
        TargetKind.Scenery => settings.Debug.CommonChestColour,
        _ => settings.Debug.MonsterColour,
    };

    private static Color ChestColour(AutoExpeditionSettings settings, ChestTier tier) => tier switch
    {
        ChestTier.Common => settings.Debug.CommonChestColour,
        ChestTier.Uncommon => settings.Debug.UncommonChestColour,
        ChestTier.Rare => settings.Debug.RareChestColour,
        _ => settings.Debug.UnknownChestColour,
    };
}
