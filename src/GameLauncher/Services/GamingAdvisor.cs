using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>How a finding should be read.</summary>
public enum AdviceLevel
{
    /// <summary>Already as it should be.</summary>
    Good,

    /// <summary>Worth knowing; nothing to change.</summary>
    Info,

    /// <summary>Something that can be improved.</summary>
    Advice,
}

/// <summary>One line of the gaming health check. <see cref="TweakId"/> names a game tweak that addresses it, when there is one.</summary>
public sealed record AdvisorFinding(string Id, string Title, string Detail, AdviceLevel Level, string? TweakId = null);

/// <summary>What the health check reads about this PC. A seam, so the logic needs no real display or registry to test. Every member is read-only.</summary>
public interface IAdvisorFacts
{
    /// <summary>The primary display's current refresh rate and the fastest it offers at the same resolution; null when unknown.</summary>
    (int Current, int Max)? DisplayRefresh();

    /// <summary>The Windows hardware-accelerated GPU scheduling mode: 2 on, 1 off, null not supported.</summary>
    int? HardwareScheduling();

    bool? GameMode();

    /// <summary>Whether "Optimizations for windowed games" is on for the user.</summary>
    bool? WindowedOptimizations();

    /// <summary>Whether Memory integrity (virtualization-based kernel protection) is on.</summary>
    bool? MemoryIntegrity();

    MemoryStatus Memory();

    /// <summary>True when the active power plan is a high performance one; null when it cannot be read.</summary>
    bool? HighPerformancePlan();

    /// <summary>The names of the graphics adapters (empty when unknown).</summary>
    IReadOnlyList<string> GpuNames();
}

/// <summary>The gaming health check: reads how this PC is set up for games and says what is worth changing - and what is not. It changes nothing. It deliberately gives
/// no advice that trades security or stability for a speculative gain (Memory integrity is explained, never switched), and it does not call a PC "unoptimized" for showing
/// more memory in use: that is Windows using the memory it has.</summary>
public static class GamingAdvisor
{
    public static IReadOnlyList<AdvisorFinding> Check(IAdvisorFacts facts)
    {
        var findings = new List<AdvisorFinding>();

        if (facts.DisplayRefresh() is { } refresh)
        {
            findings.Add(refresh.Max > refresh.Current + 5
                ? new("refresh", "Your display is not running at its fastest",
                    $"It is set to {refresh.Current} Hz, and it offers {refresh.Max} Hz at this resolution. Games cannot show more frames than the display does. Change it in Windows Settings > System > Display > Advanced display.", AdviceLevel.Advice)
                : new("refresh", "Display refresh rate", $"Running at {refresh.Current} Hz, the fastest it offers at this resolution.", AdviceLevel.Good));
        }

        if (facts.GameMode() is { } gameMode)
        {
            findings.Add(gameMode
                ? new("game-mode", "Windows Game Mode", "On: Windows favours a game over background work.", AdviceLevel.Good)
                : new("game-mode", "Windows Game Mode is off", "Turning it on lets Windows favour the game over background work. The Game tweaks list can switch it on for each game.", AdviceLevel.Advice, "game-mode"));
        }

        if (facts.WindowedOptimizations() is { } windowed)
        {
            findings.Add(windowed
                ? new("windowed", "Optimizations for windowed games", "On: windowed and borderless DirectX 10/11 games are presented the efficient way.", AdviceLevel.Good)
                : new("windowed", "Optimizations for windowed games are off",
                    "A Windows 11 setting that presents windowed and borderless DirectX 10/11 games the efficient way, which can lower latency. It helps those games only (not Vulkan, DirectX 12 or exclusive full screen). Game tweaks can switch it on for each game.",
                    AdviceLevel.Advice, "windowed-opt"));
        }

        var memory = facts.Memory();
        if (memory.TotalBytes > 0)
        {
            var free = (double)memory.AvailableBytes / memory.TotalBytes;
            findings.Add(free < 0.15
                ? new("memory", "Memory is running short", $"{memory.Describe()}. Close apps you do not need before playing; trimming memory only moves idle pages out and they come straight back.", AdviceLevel.Advice)
                : new("memory", "Memory", $"{memory.Describe()}. Plenty is free: Windows keeps what it caches usefully, so a high 'in use' number is normal.", AdviceLevel.Good));
        }

        if (facts.DisplayRefresh() is { } cap && cap.Max >= 100)
        {
            var target = Math.Max(cap.Current, cap.Max);
            findings.Add(new("frame-cap", "A frame cap for lower latency",
                $"On a {target} Hz display, capping a game at about {target - 3} fps (a few below the refresh rate) keeps it inside variable refresh rate and avoids a queue of frames waiting to be shown. Without variable refresh rate, cap at {target}. It is set in the game or the graphics driver, not here.",
                AdviceLevel.Info));
        }

        if (facts.GpuNames().Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new("reflex", "NVIDIA Reflex",
                "Your NVIDIA card supports Reflex: in games that offer it (an in-game setting), turn it on - it shortens the render queue and lowers latency. It is a game feature; Axis cannot add it to games that do not have it.", AdviceLevel.Info));
        }

        if (facts.HighPerformancePlan() is { } fast)
        {
            findings.Add(fast
                ? new("power", "Power plan", "A high performance power plan is in use.", AdviceLevel.Good)
                : new("power", "Power plan", "The balanced plan is in use. It is fine for most games; Focus play (Settings > Performance) can switch to a high performance plan only while a game runs.", AdviceLevel.Info));
        }

        if (facts.HardwareScheduling() is { } scheduling)
        {
            findings.Add(new("hags", "Hardware-accelerated GPU scheduling",
                scheduling == 2
                    ? "On. Its effect varies by game and driver; if one stutters, comparing with it off in Windows Settings > System > Display > Graphics is a fair test."
                    : "Off. Its effect varies by game and driver; it can be switched in Windows Settings > System > Display > Graphics > Default graphics settings.", AdviceLevel.Info));
        }

        if (facts.MemoryIntegrity() is { } integrity)
        {
            findings.Add(new("memory-integrity", "Memory integrity",
                integrity
                    ? "On: it protects the Windows kernel from malicious drivers. It can cost a few percent in some games. Axis does not change it - that is a security decision for you."
                    : "Off: the kernel protection is not active on this PC. Axis does not change it.", AdviceLevel.Info));
        }

        findings.Add(new("shader-cache", "Shader caches", "Games keep compiled shaders so they do not stutter while compiling them. Clearing them makes the next launch stutter, so the cleaner leaves them unticked.", AdviceLevel.Info));
        return findings;
    }
}

/// <summary>The real thing: the display mode list and read-only registry values.</summary>
public sealed class SystemAdvisorFacts(IPowerPlans powerPlans, Func<MemoryStatus> memory) : IAdvisorFacts
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);

    private const int CurrentSettings = -1;

    public (int Current, int Max)? DisplayRefresh()
    {
        try
        {
            var current = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(null, CurrentSettings, ref current) || current.dmDisplayFrequency <= 1)
                return null;

            var max = (int)current.dmDisplayFrequency;
            for (var i = 0; i < 400; i++)
            {
                var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettingsW(null, i, ref mode))
                    break;

                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmBitsPerPel == current.dmBitsPerPel)
                    max = Math.Max(max, (int)mode.dmDisplayFrequency);
            }

            return ((int)current.dmDisplayFrequency, max);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    private static T? Read<T>(RegistryKey root, string path, string name) where T : struct
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) is T value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public int? HardwareScheduling() => Read<int>(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");

    public bool? GameMode() => Read<int>(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is { } value ? value != 0 : true; // Windows 11 default: on

    public bool? WindowedOptimizations()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var text = key?.GetValue("DirectXUserGlobalSettings") as string ?? "";
            return text.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => p.Trim().Equals("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool? MemoryIntegrity() =>
        Read<int>(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is { } v ? v != 0 : null;

    public MemoryStatus Memory() => memory();

    public IReadOnlyList<string> GpuNames()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return searcher.Get().Cast<System.Management.ManagementBaseObject>().Select(o => o["Name"] as string).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    public bool? HighPerformancePlan() =>
        powerPlans.GetActive() is { } active
            ? active == FocusPlayService.HighPerformance || active == FocusPlayService.UltimatePerformance
            : null;
}
