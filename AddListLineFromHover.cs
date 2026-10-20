using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;
using Color = System.Drawing.Color;
using System.Linq;
using System.Numerics;

namespace AutoExpedition;

/// <summary>
/// The Add class and Add unique buttons under the action key list: for PickFor after the press, a bar under the cursor
/// counts down with "Hover an item" beside it, and when it runs out the item under the cursor - in the inventory, the
/// open stash tab or on the ground - goes on the bottom of the action key list: its item class as
/// "CLASS:&lt;class&gt; MIN:5ex", or a unique's art name as "ART:&lt;art name&gt;". The outcome shows under the cursor for
/// NoteFor. Drawn and finished from Render, which runs on the same thread as Tick. See AutolootSettings.AddFromHoverUi.
/// </summary>
internal static class AddListLineFromHover
{
    /// <summary>Which line a press adds: the item's class, or a unique's art name.</summary>
    public enum LineKind
    {
        Class,
        Unique,
    }

    /// <summary>The kind of line the countdown in hand adds.</summary>
    private static LineKind _kind;

    /// <summary>How long the player has to hover an item after pressing the button.</summary>
    private static readonly TimeSpan PickFor = TimeSpan.FromMilliseconds(3000);

    /// <summary>How long the outcome stays under the cursor.</summary>
    private static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(3);

    /// <summary>When the countdown ends; DateTime.MinValue when none is running.</summary>
    private static DateTime _pickAt = DateTime.MinValue;

    /// <summary>What the last countdown did, whether it went well, and when, for the note under the cursor.</summary>
    private static (string Text, bool Added, DateTime At) _note = ("", false, DateTime.MinValue);

    /// <summary>The line's minimum price, as the shipped list's own lines have it.</summary>
    private const string MinimumPrice = "MIN:5ex";

    /// <summary>Starts the countdown for this kind of line, or starts it again.</summary>
    public static void Start(LineKind kind)
    {
        _kind = kind;
        _pickAt = DateTime.UtcNow + PickFor;
    }

    /// <summary>The countdown bar and its words, or the outcome after it; at the end of the countdown, the pick itself.</summary>
    public static void Draw(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        var cursor = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

        if (_pickAt != DateTime.MinValue)
        {
            var left = (_pickAt - DateTime.UtcNow).TotalMilliseconds;

            if (left <= 0)
            {
                _pickAt = DateTime.MinValue;
                Pick(gc, settings, cursor);
            }
            else if (cursor != Vector2.Zero)
            {
                // Centred under the pointer, at the cursor warnings' height, as the take last hold's bar.
                const float wide = 80f;
                const float high = 6f;
                var at = new Vector2(cursor.X - wide / 2f, cursor.Y + 44f);
                var share = (float)Math.Clamp(left / PickFor.TotalMilliseconds, 0d, 1d);

                graphics.DrawBox(new ExileCore2.Shared.RectangleF(at.X - 1f, at.Y - 1f, wide + 2f, high + 2f), Color.Black);
                graphics.DrawBox(new ExileCore2.Shared.RectangleF(at.X, at.Y, wide * share, high), Color.FromArgb(230, Color.LimeGreen));

                const string words = "Hover an item";
                var size = graphics.MeasureText(words);

                graphics.DrawTextWithBackground(words, new Vector2(cursor.X - size.X / 2f, at.Y + high + 4f), Color.White,
                    Color.FromArgb(200, Color.Black));

                return;
            }
        }

        var age = DateTime.UtcNow - _note.At;

        if (_note.Text.Length == 0 || age >= NoteFor || cursor == Vector2.Zero)
            return;

        var alpha = (int)(255 * Math.Clamp((NoteFor - age).TotalSeconds, 0d, 1d));
        var noteSize = graphics.MeasureText(_note.Text);

        graphics.DrawTextWithBackground(_note.Text, new Vector2(cursor.X - noteSize.X / 2f, cursor.Y + 44f),
            Color.FromArgb(alpha, _note.Added ? Color.LimeGreen : CursorWarning.Warning), Color.FromArgb(alpha * 200 / 255, Color.Black));
    }

    /// <summary>Reads the hovered item and adds its line to the bottom of the action key list.</summary>
    private static void Pick(GameController gc, AutoExpeditionSettings settings, Vector2 cursor)
    {
        var item = HoveredItem(gc, cursor);
        var metadata = Safe.Read(item, static e => e.Metadata, "") ?? "";

        if (item == null || metadata.Length == 0)
        {
            Say("No item hovered", false);
            return;
        }

        if (_kind == LineKind.Unique)
        {
            AddUnique(settings, item);
            return;
        }

        var itemClass = Safe.Read(() => gc.Files.BaseItemTypes.Translate(metadata)?.ClassName, "") ?? "";

        if (itemClass.Length == 0)
        {
            Say("The item's class could not be read", false);
            return;
        }

        // A list line's conditions are separated by spaces, so a class with one in it cannot be written as CLASS:.
        if (itemClass.Contains(' '))
        {
            Say($"\"{itemClass}\" has a space and cannot be a CLASS: line", false);
            return;
        }

        AddLine(settings, $"CLASS:{itemClass}", $"CLASS:{itemClass} {MinimumPrice}");
    }

    /// <summary>
    /// Adds "ART:&lt;art name&gt;" for a unique: its art names it whether identified or not. No MIN, so it is taken at
    /// any price. Refused for an item that is not unique, whose art its whole base shares.
    /// </summary>
    private static void AddUnique(AutoExpeditionSettings settings, Entity item)
    {
        if (Safe.Read(item, static e => e.GetComponent<Mods>()?.ItemRarity, null) != ItemRarity.Unique)
        {
            Say("Not a unique", false);
            return;
        }

        var artName = Autoloot.ArtNameOfItem(item);

        if (artName.Length == 0 || artName.Contains(' '))
        {
            Say("The unique's art could not be read", false);
            return;
        }

        AddLine(settings, $"ART:{artName}", $"ART:{artName}");
    }

    /// <summary>Adds a line to the bottom of the action key list, unless a line there already has the word that names it.</summary>
    private static void AddLine(AutoExpeditionSettings settings, string namingWord, string line)
    {
        var list = settings.Automation.Autoloot.ItemsOnActionKey;
        var lines = (list.Value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        if (lines.Any(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => string.Equals(word, namingWord, StringComparison.OrdinalIgnoreCase))))
        {
            Say($"{namingWord} is already on the list", false);
            return;
        }

        lines.Add(line);
        list.Value = string.Join("\n", lines);
        Say($"Added {line}", true);
    }

    private static void Say(string text, bool added) => _note = (text, added, DateTime.UtcNow);

    /// <summary>
    /// The item under the cursor: in the player's inventory, in the open stash tab, or a ground label's item. Found by
    /// each element's own rectangle rather than IngameState.UIHover, which reads as an element at address zero on this
    /// build (see Placement.Hovered). Null when the cursor is over none.
    /// </summary>
    internal static Entity HoveredItem(GameController gc, Vector2 cursor)
    {
        bool Under(ExileCore2.Shared.RectangleF rect) =>
            rect.Width > 0f && cursor.X >= rect.Left && cursor.X <= rect.Right && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;

        if (Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false))
        {
            foreach (var slot in Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory].VisibleInventoryItems,
                         null) ?? [])
            {
                if (Under(Safe.Read(slot, static s => s.GetClientRect(), default)))
                    return Safe.Read(slot, static s => s.Item, null);
            }
        }

        if (Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.IsVisible, false))
        {
            foreach (var slot in Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.VisibleStash.VisibleInventoryItems, null) ?? [])
            {
                if (Under(Safe.Read(slot, static s => s.GetClientRect(), default)))
                    return Safe.Read(slot, static s => s.Item, null);
            }
        }

        foreach (var (label, onGround) in Ground.VisibleItems(gc))
        {
            if (Under(Safe.Read(label, static l => l.Label.GetClientRect(), default)))
                return Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);
        }

        return null;
    }
}
