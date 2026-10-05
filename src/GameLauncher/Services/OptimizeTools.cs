using System.IO;

namespace GameLauncher.Services;

/// <summary>Something the Optimize page can open. <see cref="Target"/> is a program or a link; <see cref="IsInstalled"/> tells the page
/// whether it is a program on this PC or a download page.</summary>
public sealed record OptimizeTool(string Name, string Description, string ActionText, string Target, bool IsInstalled);

/// <summary>Other tools worth knowing about next to Axis's own Optimize page. BleachBit and Mem Reduct are open source under the GPLv3,
/// which is why they are offered here to open or download rather than copied into the launcher: their code stays theirs, under their
/// licence, and Axis only starts them when they are already installed.</summary>
public static class OptimizeTools
{
    public static IReadOnlyList<OptimizeTool> Detect(Func<string, bool>? fileExists = null, Func<Environment.SpecialFolder, string>? folder = null)
    {
        fileExists ??= File.Exists;
        folder ??= Environment.GetFolderPath;
        var programFiles = new[] { folder(Environment.SpecialFolder.ProgramFiles), folder(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string? Find(string subFolder, string exe) =>
            programFiles.Select(p => Path.Combine(p, subFolder, exe)).FirstOrDefault(fileExists);

        var bleachBit = Find("BleachBit", "bleachbit.exe");
        var memReduct = Find("Mem Reduct", "memreduct.exe");

        return
        [
            new OptimizeTool("BleachBit",
                "Open-source disk cleaner for browsers, apps and system leftovers. Goes much further than the cleaner here.",
                bleachBit is null ? "Get BleachBit" : "Open BleachBit", bleachBit ?? "https://www.bleachbit.org/", bleachBit is not null),
            new OptimizeTool("Mem Reduct",
                "Open-source memory cleaner that can also clear Windows' standby memory (needs administrator rights).",
                memReduct is null ? "Get Mem Reduct" : "Open Mem Reduct", memReduct ?? "https://github.com/henrypp/memreduct", memReduct is not null),
            new OptimizeTool("Windows Storage settings",
                "Windows' own Storage Sense: automatic clean-up and a breakdown of what is using your drive.",
                "Open Storage settings", "ms-settings:storagesense", true),
            new OptimizeTool("Disk Cleanup",
                "Windows' classic cleaner, including old Windows Update files.",
                "Open Disk Cleanup", "cleanmgr.exe", true),
        ];
    }
}
