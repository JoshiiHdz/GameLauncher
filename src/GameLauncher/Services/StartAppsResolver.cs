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
                    // Real Start Menu titles often carry ®/™ (e.g. "Call of Duty®"). A first attempt
                    // forced [Console]::OutputEncoding = UTF8 on the child and StandardOutputEncoding
                    // on the parent to match - verified working on the dev machine, but a real log from
                    // a different PC still came back mangled ("Call of DutyÂ®" - the classic signature
                    // of UTF-8 bytes read back as Windows-1252/Latin-1), meaning [Console]::OutputEncoding
                    // doesn't reliably apply to a console-less redirected process across every Windows
                    // PowerShell version/locale. Base64-encoding the JSON before it ever leaves
                    // PowerShell sidesteps the whole problem: the Base64 alphabet (A-Z, a-z, 0-9, +, /,
                    // =) is a subset of plain ASCII, so it reads back identically no matter which
                    // codepage either side is using - there is no codepage left to disagree over.
                    Arguments = "-NoProfile -NonInteractive -Command "
                        + "\"Get-StartApps | ConvertTo-Json -Compress | "
                        + "ForEach-Object { [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($_)) }\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var base64Output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                Logger.Warn("Get-StartApps timed out.");
                return [];
            }

            if (string.IsNullOrWhiteSpace(base64Output))
                return [];

            var output = Encoding.UTF8.GetString(Convert.FromBase64String(base64Output));
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
                                        or JsonException or IOException or FormatException)
        {
            Logger.Warn("Couldn't resolve Store app IDs via Get-StartApps.", ex);
            return [];
        }
    }
}
