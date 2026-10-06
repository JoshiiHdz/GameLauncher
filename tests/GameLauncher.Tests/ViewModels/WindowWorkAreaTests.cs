using System.IO;
using System.Windows;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

[Collection(WpfStaCollection.Name)]
public sealed class WindowWorkAreaTests(WpfStaFixture sta)
{
    private static MainWindow Open()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-WorkArea-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        return ShellTestSupport.OpenMain(vm, 600, 400);
    }

    [Fact]
    public void AWindowWhoseBottomIsUnderTheTaskbar_IsMovedUpIntoTheWorkArea() => sta.RunAsync(async () =>
    {
        var work = SystemParameters.WorkArea;
        var window = Open();
        try
        {
            var (width, height) = (window.Width, window.Height); // the window's own minimum size applies
            window.Left = work.Left + 40;
            window.Top = work.Bottom - 100; // most of it hangs below the work area, as after restoring from maximized
            WindowWorkArea.Clamp(window);

            Assert.True(window.Top + window.Height <= work.Bottom + 1, $"bottom {window.Top + window.Height} vs work area bottom {work.Bottom}");
            Assert.Equal(height, window.Height);
            Assert.Equal(width, window.Width);
        }
        finally { window.Close(); }

        await Task.CompletedTask;
    });

    [Fact]
    public void AWindowTallerThanTheWorkArea_IsShrunkToFit() => sta.RunAsync(async () =>
    {
        var work = SystemParameters.WorkArea;
        var window = Open();
        try
        {
            window.Left = work.Left + 10;
            window.Top = work.Top + 10;
            window.Height = work.Height + 500;
            WindowWorkArea.Clamp(window);

            Assert.True(window.Height <= work.Height + 1);
            Assert.True(window.Top >= work.Top - 1);
            Assert.True(window.Top + window.Height <= work.Bottom + 1);
        }
        finally { window.Close(); }

        await Task.CompletedTask;
    });

    [Fact]
    public void AWindowAlreadyInsideTheWorkArea_IsLeftAlone() => sta.RunAsync(async () =>
    {
        var work = SystemParameters.WorkArea;
        var window = Open();
        try
        {
            var (width, height) = (window.Width, window.Height);
            window.Left = work.Left + 50;
            window.Top = work.Top + 50;
            WindowWorkArea.Clamp(window);

            Assert.Equal(work.Left + 50, window.Left, 1);
            Assert.Equal(work.Top + 50, window.Top, 1);
            Assert.Equal(width, window.Width);
            Assert.Equal(height, window.Height);
        }
        finally { window.Close(); }

        await Task.CompletedTask;
    });
}
