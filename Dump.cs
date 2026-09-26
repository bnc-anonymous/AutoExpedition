using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.Shared.Enums;
using System;
using System.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Everything the game will say about an Expedition encounter, written out once.
///
/// This exists because there is nothing to copy. Of the fifteen plugins in this HUD, not one reads
/// an Expedition chest or an Expedition monster - the only PoE 2 Expedition code anywhere reads
/// remnants, and it finds those with a single metadata prefix. So the content taxonomy this plugin
/// needs has to be established from a live encounter rather than assumed, and assumptions written
/// into a planner are the kind that look right for a season and quietly cost chests.
///
/// It is also how the highlight is identified. The game already marks, in green, exactly which
/// entities the explosive under the cursor will catch - which is a better answer than any radius
/// this plugin could compute, because it is the game's own. Something in memory changes when that
/// happens; the candidates are StateMachine states (the other plugin probes for "in_placement_range"
/// and "in_placing_range"), Targetable.isTargeted, and MinimapIcon. So every one of them is dumped
/// for every entity, and the way to find it is two dumps - one with the placement indicator parked
/// over a cluster, one with it well away - and a diff.
/// </summary>
internal static class Dump
{
    /// <summary>Set by the plugin so the dump can explain how each remnant priced.</summary>
    public static Valuation Valuation { get; set; }

    /// <summary>
    /// A remnant's fixed rune and the slot it sits in, as the re-pricing guard sees them.
    ///
    /// Read live rather than off the target, because the cached pair is what a SUCCESSFUL read
    /// stored and the question here is what the next read will find. See Scan's re-pricing.
    /// </summary>
    private static string FixedOf(Target target)
    {
        var named = Safe.Read(() => Valuation?.FixedRune(target.Entity), null) ?? "";
        var slot = Safe.Read(() => Valuation?.FixedSlot(target.Entity) ?? -1, -1);

        return slot >= 0 && named.Length == 0
            ? $"NONE@{slot} <- HALF READ, so the reward list is refused and this stays unpriced"
            : $"{(named.Length == 0 ? "none" : named)}@{slot}";
    }

    /// <summary>Set by the plugin, so the dump can sample the terrain under what has been found.</summary>
    public static Scan Scan { get; set; }

    /// <summary>The current plan, so the dump can say what the grid reads where it wants to place.</summary>
    public static Plan Plan { get; set; }

    /// <summary>The whole route and how far along it the chain is. See Planning.Chain.</summary>
    public static IReadOnlyList<System.Numerics.Vector2> Chain { get; set; }

    /// <summary>
    /// How many of the chain's links have an explosive on them.
    ///
    /// **Not Planning.Laid, which is a different number.** That one is the cursor into Chain - how
    /// much of the route lies behind the plan - and it moves only when a solve runs or an explosive
    /// comes back off. Placing one deliberately changes nothing there, since the link that went
    /// down is in the chain and in the plan already.
    ///
    /// So reading it as progress was stale between solves, and read that way for the whole life of
    /// a finished plan: a dump taken after five explosives went down on a five link chain said "0
    /// of 5 links laid", tagged every blast [planned], and reported that the explosives already
    /// down were paying nothing. The overlay's own count was right the whole time, because it adds
    /// the two halves - see Overlay.Bombs and Placement.PlacedOf.
    /// </summary>
    public static int LinksDown { get; set; }

    /// <summary>What the plan was solved against, so the score can be broken into its parts.</summary>
    public static PlanEnvironment Env { get; set; }

    /// <summary>Set by the plugin, so the dump can show how the reward choice was arrived at.</summary>
    public static AutoExpeditionSettings Settings { get; set; }

    /// <summary>Set by the plugin, so the dump can say how the spawn census is getting on.</summary>
    public static Spawns Spawns { get; set; }

    /// <summary>Set by the plugin, so the dump can say what blast the barrel test is using.</summary>
    public static Blast Reading { get; set; }

    /// <summary>Set by the plugin, so the dump can say what reach the plan was built on.</summary>
    /// <summary>
    /// What the scan is actually holding, by kind, and which dig site each kind is filed under.
    ///
    /// **"It is not drawing" has four causes and they look identical from outside**: the entity
    /// never reached the sweep, the sweep reached it and the classifier refused it, it was
    /// classified and filed under the wrong dig site so the per-site list drops it, or it is on the
    /// list and the drawing is gated on something else. Every one of those was argued about from a
    /// dump that said nothing about any of them.
    ///
    /// Counts by kind settle the first two, the site column settles the third, and the fourth is
    /// then the only one left.
    /// </summary>
    /// <summary>
    /// Keeps the newest few dumps and deletes the rest. See Debug.MaxDumps.
    ///
    /// Only the timestamped ones. The rolling files beside them - the spawn census, the remnant
    /// census, the score card - are one file each that is rewritten rather than added to, so there
    /// is nothing there to pile up and nothing to lose by leaving them alone.
    ///
    /// Failures are swallowed on purpose. A dump that cannot tidy up after itself has still been
    /// written, and the whole point of the file is to be read a minute from now - refusing to write
    /// one because an old one is locked would be the tail wagging the dog.
    /// </summary>
    /// <summary>
    /// Why the three figures beside the Verisium button are or are not on screen.
    ///
    /// **Four runtime conditions and no way to tell which failed.** The switches are all in the
    /// menu, so when nothing appears the question is always about the other four: the remnant has to
    /// be the one recommended, loaded, unrolled, close enough for the game to draw its label, and
    /// the player has to be carrying a Liquid Verisium - the button belongs to the orb, not to the
    /// remnant, so with none in the bag there is nothing to draw beside.
    ///
    /// Answering that by guessing wasted several passes. It is four reads.
    /// </summary>
    private static string Advising(GameController gc, Scan scan)
    {
        if (Rolling.Here.Best is not { } best)
            return "nothing advised, so no figures are drawn";

        // The fifth condition, and the one that looks most like a fault: everything else is in
        // place and the screen is still bare because a solve or a reroll pass is in flight. See
        // Rolling.Fresh.
        if (!Rolling.Here.Fresh)
            return "the advice is stale - a solve or a reroll pass is running, so nothing is drawn";

        Target remnant = null;

        foreach (var target in scan?.Targets ?? new List<Target>())
        {
            if (target.Kind == TargetKind.Remnant && System.Numerics.Vector2.Distance(target.Grid, best.Grid) < 1f)
                remnant = target;
        }

        if (remnant == null)
            return $"the advised remnant at ({best.Grid.X:0},{best.Grid.Y:0}) is not in the scan";

        var away = System.Numerics.Vector2.Distance(
            Safe.Read(gc, static g => g.Player.GridPos, System.Numerics.Vector2.Zero), remnant.Grid);

        var label = Safe.Read(() => remnant.Entity?.GetComponent<
            ExileCore2.PoEMemory.Components.Render>() != null, false);

        var found = Ground.Labels(gc);;

        ExileCore2.PoEMemory.Element drawn = null;

        foreach (var one in found ?? new List<ExileCore2.PoEMemory.Elements.LabelOnGround>())
        {
            if (Safe.Read(() => one.ItemOnGround?.Id ?? 0u, 0u) ==
                Safe.Read(() => remnant.Entity?.Id ?? 0u, 0u))
                drawn = Safe.Read(() => one.Label, null);
        }

        var button = drawn == null ? default : Overlay.RollButton(drawn);

        return $"the advised remnant at ({best.Grid.X:0},{best.Grid.Y:0}): {away:N0} grid away, " +
               $"live {remnant.Live}, rolled {remnant.Rerolled}, render {label}, " +
               $"label on screen {drawn != null}, " +
               (button.Width > 0f
                   ? $"Verisium button {button.Width:0}x{button.Height:0} at " +
                     $"({button.X:0},{button.Y:0}) - the figures should be drawn"
                   : "NO Verisium button - either you are too far for the game to draw the " +
                     "remnant's label, or you are not carrying a Liquid Verisium");
    }

    /// <summary>Whether the price list has arrived, which changes everything downstream.</summary>
    private static string Priced(Valuation valuation)
    {
        var (priced, total) = Safe.Read(() => valuation.Pricing(), (0, 0));

        if (total == 0)
            return "PRICES HAVE NOT ARRIVED - no recipe list at all yet; every reward reads as " +
                   "worth nothing, rewards fall back to the preference order, and nothing can be " +
                   "marked must take. Wait for NinjaPricer to finish fetching and solve again.";

        // The same judgement the overlay draws, so the two cannot disagree about whether the prices
        // are usable - the partial case is exactly the one that used to read as healthy here while
        // the solver was ranking everything at zero. See Valuation.Unpriced.
        var doubt = Safe.Read(() => valuation.Unpriced(), null);

        return doubt is { Length: > 0 }
            ? $"{doubt} - {Safe.Read(() => valuation.Priceless(), "?")}; until it finishes fetching, " +
              "every reward reads as worth nothing and the reward choices mean nothing"
            : $"prices: {priced:N0} of {total:N0} recipes priced above nothing";
    }

    private static void Prune(string directory, int keep)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("autoexpedition_*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (var i = Math.Max(1, keep); i < files.Count; i++)
                files[i].Delete();
        }
        catch (Exception)
        {
            // Nothing to say and nothing that depends on it.
        }
    }

    private static void Known(System.Text.StringBuilder b, GameController gc)
    {
        if (Scan == null)
            return;

        var site = Detonator.DetonatorGridPosition(gc);
        var counts = new SortedDictionary<string, (int All, int Here, int Live, int Lit)>();

        foreach (var target in Scan.Targets)
        {
            var name = target.Kind.ToString().ToLowerInvariant();

            counts.TryGetValue(name, out var had);

            counts[name] = (had.All + 1,
                had.Here + (System.Numerics.Vector2.Distance(target.Site, site) < 1f ? 1 : 0),
                had.Live + (target.Live ? 1 : 0),
                had.Lit + (target.Glowing ? 1 : 0));
        }

        var said = new List<string>();

        foreach (var (name, tally) in counts)
            said.Add($"{name} {tally.All} ({tally.Here} here, {tally.Live} loaded, {tally.Lit} lit)");

        // What the barrel drawing sees, since "no circles appeared" has several causes and the
        // scan holding eighty one of them rules out only the first.
        {
            var barrels = 0;
            var radius = 0f;
            var placed = "";

            foreach (var target in Scan?.Targets ?? new List<Target>())
            {
                if (target.Kind != TargetKind.Barrel)
                    continue;

                barrels++;
                radius = MathF.Max(radius, target.Sets);

                if (placed.Length == 0)
                {
                    var world = target.Where(gc);

                    placed = $"first at grid ({target.Grid.X:0},{target.Grid.Y:0}) " +
                             $"world ({world.X:0},{world.Y:0},{world.Z:0}) " +
                             $"sets {target.Sets:0.#} spent {target.Spent} live {target.Live}";
                }
            }

            b.AppendLine($"  barrels the drawing sees: {barrels}, largest radius {radius:0.#}, " +
                         (placed.Length > 0 ? placed : "none held"));

            // **Whether each one is still drawn, and the arithmetic that decided it.**
            //
            // A circle that will not go away has three possible causes that look identical on
            // screen - the blast radius reads as nothing, the nearest explosive is further than it
            // looks, or the barrel is not being matched at all - and every one of them has been
            // guessed at once already. See Overlay.Blown.
            var reach = Reading?.Radius(gc, Settings) ?? 0f;
            var standing = Detonator.PlacedExplosiveGridPositions(gc);
            var gone = Overlay.Blown(gc, Settings, Scan?.Targets ?? new List<Target>(), reach);

            b.AppendLine("  how big the explosion is: " +
                         (Reading == null ? "no reading" : Reading.Spelled(gc, Settings)));

            b.AppendLine($"  the blast the barrel test uses: {reach:0.#} grid over " +
                         $"{standing.Length} explosives down; {gone.Count} barrel(s) already covered");

            foreach (var target in Scan?.Targets ?? new List<Target>())
            {
                if (target.Kind != TargetKind.Barrel)
                    continue;

                var nearest = float.MaxValue;
                var at = System.Numerics.Vector2.Zero;

                foreach (var bomb in standing)
                {
                    var far = System.Numerics.Vector2.Distance(bomb, target.Grid);

                    if (far >= nearest)
                        continue;

                    nearest = far;
                    at = bomb;
                }

                var hidden = target.Spent || gone.Contains(Overlay.Cell(target.Grid));

                b.AppendLine($"    barrel ({target.Grid.X:0},{target.Grid.Y:0}) " +
                             (standing.Length == 0
                                 ? "no explosives down yet"
                                 : $"nearest explosive ({at.X:0},{at.Y:0}) at {nearest:0.#} grid " +
                                   $"vs blast {reach:0.#} + extent " +
                                   $"{Extents.Of(target):0.#}") +
                             $" - detonated {target.State("expedition_detonated")}, spent {target.Spent}" +
                             $" -> {(hidden ? "NOT drawn" : "still drawn")}");
            }
        }

        var ran = Placement.Last;

        b.AppendLine("  what the last placement run said: " +
                     (ran.When == default
                         ? "nothing since the plugin loaded"
                         : $"\"{ran.Said}\" - {(ran.Why.Length > 0 ? ran.Why : "no detail")} " +
                           $"({(DateTime.UtcNow - ran.When).TotalSeconds:N1}s ago)"));

        var took = Placement.Picked;

        b.AppendLine("  the last reward this run clicked: " +
                     (took.When == default
                         ? "none since the plugin loaded"
                         : $"{(took.Reward.Length > 0 ? took.Reward : "an unnamed option")} - " +
                           (took.Took
                               ? $"the remnant read it back after {took.Ms:N0}ms"
                               : $"the remnant never read it back; gave up after {took.Ms:N0}ms") +
                           $" ({(DateTime.UtcNow - took.When).TotalSeconds:N1}s ago)"));

        var ring = Placement.Verdict;

        b.AppendLine("  can the next explosive go down right now: " +
                     (ring.When == default
                         ? "never asked - the plan is not being drawn"
                         : ring.Go
                             ? "yes, the ring is green"
                             : $"no - {ring.Why}") +
                     (ring.When == default
                         ? ""
                         : $" (asked {(DateTime.UtcNow - ring.When).TotalMilliseconds:N0}ms ago)"));

        var repeats = Planner.Repeats;

        b.AppendLine($"  switches that pay once: {repeats.Distinct} distinct on this chain, " +
                     (repeats.Given > 0d
                         ? $"{repeats.Given:N0} taken back off for collecting one twice"
                         : "nothing collected twice"));

        b.AppendLine($"  spots the bans took off the table: {Planner.Forbidden}" +
                     (Planner.Forbidden == 0
                         ? " (nothing shunned, or nothing in reach of one)"
                         : " - each one removed from every link of every pass"));

        b.AppendLine("  is the expedition probably over: " +
                     (Ending.ProbablyOver ? "YES" : "no") + " - " + Ending.Says);

        b.AppendLine("  the scouting layer: " +
                     (Scouted.Live == null || Settings == null
                         ? "never built - nothing has asked it to mark anything"
                         : Scouted.Live.Describe(gc, Settings)));

        // **Default, not null-checked, and it threw the whole dump away.** Washed is a value tuple
        // that nothing has written on a map where the wash never drew - a small dig site, where the
        // scouting layer is a Grand-only thing - so First is a null string and asking it for a
        // Length is an exception. Everything after this point, including the bake-off tally the key
        // had just been pressed to see, was lost with it.
        var washed = Minimap.Washed;

        b.AppendLine($"  what the wash last drew: " +
                     (washed.Drew > 0 ? "the image" : "NOTHING") +
                     $", clipped to {washed.Surface.X:0},{washed.Surface.Y:0} " +
                     $"{washed.Surface.Width:0}x{washed.Surface.Height:0} " +
                     $"({(washed.Large ? "large map" : "small minimap")}) - {Minimap.Maps(gc)}" +
                     (washed.First is { Length: > 0 } ? $"; {washed.First}" : ""));

        b.AppendLine($"  what the scan holds: {(said.Count == 0 ? "nothing" : string.Join(", ", said))}");

        // Each one named, because the counts say how many and the question is usually about one.
        foreach (var target in Scan.Targets)
        {
            if (target.Kind != TargetKind.Strongbox)
                continue;

            b.AppendLine($"    {target.Label} at ({target.Grid.X:0},{target.Grid.Y:0}) " +
                         $"filed under ({target.Site.X:0},{target.Site.Y:0})" +
                         (System.Numerics.Vector2.Distance(target.Site, site) < 1f ? "" : " - NOT THIS SITE") +
                         $", loaded {target.Live}, lit {target.Glowing}" +
                         $", {target.Halves("glow_epk")}" +
                         // **What the game says about its size, beside what we assume.**
                         //
                         // A blast catches a thing at the blast radius plus the thing's own
                         // extent, and every extent in this plugin is either the stated floor of
                         // 2.25 or a figure measured off crossings. A strongbox has neither - it
                         // takes the floor because nobody put it in the table - and a bomb was
                         // seen to take one from further than the floor allows, which the readout
                         // then called unlit.
                         //
                         // inherent_explosion_radius is the only attribute that could state it and
                         // it has never been read on a box: the note beside Target.Sets says
                         // "sixty on a barrel, nought on everything else in a dig site", which was
                         // written about barrels and asserts the rest. Printed on both halves,
                         // since a buried box is two entities and the mound is the one the blast
                         // is likely to be testing. See Extents.Of.
                         $", extent used {Extents.Of(target):0.##}" +
                         $", inherent_explosion_radius {target.Sets:0.##}" +
                         $" (self {Target.Stated(target.Entity, "inherent_explosion_radius")}" +
                         $", held {Target.Stated(target.Held, "inherent_explosion_radius")})");
        }
    }

    /// <summary>
    /// What Render.Bounds says for each art, against the extent we already know.
    ///
    /// **Bounds was ruled out once and the counterexample may have read the wrong component.**
    /// Extents.Of records that the Heath henge has "bounds of 210.3 world" and a measured extent of
    /// 2.16 grid, which kills any rule that reads bounds as a size. But the figures in that table
    /// are single numbers, and a strongbox reads &lt;52.17, 52.17, 60.97&gt; - X and Y the footprint, Z
    /// the height. A runic henge is exactly the shape whose height dwarfs its base, so if 210.3 was
    /// its Z then the rule it disproved is not the rule "extent tracks the X/Y footprint".
    ///
    /// This does not decide it, it states it. Every art below has either a measured extent from the
    /// crossings or the stated floor, so the X, Y and Z columns can be read against a known answer:
    /// markers at 2.25, siren eggs at 17.22, sub-area caps at 17.25, barrels at 5.2. A rule that
    /// holds for all of those is a rule; one that holds for the egg alone is one object agreeing
    /// with its bounds, which is what the note in Extents was written about.
    ///
    /// Printed in grid, since that is what an extent is in, and world beside it because that is
    /// what the component holds. See Extents and Detonator.GridToWorld.
    /// </summary>
    private static void BoundsAgainstExtents(StringBuilder b, Scan scan)
    {
        b.AppendLine();
        b.AppendLine("=== what Render.Bounds says, against the extent in use ===");
        b.AppendLine("  art / metadata                             bounds X    Y      Z" +
                     "   art radius   Positioned.Size   |  extent used   drawn z / ground z");

        var seen = new HashSet<string>();

        foreach (var target in scan?.Targets ?? [])
        {
            var name = target.Art.Length > 0 ? target.Art : target.Meta;

            if (name.Length == 0 || !seen.Add(name))
                continue;

            var bounds = Safe.Read(target.Entity,
                static e => e.GetComponent<Render>()?.Bounds ?? System.Numerics.Vector3.Zero,
                System.Numerics.Vector3.Zero);

            if (bounds == System.Numerics.Vector3.Zero)
                continue;

            // **The art's own radius, which is how the blast circle is sized.**
            //
            // MiscAnimated.BaseSize is the art's radius in world units and Positioned.Scale is what
            // this map did to it; multiplied, they give the green ring the game draws to within
            // about 8%. See Detonator.ArtRadius, whose note says the thing worth repeating - look
            // for the value before fitting a model of it. Never asked of a TARGET, only of the
            // placement indicator, and it is the last stated quantity that could be an extent now
            // that Render.Bounds and inherent_explosion_radius have both come back no.
            var art = Safe.Read(target.Entity,
                static e => (e.GetComponent<Animated>()?.MiscAnimated?.BaseSize ?? 0) *
                            (e.GetComponent<Positioned>()?.Scale is { } s and > 0f ? s : 1f), 0f);

            // **Positioned.Size, which is an int and reads 65537 on every ordinary marker.**
            //
            // 65537 is 0x00010001 - two sixteen bit halves, both one - so this is a footprint in
            // cells rather than a radius, and every marker whose extent is the 2.25 floor is one
            // cell by one. That makes it the first stated field that could SEPARATE a strongbox
            // from a marker, which is the whole question: Render.Bounds could not, because a
            // sentry and a strongbox share 4.80, and inherent_explosion_radius reads nought on
            // both. Shown decoded, since the packed integer says nothing to look at.
            var size = Safe.Read(target.Entity,
                static e => e.GetComponent<Positioned>()?.Size ?? 0, 0);

            b.AppendLine($"  {Short(name),-41} " +
                         $"{bounds.X / Detonator.GridToWorld,7:0.00}" +
                         $"{bounds.Y / Detonator.GridToWorld,7:0.00}" +
                         $"{bounds.Z / Detonator.GridToWorld,7:0.00}" +
                         $"{art / Detonator.GridToWorld,12:0.00}" +
                         $"{$"{size >> 16}x{size & 0xFFFF} ({size})",18}   |  " +
                         $"{Extents.Of(target),6:0.00}   ({target.Kind})" +

                // **This table lists every art in the scan, which is why the heights go here.** The
                // per-target scan listing covers 60 grid and a few kinds, so the object that prompted
                // the question - a sub-area cap - never appeared in it and the flag could not fire.
                $"   {Safe.Read(() => target.Where(_dumping).Z, 0f):0.#} / " +
                $"{Safe.Read(() => _dumping.IngameState.Data.ToWorldWithTerrainHeight(target.Grid).Z, 0f):0.#}" +
                (MathF.Abs(Safe.Read(() => target.Where(_dumping).Z, 0f) -
                           Safe.Read(() => _dumping.IngameState.Data.ToWorldWithTerrainHeight(target.Grid).Z, 0f)) > 20f
                    ? "  <- NOT ON THE GROUND"
                    : ""));
        }

        b.AppendLine("  (grid units. A rule that fits every line is a rule; one that fits the big " +
                     "objects alone is a coincidence - see Extents.)");
        b.AppendLine("  Known answers to check a column against: every marker art 2.25, siren eggs " +
                     "17.22, sub-area caps 17.25, barrels 5.2.");
    }

    /// <summary>The last segment of a path, so the column stays readable.</summary>
    private static string Short(string name)
    {
        var cut = name.LastIndexOf('/');

        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    /// <summary>The placement loop, so the dump can report the last thing it said. See Placement.Said.</summary>
    public static Placement Placement { get; set; }

    /// <summary>The presolve, so its work can be judged. See Rehearsal.</summary>
    public static Rehearsal Rehearsal { get; set; }


    /// <summary>
    /// The environment the number on screen was worked out over, one line per kind.
    ///
    /// **The score falls as the player walks away and the marker count does not.** Build reads
    /// scan.At(site), which keeps a marker for ever once seen, so the pool is not shrinking - and
    /// yet a chain the search finished at 5,823 draws at 2,858 from across the map. Three guesses
    /// about which term loses it were all wrong, so this reports the terms instead.
    ///
    /// Per kind, because the suspicion worth testing is that the kinds behave differently: remnants
    /// stream map-wide and keep their rewards, where monsters, relics and chests unload as you
    /// leave. Two presses at two distances, read side by side, say whether a kind loses its WEIGHT
    /// (the weights are read off something that needs the entity) or loses its CATCHES (the extent
    /// each marker is caught from is). Those are different faults in different files.
    ///
    /// Caught is counted the way the objective counts it - the chain's own blast reach plus the
    /// target's extent - rather than by asking the verdict, so a disagreement between this line and
    /// the score above is itself informative.
    /// </summary>
    private static string ScoringPool()
    {
        var env = Scoring.LastEnv;
        var chain = Scoring.LastChain;

        if (env?.Targets == null || env.Targets.Count == 0)
            return "no environment built yet";

        var kinds = new SortedDictionary<TargetKind, (int Many, double Weight, double Waves,
            double Extent, int Caught, double CaughtWeight)>();

        foreach (var target in env.Targets)
        {
            kinds.TryGetValue(target.Kind, out var tally);

            var caught = false;

            foreach (var at in chain ?? [])
            {
                var reach = env.Blast + target.Radius;

                if (System.Numerics.Vector2.DistanceSquared(at, target.Grid) <= reach * reach)
                {
                    caught = true;

                    break;
                }
            }

            kinds[target.Kind] = (tally.Many + 1, tally.Weight + target.Weight,
                tally.Waves + target.Waves, tally.Extent + target.Radius,
                tally.Caught + (caught ? 1 : 0),
                tally.CaughtWeight + (caught ? target.Weight : 0d));
        }

        var said = new StringBuilder($"blast {env.Blast:0.#}, {chain?.Count ?? 0} spots");

        foreach (var (kind, tally) in kinds)
        {
            said.Append($"\n    {kind}: {tally.Many} in the pool worth {tally.Weight:N0} ")
                .Append($"(+{tally.Waves:N0} waves), extent {tally.Extent / tally.Many:0.#} each; ")
                .Append($"{tally.Caught} caught worth {tally.CaughtWeight:N0}");
        }

        return said.ToString();
    }

    /// <summary>The running score, so the number on screen can be accounted for. See Scoring.</summary>
    public static Scoring Scoring { get; set; }

    /// <summary>How far the game loads and drops things here. See Streaming.</summary>
    public static Streaming Streaming { get; set; }

    /// <summary>
    /// Raised by the button beside the dump key, lowered by the tick that acts on it.
    ///
    /// **A flag rather than the call, because the settings menu is not where a dump can be taken.**
    /// Write needs the plugin and the GameController, and a CustomNode is handed neither - it is a
    /// lambda the settings parser owns. Raising a flag puts the work back on the tick that already
    /// answers the key, so the button and the key are one path and not two.
    /// </summary>
    public static bool AskedForInSettings;

    /// <summary>What the last dump did, for the row beside the button. Empty until one is taken.</summary>
    public static string LastWritten = "";

    /// <summary>
    /// The game, for the few describers that are reached without it.
    ///
    /// Describe takes an entity and nothing else - it is called from Section, which is called per
    /// bucket - and threading a GameController through both to read one terrain height would touch
    /// every caller. Set where the dump starts and read nowhere else.
    /// </summary>
    private static GameController _dumping;

    public static void Write(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc, int range)
    {
        _dumping = gc;

        var builder = new StringBuilder();

        try
        {
            Describe(builder, gc, range);
        }
        catch (Exception ex)
        {
            builder.AppendLine();
            builder.AppendLine($"!! the dump itself threw: {ex}");
        }

        try
        {
            var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"autoexpedition_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, builder.ToString());

            // The site itself beside the dump, so the route model can be worked on without the game.
            // Written every time: it is a couple of hundred kilobytes and the alternative is another
            // trip back to a dig site when it turns out to be wanted. See Capture.
            Safe.Try(() => Capture.Write(plugin.GameController, plugin.Settings,
                Scan?.Targets, directory));
            LastWritten = System.IO.Path.GetFileName(path);
            DebugWindow.LogMsg($"[AutoExpedition] Wrote {path}", 10f);

            Prune(directory, plugin.Settings.Debug.MaxDumps.Value);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the dump: {ex.Message}", 10f);
        }
    }

    private static void Describe(StringBuilder b, GameController gc, int range)
    {
        b.AppendLine($"AutoExpedition dump - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var area = Safe.Read(() => gc?.Area?.CurrentArea, null);
        b.AppendLine($"area: {Safe.Read(() => area.DisplayName, "?")} ({Safe.Read(() => area.Name, "?")}), " +
                     $"level {Safe.Read(() => gc.IngameState.Data.CurrentAreaLevel, 0)}, " +
                     $"dimensions {Describe(Safe.Read(() => gc.IngameState.Data.AreaDimensions, default))}");

        var player = Safe.Read(() => gc.Player, null);
        b.AppendLine($"player grid: {Describe(Safe.Read(() => player.GridPos, default))}");

        DetonatorSection(b, gc);
        Window(b, gc);
        Labels(b, gc);
        TerrainGrid(b, gc, Scan);
        MapStats(b, gc);
        RuneTable(b, gc);
        WeightTable(b);
        Guards(b);
        Relics(b);
        RuneSlots(b, gc);
        EveryRecipe(b, gc);
        AtlasPassives(b, gc);
        RemnantSlotsFromTheClient(b);
        Entities(b, gc, range);
    }

    // ------------------------------------------------------------------ detonator

    private static void DetonatorSection(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("=== detonator (IngameUi.ExpeditionDetonatorElement) ===");

        var element = Safe.Read(() => gc.IngameState.IngameUi.ExpeditionDetonatorElement, null);

        if (element == null)
        {
            b.AppendLine("  no element - not in an encounter, or the offset has moved");
            return;
        }

        b.AppendLine($"  address {Safe.Read(() => element.Address, 0L):X}, " +
                     $"visible {Safe.Read(() => element.IsVisible.ToString(), "?")}, " +
                     $"RemainingExplosives {Safe.Read(() => element.RemainingExplosives, -1)}");

        b.AppendLine($"  RevertExplosiveButton: {DescribeElement(Safe.Read(() => element.RevertExplosiveButton, null))}");
        b.AppendLine($"  ToggleExplosivePlacementButton: {DescribeElement(Safe.Read(() => element.ToggleExplosivePlacementButton, null))}");

        var info = Safe.Read(() => element.Info, null);

        if (info == null)
        {
            b.AppendLine("  no Info");
            return;
        }

        b.AppendLine($"  IsExplosivePlacementActive   {Safe.Read(() => info.IsExplosivePlacementActive.ToString(), "?")}");
        b.AppendLine($"  TotalExplosiveCount          {Safe.Read(() => info.TotalExplosiveCount, -1)}");
        b.AppendLine($"  panel filled in for this site  {(Safe.Read(() => Detonator.PanelReady(gc), false) ? "yes" : "NOT YET - counts above mean nothing, an assumed set is used and the routing grid is not there either")}");
        b.AppendLine($"  PlacedExplosiveCount         {Safe.Read(() => info.PlacedExplosiveCount, -1)}");
        b.AppendLine($"  RemainingExplosiveCount      {Safe.Read(() => info.RemainingExplosiveCount, -1)}");
        b.AppendLine($"  DetonatorGridPosition        {Describe(Safe.Read(() => info.DetonatorGridPosition, default))}");
        b.AppendLine($"  LastExplosivePosition        {Describe(Safe.Read(() => info.LastExplosivePosition, default))}");
        b.AppendLine($"  PlacementIndicatorGridPosition {Describe(Safe.Read(() => info.PlacementIndicatorGridPosition, default))}");

        // The same thing at full precision, off the indicator entity rather than the panel. The
        // panel rounds to a cell, and a cell is not what the game answers about. See
        // Detonator.PlacementIndicatorGridPosition.
        var pointed = Safe.Read(() => Detonator.PlacementIndicatorGridPosition(gc), default(System.Numerics.Vector2));

        // **The cursor cell beside the landing cell, because the answer belongs to the pair.**
        //
        // Measured: the indicator landed on (1220,1822) twice, the same entity at the same position
        // both times, and read blocked=1 when the cursor was on that cell and blocked=0 when the
        // cursor was three cells away at (1223,1824) and the indicator clamped back onto it. So the
        // landing spot does not decide it - where the cursor was asking from does. Refused is keyed
        // on the landing cell alone and cannot hold that; Settled already keys on a pair. See
        // Refused.Allowed and NOTES section 9.
        var cursor = Safe.Read(() => gc.IngameState.ServerData.GridMousePosition, default);

        b.AppendLine(pointed != default
            ? $"  indicator entity: landing ({pointed.X:0.###},{pointed.Y:0.###})  " +
              $"cursor ({cursor.X},{cursor.Y})  " +
              $"blocked {(Detonator.PlacementIndicatorIsRed(gc) ? 1 : 0)}" +
              (pointed.X != cursor.X || pointed.Y != cursor.Y
                  ? "  <- CLAMPED, so this answer is about the pair"
                  : "  <- cursor is on the landing cell")
            : $"  indicator entity: not on screen (cursor ({cursor.X},{cursor.Y}))");

        // **What the model says about the link the cursor is asking for, beside what the game did.**
        //
        // The walked-plan section answers this for a plan, and a plan is exactly what there is not
        // when somebody is standing over a spot that will not take an explosive - "no plan to walk"
        // was the whole of that section on the dump taken to debug a refused link. This pair is
        // always available: the game states the last explosive and the cursor, and reports where it
        // clamped to, so the model's answer for the same pair can sit next to it.
        //
        // Wired against straight, because reach is a budget on the routed wire and a link inside
        // the straight-line range can be outside the routed one. A model landing that differs from
        // the game's is the routine and its translation parting company; agreement with the game
        // clamping means the model is right and something upstream is not asking it.
        var seat = Safe.Read(() => Detonator.LastExplosiveGridPosition(gc), default(System.Numerics.Vector2));

        if (seat != default && cursor.X != 0)
        {
            var want = new System.Numerics.Vector2(cursor.X, cursor.Y);
            var ground = Safe.Read(() => Terrain.Read(gc), null);
            var range = Safe.Read(() => Detonator.PlacementRange(gc), 0f);

            if (ground == null)
            {
                b.AppendLine("  the model on this pair: no ground model, so nothing to ask");
            }
            else if (!ground.Routing)
            {
                b.AppendLine("  the model on this pair: NOT ROUTING - the coarse grid was refused, " +
                             $"so every reach answer is the straight line ({Terrain.Broken ?? "no reason given"})");
            }
            else
            {
                var apart = System.Numerics.Vector2.Distance(seat, want);
                var wired = Safe.Read(() => ground.Wired(seat, want), apart);
                var lands = Safe.Read(() => ground.Landing(seat, want, range),
                    default(System.Numerics.Vector2));

                b.AppendLine($"  the model on this pair: ({seat.X:0},{seat.Y:0}) -> " +
                             $"({want.X:0},{want.Y:0})  {apart:0.#} straight, {wired:0.#} wired " +
                             $"of {range:0.#}  " +
                             (Safe.Read(() => ground.Reaches(seat, want, range), false)
                                 ? "REACHES"
                                 : "does NOT reach") +
                             (lands == default(System.Numerics.Vector2)
                                 ? "  (no routed landing - the wire model is not in play)"
                                 : $", lands it at ({lands.X:0},{lands.Y:0})") +
                             (ground.Forbids(want) ? "  FORBIDDEN VOLUME" : ""));
            }
        }

        var placed = Safe.Read(() => info.PlacedExplosiveGridPositions, null);
        b.AppendLine($"  PlacedExplosiveGridPositions ({placed?.Length.ToString() ?? "null"}): " +
                     $"{(placed == null ? "" : string.Join(", ", placed.Select(Describe)))}");

        var encounters = Safe.Read(() => info.Encounters, null);

        // How many expeditions this map has and how many banners have been seen, together, because
        // the pair is the whole test for the bug that bricks a site. See Finished.Seen.
        b.AppendLine($"  \"Expedition Complete\" banners seen in this area: {Finished.Seen}" +
                     $"; expeditions with nothing placed: {Safe.Read(() => Detonator.ExpeditionsNeverStarted(gc), -1)}" +
                     (Finished.Seen > 0 && Safe.Read(() => Detonator.ExpeditionsNeverStarted(gc), 0) > 0
                         ? "  <- A BANNER WITH AN UNTOUCHED SITE. This is the game bug that leaves " +
                           "the other site unable to accept explosives; it cannot be undone."
                         : ""));

        b.AppendLine($"  Encounters ({encounters?.Count.ToString() ?? "null"}):");

        foreach (var encounter in encounters ?? new List<ExileCore2.PoEMemory.Elements.ExpeditionDetonatorEncounter>())
        {
            b.AppendLine($"    detonator {Describe(Safe.Read(() => encounter.DetonatorGridPosition, default))}, " +
                         $"last {Describe(Safe.Read(() => encounter.LastExplosivePosition, default))}, " +
                         $"placed {Safe.Read(() => encounter.PlacedExplosiveCount, -1)}/" +
                         $"{Safe.Read(() => encounter.TotalExplosiveCount, -1)} " +
                         // Both, because the live one goes back to nought on detonation and the
                         // difference between the two is how a finished site is told from an
                         // untouched one. See Detonator.ExplosivesEverPlacedHere.
                         $"(ever {Safe.Read(() => Detonator.ExplosivesEverPlacedHere(new System.Numerics.Vector2(
                             encounter.DetonatorGridPosition.X, encounter.DetonatorGridPosition.Y)), -1)}, " +
                         $"set off {Safe.Read(() => Detonator.SetOff(new System.Numerics.Vector2(
                             encounter.DetonatorGridPosition.X, encounter.DetonatorGridPosition.Y))
                             ? "yes" : "not seen", "?")}), " +
                         $"active {Safe.Read(() => encounter.IsExplosivePlacementActive.ToString(), "?")} " +
                         "(placement mode, NOT the activated state below)");
        }

        Detonating(b, gc);
    }

    /// <summary>
    /// What Cleared is waiting on, which is one number and was nowhere in this file.
    ///
    /// **The yellow chain retracts link by link as its blasts are fought out, and only then.** A
    /// ring goes when its explosive is down; a LINE goes when nothing it unearthed is left standing
    /// - see Cleared. The whole of that rests on Detonator.ExplosivesDetonated answering one or more, and it
    /// answers by finding the detonator icon within three grid units of Site and reading its
    /// "activated" state.
    ///
    /// Two ways that returns nothing, and the drawing looks identical either way: the state never
    /// rises, or Site names the other detonator - a map holds two, and the untouched one is a
    /// perfectly good match for a test that only measures distance. So both are printed: what the
    /// site is, what each icon is, how far apart they are, and what every state on them reads.
    /// </summary>
    private static void Detonating(StringBuilder b, GameController gc)
    {
        var site = Detonator.DetonatorGridPosition(gc);
        var said = Detonator.ExplosivesDetonated(gc);

        b.AppendLine();
        b.AppendLine($"  Detonated() = {said}" +
                     (said < 1
                         ? "  <- BELOW ONE, so Cleared does nothing and no link ever stops drawing"
                         : "  - Cleared is free to run"));
        b.AppendLine($"  Site() = {Describe(site)}, matched against icons within 3 grid units");

        // The same list Detonated reads, so the diagnostic and the thing it diagnoses cannot
        // disagree about what is there. See Detonator.ExplosivesDetonated.
        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities, null);

        var found = 0;

        foreach (var entity in entities ?? [])
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("Expedition/ExpeditionDetonator", StringComparison.Ordinal) < 0)
                continue;

            found++;

            var at = Safe.Read(entity, static e => e.GridPos, System.Numerics.Vector2.Zero);
            var away = site == System.Numerics.Vector2.Zero ? -1f : System.Numerics.Vector2.Distance(at, site);
            var states = Safe.Read(entity, static e => e.GetComponent<StateMachine>()?.States, null);

            b.AppendLine($"    icon {Describe(at)}  {away:N1} from Site  " +
                         (away >= 0f && away <= 3f ? "MATCHES" : "skipped - too far") +
                         $"  {metadata}");

            if (states == null)
            {
                b.AppendLine("      no StateMachine - nothing to read");

                continue;
            }

            var says = new List<string>();

            for (var i = 0; i < states.Count; i++)
            {
                says.Add($"{Safe.Read(states[i], static x => x.Name, "?")}=" +
                         $"{Safe.Read(states[i], static x => x.Value, -1L)}");
            }

            b.AppendLine("      states: " + (says.Count > 0 ? string.Join(", ", says) : "none"));
        }

        if (found == 0)
            b.AppendLine("    no detonator icon in the entity list at all");
    }

    /// <summary>
    /// The map's own Expedition numbers.
    ///
    /// Explosion radius and placement range are the two figures the planner cannot do without and
    /// the game does not appear to state outright. These percentages are the nearest thing to a
    /// source, so they are worth having on record beside a chain the player actually placed.
    /// </summary>
    /// <summary>
    /// The combinations window, child by child, so the reroll button can be found.
    ///
    /// ExileCore2 names two buttons on the detonator and none on this window - it exposes Options
    /// and nothing else - so the Liquid Verisium button has no property to read. It has a position
    /// in the child tree, though, and that is stable enough to address by path once somebody has
    /// looked. This is the looking: open the window and dump, and the button is the child whose
    /// rect sits where the button is on screen.
    ///
    /// Every child gets its index, its rect, its text and whether the game considers it visible,
    /// because any of those may be what identifies it.
    /// </summary>
    private static void Window(StringBuilder b, GameController gc)
    {
        b.AppendLine("=== combinations window (IngameUi.Expedition2Window) ===");

        var window = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window, null);

        if (window == null)
        {
            b.AppendLine("  not present");
            b.AppendLine();

            return;
        }

        b.AppendLine($"  visible: {Safe.Read(() => window.IsVisible, false)}");
        b.AppendLine($"  rect:    {DescribeElement(window)}");
        b.AppendLine($"  scroll panel: {Options.Walk(gc)}");
        b.AppendLine($"  scrolling to the pick: {Options.Scroll(gc)}");
        b.AppendLine($"  what the cursor is over: {Panels.Describe(gc)}");
        b.AppendLine("  (park the cursor on the scroll thumb and dump again - the drag refuses " +
                     "unless the game names something covering the point it aims at)");
        b.AppendLine($"  options: {Safe.Read(() => window.Options?.Count ?? 0, 0)}");

        Choice(b, gc);

        b.AppendLine();
        b.AppendLine("  children (path, rect, visible, text):");

        Children(b, window, "", 0);
        b.AppendLine();
    }

    /// <summary>
    /// How the green border decided where to go.
    ///
    /// Three dumps went by with "the green is on the most expensive one" and no way to tell which
    /// of four things had happened: the remnant was not identified, its reward list was empty, the
    /// carry rate was zero, or the winning name did not match any option in the window. Each of
    /// those ends in the same fallback and looks identical from outside, so each is printed.
    /// </summary>
    private static void Choice(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("  reward choice:");

        if (Settings == null || Valuation == null || Scan == null)
        {
            b.AppendLine("    not wired up (settings, valuation or scan missing)");

            return;
        }

        var remnant = Options.Whose(gc, Scan);

        if (remnant == null)
        {
            b.AppendLine("    NO REMNANT IDENTIFIED - so nothing is picked and nothing is drawn. " +
                         "Whose() answers with the remnant the run opened the window for, or with " +
                         "the one remnant whose fixed rune sits at the position every offered " +
                         "recipe agrees on. It has no other answer and no fallback.");

            foreach (var line in Options.Explain(gc, Scan, Valuation).Split('\n'))
                b.AppendLine("    " + line);

            Standing(b, gc);

            return;
        }

        b.AppendLine($"    remnant at ({remnant.Grid.X:0},{remnant.Grid.Y:0}), sockets {remnant.Sockets}, " +
                     $"rewards {remnant.Rewards.Count}, passing {remnant.Passing?.Count ?? -1}, " +
                     $"activated {remnant.State("activated")}");
        b.AppendLine("    every remnant's activated state, to check the 2 follows the player:");
        Standing(b, gc);

        var carry = Options.Carried(Settings, Valuation, remnant, gc, Scan, Plan);

        b.AppendLine($"    carry rate {carry:0.####} exalts a point" +
                     (carry <= 0d
                         ? "  <- ZERO, so propagation cannot change the answer. Needs a reward " +
                           "point worth, propagating slots, and monsters downstream on the plan."
                         : ""));

        if (remnant.Rewards.Count == 0)
        {
            b.AppendLine("    NO REWARD LIST - nothing priced yet, so the window falls back.");

            return;
        }

        var near = Options.Locally(Settings, Valuation, remnant, gc, Scan, Plan);

        b.AppendLine($"    local rate {near:0.####} exalts a point - what a rune that does NOT carry " +
                     "forward is worth here, over the monsters this remnant's own blast unearths");

        var money = Options.Money(Settings);

        var take = Options.Take(remnant.Rewards, carry, near, money);

        // The runes in brackets are the ones that combination puts into a PROPAGATING slot. A rune
        // the remnant offers somewhere else does nothing for the chain however good it is, and this
        // is where "there was an Opulent available and it did not take it" gets answered.
        // **How many runes each slot could still be, because one is a reading and several is not.**
        //
        // Valuation.Passing lists every rune any reachable recipe could put in a slot, so a slot
        // showing one has been narrowed to the rune that is there and a slot showing thirty-two has
        // not been narrowed at all. The objective prices the strongest of them - see
        // Weighing.StrongestRunePerPropagatingSlot, which used to price all of them - so the count is the difference
        // between a remnant that is known and one that is being guessed at.
        b.AppendLine($"    propagating slots: " +
                     (remnant.Passing is { Count: > 0 }
                         ? string.Join(", ", remnant.Passing.Select(x =>
                             $"slot {x.Slot} ({x.Runes?.Count ?? 0} " +
                             ((x.Runes?.Count ?? 0) == 1 ? "rune, read" : "candidates, UNRESOLVED") + ")"))
                         : "none - nothing this remnant offers can carry forward"));

        for (var i = 0; i < remnant.Rewards.Count; i++)
        {
            var reward = remnant.Rewards[i];

            b.AppendLine($"      {(i == take ? "->" : "  ")} {reward.Name,-42} " +
                         $"value {reward.Value,10:N2}  carries {reward.Carries,6:0.#}%  " +
                         $"local {reward.Local,6:0.#}%  " +
                         $"counts {reward.Value * money,9:N2}  " +
                         $"total {reward.Value * money + reward.Carries * carry + reward.Local * near,10:N2}  " +
                         $"[{string.Join(" ", reward.Carrying ?? [])}]");
        }

        // Where one name covers more than one recipe, say so and show them - otherwise the ground
        // figure and the window figure differ with nothing to explain the gap. See Valuation.Variants.
        foreach (var reward in remnant.Rewards)
        {
            var variants = Valuation?.Variants(remnant.Entity, reward.Name)?.ToList();

            if (variants is not { Count: > 1 })
                continue;

            b.AppendLine($"      \"{reward.Name}\" is {variants.Count} different recipes - the list " +
                         "above shows the dearest, the window offers whichever the game picked:");

            foreach (var one in variants)
            {
                b.AppendLine($"          pays {one.Count}x {one.Reward}  worth {one.Value:N2}" +
                             (one.Hand ? "  (hand price)" : ""));
            }
        }

        var want = remnant.Rewards[take].Name;
        var options = Safe.Read(() => gc.IngameState.IngameUi.Expedition2Window.Options, null);
        var matched = false;
        var index = -1;

        b.AppendLine($"    wants \"{want}\"; the window offers:");

        foreach (var option in options ?? [])
        {
            index++;

            var visible = Safe.Read(() => option.IsVisible, false);
            var recipe = Safe.Read(() => option.Recipe, null);
            var name = Valuation.Name(recipe) ?? "(unnamed)";
            var same = string.Equals(name, want, StringComparison.OrdinalIgnoreCase);

            matched |= same;

            // **Where each option actually is, because every other test has proved useless.**
            //
            // The window's own rect is the full height of the screen, so an option outside the
            // scroll viewport is still inside it; and IsVisible reads true for every option whether
            // it is on the page or not. The rectangles are the only thing left that can tell a
            // clickable option from one the window has scrolled past, and the parent is here too
            // because whatever clips them is what the test has to be against.
            var at = Safe.Read(() => option.GetClientRectCache, default(ExileCore2.Shared.RectangleF));
            var parent = Safe.Read(() => option.Parent?.GetClientRectCache ?? default,
                default(ExileCore2.Shared.RectangleF));

            b.AppendLine($"      [{index}] {(same ? "MATCH " : "      ")}visible {visible,-5}  " +
                         $"{name,-42} {Valuation.Value(recipe),8:N2}  " +
                         $"at ({at.X:0},{at.Y:0} {at.Width:0}x{at.Height:0})  " +
                         $"in ({parent.X:0},{parent.Y:0} {parent.Width:0}x{parent.Height:0})" +
                         $"  clickable ({Options.Clickable(option).X:0},{Options.Clickable(option).Y:0} " +
                         $"{Options.Clickable(option).Width:0}x{Options.Clickable(option).Height:0})");

            // **Only for the one being clicked, and only because the row is not the button.**
            //
            // A row the cursor sits squarely inside can still refuse the click: the window names
            // the ROW under the pointer, and the thing that accepts a press is a child of it. The
            // rows are not even the same shape - a selected one measured 666x108 against its
            // siblings' 666x63, and the extra height is the rune strip, which is exactly where the
            // middle of the taller rectangle lands.
            //
            // So the children are listed, with what the aim point would hit. Whichever one holds
            // the reward rather than the runes is what the click should be aimed at, and no amount
            // of reasoning about the parent rectangle can say which that is.
            if (!same)
                continue;

            var kids = Safe.Kids(option);

            b.AppendLine($"          children ({kids?.Count.ToString() ?? "null"}) - the aim point " +
                         "is marked, and the one to click is whichever holds the reward:");

            for (var k = 0; k < (kids?.Count ?? 0); k++)
            {
                var kid = kids[k];
                var box = Safe.Read(() => kid.GetClientRectCache, default(ExileCore2.Shared.RectangleF));
                // **The point the plugin would actually send the cursor to**, which is the middle
                // of what Clickable hands back rather than the middle of the row. Those are only
                // the same when no icon is in the way, and the whole question here is what happens
                // when one is. A synthetic row centre printed here once already sent this hunt off
                // after the wrong five pixels.
                var spot = Options.Clickable(option);
                var aim = new System.Numerics.Vector2(spot.X + spot.Width / 2f,
                    spot.Y + spot.Height / 2f);
                var on = aim.X >= box.Left && aim.X <= box.Right &&
                         aim.Y >= box.Top && aim.Y <= box.Bottom;

                b.AppendLine($"            [{k}] ({box.X:0},{box.Y:0} {box.Width:0}x{box.Height:0})" +
                             $"  visible {Safe.Read(() => kid.IsVisible, false),-5}" +
                             $"  kids {Safe.Read(() => kid.ChildCount, -1),-3}" +
                             $"  text \"{Safe.Read(() => kid.Text, "") ?? ""}\"" +
                             (on ? "   <- THE AIM POINT LANDS HERE" : ""));
            }
        }

        if (!matched)
        {
            b.AppendLine("    NO MATCH - the wanted reward is not among the window's options, so " +
                         "the window falls back to plain price. Either the reward list belongs to " +
                         "a different remnant, or it is stale (rolled since it was read).");
        }
    }

    /// <summary>Every remnant and how far it is, when the one underfoot could not be settled.</summary>
    private static void Standing(StringBuilder b, GameController gc)
    {
        var player = Safe.Read(() => gc.Player.GridPos, System.Numerics.Vector2.Zero);

        b.AppendLine($"    player at ({player.X:0},{player.Y:0})");

        foreach (var target in Scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant)
                continue;

            // Whether a rolled remnant's locked combination was found among its priced rewards.
            //
            // **This is the condition the planner's pin depends on and it cannot be checked by eye.**
            // A rolled remnant is stuck with one combination, so the search is told to value it at
            // that one alone - and it finds it by matching the game's name against the priced list.
            // If the two ever disagree the match falls through, the remnant goes back to being
            // valued at the best of everything it could have been, and nothing says so. Saying so.
            var chosen = Valuation?.ChosenName(target.Entity);
            var pinned = "";

            if (target.Rerolled)
            {
                // Asked the way the pin itself asks - by recipe, falling back to the name only
                // where the game states no id. Comparing names here reported a pin as resolved
                // whenever ANY combination yielded the same reward, which is the case this exists
                // to catch. See Planning.Pinned.
                var wanted = Valuation?.ChosenRecipeId(target.Entity);
                var found = false;

                foreach (var reward in target.Rewards)
                {
                    found |= reward.Recipe.Length > 0 && !string.IsNullOrEmpty(wanted)
                        ? string.Equals(reward.Recipe, wanted, StringComparison.Ordinal)
                        : string.Equals(reward.Name, chosen, StringComparison.OrdinalIgnoreCase);
                }

                pinned = found
                    ? "  ROLLED, pinned to its combination"
                    : "  ROLLED BUT ITS COMBINATION IS NOT IN THE PRICED LIST - valued at the best " +
                      "of all of them";
            }

            b.AppendLine($"      remnant ({target.Grid.X:0},{target.Grid.Y:0})  " +
                         $"{System.Numerics.Vector2.Distance(player, target.Grid):0.#} away  " +
                         $"rewards {target.Rewards.Count}  activated {target.State("activated")}  " +
                         $"selected {chosen ?? "-"}  " +
                         $"offers {string.Join(", ", target.Rewards.Take(3).Select(r => r.Name))}" +
                         pinned +
                         // **Everything a reroll is supposed to move**, because one did not wake the
                         // rehearsal and reading the code could not say which link failed. A roll is
                         // meant to change is_rerolled or the socket count, which forgets the cached
                         // rewards, which moves the site's fingerprint, which clears the rehearsal's
                         // done flag. Four links, and the dump showed none of them.
                         $"  [rolled now {target.Rerolled}, remembered {target.WasRolled}, " +
                         $"sockets {target.Sockets}, rewards cached {target.Rewards.Count}]");
        }
    }

    /// <summary>
    /// Why the post-expedition pass does or does not have anything to do.
    ///
    /// Every gate between pressing the key and a click, printed with its answer. There are six of
    /// them and they all end in the run doing nothing, so from outside they are indistinguishable -
    /// which is how "F4 does not open things" stayed a guess through two rounds of looking.
    /// </summary>
    private static void Chores(StringBuilder b, GameController gc)
    {
        b.AppendLine("=== the post-expedition pass ===");

        if (Settings == null || Scan == null)
        {
            b.AppendLine("  not wired up");
            b.AppendLine();

            return;
        }

        var after = Settings.Automation.PostExpedition;

        b.AppendLine($"  automate {Settings.Automation.Enable.Value}, " +
                     $"shatter {after.Shatter.Value}, chests {after.Chests.Value}, " +
                     $"placement enabled {Settings.Automation.PreExpedition.Enable.Value}, " +
                     $"walking {Placement.Walking(gc)}, explosives left {Detonator.ExplosivesInHand(gc)}");

        // What the SCAN thinks is here, which is a different question from what the entity list
        // holds - and the difference is where two rounds of this have gone missing.
        var near = 0;

        foreach (var target in Scan.Targets)
        {
            var away = System.Numerics.Vector2.Distance(
                Safe.Read(() => gc.Player.GridPos, System.Numerics.Vector2.Zero), target.Grid);

            if (away > 60f)
                continue;

            near++;

            // Everything the chore pass asks about a remnant, in the order it asks it.
            //
            // A lone remnant sitting twenty five grid away while the pass reported "nothing within
            // 120 grid" could be explained four different ways and the dump said none of them. Each
            // test now prints its own answer, so a rejection names itself.
            var loose = Scan != null && Scan.Loose(gc, target.Grid);
            var chosen = target.Kind == TargetKind.Remnant && target.Entity != null
                ? Safe.Read(() => Valuation?.ChosenName(target.Entity), null)
                : null;

            // **What Where actually returns, which is what everything is drawn from.**
            //
            // Where caches on first call and has two branches - the entity's own position for a live
            // target, the terrain height for one the game has not loaded - so a target first seen from
            // across the map keeps the height it was given then, whatever it reports later. The entity
            // z and ground z lines in the entity listing say what the two branches WOULD give; this
            // says which one is in force. Without it there is no telling a fix that did not apply from
            // one that applied and was not the cause.
            var drawn = Safe.Read(() => target.Where(gc), default(System.Numerics.Vector3));
            var under = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(target.Grid),
                default(System.Numerics.Vector3));

            b.AppendLine($"    scan: {target.Kind,-8} ({target.Grid.X:0},{target.Grid.Y:0})  " +
                         $"{away:0.#} away  entity {(target.Entity == null ? "gone" : "live")}  " +
                         $"drawn at z {drawn.Z:0.#} (ground {under.Z:0.#}" +
                         (MathF.Abs(drawn.Z - under.Z) > 20f ? ", NOT ON THE GROUND" : "") + ")  " +
                         $"activated {target.State("activated")}  spent {target.Spent}" +
                         (target.Kind == TargetKind.Remnant
                             ? $"  passing {target.Passing?.Count ?? -1}" +
                               // **The pair that decides whether a remnant is priced at all**, and
                               // it was in no dump. Scan refuses to store a reward list read from
                               // encounter data that states a fixed rune POSITION and no fixed
                               // RUNE - a read caught mid-flight, where Reachable has nothing to
                               // filter on and hands back the generic list for the socket count.
                               // The refusal is right and silent: a remnant stuck in it reads
                               // "rewards 0" for ever, with no reward list, no choices, and a
                               // propagation priced off every rune its slots could hold. Saying the
                               // two values is the difference between reading that as a site the
                               // plugin has not got to yet and reading it as the guard firing every
                               // sweep. See Scan's re-pricing and Weighing.StrongestRunePerPropagatingSlot.
                               $"  fixed {FixedOf(target)}" +
                               // Only where it matters. A remnant with rewards is one the filter
                               // let through, and the breakdown is noise beside it.
                               (target.Rewards.Count == 0
                                   ? Environment.NewLine + "      why no rewards: " +
                                     (Safe.Read(() => Valuation?.Sieve(target.Entity), null)
                                      ?? "valuation not wired up")
                                   : "") +
                               $"  loose {loose}  rerolled {target.Rerolled}  chosen " +
                               (chosen == null ? "UNREADABLE" : chosen.Length == 0 ? "NOTHING" : $"\"{chosen}\"") +
                               $"  combinations button " +
                               (Placement.Button(gc, target.Entity, Placement.CombinationsPath).Rect
                                is { Width: > 0f } r
                                   ? $"{r.Width:0}x{r.Height:0} at ({r.X:0},{r.Y:0})"
                                   : "NOT FOUND")
                             : ""));
        }

        if (near == 0)
            b.AppendLine("    scan: nothing at all within 60 grid");

        var player = Safe.Read(() => gc.Player.GridPos, System.Numerics.Vector2.Zero);
        var found = 0;

        foreach (var target in Scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant || !target.Spent)
                continue;

            var spent = true;
            var chest = false;
            var path = Placement.ShatterPath;
            var button = Placement.Button(gc, target, path);
            var away = System.Numerics.Vector2.Distance(player, target.Grid);

            // Only the ones that could plausibly be reached, or the list is the whole map.
            if (away > 120f)
                continue;

            found++;

            b.AppendLine($"  {(spent ? "spent remnant" : "chest        ")} " +
                         $"({target.Grid.X:0},{target.Grid.Y:0})  {away:0.#} away  " +
                         $"entity {(target.Entity == null ? "gone" : "live")}  " +
                         (chest ? $"opened {target.Opened}  " : "") +
                         $"button {(button.Rect.Width > 0f ? $"{button.Rect.Width:0}x{button.Rect.Height:0} at ({button.Rect.X:0},{button.Rect.Y:0})" : "NOT FOUND")}" +
                         (button.Lit ? "  lit" : ""));
        }

        // The chests come from the entity list, because the scan's chests are MARKERS and the
        // blast has already eaten them by the time any of this matters.
        var chests = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore2.Shared.Enums.EntityType.Chest,
                out var of) ? of : null, null);

        foreach (var entity in chests ?? new List<Entity>())
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("LeaguesExpedition", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var grid = Safe.Read(entity, static e => e.GridPos, System.Numerics.Vector2.Zero);
            var away = System.Numerics.Vector2.Distance(player, grid);

            if (away > 120f)
                continue;

            found++;

            var rect = Placement.Button(gc, entity, []).Rect;

            b.AppendLine($"  chest         ({grid.X:0},{grid.Y:0})  {away:0.#} away  " +
                         $"opened {Safe.Read(entity, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false)}  " +
                         $"label {(rect.Width > 0f ? $"{rect.Width:0}x{rect.Height:0} at ({rect.X:0},{rect.Y:0})" : "NOT FOUND")}" +
                         (rect.Width > 0f
                             ? Placement.Reachable(gc, rect,
                                 Safe.Read(entity, static e => e.Id, 0u)) is { At.X: > 0f } spot
                                 ? $"  clickable at ({spot.At.X:0},{spot.At.Y:0}) room {spot.Room:0.#}"
                                 : "  ENTIRELY COVERED"
                             : ""));
        }

        if (found == 0)
            b.AppendLine("  nothing within 120 grid that could be shattered or opened");

        // What the hover test sees, for whatever the cursor happens to be on right now. Dump with
        // the cursor parked on a chest's label and this says which comparison is failing.
        b.AppendLine();
        b.AppendLine("  the hover test, for every chest near you:");

        foreach (var entity in chests ?? new List<Entity>())
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.IndexOf("LeaguesExpedition", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var grid = Safe.Read(entity, static e => e.GridPos, System.Numerics.Vector2.Zero);

            if (System.Numerics.Vector2.Distance(player, grid) > 120f ||
                Safe.Read(entity, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false))
                continue;

            b.AppendLine($"    ({grid.X:0},{grid.Y:0})  {Placement.Explain(gc, entity)}");
        }

        b.AppendLine();
    }

    /// <summary>
    /// Every visible piece of text in the interface, with the path to reach it.
    ///
    /// A net rather than a lookup, and it exists because the thing being looked for has no name.
    /// ExileCore2 names the combinations window, the detonator and the vendor panels, and nothing
    /// else about expeditions - so a popup saying the encounter is over is reachable only by
    /// walking the tree and reading what is written on it.
    ///
    /// The same method found the combinations button: dump while the thing is on screen, look for
    /// what was not there before, and address it by path afterwards. Text rather than geometry
    /// because a popup says something, and a string is a far better handle than a rectangle.
    ///
    /// Visible only, and six deep. The invisible half of the interface is enormous and permanently
    /// present, and including it would bury the one line that matters.
    /// </summary>
    private static void Texts(StringBuilder b, GameController gc)
    {
        if (Placement is { Unlit.Count: > 0 } marks)
        {
            var said = new List<string>();

            foreach (var at in marks.Unlit)
                said.Add($"({at.X:0},{at.Y:0})");

            b.AppendLine($"  markers the last blast did not get ({marks.Reason}): {said.Count} - " +
                         string.Join(", ", said) + " (ringed in red on the ground)");
        }
        else
        {
            b.AppendLine("  markers the last blast did not get: none recorded");
        }

        if (Placement is { Said.Length: > 0 })
        {
            b.AppendLine($"  the last thing placement said, {(DateTime.UtcNow - Placement.SaidAt).TotalSeconds:0.#}s " +
                         $"ago: {Placement.Said}");
        }

        b.AppendLine($"  under the cursor right now: {Panels.Describe(gc)}");

        // The same question the placement loop asks, at the place it asks it about. See Panels.Why.
        var cursor = new System.Numerics.Vector2(
            Safe.Read(gc, static g => g.IngameState.MousePosX, 0f),
            Safe.Read(gc, static g => g.IngameState.MousePosY, 0f));

        b.AppendLine($"  what covers the cursor at ({cursor.X:0},{cursor.Y:0}): {Panels.Why(gc, cursor)}");

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        if (camera != null && Plan is { Points.Count: > 0 })
        {
            for (var i = 0; i < Plan.Points.Count && i < 6; i++)
            {
                var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(Plan.Points[i]),
                    System.Numerics.Vector3.Zero);

                if (world == System.Numerics.Vector3.Zero)
                    continue;

                var screen = Safe.Read((camera, world),
                    static x => x.camera.WorldToScreen(x.world), System.Numerics.Vector2.Zero);

                if (screen == System.Numerics.Vector2.Zero)
                    continue;

                b.AppendLine($"  what covers spot {i + 1} at ({screen.X:0},{screen.Y:0}): " +
                             Panels.Why(gc, screen));
            }
        }
        b.AppendLine();
        b.AppendLine("=== every visible label in the interface (find popups here) ===");

        var root = Safe.Read(() => gc.IngameState.IngameUi, null);

        if (root == null)
        {
            b.AppendLine("  the interface root is not readable");
            b.AppendLine();

            return;
        }

        var found = 0;

        Walk(b, root, "", 0, ref found);

        if (found == 0)
            b.AppendLine("  nothing visible has any text on it");

        b.AppendLine();
    }

    private static void Walk(StringBuilder b, ExileCore2.PoEMemory.Element element, string path, int depth,
        ref int found)
    {
        if (element == null || depth > 6 || found > 400)
            return;

        var kids = Safe.Kids(element);

        if (kids == null)
            return;

        for (var i = 0; i < kids.Count; i++)
        {
            var kid = kids[i];

            if (kid == null || !Safe.Read(kid, static e => e.IsVisible, false))
                continue;

            var here = path.Length == 0 ? i.ToString() : $"{path},{i}";
            var text = Safe.Read(() => kid.Text, null);

            if (!string.IsNullOrWhiteSpace(text))
            {
                var rect = Safe.Read(kid, static e => e.GetClientRectCache, default);

                b.AppendLine($"  [{here}]  ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})  " +
                             $"\"{text.Trim().Replace("\n", " ")}\"");
                found++;
            }

            Walk(b, kid, here, depth + 1, ref found);
        }
    }

    /// <summary>Walks an element's children, three deep, which is further than a button hides.</summary>
    private static void Children(StringBuilder b, ExileCore2.PoEMemory.Element element, string path, int depth)
    {
        if (element == null || depth > 3)
            return;

        var kids = Safe.Kids(element);

        if (kids == null)
            return;

        for (var i = 0; i < kids.Count; i++)
        {
            var kid = kids[i];

            if (kid == null)
                continue;

            var here = path.Length == 0 ? i.ToString() : $"{path},{i}";
            var text = Safe.Read(() => kid.Text, null);
            var count = Safe.Read(() => (int)kid.ChildCount, 0);

            b.AppendLine($"    [{here}]  {DescribeElement(kid)}  " +
                         $"visible={Safe.Read(() => kid.IsVisible, false)}  kids={count}  " +
                         $"shiny={Safe.Read(() => kid.HasShinyHighlight, false)}  " +
                         // Printed while hunting a signal for "this button is in range". Being
                         // drawn and lighting up both turned out to mean only "drawn": the
                         // combinations button on a remnant slightly too far away passed both, the
                         // click went into the world and walked the character. If either of these
                         // moves with range, it replaces the distance floor in Placement.Near.
                         $"active={Safe.Read(() => kid.IsActive, false)}  " +
                         $"saturated={Safe.Read(() => kid.IsSaturated, false)}  " +
                         $"type={Safe.Read(() => kid.Type, default(ExileCore2.PoEMemory.ElementType))}" +
                         (string.IsNullOrWhiteSpace(text) ? "" : $"  text=\"{text.Trim()}\""));

            Children(b, kid, here, depth + 1);
        }
    }

    /// <summary>
    /// The ground labels on expedition remnants, child by child.
    ///
    /// This is where the Liquid Verisium button lives - the game hangs it off the label it draws
    /// over the remnant, and only when there is a Verisium in the inventory to roll with. The
    /// plugin walks a fixed path inside the label to find it, and this is how that path is checked
    /// or corrected: the button is the child whose rect sits where the button is on screen.
    ///
    /// If the labels come out with no children, the reason is almost certainly that no Verisium is
    /// being carried, and the game is not drawing a button at all.
    /// </summary>
    private static void Labels(StringBuilder b, GameController gc)
    {
        b.AppendLine("=== remnant ground labels (IngameUi.ItemsOnGroundLabels) ===");

        var labels = Ground.Labels(gc);

        if (labels == null)
        {
            b.AppendLine("  none visible");
            b.AppendLine();

            return;
        }

        var found = 0;

        foreach (var label in labels)
        {
            var entity = Safe.Read(() => label.ItemOnGround, null);
            var metadata = Safe.Read(() => entity?.Metadata, "") ?? "";

            if (!metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
                continue;

            found++;

            var element = Safe.Read(() => label.Label, null);

            b.AppendLine($"  entity {Safe.Read(() => entity.Id, 0u)}  {metadata}");
            b.AppendLine($"    label {DescribeElement(element)}  " +
                         $"kids={Safe.Read(() => (int)(element?.ChildCount ?? 0), 0)}");

            Children(b, element, "", 0);
        }

        if (found == 0)
            b.AppendLine($"  {labels.Count} labels visible, none of them expedition");

        b.AppendLine();
    }

    /// <summary>
    /// What the targeting grid reads under ground that is placeable by definition.
    ///
    /// The planner can refuse spots the game will not take an explosive on, and that check is off
    /// because the values in the grid have never been established - read as "anything above zero"
    /// it refused every candidate in the first dig site it ran in, which is a predicate that cannot
    /// produce a plan rather than a careful one.
    ///
    /// The detonator stands on placeable ground and so does every marker, so whatever turns up here
    /// is a placeable value. In the dump rather than only on screen because a number read off an
    /// overlay is a number somebody has to transcribe.
    /// </summary>
    /// <summary>
    /// Each link of the plan, walked cell by cell, with whatever stands beside it.
    ///
    /// The dumps kept saying the same unhelpful thing - every planned spot reads 5, and the game
    /// refuses the first one anyway. That is an answer about ENDPOINTS, and an endpoint is not a
    /// segment. This prints the segment: every cell the walk visits, the values it saw, the first
    /// blocked cells if there were any, and the three obstacles nearest the line with how much
    /// they clear it by.
    ///
    /// Either it names the cause or it rules terrain and scenery out together, and both are worth
    /// more than another round of the endpoints reading 5.
    /// </summary>
    private static void Links(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("  the last solve " + (Planning.Routed switch
        {
            null => "had no ground model, so every link was judged on distance alone",
            false => "COULD NOT ROUTE - the coarse grid was refused, so every link was judged on " +
                     "the straight line, and any it proposed can be carried into later solves as " +
                     "a floor",
            _ => "routed, so its links were judged on the wire",
        }));

        b.AppendLine("  each link of the plan, walked:");

        if (Plan == null || Plan.Points.Count == 0)
        {
            b.AppendLine("    no plan to walk");
            return;
        }

        var terrain = Terrain.Read(gc);
        var blocking = Obstacles.Read(gc, Detonator.DetonatorGridPosition(gc), 400f);

        // From the DETONATOR, because the plan starts there. Origin is the last explosive placed,
        // so walking the plan from it on a half-placed chain measured the last link against the
        // first and reported a hundred and twenty five grid hop that nothing had ever planned.
        var from = Detonator.DetonatorGridPosition(gc);
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        for (var i = 0; i < Plan.Points.Count; i++)
        {
            var to = Plan.Points[i];

            var down = false;

            foreach (var at in placed)
                down |= System.Numerics.Vector2.Distance(at, to) < 1f;

            // **The straight line was the only length here, and the model does not use it.**
            //
            // Reach is a budget on the routed wire, so a link reading "86.6 grid" against a range
            // of 90 looks comfortable while the wire the game lays is over - and the dump said
            // nothing either way. Both lengths, side by side, and the landing the model predicts
            // beside the one the detonator section reports the game chose: where those two
            // disagree is where the routine and its translation part company, which is the only
            // question this section is here to answer. See Terrain.Wired and Wire.Landing.
            var apart = System.Numerics.Vector2.Distance(from, to);
            var wired = terrain == null ? apart : Safe.Read(() => terrain.Wired(from, to), apart);
            var reach = Detonator.PlacementRange(gc);
            var lands = terrain == null
                ? System.Numerics.Vector2.Zero
                : Safe.Read(() => terrain.Landing(from, to, reach), System.Numerics.Vector2.Zero);

            b.AppendLine($"    {i + 1}. ({from.X:0},{from.Y:0}) -> ({to.X:0},{to.Y:0})  " +
                         $"{apart:0.#} straight, {wired:0.#} wired of {reach:0.#}" +
                         (wired > reach ? "  OVER" : "") +
                         (lands == System.Numerics.Vector2.Zero
                             ? ""
                             : $", model lands it at ({lands.X:0},{lands.Y:0})") +
                         (down ? "  (already placed)" : ""));
            b.AppendLine($"       terrain   {terrain?.Trace(from, to) ?? "grid unreadable"}");
            b.AppendLine($"       scenery   {blocking.Along(from, to)}");

            from = to;
        }
    }

    private static void TerrainGrid(StringBuilder b, GameController gc, Scan scan)
    {
        Texts(b, gc);
        Chores(b, gc);
        b.AppendLine("=== how far the chain is believed to range ===");
        b.AppendLine($"  predicted {Detonator.PlacementRange(gc):0.#} grid " +
                     $"(base {Detonator.BaseReach(gc):0.#}, map {Detonator.PlacementRangePct(gc):+0;-0;+0}%)");
        b.AppendLine($"  base is {(Detonator.Grand(gc) ? "GRAND" : "ordinary")} because the count is " +
                     $"{Detonator.ExplosivesToPlanFor(gc)} against {Detonator.GrandExplosives} - " +
                     $"{Detonator.Counting}");
        b.AppendLine("  (the game's own arithmetic, in integers, so it is the limit rather than " +
                     "an estimate of it - nothing measures it any more)");
        b.AppendLine();
        b.AppendLine("=== what the reset button reaches ===");
        b.AppendLine("  " + ResetAll.Audit(Settings));
        b.AppendLine("  (a reset that misses something looks exactly like one that worked, so the");
        b.AppendLine("   count is the verdict - see ResetAll.Audit)");
        b.AppendLine();
        b.AppendLine("=== how far the game streams entities in ===");
        b.AppendLine("  " + (Streaming?.Describe() ?? "not wired up"));
        b.AppendLine("  (the scouting layer paints anything further than the range setting as unwalked;");
        b.AppendLine("   loads out to says the setting can go at least that high, drops from says it " +
                     "should not go higher)");
        b.AppendLine();
        b.AppendLine("=== what the markers have unearthed so far ===");
        b.AppendLine("  " + (Spawns?.Describe() ?? "not wired up"));
        b.AppendLine("  (written to dumps/spawns.csv when you leave the area, one row per marker)");
        b.AppendLine();
        // **The terrain struct itself, past what ExileCore2 surfaces.** Every published terrain source
        // has been measured against the game's own verdicts and none of them is the placement rule, so
        // this reports the two vectors in the client's terrain struct that no project has ever
        // identified. Read-only - see Peek.
        b.AppendLine("=== the terrain struct, past what ExileCore2 exposes ===");
        b.AppendLine(Peek.Describe(gc));

        // **The game's own routing grid, which is the placement rule itself.** See Peek.Coarse. This
        // is written as a map around the indicator rather than as counts, because the thing worth
        // checking is whether a block the game refused reads non-zero here - and that is only
        // legible laid out in place.
        // The engine's own finished search, which is the only way to see ITS g values rather than
        // infer them from where a bomb landed. See Peek.Searched.
        // What the boundary sweep has proved, and the spots it caught us out on. See Frontier.
        b.AppendLine("=== the boundary sweep ===");
        b.AppendLine($"  {Checked.Here.Count} spots put to the game here, {Checked.Here.Wrong} disagreed");

        foreach (var (x, y) in Checked.Here.Disagreed)
            b.AppendLine($"    the game and this plugin disagree at ({x},{y})");

        b.AppendLine();

        // The engine's own terrain layers, decoded its way, against ExileCore2's arrays.
        // See Peek.Layered - this is the one link still taken on naming rather than reading.
        b.AppendLine("=== the terrain layers as the engine reads them ===");
        b.AppendLine(Peek.Layered(gc));

        b.AppendLine("=== the game's own last A* search ===");
        b.AppendLine(Peek.Searched(gc));

        b.AppendLine("=== the coarse routing grid (ClientExpedition+0x1A8, +0x38) ===");
        b.AppendLine(Coarse(gc));
        b.AppendLine();
        b.AppendLine("=== authored no-placement volumes ===");

        // **Through the reader that resolves the component, not the sweep that guessed at it.**
        // Forbidden.Describe walked the tile objects by hand and looked for the tag at a fixed
        // component slot, which NOTES records as producing only noise - and it took the terrain from
        // IngameData's own address rather than through ClientExpedition, which Peek's own notes warn
        // are different objects. It was reading the wrong structure, which is why it never found
        // anything. Volumes resolves the component through the type's table and reports each stage.
        b.AppendLine(Volumes.Explain(gc, Detonator.DetonatorGridPosition(gc), Terrain.Span));
        b.AppendLine();
        b.AppendLine("=== terrain targeting grid ===");
        b.AppendLine(Terrain.Describe(gc, scan?.Targets ?? new List<Target>(), Plan));
        b.AppendLine("  (known good = under the detonator and every marker, which is ground content " +
                     "STANDS on, not");
        b.AppendLine("   ground an explosive may go on; game agreed = cells the placement indicator " +
                     "confirmed,");
        b.AppendLine("   which is the only placement evidence here; an explosive needs 4 or better)");
        b.AppendLine("  " + Terrain.Agreed(gc, scan?.Targets ?? new List<Target>()));
        b.AppendLine("  what the model believed about each explosive as it landed:");
        b.AppendLine("  " + Landed.Here.Describe());
        b.AppendLine("  " + Terrain.Clearance(gc));
        b.AppendLine("  " + Terrain.Aligns(gc));
        b.AppendLine("  " + Terrain.Puzzling(gc, scan?.Targets ?? new List<Target>()));

        // **Where the routing model and the client disagreed**, which is the list to work from when
        // a run stops with "Wrong spot". Each line has everything needed to reproduce one by hand:
        // the explosive the game routes from, the cell the plan wanted, the cell the cursor was sent
        // to, and where the game put the explosive instead. See PlacementDisagreements.
        b.AppendLine("  placement model: " + PlacementDisagreements.Here.Describe());


        // What a Liquid Verisium is worth on each remnant, and on which one. The advice is built on
        // forty two observed outcomes, so the working matters more than the answer. See Rolling.
        // What repositioning a remnant would cost, which is what decides whether a roll can
        // be priced at its best position rather than where it sits. See Rolling.Positioning
        // and reroll_plan.md. A reading; nothing consults it.
        b.AppendLine("  repositioning: " + Safe.Read(
            () => Rolling.Positioning(Env,
                Chain == null ? null : new List<System.Numerics.Vector2>(Chain), Settings),
            "not measured"));
        b.AppendLine($"  rolling: {Rolling.Here.Telling}");
        b.AppendLine($"    mode {Rolling.Mode(Settings)}" +
                     // Nothing walked is not progress to report, and the STALLED warning below is
                     // about the bound and the walk disagreeing - not about a pass that had nothing
                     // to weigh, which the Telling line above already says.
                     (Rolling.Here.RoundsWalked > 0
                         ? $"; arrangements walked {Rolling.Here.RoundsWalked} of " +
                           $"{Rolling.Here.RoundsNeeded} rounds" +
                           (Rolling.Here.Stalled && Rolling.Here.RoundsWalked < Rolling.Here.RoundsNeeded
                               ? " - STALLED, a pass walked nothing new and deepening stopped short"
                               : "")
                         : "") +
                     (Rolling.Here.Stale ? ", STALE - the chain it was weighed against has been replaced" : "") +
                     (Rolling.Here.Diverted.Length > 0 ? $"; {Rolling.Here.Diverted}" : ""));

        b.AppendLine("    " + (Rolling.Here.Best is { } best
            ? $"ROLL the remnant at ({best.Grid.X:0},{best.Grid.Y:0}) - {best.Why}"
            : "nothing advised: " + Rolling.Here.Quiet));
        if (Rolling.Here.Floored.Length > 0)
            b.AppendLine("    IMPOSSIBLE FIGURE CORRECTED: " + Rolling.Here.Floored);

        // **Is the enumeration buying anything?** Screening is the whole of the reroll latency and
        // grows with the remnant count, so what a free ranking would have advised is worth knowing
        // before any of it is made cheaper. See Rolling.RankingWithoutScoring.
        b.AppendLine("    ranked without scoring: " + Rolling.Here.RankingWithoutScoring);

        if (Rolling.Here.Failure.Length > 0)
        {
            b.AppendLine($"    A REROLL PASS THREW, {Rolling.Here.SinceFailure.TotalSeconds:N0}s ago " +
                         "(kept until the zone changes, so a later pass may have succeeded since):");

            foreach (var line in Rolling.Here.Failure.Split('\n'))
                b.AppendLine("      " + line.TrimEnd());
        }

        b.AppendLine("    " + Priced(Valuation));

        // **Every remnant, not just the one a window happens to be open on.** This check first went
        // into the reward-choice section, which needs the combinations window up and a remnant
        // identified from it - so on a dump with the window closed it simply never ran, and the
        // question it exists to answer went unanswered for want of a click. One name covering
        // several recipes is a fact about the remnant; nothing about it involves the window.
        foreach (var mark in Scan?.Targets ?? [])
        {
            if (mark.Kind != TargetKind.Remnant || mark.Spent)
                continue;

            foreach (var reward in mark.Rewards ?? [])
            {
                var variants = Safe.Read(() => Valuation?.Variants(mark.Entity, reward.Name), null)
                    ?.ToList();

                if (variants is not { Count: > 1 })
                    continue;

                b.AppendLine($"    ({mark.Grid.X:0},{mark.Grid.Y:0}) \"{reward.Name}\" covers " +
                             $"{variants.Count} recipes - the ground shows the dearest at " +
                             $"{reward.Value:N2}, the window offers whichever the game picked:");

                foreach (var one in variants)
                {
                    b.AppendLine($"        pays {one.Count}x {one.Reward}  worth {one.Value:N2}" +
                                 (one.Hand ? "  (hand price)" : ""));
                }
            }
        }
        b.AppendLine("    " + Advising(gc, Scan));
        b.AppendLine("    every remnant weighed:");
        b.AppendLine(Rolling.Here.Verdicts());
        // **Printed as a share, because the absolute number says nothing about whether it is
        // reasonable.** It is a guess multiplied by however many runes a chain happens to carry, and
        // the only way to notice it has taken over the objective is to see what fraction of the
        // **The grouped payout against the additive pool it replaced.**
        //
        // Equal to the penny on a site where nothing has been classified, which is the guarantee
        // that filling the Combines column in is an improvement rather than a migration. A gap here
        // on an unclassified site is a bug in the payout, not a modelling choice. See
        // Planner.Pooled and TableGrammar.Effect's "as" clause.
        b.AppendLine($"  effects that scale other effects: {Planner.Lifting}");

        // **What the table is set to, beside what the chain did with it.** The line above says what
        // the scoring got out of the empower; this one says what it was handed. They answer different
        // questions and the difference between them is the only way to tell an edit that changed
        // nothing from an edit that never arrived. See Wrt.Empowering.
        // **Whether anything on the ground is still quoting an older table.** The staleness this
        // catches is silent by construction: a remnant priced under a withdrawn answer looks exactly
        // like a remnant priced correctly, and the only way to see it is to compare the revision it
        // was priced under against the one in force. See Wrt.Revision and Scan's re-pricing.
        var priced = Scan?.Targets?.Where(t => t?.Rewards?.Count > 0).ToList() ?? [];
        // **One store, so one revision.** This compared against a second counter that tracked a
        // settings subtree of weights; there is no such subtree now - every weight is a row - so the
        // table's own revision is the whole of the answer. See Wrt.Revision.
        var stale = priced.Count(t => t.PricedUnder != Wrt.Revision);

        // **What has been moving the revision, because every bump costs a re-solve.**
        //
        // Planning.Stale watches this number, so a table that moves while nobody is editing it
        // re-solves the site and re-prices every remnant on its own. Seen in the presolve history
        // as a revision climbing from 4 to 39 across five passes on a session where no weight had
        // been touched - and the number alone cannot say who is doing it. See Wrt.LastMovedBy.
        b.AppendLine($"  the reference table has moved {Wrt.TimesMoved} time(s) this session " +
                     $"(and {Wrt.TimesNoted} write(s) recorded an observation and moved no answer, " +
                     "which still write the file); " +
                     $"last move by {Wrt.LastMovedBy}");

        b.AppendLine($"  reference table revision {Wrt.Revision}: " +
                     (priced.Count == 0
                         ? "nothing priced yet"
                         : stale == 0
                             ? $"all {priced.Count} priced remnants are current"
                             : $"{stale} of {priced.Count} priced remnants are STALE - priced under an " +
                               "older table or older weights, so their rewards answer a withdrawn " +
                               "question"));

        var empowers = Wrt.Empowering().ToList();

        b.AppendLine("  what the table says empowers: " +
                     (empowers.Count == 0
                         ? "no row carries the empower word - nothing can lift anything"
                         : string.Join(", ",
                             empowers.Select(e => $"{e.Id} at {e.Percent:0.##}% ({e.Layer})"))));

        // **Asked the way the scoring asks, because that is where the answer went missing.**
        //
        // The row above is found by walking the table's own keys, so it is guaranteed to be found.
        // The scoring never does that: it holds a rune id from the ground and calls Combining and
        // Scope with it, which prefix it and look the row up. Those two paths agree only while the id
        // on the ground matches the id in the table, and a site where the table says fifty and the
        // payout does not move is exactly what it looks like when they do not.
        //
        // So the round trip is printed beside the row: strip the prefix back off, ask as the scoring
        // asks, and show what comes back. A word of "empower" and a matching percentage means the
        // lookup is sound and the fault is further in; a blank word means the id never resolved and
        // the rune is being dropped as an ordinary one worth nothing. See Propagation.Locally.
        foreach (var (id, percent, _) in empowers)
        {
            var name = id.StartsWith("rune:", StringComparison.OrdinalIgnoreCase) ? id[5..] : id;
            var word = Safe.Read(() => Weighing.GroupKeyOfEffect(name), "<threw>");

            // **Asked of Lift, because that is what the scoring now asks.**
            //
            // This read the rate out of the scope cell, which was where an empowering rune kept it
            // while it had nowhere better - a bare number with no target. The rate is the row's effect
            // now and Runes.Scope correctly answers empty for a rune aimed at runes, so reading the
            // scope returned nought and this line reported a disagreement that did not exist. A
            // diagnostic that cries wolf is worse than no diagnostic: the whole point of it is that an
            // evening was once spent on an empower measurement where nothing said whether the setting
            // had arrived.
            var lift = Safe.Read(() => Weighing.Lift(name), -1f);
            var effect = Safe.Read(() => Wrt.Of(Wrt.Id.Rune(name))?.Effect, null) ?? "<none>";

            b.AppendLine($"    asked as the scoring asks: \"{name}\" -> combines \"{word}\", " +
                         $"effect \"{effect}\", lift {lift:0.##}" +
                         (string.Equals(word, Weighing.Empowering, StringComparison.OrdinalIgnoreCase)
                             ? Math.Abs(lift - percent) < 0.01f
                                 ? " - SOUND, the branch is taken and the rate agrees with the table"
                                 : $" - the branch is taken but the rate disagrees with the table's {percent:0.##}"
                             : " - NOT EMPOWERING to the scoring: this rune is being dropped as an " +
                               "ordinary one, so the table's rate reaches nothing"));
        }
        b.AppendLine($"  propagation: {Planner.Propagated:N1} grouped, {Planner.Pooled:N1} pooled" +
                     (Math.Abs(Planner.Propagated - Planner.Pooled) < 0.05d
                         ? " - identical, so every modifier here is in one pool. With runes " +
                           "multiplying that should not happen on a site carrying two different " +
                           "runes"
                         : $" - {Planner.Propagated - Planner.Pooled:+0.0;-0.0} from modifiers that multiply"));

        b.AppendLine($"  the Bait rune: {Curio.Describe()}");

        // What each remnant's waves are wearing, in the form the overlay draws it. Here so the
        // arithmetic can be checked against the remnant rather than taken on trust - the bracket
        // must always reconcile to the number in front of it. See Planner.RuneTallyByRemnant.
        // **What the drawing of that actually depends on, because the figures are only half of it.**
        //
        // The yellow wave line under a remnant needs three things and RuneTallyByRemnant is one: the setting must
        // be on, "Passes above" must be over nought, and the REMNANT must have read its own
        // propagating slots - the overlay skips any remnant whose Passing is null before it ever
        // looks a cell up. A dump showing a full RuneTallyByRemnant and nothing on screen is that gap, and there
        // was no way to see it from here.
        b.AppendLine($"  drawing the waves: in world {Settings?.Display.Remnants.Propagation.ShowWaves.Value}, " +
                     $"in the combinations window {Settings?.Display.Remnants.RuneshapeCombinationsWindow.ShowWavesInWindow.Value}");

        b.AppendLine($"  what each combination row drew:{(char)10}      {Options.Drew}");

        b.AppendLine($"  the last detailed scoring pass: {Planning.Detailed}");

        // **What the solver actually decided, link by link, in one place.**
        //
        // Everything else here describes a consequence - what landed on a remnant's waves, what a
        // chain scored, what the window recommends. None of it states the decision itself: where each
        // explosive goes, and which combination the objective took at each remnant it catches. That
        // was inferred, and inferring it is how an evening went on a reward pick that turned out to be
        // exactly what the numbers said it would be.
        //
        // Read from Planner.Chosen, written by the detailed pass, so this is the plan on screen and
        // not one of the millions the search tried. See Planner.Picked.
        b.AppendLine("  the solver's intent, link by link:");

        static (int X, int Y) Celled(System.Numerics.Vector2 grid) =>
            ((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));

        // Whether the blast at that spot reaches the marker in that cell, asked of the environment
        // rather than measured here. A remnant's radius counts towards the reach and differs per
        // marker, which is why a flat distance cannot answer it.
        static bool Catching(PlanEnvironment env, System.Numerics.Vector2 spot, (int X, int Y) cell)
        {
            foreach (var mark in env?.Targets ?? [])
            {
                if (Celled(mark.Grid) == cell)
                    return Planner.Catches(env, spot, mark);
            }

            return false;
        }

        var route = Chain;

        if (route is not { Count: > 0 })
        {
            b.AppendLine("    no chain - nothing has been solved here yet");
        }
        else
        {
            for (var i = 0; i < route.Count; i++)
            {
                var spot = route[i];
                var laid = i < LinksDown;

                b.AppendLine($"    blast {i + 1} at ({spot.X:0},{spot.Y:0})" +
                             (laid ? "  [already placed]" : "  [planned]"));

                // **What this blast is actually for, before what it decides.**
                //
                // The choices below say which combination each remnant takes; they say nothing about
                // why the blast is where it is. Most links catch no remnant at all - they are there
                // for monsters, chests and relics - and for those the section said "no remnant" and
                // stopped, which reads as "this blast does nothing". Weighed here instead, from the
                // same env the chain was scored against and the same reach test the scoring uses, so
                // the figures cannot drift from the ones that made the decision.
                var weight = 0f;
                var kinds = new Dictionary<TargetKind, int>();
                var multiplying = new List<string>();

                // **Both halves of the environment, because a placed blast's markers left Targets.**
                //
                // Anything an explosive already caught moves to Shown - never scored, never credited -
                // so a tally over Targets alone reported "nothing in reach" for every placed link, on
                // the same blast whose two remnants were listed two lines below it. The audit
                // contradicting itself inside one block is worse than the audit being short.
                //
                // Counted for what they are rather than for what they are worth to the search: these
                // have been collected, so the weight here says what the blast took, not what taking it
                // would be worth now. See PlanEnvironment.Shown.
                var made = new Dictionary<(TargetKind Kind, float Weight), int>();
                var rewards = 0f;

                foreach (var mark in (Env?.Targets ?? []).Concat(Env?.Shown ?? []))
                {
                    if (!Planner.Catches(Env, spot, mark))
                        continue;

                    weight += mark.Weight;
                    kinds[mark.Kind] = kinds.GetValueOrDefault(mark.Kind) + 1;

                    // **What each one is worth, not only what kind it is.** The count line says "1
                    // Chest" where the table prices three tiers between 1.62 and 6.55, and "1
                    // Relic" where a relic is worth what its modifiers are - so the content figure
                    // could not be checked against the table by anybody reading this, which is the
                    // one thing this section is for. Grouped by kind AND weight, so identical
                    // markers collapse to a count and a different tier shows up as its own entry.
                    var costing = (mark.Kind, Weight: MathF.Round(mark.Weight, 2));

                    made[costing] = made.GetValueOrDefault(costing) + 1;

                    // **And what its chosen combination is worth, which is content too.**
                    //
                    // Settle adds the reward's price to the chain's content - that is what makes
                    // one combination worth more than another - so a blast that catches a remnant
                    // is worth its marker AND its reward. Counting only the marker had the five
                    // blasts here totalling 1,048.5 against a plan whose content was 2,911.6.
                    //
                    // Read from the pick rather than priced again: the solver chose a combination
                    // and this is what that one came to, so the two cannot disagree.
                    if (mark.Kind == TargetKind.Remnant &&
                        Planner.Chosen.TryGetValue(Celled(mark.Grid), out var took) &&
                        took.Worth > 0f)
                    {
                        rewards += took.Worth;
                        weight += took.Worth;
                    }

                    // What this marker does to OTHER things, which is the half a weight cannot show.
                    // A relic that duplicates rares is worth nothing as content and changes what every
                    // later link is worth - see PlanTarget.Once and Spread.
                    var does = new List<string>();

                    if (mark.Carries > 0f)
                        does.Add($"carries {mark.Carries:0.#}");

                    if (mark.Spread is { Length: > 0 } spread)
                        does.Add($"scoped [{string.Join(" ", spread.Select(x => $"{x.Id}:{x.Percent:0.#}"))}]");

                    if (!string.IsNullOrWhiteSpace(mark.Once))
                        does.Add($"pays once \"{mark.Once}\"");

                    if (mark.NonStacking is { Length: > 0 } capped)
                        does.Add($"does not stack [{string.Join(" ", capped.Select(x => x.Id))}]");

                    if (does.Count > 0)
                    {
                        multiplying.Add($"({mark.Grid.X:0},{mark.Grid.Y:0}) {mark.Kind} " +
                                        string.Join(", ", does));
                    }
                }

                var paid = i < Planner.Credits.Length ? Planner.Credits[i] : 0d;

                // **A placed blast is not worth nought, it is worth nothing MORE.**
                //
                // Its markers have moved to Shown - never scored, never credited - because the reward
                // is collected and the monsters are dead, so both figures come back at nought and the
                // line read "worth 0.0 here" about a blast that had taken two remnants. What it still
                // does for the chain is banked into the links after it, which is where their
                // propagation partly comes from, so that is what this says instead of printing a
                // nought and letting it be read as a verdict.
                b.AppendLine(laid
                    ? $"        already collected - content {weight:N1} taken, and whatever it " +
                      "propagates is banked into every link after it"
                    : $"        worth {weight + paid:N1} here - content {weight:N1} + " +
                      $"propagation {paid:N1}");

                b.AppendLine($"        catches {kinds.Values.Sum()} markers" +
                             (kinds.Count == 0
                                 ? " - nothing in reach"
                                 : " - " + string.Join(", ",
                                     kinds.OrderByDescending(k => k.Value)
                                         .Select(k => $"{k.Value} {k.Key}"))));

                // Every marker's own weight, adding to the content above it. A weight that does not
                // match its row in the table section below is the whole point of printing it.
                if (made.Count > 0)
                {
                    b.AppendLine("        content is made of: " + string.Join(" + ",
                        made.OrderByDescending(x => x.Value * x.Key.Weight)
                            .Select(x => x.Value == 1
                                ? $"{x.Key.Kind} {x.Key.Weight:0.##}"
                                : $"{x.Value} x {x.Key.Kind} {x.Key.Weight:0.##} = " +
                                  $"{x.Value * x.Key.Weight:0.##}")) +
                        (rewards > 0f ? $" + rewards taken {rewards:0.##}" : "") +
                        $" = {made.Sum(x => x.Value * x.Key.Weight) + rewards:0.##}");
                }

                foreach (var said2 in multiplying)
                    b.AppendLine($"        multiplies: {said2}");

                // **Every factor that went into the propagation figure above it.**
                //
                // The line said "propagation 598.1" and nothing about how. Three things were
                // invisible and each is a different way to be wrong: what a modifier was worth after
                // a Power rune empowered it, which group it added inside of, and the product the
                // groups make. See Planner.Trailed, which writes this from the arrays the payout
                // reads at the point it reads them - the blast circles were once a second
                // calculation of the same thing and they had drifted.
                if (!laid && i < Planner.Factors.Length &&
                    !string.IsNullOrWhiteSpace(Planner.Factors[i]))
                {
                    b.AppendLine("        factors:");

                    foreach (var line in Planner.Factors[i].Split('\n'))
                    {
                        if (line.Trim().Length > 0)
                            b.AppendLine("  " + line.TrimEnd());
                    }
                }

                var said = 0;

                foreach (var (cell, picked) in Planner.Chosen)
                {
                    // The same reach test the scoring uses, rather than a distance with a margin on
                    // it: a guess here would attribute a remnant to the wrong blast and the audit
                    // would be describing a decision nobody made. See Planner.Catches.
                    if (!Catching(Env, spot, cell))
                        continue;

                    said++;

                    b.AppendLine($"        remnant ({cell.X},{cell.Y}) -> take \"{picked.Reward}\", " +
                                 $"using {picked.Sockets} sockets, " +
                                 $"carries {picked.Carries:0.#}" +
                                 (Math.Abs(picked.Carries - picked.Kept) > 0.05f
                                     ? $"->{picked.Kept:0.#}"
                                     : "") +
                                 $" [{string.Join(" ", picked.Carrying)}], " +
                                 $"local {picked.Local:0.#}" +
                                 (Math.Abs(picked.Local - picked.Held) > 0.05f
                                     ? $"->{picked.Held:0.#}"
                                     : "") +
                                 (Planner.RuneTallyByRemnant.TryGetValue(cell, out var landed)
                                     ? $"  -> waves {Propagation.Waves(landed)}"
                                     : ""));

                    // **Why that one, and not the others it was offered.** The choice a remnant
                    // makes is the single decision its worth turns on, and only the winner was ever
                    // reported - so a remnant taking an option worth less than the one it would have
                    // been pinned to could not be argued with. These are the terms it was ranked on.
                    if (Planner.Rankings.TryGetValue(cell, out var ranking))
                    {
                        foreach (var line in ranking)
                            b.AppendLine($"            ranked  {line}");
                    }
                }

                // **And the remnants this blast has already taken, which the solve never chose for.**
                //
                // Once a blast is down its remnants move from env.Targets to env.Shown - never scored,
                // never credited, because the reward has been collected and the choice is made. So no
                // combination is published for them and this section said "no remnant" about a blast
                // sitting on two of them. Reporting silence as absence, on the one readout whose job is
                // to say what the solver is doing.
                //
                // What they still do matters to everything after them: their runes are banked forward
                // as plain rates, which is most of what the links ahead inherit. So they are listed
                // with what they are banking rather than with a choice. See PlanEnvironment.Shown.
                foreach (var already in Env?.Shown ?? [])
                {
                    // Remnants only. Shown carries every marker a placed blast caught, monsters and
                    // chests included, so without this one blast reported thirteen "remnants" of which
                    // eleven were monsters standing near each other, each banking nothing.
                    if (already.Kind != TargetKind.Remnant ||
                        !Planner.Catches(Env, spot, already))
                        continue;

                    var was = Celled(already.Grid);
                    var sends = new List<string>();

                    foreach (var (id, from) in Env?.BankedRunes ?? [])
                    {
                        if (from == was && !sends.Contains(id, StringComparer.OrdinalIgnoreCase))
                            sends.Add(id);
                    }

                    said++;

                    b.AppendLine($"        remnant ({was.X},{was.Y}) -> already taken, " +
                                 (sends.Count == 0
                                     ? "banking nothing forward"
                                     : $"banking [{string.Join(" ", sends)}] to every link after it"));
                }

                if (said == 0)
                    b.AppendLine("        no remnant - this blast is for monsters and chests");
            }
        }

        // **The two numbers over the button disagree, and nothing said where.** The left is what is
        // down scored with the recipes SET on the remnants; the right is the plan scored with the
        // ones the solver would pick. They are meant to be able to differ - that is the whole reason
        // there are two - but the audit printed only the solver's side, so working out which remnant
        // accounted for a sixty point gap meant guessing at mechanisms. It took three wrong ones.
        //
        // So both picks go down side by side, per remnant, with the disagreements called out.
        b.AppendLine("  what the solver picked against what is set on the ground:");

        var differing = 0;
        var compared = 0;

        foreach (var (cell, picked) in Planner.Chosen)
        {
            var solver = picked.Reward;
            var theirs = "not readable";
            var found = false;

            // **Compared by recipe, shown by name.** A remnant can offer two combinations with one
            // name - "Refutation" twice, on different rune counts - so two agreeing names are not
            // two agreeing picks, and this line said "none differ" about a site where the placed
            // score was two thousand short precisely because of that pair. The names are what a
            // reader recognises, so they are still what is printed; the verdict is the ids.
            var wanted = picked.Recipe;
            var ours = "";

            foreach (var mark in Scan?.Targets ?? [])
            {
                if (mark.Kind != TargetKind.Remnant || Celled(mark.Grid) != cell)
                    continue;

                var set = Safe.Read(() => Valuation?.ChosenName(mark.Entity), null);

                ours = Safe.Read(() => Valuation?.ChosenRecipeId(mark.Entity), null) ?? "";

                if (!string.IsNullOrWhiteSpace(set))
                {
                    theirs = set;
                    found = true;
                }

                break;
            }

            compared++;

            // Ids when both are readable, names when either is not: a recipe with no id is one the
            // game states none for, and there the name is all anybody has.
            var agrees = found &&
                         (!string.IsNullOrWhiteSpace(wanted) && !string.IsNullOrWhiteSpace(ours)
                             ? string.Equals(wanted, ours, StringComparison.Ordinal)
                             : string.Equals(solver, theirs, StringComparison.OrdinalIgnoreCase));

            if (found && !agrees)
                differing++;

            b.AppendLine($"    ({cell.X},{cell.Y})  solver \"{solver ?? "(none)"}\"  " +
                         $"ground \"{theirs}\"" +
                         (found ? agrees ? "  agree" : "  <- DIFFERS" : "  (nothing set)") +
                         // Only where they matter: the ids are unreadable strings and printing a
                         // pair of them on every line buries the three words that carry the sense.
                         (found && !agrees && !string.IsNullOrWhiteSpace(wanted)
                             ? $"  (solver recipe {wanted}, ground recipe " +
                               $"{(string.IsNullOrWhiteSpace(ours) ? "none" : ours)})"
                             : ""));
        }

        b.AppendLine(compared == 0
            ? "    nothing chosen this pass"
            : differing == 0
                ? $"    {compared} remnants compared, none differ - so a gap between the two scores " +
                  "on screen is NOT a reward pick and is worth looking at elsewhere"
                : $"    {compared} remnants compared, {differing} differ - which is where the two " +
                  "scores on screen part company");

        b.AppendLine("  runes landing on each remnant, as drawn:");

        // **Whether the table is current, because a stale one looks identical to a fresh one.** The
        // figures survive a roll and describe the runes it replaced, so a reader comparing them against
        // a remnant on screen would be comparing two different remnants. The line on the ground and the
        // combinations window both go blank while this holds; the dump prints the numbers anyway,
        // because knowing what the last pass concluded is the point of a dump.
        if (Planner.RuneTallyOutOfDate)
        {
            b.AppendLine("  !!   these figures are A ROLL OUT OF DATE - a remnant was rolled after the " +
                         "pass that wrote them, and propagation is chain-wide, so every row below " +
                         "describes runes that may no longer be there. The next detailed pass rewrites " +
                         "it. See Planner.RuneTallyOutOfDate.");
        }

        if (Planner.RuneTallyByRemnant.Count == 0)
        {
            b.AppendLine("    none recorded - no chain has been scored in detail yet");
        }
        else
        {
            var lines = new List<string>();

            foreach (var (cell, runes) in Planner.RuneTallyByRemnant)
            {
                lines.Add($"    ({cell.X},{cell.Y})  {runes.Total} " +
                          $"({runes.Sockets}+{runes.Inherited}-{runes.Wasted})" +
                          (runes.FirstSourced is { Length: > 0 } ? "  first: " + string.Join(", ", runes.FirstSourced) : "") +
                          (runes.Sockets + runes.Inherited - runes.Wasted == runes.Total
                              ? ""
                              : "   DOES NOT RECONCILE"));
            }

            lines.Sort(StringComparer.Ordinal);
            b.AppendLine(string.Join("\n", lines));
        }
        // Named for the moment it describes. It is read off the environment the plan was solved
        // in, so on a finished plan it is a statement about the site as the last solve found it
        // and not about the ground now - which read as a live claim that five explosives were
        // paying nothing.
        // **The booked set, whole.** Every other line about propagation is filtered - the
        // carrying diagnostic shows runes, the trail shows what reached a link - so a modifier
        // booked twice under two keys appears in none of them. See Planner.Bookings.
        b.AppendLine("  every booking the last detailed pass paid a share for:");

        foreach (var line in Split(Planner.Bookings))
            b.AppendLine(line.Length > 0 ? line : "      (nothing said)");

        b.AppendLine("  what the explosives down when the plan was solved were paying:");

        foreach (var line in Split(Planning.Banking))
            b.AppendLine(line.Length > 0 ? line : "      (nothing said)");

        b.AppendLine($"  why each remnant is or is not drawn: {Planning.Waved}");

        foreach (var (cell, why) in Planner.PropagationTrailByRemnant)
            b.AppendLine($"    ({cell.X},{cell.Y}) {why}");

        b.AppendLine($"  presolving this site: {Rehearsal?.Describe() ?? "not wired up"}");
        b.AppendLine($"  what the last solve did with the previous chain: {Planning.Seeded}");
        b.AppendLine($"  the best chain on file here: {Kept.Describe()}");
        b.AppendLine($"  what the last solve did with it: {Planning.Filed}");
        b.AppendLine($"  how the score on screen is made up: {Scoring?.Breakdown ?? "not wired up"}");
        b.AppendLine($"  what that score was worked out over: {ScoringPool()}");
        b.AppendLine($"  what the score did through the last solve: {Scoring?.Dip ?? "not wired up"}");
        b.AppendLine($"  blasts that missed what they were chosen for: {Missed.Here.Describe()}");
        b.AppendLine($"  hand placement against the plan: {Astray.Last}");
        b.AppendLine($"  hiding ground labels off the button: {Placement.Hid}");
        b.AppendLine($"  last beam search: {Beam.Last}");
        b.AppendLine("  the chain in hand, scored as a whole site: " + Whole(gc));
        b.AppendLine($"  last search operators: {Planner.Operators}");
        b.AppendLine($"  restart openings drawn from: {Planner.Openings}; " +
                     $"the seeding phases {Planner.Sowing}");

        // **Why a press started from nothing, which took a session of guessing to ask.** Three
        // things clear the site's learnt state - the settings button, the cold key, and each step of
        // a bake-off - and from the outside a solve that inherited a floor and one that did not look
        // identical apart from the score. See Caches.Last.
        b.AppendLine($"  the last cache clear: {Caches.Last}" +
                     (Caches.When == default
                         ? " (not this session)"
                         : $", {(DateTime.UtcNow - Caches.When).TotalSeconds:N0}s ago"));
        b.AppendLine("  links the router did not reach: treated as walls, which is what the " +
                     "REFUSED UNROUTED count above cost");
        b.AppendLine($"  threads: {Solving.Spread}");
        b.AppendLine($"  kicks that started from another thread's chain: {Solving.Migrations:N0}");
        b.AppendLine($"  threads that gave up on a dead chain: {Solving.Rescues:N0}");
        b.AppendLine($"  threads, per compared strategy: {Bakeoff.Threads}");
        // **What the search was configured to do, beside what it did.**
        //
        // Every switch here changes how a run behaves and none of them were written down, so two
        // dumps differing by a thousand points could not be told apart from two dumps differing by a
        // setting. Read back off the settings rather than remembered, so it says what was in force
        // rather than what anybody meant to put in force.
        b.AppendLine($"  what each press has been worth: {Planning.Climbed}");
        b.AppendLine($"  search switches: " +
                     $"own opening per worker {Settings?.Solver.Advanced.DestroyAndRepair.VaryOpenings.Value}, " +
                     $"vary tearing mix {Settings?.Solver.Advanced.DestroyAndRepair.VaryOperators.Value}, " +
                     $"tearing mix [{Settings?.Solver.Advanced.DestroyAndRepair.TearingMix.Value}], " +
                     $"share best {Settings?.Solver.Advanced.DestroyAndRepair.ShareBest.Value}, " +
                     $"never adopt [{Settings?.Solver.Advanced.DestroyAndRepair.ShareNot.Value}], " +
                     $"estimate detour {Settings?.Solver.Advanced.DestroyAndRepair.EstimateDetour.Value}, " +
                     $"bridge links {Settings?.Solver.Advanced.DestroyAndRepair.BridgeLinks.Value ?? -1}, " +
                     $"slide by {Settings?.Solver.Advanced.DestroyAndRepair.SlideBy.Value ?? -1f:0.#}, " +
                     $"permute up to {Settings?.Solver.Advanced.DestroyAndRepair.PermuteUpTo.Value ?? -1}, " +
                     $"accept slack {Settings?.Solver.Advanced.DestroyAndRepair.AcceptSlack.Value:0.#}%, " +
                     $"opening shakes {Settings?.Solver.Advanced.DestroyAndRepair.OpeningShakes.Value}, " +
                     $"opening cap {(Settings?.Solver.Advanced.DestroyAndRepair.OpeningMs.Value is > 0 and var ms ? $"{ms}ms" : "none")}, " +
                     $"rescue below {Settings?.Solver.Advanced.DestroyAndRepair.RescueBelow.Value:0.#}% " +
                     $"(shaken {Settings?.Solver.Advanced.DestroyAndRepair.RestartShakes.Value}), " +
                     $"adopted shake {Settings?.Solver.Advanced.DestroyAndRepair.AdoptedShake.Value}, " +
                     $"frozen prices {Settings?.Debug.FreezePrices.Value} ({Valuation.Freezer}), " +
                     $"must take above {Settings?.Rewards.MustTakeAbove.Value ?? -1f:0.#}, " +
                     $"threads {Settings?.Solver.Threads.Value ?? -1}, " +
                     $"tear {Settings?.Solver.Advanced.DestroyAndRepair.TearLeast.Value ?? -1}-{Settings?.Solver.Advanced.DestroyAndRepair.TearMost.Value ?? -1}");

        b.AppendLine($"  destroy and repair: {Repair.Telling}");
        b.AppendLine($"  published plans checked against the ceiling: {Planner.Bounds}");
        b.AppendLine($"  what the shortlist kept: {Repair.Listed}");
        b.AppendLine($"  the connective half of it: {Repair.Spreading}");
        b.AppendLine($"  sliding the tail along the route: {Repair.Sliding}");
        b.AppendLine($"  every ordering tried: {Repair.Permuting}");
        b.AppendLine($"  how far reach's bridges really walk: {Repair.Detoured}");
        b.AppendLine($"  bridges the reach operator built: {Planner.Fetches}");
        b.AppendLine($"  what asked for the last solve: {Planning.Asked}");
        b.AppendLine($"  every solve at this dig site, {Planning.Journeyed}");
        b.AppendLine($"  re-solving: {Planning.Progress}");
        b.AppendLine($"  marked as must take: {Insisted.Here.Describe(Plan)}");

        // **The count the objective charges against the flags the search can see.**
        // env.Musts is how many marks the environment was built with and decides what a
        // dropped requirement costs; PlanTarget.Must is set per target by matching the
        // mark to a marker, and it is what Demanded reads to build a chain that visits
        // them. A mark that finds no target is charged for and cannot be routed to, which
        // is a chain permanently invalid for a requirement nothing can satisfy.
        if (Env != null)
        {
            var flagged = 0;

            foreach (var target in Env.Targets)
                if (target.Must)
                    flagged++;

            b.AppendLine($"    the environment charges for {Env.Musts} and the search can " +
                         $"see {flagged} of them" +
                         (flagged == Env.Musts
                             ? ""
                             : " - MARKS THAT MATCH NO MARKER, so the chain is charged for a" +
                               " requirement no route can satisfy"));
        }

        Known(b, gc);

        // The two numbers over the placement button. Both are what the site pays - insistence is
        // no longer inside them to be announced and subtracted. See Verdict.Plain.
        if (Scoring is { Known: true } running)
        {
            b.AppendLine($"  the running score: placed {running.Yours:N1}, " +
                         $"planned {running.Planned:N1}, over {running.Down} explosives down");
        }
        else
        {
            b.AppendLine("  the running score: not worked out yet");
        }
        b.AppendLine($"  endgame committed: {Planner.Committed} links");
        b.AppendLine($"  committing: {Planner.Committing}");
        b.AppendLine($"  narrow chains examined: {Planner.Enumerated:N0} in {Planner.EnumeratedMs:N0}ms " +
                     $"over {Planner.Banded} bands");
        b.AppendLine($"  edge only mode: {Edges.Last}");
        // The score of the plan that is actually on screen.
        //
        // **The dump described every part of the search except its answer.** Comparing two runs
        // meant reading an operator's internal best and hoping it was the one that won - which it
        // often was not, since a plan can come from any of a dozen places. The plan the player is
        // looking at is the only number that settles which strategy did better.
        // **The route, whole, with the cursor in it.** The plan below is only its tail, and every
        // question that has ever been asked of a dump here - why did the score drop, why is a link
        // missing, why did undo lose the chain - is a question about the head. See Planning.Chain.
        b.AppendLine("  THE CHAIN IN HAND: " + (Chain is not { Count: > 0 }
            ? "none"
            : $"{LinksDown} of {Chain.Count} links laid - " +
              string.Join(" ", System.Linq.Enumerable.Select(Chain,
                  (at, i) => $"{(i < LinksDown ? "[" : "(")}{at.X:0},{at.Y:0}" +
                             $"{(i < LinksDown ? "]" : ")")}"))));

        b.AppendLine("  THE PLAN ON SCREEN: " + (Plan == null || Plan.Points.Count == 0
            ? "none"
            // Plain, for the reason given on Rehearsal's own high-water mark: the objective's total
            // carries an insistence bonus that is not a quantity anyone can read. The must-takes are
            // reported by name a few lines down, which says the same thing usefully.
            : $"{Plan.Plain:N1} over {Plan.Points.Count} links covering {Plan.Covered} markers - " +
              string.Join(" ", Plan.Points.ConvertAll(at => $"({at.X:0},{at.Y:0})"))));

        // **Every planned spot put back through the tests that were supposed to have vetted it.**
        //
        // A plan came back with its first link on a cell the same solve had just refused as ground -
        // Placeable dropped the previous chain for standing on it, and the new chain stood on it
        // too. The candidate builder, the bands, the bridges and the polish all consult CanPlace, so
        // one of them is not asking, or is asking about a different cell. Printing the answers per
        // spot says which: a spot that CanPlace accepts while Refused covers it means the two
        // disagree about the cell, and a spot both refuse means something bypassed CanPlace
        // altogether.
        if (Plan is { Points.Count: > 0 } && Env != null)
        {
            var lines = new List<string>();

            // **The word "refused" was printed with nothing after it**, so every spot read
            // "canplace True refused" and the line said the opposite of what it meant: a bare label
            // beside a verdict reads as a second verdict. The half that was missing is the whole
            // reason the line exists - CanPlace agreeing with the obstacle list proves nothing, and
            // the two disagreeing is the fault being hunted.
            var blocking = Safe.Read(() => Obstacles.Read(gc, Detonator.DetonatorGridPosition(gc), 400f), null);

            for (var i = 0; i < Plan.Points.Count; i++)
            {
                var at = Plan.Points[i];
                var was = i == 0 ? Env.Origin : Plan.Points[i - 1];

                lines.Add($"{i + 1}:({at.X:0.#},{at.Y:0.#}) " +
                          $"canplace {Safe.Read(() => Env.CanPlace(at), false)} " +
                          $"refused {(blocking == null ? "?" : Safe.Read(() => blocking.Covers(at), false).ToString())} " +
                          $"reach {Safe.Read(() => Env.CanReach?.Invoke(was, at), null)}");
            }

            b.AppendLine("    each planned spot, re-tested now: " + string.Join("  ", lines));
        }

        // What the score is made of.
        //
        // **A total says whether one run beat another and nothing about where to look next.** The
        // objective is content plus propagation, and those two respond to completely different
        // things - content to which markers a blast covers, propagation to the ORDER remnants fire
        // in relative to the monsters unearthed after them. A site where propagation is a tenth of
        // the score is a site where reordering cannot help however good the operator is, and one
        // where it is half says the opposite. Guessing which was costing hours.
        if (Env != null && Plan is { Points.Count: > 0 })
        {
            var verdict = Planner.Evaluate(Env, Plan.Points);
            var total = verdict.Content + verdict.Propagation;
            var plain = verdict.Plain;

            // **The number the SEARCH compares, which stopped being the one above.** The bound
            // checks below were written when the two were the same thing; once insistence became a
            // term in Total rather than a weight inside Content, the guard went on comparing the
            // smaller number and went blind at exactly the moment it was needed. See Verdict.Held.
            var scored = verdict.Total;

            // And how much there was to have.
            //
            // **"Is this a bad plan or a bad dig site" was being argued from nothing.** Two sites
            // scored 9,127 and 2,276 with near identical marker coverage, and hours went into the
            // solver on the strength of that gap - which may simply be what the two sites are worth.
            // The content of every marker in the site is known; what share of it the chain takes is
            // the whole question, and it costs a sum over a list already in hand.
            //
            // A high share means the site is poor and no search will help. A low one means there is
            // content the chain is not reaching, and then the question is why - out of range, out of
            // budget, or never scouted.
            var onOffer = 0d;
            var must = 0d;

            foreach (var target in Env.Targets)
            {
                onOffer += Planner.WorthOfTarget(target);
                must += Math.Max(0d, target.Weight - Planner.WorthOfTarget(target));
            }

            var (took, _) = Planner.Taken(Env, Plan.Points);

            b.AppendLine($"    content taken: {took:N1} of {onOffer:N1} on the site " +
                         $"({(onOffer > 0d ? took / onOffer * 100d : 0d):0}%), " +
                         $"{Plan.Covered} of {Env.Targets.Count} markers");

            if (must > 0d)
            {
                b.AppendLine($"    (a required remnant carries {must:N0} above its own worth so the " +
                             "chain is built around it; left out of the two lines here, which are " +
                             "about how much of the SITE the plan takes)");
            }

            b.AppendLine($"    enumerated solve: {Planner.EnumeratedSolveOutcome}");

            // **A bound that is beaten is not a bound, and the only way that showed was by reading
            // two lines of the card against each other.** Twice now the early stop has fired on a
            // plan that was nowhere near optimal, because RelaxedCeiling was missing a term the scorer has
            // - first the local runes, then a carrier's own waves. Both times the number was sitting
            // in the dump and nothing compared them. This compares them.
            var bound = Planner.RelaxedCeiling(Env);

            // **Only equality reads as anything.** The ceiling drops four rules the scorer keeps -
            // see Planner.RelaxedCeiling - so the fraction below is not how much of the site is
            // left, and neither a low one nor a high one is evidence about the plan. A dense site
            // sits at a few per cent with an optimal chain on screen. It is printed because when
            // the two DO meet the search stops, and because a plan above the ceiling is a fault in
            // the scorer.
            if (bound > 0d)
            {
                b.AppendLine($"    against the relaxed ceiling: {scored:N1} of {bound:N1}, " +
                             $"{scored / bound * 100d:0.#}% - the ceiling lets every carrier lift " +
                             "every monster and every remnant take every combination, so this " +
                             "fraction is NOT how much is left");
            }

            if (bound > 0d)
            {
                var terms = Planner.RelaxedCeilingTerms;

                b.AppendLine($"      the bound's product: {Planner.RelaxedCeilingPool.Monsters:N1} " +
                             $"monsters x {Planner.RelaxedCeilingPool.Mult:N2} - largest groups " +
                             $"[{Planner.RelaxedCeilingShares}]");

                b.AppendLine($"      the bound's terms: content {terms.Content:N1}, propagation " +
                             $"{terms.Carried:N1}, must-takes {terms.Musts:N1} - against the " +
                             "score's own content and propagation above, so a beaten bound says " +
                             "which half is short");
            }

            if (bound > 0d && scored > bound + 0.0001d)
            {
                b.AppendLine($"    THE RELAXED CEILING WAS BEATEN: the plan scores {scored:N1} against a " +
                             $"ceiling of {bound:N1}, so RelaxedCeiling is missing a term the scorer " +
                             "has and any early stop it caused was wrong");
            }

            if (Planner.StoppedAtRelaxedCeiling)
            {
                // The BOUND, and the score that reached it. The old wording said "this takes X,
                // which is everything the site can pay" and printed the bound as though it were the
                // content taken - so a card reading "took 71% of the site" sat directly under a
                // sentence claiming it had taken all of it.
                b.AppendLine($"    the search stopped early: it scored {scored:N1} against a relaxed " +
                             $"ceiling of {Planner.RelaxedCeiling(Env):N1} - meeting it means the " +
                             "relaxations stopped being relaxations here, so no chain can score " +
                             "higher. About the SCORE, not about how much of the site is taken, " +
                             "and the two lines above still say how much that is");
            }

            b.AppendLine($"    made of: content {verdict.Content:N1} " +
                         $"({(total > 0d ? verdict.Content / total * 100d : 0d):0}%), " +
                         $"propagation {verdict.Propagation:N1} " +
                         $"({(total > 0d ? verdict.Propagation / total * 100d : 0d):0}%), " +
                         $"over {verdict.Walked:N0} grid walked");

            // Whether the chain is allowed at all, before anything about what it is worth.
            //
            // **A dropped requirement is not a low score, it is an invalid answer**, and the two
            // were indistinguishable here for as long as insistence was only a large weight. See
            // Verdict.Missed.
            if (verdict.Missed > 0)
            {
                // **It used to end "and nothing in reach could take them", which nothing checked.**
                // The sentence was printed on every invalid chain whatever the geometry, and it
                // reads as a finding: a site with two must-takes 808 apart, a detonator 384 from
                // the nearer one and a budget of 15 links at 108 reach, was diagnosed as
                // impossible on the strength of it when the straight-line bound had 428 to spare.
                //
                // A chain that drops a must-take is either refused by the site's geometry or
                // missed by the search, and those want opposite work. So the bound is computed
                // and the two are told apart, or neither is claimed.
                b.AppendLine($"    INVALID: {verdict.Missed} must-take marker" +
                             $"{(verdict.Missed == 1 ? "" : "s")} not taken, each charged " +
                             $"{verdict.Refused:N0} - so this is the best chain found rather than " +
                             "a legal one");

                b.AppendLine("    " + Insisted.Here.Bound(Env));
            }

            // Both numbers, because they answer different questions and the gap between them is
            // itself the answer to a third. The search maximises the first; the screen shows the
            // second; a large gap means most of what the search is chasing is insistence rather
            // than anything the site pays. See Verdict.Held.
            if (verdict.Held > 0)
            {
                b.AppendLine($"    the search scored this {scored:N1}, of which " +
                             $"{verdict.Held * verdict.Refused:N0} is {verdict.Held} requirement" +
                             $"{(verdict.Held == 1 ? "" : "s")} held at {verdict.Refused:N0} each - " +
                             $"what the site pays, and what the screen shows, is {plain:N1}");
            }
        }

        b.AppendLine("  every strategy on this site: " +
                     (Bakeoff.Showing.Count > 0
                         ? string.Join("; ", Bakeoff.Showing.Select(l => l.Best ? l.Text + " <-" : l.Text))
                         : "not run"));
        b.AppendLine($"  narrow walk: {Planner.Walked}");
        b.AppendLine($"  rolling pass (bands redrawn each link): {Planner.RolledBest:N1}");
        b.AppendLine("  family seeding: " + (Planner.Seeded > 0
            ? $"{Planner.Seeded} spots, best narrow chain {Planner.SeededBest:N1}, " +
              $"{Planner.SeededMs:N0}ms before polishing"
            : "none - off, or the site has no families"));
        b.AppendLine();
        Spots(b);
        b.AppendLine();
        Links(b, gc);
        b.AppendLine();
        b.AppendLine("  scenery standing in the site, which no grid mentions:");
        b.AppendLine($"    {Obstacles.Read(gc, Detonator.DetonatorGridPosition(gc), 200f).Describe()}");

        foreach (var entity in Safe.Read(gc, static g =>
                     g.EntityListWrapper.ValidEntitiesByType.TryGetValue(
                         ExileCore2.Shared.Enums.EntityType.MiscellaneousObjects, out var of) ? of : null,
                     null) ?? new List<Entity>())
        {
            var metadata = Safe.Read(entity, static e => e.Metadata, "") ?? "";

            if (metadata.Length == 0 ||
                metadata.IndexOf("Expedition", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            var at = Safe.Read(entity, static e => e.GridPos, System.Numerics.Vector2.Zero);
            var site = Detonator.DetonatorGridPosition(gc);

            if (System.Numerics.Vector2.Distance(at, site) > 200f)
                continue;

            b.AppendLine($"    {metadata}  grid ({at.X:0},{at.Y:0})  " +
                         $"bounds {Safe.Read(entity, static e => e.GetComponent<Render>()?.Bounds ?? default, default)}  " +
                         $"{System.Numerics.Vector2.Distance(at, site):0} from the detonator");
        }
        b.AppendLine();
        Strangers(b, gc, scan);
        BoundsAgainstExtents(b, scan);
    }

    /// <summary>
    /// What the game says a blast would activate that the scan is not holding.
    ///
    /// The one section here that can tell you about content nobody has thought of yet - everything
    /// else in this file reports on things the plugin already knows the name of. See
    /// <see cref="Unexpected"/>.
    /// </summary>
    /// <summary>
    /// The spots the overlay last ringed, and how the plan's links line up with them.
    ///
    /// Written to answer one question: does a chain worth having actually pass through the best few
    /// places to catch a remnant or a rare? If it does, the search can be given a space twenty times
    /// smaller and search it properly; if it does not, the idea is dead and the number here says so.
    ///
    /// The distance column is the whole of it. A link sitting exactly on a ringed spot is the wager
    /// paying off; one sitting fifteen grid away is a chain the narrowed search could never have
    /// built.
    /// </summary>
    /// <summary>
    /// The bands as the search holds them: how many cells each has, how far it spreads, and which
    /// cells survive as corners.
    ///
    /// The drawing shows where they are; this says whether they are anything. A band of two cells
    /// offers the chain no choice at all, and no amount of leaning finds reach in it.
    /// </summary>
    /// <summary>
    /// A multi-line trace broken into lines, without a character literal nobody can type safely.
    ///
    /// Written as a helper because the obvious spelling is a newline inside single quotes, and every
    /// attempt to generate that through a shell here produced a real line break in the source.
    /// </summary>
    private static string[] Split(string text) =>
        (text ?? string.Empty).Split((char)10);

    private static void Bands(StringBuilder b)
    {
        var bands = Planner.Shapes;

        if (bands.Count == 0)
            return;

        b.AppendLine("=== the bands last drawn ===");
        b.AppendLine("  how the tree of edge chains grows:");
        b.AppendLine(Planner.Fanned);
        b.AppendLine();

        foreach (var (name, corners, all) in bands)
        {
            var low = all[0];
            var high = all[0];

            foreach (var cell in all)
            {
                low = new System.Numerics.Vector2(MathF.Min(low.X, cell.X), MathF.Min(low.Y, cell.Y));
                high = new System.Numerics.Vector2(MathF.Max(high.X, cell.X), MathF.Max(high.Y, cell.Y));
            }

            b.AppendLine($"  {name,-10} {all.Count,3} cells, spans {high.X - low.X:0}x{high.Y - low.Y:0} grid " +
                         $"from ({low.X:0},{low.Y:0}) to ({high.X:0},{high.Y:0}), {corners.Count} corners");
            b.AppendLine("      corners: " +
                         string.Join(" ", corners.ConvertAll(c => $"({c.X:0},{c.Y:0})")));
        }

        b.AppendLine();
    }

    private static void Spots(StringBuilder b)
    {
        Bands(b);

        var spots = Planner.Spots;

        b.AppendLine("=== the best spots last drawn ===");

        if (spots.Count == 0)
        {
            b.AppendLine("  none drawn - press one of the best-spot buttons in Debug first");
            b.AppendLine();

            return;
        }

        b.AppendLine($"  {spots.Count} spots, " +
                     (Planner.Family
                         ? $"every family: {Planner.Counts.Pairs} per pair, " +
                           $"{Planner.Counts.Rares} per rare, {Planner.Counts.Remnants} per remnant"
                         : Planner.Paired
                             ? "one per remnant and neighbour a single blast can take together"
                         : Planner.PerKind == null
                             ? "ranked over the site"
                             : $"the best few per {Planner.PerKind.ToString()?.ToLowerInvariant()}"));

        foreach (var (at, worth, note) in spots)
            b.AppendLine($"    {note,-16} ({at.X:0},{at.Y:0})   worth {worth,8:N1}");

        if (Planner.Family)
        {
            b.AppendLine();
            b.AppendLine("  which spot can follow which, as the search sees it:");
            b.AppendLine(Planner.Linked);
        }

        if (Plan == null || Plan.Points.Count == 0)
        {
            b.AppendLine("  no plan to compare them against");
            b.AppendLine();

            return;
        }

        b.AppendLine();
        b.AppendLine("  the plan's links against them:");

        for (var i = 0; i < Plan.Points.Count; i++)
        {
            var link = Plan.Points[i];
            var nearest = float.MaxValue;
            var which = "";

            foreach (var (at, _, note) in spots)
            {
                var away = System.Numerics.Vector2.Distance(at, link);

                if (away >= nearest)
                    continue;

                nearest = away;
                which = note;
            }

            b.AppendLine($"    {i + 1}  ({link.X:0},{link.Y:0})  nearest ringed spot {which} " +
                         $"at {nearest:0.#} grid");
        }

        b.AppendLine();
    }

    /// <summary>
    /// What a blast would set off that nobody has agreed a price for.
    ///
    /// **Two different states used to be printed as one, under the wrong name.** The heading said
    /// "things the scan does not know" and the footer said "content the planner is ignoring", and
    /// for most entries neither was true: Unexpected keeps a target whose row is Set:false, which
    /// is a thing the scan has classified and the objective has priced - at a placeholder nobody
    /// has confirmed. A sub-area entrance read as Entrance, scored 20 into the plan's content, and
    /// was reported here as unknown and ignored.
    ///
    /// So each line says which it is. Genuinely unclassified is the serious one and was the case
    /// the section was written for; waiting to be confirmed is the ordinary one and means a fresh
    /// art on a familiar object.
    /// </summary>
    private static void Strangers(StringBuilder b, GameController gc, Scan scan)
    {
        b.AppendLine("=== things a blast would set off that nobody has priced ===");

        if (scan == null)
        {
            b.AppendLine("  not wired up");
            b.AppendLine();

            return;
        }

        var strangers = Unexpected.Read(gc, scan, Detonator.DetonatorGridPosition(gc), Unexpected.Around(gc), 0);

        if (strangers.Count == 0)
        {
            b.AppendLine("  none - everything carrying a glow_epk near the site is on record");
            b.AppendLine();

            return;
        }

        foreach (var one in strangers)
        {
            // The same proximity test the ground label uses, so the two cannot disagree about
            // which target a finding is. See Overlay.Naming.
            Target standing = null;

            foreach (var target in scan.Targets)
            {
                if (System.Numerics.Vector2.Distance(target.Grid, one.Grid) < 1.5f)
                {
                    standing = target;

                    break;
                }
            }

            b.AppendLine($"  {one.Meta}  grid ({one.Grid.X:0},{one.Grid.Y:0})  " +
                         (one.Lit ? "lit right now" : "not lit") + "  " +
                         (standing == null
                             ? "NOT CLASSIFIED - the scan makes no target here at all"
                             : $"scanned as {standing.Kind} and priced into the plan, on a " +
                               "placeholder weight nobody has confirmed - agree it in the table"));

            if (standing == null)
                continue;

            // **Which rows this object actually reaches, and the key it reaches them by.**
            //
            // A row can sit in the table carrying a value, be visibly attached to this object by its
            // id, and still never answer - because the id is built from what the object looked like
            // when it was filed and the lookup is an exact ordinal match. A size typed into such a
            // row changes nothing and there is no way to see why from either end: the table shows the
            // cell, the object shows the old answer, and the key that would have joined them is
            // printed nowhere. Comparing these two lines against the found: row in the table section
            // is the whole diagnosis.
            var asked = Weighing.RowIdOfTarget(standing);
            var filed = Safe.Read(() => Unknowns.Key(standing), "");
            var found = Wrt.Id.Found(filed);

            b.AppendLine($"      weight row asked for: {asked}  ->  " +
                         (Wrt.Of(asked) is { } onKind
                             ? $"weight {onKind.Weight?.ToString("0.##") ?? "-"}, size {onKind.Size?.ToString("0.##") ?? "-"}"
                             : "NO SUCH ROW"));

            b.AppendLine($"      discovered row:       {found}  ->  " +
                         (Wrt.Of(found) is { } onFound
                             ? $"weight {onFound.Weight?.ToString("0.##") ?? "-"}, size {onFound.Size?.ToString("0.##") ?? "-"}"
                             : "NO SUCH ROW - the key above is not the one the table was filed under"));

            b.AppendLine($"      extent in use:        {Extents.Of(standing):0.##} grid");
        }

        b.AppendLine("  (a NOT CLASSIFIED line is content the planner is ignoring; the rest are " +
                     "priced and counted, and are asking for the placeholder to be agreed)");
        b.AppendLine();
    }

    private static void MapStats(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("=== map stats mentioning expedition or explos ===");

        foreach (var name in new[] { "MapStats", "MapStatsVisible" })
        {
            var stats = name == "MapStats"
                ? Safe.Read(() => gc.IngameState.Data.MapStats, null)
                : Safe.Read(() => gc.IngameState.Data.MapStatsVisible, null);

            b.AppendLine($"  {name} ({stats?.Count.ToString() ?? "null"}):");

            foreach (var (stat, value) in stats ?? new Dictionary<GameStat, int>())
            {
                var key = stat.ToString();

                if (key.Contains("xpedition", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("xplos", StringComparison.OrdinalIgnoreCase))
                    b.AppendLine($"    {key} = {value}");
            }
        }
    }

    // ------------------------------------------------------------------ entities

    /// <summary>
    /// Which row each relic walked past answered to, and which answered to nothing.
    ///
    /// **A binding that never fires looks exactly like a relic nobody has met.** The six special
    /// relics used to be matched by a chain of hand-written fragments, and one of those fragments was
    /// wrong for a year - a matcher for "UpsideExperienceKarui" against a game that says
    /// ExpeditionRelicUpsideExperience, which never fired once and said nothing about it. The only
    /// evidence was a branch not taken, which nothing can print.
    ///
    /// They are matches: cells now, and this is what shows them working: every relic weighed this
    /// session, what answered for it, and by which of the three identifiers - modifier, art or
    /// metadata. A "no row" line is a relic priced per modifier through the unknown weights, which is
    /// correct for an ordinary one and a gap for anything special.
    /// </summary>
    private static void Relics(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("=== which row each relic answered to ===");

        var seen = Safe.Read(() => Weighing.WeightAnsweredByCode(), null);

        if (seen is not { Count: > 0 })
        {
            b.AppendLine("  no relic has been weighed this session.");

            return;
        }

        foreach (var (art, said) in seen.OrderBy(x => x.Key, StringComparer.Ordinal))
            b.AppendLine($"  {art}: {said}");
    }

    /// <summary>
    /// What the guarding modifiers say a strongbox holds, beside what the ground label was read as.
    ///
    /// **The check on a path that is now authoritative.** The modifiers decide what a box is worth -
    /// see Weighing.Guarding - and the label parse is kept beside them as a second, independently
    /// computed answer, because six agreements on one site is grounds for switching over and not
    /// grounds for deleting the only thing that could catch it going wrong. The label parse goes when
    /// this has agreed across a season rather than an evening.
    ///
    /// It also shows the guard nothing counts. StrongboxRareRobotGuardNoImmediateSpawn appears in no
    /// sentence, so a label can never see it and the two columns are expected to differ by exactly one
    /// rare per box - which is the point rather than a fault, so it is taken out before the two are
    /// called equal and reported on its own.
    /// </summary>
    private static void Guards(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("=== strongbox guards: the label against the modifiers ===");

        var boxes = Scan?.Targets?
            .Where(t => t is { Kind: TargetKind.Strongbox })
            .ToList() ?? [];

        if (boxes.Count == 0)
        {
            b.AppendLine("  no strongboxes in the scan");

            return;
        }

        var agree = 0;
        var differ = 0;

        foreach (var box in boxes)
        {
            b.AppendLine($"  ({box.Grid.X:0},{box.Grid.Y:0}) {box.Art}");
            b.AppendLine($"      label: {box.Packs} read - implicit " +
                         $"{box.ImplicitPacks}n/{box.ImplicitMagicPacks}m/{box.ImplicitRarePacks}r, " +
                         $"explicit {box.ExplicitPacks}n/{box.ExplicitMagicPacks}m/" +
                         $"{box.ExplicitRarePacks}r");

            var valued = Safe.Read(() => box.Valued, null) ?? [];

            if (valued.Length == 0)
            {
                // Which of the pair was available, because "no modifiers" and "the entity carrying
                // them is not loaded" look identical from here and want different answers. A box is a
                // mound plus a chest and the mods are on the chest. See Target.Held.
                b.AppendLine("      mods: none read - " +
                             $"entity {(Safe.Read(() => box.Entity != null, false) ? "loaded" : "gone")}, " +
                             $"held {(Safe.Read(() => box.Held != null, false) ? "loaded" : "none")}");

                continue;
            }

            b.AppendLine($"      mods: {valued.Length} read from " +
                         (Safe.Read(() => box.Held != null, false) ? "the held chest" : "the object") +
                         $" - {valued.Count(v => v.Implicit)} implicit, " +
                         $"{valued.Count(v => !v.Implicit)} explicit");

            var rare = 0f;
            var magic = 0f;
            var normal = 0f;
            var guard = 0f;
            var said = 0;

            foreach (var (name, values, implicitly) in valued)
            {
                var id = TableGrammar.RowForModifier(name);

                if (id == null)
                    continue;

                said++;

                // No Safe.Read: this reads the table and an int array, never remote memory.
                var (r, m, n) = TableGrammar.Brought(id, values, out var wrong);

                rare += r;
                magic += m;
                normal += n;

                if (string.Equals(id, LateGuard, StringComparison.Ordinal))
                    guard += r + m + n;

                b.AppendLine($"      {(implicitly ? "implicit" : "explicit")} {name} " +
                             $"values=[{string.Join(", ", values)}] -> {id} " +
                             $"gives {r:0.#}r / {m:0.#}m / {n:0.#}n" +
                             (wrong.Length > 0 ? "   !! " + wrong : ""));
            }

            if (said == 0)
            {
                b.AppendLine("      mods: none of them has a row yet");

                continue;
            }

            // **Worked out here rather than asked of Weighing, because Weighing now answers with the
            // modifiers.** The comparison is only worth anything while the two sides are computed
            // independently; reading the live figure would compare the mods against themselves and
            // agree for ever. The old arithmetic was the pack counts off the label times one pack row
            // per rarity, so that is what this is.
            var live = Legacy("pack/rare", box.ImplicitRarePacks + box.ExplicitRarePacks) +
                       Legacy("pack/magic", box.ImplicitMagicPacks + box.ExplicitMagicPacks) +
                       Legacy("pack/normal", box.ImplicitPacks + box.ExplicitPacks +
                                             (box.Packs <= 0 ? 1 : 0));

            var mine = rare + magic + normal;
            var same = Math.Abs(mine - guard - live) < 0.05f;

            if (same)
                agree++;
            else
                differ++;

            b.AppendLine($"      mods {mine:0.#} (what the objective now uses) - late guard " +
                         $"{guard:0.#} = {mine - guard:0.#}, label would have said {live:0.#} - " +
                         (same ? "AGREE" : "DIFFER"));
        }

        b.AppendLine();
        b.AppendLine($"  {agree} agree, {differ} differ, over {boxes.Count} strongbox(es). The late " +
                     "guard is content no label mentions, so it is expected to be missing from the " +
                     "label column.");
    }

    /// <summary>The row for the guard that arrives after the first wave. See Guards.</summary>
    private const string LateGuard = "mod:StrongboxRareRobotGuardNoImmediateSpawn";

    /// <summary>
    /// What the label-parsed pack counts came to, the way the old arithmetic added them up.
    ///
    /// Kept as a second, independent sum for exactly as long as the label parse is kept - see Guards,
    /// and Weighing.Guarding, which is the answer this is checking.
    /// </summary>
    private static float Legacy(string row, int packs)
    {
        if (packs <= 0)
            return 0f;

        var (rare, magic, normal) = TableGrammar.TierSplitOfRow(row);

        return (rare + magic + normal) * packs;
    }

    /// <summary>
    /// Every row of the reference table, in the old notation and the new one side by side.
    ///
    /// **This is what makes stage one checkable rather than asserted.** The claim is that the new
    /// grammar says exactly what the three old columns said, so nothing moves until stage two chooses
    /// to move it - and a claim like that is either printed beside the thing it is about or it is a
    /// hope. A row whose translation reads differently from its Scope, Carries and Combines is a bug
    /// in TableGrammar.Translated, and this is where it shows.
    ///
    /// Complaints included, because a table nobody can trust is worth less than no table: an
    /// unresolved child, a target that is neither a tag nor a row, a cycle. See TableGrammar.Checked and
    /// weight_reference_table.md.
    /// </summary>
    private static void WeightTable(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("=== where a frame goes ===");
        b.Append(Spent.Table());
        var leaves = LeafCalls.Over;
        var frames = Math.Max(1, Spent.Framed);

        b.AppendLine($"  per frame, the primitives everything is built out of: " +
                     $"{leaves.GameReads / frames:N0} reads out of the game, " +
                     $"{leaves.TableQueries / frames:N0} table matches, " +
                     $"{leaves.ComponentReads / frames:N0} component fetches, " +
                     $"{leaves.StateReads / frames:N0} state values - a leaf called thousands of " +
                     "times a frame is a loop nobody meant to write");

        // On its own line because it is the expensive one and the ratio is the whole reading. A
        // throw builds an exception and captures a stack trace, so a read that fails costs
        // thousands of bytes against a successful read's nothing - see LeafCalls.GameReadThrows.
        b.AppendLine($"  of those reads, {leaves.GameReadThrows / frames:N0} a frame THREW and were " +
                     $"swallowed ({(leaves.GameReads > 0 ? 100d * leaves.GameReadThrows / leaves.GameReads : 0d):F1}% " +
                     "of them) - each one costs an exception object and a stack trace, so a path " +
                     "walked every frame into memory that is not there is expensive and silent");

        // **How the host's runtime collects, which is not this plugin's to set and decides what its
        // allocation costs.** Workstation collection suspends every thread for a gen0, and the
        // search allocates from several at once; server collection gives each core its own heap and
        // a far larger budget before it stops anybody. Read at runtime rather than off the host's
        // runtimeconfig, because what the file says and what the process does are two claims.
        b.AppendLine($"  the runtime collects in {(GCSettings.IsServerGC ? "SERVER" : "workstation")} " +
                     $"mode, latency {GCSettings.LatencyMode}, over " +
                     $"{Environment.ProcessorCount} processors - workstation mode stops every " +
                     "thread for a gen0, and the search allocates from several at once");

        var copying = Planner.Copying;

        // A List<Vector2> is 24 bytes of header plus an array of 8 (its own header and length) plus
        // 8 per link, so a copy costs about 40 bytes and 8 a link. Approximate on purpose: the
        // question is whether these are the gigabytes or a rounding error against them.
        b.AppendLine($"  the search copied {copying.Copies:N0} chains holding " +
                     $"{copying.Links:N0} links between them, which is roughly " +
                     $"{(copying.Copies * 40 + copying.Links * 8) / 1024 / 1024:N0}MB of the " +
                     "search's allocation");

        // Verdict is a sealed record of nine fields, so about 88 bytes on the heap for each one.
        b.AppendLine($"  the search built {Planner.Verdicts:N0} verdicts, one per scored chain, " +
                     $"which is roughly {Planner.Verdicts * 88 / 1024 / 1024:N0}MB");

        b.AppendLine("  where a search's allocation goes, by phase - each counts only what it " +
                     "allocated ITSELF, so the lines add up rather than nesting inside each other:");

        foreach (var (name, bytes, calls) in Planner.PhaseTotals())
            if (calls > 0)
                b.AppendLine($"    {name,-14} {bytes / 1024 / 1024,8:N0}MB over {calls,12:N0} calls");

        var routing = Wire.Routing;
        var asked = routing.Hits + routing.Misses;

        b.AppendLine($"  the router answered {routing.Hits:N0} pairs from its cache and searched " +
                     $"for {routing.Misses:N0}" +
                     (asked > 0 ? $" ({routing.Misses * 100d / asked:0.0}% missed)" : "") +
                     " - a cheaper reachability test is only worth building in proportion to the " +
                     "misses");

        var scratching = Planner.Scratching;

        b.AppendLine($"  the scoring tally was reused {scratching.Hits:N0} times and rebuilt " +
                     $"{scratching.Misses:N0} - a reuse that never hits looks exactly like one " +
                     "that works, from the allocation figure alone");

        b.Append(BackgroundWork.Table());

        b.AppendLine($"  the longest gap between two frames was {Spent.Widest:0.0}ms, with " +
                     $"{Spent.Over20} frames over 20ms and {Spent.Over50} over 50ms - measured " +
                     "between Renders, so it sees a stall wherever it happens, including one no " +
                     "stage below had a stopwatch open across");

        var grew = Spent.Allocated;

        b.AppendLine($"  allocated over that window: {grew.Process / 1024:N0}KB by the whole " +
                     $"process, {grew.Thread / 1024:N0}KB by the drawing thread, " +
                     $"{grew.Ours / 1024:N0}KB inside this plugin's two entry points - a pause " +
                     "stops every plugin, so process minus thread is somebody else's garbage and " +
                     "thread minus ours is this plugin's own, outside any stage");

        var collected = Spent.Collected;

        b.AppendLine($"  garbage collections over that window: {collected.Gen0} gen0, " +
                     $"{collected.Gen1} gen1, {collected.Gen2} gen2, stopping the process for " +
                     $"{Spent.Paused:0.0}ms of the window in total - unrelated stages sharing a " +
                     "worst frame means the process stopped, not that each stage is slow");
        b.AppendLine($"  the completion banner has been searched for {Finished.Searches:N0} " +
                     "times since load - a search is a depth-six walk of everything visible");
        // **Missed does not measure the host, whatever the line used to imply.** It counts what the
        // backstop walk classified that the callback path had not recorded - and the callback path
        // drops an entity it cannot classify at the instant the host announces it, which is the
        // least likely moment for the components to be readable. So a miss was almost always our
        // single attempt having been too early, not an entity the host never mentioned. Rescued is
        // what the retry now catches, and between them they say which of the two it is.
        b.AppendLine($"  what the host announced: {Scan.Announced:N0} times - " +
                     $"{Scan.Pushed:N0} became targets, {Scan.AlreadyKnown:N0} were already on " +
                     $"record, {Scan.WithoutGrid:N0} had no position to key on, " +
                     $"{Scan.Refused:N0} were refused as nothing to a dig site");
        b.AppendLine($"  what the membership test refused: {Scan.RefusedPaths}");
        b.AppendLine($"  entities handed over by the host: {Scan.Pushed:N0} became targets " +
                     $"({Scan.HeldForAnotherLook:N0} were kept for the next walk because they " +
                     $"could not be classified yet); the walk has since classified " +
                     $"{Scan.Missed:N0} that the callback had not");
        b.AppendLine($"  last sweep walked {Scan.Walked:N0} entities");
        b.AppendLine($"  interface questions: {Panels.Walked:N0} reached the tree walk, " +
                     $"{Panels.Culled:N0} were culled by the rectangle test against " +
                     $"{Panels.Rects} top level rects, of which {Panels.Wide:N0} sightings were " +
                     "screen sized - a cull that never fires is one whose rects cover everything");
        b.AppendLine($"  placement dots: {Forbidden.Held:N0} held, {Forbidden.Drew:N0} drawn " +
                     $"last frame ({(Forbidden.Held > 0 ? 100f - Forbidden.Drew * 100f / Forbidden.Held : 0f):0}% " +
                     $"culled as off screen before being projected), " +
                     $"{Forbidden.Across:0.###} px per world unit, cull radius " +
                     $"{Forbidden.Radius:0} world");
        b.AppendLine();

        b.AppendLine("=== the weight reference table, v1 beside v2 ===");

        var ids = Wrt.Standing.Select(r => r.Key)
            .Concat(Wrt.Yours.Select(r => r.Key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // **The verdict goes first, because a reader stops at the top.** This section ended with
        // "N complaint(s)" and nothing said so until the last line of a hundred and twenty rows - so
        // a dump reporting seven of them read, from the head, exactly like a clean one. It was missed
        // that way: the head was read, the summary was not, and five red rows were reported as none.
        //
        // The rows are built into their own buffer first so the count is known before the heading is
        // written. That is the whole of the change - the same lines, in the order somebody reads them.
        var body = new StringBuilder();

        var complaints = 0;
        var translated = 0;

        foreach (var id in ids)
        {
            var row = Wrt.Of(id);

            if (row == null)
                continue;

            var effect = row.Effect ?? "";
            var rolled = Safe.Read(() => TableGrammar.Total(id).ToString(), "?");
            var wrongs = Safe.Read(() => TableGrammar.Checked(id), null) ?? [];

            if (effect.Length > 0 && row.Effect == null)
                translated++;

            body.AppendLine($"  {id}   [{Wrt.Status(id)}]");
            // One line now that the v1 cells are gone: there is no second notation to print the
            // row in, and no translation to mark, because a row is what its effect says.
            // Share is on the line because it is the one stored number the row line did not
            // print, and a cell that reads blank in the table with a value on disk is a
            // disagreement nothing here could show. See Catalogue.Drawn.
            body.AppendLine($"      weight={Said(row.Weight)} size={Said(row.Size)} " +
                         $"share={Said(row.Share)} " +
                         $"stacks={(row.Stacks?.ToString() ?? "-")} " +
                         $"children={Quoted(row.Children)} total={rolled} " +
                         $"effect={Quoted(effect)}");

            if (row.Tags != null)
                body.AppendLine($"      tags {Quoted(row.Tags)}");

            foreach (var wrong in wrongs)
            {
                complaints++;
                body.AppendLine($"      !!   {wrong}");
            }
        }

        if (Unknowns.Imported.Count > 0)
        {
            b.AppendLine("  what the one-time unknown.json import did:");

            foreach (var said in Unknowns.Imported)
                b.AppendLine($"    {said}");

            b.AppendLine();
        }

        // **Checked against the type, not against a memory of the type.** See Catalogue.Surfaced.
        foreach (var missing in CellSurfaces())
        {
            complaints++;
            body.AppendLine($"  !!   Wrt.Row.{missing} is stored and appears nowhere - no column, no " +
                            "editor field, no hover line. Give it one or say in Catalogue.Surfaced " +
                            "why it has none");
        }

        // And the claims that are made, checked against the columns that exist. See CellClaims.
        foreach (var claim in CellClaims())
        {
            complaints++;
            body.AppendLine($"  !!   Catalogue.Surfaced: {claim}");
        }

        // **A rune with no share is a rune the reroll pricing says can never be drawn.**
        //
        // Rolls.Runes keeps only the rows whose share is above nought, and Rolling normalises what
        // it kept - so a blank is not "unknown, treated fairly", it is "this never comes up". Four
        // of the five blanks are runes that plainly do come up, one of them Opulent.
        //
        // The shares that exist already sum to one, so the shortfall cannot be handed to the blanks
        // without renormalising the rest, and there is no measurement to renormalise them against.
        // Hence a complaint rather than a number: the hole is real and inventing five figures to
        // hide it would be worse than reporting it.
        var shared = 0f;
        var blank = new List<string>();

        foreach (var id in ids)
        {
            if (Wrt.Id.RuneNamed(id) == null)
                continue;

            var share = Safe.Read(() => Wrt.Of(id)?.Share, null);

            if (share is > 0f)
                shared += share.Value;
            else
                blank.Add(id);
        }

        if (blank.Count > 0)
        {
            complaints++;
            body.AppendLine($"  !!   {blank.Count} rune(s) have no Share and are therefore absent " +
                            $"from the reroll draw pool - {string.Join(", ", blank)}. The " +
                            $"{ids.Count(x => Wrt.Id.RuneNamed(x) != null) - blank.Count} that do " +
                            $"have one sum to {shared:0.###}, so there is no room left to give " +
                            "these a share without remeasuring the others. See Rolls.Runes");
        }

        b.AppendLine($"  {ids.Count} rows ({Wrt.Known} shipped, {Wrt.Changed} yours), " +
                     $"revision {Wrt.Revision}, {translated} translated from the old columns, " +
                     (complaints == 0
                         ? "no complaints"
                         : $"**{complaints} COMPLAINT(S)** - search for !! below"));
        b.AppendLine();
        b.Append(body);
    }

    /// <summary>
    /// Every cell Wrt.Row stores that the table surfaces nowhere.
    ///
    /// Reflection rather than a second hand-written list, because two hand-written lists drift apart and
    /// the drift is the bug: a cell added to Row is invisible until somebody notices, and "somebody
    /// notices" is exactly what failed for Carries, Scope, Combines and Granter. The type is the
    /// authority on what is stored; Catalogue.Surfaced is the authority on where each one shows; this
    /// compares them and says nothing when they agree.
    /// </summary>
    private static string[] CellSurfaces()
    {
        var declared = Catalogue.Surfaced
            .Where(x => !string.IsNullOrWhiteSpace(x.Where))
            .Select(x => x.Cell)
            .ToHashSet(StringComparer.Ordinal);

        return typeof(Wrt.Row)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name)
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Every claim in Catalogue.Surfaced that its own table does not bear out.
    ///
    /// **The list checked that a cell was MENTIONED, never that what it said was true.** So it went
    /// on saying Kind showed on "the Probed line, and search" when Probed draws nothing and the
    /// search does not read that cell, and a reader - this one - described Combines as a column
    /// when it is a hover line. A list of prose nobody tests is the same hiding place as a cell
    /// nobody shows, one level up.
    ///
    /// Two things are checkable without guessing at the drawing code, and both failures above are
    /// one of them: a named column must exist in Catalogue.Columns, and prose that says "column"
    /// must name one. What a hover or an editor field says is still prose, and still on trust.
    /// </summary>
    private static string[] CellClaims()
    {
        var headers = Catalogue.Columns.Select(c => c.Header).ToHashSet(StringComparer.Ordinal);
        var wrong = new List<string>();

        foreach (var (cell, column, where) in Catalogue.Surfaced)
        {
            if (column is { Length: > 0 } && !headers.Contains(column))
            {
                wrong.Add($"{cell} claims the column \"{column}\", and the table has no such " +
                          "column - see Catalogue.Columns");
            }

            if (column is not { Length: > 0 } &&
                (where ?? "").Contains("column", StringComparison.OrdinalIgnoreCase) &&
                !(where ?? "").Contains("no column", StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"{cell} describes itself as a column and names none - say which, or say " +
                          "where it really shows");
            }
        }

        return wrong.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string Said(float? value) =>
        value == null ? "-" : TableGrammar.Printed(value.Value);

    private static string Quoted(string text) =>
        text == null ? "-" : "\"" + text + "\"";

    /// <summary>
    /// Every rune the game knows about.
    ///
    /// Written out to answer one open question: the rune tiers below A are "the rest of the purple"
    /// and "the rest of the blue", and nothing found on a rune so far says which colour it is. If
    /// the art paths differ between the two groups, this is where that will show.
    /// </summary>
    private static void RuneTable(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("=== runes ===");

        var runes = Safe.Read(() => gc.Files.Expedition2Runes.EntriesList, null);

        if (runes == null)
        {
            b.AppendLine("  unreadable");
            return;
        }

        // The relic table covers almost none of these: the runes are MONSTER modifiers, so their
        // stats live in the general description file. The relic one is tried first anyway, since
        // where it does have a line that line is the expedition-specific wording.
        var relic = Safe.Read(() => gc.Files.ExpeditionRelicStatDescriptions, null);
        var general = Safe.Read(() => gc.Files.StatDescriptions, null);

        foreach (var rune in runes)
        {
            b.AppendLine();

            // **The name the game shows, which is not the name the plugin uses.** Expedition2Rune
            // exposes an Id and nothing else - "Gasp" - while the window over the remnant reads
            // "Volcanic Rune". The mod behind the rune carries a UserFriendlyName, and if that is
            // where the displayed word comes from then every rune this plugin names is speaking a
            // language the player's screen does not. Printed to find that out.
            b.AppendLine($"  {Safe.Read(() => rune.Id, "?")}   " +
                         $"shows as \"{Safe.Read(() => rune.Mod?.UserFriendlyName, "") ?? ""}\"   " +
                         $"tier {Runes.Name(Runes.Tier(Safe.Read(() => rune.Id, "")))}");
            b.AppendLine($"    does      {RuneInfo.Describe(Safe.Read(() => rune.Mod, null), relic, general)}");
            b.AppendLine($"    at power  {RuneInfo.Describe(Safe.Read(() => rune.ModPower, null), relic, general)}");
            b.AppendLine($"    sentinel  {RuneInfo.Describe(Safe.Read(() => rune.SentinelMod, null), relic, general)}");
            b.AppendLine($"    art       tome={Safe.Read(() => rune.TomeArt, "")}  remnant={Safe.Read(() => rune.RemnantArt, "")}");
        }
    }

    /// <summary>
    /// Which runes the game will put in which slot, at which socket count, from which level.
    ///
    /// **The support set is stated outright and nothing was reading it out.** Expedition2RunesWeights
    /// is named for weights and exposes none - the open complaint everywhere else in this plugin -
    /// but it does carry, exactly, the rune, the slot, the socket count and the level for every
    /// combination the generator is allowed to produce. Valuation consults it twice and only ever to
    /// FILTER: which socket counts a remnant's own fixed rune permits. Nobody ever asked it the
    /// general question.
    ///
    /// That question is the one the census cannot answer for months. "How often is Opulent the rune
    /// a remnant passes on" needs observations; "can Opulent ever be the rune a remnant passes on,
    /// and in how many of the generator's arrangements" needs this table and one dump. The second is
    /// not the first, and it is most of what a decision about rolling actually turns on.
    ///
    /// Two views of the same rows, because they answer different questions: by rune, for "where can
    /// this one turn up", and by slot, for "what might land in the slot this remnant passes on".
    ///
    /// The arrangement count is a support size, NOT a probability. A rune legal in twice as many
    /// arrangements is not twice as likely - the table withholds the weights, and this says so
    /// rather than quietly standing in for them.
    /// </summary>
    /// <summary>
    /// What the client says about each remnant on this site, before the plugin derives anything.
    ///
    /// **The three reads everything propagating is built on, printed once.** Which slots pass a rune
    /// on, which rune is fixed and where, and which recipes are still reachable all come off the
    /// encounter data, and none of them appeared in a readout - so telling "the game says this" from
    /// "we derived this wrongly" meant tracing four layers of arrays. Every disagreement chased on
    /// this site so far has turned on that distinction.
    ///
    /// Distinct from the rune weights table above, which the two are easy to confuse. That table is
    /// what the GENERATOR may produce, keyed by the recipe's rune count - not by the remnant's
    /// socket count, which is why a two rune recipe is legal on a four socket remnant. This section
    /// is what one particular remnant actually offers.
    /// </summary>
    private static void RemnantSlotsFromTheClient(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("=== what the client says about each remnant's slots ===");
        b.AppendLine("  Slots are the CLIENT's numbering, from zero. The weights table above numbers " +
                     "from one, so its slot 1 is this section's slot 0.");
        b.AppendLine("  A * on a slot means the game passes that slot's rune on; everything else is local.");

        if (Valuation == null || Scan == null)
        {
            b.AppendLine("  no scan or valuation yet");

            return;
        }

        var any = false;

        foreach (var target in Scan.Targets)
        {
            if (target.Kind != TargetKind.Remnant || target.Entity == null)
                continue;

            any = true;

            b.AppendLine();
            b.AppendLine($"  remnant ({target.Grid.X:0},{target.Grid.Y:0}):");
            b.Append("      ").AppendLine(
                Safe.Read(() => Valuation.SlotsAndRecipesOfRemnant(target.Entity), "threw")
                    ?.TrimEnd() ?? "nothing");
        }

        if (!any)
            b.AppendLine("  no remnants in the scan");
    }

    /// <summary>
    /// The atlas passives taken, and whatever the cursor is hovering, with their text.
    ///
    /// **One of these decides a number the reroll advisor needs.** "Double or Nothing" sets how
    /// often a Verisium remnant adds a second runic modifier to future remnants - a propagating
    /// rune, in this plugin's words - and it has three choices. Which one was taken is a fact about
    /// the player that no amount of watching remnants settles quickly: it took eight remnants on one
    /// site to get 2 of 8, which is the right answer and would have been the right answer for 20%
    /// or 30% too. See Rewards.ExtraRunicModifierChance.
    ///
    /// The hovered element is printed alongside because reading a node's wording is how the atlas
    /// gets checked at all, and the dump could previously say only that something was hovered - it
    /// caught the path and the rectangle and none of the words.
    /// </summary>
    private static void AtlasPassives(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("=== atlas passives ===");

        var panel = Safe.Read(gc, static g => g.IngameState.IngameUi.AtlasTreePanel, null);
        var passives = Safe.Read(() => panel?.Passives, null);

        if (passives == null)
        {
            b.AppendLine("  the atlas tree panel is not open - open the atlas and dump again");

            return;
        }

        var taken = 0;

        foreach (var passive in passives)
        {
            if (!Safe.Read(() => passive.IsAllocatedForPlan, false))
                continue;

            taken++;

            var skill = Safe.Read(() => passive.PassiveSkill, null);
            var stats = Safe.Read(() => skill?.Stats?.Select(x => Safe.Read(() => x.ToString(), "?")).ToList(), null);

            b.AppendLine($"  {Safe.Read(() => skill?.Name, null) ?? "(unnamed)"}" +
                         $"  [{Safe.Read(() => skill?.Id, null) ?? "?"}]" +
                         (Safe.Read(() => skill?.IsNotable == true, false) ? "  notable" : "") +
                         (stats is { Count: > 0 } ? "  stats " + string.Join(", ", stats) : ""));
        }

        b.AppendLine($"  {taken} allocated of {passives.Count} on the tree");

        // Whatever is under the cursor, with its words. See Panels.Hovered.
        var over = Safe.Read(gc, static g => g.IngameState.UIHover, null);

        if (over == null || Safe.Read(() => over.Address, 0L) == 0L)
        {
            b.AppendLine("  nothing hovered");

            return;
        }

        b.AppendLine("  hovered, with every text it and its children carry:");

        var said = new List<string>();

        void Walk(ExileCore2.PoEMemory.Element at, int depth)
        {
            if (at == null || depth > 6 || said.Count > 40)
                return;

            var text = Safe.Read(() => at.Text, null);

            if (!string.IsNullOrWhiteSpace(text))
                said.Add(new string(' ', depth * 2) + text);

            foreach (var kid in Safe.Read(() => at.Children, null) ?? [])
                Walk(kid, depth + 1);
        }

        Walk(over, 0);

        if (said.Count == 0)
            b.AppendLine("    (no text on it or its children)");
        else
            foreach (var line in said)
                b.AppendLine("    " + line);
    }

    /// <summary>
    /// The rune weights table as raw bytes, beside the fields ExileCore2 decodes from them.
    ///
    /// **The row is 57 bytes and five fields cannot fill it**, so something in there is not being
    /// read - and the one thing the reroll advisor most wants is a weight, which this table is named
    /// for and does not expose. See Rolls.Runes, whose shares are chosen because of that.
    ///
    /// Printed with the known values so the columns can be located: find SlotCount, RuneSlot and
    /// Level in the bytes, and whatever moves in the gaps is a column nobody is reading. A column
    /// that varies per row, is positive, and is larger for common runes than rare ones would be the
    /// weight.
    ///
    /// A sample rather than all 175 rows, because this is for looking at rather than parsing.
    /// </summary>
    private static void RawWeightRows(StringBuilder b, GameController gc)
    {
        var rows = Safe.Read(gc, static g => g.Files.Expedition2RunesWeights.EntriesList, null);

        if (rows is not { Count: > 0 })
            return;

        // **The column at 36, which is what this table is named for and nobody reads.**
        //
        // Matching the decoded fields into the bytes pins the layout: an eight byte string
        // reference, SlotCount, RuneSlot, the rune's key, a pointer identical on every row, Level,
        // and then a four byte int that ExileCore2 stops short of. It is positive, it varies per
        // row, it is independent of Level, and it is larger for runes the table admits often than
        // for ones it admits rarely - which is what a weight is.
        //
        // Summed per rune so it can be put against Rolls.Runes, whose shares were chosen because
        // this was believed absent. If the two orderings agree, the chosen numbers can be replaced
        // by read ones - including the four runes that have no share at all and are currently
        // dropped from the reroll enumeration entirely.
        var weights = new SortedDictionary<string, (long Total, int Rows, int Least, int Most)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var id = Safe.Read(() => row.Rune?.Id, null);
            var raw = Safe.Read(() => row.M.ReadBytes(row.Address, 57), null);

            if (string.IsNullOrWhiteSpace(id) || raw == null || raw.Length < 40)
                continue;

            var candidate = BitConverter.ToInt32(raw, 36);

            if (!weights.TryGetValue(id, out var had))
                had = (0L, 0, int.MaxValue, int.MinValue);

            weights[id] = (had.Total + candidate, had.Rows + 1,
                Math.Min(had.Least, candidate), Math.Max(had.Most, candidate));
        }

        if (weights.Count > 0)
        {
            var whole = weights.Values.Sum(x => x.Total);

            b.AppendLine($"     the unread int at offset 36, summed per rune over {rows.Count} rows " +
                         $"(total {whole}):");
            b.AppendLine("       rune             rows   total    share   range        chosen share");

            foreach (var (id, what) in weights.OrderByDescending(x => x.Value.Total))
            {
                var chosen = Safe.Read(() => Wrt.Of(Wrt.Id.Rune(id))?.Share, null);

                b.AppendLine($"       {id,-16} {what.Rows,4} {what.Total,7} " +
                             $"{(whole > 0 ? what.Total * 100d / whole : 0d),7:0.00}% " +
                             $"{what.Least,5}-{what.Most,-6} " +
                             $"{(chosen is { } c ? (c * 100f).ToString("0.00") + "%" : "none")}");
            }
        }

        // **Does it sum to something within one selection context?**
        //
        // If the column is a selection weight then the runes eligible for one (rune count, slot)
        // pair are drawn against each other, and their weights should total something structured -
        // a round number most of all. If the totals are arbitrary it is not a weight, and the most
        // likely alternative is a per-arrangement percentage, which every maximum being exactly 100
        // already hints at.
        //
        // Level is left out of the key deliberately: a row states the level it becomes available
        // from, so rows of different levels compete in the same context once the area is deep
        // enough. Grouping by level as well would split one context into several.
        var contexts = new SortedDictionary<(int Count, int Slot), (long Total, int Rows)>();

        foreach (var row in rows)
        {
            var raw = Safe.Read(() => row.M.ReadBytes(row.Address, 57), null);

            if (raw == null || raw.Length < 40)
                continue;

            var key = (Safe.Read(() => row.SlotCount, -1), Safe.Read(() => row.RuneSlot, -1));

            if (!contexts.TryGetValue(key, out var had))
                had = (0L, 0);

            contexts[key] = (had.Total + BitConverter.ToInt32(raw, 36), had.Rows + 1);
        }

        if (contexts.Count > 0)
        {
            b.AppendLine("     summed within one selection context (rune count, slot):");
            b.AppendLine("       count slot  rows   total");

            foreach (var ((count, slot), what) in contexts)
                b.AppendLine($"       {count,5} {slot,4} {what.Rows,5} {what.Total,7}");
        }

        // **What IS this table?** Put its rune sets against the ones the recipes actually use.
        //
        // It is not the propagating draw and not the reroll pool - Opulent does both and is not in
        // it - and it is not the fixed rune draw either, since Protective carries two per cent here
        // and was the fixed rune in none of 1,180 observed remnants. See NOTES 8z.
        //
        // What is left is that it describes which runes may fill which slot of a recipe of a given
        // length. If so its set for (count, slot) equals the set the recipes use at that position.
        // A rune the recipes use and the table omits says the table is a SUBSET - a standard pool,
        // with the rest arriving by some other mechanism - and Opulent is the one to watch, because
        // it sits in recipes and is absent here.
        var tabled = new SortedDictionary<(int Count, int Slot), SortedSet<string>>();

        foreach (var row in rows)
        {
            var id = Safe.Read(() => row.Rune?.Id, null);

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var key = (Safe.Read(() => row.SlotCount, -1), Safe.Read(() => row.RuneSlot, -1) - 1);

            if (!tabled.TryGetValue(key, out var set))
                tabled[key] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            set.Add(id);
        }

        var used = new SortedDictionary<(int Count, int Slot), SortedSet<string>>();

        foreach (var recipe in Safe.Read(gc, static g => g.Files.Expedition2Recipes.EntriesList, null)
                               ?? new List<Expedition2Recipe>())
        {
            var needs = Safe.Read(() => recipe.RuneCountRequired, -1);

            for (var slot = 0; slot < needs; slot++)
            {
                var at = slot;
                var id = Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null);

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (!used.TryGetValue((needs, at), out var set))
                    used[(needs, at)] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                set.Add(id);
            }
        }

        b.AppendLine("     what the table lists against what the recipes use, per (rune count, slot):");
        b.AppendLine("       count slot  table  recipes   in recipes but NOT in the table");

        foreach (var key in used.Keys.Union(tabled.Keys).OrderBy(k => k.Count).ThenBy(k => k.Slot))
        {
            tabled.TryGetValue(key, out var inTable);
            used.TryGetValue(key, out var inUse);

            var missing = inUse == null
                ? new List<string>()
                : inUse.Where(x => inTable == null || !inTable.Contains(x)).ToList();

            b.AppendLine($"       {key.Count,5} {key.Slot,4} {inTable?.Count ?? 0,6} {inUse?.Count ?? 0,8}   " +
                         (missing.Count == 0 ? "-" : string.Join(", ", missing)));
        }

        // **The whole table, compactly, because the column only varies at the short recipes.**
        //
        // Every row at rune count five and above reads exactly 100; counts two, three and four are
        // the only place anything moves, and the mean climbs with the count - about 35 at two, 83
        // at three, 90 at four, then flat. Whatever the column means is visible there and nowhere
        // else, and fourteen rows of hex were not enough to read it.
        //
        // Sorted by the context so rows that compete with each other sit together.
        var all = new List<(int Count, int Slot, int Level, string Rune, int Value)>();

        foreach (var row in rows)
        {
            var raw = Safe.Read(() => row.M.ReadBytes(row.Address, 57), null);

            if (raw == null || raw.Length < 40)
                continue;

            all.Add((Safe.Read(() => row.SlotCount, -1),
                Safe.Read(() => row.RuneSlot, -1),
                Safe.Read(() => row.Level, -1),
                Safe.Read(() => row.Rune?.Id, null) ?? "?",
                BitConverter.ToInt32(raw, 36)));
        }

        // **Every four byte window of the row, and how much it moves.**
        //
        // The game calls this file Weights - Data/Balance/Expedition2RunesWeights.dat is a literal
        // string in the client - and the columns accounted for are the rune, its slot, the recipe
        // length and a level range. None of those is a weight. Three fields are still unidentified,
        // so this surveys the whole row rather than guessing at one: a column taking many distinct
        // positive values that is not one of the known five is what a weight would look like, and a
        // column that is constant or increments by one is not.
        var windows = new SortedDictionary<int, SortedSet<int>>();

        foreach (var row in rows)
        {
            var raw = Safe.Read(() => row.M.ReadBytes(row.Address, 57), null);

            if (raw == null || raw.Length < 57)
                continue;

            for (var at = 0; at + 4 <= 57; at += 1)
            {
                if (!windows.TryGetValue(at, out var seen))
                    windows[at] = seen = new SortedSet<int>();

                if (seen.Count <= 40)
                    seen.Add(BitConverter.ToInt32(raw, at));
            }
        }

        b.AppendLine("     every four byte window of the row, by how many values it takes:");
        b.AppendLine("       offset  distinct  sample");

        foreach (var (at, seen) in windows)
        {
            if (at % 4 != 0)
                continue;

            var known = at switch
            {
                8 => "  <- SlotCount", 12 => "  <- RuneSlot", 32 => "  <- min level",
                36 => "  <- max level", _ => "",
            };

            b.AppendLine($"       {at,6} {(seen.Count > 40 ? "40+" : seen.Count.ToString()),9}  " +
                         string.Join(",", seen.Take(10)) + known);
        }

        // **What the pointer at 44 points at, and is it one per recipe length?**
        //
        // It takes nine distinct values and there are nine distinct SlotCounts, and its values sit
        // a few bytes after the Id pointer's - which is what a second string adjacent to the first
        // looks like in a UTF-16 block. If each value pairs with exactly one SlotCount the guess
        // holds; if one value spans several, it is keyed on something else.
        //
        // The string is read as UTF-16 because that is how the client stores them - see the .dat
        // path strings in .rdata, which are utf-16 too.
        var pointed = new SortedDictionary<long, (SortedSet<int> Counts, int Rows, string Text)>();

        foreach (var row in rows)
        {
            var raw = Safe.Read(() => row.M.ReadBytes(row.Address, 57), null);

            if (raw == null || raw.Length < 52)
                continue;

            var at = BitConverter.ToInt64(raw, 44);
            var count = Safe.Read(() => row.SlotCount, -1);

            if (!pointed.TryGetValue(at, out var had))
            {
                // Two bytes a character, stopping at the first null pair. Bounded, because a bad
                // pointer would otherwise read until something threw.
                var text = Safe.Read(() =>
                {
                    var bytes = row.M.ReadBytes(at, 64);

                    if (bytes == null)
                        return null;

                    var end = 0;

                    while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
                        end += 2;

                    return System.Text.Encoding.Unicode.GetString(bytes, 0, end);
                }, null);

                had = (new SortedSet<int>(), 0, text);
            }

            had.Counts.Add(count);
            pointed[at] = (had.Counts, had.Rows + 1, had.Text);
        }

        b.AppendLine("     what the pointer at offset 44 points at:");
        b.AppendLine("       address           rows  SlotCounts        text");

        foreach (var (at, what) in pointed)
            b.AppendLine($"       {at:X12}  {what.Rows,6}  {string.Join(",", what.Counts),-16}  " +
                         (string.IsNullOrEmpty(what.Text) ? "(empty or unreadable)" : what.Text));

        b.AppendLine($"     every row, sorted by context ({all.Count} of {rows.Count} readable):");
        b.AppendLine("       count slot level rune             value");

        foreach (var one in all.OrderBy(x => x.Count).ThenBy(x => x.Slot)
                               .ThenByDescending(x => x.Value).ThenBy(x => x.Rune))
            b.AppendLine($"       {one.Count,5} {one.Slot,4} {one.Level,5} {one.Rune,-16} {one.Value,5}");
    }

    /// <summary>
    /// Every recipe in the game, with the rune it puts in each slot.
    ///
    /// **The raw material for any question of the form "which recipes use X".** The per-remnant
    /// sections above list only what the remnants on this site can reach, so a question about a
    /// rune that no remnant here happens to offer could not be answered from a dump at all - which
    /// has meant guessing at recipe contents several times.
    ///
    /// Ordered by rune count then id, so recipes that compete for the same remnant sit together.
    /// The value is what the reward prices at, through the same path the planner uses.
    /// </summary>
    private static void EveryRecipe(StringBuilder b, GameController gc)
    {
        var recipes = Safe.Read(gc, static g => g.Files.Expedition2Recipes.EntriesList, null);

        if (recipes is not { Count: > 0 })
            return;

        b.AppendLine();
        b.AppendLine($"=== every recipe in the game ({recipes.Count}) ===");
        b.AppendLine("  len  id                                  reward                          runes by slot");

        var lines = new List<(int Needs, string Id, string Line)>();

        foreach (var recipe in recipes)
        {
            var needs = Safe.Read(() => recipe.RuneCountRequired, 0);
            var id = Safe.Read(() => recipe.Id, null) ?? "?";
            var runes = new List<string>(needs);

            for (var slot = 0; slot < needs; slot++)
            {
                var at = slot;

                runes.Add(Safe.Read(() => recipe.Runes.ElementAtOrDefault(at)?.Id, null) ?? "-");
            }

            lines.Add((needs, id,
                $"  {needs,3}  {Short(id),-34}  {Short(Valuation.Name(recipe) ?? "?"),-30}  " +
                string.Join(" ", runes)));
        }

        foreach (var one in lines.OrderBy(x => x.Needs).ThenBy(x => x.Id, StringComparer.Ordinal))
            b.AppendLine(one.Line);

        RewardsWithNoPrice(b, recipes, Valuation);
    }

    /// <summary>
    /// Every reward the price list values at nothing, with the two fields that decide whether it can
    /// be valued at all.
    ///
    /// **This plugin does no name lookup for a reward, so a miss is not ours to spell wrong.**
    /// BuildPrices passes recipe.Reward - the BaseItemType straight out of Expedition2Recipes - into
    /// NinjaPricer's bridge, and NinjaPricer builds a CustomItem from it and calls ComputeType before
    /// it prices anything. ComputeType reads ClassName and Metadata and nothing else on that path: the
    /// Currency branch wants ClassName "StackableCurrency", the Expedition branch wants one of a short
    /// list of class names and paths, and a reward that matches none of them gets no type and
    /// therefore no price.
    ///
    /// So those two fields are what a miss turns on, and neither was printed anywhere. Reported on
    /// Perfect Flux, which has a price on poe.ninja and came through as nought; 100 of 322 recipes are
    /// unpriced in the same dump, so it is a class of rewards rather than one item. With the class
    /// names in hand the answer is either a report upstream or an override written knowing why.
    ///
    /// Grouped by ClassName, because the interesting fact is which classes are being dropped whole.
    /// </summary>
    private static void RewardsWithNoPrice(StringBuilder b, List<Expedition2Recipe> recipes,
        Valuation valuation)
    {
        if (valuation == null)
            return;

        var missed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var counted = 0;

        foreach (var recipe in recipes)
        {
            // **A custom price does not hide the reward's fields.** The first version skipped anything
            // with a price at all, so a reward somebody had already worked around - which is exactly
            // the one being asked about - was the one this could not show. An override is marked and
            // listed: it says the list still cannot answer for that reward, which is the fact wanted.
            var custom = Safe.Read(() => valuation.Overridden(recipe), false);

            if (!custom && Safe.Read(() => valuation.Value(recipe), 0d) > 0d)
                continue;

            var reward = Safe.Read(() => recipe.Reward, null);
            var kind = Safe.Read(() => reward?.ClassName, null) ?? "(no reward)";
            var name = Safe.Read(() => reward?.BaseName, null) ?? "(none)";
            var path = Safe.Read(() => reward?.Metadata, null) ?? "";

            if (!missed.TryGetValue(kind, out var named))
                missed[kind] = named = new List<string>();

            var line = (path.Length > 0 ? $"{name}  [{path}]" : name) +
                       (custom ? "   [custom price set, so the list is still not answering]" : "");

            if (!named.Contains(line))
                named.Add(line);

            counted++;
        }

        b.AppendLine();
        b.AppendLine($"=== rewards the price list values at nothing ({counted} of {recipes.Count} recipes) ===");
        b.AppendLine("  Grouped by ClassName, which with Metadata is what NinjaPricer's ComputeType reads.");
        b.AppendLine("  A class listed here is one it gives no ItemType to, so it prices nothing in it.");

        // **A dump taken before NinjaPricer has fetched lists everything and means nothing.** Seen at
        // 315 of 322 with Chaos Orb and Divine Orb among them, which is not a classification problem -
        // it is the fetch not having produced anything. Without this line that reading looks like a
        // finding.
        //
        // Through Unpriced, which is the test the score card already prints this state from, so the
        // two cannot disagree about it. Priced is the wrong signal and was tried first: it only says
        // the bridge answered, and a bridge that answers nought for everything sets it true - which is
        // exactly the state this is meant to catch.
        if (Safe.Read(() => valuation.Unpriced(), null) != null)
        {
            b.AppendLine();
            b.AppendLine($"  !!   THE PRICE LIST IS NOT ANSWERING - {Safe.Read(() => valuation.Priceless(), "?")}.");
            b.AppendLine("       So every reward below is unpriced for that reason, and the grouping says");
            b.AppendLine("       nothing about which classes NinjaPricer can price. Take another dump once");
            b.AppendLine("       prices have arrived: the score card stops saying Failed to fetch.");
        }

        if (counted == 0)
        {
            b.AppendLine("  none - every recipe has a price");

            return;
        }

        foreach (var (kind, named) in missed.OrderByDescending(x => x.Value.Count))
        {
            b.AppendLine($"  ClassName \"{kind}\" - {named.Count} distinct reward(s)");

            foreach (var one in named.OrderBy(x => x, StringComparer.Ordinal).Take(12))
                b.AppendLine($"      {one}");

            if (named.Count > 12)
                b.AppendLine($"      ... and {named.Count - 12} more");
        }
    }

    /// <summary>
    /// A game data table's row stride and row count, read by reflection.
    ///
    /// FileInMemory exposes both but not publicly, and they answer a question nothing else can: a
    /// stride wider than the fields ExileCore2 reads is a column going unread. The rune weights
    /// table is the one that matters - it is called Weights and exposes none, which is why the
    /// reroll advisor has to be given odds rather than read them.
    /// </summary>
    private static string Stride(object file, string called)
    {
        if (file == null)
            return called + " unreadable";

        object Of(string name) => file.GetType()
            .GetProperty(name, System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(file);

        return $"{called} {Safe.Read(() => Of("RecordLength"), null) ?? (object)"?"} bytes over " +
               $"{Safe.Read(() => Of("NumberOfRecords"), null) ?? (object)"?"} rows";
    }

    private static void RuneSlots(StringBuilder b, GameController gc)
    {
        b.AppendLine();
        b.AppendLine("=== which runes may sit in which slot ===");

        var weights = Safe.Read(() => gc.Files.Expedition2RunesWeights.EntriesList, null);

        if (weights == null)
        {
            b.AppendLine("  unreadable");

            return;
        }

        var level = Safe.Read(() => gc.IngameState.Data.CurrentAreaLevel, 0);

        b.AppendLine($"  {weights.Count} arrangements in the table; this area is level {level}.");
        b.AppendLine("  Slots and socket counts are as the GAME numbers them - slot 1 is the first " +
                     "socket. The census file is zero based, so its slot 0 is this table's slot 1.");
        b.AppendLine("  A count here is how many arrangements admit that rune, which is the size of " +
                     "the support and NOT a probability: the table states no weights.");

        // rune -> slots, socket counts, lowest level
        var byRune = new Dictionary<string, (SortedSet<int> Slots, SortedSet<int> Counts, int Least, int Rows, int Here)>(
            StringComparer.OrdinalIgnoreCase);

        // slot -> the runes that may sit there at this area level
        var bySlot = new Dictionary<int, SortedSet<string>>();

        foreach (var row in weights)
        {
            var rune = Safe.Read(() => row.Rune?.Id, null);

            if (string.IsNullOrWhiteSpace(rune))
                continue;

            var slot = Safe.Read(() => row.RuneSlot, -1);
            var count = Safe.Read(() => row.SlotCount, -1);
            var least = Safe.Read(() => row.Level, 0);
            var here = least <= level;

            if (!byRune.TryGetValue(rune, out var had))
                had = (new SortedSet<int>(), new SortedSet<int>(), int.MaxValue, 0, 0);

            if (slot > 0)
                had.Slots.Add(slot);

            if (count > 0)
                had.Counts.Add(count);

            byRune[rune] = (had.Slots, had.Counts, Math.Min(had.Least, least), had.Rows + 1,
                had.Here + (here ? 1 : 0));

            if (!here || slot <= 0)
                continue;

            if (!bySlot.TryGetValue(slot, out var runes))
                bySlot[slot] = runes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            runes.Add(rune);
        }

        b.AppendLine();
        b.AppendLine($"  -- by rune ({byRune.Count} of them), richest support first --");

        foreach (var (rune, what) in byRune.OrderByDescending(x => x.Value.Here).ThenBy(x => x.Key))
        {
            b.AppendLine(
                $"  {rune,-16} tier {Runes.Name(Runes.Tier(rune)),-8} " +
                $"{what.Here,4} of {what.Rows,4} arrangements at this level, " +
                $"slots [{string.Join(" ", what.Slots)}], " +
                $"socket counts [{string.Join(" ", what.Counts)}], " +
                $"from level {(what.Least == int.MaxValue ? 0 : what.Least)}");
        }

        // **What a roll would have to enumerate**, which decides whether the reroll advisor can
        // afford to be exact. A roll draws a socket count, then a rune and the slot it is fixed in,
        // and every one of those is a separate shape to price. The pair count per socket count is
        // that enumeration's width - see Valuation.FixedRunesPossibleAt.
        if (Valuation != null)
        {
            var sizes = new List<string>();

            foreach (var (sockets, _) in Rolls.Sockets)
            {
                var pairs = Safe.Read(() => Valuation.FixedRunesPossibleAt(sockets).Count, -1);

                sizes.Add($"{sockets} sockets: {pairs}");
            }

            b.AppendLine();
            b.AppendLine("  -- (rune, slot) pairs a roll could leave fixed, per socket count --");
            b.AppendLine("     " + string.Join("   ", sizes));

            // **The pairs themselves, not only how many.** The count alone cannot be checked against
            // anything. The per-rune summary above collapses each rune's rune-counts and its slots
            // across different arrangements, so a reader cannot pair them back up - it says Adaptive
            // has counts [2..9] and slots [1 2 4] without saying which slot goes with which count,
            // and a support built by combining them is far too wide: 23 runes admitted at three
            // sockets where only 8 have ever been seen over 172 fresh readings.
            //
            // Listed so the observed fixed runes in remnants.csv can be tested against the set the
            // game actually admits, which is the one question the table answers and the odds are
            // separate from. See Valuation.FixedRunesPossibleAt.
            foreach (var (sockets, _) in Rolls.Sockets)
            {
                var pairs = Safe.Read(() => Valuation.FixedRunesPossibleAt(sockets), null);

                if (pairs == null)
                    continue;

                var named = new List<string>(pairs.Count);

                foreach (var (rune, slot) in pairs)
                    named.Add($"{Safe.Read(() => rune?.Id, null) ?? "?"}@{slot}");

                named.Sort(StringComparer.Ordinal);

                b.AppendLine($"     {sockets} sockets, {pairs.Count}: {string.Join(", ", named)}");
            }
            b.AppendLine("     " + Safe.Read(() => Rolling.EnumerationWidth(Valuation, Settings), "unreadable"));

            // **Is there a column in the table that ExileCore2 does not read?**
            //
            // The file is called Weights and exposes none: Id, SlotCount, RuneSlot, Rune and Level
            // and nothing else. That matters because the reroll advisor has to invent the odds it
            // would otherwise read here - see Rolls.Runes, whose shares are chosen.
            //
            // RecordLength is the row stride the client parsed the file at, so a stride wider than
            // those five fields need is a column going unread. Printed beside the recipe table for
            // scale. Five fields want about 28 to 36 bytes: an eight byte string reference, two
            // four byte ints, a foreign key of eight or sixteen, and a four byte int.
            RawWeightRows(b, gc);

            b.AppendLine("     row stride: " +
                         Stride(Safe.Read(gc, static g => g.Files.Expedition2RunesWeights, null), "weights") +
                         "; " +
                         Stride(Safe.Read(gc, static g => g.Files.Expedition2Recipes, null), "recipes"));
        }

        b.AppendLine();
        b.AppendLine("  -- by slot, at this area level --");

        foreach (var (slot, runes) in bySlot.OrderBy(x => x.Key))
            b.AppendLine($"  slot {slot}: {runes.Count} runes - {string.Join(", ", runes)}");
    }

    private static void Entities(StringBuilder b, GameController gc, int range)
    {
        var all = Safe.Read(() => gc.EntityListWrapper.Entities?.ToList(), null) ?? new List<Entity>();

        b.AppendLine();
        b.AppendLine($"=== entities ({all.Count} in the list) ===");

        // Anything at all with "expedition" in it, at any distance, because a controller or a
        // detonator can sit well outside the range a chest sweep would use.
        var expedition = all.Where(x => Mentions(x, "expedition")).ToList();
        Section(b, $"mentions \"expedition\" ({expedition.Count})", expedition);

        // And then the two kinds this plugin exists to stop ignoring, by type rather than by name,
        // so the dump shows what they are actually called rather than confirming a guess.
        var chests = all.Where(x => Safe.Read(() => x.Type, EntityType.Error) == EntityType.Chest)
            .Where(x => Near(x, range))
            .Where(x => !Mentions(x, "expedition"))
            .ToList();
        Section(b, $"chests within {range} ({chests.Count})", chests);

        var monsters = all.Where(x => Safe.Read(() => x.Type, EntityType.Error) == EntityType.Monster)
            .Where(x => Near(x, range))
            .Where(x => !Mentions(x, "expedition"))
            .ToList();
        Section(b, $"monsters within {range} ({monsters.Count})", monsters);

        // The markers, grouped by everything that might tell one from another. If two markers that
        // stand for different content agree on every line here, then nothing in this dump
        // classifies them and the next place to look is the file tables.
        b.AppendLine();
        b.AppendLine("=== markers, grouped by what might classify them ===");

        var markers = expedition
            .Where(x => Safe.Read(() => x.Metadata, "")?.EndsWith("ExpeditionMarker", StringComparison.Ordinal) == true)
            .ToList();

        var groups = markers
            .GroupBy(x => $"base={Safe.Read(() => x.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Path, "-")} " +
                          $"ao={Safe.Read(() => x.GetComponent<Animated>()?.MiscAnimated?.AOFile, "-")} " +
                          $"miscId={Safe.Read(() => x.GetComponent<Animated>()?.MiscAnimated?.Id, "-")} " +
                          $"bounds={Describe(Safe.Read(() => x.GetComponent<Render>()?.Bounds ?? default, default(System.Numerics.Vector3)))}")
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
            b.AppendLine($"  {group.Count(),3}x  {group.Key}");

        // The compact view, and the one to diff. Two dumps - the placement indicator over a cluster
        // in one and well away in the other - and whatever changed here is the highlight.
        b.AppendLine();
        b.AppendLine("=== highlight candidates, one line each (diff two dumps to find the signal) ===");

        foreach (var entity in expedition.Concat(chests).Concat(monsters))
        {
            b.AppendLine($"  {Describe(Safe.Read(() => entity.GridPos, default))} " +
                         $"ao={Safe.Read(() => entity.GetComponent<Animated>()?.MiscAnimated?.AOFile, "-")} " +
                         $"targetable={Safe.Read(() => entity.IsTargetable.ToString(), "?")} " +
                         $"isTargeted={Safe.Read(() => entity.GetComponent<Targetable>()?.isTargeted.ToString(), "-")} " +
                         $"canBeTarget={Safe.Read(() => entity.GetComponent<StateMachine>()?.CanBeTarget.ToString(), "-")} " +
                         $"inTarget={Safe.Read(() => entity.GetComponent<StateMachine>()?.InTarget.ToString(), "-")} " +
                         $"states=[{States(entity)}] " +
                         $"{Safe.Read(() => entity.Metadata, "?")}");
        }
    }

    private static void Section(StringBuilder b, string title, List<Entity> entities)
    {
        b.AppendLine();
        b.AppendLine($"--- {title} ---");

        foreach (var entity in entities)
            Describe(b, entity);
    }

    private static void Describe(StringBuilder b, Entity entity)
    {
        b.AppendLine();
        b.AppendLine($"  [{Safe.Read(() => entity.Id, 0u)}] {Safe.Read(() => entity.Type.ToString(), "?")}  " +
                     $"{Safe.Read(() => entity.Metadata, "")}");

        var path = Safe.Read(() => entity.Path, "");
        var metadata = Safe.Read(() => entity.Metadata, "");

        // Metadata truncates at the fourth slash and Path does not, so where they differ the tail
        // is the part a prefix match would miss and an exact match would trip over.
        if (path != metadata)
            b.AppendLine($"    path      {path}");

        // Scale is the other half of a radius: EffectZones sizes every ground effect as
        // MiscAnimated.BaseSize times this, and it is where a map's modifiers show up.
        b.AppendLine($"    scale     {Safe.Read(() => entity.GetComponent<Positioned>()?.Scale.ToString("0.####"), "-")}");

        b.AppendLine($"    grid      {Describe(Safe.Read(() => entity.GridPos, default))}  " +
                     $"world {Describe(Safe.Read(() => entity.Pos, default))}  " +
                     $"distance {Safe.Read(() => entity.DistancePlayer, -1f):0.#}");

        // **Rotation, which nothing in this plugin reads and which may matter for the big objects.**
        //
        // Every extent here is one radius from GridPos, so a circle - and a circle cannot be rotated,
        // which is why this was never needed. It stops being safe the moment an object's real shape is
        // not a circle: a 15x15 footprint turned a quarter turn has its far corners somewhere quite
        // different from where a symmetric radius puts them, and a sub-area cap is drawn visibly away
        // from the position the game reports for it.
        //
        // Printed in radians as the game holds it and in quarter turns beside it, because quarter
        // turns are what tilesets actually use - ExpeditionIcons rotates the detonator's own exclusion
        // rectangle by exactly that, Math.Round(rotation / (PI / 2)), and does nothing of the sort for
        // content. Neither plugin models rotated content at all.
        // **The height of the floor under it, against the height the entity reports.**
        //
        // Entity.Pos.Z is where the object's origin is, and some objects have theirs well below the
        // ground - a marker projected through Camera.WorldToScreen at that height lands away from the
        // art by an amount that moves with the camera, since the error is vertical and the projection
        // is oblique. The two figures side by side are what says whether that is happening here, and
        // ToWorldWithTerrainHeight is the call Target.Grounded now uses for exactly this.
        var floor = Safe.Read(() => _dumping.IngameState.Data.ToWorldWithTerrainHeight(
            Safe.Read(() => entity.GridPos, default)), default(System.Numerics.Vector3));

        var sits = Safe.Read(() => entity.Pos.Z, 0f);

        b.AppendLine($"    height    entity z {sits:0.#}, ground z {floor.Z:0.#}, " +
                     $"buried by {floor.Z - sits:0.#}" +
                     (MathF.Abs(floor.Z - sits) > 20f
                         ? "  <- FAR OFF THE FLOOR, so anything drawn at the entity's own height " +
                           "misses the art"
                         : ""));

        // **Where the ART is, which is the thing a marker is meant to sit on.**
        //
        // Every position used until now came off the entity or the terrain, and neither is the model:
        // a sub-area cap's marker was 196 world units from its art on height alone, and a gap of about
        // 35 remained after that was fixed. Render carries its own position and an interact centre, so
        // the offset between entity and art is a figure to read rather than a slider to drag - and read
        // per object, it says whether the gap is a rotation of a fixed offset or something else, which
        // two caps facing the same way cannot say.
        var art = Safe.Read(() => entity.GetComponent<Render>()?.Pos, null);
        var touch = Safe.Read(() => entity.GetComponent<Render>()?.InteractCenter, null);
        var self = Safe.Read(() => entity.Pos, default(System.Numerics.Vector3));

        if (art is { } drawn)
        {
            var gap = drawn - self;

            b.AppendLine($"    art at    {Describe(drawn)}  interact {(touch is { } c ? Describe(c) : "-")}");
            b.AppendLine($"    art gap   {Describe(gap)}  " +
                         $"flat {MathF.Sqrt(gap.X * gap.X + gap.Y * gap.Y):0.#} world " +
                         $"({MathF.Sqrt(gap.X * gap.X + gap.Y * gap.Y) / Detonator.GridToWorld:0.##} grid)" +
                         (MathF.Sqrt(gap.X * gap.X + gap.Y * gap.Y) > 5f
                             ? "  <- the art is NOT where the entity says"
                             : ""));
        }

        // **Everything Render and Positioned actually expose, because guessing member names missed
        // the ones that matter.**
        //
        // Four sessions of this were spent inferring an object's true position from Entity.Pos and a
        // terrain lookup of the plugin's own. Enumerated properly, Render carries its own TerrainHeight,
        // Height and UnclampedHeight, a MeshRotation and a Rotation that are Vector3 rather than the
        // single angle Positioned holds; Positioned carries GridPosition alongside GridPos, plus
        // WorldPos and Scale. None of it was being read. The lesson is in the method: list a type's
        // members, do not guess them.
        var drawnBy = Safe.Read(entity, static e => e.GetComponent<Render>(), null);
        var placed = Safe.Read(entity, static e => e.GetComponent<Positioned>(), null);

        if (drawnBy != null)
        {
            b.AppendLine($"    render+   terrainHeight {Safe.Read(drawnBy, static r => r.TerrainHeight, 0f):0.#}  " +
                         $"height {Safe.Read(drawnBy, static r => r.Height, 0f):0.#}  " +
                         $"unclamped {Safe.Read(drawnBy, static r => r.UnclampedHeight, 0f):0.#}");

            b.AppendLine($"    render rot  rotation {Describe(Safe.Read(drawnBy, static r => r.Rotation, default(System.Numerics.Vector3)))}  " +
                         $"mesh {Describe(Safe.Read(drawnBy, static r => r.MeshRotation, default(System.Numerics.Vector3)))}  " +
                         $"deg {Safe.Read(drawnBy, static r => r.RotationInDegrees, 0f):0.##}");
        }

        if (placed != null)
        {
            b.AppendLine($"    positioned  gridPos {Describe(Safe.Read(placed, static p => p.GridPos, default(System.Numerics.Vector2)))}  " +
                         $"gridPosition {Safe.Read(placed, static p => p.GridPosition.X, 0)},{Safe.Read(placed, static p => p.GridPosition.Y, 0)}  " +
                         $"worldPos {Describe(Safe.Read(placed, static p => p.WorldPos, default(System.Numerics.Vector2)))}  " +
                         $"scale {Safe.Read(placed, static p => p.Scale, 0f):0.###}  " +
                         $"flags {Safe.Read(placed, static p => p.Flags.ToString(), "-")}");
        }

        var turn = Safe.Read(() => entity.GetComponent<Positioned>()?.Rotation, null);

        b.AppendLine($"    rotation  " + (turn is { } radians
            ? $"{radians:0.####} rad, {radians / (MathF.PI / 2f):0.##} quarter turns"
            : "not readable"));

        b.AppendLine($"    flags     valid={Safe.Read(() => entity.IsValid.ToString(), "?")} " +
                     $"alive={Safe.Read(() => entity.IsAlive.ToString(), "?")} " +
                     $"targetable={Safe.Read(() => entity.IsTargetable.ToString(), "?")} " +
                     $"hidden={Safe.Read(() => entity.IsHidden.ToString(), "?")} " +
                     $"opened={Safe.Read(() => entity.IsOpened.ToString(), "?")} " +
                     $"rarity={Safe.Read(() => entity.Rarity.ToString(), "?")}");

        var states = States(entity);

        if (states.Length > 0)
            b.AppendLine($"    states    {states}");

        var magic = Safe.Read(() => entity.GetComponent<ObjectMagicProperties>(), null);

        if (magic != null)
        {
            b.AppendLine($"    magic     rarity={Safe.Read(() => magic.Rarity.ToString(), "?")} " +
                         $"mods=[{string.Join(", ", Safe.Read(() => magic.Mods, null) ?? new List<string>())}]");

            b.AppendLine("    implicit  [" +
                         string.Join(", ", Safe.Read(() => magic.ImplicitMods, null) ??
                                           new List<string>()) + "]");

            // **The values behind the names, which nothing has ever read.**
            //
            // Mods above is a List<string> of ids and carries no numbers, which is why a strongbox's
            // pack count is parsed off the ground label instead - and Scan.ExplicitPacks writes that
            // down as though it were a fact about the game. It is a fact about THIS property.
            // ExplicitModData is a different one: it returns List<ItemMod>, and an ItemMod carries
            // Values, Level, Group and a ModRecord.
            //
            // So the claim becomes testable rather than inherited. If Values holds the pack count, a
            // modifier row in the weight reference table can name its own source and the label parse
            // goes away; if it does not, the label stays and the row says so. See
            // weight_reference_table.md, stage nought.
            Detailed(b, "implicitData", Safe.Read(() => magic.ImplicitModData, null));
            Detailed(b, "explicitData", Safe.Read(() => magic.ExplicitModData, null));
        }

        // What a monster is carrying, and what it says it is worth.
        //
        // **This is the only place a rune's magnitude could still be hiding.** The tome states every
        // other rune's numbers outright and states Opulent's as a flag - HasOpulentRuneMod:1, with
        // no quantity anywhere - so whatever it does is applied to the monsters rather than written
        // on the rune. Seen on a live one, the buff reads monster_rare_effect_buff with no value,
        // which is the same flag wearing a different name; if the amount exists at all it is a stat
        // on the monster, and nothing was reading those.
        //
        // Both are dumped raw. Guessing which stat to look for is how several hours went into the
        // rune table before the answer turned out not to be there.
        var buffs = Safe.Read(() => entity.GetComponent<Buffs>()?.BuffsList, null);

        if (buffs is { Count: > 0 })
        {
            var said = new List<string>();

            foreach (var buff in buffs)
            {
                said.Add($"{Safe.Read(() => buff.Name, "?")}" +
                         $"(charges {Safe.Read(() => buff.BuffCharges.ToString(), "?")}, " +
                         $"stacks {Safe.Read(() => buff.BuffStacks.ToString(), "?")}, " +
                         $"timer {Safe.Read(() => buff.Timer.ToString("0.#"), "?")})");
            }

            b.AppendLine($"    buffs     {string.Join(", ", said)}");
        }

        var carried = Safe.Read(() => entity.GetComponent<Stats>()?.StatDictionary, null);

        if (carried is { Count: > 0 })
        {
            var said = new List<string>();

            foreach (var (stat, value) in carried)
                said.Add($"{stat}={value}");

            said.Sort(StringComparer.Ordinal);

            b.AppendLine($"    stats     {string.Join(", ", said)}");
        }

        var chest = Safe.Read(() => entity.GetComponent<Chest>(), null);

        if (chest != null)
        {
            b.AppendLine($"    chest     opened={Safe.Read(() => chest.IsOpened.ToString(), "?")} " +
                         $"locked={Safe.Read(() => chest.IsLocked.ToString(), "?")} " +
                         $"large={Safe.Read(() => chest.IsLarge.ToString(), "?")} " +
                         $"strongbox={Safe.Read(() => chest.IsStrongbox.ToString(), "?")}");
        }

        var icon = Safe.Read(() => entity.GetComponent<MinimapIcon>(), null);

        if (icon != null)
        {
            // IconDat.Id is the thing worth having. Every marker in a dig site is the same entity
            // with the same (empty) Name, so the icon the game picks for it is the only thing
            // saying whether it stands for a chest, a runic monster or an elite - and the marker is
            // all there is to go on, because none of that content exists as an entity until the
            // explosives go off.
            b.AppendLine($"    minimap   name={Safe.Read(() => icon.Name, "")} " +
                         $"hide={Safe.Read(() => icon.IsHide.ToString(), "?")} " +
                         $"iconDat={Safe.Read(() => icon.IconDat?.Id, "-")} " +
                         $"large={Safe.Read(() => icon.IconDat?.LargeMinimapSize.ToString(), "-")} " +
                         $"small={Safe.Read(() => icon.IconDat?.SmallMinimapSize.ToString(), "-")}");
        }

        // What the marker is actually showing. A dig site marker is a stand-in for content that
        // does not exist yet, so the art it is displaying is the only thing that says which content
        // it stands for - and EffectZones already reads ground effects exactly this way.
        var animated = Safe.Read(() => entity.GetComponent<Animated>(), null);

        if (animated != null)
        {
            b.AppendLine($"    animated  base={Safe.Read(() => animated.BaseAnimatedObjectEntity?.Path, "-")} " +
                         $"id={Safe.Read(() => animated.MiscAnimated?.Id, "-")} " +
                         $"ao={Safe.Read(() => animated.MiscAnimated?.AOFile, "-")} " +
                         $"baseSize={Safe.Read(() => animated.MiscAnimated?.BaseSize.ToString(), "-")}");

            // The animated object in its own right. BaseSize on it is how EffectZones gets the
            // radius of a ground effect in world units, and for the placement indicator that is
            // the most likely home of the number the game draws its circle with.
            var baseEntity = Safe.Read(() => animated.BaseAnimatedObjectEntity, null);

            if (baseEntity != null)
            {
                b.AppendLine($"    animBase  bounds={Describe(Safe.Read(() => baseEntity.GetComponent<Render>()?.Bounds ?? default, default(System.Numerics.Vector3)))} " +
                             $"baseSize={Safe.Read(() => baseEntity.GetComponent<Animated>()?.MiscAnimated?.BaseSize.ToString(), "-")} " +
                             $"components=[{string.Join(",", Safe.Read(() => baseEntity.CacheComp, null)?.Keys.OrderBy(k => k) ?? Enumerable.Empty<string>())}]");

                var baseStats = Safe.Read(() => baseEntity.Stats, null);

                if (baseStats is { Count: > 0 })
                {
                    foreach (var (stat, value) in baseStats)
                        b.AppendLine($"      base stat {stat} = {value}");
                }
            }
        }

        // Every component the entity carries, by name. The cheapest way to find the one nobody
        // thought to ask for, which is what a marker that classifies itself would be.
        var components = Safe.Read(() => entity.CacheComp, null);

        if (components is { Count: > 0 })
            b.AppendLine($"    components {string.Join(", ", components.Keys.OrderBy(x => x))}");

        var attached = Safe.Read(() => entity.GetComponent<AttachedAnimatedObject>()?.Attachments, null);

        if (attached is { Count: > 0 })
        {
            b.AppendLine($"    attached  ({attached.Count}) " +
                         string.Join(", ", attached.Select(a => Safe.Read(() => a.ToString(), "?"))));
        }

        var render = Safe.Read(() => entity.GetComponent<Render>(), null);

        if (render != null)
            b.AppendLine($"    render    name={Safe.Read(() => render.Name, "")} bounds={Describe(Safe.Read(() => render.Bounds, default))}");

        // The whole stat dictionary, not only MonsterMinimapIcon. That one stat is how MinimapIcons
        // tells an Expedition chest's tier apart and is the likeliest classifier here too, but a
        // dump that only prints what was expected cannot correct the expectation.
        if (Safe.Read(() => entity.Metadata, "")?.Contains("Expedition2Encounter", StringComparison.Ordinal) == true)
            b.AppendLine($"    pricing   {Valuation?.Describe(entity) ?? "(no valuation)"}");

        var stats = Safe.Read(() => entity.Stats, null);

        if (stats is { Count: > 0 })
        {
            b.AppendLine($"    stats     ({stats.Count})");

            foreach (var (stat, value) in stats)
            {
                var note = stat == GameStat.MonsterMinimapIcon
                    ? $"   <- as MapIconsIndex: {Icon(value)}"
                    : "";

                b.AppendLine($"      {stat} = {value}{note}");
            }
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static bool Mentions(Entity entity, string word) =>
        Safe.Read(() => entity.Metadata, "")?.Contains(word, StringComparison.OrdinalIgnoreCase) == true ||
        Safe.Read(() => entity.Path, "")?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private static bool Near(Entity entity, int range) =>
        Safe.Read(() => entity.DistancePlayer, float.MaxValue) <= range;

    /// <summary>
    /// One line per modifier, with everything ItemMod exposes beside it.
    ///
    /// Printed raw and in full rather than filtered to what a caller expects to find, because the
    /// question this answers is which of these fields carries a number at all. Group and Level are
    /// here for the same reason: a count could be the roll's tier rather than its value, and a tier
    /// would show up as Level moving while Values stayed empty.
    ///
    /// Silent when the list is empty, which keeps it off every ordinary monster in the site.
    /// </summary>
    private static void Detailed(StringBuilder b, string label, List<ItemMod> mods)
    {
        if (mods is not { Count: > 0 })
            return;

        foreach (var mod in mods)
        {
            var values = Safe.Read(() => mod.Values, null);
            var range = Safe.Read(() => mod.ValuesMinMax, null);

            b.AppendLine($"    {label,-12}{Safe.Read(() => mod.RawName, "?")} " +
                         $"values=[{string.Join(", ", values ?? new List<int>())}] " +
                         $"minmax=[{string.Join(", ", (range ?? []).Select(r => $"{r.Min}-{r.Max}"))}] " +
                         $"level={Safe.Read(() => mod.Level.ToString(), "?")} " +
                         $"group={Safe.Read(() => mod.Group, "?")} " +
                         $"name={Safe.Read(() => mod.Name, "?")}");
        }
    }

    private static string States(Entity entity)
    {
        var states = Safe.Read(() => entity.GetComponent<StateMachine>()?.States, null);

        return states == null
            ? ""
            : string.Join(", ", states.Select(s => $"{Safe.Read(() => s.Name, "?")}={Safe.Read(() => s.Value, -1L)}"));
    }

    private static string Icon(int value) =>
        Enum.IsDefined(typeof(MapIconsIndex), value) ? ((MapIconsIndex)value).ToString() : $"unknown ({value})";

    private static string DescribeElement(ExileCore2.PoEMemory.Element element) =>
        element == null
            ? "null"
            : $"address {Safe.Read(() => element.Address, 0L):X}, " +
              $"visible {Safe.Read(() => element.IsVisible.ToString(), "?")}, " +
              $"rect {Safe.Read(() => element.GetClientRectCache.ToString(), "?")}";

    private static string Describe(GameOffsets2.Native.Vector2i v) => $"({v.X},{v.Y})";

    private static string Describe(System.Numerics.Vector2 v) => $"({v.X:0.#},{v.Y:0.#})";

    private static string Describe(System.Numerics.Vector3 v) => $"({v.X:0.#},{v.Y:0.#},{v.Z:0.#})";

    /// <summary>One cell's reading from a terrain grid, or "?" when the grid could not be read.</summary>
    /// <summary>
    /// One line per exploitable spot: which way to face, and how far past it to point.
    ///
    /// The bearing is measured from the chain head, so it is the direction to push the cursor in, and
    /// the band is how far beyond the spot the aims that worked were sitting. A spot reached from one
    /// aim says "this worked once"; a spot reached from a dozen spread over ten degrees says "point
    /// roughly there", which is the difference between a curiosity and a technique.
    /// </summary>
    private static System.Collections.Generic.List<string> Fan(
        System.Collections.Generic.List<(System.Numerics.Vector2 Cursor,
            System.Numerics.Vector2 Landing, System.Numerics.Vector2 From, int Yes, bool Ranged,
            float By)> clamps)
    {
        var spots = new System.Collections.Generic.Dictionary<(int, int, string),
            System.Collections.Generic.List<(float Bearing, float Past, int Yes)>>();
        var kept = new System.Collections.Generic.List<(int, int, string)>();
        var where = new System.Collections.Generic.Dictionary<(int, int, string),
            (System.Numerics.Vector2 Landing, System.Numerics.Vector2 From, float By)>();

        foreach (var (cursor, landing, from, yes, ranged, by) in clamps)
        {
            var key = ((int)landing.X, (int)landing.Y, ranged ? "range" : "refusal");

            if (!spots.TryGetValue(key, out var aims))
            {
                spots[key] = aims = new System.Collections.Generic.List<(float, float, int)>();
                kept.Add(key);
                where[key] = (landing, from, by);
            }

            var reach = System.Numerics.Vector2.Distance(from, landing);

            aims.Add((
                MathF.Atan2(cursor.Y - from.Y, cursor.X - from.X) * 180f / MathF.PI,
                System.Numerics.Vector2.Distance(from, cursor) - reach,
                yes));
        }

        var lines = new System.Collections.Generic.List<string>();

        foreach (var key in kept)
        {
            var aims = spots[key];
            var (landing, from, by) = where[key];

            var lowB = float.MaxValue;
            var highB = float.MinValue;
            var lowP = float.MaxValue;
            var highP = float.MinValue;

            foreach (var (bearing, past, _) in aims)
            {
                lowB = MathF.Min(lowB, bearing);
                highB = MathF.Max(highB, bearing);
                lowP = MathF.Min(lowP, past);
                highP = MathF.Max(highP, past);
            }

            lines.Add(
                $"spot ({landing.X:0},{landing.Y:0}) {key.Item3}" +
                (key.Item3 == "range" ? $" {by:0.0} grid gained" : "") +
                $" | {System.Numerics.Vector2.Distance(from, landing):0.0} from the head" +
                $" | aim {lowB:0}..{highB:0}deg, {lowP:0.0}..{highP:0.0} grid past it" +
                $" | {aims.Count} aim(s)");
        }

        return lines;
    }

    /// <summary>A computed aim, or "none" where the rule could not name one.</summary>
    private static string Aimed(System.Numerics.Vector2 at) =>
        at == System.Numerics.Vector2.Zero ? "none" : $"({at.X:0},{at.Y:0})";

    private static string Underfoot(Terrain terrain, System.Numerics.Vector2 at) =>
        terrain == null ? "?" : terrain.Room(at).ToString();

    /// <summary>
    /// The game's own coarse routing grid, laid out around the placement indicator.
    ///
    /// **The array this reads is the rule the whole investigation was after.** `0x141F5A6B0`
    /// pathfinds explosive placement on a half-tile grid and, at `0x141F5ADB2`, indexes exactly this
    /// byte array to decide whether the clamped endpoint sits in a block it may finish in. See
    /// Peek.Coarse for the layout and how it was found.
    ///
    /// Printed as `#` where the byte is zero - which is what the game refuses - and `.` where it is
    /// set, with the indicator's own block marked `@`.
    /// </summary>
    /// <summary>
    /// What the chain standing on the site is worth if the WHOLE of it had been solved for at once.
    ///
    /// **The one fact that separates a search fault from an objective fault**, and the step
    /// expedition_solve_plan.md asks for. Laying a few explosives by hand and letting the plugin
    /// finish produces a chain the full solve never found - measured on Craggy Peninsula, 21,210
    /// against 18,674, the whole of the gap in propagation. That is either a chain the objective
    /// ranks higher and the search cannot reach, or a chain the objective ranks lower and the
    /// player is right about something the weights are wrong about. Those want opposite work and
    /// nothing said which.
    ///
    /// So the links already down are put back in front of the planned ones and the lot is scored
    /// against an environment with every explosive still to place, from the detonator - which is
    /// the environment the full solve had. Beside it sits what the full solve actually returned.
    ///
    /// Plain, not total, because insistence is the same on both and would only pad them.
    /// </summary>
    private static string Whole(GameController gc)
    {
        if (Env == null)
            return "no environment";

        var placed = Safe.Read(() => Detonator.PlacedExplosiveGridPositions(gc), null);

        if (placed == null || placed.Length == 0)
            return "nothing laid by hand yet - lay a few and dump again, that is the comparison";

        // **Chain already carries the links that are down**, so prepending them counted the first
        // three twice - the readout said "18 links (3 down + 15 planned)" on a fifteen explosive
        // site. Duplicates cost the score almost nothing, because coverage is a set, which is
        // exactly why the number looked reasonable while being about the wrong chain.
        var chain = new List<System.Numerics.Vector2>(Chain ?? new List<System.Numerics.Vector2>());
        var already = chain.Count >= placed.Length;

        for (var i = 0; already && i < placed.Length; i++)
            already = (int)MathF.Round(chain[i].X) == placed[i].X &&
                      (int)MathF.Round(chain[i].Y) == placed[i].Y;

        if (!already)
        {
            for (var i = placed.Length - 1; i >= 0; i--)
                chain.Insert(0, new System.Numerics.Vector2(placed[i].X, placed[i].Y));
        }

        var links = Safe.Read(() => Detonator.ExplosiveCount(gc), chain.Count);
        var seat = Safe.Read(() => Detonator.DetonatorGridPosition(gc), Env.Origin);
        var whole = Env with { Origin = seat, Explosives = links, Placed = null };

        var rate = Planner.Rate(whole, chain);

        return $"{chain.Count} links ({placed.Length} down + {chain.Count - placed.Length} planned) " +
               $"from ({seat.X:0},{seat.Y:0}) over {links} explosives is worth " +
               $"{Planner.Plainly(whole, chain):N0} plain, holding {rate.Held} must-take(s)" +
               " - compare against what a full solve of this site returned: HIGHER means the objective agrees and the search cannot reach it, LOWER means the objective disagrees with the player";
    }

    private static string Coarse(GameController gc)
    {
        var slab = Peek.Coarse(gc);

        if (slab == null)
            return "  not readable - no dig site, or the offsets have moved";

        var b = new StringBuilder();

        b.AppendLine("  " + slab.Wider + " x " + slab.Higher + " coarse cells, origin tile " +
                     slab.FromX + "," + slab.FromY + ", " + slab.RoutableCells + " routable of " +
                     slab.Bytes.Length);

        var at = Safe.Read(gc, static g => g.IngameState.IngameUi.ExpeditionDetonatorElement?.Info
            ?.PlacementIndicatorGridPosition ?? default, default);

        var (cx, cy) = slab.Cell(at.X, at.Y);

        b.AppendLine("  indicator grid " + at.X + "," + at.Y + " is coarse cell " + cx + "," + cy);

        for (var y = cy - Wide; y <= cy + Wide; y++)
        {
            if (y < 0 || y >= slab.Higher)
                continue;

            var line = new StringBuilder();

            line.Append("  ");
            line.Append(y.ToString().PadLeft(5));
            line.Append(' ');

            for (var x = cx - Wide; x <= cx + Wide; x++)
            {
                if (x < 0 || x >= slab.Wider)
                {
                    line.Append(' ');
                    continue;
                }

                var one = slab.Bytes[y * slab.Wider + x];

                line.Append(x == cx && y == cy ? '@' : one == 0 ? '#' : '.');
            }

            b.AppendLine(line.ToString());
        }

        return b.ToString();
    }

    /// <summary>How many coarse cells either side of the indicator to write out.</summary>
    private const int Wide = 24;
}
