using System.Windows;
using GameLauncher.Models;

namespace GameLauncher.Behaviors;

/// <summary>Tells every card in a console shell which game has focus, and in which row (a shelf or the grid). The shell sets
/// <c>Game</c> and <c>FocusedRow</c> once on its root and each shelf sets <c>Row</c> on itself; all three are inherited down the tree, so a
/// card compares them to its own game and row (no per-card wiring, no lookup from game to card). The row matters because one game can
/// appear in two shelves, and only the one the focus is actually in should light up.</summary>
public static class ShellFocus
{
    public static readonly DependencyProperty GameProperty = DependencyProperty.RegisterAttached(
        "Game", typeof(GameEntry), typeof(ShellFocus),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty FocusedRowProperty = DependencyProperty.RegisterAttached(
        "FocusedRow", typeof(int), typeof(ShellFocus),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row", typeof(int), typeof(ShellFocus),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));

    public static GameEntry? GetGame(DependencyObject d) => (GameEntry?)d.GetValue(GameProperty);

    public static void SetGame(DependencyObject d, GameEntry? value) => d.SetValue(GameProperty, value);

    public static int GetFocusedRow(DependencyObject d) => (int)d.GetValue(FocusedRowProperty);

    public static void SetFocusedRow(DependencyObject d, int value) => d.SetValue(FocusedRowProperty, value);

    public static int GetRow(DependencyObject d) => (int)d.GetValue(RowProperty);

    public static void SetRow(DependencyObject d, int value) => d.SetValue(RowProperty, value);
}
