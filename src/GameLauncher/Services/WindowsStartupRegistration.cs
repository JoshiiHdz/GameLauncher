using System.IO;
using System.Runtime.InteropServices;
using Velopack.Locators;

namespace GameLauncher.Services;

internal interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

/// <summary>Only owns this app's per-user Startup shortcut. No registry/service/admin changes.</summary>
internal sealed class WindowsStartupRegistration(string startupDirectory, Func<(string Exe, string Arguments)> target) : IStartupRegistration
{
    private string ShortcutPath => Path.Combine(startupDirectory, "Axis Game Launcher.lnk");
    public bool IsEnabled => File.Exists(ShortcutPath);

    public static WindowsStartupRegistration ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), () =>
        {
            var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;
            return BuildTarget(Environment.ProcessPath,
                locator is { IsPortable: false } ? locator.UpdateExePath : null);
        });

    internal static (string Exe, string Arguments) BuildTarget(string? exe, string? updater)
    {
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe)
            || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start with Windows requires a built GameLauncher.exe.");
        // Installed builds use Velopack's stable entry point, not a version-specific content path.
        if (!string.IsNullOrWhiteSpace(updater) && File.Exists(updater))
            return (updater, "start \"" + Path.GetFileName(exe) + "\"");
        return (exe, "");
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(ShortcutPath); // Exact app-owned shortcut only; no directory deletion.
            return;
        }
        var (exe, arguments) = target();
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            Directory.CreateDirectory(startupDirectory);
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try
            {
                shortcut.TargetPath = exe;
                shortcut.Arguments = arguments;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
                shortcut.Description = "Start Axis Game Launcher when you sign in";
                shortcut.Save();
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }
}
