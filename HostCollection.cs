using System;

namespace AutoExpedition;

/// <summary>
/// Which garbage collector the host runs with, which this plugin can ask for and cannot set.
///
/// **The collector is chosen when a process starts, before any plugin is loaded.** So nothing here
/// changes the process it runs in: it writes the variable the runtime reads at startup into the
/// account's environment, and the choice takes effect the next time ExileCore2 is started - the .NET
/// process here is the host, not the game. A setting
/// that appears to do nothing is worse than none, which is why the button beside this says so and
/// why the current mode is printed next to it.
///
/// **Why it is worth a button at all.** Workstation collection suspends every thread for a gen0 and
/// the search allocates from several at once, so its garbage lands as frame spikes rather than as
/// steady cost; server collection gives each core its own heap and a far larger budget before it
/// stops anybody. Measured against the allocation work rather than instead of it - the two together
/// are what moved the frame times, see NOTES.
///
/// **It is the account's environment, not this plugin's.** Every .NET program started afterwards
/// reads it, so Restore exists and the tooltip says as much before anybody clicks.
/// </summary>
internal static class HostCollection
{
    /// <summary>The variable the .NET runtime reads at startup to pick the collector.</summary>
    private const string Variable = "DOTNET_gcServer";

    /// <summary>Asks for server collection from the next start onwards.</summary>
    public static void UseServer() => Write("1");

    /// <summary>Takes the request back, leaving the runtime to choose as it did before.</summary>
    public static void Restore() => Write(null);

    /// <summary>Whether the request is in place for this account, whatever this process is using.</summary>
    public static bool Asked =>
        Safe.Read(() => Environment.GetEnvironmentVariable(Variable,
            EnvironmentVariableTarget.User), null) == "1";

    /// <summary>Whether anything is written at all, which is what decides if Restore has work.</summary>
    public static bool Written =>
        Safe.Read(() => Environment.GetEnvironmentVariable(Variable,
            EnvironmentVariableTarget.User), null) != null;

    /// <summary>
    /// Through Safe.Try, because writing an account's environment can fail for reasons that have
    /// nothing to do with this plugin - a policy, a locked registry hive - and a menu that throws
    /// out of its own draw call takes the whole settings page down with it.
    /// </summary>
    private static void Write(string value) =>
        Safe.Try(() => Environment.SetEnvironmentVariable(Variable, value,
            EnvironmentVariableTarget.User));
}
