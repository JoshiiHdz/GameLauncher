using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The on-screen keyboard the pad types with: its layout, what its keys do to a real text box (and to the text box's binding), how the pad's own buttons drive it, and the
/// whole thing in a real window - a game's notes, and the search palette.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerKeyboardTests(WpfStaFixture sta)
{
    // ---- the layout ------------------------------------------------------------------------------------------------------

    private static string Labels(IReadOnlyList<KeySpec> row) => string.Concat(row.Where(k => k.Kind == KeyKind.Char).Select(k => k.Text));

    [Fact]
    public void TheLetterLayers_HaveDigitsLettersAndABottomRow_WithCapitalsOnTheUpperLayer()
    {
        var lower = KeyboardLayout.Rows(KeyboardLayer.Lower, acceptsReturn: false);
        var upper = KeyboardLayout.Rows(KeyboardLayer.Upper, acceptsReturn: false);

        Assert.Equal(5, lower.Count);
        Assert.Equal("1234567890", Labels(lower[0]));
        Assert.Equal("qwertyuiop", Labels(lower[1]));
        Assert.Equal("asdfghjkl'", Labels(lower[2]));
        Assert.Equal("zxcvbnm,.?", Labels(lower[3]));
        Assert.Equal("QWERTYUIOP", Labels(upper[1]));
        Assert.Equal("ASDFGHJKL'", Labels(upper[2]));
        Assert.Equal("ZXCVBNM,.?", Labels(upper[3]));

        Assert.Contains(lower[0], k => k.Kind == KeyKind.Backspace);
        Assert.Contains(lower[3], k => k.Kind == KeyKind.Shift);
        Assert.Contains(lower[4], k => k.Kind == KeyKind.Space);
        Assert.Contains(lower[4], k => k.Kind == KeyKind.Done);
        Assert.Contains(lower[4], k => k.Kind == KeyKind.Clear);
        Assert.Contains(lower[4], k => k.Kind == KeyKind.CaretLeft);
        Assert.Contains(lower[4], k => k.Kind == KeyKind.CaretRight);
    }

    [Fact]
    public void TheSymbolsLayer_HasTheCommonSymbols_AndWayBackToTheLetters()
    {
        var rows = KeyboardLayout.Rows(KeyboardLayer.Symbols, acceptsReturn: false);
        var all = string.Concat(rows.Select(Labels));

        foreach (var symbol in "@#$%&*()-_=+[]{}:;/\\|<>\"~`!")
            Assert.Contains(symbol, all);

        Assert.Contains(rows[^1], k => k is { Kind: KeyKind.Symbols, Label: "abc" });
        Assert.Contains(rows[3], k => k is { Kind: KeyKind.Symbols, Label: "ABC" });
    }

    [Fact]
    public void AnEnterKey_ExistsOnlyForATextBoxThatTakesSeveralLines()
    {
        Assert.DoesNotContain(KeyboardLayout.Rows(KeyboardLayer.Lower, acceptsReturn: false)[^1], k => k.Kind == KeyKind.Enter);
        Assert.Contains(KeyboardLayout.Rows(KeyboardLayer.Lower, acceptsReturn: true)[^1], k => k.Kind == KeyKind.Enter);
    }

    // ---- typing into a real text box ---------------------------------------------------------------------------------------

    private sealed class Note
    {
        public string Text { get; set; } = string.Empty;
    }

    private static (OnScreenKeyboard Keyboard, TextBox Box, Note Source) Open(bool acceptsReturn = false, string text = "")
    {
        var source = new Note { Text = text };
        var box = new TextBox { AcceptsReturn = acceptsReturn };
        // A binding that only updates its source when the box loses focus: what the notes box and the others use, and what the keyboard has to work with.
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Note.Text)) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        var keyboard = new OnScreenKeyboard();
        keyboard.Open(box);
        return (keyboard, box, source);
    }

    private static void Tap(OnScreenKeyboard keyboard, string label) =>
        keyboard.Press((KeySpec)keyboard.KeyLabelled(label)!.Tag);

    [Fact]
    public void TypingAndTheBinding_ReachTheTextBoxAndItsSourceAtOnce() => sta.RunAsync(async () =>
    {
        var (keyboard, box, source) = Open();
        Assert.True(keyboard.IsOpen);

        Tap(keyboard, "h");
        Tap(keyboard, "i");

        Assert.Equal("hi", box.Text);
        Assert.Equal("hi", source.Text); // not only when the box loses focus
        await Task.CompletedTask;
    });

    [Fact]
    public void ACharacter_IsInsertedAtTheCursor_AndReplacesWhatIsSelected() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open(text: "helo");
        box.SelectionStart = 3; // between "hel" and "o"
        Tap(keyboard, "l");
        Assert.Equal("hello", box.Text);
        Assert.Equal(4, box.SelectionStart);

        box.SelectionStart = 0;
        box.SelectionLength = 5;
        Tap(keyboard, "x");
        Assert.Equal("x", box.Text); // the selection was replaced
        await Task.CompletedTask;
    });

    [Fact]
    public void Backspace_RemovesTheCharacterBeforeTheCursor_OrTheSelection_AndStopsAtTheStart() => sta.RunAsync(async () =>
    {
        var (keyboard, box, source) = Open(text: "abcd");
        box.SelectionStart = 4;
        keyboard.Perform(PadKeyboardAction.Backspace);
        Assert.Equal("abc", box.Text);
        Assert.Equal("abc", source.Text);

        box.SelectionStart = 1;
        box.SelectionLength = 2; // "bc"
        keyboard.Perform(PadKeyboardAction.Backspace);
        Assert.Equal("a", box.Text);

        box.SelectionStart = 0;
        keyboard.Perform(PadKeyboardAction.Backspace); // nothing before it
        Assert.Equal("a", box.Text);
        await Task.CompletedTask;
    });

    [Fact]
    public void Backspace_TakesAWholeLineBreak_AndEnterMakesOne_OnlyWhereLinesAreAllowed() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open(acceptsReturn: true);
        Tap(keyboard, "a");
        Tap(keyboard, "Enter");
        Tap(keyboard, "b");
        Assert.Equal("a" + Environment.NewLine + "b", box.Text);

        keyboard.Perform(PadKeyboardAction.Backspace); // the b
        keyboard.Perform(PadKeyboardAction.Backspace); // the whole break, not half of it
        Assert.Equal("a", box.Text);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheCursorKeys_MoveTheCursor_WithinTheText_AndASpaceAndClearWork() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open(text: "ab");
        box.SelectionStart = 2;

        keyboard.Perform(PadKeyboardAction.CaretLeft);
        keyboard.Perform(PadKeyboardAction.CaretLeft);
        keyboard.Perform(PadKeyboardAction.CaretLeft); // already at the start
        Assert.Equal(0, box.SelectionStart);

        keyboard.Perform(PadKeyboardAction.CaretRight);
        keyboard.Perform(PadKeyboardAction.Space);
        Assert.Equal("a b", box.Text);

        keyboard.Perform(PadKeyboardAction.Clear);
        Assert.Equal(string.Empty, box.Text);
        await Task.CompletedTask;
    });

    [Fact]
    public void Shift_IsOneCapital_ThenCapsLock_ThenOff() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open();

        keyboard.Perform(PadKeyboardAction.Shift);
        Assert.Equal(KeyboardLayer.Upper, keyboard.Layer);
        Tap(keyboard, "A");
        Assert.Equal("A", box.Text);
        Assert.Equal(KeyboardLayer.Lower, keyboard.Layer); // one capital, then small letters again

        keyboard.Perform(PadKeyboardAction.Shift);
        keyboard.Perform(PadKeyboardAction.Shift); // twice: caps lock
        Assert.True(keyboard.CapsLock);
        Tap(keyboard, "B");
        Tap(keyboard, "C");
        Assert.Equal("ABC", box.Text);
        Assert.Equal(KeyboardLayer.Upper, keyboard.Layer);

        keyboard.Perform(PadKeyboardAction.Shift); // a third: off
        Assert.False(keyboard.CapsLock);
        Assert.Equal(KeyboardLayer.Lower, keyboard.Layer);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheSymbolsLayer_TypesSymbols_AndComesBack() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open();
        keyboard.Perform(PadKeyboardAction.Symbols);
        Assert.Equal(KeyboardLayer.Symbols, keyboard.Layer);

        Tap(keyboard, "@");
        Tap(keyboard, "7");
        Assert.Equal("@7", box.Text);

        keyboard.Perform(PadKeyboardAction.Symbols);
        Assert.Equal(KeyboardLayer.Lower, keyboard.Layer);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheKeys_AreButtons_SoAClickTypesToo() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open();
        keyboard.KeyLabelled("h")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("h", box.Text);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheMaxLength_OfATextBox_IsRespected() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open();
        box.MaxLength = 3;
        foreach (var key in "abcdef")
            Tap(keyboard, key.ToString());

        Assert.Equal("abc", box.Text);
        await Task.CompletedTask;
    });

    [Fact]
    public void Done_PutsTheKeyboardAway_AndTheText_IsKept() => sta.RunAsync(async () =>
    {
        var (keyboard, box, source) = Open();
        var closed = 0;
        keyboard.Closed += () => closed++;
        Tap(keyboard, "o");
        Tap(keyboard, "k");

        keyboard.Perform(PadKeyboardAction.Done);

        Assert.False(keyboard.IsOpen);
        Assert.Null(keyboard.Target);
        Assert.Equal(1, closed);
        Assert.Equal("ok", box.Text);
        Assert.Equal("ok", source.Text);
        Assert.Equal(Visibility.Collapsed, keyboard.Visibility);
        await Task.CompletedTask;
    });

    [Fact]
    public void ThePreview_ShowsWhatIsTyped_WithTheCursor_OrAPromptWhenEmpty() => sta.RunAsync(async () =>
    {
        var (keyboard, box, _) = Open();
        var preview = (TextBlock)keyboard.FindName("Preview");
        Assert.Contains("Type", preview.Text);

        Tap(keyboard, "h");
        Tap(keyboard, "i");
        Assert.Equal("hi│", preview.Text);

        box.SelectionStart = 1;
        Assert.Equal("h│i", preview.Text);
        await Task.CompletedTask;
    });

    // ---- the pad's own buttons, through the router ---------------------------------------------------------------------------

    private sealed class FakeSurface : IControllerSurface
    {
        public readonly List<string> Calls = new();
        public readonly List<PadKeyboardAction> Typed = new();
        public bool KeyboardIsOpen = true;
        public bool PaletteIsShowing;

        public bool GameTileHasFocus => false;
        public bool HeroHasFocus => false;
        public bool TopBarHasFocus => false;
        public bool KeyboardOpen => KeyboardIsOpen;
        public void KeyboardAction(PadKeyboardAction action) => Typed.Add(action);
        public void CloseKeyboard() { Calls.Add("close keyboard"); KeyboardIsOpen = false; }
        public bool TypeIntoPalette() { Calls.Add("type into palette"); return PaletteIsShowing; }
        public bool FocusTopBar() => true;
        public bool TryFocusGameTile() => true;
        public bool TryFocusLetterRail() => false;
        public bool MoveFocus(ShellDirection direction) { Calls.Add("move " + direction); return true; }
        public bool Activate() { Calls.Add("activate"); return true; }
        public void FocusGameTile() => Calls.Add("focus tile");
        public bool FocusPlay() => true;
        public bool EnsureFocus() => false;
        public void Scroll(int pages) => Calls.Add("scroll " + pages);
        public void CloseModal() => Calls.Add("close modal");
        public Rect FocusedAnchor() => new(0, 0, 1, 1);
    }

    private (LibraryViewModel Library, ShellState Shell, FakeSurface Surface, ControllerRouter Router) CreateRouter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Keyboard-" + Guid.NewGuid());
        var library = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        library.AppearanceTheme = ThemeId.ConsoleRibbon;
        var shell = new ShellState(library);
        var surface = new FakeSurface();
        library.EnterControllerMode();
        return (library, shell, surface, new ControllerRouter(library, shell, surface));
    }

    [Fact]
    public void WhileTheKeyboardIsUp_ThePadsButtonsAreTypingShortcuts_NotTheirUsualJobs() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (library, shell, surface, router) = CreateRouter();
            library.ControllerMap.Set(PadButton.X, PadFunction.Optimize); // even a remapped X is Backspace here
            var optimizeOpened = 0;
            library.OptimizeDialogForTest = _ => optimizeOpened++;

            foreach (var button in new[] { PadButton.X, PadButton.Y, PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT, PadButton.Start })
                router.Handle(button);

            Assert.Equal(
            [
                PadKeyboardAction.Backspace, PadKeyboardAction.Space, PadKeyboardAction.CaretLeft, PadKeyboardAction.CaretRight,
                PadKeyboardAction.Shift, PadKeyboardAction.Symbols, PadKeyboardAction.Done,
            ], surface.Typed);
            Assert.Equal(0, optimizeOpened);
            Assert.False(library.IsPageOpen);
            Assert.Equal(ShellTab.Home, shell.Tab);
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void WhileTheKeyboardIsUp_TheDpadMovesOverTheKeys_AIsPress_AndBPutsItAway() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (_, _, surface, router) = CreateRouter();

            router.Handle(PadButton.Right);
            router.Handle(PadButton.Down);
            router.Handle(PadButton.Accept);
            Assert.Equal(["move Right", "move Down", "activate"], surface.Calls);

            router.Handle(PadButton.Back);
            Assert.Equal("close keyboard", surface.Calls[^1]);
            Assert.False(surface.KeyboardIsOpen);
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void InTheSearchPalette_XBringsTheKeyboardBack() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (_, shell, surface, router) = CreateRouter();
            surface.KeyboardIsOpen = false;
            surface.PaletteIsShowing = true;
            shell.ModalOpen = true;

            router.Handle(PadButton.X);

            Assert.Equal(["type into palette"], surface.Calls); // handled: no other job ran
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void TheHints_SayHowToTypeInThePalette_AndStepAsideForTheKeyboard() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (library, shell, _, _) = CreateRouter();
            library.SetControllerStatus(true, null);

            shell.ModalOpen = true;
            shell.PaletteOpen = true;
            Assert.Contains("X  Type", shell.PadHints);

            shell.KeyboardOpen = true; // the keyboard shows its own help
            Assert.Equal(string.Empty, shell.PadHints);
            Assert.False(shell.HasPadHints);

            Assert.Contains("Start  Done", KeyboardLayout.Help);
            Assert.Contains("X  Backspace", KeyboardLayout.Help);
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    // ---- in a real window ----------------------------------------------------------------------------------------------------

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static async Task<(LibraryViewModel Vm, MainWindow Window)> OpenInMode()
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-KeyboardWindow-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        var window = ShellTestSupport.OpenMain(vm, 1280, 900);
        window.Activate();
        await ShellTestSupport.SettleAsync();
        vm.EnterControllerMode();
        await ShellTestSupport.SettleAsync();
        return (vm, window);
    }

    private static async Task Press(MainWindow window, PadButton button)
    {
        window.ControllerRouterForTest.Handle(button);
        await ShellTestSupport.SettleAsync();
    }

    /// <summary>Puts the pad on the key with this label and presses A.</summary>
    private static async Task TypeKey(MainWindow window, string label)
    {
        var key = window.PadKeyboard.KeyLabelled(label) ?? throw new InvalidOperationException($"no key '{label}'");
        Keyboard.Focus(key);
        await Press(window, PadButton.Accept);
        await ShellTestSupport.SettleAsync();
    }

    [Fact]
    public void AOnAGamesNotes_BringsUpTheKeyboard_TypesIntoThem_AndSavesTheNote() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode();
        try
        {
            vm.ShowGameDetailsCommand.Execute(vm.Games.First());
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<GameDetailsPage>().Single();
            var notes = page.NotesBoxRibbon;
            var details = Assert.IsType<GameDetailsViewModel>(page.DataContext);

            Keyboard.Focus(notes); // the pad is on the notes box
            await Press(window, PadButton.Accept);

            Assert.True(window.PadKeyboard.IsOpen);
            Assert.Same(notes, window.PadKeyboard.Target);
            Assert.True(Keyboard.FocusedElement is Button b && Walk(window.PadKeyboard).Contains(b), "the pad should start on a key");

            await TypeKey(window, "h");
            await TypeKey(window, "i");
            await Press(window, PadButton.Y);      // a space
            await TypeKey(window, "t");
            await Press(window, PadButton.X);      // backspace: the t is gone
            Assert.Equal("hi ", notes.Text);

            await Press(window, PadButton.Start);  // done
            Assert.False(window.PadKeyboard.IsOpen);
            Assert.Same(notes, Keyboard.FocusedElement);   // back on the notes box
            Assert.Equal("hi ", details.Notes);            // and the note itself has it
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheKeyboardHoldsThePad_EveryKeyIsReachable_AndTheFocusNeverLeavesIt() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode();
        try
        {
            vm.ShowGameDetailsCommand.Execute(vm.Games.First());
            await ShellTestSupport.SettleAsync();
            var notes = Walk(window.PageHost).OfType<GameDetailsPage>().Single().NotesBoxRibbon;
            Keyboard.Focus(notes);
            await Press(window, PadButton.Accept);
            window.UpdateLayout();

            var keys = Walk(window.PadKeyboard).OfType<Button>().ToList();
            Assert.True(keys.Count > 40);

            // Every key can be reached from the first by the D-pad alone.
            var reachable = FocusNavigator.Reachable(window.PadKeyboard, null, false);
            Assert.Equal(keys.Count, reachable.Count);
            var seen = new HashSet<FrameworkElement> { reachable[0] };
            var queue = new Queue<FrameworkElement>(seen);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var direction in new[] { ShellDirection.Up, ShellDirection.Down, ShellDirection.Left, ShellDirection.Right })
                {
                    if (FocusNavigator.Next(current, reachable, direction, window.RootGrid) is { } next && seen.Add(next))
                        queue.Enqueue(next);
                }
            }

            Assert.Equal(keys.Count, seen.Count);

            // And pressing every direction many times never takes the pad out of the keyboard.
            foreach (var direction in new[] { PadButton.Down, PadButton.Right, PadButton.Up, PadButton.Left })
            {
                for (var i = 0; i < 14; i++)
                {
                    await Press(window, direction);
                    Assert.True(Keyboard.FocusedElement is DependencyObject d && FocusNavigator.IsWithin(d, window.PadKeyboard), $"left the keyboard going {direction}");
                }
            }

            await Press(window, PadButton.Back);   // B puts it away
            Assert.False(window.PadKeyboard.IsOpen);
            Assert.Same(notes, Keyboard.FocusedElement);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ShiftAndTheSymbolsKey_KeepThePadOnTheKeyThatWasPressed() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode();
        try
        {
            vm.ShowGameDetailsCommand.Execute(vm.Games.First());
            await ShellTestSupport.SettleAsync();
            Keyboard.Focus(Walk(window.PageHost).OfType<GameDetailsPage>().Single().NotesBoxRibbon);
            await Press(window, PadButton.Accept);

            await Press(window, PadButton.LT); // Shift: the keys are redrawn as capitals
            Assert.Equal(KeyboardLayer.Upper, window.PadKeyboard.Layer);
            Assert.True(Keyboard.FocusedElement is Button { Tag: KeySpec { Kind: KeyKind.Shift } } || Keyboard.FocusedElement is Button, "the pad is still on a key after the keys were redrawn");

            Keyboard.Focus(window.PadKeyboard.KeyLabelled("123"));
            await Press(window, PadButton.Accept); // pressing the 123 key itself
            Assert.Equal(KeyboardLayer.Symbols, window.PadKeyboard.Layer);
            Assert.True(Keyboard.FocusedElement is Button { Tag: KeySpec { Kind: KeyKind.Symbols } }, "the pad should be on the key that switches back (abc)");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheSearchPalette_OpensWithTheKeyboard_NarrowsAsYouType_AndTheGameIsChosenWithA() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode();
        try
        {
            var model = new CommandPaletteViewModel(vm.BuildPaletteItems());
            var dialog = new CommandPaletteDialog(model);
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Search", dialog);
            await ShellTestSupport.SettleAsync();

            Assert.True(window.PadKeyboard.IsOpen, "the palette is no use without typing: the keyboard comes up with it");
            Assert.Same(dialog.QueryBox, window.PadKeyboard.Target);

            await TypeKey(window, "b");
            await TypeKey(window, "o");
            await TypeKey(window, "r");
            Assert.Equal("bor", model.Query);                       // live: the search narrows as the keys are pressed
            Assert.Single(model.Results);
            Assert.Equal("Borderlands", model.Results[0].Title);

            await Press(window, PadButton.Start);                    // Done: the keyboard goes, the results stay
            Assert.False(window.PadKeyboard.IsOpen);
            Assert.True(window.IsModalOpen);

            await Press(window, PadButton.X);                        // X brings it back
            Assert.True(window.PadKeyboard.IsOpen);
            await Press(window, PadButton.Back);                     // B puts it away; the palette is still there
            Assert.False(window.PadKeyboard.IsOpen);
            Assert.True(window.IsModalOpen);

            await Press(window, PadButton.Accept);                   // A chooses the highlighted game
            Assert.NotNull(model.Chosen);
            Assert.Equal("Borderlands", model.Chosen!.Title);
            await shown.Operation;
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheKeyboardIsPutAway_WhenItsDialogCloses_OrControllerModeEnds() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode();
        try
        {
            var model = new CommandPaletteViewModel(vm.BuildPaletteItems());
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Search", new CommandPaletteDialog(model));
            await ShellTestSupport.SettleAsync();
            Assert.True(window.PadKeyboard.IsOpen);

            await shown.CloseAsync(); // the dialog goes (the mouse, say): the keyboard cannot stay for a text box that is gone
            await ShellTestSupport.SettleAsync();
            Assert.False(window.PadKeyboard.IsOpen);

            vm.ShowGameDetailsCommand.Execute(vm.Games.First());
            await ShellTestSupport.SettleAsync();
            Keyboard.Focus(Walk(window.PageHost).OfType<GameDetailsPage>().Single().NotesBoxRibbon);
            await Press(window, PadButton.Accept);
            Assert.True(window.PadKeyboard.IsOpen);

            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.False(window.PadKeyboard.IsOpen); // leaving controller mode puts it away
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
