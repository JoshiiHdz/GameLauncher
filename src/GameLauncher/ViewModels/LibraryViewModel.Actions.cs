using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Two actions that leave the library: handing a game's uninstall to its launcher, and shutting the PC down.</summary>
public partial class LibraryViewModel
{
    internal const int ShutdownDelaySeconds = 10;

    /// <summary>Test seam: stands in for starting the uninstall link or launcher. Receives the target.</summary>
    internal Action<string>? OpenUninstallTargetForTest { get; set; }

    /// <summary>Finds a game's own uninstall wizard (registry, then its folder). Replaced in tests so they never read this machine's
    /// installed programs.</summary>
    internal Func<GameEntry, UninstallCommand?> UninstallWizardFinder { get; set; } =
        game => UninstallLocator.Find(game, UninstallLocator.ReadInstalledPrograms());

    /// <summary>Test seam: stands in for starting an uninstall wizard. Receives the command.</summary>
    internal Action<UninstallCommand>? StartUninstallWizardForTest { get; set; }

    /// <summary>Finds a launcher's executable; replaced in tests so they never depend on what is installed on the machine.</summary>
    internal Func<GameSource, string?> LauncherExeFinder { get; set; } = PlatformIconService.FindLauncherExe;

    /// <summary>Test seam: stands in for the "Shut down your PC?" question; receives its text, returns true for Yes.</summary>
    internal Func<string, bool>? ConfirmShutdownForTest { get; set; }

    /// <summary>Test seam: stands in for running shutdown.exe. Receives the arguments.</summary>
    internal Action<string>? StartShutdownForTest { get; set; }

    /// <summary>True from the moment a shutdown is confirmed until it is cancelled or its countdown has passed - drives the footer's
    /// "Cancel shutdown" button.</summary>
    [ObservableProperty]
    private bool _isShutdownPending;

    private CancellationTokenSource? _shutdownCts;

    private void RunShutdownExe(string arguments)
    {
        if (StartShutdownForTest is { } seam)
            seam(arguments);
        else
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
    }

    private void OpenUninstallTarget(UninstallTarget target)
    {
        if (target.Route == UninstallRoute.Wizard)
        {
            var command = new UninstallCommand(target.Target, target.Arguments);
            if (StartUninstallWizardForTest is { } wizardSeam)
                wizardSeam(command);
            else
            {
                // ShellExecute, so an uninstaller that needs administrator rights gets Windows' own prompt.
                Process.Start(new ProcessStartInfo(command.Exe, command.Arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(command.Exe) ?? "",
                });
            }

            return;
        }

        OpenUninstallTarget(target.Target);
    }

    private void OpenUninstallTarget(string target)
    {
        if (OpenUninstallTargetForTest is { } seam)
            seam(target);
        else
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>Test seam: stands in for the yes/no cards the uninstall flow can show; receives the question, returns true for yes.</summary>
    internal Func<string, bool>? ConfirmUninstallForTest { get; set; }

    /// <summary>Test seam: stands in for asking Windows to remove an Xbox/Store package (game name, family name, install folder).</summary>
    internal Func<string, string?, string, Task<(bool Succeeded, string Message)>>? RemoveXboxPackageForTest { get; set; }

    private bool ConfirmUninstall(string title, string question, string yes) =>
        ConfirmUninstallForTest is { } seam ? seam(question) : AppShell.Confirm(title, question, yes, "Cancel", warning: true);

    /// <summary>Uninstalls a game the way Revo Uninstaller would: it opens the game's own uninstall wizard (found in the installed-programs
    /// list or in the game's folder), Steam's own prompt for Steam games, or - for Xbox/Store games, which have no wizard - asks Windows
    /// to remove the package after confirming here. It never sends the user to a list to search. Nothing is deleted from here.</summary>
    [RelayCommand]
    private async Task UninstallGame(GameEntry? game)
    {
        if (game is null)
            return;

        var target = UninstallRouter.Resolve(game, LauncherExeFinder, UninstallWizardFinder);
        try
        {
            Logger.Info($"Uninstall: '{game.Name}' ({game.Source}) -> {target.Route} '{target.Target}' {target.Arguments}".TrimEnd() + ".");
            switch (target.Route)
            {
                case UninstallRoute.XboxPackage:
                    await UninstallXboxPackageAsync(game, target);
                    return;

                case UninstallRoute.NoUninstaller:
                    OfferToOpenFolder(game);
                    return;

                default:
                    OpenUninstallTarget(target);
                    StatusText = target.Explanation;
                    return;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to Windows' administrator prompt: they chose not to uninstall, so nothing else is opened.
            Logger.Info($"Uninstall: the administrator prompt for '{game.Name}' was declined.");
            StatusText = $"The uninstaller for {game.Name} wasn't started - Windows needs your permission to run it.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logger.Warn($"Uninstall: couldn't start '{target.Target}' for '{game.Name}'.", ex);
            StatusText = $"Couldn't start the uninstaller for {game.Name}: {ex.Message}";
        }
    }

    private async Task UninstallXboxPackageAsync(GameEntry game, UninstallTarget target)
    {
        var question = $"Uninstall {game.Name}?\n\nWindows will remove the game and its files from this PC.";
        if (!ConfirmUninstall("Uninstall game", question, "Uninstall"))
        {
            StatusText = "Nothing was uninstalled.";
            return;
        }

        StatusText = target.Explanation;
        var family = string.IsNullOrEmpty(target.Target) ? null : target.Target;
        var (succeeded, message) = RemoveXboxPackageForTest is { } seam
            ? await seam(game.Name, family, target.Arguments)
            : await XboxPackageRemover.RemoveAsync(game.Name, family, target.Arguments);

        StatusText = message;
        if (succeeded)
            await (RescanAfterChange ?? DefaultRescan)(); // the game is gone: the library should say so
    }

    /// <summary>A game with no uninstaller anywhere (copied into a folder rather than installed): say so, and offer its folder.</summary>
    private void OfferToOpenFolder(GameEntry game)
    {
        var question = $"{game.Name} has no uninstaller that Windows or a launcher knows about - it was most likely copied into its folder rather than installed.\n\nOpen its folder so you can remove it yourself?";
        if (ConfirmUninstall("No uninstaller found", question, "Open folder"))
            OpenInstallLocation(game);
        else
            StatusText = "Nothing was uninstalled.";
    }

    /// <summary>Shuts the PC down - only after the user says yes. The question names a running game, since that is the thing a
    /// shutdown would cut off. Windows is given a few seconds' notice rather than an instant shutdown, and never forced, so any app
    /// with unsaved work still gets to ask about it.</summary>
    [RelayCommand]
    private void ShutdownPc()
    {
        var running = _runningGameId is { } id ? _allGames.FirstOrDefault(g => g.Id == id)?.Name : null;
        var question = running is null
            ? "Shut down your PC?"
            : $"{running} is still running. Shut down your PC anyway?";

        bool confirmed;
        try
        {
            confirmed = ConfirmShutdownForTest is { } seam
                ? seam(question)
                : AppShell.Confirm("Shut down PC", question, yes: "Shut down", no: "Cancel", warning: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't ask about shutting down.", ex);
            StatusText = "Couldn't ask about shutting down, so nothing was changed.";
            return;
        }

        if (!confirmed)
        {
            StatusText = "Shutdown cancelled.";
            return;
        }

        var arguments = $"/s /t {ShutdownDelaySeconds} /c \"Axis Game Launcher: shutting down this PC\"";
        try
        {
            Logger.Info($"Shutdown: confirmed by the user, running shutdown.exe {arguments}");
            RunShutdownExe(arguments);

            StatusText = $"Shutting down in {ShutdownDelaySeconds} seconds - press Cancel shutdown to stop it.";
            _shutdownCts?.Cancel();
            _shutdownCts = new CancellationTokenSource();
            IsShutdownPending = true;
            _ = ClearShutdownPendingAsync(_shutdownCts.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Error("Shutdown: couldn't start shutdown.exe.", ex);
            StatusText = $"Couldn't shut down the PC: {ex.Message}";
        }
    }

    /// <summary>Once the countdown has passed there is nothing left to cancel (the PC is going down, or Windows refused).</summary>
    internal Func<TimeSpan, CancellationToken, Task> ShutdownCountdownDelay { get; set; } = Task.Delay;

    private async Task ClearShutdownPendingAsync(CancellationToken token)
    {
        try
        {
            await ShutdownCountdownDelay(TimeSpan.FromSeconds(ShutdownDelaySeconds + 5), token);
            IsShutdownPending = false;
        }
        catch (OperationCanceledException)
        {
            // Cancelled (or replaced by a newer shutdown): whoever cancelled it already updated the state.
        }
    }

    /// <summary>Stops a shutdown that was confirmed a moment ago.</summary>
    [RelayCommand]
    private void CancelShutdown()
    {
        _shutdownCts?.Cancel();
        IsShutdownPending = false;
        try
        {
            RunShutdownExe("/a");
            Logger.Info("Shutdown: cancelled by the user.");
            StatusText = "Shutdown cancelled.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Error("Shutdown: couldn't cancel.", ex);
            StatusText = $"Couldn't cancel the shutdown: {ex.Message}. Run \"shutdown /a\" to stop it.";
        }
    }
}
