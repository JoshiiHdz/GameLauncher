using System.Diagnostics;
using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class GameLauncherService
{
    public static void Launch(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.LaunchUri))
        {
            Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(game.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? game.InstallDir,
        });
    }
}
