using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// The ground labels, read in one place.
///
/// **Twelve call sites asked the game for this list independently, several of them per frame and
/// one of them inside a loop.** It is not a cheap property - ExileCore2 rebuilds the list from
/// memory each time - and it is the read that writes "Element with index: 0 not found. Indices:
/// [1] [8] [0] [1] [0]" into the log, because the second label container this client leaves empty
/// sits on that path. So how often it is asked is worth knowing, and worth being able to change in
/// one place rather than twelve.
/// </summary>
internal static class Ground
{
    public static IList<LabelOnGround> Labels(GameController gc) =>
        Safe.Read(() => gc.IngameState.IngameUi.ItemsOnGroundLabels, null);
}
