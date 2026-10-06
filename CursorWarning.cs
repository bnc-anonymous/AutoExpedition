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
    /// <summary>The placement status words that are warnings. Every other status stays in the score area.</summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "Bad camera angle", "Too far", "Unclickable", "Too far from runes button", "Panel failure, choose manually",
        "Failed to scroll", "Marker load failure", "Moving", "Deviated", "Placement failure", "Placement refused",
        "Placement lands wrong", "Marker not lit", "Placement key not working", "Timeout", "Window did not open",
        "Failed to click", "ExileInput2 input refused", "ExileInput2 not installed", "ExileInput2 busy", "No plan",
    };

    /// <summary>
    /// Placement status words shown nowhere: the progress of a run (Arming, Opening, Choosing and the rest, and
    /// Placing n and Moving n), and Walk closer, which a run says when the next spot is off screen - the player can
    /// see that. The score area shows the bomb count instead. See Overlay.Word.
    /// </summary>
    private static readonly HashSet<string> Unshown = new(StringComparer.Ordinal)
    {
        "Walk closer", "Arming", "Opening", "Choosing", "Scrolling", "Tidying", "Clearing", "Hiding labels",
        "Aiming again",
    };

    /// <summary>Whether a placement status word is one nobody is shown. See Unshown.</summary>
    public static bool IsUnshown(string word) =>
        word != null && (Unshown.Contains(word) || word.StartsWith("Placing ", StringComparison.Ordinal) ||
                         word.StartsWith("Moving ", StringComparison.Ordinal));

    private static readonly Color Warning = Color.FromArgb(255, 235, 90, 90);

    private static (string Word, DateTime At) _shown = ("", DateTime.MinValue);

    /// <summary>Whether a placement status word is one this shows rather than the score area.</summary>
    public static bool IsWarning(string word) => word != null && Words.Contains(word);

    /// <summary>Shows a warning, replacing any before it. A word that is not a warning is ignored.</summary>
    public static void Show(string word)
    {
        if (IsWarning(word))
            _shown = (word, DateTime.UtcNow);
    }

    /// <summary>Takes the warning down. Called when a placement run begins.</summary>
    public static void Clear() => _shown = ("", DateTime.MinValue);

    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        if (_shown.Word.Length == 0 || !Safe.Read(() => settings.Display.ScoreArea.CursorWarnings.Value, true))
            return;

        var seconds = Math.Max(1, Safe.Read(() => settings.Display.ScoreArea.CursorWarningSeconds.Value, 4));
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
