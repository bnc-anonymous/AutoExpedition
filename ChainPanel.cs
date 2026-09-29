using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2;
using ImGuiNET;

namespace AutoExpedition;

/// <summary>
/// Every chain the last press produced, with what it scores and a button to make it the plan.
///
/// **Because "fifteen distinct chains" is not the same claim as "fifteen different ideas", and only the
/// eye can tell them apart.** The distinct count compares chains for exact equality, so two routes
/// differing in one link count as two - and on the site this was written for it reads 14.7 of 16 while the
/// scores cluster at about 7,400, which is what a set of variations on one route looks like. Worse, six of
/// eight workers were measured opening on the identical spot while the winner opened elsewhere: the
/// numbers said "diverse" and the pool was mostly in one basin.
///
/// So the plans are kept rather than described again and loading one adopts it, which draws it with its
/// links, its blast circles and the markers it catches, in real terrain. A cycle key was the first version
/// of this; a list is better because the scores sit beside each other and a chain can be chosen rather
/// than stepped past.
///
/// **Loading adopts.** The chain shown becomes the plan, so a placement run started afterwards would place
/// it. The next solve replaces it either way. See Planning.Adopt.
/// </summary>
internal static class ChainPanel
{
    private static AutoExpeditionSettings _settings;

    /// <summary>Opens or closes the window, for the hotkey.</summary>
    public static void Toggle()
    {
        if (_settings?.Debug?.ShowChains == null)
            return;

        _settings.Debug.ShowChains.Value = !_settings.Debug.ShowChains.Value;
    }

    /// <summary>Draws the window, if it is switched on. Called from Render, beside the reference table.</summary>
    public static void Draw(GameController gc, AutoExpeditionSettings settings, Planning planning)
    {
        _settings = settings;

        if (settings?.Debug?.ShowChains == null || !settings.Debug.ShowChains.Value)
            return;

        var open = settings.Debug.ShowChains.Value;

        ImGui.SetNextWindowSize(new Vector2(900f, 420f), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Worker chains###aeChains", ref open))
        {
            ImGui.End();

            settings.Debug.ShowChains.Value = open;

            return;
        }

        settings.Debug.ShowChains.Value = open;

        try
        {
            Body(planning);
        }
        finally
        {
            ImGui.End();
        }
    }

    private static void Body(Planning planning)
    {
        Generated(planning);

        ImGui.Separator();

        // **Every press of the batch, not just the last one.** A batch walks ten draws and the last press is
        // the one nobody is asking about; the question is which draw found the good route. Falls back to the
        // live pool when no batch has been taken, which is the single-press case. See PressHistory.Every.
        var batch = PressHistory.Every();
        var pool = batch.Count > 0
            ? batch
            : Flat(Solving.Pool);

        if (pool.Count == 0)
        {
            ImGui.TextWrapped("No chains yet - solve once and they appear here, best first.");

            return;
        }

        var best = pool[0].Plan.Points;

        ImGui.TextWrapped($"{pool.Count} chain(s) from {Draws(pool)} press(es), best first. " +
                          "Load makes one the plan, so it draws in the world; the next solve replaces it.");

        ImGui.Separator();

        if (!ImGui.BeginTable("###aeChainRows", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingFixedFit))
            return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30f);

            // Which press it came from, so a good chain can be traced back to a draw and re-run.
            ImGui.TableSetupColumn("Draw", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Site pays", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Links", ImGuiTableColumnFlags.WidthFixed, 45f);

            // **How much of this chain is the best one's, which is the question the scores cannot answer.**
            // Counted against any link of the best chain rather than the link in the same position: two
            // routes that visit the same spots in a different order are not the same route, but they are
            // not independent either, and this is the number that says which.
            ImGui.TableSetupColumn("Shared", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Opening", ImGuiTableColumnFlags.WidthFixed, 230f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < pool.Count; i++)
            {
                var (draw, worth, plan) = pool[i];
                var links = plan.Points;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text($"{i + 1}");

                ImGui.TableNextColumn();
                ImGui.Text(draw > 0 ? $"{draw}" : "-");

                ImGui.TableNextColumn();
                ImGui.Text($"{worth:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{links.Count}");

                ImGui.TableNextColumn();
                ImGui.Text(i == 0 ? "-" : $"{Shared(links, best)}/{links.Count}");

                ImGui.TableNextColumn();
                ImGui.Text(Opening(links));

                ImGui.TableNextColumn();

                if (ImGui.Button($"Load###aeLoad{i}"))
                    planning?.Adopt(plan);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The enumerated openings, with a button to work them out and one to draw each.
    ///
    /// **Generated on demand rather than per solve, because nothing consults them yet.** An opening the
    /// search has never been given is a proposal, and a proposal wants looking at before it is wired to
    /// anything - particularly this one, whose whole claim is that it beats the greedy opening that most of
    /// the pool currently takes. See Openings.
    /// </summary>
    private static void Generated(Planning planning)
    {
        var env = planning?.Env;

        // **Off the game's thread.** The enumeration takes seconds on a Grand site and waits on the lock whenever a
        // solve is enumerating too, so run here it froze the drawing for as long as either took.
        var working = _generating is { IsCompleted: false };

        ImGui.BeginDisabled(env == null || working);

        if (ImGui.Button(working ? "Working out the openings...###aeOpen" : "Work out the openings###aeOpen"))
        {
            var (levels, want, horizon) = (_levels, _want, _horizon);

            _generating = BackgroundWork.StartAtLowPriority(() =>
            {
                Openings.Generate(env, levels, want, horizon);

                return 0;
            });
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.SliderInt("bombs###aeLevels", ref _levels, 1, 5);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);

        // How many survivors a level keeps. Chosen for spread, so a larger number buys more of the map
        // rather than more of one corner of it. See Openings.Spread.
        ImGui.SliderInt("keep###aeWant", ref _want, 1, 27);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);

        // How many links the ranking builds before it scores. The opening's worth is settled by what the
        // first few links reach; the rest is the search's business. See Openings.Finished.
        ImGui.SliderInt("score to###aeHorizon", ref _horizon, 1, 15);

        ImGui.SameLine();
        ImGui.TextDisabled(Openings.Said);

        ImGui.BeginDisabled(env == null);

        // **Saves the site, not the plan.** What comes out is a file an offline run can load to work the
        // openings out again as many times as it likes, with the solver's knobs set by the experiment rather
        // than by whatever they happened to be here. See Layout.
        if (ImGui.Button("Save layout snapshot###aeSnap"))
            Layout.SaveWithOpenings(env, _levels, _want, _horizon);

        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(Layout.Told);

        LevelCountsTable();

        var openings = Openings.Last;

        if (openings.Count == 0)
            return;

        var (branchLabels, workerLabels) = OpeningLabels(env, openings);

        ImGui.TextWrapped($"{openings.Count} opening(s) kept, best first. Branch B.V is the branch (what the " +
                          "first link catches) and the variation within it (what the second link catches); " +
                          "workers are the ones given that opening under the current roles line.");

        if (!ImGui.BeginTable("###aeOpeningRows", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        try
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30f);

            // Takes against leaves: the second is the one greedy cannot see, and the reason a dense first
            // spot can be the wrong one.
            // What a whole chain from this prefix scores, which is what the order is by. Takes and Leaves
            // are kept beside it because they are what a static ranking saw, and they were wrong.
            ImGui.TableSetupColumn("Completed", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Takes", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Links", ImGuiTableColumnFlags.WidthFixed, 230f);
            ImGui.TableSetupColumn("Branch", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Workers", ImGuiTableColumnFlags.WidthFixed, 80f);

            // Why a first bomb was kept, for a one bomb run: each next area it is the best placement towards, or the
            // richest. See Openings.Opening.KeptFor.
            ImGui.TableSetupColumn("Kept for", ImGuiTableColumnFlags.WidthFixed, 260f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < openings.Count; i++)
            {
                var opening = openings[i];

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text($"{i + 1}");

                ImGui.TableNextColumn();
                ImGui.Text($"{opening.Completed:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{opening.Caught:N0}");

                ImGui.TableNextColumn();
                ImGui.Text(Opening(opening.Links));

                ImGui.TableNextColumn();
                ImGui.Text(branchLabels[i]);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"first link catches heavy content [{opening.Branch}], " +
                                     $"second link [{opening.Variation}]");

                ImGui.TableNextColumn();
                ImGui.Text(workerLabels[i]);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(opening.KeptFor);

                ImGui.TableNextColumn();

                // Drawn as a plan of its own length, so two links draw two links - which is what makes an
                // opening something you can look at rather than read.
                if (ImGui.Button($"Show###aeShow{i}") && env != null)
                    planning.Adopt(Planner.Describe(env, opening.Links));
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        LostForksTable(planning);
    }

    /// <summary>
    /// How many openings each level made at each stage: the prefixes extended, the placements the prunes threw
    /// away, the ones offered and rolled out, and the ones kept. See Openings.LevelCounts.
    /// </summary>
    private static void LevelCountsTable()
    {
        var levels = Openings.LastLevels;

        if (levels.Count == 0)
            return;

        if (!ImGui.BeginTable("###aeOpeningLevels", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        try
        {
            ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Prefixes", ImGuiTableColumnFlags.WidthFixed, 65f);
            ImGui.TableSetupColumn("Discarded", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Forks lost", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Offered", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Rolled out", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Not rolled out", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Dominated", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Kept", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableHeadersRow();

            foreach (var level in levels)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Level}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Prefixes:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Discarded:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.ForksLost:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Offered:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.RolledOut:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.NotRolledOut:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Dominated:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{level.Kept:N0}");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The placements the prunes discarded that were the only way to some heavy content, with a button to draw
    /// each and the placement that beat it. See Openings.ForkTally.
    /// </summary>
    private static void LostForksTable(Planning planning)
    {
        var forks = Openings.LastForks;
        var env = planning?.Env;

        ImGui.TextDisabled($"Forks lost: {Openings.ForksSaid}");

        if (forks.Count == 0)
            return;

        if (!ImGui.BeginTable("###aeLostForks", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        try
        {
            ImGui.TableSetupColumn("Discarded", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Beaten by", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Only it reaches", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < forks.Count; i++)
            {
                var fork = forks[i];

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"({fork.Discarded.X:0},{fork.Discarded.Y:0})");
                ImGui.TableNextColumn();
                ImGui.Text($"({fork.BeatenBy.X:0},{fork.BeatenBy.Y:0})");
                ImGui.TableNextColumn();
                ImGui.Text($"({fork.OnlyItReaches.X:0},{fork.OnlyItReaches.Y:0})");

                // Drawn as a one link plan each, so the two placements can be looked at on the ground one after
                // the other.
                ImGui.TableNextColumn();

                if (ImGui.Button($"Show discarded###aeForkLoser{i}") && env != null)
                    planning.Adopt(Planner.Describe(env, [fork.Discarded]));

                ImGui.TableNextColumn();

                if (ImGui.Button($"Show winner###aeForkWinner{i}") && env != null)
                    planning.Adopt(Planner.Describe(env, [fork.BeatenBy]));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// Each opening's branch and variation as numbers, and which workers take it under the current roles line.
    /// Worked out once per generation, because OpeningForWorker walks the list and the panel draws every frame.
    /// </summary>
    internal static (string[] Branches, string[] Workers) OpeningLabels(PlanEnvironment env,
        IReadOnlyList<Openings.Opening> openings)
    {
        if (ReferenceEquals(openings, _labelledOpenings) && ReferenceEquals(env, _labelledEnv) &&
            _branchLabels.Length == openings.Count)
            return (_branchLabels, _workerLabels);

        var branches = new string[openings.Count];
        var workers = new string[openings.Count];
        var branchIndex = new Dictionary<string, int>();
        var variationIndex = new Dictionary<(string, string), int>();
        var variationsInBranch = new Dictionary<string, int>();

        for (var i = 0; i < openings.Count; i++)
        {
            var opening = openings[i];

            if (!branchIndex.TryGetValue(opening.Branch, out var b))
                branchIndex[opening.Branch] = b = branchIndex.Count + 1;

            if (!variationIndex.TryGetValue((opening.Branch, opening.Variation), out var v))
            {
                variationsInBranch.TryGetValue(opening.Branch, out var had);
                variationsInBranch[opening.Branch] = v = had + 1;
                variationIndex[(opening.Branch, opening.Variation)] = v;
            }

            branches[i] = $"{b}.{v}";
            workers[i] = "";
        }

        if (env?.Roles is { Count: > 0 } roles)
        {
            var links = new List<List<Vector2>>();

            foreach (var opening in openings)
                links.Add(opening.Links);

            for (var worker = 0; worker < roles.Count; worker++)
            {
                if (!Repair.TakesEnumeratedOpening(env, worker))
                    continue;

                var given = Openings.OpeningForWorker(env, links, Repair.Slot(env, worker));

                for (var i = 0; i < openings.Count; i++)
                {
                    if (ReferenceEquals(given, openings[i].Links))
                        workers[i] = workers[i].Length == 0 ? $"{worker}" : $"{workers[i]}, {worker}";
                }
            }
        }

        _labelledOpenings = openings;
        _labelledEnv = env;
        _branchLabels = branches;
        _workerLabels = workers;

        return (branches, workers);
    }

    private static IReadOnlyList<Openings.Opening> _labelledOpenings;

    private static PlanEnvironment _labelledEnv;

    private static string[] _branchLabels = [];

    private static string[] _workerLabels = [];

    /// <summary>The enumeration the button started, while it runs. See Generated.</summary>
    private static System.Threading.Tasks.Task _generating;

    private static int _levels = 2;

    private static int _want = 8;

    private static int _horizon = 3;

    /// <summary>The live pool in the shape the batch list uses, for the press-at-a-time case.</summary>
    private static List<(int Draw, double Worth, Plan Plan)> Flat(
        IReadOnlyList<(double Worth, Plan Plan)> pool)
    {
        var all = new List<(int Draw, double Worth, Plan Plan)>();

        foreach (var (worth, plan) in pool)
            all.Add((0, worth, plan));

        return all;
    }

    /// <summary>How many presses the list spans, for the line above it.</summary>
    private static int Draws(List<(int Draw, double Worth, Plan Plan)> pool)
    {
        var seen = new HashSet<int>();

        foreach (var (draw, _, _) in pool)
            seen.Add(draw);

        return seen.Count;
    }

    /// <summary>How many of this chain's links appear anywhere in the other, within a grid unit.</summary>
    private static int Shared(IReadOnlyList<Vector2> chain, IReadOnlyList<Vector2> other)
    {
        var count = 0;

        foreach (var at in chain)
        {
            foreach (var was in other)
            {
                if (Vector2.Distance(at, was) >= 1f)
                    continue;

                count++;

                break;
            }
        }

        return count;
    }

    /// <summary>
    /// The first few links, which is where a chain is decided.
    ///
    /// Four, because that is the most an opening has been measured to be - the plurality of a pool held
    /// together for two links on the site this was written for. See Solving.Openings.
    /// </summary>
    private static string Opening(IReadOnlyList<Vector2> chain)
    {
        var said = new List<string>();

        for (var i = 0; i < chain.Count && i < 4; i++)
            said.Add($"({chain[i].X:0},{chain[i].Y:0})");

        return string.Join(" ", said);
    }
}
