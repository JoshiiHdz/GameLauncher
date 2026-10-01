using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GameLauncher.Services;

/// <summary>One installed Windows package, as Get-AppxPackage itself reports it - InstallLocation is
/// the REAL content folder Windows resolves for this package, which for a "Win32 packaged"/Desktop
/// Bridge title (the shape most current Xbox/Game Pass PC games actually use) is the game's own,
/// ordinary, non-locked folder on whichever drive the user chose - not necessarily anywhere under
/// "XboxGames", and not necessarily WindowsApps either. For a pure UWP title it IS the ACL-locked
/// WindowsApps folder; XboxScanner still won't read files out of that one, but can use PackageFamilyName
/// to build an AUMID and launch it via shell:appsFolder regardless.</summary>
public sealed record XboxPackageInfo(string PackageFamilyName, string InstallLocation, string Name);

/// <summary>
/// Discovers installed packages via Get-AppxPackage - the documented, public source for "what Windows
/// thinks is installed and where" (PackageFamilyName, InstallLocation), as opposed to guessing from a
/// folder-naming convention. This is XboxScanner's PRIMARY discovery path; scanning "XboxGames" folders
/// directly is only ever the fallback, for whatever this doesn't find (older Windows without reliable
/// Appx enumeration, or a title packaged in some form Get-AppxPackage doesn't surface).
///
/// This intentionally returns EVERY installed package, games and non-games alike - Get-AppxPackage has
/// no "is this a game" flag. XboxScanner is what narrows it down (by checking each candidate's
/// InstallLocation for MicrosoftGame.config/AppxManifest.xml, the same identity check a game actually
/// needs anyway) - not this class, which would otherwise have to duplicate that same filtering logic for
/// no benefit.
/// </summary>
public static class XboxPackageDiscovery
{
    /// <summary>Test seam: bypasses PowerShell entirely. Null (the default) runs the real command.</summary>
    internal static Func<IReadOnlyList<XboxPackageInfo>>? PackagesOverrideForTest { get; set; }

    public static IReadOnlyList<XboxPackageInfo> GetInstalledPackages(CancellationToken ct = default)
    {
        if (PackagesOverrideForTest is { } fake)
            return fake();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // Same Base64-around-the-JSON approach as StartAppsResolver, and for the identical
                    // reason: real package/display names carry non-ASCII characters that a redirected
                    // child process's stdout encoding cannot be trusted to round-trip correctly across
                    // every Windows locale/PowerShell version otherwise.
                    Arguments = "-NoProfile -NonInteractive -Command "
                        + "\"Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } | "
                        + "Select-Object PackageFamilyName, InstallLocation, Name | ConvertTo-Json -Compress | "
                        + "ForEach-Object { [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($_)) }\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            var rawOutput = StartAppsResolver.RunAndReadStdout(process, TimeSpan.FromSeconds(20), ct);
            if (rawOutput is null)
            {
                Logger.Warn("Get-AppxPackage timed out.");
                return [];
            }

            var base64Output = rawOutput.Trim();
            if (string.IsNullOrWhiteSpace(base64Output))
                return [];

            var output = Encoding.UTF8.GetString(Convert.FromBase64String(base64Output));
            using var doc = JsonDocument.Parse(output);
            var results = new List<XboxPackageInfo>();

            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement }.AsEnumerable();

            foreach (var item in items)
            {
                var familyName = GetString(item, "PackageFamilyName");
                var installLocation = GetString(item, "InstallLocation");
                var name = GetString(item, "Name");

                if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(installLocation)
                    || string.IsNullOrWhiteSpace(name))
                {
                    continue; // a package with no usable install location (or identity) is nothing a scanner can act on
                }

                results.Add(new XboxPackageInfo(familyName, installLocation, name));
            }

            return results;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                        or JsonException or IOException or FormatException)
        {
            Logger.Warn("Couldn't enumerate installed packages via Get-AppxPackage - Xbox detection will "
                + "fall back to scanning XboxGames folders directly.", ex);
            return [];
        }
    }

    private static string? GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
