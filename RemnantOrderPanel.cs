using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore2;
using ImGuiNET;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// The remnant order search in the chain window, and on the minimap: run it on the site as it stands, read its best
/// orders, load one as the plan, and see them drawn. For working on the solver, not for play. See RemnantOrder.
/// </summary>
internal static class RemnantOrderPanel
{
    /// <summary>How many of the best orders are listed and drawn.</summary>
    private const int Listed = 5;

    /// <summary>How many of the listed orders the minimap draws, the best in full and the rest faded.</summary>
    private const int Drawn = 3;

    private static Task<List<RemnantOrder.Tried>> _running;
    private static List<RemnantOrder.Tried> _found = new();
    private static PlanEnvironment _foundOn;
    private static string _said = "not run here";

    /// <summary>Whether the orders are drawn on the minimap once found. On by default; not kept past a reload.</summary>
    public static bool DrawOnMinimap = true;

    /// <summary>The section's body in the chain window.</summary>
    public static void Body(Planning planning)
    {
        var env = planning?.Env;

        if (_running is { IsCompleted: true } done)
        {
            _running = null;

            if (done.IsCompletedSuccessfully)
                _found = done.Result;
            else
                _said = $"failed: {done.Exception?.GetBaseException().Message}";
        }

        ImGui.TextWrapped("Chains built from every order of the capturable remnants, laid over the solver's own spots, " +
                          "best first. A worker with opening=remnant-order in its thread role starts from one of these.");

        // **With explosives down it plans the rest**: from the last one placed, with the explosives in hand, over the
        // remnants nothing placed has caught - the same picture of the site the solver plans from.
        if (env?.Placed is { Count: > 0 } placed)
            ImGui.TextWrapped($"{placed.Count} explosive(s) down: orders the remnants still uncaught, from the last one placed " +
                              $"({env.Origin.X:0},{env.Origin.Y:0}), with {env.Explosives} in hand. Load keeps the ones down.");

        ImGui.BeginDisabled(env == null || _running != null);

        if (ImGui.Button(_running != null ? "Finding remnant orders...###aeOrdersFind" : "Find remnant orders###aeOrdersFind"))
        {
            var on = env;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            _running = Task.Run(() =>
            {
                // The same budget as the background's deep pass, so the button cannot run for minutes. See RemnantOrder.Latest.
                // Checked with the router before they are listed, since Load adopts one as the plan and the search
                // walks on estimated reach. See RemnantOrder.EstimatedReachOf.
                var found = RemnantOrder.Search(on, keep: Listed, budgetMs: 30000)
                    .Where(x => Repair.Sound(on, x.Chain)).ToList();

                _foundOn = on;
                _said = $"{found.Count} order(s) in {clock.ElapsedMilliseconds:N0} ms";

                return found;
            });
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("Draw on minimap###aeOrdersDraw", ref DrawOnMinimap);
        ImGui.SameLine();
        ImGui.TextDisabled(_said);

        // Where the time went, so a slow search says which part was slow. See RemnantOrder.LastTimings.
        ImGui.TextDisabled("Last search: " + RemnantOrder.LastTimings);

        if (_found.Count == 0)
            return;

        if (!ReferenceEquals(env, _foundOn))
            ImGui.TextDisabled("Found on an earlier state of the site; the scores below are from then.");

        if (!ImGui.BeginTable("###aeOrdersRows", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        try
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 20f);
            ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Links", ImGuiTableColumnFlags.WidthFixed, 45f);
            ImGui.TableSetupColumn("Remnants in order", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < _found.Count; i++)
            {
                var tried = _found[i];

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{i + 1}");
                ImGui.TableNextColumn();
                ImGui.Text($"{tried.Plain:N0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{tried.Chain.Count}");
                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.Join(" ", tried.Order.Select(t => _foundOn != null && t < _foundOn.Targets.Count
                    ? $"({_foundOn.Targets[t].Grid.X:0},{_foundOn.Targets[t].Grid.Y:0})"
                    : "?")));
                ImGui.TableNextColumn();

                ImGui.BeginDisabled(env == null);

                if (ImGui.Button($"Load###aeOrderLoad{i}"))
                    planning.Adopt(Planner.Describe(env, tried.Chain), SiteBestChains.Site);

                ImGui.EndDisabled();
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The best orders on the minimap: the best order's chain in full with each remnant numbered by its place in the
    /// order, and the next ones' chains faded. Only while the section's switch is on.
    /// </summary>
    public static void Draw(Graphics graphics, GameController gc)
    {
        if (!DrawOnMinimap || _found.Count == 0 || _foundOn == null || !Minimap.Showing(gc))
            return;

        using var clip = graphics.MapSurfaceClip();

        // The explosives already down, from the detonator, so the orders drawn from the last of them read as the rest
        // of one chain rather than a chain starting nowhere.
        if (_foundOn.Placed is { Count: > 0 } placed && SiteBestChains.Site != Vector2.Zero)
        {
            var down = Color.FromArgb(200, 200, 200, 200);
            var was = SiteBestChains.Site;

            foreach (var at in placed)
            {
                graphics.DrawLineOnMap(was, at, 2f, down);
                was = at;
            }
        }

        for (var i = Math.Min(Drawn, _found.Count) - 1; i >= 0; i--)
        {
            var tried = _found[i];
            var colour = i == 0 ? Color.FromArgb(255, 80, 220, 255) : Color.FromArgb(110, 80, 220, 255);
            var from = _foundOn.Origin;

            foreach (var at in tried.Chain)
            {
                graphics.DrawLineOnMap(from, at, i == 0 ? 2f : 1f, colour);
                from = at;
            }

            if (i != 0)
                continue;

            for (var k = 0; k < tried.Order.Count; k++)
            {
                if (tried.Order[k] >= _foundOn.Targets.Count)
                    continue;

                var grid = _foundOn.Targets[tried.Order[k]].Grid;

                graphics.DrawCircleOnMap(grid, false, 4f, colour, 1.5f, 16);
                graphics.DrawText($"{k + 1}", graphics.GridToMap(grid, grid) + new Vector2(6f, -6f), colour);
            }
        }
    }
}
