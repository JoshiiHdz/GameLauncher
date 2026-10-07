using System.Windows;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>What the router needs from the window, which owns the real focus: keyboard focus moves through WPF, and the router decides what a
/// button means. A test supplies a fake.</summary>
public interface IControllerSurface
{
    /// <summary>Keyboard focus is on a game's tile (a ribbon item or a Library poster), the part of the screen the shell state moves itself.</summary>
    bool GameTileHasFocus { get; }

    /// <summary>Moves keyboard focus to the nearest focusable control in that direction (hero buttons, cards, settings rows...); false if there is none.</summary>
    bool MoveFocus(ShellDirection direction);

    /// <summary>Presses the control that has keyboard focus; false when nothing is focused.</summary>
    bool Activate();

    /// <summary>Puts keyboard focus on the shell's focused game (the ribbon item, or the Library poster) and scrolls it into view.</summary>
    void FocusGameTile();

    /// <summary>The focus is on one of the hero's buttons (Play, More, the favorite star, Uninstall) on the home screen.</summary>
    bool HeroHasFocus { get; }

    /// <summary>The focus is on one of the top bar's controls (the Home and Library tabs, Search, the controller button, Settings, Power).</summary>
    bool TopBarHasFocus { get; }

    /// <summary>Puts keyboard focus on the top bar's tab for the screen showing (Home or Library); false when there is none.</summary>
    bool FocusTopBar();

    /// <summary>Puts keyboard focus on the focused game's tile right now (not after layout, as <see cref="FocusGameTile"/> does); false when there is no such tile on screen.</summary>
    bool TryFocusGameTile();

    /// <summary>Puts keyboard focus on the Library tab's A to Z rail, on the letter the focused game is under; false when there is no rail.</summary>
    bool TryFocusLetterRail();

    /// <summary>The on-screen keyboard is up (it has the pad until Done or B).</summary>
    bool KeyboardOpen { get; }

    /// <summary>One of the pad's own buttons used on the keyboard: Backspace, a space, the cursor, Shift, 123, Clear, Done.</summary>
    void KeyboardAction(PadKeyboardAction action);

    void CloseKeyboard();

    /// <summary>Brings the keyboard up on the search palette's text box; false when the palette is not what is showing.</summary>
    bool TypeIntoPalette();

    /// <summary>Puts keyboard focus on the hero's Play button (the home screen's big button for the focused game); false when there is none to focus.</summary>
    bool FocusPlay();

    /// <summary>If nothing useful has keyboard focus (the pad was just picked up), focuses the shell's focused game and returns true, so that first press is only
    /// "show me where I am".</summary>
    bool EnsureFocus();

    /// <summary>Scrolls the page under the focus by a page (-1 up, 1 down).</summary>
    void Scroll(int pages);

    void CloseModal();

    /// <summary>Where the focused control is, for a menu that opens beside it.</summary>
    Rect FocusedAnchor();
}

/// <summary>Turns controller buttons into what the PlayStation theme already does for a mouse: it calls the shell state's own focus and menu logic and
/// the library's own commands, and leaves anything that is plain keyboard focus (buttons, switches, list rows on a page) to WPF through
/// <see cref="IControllerSurface"/>. There is no second copy of any behaviour here. A and B (choose, back) and the D-pad never change; every other button does
/// the job <see cref="ControllerMap"/> gives it.</summary>
public sealed class ControllerRouter(LibraryViewModel library, ShellState shell, IControllerSurface surface)
{
    /// <summary>Back was pressed on the top level (Home, nothing open): the window asks whether to leave controller mode.</summary>
    public event Action? LeaveRequested;

    /// <summary>Start was pressed in Axis: the window asks whether to switch to controller mode, and enters it on a yes. Without a listener Start enters at once.</summary>
    public event Action? EnterRequested;

    /// <summary>The window sets this while the "Switch to controller mode?" question is showing, so the pad can answer it.</summary>
    public bool AskingToEnter { get; set; }

    public void Handle(PadButton button)
    {
        if (!library.IsControllerMode)
        {
            // The "Switch to controller mode?" question is open (Axis): the pad answers it, like the "Leave controller mode?" one.
            if (AskingToEnter && shell.ModalOpen)
            {
                if (button == PadButton.Back)
                    surface.CloseModal();
                else if (button != PadButton.Start)
                    HandleGeneric(button, PadFunction.Nothing);

                return;
            }

            // Start is the way in, whatever it is mapped to once the mode is on; nothing else does anything until then. Not while a dialog or menu is open: Start must not
            // pull the screen out from under one. From Axis it asks first (the window shows the question): the theme changes for the session, and an accidental press
            // should not do that. In the PlayStation theme it is the mode's own switch, so it goes straight in.
            if (button == PadButton.Start && !shell.ModalOpen && !shell.IsMenuOpen)
            {
                if (library.IsAxisTheme && EnterRequested is { } ask)
                    ask();
                else
                    library.EnterControllerMode();
            }

            return;
        }

        // The on-screen keyboard has the pad until it is put away: its keys are moved over and pressed like any buttons, and the pad's own buttons are typing shortcuts.
        // (They are not the remappable jobs here: Backspace has to be Backspace.)
        if (surface.KeyboardOpen)
        {
            HandleKeyboard(button);
            return;
        }

        var function = ControllerMap.Remappable.Contains(button) ? library.ControllerMap.Resolve(button) : PadFunction.Nothing;

        if (shell.IsMenuOpen)
        {
            HandleMenu(button, function);
            return;
        }

        if (shell.ModalOpen || shell.NavOpen)
        {
            if (button == PadButton.X && surface.TypeIntoPalette())
                return; // in the search palette X brings the keyboard back (it comes up by itself when the palette opens)

            if (button == PadButton.Back)
            {
                if (shell.ModalOpen)
                    surface.CloseModal();
                else
                    shell.NavOpen = false;
            }
            else
            {
                HandleGeneric(button, function);
            }

            return;
        }

        if (library.IsPageOpen)
        {
            if (button == PadButton.Back)
            {
                library.ClosePage();
                surface.FocusGameTile(); // back on the home (or Library) screen, the pad is on the focused game again
                return;
            }

            if (!HandleGeneric(button, function))
                RunJob(function);

            return;
        }

        HandleHome(button, function);
    }

    private void HandleKeyboard(PadButton button)
    {
        switch (button)
        {
            case PadButton.Back:
                surface.CloseKeyboard();
                break;
            case PadButton.X:
                surface.KeyboardAction(PadKeyboardAction.Backspace);
                break;
            case PadButton.Y:
                surface.KeyboardAction(PadKeyboardAction.Space);
                break;
            case PadButton.LB:
                surface.KeyboardAction(PadKeyboardAction.CaretLeft);
                break;
            case PadButton.RB:
                surface.KeyboardAction(PadKeyboardAction.CaretRight);
                break;
            case PadButton.LT:
                surface.KeyboardAction(PadKeyboardAction.Shift);
                break;
            case PadButton.RT:
                surface.KeyboardAction(PadKeyboardAction.Symbols);
                break;
            case PadButton.Start:
                surface.KeyboardAction(PadKeyboardAction.Done);
                break;
            default:
                HandleGeneric(button, PadFunction.Nothing); // moving over the keys, and A pressing the one that has the pad
                break;
        }
    }

    private void HandleMenu(PadButton button, PadFunction function)
    {
        if (button == PadButton.Up)
            shell.MoveMenuSelection(-1);
        else if (button == PadButton.Down)
            shell.MoveMenuSelection(1);
        else if (button == PadButton.Accept)
            shell.RunActiveMenuItem();
        else if (button == PadButton.Back || function == PadFunction.GameMenu)
            shell.CloseMenu(); // the button that opens the game's menu also closes it
    }

    /// <summary>Directions, A and the paging triggers: the same on every screen. Returns false when the button is neither (so the caller can try it as a job).</summary>
    private bool HandleGeneric(PadButton button, PadFunction function)
    {
        switch (button)
        {
            case PadButton.Up:
                Move(ShellDirection.Up);
                return true;
            case PadButton.Down:
                Move(ShellDirection.Down);
                return true;
            case PadButton.Left:
                Move(ShellDirection.Left);
                return true;
            case PadButton.Right:
                Move(ShellDirection.Right);
                return true;
            case PadButton.Accept:
                if (!surface.EnsureFocus())
                    surface.Activate();

                return true;
        }

        if (function == PadFunction.PageUp)
        {
            Page(-1);
            return true;
        }

        if (function == PadFunction.PageDown)
        {
            Page(1);
            return true;
        }

        return false;
    }

    /// <summary>How many rows of games a page button moves in the Library grid.</summary>
    internal const int LibraryPageRows = 3;

    /// <summary>A page up or down. In the Library grid it moves the focus a page of rows (scrolling the grid alone would leave the focus behind, and the next press would snap the
    /// view back to it); anywhere else it scrolls the page under the pad.</summary>
    private void Page(int pages)
    {
        if (!library.IsPageOpen && !shell.ModalOpen && !shell.NavOpen && shell.Tab == ShellTab.Library && surface.GameTileHasFocus)
        {
            for (var row = 0; row < LibraryPageRows; row++)
            {
                if (!shell.MoveFocus(pages < 0 ? ShellDirection.Up : ShellDirection.Down))
                    break;
            }

            surface.FocusGameTile();
            return;
        }

        surface.Scroll(pages);
    }

    /// <summary>On a game's tile the shell state moves the focus (ribbon items, Library grid rows); where it has nowhere to go, or the focus is on any other control,
    /// WPF moves it. Down from any tile on the home screen always lands on the Play button, whichever game it is, and Down from the first Library row lands on the
    /// controls beside it.</summary>
    private void Move(ShellDirection direction)
    {
        if (surface.EnsureFocus())
            return;

        if (surface.GameTileHasFocus && shell.MoveFocus(direction))
        {
            surface.FocusGameTile();
            return;
        }

        var onHome = shell.Tab == ShellTab.Home && !library.IsPageOpen && !shell.ModalOpen;
        var onHomeOrLibrary = !library.IsPageOpen && !shell.ModalOpen;
        if (direction == ShellDirection.Down && surface.GameTileHasFocus && onHome && surface.FocusPlay())
            return;

        // The top bar and the strip (or the Library grid) are one above the other: Up from the top of the strip is the tab for this screen, and Down from any top bar control
        // is the focused game's tile - not whatever lies furthest below that control's own column.
        if (direction == ShellDirection.Up && surface.GameTileHasFocus && onHomeOrLibrary && surface.FocusTopBar())
            return;

        if (direction == ShellDirection.Down && surface.TopBarHasFocus && onHomeOrLibrary && surface.TryFocusGameTile())
            return;

        // At the right-hand end of the Library grid the A to Z rail is next, on the letter the focused game is under (whatever height the rail's letters are at).
        if (direction == ShellDirection.Right && surface.GameTileHasFocus && shell.Tab == ShellTab.Library && onHomeOrLibrary && surface.TryFocusLetterRail())
            return;

        // And back up from the hero's buttons is always the focused game's tile, so Down then Up returns to where you were.
        if (direction == ShellDirection.Up && surface.HeroHasFocus && onHome)
        {
            surface.FocusGameTile();
            return;
        }

        surface.MoveFocus(direction);
    }

    private void HandleHome(PadButton button, PadFunction function)
    {
        if (button == PadButton.Back)
        {
            if (shell.Tab == ShellTab.Library)
                shell.ShowHomeCommand.Execute(null);
            else if (!surface.GameTileHasFocus)
                surface.FocusGameTile(); // from the hero's buttons, back to the strip first
            else
                LeaveRequested?.Invoke();

            return;
        }

        if (!HandleGeneric(button, function))
            RunJob(function);
    }

    /// <summary>A job a remappable button can have, anywhere outside a menu or a dialog.</summary>
    private void RunJob(PadFunction function)
    {
        switch (function)
        {
            case PadFunction.Settings:
                library.ShowSettingsCommand.Execute(null);
                break;
            case PadFunction.Library:
                shell.ShowLibraryCommand.Execute(null);
                break;
            case PadFunction.Optimize:
                library.ShowOptimizeCommand.Execute(null);
                break;
            case PadFunction.RescanLibrary:
                library.RescanCommand.Execute(null);
                break;
            case PadFunction.Search:
                library.ShowCommandPaletteCommand.Execute(null);
                break;
            case PadFunction.GameMenu:
                if (!library.IsPageOpen && !shell.LibraryTileFocused && shell.HeroGame is { } game)
                {
                    shell.OpenCardMenu(game, surface.FocusedAnchor());
                    shell.MoveMenuSelection(1); // the first row is lit, so A runs it at once
                }

                break;
            case PadFunction.NextTab:
                if (!library.IsPageOpen && shell.Tab == ShellTab.Home)
                    shell.ShowLibraryCommand.Execute(null);

                break;
            case PadFunction.PreviousTab:
                if (!library.IsPageOpen && shell.Tab == ShellTab.Library)
                    shell.ShowHomeCommand.Execute(null);

                break;
        }
    }
}
