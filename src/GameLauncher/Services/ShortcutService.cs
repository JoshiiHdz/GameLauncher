using System.IO;
using System.Runtime.InteropServices;

namespace GameLauncher.Services;

public static class ShortcutService
{
    public static bool DesktopShortcutExists() => File.Exists(GetShortcutPath());

    public static void CreateDesktopShortcut()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || Path.GetFileNameWithoutExtension(exePath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Shortcuts can only be created from the built GameLauncher.exe, not from 'dotnet run'.");
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;

        try
        {
            dynamic shortcut = shell.CreateShortcut(GetShortcutPath());
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.IconLocation = exePath;
                shortcut.Description = "Game Launcher";
                shortcut.Save();
            }
            finally
            {
                Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static string GetShortcutPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "Game Launcher.lnk");
    }
}
