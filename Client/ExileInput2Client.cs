using ExileCore2;
using System;
using System.Windows.Forms;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace AutoExpedition;

/// <summary>
/// How a plugin talks to ExileInput2. Copy this file into your own source and change the namespace.
///
/// Not referenced, copied. There is deliberately no assembly to share: everything crossing the
/// bridge is a type both sides already have, so there is no DLL in the HUD root, no version to keep
/// in step, and no cast to fail at load. The cost is this one file living in each consumer, and it
/// is small enough and stable enough to be the better end of that trade.
///
/// Give it a name unique to your plugin. That name is the claim on the cursor - calls made while
/// somebody else holds it return false rather than happening late, which is the same answer your
/// state machine already gets when a click does not go through.
///
/// <code>
/// _input = new ExileInput2Client(GameController, "CurrencyExchangeFlipper");
///
/// if (!_input.Take())          // somebody else has it; try again next tick
///     return;
///
/// _input.MoveTo(button.GetClientRectCache);
/// // ... later ticks ...
/// if (_input.Arrived())
///     _input.Click(ctrl: true, right: true);
/// // ... when the machine finishes or gives up ...
/// _input.Release();
/// </code>
///
/// Everything degrades to false when the plugin is not installed, which <see cref="Available"/>
/// reports so a caller can fall back to driving ExileCore2.Input itself.
/// </summary>
public sealed class ExileInput2Client
{
    private readonly GameController _gc;
    private readonly string _me;

    private Func<string, bool> _take;
    private Func<string, bool> _release;
    private Func<string, RectangleF, bool, bool, bool> _moveTo;
    private Func<string, Vector2, bool, bool, bool> _moveToPoint;
    private Func<string, Vector2, Vector2, bool> _drag;
    private Func<string, bool> _dragging;
    private Func<string, bool> _dragMissed;
    private Func<string, string> _dragTrace;
    private Func<string, RectangleF, Keys[], bool> _moveToKeys;
    private Func<string, Vector2, Keys[], bool> _moveToPointKeys;
    private Func<string, bool> _arrived;
    private Func<string, float> _drift;
    private Func<string, bool> _aiming;
    private Func<string, bool, bool, bool, bool> _click;
    private Func<string, Keys[], bool, bool> _clickKeys;
    private Func<string, Keys, bool> _tap;
    private Func<string, Keys, bool> _hold;
    private Func<string, Keys, bool> _let;
    private Func<string, bool> _releaseKeys;
    private Func<string, Vector2> _expected;
    private Func<string, bool> _forget;
    private Func<string, int, bool> _setTolerance;
    private Func<string, string> _stopped;

    private bool _looked;

    /// <summary>When the first lookup ran, and when the last retry did. See Look.</summary>
    private DateTime _lookedAt;

    private DateTime _tried;


    public ExileInput2Client(GameController gc, string name)
    {
        _gc = gc;
        _me = name;
    }

    /// <summary>Whether the input plugin is installed and answering.</summary>
    public bool Available
    {
        get
        {
            Look();
            return _take != null;
        }
    }

    /// <summary>
    /// Finds the bridge methods, once.
    ///
    /// Once rather than per call, and lazily rather than in a constructor: plugins initialise in an
    /// order nobody controls, so a lookup at construction can run before the input plugin has
    /// registered anything and cache a null for the session.
    /// </summary>
    private void Look()
    {
        // **Looked up once was wrong, because the two plugins load and reload independently.**
        //
        // Whichever registers second is invisible to whoever bound first: reload the input plugin to
        // pick up a new method and the consumer still holds a null for it, having latched its
        // lookups at its own startup. The symptom is a feature that silently does nothing and a
        // reload that does not fix it - the fix being to reload the OTHER plugin afterwards, which
        // is a rule nobody should have to know.
        //
        // So a binding that came back empty is retried, slowly, for a while. Once everything asked
        // for is in hand it latches as before and costs nothing; against an older input plugin that
        // genuinely lacks a method, it gives up after a minute rather than asking for ever.
        if (_looked && (_drag != null || DateTime.UtcNow - _lookedAt > TimeSpan.FromSeconds(60)))
            return;

        if (_looked && DateTime.UtcNow - _tried < TimeSpan.FromSeconds(2))
            return;

        _tried = DateTime.UtcNow;

        if (!_looked)
            _lookedAt = DateTime.UtcNow;

        _looked = true;

        try
        {
            var b = _gc?.PluginBridge;

            if (b == null)
                return;

            _take = b.GetMethod<Func<string, bool>>("ExileInput2.Take");
            _release = b.GetMethod<Func<string, bool>>("ExileInput2.Release");
            _moveTo = b.GetMethod<Func<string, RectangleF, bool, bool, bool>>("ExileInput2.MoveTo");
            _moveToPoint = b.GetMethod<Func<string, Vector2, bool, bool, bool>>("ExileInput2.MoveToPoint");
            _drag = b.GetMethod<Func<string, Vector2, Vector2, bool>>("ExileInput2.Drag");
            _dragging = b.GetMethod<Func<string, bool>>("ExileInput2.Dragging");
            _dragMissed = b.GetMethod<Func<string, bool>>("ExileInput2.DragMissed");
            _dragTrace = b.GetMethod<Func<string, string>>("ExileInput2.DragTrace");

            // The modifier-set forms. Null against an older ExileInput2 that does not register
            // them, which is why every call below falls back to the ctrl/shift pair rather than
            // failing - a consumer built against the newer client keeps working against the older
            // plugin, right up until it asks for a modifier the old shape cannot express.
            _moveToKeys = b.GetMethod<Func<string, RectangleF, Keys[], bool>>("ExileInput2.MoveToKeys");
            _moveToPointKeys = b.GetMethod<Func<string, Vector2, Keys[], bool>>("ExileInput2.MoveToPointKeys");
            _arrived = b.GetMethod<Func<string, bool>>("ExileInput2.Arrived");
            _drift = b.GetMethod<Func<string, float>>("ExileInput2.Drift");
            _aiming = b.GetMethod<Func<string, bool>>("ExileInput2.Aiming");
            _click = b.GetMethod<Func<string, bool, bool, bool, bool>>("ExileInput2.Click");
            _clickKeys = b.GetMethod<Func<string, Keys[], bool, bool>>("ExileInput2.ClickKeys");
            _tap = b.GetMethod<Func<string, Keys, bool>>("ExileInput2.Tap");
            _hold = b.GetMethod<Func<string, Keys, bool>>("ExileInput2.Hold");
            _let = b.GetMethod<Func<string, Keys, bool>>("ExileInput2.Let");
            _releaseKeys = b.GetMethod<Func<string, bool>>("ExileInput2.ReleaseKeys");
            _expected = b.GetMethod<Func<string, Vector2>>("ExileInput2.Expected");
            _forget = b.GetMethod<Func<string, bool>>("ExileInput2.Forget");
            _setTolerance = b.GetMethod<Func<string, int, bool>>("ExileInput2.SetTolerance");
            _stopped = b.GetMethod<Func<string, string>>("ExileInput2.Stopped");
        }
        catch
        {
            // A bridge that will not answer is the same as one that is not there.
        }
    }

    /// <summary>
    /// Claims the cursor. Safe to call every tick; false means somebody else has it.
    /// </summary>
    public bool Take()
    {
        Look();
        return _take?.Invoke(_me) ?? false;
    }

    /// <summary>Hands the cursor back, releasing any keys this plugin was holding.</summary>
    public bool Release()
    {
        Look();
        return _release?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// Sends the cursor to a spread point inside a rect.
    ///
    /// Say here what the click will need held, not at the click. The modifier then goes down
    /// partway through the approach rather than on arrival, which is both what a hand does and the
    /// faster of the two - the key gets the length of the journey to register, instead of the
    /// pointer sitting on the target while it is confirmed.
    /// </summary>
    /// <summary>
    /// Aims at a rect, pressing these modifiers on the way.
    ///
    /// The set form. Use it for anything involving alt, or any combination the two bools cannot
    /// say; the ctrl/shift overload below is the same call with a shorter spelling.
    /// </summary>
    public bool MoveTo(RectangleF rect, Keys[] modifiers)
    {
        Look();

        if (_moveToKeys != null)
            return _moveToKeys(_me, rect, modifiers ?? []);

        // An older ExileInput2. Fall back to what it does understand, and say no rather than
        // silently dropping a modifier it cannot be told about.
        return Fallback(modifiers, out var ctrl, out var shift) &&
               (_moveTo?.Invoke(_me, rect, ctrl, shift) ?? false);
    }

    /// <summary>As above, for a point rather than a rect.</summary>
    public bool MoveTo(Vector2 client, Keys[] modifiers)
    {
        Look();

        if (_moveToPointKeys != null)
            return _moveToPointKeys(_me, client, modifiers ?? []);

        return Fallback(modifiers, out var ctrl, out var shift) &&
               (_moveToPoint?.Invoke(_me, client, ctrl, shift) ?? false);
    }

    /// <summary>
    /// Clicks with these modifiers held.
    ///
    /// The set form, and the one to use when alt is involved: holding shift keeps a currency on the
    /// cursor between applications, and alt on top of it changes which currency is applied, so the
    /// combination has to be expressible as a combination.
    /// </summary>
    public bool Click(Keys[] modifiers, bool right = false)
    {
        Look();

        if (_clickKeys != null)
            return _clickKeys(_me, modifiers ?? [], right);

        return Fallback(modifiers, out var ctrl, out var shift) &&
               (_click?.Invoke(_me, ctrl, shift, right) ?? false);
    }

    /// <summary>
    /// Whether an older plugin can be told about these modifiers, and how.
    ///
    /// False when the set contains anything the ctrl/shift pair cannot express - alt, most
    /// obviously. Refusing is the right answer there: a click that quietly went out without its alt
    /// would apply a different currency to the item, and a caller that gets a false already knows
    /// how to treat it, because that is what a refused lease looks like too.
    /// </summary>
    private static bool Fallback(Keys[] modifiers, out bool ctrl, out bool shift)
    {
        ctrl = false;
        shift = false;

        foreach (var key in modifiers ?? [])
        {
            switch (key)
            {
                case Keys.LControlKey or Keys.RControlKey or Keys.ControlKey:
                    ctrl = true;
                    break;
                case Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey:
                    shift = true;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    public bool MoveTo(RectangleF rect, bool ctrl = false, bool shift = false)
    {
        Look();
        return _moveTo?.Invoke(_me, rect, ctrl, shift) ?? false;
    }

    /// <summary>Sends the cursor to a client-space point.</summary>
    public bool MoveTo(Vector2 client, bool ctrl = false, bool shift = false)
    {
        Look();
        return _moveToPoint?.Invoke(_me, client, ctrl, shift) ?? false;
    }

    /// <summary>Whether the cursor has reached where it was sent.</summary>
    public bool Arrived()
    {
        Look();
        return _arrived?.Invoke(_me) ?? false;
    }

    /// <summary>How far the cursor is from where it was sent, in client pixels.</summary>
    public float Drift()
    {
        Look();
        return _drift?.Invoke(_me) ?? 0f;
    }

    /// <summary>Whether a move has been asked for and not yet confirmed.</summary>
    public bool Aiming()
    {
        Look();
        return _aiming?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// Clicks, with modifiers confirmed down first.
    ///
    /// False where the modifier could not be confirmed and the click would have gone bare, which on
    /// a currency item is the difference between putting it away and picking it up. Keep your state
    /// and try again rather than treating it as done.
    /// </summary>
    public bool Click(bool ctrl = false, bool shift = false, bool right = false)
    {
        Look();
        return _click?.Invoke(_me, ctrl, shift, right) ?? false;
    }

    /// <summary>Where the cursor was last sent, in client space.</summary>
    public Vector2 Expected()
    {
        Look();
        return _expected?.Invoke(_me) ?? Vector2.Zero;
    }

    /// <summary>
    /// Why the cursor was taken away from this plugin, or "" while all is well.
    ///
    /// Usually "PlayerMovedTheMouse": the player reached for the mouse, which is the stop signal
    /// and the reason there is no panic key to remember. Also reports the game losing focus, the
    /// game taking typed input, and a move that never landed.
    ///
    /// **Check it on every tick of a sequence, and stop the sequence when it says something.** It
    /// stays set until <see cref="Release"/> is called, and <see cref="Take"/> refuses until then,
    /// so a caller that ignores it simply stops working rather than fighting the player for the
    /// cursor - but it stops silently, and only this tells you why.
    /// </summary>
    public string Stopped()
    {
        Look();
        return _stopped?.Invoke(_me) ?? "";
    }

    /// <summary>Whether the cursor has been taken away from this plugin. See <see cref="Stopped"/>.</summary>
    public bool WasStopped() => !string.IsNullOrEmpty(Stopped());

    /// <summary>Abandons an outstanding move, for a caller giving up on what it was doing.</summary>
    public bool Forget()
    {
        Look();
        return _forget?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// How near the cursor must get before <see cref="Arrived"/> is true, for this caller.
    ///
    /// Overrides the shared setting, which cannot suit every target: forty pixels is reasonable on
    /// a stash tab and most of a row on a 54 pixel list entry.
    /// </summary>
    public bool SetTolerance(int px)
    {
        Look();
        return _setTolerance?.Invoke(_me, px) ?? false;
    }

    /// <summary>Presses and releases a key.</summary>
    /// <summary>
    /// Presses at one point, travels to another, and lets go there. See ExileInput2's Drag.
    ///
    /// One call rather than a press and a release to pair up, so nothing here can leave a button
    /// held: the input plugin owns the gesture and puts it down on every way out, including this
    /// plugin crashing or being reloaded mid-drag.
    /// </summary>
    public bool Drag(Vector2 from, Vector2 to)
    {
        Look();
        return _drag?.Invoke(_me, from, to) ?? false;
    }

    /// <summary>
    /// Whether this build of ExileInput2 offers a drag at all.
    ///
    /// The two plugins are loaded separately and reloaded separately, so a consumer can be newer
    /// than the input it talks to - and a missing method looks exactly like a refused one from the
    /// outside. Worth telling apart: one is fixed by reloading, the other is not.
    /// </summary>
    public bool CanDrag => _drag != null;

    /// <summary>
    /// Whether the last drag gave up because the thing under the cursor was not what was aimed at.
    ///
    /// The input plugin asks the game what the cursor is over before it presses, so a drag that
    /// never happened is told apart from one that happened and achieved nothing.
    /// </summary>
    public bool DragMissed() => _dragMissed?.Invoke(_me) ?? false;

    /// <summary>Where the cursor really was at each step of the last drag. See ExileInput2's Trace.</summary>
    public string DragTrace() => _dragTrace?.Invoke(_me) ?? "not available";

    /// <summary>Whether a drag is still in flight, so a caller can wait rather than guess.</summary>
    public bool Dragging()
    {
        Look();
        return _dragging?.Invoke(_me) ?? false;
    }

    public bool Tap(Keys key)
    {
        Look();
        return _tap?.Invoke(_me, key) ?? false;
    }

    /// <summary>Holds a key down until <see cref="Let"/> or <see cref="ReleaseKeys"/>.</summary>
    public bool Hold(Keys key)
    {
        Look();
        return _hold?.Invoke(_me, key) ?? false;
    }

    /// <summary>Lets go of one key.</summary>
    public bool Let(Keys key)
    {
        Look();
        return _let?.Invoke(_me, key) ?? false;
    }

    /// <summary>Lets go of every key this plugin is holding.</summary>
    public bool ReleaseKeys()
    {
        Look();
        return _releaseKeys?.Invoke(_me) ?? false;
    }
}
