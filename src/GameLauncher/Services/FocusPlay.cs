using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>The Windows power plans, as Focus play needs them. A seam so none of it needs a real PC to test.</summary>
public interface IPowerPlans
{
    /// <summary>The plan Windows is using now; null when it cannot be read.</summary>
    Guid? GetActive();

    /// <summary>Makes a plan the active one; false when Windows refuses (a PC whose power settings are locked by policy, say).</summary>
    bool SetActive(Guid plan);

    /// <summary>Makes a new plan that starts as a copy of a built-in one, with this name; null when Windows cannot.</summary>
    Guid? Duplicate(Guid source, string name);

    /// <summary>Removes a plan this app made. Never called on a built-in plan.</summary>
    bool Delete(Guid plan);

    /// <summary>False only when the PC is known to be running on a battery.</summary>
    bool IsOnMains();
}

/// <summary>The real thing: powrprof.dll. Plans are told apart by their GUIDs, never their names, which Windows translates.</summary>
public sealed class PowerPlans : IPowerPlans
{
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootKey, ref Guid sourceSchemeGuid, out IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(IntPtr rootKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr powerSetting, byte[] buffer, uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr rootKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    public Guid? GetActive()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStructure<Guid>(pointer);
            }
            finally
            {
                LocalFree(pointer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool SetActive(Guid plan)
    {
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public Guid? Duplicate(Guid source, string name)
    {
        try
        {
            if (PowerDuplicateScheme(IntPtr.Zero, ref source, out var pointer) != 0 || pointer == IntPtr.Zero)
                return null;

            Guid created;
            try
            {
                created = Marshal.PtrToStructure<Guid>(pointer);
            }
            finally
            {
                LocalFree(pointer);
            }

            var bytes = Encoding.Unicode.GetBytes(name + "\0");
            PowerWriteFriendlyName(IntPtr.Zero, ref created, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length); // a missing name is cosmetic
            return created;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Delete(Guid plan)
    {
        try
        {
            return PowerDeleteScheme(IntPtr.Zero, ref plan) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public bool IsOnMains()
    {
        try
        {
            // 0 = on battery, 1 = plugged in, 255 = unknown (a desktop has no battery): only a known battery counts as "not plugged in".
            return !GetSystemPowerStatus(out var status) || status.AcLineStatus != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }
}

/// <summary>What happened when a game was launched with Focus play on.</summary>
public enum FocusPlayOutcome
{
    /// <summary>The PC was switched to a faster power plan.</summary>
    Switched,

    /// <summary>The PC was already on a high performance plan; nothing was changed.</summary>
    AlreadyFast,

    /// <summary>Skipped on purpose: the PC is on battery and the setting says only when plugged in.</summary>
    SkippedOnBattery,

    /// <summary>This PC has no usable faster plan, or Windows would not switch.</summary>
    NotAvailable,

    /// <summary>A switch from an earlier launch is still in force, so it was kept as it is.</summary>
    AlreadyActive,
}

public sealed record FocusPlayResult(FocusPlayOutcome Outcome, string Message);

/// <summary>Whether Focus play can work on this PC, found out by trying (see <see cref="FocusPlayService.CheckAvailability"/>).</summary>
public sealed record FocusPlayAvailability(bool Available, string Message);

/// <summary>Focus play: while a game launched from here runs, the PC uses a high performance power plan, then goes back to the plan it was on. Only the
/// plan is switched - nothing is closed, throttled or edited. The plan to go back to is written to disk before the switch, so even a crash or a
/// power cut cannot leave the PC stuck on it: the next start puts it back.</summary>
public sealed class FocusPlayService
{
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public const string OwnPlanName = "Axis Focus play";

    private readonly IPowerPlans _plans;
    private readonly AppSettings _settings;
    private readonly Action _save;

    // Begin, End and the availability check all switch the plan and share the record of what to put back, so only one runs at a time.
    private readonly object _gate = new();

    public FocusPlayService(IPowerPlans plans, AppSettings settings, Action save)
    {
        _plans = plans;
        _settings = settings;
        _save = save;
    }

    /// <summary>True while a switch made here has not been undone yet.</summary>
    public bool IsActive => _settings.FocusPlayRestorePlan is not null;

    /// <summary>Switches to the fastest plan this PC offers: Ultimate Performance, then High performance, then a High performance plan made from
    /// the built-in template (kept and reused, named "Axis Focus play"). Does nothing when the PC is already on one of those.</summary>
    public FocusPlayResult Begin(bool onlyWhenPluggedIn)
    {
        lock (_gate)
            return BeginCore(onlyWhenPluggedIn);
    }

    private bool IsFastPlan(Guid plan) =>
        plan == UltimatePerformance || plan == HighPerformance || (_settings.FocusPlayOwnPlan is { } own && Guid.TryParse(own, out var ownGuid) && ownGuid == plan);

    private List<Guid> Candidates()
    {
        // Windows' own list of plans can't be trusted for this: a Modern Standby PC hides plans from it that it still switches to happily. So each
        // plan is simply tried, fastest first, and the first one Windows accepts is used.
        var candidates = new List<Guid> { UltimatePerformance, HighPerformance };
        if (_settings.FocusPlayOwnPlan is { } saved && Guid.TryParse(saved, out var savedGuid))
            candidates.Add(savedGuid);

        return candidates;
    }

    /// <summary>Finds out whether Focus play can work on this PC, before it is offered. Windows does not say which plans it will switch to (it hides some
    /// that it still accepts), so the only reliable test is to try: each fast plan is switched to for an instant and the PC put straight back, and a plan
    /// made only to test is deleted again. The way back is saved first, exactly as in <see cref="Begin"/>, so a crash mid-check is undone at the next start.
    /// Nothing is left changed.</summary>
    public FocusPlayAvailability CheckAvailability()
    {
        lock (_gate)
        {
            if (IsActive)
                return new(true, "");

            if (_plans.GetActive() is not { } active)
                return new(false, "Windows won't say which power plan this PC is using, so Focus play can't switch it.");

            if (IsFastPlan(active))
                return new(true, "");

            _settings.FocusPlayRestorePlan = active.ToString();
            _settings.FocusPlayAppliedPlan = null;
            _save();

            Guid? usable = Candidates().Cast<Guid?>().FirstOrDefault(c => _plans.SetActive(c!.Value));
            Guid? made = null;
            if (usable is null && _plans.Duplicate(HighPerformance, OwnPlanName) is { } created)
            {
                made = created;
                usable = _plans.SetActive(created) ? created : null;
            }

            var back = usable is null || _plans.SetActive(active) || _plans.SetActive(Balanced);
            if (!back)
            {
                // Could not return: keep the record pointing at the plan that is active now, so the next start puts it right.
                _settings.FocusPlayAppliedPlan = usable.ToString();
                _save();
                Logger.Warn("Focus play: the availability check could not switch the power plan back; it will be tried again at the next start.");
                return new(true, "");
            }

            if (made is { } temporary)
                _plans.Delete(temporary); // only ever the plan this check just made

            _settings.FocusPlayRestorePlan = null;
            _settings.FocusPlayAppliedPlan = null;
            _save();

            return usable is null
                ? new(false, "This PC has no high performance power plan, and Windows won't make or switch to one (a work or school PC can block it).")
                : new(true, "");
        }
    }

    private FocusPlayResult BeginCore(bool onlyWhenPluggedIn)
    {
        if (IsActive)
            return new(FocusPlayOutcome.AlreadyActive, "");

        if (_plans.GetActive() is not { } active)
            return new(FocusPlayOutcome.NotAvailable, "Focus play isn't available on this PC: Windows won't say which power plan is in use.");

        if (onlyWhenPluggedIn && !_plans.IsOnMains())
            return new(FocusPlayOutcome.SkippedOnBattery, "Focus play is off while on battery power.");

        if (IsFastPlan(active))
            return new(FocusPlayOutcome.AlreadyFast, "");

        var candidates = Candidates();

        // Written before the switch: if anything goes wrong from here on, the next start still knows what to put back.
        _settings.FocusPlayRestorePlan = active.ToString();
        _settings.FocusPlayAppliedPlan = null;
        _save();

        var applied = candidates.FirstOrDefault(_plans.SetActive);
        if (applied == Guid.Empty && _plans.Duplicate(HighPerformance, OwnPlanName) is { } created)
        {
            _settings.FocusPlayOwnPlan = created.ToString();
            _save(); // the new plan is known before it is switched to, so a crash right after cannot lose track of it
            applied = _plans.SetActive(created) ? created : Guid.Empty;
        }

        if (applied == Guid.Empty)
        {
            _settings.FocusPlayRestorePlan = null;
            _save();
            return new(FocusPlayOutcome.NotAvailable, "Focus play isn't available on this PC: it has no high performance power plan and Windows won't make or switch to one.");
        }

        _settings.FocusPlayAppliedPlan = applied.ToString();
        _save();
        return new(FocusPlayOutcome.Switched, "Focus play: high performance power plan on while you play.");
    }

    /// <summary>Puts the original plan back - but only if the PC is still on the one this switched to, so a plan the person chose themselves in the
    /// meantime is never overwritten. Safe to call when nothing was switched (also the crash recovery at start).</summary>
    public void End()
    {
        lock (_gate)
            EndCore();
    }

    private void EndCore()
    {
        if (_settings.FocusPlayRestorePlan is not { } original)
            return;

        try
        {
            var applied = Guid.TryParse(_settings.FocusPlayAppliedPlan, out var a) ? a : (Guid?)null;
            // The plan to put back is on disk before any switch, but the plan switched to is only written once the switch worked. A crash in between leaves
            // no applied plan: then the PC is on the original (nothing to undo) or already on one of the fast plans (undo it), and the fast plans are known.
            var switched = _plans.GetActive() is { } current && (applied is not null ? current == applied : IsFastPlan(current) && !(Guid.TryParse(original, out var o) && o == current));
            if (switched && Guid.TryParse(original, out var restore))
            {
                // The original plan may have been deleted since; Balanced always exists, so it is the fallback rather than staying stuck on this one.
                if (!_plans.SetActive(restore) && !_plans.SetActive(Balanced))
                {
                    Logger.Warn("Focus play: Windows would not switch the power plan back; it will be tried again at the next start.");
                    return; // keep the record so the next start tries again
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Focus play: putting the power plan back failed; it will be tried again at the next start.", ex);
            return;
        }

        _settings.FocusPlayRestorePlan = null;
        _settings.FocusPlayAppliedPlan = null;
        _save();
    }
}

/// <summary>Raises game processes to High priority (never Realtime). Best effort: games running as administrator, or protected by anti-cheat,
/// refuse it, and that is reported as "couldn't", never as an error.</summary>
public static class GamePriority
{
    /// <summary>Test seam: stands in for changing a real process's priority. Returns true when it worked.</summary>
    internal static Func<int, bool>? RaiseForTest { get; set; }

    /// <summary>Raises each process and returns how many accepted it.</summary>
    public static int Raise(IEnumerable<int> processIds)
    {
        var raised = 0;
        foreach (var id in processIds)
        {
            if (RaiseOne(id))
                raised++;
        }

        return raised;
    }

    private static bool RaiseOne(int id)
    {
        if (RaiseForTest is { } seam)
            return seam(id);

        try
        {
            using var process = Process.GetProcessById(id);
            if (process.PriorityClass == ProcessPriorityClass.High || process.PriorityClass == ProcessPriorityClass.RealTime)
                return true;

            process.PriorityClass = ProcessPriorityClass.High;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Logger.Info($"Focus play: process {id} would not take a higher priority ({ex.Message}).");
            return false;
        }
    }
}
