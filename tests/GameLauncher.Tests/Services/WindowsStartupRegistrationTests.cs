using System.IO;
using System.Runtime.InteropServices;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

public sealed class WindowsStartupRegistrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Startup-" + Guid.NewGuid());

    [Fact]
    public void Shortcut_RoundTripsInIsolatedDirectory_AndDisableLeavesOtherFilesAlone()
    {
        Directory.CreateDirectory(_dir);
        var other = Path.Combine(_dir, "Other.txt"); File.WriteAllText(other, "keep");
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var service = new WindowsStartupRegistration(_dir, () => (exe, ""));
        Assert.False(service.IsEnabled);
        service.SetEnabled(true); Assert.True(service.IsEnabled);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic link = shell.CreateShortcut(Path.Combine(_dir, "Axis Game Launcher.lnk"));
            try { Assert.Equal(exe, (string)link.TargetPath, ignoreCase: true); Assert.Equal("", (string)link.Arguments); }
            finally { Marshal.ReleaseComObject(link); }
        }
        finally { Marshal.ReleaseComObject(shell); }
        service.SetEnabled(false); service.SetEnabled(false);
        Assert.False(service.IsEnabled); Assert.True(File.Exists(other));
    }

    [Fact]
    public void InstalledTargetUsesStableUpdater_PortableUsesItsOwnExe()
    {
        Directory.CreateDirectory(_dir);
        var updater = Path.Combine(_dir, "Update.exe"); File.WriteAllBytes(updater, []);
        Assert.Equal((updater, "start \"GameLauncher.exe\""),
            WindowsStartupRegistration.BuildTarget(@"C:\Apps\GameLauncher\current\GameLauncher.exe", updater));
        Assert.Equal((@"C:\My Games\GameLauncher.exe", ""),
            WindowsStartupRegistration.BuildTarget(@"C:\My Games\GameLauncher.exe", null));
        Assert.Throws<InvalidOperationException>(() => WindowsStartupRegistration.BuildTarget(@"C:\dotnet.exe", null));
    }

    private sealed class FakeRegistration : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }
        public bool Fail;
        public void SetEnabled(bool enabled)
        {
            if (Fail) throw new IOException("Access denied for test");
            IsEnabled = enabled;
        }
    }

    [Fact]
    public void TogglePersistsOnSuccess_AndRevertsOnRegistrationFailure()
    {
        var service = new FakeRegistration();
        var vm = new LibraryViewModel(new SettingsService(_dir), new PendingUpdateNotesService(_dir), service);
        Assert.False(vm.StartWithWindows);
        vm.StartWithWindows = true;
        Assert.True(service.IsEnabled); Assert.True(new SettingsService(_dir).Load().StartWithWindows);
        service.Fail = true; vm.StartWithWindows = false;
        Assert.True(vm.StartWithWindows); Assert.True(service.IsEnabled);
        Assert.Contains("Couldn't change", vm.StatusText);
        service.Fail = false; vm.StartWithWindows = false;
        Assert.False(vm.StartWithWindows); Assert.False(service.IsEnabled);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
