using System.Diagnostics;
using System.Windows;

namespace MutagenMon.App;

/// <summary>Implements the self-restart path (TIC-9/TIC-10): spawn a fresh
/// process, then cleanly shut this one down.</summary>
public static class SelfRestart
{
    public static void RestartAndExit()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
