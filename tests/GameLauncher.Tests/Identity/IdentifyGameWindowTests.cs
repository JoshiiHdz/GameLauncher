using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>The REAL Identify Game window and the REAL card template, on the shared STA dispatcher. What this proves: the XAML
/// loads, its bindings resolve to the view-model's members (a binding typo would otherwise fail silently at runtime), the
/// right tab opens, the card's two new menu items act on the clicked game, and the badge follows NeedsIdentity.
/// What it cannot prove: how any of it LOOKS, and real mouse/keyboard behaviour - that is the gaming-PC checklist.</summary>
[Collection(WpfStaCollection.Name)]
public class IdentifyGameWindowTests
{
    private readonly WpfStaFixture _sta;

    public IdentifyGameWindowTests(WpfStaFixture sta) => _sta = sta;

    private static IdentifyGameWindow Show(IdentifyGameViewModel vm, bool startOnCovers = false)
    {
        var window = new IdentifyGameWindow(vm, startOnCovers)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        Pump();
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Invoke(UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element)
            ?? throw new InvalidOperationException($"No automation peer for {element}.");
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        Pump();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static Wpf.Ui.Controls.Button ButtonNamed(DependencyObject root, string content) =>
        Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content as string == content);

    // ---- The window ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWindow_Loads_WithTheDetectedTitleSearched_AndAutoSearchesOnOpen()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            h.Igdb.Candidates = _ => [new CatalogCandidate(Cat.Igdb, "1", "Foo", "2019", null)];
            var vm = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);

            var window = Show(vm);
            try
            {
                Assert.Equal("Identify game", ((TabItem)window.Tabs.SelectedItem).Header);
                Assert.Equal("Foo", window.SearchBox.Text);
                Assert.Single(vm.Candidates);                                    // Loaded fired the first search
                Assert.Single(window.CandidateList.Items);             // ...and the list is bound to it
                Assert.Equal("IGDB · 2019", vm.Candidates[0].Subtitle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheCoversEntryPoint_OpensOnTheCoverTab_AndLoadsCovers()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            h.Sgdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Distinct(90), false);
            h.Sgdb.Candidates = _ => [new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null)];
            var first = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);
            await first.SearchCommand.ExecuteAsync(null);
            first.SelectedCandidate = first.Candidates.Single();
            await first.ConfirmCommand.ExecuteAsync(null);
            h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
            var vm = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);

            var window = Show(vm, startOnCovers: true);
            try
            {
                Assert.Equal("Choose cover", ((TabItem)window.Tabs.SelectedItem).Header);
                Assert.Single(vm.Covers);
                Assert.Single(window.CoverList.Items);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CoverThumbnails_MatchTheCardsStretchAndAspectRatio_ForPortraitSquareAndWideThumbnails()
    {
        // A user picks a cover by its THUMBNAIL here; the card is what shows it afterwards. If the two ever disagreed (one cropping,
        // one letterboxing) the user would see one image while choosing and a differently-cropped one once applied.
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            h.Sgdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Distinct(90), false);
            h.Sgdb.Candidates = _ => [new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null)];
            var first = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);
            await first.SearchCommand.ExecuteAsync(null);
            first.SelectedCandidate = first.Candidates.Single();
            await first.ConfirmCommand.ExecuteAsync(null);

            h.Sgdb.Covers = _ => [new CoverChoice("portrait", "https://cdn2.steamgriddb.com/p.png", null),
                                  new CoverChoice("square", "https://cdn2.steamgriddb.com/s.png", null),
                                  new CoverChoice("wide", "https://cdn2.steamgriddb.com/w.png", null)]; // a SteamGridDB-banner shape
            var sizes = new Dictionary<string, (int W, int H)> { ["portrait"] = (100, 300), ["square"] = (200, 200), ["wide"] = (400, 150) };
            h.Sgdb.Download = url => TestImages.Png(sizes[url.Contains("/p.") ? "portrait" : url.Contains("/s.") ? "square" : "wide"].W,
                sizes[url.Contains("/p.") ? "portrait" : url.Contains("/s.") ? "square" : "wide"].H);
            var vm = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);

            var window = Show(vm, startOnCovers: true);
            try
            {
                for (var i = 0; i < 10 && vm.Covers.All(c => c.Thumbnail is null); i++)
                    Pump();

                Assert.Equal(3, window.CoverList.Items.Count);
                var thumbnailBorders = Descendants<Border>(window.CoverList).Where(b => b.Width == 100 && b.Height == 130).ToList();
                Assert.Equal(3, thumbnailBorders.Count);
                foreach (var border in thumbnailBorders)
                {
                    // The frame is always the card's own ratio, whatever shape the source thumbnail is...
                    Assert.Equal(100.0 / 130.0, 200.0 / 260.0, precision: 3);
                    var image = Descendants<System.Windows.Controls.Image>(border).Single();
                    // ...and the image inside it never crops, matching GameCardTemplate.xaml's GameArtImageStyle exactly.
                    Assert.Equal(Stretch.Uniform, image.Stretch);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheButtons_AreBoundToTheRealCommands_ConfirmWorksThroughTheWindow()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            h.Sgdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Distinct(90), false);
            h.Sgdb.Candidates = _ => [new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null)];
            var vm = new IdentifyGameViewModel(h.Vm, [h.Igdb, h.Sgdb], game.Id, game.Name);
            var window = Show(vm);
            try
            {
                Assert.False(ButtonNamed(window, "Clear identity").IsEnabled);    // nothing to clear yet: CanClear binding
                window.CandidateList.SelectedItem = window.CandidateList.Items[0]; // SelectedItem binding
                Assert.Same(vm.Candidates[0], vm.SelectedCandidate);

                Invoke(ButtonNamed(window, "This is the game"));
                for (var i = 0; i < 40 && h.Record(game.Id)?.Confirmed is null; i++)
                    Pump();

                Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), h.Record(game.Id)!.Confirmed!.Key);
                Assert.True(ButtonNamed(window, "Clear identity").IsEnabled);     // and the binding follows the state
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ClosingTheWindow_CancelsInFlightWork_WithoutThrowing()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            using var release = new ManualResetEventSlim();
            h.Igdb.Candidates = _ => { release.Wait(TimeSpan.FromSeconds(10)); return [new CatalogCandidate(Cat.Igdb, "1", "late", null, null)]; };
            var vm = new IdentifyGameViewModel(h.Vm, [h.Igdb], game.Id, game.Name);
            var window = Show(vm); // Loaded started a search that is now blocked

            var exception = Record.Exception(() => window.Close());
            release.Set();
            for (var i = 0; i < 10; i++)
                Pump();

            Assert.Null(exception);
            Assert.Empty(vm.Candidates);                                          // the late results were discarded
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ThePinnedCoverNote_IsShownOnlyWhileACustomCoverIsPinned()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-foo", "Foo"));
            var vm = new IdentifyGameViewModel(h.Vm, [], game.Id, game.Name);
            var window = Show(vm);
            try
            {
                var note = Descendants<TextBlock>(window).Single(t => t.Text.StartsWith("Your custom cover is kept", StringComparison.Ordinal));
                Assert.NotEqual(Visibility.Visible, note.Visibility);
                Assert.False(vm.HasPinnedCover);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- The card template ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheCards_TakeTheirWidthFromOneSharedValue_AndTheRoundedClipMatchesIt()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-a", "Game A with a rather long name indeed"));
            h.Vm.Games.Add(game);
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml") };
            var main = new ItemsControl { ItemTemplate = (DataTemplate)dict["GameCardTemplate"] };
            main.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.Games)));
            var window = new System.Windows.Window
            {
                DataContext = h.Vm, Content = main, Width = 900, Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false,
            };
            window.Resources.MergedDictionaries.Add(dict);
            window.Show();
            window.UpdateLayout();
            try
            {
                var width = (double)dict["GameCardWidth"];
                var artHeight = (double)dict["GameCardArtHeight"];
                var button = Descendants<System.Windows.Controls.Button>(main).First(b => ReferenceEquals(b.DataContext, game));
                var root = Descendants<Border>(button).First(b => b.Name == "CardRoot");

                Assert.True(width > 124);                                                    // wider than the old 124
                Assert.Equal(width, button.ActualWidth);                                     // the card really is that wide
                Assert.Equal(width, root.Clip.Bounds.Width);                                 // the rounded-corner clip covers the whole width...
                Assert.Equal(artHeight + 30, root.Clip.Bounds.Height);                       // ...and the whole art + caption strip
                Assert.Equal(width, root.ActualWidth);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void RealCoverArt_IsNeverCropped_MismatchedAspectRatiosLetterboxInstead()
    {
        // A real, confirmed case: some SteamGridDB covers are wide banners, not 2:3 posters. UniformToFill used to crop them - the
        // artwork's own edges got cut off, not a layout bug - which is what "cover art is partially cut off" was.
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var game = await h.Add(Games.Manual("manual-cover", "Foo"));
            game.Icon = TestBitmaps.Distinct(90);
            game.IsCoverArt = true;
            h.Vm.Games.Add(game);
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml") };
            var main = new ItemsControl { ItemTemplate = (DataTemplate)dict["GameCardTemplate"] };
            main.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.Games)));
            var window = new System.Windows.Window
            {
                DataContext = h.Vm, Content = main, Width = 900, Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false,
            };
            window.Resources.MergedDictionaries.Add(dict);
            window.Show();
            window.UpdateLayout();
            try
            {
                var button = Descendants<System.Windows.Controls.Button>(main).First(b => ReferenceEquals(b.DataContext, game));
                var art = Descendants<System.Windows.Controls.Image>(button).First(i => BindingOperations.GetBinding(i, System.Windows.Controls.Image.SourceProperty)?.Path.Path == "Icon");

                Assert.Equal(System.Windows.Media.Stretch.Uniform, art.Stretch);              // never UniformToFill: the whole image, nothing cropped
                Assert.Equal(double.NaN, art.Width);                                          // still fills its layout slot (letterboxed within it)...
                Assert.Equal(System.Windows.HorizontalAlignment.Stretch, art.HorizontalAlignment); // ...it's the Stretch mode that stops the crop, not the size
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void TheCardMenu_HasTheTwoNewItems_ThatActOnTheClickedGame_AndTheBadgeFollowsNeedsIdentity()
    {
        _sta.RunAsync(async () =>
        {
            using var h = new IdentityHarness();
            var gameA = await h.Add(Games.Manual("manual-a", "Game A"));
            var gameB = await h.Add(Games.Manual("manual-b", "Game B"));
            var opened = new List<(string Id, bool Covers)>();
            h.Vm.IdentifyDialogForTest = (dialog, covers) => opened.Add((dialog.GameId, covers));
            h.Vm.Games.Add(gameA);
            h.Vm.Games.Add(gameB);

            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml") };
            var main = new ItemsControl { ItemTemplate = (DataTemplate)dict["GameCardTemplate"] };
            main.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.Games)));
            var window = new System.Windows.Window
            {
                DataContext = h.Vm, Content = main, Width = 900, Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false,
            };
            window.Resources.MergedDictionaries.Add(dict);
            window.Show();
            window.UpdateLayout();
            try
            {
                var buttonB = Descendants<System.Windows.Controls.Button>(main).First(b => ReferenceEquals(b.DataContext, gameB));
                var menu = buttonB.ContextMenu!;
                menu.PlacementTarget = buttonB;
                menu.IsOpen = true;
                Pump();

                var identify = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Identify Game...");
                var choose = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Choose Cover from Catalog...");
                Assert.Same(h.Vm.IdentifyGameCommand, identify.Command);
                Assert.Same(gameB, identify.CommandParameter);                   // the CLICKED card, not the first or last
                Assert.Same(h.Vm.ChooseCatalogCoverCommand, choose.Command);
                Assert.Same(gameB, choose.CommandParameter);

                identify.Command.Execute(identify.CommandParameter);
                choose.Command.Execute(choose.CommandParameter);
                Assert.Equal(new[] { ("manual-b", false), ("manual-b", true) }, opened);

                // The badge: hidden for a normal card, visible once identification could not settle the game.
                Border BadgeOf(GameEntry g)
                {
                    var button = Descendants<System.Windows.Controls.Button>(main).First(b => ReferenceEquals(b.DataContext, g));
                    return Descendants<Border>(button).Single(b => b.ToolTip is string && ((Binding?)BindingOperations.GetBinding(b, Border.ToolTipProperty))?.Path.Path == "IdentityBadgeText");
                }

                Assert.NotEqual(Visibility.Visible, BadgeOf(gameA).Visibility);
                gameA.IdentityBadgeText = "No confident match found.";
                gameA.NeedsIdentity = true;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, BadgeOf(gameA).Visibility);
                Assert.Equal("No confident match found.", BadgeOf(gameA).ToolTip);
                Assert.NotEqual(Visibility.Visible, BadgeOf(gameB).Visibility); // per card, not global
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }
}
