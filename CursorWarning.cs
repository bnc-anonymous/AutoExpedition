using ExileCore2;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Graphics = ExileCore2.Graphics;

namespace AutoExpedition;

/// <summary>
/// The last placement warning word, written under the mouse cursor.
///
/// These used to replace the bomb count on the last line of the score area, and players did not notice them there.
/// Only the words that ask the player to do something or say a run failed are shown here; progress words (Arming,
/// Opening, Choosing, Placing n and the like) are shown nowhere, and the score area keeps the bomb count. The word
/// alone: the sentence
/// explaining it is written for the dump and is too long to read beside the cursor. A warning lasts for Cursor warning
/// duration and is cleared when the next placement run begins. See ScoreAreaSettings.CursorWarnings.
/// </summary>
internal static class CursorWarning
{
    /// <summary>
    /// The placement status words that are warnings. Every other status stays in the score area. Missing currency is a
    /// tablet crafting run's, stopped for want of an orb. See TabletCraftingRun.
    /// </summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "Bad camera angle", "Too far from previous bomb", "Unclickable", "Too far from runes button", "Panel failure, choose manually",
        "Failed to scroll", "Marker load failure", "Character moving", "Character moved", "Mouse moved", "Key pressed",
        "Stopped", "Deviated", "Placement failure", "Placement refused",
        "Placement lands wrong", "Marker not lit", "Placement key not working", "Timeout", "Window did not open",
        "Failed to click", "ExileInput2 input refused", "ExileInput2 not installed", "ExileInput2 busy",
        "Missing currency", "AutoExpedition: Automation is enabled\nbut ExileInput2 was not found.", "Inventory full",
        "AutoExpedition: ExileInput2 is out of date.\nUpdate it - automation will not work fully.", "Item held",
        "AutoExpedition: NinjaPricer has no prices.\nLoot lists with MIN match nothing.",
    };

    /// <summary>
    /// Placement status words shown nowhere: the progress of a run (Arming, Opening, Choosing and the rest, and
    /// Placing n and Moving n), Walk closer, which a run says when the next spot is off screen - the player can
    /// see that - No plan, which an action key press with nothing to place or loot says, and which told the player
    /// nothing they could act on, and Next spot covered, a press stopping after it placed something because the next
    /// spot is under the interface: that spot is drawn yellow, not yet placeable, which already says so. The score area
    /// shows the bomb count instead. See Overlay.Word and Placement.AfterPlacing.
    /// </summary>
    private static readonly HashSet<string> Unshown = new(StringComparer.Ordinal)
    {
        "Walk closer", "Arming", "Opening", "Choosing", "Scrolling", "Tidying", "Clearing", "Hiding labels",
        "Aiming again", "No plan", "Next spot covered", "Cancelled",
    };

    /// <summary>Whether a placement status word is one nobody is shown. See Unshown.</summary>
    public static bool IsUnshown(string word) =>
        word != null && (Unshown.Contains(word) || word.StartsWith("Placing ", StringComparison.Ordinal) ||
                         word.StartsWith("Moving ", StringComparison.Ordinal));

    internal static readonly Color Warning = Color.FromArgb(255, 235, 90, 90);

    private static (string Word, DateTime At) _shown = ("", DateTime.MinValue);

    /// <summary>How long the warning showing stays up, in seconds, when it set its own; null for the setting's.</summary>
    private static int? _shownFor;

    /// <summary>Whether a placement status word is one this shows rather than the score area.</summary>
    public static bool IsWarning(string word) => word != null && Words.Contains(word);

    /// <summary>Shows a warning, replacing any before it. A word that is not a warning is ignored.</summary>
    public static void Show(string word)
    {
        if (IsWarning(word))
        {
            _shown = (word, DateTime.UtcNow);
            _shownFor = null;
        }
    }

    /// <summary>Shows a warning for this many seconds, whatever Cursor warning seconds says. See Show.</summary>
    public static void Show(string word, int seconds)
    {
        if (IsWarning(word))
        {
            _shown = (word, DateTime.UtcNow);
            _shownFor = seconds;
        }
    }

    /// <summary>Takes the warning down. Called when a placement run begins.</summary>
    public static void Clear() => _shown = ("", DateTime.MinValue);

    /// <summary>Takes the warning down if it is this word, leaving any other warning showing.</summary>
    public static void Clear(string word)
    {
        if (string.Equals(_shown.Word, word, StringComparison.Ordinal))
            Clear();
    }

    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        if (_shown.Word.Length == 0 || !Safe.Read(() => settings.Display.ScoreArea.CursorWarnings.Value, true))
            return;

        var seconds = Math.Max(1, _shownFor ?? Safe.Read(() => settings.Display.ScoreArea.CursorWarningSeconds.Value, 4));
        var age = (DateTime.UtcNow - _shown.At).TotalSeconds;

        if (age >= seconds)
            return;

        var cursor = Safe.Read(() => new Vector2(gc.IngameState.MousePosX, gc.IngameState.MousePosY), Vector2.Zero);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (cursor == Vector2.Zero || window.Width <= 0f)
            return;

        var text = _shown.Word;

        var size = graphics.MeasureText(text);

        // Below and right of the pointer, kept inside the window so a warning raised near an edge is not cut off.
        var at = new Vector2(
            Math.Clamp(cursor.X + 16f, 0f, Math.Max(0f, window.Width - size.X - 4f)),
            Math.Clamp(cursor.Y + 44f, 0f, Math.Max(0f, window.Height - size.Y - 4f)));

        // Fades over its last second rather than vanishing mid-read.
        var alpha = (int)(255 * Math.Clamp(seconds - age, 0d, 1d));

        graphics.DrawTextWithBackground(text, at, Color.FromArgb(alpha, Warning),
            Color.FromArgb(alpha * 200 / 255, Color.Black));
    }
}
