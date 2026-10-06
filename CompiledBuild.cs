using System.Diagnostics;
using System.Reflection;

namespace AutoExpedition;

/// <summary>
/// How the running copy of the plugin was compiled.
///
/// **The HUD compiles the plugin itself, through MSBuild, and did so unoptimised.** It names no configuration, so
/// the project got Debug's defaults and the DLL in Plugins/Temp carried DebuggingModes 0x107, the JIT optimiser
/// switched off. The project now sets Optimize regardless; this reads what the loaded assembly actually says, so a
/// change in how the HUD builds shows up here rather than as a solver that has quietly become several times slower.
/// </summary>
internal static class CompiledBuild
{
    /// <summary>Whether the JIT optimiser is on for this assembly, read from its DebuggableAttribute.</summary>
    public static bool Optimised { get; } =
        !(typeof(CompiledBuild).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false);
}
