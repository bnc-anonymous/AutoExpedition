namespace AutoExpedition;

/// <summary>
/// How often the handful of primitives everything is built out of get called.
///
/// **Timing a stage says where the frame goes; it does not say what is doing it.** Four dumps were
/// spent narrowing a thirty millisecond sweep to a stage, then to a branch of that stage, then to a
/// helper inside the branch - each round a guess at which leaf was hot, confirmed or refused by the
/// next dump. The answer was a helper being asked five times for one answer, and a count of calls
/// would have said so in the first dump: five scans an entity against seven hundred entities is a
/// number nothing else in the plugin comes near.
///
/// So the leaves count themselves. There are only a few of them and everything else is made of
/// them: a read out of the game, a query against the weight reference table, a component fetched
/// off an entity, a state value. A per-frame figure for each is a shape that can be recognised
/// without knowing what ran - a leaf called thousands of times a frame is being called in a loop
/// nobody meant to write, whatever the loop turns out to be.
///
/// An increment on a static field, deliberately not interlocked. These are read to spot an order of
/// magnitude, and a count that is off by a handful across threads costs nothing; making them exact
/// would put a lock on the hottest paths in the plugin to improve a diagnostic's last digit.
/// </summary>
internal static class LeafCalls
{
    /// <summary>Reads out of the game's memory, through Safe.Read.</summary>
    public static int GameReads;

    /// <summary>
    /// How many of those reads threw and were swallowed.
    ///
    /// **A caught exception is not a free branch.** Throwing builds an exception object and captures
    /// a stack trace, which is thousands of bytes - so a read that fails every frame costs orders of
    /// magnitude more than one that succeeds, while looking identical from the call site. Safe.Read
    /// catches silently and on purpose, which means nothing anywhere said how often it happens.
    ///
    /// Read against GameReads: a small ratio is ordinary streaming, and a large one means a path
    /// being walked into memory that is not there, every frame.
    /// </summary>
    public static int GameReadThrows;

    /// <summary>Identifier matches against the weight reference table. See TableGrammar.Matched.</summary>
    public static int TableQueries;

    /// <summary>Components fetched off an entity to name it. See Scan.Art.</summary>
    public static int ComponentReads;

    /// <summary>State values read off an object. See Target.State.</summary>
    public static int StateReads;

    /// <summary>The same counts over the last closed window, which is what the dump prints.</summary>
    public static (int GameReads, int GameReadThrows, int TableQueries, int ComponentReads, int StateReads) Over
    {
        get;
        private set;
    }

    /// <summary>
    /// Rolls the counts over, called by Spent when its window closes so the two tables cover the
    /// same frames and can be read against each other.
    /// </summary>
    /// <summary>Starts the counts again and drops the last window. See Spent.Forget.</summary>
    public static void Forget()
    {
        Rolled();
        Over = (0, 0, 0, 0, 0);
    }

    public static void Rolled()
    {
        Over = (GameReads, GameReadThrows, TableQueries, ComponentReads, StateReads);
        GameReads = 0;
        GameReadThrows = 0;
        TableQueries = 0;
        ComponentReads = 0;
        StateReads = 0;
    }
}
