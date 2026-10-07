using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GameLauncher.Services;

namespace GameLauncher;

/// <summary>What the pad's own buttons do to the keyboard while it is up (the keyboard's keys are pressed with A like any button).</summary>
public enum PadKeyboardAction { Backspace, Space, CaretLeft, CaretRight, Shift, Symbols, Clear, Done }

/// <summary>The on-screen keyboard (see OnScreenKeyboard.xaml): types into one <see cref="TextBox"/> at its cursor, replacing what is selected. Every change is pushed to the text
/// box's binding at once (a text box that only updates its source when it loses focus would otherwise keep the old text, since the focus is on a key).</summary>
public partial class OnScreenKeyboard : UserControl
{
    private TextBox? _target;
    private KeyboardLayer _layer = KeyboardLayer.Lower;
    private bool _capsLock;

    public OnScreenKeyboard()
    {
        InitializeComponent();
        HelpLine.Items = KeyboardLayout.HelpItems;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The keyboard is up and has a text box to type into.</summary>
    public bool IsOpen => _target is not null && Visibility == Visibility.Visible;

    public TextBox? Target => _target;

    public KeyboardLayer Layer => _layer;

    public bool CapsLock => _capsLock;

    /// <summary>Raised when the keyboard has been put away (Done, B, or its text box going away).</summary>
    public event Action? Closed;

    public void Open(TextBox target)
    {
        if (_target is not null)
            Detach();

        _target = target;
        _layer = KeyboardLayer.Lower;
        _capsLock = false;
        target.TextChanged += OnTargetChanged;
        target.SelectionChanged += OnTargetSelectionChanged;
        Rebuild(null);
        Visibility = Visibility.Visible;
        UpdatePreview();
    }

    public void Close(bool restoreFocus = true)
    {
        if (_target is null)
            return;

        var target = _target;
        PushToBinding(target);
        Detach();
        Visibility = Visibility.Collapsed;
        if (restoreFocus && target.IsVisible)
            Keyboard.Focus(target);

        Closed?.Invoke();
    }

    private void Detach()
    {
        if (_target is null)
            return;

        _target.TextChanged -= OnTargetChanged;
        _target.SelectionChanged -= OnTargetSelectionChanged;
        _target = null;
    }

    private void OnTargetChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnTargetSelectionChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_target is null)
            return;

        if (_target.Text.Length == 0)
        {
            Preview.Text = "Type with the keys below";
            Preview.Opacity = 0.55;
            return;
        }

        var caret = Math.Clamp(_target.SelectionStart, 0, _target.Text.Length);
        var oneLine = _target.Text.Replace("\r\n", " ⏎ ").Replace('\n', ' ');
        // The cursor is shown as a bar in the text; with line breaks turned into marks the index shifts, so it is placed on the original text first.
        var before = _target.Text[..caret].Replace("\r\n", " ⏎ ").Replace('\n', ' ');
        Preview.Text = oneLine.Length >= before.Length ? before + "│" + oneLine[before.Length..] : oneLine + "│";
        Preview.Opacity = 1;
    }

    // ---- Building the keys --------------------------------------------------------------------------------------------------

    private void Rebuild(KeySpec? refocus)
    {
        KeyRows.Children.Clear();
        var rows = KeyboardLayout.Rows(_layer, _target?.AcceptsReturn == true);
        var widest = rows.Max(r => r.Sum(k => k.Width));
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            var spare = (widest - row.Sum(k => k.Width)) / 2; // short rows are centred, so the columns of keys line up from row to row
            if (spare > 0.01)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spare, GridUnitType.Star) });

            foreach (var spec in row)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spec.Width, GridUnitType.Star) });
                var key = MakeKey(spec);
                Grid.SetColumn(key, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(key);
            }

            if (spare > 0.01)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spare, GridUnitType.Star) });

            KeyRows.Children.Add(grid);
        }

        if (refocus is not null)
        {
            UpdateLayout();
            if (KeyFor(refocus) is { } same)
                Keyboard.Focus(same);
        }
    }

    private Button MakeKey(KeySpec spec)
    {
        var shiftOn = spec.Kind == KeyKind.Shift && _layer == KeyboardLayer.Upper;
        var key = new Button
        {
            Content = spec.Kind == KeyKind.Shift && _capsLock ? "⇪" : spec.Label,
            Tag = spec,
            Style = (Style)Resources["KeyStyle"],
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = spec.Kind == KeyKind.Char || spec.Kind == KeyKind.Space ? 18 : 16,
        };
        System.Windows.Automation.AutomationProperties.SetName(key, spec.Kind == KeyKind.Char ? spec.Label : spec.Kind.ToString());

        if (spec.Kind == KeyKind.Done)
            key.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        else if (shiftOn || (spec.Kind == KeyKind.Symbols && _layer == KeyboardLayer.Symbols))
            key.Background = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

        key.Click += (_, _) => Press(spec);
        return key;
    }

    /// <summary>The key that does what <paramref name="spec"/> does, in the keys now showing (the same kind of key, or the same character).</summary>
    private Button? KeyFor(KeySpec spec) =>
        AllKeys().FirstOrDefault(k => k.Tag is KeySpec s && s.Kind == spec.Kind && string.Equals(s.Text, spec.Text, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Button> AllKeys() => KeyRows.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>());

    /// <summary>The key with this label on the showing layer (for tests).</summary>
    internal Button? KeyLabelled(string label) => AllKeys().FirstOrDefault(k => string.Equals(k.Content as string, label, StringComparison.Ordinal));

    /// <summary>Where the pad starts when the keyboard comes up: a letter near the middle, not the corner.</summary>
    internal Button? DefaultKey => KeyLabelled(_layer == KeyboardLayer.Upper ? "G" : "g") ?? AllKeys().FirstOrDefault();

    // ---- Typing -----------------------------------------------------------------------------------------------------------

    /// <summary>A key was pressed (with A, or by the mouse).</summary>
    public void Press(KeySpec spec)
    {
        switch (spec.Kind)
        {
            case KeyKind.Char:
                Insert(spec.Text);
                if (_layer == KeyboardLayer.Upper && !_capsLock)
                    SetLayer(KeyboardLayer.Lower, spec); // one capital, then back to small letters
                break;
            case KeyKind.Space:
                Insert(" ");
                break;
            case KeyKind.Enter:
                Insert(Environment.NewLine);
                break;
            case KeyKind.Backspace:
                Perform(PadKeyboardAction.Backspace);
                break;
            case KeyKind.CaretLeft:
                Perform(PadKeyboardAction.CaretLeft);
                break;
            case KeyKind.CaretRight:
                Perform(PadKeyboardAction.CaretRight);
                break;
            case KeyKind.Clear:
                Perform(PadKeyboardAction.Clear);
                break;
            case KeyKind.Shift:
                Perform(PadKeyboardAction.Shift);
                break;
            case KeyKind.Symbols:
                Perform(PadKeyboardAction.Symbols);
                break;
            case KeyKind.Done:
                Close();
                break;
        }
    }

    /// <summary>What the pad's own buttons do to the keyboard (X is Backspace, Y a space, the shoulders move the cursor, the triggers are Shift and 123, Start is Done).</summary>
    public void Perform(PadKeyboardAction action)
    {
        if (_target is not { } target)
            return;

        switch (action)
        {
            case PadKeyboardAction.Backspace:
                Backspace(target);
                break;
            case PadKeyboardAction.Space:
                Insert(" ");
                break;
            case PadKeyboardAction.CaretLeft:
                MoveCaret(target, -1);
                break;
            case PadKeyboardAction.CaretRight:
                MoveCaret(target, 1);
                break;
            case PadKeyboardAction.Clear:
                target.Clear();
                PushToBinding(target);
                break;
            case PadKeyboardAction.Shift:
                // One tap: capitals for the next letter. Two taps: caps lock. A third: back to small letters.
                if (_layer == KeyboardLayer.Symbols)
                    SetLayer(KeyboardLayer.Upper, null);
                else if (_layer == KeyboardLayer.Lower)
                    SetLayer(KeyboardLayer.Upper, new KeySpec("", KeyKind.Shift));
                else if (!_capsLock)
                {
                    _capsLock = true;
                    SetLayer(KeyboardLayer.Upper, new KeySpec("", KeyKind.Shift));
                }
                else
                {
                    _capsLock = false;
                    SetLayer(KeyboardLayer.Lower, new KeySpec("", KeyKind.Shift));
                }

                break;
            case PadKeyboardAction.Symbols:
                _capsLock = false;
                SetLayer(_layer == KeyboardLayer.Symbols ? KeyboardLayer.Lower : KeyboardLayer.Symbols, new KeySpec("", KeyKind.Symbols));
                break;
            case PadKeyboardAction.Done:
                Close();
                break;
        }
    }

    private void SetLayer(KeyboardLayer layer, KeySpec? refocus)
    {
        _layer = layer;
        Rebuild(refocus);
    }

    private void Insert(string text)
    {
        if (_target is not { } target)
            return;

        // A text box's MaxLength stops typed keys but not text set from code, so the keyboard keeps to it itself.
        if (target.MaxLength > 0)
        {
            var room = target.MaxLength - (target.Text.Length - target.SelectionLength);
            if (room <= 0)
                return;

            if (text.Length > room)
                text = text[..room];
        }

        var start = target.SelectionStart;
        target.SelectedText = text; // replaces what is selected, else inserts at the cursor
        target.SelectionStart = Math.Min(start + text.Length, target.Text.Length);
        target.SelectionLength = 0;
        PushToBinding(target);
    }

    private static void Backspace(TextBox target)
    {
        if (target.SelectionLength > 0)
        {
            var start = target.SelectionStart;
            target.SelectedText = string.Empty;
            target.SelectionStart = start;
        }
        else if (target.SelectionStart > 0)
        {
            var at = target.SelectionStart;
            // A line break is two characters; one Backspace takes both.
            var count = at >= 2 && target.Text[at - 2] == '\r' && target.Text[at - 1] == '\n' ? 2 : 1;
            target.Text = target.Text.Remove(at - count, count);
            target.SelectionStart = at - count;
        }

        target.SelectionLength = 0;
        PushToBinding(target);
    }

    private static void MoveCaret(TextBox target, int step)
    {
        target.SelectionLength = 0;
        target.SelectionStart = Math.Clamp(target.SelectionStart + step, 0, target.Text.Length);
    }

    private static void PushToBinding(TextBox target) =>
        BindingOperations.GetBindingExpression(target, TextBox.TextProperty)?.UpdateSource();
}
