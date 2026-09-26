namespace AutoExpedition;

/// <summary>
/// Which frame it is, so a read can be done once and reused.
///
/// Everything this plugin asks the game is a memory read, and several of the questions get asked a
/// dozen times a frame by different parts of the drawing - where the detonator is, which entity is
/// the placement indicator, whether a marker is lit. None of those answers can change between two
/// draws in the same frame, so they are worth caching for exactly one frame and no longer.
///
/// A counter rather than ExileCore2's FrameCache because the callers are static helpers: a
/// FrameCache is an instance holding a delegate, which would mean making them all instances and
/// threading them through everything that draws.
/// </summary>
internal static class Frame
{
    public static int Number { get; private set; }

    /// <summary>Called once per tick by the plugin. Everything cached per frame expires here.</summary>
    public static void Next() => Number++;
}
