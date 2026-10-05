using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Uninstalls a game that came from the Xbox app / Microsoft Store. These have no uninstall wizard: Windows removes the whole package, which
/// is what Settings' Uninstall button and Revo Uninstaller's Store-app list both do. This asks Windows to do the same, for exactly this
/// game's package, and only after the user has said yes in the launcher. Axis deletes nothing itself.
/// </summary>
public static partial class XboxPackageRemover
{
    /// <summary>"Name_publisherid": 13 lowercase letters and digits after an underscore.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$")]
    private static partial Regex FamilyNamePattern();

    /// <summary>The package folder Windows creates: <c>Name_1.2.3.4_x64__publisherid</c>.</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9.\-]+)_[0-9.]+_[A-Za-z0-9]*__(?<publisher>[a-z0-9]{13})$")]
    private static partial Regex PackageFolderPattern();

    /// <summary>The package family name of an Xbox/Store game, or null when the entry does not say. It is in the app's launch id
    /// (<c>shell:appsFolder\Family!App</c>), or in the name of the package folder under WindowsApps.</summary>
    public static string? FamilyNameOf(GameEntry game)
    {
        const string prefix = "shell:appsFolder\\";
        if (game.LaunchUri is { } uri && uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var aumid = uri[prefix.Length..];
            var bang = aumid.IndexOf('!');
            var family = bang < 0 ? aumid : aumid[..bang];
            if (FamilyNamePattern().IsMatch(family))
                return family;
        }

        foreach (var folder in Segments(game.InstallDir))
        {
            var match = PackageFolderPattern().Match(folder);
            if (match.Success)
                return match.Groups["name"].Value + "_" + match.Groups["publisher"].Value;
        }

        return null;
    }

    private static IEnumerable<string> Segments(string path) =>
        (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The PowerShell that finds the package (by family name, else the package whose folder holds the game's folder) and removes
    /// it. It contains no game data: the family name and folder are passed in the environment, so nothing can be injected into it.</summary>
    internal const string RemoveScript = """
        $ErrorActionPreference = 'Stop'
        $pkg = $null
        if ($env:AXIS_FAMILY) { $pkg = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq $env:AXIS_FAMILY } | Select-Object -First 1 }
        if (-not $pkg -and $env:AXIS_DIR) {
            $dir = $env:AXIS_DIR.TrimEnd('\')
            $pkg = Get-AppxPackage |
                Where-Object { $_.InstallLocation -and $dir.StartsWith($_.InstallLocation.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) } |
                Sort-Object { $_.InstallLocation.Length } -Descending | Select-Object -First 1
        }
        if (-not $pkg) { exit 3 }
        Remove-AppxPackage -Package $pkg.PackageFullName
        """;

    internal static ProcessStartInfo BuildStartInfo(string? familyName, string installDir)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(RemoveScript);
        info.Environment["AXIS_FAMILY"] = familyName is not null && FamilyNamePattern().IsMatch(familyName) ? familyName : "";
        info.Environment["AXIS_DIR"] = installDir ?? "";
        return info;
    }

    /// <summary>Asks Windows to remove the game's package. Returns whether it worked and a sentence for the user either way.</summary>
    public static async Task<(bool Succeeded, string Message)> RemoveAsync(string gameName, string? familyName, string installDir,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(BuildStartInfo(familyName, installDir))
                ?? throw new InvalidOperationException("PowerShell did not start.");
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode switch
            {
                0 => (true, $"Uninstalled {gameName}."),
                3 => (false, $"Windows has no installed package for {gameName}, so there is nothing to uninstall here."),
                _ => (false, $"Windows couldn't uninstall {gameName}: {FirstLine(error)}"),
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logger.Warn($"Uninstall: couldn't run PowerShell to remove '{gameName}'.", ex);
            return (false, $"Couldn't ask Windows to uninstall {gameName}: {ex.Message}");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "it gave no reason." : line;
    }
}
