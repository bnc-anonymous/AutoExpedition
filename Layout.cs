using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// Saves a dig site to a file and loads it back, so a search can be run and re-run without a game.
///
/// **The terrain is captured as answers, not as ground.** The search asks two questions about the world -
/// whether an explosive may sit on a cell, and whether a wire aimed from one cell to another lands there - and
/// both are delegates over the game's own routing. Recording what they answered, for the pairs actually asked,
/// reproduces the site exactly for any run that asks the same pairs, and needs no terrain model.
///
/// **Only two delegates, because Planner.Says consults only two.** It rules a pair out on the straight line,
/// then on Lands, and returns Yes without reaching CanReach whenever Lands is present - Planning builds
/// CanReach out of the same routing, so the second question is the first one again. A loaded site therefore
/// leaves CanReach null, which is not a gap.
///
/// **An answer that was never recorded is refused, and counted.** A snapshot cannot invent ground nobody
/// walked, so a missing cell is unplaceable and a missing pair does not land. The count is reported by Said, so
/// a run whose openings are wrong because the recording missed something says so rather than looking like a
/// result. A recording pass that runs the same search over the same site misses nothing, because the candidate
/// cells are rounded to whole grid steps and are generated from the content.
/// </summary>
internal static class Layout
{
    /// <summary>
    /// Where snapshots are written, set once at load because the panel that offers the button has no plugin
    /// reference and the host's folder layout is the plugin's business alone. See AutoExpedition.Initialise
    /// and Wrt.Home, which is set the same way for the same reason.
    /// </summary>
    internal static string Folder { get; set; } = "";

    /// <summary>What the last save or load said, for the panel to show.</summary>
    internal static string Told { get; private set; } = "no layout saved or loaded this session";

    /// <summary>
    /// Records a site and writes it, returning what to tell the player.
    ///
    /// **The recording pass runs the search it is a snapshot of**, because what has to be in the file is the
    /// answers that search asks for. Saving without running one would write a file whose every question is
    /// unanswered, which loads as a site where nothing may be placed.
    /// </summary>
    internal static string SaveWithOpenings(PlanEnvironment env, int levels, int want, int horizon)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var watched = Recording(env, out var answers);

            // **Every first and second link on the site, before any particular search asks for one.**
            // Recording only what one run asks makes the file answer that run and no other, so every change to
            // the enumerator - a tie-break that prefers a different spot, an extra anchor - explores cells the
            // file refuses, and a refusal reads as a low score rather than as a gap. Sweeping the first two
            // links covers any opening rule that could be written, and costs a second or so at the button.
            Everywhere(watched);

            // Then the search itself, for the deeper links no sweep can cover: pairs from a third link onwards
            // depend on which second link was taken, and there are too many of those to enumerate.
            //
            // **Three links and a five-link horizon, because deeper costs seconds and buys nothing.** Measured
            // on the Basin snapshot: five links kept twenty-seven ways and scored to fifteen takes 18.4
            // SECONDS, against 0.9 for this, and the button runs on the frame the player is looking at. The
            // shallow end is also all that is wanted - two good links is enough for the search to find its own
            // way from there - so the depth this records is the depth worth recording.
            Openings.Generate(watched, Math.Max(levels, 3), Math.Max(want, 12), Math.Max(horizon, 5));

            var named = $"layout_{DateTime.Now:yyyyMMdd_HHmmss}.aelayout";
            var path = Save(watched, answers, Folder, named);
            var size = new FileInfo(path).Length / 1024d / 1024d;

            // How long it took, because this is the one button that can cost a second of frame time and a
            // player who cannot see the cost cannot tell a slow site from a stuck one.
            return Told = string.Create(CultureInfo.InvariantCulture,
                $"saved {named} in {clock.ElapsedMilliseconds:N0} ms, {size:0.#} MB: {Said(watched, answers)}");
        }
        catch (Exception e)
        {
            return Told = $"could not save: {e.Message}";
        }
    }

    /// <summary>
    /// Asks the terrain about every placement and every first-to-second link, so the file answers more than the
    /// run that recorded it.
    ///
    /// Three sweeps. Whether each candidate may hold an explosive; whether the detonator can reach each one;
    /// and, from each one it can reach, whether every other candidate is reachable in turn. The third is the
    /// expensive one and is bounded by the reach - Planner.Says rules a pair out on the straight line before
    /// routing anything, so the pairs that cost are only those that could be links.
    ///
    /// The answers go into the recording by being asked for, which is the whole mechanism: nothing here reads
    /// what came back.
    /// </summary>
    private static void Everywhere(PlanEnvironment env)
    {
        var candidates = Planner.Candidates(env, out _, out _);

        // **Every whole cell the first explosive could sit on, not only the ones the generator proposes.**
        // Recording the generator's own candidates makes the file able to answer questions about the generator
        // that recorded it, and no others - so a change to where candidates come from cannot be tested against
        // it. The first link is small enough to cover exhaustively: the reach is a radius, the lattice is whole
        // numbers, and a cell beyond the reach is rejected on the straight line without routing anything.
        var span = (int)MathF.Ceiling(env.Reach) + 1;

        for (var dx = -span; dx <= span; dx++)
        {
            for (var dy = -span; dy <= span; dy++)
            {
                var at = new Vector2(MathF.Round(env.Origin.X) + dx, MathF.Round(env.Origin.Y) + dy);

                if (Planner.Span(env.Origin, at) > env.Reach)
                    continue;

                if (env.CanPlace(at))
                    Planner.Says(env, env.Origin, at);
            }
        }

        var first = new List<Vector2>();

        foreach (var at in candidates)
        {
            if (!env.CanPlace(at))
                continue;

            if (Planner.Says(env, env.Origin, at) == Certainty.Yes)
                first.Add(at);
        }

        foreach (var from in first)
        {
            foreach (var to in candidates)
                Planner.Says(env, from, to);
        }
    }

    /// <summary>What the file starts with, so a format change is a refusal to load rather than a wrong site.</summary>
    private const string Magic = "AutoExpeditionLayout2";

    /// <summary>
    /// The answers the game gave, keyed by what was asked.
    ///
    /// Held beside the environment rather than inside it, because an environment is a record the search copies
    /// with `with` all through a solve, and every copy has to share one record of what was asked.
    /// </summary>
    internal sealed class Answers
    {
        public readonly Dictionary<(float X, float Y), bool> Placeable = [];

        public readonly Dictionary<(float FromX, float FromY, float ToX, float ToY), bool> Landing = [];

        /// <summary>How many questions were asked that the file did not answer. Nought is the only good value.</summary>
        public int Missed;

        /// <summary>
        /// Whether an unrecorded answer is treated as passable instead of refused.
        ///
        /// **A diagnostic, never a result.** Refusing is the honest default: a snapshot must not invent ground
        /// nobody walked. But when a run explores past what the file covers, every refusal reads as a low
        /// score, and it is then impossible to tell a solver that made a bad choice from a file that did not
        /// answer. Setting this and comparing the two runs separates them. What comes out is a run on ground
        /// that may not exist, so it says which of the two is worth investigating and nothing else.
        /// </summary>
        public bool Assume;
    }

    /// <summary>
    /// The same environment, with its two terrain delegates writing down every answer they give.
    ///
    /// Wrapping rather than replacing: the game still answers, so a recording run behaves exactly as an
    /// ordinary one and the recording cannot change what it records. Run whatever search the snapshot is for
    /// against the returned environment, then save.
    /// </summary>
    internal static PlanEnvironment Recording(PlanEnvironment env, out Answers answers)
    {
        var said = new Answers();
        var canPlace = env.CanPlace;
        var lands = env.Lands;

        answers = said;

        return env with
        {
            CanPlace = at =>
            {
                var yes = canPlace == null || canPlace(at);

                said.Placeable[(at.X, at.Y)] = yes;

                return yes;
            },
            Lands = lands == null
                ? null
                : (from, to) =>
                {
                    var yes = lands(from, to);

                    said.Landing[(from.X, from.Y, to.X, to.Y)] = yes;

                    return yes;
                },
        };
    }

    /// <summary>
    /// Writes the site to a file and returns the path.
    ///
    /// The content and the geometry, plus the recorded answers. Not the solver's settings: an offline run is
    /// for varying those, and a file carrying them would quietly decide the experiment it is the subject of.
    /// </summary>
    internal static string Save(PlanEnvironment env, Answers answers, string folder, string named)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, named);

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        w.Write(Magic);
        w.Write(env.Origin.X);
        w.Write(env.Origin.Y);
        w.Write(env.Reach);
        w.Write(env.Blast);
        w.Write(env.Explosives);
        w.Write(env.Apart);
        w.Write(env.Musts);
        w.Write(env.Refused);
        w.Write(env.Banked ?? "");
        w.Write(env.GroupCount);

        Numbers(w, env.Bands);

        w.Write(env.Effects?.Count ?? -1);

        foreach (var (name, number) in env.Effects ?? [])
        {
            w.Write(name ?? "");
            w.Write(number);
        }

        w.Write(env.Placed?.Count ?? -1);

        foreach (var at in env.Placed ?? [])
        {
            w.Write(at.X);
            w.Write(at.Y);
        }

        w.Write(env.Secured?.Length ?? -1);

        foreach (var (id, tag, percent, group, from) in env.Secured ?? [])
        {
            w.Write(id ?? "");
            w.Write(tag);
            w.Write(percent);
            w.Write(group);
            w.Write(from.X);
            w.Write(from.Y);
        }

        w.Write(env.Targets?.Count ?? -1);

        foreach (var target in env.Targets ?? [])
            Wrote(w, target);

        w.Write(answers.Placeable.Count);

        foreach (var ((x, y), yes) in answers.Placeable)
        {
            w.Write(x);
            w.Write(y);
            w.Write(yes);
        }

        w.Write(answers.Landing.Count);

        foreach (var ((fx, fy, tx, ty), yes) in answers.Landing)
        {
            w.Write(fx);
            w.Write(fy);
            w.Write(tx);
            w.Write(ty);
            w.Write(yes);
        }

        return path;
    }

    /// <summary>
    /// Reads a site back, with terrain delegates that answer from the file.
    ///
    /// The solver knobs are left at their defaults, so a caller says what it is testing with `with`.
    /// </summary>
    internal static (PlanEnvironment Env, Answers Answers) Load(string path)
    {
        using var file = File.OpenRead(path);
        using var r = new BinaryReader(file);

        var magic = r.ReadString();

        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            throw new InvalidDataException($"not an AutoExpedition layout, or a different version: \"{magic}\"");

        var origin = new Vector2(r.ReadSingle(), r.ReadSingle());
        var reach = r.ReadSingle();
        var blast = r.ReadSingle();
        var explosives = r.ReadInt32();
        var apart = r.ReadSingle();
        var musts = r.ReadInt32();
        var refused = r.ReadDouble();
        var banked = r.ReadString();
        var groups = r.ReadInt32();
        var bands = Numbers(r);
        var effectCount = r.ReadInt32();
        var effects = effectCount < 0 ? null : new Dictionary<string, int>(effectCount);

        for (var i = 0; i < effectCount; i++)
            effects[r.ReadString()] = r.ReadInt32();

        var placedCount = r.ReadInt32();
        var placed = placedCount < 0 ? null : new List<Vector2>(placedCount);

        for (var i = 0; i < placedCount; i++)
            placed.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));

        var securedCount = r.ReadInt32();
        var secured = securedCount < 0
            ? null
            : new (string, int, float, int, (int, int))[securedCount];

        for (var i = 0; i < securedCount; i++)
            secured[i] = (r.ReadString(), r.ReadInt32(), r.ReadSingle(), r.ReadInt32(),
                (r.ReadInt32(), r.ReadInt32()));

        var targetCount = r.ReadInt32();
        var targets = targetCount < 0 ? null : new List<PlanTarget>(targetCount);

        for (var i = 0; i < targetCount; i++)
            targets.Add(Read(r));

        var answers = new Answers();
        var placeable = r.ReadInt32();

        for (var i = 0; i < placeable; i++)
            answers.Placeable[(r.ReadSingle(), r.ReadSingle())] = r.ReadBoolean();

        var landing = r.ReadInt32();

        for (var i = 0; i < landing; i++)
            answers.Landing[(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle())] =
                r.ReadBoolean();

        // **Sets are indices into the target list**, so one past its end is a crash deep inside the scorer
        // rather than a bad file. Checked here, where the file can still be named.
        for (var i = 0; i < (targets?.Count ?? 0); i++)
        {
            foreach (var also in targets[i].Sets ?? [])
            {
                if (also < 0 || also >= targets.Count)
                    throw new InvalidDataException(
                        $"marker {i} sets off marker {also}, and there are {targets.Count}");
            }
        }

        var env = new PlanEnvironment(
            origin,
            reach,
            blast,
            explosives,
            targets ?? [],
            at =>
            {
                if (answers.Placeable.TryGetValue((at.X, at.Y), out var yes))
                    return yes;

                answers.Missed++;

                return answers.Assume;
            })
        {
            // Left null deliberately: Planner.Says never asks it while Lands is present, and Planning
            // derives one from the other. See the class comment.
            CanReach = null,
            Lands = (from, to) =>
            {
                if (answers.Landing.TryGetValue((from.X, from.Y, to.X, to.Y), out var yes))
                    return yes;

                answers.Missed++;

                return answers.Assume;
            },
            Apart = apart,
            Placed = placed,
            Secured = secured,
            Banked = banked,
            Musts = musts,
            Refused = refused,
            Effects = effects,
            Bands = bands,
            GroupCount = Math.Max(1, groups),
        };

        return (env, answers);
    }

    /// <summary>What a loaded site is, and whether it answered everything it was asked.</summary>
    internal static string Said(PlanEnvironment env, Answers answers) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{env.Targets.Count} marker(s), {env.Explosives} explosive(s), blast {env.Blast:0.#}, " +
            $"reach {env.Reach:0.#}, from ({env.Origin.X:0},{env.Origin.Y:0}); " +
            $"{answers.Placeable.Count:N0} cell(s) and {answers.Landing.Count:N0} aim(s) recorded, " +
            $"{answers.Missed:N0} unanswered");

    private static void Wrote(BinaryWriter w, PlanTarget target)
    {
        w.Write(target.Grid.X);
        w.Write(target.Grid.Y);
        w.Write(target.Radius);
        w.Write(target.Weight);
        w.Write((int)target.Kind);
        w.Write(target.Carries);

        Worths(w, target.Runes);

        w.Write(target.Waves);

        Worths(w, target.PropagatingRuneWeights);

        w.Write(target.Choices?.Length ?? -1);

        foreach (var choice in target.Choices ?? [])
        {
            w.Write(choice.Reward);
            w.Write(choice.Carries);
            w.Write(choice.Local);

            Worths(w, choice.Locals);
            Names(w, choice.Runes);
            Spreads(w, choice.Spread);
        }

        w.Write(target.Once ?? "");

        w.Write(target.NonStacking?.Length ?? -1);

        foreach (var (id, tag, percent, weight) in target.NonStacking ?? [])
        {
            w.Write(id ?? "");
            w.Write(tag);
            w.Write(percent);
            w.Write(weight);
        }

        Numbers(w, target.Sets);

        w.Write(target.Must);
        w.Write(target.Mask);

        Spreads(w, target.Spread);

        w.Write(target.Group);

        w.Write(target.Parts?.Length ?? -1);

        foreach (var (mask, worth, many) in target.Parts ?? [])
        {
            w.Write(mask);
            w.Write(worth);
            w.Write(many);
        }

        Names(w, target.Named);
        Names(w, target.Recipes);
    }

    private static PlanTarget Read(BinaryReader r)
    {
        var grid = new Vector2(r.ReadSingle(), r.ReadSingle());
        var radius = r.ReadSingle();
        var weight = r.ReadSingle();
        var kind = (TargetKind)r.ReadInt32();
        var carries = r.ReadSingle();
        var runes = Worths(r);
        var waves = r.ReadSingle();
        var propagating = Worths(r);
        var choiceCount = r.ReadInt32();
        var choices = choiceCount < 0
            ? null
            : new (float, float, float, (string, float)[], string[], (string, int, float, bool)[])[choiceCount];

        for (var i = 0; i < choiceCount; i++)
            choices[i] = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), Worths(r), Names(r), Spreads(r));

        var once = r.ReadString();
        var nonStackingCount = r.ReadInt32();
        var nonStacking = nonStackingCount < 0
            ? null
            : new (string, int, float, float)[nonStackingCount];

        for (var i = 0; i < nonStackingCount; i++)
            nonStacking[i] = (r.ReadString(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle());

        var sets = Numbers(r);
        var must = r.ReadBoolean();
        var mask = r.ReadInt64();
        var spread = Spreads(r);
        var group = r.ReadInt32();
        var partCount = r.ReadInt32();
        var parts = partCount < 0 ? null : new (long, float, float)[partCount];

        for (var i = 0; i < partCount; i++)
            parts[i] = (r.ReadInt64(), r.ReadSingle(), r.ReadSingle());

        var named = Names(r);
        var recipes = Names(r);

        // Named rather than positional, because this record's parameters are matched by position and a
        // field added to the middle of it would otherwise rebind everything after it silently.
        return new PlanTarget(grid, radius, weight, kind,
            Carries: carries,
            Runes: runes,
            Waves: waves,
            PropagatingRuneWeights: propagating,
            Choices: choices,
            Once: once,
            NonStacking: nonStacking,
            Sets: sets,
            Must: must,
            Mask: mask,
            Spread: spread,
            Group: group,
            Parts: parts,
            Named: named,
            Recipes: recipes);
    }

    private static void Worths(BinaryWriter w, (string Id, float Weight)[] these)
    {
        w.Write(these?.Length ?? -1);

        foreach (var (id, weight) in these ?? [])
        {
            w.Write(id ?? "");
            w.Write(weight);
        }
    }

    private static (string, float)[] Worths(BinaryReader r)
    {
        var count = r.ReadInt32();

        if (count < 0)
            return null;

        var these = new (string, float)[count];

        for (var i = 0; i < count; i++)
            these[i] = (r.ReadString(), r.ReadSingle());

        return these;
    }

    private static void Spreads(BinaryWriter w, (string Id, int Tag, float Percent, bool Flat)[] these)
    {
        w.Write(these?.Length ?? -1);

        foreach (var (id, tag, percent, flat) in these ?? [])
        {
            w.Write(id ?? "");
            w.Write(tag);
            w.Write(percent);
            w.Write(flat);
        }
    }

    private static (string, int, float, bool)[] Spreads(BinaryReader r)
    {
        var count = r.ReadInt32();

        if (count < 0)
            return null;

        var these = new (string, int, float, bool)[count];

        for (var i = 0; i < count; i++)
            these[i] = (r.ReadString(), r.ReadInt32(), r.ReadSingle(), r.ReadBoolean());

        return these;
    }

    private static void Names(BinaryWriter w, string[] these)
    {
        w.Write(these?.Length ?? -1);

        foreach (var name in these ?? [])
            w.Write(name ?? "");
    }

    private static string[] Names(BinaryReader r)
    {
        var count = r.ReadInt32();

        if (count < 0)
            return null;

        var these = new string[count];

        for (var i = 0; i < count; i++)
            these[i] = r.ReadString();

        return these;
    }

    private static void Numbers(BinaryWriter w, int[] these)
    {
        w.Write(these?.Length ?? -1);

        foreach (var number in these ?? [])
            w.Write(number);
    }

    private static int[] Numbers(BinaryReader r)
    {
        var count = r.ReadInt32();

        if (count < 0)
            return null;

        var these = new int[count];

        for (var i = 0; i < count; i++)
            these[i] = r.ReadInt32();

        return these;
    }
}
