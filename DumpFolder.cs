using System;
using System.Diagnostics;
using System.IO;
using ExileCore2;

namespace AutoExpedition;

/// <summary>
/// Opens the folder the dumps and the recordings are written to.
///
/// **One place, because two buttons point at it.** It sits under Investigation, where a dump is
/// taken, and again at the top of Recording, where the csv files people are asked to send land - and
/// somebody looking for a file should not have to know which section the button was filed under.
/// Copying the body would have been two descriptions of one path, which is the fault that costs a
/// session when one of them changes.
///
/// The folder is created if it is not there, so the button works before the first dump rather than
/// failing at whoever pressed it.
/// </summary>
internal static class DumpFolder
{
    /// <summary>Where it is, which is one subfolder of the plugin's own config directory.</summary>
    public static string Path => System.IO.Path.Combine(Unknowns.Home, "dumps");

    public static void Open()
    {
        try
        {
            var where = Path;

            Directory.CreateDirectory(where);
            Process.Start(new ProcessStartInfo { FileName = where, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not open the dump folder: {ex.Message}", 5f);
        }
    }
}
