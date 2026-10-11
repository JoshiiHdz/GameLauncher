using System.IO;
using System.Text;
using System.Text.Json;

namespace GameLauncher.Services;

/// <summary>One press of a direction (or any button) in controller mode, with where the pad was and where it went.</summary>
/// <param name="Screen">What was on screen ("Home tab", "page", "dialog", "menu Game"...).</param>
/// <param name="Button">The pad button.</param>
/// <param name="From">The control the pad was on, with its box on screen.</param>
/// <param name="To">The control it is on after the press.</param>
/// <param name="Rule">Which rule moved it: "tile", "down-to-play", "up-to-tile", "hero-game", "spatial", "list"... or "none".</param>
/// <param name="Moved">The focus is on a different control than before.</param>
/// <param name="Stick">The left stick's raw position when it was in use ("x,y"), else null.</param>
public sealed record NavStep(DateTime At, string Screen, string Button, string From, string To, string Rule, bool Moved, string? Stick = null);

/// <summary>What the launcher learns about how well the pad's navigation works. Every press in controller mode is written as a line of JSON (a log that can be sent back and read
/// with any tool), and the same stream is boiled down to a short report: where the pad hit a wall, where people pressed the opposite way straight away (a move they did not
/// mean), the most used moves, and how often the stick was pushed into a corner and ignored. Nothing here changes what navigation does: it is evidence for the next fix.
/// The files live in the logs folder and are capped (<see cref="MaxBytes"/>; the old one is kept as .1).</summary>
public sealed class NavigationTrace
{
    internal const long MaxBytes = 1_500_000;

    /// <summary>A press the other way, this soon after a move that landed on the control it came from, is counted as "taken back".</summary>
    internal static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(2.5);

    private readonly string? _logPath;
    private readonly string? _summaryPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _moves = new();
    private readonly Dictionary<string, int> _undone = new();
    private readonly Dictionary<string, int> _walls = new();
    private readonly Dictionary<string, int> _rules = new();
    private NavStep? _previous;

    /// <param name="directory">Where the files go; null keeps everything in memory (tests).</param>
    public NavigationTrace(string? directory)
    {
        if (directory is null)
            return;

        _logPath = Path.Combine(directory, "controller-navigation.jsonl");
        _summaryPath = Path.Combine(directory, "controller-navigation-summary.txt");
    }

    public int Steps { get; private set; }
    public int Moves { get; private set; }
    public int Walls { get; private set; }
    public int TakenBack { get; private set; }

    /// <summary>Direction presses the stick made that were ignored because they were diagonal (set by the window from the pad reader).</summary>
    public int IgnoredDiagonals { get; set; }

    private static bool IsDirection(string button) => button is "Up" or "Down" or "Left" or "Right";

    private static string Opposite(string button) => button switch { "Up" => "Down", "Down" => "Up", "Left" => "Right", "Right" => "Left", _ => "" };

    /// <summary>Notes one press: counts it, spots a wall or a move taken straight back, and appends it to the log.</summary>
    public void Record(NavStep step)
    {
        bool undo;
        lock (_lock)
        {
            Steps++;
            var direction = IsDirection(step.Button);
            undo = direction && step.Moved && _previous is { Moved: true } p
                && Opposite(p.Button) == step.Button && step.To == p.From && step.At - p.At <= UndoWindow;

            if (step.Moved)
            {
                Moves++;
                Bump(_moves, $"{step.From}  --{step.Button}-->  {step.To}");
                Bump(_rules, step.Rule);
            }
            else if (direction)
            {
                Walls++;
                Bump(_walls, $"{step.Screen}: {step.Button} from {step.From}");
            }

            if (undo)
            {
                TakenBack++;
                var first = _previous!;
                Bump(_undone, $"{first.From}  --{first.Button}-->  {first.To}");
            }

            _previous = step;
        }

        Append(step, undo);
    }

    private static void Bump(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private void Append(NavStep step, bool undo)
    {
        if (_logPath is null)
            return;

        try
        {
            var line = JsonSerializer.Serialize(new
            {
                t = step.At.ToString("HH:mm:ss.fff"),
                screen = step.Screen,
                btn = step.Button,
                from = step.From,
                to = step.To,
                rule = step.Rule,
                moved = step.Moved,
                back = undo ? true : (bool?)null,
                stick = step.Stick,
            });

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxBytes)
                    File.Move(_logPath, _logPath + ".1", overwrite: true);

                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A log that cannot be written must never get in the way of the pad.
        }
    }

    /// <summary>The short report: what to look at first.</summary>
    public string Summary()
    {
        lock (_lock)
        {
            var text = new StringBuilder();
            text.AppendLine($"Controller navigation: {Steps} presses, {Moves} moves, {Walls} direction presses that went nowhere, {TakenBack} moves taken straight back.");
            if (IgnoredDiagonals > 0)
                text.AppendLine($"The stick was pushed into a corner and ignored {IgnoredDiagonals} times.");

            Section(text, "Taken straight back (a move that was probably not meant - the best clues):", _undone, 8);
            Section(text, "Walls (a direction pressed where the pad could not go):", _walls, 8);
            Section(text, "Most used moves:", _moves, 8);
            Section(text, "Moves by rule:", _rules, 12);
            return text.ToString();
        }
    }

    private static void Section(StringBuilder text, string title, Dictionary<string, int> counts, int top)
    {
        if (counts.Count == 0)
            return;

        text.AppendLine().AppendLine(title);
        foreach (var (key, count) in counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(top))
            text.AppendLine($"  {count,4} x  {key}");
    }

    /// <summary>Writes <see cref="Summary"/> next to the log (replacing the last one).</summary>
    public void WriteSummary()
    {
        if (_summaryPath is null || Steps == 0)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_summaryPath)!);
            File.WriteAllText(_summaryPath, Summary(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
