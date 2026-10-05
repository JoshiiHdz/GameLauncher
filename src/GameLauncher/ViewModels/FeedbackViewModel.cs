using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public sealed record FeedbackKindOption(FeedbackKind Kind, string Label);

/// <summary>The in-app feedback form: pick a type, write a message, optionally leave a contact, send. If sending is not possible
/// (no relay in this build, or no connection) the form says so and offers the GitHub issue form as a fallback.</summary>
public sealed partial class FeedbackViewModel : ObservableObject
{
    private readonly FeedbackService _service;
    private readonly Func<string>? _detailsForTest;

    public FeedbackViewModel() : this(new FeedbackService(), null) { }

    internal FeedbackViewModel(FeedbackService service, Func<string>? detailsForTest)
    {
        _service = service;
        _detailsForTest = detailsForTest;
        _selectedKind = Kinds[0];
    }

    public IReadOnlyList<FeedbackKindOption> Kinds { get; } =
    [
        new(FeedbackKind.Bug, "Something is broken"),
        new(FeedbackKind.Idea, "Feature idea"),
        new(FeedbackKind.Other, "Something else"),
    ];

    [ObservableProperty]
    private FeedbackKindOption _selectedKind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _message = "";

    [ObservableProperty]
    private string _contact = "";

    /// <summary>App version, Windows version and the last few error lines. On by default: it is what makes a bug fixable.</summary>
    [ObservableProperty]
    private bool _includeDetails = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True once the message went through; the window closes itself shortly after.</summary>
    [ObservableProperty]
    private bool _isSent;

    /// <summary>Shown when sending failed: the user can still report it on GitHub.</summary>
    [ObservableProperty]
    private bool _showGitHubFallback;

    public int MaxLength => FeedbackService.MaxUserTextLength;

    private bool CanSend() => !IsSending && !IsSent && !string.IsNullOrWhiteSpace(Message);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        IsSending = true;
        ShowGitHubFallback = false;
        StatusText = "Sending...";
        try
        {
            var details = IncludeDetails ? (_detailsForTest?.Invoke() ?? FeedbackService.CurrentDetails()) : null;
            var outcome = await _service.SendAsync(SelectedKind.Kind, Message, Contact, details);
            (StatusText, ShowGitHubFallback) = outcome switch
            {
                FeedbackOutcome.Sent => ("Sent - thank you!", false),
                FeedbackOutcome.TooSoon => ("You just sent one. Please wait a few seconds before sending another.", false),
                FeedbackOutcome.NotConfigured => ("This version can't send feedback directly. You can report it on GitHub instead.", true),
                _ => ("Couldn't send it - check your connection and try again, or report it on GitHub instead.", true),
            };
            IsSent = outcome == FeedbackOutcome.Sent;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Test seam: receives the GitHub issue link instead of opening the browser.</summary>
    internal Action<string>? OpenUrlForTest { get; set; }

    [RelayCommand]
    private void OpenGitHub()
    {
        var url = BugReport.BuildIssueUrl(AppInfo.Version, Environment.OSVersion.VersionString);
        if (OpenUrlForTest is { } seam)
        {
            seam(url);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warn("Feedback: couldn't open the GitHub issue page.", ex);
            StatusText = "Couldn't open the browser. The page is github.com/JoshiiHdz/GameLauncher/issues.";
        }
    }
}
