using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests;

/// <summary>
/// Exercises the REAL ChangeCoverWindow - not a stand-in delegate. LibraryViewModelChangeCoverCommandTests
/// and LibraryViewModelGameCardWiringTests both substitute ChangeCoverPreviewDialogForTest for the actual
/// dialog, which proves LibraryViewModel's own commit/cancel logic is correct but says nothing about
/// whether the window itself - its Apply/Cancel buttons, its closing behavior, its layout at different
/// sizes, and (the reason this file exists) the crop the preview actually shows - is correct. This file
/// covers that remaining, previously-unverified surface.
///
/// IMPORTANT DISTINCTION: every "click" here is driven through a real AutomationPeer's IInvokeProvider
/// against the actual ui:Button elements from the actual compiled window - which does exercise the real
/// Command binding and the real ButtonBase.OnClick path a mouse click would also take. It is still not a
/// physical mouse-driven interaction test (no real OS input is synthesized, and this off-screen window
/// never has real keyboard focus), so it cannot observe hit-testing, real mouse-hover visuals, or genuine
/// OS-level accelerators. Alt+F4 specifically is Windows/WPF platform behavior (WM_SYSKEYDOWN -> WM_CLOSE
/// -> Window.Close()), not application code, and isn't something a same-process unit test can trigger
/// without synthesizing real OS input; the "WindowClose" tests below instead call Window.Close() directly
/// - the exact same method Alt+F4 and the title bar's own close button both funnel into - to verify what
/// actually matters at the application level: closing by any path, without Apply or Cancel ever running,
/// must never leave Applied incorrectly set.
/// </summary>
[Collection(WpfStaCollection.Name)]
public class ChangeCoverWindowTests
{
    private readonly WpfStaFixture _sta;

    public ChangeCoverWindowTests(WpfStaFixture sta) => _sta = sta;

    private static BitmapImage MakeImage(int width, int height)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
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

    private static (ChangeCoverWindow Window, ChangeCoverDialogViewModel ViewModel) BuildDialog(BitmapImage preview, string gameName = "Test Game")
    {
        var vm = new ChangeCoverDialogViewModel(gameName, preview);
        var window = new ChangeCoverWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        return (window, vm);
    }

    /// <summary>Invokes `element` through the same AutomationPeer/IInvokeProvider mechanism real
    /// accessibility tools (and, underneath, WPF's own input handling for a real click) use - this runs
    /// the control's actual OnClick override, including its bound Command, rather than merely raising the
    /// public Click routed event (which would notify handlers without ever invoking the command the way a
    /// real click does).</summary>
    private static void Invoke(UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element)
            ?? throw new InvalidOperationException($"No automation peer available for {element}.");
        var invokeProvider = (IInvokeProvider?)peer.GetPattern(PatternInterface.Invoke)
            ?? throw new InvalidOperationException($"{element} does not support the Invoke automation pattern.");

        // ButtonAutomationPeer.IInvokeProvider.Invoke() does not click synchronously - per its own
        // documented/observed implementation it posts the click via Dispatcher.BeginInvoke at
        // DispatcherPriority.Input rather than running it inline. Without pumping the queue afterward,
        // an assertion made immediately after this call would observe pre-click state (and, worse, leave
        // the click still pending to fire unpredictably during a LATER test sharing this same STA
        // dispatcher - which is exactly what produced this file's first, deeply confusing failure: the
        // Apply test's assertion ran before its own click had fired, and the still-pending click then
        // fired during the NEXT test instead).
        invokeProvider.Invoke();
        PumpDispatcher();
    }

    private static void PumpDispatcher()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    // ---- Real Apply/Cancel buttons, and closing without either ----------------------------------------

    [Fact]
    public void ApplyButton_RealClick_SetsAppliedTrue_AndClosesTheWindow()
    {
        _sta.RunAsync(async () =>
        {
            var (window, vm) = BuildDialog(MakeImage(200, 200));

            Invoke(window.ApplyButton);

            Assert.True(vm.Applied);
            Assert.False(window.IsVisible);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void CancelButton_RealClick_LeavesAppliedFalse_AndClosesTheWindow()
    {
        _sta.RunAsync(async () =>
        {
            var (window, vm) = BuildDialog(MakeImage(200, 200));

            Invoke(window.CancelButton);

            Assert.False(vm.Applied);
            Assert.False(window.IsVisible);
            await Task.CompletedTask;
        });
    }

    /// <summary>Stands in for Alt+F4 and the title bar's own close button - see this file's header
    /// comment for why those two specific interactions aren't directly simulable here, and why
    /// Window.Close() is the meaningful equivalent to test instead.</summary>
    [Fact]
    public void WindowClose_WithoutApplyOrCancel_LeavesAppliedFalse_DoesNotThrow()
    {
        _sta.RunAsync(async () =>
        {
            var (window, vm) = BuildDialog(MakeImage(200, 200));

            var exception = Record.Exception(() => window.Close());

            Assert.Null(exception);
            Assert.False(vm.Applied);
            Assert.False(window.IsVisible);
            await Task.CompletedTask;
        });
    }

    /// <summary>ChangeCoverWindow's constructor subscribes viewModel.RequestClose += Close - proves that
    /// wiring doesn't cause a re-entrant/double Close() when the window is closed through a path that
    /// never raises RequestClose at all (a plain Close() call, standing in for Alt+F4/title-bar close).</summary>
    [Fact]
    public void WindowClose_NeverRaisesRequestCloseASecondTime()
    {
        _sta.RunAsync(async () =>
        {
            var (window, vm) = BuildDialog(MakeImage(200, 200));
            var requestCloseCount = 0;
            vm.RequestClose += () => requestCloseCount++;

            window.Close();

            Assert.Equal(0, requestCloseCount);
            await Task.CompletedTask;
        });
    }

    // ---- Portrait, square, and landscape previews all crop to the card's own 2:3 box -------------------

    [Theory]
    [InlineData(100, 300)] // portrait
    [InlineData(200, 200)] // square
    [InlineData(400, 150)] // landscape
    public void PreviewBorder_AlwaysCropsToTheCardsExactAspectRatio_RegardlessOfSourceImageShape(int width, int height)
    {
        _sta.RunAsync(async () =>
        {
            var image = MakeImage(width, height);
            var (window, _) = BuildDialog(image);
            try
            {
                // 190x285 - the same 2:3 ratio (124x186) the library card itself uses. Fixed regardless
                // of the source image's own aspect ratio: that invariance IS the fix - see
                // ChangeCoverWindow.xaml's own remarks on why Stretch="Uniform" here was wrong.
                Assert.Equal(190, window.PreviewBorder.ActualWidth, precision: 0);
                Assert.Equal(285, window.PreviewBorder.ActualHeight, precision: 0);
                Assert.Equal(190.0 / 285.0, 124.0 / 186.0, precision: 3);
                Assert.True(window.PreviewBorder.ClipToBounds, "The crop box must actually clip - UniformToFill alone only overflows, it doesn't crop, without ClipToBounds.");

                // The dimension/ratio asserts above pin the FRAME, not the image inside it - a frame of
                // the right shape holding a Stretch="Uniform" (letterboxed, not cropped) image would
                // satisfy every assertion above while showing exactly the wrong thing again. This is the
                // one assertion that actually pins the crop behavior itself.
                Assert.Equal(Stretch.UniformToFill, window.PreviewImageElement.Stretch);
            }
            finally
            {
                window.Close();
            }
            await Task.CompletedTask;
        });
    }

    // ---- Default and minimum window sizes: content never overlaps the footer --------------------------

    [Fact]
    public void AtDefaultSize_ContentAreaNeverOverlapsTheFooter()
    {
        _sta.RunAsync(async () =>
        {
            var (window, _) = BuildDialog(MakeImage(200, 200));
            try
            {
                AssertScrollViewerEndsBeforeFooterBegins(window);
            }
            finally
            {
                window.Close();
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void AtMinimumSize_ContentAreaStillNeverOverlapsTheFooter()
    {
        _sta.RunAsync(async () =>
        {
            var (window, _) = BuildDialog(MakeImage(200, 200));
            try
            {
                window.Height = window.MinHeight;
                window.Width = window.MinWidth;
                window.UpdateLayout();

                AssertScrollViewerEndsBeforeFooterBegins(window);

                // The whole point of wrapping the content in a ScrollViewer: at the size where the fixed
                // 190x285 crop box plus the header text and Expander no longer fit, it scrolls instead of
                // pushing into (or being silently clipped behind) the footer.
                Assert.True(window.ContentScrollViewer.ScrollableHeight > 0,
                    "Expected the content to actually need scrolling at MinHeight - if this starts failing, MinHeight may have grown enough that this test no longer exercises the overlap risk it's meant to guard.");
            }
            finally
            {
                window.Close();
            }
            await Task.CompletedTask;
        });
    }

    private static void AssertScrollViewerEndsBeforeFooterBegins(ChangeCoverWindow window)
    {
        var scrollViewerBottom = window.ContentScrollViewer
            .TransformToAncestor(window)
            .Transform(new Point(0, window.ContentScrollViewer.ActualHeight)).Y;
        var footerTop = window.FooterGrid
            .TransformToAncestor(window)
            .Transform(new Point(0, 0)).Y;

        Assert.True(scrollViewerBottom <= footerTop + 0.5,
            $"Content area (bottom={scrollViewerBottom}) overlaps the footer (top={footerTop}).");
    }
}
