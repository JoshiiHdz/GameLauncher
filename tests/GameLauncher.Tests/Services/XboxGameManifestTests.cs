using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// XboxGameManifest.ReadLaunchTarget - reads a game's OWN declared launch target from
/// MicrosoftGame.config/AppxManifest.xml (real files, real temp directories - the same approach every
/// other filesystem-facing test in this project already uses). The point of these files existing at all
/// is that the old "there's no manifest to read" comment on XboxScanner was wrong; these tests pin that
/// it actually gets read, and read CORRECTLY - a single confident target, several equally-plausible ones
/// reported as ambiguous rather than picked arbitrarily, and no crash on anything malformed.
/// </summary>
public class XboxGameManifestTests : IDisposable
{
    private readonly string _root;

    public XboxGameManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests_XboxManifest_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string fileName, string xml) => File.WriteAllText(Path.Combine(_root, fileName), xml);

    // ---- MicrosoftGame.config -------------------------------------------------------------------------

    [Fact]
    public void MicrosoftGameConfig_OneExecutable_NoTargetDeviceFamilyAtAll_IsUnambiguous()
    {
        Write("MicrosoftGame.config", """
            <Game configVersion="1">
              <Identity Name="MyGame" Publisher="CN=Test" Version="1.0.0.0" />
              <ExecutableList>
                <Executable Name="MyGame.exe" Id="Game" />
              </ExecutableList>
            </Game>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
        Assert.Equal("MyGame.exe", result.Target!.ExeRelativePath);
        Assert.Equal("Game", result.Target.ApplicationId);
    }

    [Fact]
    public void MicrosoftGameConfig_MultipleDeviceFamilies_ThePcOneIsPicked_NotJustTheFirst()
    {
        Write("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="MyGame_Xbox.exe" Id="XboxApp" TargetDeviceFamily="Xbox" />
                <Executable Name="MyGame_PC.exe" Id="PcApp" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
        Assert.Equal("MyGame_PC.exe", result.Target!.ExeRelativePath);
        Assert.Equal("PcApp", result.Target.ApplicationId);
    }

    [Fact]
    public void MicrosoftGameConfig_DesktopIsAlsoRecognizedAsPc_CaseInsensitive()
    {
        Write("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="Console.exe" Id="ConsoleApp" TargetDeviceFamily="Scarlett" />
                <Executable Name="Desktop.exe" Id="DesktopApp" TargetDeviceFamily="desktop" />
              </ExecutableList>
            </Game>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
        Assert.Equal("Desktop.exe", result.Target!.ExeRelativePath);
    }

    [Fact]
    public void MicrosoftGameConfig_MultiplePcCandidates_IsAmbiguous_NotAnArbitraryPick()
    {
        Write("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="MyGame.exe" Id="Game" TargetDeviceFamily="PC" />
                <Executable Name="MyGameLauncher.exe" Id="Launcher" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Target);
        // The candidate list is what lets a caller with independent identity evidence still resolve this
        // (XboxScanner.TryDisambiguateViaConfirmedIdentity) - losing it here would force every ambiguous
        // manifest to stay unresolved even when that evidence exists.
        Assert.Equal(new[] { "Game", "Launcher" }, result.Candidates!.Select(c => c.ApplicationId));
    }

    [Fact]
    public void MicrosoftGameConfig_OnlyExplicitlyNonPcEntries_IsIncompatibleTarget_DistinctFromNone()
    {
        // Both executables explicitly declare a non-PC TargetDeviceFamily - there is genuinely no PC
        // target here, not "an ambiguous pair to pick from", and not "the file said nothing" either: it
        // DID say something, an affirmative "no PC build" - which a caller must be able to tell apart
        // from a manifest that named nothing at all, or a folder search could still pick up one of these
        // same console-only executables and launch it as if this declaration never existed.
        Write("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="Xbox1.exe" Id="A" TargetDeviceFamily="Xbox" />
                <Executable Name="Xbox2.exe" Id="B" TargetDeviceFamily="Scarlett" />
              </ExecutableList>
            </Game>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.IncompatibleTarget, result.Outcome);
        Assert.NotEqual(XboxManifestOutcome.None, result.Outcome);
    }

    [Fact]
    public void MicrosoftGameConfig_Malformed_IsNoInfo_NotAnException()
    {
        Write("MicrosoftGame.config", "<Game><Executable"); // truncated/invalid XML

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.None, result.Outcome);
    }

    [Fact]
    public void MicrosoftGameConfig_Absent_FallsThroughToAppxManifest()
    {
        Write("AppxManifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" Executable="Real.exe" />
              </Applications>
            </Package>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
        Assert.Equal("Real.exe", result.Target!.ExeRelativePath);
    }

    // ---- AppxManifest.xml -------------------------------------------------------------------------

    [Fact]
    public void AppxManifest_OneApplication_ReadsExecutableIdAndDisplayName()
    {
        Write("AppxManifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="App" Executable="Game\Game.exe">
                  <uap:VisualElements DisplayName="My Real Game Title" />
                </Application>
              </Applications>
            </Package>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
        Assert.Equal(@"Game\Game.exe", result.Target!.ExeRelativePath);
        Assert.Equal("App", result.Target.ApplicationId);
        Assert.Equal("My Real Game Title", result.Target.DisplayName);
    }

    [Fact]
    public void AppxManifest_MultipleApplications_IsAmbiguous()
    {
        Write("AppxManifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App1" Executable="One.exe" />
                <Application Id="App2" Executable="Two.exe" />
              </Applications>
            </Package>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public void AppxManifest_ApplicationWithNoExecutable_IsNoInfo_APureUwpEntryPoint()
    {
        // A pure-UWP application (EntryPoint-based, not Executable-based) legitimately has no
        // Executable attribute at all - not malformed, just nothing this scanner can use as a file path.
        Write("AppxManifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.None, result.Outcome);
    }

    [Fact]
    public void NeitherFileExists_IsNoInfo_NotAnException()
    {
        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.None, result.Outcome);
    }

    [Fact]
    public void LowercaseAppxmanifestFileName_IsAlsoRead()
    {
        Write("appxmanifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" Executable="Real.exe" />
              </Applications>
            </Package>
            """);

        var result = XboxGameManifest.ReadLaunchTarget(_root);

        Assert.Equal(XboxManifestOutcome.Single, result.Outcome);
    }
}
