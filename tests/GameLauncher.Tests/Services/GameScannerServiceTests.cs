using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises GameScannerService.DeduplicateByInstallLocation directly against scripted GameEntry lists
/// - no real scanning involved. Covers the real, confirmed A Way Out case (a Manual entry discovering a
/// launcher-verified game's executable several folders below its true root) as well as the cases that
/// must NOT merge, so the nesting rule stays narrowly scoped to "reconcile a Manual entry into a
/// launcher's own verified install," not a general "any nested path is a duplicate" rule.
/// </summary>
public class GameScannerServiceTests
{
    private static GameEntry MakeGame(string id, string installDir, GameSource source, string name = "Game", string? executablePath = null) => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = executablePath ?? installDir + @"\game.exe",
        InstallDir = installDir,
        Source = source,
    };

    [Fact]
    public void ManualEntryNestedUnderLauncherRoot_WithMatchingExecutable_MergesIntoTheLauncherEntry()
    {
        // The exact real A Way Out shape: EA's own root is the true install location; a manual scan of
        // the same drive discovers the executable's own containing folder, several levels deeper, as an
        // unrelated-looking second "game" - but BOTH scans resolve to the identical AWayOut.exe file,
        // which is the actual corroboration that makes this safe to merge (see FindMergeTarget).
        const string sharedExe = @"G:\EASports\AWayOut\Haze1\Binaries\Win64\AWayOut.exe";
        var eaEntry = MakeGame("ea-awayout", @"G:\EASports\AWayOut", GameSource.Ea, "AWayOut", sharedExe);
        var manualEntry = MakeGame("manual-haze1", @"G:\EASports\AWayOut\Haze1\Binaries\Win64", GameSource.Manual, "Haze1", sharedExe);

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEntry]);

        var survivor = Assert.Single(deduped);
        Assert.Same(eaEntry, survivor); // the launcher-detected entry survives, not the manual one
        Assert.Equal("ea-awayout", mergedIds["manual-haze1"]); // loser id -> winner id
    }

    [Fact]
    public void ManualScannerPicksTheFriendsPassBuild_LauncherStillMergesCorrectly_VerifiedExecutableContext()
    {
        // The exact confirmed disagreement: EA's scan correctly excludes A Way Out's Friend's Pass trial
        // build (AWayOut_friend.exe) via GameExeFinder's EA-specific pattern, but ManualFolderScanner's
        // own, separate exclusion list does not know about it - if that build happens to be the larger
        // file, Manual's own independently-computed ExecutablePath points at the WRONG file, even though
        // its InstallDir (the exe's containing folder) is identical either way. A plain "do both sides'
        // ExecutablePath strings match exactly" check would refuse this merge - the fix instead checks
        // whether the LAUNCHER's own verified exe lands inside the Manual candidate's InstallDir subtree,
        // which it does here regardless of what Manual's own (wrong) guess was.
        const string installRoot = @"G:\EASports\AWayOut";
        const string win64Dir = @"G:\EASports\AWayOut\Haze1\Binaries\Win64";
        var eaEntry = MakeGame("ea-awayout", installRoot, GameSource.Ea, "AWayOut", win64Dir + @"\AWayOut.exe");
        var manualEntry = MakeGame("manual-haze1", win64Dir, GameSource.Manual, "Haze1", win64Dir + @"\AWayOut_friend.exe");

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEntry]);

        var survivor = Assert.Single(deduped);
        Assert.Same(eaEntry, survivor); // the correct, EA-verified executable path is what's kept
        Assert.Equal("ea-awayout", mergedIds["manual-haze1"]);
    }

    [Fact]
    public void FriendsPassStyleSuffix_IsOnlyAcceptedForEaSource_NotForOtherLaunchers()
    {
        // The real, confirmed gap: IsVerifiedSameExecutable used to check only the filenames, not which
        // source the launcher entry came from - so a Steam/Xbox/Ubisoft/etc. game whose folder happened
        // to also contain an unrelated "Foo_friend.exe" would have been accepted as a "Friend's Pass"
        // match too, even though that naming convention is specific to EA. Same filename pair, same
        // folder, as the confirmed EA case - only the Source differs.
        // Launcher root and the exe's own (nested) folder must differ - an IDENTICAL InstallDir on both
        // sides would hit the unconditional exact-InstallDir-match rule instead, never actually
        // exercising IsVerifiedSameExecutable's source check at all.
        const string installRoot = @"C:\Games\Foo";
        const string binDir = @"C:\Games\Foo\Bin";
        var realExe = binDir + @"\Foo.exe";
        var friendExe = binDir + @"\Foo_friend.exe";

        foreach (var nonEaSource in new[] { GameSource.Steam, GameSource.Epic, GameSource.Gog, GameSource.Xbox, GameSource.Ubisoft })
        {
            var launcherEntry = MakeGame($"{nonEaSource}-foo", installRoot, nonEaSource, "Foo", realExe);
            var manualEntry = MakeGame($"manual-foo-{nonEaSource}", binDir, GameSource.Manual, "Foo", friendExe);

            var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([launcherEntry, manualEntry]);

            Assert.Equal(2, deduped.Count); // both survive - the exception does not apply outside EA
            Assert.Empty(mergedIds);
        }

        // The EA case, side by side with the same filenames, still merges correctly.
        var eaEntry = MakeGame("ea-foo", installRoot, GameSource.Ea, "Foo", realExe);
        var manualEaEntry = MakeGame("manual-foo-ea", binDir, GameSource.Manual, "Foo", friendExe);

        var (eaDeduped, eaMergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEaEntry]);

        var survivor = Assert.Single(eaDeduped);
        Assert.Same(eaEntry, survivor);
        Assert.Equal("ea-foo", eaMergedIds["manual-foo-ea"]);
    }

    [Fact]
    public void CombinedScannerAndDedupRegression_FriendsPassLarger_RealFilesThroughTheActualSelectionLogic()
    {
        // End-to-end regression against REAL temp files run through the ACTUAL scanning logic each side
        // uses - not hand-constructed GameEntry objects, and not a stand-in for ManualFolderScanner
        // (GameExeFinder alone doesn't exercise its own traversal, its own separate exclusion list, or
        // its own entry construction/naming - only the real ManualFolderScanner.Scan call does). "EA"
        // uses GameExeFinder with EA's own Friend's Pass exclusion pattern, matching EaScanner's real
        // call; "Manual" runs the real ManualFolderScanner.Scan against a WatchedFolder covering the same
        // temp tree, matching what a real scan would actually produce.
        var root = Path.Combine(Path.GetTempPath(), "GameLauncherTests_CombinedDedup_" + Guid.NewGuid());
        var win64Dir = Path.Combine(root, "Haze1", "Binaries", "Win64");
        Directory.CreateDirectory(win64Dir);
        var realExe = Path.Combine(win64Dir, "AWayOut.exe");
        var friendExe = Path.Combine(win64Dir, "AWayOut_friend.exe");
        File.WriteAllBytes(realExe, new byte[50_000_000]);
        File.WriteAllBytes(friendExe, new byte[90_000_000]); // deliberately the larger file

        try
        {
            var eaExe = GameExeFinder.FindLargestExe(root, extraExcludePatterns: [GameExeFinder.EaFriendsPassExcludePattern]);
            Assert.Equal(realExe, eaExe); // sanity: EA's own selection is correct
            var eaEntry = MakeGame("ea-awayout", root, GameSource.Ea, "AWayOut", eaExe!);

            var manualResults = ManualFolderScanner.Scan([new WatchedFolder { Path = root }]);
            var manualEntry = Assert.Single(manualResults);
            Assert.Equal(friendExe, manualEntry.ExecutablePath); // sanity: this IS the real, confirmed disagreement
            Assert.Equal("Haze1", manualEntry.Name); // sanity: the wrong, internal-codename display name

            var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEntry]);

            var survivor = Assert.Single(deduped);
            Assert.Same(eaEntry, survivor);
            Assert.Equal("ea-awayout", mergedIds[manualEntry.Id]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManualEntryNestedUnderLauncherRoot_WithDifferentExecutable_DoesNotMerge_AmbiguousCase()
    {
        // Nesting alone is not proof of common ownership - a genuinely different game that a user
        // happened to manually drop inside a launcher's own install folder must NOT be swallowed into
        // that launcher's entry purely because of where it physically sits. Only ONE ancestor candidate
        // exists here (unlike the Base/Expansion cases below), so this proves executable corroboration
        // is required even in the simplest, single-ancestor case - not just as a tiebreak between
        // multiple candidates.
        var eaEntry = MakeGame("ea-base", @"C:\Games\Base", GameSource.Ea, executablePath: @"C:\Games\Base\Base.exe");
        var unrelatedManualGame = MakeGame("manual-indie", @"C:\Games\Base\extras\SomeOtherGame", GameSource.Manual,
            "SomeOtherGame", @"C:\Games\Base\extras\SomeOtherGame\SomeOtherGame.exe");

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, unrelatedManualGame]);

        Assert.Equal(2, deduped.Count); // both survive, unmerged - genuinely ambiguous
        Assert.Empty(mergedIds);
    }

    [Fact]
    public void LauncherExeNestedDeeperThanASeparateManualGameInTheSameParentFolder_DoesNotMerge_ContainmentIsNotEnough()
    {
        // The real, confirmed false-merge counterexample to subtree CONTAINMENT (an earlier version of
        // this check): EA's own exe sits at Base\Bin\Win64\Base.exe (folder Base\Bin\Win64), and a
        // Manual scan separately, correctly stops at Base\Bin\Indie.exe (folder Base\Bin - NOT
        // descending further, per ManualFolderScanner's own "claimed" rule). The launcher's exe is
        // technically located SOMEWHERE under Base\Bin - but Base\Bin\Win64 is not Base\Bin, so these
        // are two genuinely separate games sharing a common parent folder, not one game found two ways.
        // Exact folder equality must refuse this; mere containment would not.
        var eaEntry = MakeGame("ea-base", @"C:\Games\Base", GameSource.Ea, executablePath: @"C:\Games\Base\Bin\Win64\Base.exe");
        var indieGame = MakeGame("manual-indie", @"C:\Games\Base\Bin", GameSource.Manual, "Indie", @"C:\Games\Base\Bin\Indie.exe");

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, indieGame]);

        Assert.Equal(2, deduped.Count); // both survive, unmerged - genuinely two different games
        Assert.Empty(mergedIds);
    }

    [Fact]
    public void SameFolderContainsAGenuinelyUnrelatedGame_DoesNotMerge_FolderEqualityAloneIsNotEnough()
    {
        // The real, confirmed false-merge counterexample to folder EQUALITY alone (an earlier version of
        // this check): a watched folder covering Base\Bin directly, which genuinely contains BOTH the
        // launcher-verified game's own Base.exe AND a completely separate, unrelated Indie.exe as
        // siblings - ManualFolderScanner emits an entry for EACH loose exe found directly in a watched
        // root (see its own Walk method), so both end up with the IDENTICAL InstallDir "Base\Bin". Indie
        // is not EA's game, not a Friend's Pass variant of it, and must not be merged just because it
        // happens to live in the same folder as something that is.
        var eaEntry = MakeGame("ea-base", @"C:\Games\Base", GameSource.Ea, executablePath: @"C:\Games\Base\Bin\Base.exe");
        var baseAsManual = MakeGame("manual-base", @"C:\Games\Base\Bin", GameSource.Manual, "Base", @"C:\Games\Base\Bin\Base.exe");
        var indieGame = MakeGame("manual-indie", @"C:\Games\Base\Bin", GameSource.Manual, "Indie", @"C:\Games\Base\Bin\Indie.exe");

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, baseAsManual, indieGame]);

        // The EA entry survives, its own manual rediscovery (identical exe) correctly merges into it -
        // but Indie, sharing only the folder and nothing else, survives as its own separate game.
        Assert.Equal(2, deduped.Count);
        Assert.Contains(eaEntry, deduped);
        Assert.Contains(indieGame, deduped);
        Assert.Equal("ea-base", mergedIds["manual-base"]);
        Assert.False(mergedIds.ContainsKey("manual-indie"));
    }

    [Fact]
    public void ManualEntryExactlyMatchingLauncherInstallDir_StillMerges_NoExecutableCorroborationNeeded()
    {
        // The original, unconditional rule: an IDENTICAL install location is the same game by
        // definition, regardless of what each side thinks the launch executable is - deliberately
        // different exe paths here to prove this rule doesn't depend on FindMergeTarget's nesting-only
        // executable check at all.
        var eaEntry = MakeGame("ea-game", @"C:\Games\Foo", GameSource.Ea, executablePath: @"C:\Games\Foo\Foo.exe");
        var manualEntry = MakeGame("manual-game", @"C:\Games\Foo", GameSource.Manual, executablePath: @"C:\Games\Foo\OtherLauncher.exe");

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEntry]);

        Assert.Single(deduped);
        Assert.Equal("ea-game", mergedIds["manual-game"]);
    }

    [Fact]
    public void TwoUnrelatedManualEntries_NeverMergeViaNesting()
    {
        // Both entries are Manual - the nesting rule only ever fires when exactly one side is Manual,
        // so two manual entries must never be silently merged just because one path happens to sit
        // inside the other (ManualFolderScanner's own "claimed: don't descend" rule already prevents
        // this from happening for entries it discovers itself, but this proves the dedup step doesn't
        // introduce a NEW way for it to happen either).
        var outer = MakeGame("manual-outer", @"D:\Games\Outer", GameSource.Manual);
        var inner = MakeGame("manual-inner", @"D:\Games\Outer\Inner", GameSource.Manual);

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([outer, inner]);

        Assert.Equal(2, deduped.Count); // both survive, untouched
        Assert.Empty(mergedIds);
    }

    [Fact]
    public void TwoLauncherEntries_NeverMergeViaNesting_DlcOrExpansionCase()
    {
        // A base game and a separately-registered DLC/expansion installed as its own subfolder must
        // never be silently swallowed just because one path sits inside the other - only a Manual
        // entry ever gets reconciled away like this.
        var baseGame = MakeGame("ea-base", @"C:\Games\Base", GameSource.Ea);
        var expansion = MakeGame("ea-dlc", @"C:\Games\Base\Expansion", GameSource.Ea);

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([baseGame, expansion]);

        Assert.Equal(2, deduped.Count);
        Assert.Empty(mergedIds);
    }

    // ---- The concrete bug: base game + its own expansion, both ancestors of one nested Manual entry --

    [Theory]
    [InlineData(false)] // base game scanned/kept first
    [InlineData(true)]  // expansion scanned/kept first - result must be identical either way
    public void ManualEntryNestedUnderBaseAndExpansionBoth_MergesIntoTheMoreSpecificExpansion_NotTheBase(bool expansionFirst)
    {
        const string sharedExe = @"C:\Games\Base\Expansion\Bin\Expansion.exe";
        var baseGame = MakeGame("ea-base", @"C:\Games\Base", GameSource.Ea, executablePath: @"C:\Games\Base\Base.exe");
        var expansion = MakeGame("ea-expansion", @"C:\Games\Base\Expansion", GameSource.Ea, executablePath: sharedExe);
        var manualEntry = MakeGame("manual-bin", @"C:\Games\Base\Expansion\Bin", GameSource.Manual, executablePath: sharedExe);

        var input = expansionFirst
            ? new List<GameEntry> { expansion, baseGame, manualEntry }
            : new List<GameEntry> { baseGame, expansion, manualEntry };

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation(input);

        Assert.Equal(2, deduped.Count); // base and expansion both survive as separate games
        Assert.Contains(baseGame, deduped);
        Assert.Contains(expansion, deduped);
        Assert.Equal("ea-expansion", mergedIds["manual-bin"]); // merged into the MORE SPECIFIC owner
    }

    [Fact]
    public void UnrelatedEntriesAtDifferentPaths_AllSurviveIndependently()
    {
        var a = MakeGame("ea-a", @"C:\Games\A", GameSource.Ea);
        var b = MakeGame("manual-b", @"D:\Games\B", GameSource.Manual);

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([a, b]);

        Assert.Equal(2, deduped.Count);
        Assert.Empty(mergedIds);
    }

    [Fact]
    public void SimilarlyNamedSiblingPaths_DoNotFalselyMerge()
    {
        // "AWayOut" is not an ancestor of "AWayOut2" as a STRING prefix, but IsAncestorDir requires
        // the full path separator, so this must not falsely match.
        var eaEntry = MakeGame("ea-game", @"C:\Games\AWayOut", GameSource.Ea);
        var manualEntry = MakeGame("manual-game", @"C:\Games\AWayOut2\Sub", GameSource.Manual);

        var (deduped, mergedIds) = GameScannerService.DeduplicateByInstallLocation([eaEntry, manualEntry]);

        Assert.Equal(2, deduped.Count);
        Assert.Empty(mergedIds);
    }

    // ---- SafeApplyCoverArt: both the primary attempt AND its own icon fallback can fail -------------

    [Fact]
    public void CoverArtThrows_IconFallbackSucceeds_UsesTheFallbackIcon()
    {
        var game = MakeGame("game-1", @"C:\Games\Test", GameSource.Manual);
        var fallbackIcon = new System.Windows.Media.Imaging.BitmapImage();

        GameScannerService.SafeApplyCoverArt(game, null, existingSelection: null,
            applyCoverArt: (_, _) => throw new InvalidOperationException("simulated cover art failure"),
            getIcon: _ => fallbackIcon);

        Assert.Same(fallbackIcon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void CoverArtThrows_AndIconFallbackAlsoThrows_LeavesIconNull_DoesNotThrow()
    {
        // The exact case this exists for: a corrupted/unreadable exe fails icon extraction too - both
        // attempts fail, but the method itself must still return normally (Icon null, IsCoverArt false)
        // rather than letting the second exception escape and abort enrichment for every other game
        // still waiting in GameScannerService's own per-game loop.
        var game = MakeGame("game-1", @"C:\Games\Test", GameSource.Manual);
        game.Icon = new System.Windows.Media.Imaging.BitmapImage(); // pre-existing value must be cleared, not left stale

        var exception = Record.Exception(() => GameScannerService.SafeApplyCoverArt(game, null, existingSelection: null,
            applyCoverArt: (_, _) => throw new InvalidOperationException("simulated cover art failure"),
            getIcon: _ => throw new InvalidOperationException("simulated icon extraction failure")));

        Assert.Null(exception);
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void OneGamesDoubleFailure_DoesNotPreventTheNextGameFromBeingEnriched()
    {
        // Directly proves the "cannot prevent subsequent games from being enriched" requirement: two
        // games processed through the same loop shape SafeApplyCoverArt is actually called from - the
        // first fails both attempts, the second must still be enriched normally afterward.
        var failingGame = MakeGame("game-1", @"C:\Games\Failing", GameSource.Manual);
        var nextGame = MakeGame("game-2", @"C:\Games\Next", GameSource.Manual);
        var nextGameIcon = new System.Windows.Media.Imaging.BitmapImage();

        foreach (var game in new[] { failingGame, nextGame })
        {
            GameScannerService.SafeApplyCoverArt(game, null, existingSelection: null,
                applyCoverArt: (g, _) =>
                {
                    if (g.Id == "game-1")
                        throw new InvalidOperationException("simulated cover art failure");
                    g.Icon = nextGameIcon;
                    g.IsCoverArt = false;
                    return null;
                },
                getIcon: _ => throw new InvalidOperationException("simulated icon extraction failure"));
        }

        Assert.Null(failingGame.Icon);
        Assert.False(failingGame.IsCoverArt);
        Assert.Same(nextGameIcon, nextGame.Icon); // the second game was still enriched normally
    }

    // ---- SafeApplyCoverArt: routing an existing user selection vs running the automatic matcher -----

    [Fact]
    public void ExistingUserSelection_RoutesToStoredArtwork_NeverRunsTheAutomaticMatcher()
    {
        var game = MakeGame("game-1", @"C:\Games\Test", GameSource.Manual);
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        var automaticMatcherCalled = false;
        var storedArtworkCalled = false;

        var result = GameScannerService.SafeApplyCoverArt(game, null, selection,
            applyCoverArt: (_, _) => { automaticMatcherCalled = true; return null; },
            applyStoredArtworkSafely: (_, _) => storedArtworkCalled = true);

        Assert.False(automaticMatcherCalled);
        Assert.True(storedArtworkCalled);
        Assert.Null(result); // nothing new to persist - the existing selection stands
    }

    [Fact]
    public void NoExistingSelection_RunsTheAutomaticMatcher_NeverTouchesStoredArtwork()
    {
        var game = MakeGame("game-1", @"C:\Games\Test", GameSource.Manual);
        var automaticResult = new ArtworkSelection { MatchMethod = "ExactTitle" };
        var storedArtworkCalled = false;

        var result = GameScannerService.SafeApplyCoverArt(game, null, existingSelection: null,
            applyCoverArt: (_, _) => automaticResult,
            applyStoredArtworkSafely: (_, _) => storedArtworkCalled = true);

        Assert.False(storedArtworkCalled);
        Assert.Same(automaticResult, result);
    }

    [Fact]
    public void ExistingAutomaticSelection_NotUserSelected_StillRunsTheAutomaticMatcher()
    {
        // A previously-recorded AUTOMATIC selection (IsUserSelected: false) is not authoritative the
        // way a user's own pick is - it's just metadata from a prior match, and a fresh scan is free to
        // re-evaluate it exactly like having no selection at all.
        var game = MakeGame("game-1", @"C:\Games\Test", GameSource.Manual);
        var existing = new ArtworkSelection { MatchMethod = "ExactTitle", IsUserSelected = false };
        var automaticMatcherCalled = false;

        GameScannerService.SafeApplyCoverArt(game, null, existing,
            applyCoverArt: (_, _) => { automaticMatcherCalled = true; return null; },
            applyStoredArtworkSafely: (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.True(automaticMatcherCalled);
    }
}
