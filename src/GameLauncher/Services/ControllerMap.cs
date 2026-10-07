namespace GameLauncher.Services;

/// <summary>What a controller button can do in controller mode.</summary>
public enum PadFunction
{
    Nothing,

    // The jobs a user can give a button (see <see cref="ControllerMap.Choices"/>):
    Settings,
    Library,
    Optimize,
    RescanLibrary,

    // The jobs buttons have out of the box (a user keeps them by leaving a button on "Default"):
    Search,
    GameMenu,
    PreviousTab,
    NextTab,
    PageUp,
    PageDown,
}

/// <summary>Which button does what in controller mode. A and B are fixed (choose and back) and so is the D-pad and the left stick (they move); X, Y, Start, LB, RB, LT
/// and RT can each be given one of a short list of jobs, or left on their default. Only the changes are stored, so a default can change in a later version without
/// overriding anyone's choice, and "reset" simply forgets the changes.</summary>
public sealed class ControllerMap
{
    /// <summary>The buttons that can be remapped.</summary>
    public static IReadOnlyList<PadButton> Remappable { get; } = [PadButton.X, PadButton.Y, PadButton.Start, PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT];

    /// <summary>The jobs a user can choose, besides a button's own default. Deliberately short.</summary>
    public static IReadOnlyList<PadFunction> Choices { get; } =
        [PadFunction.Settings, PadFunction.Library, PadFunction.Optimize, PadFunction.RescanLibrary, PadFunction.Nothing];

    public static PadFunction DefaultFor(PadButton button) => button switch
    {
        PadButton.X => PadFunction.Search,
        PadButton.Y => PadFunction.Settings,
        PadButton.Start => PadFunction.GameMenu,
        PadButton.LB => PadFunction.PreviousTab,
        PadButton.RB => PadFunction.NextTab,
        PadButton.LT => PadFunction.PageUp,
        PadButton.RT => PadFunction.PageDown,
        _ => PadFunction.Nothing,
    };

    private readonly Dictionary<PadButton, PadFunction> _changes = new();

    /// <summary>Raised after any change, so the screen's hints and the settings list can follow.</summary>
    public event Action? Changed;

    /// <summary>What the button does now. A and B, the D-pad and the stick are not mapped (they return Nothing here and are handled as they always are).</summary>
    public PadFunction Resolve(PadButton button) =>
        _changes.TryGetValue(button, out var function) ? function : DefaultFor(button);

    public bool IsDefault(PadButton button) => !_changes.ContainsKey(button);

    /// <summary>Another button already does this job. Two buttons never share a job (Nothing is no job, and any number of buttons can have it).</summary>
    public bool IsTakenByAnother(PadButton button, PadFunction function) =>
        function != PadFunction.Nothing && Remappable.Any(other => other != button && Resolve(other) == function);

    /// <summary>The button that has this job now, or null when none does (or the job is Nothing).</summary>
    public PadButton? HolderOf(PadFunction function) =>
        function == PadFunction.Nothing ? null : Remappable.Where(b => Resolve(b) == function).Select(b => (PadButton?)b).FirstOrDefault();

    /// <summary>What a press of this button cycles through: its default first (if no other button has taken that job), then each choice that is not that same job and not already
    /// on another button. Nothing is always there.</summary>
    public List<PadFunction?> AvailableOptions(PadButton button)
    {
        var options = new List<PadFunction?>();
        if (!IsTakenByAnother(button, DefaultFor(button)))
            options.Add(null); // null = the default

        options.AddRange(Choices.Where(c => c != DefaultFor(button) && !IsTakenByAnother(button, c)).Select(c => (PadFunction?)c));
        return options;
    }

    /// <summary>Gives the button a job; null (or the button's own default job) puts it back on its default. A job another button already has is refused (returns false and nothing
    /// changes), and so are A and B, which cannot be remapped. Returns true when the button now has the job asked for.</summary>
    public bool Set(PadButton button, PadFunction? function)
    {
        if (!Remappable.Contains(button))
            return false;

        var wanted = function ?? DefaultFor(button);
        if (IsTakenByAnother(button, wanted))
            return false;

        var changed = wanted == DefaultFor(button) ? _changes.Remove(button) : Replace(button, wanted);
        if (changed)
            Changed?.Invoke();

        return true;
    }

    private bool Replace(PadButton button, PadFunction function)
    {
        if (_changes.TryGetValue(button, out var current) && current == function)
            return false;

        _changes[button] = function;
        return true;
    }

    /// <summary>Moves a button on to its next option (default, then each choice, then back to the default), skipping any job another button has.</summary>
    public void Cycle(PadButton button)
    {
        var options = AvailableOptions(button);
        if (options.Count == 0)
            return;

        var at = options.IndexOf(_changes.TryGetValue(button, out var current) ? current : null);
        Set(button, options[(Math.Max(at, -1) + 1) % options.Count]);
    }

    /// <summary>Forgets every change: all buttons are back on their defaults.</summary>
    public void Reset()
    {
        if (_changes.Count == 0)
            return;

        _changes.Clear();
        Changed?.Invoke();
    }

    public bool IsAllDefault => _changes.Count == 0;

    /// <summary>The changes as they are saved: button name to job name. Anything not listed is on its default.</summary>
    public Dictionary<string, string> ToSettings() => _changes.ToDictionary(c => c.Key.ToString(), c => c.Value.ToString());

    /// <summary>Loads saved changes; a name that is not a remappable button or a known job is ignored.</summary>
    public void Load(IReadOnlyDictionary<string, string>? saved)
    {
        _changes.Clear();
        if (saved is null)
            return;

        // Read what is valid first; then accept the changes in the order of the buttons on the pad, refusing any job that another button already has (a saved file can have been
        // edited, or written by an older version). Buttons that are not changed keep their default job, which counts as taken too.
        var wanted = new Dictionary<PadButton, PadFunction>();
        foreach (var (buttonName, functionName) in saved)
        {
            if (Enum.TryParse<PadButton>(buttonName, out var button) && Remappable.Contains(button)
                && Enum.TryParse<PadFunction>(functionName, out var function) && Enum.IsDefined(function)
                && (function == PadFunction.Nothing || Choices.Contains(function)) && function != DefaultFor(button))
                wanted[button] = function;
        }

        var taken = Remappable.Where(b => !wanted.ContainsKey(b)).Select(DefaultFor).ToHashSet();
        foreach (var button in Remappable.Where(wanted.ContainsKey))
        {
            var function = wanted[button];
            if (function != PadFunction.Nothing && !taken.Add(function))
                continue; // another button has it

            _changes[button] = function;
        }

        Changed?.Invoke();
    }

    /// <summary>The words for a job, as the settings list and the hints show it.</summary>
    public static string Describe(PadFunction function) => function switch
    {
        PadFunction.Settings => "Settings",
        PadFunction.Library => "Library",
        PadFunction.Optimize => "Optimize",
        PadFunction.RescanLibrary => "Rescan library",
        PadFunction.Search => "Search",
        PadFunction.GameMenu => "Game menu",
        PadFunction.PreviousTab => "Previous tab",
        PadFunction.NextTab => "Next tab",
        PadFunction.PageUp => "Page up",
        PadFunction.PageDown => "Page down",
        _ => "Nothing",
    };

    /// <summary>The button's name as the player sees it on the pad.</summary>
    public static string ButtonName(PadButton button) => button switch
    {
        PadButton.Accept => "A",
        PadButton.Back => "B",
        PadButton.LB => "LB",
        PadButton.RB => "RB",
        PadButton.LT => "LT",
        PadButton.RT => "RT",
        _ => button.ToString(),
    };
}
