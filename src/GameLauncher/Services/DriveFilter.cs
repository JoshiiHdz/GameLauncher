using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>Answers "is this path on a drive the user switched off?". One instance is built per scan (or per question) from a snapshot of
/// the ignored list, and remembers each drive letter's serial number so a library of thousands of games asks Windows once per drive.</summary>
public sealed class DriveFilter
{
    private readonly List<IgnoredDrive> _ignored;
    private readonly Func<string, uint?> _serialOf;
    private readonly Dictionary<string, uint?> _serials = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="serialOf">The volume serial of a drive root like "D:\", or null when unknown. Tests supply their own.</param>
    public DriveFilter(IEnumerable<IgnoredDrive> ignored, Func<string, uint?>? serialOf = null)
    {
        _ignored = ignored.Select(i => new IgnoredDrive { Letter = NormalizeLetter(i.Letter), VolumeSerial = i.VolumeSerial, Label = i.Label }).ToList();
        _serialOf = serialOf ?? VolumeInfo.GetSerialNumber;
    }

    public bool HasAny => _ignored.Count > 0;

    /// <summary>"D:\Games\Foo", "d:" and "D:\" all become "D:". Null for a path with no drive letter (a network share, a relative path).</summary>
    public static string NormalizeLetter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        if (path.Length == 1 && char.IsLetter(path[0]))
            return path.ToUpperInvariant() + ":"; // "d" as someone typed it

        var root = path.Length >= 2 && path[1] == ':' ? path[..2] : "";
        return root.ToUpperInvariant();
    }

    /// <summary>True when <paramref name="path"/> (any file or folder, or just a drive) is on an ignored drive.</summary>
    public bool IsIgnored(string? path)
    {
        if (_ignored.Count == 0)
            return false;

        var letter = NormalizeLetter(path);
        if (letter.Length == 0)
            return false;

        var serial = SerialOf(letter);
        foreach (var entry in _ignored)
        {
            // The serial wins whenever both sides know it: it follows the disk, not the letter.
            if (entry.VolumeSerial is { } wanted && serial is { } actual)
            {
                if (wanted == actual)
                    return true;
            }
            else if (string.Equals(entry.Letter, letter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private uint? SerialOf(string letter)
    {
        if (!_serials.TryGetValue(letter, out var serial))
        {
            try
            {
                serial = _serialOf(letter + Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                serial = null;
            }

            _serials[letter] = serial;
        }

        return serial;
    }
}
