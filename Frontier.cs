using ExileCore2;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Puts our placement model to the game, one spot at a time, by moving the cursor there and reading
/// what the indicator actually does.
///
/// **It tests the boundary, because that is where a model is wrong.** Deep inside the green the
/// answer is obvious and agreeing there proves nothing; the spots worth a probe are the green ones
/// with a red neighbour and the red ones next to them. That is a few hundred cells rather than the
/// twenty-five thousand in range.
///
/// **The comparison uses the game's own requested point, not the cell we aimed at.** The cursor
/// lands where it lands, and the client's PlacementIndicatorGridPosition says which cell it took
/// that to mean. Asking "given THAT request, did the explosive land where we predicted" removes
/// every bit of cursor-positioning error from the measurement and leaves only the model under test.
///
/// **Resumable on purpose.** A dig site is many screens wide, so this sweeps what is in front of the
/// player, stops when it runs out, and picks up where it left off after they walk. See Checked for
/// the memory that makes that work.
///
/// Stops the moment the player touches the mouse - ExileInput2 reports that through WasStopped, and
/// taking the cursor back is the only panic key this needs.
/// </summary>
internal sealed class Frontier
{
    private readonly ExileInput2Client _input;

    public Frontier(ExileInput2Client input) => _input = input;

    /// <summary>Whether a sweep is under way. The hotkey toggles it.</summary>
    public bool Running { get; private set; }

    /// <summary>What stopped the last sweep, for the status line.</summary>
    public string Says { get; private set; } = "";

    private Vector2 _aiming;

    /// <summary>The last spot tested, which is where the next one is measured from. See Next.</summary>
    private Vector2 _walked;
    private Vector2 _settled;
    private int _still;
    private int _done;
    private int _wrong;
    private readonly List<string> _log = new();

    /// <summary>Start, or stop and write out what was learnt.</summary>
    public void Toggle(GameController gc)
    {
        if (Running)
        {
            Finish(gc, "stopped by hand");

            return;
        }

        if (!_input.Available)
        {
            Says = "ExileInput2 is not installed, so the cursor cannot be moved";

            return;
        }

        Running = true;
        _aiming = Vector2.Zero;
        _walked = Vector2.Zero;
        _done = 0;
        _wrong = 0;
        _log.Clear();
        Says = "sweeping";
    }

    /// <summary>
    /// One step of the sweep, per frame.
    ///
    /// Deliberately one probe at a time and never in parallel: the thing being read is a single
    /// indicator that follows a single cursor, so there is nothing to overlap.
    /// </summary>
    public void Step(GameController gc, AutoExpeditionSettings settings)
    {
        if (!Running)
            return;

        if (!Detonator.Showing(gc))
        {
            Finish(gc, "the placement indicator is not up");

            return;
        }

        if (_input.WasStopped())
        {
            Finish(gc, "the cursor was taken back");

            return;
        }

        if (!_input.Take())
            return;

        // ---- waiting on the spot we last moved to -------------------------------------------
        if (_aiming != Vector2.Zero)
        {
            if (!_input.Arrived())
                return;

            // A frame with no reading is skipped: this routine exists to catch the indicator
            // disagreeing with the plan, and a missing one must not read as agreement.
            var landed = Detonator.PlacementIndicatorGridPosition(gc);

            if (landed == Vector2.Zero)
                return;

            // The indicator chases the cursor over a few frames, and reading it mid-flight is what
            // manufactured phantom disagreements before. Wait for it to hold still.
            if (landed != _settled)
            {
                _settled = landed;
                _still = 0;

                return;
            }

            if (++_still < Steady)
                return;

            Record(gc, settings, landed);
            _aiming = Vector2.Zero;

            return;
        }

        // ---- pick the next spot ---------------------------------------------------------------
        var next = Next(gc, settings);

        if (next == Vector2.Zero)
        {
            Finish(gc, "nothing left to test on this screen - walk and start again");

            return;
        }

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(next), Vector3.Zero);
        var on = camera == null || world == Vector3.Zero
            ? Vector2.Zero
            : Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        if (on == Vector2.Zero || !Onscreen(gc, on))
        {
            // Off the window after all - write it off rather than stall on it forever.
            Checked.Here.Note(next, true);

            return;
        }

        _aiming = next;
        _walked = next;
        _settled = Vector2.Zero;
        _still = 0;
        _input.MoveTo(on);
    }

    /// <summary>
    /// What the game did with the spot, against what we said it would do.
    ///
    /// **The question is "does a bomb land HERE", not "where does the bomb land".** Comparing
    /// landing coordinates looks equivalent and is not: when the game REFUSES a spot it leaves the
    /// indicator parked on the cursor and marks it invalid rather than clamping it anywhere, while
    /// our router reports no route by returning the explosive. Two answers that both mean "you
    /// cannot place here" then read as a disagreement, which is how a first run called 63 spots
    /// wrong where the game and the model in fact agreed on every one.
    ///
    /// So both sides are reduced to the boolean the overlay actually draws. The game's half is its
    /// own verdict - the indicator's art says pass or fail - together with the landing being the
    /// spot that was asked for, since a clamped landing elsewhere is not a bomb on this spot either.
    /// </summary>
    private void Record(GameController gc, AutoExpeditionSettings settings, Vector2 landed)
    {
        var asked = Detonator.RequestedGridPosition(gc);
        var head = Detonator.LastExplosiveGridPosition(gc);
        var reach = MathF.Max(1f, Detonator.PlacementRange(gc));
        var ground = Terrain.Read(gc);
        var slab = Peek.Coarse(gc);
        var apart = MathF.Max(1f, Safe.Read(() => settings.Debug.ApartAtLeast.Value, 20f));
        var bombs = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacedExplosiveGridPositions, null);

        var says = Detonator.PlaceableNow(gc);

        if (says == null || slab == null || ground == null)
            return;

        var onIt = (int)MathF.Round(landed.X) == (int)MathF.Round(asked.X) &&
                   (int)MathF.Round(landed.Y) == (int)MathF.Round(asked.Y);

        var game = says == true && onIt;
        var ours = Allows(slab, ground, head, bombs, asked, apart * apart, reach);
        var agreed = game == ours;

        Checked.Here.Note(_aiming, agreed);
        Checked.Here.Note(asked, agreed);

        _done++;

        if (!agreed)
            _wrong++;

        _log.Add($"asked ({asked.X:0},{asked.Y:0}) game {(game ? "places" : "refuses")} " +
                 $"(art {(says == true ? "pass" : "fail")}, landed ({landed.X:0},{landed.Y:0})) " +
                 $"ours {(ours ? "places" : "refuses")} " + (agreed ? "agree" : "DISAGREE"));
    }

    /// <summary>
    /// The next untested frontier spot: a placeable cell beside an unplaceable one, or the
    /// unplaceable one beside it.
    ///
    /// **Nearest to the LAST spot tested, not to the player.** The frontier is a ring, and picking
    /// whatever is closest to the player walks straight across it and back on every step - the
    /// cursor flies about and the run is impossible to watch. Stepping to the nearest untested
    /// neighbour instead traces the boundary the way a finger would, which is both shorter in total
    /// travel and legible while it happens. The player is only the starting point, for the first
    /// spot of a run.
    /// </summary>
    private Vector2 Next(GameController gc, AutoExpeditionSettings settings)
    {
        var slab = Peek.Coarse(gc);
        var ground = Terrain.Read(gc);

        if (slab == null || ground == null)
            return Vector2.Zero;

        var head = Detonator.LastExplosiveGridPosition(gc);
        var reach = MathF.Max(1f, Detonator.PlacementRange(gc));
        var apart = MathF.Max(1f, Safe.Read(() => settings.Debug.ApartAtLeast.Value, 20f));
        var bombs = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacedExplosiveGridPositions, null);
        var me = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        // Walk on from where the cursor already is; fall back to the player to open a run.
        var from = _walked == Vector2.Zero ? me : _walked;

        var best = Vector2.Zero;
        var closest = float.MaxValue;
        var edge = (int)(reach + 2f);

        for (var y = (int)head.Y - edge; y <= (int)head.Y + edge; y++)
        {
            for (var x = (int)head.X - edge; x <= (int)head.X + edge; x++)
            {
                var at = new Vector2(x, y);

                if (!Checked.Here.Wanted(at))
                    continue;

                // Reachable from the player, so it stays on screen, but ordered from the cursor.
                if (Vector2.DistanceSquared(me, at) > Sweep * Sweep)
                    continue;

                var near = Vector2.DistanceSquared(from, at);

                if (near >= closest)
                    continue;

                if (!Frontiered(slab, ground, head, bombs, at, apart * apart, reach))
                    continue;

                closest = near;
                best = at;
            }
        }

        return best;
    }

    /// <summary>Whether this spot sits on the boundary - it and a four-neighbour disagree.</summary>
    private static bool Frontiered(Peek.Slab slab, Terrain ground, Vector2 head,
        GameOffsets2.Native.Vector2i[] bombs, Vector2 at, float spacing, float reach)
    {
        var mine = Allows(slab, ground, head, bombs, at, spacing, reach);

        if (mine != Allows(slab, ground, head, bombs, at + new Vector2(1f, 0f), spacing, reach) ||
            mine != Allows(slab, ground, head, bombs, at - new Vector2(1f, 0f), spacing, reach) ||
            mine != Allows(slab, ground, head, bombs, at + new Vector2(0f, 1f), spacing, reach) ||
            mine != Allows(slab, ground, head, bombs, at - new Vector2(0f, 1f), spacing, reach))
        {
            return true;
        }

        // **Inside the green, on a lattice, because a wrong region has no edge of its own.** The
        // frontier test above only ever offers a spot whose verdict differs from a neighbour's, so
        // ground we are uniformly wrong about is never put to the game at all - it is green, its
        // neighbours are green, and nothing marks it. That is how a whole forbidden volume survived
        // 1,297 tested spots with nothing to show for it: the sweep was structurally unable to look
        // there.
        //
        // Only the greens, since a red spot we never place on costs nothing to be wrong about, and
        // only every Stride-th one, so the interior costs a fraction of the boundary rather than the
        // whole area. A volume big enough to matter is far wider than the stride.
        return mine && (int)at.X % Stride == 0 && (int)at.Y % Stride == 0;
    }

    /// <summary>
    /// How far apart the interior samples sit. Small enough to land inside any volume worth knowing
    /// about, large enough that the interior does not swamp the boundary.
    /// </summary>
    private const int Stride = 4;

    /// <summary>The placement rule, as the overlay draws it. See Forbidden.Placement.</summary>
    private static bool Allows(Peek.Slab slab, Terrain ground, Vector2 head,
        GameOffsets2.Native.Vector2i[] bombs, Vector2 at, float spacing, float reach)
    {
        if (!slab.Routable((int)at.X, (int)at.Y))
            return false;

        // The same authored refusal the overlay applies; see Forbidden.Allows and Volumes.
        if (ground != null && ground.Forbids(at))
            return false;

        if (bombs != null)
        {
            for (var i = 0; i < bombs.Length; i++)
                if (Vector2.DistanceSquared(new Vector2(bombs[i].X, bombs[i].Y), at) <= spacing)
                    return false;
        }

        // The same order the planner and the overlay take: a measured pair is the game stating the
        // answer, and only an unmeasured one falls through to the model. See Forbidden.Allows.
        return Vector2.DistanceSquared(head, at) <= (reach + 1f + Past) * (reach + 1f + Past) &&
               ground.Aiming(head, at, reach, out _);
    }

    private static bool Onscreen(GameController gc, Vector2 on)
    {
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default(RectangleF));

        return on.X > window.X + Margin && on.X < window.X + window.Width - Margin &&
               on.Y > window.Y + Margin && on.Y < window.Y + window.Height - Margin;
    }

    private void Finish(GameController gc, string why)
    {
        Running = false;
        _aiming = Vector2.Zero;
        _input.Release();

        Says = $"{why}: {_done} tested this run, {_wrong} disagreed " +
               $"({Checked.Here.Count} tested here in total, {Checked.Here.Wrong} disagreed)";

        if (_log.Count == 0)
            return;

        Safe.Try(() =>
        {
            var directory = Path.Combine(Where ?? ".", "dumps");

            Directory.CreateDirectory(directory);

            var b = new StringBuilder();

            b.AppendLine("# every spot this sweep put to the game, and what it said");
            b.AppendLine($"# {_done} tested, {_wrong} disagreed");
            b.AppendLine("# aim = where the cursor was sent, asked = the request the client took");
            b.AppendLine("# from it, game = where the explosive would go, ours = what we predicted");

            foreach (var line in _log)
                b.AppendLine(line);

            File.WriteAllText(
                Path.Combine(directory, $"frontier_{DateTime.Now:yyyyMMdd_HHmmss}.txt"),
                b.ToString());
        });
    }

    /// <summary>Where to write the log - the plugin's config directory, handed over on start.</summary>
    public static string Where { get; set; }

    /// <summary>
    /// How many still frames before the indicator is believed to have settled.
    ///
    /// Two, not one: one frame of stillness can be the indicator not having moved YET rather than
    /// having finished moving, and a reading taken then is the previous spot's answer.
    /// </summary>
    private const int Steady = 2;

    /// <summary>How far from the player to look for spots, in grid. Beyond this is off screen.</summary>
    private const float Sweep = 70f;

    /// <summary>How far past the reach a spot can still be worth asking about.</summary>
    private const float Past = 30f;

    private const float Margin = 40f;
}
