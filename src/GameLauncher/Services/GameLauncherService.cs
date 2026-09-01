using System.Diagnostics;
using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class GameLauncherService
{
    /// <summary>Starts the game and returns the process that was started, where there is one.
    /// For Steam this is the URI handler rather than the game, so it can be null or unrelated -
    /// GameSessionWatcher works out the real game process separately.</summary>
    public static Process? Launch(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.LaunchUri))
            return Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });

        return Process.Start(new ProcessStartInfo(game.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? game.InstallDir,
        });
    }
}
