using System.Runtime.CompilerServices;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.TestSupport;

/// <summary>Runs once when the test assembly loads. No test may ever change the power plan of the PC it runs on, so every library view model
/// starts with this pretend set of plans (Balanced in use, High performance available) unless a test supplies its own.</summary>
internal static class NoRealPowerPlans
{
    [ModuleInitializer]
    internal static void Install() => LibraryViewModel.PowerPlansFactory = () => new PretendPowerPlans();
}

internal sealed class PretendPowerPlans : IPowerPlans
{
    private Guid _active = FocusPlayService.Balanced;

    public Guid? GetActive() => _active;

    public bool SetActive(Guid plan)
    {
        if (plan != FocusPlayService.HighPerformance && plan != FocusPlayService.Balanced)
            return false;

        _active = plan;
        return true;
    }

    public Guid? Duplicate(Guid source, string name) => null;

    public bool Delete(Guid plan) => false;

    public bool IsOnMains() => true;
}
