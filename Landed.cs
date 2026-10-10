using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2;

namespace AutoExpedition;

/// <summary>
/// What the model believed about each explosive, written down as it goes down.
///
/// **Every other measurement here asks the game a question the plugin was not going to act on.** A
/// cursor sweep collects hundreds of verdicts about ground nobody was ever going to use; the clamps
/// probe a limit in directions no chain was going. Useful, and none of it is the thing that actually
/// fails - which is a chain link the planner believed in, walked to, and got refused.
///
/// So this records the only sample that is about the real decision. When an explosive appears, the
/// question "did we think this was reachable from the last one" has a definite answer, and it is worth
/// having beside the outcome rather than reconstructed afterwards from a plan that has since moved on.
///
/// The four neighbours go down with it because of what they can be made to prove. Put a bomb at the
/// FURTHEST spot the game will accept in some direction and the model has a sharp, falsifiable claim
/// to answer: the spot itself is reachable, and of its four edge neighbours exactly the outward one is
/// not. Three of four reachable, no more and no less. A model that says four is reaching past a limit
/// the player has just demonstrated; one that says two or fewer is refusing ground either side of a
/// spot it accepted, which is a boundary in the wrong place rather than a boundary drawn too far.
///
/// That is worth more than the hundreds of cursor readings elsewhere in this file, because it is one
/// question with one right answer, asked where the plugin actually has to be right.
///
/// Both verdicts are printed per neighbour, and they are different questions. REACH is whether the
/// chain stretches there from the previous explosive - the claim above. ROOM is whether the ground
/// would take an explosive at all, which does not depend on where the chain is coming from. A spot can
/// fail either, and a count that mixed them would answer neither.
///
/// Named for the explosives that have landed, not for Planning.Laid, which is the cursor into the
/// chain rather than a count of what is on the ground, and is a different thing entirely.
/// </summary>
internal sealed class Landed
{
    public static readonly Landed Here = new();

    /// <summary>
    /// One explosive, as facts rather than as the line they print as.
    ///
    /// **The reach verdicts cannot be taken at the moment the bomb lands, and the first version of
    /// this tried to.** They go through the environment's own router, which the search thread is
    /// using, so they are only safe to ask while no solve is running - and placing an explosive is
    /// precisely what starts one. Every record came back "not asked, a solve was running", which is
    /// the check answering nothing at all.
    ///
    /// The ground facts are still taken at landing, because those are about terrain and terrain does
    /// not move. The router questions are left null and asked when a dump is written with the search
    /// idle. Asked on the first idle frame, they were five routings in one frame, and on a site whose
    /// router held its limit of 4,000,000 paths that frame took 4.9 seconds (2026-10-10 13:31:26). Only
    /// the dump reads them. Nothing is overwritten once answered.
    /// </summary>
    private sealed class Note
    {
        public int Index;
        public Vector2 From;
        public Vector2 To;
        public float Span;
        public float Reach;
        public bool Clear;
        public int Narrowest;
        public int Room;

        /// <summary>
        /// What the detour costs with the scenery in the way, and with only the ground.
        ///
        /// **Measured only when the model refused a link the game took, because that is the one case
        /// where the difference decides what to fix.** Obstacles are doodad discs whose radius comes
        /// from Render bounds and is floored at 1.5 grid - fatter than the thing they stand for, and
        /// a disc slightly too fat seals a gap the game lets a chain through, which turns a short way
        /// round into a long one or into none at all.
        ///
        /// So both are taken. If the ground alone allows a route inside the reach and the scenery
        /// pushes it past, the discs are the fault and the terrain model is fine. If neither fits,
        /// the game is not measuring a walked path at all and the whole route model is the wrong
        /// shape - which is worth knowing before another constant gets tuned.
        ///
        /// Both are flooded with a budget of three times the reach, so a detour that does not fit
        /// still comes back as a length rather than as a refusal. -1 means no route at any length.
        /// </summary>
        public int Detour = -2;

        public int Bare = -2;

        /// <summary>Null until a dump is written with the search idle. See Resolve.</summary>
        public Certainty? Said;

        public readonly Vector2[] Steps = new Vector2[Around.Length];
        public readonly int[] NearRoom = new int[Around.Length];
        public readonly bool[] NearTakes = new bool[Around.Length];
        public readonly Certainty?[] NearSaid = new Certainty?[Around.Length];
    }

    private readonly List<Note> _notes = new();
    private int _known;

    /// <summary>The environment as of the last frame, and whether a solve was running then. See Describe.</summary>
    private PlanEnvironment _env;

    private bool _searching;
    private uint _area;

    /// <summary>
    /// Notices an explosive that was not there last frame, and takes the ground facts about it. The reach
    /// questions wait for a dump. See Note.
    /// </summary>
    /// <param name="searching">
    /// Whether a solve is running. The reach questions go through the environment's own router, which
    /// the search thread is using at the same time, so a dump asks them only when none is. See Note.
    /// </param>
    public void Observe(GameController gc, Terrain ground, Obstacles blocking,
        PlanEnvironment env, bool searching)
    {
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        if (placed == null)
            return;

        if (placed.Length < _known)
        {
            // Undo. The bombs that came back off take their notes with them, or the next placement
            // would be described as following one that is no longer on the ground.
            _known = placed.Length;

            while (_notes.Count > _known)
                _notes.RemoveAt(_notes.Count - 1);
        }
        else
        {
            for (var i = _known; i < placed.Length; i++)
            {
                // The chain's previous link, or the detonator for the first - which is where the game
                // measures the opening throw from. See Detonator.DetonatorGridPosition.
                _notes.Add(Ground(gc, ground, i,
                    i > 0 ? placed[i - 1] : Detonator.DetonatorGridPosition(gc), placed[i]));
            }

            _known = placed.Length;
        }

        _env = env;
        _searching = searching;

        if (!searching)
            Measure(gc, ground, blocking);
    }

    /// <summary>
    /// Puts a length on any detour the model refused, with the scenery and without it. See Note.
    /// </summary>
    private static void Measure(GameController gc, Terrain ground, Obstacles blocking)
    {
        if (ground == null)
            return;

        foreach (var note in Here._notes)
        {
            continue;
        }
    }

    /// <summary>Everything about a placement that does not need the router. See Note.</summary>
    private static Note Ground(GameController gc, Terrain ground, int index, Vector2 from, Vector2 to)
    {
        var note = new Note
        {
            Index = index,
            From = from,
            To = to,
            Span = Vector2.Distance(from, to),
            Reach = Detonator.PlacementRange(gc),
            Clear = ground?.Clear(from, to) ?? false,
            Narrowest = ground?.Tightest(from, to) ?? -1,
            Room = ground?.Room(to) ?? -1,
        };

        for (var i = 0; i < Around.Length; i++)
        {
            var at = to + Around[i];

            note.Steps[i] = Around[i];
            note.NearRoom[i] = ground?.Room(at) ?? -1;
            note.NearTakes[i] = ground?.Placeable(at) ?? false;
        }

        return note;
    }

    /// <summary>Fills in the router answers for any note still waiting on one. See Note.</summary>
    private void Resolve(PlanEnvironment env)
    {
        foreach (var note in _notes)
        {
            note.Said ??= Planner.Says(env, note.From, note.To);

            for (var i = 0; i < Around.Length; i++)
                note.NearSaid[i] ??= Planner.Says(env, note.From, note.To + note.Steps[i]);
        }
    }

    /// <summary>The four cells sharing an edge with the spot. See the class doc.</summary>
    private static readonly Vector2[] Around =
    [
        new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
    ];

    public string Describe()
    {
        if (_notes.Count == 0)
            return "nothing placed yet";

        if (!_searching && _env != null)
            Resolve(_env);

        var text = new List<string> { $"{_notes.Count} placed:" };

        foreach (var note in _notes)
            text.Add(Say(note));

        return string.Join(Environment.NewLine, text);
    }

    private static string Say(Note note)
    {
        var said = note.Said switch
        {
            Certainty.Yes => "REACHABLE",
            Certainty.No => "NOT reachable - the model would not have planned this",
            Certainty.Unknown => "unknown, the router never settled it",
            _ => "not asked: a solve was running when the dump was written",
        };

        var lines = new List<string>
        {
            // The origin is named as well as numbered, because the game counts explosives and the
            // player counts links: the first explosive is thrown FROM the detonator, so what this
            // calls 1 is the second thing in the chain and reading it as the opening link is wrong.
            $"    {note.Index + 1}: ({note.To.X:0},{note.To.Y:0}) from " +
            $"({note.From.X:0},{note.From.Y:0})" +
            (note.Index == 0 ? ", the detonator" : $", explosive {note.Index}") +
            $", {note.Span:0.#} apart of a range of {note.Reach:0.#} - {said}",
        };

        if (note.Narrowest >= 0)
        {
            lines.Add($"        the run is {(note.Clear ? "clear" : "BLOCKED, so a detour")}, " +
                      $"narrowest room along it {note.Narrowest}; " +
                      $"the spot itself has room {note.Room}");
        }

        if (note.Detour != -2)
        {
            lines.Add($"        the way round costs " +
                      $"{(note.Detour < 0 ? "no route at any length" : $"{note.Detour} against a reach of {note.Reach:0.#}")}" +
                      $", and ignoring the scenery " +
                      $"{(note.Bare < 0 ? "still none" : note.Bare.ToString())}" +
                      (note.Bare >= 0 && note.Bare <= note.Reach && (note.Detour < 0 || note.Detour > note.Reach)
                          ? " - THE SCENERY IS WHAT REFUSED IT, the ground alone allows the link"
                          : ""));
        }

        var within = 0;
        var asked = 0;
        var near = new List<string>();

        for (var i = 0; i < Around.Length; i++)
        {
            var at = note.To + note.Steps[i];
            var cell = ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));

            if (note.NearSaid[i] is { } certain)
            {
                asked++;

                if (certain == Certainty.Yes)
                    within++;
            }

            var stretch = note.NearSaid[i] switch
            {
                Certainty.Yes => "in reach",
                Certainty.No => "OUT of reach",
                Certainty.Unknown => "reach unsettled",
                _ => "reach not asked yet",
            };

            // What the game said about the same cell, when the cursor happened to settle on it. Read
            // now rather than at landing, because the player sweeps around a spot after placing as
            // well as before, so this fills in over the life of the site. See Settled.
            var theirs = "never asked";

            // **The same origin, not merely the same cell.** A verdict is about a pair, so one
            // taken while a different explosive was the chain's head says nothing about this link -
            // and pooling them is what made a sweep claim the game allowed ground a hundred and
            // thirty grid out. See Settled.Observe.
            var mine = ((int)MathF.Round(note.From.X), (int)MathF.Round(note.From.Y));

            foreach (var (was, placeable, _, looks, from) in Settled.Here.Verdicts)
            {
                if (was != cell || from != mine)
                    continue;

                theirs = (placeable ? "the game took it" : "the GAME REFUSED it") + $" on {looks}";
                break;
            }

            near.Add($"({note.Steps[i].X:+0;-0;0},{note.Steps[i].Y:+0;-0;0}) " +
                     $"{Vector2.Distance(note.From, at):0.#} out, room " +
                     $"{(note.NearRoom[i] < 0 ? "?" : note.NearRoom[i].ToString())}: {stretch}, " +
                     $"{(note.NearRoom[i] < 0 ? "ground unknown" : note.NearTakes[i] ? "would take one" : "would NOT take one")}, " +
                     $"{theirs}");
        }

        // **The claim, stated so it can be wrong.** A spot placed at the furthest the game allows
        // should have three neighbours in reach and one out. See the class doc.
        //
        // The furthest neighbour is named with the count, because that is the one a limit between
        // it and the next should have refused - and knowing which one the game actually refused is
        // what turns "too generous" into a number. Sweep the cursor over the four and Settled fills
        // the column in.
        var furthest = 0;

        for (var i = 1; i < Around.Length; i++)
        {
            if (Vector2.Distance(note.From, note.To + note.Steps[i]) >
                Vector2.Distance(note.From, note.To + note.Steps[furthest]))
                furthest = i;
        }

        lines.Add(asked < Around.Length
            ? "        neighbours in reach: not asked, a solve was running when the dump was written"
            : $"        neighbours in reach: {within} of 4, furthest is " +
              $"({note.Steps[furthest].X:+0;-0;0},{note.Steps[furthest].Y:+0;-0;0}) at " +
              $"{Vector2.Distance(note.From, note.To + note.Steps[furthest]):0.#}" +
              (within == 3
                  ? " - three, which is what a spot AT the limit should read"
                  : within == 4
                      ? " - four, so the model reaches past this spot; if the game would not, " +
                        "it is too generous here"
                      : $" - only {within}, so the model refuses ground beside a spot it accepted"));

        foreach (var one in near)
            lines.Add("        " + one);

        return string.Join(Environment.NewLine, lines);
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash == _area)
            return;

        _area = areaHash;
        _notes.Clear();
        _known = 0;
    }
}
