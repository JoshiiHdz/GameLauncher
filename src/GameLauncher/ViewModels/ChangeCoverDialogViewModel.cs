using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameLauncher.ViewModels;

/// <summary>Backs ChangeCoverWindow's local-only Preview -> Apply/Cancel step. Deliberately holds no
/// gameId, no file bytes, and calls nothing in LibraryViewModel or ArtworkAssetStore - it only ever
/// displays the already-decoded preview image LibraryViewModel.ValidateLocalCoverImageAsync produced and
/// records which button the user pressed. The actual write-to-disk/commit-to-settings step
/// (ApplyValidatedCoverImageAsync) runs back in LibraryViewModel.ChangeCoverAsync, AFTER this dialog has
/// already closed and only if Applied came back true - so a user closing this window any way at all
/// (Cancel, the title bar's own close button, Alt+F4) is exactly equivalent, and none of them can leave
/// anything partially changed because nothing here is capable of changing anything in the first place.</summary>
public sealed partial class ChangeCoverDialogViewModel : ObservableObject
{
    public string GameName { get; }
    public BitmapImage PreviewImage { get; }

    /// <summary>True only once ApplyCommand has run - read by ChangeCoverWindow's owner (see
    /// LibraryViewModel.ShowChangeCoverPreviewDialog) after ShowDialog() returns, since that's the one
    /// signal it needs to decide whether to go on and actually apply the previewed image.</summary>
    public bool Applied { get; private set; }

    /// <summary>Raised by either command below - ChangeCoverWindow's code-behind subscribes and closes
    /// itself, so this view model never needs to reference the Window it's hosted in.</summary>
    public event Action? RequestClose;

    public ChangeCoverDialogViewModel(string gameName, BitmapImage previewImage)
    {
        GameName = gameName;
        PreviewImage = previewImage;
    }

    [RelayCommand]
    private void Apply()
    {
        Applied = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Applied = false;
        RequestClose?.Invoke();
    }
}
