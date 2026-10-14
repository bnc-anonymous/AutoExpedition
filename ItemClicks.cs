using ExileCore2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Choosing and aiming clicks on items laid out in a grid - tablets in the stash, the bench or the inventory, and the
/// inventory's items for Autodeposit: which item of a batch comes next, where in its cell to aim, and whether the cursor
/// is in it. Shared by every batch of clicks so they follow one rule. See Tablets.MoveNext and InventoryDeposit.Choose.
/// </summary>
internal static class ItemClicks
{
    /// <summary>Picks where in a cell a click lands, and which item of a batch comes next.</summary>
    private static readonly Random Pick = new();

    /// <summary>Pixels kept clear of a cell's edge when aiming anywhere in it, so the click stays on the item.</summary>
    private const float AnywhereMargin = 4f;

    /// <summary>Whether the game's cursor is inside a cell, 2 pixels in from its edges.</summary>
    public static bool CursorInside(GameController gc, RectangleF cell)
    {
        var at = Safe.Read(() => new Vector2(gc.IngameState.MousePosX, gc.IngameState.MousePosY), Vector2.Zero);

        return at.X >= cell.Left + 2f && at.X <= cell.Right - 2f && at.Y >= cell.Top + 2f && at.Y <= cell.Bottom - 2f;
    }

    /// <summary>A cell less AnywhereMargin on every side, so a click aimed in it stays on the item.</summary>
    private static RectangleF Inside(RectangleF cell) =>
        cell.Width > 2f * AnywhereMargin && cell.Height > 2f * AnywhereMargin
            ? new RectangleF(cell.Left + AnywhereMargin, cell.Top + AnywhereMargin, cell.Width - 2f * AnywhereMargin,
                cell.Height - 2f * AnywhereMargin)
            : new RectangleF(cell.Center.X, cell.Center.Y, 0f, 0f);

    /// <summary>The point of a cell, less its margin, nearest to a point outside it, or the point itself when inside.</summary>
    public static Vector2 NearestPointOf(RectangleF cell, Vector2 from)
    {
        var inside = Inside(cell);

        return new Vector2(Math.Clamp(from.X, inside.Left, inside.Right), Math.Clamp(from.Y, inside.Top, inside.Bottom));
    }

    /// <summary>
    /// The next of a batch of items to click, the one rule every batch of clicks follows - tablets withdrawn, deposited
    /// or loaded into the bench, tablets crafted, the inventory deposited. Withdrawing valuable tablets keeps its batch in
    /// order of worth instead (Tablets.BatchNext's inOrder).
    ///
    /// The batch's first: the top-left item 40% of the time, the bottom-left 40%, the one under the top-left 10% (the
    /// top-left itself when nothing is under it), and any of them 10%. Left is the leftmost column holding an item. Every
    /// later one, and every one with first false: the nearest the cursor 95% of the time, the second nearest 4%, the
    /// third 1%. Loading the bench passes first false always, so each load of three starts from the cursor. Chosen
    /// 2026-10-11, the later clicks' shares changed from 85, 10 and 5 on 2026-10-12; not measured.
    /// </summary>
    public static T NextItemOf<T>(IReadOnlyList<T> items, Func<T, RectangleF> rectOf, Vector2 mouse, bool first)
    {
        if (!first)
        {
            // The nearest three in one pass, rather than sorting the batch for them.
            T nearest = items[0], second = items[0], third = items[0];
            float nearestGap = float.MaxValue, secondGap = float.MaxValue, thirdGap = float.MaxValue;

            foreach (var item in items)
            {
                var gap = Vector2.DistanceSquared(mouse, NearestPointOf(rectOf(item), mouse));

                if (gap < nearestGap)
                {
                    (third, thirdGap) = (second, secondGap);
                    (second, secondGap) = (nearest, nearestGap);
                    (nearest, nearestGap) = (item, gap);
                }
                else if (gap < secondGap)
                {
                    (third, thirdGap) = (second, secondGap);
                    (second, secondGap) = (item, gap);
                }
                else if (gap < thirdGap)
                    (third, thirdGap) = (item, gap);
            }

            // 95% the nearest, 4% the second, 1% the third; with fewer items, the farthest there is.
            var closeness = Pick.Next(100);

            return closeness < 95 || items.Count == 1 ? nearest
                : closeness < 99 || items.Count == 2 ? second
                : third;
        }

        var leftmost = items.Min(i => rectOf(i).Left);
        var column = items.Where(i => rectOf(i).Left - leftmost < rectOf(i).Width / 2f).OrderBy(i => rectOf(i).Top).ToList();
        var roll = Pick.Next(100);

        return roll switch
        {
            < 40 => column[0],
            < 80 => column[^1],
            < 90 => column.Count > 1 ? column[1] : column[0],
            _ => items[Pick.Next(items.Count)],
        };
    }

    /// <summary>A point anywhere in a cell, uniformly: successive clicks do not cluster on its centre.</summary>
    public static Vector2 AnywhereIn(RectangleF cell)
    {
        var inside = Inside(cell);

        return new Vector2(inside.Left + (float)Pick.NextDouble() * inside.Width, inside.Top + (float)Pick.NextDouble() * inside.Height);
    }

    /// <summary>How deep into a cell, as a share of its size, a click aimed near the edge facing the cursor may land.</summary>
    private const float NearEdgeDepth = 0.5f;

    /// <summary>
    /// How deep into a cell, as a share of its size, such a click lands at least: about 14 pixels of a 70 pixel cell,
    /// where it was AnywhereMargin's 4 and a click landed in the cell before (2026-10-09 23:36). Chosen.
    /// </summary>
    private const float NearEdgeLeast = 0.2f;

    /// <summary>How far along that edge, as a share of the cell's size either way, the click may stray.</summary>
    private const float NearEdgeSpread = 0.2f;

    /// <summary>
    /// A point just inside a cell's edge facing the cursor: the cell's nearest point to it, taken a random depth inwards
    /// on each axis the cursor lies outside, and a random way along it on each axis the cursor lies within.
    /// </summary>
    public static Vector2 NearEdgeOf(RectangleF cell, Vector2 from)
    {
        var inside = Inside(cell);
        var nearest = NearestPointOf(cell, from);

        float Along(float at, float point, float low, float high, float size)
        {
            if (point < low)
                return at + (NearEdgeLeast + (float)Pick.NextDouble() * (NearEdgeDepth - NearEdgeLeast)) * size;

            if (point > high)
                return at - (NearEdgeLeast + (float)Pick.NextDouble() * (NearEdgeDepth - NearEdgeLeast)) * size;

            return Math.Clamp(at + ((float)Pick.NextDouble() * 2f - 1f) * NearEdgeSpread * size, low, high);
        }

        return new Vector2(Along(nearest.X, from.X, inside.Left, inside.Right, inside.Width),
            Along(nearest.Y, from.Y, inside.Top, inside.Bottom, inside.Height));
    }

    /// <summary>
    /// A move's flourishes for a run's log - ", swooped", ", overshot 6px" - or nothing for a plain move. See
    /// ExileInput2's LastMoveTimes.
    /// </summary>
    public static string FlourishSaid((float WaitMs, float TravelMs, bool Swooped, float OvershotPx) times) =>
        (times.Swooped ? ", swooped" : "") + (times.OvershotPx > 0f ? $", overshot {times.OvershotPx:0}px" : "");
}
