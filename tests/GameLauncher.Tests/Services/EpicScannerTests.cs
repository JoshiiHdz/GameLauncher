using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises EpicScanner.ParseManifest directly against JSON strings - split out from ParseItem
/// specifically so malformed-manifest handling (an unexpected root shape, missing fields, wrong-typed
/// fields) can be tested without touching the real filesystem or the hardcoded
/// %ProgramData%\Epic\EpicGamesLauncher\... path Scan() reads from.
///
/// A real ExecutablePath is required for ParseManifest to accept a manifest at all (see its own
/// "missing exe" check), so every well-formed-manifest test below points InstallLocation/
/// LaunchExecutable at a real temp file this fixture creates and cleans up.
/// </summary>
public class EpicScannerTests : IDisposable
{
    private readonly string _installDir;
    private readonly string _exeName = "game.exe";

    public EpicScannerTests()
    {
        _installDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests_Epic_" + Guid.NewGuid());
        Directory.CreateDirectory(_installDir);
        File.WriteAllBytes(Path.Combine(_installDir, _exeName), []);
    }

    public void Dispose()
    {
        try { Directory.Delete(_installDir, recursive: true); } catch (IOException) { }
    }

    private string WellFormedManifestJson() =>
        $$"""
        {
            "DisplayName": "Test Game",
            "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(_installDir)}},
            "LaunchExecutable": "{{_exeName}}",
            "AppName": "TestGameApp"
        }
        """;

    [Fact]
    public void WellFormedManifest_ParsesSuccessfully()
    {
        var entry = EpicScanner.ParseManifest(WellFormedManifestJson(), "test.item");

        Assert.NotNull(entry);
        Assert.Equal("Test Game", entry!.Name);
        Assert.Equal("epic-TestGameApp", entry.Id);
        Assert.Equal(Path.Combine(_installDir, _exeName), entry.ExecutablePath);
    }

    [Fact]
    public void WrongShapedRoot_ArrayInsteadOfObject_ReturnsNullWithoutThrowing()
    {
        var entry = EpicScanner.ParseManifest("[1, 2, 3]", "test.item");
        Assert.Null(entry);
    }

    [Fact]
    public void WrongShapedRoot_BareStringInsteadOfObject_ReturnsNullWithoutThrowing()
    {
        var entry = EpicScanner.ParseManifest("\"just a string\"", "test.item");
        Assert.Null(entry);
    }

    [Fact]
    public void MissingField_ReturnsNullWithoutThrowing()
    {
        var json = """{ "DisplayName": "Test Game", "InstallLocation": "C:\\Games\\Test" }""";
        var entry = EpicScanner.ParseManifest(json, "test.item");
        Assert.Null(entry);
    }

    [Theory]
    [InlineData("""{ "DisplayName": 123, "InstallLocation": "C:\\Games\\Test", "LaunchExecutable": "game.exe", "AppName": "App" }""")]
    [InlineData("""{ "DisplayName": "Test", "InstallLocation": 123, "LaunchExecutable": "game.exe", "AppName": "App" }""")]
    [InlineData("""{ "DisplayName": "Test", "InstallLocation": "C:\\Games\\Test", "LaunchExecutable": ["game.exe"], "AppName": "App" }""")]
    [InlineData("""{ "DisplayName": "Test", "InstallLocation": "C:\\Games\\Test", "LaunchExecutable": "game.exe", "AppName": null }""")]
    [InlineData("""{ "DisplayName": {"nested": true}, "InstallLocation": "C:\\Games\\Test", "LaunchExecutable": "game.exe", "AppName": "App" }""")]
    public void WrongTypedField_ReturnsNullWithoutThrowing_InsteadOfInvalidOperationException(string json)
    {
        // Every one of these has valid JSON syntax and every required KEY present - only a value's
        // TYPE is wrong. The original code only checked TryGetProperty (existence), so each of these
        // used to reach GetString() on a non-string JsonElement and throw InvalidOperationException,
        // which escaped ParseItem's old catch clause entirely (it only listed JsonException/IOException/
        // UnauthorizedAccessException) and could crash the whole scan over one malformed manifest.
        var entry = EpicScanner.ParseManifest(json, "test.item");
        Assert.Null(entry);
    }

    [Fact]
    public void OverflowingAppNameId_StillParsesAsAnOversizedStringId_WithoutThrowing()
    {
        // AppName is used as a plain string in the composed Id, not parsed as a number - an
        // "overflowing" value here just means an unusually long string, which must not throw either.
        var hugeAppName = new string('9', 500);
        var json = $$"""
            {
                "DisplayName": "Test Game",
                "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(_installDir)}},
                "LaunchExecutable": "{{_exeName}}",
                "AppName": "{{hugeAppName}}"
            }
            """;

        var entry = EpicScanner.ParseManifest(json, "test.item");
        Assert.NotNull(entry);
        Assert.Equal($"epic-{hugeAppName}", entry!.Id);
    }

    [Fact]
    public void MalformedItemAlongsideValidGames_OnlyTheMalformedOneIsSkipped()
    {
        var validJson = WellFormedManifestJson();
        var malformedJson = """{ "DisplayName": 123, "InstallLocation": "x", "LaunchExecutable": "x", "AppName": "x" }""";

        var validEntry = EpicScanner.ParseManifest(validJson, "valid.item");
        var malformedEntry = EpicScanner.ParseManifest(malformedJson, "malformed.item");

        Assert.NotNull(validEntry); // unaffected by the malformed one being parsed alongside it
        Assert.Null(malformedEntry);
    }

    [Fact]
    public void InvalidJsonSyntax_ReturnsNullWithoutThrowing()
    {
        var entry = EpicScanner.ParseManifest("{ not valid json", "test.item");
        Assert.Null(entry);
    }

    [Fact]
    public void MissingExecutable_ReturnsNullWithoutThrowing()
    {
        var json = $$"""
            {
                "DisplayName": "Test Game",
                "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(_installDir)}},
                "LaunchExecutable": "does-not-exist.exe",
                "AppName": "App"
            }
            """;

        var entry = EpicScanner.ParseManifest(json, "test.item");
        Assert.Null(entry);
    }
}
