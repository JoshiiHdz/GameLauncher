using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>A [Fact] that only runs where this account can write to a ready drive's XboxGames folder (it
/// creates and deletes its own uniquely-named subfolder there - never touching, listing summary of, or
/// asserting on anything else that might already be in it, since a real Xbox install could already have
/// real games there). Skipped, not failed, where it can't - the fallback-layer tests need a REAL folder a
/// real drive enumeration will see, which is inherently not injectable the way package discovery is.</summary>
public sealed class FactRequiresWritableXboxGamesFolderAttribute : FactAttribute
{
    public FactRequiresWritableXboxGamesFolderAttribute()
    {
        if (XboxGamesFolderSupport.ReadyDriveRoot.Value is null)
            Skip = "No ready, writable drive with an accessible XboxGames folder was found - the real-folder fallback tests need one.";
    }
}

internal static class XboxGamesFolderSupport
{
    public static readonly Lazy<string?> ReadyDriveRoot = new(Probe);

    private static string? Probe()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
            }
            catch (IOException)
            {
                continue;
            }

            var probeDir = Path.Combine(drive.RootDirectory.FullName, "XboxGames", "GameLauncherTests_Probe_" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(probeDir);
                Directory.Delete(probeDir);
                return drive.RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}

/// <summary>
/// XboxScanner - metadata-first, layout-independent, package-registration-driven Xbox detection.
/// BuildCandidateEntry is exercised directly for most scenarios (fast, no real drives needed); a smaller
/// set of Scan()-level tests prove the outer wiring (which candidates get filtered before reaching it,
/// and the real "XboxGames" folder fallback for whatever package discovery doesn't cover).
/// </summary>
public class XboxScannerTests : IDisposable
{
    private readonly string _root;
    private static readonly IReadOnlyList<(string Name, string Aumid)> NoPackagedApps = Array.Empty<(string, string)>();

    public XboxScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests_XboxScanner_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        XboxPackageDiscovery.PackagesOverrideForTest = null;
        StartAppsResolver.PackagedAppsOverrideForTest = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private string WriteExe(string relativePath, int sizeBytes)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[sizeBytes]);
        return fullPath;
    }

    // ---- Metadata-first: MicrosoftGame.config/AppxManifest.xml name the executable directly ----------

    [Fact]
    public void MicrosoftGameConfig_NamesTheExecutable_UsedDirectly_ALargerUnrelatedExeDoesNotWin()
    {
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList><Executable Name="Game.exe" Id="Game" TargetDeviceFamily="PC" /></ExecutableList>
            </Game>
            """);
        var realExe = WriteExe("Game.exe", 5_000_000);
        WriteExe(@"Redist\vcredist_x64.exe", 90_000_000); // larger, but never even considered

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", NoPackagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(realExe, entry.ExecutablePath);
        Assert.Equal("TestPkg_8wekyb3d8bbwe!Game", entry.LaunchUri!.Replace("shell:appsFolder\\", ""));
    }

    // ---- Path validation: a manifest-declared executable must stay inside the install root -----------

    [Fact]
    public void ManifestDeclaresAnAbsolutePath_IsRefused_NeverCombinedAndTrusted()
    {
        // Microsoft's schema specifies a game-root-RELATIVE path. An absolute one could point ANYWHERE
        // on the machine (a real file might genuinely exist there, e.g. a system exe) - this must never
        // be combined with the install root and launched as if it were the game.
        var elsewhere = Path.Combine(Path.GetTempPath(), "GameLauncherTests_PathEscape_" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        var outsideExe = Path.Combine(elsewhere, "NotTheGame.exe");
        File.WriteAllBytes(outsideExe, new byte[1000]);

        try
        {
            WriteFile("MicrosoftGame.config", $$"""
                <Game configVersion="1">
                  <ExecutableList><Executable Name="{{outsideExe}}" Id="Game" /></ExecutableList>
                </Game>
                """);

            var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

            // Nothing inside _root satisfies this (no manifest-valid exe, nothing for the fallback search
            // to find either) - the absolute path must never be trusted, and the entry has nothing else
            // to launch with.
            Assert.Null(entry);
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ManifestDeclaresADotDotEscape_IsRefused_NeverEscapesTheInstallRoot()
    {
        // A sibling folder holding a real exe - if "..\" traversal were honored, this exe would be
        // reachable from _root and could be launched as if it belonged to a completely different game's
        // install.
        var siblingDir = _root + "_sibling_" + Guid.NewGuid();
        Directory.CreateDirectory(siblingDir);
        var siblingExe = Path.Combine(siblingDir, "SiblingGame.exe");
        File.WriteAllBytes(siblingExe, new byte[1000]);

        try
        {
            var siblingFolderName = Path.GetFileName(siblingDir);
            WriteFile("MicrosoftGame.config", $$"""
                <Game configVersion="1">
                  <ExecutableList><Executable Name="..\{{siblingFolderName}}\SiblingGame.exe" Id="Game" /></ExecutableList>
                </Game>
                """);

            var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

            Assert.Null(entry);
        }
        finally
        {
            try { Directory.Delete(siblingDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ManifestDeclaresAnOrdinaryRelativePath_WithNoTraversal_StillWorksNormally()
    {
        // Guards against an over-broad path-validation fix rejecting completely legitimate nested paths
        // that merely happen to contain directory separators (not ".." itself).
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList><Executable Name="Binaries\Win64\Game.exe" Id="Game" /></ExecutableList>
            </Game>
            """);
        var expected = WriteExe(@"Binaries\Win64\Game.exe", 5_000_000);

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(expected, entry.ExecutablePath);
    }

    [Fact]
    public void ManifestDeclaresAnExecutableThatDoesNotExist_FallsBackToSearch_NotACrash()
    {
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList><Executable Name="Missing.exe" Id="Game" /></ExecutableList>
            </Game>
            """);
        var realExe = WriteExe(@"Content\Real.exe", 5_000_000);

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(realExe, entry.ExecutablePath);
    }

    [Fact]
    public void NoManifestAtAll_ExecutableSevenLevelsDeep_StillFound()
    {
        var expected = WriteExe(@"a\b\c\d\e\f\g\Game.exe", 5_000_000);

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(expected, entry.ExecutablePath);
    }

    // ---- Incomplete search: "found something" is never published as "resolved" ------------------------

    [Fact]
    public void NoManifest_SearchBudgetExhausted_NoAumidEither_EntryIsNull_ProvisionalNeverPublished()
    {
        // The corrected behavior: an incomplete search cannot confirm its provisional find is the right
        // (or only) candidate, so that candidate is never surfaced as a resolved ExecutablePath - not even
        // as a last resort. With nothing else to launch with (no package identity at all here), the whole
        // entry must be null, not a GameEntry quietly launching an unconfirmed guess.
        WriteExe("Game.exe", 5_000_000);
        WriteExe(@"a\b\c\d\e\OtherStuff.exe", 90_000_000); // unreachable at this budget

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps,
            CancellationToken.None, out _, searchMaxDirectoriesToVisit: 1);

        Assert.Null(entry);
    }

    [Fact]
    public void NoManifest_SearchBudgetExhausted_ButAConfirmedAumidExists_UsesActivationTarget_NeverTheUnconfirmedExe()
    {
        // Same incomplete search as above, but this time a confirmed package identity IS available - the
        // entry still gets added (AUMID alone is enough), but its ExecutablePath must be the installRoot
        // placeholder, never the provisional exe the search happened to see before running out of budget.
        var provisional = WriteExe("Game.exe", 5_000_000);
        WriteExe(@"a\b\c\d\e\OtherStuff.exe", 90_000_000); // unreachable at this budget

        var packagedApps = new[] { ("Test Game", "TestPkg_8wekyb3d8bbwe!Game") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps,
            CancellationToken.None, out _, searchMaxDirectoriesToVisit: 1);

        Assert.NotNull(entry);
        Assert.Equal("shell:appsFolder\\TestPkg_8wekyb3d8bbwe!Game", entry.LaunchUri);
        Assert.Equal(_root, entry.ExecutablePath);
        Assert.NotEqual(provisional, entry.ExecutablePath);
    }

    // ---- IncompatibleTarget: an explicitly console-only declaration is never overridden by a search ----

    [Fact]
    public void ManifestDeclaresOnlyAConsoleTarget_ARealConsoleExeExists_NeverPickedUpBySearch()
    {
        // The concrete regression this guards: a REAL, existing Console.exe sitting right there in the
        // folder - if the search fallback ran anyway (treating IncompatibleTarget the same as "the
        // manifest said nothing"), it would find and launch this exact file, silently overriding the
        // manifest's own explicit "no PC build" declaration.
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList><Executable Name="Console.exe" Id="ConsoleApp" TargetDeviceFamily="Xbox" /></ExecutableList>
            </Game>
            """);
        WriteExe("Console.exe", 5_000_000);

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        // Nothing else to launch with either (no package identity here) - the entry must be null, not
        // Console.exe picked up as a substitute.
        Assert.Null(entry);
    }

    [Fact]
    public void ManifestDeclaresOnlyAConsoleTarget_ButAConfirmedAumidExists_UsesActivationTarget_NeverTheConsoleExe()
    {
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList><Executable Name="Console.exe" Id="ConsoleApp" TargetDeviceFamily="Xbox" /></ExecutableList>
            </Game>
            """);
        WriteExe("Console.exe", 5_000_000);

        var packagedApps = new[] { ("Test Game", "TestPkg_8wekyb3d8bbwe!Game") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal("shell:appsFolder\\TestPkg_8wekyb3d8bbwe!Game", entry.LaunchUri);
        Assert.Equal(_root, entry.ExecutablePath); // never Console.exe
    }

    [Fact]
    public void AmbiguousManifest_TwoRealExistingExecutables_NoIndependentEvidence_NeverPicksBySize_StaysUnresolved()
    {
        // Both candidates genuinely exist - this is the real conflict a size-based guess would resolve
        // wrongly half the time. "B.exe" is deliberately much larger, so a "just take the biggest" or
        // "just take the first one listed" fallback would produce a confident, wrong answer here instead
        // of correctly staying unresolved.
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        WriteExe("A.exe", 5_000_000);
        WriteExe("B.exe", 90_000_000);

        // No package identity at all (the XboxGames-folder-only fallback shape) and no Start Menu data -
        // genuinely nothing independent to resolve the ambiguity with.
        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        // Nothing to launch with at all: no confident exe (ambiguity was never resolved, and the bounded
        // search is never even consulted for an unresolved-ambiguous manifest) and no AUMID either.
        Assert.Null(entry);
    }

    [Fact]
    public void AmbiguousManifest_TwoRealExistingExecutables_ResolvedByConfirmedPackageIdentity_PicksTheConfirmedOneNeverTheLarger()
    {
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        var confirmedRealExe = WriteExe("A.exe", 5_000_000); // the SMALLER one, but the one Windows confirms
        WriteExe("B.exe", 90_000_000);

        // Windows itself (via Get-StartApps) confirms "AppA" is a real, registered application id for
        // this exact package - independent evidence, not a guess. "AppB" has no such confirmation.
        var packagedApps = new[] { ("Test Game", "TestPkg_8wekyb3d8bbwe!AppA") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(confirmedRealExe, entry.ExecutablePath); // never B.exe, despite being 18x larger
    }

    [Fact]
    public void AmbiguousManifest_MultipleCandidatesIndependentlyConfirmed_NeitherExeNorAumidIsSelected()
    {
        // Both "AppA" and "AppB" happen to be confirmed registered application ids for this package (a
        // companion app scenario) - independent evidence exists, but it doesn't narrow to exactly ONE
        // candidate for the EXECUTABLE (TryDisambiguateViaConfirmedIdentity) OR for the AUMID
        // (ResolveAumid's tier 2, which also now requires a unique family match) - neither launch
        // mechanism is entitled to pick one of two equally-corroborated candidates, so this must produce
        // NO entry at all: nothing here is confidently launchable, not A.exe, not B.exe, and not either
        // app id via a name-similarity guess.
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        WriteExe("A.exe", 5_000_000);
        WriteExe("B.exe", 90_000_000);

        var packagedApps = new[]
        {
            ("Test Game", "TestPkg_8wekyb3d8bbwe!AppA"),
            ("Test Game Companion", "TestPkg_8wekyb3d8bbwe!AppB"),
        };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.Null(entry);
    }

    [Fact]
    public void AmbiguousManifest_UnresolvedByIdentity_NeverFallsBackToAnUnrelatedFamilyMatch_TheToolsCase()
    {
        // The manifest names "AppA"/"AppB" - genuinely ambiguous, and neither is independently confirmed.
        // The package's family has exactly ONE Start Menu entry, "Tools" - a THIRD application id that
        // matches NEITHER of the manifest's own candidates. ResolveAumid's tier 2 in isolation would
        // happily accept this as "the one confirmed family match" (it never checks correspondence to the
        // manifest's own candidate list at all) and return "Fam!Tools" as if it were the game - it is not:
        // it's some unrelated tool the same package family happens to also register. This must select
        // NEITHER launch mechanism.
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        WriteExe("A.exe", 5_000_000);
        WriteExe("B.exe", 90_000_000);

        var packagedApps = new[] { ("Some Tool", "TestPkg_8wekyb3d8bbwe!Tools") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.Null(entry);
    }

    [Fact]
    public void AmbiguousManifest_UnresolvedAndNoPackageIdentityAtAll_NeverFallsBackToAFreeTextGuessEither()
    {
        // The XboxGames-folder-only fallback shape (no confirmed package identity): a free-text
        // folder-name-vs-Start-Menu-title match would happily resolve "My Amazing Game" against a
        // similarly-named Start Menu entry, completely independent of the manifest's own, unresolved
        // conflict between A.exe and B.exe - sidestepping the exact uncertainty this is meant to preserve
        // by using an entirely unrelated signal to do it.
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        WriteExe("A.exe", 5_000_000);
        WriteExe("B.exe", 90_000_000);

        var packagedApps = new[] { ("My Amazing Game", "SomeOtherFam_8wekyb3d8bbwe!Whatever") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "My Amazing Game", packageFamilyName: null, packagedApps, CancellationToken.None, out _);

        Assert.Null(entry);
    }

    // ---- Protected/packaged games: no readable exe, but a verified activation target is enough --------

    [Fact]
    public void NoExecutableAnywhere_ButAConfirmedAumid_IsStillAdded_ActivationTargetAlone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content")); // exists, but genuinely empty of exes
        var packagedApps = new[] { ("Test Game", "TestPkg_8wekyb3d8bbwe!Game") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", "TestPkg_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal("shell:appsFolder\\TestPkg_8wekyb3d8bbwe!Game", entry.LaunchUri);
        // ExecutablePath still has to be SOME non-null string (the model requires it) - the install
        // directory itself, which IconService already knows how to treat (look inside for an icon).
        Assert.Equal(_root, entry.ExecutablePath);
    }

    [Fact]
    public void NoExecutable_AndNoAumid_ReturnsNull_NothingToLaunchWith()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content"));

        var entry = XboxScanner.BuildCandidateEntry(_root, "Test Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        Assert.Null(entry);
    }

    // ---- AUMID resolution tiers: package identity beats free-text title matching ----------------------

    [Fact]
    public void Tier1_ManifestApplicationId_BuildsAumidDirectly_NoStartAppsNeededAtAll()
    {
        WriteFile("MicrosoftGame.config", """
            <Game configVersion="1"><ExecutableList><Executable Name="G.exe" Id="MyAppId" /></ExecutableList></Game>
            """);
        WriteExe("G.exe", 1000);

        // Empty packagedApps - proves this tier needs no Start Menu data at all.
        var entry = XboxScanner.BuildCandidateEntry(_root, "Whatever Folder Name", "Fam_8wekyb3d8bbwe", NoPackagedApps, CancellationToken.None, out _);

        Assert.Equal("shell:appsFolder\\Fam_8wekyb3d8bbwe!MyAppId", entry!.LaunchUri);
    }

    [Fact]
    public void Tier2_PackageIdentityConfirmed_NoApplicationIdFromManifest_NarrowsToThatFamilyOnly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content"));
        var packagedApps = new[]
        {
            ("Some Other Unrelated Game", "OtherFam_8wekyb3d8bbwe!App"), // different family - must be ignored
            ("My Real Game", "Fam_8wekyb3d8bbwe!RealApp"),
        };

        var entry = XboxScanner.BuildCandidateEntry(_root, "My Real Game Folder", "Fam_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.Equal("shell:appsFolder\\Fam_8wekyb3d8bbwe!RealApp", entry!.LaunchUri);
        Assert.Equal("My Real Game", entry.Name);
    }

    [Fact]
    public void Tier2_MultipleAppsInTheSameFamily_NeverDisambiguatedByNameGuessing_NoAumidSelected()
    {
        // Package identity IS confirmed (Fam_8wekyb3d8bbwe), but TWO of its own application ids are
        // independently registered - even though one ("Game") is an exact name match and would have won
        // the old name-similarity tie-break, having confirmed package identity is supposed to mean real
        // evidence, not "guess which of our own apps is the game" with extra steps. No AUMID must be
        // selected here at all.
        var realExe = WriteExe(@"Content\Game.exe", 5_000_000); // a real, resolvable exe so the entry isn't null for an unrelated reason
        var packagedApps = new[]
        {
            ("My Amazing Game", "Fam_8wekyb3d8bbwe!Game"),
            ("My Amazing Game VR Companion", "Fam_8wekyb3d8bbwe!Companion"),
        };

        var entry = XboxScanner.BuildCandidateEntry(_root, "My Amazing Game", "Fam_8wekyb3d8bbwe", packagedApps, CancellationToken.None, out _);

        Assert.NotNull(entry);
        Assert.Equal(realExe, entry.ExecutablePath); // the exe still resolves fine independently of AUMID resolution
        Assert.Null(entry.LaunchUri); // but no AUMID is selected among the two equally-confirmed candidates
    }

    [Fact]
    public void Tier3_NoPackageIdentityAtAll_FallsBackToFreeTextFolderNameMatch_TheOldWeakestTier()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content"));
        var packagedApps = new[] { ("Call of Duty: Black Ops 7", "Fam_8wekyb3d8bbwe!App") };

        var entry = XboxScanner.BuildCandidateEntry(_root, "Call of Duty", packageFamilyName: null, packagedApps, CancellationToken.None, out _);

        Assert.Equal("Call of Duty: Black Ops 7", entry!.Name);
    }

    // ---- Full Scan(): what gets filtered before ever reaching BuildCandidateEntry ---------------------

    [Theory]
    [InlineData("38985CA0.BO7DLC56GamePassPack03")]
    [InlineData("38985CA0.BO7DLC17StandardLaunchTracker")]
    [InlineData("38985CA0.BO7DLC01GameStub01")]
    [InlineData("38985CA0.BO7DLC19GamePassLaunchTracker")]
    [InlineData("AnotherPublisher.NewGame42DLC02")]
    [InlineData("AnotherPublisher.ArcadeLaunchTracker01")]
    [InlineData("AnotherPublisher.ArcadeGameStub01")]
    public void Scan_CompactedAddonPackageNames_FromGamingPcLog_AreNotGames(string packageName)
    {
        var installRoot = Path.Combine(_root, "WindowsApps", packageName + "_0.0.9.0_x64__publisher");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "MicrosoftGame.config"),
            """<Game><ExecutableList><Executable Name="Stub.exe" Id="Game" /></ExecutableList></Game>""");
        File.WriteAllBytes(Path.Combine(installRoot, "Stub.exe"), new byte[1000]);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new(packageName + "_publisher", installRoot, packageName)];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        Assert.Empty(XboxScanner.Scan([_root]));
    }

    [Theory]
    [InlineData("Publisher.WorldCup27")]
    [InlineData("Publisher.DLCQuest")]
    public void Scan_OrdinaryCompactGameNames_AreNotMistakenForAddonMarkers(string name)
    {
        var location = Path.Combine(_root, name);
        Directory.CreateDirectory(location);
        File.WriteAllText(Path.Combine(location, "MicrosoftGame.config"),
            """<Game><ExecutableList><Executable Name="Game.exe" Id="Game" /></ExecutableList></Game>""");
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new(name + "_pub", location, name)];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];
        Assert.Single(XboxScanner.Scan([_root]));
    }

    [Theory]
    [InlineData("<TargetDeviceFamilyForDLC>PC</TargetDeviceFamilyForDLC>")]
    [InlineData("<AllowedProducts><AllowedProduct>StoreBaseGame</AllowedProduct></AllowedProducts>")]
    [InlineData("<RelatedProducts><RelatedProduct>StoreBaseGame</RelatedProduct></RelatedProducts>")]
    public void Scan_ContentOnlyDlcMetadata_BlocksBothDiscoveryPaths_EvenWithUndeclaredExeAndAumid(string metadata)
    {
        var content = Path.Combine(_root, "XboxGames", "OrdinaryLookingProduct", "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
            $"<Game>{metadata}</Game>");
        File.WriteAllText(Path.Combine(content, "AppxManifest.xml"),
            """<Package><Applications><Application Id="Game" Executable="Payload.exe" /></Applications></Package>""");
        File.WriteAllBytes(Path.Combine(content, "Payload.exe"), new byte[1000]);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new("Product_publisher", content, "Product")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [("OrdinaryLookingProduct", "Product_publisher!Game")];

        Assert.Empty(XboxScanner.Scan([_root]));
        XboxPackageDiscovery.PackagesOverrideForTest = () => [];
        Assert.Empty(XboxScanner.Scan([_root])); // Folder-only discovery must respect the same metadata.
    }

    [Theory]
    [InlineData("<TargetDeviceFamilyForDLC>PC</TargetDeviceFamilyForDLC>")]
    [InlineData("<AllowedProducts><AllowedProduct>StoreBaseGame</AllowedProduct></AllowedProducts>")]
    [InlineData("<RelatedProducts><RelatedProduct>StoreBaseGame</RelatedProduct></RelatedProducts>")]
    public void Scan_LaunchableGameWithProductRelationships_IsNotMistakenForContentOnlyDlc(string metadata)
    {
        var content = Path.Combine(_root, "XboxGames", "Launchable Game", "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
            $"<Game>{metadata}<ExecutableList><Executable Name=\"Game.exe\" Id=\"Game\" /></ExecutableList></Game>");
        File.WriteAllBytes(Path.Combine(content, "Game.exe"), new byte[1000]);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new("Product_publisher", content, "Product")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [("Launchable Game", "Product_publisher!Game")];
        Assert.Equal(Path.Combine(content, "Game.exe"), Assert.Single(XboxScanner.Scan([_root])).ExecutablePath);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [];
        Assert.Equal(Path.Combine(content, "Game.exe"), Assert.Single(XboxScanner.Scan([_root])).ExecutablePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scan_WindowsAppsRegistrationAndXboxGamesContent_ProduceOneGame_WithStableIdAndLegacyMigration(bool hasIdentity)
    {
        var registration = Path.Combine(_root, "WindowsApps", "Publisher.TestGame_1.2.0_x64__abc");
        var folder = Path.Combine(_root, "XboxGames", "Test Game");
        var content = Path.Combine(folder, "Content");
        Directory.CreateDirectory(registration);
        Directory.CreateDirectory(content);
        var identity = hasIdentity ? "<Identity Name=\"Publisher.TestGame\" Publisher=\"CN=Publisher\" />" : "";
        var config = $"<Game>{identity}<ExecutableList><Executable Name=\"Game.exe\" Id=\"Game\" /></ExecutableList></Game>";
        File.WriteAllText(Path.Combine(registration, "MicrosoftGame.config"), config);
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), config);
        File.WriteAllBytes(Path.Combine(registration, "Game.exe"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(content, "Game.exe"), new byte[1000]);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new("Publisher.TestGame_abc", registration, "Publisher.TestGame")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [("Test Game", "Publisher.TestGame_abc!Game")];
        var oldFolderEntry = XboxScanner.BuildCandidateEntry(folder, "Test Game", null,
            [("Test Game", "Publisher.TestGame_abc!Game")], CancellationToken.None, out _)!;
        var registeredEntry = XboxScanner.BuildCandidateEntry(registration, "Test Game", "Publisher.TestGame_abc",
            [("Test Game", "Publisher.TestGame_abc!Game")], CancellationToken.None, out _)!;

        var game = Assert.Single(XboxScanner.Scan([_root]));

        Assert.Equal(registeredEntry.Id, game.Id);
        Assert.Equal(oldFolderEntry.Id, game.LegacyId);
        Assert.Equal(content, game.InstallDir);
        Assert.Equal(Path.Combine(content, "Game.exe"), game.ExecutablePath);
        Assert.Equal("shell:appsFolder\\Publisher.TestGame_abc!Game", game.LaunchUri);
        Assert.Equal(game.Id, GameScannerService.ComputeLegacyIdRemap([game])[oldFolderEntry.Id]);
        Assert.Equal(game.Id, Assert.Single(XboxScanner.Scan([_root])).Id);
    }

    [Fact]
    public void Scan_ContentFolderWithoutManifest_DoesNotHideExecutableOutsideIt()
    {
        var folder = Path.Combine(_root, "XboxGames", "Ordinary Game");
        Directory.CreateDirectory(Path.Combine(folder, "Content"));
        File.WriteAllBytes(Path.Combine(folder, "Game.exe"), new byte[1000]);
        XboxPackageDiscovery.PackagesOverrideForTest = () => [];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];
        var game = Assert.Single(XboxScanner.Scan([_root]));
        Assert.Equal(Path.Combine(folder, "Game.exe"), game.ExecutablePath);
    }

    [Fact]
    public void Scan_SameDisplayTitleButDistinctPackageIdentities_AreNotMerged()
    {
        var registration = Path.Combine(_root, "WindowsApps", "First");
        var content = Path.Combine(_root, "XboxGames", "Same Title", "Content");
        Directory.CreateDirectory(registration);
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(registration, "MicrosoftGame.config"),
            """<Game><Identity Name="First" /><ExecutableList><Executable Name="Game.exe" Id="Game" /></ExecutableList></Game>""");
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
            """<Game><Identity Name="Second" /><ExecutableList><Executable Name="Game.exe" Id="Game" /></ExecutableList></Game>""");
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new("First_pub", registration, "First"), new("Second_pub", content, "Second")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [("Same Title", "First_pub!Game"), ("Same Title", "Second_pub!Game")];

        var games = XboxScanner.Scan([_root]);
        Assert.Equal(2, games.Count);
        Assert.Equal(2, games.Select(g => g.Id).Distinct().Count());
        Assert.Equal(2, games.Select(g => g.LaunchUri).Distinct().Count());
    }

    [Fact]
    public void Scan_APackageWithNoGamingEvidenceAtAll_IsNeverAdded_NotEveryRegisteredApp()
    {
        var installLoc = Path.Combine(_root, "NotAGame");
        Directory.CreateDirectory(installLoc);
        File.WriteAllText(Path.Combine(installLoc, "AppxManifest.xml"), // every packaged app has one of these - games and non-games alike
            """<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Applications><Application Id="App" Executable="notepad.exe" /></Applications></Package>""");

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("NotAGame_8wekyb3d8bbwe", installLoc, "NotAGame")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        var games = XboxScanner.Scan();

        Assert.DoesNotContain(games, g => g.InstallDir == installLoc);
    }

    [Fact]
    public void Scan_MicrosoftGameConfigAtACustomInstallPath_IsAddedAsAGame_NotJustXboxGamesLocations()
    {
        // Deliberately NOT under any "XboxGames" folder - proves discovery isn't limited to that one
        // convention, the whole point of reading package registration in the first place.
        var installLoc = Path.Combine(_root, "MyCustomGamesFolder", "SomeGame");
        Directory.CreateDirectory(installLoc);
        File.WriteAllText(Path.Combine(installLoc, "MicrosoftGame.config"),
            """<Game configVersion="1"><ExecutableList><Executable Name="Game.exe" Id="App" /></ExecutableList></Game>""");
        var exePath = Path.Combine(installLoc, "Game.exe");
        File.WriteAllBytes(exePath, new byte[1000]);

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Custom_8wekyb3d8bbwe", installLoc, "SomeGame")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        var games = XboxScanner.Scan();

        var found = Assert.Single(games, g => g.InstallDir == installLoc);
        Assert.Equal(exePath, found.ExecutablePath);
    }

    [Fact]
    public void Scan_ManifestDeclaresOnlyAConsoleTarget_TheRealConsoleExeIsNeverAddedThroughTheFullScan()
    {
        // The end-to-end version of the BuildCandidateEntry-level test above - proves the same
        // "explicitly incompatible, never overridden by search" behavior survives the real Scan() outer
        // gate and package-registration wiring, not just the inner helper in isolation.
        var installLoc = Path.Combine(_root, "ConsoleOnlyGame");
        Directory.CreateDirectory(installLoc);
        File.WriteAllText(Path.Combine(installLoc, "MicrosoftGame.config"),
            """<Game configVersion="1"><ExecutableList><Executable Name="Console.exe" Id="App" TargetDeviceFamily="Xbox" /></ExecutableList></Game>""");
        File.WriteAllBytes(Path.Combine(installLoc, "Console.exe"), new byte[1000]);

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("ConsoleOnly_8wekyb3d8bbwe", installLoc, "ConsoleOnlyGame")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        var games = XboxScanner.Scan();

        Assert.DoesNotContain(games, g => g.InstallDir == installLoc);
    }

    [Fact]
    public void Scan_ADlcStubPackage_IsSkipped_EvenWithMicrosoftGameConfig()
    {
        var installLoc = Path.Combine(_root, "MyGame - Multiplayer DLC");
        Directory.CreateDirectory(installLoc);
        File.WriteAllText(Path.Combine(installLoc, "MicrosoftGame.config"),
            """<Game configVersion="1"><ExecutableList><Executable Name="Dlc.exe" Id="App" /></ExecutableList></Game>""");
        File.WriteAllBytes(Path.Combine(installLoc, "Dlc.exe"), new byte[1000]);

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Dlc_8wekyb3d8bbwe", installLoc, "Dlc")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        var games = XboxScanner.Scan();

        Assert.DoesNotContain(games, g => g.InstallDir == installLoc);
    }

    [Fact]
    public void Scan_PropagatesCancellation()
    {
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_x", _root, "X")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XboxScanner.Scan(cts.Token));
    }

    // ---- The real XboxGames-folder fallback: for whatever package discovery didn't cover --------------

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_APackageDiscoveryMisses_IsStillFoundThroughTheRealXboxGamesFolderFallback()
    {
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var gameFolderName = "GameLauncherTests_Fallback_" + Guid.NewGuid();
        var gameDir = Path.Combine(driveRoot, "XboxGames", gameFolderName);
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[1000]);

        // Package discovery finds NOTHING for this title - simulating Get-AppxPackage not covering it.
        XboxPackageDiscovery.PackagesOverrideForTest = () => [];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            var found = Assert.Single(games, g => g.InstallDir == gameDir);
            Assert.Equal(exePath, found.ExecutablePath);
        }
        finally
        {
            try { Directory.Delete(gameDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_AProtectedGameWithNoDiscoverableExecutable_IsStillAddedThroughScan_ActivationTargetAlone()
    {
        // The end-to-end version of BuildCandidateEntry's own AUMID-only test above: proves the SAME
        // "no exe, activation target alone is enough" behavior survives the real Scan() outer gate, not
        // just the inner helper in isolation. This is the honestly-testable shape of "protected game"
        // within that gate's current scope: a real XboxGames-folder install (satisfies the gate) whose
        // executable genuinely cannot be found anywhere inside it (simulating an ACL-locked or otherwise
        // unreadable packaged content folder). A truly package-only, WindowsApps-rooted title with
        // NEITHER an XboxGames location NOR a readable MicrosoftGame.config remains a known, open
        // limitation of the outer gate - see the class remarks - since nothing observable distinguishes
        // it from an arbitrary non-game UWP app without one of those two signals.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var gameFolderName = "GameLauncherTests_Protected_" + Guid.NewGuid();
        var gameDir = Path.Combine(driveRoot, "XboxGames", gameFolderName);
        Directory.CreateDirectory(gameDir); // exists, but genuinely empty - nothing for the search to find

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", gameDir, gameFolderName)];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [(gameFolderName, "Fam_8wekyb3d8bbwe!Game")];

        try
        {
            var games = XboxScanner.Scan();

            var found = Assert.Single(games, g => g.InstallDir == gameDir);
            Assert.Equal("shell:appsFolder\\Fam_8wekyb3d8bbwe!Game", found.LaunchUri);
            Assert.Equal(gameDir, found.ExecutablePath); // no exe found - the directory placeholder IconService already understands
        }
        finally
        {
            try { Directory.Delete(gameDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_APackageAlreadyFoundViaRegistration_IsNotAlsoAddedFromTheFolderFallback_NoDuplicate()
    {
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var gameFolderName = "GameLauncherTests_Dedup_" + Guid.NewGuid();
        var gameDir = Path.Combine(driveRoot, "XboxGames", gameFolderName);
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[1000]);

        // This SAME folder is ALSO reported by package discovery - Scan() must produce exactly one entry
        // for it, not two.
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", gameDir, gameFolderName)];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            Assert.Single(games, g => g.InstallDir == gameDir);
        }
        finally
        {
            try { Directory.Delete(gameDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_APackageRegisteredAtItsContentSubfolder_SuppressesTheParentFolderFallback_WithoutTouchingAnUnrelatedSibling()
    {
        // The real-world shape this covers: Get-AppxPackage's InstallLocation for a package is often the
        // "Content" subfolder the Xbox app created UNDER the umbrella game folder, not that folder
        // itself - "XboxGames\Game" is the umbrella folder the fallback walk enumerates directly;
        // "XboxGames\Game\Content" is what package registration actually reports. Exact-path dedup
        // missed this entirely (neither string equals the other), producing two GameEntry objects for
        // one real install. A completely unrelated sibling ("OtherGame", no package registration at all)
        // is included specifically to prove the fix doesn't over-suppress: it must still be found via
        // the ordinary fallback path, unaffected by the parent/Content relationship of its neighbor.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var testId = Guid.NewGuid();
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", $"GameLauncherTests_Parent_{testId}");
        var contentDir = Path.Combine(umbrellaDir, "Content");
        Directory.CreateDirectory(contentDir);
        var exePath = Path.Combine(contentDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[1000]);

        var siblingDir = Path.Combine(driveRoot, "XboxGames", $"GameLauncherTests_Sibling_{testId}");
        Directory.CreateDirectory(siblingDir);
        var siblingExe = Path.Combine(siblingDir, "Other.exe");
        File.WriteAllBytes(siblingExe, new byte[1000]);

        // Package registration reports the CONTENT subfolder, not the umbrella folder - and knows
        // nothing at all about the sibling.
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", contentDir, "Game")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            // Exactly one entry for the real install, at its actual (Content) install root - never a
            // second one for the umbrella folder the fallback walk would otherwise have processed too.
            Assert.Single(games, g => g.InstallDir == contentDir);
            Assert.DoesNotContain(games, g => g.InstallDir == umbrellaDir);

            // The unrelated sibling is untouched by the fix - still found via the ordinary fallback path.
            Assert.Single(games, g => g.InstallDir == siblingDir);
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(siblingDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_APackageRegisteredAtAnEmptySubfolder_NeverHidesTheRealGameElsewhereUnderTheSameParent()
    {
        // The regression this guards: containment-based dedup marks the TOP-LEVEL folder handled only
        // when the package registration actually produced a usable GameEntry - not merely because some
        // package claims to live under it. Here, package registration points at a subfolder that turns
        // out to be empty/unlaunchable (a stale or wrong registration - BuildCandidateEntry returns null
        // for it), while the REAL, launchable game content sits in a DIFFERENT subfolder of the very same
        // umbrella folder. If containment suppressed the parent based on the failed registration alone,
        // the fallback walk would never get a chance to find the real content at all - the game would
        // silently disappear entirely, not just get a "wrong" entry.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var testId = Guid.NewGuid();
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", $"GameLauncherTests_EmptyReg_{testId}");
        var emptyRegisteredSubfolder = Path.Combine(umbrellaDir, "StaleRegistration");
        Directory.CreateDirectory(emptyRegisteredSubfolder); // exists, but has nothing launchable in it
        var realContentDir = Path.Combine(umbrellaDir, "Content");
        Directory.CreateDirectory(realContentDir);
        var realExe = Path.Combine(realContentDir, "Game.exe");
        File.WriteAllBytes(realExe, new byte[1000]);

        // Package registration points at the EMPTY subfolder specifically - not Content, not the umbrella
        // folder itself.
        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", emptyRegisteredSubfolder, "StaleRegistration")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            // The real game is still found - via the fallback walk processing the (never-suppressed)
            // umbrella folder, since the failed package registration never marked it handled.
            var found = Assert.Single(games, g => g.InstallDir == umbrellaDir || g.InstallDir == realContentDir);
            Assert.NotEqual(emptyRegisteredSubfolder, found.ExecutablePath);
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- A manifest rejection deeper in the tree must not be undone by the shallower fallback walk -----

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_ConsoleOnlyManifestUnderContent_TheRealExeIsNeverPickedUpByTheParentFallbackWalk()
    {
        // The regression this guards: the manifest lives at "XboxGames\Game\Content\MicrosoftGame.config"
        // (where package registration actually points), NOT at the top-level "XboxGames\Game" folder the
        // fallback walk enumerates directly. Package processing at Content correctly rejects the
        // console-only declaration and produces no entry, which (correctly, per the empty-registration
        // test above) leaves the top-level folder eligible for fallback - but the fallback's OWN manifest
        // read at the top level finds NOTHING (there's no manifest there), and without carrying the
        // rejection forward, its search would happily find and launch the exact Console.exe the deeper
        // manifest already, explicitly, rejected.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", "GameLauncherTests_ConsoleReject_" + Guid.NewGuid());
        var contentDir = Path.Combine(umbrellaDir, "Content");
        Directory.CreateDirectory(contentDir);
        File.WriteAllText(Path.Combine(contentDir, "MicrosoftGame.config"),
            """<Game configVersion="1"><ExecutableList><Executable Name="Console.exe" Id="App" TargetDeviceFamily="Xbox" /></ExecutableList></Game>""");
        File.WriteAllBytes(Path.Combine(contentDir, "Console.exe"), new byte[1000]);

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", contentDir, "Game")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            Assert.DoesNotContain(games, g => g.InstallDir == umbrellaDir || g.InstallDir == contentDir);
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_UnresolvedAmbiguousManifestUnderContent_NeitherCandidateIsPickedUpByTheParentFallbackWalk()
    {
        // Same shape as above, for the OTHER rejection reason: two real, existing, equally-plausible
        // executables declared under Content, with no independent identity evidence to resolve between
        // them. The fallback walk at the top level must not treat this as "nothing here, search freely" -
        // it would otherwise confidently launch whichever of the two happens to be larger.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", "GameLauncherTests_AmbiguousReject_" + Guid.NewGuid());
        var contentDir = Path.Combine(umbrellaDir, "Content");
        Directory.CreateDirectory(contentDir);
        File.WriteAllText(Path.Combine(contentDir, "MicrosoftGame.config"), """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        File.WriteAllBytes(Path.Combine(contentDir, "A.exe"), new byte[5_000_000]);
        File.WriteAllBytes(Path.Combine(contentDir, "B.exe"), new byte[90_000_000]); // deliberately much larger

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", contentDir, "Game")];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            Assert.DoesNotContain(games, g => g.InstallDir == umbrellaDir || g.InstallDir == contentDir);
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
        }
    }

    [FactRequiresWritableXboxGamesFolder]
    public void Scan_UnresolvedAmbiguousManifestUnderContent_WithRealMatchingStartMenuEntries_StillSelectsNoAumidEither()
    {
        // The exact regression reported: an ambiguous Content manifest (A/B, neither independently
        // confirmed) PLUS real Start Menu entries that WOULD match via the fallback's free-text guess -
        // "Game" and "Game Companion", both textually matching the umbrella folder's own name. The OTHER
        // ambiguous-manifest fallback test above uses an EMPTY Start Menu list, which can't tell "blocked"
        // apart from "nothing happened to match anyway" - this one proves the block itself, not an
        // absence of candidates to wrongly pick from.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", "GameLauncherTests_AmbiguousStartMenu_" + Guid.NewGuid());
        var umbrellaName = Path.GetFileName(umbrellaDir);
        var contentDir = Path.Combine(umbrellaDir, "Content");
        Directory.CreateDirectory(contentDir);
        File.WriteAllText(Path.Combine(contentDir, "MicrosoftGame.config"), """
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="A.exe" Id="AppA" TargetDeviceFamily="PC" />
                <Executable Name="B.exe" Id="AppB" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """);
        File.WriteAllBytes(Path.Combine(contentDir, "A.exe"), new byte[5_000_000]);
        File.WriteAllBytes(Path.Combine(contentDir, "B.exe"), new byte[90_000_000]);

        XboxPackageDiscovery.PackagesOverrideForTest = () => [new XboxPackageInfo("Fam_8wekyb3d8bbwe", contentDir, umbrellaName)];
        // A DIFFERENT, unrelated package family than the one actually rejected - so this can only ever be
        // reached through the fallback's own free-text (no-package-identity) tier, never through the
        // rejected package's own (already-blocked) identity-scoped tiers.
        StartAppsResolver.PackagedAppsOverrideForTest = () =>
        [
            (umbrellaName, "Unrelated_8wekyb3d8bbwe!Game"),
            ($"{umbrellaName} Companion", "Unrelated_8wekyb3d8bbwe!Companion"),
        ];

        try
        {
            var games = XboxScanner.Scan();

            Assert.DoesNotContain(games, g => g.InstallDir == umbrellaDir || g.InstallDir == contentDir);
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- Identity is stable regardless of what else is (or isn't) also registered on any given scan ----

    [Fact]
    public void Scan_TheSameGamesId_NeverChanges_AsASiblingRegistrationAppearsAndDisappears_EvenWhenUnusable()
    {
        // The regression this guards: an EARLIER version of this scanner decided whether a game's Id
        // could safely be the shared, folder-based one by counting how many OTHER packages claimed the
        // same top-level folder AT SCAN TIME - meaning the SAME real game's Id could silently change
        // depending on whether some unrelated (even genuinely unusable/stale) second registration
        // happened to exist that particular day, disconnecting its favorites/hidden-state/artwork for no
        // reason tied to the game itself. Id is now anchored to PackageFamilyName alone (see the class
        // remarks) - intrinsic to this one package, never affected by what else is or isn't registered
        // alongside it. One registration, then two (one genuinely unusable), then one again - the real
        // game's Id must be identical all three times, and a saved override survives right along with it.
        // Not under a real XboxGames folder here (this test doesn't need one - it's about sibling-count
        // independence, not location) - a MicrosoftGame.config is what satisfies the "is this a game"
        // gate instead, exactly as XboxScanner's own remarks describe for a custom install path.
        var contentDir = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentDir);
        File.WriteAllText(Path.Combine(contentDir, "MicrosoftGame.config"),
            """<Game configVersion="1"><ExecutableList><Executable Name="Game.exe" Id="App" /></ExecutableList></Game>""");
        File.WriteAllBytes(Path.Combine(contentDir, "Game.exe"), new byte[1000]);
        var gamePackage = new XboxPackageInfo("Fam1_8wekyb3d8bbwe", contentDir, "Game");
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        XboxPackageDiscovery.PackagesOverrideForTest = () => [gamePackage];
        var idBefore = Assert.Single(XboxScanner.Scan(), g => g.InstallDir == contentDir).Id;

        var savedOverrides = new Dictionary<string, GameOverride> { [idBefore] = new GameOverride { Favorite = true } };

        // An unusable second registration appears - empty, no manifest, no exe, no Start Menu entry.
        // Nothing about the real game's OWN registration changed at all.
        var unusableDir = Path.Combine(_root, "Unusable");
        Directory.CreateDirectory(unusableDir);
        var unusablePackage = new XboxPackageInfo("Fam2_8wekyb3d8bbwe", unusableDir, "Unusable");
        XboxPackageDiscovery.PackagesOverrideForTest = () => [gamePackage, unusablePackage];
        var gamesDuring = XboxScanner.Scan();
        var idDuring = Assert.Single(gamesDuring, g => g.InstallDir == contentDir).Id;
        Assert.DoesNotContain(gamesDuring, g => g.InstallDir == unusableDir); // confirms it really was unusable, not silently added

        // The second registration disappears again.
        XboxPackageDiscovery.PackagesOverrideForTest = () => [gamePackage];
        var idAfter = Assert.Single(XboxScanner.Scan(), g => g.InstallDir == contentDir).Id;

        Assert.Equal(idBefore, idDuring);
        Assert.Equal(idBefore, idAfter);
        Assert.True(savedOverrides.ContainsKey(idDuring)); // the saved override is still found at every stage - same key, unchanged
        Assert.True(savedOverrides.ContainsKey(idAfter));
    }

    // ---- Identity continuity: an install already in someone's library must keep its Id across upgrade ---

    [Fact]
    public void PackageWithKnownIdentity_IdIsFamilyBased_LegacyIdCarriesTheOldFolderHashedOne()
    {
        // Id itself is now anchored to PackageFamilyName (stable regardless of siblings/subfolder depth -
        // see the class remarks) - NOT the same value the pre-registration scanner would have hashed for
        // this same top-level "XboxGames\Game" folder. LegacyId is what carries THAT old value forward,
        // so GameScannerService can still find an existing override under it (next test).
        var topLevelFolder = _root; // stands in for "XboxGames\Game" - the value the OLD scanner hashed
        var contentRoot = Path.Combine(topLevelFolder, "Content");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllBytes(Path.Combine(contentRoot, "Game.exe"), new byte[1000]);

        // "Old style": as if the pre-registration scanner found this by walking the top-level folder
        // directly - no package identity, no canonicalIdRoot override, so installRoot itself (the folder
        // itself, in that old world) is what Id hashed to.
        var oldStyleEntry = XboxScanner.BuildCandidateEntry(topLevelFolder, "Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);

        // "New": package registration reports the nested Content root, WITH a real package identity -
        // exactly what Scan() itself now passes through.
        var newStyleEntry = XboxScanner.BuildCandidateEntry(contentRoot, "Game", "Fam_8wekyb3d8bbwe", NoPackagedApps,
            CancellationToken.None, out _, canonicalIdRoot: topLevelFolder);

        Assert.NotNull(oldStyleEntry);
        Assert.NotNull(newStyleEntry);
        Assert.NotEqual(oldStyleEntry.Id, newStyleEntry.Id); // family-based now, not folder-based - deliberately different
        Assert.Equal(oldStyleEntry.Id, newStyleEntry.LegacyId); // but LegacyId is exactly what the old Id was
        // The two entries deliberately do NOT share InstallDir/ExecutablePath - only identity is anchored
        // to the top-level folder; the actual install location stays precise.
        Assert.NotEqual(oldStyleEntry.InstallDir, newStyleEntry.InstallDir);
    }

    [Fact]
    public void PackageWithKnownIdentity_ASavedOverrideKeyedOnTheOldId_IsFoundViaLegacyId()
    {
        // The concrete, user-visible consequence of the LegacyId mechanism: GameScannerService falls back
        // to looking up an override by LegacyId when none exists yet under the new, family-based Id (its
        // own remarks explain why) - this proves that fallback lookup itself succeeds for a real override
        // dictionary shaped exactly like AppSettings.Overrides, without needing the full scan pipeline.
        var topLevelFolder = _root;
        var contentRoot = Path.Combine(topLevelFolder, "Content");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllBytes(Path.Combine(contentRoot, "Game.exe"), new byte[1000]);

        var oldStyleEntry = XboxScanner.BuildCandidateEntry(topLevelFolder, "Game", packageFamilyName: null, NoPackagedApps, CancellationToken.None, out _);
        Assert.NotNull(oldStyleEntry);

        // A real override, as it would exist in AppSettings.Overrides from before the upgrade.
        var savedOverrides = new Dictionary<string, GameOverride>
        {
            [oldStyleEntry.Id] = new GameOverride { Favorite = true, CustomName = "My Renamed Game" },
        };

        var newStyleEntry = XboxScanner.BuildCandidateEntry(contentRoot, "Game", "Fam_8wekyb3d8bbwe", NoPackagedApps,
            CancellationToken.None, out _, canonicalIdRoot: topLevelFolder);
        Assert.NotNull(newStyleEntry);

        // Not found directly under the new Id...
        Assert.False(savedOverrides.ContainsKey(newStyleEntry.Id));
        // ...but IS found via LegacyId, exactly the lookup GameScannerService itself now performs.
        Assert.NotNull(newStyleEntry.LegacyId);
        Assert.True(savedOverrides.TryGetValue(newStyleEntry.LegacyId!, out var found), "The saved override was not found under LegacyId - it would silently orphan on upgrade.");
        Assert.True(found.Favorite);
        Assert.Equal("My Renamed Game", found.CustomName);
    }

    // ---- Two DIFFERENT real packages sharing one umbrella folder must never collide on Id -------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_TwoPackagesSharingOneCanonicalFolder_NeverCollideOnId_RegardlessOfEnumerationOrder(bool reverseOrder)
    {
        // A base game and a separately-registered companion app both happening to live under the same
        // umbrella "XboxGames\Game" folder (Content and Companion, respectively) - both are real,
        // distinct, independently-launchable installs. Collapsing both onto the folder's single, shared,
        // historical Id (the normal continuity behavior for the common one-package case) would either
        // merge their favorites/hidden-state/artwork or produce two GameEntry objects with an IDENTICAL
        // Id - and picking one arbitrarily by processing order would make WHICH one keeps the shared id
        // pure luck. Run with the package list in both orders to prove the outcome doesn't depend on it.
        var driveRoot = XboxGamesFolderSupport.ReadyDriveRoot.Value!;
        var umbrellaDir = Path.Combine(driveRoot, "XboxGames", "GameLauncherTests_SharedFolder_" + Guid.NewGuid());
        var contentDir = Path.Combine(umbrellaDir, "Content");
        var companionDir = Path.Combine(umbrellaDir, "Companion");
        Directory.CreateDirectory(contentDir);
        Directory.CreateDirectory(companionDir);
        File.WriteAllBytes(Path.Combine(contentDir, "Game.exe"), new byte[5_000_000]);
        File.WriteAllBytes(Path.Combine(companionDir, "Companion.exe"), new byte[1_000_000]);

        var gamePackage = new XboxPackageInfo("Fam1_8wekyb3d8bbwe", contentDir, "Game");
        var companionPackage = new XboxPackageInfo("Fam2_8wekyb3d8bbwe", companionDir, "Companion");
        XboxPackageDiscovery.PackagesOverrideForTest = () => reverseOrder ? [companionPackage, gamePackage] : [gamePackage, companionPackage];
        StartAppsResolver.PackagedAppsOverrideForTest = () => [];

        try
        {
            var games = XboxScanner.Scan();

            var relevant = games.Where(g => g.InstallDir == contentDir || g.InstallDir == companionDir).ToList();
            Assert.Equal(2, relevant.Count); // both are real, launchable installs - neither is dropped
            Assert.Equal(relevant.Count, relevant.Select(g => g.Id).Distinct().Count()); // and they never share an Id
        }
        finally
        {
            try { Directory.Delete(umbrellaDir, recursive: true); } catch (IOException) { }
        }
    }
}
