using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// ChangeCoverDialogViewModel deliberately does nothing but record which button was pressed and raise
/// RequestClose - it never touches settings, disk, or LibraryViewModel. These tests exist to pin exactly
/// that: Apply and Cancel are otherwise indistinguishable from each other except for the one bit
/// (Applied) the owning command reads after the dialog closes.
/// </summary>
public class ChangeCoverDialogViewModelTests
{
    private static BitmapImage MakePreviewImage()
    {
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Gray8, null, new byte[16], 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [Fact]
    public void Constructor_ExposesGameNameAndPreviewImage_AppliedStartsFalse()
    {
        var preview = MakePreviewImage();

        var vm = new ChangeCoverDialogViewModel("My Game", preview);

        Assert.Equal("My Game", vm.GameName);
        Assert.Same(preview, vm.PreviewImage);
        Assert.False(vm.Applied);
    }

    [Fact]
    public void ApplyCommand_SetsAppliedTrue_RaisesRequestClose()
    {
        var vm = new ChangeCoverDialogViewModel("My Game", MakePreviewImage());
        var closeRaised = false;
        vm.RequestClose += () => closeRaised = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(vm.Applied);
        Assert.True(closeRaised);
    }

    [Fact]
    public void CancelCommand_LeavesAppliedFalse_RaisesRequestClose()
    {
        var vm = new ChangeCoverDialogViewModel("My Game", MakePreviewImage());
        var closeRaised = false;
        vm.RequestClose += () => closeRaised = true;

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Applied);
        Assert.True(closeRaised);
    }

    /// <summary>Guards against a regression where Cancel, reached after some earlier Apply somehow ran
    /// (impossible today - the window closes on the first RequestClose - but worth pinning since
    /// Applied has no other guard), would leave a stale `true` in place instead of explicitly resetting
    /// it.</summary>
    [Fact]
    public void CancelCommand_ExplicitlySetsAppliedFalse_EvenIfSomehowCalledAfterApply()
    {
        var vm = new ChangeCoverDialogViewModel("My Game", MakePreviewImage());

        vm.ApplyCommand.Execute(null);
        Assert.True(vm.Applied);

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Applied);
    }
}
