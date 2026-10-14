using System;
using System.Collections.Generic;
using Color = System.Drawing.Color;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// Five labelled zones drawn along the bottom of the open stash's item area, each starting a tablet run when the action
/// key is pressed over it: craft, reforge, craft and reforge, and withdraw over deposit. Each says what is waiting for it. Outside them the key
/// prices the tablet under the cursor as before. Drawn only with Enable automation and Enable tablet automation both
/// on. See TabletRerollingSettings.EnableTabletAutomation.
///
/// Hover and the key rather than a click, because a click on the overlay goes through to the stash under it.
/// </summary>
internal static partial class Tablets
{
    private enum ActionZone
    {
        Reforge,
        Craft,
        CraftAndReforge,
        Withdraw,
        Deposit,
    }

    /// <summary>What the zones say is waiting, worked out once a read. See CountForZones.</summary>
    private sealed record ZoneCounts(int JunkInStash, int JunkCarried, int Unidentified, int Craftable);

    private static ZoneCounts _zoneCounts = new(0, 0, 0, 0);

    /// <summary>When the zones' counts were last worked out. See CountForZones.</summary>
    private static DateTime _zonesCountedAt;

    /// <summary>How often the zones' counts are worked out: they are only read.</summary>
    private static readonly TimeSpan ZonesCountEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether the open stash tab is the fragment tab, as at the last read: the zones show only there. See
    /// StashTabWithIcon.
    /// </summary>
    private static bool _onFragmentTab;

    /// <summary>
    /// The colour the player gave the fragment tab, as the server keeps it, or null when it has none. See
    /// NoteFragmentTab and ZoneOutline.
    /// </summary>
    private static Color? _fragmentTabColour;

    /// <summary>
    /// Notes whether the fragment tab is the one open, and the colour it was given: the server's entry at the open tab's
    /// visible index with the same name. Several entries share an index - a dump of 2026-10-09 23:14 listed seven of
    /// type Fragment at index 21, one of them named as the tab - so the name decides. Called once a read.
    ///
    /// ServerStashTab.Color is read as 0xAARRGGBB: 0xFF638000, a green, on the tab the player had coloured green; Color2
    /// held the same bytes the other way round, #008063. It is the player's choice, not a default: the zones' outline
    /// changed with the tab's colour when the player saved a new one (2026-10-09).
    /// </summary>
    private static void NoteFragmentTab(GameController gc)
    {
        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);
        var visible = Safe.Read(stash, static s => s.IndexVisibleStash, -1);

        _onFragmentTab = _stashRect.Width > 0f && visible >= 0 && visible == StashTabWithIcon(gc, FragmentTabIcon);

        if (!_onFragmentTab)
            return;

        var names = Safe.Read(stash, static s => s.AllStashNames, null);
        var name = names != null && visible < names.Count ? names[visible] : null;
        var tab = (Safe.Read(gc, static g => g.IngameState.ServerData.PlayerStashTabs, null) ?? [])
            .FirstOrDefault(t => Safe.Read(t, static x => (int)x.VisibleIndex, -1) == visible && Safe.Read(t, static x => x.Name, null) == name);
        var raw = Safe.Read(tab, static t => t.Color, 0u);

        _fragmentTabColour = raw == 0u ? null : Color.FromArgb(255, (int)(raw >> 16) & 0xFF, (int)(raw >> 8) & 0xFF, (int)raw & 0xFF);
    }

    /// <summary>Whether this run is a craft and reforge run, going between the two until a pass of both does nothing.</summary>
    private static bool _craftAndReforge;

    /// <summary>Passes of crafting then reforging in a craft and reforge run.</summary>
    private static int _combinedPasses;

    /// <summary>Currency used and tablets reforged over a craft and reforge run's passes.</summary>
    private static int _combinedCrafted, _combinedReforged;

    /// <summary>Currency used by the crafting half of the pass going. See FinishRun.</summary>
    private static int _passCrafted;

    /// <summary>A zone's background, and the one under the cursor or running. Chosen by eye, against LabelBackground's 190.</summary>
    private static readonly Color ZoneBackground = Color.FromArgb(90, 0, 0, 0);

    private static readonly Color ZoneBackgroundLit = Color.FromArgb(130, 0, 0, 0);

    /// <summary>
    /// A zone's two-pixel outline, outer and inner pixel, in the fragment tab's own colour as the game draws its outline:
    /// the tab's colour times OutlineOuterShare and OutlineInnerShare. The shares are from one tab - colour #638000, its
    /// outline read off the screen by the player as #324400 outside and #5E7B00 inside (2026-10-09) - so they are near,
    /// not exact. Lit, under the cursor or running, each half as bright again, chosen. A tab with no colour gets grey.
    /// </summary>
    private static (Color Outer, Color Inner) ZoneOutline(bool lit)
    {
        var colour = _fragmentTabColour ?? Color.FromArgb(255, 120, 120, 120);
        var brighter = lit ? 1.5f : 1f;

        return (Shaded(colour, OutlineOuterShare * brighter), Shaded(colour, OutlineInnerShare * brighter));
    }

    /// <summary>The outline's outer pixel as a share of the tab's colour: 0x32/0x63 and 0x44/0x80, about 0.51 and 0.53.</summary>
    private const float OutlineOuterShare = 0.52f;

    /// <summary>The outline's inner pixel as a share of the tab's colour: 0x5E/0x63 and 0x7B/0x80, about 0.95 and 0.96.</summary>
    private const float OutlineInnerShare = 0.95f;

    /// <summary>A colour with each channel times a share, kept within 0 to 255.</summary>
    private static Color Shaded(Color colour, float share) =>
        Color.FromArgb(255, Math.Clamp((int)Math.Round(colour.R * share), 0, 255), Math.Clamp((int)Math.Round(colour.G * share), 0, 255),
            Math.Clamp((int)Math.Round(colour.B * share), 0, 255));

    /// <summary>
    /// Inventory cells holding anything when the craft and reforge run started: what the put-away at its end leaves. See
    /// PutAway.
    /// </summary>
    private static readonly HashSet<(int X, int Y)> CombinedCellsAtStart = [];

    /// <summary>The most passes a craft and reforge run makes, however much each does.</summary>
    private const int MostCombinedPasses = 20;

    /// <summary>
    /// The zones' rectangles, left to right, under the stash's item area, or none when the stash is closed, the zones are
    /// off or their text has not been measured yet. The area is the stash's StashInventoryPanel: 7,119 873x1030 in a dump
    /// of 2026-10-09 22:46, with nothing under the cursor at 584,1061 but the panel itself.
    ///
    /// Each zone is sized from its text, which the HUD draws the same size at every resolution, and the row is centred
    /// where it was at 1440. Sized from the stash instead, the zones shrank below 1440 around text that did not, and the
    /// text ran past their edges. The gap between zones is a fixed 8 pixels. Craft and Reforge are left out with Hide
    /// individual Craft/Reforge zones on, or when all five would be wider than the item area.
    /// </summary>
    private static List<(ActionZone Zone, RectangleF Rect)> ActionZones(GameController gc, TabletRerollingSettings settings)
    {
        if (!_automationOn || !_tabletAutomation || _stashRect.Width <= 0f || !_onFragmentTab || _zoneTextSize is not { } text)
            return [];

        var panel = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement.StashInventoryPanel, null);
        var area = Safe.Read(panel, static p => p.GetClientRectCache, default);

        if (area.Width <= 0f)
            area = _stashRect;

        const float gap = 8f;
        var padding = text.LineHeight / 2f;
        // Four lines - the header, the title and two counts - and the half-height Withdraw and Deposit two each.
        var halfHeight = 2f * text.LineHeight + 2f + padding;
        var height = Math.Max(4f * text.LineHeight + 2f + 2f * padding, 2f * halfHeight + gap);
        var individual = text.CraftOrReforgeWidth + 2f * padding;
        var combined = text.CraftAndReforgeWidth + 2f * padding;
        var stacked = text.WithdrawOrDepositWidth + 2f * padding;
        var allFive = 2f * individual + combined + stacked + 3f * gap;
        var hideIndividual = settings.TabletAutomation.HideIndividualCraftReforgeZones.Value || allFive > area.Width;
        var total = hideIndividual ? combined + stacked + gap : allFive;

        // The offset sliders are in pixels of the item area at 1440, drawn in pixels of this one: its width over its 873
        // there.
        var unit = area.Width / ItemAreaWidthAt1440;
        var centreX = area.Left + (RowCentreRightOfArea + settings.TabletAutomation.ActionZonesXOffset.Value) * unit;
        var centreY = area.Bottom + (RowCentreBelowArea + settings.TabletAutomation.ActionZonesYOffset.Value) * unit;
        var left = centreX - total / 2f;
        var top = centreY - height / 2f;
        var zones = new List<(ActionZone Zone, RectangleF Rect)>();

        if (!hideIndividual)
        {
            zones.Add((ActionZone.Craft, new RectangleF(left, top, individual, height)));
            zones.Add((ActionZone.Reforge, new RectangleF(left + individual + gap, top, individual, height)));
            left += 2f * (individual + gap);
        }

        zones.Add((ActionZone.CraftAndReforge, new RectangleF(left, top, combined, height)));
        // Withdraw over Deposit, each half the height, the gap between them.
        zones.Add((ActionZone.Withdraw, new RectangleF(left + combined + gap, top, stacked, (height - gap) / 2f)));
        zones.Add((ActionZone.Deposit, new RectangleF(left + combined + gap, top + (height + gap) / 2f, stacked, (height - gap) / 2f)));

        return zones;
    }

    /// <summary>
    /// The HUD font's line height and the widest line of each kind of zone, measured on each draw for ActionZones, which
    /// runs where no Graphics is to hand. Null before the first draw.
    /// </summary>
    private static ZoneTextSize? _zoneTextSize;

    private readonly record struct ZoneTextSize(float LineHeight, float CraftOrReforgeWidth, float CraftAndReforgeWidth,
        float WithdrawOrDepositWidth);

    /// <summary>
    /// Measures every line the zones can show, the counts at three digits, so a zone keeps its width as counts and the
    /// header change.
    /// </summary>
    private static ZoneTextSize MeasureZoneText(Graphics graphics)
    {
        string[] headers = [$"Hover and {_actionKeyName}:", $"{_actionKeyName} to stop"];
        var wide = new ZoneCounts(888, 888, 888, 888);

        float Widest(params ActionZone[] zones) =>
            zones.SelectMany(zone => ZoneText(zone, wide) is var (title, lines) ? [.. headers, title, .. lines] : headers)
                .Max(line => graphics.MeasureText(line).X);

        return new ZoneTextSize(graphics.MeasureText("Hg").Y + 2f, Widest(ActionZone.Craft, ActionZone.Reforge),
            Widest(ActionZone.CraftAndReforge), Widest(ActionZone.Withdraw, ActionZone.Deposit));
    }

    /// <summary>A zone's title and the grey lines under it.</summary>
    private static (string Title, string[] Lines) ZoneText(ActionZone zone, ZoneCounts counts) => zone switch
    {
        ActionZone.Reforge => ("Reforge Tablets", [$"{counts.JunkInStash} in stash", $"{counts.JunkCarried} in inventory"]),
        ActionZone.Craft => ("Craft Tablets", [$"{counts.Unidentified} unidentified", $"{counts.Craftable} craftable"]),
        ActionZone.Withdraw => ("Withdraw", []),
        ActionZone.Deposit => ("Deposit", []),
        _ => ("Craft and Reforge", ["Full auto"]),
    };

    /// <summary>
    /// The centre of the zones' row from the item area's bottom left, in pixels at 1440: where the row's centre was when
    /// it was 802x100, 12 in from the area's left past ZonesInsetFromArea, its bottom ZonesDropBelowArea - 6 below the
    /// area's bottom. Those were the zones' size and place set in game (2026-10-09) before they were sized from their text.
    /// </summary>
    private const float RowCentreRightOfArea = 12f + ZonesInsetFromArea + 802f / 2f;

    /// <inheritdoc cref="RowCentreRightOfArea"/>
    private const float RowCentreBelowArea = ZonesDropBelowArea - 6f - 100f / 2f;

    /// <summary>
    /// How far right of the item area's left edge, past a margin of 12, and how far below its bottom the zones' row sat
    /// before the offset sliders: 13 and 125, where the player set the sliders in game (2026-10-09). Built in so the sliders
    /// read 0 there; Migrated step 31 took them off saved offsets. See RowCentreRightOfArea.
    /// </summary>
    internal const int ZonesInsetFromArea = 13;

    /// <summary>The stash item area's width at 2560x1440, which the offset sliders' pixels are measured against.</summary>
    private const float ItemAreaWidthAt1440 = 873f;

    /// <inheritdoc cref="ZonesInsetFromArea"/>
    internal const int ZonesDropBelowArea = 125;

    /// <summary>The zone under the cursor, or null.</summary>
    private static ActionZone? ZoneUnderCursor(GameController gc, TabletRerollingSettings settings)
    {
        var mouse = ImGuiNET.ImGui.GetMousePos();

        foreach (var (zone, rect) in ActionZones(gc, settings))
        {
            if (mouse.X >= rect.Left && mouse.X <= rect.Right && mouse.Y >= rect.Top && mouse.Y <= rect.Bottom)
                return zone;
        }

        return null;
    }

    /// <summary>Starts the run a zone stands for, or says why it cannot.</summary>
    private static void StartZone(GameController gc, TabletRerollingSettings settings, ActionZone zone)
    {
        switch (zone)
        {
            case ActionZone.Reforge:
                ToggleReforging(gc, settings, TabletRun.FullAuto);
                return;

            case ActionZone.Craft:
                ToggleReforging(gc, settings, TabletRun.Craft);
                return;

            case ActionZone.Deposit:
                ToggleReforging(gc, settings, TabletRun.Deposit);
                return;

            case ActionZone.Withdraw:
                ToggleReforging(gc, settings, TabletRun.WithdrawValuable);

                if (_reforgingHasCursor)
                    StartWithdrawValuable();

                return;

            case ActionZone.CraftAndReforge:
                ToggleReforging(gc, settings, TabletRun.Craft);

                if (_reforgingHasCursor)
                {
                    _craftAndReforge = true;
                    _combinedPasses = 1;
                    _combinedCrafted = 0;
                    _combinedReforged = 0;
                    CombinedCellsAtStart.Clear();

                    foreach (var cell in InventoryCellsTaken(gc))
                        CombinedCellsAtStart.Add(cell);
                }

                return;
        }
    }

    /// <summary>
    /// A run that has done all it can: stopped, saying why - or, in a craft and reforge run, on to the other half. After
    /// crafting comes reforging, from the stash as crafting left it; after reforging, crafting again, which identifies
    /// the reforged tablets first. The run ends after a pass in which neither did anything, or after MostCombinedPasses.
    /// </summary>
    private static void FinishRun(GameController gc, string said)
    {
        if (!_craftAndReforge)
        {
            // A crafting run that used up its work ends quietly; a reforging one says what junk it left, and why.
            if (_run == TabletRun.Craft)
                FinishQuietly(said);
            else
                StopReforging(said);

            return;
        }

        if (_run == TabletRun.Craft)
        {
            _passCrafted = _crafted;
            _combinedCrafted += _crafted;

            _run = TabletRun.FullAuto;
            _phase = FullAutoPhase.Navigate;
            _navigateStep = 0;
            // The tablets crafting finished go back before any junk comes out: left in, they took the room the junk
            // would have (2026-10-10). See Depositable.
            _depositNext = true;
            _stashEmptied = false;
            _roomForBench = 0;
            _rounds = 0;
            _reforged = 0;
            _moved = 0;
            SubTabsDone.Clear();
            ReforgingSent.Clear();
            MovesPending.Clear();
            MovesRetried.Clear();
            BenchLoadsPending.Clear();
            BenchLoadsRetried.Clear();
            Unloadable.Clear();
            RollsBegin(gc);
            Progressed();
            Say("reforging");
            return;
        }

        _combinedReforged += _reforged;

        // Done: the tablets tidied to the front before the run ends, when asked. See TabletTidy.
        if (_reforged == 0 && _passCrafted == 0 || _combinedPasses >= MostCombinedPasses)
        {
            var finished = $"done: {_combinedCrafted} currency used, {_combinedReforged} reforged" +
                          (_combinedPasses >= MostCombinedPasses ? $", stopped at the most passes a run makes" : "");

            // The tablets and currency kept in the inventory between passes put away first, then the tidy when asked.
            StartTidy(gc, finished, _tidyAfterCraftAndReforge);
            return;
        }

        _combinedPasses++;
        _run = TabletRun.Craft;
        _moved = 0;
        ReforgingSent.Clear();
        MovesPending.Clear();
        MovesRetried.Clear();
        StartCrafting(gc);
        Progressed();
        Say("crafting");
    }

    /// <summary>
    /// What each zone says is waiting: junk in the fragment tab's Expedition sub-tabs and carried, apart, and
    /// unidentified and craftable tablets in both together. The sub-tabs' figures are the sub-tab records, as each
    /// sub-tab was when last seen. Called once a read. See TabletSubTabs.
    /// </summary>
    private static void CountForZones(GameController gc, TabletRerollingSettings settings)
    {
        // **At most once a second, and from the drawn inventory when it is open.** The game's own record of the inventory
        // makes new tablet records each time, which the once-a-read judgements do not reach: every carried tablet's junk
        // check built its trade search again, about 39ms a read and 50MB with an inventory full of tablets, during a
        // crafting run (dump 2026-10-09 23:40). The drawn inventory's tablets are the read's own records.
        if (DateTime.UtcNow - _zonesCountedAt < ZonesCountEvery)
            return;

        _zonesCountedAt = DateTime.UtcNow;

        var records = Book.SubTabs.Values.ToList();
        var carried = Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false)
            ? InventoryTablets()
            : CarriedTablets(gc);

        _zoneCounts = new ZoneCounts(
            records.Sum(r => r.Junk),
            carried.Count(t => IsJunk(gc, t, settings)),
            records.Sum(r => r.Unidentified ?? 0) + carried.Count(t => !t.Identified),
            records.Sum(r => r.Craftable) +
            carried.Count(t => t.Uses == FullUses && (t.Rarity == ItemRarity.Normal || t.Identified) &&
                               CraftingActions.Contains(ActionOf(t, settings))));
    }

    /// <summary>Draws the zones, the one under the cursor lit, each with what is waiting for it.</summary>
    private static void DrawActionZones(Graphics graphics, GameController gc, TabletRerollingSettings settings)
    {
        _zoneTextSize = MeasureZoneText(graphics);

        var zones = ActionZones(gc, settings);

        if (zones.Count == 0)
            return;

        var under = _reforgingHasCursor ? null : ZoneUnderCursor(gc, settings);
        var height = _zoneTextSize.Value.LineHeight;

        foreach (var (zone, rect) in zones)
        {
            var (title, lines) = ZoneText(zone, _zoneCounts);

            var running = _reforgingHasCursor && zone switch
            {
                ActionZone.Reforge => !_craftAndReforge && _run == TabletRun.FullAuto,
                ActionZone.Craft => !_craftAndReforge && _run == TabletRun.Craft,
                ActionZone.Withdraw => _run == TabletRun.WithdrawValuable,
                ActionZone.Deposit => _run == TabletRun.Deposit,
                _ => _craftAndReforge,
            };

            var lit = zone == under || running;
            var text = Color.White;

            // Lighter than a label's background, so the zones read as areas to hover rather than buttons, outlined as the
            // fragment tab is; the one lit only a little stronger.
            graphics.DrawBox(new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom),
                lit ? ZoneBackgroundLit : ZoneBackground);
            var (outer, inner) = ZoneOutline(lit);

            DrawInsetBorder(graphics, rect, 1, outer);
            DrawInsetBorder(graphics, new RectangleF(rect.Left + 1f, rect.Top + 1f, rect.Width - 2f, rect.Height - 2f), 1, inner);

            var header = running ? $"{_actionKeyName} to stop" : $"Hover and {_actionKeyName}:";
            // Placed as a four-line block centred top to bottom, whatever this zone's own count, so the zones' lines
            // start level. Never above the top.
            // The half-height Withdraw and Deposit have their two lines alone, centred in themselves.
            var block = height * (lines.Length == 0 ? 2 : 4) + 2f;
            var y = rect.Top + Math.Max(2f, (rect.Height - block) / 2f);

            foreach (var (words, colour) in new[] { (header, Dim), (title, text) })
            {
                var size = graphics.MeasureText(words);

                graphics.DrawText(words, new Vector2(rect.Left + (rect.Width - size.X) / 2f, y), colour);
                y += height;
            }

            y += 2f;

            foreach (var line in lines)
            {
                if (y + height > rect.Bottom)
                    break;

                var lineSize = graphics.MeasureText(line);

                graphics.DrawText(line, new Vector2(rect.Left + (rect.Width - lineSize.X) / 2f, y), Dim);
                y += height;
            }
        }
    }
}
