using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>One line of the gaming health check on the Optimize page: what was found and, when a game tweak addresses it, a button that switches that tweak on.</summary>
public sealed partial class AdvisorRow
{
    private readonly LibraryViewModel? _library;
    private readonly Action _refresh;

    internal AdvisorRow(AdvisorFinding finding, LibraryViewModel? library, Action refresh)
    {
        Finding = finding;
        _library = library;
        _refresh = refresh;
    }

    public AdvisorFinding Finding { get; }

    public string Title => Finding.Title;

    public string Detail => Finding.Detail;

    /// <summary>A WPF-UI symbol name for the level.</summary>
    public string Icon => Finding.Level switch
    {
        AdviceLevel.Good => "CheckmarkCircle24",
        AdviceLevel.Advice => "Warning24",
        _ => "Info24",
    };

    public bool IsAdvice => Finding.Level == AdviceLevel.Advice;

    /// <summary>The tweak that addresses this finding, when it is one that can be switched on from here and is not already.</summary>
    private GameLauncher.Services.GameTweak? Tweak =>
        Finding.TweakId is { } id && _library?.GameTweakRows.FirstOrDefault(r => r.Tweak.Id == id) is { IsOn: false } row ? row.Tweak : null;

    public bool HasAction => Tweak is not null;

    public string ActionText => Tweak is { } t ? $"Switch on: {t.Name}" : "";

    [RelayCommand]
    private void Apply()
    {
        if (Finding.TweakId is { } id && _library?.GameTweakRows.FirstOrDefault(r => r.Tweak.Id == id) is { } row)
            row.IsOn = true; // it is then applied when a game starts, like any other tweak

        _refresh();
    }
}
