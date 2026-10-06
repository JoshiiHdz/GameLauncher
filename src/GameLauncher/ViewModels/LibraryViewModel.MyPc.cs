using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>My PC: a page of what is inside this computer (board, BIOS, Secure Boot, processor, memory, graphics, drives).</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: stands in for reading the real hardware.</summary>
    internal Func<SystemInfoSnapshot>? SystemInfoForTest { get; set; }

    private bool _readingMyPc;

    /// <summary>Reads the hardware (a second or two of Windows queries, off the UI thread) and opens the My PC page. Asking again while it reads does nothing.</summary>
    [RelayCommand]
    private async Task ShowMyPc()
    {
        if (_readingMyPc)
            return;

        _readingMyPc = true;
        try
        {
            var read = SystemInfoForTest ?? SystemInfoService.Collect;
            var snapshot = await Task.Run(read);
            OpenPage(MyPcPageKey, "My PC", snapshot);
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the My PC page.", ex);
            StatusText = $"Couldn't read this PC: {ex.Message}";
        }
        finally
        {
            _readingMyPc = false;
        }
    }
}
