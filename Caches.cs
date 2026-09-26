using System;

namespace AutoExpedition;

/// <summary>
/// Throws away everything the plugin has worked out about where it is standing.
///
/// **For measuring, not for playing.** Almost everything here learns: the router floods the ground
/// and keeps it, the scan remembers markers the game has unloaded, the blast radius and the chain's
/// reach are read once and reused, the planner hands its last answer to the next solve as a starting
/// point. All of that makes the plugin better the longer you stand in a dig site, and all of it
/// makes a measurement meaningless - a change cannot be compared against a warm state it did not
/// have to build.
///
/// So this is the button that gets back to the state of having just walked in, without reloading the
/// plugin and losing the settings and the tuning with it.
///
/// **It is a request rather than an action**, because it is pressed in the settings window and the
/// things it clears are owned by the plugin. The flag is read on the next tick, where all of them
/// are in scope.
/// </summary>
internal static class Caches
{
    /// <summary>Set by the button, cleared by the tick that acts on it.</summary>
    public static bool Wanted;

    /// <summary>
    /// The same request, keeping the markers and the ground facts on disk.
    ///
    /// **Re-walking a Grand site to test a change to the planner is minutes of nothing.** The full
    /// reset is right for a measurement that has to start from having just arrived, and wrong for
    /// the ordinary case of "solve that again as though you had not just solved it": the site's
    /// contents took a lap to collect and nothing about the search is learnt from them.
    ///
    /// So this forgets the derived work - the routed ground, the last plan, the filed best chain,
    /// the readings taken here - and leaves the markers, the scouting layer and the saved ground
    /// file where they are.
    /// </summary>
    public static bool WantedKeepingScan;


    /// <summary>What the last clear threw away, so the button can say it happened.</summary>
    public static string Last { get; private set; } = "nothing cleared yet";

    /// <summary>When that was, so a dump can say whether it explains the solve beside it.</summary>
    public static DateTime When { get; private set; }

    /// <summary>
    /// Everything, in one pass.
    ///
    /// The per-area caches are cleared by telling them the area changed - which is the same path a
    /// zone takes, so there is no second way for them to forget and no chance of the two drifting
    /// apart. The stamp is the clock rather than a fixed number, so it cannot collide with whatever
    /// they are holding and quietly do nothing.
    /// </summary>
    /// <param name="keepScan">
    /// Whether the markers the scan remembers survive.
    ///
    /// **Forgetting them is not a reset, it is a loss.** The scan holds content the game has since
    /// unloaded - everything walked past and now off screen - and that cannot be read back without
    /// walking there again. A solve after a full clear therefore searches a smaller site than the
    /// one before it: measured, ninety markers became seventy seven, which moves the score by more
    /// than any change being compared.
    ///
    /// So a measurement keeps it. What it wants cold is the derived work - the routed ground, the
    /// last plan, the readings taken here - not the site's contents.
    /// </param>
    /// <param name="area">
    /// The area whose saved ground facts to delete, or nought to leave the file alone.
    /// </param>
    /// <param name="keepGround">
    /// Whether the saved walkability facts survive, while the best chain filed beside them does not.
    ///
    /// **The two were one switch and they are not one thing.** Deleting the filed chain is the whole
    /// point of forgetting: leave it and the next solve loads it straight back as its floor, so a
    /// "cleared" site opens at exactly the score it opened at before and nothing has been reset. The
    /// ground is the opposite - it is minutes of flooding, it is the same answer every time because
    /// the terrain does not move, and keeping it makes two solves comparable rather than measuring
    /// the router twice.
    /// </param>
    /// <summary>
    /// Deletes every site file the plugin has written, for every area it has seen.
    ///
    /// The four kinds share one folder and are named by what they are - site_ for the markers,
    /// ground_ for the walkable cells, plan_ for the filed chains, scout_ for the scouting layer -
    /// so the sweep is by pattern rather than by a list of areas nobody keeps.
    ///
    /// Silent about failures. A file the game or another process holds open will still be there
    /// next time, and a line on screen about it helps nobody in the middle of a map.
    /// </summary>
    private static void Wipe(string home)
    {
        if (string.IsNullOrEmpty(home))
            return;

        try
        {
            var directory = System.IO.Path.Combine(home, "sites");

            if (!System.IO.Directory.Exists(directory))
                return;

            foreach (var pattern in new[] { "site_*.tsv", "ground_*.tsv", "plan_*.tsv", "scout_*.txt" })
            {
                foreach (var path in System.IO.Directory.GetFiles(directory, pattern))
                {
                    try
                    {
                        System.IO.File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // One file being held open is not a reason to abandon the rest.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Nothing useful to do about it, and a failed delete is not worth a line on screen.
        }
    }

    public static void Clear(Scan scan, Blast blast, Planning planning,
        Boundary boundary, Snap snap, Cleared cleared, Scouted scouted = null,
        bool keepScan = false, uint area = 0, bool keepGround = false, bool keepRouting = false)
    {
        var stamp = unchecked((uint)DateTime.UtcNow.Ticks);

        Unexpected.Forget();
        Extents.Forget();
        Missed.Here.AreaChange(stamp);

        // The best chain goes too. It is the strongest thing the plugin remembers about a site, so
        // a reset that kept it would leave the next solve inheriting a floor the player had just
        // asked to be rid of. See Kept.
        Kept.ForgetAll(area);

        // And what the presses climbed to, which is a statement about that floor. See Planning.Climb.
        Planning.Forgetting();

        // **Every site's files, not just this one's.**
        //
        // Clearing the scan empties what it holds now; the files it was loaded from are untouched,
        // so leaving the area and coming back reads it all straight back in. That is the worst
        // shape a reset can have - the state looks fresh until you zone, and then silently is not -
        // and it made every "fresh approach" measurement taken through this button a warm one.
        //
        // Area-scoped deletes were the first fix and they are not enough either: a map instance
        // gets a new hash, so "this area" is a different file from the one a test was warmed on,
        // and the old ones sit there for whichever instance happens to collide with them next. The
        // button means "the state of having just walked in", and that state is one where the plugin
        // has no files at all.
        //
        // All four kinds live in one folder, so one sweep covers them: the markers, the walkable
        // ground, the filed chains and the scouting layer.
        if (!keepScan)
        {
            Wipe(scan?.Home);

            scan?.AreaChange(stamp);

            // **The layer in memory as well as the file it came from.** Wipe takes the file; this
            // takes what is already loaded, which is what the minimap is actually drawing. Without
            // it the ground stayed painted as walked until the next zone, so the button looked
            // like it had missed the one layer a player can watch it miss.
            //
            // Forget rather than AreaChange: that one calls Keep first and would write the layer
            // straight back into the file deleted a line above. See Scouted.Forget.
            scouted?.Forget();
        }
        blast?.AreaChange(stamp);
        boundary?.AreaChange(stamp);
        snap?.AreaChange(stamp);
        cleared?.AreaChange(stamp);

        // The reroll advice too. It is an opinion about a chain, and the chain has just been thrown
        // away - left alone it kept reporting "nothing worth rolling" in the settled colour about a
        // plan that no longer exists, which reads as an answer rather than as a leftover.
        Rolling.Here.ForgetTheSite();

        // **What the plugin has read about the interface, and everything that measures itself.**
        //
        // These are not facts about the site, which is why they were missed when this button was
        // written and every time it has been extended since. They are still readings, and they
        // still carry across a press: the gathered interface rectangles, the learnt path to the
        // completion banner and the backoff behind it, and the frame table with its pause,
        // allocation and leaf counts - all of which run from when the plugin loaded.
        //
        // A run tested by pressing this and then dumping was being read against everything that
        // happened beforehand, including the walk through an area to reach somewhere worth
        // testing. The button means "the state of having just walked in", and that state has no
        // readings in it of any kind.
        Panels.Forget();
        Finished.Forget();
        Scan.Forgetting();
        Spent.Forget();
        BackgroundWork.Forget();
        Planner.ForgetCounts();
        Wire.ForgetCounts();

        // Last, because it cancels a search that may still be reading the rest of them.
        planning?.Forget();

        When = DateTime.UtcNow;
        Last = "cleared: the plan, the best chain on file, every reading taken here, " +
               "the interface snapshot, the banner path, and the frame measurements" +
               (keepScan
                   ? ", and kept the markers"
                   : ", the scan, and every site file on disk - markers, ground, plans, scouting") +
               (keepGround ? ", and kept the walkable ground" : "");
    }
}
