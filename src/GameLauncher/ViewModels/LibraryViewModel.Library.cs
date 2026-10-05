using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Win32;

namespace GameLauncher.ViewModels;

/// <summary>Library-level actions: watched folders, the desktop shortcut, the logs folder and the feedback form.</summary>
public partial class LibraryViewModel
{
    // Async (and awaiting the refresh below) rather than firing RefreshCommand and discarding the
    // task: a discarded task's exceptions only ever surface via App.xaml.cs's global
    // UnobservedTaskException logging, well after the fact and with no way to reflect the failure in
    // this command's own state. Awaiting it here means a failure is observed at the actual call site,
    // and the generated AddFolderCommand/RemoveFolderCommand stay IAsyncRelayCommand - the same
    // ICommand-compatible type XAML already binds to (see MainWindow.xaml / SettingsPage.xaml).
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select a games folder" };
        if (dialog.ShowDialog() != true)
            return;

        if (WatchedFolders.Any(w => string.Equals(w.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
            return;

        var watched = new WatchedFolder { Path = dialog.FolderName };
        WatchedFolderResolver.CaptureAnchor(watched); // so a later drive-letter change can self-heal

        WatchedFolders.Add(watched);
        _settings.WatchedFolders.Add(watched);
        _settingsService.Save(_settings);

        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(WatchedFolder? folder)
    {
        if (folder is null)
            return;

        WatchedFolders.Remove(folder);
        _settings.WatchedFolders.Remove(folder);
        _settingsService.Save(_settings);

        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDir);
            Process.Start(new ProcessStartInfo(Logger.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            StatusText = $"Couldn't open logs folder: {ex.Message}";
        }
    }

    /// <summary>Test seam: receives the feedback form's view model instead of opening the real window.</summary>
    internal Action<FeedbackViewModel>? FeedbackDialogForTest { get; set; }

    /// <summary>Opens the in-app feedback form (bug report / feature idea). The form sends through the project's relay and falls
    /// back to the GitHub issue page by itself when sending isn't possible.</summary>
    [RelayCommand]
    private void ReportBug()
    {
        try
        {
            Logger.Info("Feedback: opening the form.");
            var form = new FeedbackViewModel();
            if (FeedbackDialogForTest is { } seam)
            {
                seam(form);
                return;
            }

            AppShell.ShowModal("Send Feedback", new FeedbackDialog(form));
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the feedback form.", ex);
            StatusText = $"Couldn't open the feedback form: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateDesktopShortcut))]
    private void CreateDesktopShortcut()
    {
        try
        {
            ShortcutService.CreateDesktopShortcut();
            StatusText = "Desktop shortcut created";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                        or COMException)
        {
            Logger.Error("Couldn't create desktop shortcut.", ex);
            StatusText = $"Couldn't create shortcut: {ex.Message}";
            return;
        }

        RefreshShortcutState();
    }

    private void RefreshShortcutState()
    {
        if (ShortcutService.DesktopShortcutExists())
        {
            DesktopShortcutButtonText = "Shortcut Already on Desktop";
            CanCreateDesktopShortcut = false;
        }
        else
        {
            DesktopShortcutButtonText = "Create Desktop Shortcut";
            CanCreateDesktopShortcut = true;
        }
    }
}
