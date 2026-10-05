namespace GameLauncher.Models;

/// <summary>Sizes as plain text ("2 GB", "350 MB") - one spelling for every screen.</summary>
public static class ByteFormat
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
