using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GameLauncher.Services;

/// <summary>
/// Resolves the AppUserModelID (AUMID) for installed Store/MSIX apps via PowerShell's Get-StartApps,
/// so packaged games (Xbox/Game Pass titles) can be launched the way a real Start Menu or desktop
/// shortcut launches them: "shell:appsFolder\{AUMID}".
///
/// This exists because running an Xbox title's exe (e.g. gamelaunchhelper.exe) directly, the way a
/// normal Win32 game is launched, skips the activation context the OS sets up when a shortcut
/// activates the package properly - a documented source of failures for third-party launchers
/// handling Store apps. Get-StartApps entries for real packaged apps have an AUMID containing "!"
/// (PackageFamilyName!AppId); plain Start Menu shortcuts to traditional exes don't, and are ignored.
/// </summary>
public static class StartAppsResolver
{
    public static IReadOnlyList<(string Name, string Aumid)> GetPackagedApps()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // Real Start Menu titles often carry ®/™ (e.g. "Call of Duty®"). Without forcing
                    // both sides to UTF-8, .NET reads the child process's stdout using the system's
                    // ANSI codepage instead, silently mangling those characters (® became a stray "r")
                    // - which then broke cover-art matching downstream, since the corrupted name never
                    // matches the real game on SteamGridDB. Both the encoding .NET reads with and the
                    // one PowerShell actually writes with have to agree, hence setting both.
                    Arguments = "-NoProfile -NonInteractive -Command "
                        + "\"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; "
                        + "Get-StartApps | ConvertTo-Json -Compress\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                Logger.Warn("Get-StartApps timed out.");
                return [];
            }

            if (string.IsNullOrWhiteSpace(output))
                return [];

            using var doc = JsonDocument.Parse(output);
            var results = new List<(string, string)>();

            // A single result comes back as an object, not an array.
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement }.AsEnumerable();

            foreach (var item in items)
            {
                var name = item.GetProperty("Name").GetString();
                var appId = item.GetProperty("AppID").GetString();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId) || !appId.Contains('!'))
                    continue; // not a packaged app's AUMID - a plain Start Menu shortcut

                results.Add((name, appId));
            }

            return results;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                        or JsonException or IOException)
        {
            Logger.Warn("Couldn't resolve Store app IDs via Get-StartApps.", ex);
            return [];
        }
    }
}
