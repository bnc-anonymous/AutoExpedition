using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace AutoExpedition;

/// <summary>
/// The crafting run's first step: every unidentified Expedition Tablet in the fragment tab's sub-tabs identified, so the
/// currencies after it see what each one needs. Unidentified tablets are ctrl+clicked out of the stash, the stash is
/// closed, Doryani is ctrl+clicked, the stash is opened again and the tablets that came out are ctrl+clicked back in.
/// If the inventory fills before the stash is gone through, the step goes round again for the rest.
///
/// Only cells empty when the run started are deposited, so tablets the player was carrying stay in the inventory. They
/// are identified with the rest, as Doryani identifies the whole inventory.
/// </summary>
internal static partial class Tablets
{
    /// <summary>Inventory cells holding anything when the crafting run started. See IdentifyDeposit.</summary>
    private static readonly HashSet<(int X, int Y)> InventoryCellsAtStart = [];

    /// <summary>Whether the last withdraw of unidentified tablets stopped on a full inventory, with sub-tabs left.</summary>
    private static bool _identifyStoppedFull;

    /// <summary>What moving an unidentified tablet out of the stash is called, in the status line.</summary>
    private const string UnidentifiedTablet = "an unidentified tablet";

    /// <summary>Whether the crafting run is in its identification step.</summary>
    private static bool CraftIdentifying => _craftPhase is CraftPhase.IdentifyNavigate or CraftPhase.IdentifyWithdraw or
        CraftPhase.IdentifyClose or CraftPhase.IdentifyDoryani or CraftPhase.IdentifyOpenStash or CraftPhase.IdentifyDepositNavigate or
        CraftPhase.IdentifyDeposit;

    /// <summary>Begins the identification step. Called as a crafting run starts.</summary>
    private static void StartCraftIdentifying(GameController gc)
    {
        InventoryCellsAtStart.Clear();

        foreach (var cell in InventoryCellsTaken(gc))
            InventoryCellsAtStart.Add(cell);

        _identifyStoppedFull = false;
        BeginCraftIdentifyRound();
    }

    /// <summary>One round of withdraw, Doryani and deposit, from the fragment tab.</summary>
    private static void BeginCraftIdentifyRound()
    {
        _craftPhase = CraftPhase.IdentifyNavigate;
        _navigateStep = 0;
        SubTabsDone.Clear();
        ReforgingSent.Clear();
        _moved = 0;
    }

    /// <summary>The cells of every item in the inventory, from the game's own record of it.</summary>
    private static List<(int X, int Y)> InventoryCellsTaken(GameController gc)
    {
        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);

        return (Safe.Read(main, static m => m.Inventory.InventorySlotItems, null) ?? [])
            .Select(s => (Safe.Read(s, static x => x.PosX, -1), Safe.Read(s, static x => x.PosY, -1)))
            .ToList();
    }

    /// <summary>
    /// The inventory's tablets in cells that were empty when the run started: those the identification step took out of
    /// the stash. Matched to their cells by the slot's own screen rectangle. See CentreWithin.
    /// </summary>
    private static List<TabletOnScreen> TabletsBroughtOut(GameController gc)
    {
        var slots = CarriedByCell(gc);

        return InventoryTablets()
            .Where(t => slots.FirstOrDefault(s => CentreWithin(t.Rect, s.Rect)) is { Rect.Width: > 0f } slot &&
                        !InventoryCellsAtStart.Contains(slot.Cell))
            .ToList();
    }

    /// <summary>One step of the identification, chosen by phase from what the game shows now.</summary>
    private static void StepCraftIdentifying(GameController gc, TabletRerollingSettings settings)
    {
        switch (_craftPhase)
        {
            case CraftPhase.IdentifyNavigate:
            case CraftPhase.IdentifyDepositNavigate:
                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                NavigateNext(gc);
                return;

            case CraftPhase.IdentifyWithdraw:
            {
                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                // A used one stays where it is, as in every tablet run.
                var unidentified = StashTablets().Where(t => !t.Identified && t.Uses == FullUses).ToList();

                // A full inventory: identify what came out, then come back for the rest.
                if (InventoryFreeCells(gc) == 0 && unidentified.Count > 0)
                {
                    _identifyStoppedFull = true;
                    _craftPhase = CraftPhase.IdentifyClose;
                    _spaceTries = 0;
                    return;
                }

                if (MoveNext(gc, unidentified, UnidentifiedTablet, true))
                    return;

                if (NextSubTab(gc, SubTabPurpose.Unidentified) is { } next)
                {
                    ClickSubTab(gc, next);
                    return;
                }

                _identifyStoppedFull = false;

                // Nothing to identify, carried or brought out: on to the currencies.
                if (UnidentifiedTabletsCarried(gc) == 0)
                {
                    _craftPhase = CraftPhase.Supply;
                    return;
                }

                _craftPhase = CraftPhase.IdentifyClose;
                _spaceTries = 0;
                return;
            }

            case CraftPhase.IdentifyClose:
                if (PanelsClosedFor(gc, DoryaniPath))
                    _craftPhase = CraftPhase.IdentifyDoryani;

                return;

            case CraftPhase.IdentifyDoryani:
                if (UnidentifiedTabletsCarried(gc) == 0)
                {
                    if (_reforgingDoing == "Doryani")
                        Progressed();

                    _craftPhase = CraftPhase.IdentifyOpenStash;
                    return;
                }

                SendWorld(gc, DoryaniPath, true, "Doryani", () => UnidentifiedTabletsCarried(gc) == 0);
                return;

            case CraftPhase.IdentifyOpenStash:
                if (_stashRect.Width > 0f)
                {
                    _craftPhase = CraftPhase.IdentifyDepositNavigate;
                    _navigateStep = 0;
                    SubTabsDone.Clear();
                    ReforgingSent.Clear();
                    _moved = 0;
                    return;
                }

                SendWorld(gc, StashPath, false, "the stash", () => _stashRect.Width > 0f);
                return;

            case CraftPhase.IdentifyDeposit:
            {
                if (_stashRect.Width <= 0f)
                {
                    StopReforging("the stash closed");
                    return;
                }

                var brought = TabletsBroughtOut(gc);

                // A full sub-tab showing: to one with room first, rather than waiting out a tablet that cannot go in.
                if (StashTablets().Count >= SubTabHolds && brought.Count > 0 && NextSubTab(gc, SubTabPurpose.Room) is { } room)
                {
                    ClickSubTab(gc, room);
                    return;
                }

                if (MoveNext(gc, brought, DepositedTablet, false))
                    return;

                if (_identifyStoppedFull)
                {
                    BeginCraftIdentifyRound();
                    return;
                }

                _craftPhase = CraftPhase.Supply;
                return;
            }
        }
    }
}
