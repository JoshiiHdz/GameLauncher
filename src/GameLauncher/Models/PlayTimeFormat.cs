namespace GameLauncher.Models;

/// <summary>How tracked play time reads on screen: minutes below an hour ("12 min"), hours with one decimal above it ("38.2 h").
/// Minutes below an hour because "0.2 h" reads like a bug for a short session.</summary>
public static class PlayTimeFormat
{
    public static string Duration(long seconds)
    {
        if (seconds <= 0)
            return "";

        return seconds < 3600 ? $"{Math.Max(1, seconds / 60)} min" : $"{seconds / 3600d:0.#} h";
    }
}
