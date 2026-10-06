using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

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

    /// <summary>Says a snapshot is being written, for the panel while the save runs off the frame.</summary>
    internal static void Saving() => Told = "saving - asking the terrain about every routed pair and cell...";

    /// <summary>
    /// Records a site and writes it, returning what to tell the player.
    ///
    /// **The recording pass runs the search it is a snapshot of**, because what has to be in the file is the
    /// answers that search asks for. Saving without running one would write a file whose every question is
    /// unanswered, which loads as a site where nothing may be placed.
    /// </summary>
    internal static string SaveWithOpenings(PlanEnvironment env, int levels, int want, int horizon, string area = "",
        string mapStats = "", List<(double Scored, List<Vector2> Route)> known = null, string notes = "",
        Dictionary<Vector2, double> seenAtMs = null)
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

            // **And everything the game's own solver has asked on this site.** An offline search asks far more than
            // the sweeps above - one press on Frigid Bluffs asked 518,350 distinct throws and 71,697 cells the file
            // could not answer, against about 70,000 and 44,000 recorded - and every unanswered one is a guess that
            // can make an offline chain one the game would refuse. The router keeps the routed length of every pair
            // it has been asked across solves, so replaying them costs a lookup each. See Wire.KnownPairs.
            var replayed = 0;

            foreach (var (fx, fy, tx, ty) in Terrain.KeptRouter?.KnownPairs ?? [])
            {
                watched.Lands?.Invoke(new Vector2(fx, fy), new Vector2(tx, ty));
                replayed++;
            }

            var placed = PlacementsOfArea(watched);

            // The map in the name, so a folder of snapshots says which site each one is.
            var map = new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(area ?? "", char.IsLetterOrDigit)));
            var named = map.Length > 0
                ? $"layout_{DateTime.Now:yyyyMMdd_HHmmss}_{map}.aelayout"
                : $"layout_{DateTime.Now:yyyyMMdd_HHmmss}.aelayout";
            answers.KnownChains = known ?? [];

            // Scored on the environment the game is solving with, not the recording: the same figure as the line under
            // the plan, now. NaN for a chain that could not be scored.
            foreach (var (_, route) in answers.KnownChains)
                answers.KnownChainsScoredAtSave.Add(Safe.Read(() => Planner.Plainly(env, route), double.NaN));
            answers.Notes = notes ?? "";

            answers.SeenAtMs = seenAtMs;

            var path = Save(watched, answers, Folder, named, area, mapStats);
            var size = new FileInfo(path).Length / 1024d / 1024d;

            // How long it took, because this is the one button that can cost a second of frame time and a
            // player who cannot see the cost cannot tell a slow site from a stuck one.
            return Told = string.Create(CultureInfo.InvariantCulture,
                $"saved {named} in {clock.ElapsedMilliseconds:N0} ms, {size:0.#} MB, {replayed:N0} routed pairs and " +
                $"{placed:N0} cells asked: {Said(watched, answers)}");
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
    /// <summary>
    /// Asks whether an explosive may go on every cell of the site's area - every target's cell and the reach around it -
    /// so an offline search's placement questions are answered rather than guessed. Capped at three million cells; past
    /// that only the cells within reach of a target are asked. Returns how many were asked.
    /// </summary>
    private static int PlacementsOfArea(PlanEnvironment env)
    {
        if (env.CanPlace == null || env.Targets is not { Count: > 0 } targets)
            return 0;

        var margin = env.Reach + 1f;
        var (left, top, right, bottom) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);

        foreach (var t in targets)
        {
            left = MathF.Min(left, t.Grid.X - margin);
            top = MathF.Min(top, t.Grid.Y - margin);
            right = MathF.Max(right, t.Grid.X + margin);
            bottom = MathF.Max(bottom, t.Grid.Y + margin);
        }

        var whole = (right - left + 1f) * (bottom - top + 1f) <= 3_000_000f;
        var asked = 0;

        for (var y = MathF.Floor(top); y <= bottom; y++)
        {
            for (var x = MathF.Floor(left); x <= right; x++)
            {
                var at = new Vector2(x, y);

                if (!whole && !NearAnyTarget(targets, at, margin))
                    continue;

                env.CanPlace(at);
                asked++;
            }
        }

        return asked;
    }

    /// <summary>Whether a cell is within the given distance of any target. See PlacementsOfArea.</summary>
    private static bool NearAnyTarget(IReadOnlyList<PlanTarget> targets, Vector2 at, float within)
    {
        foreach (var t in targets)
        {
            if (Vector2.DistanceSquared(t.Grid, at) <= within * within)
                return true;
        }

        return false;
    }

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
    /// Marks the optional trailer after the recorded answers: the map's name, the environment's Gaining Traction fields,
    /// the map's increase to each group's stat and the map's modifiers as text. A file without it is an older one and
    /// loads as before. <see cref="ExtrasMagicFirst"/> files carry only the name and traction. See Save.
    /// </summary>
    private const string ExtrasMagic = "AutoExpeditionLayoutExtras12";

    /// <summary>The eleventh trailer, without each remnant's Gaining Traction table. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicEleventh = "AutoExpeditionLayoutExtras11";

    /// <summary>The tenth trailer, without the map's rare and magic increases. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicTenth = "AutoExpeditionLayoutExtras10";

    /// <summary>The ninth trailer, without each created entry's plain or empowered marking. Still read. See
    /// ExtrasMagic.</summary>
    private const string ExtrasMagicNinth = "AutoExpeditionLayoutExtras9";

    /// <summary>The eighth trailer, with "per" effects among the own effects rather than apart. Still read. See
    /// ExtrasMagic.</summary>
    private const string ExtrasMagicEighth = "AutoExpeditionLayoutExtras8";

    /// <summary>The seventh trailer, without each own effect's counted tag. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicSeventh = "AutoExpeditionLayoutExtras7";

    /// <summary>The sixth trailer, without each own effect's share of the waves. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicSixth = "AutoExpeditionLayoutExtras6";

    /// <summary>
    /// Marks the optional section after the trailer that holds the ground Terrain.Aiming answers from: the pathfinding
    /// grid, the coarse routing grid and the clamp settings, compressed.
    ///
    /// **A snapshot answered reach only from what had been asked before it was saved, and refused everything else.**
    /// On a Frigid Bluffs site (2026-10-04) that refused two links of a hand-placed chain the game had accepted and
    /// the model in game routed, so offline could not find the 74,000 chain the game reached, and the offline ground
    /// was larger or smaller with how much had been asked before each save - 761,465 recorded aims in one snapshot,
    /// 1,708,615 in another of the same site. With this section a pair the file does not answer is routed as the
    /// game would route it. See Load and Terrain.FromAimingInputs.
    /// </summary>
    private const string GroundMagic = "AutoExpeditionLayoutGround1";

    /// <summary>
    /// Marks the optional section holding, for each target in order, how many milliseconds after the site was first
    /// seen the scan first saw it, or -1 where that is not known.
    ///
    /// **So a site being scouted can be replayed offline.** The presolve's faults on arrival - passes cut by every
    /// marker, an order search that never finished - happen only while markers are still arriving, and a snapshot is
    /// the site at one moment: checking each change took another scouting run in game. With the order the markers
    /// came in, the harness can reveal them as they came. See Answers.SeenAtMsOfTarget.
    /// </summary>
    private const string SeenMagic = "AutoExpeditionLayoutSeen1";

    /// <summary>The fifth trailer, without the known chains' scores at save time. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicFifth = "AutoExpeditionLayoutExtras5";

    /// <summary>The fourth trailer, without each remnant's propagation inputs. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicFourth = "AutoExpeditionLayoutExtras4";

    /// <summary>The third trailer, without the notes. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicThird = "AutoExpeditionLayoutExtras3";

    /// <summary>The first trailer, with the name and Gaining Traction only. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicFirst = "AutoExpeditionLayoutExtras1";

    /// <summary>The second trailer, without the known chains. Still read. See ExtrasMagic.</summary>
    private const string ExtrasMagicSecond = "AutoExpeditionLayoutExtras2";

    /// <summary>
    /// The answers the game gave, keyed by what was asked.
    ///
    /// Held beside the environment rather than inside it, because an environment is a record the search copies
    /// with `with` all through a solve, and every copy has to share one record of what was asked.
    /// </summary>
    internal sealed class Answers
    {
        /// <summary>The map the site was saved on, or empty for a file saved before the name was kept.</summary>
        public string Area = "";

        /// <summary>Unanswered placement questions, counted each time asked. See Missed.</summary>
        public int MissedPlaceable;

        /// <summary>Unanswered throw questions, counted each time asked. See Missed.</summary>
        public int MissedLanding;

        /// <summary>The distinct cells asked about and not answered.</summary>
        public readonly HashSet<(float X, float Y)> MissedPlaces = [];

        /// <summary>The distinct throws asked about and not answered.</summary>
        public readonly HashSet<(float FromX, float FromY, float ToX, float ToY)> MissedThrows = [];

        /// <summary>The map's modifiers as "stat=value" separated by spaces, or empty where not kept. See Save.</summary>
        public string MapStats = "";

        /// <summary>
        /// The best chains seen at the site when it was saved, with what each scored in game, best first: a target for
        /// an offline search. Empty where not kept. See SiteBestChains.
        /// </summary>
        public List<(double Scored, List<Vector2> Route)> KnownChains = [];

        /// <summary>
        /// Each of KnownChains scored by the game as the site stood at the save, same order, where Scored is its score
        /// when found. Empty in a file saved before these were kept. See Save.
        /// </summary>
        public List<double> KnownChainsScoredAtSave = [];

        /// <summary>
        /// The plugin's version and build time and the scoring settings at save time, as text, for explaining a
        /// mismatch between a layout and a later run. Not applied offline. Empty where not kept.
        /// </summary>
        public string Notes = "";

        /// <summary>
        /// Whether the file kept each remnant's own effects, held lifts, carried wave shares and wave count, which an
        /// older one did not. See Save.
        /// </summary>
        public bool PropagationInputs;

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

        /// <summary>
        /// The ground the file kept, which answers a reach the recording does not, or null for a file saved before
        /// it was kept. See GroundMagic.
        /// </summary>
        public Terrain Ground;

        /// <summary>How many reach questions the recording did not answer and Ground routed. See Ground.</summary>
        public int Routed;

        /// <summary>
        /// Route every aim through the kept ground instead of answering from the recorded ones first, so a solve pays
        /// for routing as a cold one in game does. Only where the file kept the ground. Set by the offline harness.
        /// </summary>
        public bool RouteEveryAim;

        /// <summary>
        /// When each marker was first seen, by its place, for the save to write, in milliseconds after the site was first seen; null
        /// when not recorded. See SeenMagic.
        /// </summary>
        public Dictionary<Vector2, double> SeenAtMs;

        /// <summary>
        /// When each target was first seen, as read back, in target order, in milliseconds after the site was first
        /// seen; -1 where not known, null for a file that kept none. See SeenMagic.
        /// </summary>
        public double[] SeenAtMsOfTarget;
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
    internal static string Save(PlanEnvironment env, Answers answers, string folder, string named, string area = "",
        string mapStats = "")
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

        // **The trailer, after everything an older reader stops at.** The map's name, and Gaining Traction, which the
        // file did not keep: offline, every remnant after the first was then scored without its extra packs, and one
        // Frigid Bluffs chain scored 21,898 against 41,707 in game. See Load.
        w.Write(ExtrasMagic);
        w.Write(area ?? "");
        w.Write(env.MagicPacksPerRemnantCompleted);
        w.Write(env.RarePacksPerRemnantCompleted);
        w.Write(env.RareModifierShareOfRare);
        w.Write(env.RemnantsCompletedInArea);

        // What the map already adds to each group's stat, which a relic's increase to the same stat adds to: without
        // it a relic's "+50% rare monsters" is scored as x1.5 on a map that makes it x1.14. See
        // PlanEnvironment.IncreaseBaseOfGroup.
        w.Write(env.IncreaseBaseOfGroup?.Length ?? -1);

        foreach (var based in env.IncreaseBaseOfGroup ?? [])
            w.Write(based);

        w.Write(mapStats ?? "");

        // The best chains seen at the site, so an offline search has the game's own result to aim at.
        w.Write(answers.KnownChains.Count);

        foreach (var (scored, route) in answers.KnownChains)
        {
            w.Write(scored);
            w.Write(route.Count);

            foreach (var at in route)
            {
                w.Write(at.X);
                w.Write(at.Y);
            }
        }

        w.Write(answers.Notes ?? "");

        // **Each remnant's propagation inputs**, in target order. Built in game from what the recipe shows - every rune
        // it holds, and which slot each propagating rune sits in - and not from anything else the file keeps, so
        // offline they were missing: a chain on Frigid Bluffs scored 92,310 offline against 86,933 in game, with
        // content agreeing to the decimal and all of the difference in propagation. See Answers.PropagationInputs.
        w.Write(env.Targets?.Count ?? -1);

        foreach (var target in env.Targets ?? [])
        {
            w.Write(target.OwnEffectsOfChoices?.Length ?? -1);

            foreach (var effects in target.OwnEffectsOfChoices ?? [])
            {
                w.Write(effects?.Length ?? -1);

                foreach (var (tag, count, factor, waveShare) in effects ?? [])
                {
                    w.Write(tag);
                    w.Write(count);
                    w.Write(factor);
                    w.Write(waveShare);
                }
            }

            w.Write(target.HeldLiftOfChoices?.Length ?? -1);

            foreach (var lifts in target.HeldLiftOfChoices ?? [])
                Fractions(w, lifts);

            w.Write(target.CarriedWaveSharesOfChoices?.Length ?? -1);

            foreach (var shares in target.CarriedWaveSharesOfChoices ?? [])
                Worths(w, shares);

            w.Write(target.WaveCount);

            // What each combination's "per" effects add, from the ninth trailer. See PlanTarget.CreatedOfChoices.
            w.Write(target.CreatedOfChoices?.Length ?? -1);

            foreach (var created in target.CreatedOfChoices ?? [])
            {
                w.Write(created?.Length ?? -1);

                foreach (var (source, rate, waveShare, mask, worthEach, unaffected, empowerment) in created ?? [])
                {
                    w.Write(source);
                    w.Write(rate);
                    w.Write(waveShare);
                    w.Write(mask);
                    w.Write(worthEach);
                    w.Write(unaffected);
                    w.Write(empowerment);
                }
            }
        }

        // **Each known chain scored again as the site stood when saved**, in the same order. A chain's own score is
        // from when it was found, and the site can be valued differently by the save - a Scorched Cay chain found at
        // 8,873 was worth 7,310 to the game at the save, and 7,310 offline. This is the figure offline should match.
        w.Write(answers.KnownChainsScoredAtSave.Count);

        foreach (var scored in answers.KnownChainsScoredAtSave)
            w.Write(scored);

        // The map's rare and magic pack increases, which Gaining Traction adds to. See PlanEnvironment.RareIncreaseOfMap.
        w.Write(env.RareIncreaseOfMap);
        w.Write(env.MagicIncreaseOfMap);

        // Each target's Gaining Traction table, in target order. See PlanTarget.TractionByBefore.
        w.Write(env.Targets?.Count ?? -1);

        foreach (var target in env.Targets ?? [])
        {
            w.Write(target.TractionByBefore?.Length ?? -1);

            foreach (var (normal, magic, rare) in target.TractionByBefore ?? [])
            {
                w.Write(normal);
                w.Write(magic);
                w.Write(rare);
            }
        }

        // **The ground itself, after the trailer, so a reader that stops at the trailer still reads the file.** See
        // GroundMagic.

        // **When each marker was first seen**, for replaying a site being scouted. See SeenMagic.
        if (answers.SeenAtMs is { Count: > 0 } seen)
        {
            w.Write(SeenMagic);
            w.Write(env.Targets.Count);

            foreach (var target in env.Targets)
                w.Write(seen.TryGetValue(target.Grid, out var ms) ? ms : -1d);
        }

        return path;
    }

    /// <summary>
    /// Writes the ground section. The pathfinding grid is written as bytes where every cell fits one, and as ints
    /// otherwise, and both grids are compressed - a map's grid is millions of cells. See GroundMagic.
    /// </summary>
    private static void WroteGround(BinaryWriter w,
        (int[][] Walkable, Peek.Slab Coarse, bool Snaps, float Apart, GameOffsets2.Native.Vector2i[] Bombs, bool Last) ground)
    {
        w.Write(GroundMagic);

        var (walkable, coarse, snaps, apart, bombs, last) = ground;
        var small = walkable.All(row => row == null || row.All(x => x is >= 0 and <= 255));

        using (var packed = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, true))
            using (var g = new BinaryWriter(deflate))
            {
                g.Write(walkable.Length);
                g.Write(small);

                foreach (var row in walkable)
                {
                    g.Write(row?.Length ?? -1);

                    foreach (var cell in row ?? [])
                    {
                        if (small)
                            g.Write((byte)cell);
                        else
                            g.Write(cell);
                    }
                }

                g.Write(coarse != null);

                if (coarse != null)
                {
                    g.Write(coarse.Wider);
                    g.Write(coarse.Higher);
                    g.Write(coarse.FromX);
                    g.Write(coarse.FromY);
                    g.Write(coarse.Bytes.Length);
                    g.Write(coarse.Bytes);
                }
            }

            w.Write((int)packed.Length);
            w.Write(packed.GetBuffer(), 0, (int)packed.Length);
        }

        w.Write(snaps);
        w.Write(apart);
        w.Write(last);
        w.Write(bombs?.Length ?? -1);

        foreach (var bomb in bombs ?? [])
        {
            w.Write(bomb.X);
            w.Write(bomb.Y);
        }
    }

    /// <summary>Reads the ground section back into a terrain that answers as the game's did. See WroteGround.</summary>
    private static Terrain ReadGround(BinaryReader r)
    {
        var packedLength = r.ReadInt32();
        var packed = r.ReadBytes(packedLength);
        int[][] walkable;
        Peek.Slab coarse = null;

        using (var deflate = new System.IO.Compression.DeflateStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress))
        using (var g = new BinaryReader(deflate))
        {
            walkable = new int[g.ReadInt32()][];

            var small = g.ReadBoolean();

            for (var y = 0; y < walkable.Length; y++)
            {
                var length = g.ReadInt32();

                if (length < 0)
                    continue;

                walkable[y] = new int[length];

                for (var x = 0; x < length; x++)
                    walkable[y][x] = small ? g.ReadByte() : g.ReadInt32();
            }

            if (g.ReadBoolean())
            {
                var (wide, high, fromX, fromY) = (g.ReadInt32(), g.ReadInt32(), g.ReadInt32(), g.ReadInt32());

                coarse = new Peek.Slab(wide, high, fromX, fromY, g.ReadBytes(g.ReadInt32()));
            }
        }

        var snaps = r.ReadBoolean();
        var apart = r.ReadSingle();
        var last = r.ReadBoolean();
        var bombCount = r.ReadInt32();
        var bombs = bombCount < 0 ? null : new GameOffsets2.Native.Vector2i[bombCount];

        for (var i = 0; i < bombCount; i++)
            bombs[i] = new GameOffsets2.Native.Vector2i(r.ReadInt32(), r.ReadInt32());

        return null;
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

        // The trailer, when the file has one. See Save.
        var (magicPacks, rarePacks, rareModifierShare, completed) = (0f, 0f, 0f, 0);

        float[] increaseBase = null;
        var (rareIncrease, magicIncrease) = (0f, 0f);

        if (file.Position < file.Length && r.ReadString() is var tag &&
            (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh || tag == ExtrasMagicSixth || tag == ExtrasMagicFifth || tag == ExtrasMagicFourth || tag == ExtrasMagicThird ||
             tag == ExtrasMagicSecond ||
             tag == ExtrasMagicFirst))
        {
            answers.Area = r.ReadString();
            magicPacks = r.ReadSingle();
            rarePacks = r.ReadSingle();
            rareModifierShare = r.ReadSingle();
            completed = r.ReadInt32();

            if (tag != ExtrasMagicFirst)
            {
                var bases = r.ReadInt32();

                increaseBase = bases < 0 ? null : new float[bases];

                for (var i = 0; i < bases; i++)
                    increaseBase[i] = r.ReadSingle();

                answers.MapStats = r.ReadString();
            }

            if (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh || tag == ExtrasMagicSixth || tag == ExtrasMagicFifth || tag == ExtrasMagicFourth ||
                tag == ExtrasMagicThird)
            {
                var chains = r.ReadInt32();

                for (var c = 0; c < chains; c++)
                {
                    var scored = r.ReadDouble();
                    var links = r.ReadInt32();
                    var route = new List<Vector2>(links);

                    for (var k = 0; k < links; k++)
                        route.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));

                    answers.KnownChains.Add((scored, route));
                }
            }

            if (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh || tag == ExtrasMagicSixth || tag == ExtrasMagicFifth || tag == ExtrasMagicFourth)
                answers.Notes = r.ReadString();

            if (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh || tag == ExtrasMagicSixth || tag == ExtrasMagicFifth)
            {
                var kept = r.ReadInt32();

                if (targets == null || kept != targets.Count)
                    throw new InvalidDataException($"propagation inputs for {kept} markers, and there are {targets?.Count ?? -1}");

                for (var i = 0; i < kept; i++)
                {
                    var choiceCount = r.ReadInt32();
                    var own = choiceCount < 0 ? null : new (int Tag, bool Count, float Factor, float WaveShare)[choiceCount][];

                    for (var c = 0; c < choiceCount; c++)
                    {
                        var ownEffectCount = r.ReadInt32();

                        own[c] = ownEffectCount < 0 ? null : new (int, bool, float, float)[ownEffectCount];

                        // Files before the seventh trailer kept no share: every wave, as they were scored.
                        for (var e = 0; e < ownEffectCount; e++)
                        {
                            own[c][e] = (r.ReadInt32(), r.ReadBoolean(), r.ReadSingle(),
                                tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh
                                    ? r.ReadSingle()
                                    : 1f);

                            // The eighth trailer kept a counted tag on each; a "per" effect there was a factor on
                            // nothing and is read as none.
                            if (tag == ExtrasMagicEighth && r.ReadInt32() >= 0)
                                own[c][e] = (own[c][e].Tag, false, 1f, 1f);
                        }
                    }

                    var liftCount = r.ReadInt32();
                    var lifts = liftCount < 0 ? null : new float[liftCount][];

                    for (var c = 0; c < liftCount; c++)
                        lifts[c] = Fractions(r);

                    var shareCount = r.ReadInt32();
                    var shares = shareCount < 0 ? null : new (string Id, float Share)[shareCount][];

                    for (var c = 0; c < shareCount; c++)
                        shares[c] = Worths(r);

                    targets[i] = targets[i] with
                    {
                        OwnEffectsOfChoices = own,
                        HeldLiftOfChoices = lifts,
                        CarriedWaveSharesOfChoices = shares,
                        WaveCount = r.ReadInt32(),
                    };

                    if (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth)
                    {
                        var createdCount = r.ReadInt32();
                        var created = createdCount < 0 ? null : new (int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment)[createdCount][];

                        for (var c = 0; c < createdCount; c++)
                        {
                            var entries = r.ReadInt32();

                            created[c] = entries < 0 ? null : new (int Source, float Rate, float WaveShare, long Mask, float WorthEach, bool UnaffectedByRunes, int Empowerment)[entries];

                            for (var e = 0; e < entries; e++)
                                created[c][e] = (r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadInt64(), r.ReadSingle(),
                                    r.ReadBoolean(), tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth ? r.ReadInt32() : 0);
                        }

                        targets[i] = targets[i] with { CreatedOfChoices = created };
                    }
                }

                answers.PropagationInputs = true;
            }

            if (tag == ExtrasMagic || tag == ExtrasMagicEleventh || tag == ExtrasMagicTenth || tag == ExtrasMagicNinth || tag == ExtrasMagicEighth || tag == ExtrasMagicSeventh || tag == ExtrasMagicSixth)
            {
                var scores = r.ReadInt32();

                for (var c = 0; c < scores; c++)
                    answers.KnownChainsScoredAtSave.Add(r.ReadDouble());
            }

            if (tag == ExtrasMagic || tag == ExtrasMagicEleventh)
            {
                rareIncrease = r.ReadSingle();
                magicIncrease = r.ReadSingle();
            }

            if (tag == ExtrasMagic)
            {
                var kept = r.ReadInt32();

                for (var i = 0; i < kept; i++)
                {
                    var length = r.ReadInt32();
                    var table = length < 0 ? null : new (float Normal, float Magic, float Rare)[length];

                    for (var k = 0; k < length; k++)
                        table[k] = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                    if (targets != null && i < targets.Count)
                        targets[i] = targets[i] with { TractionByBefore = table };
                }
            }
        }

        // The sections after the trailer, each when the file kept it: the ground, and when each marker was first seen.
        // See GroundMagic and SeenMagic.
        while (file.Position < file.Length && r.ReadString() is var section)
        {
            if (section == GroundMagic)
                answers.Ground = ReadGround(r);
            else if (section == SeenMagic)
            {
                var seenCount = r.ReadInt32();

                answers.SeenAtMsOfTarget = new double[seenCount];

                for (var i = 0; i < seenCount; i++)
                    answers.SeenAtMsOfTarget[i] = r.ReadDouble();
            }
            else
                break;
        }

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
                answers.MissedPlaceable++;

                lock (answers.MissedPlaces)
                    answers.MissedPlaces.Add((at.X, at.Y));

                return answers.Assume;
            })
        {
            // Left null deliberately: Planner.Says never asks it while Lands is present, and Planning
            // derives one from the other. See the class comment.
            CanReach = null,
            Lands = (from, to) =>
            {
                if (!(answers.RouteEveryAim && answers.Ground != null) &&
                    answers.Landing.TryGetValue((from.X, from.Y, to.X, to.Y), out var yes))
                    return yes;

                // Routed as the game routes it, where the file kept the ground. See GroundMagic.
                if (answers.Ground != null)
                {
                    Interlocked.Increment(ref answers.Routed);

                    return answers.Ground.Aiming(from, to, reach, out _);
                }

                answers.Missed++;
                answers.MissedLanding++;

                lock (answers.MissedThrows)
                    answers.MissedThrows.Add((from.X, from.Y, to.X, to.Y));

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
            MagicPacksPerRemnantCompleted = magicPacks,
            RarePacksPerRemnantCompleted = rarePacks,
            RareIncreaseOfMap = rareIncrease,
            MagicIncreaseOfMap = magicIncrease,
            RareModifierShareOfRare = rareModifierShare,
            RemnantsCompletedInArea = completed,
            IncreaseBaseOfGroup = increaseBase,
        };

        return (env, answers);
    }

    /// <summary>What a loaded site is, and whether it answered everything it was asked.</summary>
    internal static string Said(PlanEnvironment env, Answers answers) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{env.Targets.Count} marker(s), {env.Explosives} explosive(s), blast {env.Blast:0.#}, " +
            $"reach {env.Reach:0.#}, from ({env.Origin.X:0},{env.Origin.Y:0}); " +
            $"{answers.Placeable.Count:N0} cell(s) and {answers.Landing.Count:N0} aim(s) recorded" +
            $"{(answers.Ground != null ? " and the ground kept, so an aim not recorded is routed, " : " and no ground kept, so an aim not recorded is refused, ")}" +
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

    private static void Fractions(BinaryWriter w, float[] these)
    {
        w.Write(these?.Length ?? -1);

        foreach (var one in these ?? [])
            w.Write(one);
    }

    private static float[] Fractions(BinaryReader r)
    {
        var count = r.ReadInt32();
        var found = count < 0 ? null : new float[count];

        for (var i = 0; i < count; i++)
            found[i] = r.ReadSingle();

        return found;
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
