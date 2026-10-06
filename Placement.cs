using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExpedition;

/// <summary>What the placement sequence is waiting for.</summary>
internal enum Step
{
    Idle,

    /// <summary>Placement mode asked for, waiting for the game to turn it on.</summary>
    Toggling,

    /// <summary>The cursor is on its way to a planned spot.</summary>
    Moving,

    /// <summary>Arrived; checking the game agrees this is the spot before clicking.</summary>
    Checking,

    /// <summary>Clicked; waiting for the explosive count to move.</summary>
    Confirming,

    /// <summary>
    /// A click was thrown away by something the game had under the cursor; toggling to clear it.
    ///
    /// **A tooltip can eat the click and leave no trace.** A Vaal Relic's ground tooltip carries
    /// underlined keywords - "elemental ailment threshold" and its like - which are themselves
    /// clickable, and the game gives them the click ahead of the explosive. UIHover does not name
    /// them, so the gate that refuses a click over a piece of interface does not see it, and the run
    /// reads it as the game refusing a perfectly good spot.
    ///
    /// Toggling placement mode off and on clears whatever had it. See Cleared.
    /// </summary>
    Clearing,

    /// <summary>A remnant is set to the wrong combination; the cursor is on its way to its button.</summary>
    Opening,

    /// <summary>The button has been clicked; waiting for the combinations window to come up.</summary>
    Opened,

    /// <summary>
    /// The scroll bar is being dragged to bring the chosen reward into view.
    ///
    /// A step of its own because a drag spans frames and the run must not do anything else while
    /// the button is down. See Options.Scrolling.
    /// </summary>
    Scrolling,

    /// <summary>The cursor is on its way to a spent remnant's shatter button.</summary>
    Shattering,

    /// <summary>Shatter clicked; waiting for the button to go away.</summary>
    Shattered,

    /// <summary>Walking to a chest the run clicked; waiting to arrive before doing anything else.</summary>
    Arriving,

    /// <summary>The combinations window is up; the cursor is on its way to the best option.</summary>
    Choosing,

    /// <summary>The option has been clicked; waiting for the window to go away.</summary>
    Chose,

    /// <summary>
    /// A reward has been taken and the explosive just placed covers another remnant with none chosen; waiting for the
    /// game to put that remnant's window up. See Placement.AfterPick.
    /// </summary>
    AwaitingWindow,
}

/// <summary>
/// Putting the explosives down, a tick at a time.
///
/// One press places every planned explosive it can see, in order, and then stops. It stops for
/// ordinary reasons rather than exceptional ones - the next spot is off screen and you need to
/// walk, or the plan is finished - and neither is a failure, so both say so plainly rather than
/// reading like something went wrong.
///
/// **It never detonates.** Nothing here clicks a button. The only click is out in the world, on a
/// spot the game has already confirmed, and a planned spot that projects underneath a panel is
/// refused rather than clicked - which matters most for the panel in the bottom right corner,
/// because that is the one holding the button that starts the encounter. Starting it is the
/// player's decision and there is no path through this code that takes it.
///
/// **Two checks stand between arriving and clicking, and both fail closed.** The game states where
/// the next explosive would land, so that is compared against the planned spot; and the game lights
/// up everything the blast would catch, so the markers the plan expected to be caught have to
/// actually be lit. The first says the cursor is in the right place. The second says the right
/// place was right - it needs no radius, no disc model and no assumption about the shape of a
/// blast, because it is the game answering the question directly.
///
/// Failing either is not an error, it is a miss: the cursor is nudged by the difference and it tries
/// again a few times, then gives up and says which check it could not satisfy. It never clicks on an
/// unverified spot, because an explosive spent in the wrong place cannot be got back.
///
/// **Undo costs nothing to support.** Which planned spots are already done is re-derived from the
/// game's own list of placed positions every time it is asked, and never counted and remembered
/// here - so shift+V taking the last explosive back off simply makes that spot the next one to
/// place again. Nothing to keep in step means nothing to get out of step.
///
/// The player moving the mouse stops everything, which is ExileInput2's doing rather than this
/// plugin's - it notices the cursor is somewhere it did not put it, drops the lease and reports why.
/// That is the whole safety model, and it is why there is no panic key to remember.
/// </summary>
internal sealed class Placement
{
    private readonly ExileInput2Client _input;

    private Step _step;
    private Plan _plan = Plan.Empty;

    // Kept so the sequence can re-ask the reward question between explosives. Advance is reached
    // from six places and threading two more arguments through all of them to answer one question
    // would be worse than holding what the last tick was given.
    private Scan _scan;
    private Valuation _valuation;
    private float _reach;
    private Vector2 _target;
    private DateTime _deadline;
    private int _placedWhenStarted;
    private int _landed;


    /// <summary>How many times placement mode has been asked for, for the spot in hand.</summary>
    private int _arms;

    /// <summary>
    /// Whether the circle is up because THIS turned it on, and the key that did it.
    ///
    /// Kept so the run can put the game back how it found it. A run that gives up with the circle
    /// still showing has left the player armed for a placement they did not ask for - the next
    /// click anywhere in the world puts an explosive down. Turning it off is not tidiness, it is
    /// undoing a thing that was done on their behalf.
    ///
    /// Only when this armed it. A player who had placement mode on before pressing the key gets it
    /// left alone, because that state is theirs.
    /// </summary>
    private bool _armed;

    /// <summary>Whether this run has already tried clearing the cursor once. See Cleared.</summary>
    private bool _cleared;

    /// <summary>What the game's placement state was when the click went out. See Land.</summary>
    private bool _activeAtClick;

    private bool _showingAtClick;

    /// <summary>
    /// Where the click went, and what the game said about that spot as it went.
    ///
    /// **Because "the click went out and nothing happened" is not a diagnosis.** The old record was
    /// two booleans, so a run that failed could say placement mode was on and the circle was up and
    /// nothing else - which rules out the two cheap explanations and leaves every real one open.
    /// The screen point says whether the cursor was where it was meant to be, the landing says what
    /// the game thought the click would do, and blocked says whether the game was already refusing.
    /// See Confirming's timeout.
    /// </summary>
    private Vector2 _clickedAt;

    private Vector2 _landingAtClick;

    private bool _blockedAtClick;

    /// <summary>
    /// How many clicks in a row the game has taken and done nothing with, at this site.
    ///
    /// **A site can be bricked, and then every spot refuses.** Seen in game and confirmed against a
    /// recording: a false "Expedition Complete" at one dig site leaves the OTHER site in a map
    /// unusable - the ground draws as placeable, the placement circle comes up, the indicator
    /// follows the cursor, and a click places nothing, anywhere on the site. Leaving the instance
    /// and rejoining does not clear it; walking out of range and back does not clear it.
    ///
    /// One refusal is a spot the game did not like and is worth replanning around. Several in a row,
    /// at different spots, with placement mode confirmed on at each click, is not about the spots -
    /// and saying "press again to replan" to somebody whose site cannot accept anything is worse
    /// than saying nothing. See Confirming's timeout and Finished.
    /// </summary>
    private int _declined;

    private Keys _key = Keys.V;

    /// <summary>The game, for the exits that have to touch it. Set whenever the sequence runs.</summary>
    private GameController _game;

    /// <summary>
    /// The content this spot was chosen for, taken from the plan.
    ///
    /// From the plan rather than recomputed from the blast radius, because the two are not the same
    /// question. The planner picked this spot because these specific markers fall inside it;
    /// re-deriving that set at placement time instead asks whether the geometry still agrees with
    /// itself, and any error in the radius or the marker extent then invents a marker that ought to
    /// be glowing and is not. That cannot be corrected by aiming, because nothing was wrong with
    /// the aim - so it was corrected at, three times, and reported as Stuck.
    /// </summary>
    private Vector2[] _expect = [];

    /// <summary>
    /// How many frames running the coverage has come up short.
    ///
    /// The indicator moving and the highlighting following it are not guaranteed to be the same
    /// frame, so one frame of disagreement is a frame to wait rather than a reason to stop.
    /// </summary>
    /// <summary>
    /// Frames a mismatch between the indicator and the planned spot must survive before the run
    /// treats it as the game's answer rather than the indicator still catching up with the cursor.
    /// </summary>
    private const int SettleFrames = 3;

    /// <summary>The cursor position, in grid units, that the run aimed at to land on _target.</summary>
    private Vector2 _aimedGridPosition;

    private int _short;

    /// <summary>
    /// Frames the placement indicator has been standing somewhere other than the planned spot.
    ///
    /// The indicator animates towards the cursor, so the first frame after a move can still show
    /// the previous spot. Only a mismatch that survives SettleFrames is the game answering.
    /// </summary>
    private int _offSpotFrames;

    public Placement(ExileInput2Client input) => _input = input;

    /// <summary>
    /// What it is doing, or why it stopped, in as few words as will do. One or two words.
    ///
    /// Short because of where it goes: a line above the placement button, read out of the corner of
    /// an eye in the middle of doing something else. "Deviated" is a thing you can take in without
    /// stopping; a sentence explaining what deviated from what is not.
    ///
    /// A finished run's last word does not stay on screen for ever. It used to, and the effect was
    /// a plugin that looked stuck on "Stopped" long after it had tidied up and was ready to go -
    /// which is worse than saying nothing, because the one thing a status line must not do is
    /// describe a state that has passed.
    /// </summary>
    public string Status => _step != Step.Idle || DateTime.UtcNow < _lingers ? _status : "";

    /// <summary>The same thing at length, for the debug line. Nobody reads this mid-fight.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>
    /// The last thing placement said, and when, whether or not it is still on screen.
    ///
    /// **Status expires and the reason it expired is the reason it was wanted.** A refusal shows for
    /// a second or two and then the readout goes back to the count, so by the time a dump is taken -
    /// which is after the thing went wrong, always - Status is empty and F6 says nothing about why
    /// placement stopped. This does not expire, so the key can be pressed at leisure.
    /// </summary>
    public string Said { get; private set; } = "";

    public DateTime SaidAt { get; private set; }

    private string _status = "";
    private DateTime _lingers;

    /// <summary>How long the last word of a finished run stays up.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    public bool Busy => _step != Step.Idle;

    /// <summary>How long any one step may take before it is treated as not going to happen.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a walk to a chest may take before the run gives up on it.
    ///
    /// Much longer than the other steps, because this one is measured in how far away the chest is
    /// rather than in how long the client takes to answer. Still bounded: a walk that never ends is
    /// a character stuck on scenery, and the run should stop rather than wait for the map to close.
    /// </summary>
    private static readonly TimeSpan Arriving = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How far inside the window a spot has to project to count as somewhere to put the cursor.
    ///
    /// The edge of the screen is not somewhere to aim: the projection is least trustworthy there,
    /// and a spot that near the edge is one to walk towards rather than stretch for.
    /// </summary>
    private const float Margin = 48f;

    /// <summary>
    /// How long to give the key before deciding the circle is not coming up.
    ///
    /// Short, because when it works it works within a frame or two, and because being wrong here
    /// costs one more tap rather than anything that matters.
    /// </summary>
    private static readonly TimeSpan Tapped = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Starts placing, from wherever the chain has got to.
    ///
    /// Refuses rather than queues. Every reason it cannot start is one the player can see and fix,
    /// and a press that silently means "later" is a press that appears to have done nothing.
    /// </summary>
    /// <summary>
    /// The markers a blast was chosen for and did not light, for the overlay to ring.
    ///
    /// Kept until the next explosive goes down, which is the moment the question they raise has been
    /// answered one way or the other. See Overlay's Unlit.
    /// </summary>
    public IReadOnlyList<Vector2> Unlit => _unlit;

    /// <summary>
    /// Why those markers are marked, in the two words that go under them.
    ///
    /// **The two failures are one concept: a blast was tried here and this content did not come.**
    /// One is the terrain under the marker refusing to light it, the other is the chain not
    /// stretching far enough round an obstruction to put the blast there at all - and from the
    /// player's side both are "that was the plan and it did not happen to those things". Marking
    /// only the first left the commoner failure with a cross on the ground and nothing saying what
    /// it cost.
    /// </summary>
    public string Reason { get; private set; } = "";

    private Vector2[] _unlit = Array.Empty<Vector2>();

    /// <summary>
    /// The spots a blast was tried at and could not be placed from, for the overlay to cross out.
    ///
    /// A different thing from the markers: those say what was missed, this says where it was missed
    /// FROM. Both are wanted, because the two failures look identical in the readout and completely
    /// different on the ground - a marker that will not light is a fact about terrain under the
    /// marker, and a spot that will not take an explosive is a fact about the route to it.
    ///
    /// Held to the same moment as the triangles: until the next explosive lands.
    /// </summary>
    public IReadOnlyList<Vector2> Spoiled => _spoiled;

    private readonly List<Vector2> _spoiled = new();

    /// <summary>Writes down a spot that did not work, once. See Spoiled.</summary>
    private void Spoil(Vector2 at)
    {
        if (at == Vector2.Zero)
            return;

        foreach (var had in _spoiled)
        {
            if (Vector2.Distance(had, at) < 1f)
                return;
        }

        _spoiled.Add(at);
    }

    /// <summary>The planner, so a run can read the chain it is working from without taking it.</summary>
    private Planning _planning;

    /// <summary>
    /// How many pulls at a scroll bar before deciding it is not going to work.
    ///
    /// **One.** It was six, on the reasoning that a bar might need more than one pull to bring a
    /// distant option into view - which is true, and irrelevant next to what actually happens when
    /// the first pull achieves nothing: the same drag, from the same point, by the same distance,
    /// five more times. A gesture that failed is not a gesture that needs repeating.
    ///
    /// A pull that WORKS and still leaves the option out of view would want another, and that is a
    /// different thing to build - measure the movement, and go again only if the list actually
    /// moved. Until that exists, once.
    /// </summary>
    private const int Scrolls = 1;

    private int _scrolls;

    public string Begin(GameController gc, AutoExpeditionSettings settings, Planning planning)
    {
        // A press is a new attempt, so the last one's warning comes down. See CursorWarning.
        CursorWarning.Clear();

        _game = gc;
        _planning = planning;
        _scrolls = 0;

        // **Not cleared here.** A press is an attempt, and an attempt answers nothing - the marks
        // exist to say "this did not work", and wiping them the moment somebody tries again removes
        // them exactly when they are being read. Only an explosive actually reaching the ground
        // settles the question, so only that clears them. See Step.Confirming.

        if (Walking(gc))
        {
            Say("Moving", "stand still and press again");

            return Status;
        }

        // Before anything asks what there is to do.
        //
        // A press gives up on whatever it could not reach, and the next press is entitled to try
        // again - the camera has usually turned or the player has moved. Clearing it further down
        // was a deadlock: the refusal below asks Chore whether there is anything to tidy, Chore
        // answered "nothing" because everything was still on the skip list from last time, and the
        // run ended at that refusal without ever reaching the line that empties it.
        _skip.Clear();

        if (!_input.Available)
            {
                Say("ExileInput2 not installed", "ExileInput2 is not installed, so nothing can be placed");

                return Status;
            }

        // Nothing to place is not nothing to do.
        //
        // These two refusals used to sit in front of everything, so a finished dig site full of
        // spent remnants answered F4 with "no explosives left" and stopped - the shatter pass was
        // written and unreachable, because the run could never start to get to it.
        var breaking = Chore(gc, settings) is { Of: not null } or { Waiting: > 0 };

        if (planning.Plan.Points.Count == 0 && !breaking)
            {
                Say("No plan", "there is no plan to place");

                return Status;
            }

        if (Detonator.ExplosivesInHand(gc) <= 0 && !breaking)
        {
            Say(Detonator.FormattedExplosivesPlacedOutOfTotal(gc, "Spent"), "no explosives left");

            return Status;
        }

        _plan = planning.Plan;
        _reach = planning.Reach;
        _landed = 0;
        _rewrote.Clear();
        _shattered = 0;

        // Nothing is half-picked at the start of a run. See Took.
        _picking = "";
        _picked = null;

        // Nor is a remnant being rewritten, or the reward it was rewritten for. Kept from an earlier run - on this
        // map or a previous one - they answered for the next window that opened, which then looked for that
        // remnant's reward among another remnant's options and stopped at "Choose". _wants is keyed by entity id,
        // and ids are reused from one area to the next. See Options.Pick.
        _change = null;
        _wanted = null;
        _wants.Clear();
        _clicked = DateTime.MinValue;

        // Unconditionally, before claiming. A stop is sticky until the holder hands the cursor
        // back - that is deliberate on ExileInput2's side, so that taking the mouse away stops the
        // automation until it is asked for again rather than the instant the hand comes to rest -
        // and releasing something not held costs nothing. Doing it only when the stop was visible
        // from here left the press that should have started a run spending itself on the tidying up
        // instead, which is what made it take two.
        _input.Release();

        if (!_input.Take())
            {
                Say("ExileInput2 busy", "another plugin is using the cursor");

                return Status;
            }

        Advance(gc, settings);

        return Status;
    }

    /// <summary>Steps the sequence. Called every tick; returns immediately when idle.</summary>
    public void Tick(GameController gc, AutoExpeditionSettings settings, Scan scan, Blast blast,
        Valuation valuation)
    {
        // Kept BEFORE the idle check, not after it.
        //
        // They used to be set below it, which meant they were still null on the frame a run
        // starts - Tick returns straight back out while idle, and Begin then runs Advance in the
        // same frame. Advance could not ask about rewards without them, so it placed the first
        // explosive of every run without checking whether the remnant under it was set to the
        // right thing. Every bomb after the first was checked, which is what made it look like an
        // ordering problem rather than a missing input.
        _game = gc;
        _scan = scan;
        _valuation = valuation;

        // Before anything else, including the idle check: an identity that outlives its window is
        // the one way this can answer for a window it did not open. See Opened.
        if (Opened != null && !Safe.Read(gc,
                static g => g.IngameState.IngameUi.Expedition2Window.IsVisible, false))
            Opened = null;

        // Standing still is a condition of the whole run, not just of starting one.
        //
        // Every click this makes is aimed at something whose position was read a frame or two ago -
        // a planned spot projected to the screen, a button on a label that hangs off an entity. A
        // walking character moves the camera, and the camera moves all of it, so a cursor sent to
        // where the button was lands where the button is not. Worse, a mis-aimed click in the world
        // is a movement command, which moves the character further.
        //
        // So the run stops the moment the player starts walking, and says so. It is the same
        // instinct as ExileInput2 dropping the lease when the mouse moves: the player doing
        // something is the signal that the plugin should not be.
        // Walking stops the run, EXCEPT while it is tidying up.
        //
        // Opening a chest is done by walking to it, so the run's own click starts the character
        // moving - and the gate then read that as the player taking over and stopped immediately
        // after every chest. The steps below are the ones where movement is the expected
        // consequence of what was just clicked rather than a sign that somebody else is driving.
        //
        // The combinations button is on that list for the same reason, arrived at the hard way. A
        // remnant slightly too far off has a button that is drawn and lights up under the cursor,
        // so every test the run had said "press it" - and the press walked the character, which
        // stopped the run. Walking to a button you just pressed is the game doing what you asked.
        if (_step != Step.Idle && _step != Step.Shattering && _step != Step.Shattered &&
            _step != Step.Arriving && _step != Step.Opening && _step != Step.Opened &&
            Walking(gc))
        {
            Give("Moving", "you started walking, so the run stopped");

            return;
        }

        if (_step == Step.Idle)
            return;

        // ExileInput2 gives up the moment the player touches the mouse, the game loses focus or a
        // move never lands. Any of those means stop, not retry.
        var stopped = _input.Stopped();

        if (!string.IsNullOrEmpty(stopped))
        {
            Give(stopped == "PlayerMovedTheMouse" ? "Stopped" : "Stopped",
                stopped == "PlayerMovedTheMouse" ? "you moved the mouse" : "stopped: " + stopped);

            return;
        }

        if (DateTime.UtcNow > _deadline)
        {
            // A tap that did not bring the circle up gets another go rather than ending the run.
            // The key is a TOGGLE, so a tap that arrived while the circle was already up takes it
            // away - and the fix for that is the same tap again.
            // Clearing goes the same way: Arm re-aims if the circle is up and taps again if it is
            // not, which is exactly what a half-finished toggle wants.
            if (_step == Step.Toggling || _step == Step.Clearing)
                Arm(gc, settings);
            else if (_step == Step.Confirming)
            {
                // **"Timeout while confirming" says when it gave up and nothing about what went
                // wrong.** Confirming watches one number - the game's count of placed explosives -
                // so it cannot tell a click that never went out from one the game received and
                // declined. A refused click now says so at the moment it is refused (see Sent), so
                // reaching here means the click WAS sent and the game did not place anything.
                //
                // What it can still say is the state the game was left in, which is the difference
                // between "the circle went down, so something happened" and "the circle is still
                // up and the count never moved".
                var placed = Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, -1);
                var circle = Safe.Read(() => Detonator.Showing(gc), false);

                // **Before blaming the spot, clear whatever might have taken the click.** Once per
                // run, because it costs two keypresses and a re-aim and fixes a cause that leaves no
                // other trace. See Step.Clearing and Cleared.
                if (_activeAtClick && !_cleared && Cleared(gc, settings))
                    return;

                if (_activeAtClick)
                    _declined++;

                // Two is enough, because the run only reaches here after every gate has passed -
                // the cursor on the spot, the indicator agreeing, and the markers lit. Two of those
                // in a row at different spots is the site, not the spots. See _declined.
                if (_activeAtClick && _declined >= 2)
                {
                    // **The bricked-site explanation has a signature, and it is checked now.**
                    //
                    // This used to assert it outright: several clicks refused meant a false
                    // "Expedition Complete" elsewhere in the map had made the site unusable and
                    // there was nothing to be done. Said on a site where the plugin's own counters
                    // read nought banners and nought untouched expeditions, and where the ring was
                    // green and the player could place by hand straight afterwards. Telling somebody
                    // their site is lost is the strongest thing this plugin says; it does not get to
                    // say it on evidence it is holding proof against.
                    //
                    // So the diagnosis needs its own evidence - a banner seen here AND an expedition
                    // in this map with nothing in it - and without that the report is what happened
                    // and nothing more. See Finished.Seen and Detonator.ExpeditionsNeverStarted.
                    var banners = Finished.Seen;
                    var idle = Safe.Read(() => Detonator.ExpeditionsNeverStarted(gc), 0);

                    Give(banners > 0 && idle > 0 ? "Site will not take explosives" : "Clicks not taking",
                        $"{_declined} clicks in a row went out with placement mode on and the game " +
                        $"placed nothing - it still reports {placed} placed of " +
                        $"{Safe.Read(() => Detonator.ExplosivesToPlanFor(gc), -1)}, with " +
                        $"{Safe.Read(() => Detonator.ExplosivesInHand(gc), -1)} in hand. " + Clicked(gc) +
                        (banners > 0 && idle > 0
                            ? $" This map has raised {banners} \"Expedition Complete\" banner" +
                              (banners == 1 ? "" : "s") + $" with {idle} expedition" +
                              (idle == 1 ? "" : "s") + " still untouched, which is the signature of " +
                              "the bug that leaves a site unable to accept anything - rejoining the " +
                              "instance does not clear it, and nothing here can fix it."
                            : " No banner has been raised here and no expedition in this map is " +
                              "untouched, so this is NOT the bug that bricks a site. Try the spot by " +
                              "hand: if it takes, the fault is in this run rather than in the site."));

                    return;
                }

                Give("Placement failure",
                    $"the click went out and the game placed nothing - it still reports {placed} " +
                    $"placed, was {_placedWhenStarted} before the click. " + Clicked(gc) +
                    " Now the circle " +
                    (circle ? "is still up" : "has gone down") +
                    $"; {Safe.Read(() => Detonator.ExplosivesInHand(gc), -1)} explosives remain - " +
                    (_activeAtClick
                        ? "the game may have refused the spot, so press again to replan around it" +
                          (_cleared
                              ? " - and toggling placement mode to clear the cursor was already " +
                                "tried, so whatever took the click is not a tooltip"
                              : "")
                        : "placement mode was not on, so the click could not place anything - the " +
                          "toggle key may not have taken"));
            }
            else
                Give("Timeout", $"gave up waiting while {_step.ToString().ToLowerInvariant()}");

            return;
        }

        // The window opening interrupts whatever was happening, from wherever it happened. Placing
        // an explosive over an undecided remnant makes the game put it up and wait, and until it is
        // dealt with nothing else can proceed - so it is handled here rather than as one more case
        // in the sequence below.
        // **Scrolling is excluded for the same reason Choosing is: it is already dealing with this
        // window.** The branch exists so an opening window interrupts whatever was happening, and a
        // step that came out of that interruption must not be interrupted by it in turn. Without
        // Scrolling here, every tick of a drag started another one - each cancelling the last,
        // releasing the button and restarting the journey - so six pulls were spent in a few frames
        // and the cursor never reached the bar at all.
        if (_step != Step.Choosing && _step != Step.Chose && _step != Step.Scrolling &&
            Options.Open(gc))
        {
            // How long the game took to put up the next remnant's window after a reward was taken. See AfterPick.
            if (_step == Step.AwaitingWindow)
                NextWindowSaid = $"the window for the next remnant came up {(DateTime.UtcNow - _awaitingSince).TotalMilliseconds:0}ms " +
                                 $"after the last reward took ({DateTime.Now:HH:mm:ss})";

            Choose(gc, settings, valuation, scan);

            return;
        }

        switch (_step)
        {
            case Step.Toggling:
                if (Detonator.Showing(gc))
                    Aim(gc, settings);

                return;

            case Step.Clearing:
                // Off first, which is what drops the tooltip, then straight back on through the
                // ordinary path so the circle is waited for rather than assumed.
                if (!Detonator.Placing(gc))
                {
                    if (Sent(_input.Tap(_key), $"the {_key} keypress bringing placement mode back"))
                        Wait(Step.Toggling, Tapped);
                }

                return;

            case Step.Moving:
                if (_input.Arrived())
                    Wait(Step.Checking);

                return;

            case Step.Checking:
                Check(gc, settings, scan);

                return;

            case Step.Scrolling:
                // Wait for the drag to finish, then ask again: the option may now be reachable, may
                // need another pull, or may have turned out to be unreachable after all. Choose
                // decides, which keeps one place making that judgement.
                if (_input.Dragging())
                    return;

                // The cursor arrived somewhere that was not the thumb, so nothing was pressed.
                // Aiming again at the same numbers would land in the same place, so the run hands
                // it back rather than spending its remaining pulls on the same mistake.
                if (_input.DragMissed())
                {
                    Give("Failed to scroll", "the scroll bar is not where it was read to be - nothing " +
                                          "was clicked; scroll to the reward and press again");

                    return;
                }

                Choose(gc, settings, _valuation, _scan);

                return;

            case Step.Opening:
                if (_input.Arrived())
                {
                    // The game says whether the cursor is on the button, so there is no need to
                    // trust the path: hovering a button lights it up, and an element that has not
                    // lit up is not the button whatever the child index says. A click sent blind
                    // here lands in the world, which moves the character.
                    // **Something else's name over the button, which blocks the click and not the
                    // blast.** So it is checked here rather than anywhere the geometry is: the ring
                    // was green, the spot is good, and the click would land on a Verisium Sentry's
                    // label. Toggle Highlighting takes every label off the screen, so the plugin
                    // taps it, clicks, and taps it back. See AutomationSettings.Unhide.
                    if (Hiding(gc, settings))
                    {
                        Wait(Step.Opening, Patience);
                        Say("Hiding labels", "something else's name is over the button - taking the " +
                                             "ground labels off the screen and trying again");

                        return;
                    }

                    // Still settling from that. Reading the highlight now would read the screen as
                    // it was before the key landed, which is the fault this whole pause exists for.
                    if (_hidden && DateTime.UtcNow - _hidAt < Hides)
                        return;

                    if (!Lit(_change, CombinationsPath))
                    {
                        // **Ask the game what owns this point, then aim past it.** A button that
                        // does not light up has two causes and they want opposite responses: the
                        // path is wrong, which is fatal, or something is drawn over the exact pixel
                        // the cursor is on, which is not. A rare monster's health bar is the case
                        // that turns up - 558 pixels wide across the top half of a 54 pixel button
                        // - and no key hides one, so Toggle Highlighting was never going to help.
                        //
                        // UIHover only answers about where the cursor already is, which is why this
                        // lives here rather than at aiming time: by now the cursor IS there, so the
                        // client will name the obstruction and its rectangle can be subtracted from
                        // the button. See Panels.Hovered and Panels.Clearest.
                        if (Dodged(gc))
                        {
                            Wait(Step.Opening, Arriving);
                            Say("Aiming again", "something is drawn over the button - moving to the " +
                                                "part of it that is clear");

                            return;
                        }

                        Showing(settings);

                        Give("Failed to click", "the combinations button did not light up under the " +
                                             "cursor - " + Panels.Describe(gc) +
                                             " is over it, or the element path is wrong; pick the " +
                                             "combination yourself and press again");

                        return;
                    }

                    if (!Sent(_input.Click(), "the click opening the combinations window"))
                        return;

                    Showing(settings);

                    // Long enough for a short walk and no longer - see Opens. It is cut short
                    // the moment it is clear no walk is happening at all, see Step.Opened.
                    Wait(Step.Opened, Opens);
                    Standing(gc);
                    Say("Opening", "opening the combinations window");
                }

                return;

            case Step.Opened:
                // Waiting, but not blindly.
                //
                // **Twenty seconds of a word that never changes reads as nothing happening at all.**
                // The long allowance is for the case where the click was out of range and the game
                // walks the character over; when the click simply missed, the window never opens and
                // the run sat there for twenty seconds saying "Opening" - long enough to wander off,
                // stop the automation by moving, and never work out what had gone wrong.
                //
                // So the allowance is spent only while it is being used. A walk moves the character;
                // if nothing has moved for the ordinary patience, there is no walk and the click
                // missed, which is an answer rather than something to keep waiting for.
                if (Moving(gc))
                {
                    Say("Opening", "walking to the remnant to open the combinations window - " +
                                   $"{Left():0.0}s left");

                    return;
                }

                if (DateTime.UtcNow - _stood > Standstill)
                {
                    Give("Window did not open", "the combinations button was clicked and nothing " +
                                                "opened, and the character is not walking - pick " +
                                                "the combination yourself and press again");

                    return;
                }

                // The count is the point: a number going down is the difference between waiting and
                // hanging, and it is what was missing when this went unnoticed.
                Say("Opening", $"opening the combinations window - {Left():0.0}s");

                return;

            case Step.Shattering:
                if (_input.Arrived())
                {
                    // What the game says is under the cursor - the last word, after the aiming
                    // has already worked out where the label is exposed. A disagreement is not
                    // worth stopping the run for: something is in front of this one and the next
                    // may be clear, so it gives up on this one and moves on.
                    if (!Under(gc))
                    {
                        // Said out loud, because the alternative is a run that quietly does
                        // nothing and a player with no way to tell why.
                        Say("Tidying", "the cursor is on " +
                                       Describe(Safe.Read(gc, static g => g.IngameState.UIHover, null)) +
                                       $", {_input.Drift():0.#} pixels off target - skipping that one");

                        _skip.Add(Safe.Read(_chore, static e => e.Id, 0u));
                        Advance(gc, settings);

                        return;
                    }

                    if (!Sent(_input.Click(), "the click shattering the remnant"))
                        return;

                    Wait(Step.Shattered);
                }

                return;

            case Step.Arriving:
                if (!Walking(gc))
                    Advance(gc, settings);

                return;

            case Step.Shattered:
                // The label going away - or the chest reporting itself open - is the game accepting
                // it. Then straight on to the nearest remaining job, so a finished dig site clears
                // in one press.
                if (_chore == null ||
                    Safe.Read(_chore, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false) ||
                    Button(gc, _chore, _path).Rect.Width <= 0f)
                {
                    _shattered++;
                    Advance(gc, settings);
                }

                return;

            case Step.Choosing:
                if (!Options.Open(gc))
                {
                    Advance(gc, settings);
                }
                else if (_input.Arrived())
                {
                    // No UI check here, and that is not an oversight: this click is MEANT for the
                    // interface. The window is open and the cursor is on one of its options.
                    if (!Sent(_input.Click(), "the click taking the reward"))
                        return;

                    Settling();
                    _clicked = DateTime.UtcNow;
                    Wait(Step.Chose);
                    Say("Choosing", "taking the reward");
                }

                return;

            case Step.Chose:
                // **The window going away is the CLICK landing, not the pick taking.**
                //
                // Picking is a server round trip. The window closes on the click and the remnant
                // goes on reporting its old combination until the server answers, so a run that
                // took the window closing as the answer walked straight back to the same button and
                // picked the same reward a second time. Seen in game, twice on one remnant.
                //
                // So the window closing only starts the question. The answer is the remnant reading
                // back what was clicked - see Took - and until it does, this waits.
                if (Options.Open(gc))
                    return;

                // Crossed off either way. Whether the pick took is a question about the server;
                // whether this run visits the remnant again is a question about this run, and the
                // answer to that is no - a second pick is worse than a missed one, because it
                // spends a reward the player has already been given.
                if (Took(gc) || DateTime.UtcNow - _clicked >= Answers)
                {
                    // Remembered as the run's own choice, so it is not taken for the player's. See SetByPlacement.
                    if (Took(gc) && _picked != null)
                        RecordSetByPlacement(Safe.Read(_picked, static e => e.GridPos, Vector2.Zero), _pickingRecipe);

                    Picked = (_picking, Took(gc),
                        (DateTime.UtcNow - _clicked).TotalMilliseconds, DateTime.UtcNow);

                    // The rewrite this pick was for is done, so it must not answer for the next window, which an
                    // explosive landing on another remnant opens. See Begin.
                    _change = null;
                    _wanted = null;

                    Settling();
                    AfterPick(gc, settings);
                }
                else
                {
                    Say("Choosing", _picking.Length > 0
                        ? $"waiting for {_picking} to take"
                        : "waiting for the reward to take");
                }

                return;

            case Step.AwaitingWindow:
                // The window opening is handled above, as every window is. What is left here is it not
                // opening in time: move on, and say which remnant was left without a reward.
                if (DateTime.UtcNow - _awaitingSince >= NextWindowWithin)
                {
                    NextWindowSaid = $"no window came up within {NextWindowWithin.TotalMilliseconds:0}ms for the " +
                                     $"remnant at ({_awaitedAt.X:0},{_awaitedAt.Y:0}) - it was left without a reward " +
                                     $"({DateTime.Now:HH:mm:ss})";
                    Advance(gc, settings);
                }

                return;

            case Step.Confirming:
                if (Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0) > _placedWhenStarted)
                {
                    _landed++;
                    _justPlaced = _expect;
                    _declined = 0;
                    _armed = false;
                    _unlit = Array.Empty<Vector2>();
                    _spoiled.Clear();
                    Reason = "";
                    Advance(gc, settings);
                }

                return;
        }
    }

    /// <summary>
    /// Moves on to the next planned spot, or finishes.
    ///
    /// Called to start the run and again after each explosive lands, so the run carries itself:
    /// one press places everything on screen rather than one thing. Every way the run can end ends
    /// here, and each ending says which it was.
    /// </summary>
    private void Advance(GameController gc, AutoExpeditionSettings settings)
    {
        // Nothing left to place is not the same as nothing left to do: a remnant that has been
        // blown and cleared offers a shatter button, and the chain is usually standing among
        // several of them. Nearest first, so the run works outwards from where you are rather than
        // marching back and forth across the dig site.
        //
        // **Or something nearer than the next spot, which is how a lone remnant gets its turn.**
        //
        // Maps hold remnants attached to no expedition at all - see Scan.Loose - and the whole of
        // the job at one is to pick a reward and shatter it. The chore flow already knew how; it was
        // only ever reached when placement had nothing left to do, so a press beside a lone remnant
        // went to a dig site across the map instead, refused for being off screen, and read as the
        // key doing nothing.
        //
        // Nearer than the next planned spot is the whole test, and it needs no threshold: a dig
        // site's own remnants are not loose, and a loose one is by definition further from the
        // detonator than a site reaches - so this can only fire when you are genuinely standing
        // closer to the lone thing than to the chain.
        if (Detonator.ExplosivesInHand(gc) <= 0 || Next(gc, _plan) < 0 || Closer(gc, settings))
        {
            // Still walking towards the last one. Choosing the next while moving would aim the
            // cursor at a label that is sliding across the screen, so it waits instead.
            if (Walking(gc))
            {
                Wait(Step.Arriving, Arriving);
                Say("Tidying", "walking to it");

                return;
            }

            var todo = Chore(gc, settings);

            if (todo.Of != null)
            {
                _chore = todo.Of;
                _path = todo.Path;

                // **A chore is not a rewrite, and the two cannot both be live.** Choose now asks
                // which remnant the run is acting on, so a _change left over from an earlier
                // placement would answer for a window opened by a chore - the very confusion of
                // identity this pair of fields was just taught to avoid.
                _change = null;
                _wanted = _wants.TryGetValue(Safe.Read(todo.Of, static e => e.Id, 0u), out var owed)
                    ? owed
                    : null;

                // Only a chore that opens the combinations window names a remnant; a chest or a
                // shatter button is not one, and must not leave an identity behind for the next
                // window that happens to open.
                Opened = todo.Path == CombinationsPath
                    ? Remnant(Safe.Read(todo.Of, static e => e.Id, 0u))
                    : null;
                // As tight as the target is, and no tighter.
                //
                // The tolerance is how far from the requested point the cursor may settle and
                // still count as arrived. Twelve is right for a label nothing is covering - the
                // cursor lands somewhere on it whatever it does - and hopeless for a six pixel
                // sliver, where arriving twelve pixels out means arriving on the label behind.
                //
                // A fixed two was the other mistake: the cursor does not land on a sub-pixel, so a
                // tolerance below what it can actually achieve means it never reports arrival at
                // all, the step times out, and nothing is clicked. Hence the floor.
                var spot = Exposed(gc, Panels.Covered(gc), todo.At,
                    Safe.Read(todo.Of, static e => e.Id, 0u));

                // Inside the gap, not merely near it. Arrival is judged against this number, so a
                // tolerance equal to the room available lets the cursor report success from just
                // outside the sliver it was aimed at - which is how a chest with a seven pixel gap
                // ended up hovering the label behind it and being written off as covered.
                _input.SetTolerance((int)Math.Clamp(spot.Room - 1f, Closest, 12f));

                if (!Sent(_input.MoveTo(spot.At), "the move to " + todo.Word))
                    return;

                Wait(Step.Shattering);
                Say("Tidying", todo.Word);

                return;
            }

            if (todo.Waiting > 0)
            {
                Give(Obscured, $"{todo.Waiting} thing{(todo.Waiting == 1 ? " is" : "s are")} ready " +
                               "to be dealt with and not on screen - walk towards them and press again");

                return;
            }
        }

        if (Detonator.ExplosivesInHand(gc) <= 0)
        {
            Give(Detonator.FormattedExplosivesPlacedOutOfTotal(gc, "Spent"), _shattered > 0
                ? $"out of explosives, {_shattered} thing{(_shattered == 1 ? "" : "s")} dealt with"
                : "out of explosives");

            return;
        }

        var index = Next(gc, _plan);

        if (index < 0)
        {
            Give("Ready", _shattered > 0
                ? $"the plan is placed and {_shattered} thing{(_shattered == 1 ? "" : "s")} " +
                  "dealt with - detonate when you are ready"
                : "the plan is placed - detonate when you are ready");

            return;
        }

        var next = _plan.Points[index];

        // A remnant this blast would set off, currently set to the wrong combination.
        //
        // Placing first and fixing afterwards is not an option: an explosive on a remnant that has
        // already been decided just detonates it with whatever was ticked. So the change has to
        // happen before the bomb, and if it cannot happen the bomb must not either - hence this
        // sitting above every other check, and hence it refusing rather than settling for the
        // wrong reward.
        if (Rewrite(gc, settings, index, out var wanted) is { } change)
        {
            var button = Combinations(gc, change);
            if (button.Width <= 0f || !OnScreen(gc, button.Center) ||
                Panels.Covers(Panels.Covered(gc), button.Center))
            {
                // Its own words, because it is its own problem: the button only appears on a
                // remnant you are standing near, so this means walk to THAT remnant - not the
                // same thing as loot being hidden behind something.
                Give("Too far from runes button",
                    "the remnant at this spot is set to the wrong combination and its button is " +
                    "not on screen - walk up to it and press again");

                return;
            }

            _change = change;
            _wanted = wanted;
            _chore = null;
            _dodged = 0;
            Opened = change;
            _input.SetTolerance(12);

            // **Aim at the part of the button nothing is drawn over.** A rare monster's health bar
            // sits across the top of it often enough to matter - measured on one dump, an ornament
            // 558 wide covering the upper half of a 54 pixel button - and the game will not light a
            // button whose hovered point belongs to something else. The blast does not care, the
            // ring stays green, and the run stops saying the element path must be wrong.
            //
            // Nothing to do with ground labels, which is what Toggle Highlighting was added for: a
            // health bar is not one and no key hides it. See Panels.Clearest.
            if (!Sent(_input.MoveTo(Aiming(gc, button)),
                    "the move to the combinations button"))
                return;

            Wait(Step.Opening, Arriving);
            Say("Opening", "changing the remnant's combination before placing");

            return;
        }

        // A chain that has gone somewhere the plan did not expect cannot be fixed by aiming better,
        // and this is the honest thing to say about it.
        var origin = Detonator.LastExplosiveGridPosition(gc);
        var out_ = Vector2.Distance(origin, next);

        if (_reach > 0f && out_ > _reach + 2f)
        {
            Give("Deviated", $"the chain has left the plan - the next planned spot is {out_:0} grid " +
                             $"out and the chain's range is {_reach:0}; re-plan from the placement button");

            return;
        }

        // Everything that makes a spot unusable is settled BEFORE the placement key is touched.
        //
        // Priming the game and then discovering the spot is off screen leaves the circle up, the
        // cursor where it was, and nothing to show for the key press - which is what "it pushed V
        // and then did not move" was. The key is only for a spot this is going to aim at.
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var at = Screen(gc, camera, next);

        // **Edge, the same margin the ring is coloured by.** These are two tests of one question -
        // can this spot be clicked from where you are standing - and while they used different
        // numbers the ring said yes at a spot the run then refused for being off screen. A readout
        // that promises something the very next guard denies is worse than no readout. See
        // OnScreen's margin, and Ready.
        if (at == Vector2.Zero || !OnScreen(gc, at, Edge))
        {
            Give("Walk closer", $"the next spot is {out_:0} grid out and off screen - " +
                                "walk towards it and press again");

            return;
        }

        if (Panels.Covers(Panels.Covered(gc), at))
        {
            Give("Unclickable", "the next spot is behind a panel - move the camera and press again");

            return;
        }

        _target = next;
        _expect = _plan.CaughtBy(index);
        _arms = 0;
        _short = 0;
        _offSpotFrames = 0;
        _aimedGridPosition = Vector2.Zero;

        // Per spot, so each gets one attempt at clearing the cursor and no spot gets two. See Cleared.
        _cleared = false;
        _placedWhenStarted = Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0);

        Arm(gc, settings);
    }

    /// <summary>
    /// Makes sure the game is in placement mode, then aims.
    ///
    /// Checked before every explosive rather than once at the start, because the game leaves
    /// placement mode when an explosive goes down. Arming once and assuming it held is why the
    /// second spot of a run was being aimed at with no indicator and no green circle - and with no
    /// circle there is nothing lighting up, so the coverage check could never pass and the click
    /// had nothing to place.
    ///
    /// The test is the same one the player uses: if the circle is not showing, press the key. If it
    /// is showing, it stays showing while the cursor moves, so there is nothing to do.
    /// </summary>
    /// <summary>
    /// Toggles placement mode off and on to take the click back off whatever had it.
    ///
    /// **A tooltip can take the click, and nothing else in this run can see that it did.** Reported
    /// from a live site: the cursor arrived on a planned spot over a Vaal Relic's ground tooltip,
    /// whose underlined keywords are clickable in their own right, and the game gave the click to
    /// the keyword instead of to the explosive. Every gate had passed - placement mode on, the
    /// circle up, the indicator on the spot, the markers lit - and IngameState.UIHover named
    /// nothing, so Panels.UnderCursor had nothing to refuse. The run then read the game as declining
    /// a spot the player could place on by hand a second later.
    ///
    /// Toggling the mode clears it, which is what a player does by reflex. Two keypresses and a
    /// re-aim against a run that would otherwise end.
    ///
    /// **Once per run, and that is the whole of the loop protection.** If the click is being eaten
    /// by something a toggle does not shift, trying again forever is a cursor that will not give up
    /// and an explosive that never goes down. One attempt, and then the ordinary report - which now
    /// says the toggle was tried, so the next person knows it is not that.
    /// </summary>
    private bool Cleared(GameController gc, AutoExpeditionSettings settings)
    {
        var key = Safe.Read(() => settings.ToggleKey.Value.Key, Keys.V);

        if (!Detonator.Placing(gc))
            return false;

        if (!Sent(_input.Tap(key), $"the {key} keypress clearing the cursor"))
            return false;

        _cleared = true;
        _key = key;
        Wait(Step.Clearing, Tapped);
        Say("Clearing", "the click went nowhere - something under the cursor took it, so placement " +
                        $"mode is being toggled with {key} to take it back");

        return true;
    }

    private void Arm(GameController gc, AutoExpeditionSettings settings)
    {
        var fromLast = FromLastExplosive(gc);

        if (Detonator.Showing(gc))
        {
            Aim(gc, settings);

            return;
        }

        // Too far from the last explosive, the game will not enter placement mode at all. See PlacementModeRange.
        if (fromLast >= PlacementModeRange)
        {
            Give("Too far", $"you are {fromLast:0} grid from the last explosive - the game will not show the " +
                            $"placement circle from {PlacementModeRange:0} or more; walk closer");

            return;
        }

        if (++_arms > 4)
        {
            Give("Placement key not working", $"the placement circle would not come up ({fromLast:0} grid from the last explosive)");

            return;
        }

        // Before the key, not after it. Arming for a spot that cannot be aimed at only flashes the
        // circle on and straight back off again.
        if (!Reachable(gc, out _, out var word, out var why))
        {
            Give(word, why);

            return;
        }

        // The game's own toggle, tapped rather than clicked. The detonator element does carry a
        // toggle button, but a key is one send against a move plus a click, and it cannot miss.
        var key = Safe.Read(() => settings.ToggleKey.Value.Key, Keys.V);

        // A refused tap leaves the circle down and the run waiting for it to appear, which reads
        // as the game ignoring the key rather than as the key never being sent. See Sent.
        if (!Sent(_input.Tap(key), $"the {key} keypress showing the placement circle"))
            return;

        _armed = true;
        _key = key;
        Wait(Step.Toggling, Tapped);
        Say("Arming", $"showing the placement circle with {key}");
    }

    /// <summary>
    /// How far the player stands from the last explosive down, or from the detonator before any is, in grid.
    /// </summary>
    private static float FromLastExplosive(GameController gc)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var last = Detonator.LastExplosiveGridPosition(gc);

        return player == Vector2.Zero || last == Vector2.Zero ? 0f : Vector2.Distance(player, last);
    }

    /// <summary>
    /// How near the last explosive - or the detonator, before any is down - the player has to stand for the game
    /// to enter placement mode, in grid. The placement key does nothing from this far or further.
    ///
    /// Measured by hand on Scorched Cay (Grand, reach 155, 2026-09-29): refused at 181 and 191, and 178 was
    /// about the limit, so under 180 is the rule taken. Not yet known whether it scales with the map's
    /// placement-distance modifier (MapExpeditionMaximumPlacementDistancePct, 44 on that map) or with the
    /// reach; a site with a different reach will say.
    /// </summary>
    internal const float PlacementModeRange = 180f;

    /// <summary>The player's distance from the last explosive against PlacementModeRange, for the dump.</summary>
    internal static string PlacementReach(GameController gc) =>
        $"you are {FromLastExplosive(gc):0} grid from the last explosive; placement mode needs under {PlacementModeRange:0}";

    /// <summary>
    /// Sends the cursor to the richest option in the combinations window.
    ///
    /// Stops the run rather than guessing when there is nothing to go on: an unpriced window means
    /// every option reads as worth nothing, and the one thing worse than waiting for the player is
    /// spending their remnant on whichever option happened to be first.
    /// </summary>
    private void Choose(GameController gc, AutoExpeditionSettings settings, Valuation valuation,
        Scan scan)
    {
        if (!settings.Automation.On(settings.Automation.PreExpedition.ChooseRewards))
        {
            Give("Panel failure, choose manually", "the combinations window is open - pick a reward and press again");

            return;
        }

        var (rect, value, name, recipe, byName, index) =
            Options.Pick(gc, settings, valuation, scan, _plan, _change, _wanted);

        // **Nothing established, so nothing clicked.**
        //
        // Either the window could not be attributed to a remnant, or it was and the reward the run
        // decided on is not among the options it offers. Both mean the plugin has been shown that
        // it does not understand this window, and the only irreversible action available is to
        // spend the remnant. So the run ends and says which of the two it was. See Options.Unknown.
        if (index == Options.Unknown)
        {
            Give("Panel failure, choose manually", Options.Whose(gc, scan) == null
                ? "the combinations window is open and the plugin cannot tell which remnant it " +
                  "belongs to - pick a reward yourself, or close it and let the run open it"
                : $"the combinations window does not offer {name}, which is what was decided for " +
                  "this remnant - its reward list may have gone stale, so press again");

            return;
        }

        // The reward is decided and the window is still drawing itself. Come back next frame; the
        // run's own deadline ends it if the options never become clickable.
        if (index == Options.Waiting)
        {
            Say("Choosing", $"waiting for the window to settle before taking {name}");

            return;
        }

        // **Found, priced, and off the page.** The window shows a handful of combinations at a time
        // and a six socket remnant offers nineteen, so the one worth taking is routinely scrolled
        // out of sight - and an option keeps its rectangle when it is, which is how the cursor came
        // to be walked to a point outside the window and clicked there.
        //
        // Handed back rather than waited on: the geometry is not going to change on its own, and
        // the plugin has no way to scroll the window. See Options.Hidden.
        if (index == Options.Hidden)
        {
            // **Drag the bar to it rather than giving up.** The window shows a handful of a
            // remnant's nineteen combinations and the one worth taking is routinely out of sight;
            // the thumb is the only way to bring it into view, since there is no wheel to send.
            //
            // Bounded, because a drag that does not move the list is a drag that never will: the
            // thumb may be at the end of its travel, the arithmetic may be wrong, or the panel may
            // not be what it looks like. A handful of attempts, then the honest refusal.
            var (from, to, needed) = Options.Scrolling(gc, Options.Scrollable);

            // Each reason recorded separately. "The drag did not happen" has four causes and the
            // refusal named none of them, so the same message covered a missing bridge method, a
            // lease nobody holds, arithmetic that found nothing to do, and a bar that has been
            // pulled six times already.
            Reason = $"drag: wanted {needed}, pulls {_scrolls} of {Scrolls}, " +
                     $"method {(_input.CanDrag ? "bound" : "MISSING")}, " +
                     $"from ({from.X:0},{from.Y:0}) to ({to.X:0},{to.Y:0}); " +
                     $"last drag: {_input.DragTrace()}";

            if (needed && _scrolls < Scrolls && _input.Drag(from, to))
            {
                _scrolls++;

                Wait(Step.Scrolling, Arriving);
                Say("Scrolling", $"dragging the list to bring {name} into view");

                return;
            }

            // **Unreachable and nothing to scroll is not a scrolling problem.**
            //
            // "Scroll for it" told the player to scroll down to a combination sitting in front of
            // them on a window with no scroll bar - advice that cannot be followed, about a state
            // that cannot be true. Scrolling says the list fits, so the option was judged off the
            // page by geometry that had not settled, and the answer is another frame rather than a
            // refusal. The run's own deadline still ends it if it never settles.
            if (!needed)
            {
                Say("Choosing", $"waiting for the window to settle before taking {name}");

                return;
            }

            Give("Failed to scroll", $"the reward worth taking is {name}, and it is scrolled out of " +
                                  "sight in the combinations window - " +
                                  (_input.CanDrag
                                      ? "scroll down to it and press again, or pick it yourself"
                                      : "this build of ExileInput2 has no Drag, so it cannot be " +
                                        "scrolled for you - reload ExileInput2, or scroll down to " +
                                        "it and press again") + $" [{Reason}]");

            return;
        }

        if (rect.Width <= 0f)
        {
            Give("Panel failure, choose manually", "the combinations window is open, nothing in it is priced and none of it " +
                           "matches the preference list - pick a reward yourself and press again");

            return;
        }

        // What is about to be clicked, and for which remnant, so Chose can tell whether it took
        // rather than assuming it did. See _picking.
        _picking = name ?? "";
        _pickingRecipe = recipe ?? "";
        _picked = _change?.Entity ?? _chore;

        _input.SetTolerance(12);
        _input.MoveTo(rect);
        Wait(Step.Choosing);
        Say("Choosing", byName
            ? $"nothing in the window is priced, so taking {name} from the preference list"
            : $"moving to the best reward, {name}, worth {value:0.##}ex with what it carries");
    }

    /// <summary>
    /// Whether this is a lone remnant waiting on a decision.
    ///
    /// **Loose only, and that restriction is what keeps the ordinary flow intact.** A dig site's own
    /// remnants are undecided too, and the place their rewards get chosen is inside the placement
    /// run - Rewrite presses this same button before putting an explosive on one, so that the bomb
    /// and the choice cannot disagree. Treating those as chores would mean the first press at a dig
    /// site opened a remnant window instead of solving the site.
    ///
    /// Spent ones are excluded because their reward has already been used, and decided ones because
    /// there is nothing left to ask.
    /// </summary>
    private bool Wants(GameController gc, AutoExpeditionSettings settings, Target target,
        out Reward want)
    {
        want = null;

        if (target.Kind != TargetKind.Remnant || target.Entity == null || target.Spent ||
            _valuation == null || _scan == null || !_scan.Loose(gc, target.Grid))
            return false;

        // Just clicked, and the game has not said so yet. See _settling.
        if (_settling.TryGetValue(Safe.Read(() => target.Entity.Id, 0u), out var when) &&
            DateTime.UtcNow - when < Settles)
            return false;

        var chosen = Safe.Read(() => _valuation.ChosenName(target.Entity), null);

        // Nothing picked yet, which is the ordinary state of a remnant standing on its own.
        if (Undecided(chosen))
            return true;

        // **Something is picked and it is the wrong thing**, which is the same job the placement
        // run does before putting an explosive on a site's remnant - see Rewrite, whose comparison
        // this is. A lone remnant has no explosive coming, so nothing was doing it.
        //
        // Behind Overrule, because that switch is the whole of the player's answer to "may the
        // plugin change a reward I have already chosen". With it off the disagreement is shown
        // instead - in red, above the planner's pick - and nothing is touched.
        //
        // A rolled remnant is left alone whatever the setting says: its combination cannot be
        // changed, the window opens anyway, and the entries look clickable while doing nothing.
        if (!settings.Rewards.Overrule || target.Rerolled || target.Rewards.Count == 0)
            return false;

        // **What the plan was scored with, not a fresh answer at click time.** Deciding again here is
        // how the run came to tick a reward the plan had not chosen - and this is the one caller where
        // that is not a display fault but a wrong click. See Options.Solved.
        var mine = Options.Solved(target);

        var wants = target.Rewards[mine >= 0
            ? mine
            : Options.Take(target.Rewards,
                Options.Carried(settings, _valuation, target, gc, _scan, _plan),
                Options.Locally(settings, _valuation, target, gc, _scan, _plan),
                Options.Money(settings))];

        if (Safe.Read(() => _valuation.AlreadySetTo(target.Entity, wants), false))
            return false;

        want = wants;

        return true;
    }

    /// <summary>
    /// Whether a remnant's chosen-combination reading means "nothing has been picked".
    ///
    /// **Three spellings for one answer, and only two of them were being read.** ChosenName returns
    /// null when the remnant cannot be read at all, an empty string when it can, and a literal "?"
    /// - Valuation.Unnamed - for a recipe with no description and no id, which is what an undecided
    /// remnant comes back as. So a lone remnant with nothing picked read as decided, the chore pass
    /// skipped it, and the key reported "no plan to place" with the thing it was meant to act on
    /// twenty five grid away.
    ///
    /// Named here rather than tested inline because Valuation already has the same three-way
    /// reading in two places, and a fourth copy spelled differently is how this came about.
    /// </summary>
    private static bool Undecided(string chosen) =>
        string.IsNullOrWhiteSpace(chosen) || chosen == Valuation.Unnamed;

    /// <summary>
    /// Whether a lone remnant near you is waiting on a decision, for the key to give way to.
    ///
    /// The key asks "is there a chain to plan" before anything else, and answers it across the whole
    /// map - so standing next to a remnant belonging to nothing, with explosives still unplaced at a
    /// dig site elsewhere, F4 went off to solve that site and reported "No route". The thing in
    /// front of you is the thing you meant.
    /// </summary>
    public bool Waiting(GameController gc, AutoExpeditionSettings settings)
    {
        if (!settings.Automation.On(settings.Automation.PreExpedition.ChooseRewards) || _scan == null)
            return false;

        // **The game's own answer to "is there something to place here", and it outranks ours.**
        // While the placement button is up there is a dig site in front of you with explosives to
        // put down, and the key belongs to planning that - a lone remnant standing nearby does not
        // change what you came to do. It is only when the client stops offering the tool that a
        // remnant needing a decision is the most useful thing the key can reach.
        if (Detonator.Placeable(gc))
            return false;

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (player == Vector2.Zero)
            return false;

        foreach (var target in _scan.Targets)
        {
            if (Wants(gc, settings, target, out _) && Vector2.Distance(player, target.Grid) <= Near)
                return true;
        }

        return false;
    }

    /// <summary>How close a lone remnant has to be to take the key's attention, in grid units.</summary>
    private const float Near = 120f;

    /// <summary>
    /// Whether pressing the key right now would actually do something at this spot.
    ///
    /// **Green used to mean "this is the next one", which is a different claim.** The next link is
    /// next whatever the game is doing, so the ring said go at moments when nothing would happen and
    /// the only way to find out was to press the key.
    ///
    /// **What has to be true is everything the press will do, not its first step.** A press runs
    /// the whole sequence without stopping, so both of these have to hold:
    ///
    /// - If the blast catches a remnant whose reward is unchosen or wrong, that remnant's Runeshape
    ///   Combinations BUTTON has to be clickable, because settling the reward comes first.
    /// - The ground has to be aimable: the point the cursor must go to for this spot - see Pointed,
    ///   since it is that point rather than the spot that gets clicked - is on screen, ahead of the
    ///   camera, and not behind a panel or anything else the interface paints.
    ///
    /// Only the button used to be asked when a reward was owed, on the reasoning that the ground
    /// was not the question yet. It is: the press does not stop once the reward is taken, so a ring
    /// that promised half a press was green for something that could not finish.
    ///
    /// Asking one global question instead - is any remnant anywhere waiting - was wrong in both
    /// directions at once: a remnant across the site with an unmade choice held the ring yellow for
    /// a blast that had nothing to do with it, and a blast that did need one went green as long as
    /// the window happened to be reachable from somewhere else.
    ///
    /// Range is deliberately not a test. A link out of reach is a fault in the plan rather than a
    /// thing to wait for, and the chain drawing already has its own way of saying so.
    /// </summary>
    /// <param name="caught">The markers this link catches, from Plan.CaughtBy.</param>
    /// <param name="from">
    /// The link this one is placed from - the spot before it in the plan - or zero for the newest
    /// explosive on the ground. It decides where the cursor has to point. See Pointed.
    /// </param>
    /// <param name="record">
    /// Whether this answer is the one the dump should report.
    ///
    /// **Only the next blast's, and that had to be said explicitly.** Reachable asks this for every
    /// link of the chain, so a single shared verdict kept whichever call happened to run last - the
    /// first link the run could NOT reach, several blasts away and usually off the edge of the
    /// screen. The readout then said "the spot is not on screen" about the next bomb, which was
    /// sitting in plain view and perfectly placeable.
    /// </param>
    public bool Ready(GameController gc, AutoExpeditionSettings settings, Vector2 spot,
        Vector2 from, Vector2[] caught, List<RectangleF> covered, out string why, bool record = true)
    {
        why = "";

        // **Not Detonator.Placeable, which asks whether the TOGGLE BUTTON is on screen.** That
        // button was invisible thirteen explosives into a chain with two left to place, so the test
        // it looks like - "may an explosive be placed" - is not the test it is. What decides it is
        // whether the detonator has any explosives left to give.
        //
        // Placement mode being off is deliberately not a reason either: the automation taps the
        // toggle itself as its first step, so "the tool is not in hand yet" is work it does rather
        // than a reason the key would do nothing.
        if (Detonator.ExplosivesInHand(gc) <= 0)
        {
            why = "no explosives left";

            return Note(false, why, record);
        }

        // The game will not enter placement mode this far from the last explosive, so a press here would tap the
        // key and nothing would come up. Yellow rather than green, however well the link itself reaches.
        if (FromLastExplosive(gc) is var fromLast && fromLast >= PlacementModeRange)
        {
            why = $"you are {fromLast:0} grid from the last explosive - placement mode needs under {PlacementModeRange:0}";

            return Note(false, why, record);
        }

        // A remnant under THIS blast whose reward still needs settling, if there is one.
        Target owed;

        using (Spent.On("Ready/Owed"))
            owed = Owed(gc, settings, caught);

        if (owed != null)
        {
            var button = Combinations(gc, owed);

            // **The same three tests the placement run applies, in the same order.** Anything looser
            // here is a ring that promises what the next guard refuses, which is the whole failure
            // this is fixing - and Exposed, which hunts for a clickable corner, is looser.
            if (button.Width <= 0f || !OnScreen(gc, button.Center) ||
                Panels.Covers(covered, button.Center))
            {
                why = $"the combinations button on {owed.Label} is not on screen - walk up to it";

                return Note(false, why, record);
            }

            // The button is clickable, so the reward can be settled - but one press settles the
            // reward AND places the explosive, in that order and without stopping in between, so
            // the ground still has to be aimable or the press cannot finish. Falling through to
            // the ground tests rather than returning here is what makes the ring's promise the
            // whole press instead of its first half.
        }

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        // **The point the press would click, which is what has to be reachable.** The placement run
        // tests this same point before it moves the cursor - see Reachable - so a ring worked out
        // from anything else is a ring promising a press that the run would then refuse.
        Vector2 aim;

        using (Spent.On("Ready/Pointed"))
            aim = Pointed(gc, from, spot);
        var world = aim == Vector2.Zero
            ? Vector3.Zero
            : Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(aim), Vector3.Zero);
        var point = camera == null || world == Vector3.Zero
            ? Vector2.Zero
            : Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);

        var pointing = "";

        // **OnScreen, not a test against Vector2.Zero.**
        //
        // WorldToScreen projects a point behind the camera or past the edge of the window to
        // coordinates that are simply outside it - large, negative, whatever the maths gives - and
        // never to the origin. So checking for Zero asks "did the projection return nothing", which
        // it essentially never does, and the ring went green over ground nobody could see. The
        // window bounds are the actual question, and Placement already had the helper for it two
        // hundred lines away; writing a second, worse one beside it is how this was missed.
        // **Behind the camera as well as outside the window.** WorldToScreen mirrors a point
        // behind the viewer round in front of it, so a spot below and behind you comes back as an
        // ordinary coordinate - usually in a corner, under the permanent furniture - and the window
        // test waves it through. The plugin then asked what was painted there and refused the spot
        // as blocked by a life orb it was nowhere near.
        //
        // Asked here rather than later because of what comes after it: what is painted at a point
        // is found by descending the whole interface tree, and doing that for spots that are not on
        // the screen at all is most of that work and all of the noise it makes. See Ahead.
        if (point == Vector2.Zero || !OnScreen(gc, point, Edge) ||
            !Ahead(gc, camera, world, point))
        {
            // **With the numbers, because "not on screen" has two causes that read alike.** The
            // projection can fail and hand back the origin, or it can succeed and land outside the
            // window - and one of those is a bug in the read while the other is the honest answer.
            // Saying which, and where, is the difference between fixing it and guessing at it.
            var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default(RectangleF));

            why = (point == Vector2.Zero
                ? "the spot would not project at all (WorldToScreen gave nothing)"
                : !Ahead(gc, camera, world, point)
                ? $"the spot is BEHIND the camera - it projects to ({point.X:0},{point.Y:0}), " +
                  "which is a mirror image rather than a place you can click"
                : $"the spot projects to ({point.X:0},{point.Y:0}), outside the window " +
                  $"{window.Width:0}x{window.Height:0} less a {Edge:0} margin") + pointing;

            return Note(false, why, record);
        }

        if (Panels.Covers(covered, point))
        {
            why = "the spot is behind a panel" + pointing;

            return Note(false, why, record);
        }

        // **And behind anything else the interface paints.**
        //
        // The covered list knows six Expedition windows and nothing else, because that is all it was
        // ever built to know. The buff bar in the top-left corner is not one of them, nor is the
        // debuff bar under it, so a spot behind a buff icon read as perfectly clear while a click
        // there would be swallowed by the icon.
        //
        // Blocked asks the interface tree what is actually painted at the point instead, which is
        // both the correct question and one that needs no list to keep up to date - a bar the game
        // adds next league is caught by it without anybody noticing it had to be.
        //
        // Not folded into the covered list: that list is rectangles gathered once a frame for
        // several callers, and what is wanted here is one point tested against what is really drawn.
        // Fresh when this is the link being acted on. The colouring pass can use an answer a few
        // frames old; the press that puts an explosive down cannot. See Panels.Blocked.
        // **Not `record`, which is about the readout and was deciding this as a side effect.**
        //
        // Panels.Blocked walks the whole interface tree for an answer about one point, and it keeps
        // that answer for a quarter of a second - "a quarter second for drawing, this instant for
        // acting", as it puts it. Passing `record` as the freshness flag made the FIRST link of the
        // chain ask for a fresh descent every single frame, because record marks the one link whose
        // reason the dump should print.
        //
        // Measured: 2.411ms a frame, 2.4ms per descent, ninety per cent of the largest stage in
        // the whole plugin - to bypass a cache on behalf of a readout that does not care how old
        // the answer is. This is the drawing path; the run has its own. See Ready's record.
        bool blocked;

        using (Spent.On("Ready/Blocked"))
            blocked = Panels.Blocked(gc, point);

        if (blocked)
        {
            // Named, because "something is in the way" and "a buff icon is in the way" lead to
            // completely different next moves - wait, or step sideways.
            why = Panels.Why(gc, point) + pointing;

            return Note(false, why, record);
        }

        return Note(true, "", record);
    }

    /// <summary>
    /// The remnant under this blast that still owes a reward decision, or nothing.
    ///
    /// Wants is the same test the automation uses to decide there is a window worth opening - no
    /// choice made, or one that differs from the plan's while Overrule is on - so the ring and the
    /// key can never disagree about whether there is work here.
    /// </summary>
    private Target Owed(GameController gc, AutoExpeditionSettings settings, Vector2[] caught) =>
        Choosing(gc, settings, caught, out _);

    /// <summary>
    /// Whether the next ring was green last frame, and if not, which test shut it.
    ///
    /// **A colour that fails to change says nothing about why.** The first version of Ready gated
    /// on a button that is invisible for most of a chain, so the ring stayed yellow through every
    /// moment it was meant to be green and looked exactly like a ring that had not been wired up.
    /// One line in the dump is the difference between finding that in a minute and arguing about it.
    /// </summary>
    public static (bool Go, string Why, DateTime When) Verdict { get; private set; }

    /// <summary>
    /// The last reward this run clicked, whether the game agreed, and how long that took.
    ///
    /// For the dump. A remnant picked twice is a race nobody can see from the outside, so the one
    /// thing worth writing down is how long the round trip actually takes on this connection - the
    /// ceiling in Answers is a guess until there are numbers beside it.
    /// </summary>
    public static (string Reward, bool Took, double Ms, DateTime When) Picked { get; private set; }

    /// <summary>Records what Ready last answered. See Verdict.</summary>
    private static bool Note(bool go, string why, bool record)
    {
        if (record)
            Verdict = (go, why, DateTime.UtcNow);

        return go;
    }


    /// <summary>
    /// Remnants whose reward was just clicked, and when, so the same one is not chosen twice.
    ///
    /// **Picking is a server round trip and the client does not know the answer yet.** The click
    /// goes out, the window is still up for a frame or two, and the remnant goes on reporting no
    /// combination chosen - so the pass that found it undecided a moment ago finds it undecided
    /// again, walks back to the same button and picks a second time. Seen on the first run of this.
    ///
    /// A short silence per remnant rather than a global one, because two lone remnants side by side
    /// are two separate jobs and the second should not wait on the first.
    /// </summary>
    private readonly Dictionary<uint, DateTime> _settling = new();

    /// <summary>What the last placed explosive was chosen to catch, for AfterPick. Empty before the first lands.</summary>
    private Vector2[] _justPlaced = [];

    /// <summary>When AwaitingWindow began, and the remnant it waits on. See AfterPick.</summary>
    private DateTime _awaitingSince;

    private Vector2 _awaitedAt;

    /// <summary>
    /// How long to wait for the next remnant's window after a reward is taken. Chosen, not measured: the game puts
    /// the windows up one at a time, and the first dump that caught it had the next one open after the run had
    /// already moved on. NextWindowSaid records how long it actually takes.
    /// </summary>
    private static readonly TimeSpan NextWindowWithin = TimeSpan.FromMilliseconds(500);

    /// <summary>What the last wait for another remnant's window came to, for the dump.</summary>
    public static string NextWindowSaid { get; private set; } = "no wait for a second window yet";

    /// <summary>
    /// After a reward is taken, waits for the next remnant's window if the explosive just placed covers another
    /// remnant still without a reward; otherwise moves on.
    ///
    /// **The game puts the windows up one at a time.** An explosive covering three undecided remnants opens the
    /// window for one, and the next only after its reward is taken. The run moved on as soon as a pick took, so
    /// when the next link could not be placed it ended - "Walk closer" - and the third remnant's window came up
    /// after, with nothing left to answer it. Seen on one Grand site (2026-09-30): the remnant at (369,1214) was left
    /// with its window open and no reward.
    /// </summary>
    private void AfterPick(GameController gc, AutoExpeditionSettings settings)
    {
        var owed = _justPlaced.Length == 0 || _scan == null || _valuation == null
            ? null
            : _scan.Targets.FirstOrDefault(target =>
                target.Kind == TargetKind.Remnant && target.Rewards.Count > 0 && !target.Rerolled &&
                _justPlaced.Any(at => Vector2.Distance(target.Grid, at) < 1f) &&
                !_settling.ContainsKey(Safe.Read(() => target.Entity.Id, 0u)) &&
                Undecided(Safe.Read(() => _valuation.ChosenName(target.Entity), null)));

        if (owed == null)
        {
            Advance(gc, settings);

            return;
        }

        _awaitingSince = DateTime.UtcNow;
        _awaitedAt = owed.Grid;

        // A patience past NextWindowWithin, so the run's own deadline does not end it as a timeout first.
        Wait(Step.AwaitingWindow, NextWindowWithin + TimeSpan.FromSeconds(2));
        Say("Choosing", "waiting for the next remnant's window");
    }

    /// <summary>How long to leave a remnant alone after clicking its reward.</summary>
    private static readonly TimeSpan Settles = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Starts that silence for whichever remnant this click was aimed at, and crosses it off.
    ///
    /// **This is the moment a remnant is dealt with**, because this is the click that deals with it.
    /// Recording it when the run merely noticed the remnant needed changing meant a run that could
    /// not reach the button crossed it off anyway - see Choosing.
    /// </summary>
    private void Settling()
    {
        // Only the reward change has a dealt-with set; a chore is finished by the game removing
        // what it was about.
        //
        // **Unconditionally, not only when there is no chore in flight.** The cross-off used to sit
        // inside the "no chore" branch, so a remnant whose combination was being rewritten while a
        // chore was under way was never crossed off at all - and the guarantee this set exists to
        // give is that no remnant is visited twice in a run.
        var rewriting = Safe.Read(() => _change?.Entity?.Id ?? 0u, 0u);

        if (rewriting != 0u)
        {
            _rewrote.Add(rewriting);
            _settling[rewriting] = DateTime.UtcNow;
        }

        var id = Safe.Read(() => _chore?.Id ?? 0u, 0u);

        if (id != 0u)
            _settling[id] = DateTime.UtcNow;
    }

    /// <summary>How many times one button may be re-aimed before the run gives up on it.</summary>
    private const int Dodges = 2;

    private int _dodged;

    /// <summary>
    /// Moves to a clear part of the combinations button, if the game named something over it.
    ///
    /// Bounded, and false when there is nothing to move to - a button entirely under something else
    /// has no clear part, and re-aiming at the same pixel forever is how a run hangs rather than
    /// fails. Each attempt subtracts one more obstruction, so two passes handle a button with two
    /// things over it and the third says so honestly.
    /// </summary>
    private bool Dodged(GameController gc)
    {
        if (_dodged >= Dodges || _change == null)
            return false;

        var button = Button(gc, Safe.Read(() => _change.Entity, null), CombinationsPath).Rect;

        if (button.Width <= 0f)
            return false;

        var over = Panels.Hovered(gc);

        if (over.Width <= 0f)
            return false;

        var clear = Panels.Clearest(button, new List<RectangleF> { over });

        if (clear.Width <= 0f || clear.Height <= 0f)
            return false;

        _dodged++;
        _input.SetTolerance(8);
        _input.MoveTo(clear);

        return true;
    }

    /// <summary>
    /// Where inside a button to put the cursor, given whatever the game has drawn over it.
    ///
    /// The whole button when nothing is in the way, which is almost always. The clear part when
    /// something is, and the whole button again when nothing is clear - aiming at the middle and
    /// failing honestly beats refusing to aim at all, since the hover check decides either way.
    /// </summary>
    private static RectangleF Aiming(GameController gc, RectangleF button)
    {
        var over = Panels.Over(gc, button);

        if (over.Count == 0)
            return button;

        var clear = Panels.Clearest(button, over);

        return clear.Width > 0f && clear.Height > 0f ? clear : button;
    }

    /// <summary>The remnant whose combination is being changed, so the hover can be checked.</summary>
    private Target _change;

    /// <summary>The reward the run decided _change should be set to, decided once. See Options.Pick.</summary>
    private Reward _wanted;

    /// <summary>
    /// The remnant whose combinations button this plugin clicked, while its window is still up.
    ///
    /// **Which remnant an open window belongs to is not readable, so it is remembered instead.**
    /// Expedition2Window exposes a list of options and nothing else - no encounter, no entity, no
    /// back-reference of any kind - so anything derived from the window alone is evidence rather
    /// than fact. The window opens by clicking a button on one remnant's own ground label, and the
    /// run knows which one it clicked. That is not an inference and nothing read afterwards can
    /// improve on it.
    ///
    /// Static because the overlay needs the same answer as the run, and there is only ever one
    /// window. Cleared the moment the window is not on screen, so it can never answer for a window
    /// somebody else opened later - a stale identity here would be worse than no identity, since
    /// no identity is now refused loudly and a stale one would be acted on silently.
    /// </summary>
    public static Target Opened { get; private set; }

    /// <summary>The reward each queued chore was judged against, by entity. See Wants.</summary>
    private readonly Dictionary<uint, Reward> _wants = new();

    /// <summary>The scanned remnant with this entity id, or null. See Opened.</summary>
    private Target Remnant(uint id)
    {
        if (id == 0u || _scan == null)
            return null;

        foreach (var target in _scan.Targets)
        {
            if (target.Kind == TargetKind.Remnant &&
                Safe.Read(() => target.Entity?.Id ?? 0u, 0u) == id)
                return target;
        }

        return null;
    }

    /// <summary>Whether the plugin took the labels off the screen and owes the game a key back.</summary>
    private bool _hidden;

    /// <summary>
    /// Takes the ground labels off the screen, if one is over the button and the switch allows it.
    ///
    /// **Only when something is actually in the way.** Tapping a key the player did not press is a
    /// real intrusion, so it happens on the one occasion that earns it and not as a precaution -
    /// most clicks are unobstructed and should stay untouched.
    /// </summary>
    /// <returns>Whether it has just taken them down, so the caller can let the screen catch up.</returns>
    private bool Hiding(GameController gc, AutoExpeditionSettings settings)
    {
        if (_hidden)
        {
            Hid = "already hidden by this run";

            return false;
        }

        if (!settings.Automation.On(settings.Automation.Unhide))
        {
            Hid = "switched off - automation, or Toggle Highlighting when buttons are obscured";

            return false;
        }

        var button = Button(gc, _change?.Entity, CombinationsPath).Rect;

        if (button.Width <= 0f)
        {
            Hid = "no combinations button to be obscured - the label was not readable";

            return false;
        }

        if (!Panels.Shaded(gc, button.Center, Safe.Read(() => _change?.Entity, null)))
        {
            // The counts are named because the last miss here read as a thorough check that had
            // found nothing - 79 labels looked at - when what was over the button was the hovered
            // panel, which is not a label and was not being looked at at all. See Panels.Shaded.
            Hid = $"nothing over the button at ({button.Center.X:0},{button.Center.Y:0}), so " +
                  $"nothing to hide - {Ground.Labels(gc)?.Count ?? -1} ground labels, plus the " +
                  $"hovered label and the ground tooltip, all looked at";

            return false;
        }

        var key = Safe.Read(() => settings.HighlightKey.Value.Key, Keys.Z);

        if (!_input.Tap(key))
        {
            Hid = $"something IS over the button, and the tap of {key} was refused - no input " +
                  "lease, or ExileInput2 is not available";

            return false;
        }

        Hid = $"something was over the button, tapped {key} to hide the labels";
        _hidden = true;
        _hidAt = DateTime.UtcNow;

        return true;
    }

    /// <summary>
    /// What the last attempt to hide the ground labels did, for the dump.
    ///
    /// **Four ways for this to do nothing and no way to tell them apart.** Switched off, no button
    /// to read, nothing actually covering it, or a tap the input plugin refused - and from the
    /// outside all four are "the labels never hid". The one that matters most is the difference
    /// between "Shaded found nothing" and "the tap was refused", because the first is a detection
    /// problem in this plugin and the second is a lease or a bridge that is not there.
    /// </summary>
    public static string Hid { get; private set; } = "has not tried";

    /// <summary>When the labels were taken down, so the screen is given time to agree.</summary>
    private DateTime _hidAt = DateTime.MinValue;

    /// <summary>
    /// How long to let the client act on the key before reading the screen again.
    ///
    /// **The hide used to be undone before it could do anything.** The key was tapped and the
    /// button's highlight read on the same frame - so the answer came back from the screen as it was
    /// BEFORE the labels went, the run concluded the button was not a button, and the way out of
    /// that branch taps the key back. The labels flickered and the click was never attempted, which
    /// from outside looks exactly like the hiding not happening at all.
    ///
    /// A key press is not a function call. It goes to the client, which acts on it on its own
    /// schedule, and everything downstream of one has to wait for the screen to say so.
    /// </summary>
    private static readonly TimeSpan Hides = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Puts them back, which is owed the moment the click is done or abandoned.
    ///
    /// **Called on every way out of that step, including the refusals.** A plugin that hides the
    /// game's labels and then gives up leaves the player looking at a dig site with no names on it
    /// and no idea why - which is worse than the click it was trying to rescue.
    /// </summary>
    private void Showing(AutoExpeditionSettings settings)
    {
        if (!_hidden)
            return;

        _hidden = false;

        var key = Safe.Read(() => settings.HighlightKey.Value.Key, Keys.Z);

        Hid = _input.Tap(key)
            ? Hid + $"; then tapped {key} to put them back"
            : Hid + $"; and the tap of {key} to put them back WAS REFUSED - the labels may still " +
                    "be hidden";
    }

    /// <summary>
    /// The reward this run has just clicked, and the remnant it was clicked for.
    ///
    /// Both, because neither alone answers the question. The name says which combination to expect
    /// to read back; the remnant says where to read it. A window opened by the game landing a blast
    /// on an undecided remnant has no _change behind it, which is the case that went round twice.
    /// </summary>
    private string _picking = "";

    /// <summary>
    /// What identifies the combination being clicked, rather than what it yields. See Took.
    ///
    /// Kept beside the name because a remnant can offer two combinations under one name, and the
    /// name alone cannot tell a click that took from one that set the other.
    /// </summary>
    private string _pickingRecipe = "";

    /// <summary>
    /// The recipe the placement run set on each remnant in this area, by cell. A reward set on a remnant is the
    /// player's choice unless it is the one recorded here: the run sets must takes to the reward the plan was pinned
    /// to, and that is not a decision the player made. Written to sites/placement_choices_{area}.tsv as it changes and
    /// read back on entering the area, so it survives a reload and a trip out and back. See Valuation.ChosenByPlayer.
    /// </summary>
    private static readonly Dictionary<(int X, int Y), string> _setByPlacement = new();

    /// <summary>The area _setByPlacement belongs to, and so the file it is written to.</summary>
    private static uint _setByPlacementArea;

    private static void RecordSetByPlacement(Vector2 grid, string recipe)
    {
        if (grid == Vector2.Zero || string.IsNullOrEmpty(recipe))
            return;

        lock (_setByPlacement)
        {
            _setByPlacement[((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y))] = recipe;

            Safe.Do(() => System.IO.File.WriteAllLines(SetByPlacementFile(_setByPlacementArea),
                _setByPlacement.Select(x => $"{x.Key.X}\t{x.Key.Y}\t{x.Value}")));
        }
    }

    private static string SetByPlacementFile(uint area)
    {
        var folder = System.IO.Path.Combine(Kept.Home, "sites");

        System.IO.Directory.CreateDirectory(folder);

        return System.IO.Path.Combine(folder, $"placement_choices_{area}.tsv");
    }

    /// <summary>Whether the placement run set this recipe on the remnant at this cell. See _setByPlacement.</summary>
    internal static bool SetByPlacement(Vector2 grid, string recipe)
    {
        lock (_setByPlacement)
            return _setByPlacement.TryGetValue(((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y)), out var set) &&
                   string.Equals(set, recipe, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the run set in the area being entered: read from its file, or nothing for an area not seen before. See
    /// _setByPlacement.
    /// </summary>
    internal static void LoadSetByPlacement(uint area)
    {
        lock (_setByPlacement)
        {
            _setByPlacement.Clear();
            _setByPlacementArea = area;

            if (area == 0 || Kept.Home.Length == 0)
                return;

            Safe.Do(() =>
            {
                var path = SetByPlacementFile(area);

                if (!System.IO.File.Exists(path))
                    return;

                foreach (var line in System.IO.File.ReadLines(path))
                {
                    var parts = line.Split('\t');

                    if (parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
                        _setByPlacement[(x, y)] = parts[2];
                }
            });
        }
    }

    private Entity _picked;

    /// <summary>When the reward was clicked, so the wait for it has an end. See Answers.</summary>
    private DateTime _clicked = DateTime.MinValue;

    /// <summary>
    /// How long to wait for the server to agree before giving up on hearing it.
    ///
    /// Generous, because it only ever costs this long when the answer never comes - and when it
    /// does come it is usually inside a couple of hundred milliseconds, which is what the ordinary
    /// case pays. A ceiling rather than a fixed delay: a fixed delay that is too short once is a
    /// remnant picked twice again, which is the thing this exists to stop.
    /// </summary>
    private static readonly TimeSpan Answers = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Whether the remnant now reads back the reward that was clicked.
    ///
    /// True when there is nothing to check against - a window nobody can attribute to a remnant, or
    /// a run with no valuation - because a wait with no way to end it is a stall, and the ceiling
    /// in Chose is what covers that case instead.
    /// </summary>
    private bool Took(GameController gc)
    {
        if (_picked == null || _picking.Length == 0 || _valuation == null)
            return true;

        // The recipe when there is one, since two combinations can share a name and reading one
        // back would report the other's click as having taken. See Valuation.AlreadySetTo.
        if (_pickingRecipe.Length > 0)
        {
            var set = Safe.Read(() => _valuation.ChosenRecipeId(_picked), null);

            if (!string.IsNullOrWhiteSpace(set))
                return string.Equals(set, _pickingRecipe, StringComparison.Ordinal);
        }

        var chosen = Safe.Read(() => _valuation.ChosenName(_picked), null);

        return chosen != null &&
               string.Equals(chosen, _picking, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remnants this run has already dealt with, so none is visited twice and none stalls the run.
    ///
    /// Picking a combination is a server round trip: the window closes on the click, and
    /// SelectedRecipe only reads back the new one once the server has answered. In between it
    /// reads the OLD combination, which looks exactly like "still wrong" - so the sequence went
    /// round and changed it a second time.
    ///
    /// A settle delay was the obvious fix and is the wrong one: any number chosen is a guess about
    /// the wire, and a guess that is too short once is a remnant changed twice again. This cannot
    /// be too short. A remnant is diverted to at most once per run, and if the change did not take,
    /// the run says so rather than trying again.
    /// </summary>
    private readonly HashSet<uint> _rewrote = new();

    /// <summary>
    /// A remnant this link would set off whose combination is set to something else.
    ///
    /// Only one that has ALREADY been decided. An undecided remnant needs nothing done in advance -
    /// the explosive landing on it makes the game put the window up by itself, and the existing
    /// path takes it from there. This is the other case, which had no path at all: a remnant
    /// decided earlier, by hand or by an earlier plan, that the chain is about to spend.
    /// </summary>
    /// <summary>
    /// The remnant a link is about to set off that still needs its reward choosing, or null.
    ///
    /// **This was written twice and the two copies disagreed, which is what let a green ring end in
    /// "Too far from runes button".** The placement run asked this question; the readiness test that
    /// colours the ring asked Wants instead - and Wants begins by requiring Scan.Loose, because it
    /// was written for a remnant standing on its own in a map. Every remnant a PLANNED blast covers
    /// fails that test, so the readiness check never once looked at the combinations button for the
    /// case it exists for, and the ring went green on the ground alone.
    /// </summary>
    /// <remarks>
    /// **Nothing is crossed off here.** This used to add the remnant to the dealt-with set the
    /// moment it NOTICED one needed changing - so the very attempt that then failed, because the
    /// combinations button was off screen, marked it done. From that point the remnant was invisible
    /// to both callers: the ring went green because nothing was owed, and the next press placed an
    /// explosive on a reward nobody had chosen. The failure was silent and permanent.
    ///
    /// A remnant is dealt with when its reward has been clicked, and that is where it is recorded.
    /// See Settling.
    /// </remarks>
    private Target Choosing(GameController gc, AutoExpeditionSettings settings,
        IEnumerable<Vector2> caught, out Reward want)
    {
        want = null;

        // Overruling off means a chosen remnant is somebody's decision, not a mistake to correct.
        if (!settings.Automation.On(settings.Automation.PreExpedition.ChooseRewards) || !settings.Rewards.Overrule ||
            _scan == null || _valuation == null)
            return null;

        // Only the link about to be placed.
        //
        // It was briefly every link in the plan, on the reasoning that a combination stays
        // changeable until detonation so a remnant behind the chain is still worth putting right.
        // True, and not worth what it costs: the button only works up close, so a wrong remnant
        // two links back sends the run walking back across the dig site between explosives. The
        // sequence is meant to place what is in front of you.
        //
        // The consequence is real and accepted - a remnant already covered by a placed explosive
        // is left as it is. It was checked when its own explosive went down, which is the moment
        // it was in front of you.
        foreach (var at in caught ?? [])
        {
            foreach (var target in _scan.Targets)
            {
                if (target.Kind != TargetKind.Remnant || target.Rewards.Count == 0 ||
                    Vector2.Distance(target.Grid, at) >= 1f)
                    continue;

                // Dealt with already, whatever it currently reads as.
                //
                // The read is the problem: picking a combination is a server round trip, so for a
                // frame or two afterwards SelectedRecipe still returns the OLD one. Checking it
                // again immediately therefore says "still wrong" about a remnant that was just put
                // right, and the sequence either changed it twice or - once that was guarded -
                // stopped dead and made the player press again. Neither is what should happen: the
                // window closing IS the game accepting the pick, and that already happened.
                if (_rewrote.Contains(Safe.Read(() => target.Entity.Id, 0u)))
                    continue;

                // A rolled remnant cannot be changed, so there is nothing here to overrule.
                //
                // **Rerolling is not choosing.** Liquid Verisium rerolls the runes, the socket count
                // can change with them, and what comes out is final: it cannot be rolled again and
                // its combination cannot be changed, even though the window still opens and the
                // entries still look clickable. So this would walk to it, open it, and click
                // something that does nothing.
                //
                // Only rolled ones. A remnant whose reward was merely chosen is still open to being
                // chosen again, and whether that happens is what the Overrule setting decides.
                if (target.Rerolled)
                    continue;

                string chosen;

                using (Spent.On("Owed/Selection"))
                    chosen = _valuation.ChosenName(target.Entity);

                if (chosen == null)
                    continue;

                var took = Options.Solved(target);

                // Split out from the game reads above it. Ready/Owed allocates about 123KB per
                // call - 246,936 bytes a frame over two calls - and this is the only part of it
                // that does arithmetic rather than reading the client: Carried and Locally both
                // walk the chain through Reroll.Downstream and Reroll.Local, weighing every
                // position every link catches, and nothing said which side the bytes were on.
                Reward wants;

                using (Spent.On("Owed/Rates"))
                {
                    wants = target.Rewards[took >= 0
                        ? took
                        : Options.Take(target.Rewards,
                            Options.Carried(settings, _valuation, target, gc, _scan, _plan),
                            Options.Locally(settings, _valuation, target, gc, _scan, _plan),
                            Options.Money(settings))];
                }

                // **Already set to what the run would pick, so there is nothing to open.** This is
                // the only place that test belongs: the window costs a walk across the dig site to
                // reach, so a remnant needing no change should never be walked to at all - not
                // walked to, opened, and then found to need nothing.
                if (Safe.Read(() => _valuation.AlreadySetTo(target.Entity, wants), false))
                    continue;

                want = wants;

                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// The same question asked by the placement run, which is about to act on the answer.
    /// </summary>
    private Target Rewrite(GameController gc, AutoExpeditionSettings settings, int index,
        out Reward want) =>
        Choosing(gc, settings, _plan.CaughtBy(index), out want);

    /// <summary>
    /// The nearest thing left to tidy up, whatever kind it is.
    ///
    /// **Nearest wins, across both kinds.** Not all the remnants and then all the chests: whatever
    /// is closest is what gets done, so a site whose remnants and chests are interleaved is cleared
    /// in the order you would walk it - remnant, chest, remnant - rather than walked twice.
    ///
    /// Nearest to the PLAYER rather than to the chain, because this is entirely about walking. Both
    /// jobs need to be close: the shatter button only works up close, and a chest is opened by
    /// going to it.
    /// </summary>
    /// <summary>
    /// Whether the nearest chore is closer to the player than the next planned spot is.
    ///
    /// **The comparison, not a distance.** A threshold would need a number nothing has measured and
    /// would behave differently on a Grand site to an ordinary one; which of the two is nearer is
    /// the question actually being asked, and both answers move with the player in the same units.
    ///
    /// Nought when either has no position, which leaves the ordinary order alone. See Advance.
    /// </summary>
    private bool Closer(GameController gc, AutoExpeditionSettings settings)
    {
        var player = Safe.Read(() => gc.Player.GridPos, Vector2.Zero);
        var index = Next(gc, _plan);

        if (player == Vector2.Zero || index < 0 || index >= _plan.Points.Count)
            return false;

        var todo = Chore(gc, settings);

        if (todo.Of == null)
            return false;

        var at = Safe.Read(todo.Of, static e => e.GridPos, Vector2.Zero);

        return at != Vector2.Zero &&
               Vector2.Distance(player, at) < Vector2.Distance(player, _plan.Points[index]);
    }

    private (Entity Of, int[] Path, string Word, RectangleF At, int Waiting) Chore(
        GameController gc, AutoExpeditionSettings settings)
    {
        var player = Safe.Read(() => gc.Player.GridPos, Vector2.Zero);

        if (player == Vector2.Zero)
            return (null, null, null, default, 0);

        var after = settings.Automation.PostExpedition;
        var covered = Panels.Covered(gc);

        Entity nearest = null;
        int[] path = null;
        string word = null;
        var at = default(RectangleF);
        var closest = float.MaxValue;

        // Things that want doing and cannot be reached from here. Counted rather than chosen, so
        // "walk closer" can be said about a real number instead of about the one candidate that
        // happened to be nearest.
        var waiting = 0;

        void Consider(Entity of, int[] into, string says)
        {
            if (_skip.Contains(Safe.Read(of, static e => e.Id, 0u)))
                return;

            var rect = Button(gc, of, into).Rect;

            // Usable, not merely near. A label belonging to something behind the camera is drawn
            // at an off-screen position, so distance in the world says nothing about whether it can
            // be clicked - and picking the nearest and then refusing it meant one unreachable thing
            // stopped the whole pass while reachable ones stood around it.
            var spot = Exposed(gc, covered, rect, Safe.Read(of, static e => e.Id, 0u)).At;

            if (spot == Vector2.Zero)
            {
                waiting++;

                return;
            }

            var distance = Vector2.Distance(player, Safe.Read(of, static e => e.GridPos, Vector2.Zero));

            if (distance >= closest)
                return;

            closest = distance;
            nearest = of;
            path = into;
            at = rect;
            word = says;
        }

        if (settings.Automation.On(after.Shatter) && _scan != null)
        {
            foreach (var target in _scan.Targets)
            {
                if (target.Shatterable && target.Entity != null)
                    Consider(target.Entity, ShatterPath, "shattering a spent remnant");
            }
        }

        // **A remnant nobody has decided yet, which is most often one standing on its own.**
        //
        // Maps are full of remnants belonging to no expedition at all. There is no chain to plan for
        // them and nothing to detonate: you pick a reward and shatter it, and the shatter is not
        // offered until something is picked. The plugin could already choose from an open window and
        // could already press this button before placing an explosive over a remnant - it simply had
        // no reason to press it when there was no explosive to place.
        //
        // Undecided only. A remnant already blown has had its reward spent, and one that has been
        // chosen needs no second visit - so this is the button for a decision that is still
        // outstanding, and Choose takes over the moment the window comes up.
        //
        // Behind the same switch as the choosing itself: opening the window to pick a reward and
        // picking it are one intention, and a player who has turned the picking off does not want
        // windows opened on their behalf either.
        if (settings.Automation.On(settings.Automation.PreExpedition.ChooseRewards) && _scan != null && _valuation != null)
        {
            foreach (var target in _scan.Targets)
            {
                if (!Wants(gc, settings, target, out var want))
                    continue;

                // Kept against the entity, because the chore is queued here and taken up several
                // frames later by whichever one wins - and the reward it was judged against has to
                // travel with it. See Options.Pick.
                _wants[Safe.Read(() => target.Entity.Id, 0u)] = want;

                Consider(target.Entity, CombinationsPath, "opening the combinations window");
            }
        }

        // Chests come from the entity list, NOT from the scan.
        //
        // The scan's chests are chest MARKERS, classified from the signpost art standing in the dig
        // site - and the blast consumes the marker and leaves a chest. So a scan target's entity is
        // the thing that is gone, its label belongs to nothing, and every chest counted as
        // unreachable. What wants clicking is the chest itself, which only exists after the blast.
        if (settings.Automation.On(after.Chests))
        {
            var chests = Safe.Read(gc, static g =>
                g.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Chest, out var of)
                    ? of
                    : null, null);

            foreach (var chest in chests ?? new List<Entity>())
            {
                var metadata = Safe.Read(chest, static e => e.Metadata, "") ?? "";

                if (metadata.IndexOf("LeaguesExpedition", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (Safe.Read(chest, static e => e.GetComponent<Chest>()?.IsOpened ?? true, true))
                    continue;

                // Filed under Chests is not the same as openable. GenericShatterable - the barrels
                // a blast breaks - sits at the same path as every reward chest, and this pass would
                // have sent the cursor to one and clicked at something the game will not let you
                // target. Targetable is the game answering that question itself.
                if (!Safe.Read(chest, static e => e.IsTargetable, false))
                    continue;

                // A chest's label IS the button - it has no children - so the path is empty.
                Consider(chest, [], "opening an excavated chest");
            }
        }

        return (nearest, path, word, at, waiting);
    }

    /// <summary>Whether the cursor is on the thing it was sent to, by whichever test suits it.</summary>
    private bool Under(GameController gc) =>
        _path is { Length: > 0 } ? Button(gc, _chore, _path).Lit : Hovered(gc, _chore);

    /// <summary>
    /// A spot on this rectangle that can actually be clicked, or nothing when none can.
    ///
    /// The middle first, because that is where a thing is if nothing is in front of it. When the
    /// middle is covered the rest is sampled on a grid, and a label that is nine tenths hidden
    /// behind a panel is still clickable through whatever corner of it is showing - which is the
    /// case this exists for, and one that refusing on the centre alone got wrong.
    ///
    /// Inset, so a spot is never chosen on the very edge of the label where a pixel of drift lands
    /// outside it. Coarse, because the grid is only looking for a gap big enough to put a cursor
    /// in, and forty odd samples of an interface walk is already more work than this deserves.
    /// </summary>
    private static (Vector2 At, float Room) Exposed(GameController gc, List<RectangleF> covered,
        RectangleF rect, uint mine = 0u)
    {
        if (rect.Width <= 0f || rect.Height <= 0f)
            return (Vector2.Zero, 0f);

        // Read once for the whole calculation rather than per candidate spot.
        //
        // There was a third test here, projecting a box for every Verisium Sentry on the reasoning
        // that a monster standing in front of a chest hides its label. The evidence says otherwise:
        // what was actually in the way was a sibling GROUND LABEL, and the world never came into
        // it. The projection was a guess with no case behind it and a standing risk of refusing
        // spots that were free, so it is gone rather than left in to be wrong quietly.
        var others = Neighbours(gc, mine);

        bool Clear(Vector2 at) =>
            OnScreen(gc, at) && !Panels.Covers(covered, at) && !Panels.Blocked(gc, at);

        // What is left of the label once every other label is cut out of it.
        //
        // Cut rather than sampled. A grid over the label finds a gap when the gap is a fifth of it
        // and misses when it is a twentieth, which is exactly the case this has to get right - a
        // label with a sliver showing is still a label that can be clicked. Subtracting rectangles
        // gives the sliver itself, and the middle of the biggest piece is as far from an edge as
        // the geometry allows.
        var free = new List<RectangleF> { rect };

        foreach (var other in others)
            free = Cut(free, other);

        var best = Vector2.Zero;
        var biggest = 0f;
        var room = 0f;

        foreach (var piece in free)
        {
            var area = piece.Width * piece.Height;

            if (area <= biggest || piece.Width < Sliver || piece.Height < Sliver)
                continue;

            // Whole pixels, because that is what the cursor lands on.
            //
            // The game reports the cursor as integers, so a target at x.5 is one the pointer can
            // never sit exactly on - it reads back half a pixel out, and that half pixel has to be
            // paid for out of the tolerance before any real error is. Rounding the target removes
            // it and buys the whole allowance back for the gap itself.
            var middle = new Vector2(
                MathF.Round(piece.X + piece.Width / 2f),
                MathF.Round(piece.Y + piece.Height / 2f));

            // Unless rounding has just walked it off the piece, which a narrow one can do.
            if (middle.X < piece.Left || middle.X > piece.Right ||
                middle.Y < piece.Top || middle.Y > piece.Bottom)
                continue;

            if (!Clear(middle))
                continue;

            biggest = area;
            best = middle;

            // How far the cursor may stray from the middle of this piece and still be on it.
            room = MathF.Min(piece.Width, piece.Height) / 2f;
        }

        return (best, room);
    }

    /// <summary>
    /// How small a piece of a label may be and still be worth aiming at, in pixels.
    ///
    /// **There is a floor here and it is arithmetic rather than a shortcoming.** Arrival is judged
    /// by the distance between where the cursor was sent and where the game says it is, and the
    /// game says so in whole pixels - so a tolerance below one is asking about a difference it
    /// cannot express, and the step waits for an arrival that never comes.
    ///
    /// That puts the smallest usable gap at about three pixels: a tolerance of one, inside a piece
    /// whose middle is at least one pixel from either edge. Below that the click would miss as
    /// often as hit, and refusing is the honest answer rather than trying and mis-clicking.
    ///
    /// It was six while the target was being aimed at fractional coordinates, which spent most of
    /// the allowance on rounding before any real error was counted.
    /// </summary>
    private const float Sliver = 3f;


    /// <summary>
    /// How far the game's stated landing spot may be from the planned one, in grid units.
    ///
    /// One, and not a setting, because one is what the arithmetic allows rather than a preference.
    /// A plan's points are floats and the game snaps a placement to whole grid cells, so a cursor
    /// aimed exactly right reads either exactly on the spot or one unit off from that rounding.
    /// Zero would therefore reject correct placements, and above one it would accept an explosive
    /// landing somewhere the plan did not choose.
    ///
    /// It was a slider on the reasoning that the figure was inferred rather than read. It is not
    /// inferred - the grid is integral - so there was nothing here for anyone to tune.
    /// </summary>


    /// <summary>
    /// The tightest the cursor is ever asked to be, in pixels.
    ///
    /// One, now that the target is rounded to whole pixels before it is sent: the cursor can sit
    /// exactly on it, so the only allowance needed is for the frame or two before the game reports
    /// the new position. It was four, then two, while the target was fractional and some of the
    /// tolerance was being spent on the rounding rather than on the gap.
    /// </summary>
    private const float Closest = 1f;

    /// <summary>
    /// Every rectangle with one rectangle cut out of it.
    ///
    /// Up to four pieces per overlap - above, below, left, right - which is the standard way of
    /// doing this and produces overlapping pieces in the corners. That does not matter here: the
    /// pieces are only used to find a point, and a point counted twice is the same point.
    /// </summary>
    private static List<RectangleF> Cut(List<RectangleF> from, RectangleF hole)
    {
        var left = new List<RectangleF>();

        foreach (var piece in from)
        {
            if (hole.Right <= piece.Left || hole.Left >= piece.Right ||
                hole.Bottom <= piece.Top || hole.Top >= piece.Bottom)
            {
                left.Add(piece);

                continue;
            }

            if (hole.Top > piece.Top)
                left.Add(new RectangleF(piece.X, piece.Y, piece.Width, hole.Top - piece.Top));

            if (hole.Bottom < piece.Bottom)
                left.Add(new RectangleF(piece.X, hole.Bottom, piece.Width, piece.Bottom - hole.Bottom));

            if (hole.Left > piece.Left)
                left.Add(new RectangleF(piece.X, piece.Y, hole.Left - piece.Left, piece.Height));

            if (hole.Right < piece.Right)
                left.Add(new RectangleF(hole.Right, piece.Y, piece.Right - hole.Right, piece.Height));
        }

        return left;
    }

    /// <summary>
    /// Whether the character is on the move.
    ///
    /// Two readings, because neither is reliable alone. Actor.isMoving is the client's own answer
    /// and is what a movement skill sets; the Moving action flag is the same state read from the
    /// action word. Either being set is enough - this is a gate that should err towards refusing.
    /// </summary>
    internal static bool Walking(GameController gc)
    {
        var actor = Safe.Read(gc, static g => g.Player?.GetComponent<Actor>(), null);

        if (actor == null)
            return false;

        return Safe.Read(actor, static a => a.isMoving, false) ||
               Safe.Read(actor, static a => (a.Action & ActionFlags.Moving) != 0, false);
    }

    /// <summary>How many jobs this run has finished, for the word at the end.</summary>
    private int _shattered;

    /// <summary>The path into the label of whatever is being dealt with, empty for a chest.</summary>
    private int[] _path;

    /// <summary>Whatever the tidy-up pass is currently pointed at.</summary>
    private Entity _chore;

    /// <summary>Things this run has given up on because something was in front of them.</summary>
    private readonly HashSet<uint> _skip = new();

    /// <summary>
    /// Whether the game says the cursor is over this entity's ground label.
    ///
    /// The one test that needs no model of the interface: whatever is actually on top at that spot
    /// is what UIHover returns, panels and all. Walked up the parents because the hovered element
    /// is usually a child of the label - the text, or the icon inside it - rather than the label.
    /// </summary>
    private static bool Hovered(GameController gc, Entity of)
    {
        var id = Safe.Read(of, static e => e.Id, 0u);
        var hover = Hover(gc);

        if (id == 0u)
            return false;

        // A check that cannot run does not get to veto.
        //
        // UIHover comes back as an element at address zero on this build - not a wrong element, no
        // element - so every comparison against it was false and every chest was refused. Four
        // rounds went into which two elements to compare and in which direction, and the answer was
        // that one of the operands did not exist.
        //
        // So when the game will not say what is under the cursor, the geometry stands on its own:
        // the point was chosen inside the label's own hit rect, with every other label cut out of
        // it, nothing painted over it, and the cursor landing within a pixel or two. That is a
        // weaker guarantee than the game's own answer and it is the one available.
        if (hover == null)
            return true;

        var labels = Ground.Labels(gc);

        if (labels == null)
            return false;

        foreach (var label in labels)
        {
            if (Safe.Read(() => label.ItemOnGround?.Id ?? 0u, 0u) != id)
                continue;

            var want = Safe.Read(() => label.Label, null);

            // Either way round, because which of the two is the hovered element depends on the
            // label and cannot be assumed. A chest's label is a LEAF - the dump says kids=0 - so
            // what the game reports as hovered is the container above it, and walking down from
            // the leaf finds nothing at all. A remnant's label is a container, and then the
            // hovered element is the child inside it and walking down is exactly right.
            //
            // So: the same element, something inside it, or something it is inside.
            return want != null &&
                   (Same(want, hover) || Within(want, hover, 0) || Within(hover, want, 0));
        }

        return false;
    }

    /// <summary>
    /// Whether the hovered element is this label or something inside it.
    ///
    /// Downwards, which is called twice with the arguments swapped rather than once up the parent
    /// chain. The parent chain was the first attempt and came back wrong - a cursor sitting
    /// squarely on the right label reported as hovering something else - and a downward walk is
    /// bounded by something known, since a label is three or four elements deep where the distance
    /// up to the interface root is not a number this can rely on.
    /// </summary>
    private static bool Within(Element parent, Element wanted, int depth)
    {
        if (parent == null || wanted == null || depth > 6)
            return false;

        var kids = Safe.Kids(parent);

        if (kids == null)
            return false;

        foreach (var kid in kids)
        {
            if (Same(kid, wanted) || Within(kid, wanted, depth + 1))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The whole hover comparison, written out, for the dump.
    ///
    /// Four rounds of this have been spent reasoning about which of two elements the game reports
    /// and which direction to walk between them, each time from one symptom and a guess. This
    /// prints the operands and every test's answer, so the next round starts from the arithmetic
    /// rather than from another theory.
    /// </summary>
    /// <summary>
    /// What the game says is under the cursor, or null when it will not say.
    ///
    /// Two fields, because the first one does not work here: IngameState.UIHover reads back as an
    /// element at address zero. UIHoverElement is the other name for the same idea and may be the
    /// one this build fills in, so it is tried second, and an address of zero is treated as no
    /// answer rather than as an answer of nothing.
    /// </summary>
    private static Element Hover(GameController gc)
    {
        foreach (var element in new[]
                 {
                     Safe.Read(gc, static g => g.IngameState.UIHover, null),
                     Safe.Read(gc, static g => g.IngameState.UIHoverElement, null),
                 })
        {
            if (element != null && Safe.Read(element, static e => e.Address, 0L) != 0L)
                return element;
        }

        return null;
    }

    internal static string Explain(GameController gc, Entity of)
    {
        var id = Safe.Read(of, static e => e.Id, 0u);
        var hover = Hover(gc);
        var labels = Ground.Labels(gc);

        if (labels == null)
            return "no ground labels at all";

        foreach (var label in labels)
        {
            if (Safe.Read(() => label.ItemOnGround?.Id ?? 0u, 0u) != id)
                continue;

            var want = Safe.Read(() => label.Label, null);

            if (want == null)
                return "the label element is null";

            return $"label {Safe.Read(want, static e => e.Address, 0L):X} " +
                   $"kids {Safe.Read(want, static e => (int)e.ChildCount, 0)}   " +
                   $"UIHover {Safe.Read(gc, static g => g.IngameState.UIHover?.Address ?? 0L, 0L):X} " +
                   $"UIHoverElement {Safe.Read(gc, static g => g.IngameState.UIHoverElement?.Address ?? 0L, 0L):X}   " +
                   $"hover {Safe.Read(hover, static e => e.Address, 0L):X} {Describe(hover)}   " +
                   $"same {Same(want, hover)}  " +
                   $"hover-inside-label {Within(want, hover, 0)}  " +
                   $"label-inside-hover {Within(hover, want, 0)}";
        }

        return $"no label is filed under entity {id}";
    }

    /// <summary>Enough about an element to recognise it in a status line.</summary>
    private static string Describe(Element element)
    {
        if (element == null)
            return "nothing";

        var text = Safe.Read(element, static e => e.Text, null);
        var rect = Safe.Read(element, static e => e.GetClientRectCache, default);

        return (string.IsNullOrWhiteSpace(text) ? "an unnamed element" : $"\"{text.Trim()}\"") +
               $" ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})";
    }

    private static bool Same(Element one, Element other) =>
        one != null && other != null &&
        Safe.Read(one, static e => e.Address, 0L) == Safe.Read(other, static e => e.Address, 0L) &&
        Safe.Read(one, static e => e.Address, 0L) != 0L;

    /// <summary>
    /// The button on a remnant's ground label that opens the combinations window.
    ///
    /// Addressed by child index inside the label, the same way the Liquid Verisium button is, and
    /// for the same reason: ExileCore2 names neither of them. The index is read off a dumped label
    /// rather than guessed - child 1 is a 54 by 54 element at the left hand end, and it reports
    /// itself visible only on remnants near enough to interact with, which is the behaviour a
    /// button has and a decoration does not.
    ///
    /// It is not trusted, either. The hover check in <see cref="Lit"/> is what actually decides
    /// whether this is a button, because the game will say so and a child index cannot.
    /// </summary>
    private static RectangleF Combinations(GameController gc, Target remnant) =>
        Button(gc, remnant, CombinationsPath).Rect;

    /// <summary>The shatter button on a spent remnant's label, or nothing when it is not offered.</summary>
    private static RectangleF Shatter(GameController gc, Target remnant) =>
        Button(gc, remnant, ShatterPath).Rect;

    /// <summary>
    /// A button on a remnant's ground label, addressed by the path of child indices to reach it.
    ///
    /// ExileCore2 names none of these - it exposes the label and stops - so each is a path read off
    /// a dump taken with the button hovered, and confirmed by the one element in the whole interface
    /// that lit up. The rectangle is what to click and the highlight is what says the path is still
    /// right; both come from here so they cannot drift apart.
    /// </summary>
    internal static (RectangleF Rect, bool Lit) Button(GameController gc, Target remnant, int[] path) =>
        Button(gc, Safe.Read(() => remnant.Entity, null), path);

    /// <summary>
    /// The part of a ground label that is actually clickable, as opposed to the wrapper round it.
    ///
    /// **The game says which, and the question was how to ask.** A label is a wrapper whose top
    /// fifth or so is empty - clicks pass straight through it - around an inner element that is
    /// solid. Aiming at the wrapper's middle lands in that gap, and blocking on the wrapper's rect
    /// refuses spots that are free, so both halves of this need the inner one.
    ///
    /// ElementType looked like the principled way to ask and is not: dumped across a live dig site
    /// the values that come back are 0, 8036, 15478, 17707, 30957 and 53373, and the enum's
    /// MiscGroundLabel and GroundItemLabel - 8720 and 16516 - appear nowhere. Whatever that field
    /// holds on this build, it is not a label's kind, so the branch reading it was dead code that
    /// read as the real rule.
    ///
    /// So it is the shape after all: the first visible child with a rectangle. A guess that happens
    /// to hold, and one that would go on quietly holding until a patch put a decoration there -
    /// which is worth writing down rather than dressing up.
    /// </summary>
    private static Element Hit(Element label)
    {
        var first = Safe.Kid(label, 0);

        if (first == null || !Safe.Read(first, static e => e.IsVisible, false))
            return label;

        var rect = Safe.Read(first, static e => e.GetClientRectCache, default);

        return rect.Width > 0f && rect.Height > 0f ? first : label;
    }

    /// <summary>Whether any of these rectangles contains the spot.</summary>
    private static bool Inside(List<RectangleF> rects, Vector2 at)
    {
        foreach (var rect in rects)
        {
            if (at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The other ground labels, which take a click before the one underneath them does.
    ///
    /// The thing that was actually in the way, found in the end by reading the path the game's own
    /// tree gave: 8,0,0,188,0 - a sibling of the chest's label at 8,0,0,181, drawn later and so on
    /// top. Not a panel and not a model, which is why two rounds of looking for those found
    /// nothing.
    ///
    /// The whole of the CHILD's rect counts, not the part of it with writing on and not the wrapper
    /// around it. A label's inner element is a hit area: the game routes a click to whichever one
    /// contains the cursor, so the space around the words in a wide label swallows a click exactly
    /// as the words do - while the wrapper's own margin above it does not, and blocking on that
    /// refused spots that were free.
    ///
    /// All of them rather than only the ones drawn later. Which of two overlapping labels wins is
    /// decided by an order this cannot see reliably, and being wrong costs a spot on a label where
    /// there are forty five others.
    /// </summary>
    private static List<RectangleF> Neighbours(GameController gc, uint mine)
    {
        var found = new List<RectangleF>();
        var labels = Ground.Labels(gc);

        if (labels == null)
            return found;

        foreach (var label in labels)
        {
            if (Safe.Read(() => label.ItemOnGround?.Id ?? 0u, 0u) == mine)
                continue;

            var element = Safe.Read(() => label.Label, null);

            if (element == null || !Safe.Read(element, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(Hit(element), static e => e.GetClientRectCache, default);

            if (rect.Width > 0f && rect.Height > 0f)
                found.Add(rect);
        }

        return found;
    }

    /// <summary>Where on a label the cursor could go, for the dump. Zero when nowhere.</summary>
    /// <summary>
    /// Where on a label the cursor could go, for the dump. Zero when nowhere.
    ///
    /// The entity matters and is not optional: without it the label is cut out of ITSELF, because
    /// it is one of the labels on screen - which the dump duly reported as entirely covered on a
    /// chest that was perfectly clear.
    /// </summary>
    internal static (Vector2 At, float Room) Reachable(GameController gc, RectangleF rect, uint mine) =>
        Exposed(gc, Panels.Covered(gc), rect, mine);

    internal static (RectangleF Rect, bool Lit) Button(GameController gc, Entity of, int[] path)
    {
        var id = Safe.Read(of, static e => e.Id, 0u);

        if (id == 0u)
            return (default, false);

        var labels = Ground.Labels(gc);

        if (labels == null)
            return (default, false);

        foreach (var label in labels)
        {
            if (Safe.Read(() => label.ItemOnGround?.Id ?? 0u, 0u) != id)
                continue;

            var at = Safe.Read(() => label.Label, null);

            foreach (var step in path)
            {
                at = Safe.Kid(at, step);

                if (at == null || !Safe.Read(at, static e => e.IsVisible, false))
                    return (default, false);
            }

            // The label itself is a wrapper. What takes the click is the child inside it, and the
            // difference is not cosmetic: the top fifth or so of the wrapper is empty and clicks
            // pass straight through it. Aiming at the wrapper's middle therefore lands high, in the
            // gap, and the game hands the click to whatever is behind.
            //
            // Only for the label itself - a path given explicitly has already named the element it
            // means, and descending further would name a different one.
            if (path.Length == 0)
                at = Hit(at);

            var rect = Safe.Read(at, static e => e.GetClientRectCache, default);

            return rect.Width > 0f && rect.Height > 0f
                ? (rect, Safe.Read(at, static e => e.HasShinyHighlight, false))
                : (default, false);
        }

        return (default, false);
    }

    /// <summary>
    /// Which child of a remnant's label opens the combinations window.
    ///
    /// Child 0, and this one is not reasoned - it is observed. Hovering the button and dumping lit
    /// up child 0 of that remnant's label and nothing else anywhere in the interface: one
    /// shiny=True among every child of every label in the site. A guess cannot be that clean.
    ///
    /// It is the 54 by 54 element at the RIGHT hand end of the label. The mirror of it on the left,
    /// child 1, stays dark under the cursor, which is how a decoration behaves.
    /// </summary>
    internal static readonly int[] CombinationsPath = [0];

    /// <summary>
    /// Which child shatters a remnant once it is finished with.
    ///
    /// Child 2,0 - a 54 by 54 element inside a 162 by 108 container - found the same way, and it
    /// behaves as a button should: the container reports itself hidden while the remnant is live
    /// and visible once it is spent, and hovering the child inside it was the single lit element in
    /// the interface.
    /// </summary>
    internal static readonly int[] ShatterPath = [2, 0];

    /// <summary>
    /// Which child rerolls a remnant: the Liquid Verisium button, child 4,0,0.
    ///
    /// Here rather than in the overlay because two things need it now - the reroll border draws on
    /// it, and the placement step has to know where it is so it never aims behind one.
    /// </summary>
    internal static readonly int[] RollPath = [4, 0, 0];

    /// <summary>
    /// Every button on a remnant's label that a stray click would cost something for.
    ///
    /// Declared AFTER the three it is built from, which is load bearing rather than tidy: static
    /// fields initialise in declaration order, so listing this above RollPath left a null in the
    /// array. Nothing complained - the read that walked it threw, the diagnostic printed
    /// "unreadable", and the placement check failed closed and refused every spot with a label
    /// under it.
    /// </summary>
    internal static readonly int[][] ButtonPaths = [CombinationsPath, ShatterPath, RollPath];

    /// <summary>
    /// Whether the game is lighting the button up under the cursor.
    ///
    /// HasShinyHighlight is the client's own hover state, so this is the same class of check as
    /// asking the game which markers a blast catches rather than working it out from a radius: it
    /// is the answer rather than a model of the answer. Without it a wrong child index sends a
    /// click into the world, and a click in the world moves the character.
    /// </summary>
    private bool Lit(Target remnant, int[] path) =>
        remnant != null && _game != null && Button(_game, remnant, path).Lit;

    /// <summary>The two checks, and what to do about failing them.</summary>
    private void Check(GameController gc, AutoExpeditionSettings settings, Scan scan)
    {

        // The mode can drop between aiming and arriving - the game takes it away when an explosive
        // lands, and the player can toggle it themselves. Without the circle there is nothing to
        // check against, so put it back rather than measuring nothing.
        if (!Detonator.Showing(gc))
        {
            Arm(gc, settings);

            return;
        }

        // **Read off the entity, and nothing else will do, because the guard below compares this
        // against the raw field.**
        //
        // Detonator.PlacementIndicatorGridPosition falls back to the element's own field when the entity cannot be
        // found - which is the right fallback for a readout and the wrong one here. The
        // out-of-reach test below asks how far the landing is from the spot the plan wants; on
        // the fallback the landing IS the request, so a cursor parked on the spot always answered
        // nought however far the explosive would really go. A safety test that cannot fail is
        // what let the run move the cursor to a stretched aim and click it: the reported fault was
        // "it moved the mouse to the wrong spot and then clicked", and undoing and pressing again
        // placed correctly, because by then the first attempt had taught Snapped where that aim
        // really lands.
        //
        // So an unreadable entity is a frame with no answer rather than a frame that agrees. The
        // run waits, which costs a frame; clicking blind costs an explosive.
        var indicator = Detonator.PlacementIndicatorGridPosition(gc);

        if (indicator == Vector2.Zero)
            return;

        if (Detonator.PlacementIndicatorIsRed(gc))
        {
            // **Said and recorded, never filed.** This used to add the spot to a list the planner
            // then avoided, and then to ask for a re-solve. Both hid the fault: the placeable rule
            // is the game's own routine now, so a refusal it did not predict is wrong about that
            // cell, and routing the next chain around the cell removes the one piece of evidence
            // that would find out why. See Planning.Placeable.
            //
            // **What blocks a spot here is usually a doodad with no entity behind it** - nothing in
            // any bucket stands within twelve grid of the one that caused this - so neither the
            // terrain grid nor the obstacle list can know, and the plugin finds out by being told
            // no. That is a real limit rather than an arithmetic slip, and it is still worth
            // stopping on: a limit nobody is shown is a limit nobody closes, and the alternative
            // was a run that quietly walked around the same unmodelled object at every site.
            //
            // The aim is recorded beside the spot because they can differ. A refusal at an aim
            // past the spot says nothing about the spot itself - the cell being refused is the one
            // the cursor is on - and reading the two as the same fact is what an earlier version
            // of this did.
            var aimedAt = _aimedGridPosition == Vector2.Zero ? _target : _aimedGridPosition;

            PlacementDisagreements.Here.NoteRefusal(_landed + 1, _target, aimedAt,
                Detonator.LastExplosiveGridPosition(gc), indicator, Detonator.PlacementRange(gc));

            // A refusal has no markers to ring - the explosive did not go anywhere. Cleared so the
            // label above does not end up on triangles left by an earlier failure.
            _unlit = Array.Empty<Vector2>();
            Reason = "Refused";

            Give("Placement refused", $"the game will not take an explosive at ({_target.X:0},{_target.Y:0})" +
                            (Cell(aimedAt) == Cell(_target)
                                ? ""
                                : $", aimed at from ({aimedAt.X:0},{aimedAt.Y:0})") +
                            " - the plan decided that cell was placeable and the client disagrees, " +
                            "so the reading is in the F6 dump");

            return;
        }

        var off = Vector2.Distance(indicator, _target);

        // **The indicator is on the spot the plan chose, or there is no click.**
        //
        // The placement indicator is not a hint about where the explosive might go - it is where
        // the explosive goes. So the whole of the safety question is whether it is standing on the
        // cell the plan asked for, and a landing anywhere else is a failure however good it looks.
        //
        // Grid cells rather than exact coordinates, because the two sides are not the same kind of
        // number. A plan spot comes off an entity position and is fractional; a landing is built by
        // the game as an integer point plus an integer offset and is not. Comparing the floats
        // could never agree, so the comparison is at the granularity both can express - the same
        // one Refused already works in.
        //
        // **This used to accept a landing that covered everything the plan wanted, wherever it
        // sat.** The argument was that coverage is the game's own answer and position is only this
        // plugin's aim. It no longer holds: the aim is computed from the game's own routing
        // routine, so a landing away from the spot means the model of that routine was wrong, and
        // that is the one thing worth hearing about rather than papering over. It is also not safe
        // on its own - an explosive a couple of grid inward still catches its markers and the next
        // link then has to stretch from somewhere the chain was never planned from.
        if (Cell(indicator) != Cell(_target))
        {
            // The indicator animates towards the cursor, so the first frame after a move can show
            // the previous spot. Read again before calling it a failure. Frames rather than
            // milliseconds, matching _short below; the run is already parked and costs nothing by
            // waiting.
            if (++_offSpotFrames < SettleFrames)
                return;

            // **A landing away from the planned spot is a fault in the model, and the run stops on
            // it rather than working round it.**
            //
            // The aim was computed from the game's own routing routine - route, string-pull, cut,
            // and the endpoint refinement. If the indicator is not standing where that computation
            // said it would, one of those steps disagrees with the client, and the evidence for
            // which is on screen at this exact moment: the origin, the spot, the aim sent, the
            // reach, and where the game actually put it.
            //
            // Replanning threw that away. It asked the solver for a different chain, the cursor
            // moved on, and the next reading was about a different pair - so the disagreement was
            // never looked at and the model kept the fault. The mend path was worse: it adjusted
            // the plan around the failure automatically, which is the same paper-over with a
            // shorter wait. Neither is what a wrong model wants; being told is.
            //
            // Nothing is filed against the ground either. A link falling short is not a statement
            // about the square - it is a statement about the PAIR, this origin to that spot - and
            // Spoil used to ring three grid around it. That banned a seven by seven block of
            // perfectly good ground for the rest of the site, the clamped cell included, which is
            // the one place the game had just said an explosive could go. Measured: 41 refused
            // cells at one site, in blobs of exactly that shape, with every re-solve from run 2
            // onwards dropping its floor because the standing chain's first spot had been ruled
            // out from under it.
            //
            // What is recorded here is the part the per-frame readings do not carry: which spot the
            // plan wanted, and what this run aimed at to get there.
            var aimed = _aimedGridPosition == Vector2.Zero ? _target : _aimedGridPosition;
            var from = Detonator.LastExplosiveGridPosition(gc);

            PlacementDisagreements.Here.NoteWrongSpot(_landed + 1, _target, aimed, from, indicator,
                Detonator.PlacementRange(gc));

            // What is ACTUALLY lost, which is not everything the blast was for. The game is already
            // answering at the position it chose, so the same shortfall the not-lit path uses gives
            // the honest list: the markers that would not light from where the explosive can go.
            // Ringing the whole expected set said ten markers were unreachable when the real cost
            // was two, which is the kind of overstatement that stops a warning being believed.
            Shortfall(gc, settings, scan, out var beyond);

            _unlit = beyond.ToArray();
            Reason = "Wrong spot";

            // Where the cursor was sent and where it is, and which cell the pick simulation makes of that pixel, so a
            // landing off the spot says whether the cursor left its rect, the simulation disagrees with the game, or
            // the routing model is wrong. See AimRect.
            var cursorPixel = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY),
                Vector2.Zero);
            var cursorCell = Safe.Read(gc, static g =>
            {
                var at = g.IngameState.ServerData.GridMousePosition;

                return new Vector2(at.X, at.Y);
            }, Vector2.Zero);
            var simulated = SimulatedCell(gc, Safe.Read(gc, static g => g.IngameState.Camera, null), cursorPixel, aimed);

            Give("Placement lands wrong", $"spot {_landed + 1} was aimed at ({aimed.X:0},{aimed.Y:0}) to land " +
                               $"on ({_target.X:0},{_target.Y:0}), and the game puts the explosive " +
                               $"on ({indicator.X:0},{indicator.Y:0}) instead - the cursor was sent into " +
                               $"({_aimRect.Left:0.#}-{_aimRect.Right:0.#}, {_aimRect.Top:0.#}-{_aimRect.Bottom:0.#})px, " +
                               $"and is at ({cursorPixel.X:0},{cursorPixel.Y:0})px over cell ({cursorCell.X:0},{cursorCell.Y:0}); " +
                               (simulated == null
                                   ? "the pick simulation could not read that pixel"
                                   : $"the pick simulation reads that pixel as ({simulated.Value.X:0},{simulated.Value.Y:0})"));

            return;
        }

        _offSpotFrames = 0;

        var (lit, missing, unknown) = Coverage(gc, scan);

        // The game is highlighting exactly what this explosive would take, and it has to agree that
        // nothing the spot was chosen for is missing. At least one marker must be confirmed lit -
        // "nothing was missing" means nothing at all when nothing was loaded to ask.
        if (missing == 0 && lit > 0)
        {
            Land(gc, settings, off, lit, unknown);

            return;
        }

        // Something this spot was chosen for is not lit, so the blast is not the one that was
        // planned and the run stops on it.
        //
        // **A shortfall used to be weighed and a small one landed anyway.** Up to a sixth of the
        // blast's expected worth could be missing and the click still went in, on the argument that
        // the plan credits a blast with the markers inside a circle while the game highlights what
        // it would really take, and uneven ground makes those differ - a marker over a lip sits
        // inside the circle and does not light.
        //
        // That is an explanation of the disagreement, not a reason to act on the plan anyway. The
        // whole point of asking the game is that its answer decides; a threshold that lands the
        // blast regardless means the answer was only consulted when convenient, and the difference
        // between the planned blast and the one that goes off is never looked at. The markers are
        // named on the ground instead and the run says so.
        //
        // Confirmed over two frames first, because the indicator moving and the highlighting
        // catching up with it are not guaranteed to be the same frame.
        if (missing > 0)
        {
            if (++_short < 2)
                return;

            var (lost, wanted) = Shortfall(gc, settings, scan, out var dark);

            // Written down either way - the planner is wrong about those two cells whether or not
            // this blast is worth landing, and the next chain should know. See Missed.
            // **Only for the spot the PLAN chose, never for a probe.** Missed is a record of where
            // the planner was wrong, and a probe position is not somewhere the planner ever asked
            // for - writing one down teaches it a true but useless fact about a cell nothing will
            // ever use again. Measured: sixteen probes against a couple of dozen dark markers put
            // three hundred and sixty seven entries into a list that had held single figures, and
            // every one of them was noise the next solve had to carry.
            // Every spot tried, including the probes.
            //
            // **A probe is a measurement, not a guess, and throwing it away wasted the whole
            // sweep.** Sixteen spots were visited and the game said what each of them catches; not
            // recording that left the planner knowing one bad cell out of seventeen, free to choose
            // the next one along and walk there to find out what had just been found out.
            //
            // The flood this used to cause was not the probes - it was exact-cell recording with no
            // spread, so every one of them was a separate fact about a separate point. With the
            // neighbourhood written down instead, sixteen probes over a few dark markers is a
            // patch of ground ruled out, which is what was actually learnt. See Missed.
            var learnt = false;

            foreach (var want in dark)
                learnt |= Missed.Here.Note(_target, want, _landed + 1);


            // **The sweep is gone and the markers are named instead.**
            //
            // Walking the alternatives was a lot of machinery for a poor answer: it moved the cursor
            // over twenty spots, took seconds, learnt one cell at a time, and usually arrived where
            // it started - because a marker that will not light from the planner's own choice
            // generally will not light from the ground beside it either. Three separate bugs lived
            // in it, all from the probe moving the target under everything else that reads it.
            //
            // What is actually wanted is to be TOLD. The run stops, the markers that would not light
            // are marked on the ground, and the decision is handed back to somebody who can see the
            // site - which is the same bargain the rest of this makes when the game and the model
            // disagree. See Overlay's Unlit.
            _unlit = dark.ToArray();
            Reason = "Unlit";

            Spoil(_target);

            Give("Marker not lit", learnt
                ? $"blast #{_landed + 1}: {missing} of the {_expect.Length} markers it was chosen " +
                  $"for are not highlighted, worth {lost:N0} of {wanted:N0} - they are ringed in " +
                  "red on the ground; working out a chain that reaches them"
                : $"blast #{_landed + 1}: {missing} of the {_expect.Length} markers it was chosen " +
                  "for are not highlighted and are ringed in red on the ground - the blast is not " +
                  "the one that was planned");

            return;
        }

        _short = 0;

        // Expected something here and could not confirm ANY of it, because the game has those
        // markers unloaded. That is not the same as a clean check and it must not be treated as
        // one: the whole reason to trust an automated click is that the game has been asked what
        // the blast would catch and has agreed, and here it has not been asked.
        //
        // One marker lit is enough - it proves the blast is where it is believed to be, and with
        // nothing missing the rest follows. Zero lit with things unloaded proves nothing at all.
        if (lit == 0 && unknown > 0)
        {
            Give("Marker load failure", $"{unknown} expected markers are not loaded, so the blast cannot be " +
                               "confirmed - walk closer and press again");

            return;
        }

        Land(gc, settings, off, lit, unknown);
    }

    /// <summary>
    /// What the game's own state was in the frame the click went out, in one sentence.
    ///
    /// **The half that was missing when a run failed.** Two booleans - mode on, circle up - rule out
    /// the two cheap explanations and leave every real one open, which is how "the click went out
    /// and the game placed nothing" came to be followed by a guess. Where the cursor was, where the
    /// game said the explosive would land, and whether it was already refusing are the three things
    /// that separate a click that missed from a click the game threw away.
    /// </summary>
    private string Clicked(GameController gc)
    {
        var now = Safe.Read(() => Detonator.PlacementIndicatorGridPosition(gc), Vector2.Zero);

        return $"At the click: placement mode {(_activeAtClick ? "ON" : "OFF")}, circle " +
               (_showingAtClick ? "up" : "DOWN") +
               $", cursor at screen ({_clickedAt.X:0},{_clickedAt.Y:0}), aimed at " +
               $"({_target.X:0},{_target.Y:0}), the game's landing " +
               (_landingAtClick == Vector2.Zero
                   ? "unreadable"
                   : $"({_landingAtClick.X:0},{_landingAtClick.Y:0}), " +
                     $"{Vector2.Distance(_landingAtClick, _target):0.#} grid off") +
               $", and the spot reading {(_blockedAtClick ? "BLOCKED" : "clear")}" +
               (now != Vector2.Zero
                   ? $"; the landing now reads ({now.X:0},{now.Y:0})."
                   : "; no indicator now.");
    }

    /// <summary>Clicks, or says what it would have clicked. Both gates have passed by here.</summary>
    private void Land(GameController gc, AutoExpeditionSettings settings, float off, int lit, int unknown)
    {
        var covered = $"{lit} expected markers lit" +
                      (unknown > 0 ? $", {unknown} could not be checked (not loaded)" : "");

        // Last of all, and after everything else has passed: is there a piece of interface under
        // the cursor? The planned spot is a world position and the game draws its UI over the
        // world, so a perfectly good blast site can have a button sitting on top of it - and the
        // click would open that button instead of placing anything.
        //
        // Checked here rather than at aiming time because the cursor arrives some frames later and
        // the interface moves: a tooltip follows the pointer, a health globe fills, a quest tracker
        // expands. What matters is what is under the cursor in the frame the click goes out.
        if (Panels.UnderCursor(gc))
        {
            // Says WHAT it saw, because "something in the interface" sends you to a dump to find
            // out which of a hundred elements it meant - and the answer is one line the game can
            // hand over right now.
            Give(Obscured, "a click would hit " + Panels.Describe(gc) + " - move the camera");

            return;
        }

        // **What the game's placement state was AT the click**, because the timeout can only see it
        // afterwards and the two are different questions. A click into placement mode that is not on
        // does nothing, silently, and the lit-marker gate does not rule that out: a marker also
        // stays lit from an explosive already placed, so a site elsewhere on the map with an
        // undetonated chain can satisfy it while this site's circle is down. See Confirming.
        _activeAtClick = Safe.Read(() => Detonator.Placing(gc), false);
        _showingAtClick = Safe.Read(() => Detonator.Showing(gc), false);
        _clickedAt = Safe.Read(() => new Vector2(gc.IngameState.MousePosX, gc.IngameState.MousePosY),
            Vector2.Zero);

        _landingAtClick = Safe.Read(() => Detonator.PlacementIndicatorGridPosition(gc), Vector2.Zero);
        _blockedAtClick = Safe.Read(() => Detonator.PlaceableNow(gc), null) == false;

        // The click is the whole point of the run, so a refused one is reported rather than waited
        // on. See Sent.
        if (!Sent(_input.Click(), $"the click placing explosive {_landed + 1}"))
            return;

        Wait(Step.Confirming);
        Say($"Placing {_landed + 1}", $"placing explosive {_landed + 1} - {off:0.#} grid off, {covered}");
    }

    /// <summary>
    /// What the game says this blast will catch, against what the plan chose the spot for.
    ///
    /// The last gate before the click and the one that decides. The position check only says the
    /// cursor is where it was sent; this is the GAME stating which markers the explosive would
    /// take, so it needs no radius, no disc model and no assumption about the shape of a blast.
    ///
    /// The expected set comes from the plan, which is what makes the question the right one: this
    /// spot was chosen because these markers fall in it, so one of them not lighting up means the
    /// blast about to happen is not the blast that was planned.
    ///
    /// Markers lit that the plan did not expect are not counted against it. A blast catching MORE
    /// than intended is a better blast, and the plan being pessimistic is no reason to refuse it.
    ///
    /// A marker the game has unloaded cannot be asked whether it is glowing. Those are counted
    /// apart and reported rather than quietly passed over - "everything expected is lit" reads very
    /// differently from "everything expected is lit, and two more could not be asked".
    /// </summary>
    /// <returns>How many expected markers are lit, how many are not, and how many could not be asked.</returns>
    /// <summary>
    /// How much a shortfall is worth, against what the spot was chosen for.
    ///
    /// A share rather than a number, because weights are a preference: a player who scales theirs
    /// up would otherwise find a tolerance set in points quietly becoming a tolerance of nothing.
    ///
    /// Only markers the game is actually answering about. One it has unloaded is not lit and is not
    /// missing either, and treating those as unreachable would teach the planner nonsense about
    /// ground nobody has looked at.
    /// </summary>
    private (float Lost, float Wanted) Shortfall(GameController gc, AutoExpeditionSettings settings,
        Scan scan, out List<Vector2> dark)
    {
        dark = new List<Vector2>();

        var lost = 0f;
        var wanted = 0f;

        if (_expect.Length == 0 || !Detonator.Showing(gc))
            return (0f, 0f);

        var here = scan.At(Detonator.DetonatorGridPosition(gc));

        foreach (var want in _expect)
        {
            foreach (var target in here)
            {
                if (Vector2.Distance(target.Grid, want) >= 1f)
                    continue;

                var worth = MathF.Max(0f, Weighing.WeightOfTarget(target, settings));

                wanted += worth;

                if (target.Live && target.GlowReadable && !target.Glowing && !target.InPlacedBlast)
                {
                    lost += worth;
                    dark.Add(want);
                }

                break;
            }
        }

        return (lost, wanted);
    }

    private (int Lit, int Missing, int Unknown) Coverage(GameController gc, Scan scan)
    {
        // Nothing lights up when the game is not showing a placement circle, so the check would
        // report every marker as missing and be measuring its own absence.
        if (_expect.Length == 0 || !Detonator.Showing(gc))
            return (0, 0, 0);

        var here = scan.At(Detonator.DetonatorGridPosition(gc));
        var lit = 0;
        var missing = 0;
        var unknown = 0;

        foreach (var want in _expect)
        {
            Target found = null;

            foreach (var target in here)
            {
                if (Vector2.Distance(target.Grid, want) < 1f)
                {
                    found = target;

                    break;
                }
            }

            // Taken by an explosive already down counts as lit: the client does not light it again under
            // the circle, and this blast losing it loses nothing. See Target.InPlacedBlast.
            // A marker whose highlight cannot be read is not known to be dark. See Target.GlowReadable.
            if (found is not { Live: true } || !found.GlowReadable)
                unknown++;
            else if (found.Glowing || found.InPlacedBlast)
                lit++;
            else
                missing++;
        }

        return (lit, missing, unknown);
    }


    /// <summary>
    /// Sends the cursor to the spot in hand.
    ///
    /// The circle comes up FIRST, and this is where that is guaranteed rather than merely arranged:
    /// aiming with no circle showing hands straight back to <see cref="Arm"/> instead of moving.
    /// The order matters because the two are not interchangeable - pressing the key is what makes a
    /// click place an explosive, so a cursor that travels first is a cursor sitting on a spot that
    /// cannot be used yet, and a key pressed afterwards is one pressed with the pointer already out
    /// in the world.
    ///
    /// Arm only aims once the circle is up, and this only arms while it is not, so between them
    /// there is exactly one hop and no way round the ordering.
    /// </summary>
    private void Aim(GameController gc, AutoExpeditionSettings settings)
    {
        if (!Detonator.Showing(gc))
        {
            Arm(gc, settings);

            return;
        }

        if (!Reachable(gc, out var at, out var word, out var why))
        {
            Give(word, why);

            return;
        }

        // How near the game's reported cursor must be to the point sent before the move counts as arrived.
        // ExileInput2 ends every journey exactly on the point it was given; this only gates Arrived, and a
        // tolerance of a pixel or two risks a move that never reports arriving and times out.
        // A rect rather than a point, so ExileInput2 picks where in it to land the way it does for any
        // rect - wandering about the centre from one explosive to the next - and bounds its arrival
        // tolerance by the rect. See AimRect.
        _input.SetTolerance(12);
        _input.MoveTo(_aimRect);
        Wait(Step.Moving);
        Say($"Moving {_landed + 1}", $"moving to spot {_landed + 1}");
    }

    /// <summary>
    /// Whether the cursor could be put on the planned spot at all, and what is in the way if not.
    ///
    /// Asked BEFORE the placement circle is brought up, which is the point of it being its own
    /// method. It used to live in the aiming step alone, so a spot behind a panel or a button went:
    /// tap V, circle appears, aim, refuse, tap V again to put the circle away. Two keypresses and a
    /// flash of the interface to arrive at an answer that was knowable before the first one - and
    /// the same two next press, because nothing about the situation had changed.
    ///
    /// Still checked at aiming time as well. The camera can move between arming and aiming, and
    /// this is cheap.
    /// </summary>
    /// <summary>
    /// Where the cursor has to be for the explosive to land on a spot.
    ///
    /// The planned spot, which is where the run has always sent it.
    /// </summary>
    private static Vector2 Pointed(GameController gc, Vector2 spot) =>
        Pointed(gc, Vector2.Zero, spot);

    /// <summary>The same, asked about a spot laid from a given link rather than from the last.</summary>
    /// <param name="from">The link this one is placed from, or zero for the newest one down.</param>
    private static Vector2 Pointed(GameController gc, Vector2 from, Vector2 spot)
    {
        return spot;
    }

    private bool Reachable(GameController gc, out Vector2 at, out string word, out string why)
    {
        word = "";
        why = "";

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);

        // **Where to point, which is not always where the explosive goes.** See Pointed.
        // Kept, because the check after the click has to name what was aimed at. A landing away
        // from the spot is only diagnosable alongside the aim that was supposed to produce it, and
        // recomputing it later would ask a model that has learnt from the failure in between.
        _aimedGridPosition = Pointed(gc, _target);

        var (aimRect, verdict) = AimRect(gc, camera, _aimedGridPosition);

        _aimRect = aimRect;
        at = verdict == AimVerdict.Aimed ? aimRect.Center : Vector2.Zero;

        // **A cell the game cannot be made to read reliably from here is said before the circle comes up.** From
        // a shallow angle the line of sight can run along the ground across the whole cell, and then no pixel is
        // safe; coming closer steepens it. See AimRect.
        if (verdict == AimVerdict.Unreliable)
        {
            word = "Bad camera angle";
            why = $"no point of cell ({MathF.Floor(_aimedGridPosition.X):0},{MathF.Floor(_aimedGridPosition.Y):0}) " +
                  "reads reliably as that cell from this camera angle - the ground there runs nearly along the line " +
                  "of sight; move closer and press again";

            return false;
        }

        // Edge, like every other test about a spot on the ground. See OnScreen's margin.
        if (at == Vector2.Zero || !OnScreen(gc, at, Edge))
        {
            word = "Walk closer";
            why = "the next spot is off screen - walk towards it and press again";

            return false;
        }

        // A planned spot that projects under a panel must not be clicked: the click would land on
        // the panel, and the panel in that corner is the one holding the button that starts the
        // encounter. Refusing is the only safe answer.
        if (Panels.Covers(Panels.Covered(gc), at))
        {
            word = "Unclickable";
            why = "the next spot is behind a panel - move the camera and press again";

            return false;
        }

        if (Panels.Covers(Buttons(gc), at))
        {
            word = "Unclickable";
            why = "the next spot is under one of a remnant's buttons - move the camera so the " +
                  "spot is clear, and press again";

            return false;
        }

        // **And everything else the interface paints, which the two tests above do not cover.**
        //
        // Covered is a list of the panels this plugin knows by name and Buttons is three rectangles
        // on a ground label. Neither holds the skill bar - and a left click over a skill does not
        // place an explosive, it uses the skill. So a spot projecting into that corner passed both
        // tests, the cursor travelled to it, and the run then refused at the click with "Click
        // location obscured", naming the skill it would have hit.
        //
        // Panels.Blocked is the test that knows: it walks what the interface actually paints at a
        // point and ends in the same judgement the hover check makes, so it is the same answer the
        // click would have given - arrived at before the mouse moves rather than after. Ready has
        // always used it, which is why the spot was not drawn green; only the run had not asked.
        //
        // Fresh, because this is the decision to act. A stale "clear" here is the one error this
        // must not make. See Panels.Blocked.
        if (Panels.Blocked(gc, at, true))
        {
            word = Obscured;
            why = "the next spot is under " + Panels.Blocker(gc, at) + " - move the camera";

            return false;
        }

        return true;
    }

    /// <summary>
    /// The buttons on expedition ground labels, as rectangles the cursor must not be sent into.
    ///
    /// Known BEFORE the cursor moves, which is the whole point. The click already refuses when the
    /// game reports one of these under the pointer, but by then the cursor has travelled, the run
    /// has spent a second, and the answer is "no" - and the next press does exactly the same thing,
    /// because nothing about the situation changed. Reading the rectangles up front turns that into
    /// an instant, specific refusal that names the button in the way.
    ///
    /// Three of them: the Runeshape Combinations button, the shatter button, and the Liquid
    /// Verisium. They are the ones a stray click actually costs something for - it opens a window
    /// over the site, shatters a remnant early, or spends an orb - which is the same rule the hover
    /// check settled on, arrived at here from the other end.
    ///
    /// Only labels belonging to expedition content are consulted, so the help icon on a ritual
    /// altar is not in this list and never was: a click goes through it into the world.
    /// </summary>
    private static List<RectangleF> Buttons(GameController gc)
    {
        var found = new List<RectangleF>();
        var labels = Ground.Labels(gc);

        if (labels == null)
            return found;

        foreach (var label in labels)
        {
            var metadata = Safe.Read(() => label.ItemOnGround?.Metadata, "") ?? "";

            // **A remnant's, not any expedition label's, and that distinction cost the whole
            // afternoon.** These are child indices into a remnant's label - CombinationsPath is
            // simply [0] - and a strongbox's label has a first child too: the entire content block,
            // six hundred and sixty pixels by a hundred and thirty eight. Walked as a button, it
            // made every planned spot behind any strongbox read as "behind a button on a remnant's
            // label", which is a sentence that should have been a clue on its own since there was no
            // remnant anywhere near.
            if (!metadata.StartsWith(Scan.RemnantMetadata, StringComparison.Ordinal))
                continue;

            var element = Safe.Read(() => label.Label, null);

            if (element == null)
                continue;

            foreach (var path in new[] { CombinationsPath, ShatterPath, RollPath })
            {
                var rect = Child(element, path);

                if (rect.Width > 0f && rect.Height > 0f)
                    found.Add(rect);
            }
        }

        return found;
    }

    /// <summary>One element inside a label by its path of child indices, when it is drawn.</summary>
    private static RectangleF Child(Element label, int[] path)
    {
        var at = label;

        foreach (var index in path)
        {
            at = Safe.Kid(at, index);

            if (at == null)
                return default;
        }

        return Safe.Read(at, static e => e.IsVisible, false)
            ? Safe.Read(at, static e => e.GetClientRectCache, default)
            : default;
    }

    /// <summary>Whether a projected point is somewhere the cursor can usefully be put.</summary>
    private static bool OnScreen(GameController gc, Vector2 at) => OnScreen(gc, at, Margin);

    /// <summary>
    /// Whether a point is inside the window, with a chosen margin.
    ///
    /// **The margin is about what is being clicked, not about the screen.** Forty eight pixels is
    /// right for a LABEL: they are small, the cursor drifts, and the edge of one is where a click
    /// slides off onto whatever is behind it. For a spot on open ground it is far too much - a
    /// planned blast projecting to ten pixels from the top of the screen is plainly visible and
    /// perfectly clickable, and the label's margin called it off screen.
    /// </summary>
    private static bool OnScreen(GameController gc, Vector2 at, float margin)
    {
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (window.Width <= 0f || window.Height <= 0f)
            return false;

        return at.X > margin && at.Y > margin &&
               at.X < window.Width - margin && at.Y < window.Height - margin;
    }

    /// <summary>
    /// Whether a spot is genuinely in front of the camera, rather than mirrored round from behind.
    ///
    /// **A point behind the camera projects to the wrong place, not to nowhere.** The transform
    /// mirrors it in front of the viewer, so a spot below and behind you comes back as an ordinary
    /// screen coordinate - often in a corner, under the permanent furniture. The window test cannot
    /// tell that apart from a real one, so the plugin went on to ask what was painted there and
    /// refused the spot as blocked by a life orb it was never actually near.
    ///
    /// Worse than a wrong answer, it is an expensive one: what is painted at a point is found by
    /// descending the whole interface tree, and doing that for spots that are not on the screen at
    /// all is most of the work and all of the noise.
    ///
    /// A short step from the player towards the spot cannot be behind the camera when the player is
    /// not, so its projected direction is trustworthy whatever the far end does. If the far end
    /// projects the opposite way, it is behind. Same test the overlay uses to aim its lines - see
    /// Overlay.Toward, which this is deliberately the same shape as.
    ///
    /// True when the step cannot be read, which leaves the caller with the behaviour it had.
    /// </summary>
    private static bool Ahead(GameController gc, Camera camera, Vector3 world, Vector2 point)
    {
        var me = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (camera == null || me == Vector3.Zero || world == Vector3.Zero)
            return true;

        var along = world - me;
        var length = along.Length();

        if (length <= 0.001f)
            return true;

        var from = Safe.Read((camera, me), static x => x.camera.WorldToScreen(x.me), Vector2.Zero);

        // Ten grid out, which is far enough that the projected step is longer than the rounding on
        // it and near enough that it cannot itself have gone behind the viewer.
        var near = me + along / length * (10f * Detonator.GridToWorld);
        var step = Safe.Read((camera, near), static x => x.camera.WorldToScreen(x.near),
            Vector2.Zero);

        if (from == Vector2.Zero || step == Vector2.Zero || step == from || point == from)
            return true;

        return Vector2.Dot(Vector2.Normalize(step - from), Vector2.Normalize(point - from)) > 0f;
    }

    /// <summary>
    /// How far from the edge a spot on the ground still counts as reachable, in pixels.
    ///
    /// Small but not nought: a click landing on the very outermost pixel is one the cursor can drift
    /// off entirely, and a click that misses in this game moves the character.
    /// </summary>
    private const float Edge = 6f;

    /// <summary>
    /// What the HUD says when the cursor cannot be put where the click needs to land.
    ///
    /// One phrase for two causes, deliberately. Something in the interface sitting over a blast
    /// site and a loot label sitting off the edge of the screen are different problems to the code
    /// and the same problem to the player: the place that needs clicking cannot be clicked, and the
    /// answer to both is to move. The sentence underneath still says which it was.
    /// </summary>
    private const string Obscured = "Unclickable";

    /// <summary>Says one thing two ways: a word for the HUD, a sentence for the debug line.</summary>
    private void Say(string status, string detail = null)
    {
        // Not a word about moving on once this run has put an explosive down: the run stops at the edge of what it can
        // reach, and the player already knows the next spot is further on.
        if (!(_landed > 0 && MovingOn.Contains(status)))
            CursorWarning.Show(status);

        _status = status;
        _lingers = DateTime.UtcNow + Linger;
        Detail = _landed > 0 ? $"placed {_landed}; {detail ?? status}" : detail ?? status;

        // Kept past its welcome, for the dump. See Said.
        if (status.Length > 0 || detail is { Length: > 0 })
        {
            Said = detail is { Length: > 0 } ? $"{status}: {detail}" : status;
            SaidAt = DateTime.UtcNow;
        }
    }

    /// <summary>The warnings that only say the next spot is further away. See Say.</summary>
    private static readonly HashSet<string> MovingOn = new(StringComparer.Ordinal) { "Bad camera angle", "Too far" };

    /// <summary>Hands the cursor back and says why. Every way out of the sequence goes through here.</summary>
    /// <summary>The grid cell a spot rounds to, which is the granularity Refused works in.</summary>
    private static (int X, int Y) Cell(Vector2 at) =>
        ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));

    /// <summary>
    /// The run never restarts itself, and that is a decision rather than an omission.
    ///
    /// **A run that stops, lets go of the mouse, and then takes it back is worse than one that
    /// never tried.** It was briefly written the other way: a spot that could not be lit set a flag,
    /// the chain was re-solved in the background, and placement began again on its own. Watched in
    /// play it reads as the plugin giving up - the cursor stops, the player reaches for the mouse -
    /// and then the cursor moves again a second later, which is the one thing automation driving a
    /// pointer must never do.
    ///
    /// So the recovery all happens INSIDE the run, continuously, while the player can see it: the
    /// alternatives are computed from the geometry and walked one after another. If none of them
    /// work the run ends, the mouse is handed back, and it stays handed back until the key is
    /// pressed again. A background re-plan still happens, because that costs nobody the pointer.
    /// </summary>

    /// <summary>
    /// The last thing a run said, kept after the on-screen message has gone.
    ///
    /// **Status clears itself, which is right on screen and useless afterwards.** "It went green and
    /// then failed" is a question about something that already happened, and by the time the dump is
    /// written the line that would have explained it has timed out. This keeps the last one whatever
    /// its age, with the age attached so a stale answer cannot be mistaken for a fresh one.
    /// </summary>
    public static (string Said, string Why, DateTime When) Last { get; private set; }

    /// <summary>
    /// Whether an input actually went out, and an honest refusal when it did not.
    ///
    /// **Every one of these returns a bool and not one of them was read.** Click, MoveTo and Tap all
    /// answer whether the thing happened - false when ExileInput2 has no such bridge method, when
    /// the lease was refused, when the game is not focused or is taking typed input, or when the
    /// player has taken the mouse back. Thrown away, a refusal is indistinguishable from success:
    /// the run advances to the step that waits for the result, the result never comes, and some
    /// seconds later it reports "Timeout" - which says when the run gave up and nothing about why.
    ///
    /// Seen exactly that way: the cursor walked to the spot, every gate passed - zero grid off,
    /// three expected markers lit - and then nothing was placed and nothing was said.
    ///
    /// Stopped is asked because it usually knows: it names the player taking the mouse back, the
    /// window losing focus, and the game taking typed input. Where it says nothing, the honest
    /// answer is that the bridge refused and the reasons are listed rather than guessed at.
    /// </summary>
    private bool Sent(bool went, string what)
    {
        if (went)
            return true;

        var why = Safe.Read(() => _input.Stopped(), "") ?? "";

        Give("ExileInput2 input refused",
            $"{what} was not sent - " +
            (why.Length > 0
                ? why
                : "ExileInput2 refused it: no lease, the game is not focused, it is taking typed " +
                  "input, or this build has no such method") +
            " - press again");

        return false;
    }

    private void Give(string status, string detail = null)
    {
        Last = (status, detail ?? "", DateTime.UtcNow);

        // Put the circle away if this is what brought it out. After an explosive actually lands the
        // game has already taken it down, so the test for it still showing is what keeps this from
        // arming the player by mistake on the way out.
        //
        // Not after the player has taken the mouse back. The lease is gone by then and they are
        // driving; sending a key into that is the plugin arguing with somebody who has stopped it.
        if (_armed && _game != null && !_input.WasStopped() && Detonator.Showing(_game))
            _input.Tap(_key);

        _armed = false;

        _input.Release();
        _step = Step.Idle;
        Say(status, detail);
    }

    /// <summary>
    /// Drops the marks explaining the last placement, leaving the run alone.
    ///
    /// **An undo takes the question away, and only the automated path noticed.** The triangles and
    /// their reason say why the explosive that just landed did not collect what it was chosen for -
    /// so they were cleared when the NEXT one landed, which is the moment the answer stops being
    /// about the spot in front of you. Taking that explosive back off is the same moment arriving
    /// from the other direction, and nothing was watching for it: the spot is empty, the plan is
    /// re-solved, and a red triangle still sits over a marker explaining a blast that is no longer
    /// there. Placed by hand it never cleared at all, because the clearing lived inside a run that
    /// had not happened.
    ///
    /// Separate from Forget, which also stops the run and says so - right for a key press, wrong
    /// here. An undo mid-run is an undo, not a cancellation. See AutoExpedition's undo handling.
    /// </summary>
    public void Unmark()
    {
        _unlit = Array.Empty<Vector2>();
        _spoiled.Clear();
        Reason = "";
    }

    /// <summary>Abandons whatever it was doing, for a zone change or a cancelling key press.</summary>
    public void Forget()
    {
        // A different dig site, so the marks are about ground nobody is standing on - and so is a
        // site's refusal to take anything. See _declined.
        _declined = 0;
        _cleared = false;
        _unlit = Array.Empty<Vector2>();
        _spoiled.Clear();
        Reason = "";

        if (_step != Step.Idle)
        {
            Give("Stopped", "stopped by a key press");
        }
        else
        {
            _status = "";
            Detail = "";
        }
    }

    /// <summary>
    /// The whole allowance for opening a combinations window, walk included.
    ///
    /// **A remnant you are placing explosives around is a few steps away, not across the site.** The
    /// twenty seconds this used to get came from the chest pass, where the walk really can be long;
    /// here it only ever bought a longer silence when something had gone wrong. Three seconds covers
    /// the walk that actually happens and bounds the one that does not.
    /// </summary>
    private static readonly TimeSpan Opens = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the character must stand still after the click before the window is given up on.
    ///
    /// Half a second. Not the ordinary patience, which is about how long the CLIENT may take to
    /// answer a question - this is about whether a walk started, and a walk that has not started in
    /// half a second is not going to. Short enough that a missed click reads as a missed click
    /// rather than as something in progress, which is the whole fault being fixed.
    /// </summary>
    private static readonly TimeSpan Standstill = TimeSpan.FromSeconds(0.5);

    /// <summary>Where the character was when the current wait started, and when it last moved.</summary>
    private Vector2 _stoodAt;

    private DateTime _stood;

    /// <summary>Marks the character as standing still from now. See Step.Opened.</summary>
    private void Standing(GameController gc)
    {
        _stoodAt = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        _stood = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether the character has moved since the last time this was asked.
    ///
    /// Two grid, which is more than the jitter on a standing character and far less than a step.
    /// </summary>
    private bool Moving(GameController gc)
    {
        var at = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (at == Vector2.Zero || Vector2.Distance(at, _stoodAt) <= 2f)
            return false;

        _stoodAt = at;
        _stood = DateTime.UtcNow;

        return true;
    }

    /// <summary>How long the current step has left, in seconds, for the readout.</summary>
    private double Left() => Math.Max(0d, (_deadline - DateTime.UtcNow).TotalSeconds);

    private void Wait(Step step) => Wait(step, Patience);

    private void Wait(Step step, TimeSpan patience)
    {
        _step = step;
        _deadline = DateTime.UtcNow + patience;
    }

    /// <summary>The screen rect the cursor is sent into for the spot in hand. See AimRect.</summary>
    private RectangleF _aimRect;

    /// <summary>Which of the safe points in a cell the cursor goes to. See AimRect.</summary>
    private static readonly Random AimChoice = new();

    /// <summary>What AimRect found. See AimRect.</summary>
    private enum AimVerdict
    {
        /// <summary>A rect whose every point the game reads as the aimed cell.</summary>
        Aimed,

        /// <summary>The camera or the height map could not be read, or the cell does not project.</summary>
        NotProjected,

        /// <summary>The cell projects, but no point of it reads as the cell with margin from this camera.</summary>
        Unreliable,
    }

    /// <summary>How far from its cell's centre a candidate aim point may be, as a fraction of a cell each way.</summary>
    private const float AimSpread = 0.35f;

    /// <summary>Candidate aim points per axis, spread evenly across AimSpread. See AimRect.</summary>
    private const int AimSamples = 7;

    /// <summary>How far off its pixel the cursor may land and still have to read as the cell, in pixels.</summary>
    private const float AimPixelTolerance = 1.5f;

    /// <summary>How far the game's ground may differ from BlendedHeight and the aim still hold, in world units.</summary>
    private const float AimHeightTolerance = 0.5f;

    /// <summary>Half the size of the rect handed to ExileInput2 around the chosen pixel. Inside AimPixelTolerance.</summary>
    private const float AimRectHalfPx = 1f;

    /// <summary>
    /// A screen rect for the cursor that the game will read as the aimed cell, or why there is none.
    ///
    /// **Chosen by simulating the game's cursor pick, not by projecting the cell's centre.** Measured on
    /// 2026-09-30: the game names the cursor's cell by rounding its grid position down, Camera.WorldToScreen of
    /// its own cursor ground point lands on the cursor pixel to within 0.03 pixels, and the ground it picks
    /// against is the height map blended between cell centres (see BlendedHeight). Projecting the centre at that
    /// height was still not enough on a raised cell seen from a shallow angle: at (686,958) the line of sight
    /// through the centre fell about 8.2 units a cell, the far slope of the bump 7.8, so the sight line grazed
    /// the slope and the game read the pixel as (686,959) - a few tenths of a unit of height moved the pick most
    /// of a cell.
    ///
    /// So each of AimSamples squared points across the cell is projected, and kept only if the game's pick for
    /// its pixel - found by PickedGrid - lands in the cell, and still does with the pixel AimPixelTolerance off
    /// each way and the ground AimHeightTolerance higher or lower. One of the kept points is chosen at random,
    /// and ExileInput2 is given a small rect about it, so where the cursor lands varies inside the safe part.
    /// None kept means the cell cannot be aimed at reliably from this camera, and the run says so before the
    /// placement circle comes up.
    /// </summary>
    private static (RectangleF Rect, AimVerdict Verdict) AimRect(GameController gc, Camera camera, Vector2 grid)
    {
        var data = Safe.Read(gc, static g => g.IngameState.Data, null);
        var view = camera == null ? null : Safe.Read(camera, static c => c.Snapshot, null);

        if (view == null || data == null || grid == Vector2.Zero)
            return (default, AimVerdict.NotProjected);

        var heights = new Dictionary<(int X, int Y), float>();

        float HeightOf(int x, int y)
        {
            if (!heights.TryGetValue((x, y), out var h))
                heights[(x, y)] = h = HeightOfCell(data, x, y);

            return h;
        }

        var cellX = (int)MathF.Floor(grid.X);
        var cellY = (int)MathF.Floor(grid.Y);
        var safe = new List<Vector2>();
        var projected = false;

        for (var i = 0; i < AimSamples; i++)
        for (var j = 0; j < AimSamples; j++)
        {
            var point = new Vector2(
                cellX + 0.5f - AimSpread + 2f * AimSpread * i / (AimSamples - 1),
                cellY + 0.5f - AimSpread + 2f * AimSpread * j / (AimSamples - 1));
            var height = BlendedHeight(HeightOf, point);

            if (float.IsNaN(height))
                continue;

            var pixel = Safe.Read(() => view.WorldToScreen(new Vector3(point * Detonator.GridToWorld, height)),
                Vector2.Zero);

            if (pixel == Vector2.Zero)
                continue;

            projected = true;

            if (ReadsAsCell(view, HeightOf, pixel, point, cellX, cellY))
                safe.Add(pixel);
        }

        if (safe.Count == 0)
            return (default, projected ? AimVerdict.Unreliable : AimVerdict.NotProjected);

        var chosen = safe[AimChoice.Next(safe.Count)];

        return (new RectangleF(chosen.X - AimRectHalfPx, chosen.Y - AimRectHalfPx, 2f * AimRectHalfPx,
            2f * AimRectHalfPx), AimVerdict.Aimed);
    }

    /// <summary>
    /// Whether the game reads a pixel as the cell, with the pixel off by AimPixelTolerance each way and, at the
    /// pixel itself, the ground AimHeightTolerance higher and lower. See AimRect.
    /// </summary>
    private static bool ReadsAsCell(Camera.CameraSnapshot view, Func<int, int, float> heightOf, Vector2 pixel,
        Vector2 near, int cellX, int cellY)
    {
        var t = AimPixelTolerance;
        var trials = new (Vector2 Nudge, float Lift)[]
        {
            (Vector2.Zero, 0f), (new Vector2(t, 0f), 0f), (new Vector2(-t, 0f), 0f), (new Vector2(0f, t), 0f),
            (new Vector2(0f, -t), 0f), (Vector2.Zero, AimHeightTolerance), (Vector2.Zero, -AimHeightTolerance),
        };

        foreach (var (nudge, lift) in trials)
        {
            var hit = PickedGrid(view, heightOf, pixel + nudge, near, lift);

            if (hit == null || (int)MathF.Floor(hit.Value.X) != cellX || (int)MathF.Floor(hit.Value.Y) != cellY)
                return false;
        }

        return true;
    }

    /// <summary>How far above and below the local ground the line of sight is followed, in world units.</summary>
    private const float PickSearchHeight = 12f;

    /// <summary>The step down the line of sight while looking for the ground, in world units of height.</summary>
    private const float PickStep = 0.25f;

    /// <summary>
    /// Where the game's cursor pick for a pixel meets the ground, in grid, or null when it cannot be worked out.
    ///
    /// The line of sight through a pixel is the set of world points that project onto it, and it is straight, so
    /// it is fixed by two of them: the ground-plane point projecting onto the pixel is solved at a height above
    /// the local ground and at one below, and the line between them is followed downwards until it meets
    /// BlendedHeight (plus lift), then bisected to a fraction of a step. Uses the camera's projection only.
    /// Up is negative in world height, so the line starts at the more negative end.
    /// </summary>
    private static Vector2? PickedGrid(Camera.CameraSnapshot view, Func<int, int, float> heightOf, Vector2 pixel,
        Vector2 near, float lift)
    {
        var ground = BlendedHeight(heightOf, near);

        if (float.IsNaN(ground))
            return null;

        var above = ground - PickSearchHeight;
        var below = ground + PickSearchHeight;
        var start = OnPixel(view, pixel, above, near * Detonator.GridToWorld);
        var end = OnPixel(view, pixel, below, near * Detonator.GridToWorld);

        if (start == null || end == null)
            return null;

        float Clearance(float z, out Vector2 grid)
        {
            var along = (z - above) / (below - above);

            grid = Vector2.Lerp(start.Value, end.Value, along) / Detonator.GridToWorld;

            var surface = BlendedHeight(heightOf, grid);

            return float.IsNaN(surface) ? float.NaN : z - (surface + lift);
        }

        var was = above;

        for (var z = above + PickStep; z <= below; z += PickStep)
        {
            var gap = Clearance(z, out _);

            if (float.IsNaN(gap))
                return null;

            if (gap < 0f)
            {
                was = z;

                continue;
            }

            // Between was (above the ground) and z (at or below it).
            var high = was;
            var low = z;

            for (var k = 0; k < 8; k++)
            {
                var mid = (high + low) / 2f;

                if (Clearance(mid, out _) < 0f)
                    high = mid;
                else
                    low = mid;
            }

            Clearance(low, out var hit);

            return hit;
        }

        return null;
    }

    /// <summary>The world ground-plane point at a height that projects onto a pixel, by Newton's method from a guess.</summary>
    private static Vector2? OnPixel(Camera.CameraSnapshot view, Vector2 pixel, float height, Vector2 guess)
    {
        var at = guess;

        for (var k = 0; k < 6; k++)
        {
            var here = view.WorldToScreen(new Vector3(at, height));
            var alongX = view.WorldToScreen(new Vector3(at + Vector2.UnitX, height)) - here;
            var alongY = view.WorldToScreen(new Vector3(at + Vector2.UnitY, height)) - here;
            var det = alongX.X * alongY.Y - alongY.X * alongX.Y;

            if (MathF.Abs(det) < 1e-9f)
                return null;

            var off = pixel - here;

            at += new Vector2((off.X * alongY.Y - alongY.X * off.Y) / det, (alongX.X * off.Y - off.X * alongX.Y) / det);
        }

        return at;
    }

    /// <summary>
    /// The height of the surface the game picks the cursor against, at a grid point: the height map's four
    /// nearest cell centres blended bilinearly, a cell centre being its whole-number corner plus a half.
    ///
    /// Fitted on 2026-09-30 against 1,601 cursor readings, each the height at which the game's cursor ground
    /// point projects onto the cursor pixel: within 0.25 of it on 99% of them, where the per-cell height was off
    /// by up to 7.8. A neighbour more than BlendedHeightStep away from the point's own cell is taken as level
    /// with it, so a wall or pillar beside placeable ground does not drag the blend up it; that made no
    /// difference on the readings that fitted. The ones that did not - 16 of 1,601 - had the cursor over
    /// ground the height map puts 23 to 219 above the level the game picked, which placeable ground is not.
    /// </summary>
    internal static float BlendedHeight(IngameData data, Vector2 grid) =>
        BlendedHeight((x, y) => HeightOfCell(data, x, y), grid);

    /// <summary>The same, over a height lookup the caller supplies. See BlendedHeight.</summary>
    private static float BlendedHeight(Func<int, int, float> heightOf, Vector2 grid)
    {
        var own = heightOf((int)MathF.Floor(grid.X), (int)MathF.Floor(grid.Y));

        if (float.IsNaN(own))
            return float.NaN;

        var x = grid.X - 0.5f;
        var y = grid.Y - 0.5f;
        var left = (int)MathF.Floor(x);
        var top = (int)MathF.Floor(y);
        var fx = x - left;
        var fy = y - top;

        float Level(int cx, int cy)
        {
            var h = heightOf(cx, cy);

            return float.IsNaN(h) || MathF.Abs(h - own) > BlendedHeightStep ? own : h;
        }

        return Level(left, top) * (1f - fx) * (1f - fy) + Level(left + 1, top) * fx * (1f - fy) +
               Level(left, top + 1) * (1f - fx) * fy + Level(left + 1, top + 1) * fx * fy;
    }

    /// <summary>How far a neighbour's height may differ and still be blended in. See BlendedHeight.</summary>
    private const float BlendedHeightStep = 16f;

    /// <summary>The height map's value for one cell, or NaN when unreadable.</summary>
    private static float HeightOfCell(IngameData data, int x, int y) =>
        Safe.Read(() => data.ToWorldWithTerrainHeight(new Vector2(x, y)).Z, float.NaN);

    /// <summary>
    /// Which cell the game would read a pixel as, by PickedGrid near a grid point, or null. For the Wrong spot
    /// message, so a failure says whether the simulation agrees with the game.
    /// </summary>
    private static Vector2? SimulatedCell(GameController gc, Camera camera, Vector2 pixel, Vector2 near)
    {
        var data = Safe.Read(gc, static g => g.IngameState.Data, null);
        var view = camera == null ? null : Safe.Read(camera, static c => c.Snapshot, null);

        if (view == null || data == null)
            return null;

        var hit = PickedGrid(view, (x, y) => HeightOfCell(data, x, y), pixel, near, 0f);

        return hit == null ? null : new Vector2(MathF.Floor(hit.Value.X), MathF.Floor(hit.Value.Y));
    }

    /// <summary>
    /// Where a grid point shows on screen, through ToWorldWithTerrainHeight.
    ///
    /// **That call already lands half a cell along both axes from the point it is given**, so a whole-number
    /// grid point comes out at its cell's centre. Measured on flat ground (2026-09-30, four dumps): the game's
    /// cursor point projected at its own world position landed on the cursor pixel exactly, while the same
    /// point through ToWorldWithTerrainHeight landed about half a cell up the screen every time, whatever the
    /// cursor's position inside its cell. The game names the cell under the cursor by rounding down, so the
    /// whole-number aims the run sends are aimed at cell centres already. Adding another half cell here was
    /// tried and aims at the next cell's corner.
    /// </summary>
    private static Vector2 Screen(GameController gc, Camera camera, Vector2 grid)
    {
        if (camera == null || grid == Vector2.Zero)
            return Vector2.Zero;

        var world = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(grid), Vector3.Zero);

        return world == Vector3.Zero
            ? Vector2.Zero
            : Safe.Read((camera, world), static x => x.camera.WorldToScreen(x.world), Vector2.Zero);
    }

    /// <summary>
    /// The first planned spot the game does not already have an explosive on.
    ///
    /// Matched by position against the game's own list rather than by counting presses, which is
    /// what makes undo work for free, and what lets an explosive the player put down by hand in
    /// roughly the right place count as that link being done.
    /// </summary>
    private static int Next(GameController gc, Plan plan)
    {
        var placed = Detonator.PlacedExplosiveGridPositions(gc);

        for (var i = 0; i < plan.Points.Count; i++)
        {
            var already = false;

            foreach (var at in placed)
            {
                if (Vector2.Distance(at, plan.Points[i]) < Astray.Owned)
                {
                    already = true;

                    break;
                }
            }

            if (!already)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Whether the chain has gone somewhere the plan did not ask for.
    ///
    /// Counted rather than inferred from a flag: of the explosives placed since the plan was made,
    /// how many are NOT sitting on one of its spots. One is enough - the player put an explosive
    /// somewhere by hand, so the rest of the route is being measured from somewhere it did not
    /// expect and is worth re-planning.
    ///
    /// Undo unwinds it on its own. Take the manual explosive back off and the count goes with it.
    /// </summary>
    public static bool Deviated(GameController gc, Planning planning)
    {
        if (planning.Plan.Points.Count == 0)
            return false;

        var placed = Safe.Read(() => Detonator.Info(gc).PlacedExplosiveCount, 0);

        return placed - planning.Laid > PlacedOf(gc, planning.Plan);
    }

    /// <summary>
    /// How many of the plan's spots already have an explosive on them, for the overlay.
    ///
    /// Derived rather than stored, for the same reason as everything else about the chain's state:
    /// the game knows, and anything this plugin remembered instead could disagree with it.
    /// </summary>
    public static int PlacedOf(GameController gc, Plan plan)
    {
        var placed = Detonator.PlacedExplosiveGridPositions(gc);
        var done = 0;

        foreach (var point in plan.Points)
        {
            foreach (var at in placed)
            {
                if (Vector2.Distance(at, point) < Astray.Owned)
                {
                    done++;

                    break;
                }
            }
        }

        return done;
    }
}
