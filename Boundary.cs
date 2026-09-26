using ExileCore2;
using ExileCore2.PoEMemory.Components;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>One marker changing state, and how far away it was when it did.</summary>
/// <param name="Paired">
/// Whether the object was a resolved pair when this reading was taken.
///
/// **A buried strongbox is two entities on one cell** - the mound a blast breaks and the chest
/// inside it - and Glowing is true when EITHER is lit. Whether the second half had been found yet
/// changes what the reading is about, and nothing in the file said which. Fitting a threshold to
/// the strongbox rows agrees 79 to 83% of the time where every single-entity art agrees 93 to
/// 100%, which is the shape of two populations mixed together. This is the column that separates
/// them. See Target.Held.
/// </param>
/// <param name="Lit">True for the moment it lit up, false for the moment it stopped.</param>
internal sealed record Crossing(DateTime When, TargetKind Kind, string Art, bool Lit,
    float Distance, float Blast, bool Paired, float From, int Size)
{
    /// <summary>
    /// What this crossing implies the marker's extent is.
    ///
    /// A marker lights up when the blast touches it, so at the moment it lights the distance is the
    /// blast radius plus its own extent. Lighting is a lower bound on the extent and going dark is
    /// an upper one, because the cursor moves in steps and the crossing is only ever caught
    /// somewhere inside the step that caused it.
    /// </summary>
    public float Extent => Distance - Blast;
}

/// <summary>
/// The moment a marker lights up, recorded rather than summarised.
///
/// This replaces watching a live bracket. The bracket held only the extreme reading on each side,
/// so a single odd sample set a bound that nothing later could widen, and there was no way to see
/// whether the readings agreed. Every crossing is now kept, with what kind of marker it was and
/// what art it wore, and written to a file that can be read at leisure.
///
/// What it is for: the ring drawn on a marker is meant to overlap the blast circle exactly when
/// that marker would be caught, so its radius should be the catch distance less the blast radius.
/// The blast radius is known. This is the other half, measured from the game doing it rather than
/// guessed - and per kind, because the sizes look like they differ slightly.
/// </summary>
internal sealed class Boundary
{
    private readonly List<Crossing> _crossings = new();
    private readonly Dictionary<(int X, int Y), bool> _was = new();

    /// <summary>
    /// The bracket for each art, which is the grain that matters.
    ///
    /// Per art rather than per kind because the sizes look like they differ slightly between
    /// markers of the same kind - monstermarker against monstermarker_03 - and a per-kind average
    /// would hide exactly that. Remnants have no art of their own, so they are filed under the
    /// kind name instead.
    /// </summary>
    private readonly Dictionary<string, Fit> _fits = new(StringComparer.OrdinalIgnoreCase);

    private int _fittedAt = -1;

    private uint _area;
    private string _path;
    private string _summary;
    private int _tight;

    /// <summary>
    /// What the crossings for one art come to: the edge that best explains them, and how well.
    ///
    /// **A bracket cannot survive a bad reading and this can.** The floor was the furthest a marker
    /// had been seen lit and the ceiling the nearest it had been seen dark, each moving one way
    /// only - so a single odd sample set a bound nothing later could widen, and once the floor rose
    /// past the ceiling the art was unmeasurable for the rest of the session with no way to say so
    /// or to start again. Every crossing is already kept; the summary was the only thing that could
    /// be poisoned.
    ///
    /// So the edge is fitted rather than accumulated: the distance that, taken as the extent,
    /// agrees with the most readings - lit inside it, dark outside. One reading in fifty that
    /// disagrees moves the answer not at all and shows as agreement of 98% instead of hiding.
    /// Validated against the arts whose answer is already known, over 44,835 rows: every marker art
    /// fits 2.25 at 93 to 100%, the siren eggs 17.22, the sub-area caps 17.25, the barrels 5.2.
    /// </summary>
    private readonly record struct Fit(float Extent, float Agreement, int Crossings,
        float Low, float High);

    /// <summary>How well a fit must explain its readings before the answer is worth writing.</summary>
    private const float GoodEnough = 0.9f;

    /// <summary>How many crossings an art needs before its fit means anything.</summary>
    private const int Enough = 12;

    /// <summary>How many new crossings before the fits are worked out again.</summary>
    private const int Between = 8;

    public int Count => _crossings.Count;

    public IReadOnlyList<Crossing> Crossings => _crossings;

    /// <summary>How many of the most recent lightings the readout shows. See Lines.</summary>
    private const int Recent = 5;

    /// <summary>
    /// The last few times something lit up, as the range the edge must lie in.
    ///
    /// **A per-art summary cannot be watched.** It answers "what do we think" where the question
    /// while sweeping is "what just happened", and a number that moves by a hundredth every few
    /// seconds looks identical to one that is stuck. These are the readings themselves, newest
    /// first, so a sweep can be seen working: five ranges that overlap is an answer, five that do
    /// not is a session measuring two objects at once.
    ///
    /// Lightings only. Going dark is a reading too and the fit uses both, but a sweep is watched
    /// by moving the indicator INTO something, and mixing the two halves doubles the lines for
    /// nothing.
    /// </summary>
    public IEnumerable<string> Lines()
    {
        Refit();

        var world = Detonator.GridToWorld;
        var shown = 0;

        for (var i = _crossings.Count - 1; i >= 0 && shown < Recent; i--)
        {
            var c = _crossings[i];

            if (!c.Lit)
                continue;

            shown++;

            var low = MathF.Min(c.From, c.Distance) - c.Blast;
            var high = MathF.Max(c.From, c.Distance) - c.Blast;
            var art = string.IsNullOrEmpty(c.Art) ? c.Kind.ToString().ToLowerInvariant() : c.Art;

            // **World first, because world is what the Size cell takes.** The cell is documented
            // in world - see Wrt.Row.Size - while the measured table in Extents and the fallbacks
            // beside it are in grid, so the same magnitude is written two ways a few files apart
            // and reading one to fill in the other is a ten-fold error. Both are shown until that
            // is settled, each with its unit against it.
            yield return $"{c.When:HH:mm:ss}  {art}  lit between {low * world:0.#} and " +
                         $"{high * world:0.#} world ({low:0.00}-{high:0.00} grid)" +
                         (c.Paired ? "  (pair)" : "");
        }

        if (shown == 0)
            yield return "nothing has lit up yet - sweep the indicator across a marker";

        foreach (var pair in _fits.OrderByDescending(x => x.Value.Agreement).Take(3))
        {
            var f = pair.Value;

            yield return $"    {pair.Key}: agreed {f.Low * world:0.#} to {f.High * world:0.#} world " +
                         $"({f.Low:0.00}-{f.High:0.00} grid) by {f.Agreement:P0} of {f.Crossings}" +
                         $" - use {f.Extent * world:0.#}";
        }
    }

    /// <summary>How many arts are well explained, against how many have been seen at all.</summary>
    public (int Tight, int Seen) Progress()
    {
        Refit();

        var tight = 0;

        foreach (var f in _fits.Values)
        {
            if (f.Crossings >= Enough && f.Agreement >= GoodEnough)
                tight++;
        }

        return (tight, _fits.Count);
    }

    /// <summary>
    /// Works out each art's edge from the crossings kept for it.
    ///
    /// The candidates are the readings themselves - the edge can only sit at one of them - so this
    /// is a sort and a sweep per art rather than a search. Re-run on a handful of new crossings
    /// rather than on every one, since the answer cannot move far on a single reading and the
    /// readout asks for it every frame it is drawn.
    /// </summary>
    private void Refit()
    {
        if (_fittedAt >= 0 && _crossings.Count - _fittedAt < Between)
            return;

        _fittedAt = _crossings.Count;
        _fits.Clear();

        var seen = new Dictionary<string, List<(float Low, float High)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in _crossings)
        {
            var art = string.IsNullOrEmpty(c.Art) ? c.Kind.ToString().ToLowerInvariant() : c.Art;

            if (!seen.TryGetValue(art, out var had))
                seen[art] = had = new List<(float, float)>();

            // **Each crossing is an interval, and the edge is inside it.** The marker was in one
            // state at the previous indicator position and the other at this one, so the edge lies
            // between the two distances - a fact, not an estimate. A fast sweep gives a whole grid
            // step of uncertainty and a slow one a fraction of it, and both are true.
            had.Add((MathF.Min(c.From, c.Distance) - c.Blast,
                     MathF.Max(c.From, c.Distance) - c.Blast));
        }

        foreach (var (art, intervals) in seen)
        {
            if (intervals.Count == 0)
                continue;

            // The answer is where the most intervals agree. Every endpoint is a candidate, since
            // the count of overlapping intervals can only change at one - so this is a sweep over
            // the ends rather than a search over the line.
            var ends = intervals.Select(x => x.Low).Concat(intervals.Select(x => x.High))
                .Distinct().OrderBy(x => x).ToList();

            var best = ends[0];
            var most = -1;
            float lowest = 0f, highest = 0f;

            foreach (var at in ends)
            {
                var covering = intervals.Count(x => x.Low <= at && at <= x.High);

                if (covering <= most)
                    continue;

                most = covering;
                best = at;
                lowest = intervals.Where(x => x.Low <= at && at <= x.High).Max(x => x.Low);
                highest = intervals.Where(x => x.Low <= at && at <= x.High).Min(x => x.High);
            }

            // **The low end of where they agree, not the middle of it.** Of the two ways to be
            // wrong, a catch extent that is too SMALL costs a wasted explosive - the planner does
            // not know an earlier blast already took the thing, so it spends a link covering it
            // again - and one that is too LARGE costs the object itself, because the planner
            // believes a blast reaches something it does not and nothing ever covers it. A wasted
            // link is a worse chain; a missed strongbox is content left in the ground. See
            // Planning.Taken and Extents.
            _fits[art] = new Fit(lowest, (float)most / intervals.Count, intervals.Count,
                lowest, highest);

            _ = best;
        }
    }

    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _was.Clear();
            _last = Vector2.Zero;
        }

        _area = areaHash;
    }

    /// <summary>
    /// Notices any marker that has changed state since the last look.
    ///
    /// Keyed on grid position rather than on the target, so a marker the game unloads and reloads
    /// is still the same marker and does not read as a fresh crossing.
    /// </summary>
    public void Observe(Vector2 indicator, IReadOnlyList<Target> targets, float blast)
    {
        if (indicator == Vector2.Zero || blast <= 0f)
            return;

        // **Mark a marker and only it is measured.** Every live marker crossing its edge is a
        // reading, which is right for the arts that are everywhere and useless for the one object
        // in front of you - a session leaves eight thousand monster marker rows and twenty for the
        // thing being asked about, and the twenty are the ones that matter.
        //
        // Insisting is already how a player points at one marker and it already has the cells, so
        // it doubles as "measure this". Mark nothing and everything is recorded, as before. See
        // Insisted, and DebugSettings.ShowMeasurements for the drawing.
        var only = Insisted.Here.Count > 0;

        // A jump is not a crossing.
        //
        // The indicator moves a grid unit at a time while the cursor is being swept, and those steps
        // are what carry a marker over its edge. It also moves in leaps - placement mode ending, or
        // the chain origin shifting once an explosive is placed - and then every lit marker goes
        // dark at once for a reason that has nothing to do with distance. Recording those produced
        // seventy nine impossible readings in the first session, several of them negative.
        var jumped = _last != Vector2.Zero && Vector2.Distance(_last, indicator) > MaxStep;
        var settled = _last;

        _last = indicator;

        if (jumped)
        {
            // The states are still worth taking, so the next real crossing is measured against
            // where things actually are rather than against a stale picture.
            foreach (var target in targets)
            {
                if (target.Live)
                    _was[Cell(target)] = target.Glowing;
            }

            return;
        }


        foreach (var target in targets)
        {
            if (!target.Live || (only && !Insisted.Here.Wants(target.Grid)))
                continue;

            var key = Cell(target);
            var lit = target.Glowing;

            if (_was.TryGetValue(key, out var before) && before == lit)
                continue;

            var first = !_was.ContainsKey(key);
            _was[key] = lit;

            // The first look at a marker is not a crossing - it is just finding out what state it
            // is already in. Only a change from a known state says anything about where the edge is.
            if (first)
                continue;

            var distance = Vector2.Distance(indicator, target.Grid);

            // A marker cannot stop being caught while it is closer than the blast radius. If one
            // appears to, the reading is about something other than the edge and is not evidence.
            if (distance < blast)
                continue;

            // **Where it was the frame before, so a crossing is a range rather than a point.**
            //
            // The indicator moves a whole grid step at a time, so the edge is not AT this distance
            // - it is somewhere between here and where the indicator was standing when the marker
            // was in its other state. Both ends were available all along and the earlier one was
            // computed and discarded. Watching a sweep, the range is the thing that tells you
            // whether the readings agree: five that overlap is an answer, five that do not is a
            // session measuring two objects.
            var was = settled == Vector2.Zero
                ? distance
                : Vector2.Distance(settled, target.Grid);

            // **The footprint the game states, against the extent being measured.**
            //
            // Positioned.Size packs two sixteen bit halves: every ordinary marker reads 0x00010001
            // and every one of them measures the 2.25 floor, and a buried strongbox reads
            // 0x00020002 and measures about twice that. Two points are not a rule - the same
            // mistake as reading a size off Render.Bounds, which agreed with one object and was
            // wrong about the next - so it is recorded beside each reading rather than turned into
            // arithmetic. The eggs, the sub-area caps and the barrels are the rows that would
            // settle it, since their extents are already known and are nothing like the floor.
            var footprint = Safe.Read(target.Entity,
                static e => e.GetComponent<Positioned>()?.Size ?? 0, 0);

            var crossing = new Crossing(DateTime.Now, target.Kind, target.Art, lit, distance,
                blast, target.Held != null, was, footprint);

            _crossings.Add(crossing);

            // Measured, written down, and NOT acted on. See Extents.Forget: a ring that resizes
            // itself mid-dig cannot be told apart from one that is wrong, and this measurement has
            // been wrong. The conclusion goes to marker_extent.txt for somebody to read and put in
            // the table; nothing on screen moves because of it.
        }
    }

    /// <summary>How far the indicator may move between looks and still count as a step.</summary>
    private const float MaxStep = 3f;

    private Vector2 _last;

    private static (int X, int Y) Cell(Target target) =>
        ((int)MathF.Round(target.Grid.X), (int)MathF.Round(target.Grid.Y));

    /// <summary>Filed under the art where there is one, and under the kind where there is not.</summary>
    private static string Key(Target target) =>
        string.IsNullOrEmpty(target.Art) ? target.Kind.ToString().ToLowerInvariant() : target.Art;

    /// <summary>
    /// Writes the per-art answer, once there is enough evidence to be worth writing.
    ///
    /// Rewritten in full whenever another art becomes well measured, rather than appended: it is a
    /// dozen lines of conclusions, and a file that keeps its own history of guesses is harder to
    /// read than one that just says the current answer.
    ///
    /// "Enough" is a bracket of half a grid unit or better. Looser than that and the midpoint is a
    /// guess dressed up in decimals.
    /// </summary>
    public void Summarise(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        var (tight, seen) = Progress();

        if (tight == 0 || tight == _tight)
            return;

        _tight = tight;

        try
        {
            var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
            Directory.CreateDirectory(directory);
            _summary ??= Path.Combine(directory, "marker_extent.txt");

            var text = new List<string>
            {
                "Marker extent, measured from the game.",
                "",
                "How far beyond the blast radius a marker is still caught. The ring drawn on a marker",
                "should be this wide, so that overlapping the blast circle means the marker is caught.",
                "",
                "Measured by watching each marker light up and go dark as the indicator crosses its",
                "edge: lighting up is a floor on the extent and going dark is a ceiling, because the",
                "indicator moves in whole grid steps and the crossing is caught somewhere inside one.",
                "",
                $"{tight} of {seen} arts explained by a single edge to {GoodEnough:P0} or better.",
                "",
                "World units, which is what the Size column takes - see Wrt.Row.Size. The measured",
                "table in Extents.cs is in GRID, and grid times 250/23 is world, so do not read one",
                "and type it into the other.",
                "",
                "An edge that explains every reading is one number the game is using. One that",
                "explains most of them is either a noisy session or two things being measured at",
                "once - a buried strongbox is a mound and a chest on one cell, and the paired",
                "column in the csv beside this is what separates those.",
                "",
                $"{"art",-34}{"use (world)",13}{"agreed range",22}{"agrees",9}{"crossings",11}",
            };

            foreach (var pair in _fits.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var f = pair.Value;

                var world = Detonator.GridToWorld;

                text.Add($"{pair.Key,-34}{f.Extent * world,13:0.#}" +
                         $"{$"{f.Low * world:0.#} to {f.High * world:0.#}",22}" +
                         $"{f.Agreement,9:P0}{f.Crossings,11}");
            }

            File.WriteAllLines(_summary, text);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the extent summary: {ex.Message}", 5f);
        }
    }

    /// <summary>
    /// Writes every crossing to a file beside the dumps.
    ///
    /// Appended rather than replaced, and written as it goes rather than at the end, because the
    /// interesting session is the one where something unexpected happens and the HUD is closed
    /// before anyone thinks to save.
    /// </summary>
    public void Save(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        if (_crossings.Count == 0)
            return;

        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "marker_extent.csv");

                if (!File.Exists(_path))
                    File.WriteAllText(_path, "when,kind,art,litUp,distanceGrid,blastGrid,extentGrid,extentWorld,paired,fromExtentGrid,footprint\n");
            }

            var lines = new List<string>();

            foreach (var c in _crossings)
            {
                lines.Add(string.Join(",",
                    c.When.ToString("s", CultureInfo.InvariantCulture),
                    c.Kind,
                    string.IsNullOrEmpty(c.Art) ? "-" : c.Art,
                    c.Lit,
                    c.Distance.ToString("0.###", CultureInfo.InvariantCulture),
                    c.Blast.ToString("0.###", CultureInfo.InvariantCulture),
                    c.Extent.ToString("0.###", CultureInfo.InvariantCulture),
                    (c.Extent * Detonator.GridToWorld).ToString("0.#", CultureInfo.InvariantCulture),
                    c.Paired,
                    (c.From - c.Blast).ToString("0.###", CultureInfo.InvariantCulture),
                    $"{c.Size >> 16}x{c.Size & 0xFFFF}"));
            }

            File.AppendAllLines(_path, lines);
            _crossings.Clear();
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the extent log: {ex.Message}", 5f);
        }
    }
}
