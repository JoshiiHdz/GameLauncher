using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameLauncher.Services;

/// <summary>What Windows has for one tweak. <see cref="Applied"/> is true when the tweak's own target is already how Windows is (so switching it on changes
/// nothing), false when it is not, and null when that cannot be said (a per-game setting, a service that is not on this PC).</summary>
public sealed record TweakWindowsState(bool? Applied, string Text);

/// <summary>Whether a Windows service is running. A seam, so tests never ask the real service manager.</summary>
public interface IServiceStates
{
    /// <summary>True running, false stopped, null when the service is not on this PC or cannot be read.</summary>
    bool? IsRunning(string service);
}

/// <summary>The real service manager, asked directly through its API (a status query needs no permission). No process is started to find out: reading a state must not
/// flash a busy cursor or spawn anything.</summary>
public sealed class SystemServiceStates : IServiceStates
{
    private static readonly Regex ServiceName = new("^[A-Za-z0-9_]{1,40}$", RegexOptions.Compiled);

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 4;
    private const uint ServiceStopped = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public bool? IsRunning(string service)
    {
        if (!ServiceName.IsMatch(service))
            return null;

        try
        {
            var manager = OpenSCManagerW(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
                return null;

            try
            {
                var handle = OpenServiceW(manager, service, ServiceQueryStatus);
                if (handle == IntPtr.Zero)
                    return null; // not installed (1060) or not readable

                try
                {
                    if (!QueryServiceStatus(handle, out var status))
                        return null;

                    return status.dwCurrentState == ServiceRunning ? true : status.dwCurrentState == ServiceStopped ? false : null;
                }
                finally
                {
                    CloseServiceHandle(handle);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }
}

/// <summary>Reads, without changing anything, what Windows has for each game tweak. This is what lets the Optimize page say "Game Mode is already on in Windows" next to a
/// switch that means "apply this when a game starts": the two are different questions, and a person has to be able to see both.</summary>
public static class TweakStateReader
{
    private const string GameBar = @"Software\Microsoft\GameBar";

    private static int? Dword(IUserRegistry registry, string key, string name) =>
        registry.Read(key, name) is { } text && text.StartsWith("dword:", StringComparison.Ordinal) && int.TryParse(text[6..], out var value) ? value : null;

    /// <summary>The state for a user-setting or service tweak; null for the launcher's own (memory and power plan, read by the library) and unknown ids.</summary>
    public static TweakWindowsState? Read(string id, IUserRegistry registry, IServiceStates services)
    {
        switch (id)
        {
            case "game-mode":
            {
                // Windows 11 has Game Mode on until someone turns it off, so a missing value means on.
                var on = Dword(registry, GameBar, "AutoGameModeEnabled") is not 0;
                return on
                    ? new(true, "Already on in Windows")
                    : new(false, "Off in Windows - turned on while a game runs");
            }

            case "capture-off":
            {
                var recording = Dword(registry, @"System\GameConfigStore", "GameDVR_Enabled");
                var capture = Dword(registry, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
                if (recording is 1 || capture is 1)
                    return new(false, "Recording is on in Windows - paused while a game runs");

                return recording is 0 || capture is 0
                    ? new(true, "Already off in Windows")
                    : new(null, "Windows default - paused while a game runs");
            }

            case "quiet-toasts":
                return Dword(registry, @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED") is 0
                    ? new(true, "Banners already off in Windows")
                    : new(false, "Banners are on in Windows - silenced while a game runs");

            case "windowed-opt":
            {
                var raw = registry.Read(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
                var text = raw is not null && raw.StartsWith("string:", StringComparison.Ordinal) ? raw[7..] : null;
                var on = text is not null && text.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => p.Trim().Equals("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase));
                return on
                    ? new(true, "Already on in Windows")
                    : new(false, "Off in Windows - turned on while a game runs");
            }

            case "gpu-high":
                return new(null, "Set for each game when it starts");
        }

        if (GameTweakCatalog.ById(id) is { Kind: TweakKind.Service })
        {
            var running = GameTweakCatalog.ServicesFor(id).Select(services.IsRunning).ToList();
            if (running.All(r => r is null))
                return new(null, "Not on this PC (or cannot be read)");

            return running.Any(r => r is true)
                ? new(false, "Running - paused while a game runs")
                : new(true, "Already stopped");
        }

        return null;
    }
}
