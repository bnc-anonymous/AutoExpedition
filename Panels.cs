using ExileCore2;
using ExileCore2.PoEMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoExpedition;

/// <summary>
/// What is open over the world, and where.
///
/// Two different answers, because the windows are two different things.
///
/// **Fullscreen and large panels hide everything.** The atlas, the passive tree, the inventory and
/// stash: there is no world visible behind them, so nothing drawn on the world means anything.
/// These are the two lists every overlay in the HUD checks.
///
/// **Everything else just covers part of the screen.** The Expedition windows sit over a dig site
/// you are still looking at, and blanking the whole overlay because one of them is open throws away
/// the rest of the site to protect a corner of it. So those are reported as rectangles instead, and
/// drawing skips what falls inside them.
///
/// The overlay cannot be drawn genuinely behind them - the HUD composites on top of the game and
/// there is no z-order to slot into - but skipping the covered part looks the same.
/// </summary>
internal static class Panels
{
    /// <summary>Whether the world is hidden outright, so nothing on it is worth drawing.</summary>
    public static bool Hidden(GameController gc)
    {
        var ui = Safe.Read(gc, static g => g.IngameState.IngameUi, null);

        if (ui == null)
            return false;

        // **These two lists are the whole answer in this client, and adding to them cost sixty log
        // lines a second.** The HUD's notes warn that OpenLeftPanel and OpenRightPanel miss the
        // atlas and the passive tree, which is true; the inference that the inventory and stash
        // therefore need them was not. Both lists already carry that pair here - confirmed at the
        // keyboard, the overlay hides when either is opened over a dig site.
        //
        // What the two extra reads did instead was write "Element with index: 0 not found" twice a
        // frame, forever. Every named element on the interface is a hard-coded index path inside
        // ExileCore2, and in this client neither of those paths resolves: the read returns nothing,
        // logs a line, and answers no. A dump of every name on IngameUi lists eight such casualties
        // and those are the only two this plugin ever touched.
        return Safe.Read(() => ui.FullscreenPanels.Any(x => x.IsVisible), false) ||
               Safe.Read(() => ui.LargePanels.Any(x => x.IsVisible), false);
    }

    /// <summary>
    /// The windows that cover only part of the screen, as rectangles to avoid.
    ///
    /// Built once a frame by the caller and passed around, since every marker is tested against it.
    /// </summary>
    /// <summary>
    /// Whether a spot on screen has a piece of interface sitting on top of it.
    ///
    /// Covered() collects the panels this plugin knows by name, which is the right list for
    /// deciding whether to DRAW something - a world overlay under the atlas tree is noise. It is
    /// the wrong list for deciding whether something can be CLICKED, because the interface is
    /// littered with small panels nothing has a name for: the map objectives block in the top
    /// corner hid an excavated chest's label completely, and the plugin was willing to send the
    /// cursor into it.
    ///
    /// So this asks the tree instead. Every direct child of the interface root that is visible and
    /// covers the spot counts as in the way, with one exception: anything as large as the window
    /// itself, which is a layer rather than a panel - the world, the ground labels, the health
    /// globes' backdrop. Those cover everything and block nothing.
    ///
    /// **What blocks is what is PAINTED, not what a panel's bounds enclose.** The map objectives
    /// block is mostly empty: its heading and its lines of text sit inside a rectangle far larger
    /// than either, and the space between them can be clicked straight through. Treating the
    /// rectangle as solid refused clicks that would have worked, so the walk goes down to the
    /// leaves - an element with no visible children, or one with text on it - and only those count.
    ///
    /// Wrong in the cautious direction costs a walk; wrong the other way sends the cursor into a
    /// panel, and a click that lands anywhere unintended in this game moves the character. Hence
    /// this plus the hover check before the click, which needs no model of the interface at all.
    /// </summary>
    public static bool Blocked(GameController gc, Vector2 at, bool fresh = false)
    {
        // **The walk is for the click, and the click alone.**
        //
        // Two kinds of caller ask this. The run asks about the one spot it is on the point of
        // pressing, and wants the exact answer: a stale or approximate "clear" there sends the
        // cursor into a panel, and a click that lands anywhere unintended in this game moves the
        // character. The readiness pass asks about every link of the chain, every frame, to decide
        // what colour to paint a dot.
        //
        // Those cost the same and are worth wildly different amounts. Measured: the walk is 0.74ms
        // once the interface is snapshotted, and the readiness pass ran it about four times a frame
        // - three milliseconds of a sixteen millisecond frame spent colouring dots.
        //
        // **Memoising it did not help, and could not.** The answers were kept against the rounded
        // screen position, which moves under a world spot every time the camera does. Standing
        // still the memo hit; walking - the only time the stutter is felt - every frame was a miss.
        //
        // So the two callers get different tests. The run gets the walk. The drawing gets the
        // rectangles gathered once a frame by Snapshot, which is arithmetic: the fixed furniture
        // and the named panels, no tree, no keeping, nothing to go stale inside a frame.
        //
        // **What the cheap test misses is a dot painted green under some small unnamed element**,
        // and that costs nothing, because the run checks again with fresh set before it moves and
        // refuses with a reason if the spot turns out to be covered. The expensive answer is still
        // there for the decision that needs it; it is no longer paid for sixty times a second to
        // tint something.
        if (!fresh)
        {
            bool furnished;

            using (Spent.On("Blocked/Furnished"))
                furnished = Furnished(gc, at);

            if (furnished)
                return true;

            using (Spent.On("Blocked/Covered"))
                return Covers(Covered(gc), at);
        }

        return Blocking(gc, at);
    }

    private static bool Blocking(GameController gc, Vector2 at)
    {
        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (ui == null || window.Width <= 0f || window.Height <= 0f)
            return false;

        // Written down rather than worked out, and checked first because it is both cheaper and
        // more certain than the walk. See Furniture.
        if (Furnished(gc, at))
            return true;

        // **Nothing to descend into when nothing top level covers the point.** The walk exists to
        // find the smallest visible element painting at a spot, and an element can only paint where
        // its ancestors do - so a point outside every top level child of the interface has nothing
        // above it at any depth. A spot on open ground is that point, which is almost every spot
        // this is asked about. See Snapshot.
        if (!Inside(_panels, at))
        {
            Culled++;

            return false;
        }

        Walked++;

        var hit = Over(ui, at, window.Width * window.Height * 0.6f, 0);

        // **The two tests disagreed about ground labels and only one of them had done the reading.**
        // The hover check knows that an expedition label is decoration except at three known button
        // paths - confirmed in game, the explosive goes down through the help icon and does not
        // through the buttons. This one knew only that something painted was there, so a strongbox's
        // label, which is six hundred pixels wide and carries two lines of text, refused every spot
        // behind it. The game places through it perfectly happily.
        //
        // So the rectangle test now ends in the same judgement the hover test makes. One rule, in
        // one place, for the one question they both ask.
        return hit != null && !Allowed(gc, hit).Allowed;
    }

    /// <summary>
    /// What Blocked decides about a spot and why, in words.
    ///
    /// **Blocked is a bool and the question behind it has three answers**: nothing is drawn there,
    /// something is drawn and a click passes through it, or something is drawn and it would take the
    /// click. Those want completely different responses and the readout could not tell them apart -
    /// so "it still refuses" was argued about from a dump that said nothing about the refusal.
    ///
    /// Reports the element by its text or texture, which is what identifies it to somebody looking
    /// at the screen, and the verdict Allowed reached about it.
    /// </summary>
    /// <summary>
    /// What would take a click at this spot, named in a few words for the player. Why is the full diagnostic, for the
    /// dump. See Placement's Click location obscured.
    /// </summary>
    public static string Blocker(GameController gc, Vector2 at)
    {
        if (Furnished(gc, at))
            return InBuffBar(gc, at) ? "the buff bar" : "the bottom bar";

        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);
        var hit = ui == null || window.Width <= 0f ? null : Over(ui, at, window.Width * window.Height * 0.6f, 0);

        if (hit == null)
            return "part of the interface";

        if (IsEnemyHealthBar(hit))
            return "an enemy health bar";

        if (InChat(gc, hit))
            return "the chat";

        var text = (Safe.Read(hit, static e => e.Text, null) ?? "").Trim();

        if (text.Length > 0)
            return text.Length > 40 ? $"\"{text[..40]}...\"" : $"\"{text}\"";

        var texture = Safe.Read(hit, static e => e.TextureName, null) ?? "";

        return texture.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(texture) : "part of the interface";
    }

    /// <summary>Whether an element is, or sits inside, one of the enemy health bars, by its texture.</summary>
    private static bool IsEnemyHealthBar(Element element)
    {
        // The hit element and up to three parents: the ornament, the bar and its fill are separate elements.
        var at = element;

        for (var step = 0; at != null && step < 4; step++, at = Safe.Read(at, static e => e.Parent, null))
        {
            var texture = Safe.Read(at, static e => e.TextureName, null) ?? "";

            if (texture.Contains("/EnemyHealthBars/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether any rare or unique monster is alive among the loaded entities.</summary>
    private static bool AnyLivingRareOrUnique(GameController gc)
    {
        var monsters = Safe.Read(gc, static g =>
            g.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore2.Shared.Enums.EntityType.Monster, out var of)
                ? of
                : null, null);

        if (monsters == null)
            return true;

        foreach (var monster in monsters)
        {
            if (!Safe.Read(monster, static e => e.IsAlive, false))
                continue;

            var rarity = Safe.Read(monster, static e =>
                e.GetComponent<ExileCore2.PoEMemory.Components.ObjectMagicProperties>()?.Rarity ??
                ExileCore2.Shared.Enums.MonsterRarity.White, ExileCore2.Shared.Enums.MonsterRarity.White);

            if (rarity is ExileCore2.Shared.Enums.MonsterRarity.Rare or ExileCore2.Shared.Enums.MonsterRarity.Unique)
                return true;
        }

        return false;
    }

    public static string Why(GameController gc, Vector2 at)
    {
        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (ui == null || window.Width <= 0f || window.Height <= 0f)
            return "no interface to test against";

        if (Furnished(gc, at))
            return $"BLOCKED by {(InBuffBar(gc, at) ? "the buff bar" : "the bottom bar")} - {Furnishings(gc)}";

        var hit = Over(ui, at, window.Width * window.Height * 0.6f, 0);

        if (hit == null)
            return "clear - nothing drawn there";

        var text = Safe.Read(hit, static e => e.Text, null) ?? "";
        var texture = Safe.Read(hit, static e => e.TextureName, null) ?? "";
        var rect = Safe.Read(hit, static e => e.GetClientRectCache, default);
        var verdict = Allowed(gc, hit);

        var named = text.Trim().Length > 0
            ? $"\"{text.Trim()}\""
            : texture.Length > 0
                ? texture
                : "an unnamed painted element";

        // **Everything that decided it, because the name is often the least reliable part.** A
        // read of Text on an element that has none comes back as whatever bytes were there, so a
        // blocker can announce itself as two characters of nonsense - and the walk treats any
        // non-empty text as proof the element paints. The path says what it really is, and the
        // texture, background and interactive flags say which of the three tests caught it.
        return $"{(verdict.Allowed ? "CLEAR" : "BLOCKED")} by {named} " +
               $"at ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) - {verdict.Why}" +
               $" [{Path(hit)}" +
               $" texture={(texture.Length > 0 ? texture : "none")}" +
               $" bg={Safe.Read(hit, static e => e.BgColor.A, (byte)0)}" +
               $" lit={Interactive(hit)}" +
               $" kids={Safe.Read(hit, static e => (int)e.ChildCount, 0)}]";
    }

    /// <summary>
    /// What is actually drawn at this spot, or null when nothing is.
    ///
    /// Returns the element rather than a bool so the caller can ask what it is. See Blocked.
    /// </summary>
    private static Element Over(Element element, Vector2 at, float whole, int depth)
    {
        if (element == null || depth > 8)
            return null;

        var kids = Safe.Kids(element);

        if (kids == null)
            return null;

        foreach (var kid in kids)
        {
            if (kid == null || !Safe.Read(kid, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(kid, static e => e.GetClientRectCache, default);

            if (rect.Width <= 0f || rect.Height <= 0f ||
                at.X < rect.Left || at.X > rect.Right || at.Y < rect.Top || at.Y > rect.Bottom)
                continue;

            // A layer rather than a panel: the world, the ground labels, the backdrop. They cover
            // everything and block nothing, so only what is inside them matters.
            if (rect.Width * rect.Height >= whole)
            {
                var inside = Over(kid, at, whole, depth + 1);

                if (inside != null)
                    return inside;

                continue;
            }

            // Whether this element PAINTS anything, which is the whole question.
            //
            // Having no children was the first answer and it is wrong in both directions: a panel's
            // backdrop is a container full of rows and still perfectly solid, while an empty layout
            // box paints nothing and can be clicked straight through. The element says which it is
            // - a texture to draw, text to write, or a background colour that is not transparent.
            // Anything with none of those is a box holding other things, so the walk goes into it.
            var texture = Safe.Read(kid, static e => e.TextureName, null);
            var text = Safe.Read(kid, static e => e.Text, null);
            var background = Safe.Read(kid, static e => e.BgColor.A, (byte)0);

            // A texture or a filled background is the element itself painting, whatever else it
            // holds - a panel's backdrop is a container full of rows and still perfectly solid.
            if (!string.IsNullOrEmpty(texture) || background > 0)
                return kid;

            // **Text only counts on something with nothing inside it.**
            //
            // Reading Text on an element that has none returns whatever bytes happened to be there,
            // and the walk was taking any non-empty string as proof the element paints. A container
            // of nine children at the top of the screen - no texture, no background - announced
            // itself as two characters of nonsense and refused every spot behind it.
            //
            // A real piece of text is a leaf: the label IS the text. A box with children that also
            // reports text is a bad read far more often than it is a label, and the cost of being
            // wrong is only that the walk carries on into the children, where anything actually
            // painted will catch the point anyway.
            var holds = Safe.Read(kid, static e => (int)e.ChildCount, 0);

            if (!string.IsNullOrWhiteSpace(text) && holds == 0)
                return kid;

            var deeper = Over(kid, at, whole, depth + 1);

            if (deeper != null)
                return deeper;
        }

        return null;
    }

    /// <summary>
    /// Whether another object's ground label is sitting over this point.
    ///
    /// **A label does not block an explosive and does block a click**, which is why this is a test
    /// of its own rather than another entry in Covered. A Verisium Sentry standing near a remnant
    /// puts its name across the remnant's Runeshape Combinations button: the placement indicator
    /// ignores it entirely, so the ring is green and the plan is sound, and the click lands on the
    /// sentry's label instead of the button. Nothing about that is visible from the geometry the
    /// rest of this file checks.
    ///
    /// The label belonging to <paramref name="owner"/> is skipped, because the button is a child of
    /// it - a remnant's own label always covers its own button, and always should.
    ///
    /// See AutomationSettings.Unhide for what is done about it.
    /// </summary>
    public static bool Shaded(GameController gc, Vector2 at, Entity owner)
    {
        var mine = Safe.Read(() => owner?.Id ?? 0u, 0u);

        foreach (var label in Ground.Labels(gc) ?? [])
        {
            if (label == null)
                continue;

            if (mine != 0u && Safe.Read(() => label.ItemOnGround?.Id ?? 0u, 0u) == mine)
                continue;

            if (Covers(Safe.Read(() => label.Label, null), at))
                return true;
        }

        // **The hovered label is not in that list, and it is the one most likely to be in the way.**
        // A plate in ItemsOnGroundLabels is the small name; hovering it swaps in a far larger panel
        // carried separately as LabelOnHover, and a relic's mods make it large indeed. Reported from
        // the game with a Vaal relic's text lying across the Runeshape Combinations button while this
        // said "nothing over the button, 79 ground labels looked at" - which was true of the 79 and
        // false of the screen.
        //
        // The hovered item is skipped when it is the owner's own, on the same reasoning as above: a
        // remnant's own label sitting over its own button is the case the caller is already handling.
        var hovering = Safe.Read(() => gc.IngameState.IngameUi.ItemsOnGroundLabelElement, null);

        if (hovering != null &&
            (mine == 0u || Safe.Read(() => hovering.ItemOnHover?.Id ?? 0u, 0u) != mine) &&
            Covers(Safe.Read(() => hovering.LabelOnHover, null), at))
            return true;

        // And the ground tooltip, which is a third element again - the framed panel the client draws
        // for an item under the cursor. Cheap to ask, and it costs a run when it is missed.
        var tooltip = Safe.Read(() => gc.IngameState.IngameUi.ItemOnGroundTooltip, null);

        return tooltip != null &&
               (Covers(tooltip, at) || Covers(Safe.Read(() => tooltip.TooltipUI, null), at));
    }

    /// <summary>Whether a visible element's rectangle contains the point. See Shaded.</summary>
    private static bool Covers(Element element, Vector2 at)
    {
        if (element == null || !Safe.Read(() => element.IsVisible, false))
            return false;

        var rect = Safe.Read(() => element.GetClientRectCache, default(RectangleF));

        return rect.Width > 0f && rect.Height > 0f && rect.Contains(at);
    }

    /// <summary>
    /// The largest part of <paramref name="within"/> that none of <paramref name="blocked"/> covers.
    ///
    /// **Two unrelated obstructions turned out to be the same question.** A rare monster's health
    /// bar lying across the top of a Runeshape Combinations button, and the rune icons inside a
    /// combination row that the game simply will not accept a click on - neither can be moved, and
    /// in both cases the target is a rectangle with a hole in it rather than a rectangle that is
    /// gone. Aiming at the middle finds the hole; aiming at the clear part works.
    ///
    /// Bands rather than an exact largest-empty-rectangle, which is a much harder problem for no
    /// gain here: a blocker either sits across the target horizontally or vertically, so the widest
    /// clear column and the tallest clear row between them cover every case seen. The larger of the
    /// two wins, and an empty rect means there is nowhere to click.
    ///
    /// Inset a little, because a point on the boundary of an element belongs to whichever one the
    /// client rounds towards.
    /// </summary>
    /// <summary>Whether two rectangles overlap. ExileCore2's RectangleF has no such method.</summary>
    private static bool Meets(RectangleF a, RectangleF b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public static RectangleF Clearest(RectangleF within, IReadOnlyList<RectangleF> blocked,
        float inset = 3f)
    {
        if (within.Width <= 0f || within.Height <= 0f)
            return default;

        if (blocked == null || blocked.Count == 0)
            return within;

        var wide = Band(within, blocked, across: true, inset);
        var tall = Band(within, blocked, across: false, inset);

        return wide.Width * wide.Height >= tall.Width * tall.Height ? wide : tall;
    }

    /// <summary>
    /// The longest run along one axis that no blocker reaches into.
    ///
    /// Walks the edges of the blockers rather than every pixel: a clear run can only begin where the
    /// target begins or a blocker ends, and can only end where the target ends or a blocker begins,
    /// so those are the only positions worth testing.
    /// </summary>
    private static RectangleF Band(RectangleF within, IReadOnlyList<RectangleF> blocked, bool across,
        float inset)
    {
        var from = across ? within.Left : within.Top;
        var to = across ? within.Right : within.Bottom;

        var edges = new List<float> { from, to };

        foreach (var rect in blocked)
        {
            if (rect.Width <= 0f || rect.Height <= 0f || !Meets(rect, within))
                continue;

            edges.Add(across ? rect.Left : rect.Top);
            edges.Add(across ? rect.Right : rect.Bottom);
        }

        edges.Sort();

        var bestFrom = 0f;
        var bestTo = 0f;

        for (var i = 0; i + 1 < edges.Count; i++)
        {
            var low = MathF.Max(from, edges[i]);
            var high = MathF.Min(to, edges[i + 1]);

            if (high - low <= inset * 2f)
                continue;

            var middle = (low + high) / 2f;
            var clear = true;

            foreach (var rect in blocked)
            {
                if (rect.Width <= 0f || rect.Height <= 0f || !Meets(rect, within))
                    continue;

                var at = across ? rect.Left : rect.Top;
                var end = across ? rect.Right : rect.Bottom;

                if (middle > at && middle < end)
                {
                    clear = false;

                    break;
                }
            }

            if (clear && high - low > bestTo - bestFrom)
            {
                bestFrom = low;
                bestTo = high;
            }
        }

        if (bestTo - bestFrom <= inset * 2f)
            return default;

        return across
            ? new RectangleF(bestFrom + inset, within.Top + inset,
                bestTo - bestFrom - inset * 2f, MathF.Max(1f, within.Height - inset * 2f))
            : new RectangleF(within.Left + inset, bestFrom + inset,
                MathF.Max(1f, within.Width - inset * 2f), bestTo - bestFrom - inset * 2f);
    }

    /// <summary>
    /// Everything drawn over this point that is not the element itself, as rectangles.
    ///
    /// Used to aim AROUND an obstruction rather than to refuse because of one. See Clearest.
    /// </summary>
    public static List<RectangleF> Over(GameController gc, RectangleF within)
    {
        var found = new List<RectangleF>();

        foreach (var rect in Covered(gc))
        {
            if (rect.Width > 0f && rect.Height > 0f && Meets(rect, within))
                found.Add(rect);
        }

        return found;
    }

    public static List<RectangleF> Covered(GameController gc)
    {
        // **Gathered once a frame, like the rest of the interface.** This is asked by the drawing
        // for every link of the chain and by the run before it moves, and it used to build a fresh
        // list, a fresh array and three closures on each of those calls for an answer that cannot
        // change between two draws of the same frame.
        //
        // The list is handed out rather than copied, which is safe only because no caller writes to
        // it - checked, and the reason to keep it that way. See Snapshot.
        if (_coveredAt == Frame.Number)
            return _covered;

        _coveredAt = Frame.Number;

        var found = _covered;

        found.Clear();

        var ui = Safe.Read(gc, static g => g.IngameState.IngameUi, null);

        if (ui == null)
            return found;

        // The Expedition surfaces that exist in this game: the combinations window and the haggle
        // window. Both overlap a live dig site.
        //
        // **Four PoE1 leftovers used to be read here.** ExpeditionWindow, ExpeditionWindowEmpty,
        // ExpeditionLockerElement and ExpeditionNpcDialog are marked obsolete in ExileCore2 because
        // they are the FIRST game's expedition interface, and they were never going to open in this
        // one. Nothing is lost by not asking, and the four obsolete-API warnings this project had
        // been carrying go with them.
        foreach (var element in new[]
                 {
                     Safe.Read<Element>(() => ui.Expedition2Window, null),
                     Safe.Read<Element>(() => ui.HaggleWindow, null),
                 })
        {
            if (!Open(element))
                continue;

            var rect = Safe.Read(() => element.GetClientRect(), default);

            if (rect.Width > 0f && rect.Height > 0f)
                found.Add(rect);
        }

        return found;
    }

    /// <summary>Whether a point on screen is under one of those windows.</summary>
    /// <summary>
    /// Whether the game reckons there is a piece of UI under the cursor right now.
    ///
    /// The check that has to happen immediately before a click, and it cannot be done with
    /// rectangles. The overlay knows about the handful of panels it was taught to avoid; the game
    /// knows about every button, tooltip, minimap edge and quest tracker on the screen, and a click
    /// that lands on one of those opens something nobody asked for. UIHover is the game answering
    /// the question directly - it is what the client itself uses to decide whether a click goes to
    /// the interface or into the world.
    ///
    /// The root element is not UI for this purpose: UIHover is never null in practice, it reports
    /// the outermost container when the cursor is over open ground, so the test is whether the
    /// thing under the cursor has a PARENT. Anything nested inside the root is a real piece of
    /// interface.
    ///
    /// **Being interface is not enough - it has to be interface that takes clicks.** Half the
    /// screen is passive readout, and a world click goes straight through it: the map content
    /// panel's help icons, the rune icons drawn on a remnant's ground label. The parts that do
    /// swallow a click are the buttons - the Liquid Verisium on a remnant, the Runeshape
    /// Combinations button on the right of the same label - and refusing to place because the
    /// planned spot happened to sit under a `(i)` icon in the corner is refusing for nothing.
    ///
    /// The game separates the two itself, and the plugin already relies on it elsewhere: an
    /// element that takes clicks LIGHTS UP when the pointer is on it, which is exactly how the
    /// shatter button is confirmed before pressing it. So the test is not "is something there" but
    /// "is the thing there lit", walked up a few ancestors because the highlight often sits on a
    /// button's wrapper rather than on the icon inside it.
    ///
    /// Fails closed. A read that throws, or a state that cannot be made sense of, counts as "there
    /// is something there" - refusing to click costs a retry, clicking the wrong thing costs
    /// whatever that button does.
    /// </summary>
    public static bool UnderCursor(GameController gc)
    {
        return Safe.Read(gc, static g =>
        {
            var hover = g.IngameState.UIHover;

            if (hover == null || hover.Address == 0)
                return false;

            // The world itself, reported as the outermost element.
            if (hover.Parent == null)
                return false;

            if (!hover.IsVisible)
                return false;

            return !Allowed(g, hover).Allowed;
        }, true);
    }

    /// <summary>
    /// Whether this element is one of the few a click may safely pass through.
    ///
    /// **Everything blocks unless it is named here, and that direction is deliberate.** The test
    /// started the other way round - work out what would cost something and block only that - and
    /// the trouble with it is the things nobody has looked at yet. A lit button on a chest's label,
    /// a panel this plugin has never met, anything a patch adds: all of them read as "not on the
    /// list of costly things" and got a click sent into them. Refusing to place costs a keypress;
    /// clicking an unknown button costs whatever that button does.
    ///
    /// The list, and why each is on it:
    ///
    /// **The decoration on an expedition ground label.** A remnant's label carries rune icons and a
    /// help icon, and the help icon is the same size as the buttons and lights up under the cursor
    /// exactly as they do - so nothing about how it looks or behaves separates them. What separates
    /// them is position: the three things a click spends something on are at known child paths (the
    /// Runeshape Combinations button, the shatter button, the Liquid Verisium), and everything else
    /// on that label is drawing. Confirmed in game: the explosive goes down through the help icon
    /// and does not through the buttons.
    ///
    /// Nothing else is on the list. Somebody else's ground label is not - a chest's label opens a
    /// chest - and neither is any part of the interface proper.
    /// </summary>
    /// <returns>Whether a click may pass, and what decided it, for the readout.</returns>
    private static (bool Allowed, string Why) Allowed(GameController gc, Element hover)
    {
        // **An enemy health bar with no rare or unique monster alive.** The bar at the top of the screen stays in the
        // element tree reporting IsVisible true after its monster dies, and paints nothing. Seen 2026-10-02: a spot
        // refused under RareOrnament.dds at (1001,34 558x71) with no living rare loaded. Blocking only while one is
        // alive keeps the refusal for a bar that may really be drawn.
        if (IsEnemyHealthBar(hover) && !AnyLivingRareOrUnique(gc))
            return (true, "an enemy health bar, with no rare or unique monster alive to show it");

        // **The chat panel, which is on screen for hours after it stops being on screen.**
        //
        // Seen in a dump taken over a dig site: three lines from LOGIN - two channel joins and the
        // item filter's name and version - still alive in the element tree, reporting IsVisible
        // true, lit true, and an 850x93 rectangle sitting over the spot the plan wanted. Nothing
        // was drawn there. The game stops RENDERING old chat some way the message elements do not
        // report, so the walk found a solid obstruction where the player sees open ground and the
        // explosive was refused at a spot the game places through perfectly happily.
        //
        // **But only while it is shut.** Open the chat with enter and parts of it do take a click,
        // so passing everything through would trade a false refusal for a real misclick - the
        // trade this whole list exists to avoid.
        //
        // The title panel is what says which it is: confirmed in game, its IsVisible reads false
        // while chat is closed and true while it is open, where the message lines report visible
        // either way. Matched on the panel rather than on the words, since a message is whatever
        // somebody typed and the corner it sits in is a layout choice.
        // **Asked by address, not by name.** The obvious way to write this is to read PathFromRoot
        // and look for "ChatPanel" in it, and that is what it did - once per frame, for as long as
        // a chat line sat over a link. Every read of PathFromRoot makes ExileCore2 write "Element
        // with index: 0 not found" to the log, so the test that decided a click was safe was also
        // the thing filling the log sixty times a second.
        //
        // The panel is a named element with a real address, so walking the hit element's parents
        // and comparing addresses answers the same question, reads no strings, and logs nothing.
        if (InChat(gc, hover))
        {
            return Chatting(gc)
                ? (false, "chat, which is open and takes clicks")
                : (true, "chat, which is shut and paints nothing you can see");
        }

        var labels = Ground.Labels(gc);

        if (labels == null)
            return (false, "no ground labels to check against");

        var addresses = new HashSet<long>();

        for (var at = hover; at != null && addresses.Count < 12;
             at = Safe.Read(at, static e => e.Parent, null))
        {
            var address = Safe.Read(at, static e => (long)e.Address, 0L);

            if (address != 0L)
                addresses.Add(address);
        }

        foreach (var label in labels)
        {
            var element = Safe.Read(() => label.Label, null);
            var here = Safe.Read(element, static e => (long)e.Address, 0L);

            if (here == 0L || !addresses.Contains(here))
                continue;

            var metadata = Safe.Read(() => label.ItemOnGround?.Metadata, "") ?? "";

            // StrongBoxes as well as Expedition, and for the same reason the sweep needs both: a
            // buried strongbox is dig site content whose path does not always say so.
            if (!metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) &&
                !metadata.Contains("StrongBoxes", StringComparison.OrdinalIgnoreCase))
                return (false, $"label {Tail(metadata)}");

            // Only on a remnant. The paths are a remnant's - see Placement.Buttons - and on any
            // other label they resolve to whatever happens to be at those indices, which on a
            // strongbox is the whole label.
            if (metadata.StartsWith(Scan.RemnantMetadata, StringComparison.Ordinal))
            {
                foreach (var path in Placement.ButtonPaths)
                {
                    var button = Resolve(element, path);
                    var where = Safe.Read(button, static e => (long)e.Address, 0L);

                    if (where != 0L && addresses.Contains(where))
                        return (false, $"button {string.Join(",", path)} on {Tail(metadata)}");
                }

                return (true, $"decoration on {Tail(metadata)}");
            }

            // **A strongbox label DOES carry a button, which this used to deny.** The line above
            // said its label is "a name and a guard count with no button on it" and let every click
            // through. Read off the ground with the cursor on one: a 54x54 element, lit, sitting on
            // StrongBoxes/MartialStrongboxExpedition - the same size as a remnant's buttons and
            // behaving the same way. A placement click aimed at ground under it pressed it instead.
            //
            // The button paths cannot be used here, so what is left is the game's own lighting.
            // That is not good enough to tell a button from a decoration on a REMNANT, where a help
            // icon lights exactly as the buttons do - which is why the paths exist. On a label whose
            // controls nobody has mapped it is the only signal there is, and the cost is one-sided:
            // refusing costs a keypress and a nudge of the camera, pressing an unknown button costs
            // whatever that button does. See the direction this whole list is written in.
            //
            // The label itself still passes, so a click lands through the name and the guard count
            // as it did before. Only something the game is lighting inside it is refused.
            var hovered = Safe.Read(hover, static e => (long)e.Address, 0L);

            if (hovered != here && Interactive(hover))
                return (false, $"something lit on {Tail(metadata)} - a label whose controls are not mapped");

            return (true, $"decoration on {Tail(metadata)}");
        }

        return (false, "not a ground label");
    }

    /// <summary>
    /// Whether the game is lighting this element, or a close ancestor, under the pointer.
    ///
    /// Reported rather than relied on. It looked like the way to tell a button from a decoration
    /// and it is not: a remnant's help icon lights up exactly as its buttons do. Kept because it
    /// is the first thing worth knowing about an element nobody has identified yet.
    /// </summary>
    private static bool Interactive(Element element)
    {
        var at = element;

        for (var up = 0; up < 4 && at != null; up++, at = Safe.Read(at, static e => e.Parent, null))
        {
            if (Safe.Read(at, static e => e.HasShinyHighlight, false) ||
                Safe.Read(at, static e => e.isHighlighted, false))
                return true;
        }

        return false;
    }

    /// <summary>One element inside another by its path of child indices, or null.</summary>
    /// <summary>
    /// The individual controls that always take a click, named outright.
    ///
    /// **A model of what is painted cannot answer this, and seventeen dumps proved it.** The charm
    /// slots, the skill slots, the flask buttons and the detonator's own buttons all report no
    /// texture, no background and no text - so the walk goes straight through them and calls the
    /// ground behind them clear. The game disagrees: park the cursor on any of them and UIHover
    /// names it, which is the client saying a click there belongs to the interface.
    ///
    /// UIHover only answers for where the cursor already is, which is no use for deciding where to
    /// send it. So these are written down instead.
    ///
    /// **Each control, not the cluster it sits in.** The first version of this blocked the three
    /// bottom-bar containers outright, which is wrong in the way that matters: the bar is mostly
    /// gaps, and the ground shows through between one skill slot and the next. Blocking the
    /// containers threw away every one of those, and the whole reason these were gathered one at a
    /// time was that the spaces around them can be clicked. Each entry resolves to its own element
    /// and contributes only that element's rectangle.
    ///
    /// **This is indices into a live tree, so it is fragile by construction** - a patch that inserts
    /// one child anywhere above these shifts them all, and a wrong index silently blocks the wrong
    /// rectangle or nothing at all. That is the trade being made deliberately: a false refusal costs
    /// a spot, while a click into the skill bar fires a skill. See Furnishings, which reports what
    /// each path resolved to so a shift shows up in a dump rather than in the game.
    /// </summary>
    private static readonly int[][] Furniture =
    {
        // The charm slot that was sampled. Its neighbours sit under 96->6 beside it; only this one
        // was ever dumped, so only this one is claimed.
        [96, 6, 3],

        // The left cluster: the flask button and the three slots below it that were sampled.
        [96, 7, 2],
        [96, 7, 8, 2, 2],
        [96, 7, 8, 3, 2],
        [96, 7, 8, 4, 2],

        // The right cluster: the button beside the skill bar, then the bar's own slots.
        [96, 9, 8],

        // Slots 0 to 7 of the skill bar. Six of the eight were sampled - 0, 2, 3, 5, 6 and 7 - and
        // the two that were not are the same control at the same size in the same row, so they are
        // included rather than left as the one gap in a bar the cursor must not cross.
        [96, 9, 11, 0],
        [96, 9, 11, 1],
        [96, 9, 11, 2],
        [96, 9, 11, 3],
        [96, 9, 11, 4],
        [96, 9, 11, 5],
        [96, 9, 11, 6],
        [96, 9, 11, 7],

        // The detonator's own controls: the placement toggle, and the revert button on top of it.
        [96, 9, 16, 1, 0],
        [96, 9, 16, 1, 0, 0, 1],
    };

    /// <summary>
    /// Whether a point is on a piece of that furniture.
    ///
    /// The whole cluster rather than the individual control: the gaps between two skill slots are
    /// not somewhere to put a cursor either, and a rectangle around the group is both cheaper and
    /// closer to how a person reads that part of the screen.
    /// </summary>
    public static bool Furnished(GameController gc, Vector2 at)
    {
        Snapshot(gc);

        return Inside(_furniture, at);
    }

    /// <summary>
    /// Every rectangle the interface is painting this frame, gathered once.
    ///
    /// **Asking about a point used to mean reading the interface again.** Furnished resolved twenty
    /// element paths per call - each one a walk down child lists materialised out of game memory -
    /// and Blocking then descended the whole tree behind it. The readiness pass asks about a point
    /// per link, the overlay asks for its own reasons, and the run asks before it moves: measured
    /// at 2.4ms for a single descent.
    ///
    /// None of it moves inside a frame. So it is read once and every question afterwards is a
    /// handful of float comparisons against two lists: the furniture, which is the fixed bottom
    /// bar and its neighbours, and the panels, which is whatever top level windows are open.
    ///
    /// **The panels list is what makes the walk rare.** A point outside every top level element
    /// cannot have anything painted on it, so there is nothing to descend into - and a spot on open
    /// ground, which is almost every spot this is asked about, ends there.
    ///
    /// **Not every frame, and the two layers do different jobs.** Blocked answers from its own
    /// memo first and only reaches here when a point misses it - so this rebuilds at most once a
    /// frame, and only on a frame where some point actually has to be worked out. At the default
    /// keeping window that is a few times a second rather than sixty.
    ///
    /// The memo saves asking about the SAME point twice; this saves reading the interface twice
    /// for two DIFFERENT points in one frame, which is what the readiness pass does at every link
    /// of the chain. Neither covers the other's case.
    ///
    /// Keyed on the frame counter rather than on a clock, because that is what "does not move
    /// inside a frame" means. See Frame and Blocked.
    /// </summary>
    private static void Snapshot(GameController gc)
    {
        if (_snapped == Frame.Number)
            return;

        // **Screen furniture does not move when the camera does, which is why keeping it works
        // here and did not work for a world spot.**
        //
        // An earlier attempt kept the answer to "is this world point covered" against the point's
        // screen position, and the camera moves that under a fixed spot every frame - so it hit
        // while standing still and missed on every frame of walking, which is the only time the
        // stutter is felt. These rectangles are the opposite: the bottom bar, the panels, the
        // windows. They sit at fixed screen positions and change when somebody opens something,
        // not sixty times a second.
        //
        // Rebuilding them costs 1.67MB of throwaway objects, because reaching a written-down path
        // reads the whole child-pointer array at every level it passes. At sixty frames a second
        // that is a hundred megabytes a second of garbage for an answer nobody could see change.
        //
        // What staleness buys is a panel that opened staying uncounted for up to the interval,
        // which shows as a dot keeping its colour for a moment. The press itself is never stale:
        // it goes through Blocking with fresh set, which reads the interface there and then.
        var now = Clock.ElapsedMilliseconds;

        if (_furnished && now - _snappedAt < Math.Max(0, Keeping))
            return;

        _snappedAt = now;
        _furnished = true;
        _snapped = Frame.Number;
        _furniture.Clear();
        _panels.Clear();

        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);

        if (ui == null)
            return;

        if (BuffBarRect(ui) is { Width: > 0f, Height: > 0f } buffs)
            _furniture.Add(buffs);

        foreach (var path in Furniture)
        {
            var element = Resolve(ui, path);

            if (element == null || !Safe.Read(element, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(element, static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width > 0f && rect.Height > 0f)
                _furniture.Add(rect);
        }

        var kids = Safe.Kids(ui);

        if (kids == null)
            return;

        foreach (var kid in kids)
        {
            if (kid == null || !Safe.Read(kid, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width <= 0f || rect.Height <= 0f)
                continue;

            _panels.Add(rect);

            var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default(RectangleF));

            if (window.Width > 0f && rect.Width * rect.Height >= window.Width * window.Height * 0.6f)
                Wide++;
        }
    }

    /// <summary>
    /// The largest band of the screen that no open side panel and no part of the Escape menu covers: the whole window
    /// when nothing is open, empty when nothing is clear. See DisplaySettings.HideBehindPanels.
    ///
    /// A side panel - the inventory, the character sheet, the stash - is the top level child of the interface that holds
    /// IngameUi.OpenLeftPanel or OpenRightPanel, while it is visible and shows at least one child. On a 2560x1440 window
    /// (2026-10-06) the character sheet was child 33 at (0,0 887x1440) and the inventory child 34. A test on shape alone
    /// was tried first and took elements that are not panels; see the comment where they are resolved.
    ///
    /// The Escape menu is not under IngameUi. It is looked for under EscapeState.UIRoot while TheGame.IsEscapeState,
    /// taking the first elements down from that root smaller than three fifths of the window. In this client that root
    /// does not resolve - it reads as invisible, with an infinite rectangle and no children (2026-10-06) - and the menu
    /// is not under IngameState.UIRoot either, so nothing is found. The answer is then empty, and the caller draws
    /// nothing while the menu is open rather than drawing over it.
    ///
    /// One band, because a draw list clips to one rectangle: with a menu in the middle of the screen the drawing keeps
    /// the larger of the strips beside, above or below it. See Clearest.
    /// </summary>
    public static RectangleF ClearOfPanels(GameController gc)
    {
        // **Worked out every 100 ms, the whole answer at once** - which panels, whether they show, and the band they
        // leave clear. The left panel is a different element for each panel (the character sheet 33, the market 38, in
        // dumps of 2026-10-06), so finding it and checking it are one question and are asked together. A panel just
        // opened or closed is clipped wrongly for at most this long; the length is the player's choice. Every frame it
        // cost about 0.11 ms and 220 KB a frame (2026-10-06), so at 100 ms about a twentieth of a millisecond a frame.
        if (Clock.ElapsedMilliseconds - _clearOfPanelsAt < ClearOfPanelsMs)
            return _clearOfPanels;

        _clearOfPanelsAt = Clock.ElapsedMilliseconds;
        SidePanels = 0;
        EscapeMenuOpen = false;
        _panelsToClear.Clear();

        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default(RectangleF));

        // Client coordinates, as the interface rectangles are.
        var whole = new RectangleF(0f, 0f, window.Width, window.Height);

        _clearOfPanels = whole;

        if (window.Width <= 0f || window.Height <= 0f)
            return _clearOfPanels;

        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);
        var kids = ui == null ? null : Safe.Kids(ui);
        // **The game's own two side panels, by name, not anything shaped like one.** A test on size alone - full height,
        // at an edge, under half the window - took the chat's column, and then an element at index 98 that is always
        // visible at 0,0 887x1440 whatever is open (2026-10-06), and cut the left of the screen off with nothing open.
        // OpenLeftPanel was the character sheet's element, index 33, in the same dumps. An index that does not resolve
        // leaves nothing clipped rather than the wrong thing.
        //
        // These two names resolve in this client - 19 "not found" log lines that whole day, none at the lookup's
        // rhythm - so asking every 100 ms writes nothing to the log. See Hidden.
        var leftPanelTop = TopLevelOf(ui, Safe.Read(gc, static g => (Element)g.IngameState.IngameUi.OpenLeftPanel, null));
        var rightPanelTop = TopLevelOf(ui, Safe.Read(gc, static g => (Element)g.IngameState.IngameUi.OpenRightPanel, null));

        SidePanelsSaid = "";

        if (kids != null)
        {
            for (var i = 0; i < kids.Count; i++)
            {
                var kid = kids[i];
                var address = kid == null ? 0L : Safe.Read(kid, static e => (long)e.Address, 0L);
                var side = address != 0L && address == leftPanelTop ? "left" : address != 0L && address == rightPanelTop ? "right" : null;

                if (side == null)
                    continue;

                var visible = Safe.Read(kid, static e => e.IsVisible, false);
                var rect = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

                // Showing something, not only there: a panel that is open shows at least one child.
                var showing = visible ? VisibleChildrenOf(kid) : 0;

                SidePanelsSaid += $" {side} [{i}] ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) " +
                                  (visible ? $"showing {showing}" : "closed");

                if (!visible || showing == 0 || rect.Width <= 0f || rect.Height <= 0f)
                    continue;

                _panelsToClear.Add(rect);
                SidePanels++;
            }
        }

        if (leftPanelTop == 0L && rightPanelTop == 0L)
            SidePanelsSaid = " (OpenLeftPanel and OpenRightPanel did not resolve, so nothing is clipped for them)";

        if (Safe.Read(gc, static g => g.Game.IsEscapeState, false))
        {
            EscapeMenuOpen = true;

            var before = _panelsToClear.Count;

            EscapeMenuParts(Safe.Read(gc, static g => g.Game.EscapeState.UIRoot, null), window.Width * window.Height * 0.6f, 0);

            EscapeMenuRects = _panelsToClear.Count - before;

            if (EscapeMenuRects == 0)
            {
                _clearOfPanels = new RectangleF(0f, 0f, 0f, 0f);

                return _clearOfPanels;
            }
        }

        if (_panelsToClear.Count > 0)
            _clearOfPanels = Clearest(whole, _panelsToClear, 0f);

        return _clearOfPanels;
    }

    /// <summary>Adds the Escape menu's elements under this one to the rectangles to keep clear. See ClearOfPanels.</summary>
    private static void EscapeMenuParts(Element at, float windowSized, int depth)
    {
        var kids = at == null || depth > 3 ? null : Safe.Kids(at);

        if (kids == null)
            return;

        foreach (var kid in kids)
        {
            if (kid == null || !Safe.Read(kid, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(kid, static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width <= 0f || rect.Height <= 0f)
                continue;

            if (rect.Width * rect.Height >= windowSized)
                EscapeMenuParts(kid, windowSized, depth + 1);
            else
                _panelsToClear.Add(rect);
        }
    }

    /// <summary>How many of an element's children are visible with a size of their own. See ClearOfPanels.</summary>
    private static int VisibleChildrenOf(Element element)
    {
        var showing = 0;

        foreach (var child in Safe.Kids(element) ?? [])
        {
            if (child == null || !Safe.Read(child, static e => e.IsVisible, false))
                continue;

            var rect = Safe.Read(child, static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width > 0f && rect.Height > 0f)
                showing++;
        }

        return showing;
    }

    /// <summary>The address of the top level child of the interface that holds this element, or nought. See ClearOfPanels.</summary>
    private static long TopLevelOf(Element ui, Element inside)
    {
        var root = Safe.Read(ui, static e => (long)e.Address, 0L);

        for (var at = inside; at != null; at = Safe.Read(at, static e => e.Parent, null))
        {
            var parent = Safe.Read(at, static e => e.Parent, null);

            if (parent == null)
                return 0L;

            if (Safe.Read(parent, static e => (long)e.Address, 0L) == root)
                return Safe.Read(at, static e => (long)e.Address, 0L);
        }

        return 0L;
    }

    /// <summary>How often ClearOfPanels works its answer out again. The player's choice, not measured.</summary>
    private const int ClearOfPanelsMs = 100;


    /// <summary>Which top level children ClearOfPanels last took for side panels, by index and rectangle, for the dump.</summary>
    public static string SidePanelsSaid { get; private set; } = "";

    /// <summary>How many side panels ClearOfPanels last found, for the dump.</summary>
    public static int SidePanels { get; private set; }

    /// <summary>Whether ClearOfPanels last found the Escape menu open, for the dump.</summary>
    public static bool EscapeMenuOpen { get; private set; }

    /// <summary>How many rectangles of the Escape menu ClearOfPanels last found, for the dump.</summary>
    public static int EscapeMenuRects { get; private set; }

    /// <summary>What ClearOfPanels last answered, for the dump.</summary>
    public static RectangleF LastClearOfPanels => _clearOfPanels;

    /// <summary>The rectangles ClearOfPanels last kept clear, for the dump. Not to be written to.</summary>
    public static IReadOnlyList<RectangleF> LastPanelsToClear => _panelsToClear;

    private static readonly List<RectangleF> _panelsToClear = new();

    private static RectangleF _clearOfPanels;

    private static long _clearOfPanelsAt = -10000;

    /// <summary>Whether a point falls in any of these. See Snapshot.</summary>
    private static bool Inside(List<RectangleF> rects, Vector2 at)
    {
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];

            if (at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom)
                return true;
        }

        return false;
    }

    /// <summary>What Covered last gathered, and the frame it gathered it on. See Covered.</summary>
    private static readonly List<RectangleF> _covered = new();

    private static int _coveredAt = -1;

    private static readonly List<RectangleF> _furniture = new();

    private static readonly List<RectangleF> _panels = new();

    /// <summary>
    /// Throws away the gathered rectangles, so the next question reads the interface again.
    ///
    /// For the reset button, which means "the state of having just walked in". A kept rectangle is
    /// a reading like any other, and a measurement run that starts with one is warm. See
    /// Caches.Clear.
    /// </summary>
    public static void Forget()
    {
        _snapped = -1;
        _snappedAt = 0;
        _furnished = false;
        _coveredAt = -1;
        _furniture.Clear();
        _panels.Clear();
        _covered.Clear();
        Walked = 0;
        Culled = 0;
        Wide = 0;
    }

    private static int _snapped = -1;

    private static long _snappedAt;

    private static bool _furnished;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>
    /// How long the gathered interface rectangles stand before they are read again, in
    /// milliseconds. See PlacementCircleSettings.PanelCacheMs and Snapshot.
    ///
    /// **A field rather than a settings read**, and set once a frame by the plugin, the same way
    /// the solver's rules are carried rather than looked up. This is reached from the drawing, the
    /// readiness pass and the run, and most of those call sites have no settings object to hand.
    ///
    /// Nought turns the keeping off, which is what the slider's bottom end means: the rectangles
    /// are then gathered once a frame, as they were before this was a setting.
    /// </summary>
    public static int Keeping { get; set; } = 100;

    /// <summary>
    /// How many point questions ended in a tree walk, and how many the cull answered.
    ///
    /// **A cull that never fires looks exactly like one that works.** The rect list it tests
    /// against is the top level children of the interface, and if those are mostly containers the
    /// size of the screen then every point is inside one and nothing is culled - which is a thing
    /// to know rather than to assume. Printed by the dump beside the frame table.
    /// </summary>
    public static int Walked { get; private set; }

    /// <summary>And how many never reached it. See Walked.</summary>
    public static int Culled { get; private set; }

    /// <summary>How many of the snapshot's rectangles are big enough to cover most of the screen.</summary>
    public static int Wide { get; private set; }

    /// <summary>How many rectangles the cull tests against at all. See Walked.</summary>
    public static int Rects => _panels.Count;

    /// <summary>What each named path resolved to, so a shifted index is visible. See Furniture.</summary>
    public static string Furnishings(GameController gc)
    {
        var ui = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);

        if (ui == null)
            return "no interface";

        var said = new List<string>();
        var found = 0;
        var area = 0f;

        foreach (var path in Furniture)
        {
            var element = Resolve(ui, path);
            var where = string.Join("->", path);

            // Only the failures are named. Sixteen healthy rectangles is a wall of text nobody
            // reads; one missing index is the thing worth seeing, and it is what a game patch does.
            if (element == null)
            {
                said.Add($"{where} MISSING");

                continue;
            }

            var rect = Safe.Read(element, static e => e.GetClientRectCache, default(RectangleF));

            if (rect.Width <= 0f || rect.Height <= 0f)
            {
                said.Add($"{where} no rectangle");

                continue;
            }

            found++;
            area += rect.Width * rect.Height;
        }

        var buffs = BuffBarRect(ui);

        return $"{found} of {Furniture.Length} controls resolved, {area:N0} px blocked" +
               (said.Count > 0 ? " - " + string.Join("; ", said) : "") +
               $"; buff bar {string.Join("->", BuffBar)} " +
               (buffs.Width > 0f ? $"estimated at ({buffs.X:0},{buffs.Y:0} {buffs.Width:0}x{buffs.Height:0})" : "not found or empty");
    }

    /// <summary>Whether a point is inside the estimated buff bar, for naming what blocked it. See BuffBarRect.</summary>
    private static bool InBuffBar(GameController gc, Vector2 at)
    {
        var rect = BuffBarRect(Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null));

        return at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom && rect.Width > 0f;
    }

    /// <summary>The buff bar at the top left, by path. See BuffBarRect.</summary>
    private static readonly int[] BuffBar = [17, 6, 0];

    /// <summary>
    /// The screen the buff bar covers, estimated, or empty when it is not found or shows no buff.
    ///
    /// **Estimated, because its icons report a position and no size.** A click over a buff is taken by the bar, so the
    /// cursor must not be sent there - and the rectangle tests let it through: on a Grazed Prairie site (2026-10-06) a
    /// spot at (203,50) read as clear and the hover check then refused it, over [17,6,0,3] reading (202,5 0x0). So the
    /// left and top are the least of the visible icons' positions, an icon's width is the commonest gap between
    /// neighbouring icons (about 95 px at 2560x1440; 96 when there is one icon), and the bottom is the lowest of their
    /// charge counts, which do report a size, or one icon below the top when none shows. Path [17,6,0] was the bar in two
    /// dumps that day.
    /// </summary>
    private static RectangleF BuffBarRect(Element ui)
    {
        var bar = ui == null ? null : Resolve(ui, BuffBar);
        var icons = bar == null ? null : Safe.Kids(bar);

        if (icons == null)
            return default;

        var xs = new List<float>();
        var top = float.MaxValue;
        var bottom = float.MinValue;

        foreach (var icon in icons)
        {
            if (icon == null || !Safe.Read(icon, static e => e.IsVisible, false))
                continue;

            var at = Safe.Read(icon, static e => e.GetClientRectCache, default(RectangleF));

            xs.Add(at.X);
            top = MathF.Min(top, at.Y);

            // The charge count sits two levels down, at [icon,1,0], and is the one part with a size.
            foreach (var part in Safe.Kids(icon) ?? [])
            foreach (var text in (part == null ? null : Safe.Kids(part)) ?? [])
            {
                var rect = text == null ? default : Safe.Read(text, static e => e.GetClientRectCache, default(RectangleF));

                if (rect.Height > 0f)
                    bottom = MathF.Max(bottom, rect.Bottom);
            }
        }

        if (xs.Count == 0)
            return default;

        xs.Sort();

        var gaps = new List<float>();

        for (var i = 1; i < xs.Count; i++)
        {
            if (xs[i] - xs[i - 1] >= 20f)
                gaps.Add(MathF.Round(xs[i] - xs[i - 1]));
        }

        var width = gaps.Count > 0 ? gaps.GroupBy(g => g).OrderByDescending(g => g.Count()).First().Key : 96f;

        if (bottom < top + width)
            bottom = top + width;

        return new RectangleF(xs[0], top, xs[^1] + width - xs[0], bottom - top);
    }

    private static Element Resolve(Element from, int[] path)
    {
        var at = from;

        foreach (var index in path)
        {
            // The count before the list: ChildCount says whether the index can exist without
            // reading any memory for the children themselves, so a path that runs out is refused
            // before the array behind it is ever read.
            if (Safe.Read(at, static e => e.ChildCount, 0) <= index || index < 0)
                return null;

            at = Safe.Kid(at, index);

            if (at == null)
                return null;
        }

        return at;
    }

    /// <summary>
    /// The last two parts of a metadata path, which is the part that names the thing.
    ///
    /// The whole path ran the status line off the right of the screen, and everything before the
    /// last slash or two is shared by every object of that kind anyway.
    /// </summary>
    private static string Tail(string metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return "(none)";

        var parts = metadata.Split('/');

        return parts.Length <= 2 ? metadata : string.Join("/", parts[^2..]);
    }

    /// <summary>
    /// What the game says is under the cursor, for the dump.
    ///
    /// Here because identifying the thing that refused a placement used to mean reading the whole
    /// interface dump and guessing which of a hundred elements it was. The path is what the rest of
    /// the dump prints, so it can be matched up by eye.
    /// </summary>
    /// <summary>
    /// The rectangle of whatever the game says is under the cursor, or nothing.
    ///
    /// **The only source that knows about everything.** Covered lists the six Expedition panels;
    /// Furniture lists the controls somebody sat down and sampled. Neither knows about a rare
    /// monster's health bar, which moves with the monster, has no fixed path, and is drawn straight
    /// across the top of a Runeshape Combinations button often enough to stop a run.
    ///
    /// UIHover is the client answering directly, and its limitation is that it only answers about
    /// where the cursor already IS - useless for choosing where to send it, exactly right for
    /// working out why the place it was sent did not work. See Placement's Opening step.
    ///
    /// The root element is excluded for the reason UnderCursor excludes it: UIHover names the
    /// outermost container over open ground, and the whole screen is not an obstruction.
    /// </summary>
    public static RectangleF Hovered(GameController gc) => Safe.Read(gc, static g =>
    {
        var hover = g.IngameState.UIHover;

        if (hover?.Address is null or 0)
            return default;

        if (hover.Parent == null)
            return default;

        var rect = hover.GetClientRectCache;

        return rect.Width > 0f && rect.Height > 0f ? rect : default;
    }, default(RectangleF));

    public static string Describe(GameController gc) => Safe.Read(gc, static g =>
    {
        var hover = g.IngameState.UIHover;

        if (hover == null || hover.Address == 0)
            return "nothing (UIHover reads no address)";

        if (hover.Parent == null)
            return "the world (the outermost element, which is what open ground reports)";

        var rect = hover.GetClientRectCache;
        var text = hover.Text ?? "";

        var (allowed, why) = Allowed(g, hover);

        return $"[{Path(hover)}] ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) " +
               $"visible={hover.IsVisible} lit={Interactive(hover)} ({why})" +
               (text.Length > 0 ? $" \"{text}\"" : "") +
               (allowed ? "  -> clicks go through it" : "  -> blocks a placement");
    }, "unreadable");

    public static bool Covers(List<RectangleF> covered, Vector2 at)
    {
        foreach (var rect in covered)
        {
            if (at.X >= rect.Left && at.X <= rect.Right && at.Y >= rect.Top && at.Y <= rect.Bottom)
                return true;
        }

        return false;
    }

    private static bool Open(Element element) =>
        element != null && Safe.Read(element, static e => e.IsVisible, false);

    /// <summary>
    /// Whether the chat is actually open, which its message lines will not say.
    ///
    /// **The one element in that panel whose visibility is honest.** A chat line stays IsVisible
    /// for as long as the session lasts - three from login were still reporting themselves painted
    /// hours later, over a dig site - so nothing about a message tells you whether anybody can see
    /// it. The title panel tracks the real state: false while shut, true once enter opens it.
    ///
    /// Found once and kept, because it is one element in a panel that does not move. Re-found if
    /// the address goes, which happens on a zone change.
    /// </summary>
    private static bool Chatting(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.ChatTitlePanel.IsVisible, false);

    /// <summary>
    /// Whether an element sits inside the chat panel.
    ///
    /// **The parent chain by address, because names are expensive here.** PathFromRoot is the
    /// readable way to ask which panel something belongs to, and reading it makes ExileCore2 write
    /// a line to the log every time. Addresses answer the same question for nothing.
    ///
    /// Bounded, because a parent chain that loops would otherwise hang the frame, and nothing on
    /// this interface is twenty levels from a panel it belongs to.
    /// </summary>
    /// <summary>
    /// An element's path from the root, read once per element and then remembered.
    ///
    /// **For the words a person reads, and nowhere else.** PathFromRoot is the only thing that says
    /// what an element actually is, which is why the two explanations here use it - and it is also
    /// what writes "Element with index: 0 not found" to the log on every read. An element's path
    /// does not change, so reading it once per address costs one line per element ever rather than
    /// one per frame, and the decisions that run every frame do not ask for it at all.
    /// </summary>
    public static string Path(Element at)
    {
        var address = Safe.Read(at, static e => (long)e.Address, 0L);

        if (address == 0L)
            return "?";

        if (_paths.TryGetValue(address, out var was))
            return was;

        var path = Safe.Read(at, static e => e.PathFromRoot, "?") ?? "?";

        if (_paths.Count > 4096)
            _paths.Clear();

        _paths[address] = path;

        return path;
    }

    private static readonly Dictionary<long, string> _paths = new();

    private static bool InChat(GameController gc, Element hover)
    {
        var want = Safe.Read(gc, static g => (long)g.IngameState.IngameUi.ChatPanel.Address, 0L);

        if (want == 0L || hover == null)
            return false;

        for (var at = hover; at != null; at = Safe.Read(at, static e => e.Parent, null))
        {
            if (Safe.Read(at, static e => (long)e.Address, 0L) == want)
                return true;
        }

        return false;
    }
}
