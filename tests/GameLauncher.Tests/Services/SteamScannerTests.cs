using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises SteamScanner.ParseManifest directly against real temp .acf files - no real Steam
/// install/registry involved. Covers NonGameAppIds exclusion specifically, since that set was twice
/// flagged (Wallpaper Engine, appid 431960, still appearing in the library despite being requested as
/// excluded) without regression coverage to catch it disappearing again.
/// </summary>
public class SteamScannerTests : IDisposable
{
    private readonly string _steamAppsDir;

    public SteamScannerTests()
    {
        _steamAppsDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests_Steam_" + Guid.NewGuid(), "steamapps");
        Directory.CreateDirectory(Path.Combine(_steamAppsDir, "common", "TestGame"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_steamAppsDir)!, recursive: true); } catch (IOException) { }
    }

    private string WriteManifest(string appId, string name, string installDir)
    {
        var manifestPath = Path.Combine(_steamAppsDir, $"appmanifest_{appId}.acf");
        File.WriteAllText(manifestPath, $$"""
            "AppState"
            {
                "appid"		"{{appId}}"
                "name"		"{{name}}"
                "installdir"		"{{installDir}}"
            }
            """);
        return manifestPath;
    }

    [Fact]
    public void OrdinaryGame_IsParsed()
    {
        var manifest = WriteManifest("12345", "Test Game", "TestGame");
        var entry = SteamScanner.ParseManifest(manifest, _steamAppsDir);

        Assert.NotNull(entry);
        Assert.Equal("steam-12345", entry!.Id);
        Assert.Equal("Test Game", entry.Name);
    }

    [Fact]
    public void WallpaperEngine_Appid431960_IsExcluded()
    {
        Directory.CreateDirectory(Path.Combine(_steamAppsDir, "common", "wallpaper_engine"));
        var manifest = WriteManifest("431960", "Wallpaper Engine", "wallpaper_engine");

        var entry = SteamScanner.ParseManifest(manifest, _steamAppsDir);

        Assert.Null(entry);
    }

    [Fact]
    public void SteamworksCommonRedistributables_Appid228980_IsExcluded()
    {
        // Regression for the original exclusion - proves the new entry didn't accidentally replace it.
        Directory.CreateDirectory(Path.Combine(_steamAppsDir, "common", "Steamworks Shared"));
        var manifest = WriteManifest("228980", "Steamworks Common Redistributables", "Steamworks Shared");

        var entry = SteamScanner.ParseManifest(manifest, _steamAppsDir);

        Assert.Null(entry);
    }
}
