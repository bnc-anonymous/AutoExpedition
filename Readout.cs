using ExileCore2;
using System;
using System.Collections.Generic;
using System.Drawing;
using Graphics = ExileCore2.Graphics;
using System.Numerics;


namespace AutoExpedition;

/// <summary>
/// The stack of lines above the detonator button, as a list of rows rather than as arithmetic.
///
/// **Everything drawn here used to work out its own Y from somebody else's, and every addition broke
/// one of the others.** The word was placed against the button, the score was placed above the word
/// by subtracting its height, the share line was placed below the word by adding the word's height
/// plus six, the countdown bar was placed one pixel under the word - and the warm-up readout was put
/// in the same place as the bar, on the reasoning that the two are never shown at once. Which was
/// true, until the tally column also started at the word and grew down through both of them.
///
/// The offsets were each correct in isolation and there was no place that said what the column
/// looked like, so the only way to find a collision was to see one in game. That is the third or
/// fourth time a line in this corner has landed on top of another.
///
/// So the column is a list. Rows are added top to bottom, the block is measured once, and the
/// positions come out of the layout instead of out of each caller's head. Reordering the readout is
/// then moving a call, and a row that is not added takes no room.
///
/// One row is the ANCHOR, and it is the one pinned to the button - rows added before it stack
/// upwards and rows added after it stack downwards. Without that the whole block would grow away
/// from the button as lines appeared, which is the thing to avoid: the score is what is being read,
/// and it should not move because a warm-up started talking underneath it.
/// </summary>
internal sealed class Readout
{
    /// <summary>A piece of a line, so one row can carry more than one colour. See Line.</summary>
    private readonly record struct Piece(string Text, Color Colour, Vector2 Size);

    private sealed class Row
    {
        public List<Piece> Pieces;
        public bool Right;
        public float Bar = -1f;
        public Color BarColour;
        public float Height;
        public float Width;
    }

    private readonly Graphics _graphics;
    private readonly List<Row> _rows = new();
    private int _anchor = -1;

    /// <summary>How wide the block is at least, so the countdown bar can span the button.</summary>
    private readonly float _least;

    public Readout(Graphics graphics, float least)
    {
        _graphics = graphics;
        _least = least;
    }

    /// <summary>How tall the anchor row is, which is what the caller pins against the button.</summary>
    public float AnchorHeight => _anchor >= 0 ? _rows[_anchor].Height : 0f;

    /// <summary>A centred line, in as many coloured pieces as it takes.</summary>
    public void Line(params (string Text, Color Colour)[] pieces) => Add(false, false, pieces);

    /// <summary>
    /// A line flush with the right hand edge of the block.
    ///
    /// For columns of numbers, which get compared rather than read - ragged digits are hard to
    /// compare and the block's edge is a straighter rule than whichever line happens to be widest.
    /// </summary>
    public void Right(string text, Color colour) => Add(true, false, (text, colour));

    /// <summary>The line pinned to the button. See the class summary.</summary>
    public void Anchor(params (string Text, Color Colour)[] pieces) => Add(false, true, pieces);

    /// <summary>Empty space, for room a row will want back in a moment. See Gap in Overlay.</summary>
    public void Gap(float height) =>
        _rows.Add(new Row { Pieces = new List<Piece>(), Height = height });

    /// <summary>A bar that fills from the left, for a countdown.</summary>
    public void Bar(float filled, Color colour) =>
        _rows.Add(new Row
        {
            Pieces = new List<Piece>(),
            Bar = filled < 0f ? 0f : filled > 1f ? 1f : filled,
            BarColour = colour,
            Height = BarHeight,
        });

    /// <summary>How tall a countdown bar is, and the room kept for one that is not drawn.</summary>
    public const float BarHeight = 3f;

    /// <summary>How wide a countdown bar is, centred on the column. See Paint.</summary>
    public const float BarWidth = 90f;

    private void Add(bool right, bool anchor, params (string Text, Color Colour)[] pieces)
    {
        var row = new Row { Pieces = new List<Piece>(pieces.Length), Right = right };

        foreach (var (text, colour) in pieces)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            Vector2 size;

            using (Spent.On("Readout/MeasureText"))
                size = _graphics.MeasureText(text);

            row.Pieces.Add(new Piece(text, colour, size));
            row.Width += size.X;

            if (size.Y > row.Height)
                row.Height = size.Y;
        }

        // An anchor with nothing in it still holds its place in the order, because the rows below it
        // are placed relative to it and a missing word must not pull them up over the button.
        if (row.Pieces.Count == 0 && !anchor)
            return;

        if (anchor)
            _anchor = _rows.Count;

        _rows.Add(row);
    }

    /// <summary>
    /// Draws the block, with the anchor row's top edge at anchorTop and everything centred on centre.
    /// </summary>
    public void Draw(Vector2 centre, float anchorTop)
    {
        var widest = _least;

        foreach (var row in _rows)
        {
            if (row.Width > widest)
                widest = row.Width;
        }

        var right = centre.X + widest / 2f;

        // Upwards from the anchor, so a row appearing below it never moves it. See the class summary.
        var top = anchorTop;

        for (var i = (_anchor >= 0 ? _anchor : 0) - 1; i >= 0; i--)
        {
            top -= _rows[i].Height + Space;
            Paint(_rows[i], centre.X, right, top);
        }

        top = anchorTop;

        for (var i = _anchor >= 0 ? _anchor : 0; i < _rows.Count; i++)
        {
            Paint(_rows[i], centre.X, right, top);
            top += _rows[i].Height + Space;
        }
    }

    /// <summary>
    /// How much air between rows.
    ///
    /// None. The measured height of a line of text already carries its own leading, so anything
    /// added here is a second gap on top of the one the font intends - which read as a column of
    /// loosely related lines rather than as one block.
    /// </summary>
    public const float Space = 0f;

    private void Paint(Row row, float centre, float right, float top)
    {
        if (row.Bar >= 0f)
        {
            // **A fixed width, because the thing it measures is not a width.**
            //
            // This used to span whichever was wider, the row or the block's minimum - and the
            // minimum is the detonator toggle button, so the bar was sized by a piece of the game's
            // interface. It changed with the button and with the widest line above it, which meant
            // the same fraction drew a different length from one moment to the next and the bar was
            // not comparable with itself.
            var from = centre - BarWidth / 2f;

            _graphics.DrawBox(new Vector2(from, top), new Vector2(from + BarWidth * row.Bar,
                top + BarHeight), row.BarColour);

            return;
        }

        if (row.Pieces.Count == 0)
            return;

        var at = row.Right ? right - row.Width : centre - row.Width / 2f;

        // The background goes down once for the whole row. Painted per piece, a line in three
        // colours shows as three boxes with three seams down it.
        //
        // **As a text background, not as a box**: the whole row's text drawn invisible with a black
        // background, then the pieces over it. An explosive's label in the world draws its background
        // with DrawTextWithBackground, and a row drawn later with DrawBox still showed that label
        // through its box while its own text sat on top, so the two lines muddled together. Drawn
        // the way the label is drawn, the later one covers the earlier one, background and all.
        _graphics.DrawTextWithBackground(string.Concat(row.Pieces.ConvertAll(x => x.Text)), new Vector2(at, top),
            Color.Transparent, Color.Black);

        foreach (var piece in row.Pieces)
        {
            _graphics.DrawText(piece.Text, new Vector2(at, top), piece.Colour);
            at += piece.Size.X;
        }
    }
}
