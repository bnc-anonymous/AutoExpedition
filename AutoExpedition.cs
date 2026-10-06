using System.IO;
using System;
using System.Runtime;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Nodes;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Numerics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Expedition, planned and placed.
///
/// The thing it exists to do better than what is already available: score every piece of content in
/// the dig site, not only the remnants. The plugin everyone uses for this scores remnants and
/// nothing else - no chests of any size, no runic monsters - so a chain that walks past a row of
/// chests to reach one more remnant looks optimal to it.
///
/// Nothing here is trusted until a real encounter has been dumped. No plugin in this HUD reads
/// Expedition chests or monsters today, so there is nothing to copy the metadata from and nothing
/// to check it against; the dump is the first step and the rest waits on it.
/// </summary>
public partial class AutoExpedition : BaseSettingsPlugin<AutoExpeditionSettings>
{
    private ExileInput2Client _input;

    /// <summary>The boundary sweep, which puts the placement model to the game. See Frontier.</summary>
    private Frontier _frontier;

    /// <summary>The last thing the sweep said, so each change is reported once. See Tick.</summary>
    private string _swept = "";
    /// <summary>The terrain snapshot the clamp diagnostic reads. See the Observe call.</summary>
    private Terrain _ground;

    /// <summary>The scenery snapshot the placement diagnostic routes against. See the Observe call.</summary>
    private Obstacles _blocking;

    private Scan _scan;
    private readonly Correlate _correlate = new();
    private readonly Snap _snap = new();
    private readonly Cleared _cleared = new();

    /// <summary>The batch of cold presses, when one is being measured. See RepeatedPresses.</summary>
    private readonly RepeatedPresses _repeats = new();

    private readonly Boundary _boundary = new();

    /// <summary>Which of a Grand site has been walked near enough to load. See Scouted.</summary>
    private readonly Scouted _scouted = new();

    /// <summary>How far the game loads and drops things, which is what Scouted rests on.</summary>
    private readonly Streaming _streaming = new();
    private readonly Planning _planning = new();
    private readonly Blast _blast = new();
    private readonly Scoring _scoring = new();

    /// <summary>Solves the site before the first press asks for it. See Rehearsal.</summary>
    private readonly Rehearsal _rehearsal = new();

    /// <summary>Whether the game's placement tool was on offer last tick, so its arrival can be noticed. See Tick.</summary>
    private bool _toolWasShowing;

    /// <summary>How many explosives were down last frame, so a landing can be noticed.</summary>
    private int _wereDown;

    /// <summary>
    /// Where they were, so an undo can say which ground it has just freed.
    ///
    /// The count says one has come off and not which, and the game's list has already dropped it by
    /// the time anything asks.
    /// </summary>
    private Vector2[] _werePlaced = [];

    private int _wereOff;

    /// <summary>Whether a run is waiting on a new plan before carrying on. See Placement.Resume.</summary>
    private readonly Census _census = new();
    private readonly Spawns _spawns = new();

    /// <summary>The live chain last checked against a batch's target, so each is scored once. See RepeatStopAtScore.</summary>
    private List<System.Numerics.Vector2> _repeatChecked;

    /// <summary>A cold solve asked for and waiting on the sweep to find the site again.</summary>
    private bool _coldWanted;

    /// <summary>
    /// A solve the action key asked for that is waiting on the ground being flooded.
    ///
    /// **The flood was coming out of the solving window, and the window is the whole budget.** F3
    /// has always learnt the site first and then solved; the action key went straight to Start, so
    /// on a router holding nothing every worker flooded as it searched - openings of five to seven
    /// seconds against about three warm, and two of eight threads finishing on 219 and 293 rounds,
    /// which is not a search. The time was spent either way. The difference is whether it is spent
    /// inside the eight seconds the solver was given.
    ///
    /// In play the preflood covers this on the walk in and the press finds the site already learnt.
    /// This is for the case the walk did not cover - a reset, a first press on arrival - where the
    /// honest order is flood, then solve, rather than both at once.
    /// </summary>
    private bool _solveWanted;

    /// <summary>What to tell Planning the deferred solve was asked for by. See _solveWanted.</summary>
    private string _solveCause = "the action key";

    /// <summary>When the deferred press arrived, so the wait can be bounded. See _solveWanted.</summary>
    private DateTime _solveAskedAt = DateTime.MinValue;

    /// <summary>
    /// The longest a press waits for the ground before solving cold anyway.
    ///
    /// Generous against the two and a half seconds a cold site actually takes, because the cost of
    /// being wrong in one direction is a key that feels slow and in the other a key that feels
    /// broken.
    /// </summary>
    private static readonly TimeSpan Waiting = TimeSpan.FromSeconds(15);

    /// <summary>Every strategy on this site, one after another. See Bakeoff.</summary>
    private readonly Bakeoff _bakeoff = new();





    /// <summary>
    /// How many preflood passes one site may have.
    ///
    /// Each is cheap after the first - only cells nobody has flooded cost anything - but each also
    /// rebuilds the environment and the candidate set to find out what is new, and a site that keeps
    /// trickling markers should not spend the whole walk doing that.
    /// </summary>
    /// <summary>
    /// How often the preflood may run again while a site is revealing itself.
    ///
    /// The flooding is not what this rations - a warm pass is fifteen cells in thirty seven
    /// milliseconds, and it skips every cell already held. What it rations is the environment built
    /// in order to ask which candidates there are, which is the expensive half of a pass.
    /// </summary>
    private static readonly TimeSpan Again = TimeSpan.FromMilliseconds(1000);





    private DateTime _coldAt;
    private int _coldSaw;
    private int _coldSteady;

    private Placement _placement;
    private Valuation _valuation;

    /// <summary>What was last said about the prices, so a state is announced once. See Told.</summary>
    private string _priceless;

    /// <summary>
    /// Says out loud when the reward prices cannot be trusted, and again when they come back.
    ///
    /// **Silence is the dangerous state here.** NinjaPricer answering with nothing leaves every
    /// recipe at zero, so the solver ranks rewards arbitrarily and every readout carries on looking
    /// exactly as it does when the prices are good. Caught in game with four of three hundred and
    /// twenty two recipes priced, and nothing anywhere said so.
    ///
    /// On the transition only. The condition is true on every frame it holds, and a log line a frame
    /// is not a warning, it is a wall - the player stops reading it and the next real one is lost in
    /// it. The recovery is announced for the same reason: a warning that never retracts teaches
    /// people to ignore warnings.
    /// </summary>
    /// <summary>What has already been said about a bogus banner, so it is said once. See Banners.</summary>
    private int _warned;

    /// <summary>When the search region was last worked out. See Scouted.Wanted.</summary>
    private DateTime _vouched = DateTime.MinValue;

    /// <summary>
    /// Says so when the game claims the expedition is over while a whole site is untouched.
    ///
    /// **This is the warning for a bug that cannot be undone.** The banner fires on the last
    /// monster's death and should fire once per expedition; seen firing twice at one dig site, once
    /// on an ice-encased unique and once legitimately - and the map's OTHER site was then unable to
    /// accept an explosive at all. The ground drew as placeable, the circle came up, the indicator
    /// followed the cursor, and every click did nothing. Rejoining the instance did not clear it.
    ///
    /// By the time the banner is up the damage is already done server-side, so there is nothing to
    /// prevent. What there is to do is tell the player before they walk across the map to a site that
    /// is already lost - and to say which, since the detonator panel names every expedition in the
    /// map and how much of each has been placed. See Detonator.Sites and Finished.Seen.
    ///
    /// On the count, not on the banner being up: it stays up for seconds and a line a frame is a
    /// wall rather than a warning.
    /// </summary>
    private void Banners()
    {
        // **The expensive half only runs when the warning could fire.**
        //
        // The whole of this is a warning that a completion banner appeared while detonators are
        // still untouched, so with none untouched there is nothing it could say. Watch's fallback
        // search is a depth-six walk of everything visible and measured 20ms, which is a visible
        // hitch every time the backoff lets one through - paid, until now, on every site in every
        // map whether or not the condition it warns about could hold.
        //
        // Following an already-learned path stays cheap and unconditional, so a banner seen while
        // the path works is still counted.
        if (!Settings.Recording.WatchFinished)
            return;

        var idle = Safe.Read(() => Detonator.ExpeditionsNeverStarted(GameController), 0);

        Finished.Watch(GameController, idle > 0);

        if (Finished.Seen <= _warned)
            return;

        _warned = Finished.Seen;

        if (idle <= 0)
            return;

        var sites = Safe.Read(() => Detonator.Sites(GameController), null) ?? [];

        // **Stated as an observation, not as a diagnosis.** It used to say this IS a known game bug
        // and that the other site was unusable, which is a strong claim from two counts that can
        // disagree for ordinary reasons - a site walked past and never approached, a banner for an
        // encounter whose markers had not streamed in. Reported as a mismatch worth checking, which
        // is what the numbers actually support.
        DebugWindow.LogError(
            $"[AutoExpedition] Warning, detected {Finished.Seen} \"Expedition Complete\" " +
            $"banner{(Finished.Seen == 1 ? "" : "s")} but only {sites.Count - idle} of " +
            $"{sites.Count} expedition site{(sites.Count == 1 ? "" : "s")} in this map " +
            "have been placed in. You might not be able to place bombs at your next expedition " +
            "site due to an in-game bug. Sites: " +
            // The high-water count, not the live one, which goes back to nought on detonation and
            // would print a finished expedition as 0/5. See Detonator.ExplosivesEverPlacedHere.
            string.Join(", ", sites.Select(static x =>
                $"({x.At.X:0},{x.At.Y:0}) {Detonator.ExplosivesEverPlacedHere(x.At)}/{x.Total}")), 20f);
    }

    /// <summary>What has already been said about a broken read, so it is said once. See Offsets.</summary>
    private bool _broke;

    /// <summary>
    /// Says so, loudly, when the routing grid cannot be believed - and the solve then refuses.
    ///
    /// **The failure this replaces was silent.** Terrain.Trusted rejected the grid, the planner fell
    /// back to straight lines, and the plugin went on drawing greens and publishing chains built
    /// from a stand-in for the placement rule rather than from the rule. Worse chains, no symptom,
    /// and nothing to notice until someone compared scores across a patch.
    ///
    /// Refusing is Planning.Start's job; this is the half the player sees. Once per area, because
    /// the condition is a fact about the read and not an event, and a line a frame is a wall.
    /// </summary>
    private void Offsets()
    {
        if (Terrain.Broken == null || _broke)
            return;

        _broke = true;

        DebugWindow.LogError(
            "[AutoExpedition] Offsets are broken - refusing to solve. " + Terrain.Broken +
            ". The plugin reads the client's memory directly and a game patch moves what it reads; " +
            "the repair procedure is in Offsets.cs. Nothing will be planned or placed until it is " +
            "fixed, deliberately: planning from a stand-in for the game's own rule looks identical " +
            "on screen and is quietly worse.", 30f);
    }

    private void Told()
    {
        var doubt = Safe.Read(() => _valuation?.Unpriced(), null);

        if (doubt == _priceless)
            return;

        if (doubt is { Length: > 0 })
        {
            // The counts go here rather than on the remnant: this is the line with room to read
            // them, and they are what tells a slow fetch apart from a dead one. See Priceless.
            DebugWindow.LogError(
                $"[AutoExpedition] {doubt} - {Safe.Read(() => _valuation?.Priceless(), "?")}. " +
                "Rewards are unranked and the plan means nothing until they arrive.", 10f);
        }
        else if (_priceless is { Length: > 0 })
            DebugWindow.LogMsg("[AutoExpedition] reward prices have arrived", 5f);

        _priceless = doubt;
    }
    private string _status = "";

    public override bool Initialise()
    {
        // Whether the game is in front, polled off the frames so a long gap can say it was spent tabbed out. See
        // Spent.WatchFocus.
        Spent.WatchFocus(() => GameController.Window.IsForeground());

        // **The only collection setting a plugin can reach.**
        //
        // Whether the runtime collects in workstation or server mode is fixed when the process
        // starts, long before any plugin loads, so it cannot be set from here - and this host
        // rewrites its own runtimeconfig on launch, so a user editing that file does not keep it
        // either. The environment variable is the only durable route and is not a reasonable thing
        // to ask of somebody installing a plugin.
        //
        // Latency mode is settable at runtime. SustainedLowLatency stops the runtime doing BLOCKING
        // gen2 collections, which is the one kind that would explain a single four hundred
        // millisecond gap between frames; it does nothing for the gen0 and gen1 collections that
        // are the bulk of the count, so it is a partial measure and the real answer is to allocate
        // less. Set here and left, because it is a property of how this plugin wants to behave
        // rather than of any one moment.
        //
        // Reversible and process-wide: it affects every plugin in the host, which is the same thing
        // the collections it is trying to avoid already do.
        Safe.Do(() => GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency);

        // Before anything reads a weight: a saved file can hold a number that answered a different
        // question, and the plan would quietly be built on it. See Migrated.
        // The table has to be up before the migration can ask it what a weight now ships as.
        Wrt.Home = ConfigDirectory;
        Wrt.Source = DirectoryFullName;

        // Beside the dumps, because a snapshot is read by the same people reading those and an offline run
        // wants both from one folder. See Layout.
        Layout.Folder = Path.Combine(ConfigDirectory, "dumps", "layouts");
        Wrt.Load();

        // Where the weights used to live. Named rather than derived inside Migrated so the one
        // place that knows the host's layout is the plugin, which is the only thing that does.
        var saved = Path.Combine(ConfigDirectory, "..", "global", "AutoExpedition_settings.json");

        var settled = Migrated.Apply(Settings, saved);

        if (settled.Length > 0)
            LogMessage($"AutoExpedition: updated saved settings - {settled}", 10f);

        // **Again, because the migration can put a number in front of the table after it has read
        // it.** Migrated.Apply writes a tuned slider out as a setting: row, and Wrt.Load is what moves
        // a count off the weight column and onto the child edge it now belongs on - so on the one load
        // where both happen, the second half has already run. Loading again is idempotent: the count
        // migration skips a row whose children somebody has edited, and the thinning has nothing left
        // to take. See Wrt.Counted.
        if (settled.Length > 0)
            Wrt.Load();

        _input = new ExileInput2Client(GameController, "AutoExpedition");
        _scan = new Scan(GameController) { Home = ConfigDirectory };
        _scouted.Home = ConfigDirectory;
        Kept.Home = ConfigDirectory;

        // A reload part way through a map does not see an area change, so the area in hand is read now. See
        // Placement.LoadSetByPlacement.
        Placement.LoadSetByPlacement(Safe.Read(() => GameController.Area.CurrentArea.Hash, 0u));
        Unknowns.Home = ConfigDirectory;
        Unknowns.Load();
        Marks.Home = ConfigDirectory;
        Marks.Load();
        Wrt.Home = ConfigDirectory;

        // The shipped table travels with the plugin; yours stays in your config. See Wrt.Source.
        Wrt.Source = DirectoryFullName;
        Wrt.Load();

        // The modifiers this character refuses, and the list of what it has been offered.
        MustAvoidMods.Home = ConfigDirectory;
        MustAvoidMods.Load();
        _placement = new Placement(_input);
        _frontier = new Frontier(_input);
        Frontier.Where = ConfigDirectory;
        _valuation = new Valuation(GameController, Settings);
        Dump.Valuation = _valuation;
        Dump.Scan = _scan;
        Dump.Reading = _blast;
        Dump.Settings = Settings;

        // **One node behind two menu rows, rather than two nodes kept in step.** Overruling is
        // shown under Rewards and again under Display, Remnants, Rewards, because it decides what
        // is drawn there as well as what the plugin does. Pointing the second property at the first
        // makes the two rows edit the same object, so neither can be stale; the mirror is
        // JsonIgnore, so nothing was read into it to lose. See RemnantRewardSettings.Overrule.
        if (Settings?.Rewards?.Overrule != null && Settings.Display?.Remnants?.Rewards != null)
            Settings.Display.Remnants.Rewards.Overrule = Settings.Rewards.Overrule;
        Dump.Spawns = _spawns;
        Dump.Scoring = _scoring;
        Dump.Placement = _placement;
        Dump.Repeats = _repeats;
        Dump.Rehearsal = _rehearsal;
        Dump.Streaming = _streaming;

        // ListNode choices are not serialised, so they have to be supplied on every start or the
        // dropdown comes up empty with a saved value it cannot show.
        Settings.Display.Prices.PriceIn.SetListValues(new List<string> { "Exalted", "Divine", "Chaos" });
        // One search to choose.
        Settings.Solver.Advanced.Strategy.SetListValues(new List<string> { SolverSettings.DestroyRepair });

        // The checkbox is left as saved rather than set from the old choice: it ships on, and a player who
        // unticked it while the exploit strategy was selected was asking for exploits off.
        if (Settings.Solver.Advanced.Strategy.Value != SolverSettings.DestroyRepair)
            Settings.Solver.Advanced.Strategy.Value = SolverSettings.DestroyRepair;
        Settings.Display.PlacementCircle.Unreachable.SetListValues(PlacementCircleSettings.WhenUnreachable.ToList());
        Settings.Solver.Reroll.Mode.SetListValues(RerollSettings.Modes.ToList());
        Settings.Automation.PostExpedition.LineFrom.SetListValues(
            PostExpeditionSettings.WhenToLine.ToList());

        Register(Settings.ActionHotkey);
        Register(Settings.TableHotkey);
        Register(Settings.Debug.ColdHotkey);
        Register(Settings.Debug.RepeatHotkey);
        Register(Settings.Debug.BrowseHotkey);
        Register(Settings.Debug.DumpHotkey);
        Register(Settings.Debug.FrontierHotkey);
        Register(Settings.Debug.CorrelateHotkey);
        Register(Settings.Debug.ScoreHotkey);
        Register(Settings.InsistHotkey);

        return true;
    }

    /// <summary>
    /// Marks or unmarks the marker under the cursor as one the chain must take.
    ///
    /// Says which marker and which way it went, because the ring appears on a thing that may be off
    /// the edge of the screen and a key that silently does nothing is indistinguishable from a key
    /// that missed. It does NOT re-solve on its own: the plan on screen is still the answer to the
    /// old question, and replacing it without being asked would throw away a chain that may be
    /// halfway placed.
    /// </summary>
    /// <summary>When the must-take key was last pressed, or MinValue once acted on. See MarksSettle.</summary>
    private DateTime _marksChangedAt = DateTime.MinValue;

    /// <summary>
    /// How long the must-take marks must stay as they are before the site is solved again with them. The key cycles must
    /// take, must avoid and nothing, so going from must take to nothing passes through must avoid, and solving on each
    /// press would plan around a mark nobody wanted. 1000 ms, chosen so that cycling a mark with several taps of the key
    /// lands on the one wanted before a solve starts; not measured.
    ///
    /// **A solve, whatever was running.** The plan and the line under each remnant are worked out against the marks a
    /// solve started with, and they waited for the solve in hand to end - a press's whole window, or a continuous pass's
    /// - before showing a mark. Starting a solve supersedes the one running and keeps its best chain, and each worker
    /// carries its own chain over, so little is lost. See WorkerChains.
    /// </summary>
    private static readonly TimeSpan MarksSettle = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Solves the site again once the must-take marks have stood for MarksSettle. Not once an explosive is down, since a
    /// solve then interrupts placing (todo section 0), and it does not resume continuous solving the action key stopped.
    /// </summary>
    private void SolveAfterMarks(int down)
    {
        if (_marksChangedAt == DateTime.MinValue || DateTime.UtcNow - _marksChangedAt < MarksSettle)
            return;

        _marksChangedAt = DateTime.MinValue;

        if (down > 0 || _placement.Busy || Detonator.ExplosivesInHand(GameController) <= 0 ||
            Detonator.DetonatorGridPosition(GameController) == Vector2.Zero)
            return;

        _planning.Start(GameController, Settings, _scan, _blast, _valuation, ZoneCancellationToken,
            cause: Planning.MustTakeChangedCause);
    }

    private void Insist(Target target)
    {
        // No message either way: the mark shows as rings in the world and on the minimap, and a line in the HUD's log
        // corner for every press read as debugging output.
        if (target == null)
            return;

        Insisted.Here.Cycle(target.Grid);

        // A solve with the new marks once they stop changing. See MarksSettle.
        _marksChangedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Makes the marker under the key the one the chain takes last, or clears that. See Insisted.ToggleTakenLast.
    /// </summary>
    private void ToggleTakenLast(Target target)
    {
        if (target == null)
            return;

        Insisted.Here.ToggleTakenLast(target.Grid);
        _marksChangedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A bar centred under the cursor while the must take key is held on a marker, full when the key goes down and empty
    /// when the hold marks it taken last, in the must take green. At the cursor warnings' height. See DebugSettings.TakeLastHoldMs.
    /// </summary>
    private void DrawTakeLastHold()
    {
        if (_insistDownAt == DateTime.MinValue || _insistHeld || _insistTarget == null)
            return;

        var holdMs = Math.Max(1, Settings.Debug.TakeLastHoldMs.Value);
        var left = 1f - (float)Math.Clamp((DateTime.UtcNow - _insistDownAt).TotalMilliseconds / holdMs, 0d, 1d);
        var cursor = Safe.Read(() => new Vector2(GameController.IngameState.MousePosX, GameController.IngameState.MousePosY),
            Vector2.Zero);

        if (cursor == Vector2.Zero)
            return;

        // Centred under the pointer, at the cursor warnings' height.
        const float wide = 80f;
        const float high = 6f;
        var at = new Vector2(cursor.X - wide / 2f, cursor.Y + 44f);

        Graphics.DrawBox(new ExileCore2.Shared.RectangleF(at.X - 1f, at.Y - 1f, wide + 2f, high + 2f), Color.Black);
        Graphics.DrawBox(new ExileCore2.Shared.RectangleF(at.X, at.Y, wide * left, high),
            Color.FromArgb(230, (Color)Settings.Display.ThePlan.StepColour));
    }

    /// <summary>When the must take key went down, or MinValue while it is up. See DebugSettings.TakeLastHoldMs.</summary>
    private DateTime _insistDownAt = DateTime.MinValue;

    /// <summary>The marker under the cursor when the key went down.</summary>
    private Target _insistTarget;

    /// <summary>Whether this press has already been taken as a hold.</summary>
    private bool _insistHeld;

    /// <summary>
    /// A hotkey the game never hears about is a hotkey that silently never fires: Input only tracks
    /// keys handed to RegisterKey, and rebinding one in the menu makes a value it has not been told
    /// about. Both halves are needed and only the second is easy to forget.
    /// </summary>
    private static void Register(HotkeyNodeV2 hotkey)
    {
        Bind(hotkey);

        // Rebinding in the menu makes a value Input has never been told about, so it has to be
        // registered again - and a key left unbound must not be, since registering None would
        // claim a key nobody pressed.
        hotkey.OnValueChanged += () => Bind(hotkey);
    }

    private static void Bind(HotkeyNodeV2 hotkey)
    {
        if (Safe.Read(() => hotkey.Value.Key, Keys.None) != Keys.None)
            Input.RegisterKey(hotkey.Value);
    }

    /// <summary>
    /// Puts every rune the game knows about into the per-rune weight list, once.
    ///
    /// Not in Initialise: the file tables are not loaded until a character is in a world, so asking
    /// there gets nothing. It runs from Tick until it succeeds, and only adds runes the list does
    /// not already mention - so anything edited stays edited, and a rune added by a patch turns up
    /// on its own.
    /// </summary>

    public override void Tick()
    {
        // **The other entry point, and the one that was never measured.** The host lists this
        // plugin twice and every stage timed until now was inside Render, so a table that
        // accounted for its own frame could still not account for the host's figure. See Spent.
        using var timing = Spent.On("Tick");

        // **The head and the tail, bracketed rather than picked apart.** Between them they are the
        // 0.31ms of Tick that no stage accounted for, and neither is one thing: the head clears
        // caches and answers the settings window's buttons, the tail polls eight hotkeys and
        // decides whether to solve. A stage each says which half to look in, which is cheaper than
        // putting a stopwatch round twenty statements to find out it is one of them.
        var head = Spent.On("Tick/Head");

        // Everything cached for a single frame expires here, before anything reads the game.
        Frame.Next();

        // Carried rather than looked up, for the reason Panels.Keeping gives.
        Panels.Keeping = Safe.Read(() => Settings.Display.PlacementCircle.PanelCacheMs.Value, 100);

        // Read once here so the static helpers that judge whether an explosive is a planned link
        // agree for the whole frame, and follow the slider when it moves. See Astray.Owned.
        Astray.Owns(Settings);

        // A combinations window that has gone takes its identity with it. One flag read, and the
        // only thing here that has to happen while no window is open. See Options.Closed.
        using (Spent.On("Tick/Options.Closed"))
            Options.Closed(GameController);

        // The map's rarity and pack modifiers shape remnant waves before anything prices a remnant. A changed map bumps
        // the table's revision once; an unchanged one costs a few stat reads. See Weighing.MapForRemnantWaves.
        using (Spent.On("Tick/MapForRemnantWaves"))
            TableGrammar.SetMapForRemnantWaves(Safe.Read(() => Weighing.MapForRemnantWaves(GameController), default));




        // Built here rather than from the settings delegate, which has no GameController.
        using (Spent.On("Tick/RuneInfo.Build"))
            RuneInfo.Build(GameController);

        // Swept every tick rather than on demand, so walking past a marker is enough to remember
        // it. A tally that only knows what is on screen is empty exactly when it is wanted.
        // Asked for from the settings window, acted on here where everything it clears is in scope.
        if (Caches.Wanted)
        {
            Caches.Wanted = false;

            // The deliberate reset, so the saved ground facts go with it. The measurement clears
            // below keep theirs, because a comparison run wants the site it has learnt about.
            Caches.Clear(_scan, _blast, _planning, _boundary, _snap, _cleared, _scouted,
                area: Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u));

            // **What the preflood REMEMBERS goes too, or it believes it has already been here.**
            //
            // These four live on the plugin rather than in the router, so clearing the caches left
            // them saying "this site was flooded at this position with this many markers a moment
            // ago" - and the detonator has not moved, so the new-site test stayed false and the
            // ground was never re-learnt. The clear looked like it had worked, right up until the
            // first solve routed across a site it knew nothing about.

            // **And the presolve, which would otherwise never notice the clear happened.**
            //
            // It re-opens its question by OBSERVING the site change - the marker count moving, a
            // reward becoming readable - and it is not ticked at all while the ground is being
            // flooded. A clear empties the scan and the scan refills within a second, so the whole
            // 101 to nought to 101 excursion happens while the presolve is suspended behind the
            // preflood it now waits for. It wakes with the same count it went to sleep with,
            // nothing looks different, and it is still holding the "done" it reached before the
            // clear - so the readout says settled about a site whose every fact has just been
            // thrown away.
            //
            // Told rather than left to infer, because the one thing it cannot see is the moment it
            // was not watching.
            _rehearsal.AreaChange(unchecked((uint)DateTime.UtcNow.Ticks));
        }


        // The same, minus the site itself. See Caches.WantedKeepingScan.
        // **The narrowest button, handled first because it is the one that must not do anything else.**
        // See Caches.WantedPlanOnly.
        if (Caches.WantedPlanOnly)
        {
            Caches.WantedPlanOnly = false;

            // Deleting the plan is not asking for a new one: nothing searches until the action key does. See
            // Rehearsal.StopContinuing.
            _rehearsal.StopContinuing("deleting the plan");

            Caches.ForgetPlan(Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u), _planning);
        }

        if (Caches.WantedKeepingScan)
        {
            Caches.WantedKeepingScan = false;

            // As the plan-only button: nothing searches until the action key does.
            _rehearsal.StopContinuing("deleting the plan and ground");

            // The area IS passed, so the filed best chain goes - without that the next solve loads
            // it back as its floor and nothing has been forgotten. The ground facts are kept because
            // the scan is: they live in the same site files, and only a full clear deletes those.
            //
            // **This button means "cold start", so what the solver derived goes and what the site cost
            // to learn stays.** The floods are the terrain, derived from the very ground facts kept on
            // the line above, and the terrain does not move - so they are kept too, and only the plan,
            // the aims and the readings are dropped.
            //
            // It was not free. Measured on the first press after one: 2,588ms of preflood before a
            // worker started, openings of 5.7 to 7.3 seconds against about 3 warm, and 168,870 of
            // 219,977 routing questions refused once the budget ran out - every refusal read as a
            // wall. Two of the eight threads got 219 and 293 rounds and finished around 1,150,
            // which is no search at all, so a reset cost two workers outright and told the other
            // six a worse story about the ground.
            Caches.Clear(_scan, _blast, _planning, _boundary, _snap, _cleared,
                keepScan: true,
                area: Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u));

            // And nothing solves again until the player asks, so the next press is the first solve. The full clear
            // does not hold: it stands for arriving fresh, where the presolve is meant to run. See
            // Planning.HeldAfterColdStart.
            Planning.HeldAfterColdStart = true;
        }

        // The edge points button, answered where the site is in scope. See Planner.EdgePointsPending.
        if (Planner.EdgePointsPending)
        {
            Planner.EdgePointsPending = false;

            // The same environment the planner would solve now, thrown from where the next explosive
            // goes, so the rings answer "where should the next one go" rather than "what was this
            // site worth before anyone touched it".
            Planner.ComputeEdgePointsForDrawing(
                Planning.Build(GameController, Settings, _scan, _blast, _valuation, true, true, out _, out _));
        }

        // Where the player has been, and what the scan has met, for the scouting layer.
        //
        // **Kept up whether or not it is drawn**, which the display switch used to decide. The layer
        // is no longer only a picture: "Partial presolve" is read from it, so a plugin with the
        // drawing turned off would have claimed a whole answer on a site it had seen a corner of.
        // The cost is a bitmask and one pass over the markers a second.
        //
        // **And on every kind of site**, where it was Grand only. An ordinary expedition loads in
        // one piece, so walking is not the question there - but whether the scan has MET the whole
        // site still is, and that is answered round the markers rather than round the player. See
        // Scouted.Mark.
        {
            // Built here as well as on an area change, because a reload does not fire one and the
            // layer would otherwise stay blank until the next zone. Returns at once once it has its
            // ground. See Scouted.AreaChange.
            _scouted.AreaChange(Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u),
                GameController);

            _scouted.Mark(GameController,
                Safe.Read(GameController, static g => g.Player.GridPos, Vector2.Zero),
                Settings.Debug.ScoutReach.Value);

            // How much ground there is to search, which the markers decide - a radius around each
            // one, since content clusters and a marker with nothing beside it is the site saying
            // there is nothing beside it. The whole map on a Grand site, where the markers cannot
            // delimit ground nobody has been near enough to find a marker in. See Scouted.Wanted.
            //
            // On its own cadence rather than the frame's: it walks every marker the scan holds.
            if (DateTime.UtcNow - _vouched > TimeSpan.FromMilliseconds(500))
            {
                _vouched = DateTime.UtcNow;

                var grand = Detonator.Grand(GameController);
                var markers = _scan?.Standing(Detonator.DetonatorGridPosition(GameController)) ?? [];
                List<Vector2> elsewhere = null;

                // **And the sites nobody has walked to, when asked for.** Remnants and detonators
                // carry from right across the map, so each expedition can be given its patch of red
                // from the moment the area loads - a map-scale answer to "where is there still
                // something I have not been near", against the site-scale one above.
                //
                // Handed over separately because it is drawn and never counted: a second expedition
                // across the map would otherwise leave the readout reading partial for ever. See
                // Display.UnscoutedGround.ShowUnscoutedFar and Scouted.Wanted.
                if (!grand && Settings.Display.UnscoutedGround.ShowUnscoutedFar)
                {
                    elsewhere = _scan?.Landmarks() ?? [];

                    foreach (var one in Safe.Read(() => Detonator.Sites(GameController), null) ?? [])
                        elsewhere.Add(one.At);
                }

                // **What counts as this site is what a chain could walk through.**
                //
                // Markers are filed by nearest detonator and nearest has no limit, so a remnant
                // standing on its own is adopted by whichever site is least far away. One such,
                // 570 grid out, spread its scouting disc over ground nobody had been near: thirty
                // six tiles stayed unscouted and the readout said "Partial presolve" for ever about
                // a site with nothing left to see.
                //
                // A chain travels in hops of one reach, so the region is grown the same way rather
                // than cut at a radius somebody chose. It expands on its own as markers arrive,
                // which is the behaviour this layer already wanted - a marker at the edge of what
                // has streamed in is the site pointing at more of itself. See Scan.Joined.
                if (!grand)
                {
                    var site = Detonator.DetonatorGridPosition(GameController);
                    var reach = MathF.Max(1f, Detonator.PlacementRange(GameController));
                    var joined = _scan?.Joined(site, reach);

                    if (joined is { Count: > 0 })
                        markers = joined;
                }

                _scouted.Wanted(GameController, markers, elsewhere,
                    Settings.Display.UnscoutedGround.MarkerRadius.Value, grand,
                    Detonator.DetonatorGridPosition(GameController));
            }
        }

        head.Dispose();

        using (Spent.On("Tick/Scan.Tick"))
            _scan.Tick(Settings.Debug.SweepMs.Value);
        // The words on a strongbox, which is the only place its pack count is written. See Guards.
        using (Spent.On("Tick/Scan.Guards"))
            _scan.Guards(Settings.Debug.SweepMs.Value);

        using (Spent.On("Tick/Scan.Price"))
            _scan.Price(_valuation, Settings.Debug.SweepMs.Value);

        // Which remnants belong to no dig site, from what a picked recipe does to them. See Scan.SettleLoneRemnants.
        using (Spent.On("Tick/Scan.Lone"))
            _scan.SettleLoneRemnants(entity => _valuation?.ChosenName(entity) ?? "");

        using (Spent.On("Tick/Told"))
            Told();
        using (Spent.On("Tick/Offsets"))
            Offsets();

        using (Spent.On("Tick/Banners"))
            Banners();

        // Whether the fight is probably finished, which is what the loot line waits for. Asked here
        // rather than where it is drawn, so the dump can report it on a frame that draws nothing.
        // See Ending.
        using (Spent.On("Tick/Ending.Watch"))
            Ending.Watch(GameController, _scan, _planning.Plan);

        // Anything the plugin cannot name, noticed while walking rather than at the key press.
        // See Weighing.Catalogue.
        using (Spent.On("Tick/Scan.Learn"))
            _scan.Learn(Settings, Settings.Debug.SweepMs.Value);

        // **The best route this site ever gave up, read back without waiting for a key press.**
        //
        // Kept.Load was reached from two places and both of them needed something to have happened
        // first: a solve, or a score that already had a chain to score. After a plugin reload
        // neither is true - there is no plan, so there is no score, so the fallback that would have
        // shown the filed chain's worth could not see it either, because loading it was downstream
        // of the thing it was meant to supply.
        //
        // Read here instead, on the site rather than on an event. It returns immediately when the
        // area and the site have not changed, which is every frame but the first at a dig site.
        using (Spent.On("Tick/Kept.Load"))
            Kept.Load(Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u),
                Detonator.DetonatorGridPosition(GameController),
                Safe.Read(() => GameController.Area.CurrentArea.Name, "") ?? "",
                Safe.Read(() => GameController.IngameState.Data.AreaDimensions, Vector2.Zero));

        // The same, for anything the game says a blast acts on that we cannot name. See Unknowns.
        using (Spent.On("Tick/Unknowns.Keep"))
            Unknowns.Keep();
        using (Spent.On("Tick/Marks.Keep"))
            Marks.Keep();
        using (Spent.On("Tick/Wrt.Keep"))
            Wrt.Keep();

        // And every relic modifier on offer, so Must avoid mods has something to tick. Read
        // on the frame rather than in the weighing, which runs off it. See MustAvoidMods.Notice.
        foreach (var target in _scan.Targets)
        {
            MustAvoidMods.Notice(target);

            // A rune that has never been seen placed. Stops looking the moment it finds one, so on
            // every ordinary site this is one string comparison per remnant. See Curio.
            if (Settings.Debug.WatchBait)
                Curio.Notice(target);
        }

        using (Spent.On("Tick/Curio.Announce"))
            Curio.Announce(Settings.Debug.WatchBait);

        // A remnant rich enough to build the chain around gets marked must take, once, in the same
        // store the key writes to - and is then left alone whatever you do with it. Here rather
        // than in the solve because it is a fact about the site, not about a plan, and marking it
        // during a search would have the answer change under the thing reading it. See
        // Insisted.Automatic.
        // The game's own bug first, for the one marker where an avoid is about the rest of the map
        // rather than about this site. Before the threshold because both offer through the same set
        // and the first one there wins, and a boss over the must-take line would otherwise be marked
        // take and the workaround never get a say. See Insisted.Bugged.
        using (Spent.On("Tick/Insisted.Bugged"))
            Insisted.Here.Bugged(GameController, _scan, Settings);

        using (Spent.On("Tick/Insisted.Automatic"))
            Insisted.Here.Automatic(_scan, Settings, _valuation);

        // And forgets the ones there is nothing left to decide about, before anything reads them.
        // A mark on a shattered remnant counts against every plan that follows it. See Settled.
        using (Spent.On("Tick/Insisted.Settled"))
            Insisted.Here.Settled(_scan);


        // After the sweep, so Live means "the game has it as of this sweep". Every frame rather
        // than on the sweep's own rhythm would measure the same transitions repeatedly, since
        // nothing reattaches an entity between sweeps.
        if (_scan.Swept && Settings.Recording.RecordStreaming)
        {
            _streaming.Observe(this,
                Safe.Read(GameController, static g => g.Player.GridPos, Vector2.Zero), _scan.Targets);
        }

        // Every remnant seen, written down once per state it is in. Passive: it records what the
        // game generates so that what a reroll is worth can eventually be measured rather than
        // guessed, and it is on the sweep's rhythm because nothing here changes faster than that.
        // No longer behind the debug switch as well: a recording is a recording whether or not the
        // overlay is drawing, and pairing the two meant turning the drawing on to gather evidence.
        if (Settings.Recording.Census)
        {
            _census.Observe(this, GameController, _scan, _valuation);
            RemnantOffers.Flush(this);
        }

        // What each marker actually turns into. Every frame rather than on the sweep, because a
        // monster that dies between two sweeps is a monster that never appeared as far as a sampled
        // count is concerned - and the whole point is to count what was unearthed, not what is
        // still standing.
        // Tested here rather than only inside Observe, which read it after a walk of the monster
        // list had already been paid for.
        if (Settings.Recording.RecordSpawns)
            using (Spent.On("Tick/Spawns.Observe"))
                _spawns.Observe(this, GameController, _scan, Settings, _valuation,
                    _blast.Radius(GameController, Settings) ?? 0f, _planning);

        // Taken whenever the indicator happens to be up, and kept twice over: for this map, and -
        // with the map's modifiers divided out - for every map after it. It cannot be read at all
        // once placement mode is off, and it is wanted before then to plan with.
        using (Spent.On("Tick/Blast.Observe"))
            _blast.Observe(GameController);

        // Walking to a different expedition in the same map means the plan is for somewhere else.
        if (_planning.Moved(Detonator.DetonatorGridPosition(GameController)))
            _placement.Forget();

        // **An edited weight is a different question, so the answer is thrown away and asked again.**
        // Without this the score and every number over the ground kept the values they were solved
        // with, and only a manual solve showed your own edit. See Planning.Stale.
        // Not during a batch of repeats: the clear between presses moves the reference table's own
        // revision, so this would fire between every pair and add a press nobody counted. See
        // RepeatedPresses.
        if (!_planning.Searching && !_repeats.Running && _planning.Stale(Settings))
        {
            _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                ZoneCancellationToken, cause: "the reference table or a weight changed");
        }

        // Collects the answer whenever the background search has one.
        using (Spent.On("Tick/Planning.Poll"))
            _planning.Poll(this, _scan);

        using (Spent.On("Tick/Placement.Tick"))
            _placement.Tick(GameController, Settings, _scan, _blast, _valuation);

        var tail = Spent.On("Tick/Tail");

        // **A refusal or a wrong landing no longer asks for a chain.** Both used to re-solve here,
        // on the reasoning that a spot the game would not take is a spot the planner did not know
        // was bad and now does. The rule is the game's own routine, so either one is the model
        // being wrong about that cell - and solving around it is what stopped anybody finding out
        // which part of the model. The run stops and records instead. See PlacementDisagreements.

        // **A roll makes the plan an answer to a different site, so it is asked again.**
        //
        // The loop this completes is: solve, suggest a roll, roll it, solve again, suggest again.
        // Rolling a remnant changed what that remnant offers and nothing re-solved, so the chain on
        // screen still routed for the combination that had just been replaced - and the advice, which
        // recomputes once per plan, had no new plan to recompute against and sat on its old answer.
        //
        // Given the reroll window rather than a press's, because that is what the setting is for: the
        // chain is seeded with the previous one and has a single remnant's difference to absorb. See
        // SolverSettings.LoopSolveMs.
        //
        // TakeRolled clears as it reads, so one roll starts one solve. Left as a flag it would start
        // another on every tick until something else cleared it.
        // **A presolve in flight is stopped rather than waited on, because a roll is what it is
        // in the way of.**
        //
        // The dispatch below waits for the search to be idle, and a presolve window is four seconds
        // on an ordinary site. Worse, the presolve is usually IN that window because of this very
        // roll: its fingerprint holds the rewards and moves as soon as they change, which is before
        // the scan reads the remnant's new socket count and sets the flag. So the rehearsal
        // dispatched a four second pass for the roll, the roll's own pass queued behind it, and the
        // advice arrived seven seconds after the click. Measured: run 2 a 4,084ms presolve caused by
        // the roll, run 3 the roll's own 1,120ms pass starting 7ms after it ended.
        //
        // Stopping loses nothing - Stop publishes the best found so far and the next pass is seeded
        // from it - and it frees the next frame for the pass that answers the site as it now is.
        //
        // Only a presolve. A search the player asked for is not interrupted by this.
        // **Not in the continuous mode, where the whole point is that the solver keeps going.**
        // There the advice stands while a search runs and says which chain it was weighed against,
        // so nothing is waiting on this search to end. See RerollSettings.Mode.
        // **Nothing is re-solved for a roll at a site that has been set off.** Coming back through a portal
        // reloads every remnant, and the ones already rolled read as rolls again - so the site that had just been
        // detonated was planned afresh, on an assumed five explosives because the panel had not filled in yet.
        // The signal is taken and dropped, so it does not fire later either.
        if (_scan.RollPending && Detonator.SetOffHere(GameController))
            _scan.TakeRolled();

        if (_scan.RollPending && _planning.Searching && Planning.Rehearsing && !_placement.Busy &&
            Rolling.Mode(Settings) != RerollSettings.Continuous)
            _planning.Stop("a remnant was rolled");

        // **A roll makes the propagation figures wrong, whether or not anything can act on it yet.**
        //
        // The block below only runs when no search is in flight, which in the continuous mode is
        // almost never - so putting this inside it, which is where it was first written, meant the one
        // mode people use never marked anything. Reported from the game: a rolled remnant went on
        // showing the rune it used to pass until the next solve cycle published.
        //
        // RollPending rather than TakeRolled, because this must not consume the signal the block below
        // is waiting for.
        //
        // Planner.RuneTallyByRemnant is published by the detailed pass and read only by the readouts,
        // so marking it is enough: the line and the combinations window both go blank rather than
        // stating runes that are no longer there. See Planner.RuneTallyOutOfDate.
        if (_scan.RollPending)
            Planner.RuneTallyOutOfDate = true;

        // **In the continuous mode a roll takes over the search rather than queueing behind it.**
        //
        // The guard on Searching defers the roll's re-solve until whatever is running has finished,
        // and in this mode something is nearly always running - so the search in flight spends its
        // whole budget on the site as it was BEFORE the roll, and only then does the question the roll
        // asked get started. Reported from a Grand site as "solve, reroll, solve, reroll", with an
        // eight second wait on each side of the roll instead of one.
        //
        // Start already does the right thing with a search in flight: it cancels it and takes over,
        // which is how a press behaves, and the pass it cancels has published its best so the new one
        // is seeded from it. Nothing is lost that the roll had not already invalidated.
        //
        // Still not while a placement run is going: that is the half of the cycle where advice and
        // re-solving are both out of place. See Rolling.Consider.
        var takesOver = Rolling.Mode(Settings) == RerollSettings.Continuous;

        if ((takesOver || !_planning.Searching) && !_placement.Busy && _scan.TakeRolled())
        {
            // **The advice is dropped as well as re-solved, because the plan may not change.** Consider
            // recomputes once per plan, keyed on the plan's identity - and a re-solve that fails to
            // beat the standing chain keeps it, quite deliberately, so nothing would tell the advice
            // to look again. It would then go on recommending a roll it weighed against the
            // combination you have just replaced. Forgetting it costs one pass and cannot be wrong.
            //
            // **Except in the continuous mode, where it is the one thing that cannot happen.** Forget
            // empties the verdicts, which blanks every readout until the next pass has screened the
            // whole site - seconds, once per roll, which is what that mode exists to avoid. The
            // verdicts for every OTHER remnant are still about the chain in front of you; only the
            // rolled one is spent, and Divert drops that by the same already-rolled reading Refuses
            // uses. Their gains are a roll out of date, which is what Stale is for.
            if (Rolling.Mode(Settings) != RerollSettings.Continuous)
                Rolling.Here.Forget();


            _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                ZoneCancellationToken, cause: "a remnant was rolled", looped: true);

            // **And the presolve is told, so the roll is not solved a second time.** Its
            // fingerprint holds the rewards, which is exactly what a roll changes, so it would
            // re-open for the event this solve was started for. Only when the solve really began -
            // Start can refuse, and a refused solve is a site the presolve should still cover.
            // See Rehearsal.RemnantWasRolled.
            if (_planning.Searching)
                _rehearsal.RemnantWasRolled();
        }

        // **How many explosives are down, read once for the whole tick.**
        //
        // Three things want it and each used to fetch it for itself - the reroll advice twice over,
        // once to decide whether the ground was settling and once inside Consider to decide whether
        // to advise at all, and the crosses below a third time. Reading remote memory three times for
        // one number is cheap and still wrong: they are three answers to one question, and nothing
        // guaranteed they agreed. Hoisted so the tick has a single figure and every reader is looking
        // at the same ground.
        var down = Safe.Read(() => Detonator.Info(GameController).PlacedExplosiveCount, 0);

        // Which single remnant is worth a Liquid Verisium, worked out against the solved chain -
        // there is no answer to that question before there is a chain. Once per plan, and it stops
        // on its own as soon as an explosive goes down. See Rolling.
        // Not while a run is laying explosives or one has just come off: the advice is about the
        // decision between runs, and asking during one costs a solve per link for an answer nobody
        // sees. See Rolling.Consider.
        using (Spent.On("Tick/Rolling.Consider"))
            using (Spent.On("Tick/Rolling.Consider"))
                Rolling.Here.Consider(GameController, Settings, _scan, _planning, _valuation,
                _placement.Busy || down != _wereDown || _repeats.Running, down);

        // What the plan is worth against what is actually down. Recomputed only when one of the
        // three things it depends on moves, which is not often.
        using (Spent.On("Tick/Scoring.Update"))
            _scoring.Update(GameController, Settings, _scan, _blast, _valuation, _planning);

        // Explosives down is read above, once for the tick.
        int off;

        using (Spent.On("Tick/Detonated"))
            off = Detonator.ExplosivesDetonated(GameController);

        // Held from the frame before rather than from the last CHANGE, so an undo is noticed on the
        // frame it happens.
        var before = _werePlaced;

        using (Spent.On("Tick/Placed"))
            _werePlaced = Detonator.PlacedExplosiveGridPositions(GameController) ?? [];

        if (down != _wereDown || off != _wereOff)
        {
            // **An explosive taken back off the ground leaves a plan that is one link too short.**
            //
            // While links are being placed the plan holds only what is LEFT - four down means a
            // plan of one - because the placed prefix is a fact and the remainder is the question.
            // Undo takes one back at a time, and each undo frees a link the plan has no entry for:
            // undo four and the plan is still the single spot it was, drawn as a line straight from
            // the detonator with the earlier links simply absent, and scored alone. Seen as "0/608"
            // where the chain it came from was worth 5,322.
            //
            // Dropped rather than left up, because a stale plan is worse than none: every readout
            // is built from it, and each then says something confident and wrong.
            //
            // Getting it back is the other half and needs no special case. The placed count is part
            // of the presolve's fingerprint, so this same change re-opens the question, and the
            // site's best chain is still on file - so the re-solve is seeded from that rather than
            // searched for from nothing, and the whole chain returns with the freed link restored
            // to it. Undoing one at a time therefore walks back up the same chain it walked down.
            //
            // **The ground an explosive was standing on is ground again.**
            //
            // A spot with an explosive on it reads red, and so does everything within the minimum
            // separation of it - and those readings were filed as refusals for the life of the
            // site. Taking the explosive back off is exactly the moment they stop being true, so
            // they are handed back here. Without it, undoing a chain left every one of its own
            // links on a cell the plugin believed was dead, which scored the route at minus
            // infinity. Detonator.BlockedByOwnExplosive is the half that stops new
            // ones being recorded.

            // **A bomb that landed somewhere the plan did not ask for.**
            //
            // The only thing a landing can do that the chain in hand does not already account for:
            // the route is now measured from ground it did not choose, and its remaining links are
            // answers to a question nobody is on. This used to happen by accident, because the
            // presolve's fingerprint held the placed count and so re-solved after every explosive -
            // see Rehearsal, where that is now gone. Asked for explicitly instead, on the one
            // condition that warrants it.
            //
            // **No longer dispatched, and it contradicted a standing decision.** Re-solving because
            // a blast landed away from the plan is replanning on a failure, which this plugin was
            // told not to do - the placement run records the disagreement and stops rather than
            // routing around it, see PlacementDisagreements. It also breaks the one flow where the
            // player is committed: the search starts behind them, and their next press stops it
            // instead of placing.
            //
            // Kept as a reading rather than deleted, because Deviated is what the record is built
            // from and the question "did this land where the plan asked" is still worth asking.
            if (down > _wereDown && !_planning.Searching &&
                Placement.Deviated(GameController, _planning))
            {
                // Recorded by the placement run itself. Nothing is dispatched from here.
            }

            // **The cursor moves back and the chain stays.** See Planning.Track, which is where
            // this stopped being a search problem: the route is held whole, so the link the undo
            // freed is simply the one in front of the cursor and the plan is the chain from there.
            //
            // A re-solve only when the chain cannot answer - nothing planned here, or explosives on
            // the ground that this chain never asked for, so what is being walked back up is not
            // the route in hand. Asked for rather than merely forgotten: forgetting leaves the
            // screen empty and the replacement only arrives if the presolve happens to be on.
            if (down < _wereDown && _planning.Ready &&
                !_planning.Track(GameController, Settings, _scan, _blast, _valuation))
            {
                _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                    ZoneCancellationToken, cause: "an explosive was taken back off the ground");
            }

            _wereDown = down;
            _wereOff = off;

        }

        // The press that arrived while something else was running, released now it is not.
        //
        // Every gate the presolve waits on - a bake-off and a cold solve are both measurements that a
        // stray press would spoil. See _solveWanted.
        //
        // **Bounded, because a press must always do something.** A gate that never opens would
        // swallow it silently, so after the wait the press goes through regardless and the dump says
        // which it was.
        var waited = _solveWanted && DateTime.UtcNow - _solveAskedAt > Waiting;

        if (_solveWanted && !_planning.Searching && !_coldWanted && !_bakeoff.Running &&
            !_repeats.Running)
        {
            _solveWanted = false;

            _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                ZoneCancellationToken,
                cause: waited ? _solveCause + " gave up waiting for" : _solveCause);
        }

        // The walk spent on what the first press would otherwise pay for.
        //
        // Never while a cold solve is pending. F3 exists to measure a search from nothing, and a
        // rehearsal that ignored it would destroy the only comparable measurement here, nor while a
        // bakeoff is running, which is measuring strategies against each other. See Rehearsal.
        //
        // **And never while explosives are on the ground.** The presolve answers "has the site
        // changed", and during a placement run it changes constantly - every blast that lands moves
        // remnants out of the target list - so it re-opened and dispatched a search the player had
        // not asked for, in the middle of the one flow where they are committed and pressing a key
        // per link. The advice already refuses for the same reason and says so: see
        // Rolling.Consider, where "rolls are worth advising before placing".
        //
        // The chain in hand is the one being placed and nothing may replace it until the ground is
        // clear again.
        // **Nor during a batch of repeats.** A presolve is a search, Planning.Start cancels whatever
        // is in flight to take over, and a press cut off halfway is not a sample of anything. See
        // RepeatedPresses.
        // When each step of arriving happened, for the dump; each test runs only until its step is stamped. See SiteArrival.
        if (!SiteArrival.Noted(SiteArrival.Step.DetonatorEntity) &&
            Detonator.DetonatorGridPositionFromEntity(GameController) != Vector2.Zero)
            SiteArrival.Note(SiteArrival.Step.DetonatorEntity);

        if (!SiteArrival.Noted(SiteArrival.Step.FirstMarkers) && _scan.Targets.Count > 0)
            SiteArrival.Note(SiteArrival.Step.FirstMarkers);

        if (!_coldWanted && !_bakeoff.Running && !_repeats.Running && down <= 0)
            _rehearsal.Tick(GameController, Settings, _scan, _blast, _valuation, _planning,
                _placement, ZoneCancellationToken);

        // **The ground is asked again when the placement tool appears, and the plan re-solved if it no longer
        // holds.** A plan made on the approach can be overtaken by the ground: the Runed Monoliths of a stone
        // circle are written into the ground the game routes over only once they rise, and on Caldera a link
        // planned before that was put 4 grid short while its ring read green. The tool appearing is the moment
        // the player is in range to act, so it is the moment to check - once, on its arrival, not every frame.
        // See Planning.StillHolds.
        var toolShowing = Detonator.Placeable(GameController);

        if (toolShowing && !_toolWasShowing && _planning.Ready && !_planning.Searching && !_placement.Busy &&
            !Detonator.SetOffHere(GameController) &&
            !_planning.StillHolds(GameController, Settings, _scan, _blast, _valuation, out var broken))
        {
            _planning.Start(GameController, Settings, _scan, _blast, _valuation, ZoneCancellationToken,
                cause: $"the ground changed under the plan - {broken}");
        }

        _toolWasShowing = toolShowing;

        // **What is already true about the explosives on the ground, without being asked.**
        //
        // Reloading the plugin mid-chain, or walking back to a half-finished dig, left every remnant
        // blank: the figures over them come from the detailed pass, which needs an environment, which
        // only a solve built. Pressing a key to find out what a bomb already placed is doing is the
        // wrong way round. Latched inside, on the site and the placed count, so it costs one build
        // rather than one a frame. Behind the same gates as the presolve. See Planning.Showing.
        if (!_coldWanted && !_bakeoff.Running)
            _planning.Showing(GameController, Settings, _scan, _blast, _valuation);

        // One key, three meanings, resolved in the order they come up.
        //
        // Re-plan, if the cursor is on the game's own placement button - the gesture for "the chain
        // has moved on, work it out again from here". Place, if there is a plan. Scan, if there is
        // not, which is what the first press at a new dig site does.
        //
        // A press arriving mid-run is IGNORED rather than treated as a cancel. It used to cancel,
        // and that was a mistake of my own making: a run lasts a second or two, the obvious thing
        // to do when it looks stuck is press the key again, and the press then stopped the very
        // thing it was meant to hurry along - reporting "Stopped", which reads like a fault. There
        // is no need for a cancel key either way. Moving the mouse is the stop, it always was, and
        // it does not have to be remembered.
        //
        // The plan is kept rather than re-searched after each explosive: it is the route, and a
        // route that silently redrew itself between two presses of the same key would be a
        // different plugin. Re-planning is a thing you ask for.
        //
        // Every answer and every reason there is not one lives on the planner and the placement
        // sequence, and the prompt at the detonator says them - so nothing is kept here.
        tail.Dispose();

        // **The keys, timed apart from the work above them.** Eight of them are polled on every
        // frame whether or not anything is pressed, and a poll reads the keyboard through the
        // host - so the cost is eight times whatever that costs, sixty times a second, for an
        // answer that is almost always no. Whether that is worth anything is a measurement.
        using var keys = Spent.On("Tick/Keys");

        // **Opens the window listing the pool's chains.** A cycle key was the first version of this: a
        // list is better because the scores sit beside each other and a chain can be chosen rather than
        // stepped past. See ChainPanel.
        if (Settings.Debug.BrowseHotkey.PressedOnce())
            ChainPanel.Toggle();

        // **The same search, several times, over the same cold site.** Reuses the cold key's sweep
        // wait below - the scan is emptied by the clear and the markers only come back on the next
        // sweep, so solving immediately searches a site the plugin has just forgotten. See
        // RepeatedPresses and PressHistory.
        // **Read once into a local, because PressedOnce CONSUMES the press.** Asking it twice in one
        // tick means the second question is answered no whatever the key did, so the pair of branches
        // below - stop a running batch, or start one - between them swallowed every press and started
        // nothing. The same trap Scan.RollPending exists to avoid.
        var repeat = Settings.Debug.RepeatHotkey.PressedOnce();

        // Pressed again while one is running: stop rather than start a second, since two batches
        // interleaved measure neither.
        if (repeat && _repeats.Running)
        {
            _repeats.Abandon("the key was pressed again");
            _coldWanted = false;
        }
        else if (repeat && !_placement.Busy && !_bakeoff.Running)
        {
            // **The area, because without it the filed chain survives on disk.**
            //
            // Kept.ForgetAll clears the chain it holds and deletes the file only when it is told which
            // area to delete; with nought it does the first and not the second. Kept.Load then re-reads
            // that file at the start of the next solve - its own guard is "same area, same site, do
            // nothing", and forgetting resets the area so the guard no longer holds - and offers it as a
            // floor. So a batch cleared memory and handed press two press one's answer, which is
            // precisely the continuation this exists to prevent. See Kept.Load and Kept.Offer.
            Caches.Clear(_scan, _blast, _planning, _boundary, _snap, _cleared, keepScan: true,
                area: Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u));

            _repeats.Begin(Settings.Debug.RepeatCount.Value, Settings.Debug.RepeatFrom.Value,
                Settings.Debug.RepeatStopAtScore.Value);

            // **A batch measures presses and nothing else.** The reroll advice is skipped while it runs (see the
            // Rolling call), and the continuous reroll mode is stopped as the action key stops it - so the batch's
            // last press is where it ends, rather than handing over to "Solving: until reroll". The action key
            // starts it again. See Rehearsal.StopContinuing.
            _rehearsal.StopContinuing("a batch of repeated presses");

            _coldWanted = true;
            _coldAt = DateTime.UtcNow;
        }

        // Solve from cold, the baseline way.
        //
        // Clears the site first, then runs each ticked search over it in turn, and latches so the
        // action key clears too from here on - a comparison run that warms up halfway through is
        // not a comparison. See ColdHotkey.
        if (Settings.Debug.ColdHotkey.PressedOnce() && !_placement.Busy)
        {
            // The markers are kept: clearing them would make each run search a different site.
            Caches.Clear(_scan, _blast, _planning, _boundary, _snap, _cleared,
                keepScan: true);

            // Whole site for this press, not from now on.
            //
            // The point of the pair is a baseline and a contender: F3 is always the baseline and the
            // action key is always whatever is selected. Setting the dropdown here made the next
            // press inherit the baseline, so both halves of the comparison ran the same search.
            // Nothing to compare when there is nothing to place.
            //
            // Four searches would each come back empty and the tally would read "nothing to place"
            // four times over, which is a strange way to say "you have used all your explosives".
            if (!Bakeoff.Possible(Detonator.ExplosivesInHand(GameController)))
            {
                Bakeoff.Say("every explosive is placed - nothing left to plan");

                return;
            }

            // Not one search now, but all of them - see Bakeoff. The action key is left alone: once
            // the best plan is on screen the next press should place it, which is what it does when
            // a plan is ready. Latching every later press to re-solve was for comparing two runs by
            // hand, and the bake-off does that comparing itself.
            if (!_bakeoff.Begin(Settings))
                return;

            // Asked for, not done here. Clearing empties the scan, and the markers only come back on
            // the next sweep - solving immediately searched a site the plugin had just forgotten and
            // reported nothing in the dig site. The solve waits below until the sweep has run.
            _coldWanted = true;
            _coldAt = DateTime.UtcNow;
        }

        // The cold solve, once the site has been read again - properly, not partly.
        //
        // **Any markers is not the same as all of them.** Waiting on the first target to appear
        // starts the solve in the middle of a sweep: measured, two runs meant to be identical saw
        // ninety markers and seventy, which is a bigger difference than anything being compared.
        // A full sweep interval with the count unchanged means the sweep has finished.
        var reading = _scan.Targets.Count;

        if (_coldWanted && reading > 0 && reading == _coldSaw)
            _coldSteady++;
        else
            _coldSteady = 0;

        _coldSaw = reading;

        if (_coldWanted && !_planning.Searching && _coldSteady >= 2 &&
            DateTime.UtcNow - _coldAt > TimeSpan.FromMilliseconds(
                Math.Max(200, Settings.Debug.SweepMs.Value) * 2))
        {
            _coldWanted = false;

            // A bake-off drives itself below, once the site has been read.
            if (_bakeoff.Running)
                return;

            // So does a batch of repeats, and it starts its own first press rather than falling into
            // the single solve below - which would run one press outside the batch's counting.
            if (_repeats.Running)
                return;

            // Forced around the call and put back: Planning reads the strategy as it starts, so the
            // dropdown is only wrong for the instant it takes to launch the search.
            var chosen = Settings.Solver.Advanced.Strategy.Value;

            _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                ZoneCancellationToken, cause: "the cold solve comparison", askedByPlayer: true);

            Settings.Solver.Advanced.Strategy.Value = chosen;
        }

        // **Cleared the moment it finishes, whoever asked for it.** It used to be collected only on
        // the bake-off's path, which was fine while F3 was the only thing that started one - and
        // would have wedged the moment the preflood did, because everything below waits on it being
        // null and nothing else would ever have set it so.
        // **One press after another of the same search, each from the same cold state.**
        //
        // The routing stays, as it does between the bake-off's strategies and for the same measured
        // reason: each solve floods ground and keeps it, so a press late in a batch would inherit what
        // its predecessors paid for. The markers stay too, or each press would search a different site.
        // A batch press that has reached its target stops there, keeping what it found, and the next one starts.
        // See DebugSettings.RepeatStopAtScore.
        //
        // **Only a chain this press's own search published.** A solve's live chain starts as the standing one, carried
        // from before the press, and reading that stopped the first presses of every batch at once - counted as
        // reaching the target with nought rounds run and a pool best of 9,131 against a target of 9,751.
        //
        // Nor the standing chain republished: a solve publishes it once as it begins, which a time test alone let
        // through - press 1 of the next batch again stopped at 1.2 s on 9,751 with a pool best of 9,131.
        if (_repeats.Running && _repeats.Target > 0d && _planning.Searching &&
            Planning.LastGainAt > _repeats.PressStarted &&
            _planning.Live is { Count: > 0 } live && !ReferenceEquals(live, _repeatChecked) &&
            !(_planning.Standing is { } standing && System.Linq.Enumerable.SequenceEqual(standing, live)))
        {
            _repeatChecked = live;

            if (_planning.Env != null && Planner.Plainly(_planning.Env, live) >= _repeats.Target)
            {
                _repeats.Reached();
                _planning.Stop("the batch's target score was reached");
            }
        }

        if (_repeats.Running && !_coldWanted && !_planning.Searching)
        {
            _repeats.Tick(() =>
            {
                // The area for the same reason as at the key press: the filed chain has to go, or every
                // press after the first starts from the one before it. See Kept.ForgetAll.
                Caches.Clear(_scan, _blast, _planning, _boundary, _snap, _cleared,
                    keepScan: true,
                    area: Safe.Read(GameController, static g => g.Area.CurrentArea.Hash, 0u));

                // **A different draw each press, or the batch measures one search five times.** Every
                // seed in the search is a constant plus the worker's number, so without this the five
                // presses are identical by construction - measured, spread nought over five. See
                // PlanEnvironment.Draw.
                Planning.Draws++;

                _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                    ZoneCancellationToken, cause: "a repeated cold press", askedByPlayer: true);

                return _planning.Searching;
            });

            return;
        }

        // One strategy after another, each with the window it would get alone.
        if (_bakeoff.Running && !_coldWanted)
        {
            _bakeoff.Tick(this, _planning, name =>
            {
                // Cold for every strategy, not just the first.
                //
                // **Whoever ran last was searching the best map.** Each solve floods ground and
                // keeps it, so running four in a row hands each one the routing its predecessors
                // paid for - measured, the same search scored 1,535.0 second in the order and
                // 1,600.8 fourth. That is a sixty five point head start for being late in the list,
                // which is not a property of the strategy.
                //
                // The markers are kept, as ever: forgetting those would have each strategy search a
                // different site, which is a worse unfairness than the one being fixed.
                // **Nothing here clears routing, and nothing ever did.** Reach answers come from the
                // game's own coarse grid and from Wire's per-call search, whose working state is thread
                // static - the plugin holds no routing cache to keep or drop. Clear took a keepRouting flag
                // that its body never read, and three call sites passed it with comments saying the routing
                // stays, which was true only because there was nothing to lose.
                Caches.Clear(_scan, _blast, _planning, _boundary, _snap,
                    _cleared, keepScan: true);

                var chosen = Settings.Solver.Advanced.Strategy.Value;

                Settings.Solver.Advanced.Strategy.Value = name;

                _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                    ZoneCancellationToken, cause: $"the bake-off, trying {name}", askedByPlayer: true);

                Settings.Solver.Advanced.Strategy.Value = chosen;
            });

            return;
        }

        SolveAfterMarks(down);

        if (Settings.ActionHotkey.PressedOnce() && !_placement.Busy)
        {

            // **Pressing the key at a dig site is asking to see it, whatever the press then does.**
            //
            // A presolve draws nothing until asked, and the ask was recorded in one place only -
            // Start, where a search begins. Every other thing this key can do therefore left the
            // ground blank: stopping a rehearsal revealed nothing, and a rehearsal that had already
            // finished on its own went straight to placing with no chain, no numbered links and no
            // blast circles drawn, which is the plugin moving the mouse over a plan the player has
            // not been shown.
            //
            // One rule, set once, before the press is interpreted: the key was pressed here, so
            // here is on show. See Planning.Show.
            Planning.Show(Detonator.DetonatorGridPosition(GameController));

            // Solving needs explosives. With none left there is nothing to solve for, so the key
            // goes straight to the placement sequence - which is where shattering lives, and which
            // says so itself if there is nothing there to shatter either.
            var spent = Detonator.ExplosivesInHand(GameController) <= 0;

            // Pressed while it is still thinking: stop, and keep what it has.
            //
            // The same key starting and stopping is what a player expects from something visibly
            // working - and the alternative was worse than doing nothing, because Start cancels
            // before it re-plans, so a second press threw the search away and began an identical
            // one. There was no way to say "that is good enough".
            //
            // **Not once explosives are down, where the key means "place the next one".** Half way
            // through a run the player is committed and acting, and a search they did not ask for
            // had started behind them - so the press that should have placed the fifth explosive
            // stopped that search instead, and nothing was placed. Measured: four down, a solve
            // triggered, and the next press cancelled it.
            //
            // Stopping is the right answer for somebody waiting on a plan. It is the wrong one for
            // somebody using it.
            //
            // **So with explosives down it still stops when there is no plan to use.** With none in hand
            // the press fell through to the start below and restarted the very solve it was meant to end -
            // after a reload mid-run, every press threw the search away and began it again. Stopping keeps
            // the best chain reached, so the next press places from it.
            if ((down <= 0 || !_planning.Ready) && _planning.Searching && _planning.Stop())
            {
                // Nothing else this press. Stopping IS the action - and it stops the continuous
                // reroll mode too, or the presolve would start the next pass half a second later.
                _rehearsal.StopContinuing();
            }
            // **Between two passes of the continuous reroll mode the key still means stop.** There is
            // half a second between one pass ending and the next starting, and a press landing in it
            // would otherwise go on to the placement branch below and start laying explosives -
            // which is what the second press is for, not the first. See Rehearsal.Continuing.
            else if (down <= 0 && _rehearsal.Continuing)
            {
                _rehearsal.StopContinuing();
            }
            // **An open combinations window outranks everything, and a remnant standing on its own
            // is why that matters.** Maps are full of remnants attached to no expedition at all: you
            // click one, the window comes up, and choosing a reward is the whole of what there is to
            // do - the shatter that starts the encounter is not offered until something is picked.
            //
            // The branch below asks whether there is a chain to plan, and for those remnants the
            // answer is no and the key went off to solve a dig site on the other side of the map
            // instead. Placement already treats the window as an interruption from wherever it
            // happens; this makes the key agree with it. Nothing about the choosing cares which
            // expedition a remnant belongs to, or whether it belongs to one.
            else if (Options.Open(GameController) || _placement.Waiting(GameController, Settings))
                _placement.Begin(GameController, Settings, _planning);
            else if (Detonator.OverToggle(GameController) ||
                (!_planning.Ready && !spent) || !Settings.Automation.On(Settings.Automation.PreExpedition.Enable))
            {
                _planning.Start(GameController, Settings, _scan, _blast, _valuation,
                    ZoneCancellationToken, cause: "the action key", askedByPlayer: true);

                // Asking for a plan undoes an earlier stop. See Rehearsal.ResumeContinuing.
                _rehearsal.ResumeContinuing();
            }
            else
                _placement.Begin(GameController, Settings, _planning);
        }

        // The reference table is not a debug tool, so its key is not behind the debug switch.
        if (Settings.TableHotkey.PressedOnce())
            Catalogue.Toggle();

        // Pointing at a marker and saying "that one". See Insisted.
        //
        // **Above the debug gate, and that is the whole point of where it sits.** It spent a release
        // filed with the dump keys below, which meant it did nothing at all unless debug mode was
        // switched on - while the marks it makes are drawn unconditionally, so the feature looked
        // wired up and simply never fired. It is a playing key, pressed every session to tell the
        // planner the one thing it cannot work out for itself. It belongs with the action key.
        //
        // **A tap cycles the mark on release; a hold marks it taken last.** The tap waits for the key to come up so the
        // two can be told apart. The marker is the one under the cursor when the key went down. See DebugSettings.TakeLastHoldMs.
        if (Settings.InsistHotkey.PressedOnce())
        {
            _insistDownAt = DateTime.UtcNow;
            _insistTarget = Insisted.Under(GameController, _scan.Targets);
            _insistHeld = false;
        }

        if (_insistDownAt != DateTime.MinValue)
        {
            if (!Settings.InsistHotkey.IsPressed())
            {
                if (!_insistHeld)
                    Insist(_insistTarget);

                _insistDownAt = DateTime.MinValue;
                _insistTarget = null;
            }
            else if (!_insistHeld &&
                     DateTime.UtcNow - _insistDownAt >= TimeSpan.FromMilliseconds(Settings.Debug.TakeLastHoldMs.Value))
            {
                _insistHeld = true;
                ToggleTakenLast(_insistTarget);
            }
        }

        Dump.Plan = _planning.Plan;
        Dump.Chain = _planning.Chain;
        // The head the plan starts after, plus however many of the plan's own spots now have an
        // explosive on them. See Dump.LinksDown for why the first half alone is not the answer.
        Dump.LinksDown = _planning.Laid +
                         (_planning.Plan == null
                             ? 0
                             : Placement.PlacedOf(GameController, _planning.Plan));
        Dump.Env = _planning.Env;
        Dump.BlastScores = _planning.BlastScores;

        // **The dump key answers to nothing but itself.**
        //
        // It used to sit behind the debug switch, on the reasoning that a mode which is off but
        // still writes files when a function key is brushed is not off. That is true of a mode that
        // watches and records by itself, which is what the switch now covers under Recording - and
        // a dump is the opposite of that. It writes once, when asked, and what it is most often
        // asked for is why the plugin is behaving the way it is with everything turned off.
        //
        // Requiring the debug mode to be on in order to record what the plugin does with it off is
        // a measurement that cannot be taken. The four lines above are assignments off fields
        // already in hand, so they cost nothing to keep current for it.
        // The button in the settings menu comes through here too, so an unbound key is not a
        // reason to be unable to take a dump. See Dump.AskedForInSettings.
        if (Settings.Debug.DumpHotkey.PressedOnce() || Dump.AskedForInSettings)
        {
            Dump.AskedForInSettings = false;

            Dump.Write(this, GameController, Settings.Debug.DumpRange.Value);
        }

        // The rest of the debug keys do answer to the switch, since each of them turns something on
        // rather than writing one file.
        if (!Settings.Debug.ShowOverlay)
            return;

        if (Settings.Debug.FrontierHotkey.PressedOnce())
            _frontier.Toggle(GameController);

        // One probe a frame while the sweep is running. See Frontier.Step.
        _frontier?.Step(GameController, Settings);

        // **Say every change, not just the keypress.** The sweep stops for several reasons that are
        // not the hotkey - the indicator going away, the cursor being taken back, the screen running
        // out of untested ground - and reporting only what the key did left it looking as though
        // nothing had happened at all. Anything that moves the status says so, once.
        if (_frontier != null && _frontier.Says != _swept)
        {
            _swept = _frontier.Says;

            if (_swept.Length > 0)
                LogMessage("AutoExpedition: " + _swept, 10f);
        }

        if (Settings.Debug.CorrelateHotkey.PressedOnce())
            _status = _correlate.Press(this, GameController, _scan);

        if (Settings.Debug.ScoreHotkey.PressedOnce())
        {
            _status = Scorecard.Write(this, GameController, Settings, _scan, _blast,
                _valuation, _planning.Plan, _planning.Laid);
        }
    }

    public override void OnUnload() => _boundary.Save(this);

    /// <summary>
    /// One long sentence broken into lines that fit, on word boundaries.
    ///
    /// Measured rather than counted in characters: the readout is drawn in a proportional font, so
    /// a character limit is either too cautious for narrow text or too generous for wide text, and
    /// this line carries element paths made of both.
    /// </summary>
    private static List<string> Wrapped(string text, ExileCore2.Graphics graphics, float width)
    {
        var lines = new List<string>();
        var line = "";

        foreach (var word in text.Split(' '))
        {
            var tried = line.Length == 0 ? word : line + " " + word;

            if (line.Length > 0 && Safe.Read(() => graphics.MeasureText(tried).X, 0f) > width)
            {
                lines.Add(line);
                line = word;

                continue;
            }

            line = tried;
        }

        if (line.Length > 0)
            lines.Add(line);

        return lines;
    }

    /// <summary>
    /// Handed each entity as the game loads it, which is what removed the periodic walk.
    ///
    /// Wrapped because a throw here is a throw inside the host's own loop over its entity list, and
    /// the cost of one bad read should not be the rest of the area never arriving. See Scan.Arrived.
    /// </summary>
    public override void EntityAdded(Entity entity)
    {
        using (Spent.On("EntityAdded"))
            Safe.Do(() => _scan.Arrived(entity));
    }

    /// <summary>And told when one goes, which is what the walk's opening move was for.</summary>
    public override void EntityRemoved(Entity entity)
    {
        using (Spent.On("EntityRemoved"))
            Safe.Do(() => _scan.Departed(entity));
    }

    public override void AreaChange(AreaInstance area)
    {
        Spent.AreaChanged();

        // Timed whole, so a long gap across a map load can say whether this plugin's handling of it was the cost.
        using var changing = Spent.On("AreaChange");

        var hash = Safe.Read(() => area.Hash, 0u);

        SiteArrival.Entered();
        _scan.AreaChange(hash, real: true);

        // A different site is a different problem, and a median across two of them describes neither.
        PressHistory.Forget();
        Placement.LoadSetByPlacement(hash);
        _repeats.Abandon("the area changed");

        _correlate.AreaChange(hash);
        _snap.AreaChange(hash);
        _cleared.AreaChange(hash);
        _boundary.AreaChange(hash);
        _scouted.AreaChange(hash, GameController);
        _streaming.AreaChange(hash);
        _planning.AreaChange(hash);
        Checked.Here.AreaChange(hash);
        Settled.Here.AreaChange(hash);
        PlacementDisagreements.Here.AreaChange(hash);
        Landed.Here.AreaChange(hash);
        _ground = null;
        _blocking = null;
        _rehearsal.AreaChange(hash);

        // A press held for the last site's flood is not owed to this one. See _solveWanted.
        _solveWanted = false;

        Missed.Here.AreaChange(hash);
        Insisted.Here.AreaChange(hash);

        // A different dig site, so the advice is about remnants nobody is standing near.
        Rolling.Here.ForgetTheSite();

        // The sighting keeps; the remnant it was on does not.
        Curio.AreaChange();

        _blast.AreaChange(hash);
        _census.AreaChange(hash);
        _spawns.AreaChange(this, hash);
        Finished.AreaChange();
        Ending.AreaChange();

        // Counted per area, so what has been said about it is too. See Banners.
        _warned = 0;

        // And a diagnosis is about the site that produced it, so a bad read cannot condemn the next
        // map before anything has been read there. See Terrain.Broken.
        Terrain.Unbreak();
        _broke = false;
        _placement.Forget();
        _status = "";
    }

    public override void Render()
    {
        if (!Settings.Enable)
            return;

        // The whole of Render up to the frame mark, so a stall in a part with no stage of its own - a panel, a
        // settings window - is still named on a long gap. Closed just before Spent.Frame, which is where the gap is
        // measured. See Spent.LongGaps.
        var rendering = Spent.On("Render");

        // **First, above every guard about being at a dig site.**
        //
        // It is a reference, and a reference you can only read while standing at a live expedition
        // with nothing open in front of you is not one. Everything below returns early when there
        // is no detonator, or when a panel covers the screen - which is correct for drawing a plan
        // on the ground and exactly wrong for a table you open to look up what a chest is worth.
        // Read it in a hideout, read it with the inventory up. See Catalogue.
        using (Spent.On("Catalogue.Draw"))
            Catalogue.Draw(GameController, Settings, _scan);

        using (Spent.On("ChainPanel.Draw"))
            ChainPanel.Draw(GameController, Settings, _planning, _scan);

        var ui = Safe.Read(() => GameController.IngameState.IngameUi, null);

        if (ui == null)
            return;

        // The world drawing is cut off at the edge of an open side panel and around the Escape menu, so the plan and the
        // debug drawing stop painting over them. The score area is not; see Overlay.Draw and
        // DisplaySettings.HideBehindPanels.
        RectangleF clearOfPanels;

        using (Spent.On("Panels.ClearOfPanels"))
            clearOfPanels = Settings.Display.HideBehindPanels
                ? Panels.ClearOfPanels(GameController)
                : new RectangleF(-1e5f, -1e5f, 2e5f, 2e5f);

        // **Nothing clear, so nothing drawn.** Clipping to an empty rectangle did not stop the drawing: with the Escape
        // menu open and the clip at 0x0 (2026-10-06), the overlay still painted over the menu. So an empty answer
        // returns here instead, before anything on the screen is drawn, as Panels.Hidden does for a full screen panel.
        if (clearOfPanels.Width <= 0f || clearOfPanels.Height <= 0f)
            return;

        // Before the panel gate below, and before the dig-site test: the combinations window is
        // itself a panel, and it is opened at the vendor rather than in the dig site.
        if (Settings.Display.Remnants.RuneshapeCombinationsWindow.ShowWindowPrices)
        {
            using (Spent.On("Options.Draw"))
                Options.Draw(Graphics, GameController, Settings, _valuation, _scan, _planning.Plan);
        }


        // Before the dig-site test, because a warning such as No input or Busy is not about a dig site.
        using (Spent.On("CursorWarning.Draw"))
            CursorWarning.Draw(Graphics, GameController, Settings);

        DrawTakeLastHold();

        // Before the dig-site test, because Bond's rares fight in the encounters after it.
        if (Settings.Display.Remnants.Propagation.BondRuneRadius && !Panels.Hidden(GameController))
        {
            using (Spent.On("BondTransfer.Draw"))
            using (Graphics.BeginRectClip(clearOfPanels, true))
                BondTransfer.Draw(Graphics, GameController, Settings.Display.Remnants.Rewards.OverruledColour);
        }

        // Nothing else to draw outside a dig site. The detonator element is the cheapest test for
        // that - it exists only where there is an encounter to detonate.
        if (Detonator.Info(GameController) == null)
            return;

        SiteArrival.Note(SiteArrival.Step.DetonatorPanel);

        // Only for the panels that leave no world visible behind them. A window covering part of
        // the screen is handled by skipping what it covers, not by blanking the lot.
        if (Panels.Hidden(GameController))
            return;

        SiteArrival.Note(SiteArrival.Step.FirstDraw);

        // Snapshotted once per area rather than per frame: reading the grid materialises the whole
        // of it, and only the diagnostics consult it. It goes slightly stale as remnants are spent
        // and their footprints stop eating the clearance around them, which a diagnostic can live
        // with.
        _ground ??= Terrain.Read(GameController);

        // The same scenery the planner routes around, snapshotted once, so the diagnostic below asks
        // about the obstacles the model actually used rather than a fresh reading of them.
        _blocking ??= Obstacles.Read(GameController, Detonator.DetonatorGridPosition(GameController), 400f);

        // What we believed about each explosive, taken at the moment it lands.
        //
        // **Outside the placement block entirely, having been wrongly inside two of its gates in
        // turn.** First it sat in the not-blocked branch, which runs only while the indicator is
        // green - and an explosive put down at the limit leaves the cursor on ground the game
        // refuses, so it never ran for exactly the placement worth recording. Moving it out of that
        // left it inside the placement-mode gate, where it reported "nothing placed yet" for a site
        // with a bomb on the ground, because the mode had been toggled off before the dump.
        //
        // Whether an explosive is on the ground has nothing to do with the colour of the marker under
        // the cursor, nor with whether placement mode happens to be switched on right now.
        using (Spent.On("Landed.Observe"))
            Landed.Here.Observe(GameController, _ground, _blocking, _planning.Env, _planning.Searching);

        // Measured before drawing, so the circle shown is this frame's best answer rather than the
        // previous one's. The game is answering the planner's question for free whenever the
        // indicator is down; all this does is write the answer down.
        if (Detonator.Placing(GameController))
        {
            // **A frame with no reading is skipped, not guessed at.** This writes the answer
            // down, and the dataset it writes to is the one the placement run later aims from -
            // see Detonator.PlacementIndicatorGridPosition for what guessing here used to cost.
            var indicator = Detonator.PlacementIndicatorGridPosition(GameController);

            if (indicator == Vector2.Zero)
                return;

            // **One reading a frame that is unambiguously about one cell.** Taken here rather than
            // inside either branch below, because those split on the verdict and this wants both.
            // See Settled for why the sets those branches fill cannot be measured against.
            if (Detonator.PlaceableNow(GameController, out var standing) is { } verdict)
            {
                // Whether the chain itself could account for a refusal here, so the measurement can
                // set those aside. BlockedByOwnExplosive is the placed explosives; the detonator
                // enforces the same separation with the same figure. Erring towards explaining too much
                // is the safe direction - it discards evidence rather than inventing agreement.
                var site = Detonator.DetonatorGridPosition(GameController);
                var apart = MathF.Max(1f, Settings.Debug.ApartAtLeast.Value);


                Settled.Here.Observe(standing, Detonator.RequestedGridPosition(GameController), verdict,
                    Detonator.BlockedByOwnExplosive(GameController, Settings, standing) ||
                    Vector2.DistanceSquared(site, standing) < apart * apart,
                    Detonator.LastExplosiveGridPosition(GameController));
            }

            // A blocked spot is not a placement. Measuring from one would teach the reach probe
            // that the chain stretches further than it does.
            // **The game showing the circle red is an answer too, and nothing was writing it down.**
            // Everything below measures from a spot the game would accept; a blocked one was simply
            // skipped, on the reasoning that it teaches the range probe nothing. True - and it
            // teaches something else entirely, which is that an explosive cannot go there. The
            // planner had no way to know until a run walked over and was refused, so a spot the
            // cursor was sitting on, showing red, stayed in the plan.


            if (!Detonator.PlacementIndicatorIsRed(GameController))
            {
                _scan.Confirm(Detonator.DetonatorGridPosition(GameController));
                // Every marker that lights up or goes dark, with how far away it was. Written to a
                // file as it goes, because the useful session is the one that ends unexpectedly.
                // Behind Data collection's switch. See RecordingSettings.CollectMarkerEdges.
                if (Settings.Recording.CollectMarkerEdges)
                {
                    _boundary.Observe(indicator, _scan.Targets,
                        Detonator.BlastRadius(GameController, Settings.Debug.CircleCorrection.Value) ?? 0f);

                    if (_boundary.Count >= 20)
                        _boundary.Save(this);

                    // Writes the per-art conclusion the moment another art is well enough measured.
                    _boundary.Summarise(this);
                }
                _snap.Observe(indicator);

                // **And what the game states outright about where the indicator is standing.**
                //
                // The reading below has to infer its answer from the cursor and the indicator landing
                // on the same cell, so it is silent whenever the cursor is between cells. This one is
                // told: the marker's animation swaps to a "_fail" variant where an explosive cannot go.
                // A sweep of the circle therefore writes down every cell it crosses rather than the few
                // the cursor happened to settle on. See Detonator.PlaceableNow.
                // The cell comes back from the same entity as the verdict - see PlaceableNow's at -
                // rather than from Indicator, which falls back to a field that follows the cursor past
                // the limit and would file this answer against the wrong ground.
            }
        }

        // Built once and shared. Every window's rect is a memory read, and this was being rebuilt
        // three times a frame - by the overlay, by the art labels and by the status line.
        List<RectangleF> covered;

        using (Spent.On("Panels.Covered"))
            covered = Panels.Covered(GameController);

        using (Spent.On("Cleared.Observe"))
        {
            _cleared.Observe(GameController, _planning.Plan.Points, Settings.Debug.ScoutReach.Value);
        }

        using (Spent.On("Overlay.Draw"))
        {
            Overlay.Draw(Graphics, GameController, Settings, _scan, _snap, _valuation,
                _boundary, _planning, _placement, _blast, _scoring, _spawns, _cleared, _scouted,
                covered, clearOfPanels);
        }

        rendering.Dispose();
        Spent.Frame();

        if (!Settings.Debug.ShowOverlay)
            return;

        // The long form of whatever the one-word status above the placement button is saying.
        var detail = _placement.Detail.Length > 0 ? _placement.Detail : _planning.Detail;

        // Behind debug mode as well as its own switch: it reports what the plugin is doing rather
        // than anything about the dig site, which is a debug drawing however useful.
        if (Settings.Debug.ShowOverlay && Settings.Debug.ShowStatus &&
            (_correlate.Ready || _status.Length > 0 || detail.Length > 0))
        {
            var line = _correlate.Ready
                ? $"AutoExpedition: remembered {_correlate.Count} markers - press F7 again after detonating, and after clearing"
                : $"AutoExpedition: {(_status.Length > 0 ? _status : detail)}";

            var at = new Vector2(Settings.Debug.TallyX.Value, Settings.Debug.TallyY.Value - 20f);

            // Wrapped, because these sentences got long enough to run off the right of the screen
            // and take the answer with them - the placement refusal names the element in the way,
            // and the name was the half that fell off. Upwards from the anchor so the first line
            // stays where it has always been and the overflow grows into empty space above it.
            var lines = Wrapped(line, Graphics, System.Math.Max(200f, Settings.Debug.TallyX.Value - 20f));

            for (var i = 0; i < lines.Count; i++)
            {
                var row = new Vector2(at.X, at.Y - (lines.Count - 1 - i) * 20f);

                if (!Panels.Covers(covered, row))
                    Graphics.DrawText(lines[i], row, Color.FromArgb(255, 255, 220, 120));
            }
        }

    }
}
