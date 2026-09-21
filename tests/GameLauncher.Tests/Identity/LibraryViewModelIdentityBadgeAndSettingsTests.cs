using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>The card badge (an invitation to identify a game automatic resolution could not settle) and the IGDB credential
/// settings the whole IGDB-primary design depends on the user being able to enter. All against isolated temp directories and
/// synthetic strings: no test here reads, writes or prints a real credential.</summary>
public class LibraryViewModelIdentityBadgeAndSettingsTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        _h.Dispose();
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    // ---- The badge --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoConfidentMatch_BadgesTheGame_WithAnActionableTooltip()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));

        await _h.Scan(game); // both catalogs answer NoMatch by default

        Assert.True(game.NeedsIdentity);
        Assert.Contains("No confident match", game.IdentityBadgeText);
        Assert.Contains("Right-click", game.IdentityBadgeText);
    }

    [Fact]
    public async Task AnAmbiguousName_BadgesTheGame_AndSaysItWasNotGuessed()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        _h.Igdb.Title = _ => CatalogSearchResult.Ambiguous();
        _h.Sgdb.Title = _ => CatalogSearchResult.Ambiguous();

        await _h.Scan(game);

        Assert.True(game.NeedsIdentity);
        Assert.Contains("not guessed", game.IdentityBadgeText);
    }

    [Fact]
    public async Task AContradictedMatch_BadgesTheGame()
    {
        var game = await _h.Add(Games.Steam("1091500", "Cyberpunk 2077"));
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Cyberpunk 2077");
        _h.Igdb.Consistency = (_, _) => LauncherConsistency.Contradicted;

        await _h.Scan(game);

        Assert.True(game.NeedsIdentity);
        Assert.Contains("disagreed with the launcher", game.IdentityBadgeText);
    }

    [Fact]
    public async Task AnIdentifiedGame_IsNotBadged()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");

        await _h.Scan(game);

        Assert.False(game.NeedsIdentity);
        Assert.Equal("", game.IdentityBadgeText);
    }

    [Fact]
    public async Task ACatalogOutage_DoesNotBadgeTheGame_ItIsRetriedNotAccused()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        _h.Igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _h.Sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        await _h.Scan(game);

        Assert.False(game.NeedsIdentity);
    }

    [Fact]
    public async Task ConfirmingAnIdentity_ClearsTheBadge_AndClearingRetriesAtOnce_SoTheBadgeFollowsThatFreshAttempt()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        await _h.Scan(game);
        Assert.True(game.NeedsIdentity);

        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id,
            new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null), state.DecisionRevision, state.IdentityRevision);
        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.False(game.NeedsIdentity);

        // Clear goes back to automatic AND runs one forced automatic unit immediately (design 6.6): the badge reports what that
        // fresh attempt found, not a stale earlier verdict.
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        state = _h.Vm.GetIdentityDialogState(game.Id)!;
        Assert.Equal(IdentityChangeOutcome.Success, await _h.Vm.ClearIdentityAsync(game.Id, state.DecisionRevision, state.IdentityRevision));
        Assert.False(game.NeedsIdentity);                    // the catalog now knows it
        Assert.Equal(IdentityState.AutoResolved, _h.Vm.GetIdentityDialogState(game.Id)!.State);

        // A never-resolved game: Confirm, then Clear - the forced attempt finds nothing, so the badge comes back.
        _h.Igdb.Title = _ => CatalogSearchResult.NoMatch();
        var other = await _h.Add(Games.Manual("manual-bar", "Bar"));
        await _h.Scan(other);
        state = _h.Vm.GetIdentityDialogState(other.Id)!;
        await _h.Vm.ConfirmIdentityAsync(other.Id, new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null), state.DecisionRevision, state.IdentityRevision);
        Assert.False(other.NeedsIdentity);
        state = _h.Vm.GetIdentityDialogState(other.Id)!;
        await _h.Vm.ClearIdentityAsync(other.Id, state.DecisionRevision, state.IdentityRevision);
        Assert.True(other.NeedsIdentity);                    // the fresh attempt failed, so it is badged again
    }

    [Fact]
    public async Task APinnedCover_DoesNotHideTheBadge_IdentityAndArtworkAreSeparate()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Badge-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { await _h.Vm.ApplyLocalCoverImageAsync(game.Id, png); }
        finally { File.Delete(png); }

        await _h.Scan(game);

        Assert.True(game.IsCoverArt);
        Assert.True(game.NeedsIdentity);
    }

    [Fact]
    public async Task AQuarantinedRecord_IsBadged_WithoutTouchingIt()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        using var doc = JsonDocument.Parse("""{"Confirmed":{"Key":"garbage"},"future":{"x":1}}""");
        _h.Vm.EnsureOverrideForTest(game.Id).Identity = new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };

        await _h.Scan(game);

        Assert.True(game.NeedsIdentity);
        Assert.Contains("couldn't be read", game.IdentityBadgeText);
        Assert.True(_h.Record(game.Id)!.IsQuarantined);
    }

    [Fact]
    public async Task AnUpgradedLibraryStillWaitingOnLegacyRevalidation_IsNotBadged_ItsCoverIsFine()
    {
        var game = await _h.Add(Games.Manual("manual-foo", "Foo"));
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord();
        over.Identity.LegacyEvidence.Add(new LegacyAssociation
        {
            Namespace = Cat.Sgdb, Id = "777", Title = "Old Match", SourceProvider = ArtworkProvider.SteamGridDb, Status = LegacyStatus.Pending,
        });
        _h.Igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _h.Sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        await _h.Scan(game);

        Assert.False(game.NeedsIdentity);
    }

    // ---- IGDB credentials in Settings ---------------------------------------------------------------------------------------

    private LibraryViewModel FreshViewModel(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdSettings-" + Guid.NewGuid());
        _dirs.Add(dir);
        // hermetic: no relay unless a test asks for one, whatever address this build happens to embed
        return new LibraryViewModel(new SettingsService(dir), new PendingUpdateNotesService(dir)) { RelayOverrideForTest = () => null };
    }

    [Fact]
    public void TheClientId_IsPersistedInSettings_TrimmedAndRestoredOnTheNextLaunch()
    {
        var vm = FreshViewModel(out var dir);

        vm.IgdbClientId = "  my-client-id  ";

        var reloaded = new SettingsService(dir).Load();
        Assert.Equal("my-client-id", reloaded.IgdbClientId);
        Assert.Equal("my-client-id", new LibraryViewModel(new SettingsService(dir), new PendingUpdateNotesService(dir)).IgdbClientId);
    }

    [Fact]
    public void ABlankClientId_RemovesIt()
    {
        var vm = FreshViewModel(out var dir);
        vm.IgdbClientId = "abc";

        vm.IgdbClientId = "   ";

        Assert.Null(new SettingsService(dir).Load().IgdbClientId);
    }

    [Fact]
    public void TheClientSecret_IsStoredProtected_NeverInSettingsJson_AndOnlyPresenceIsReadBack()
    {
        var vm = FreshViewModel(out var dir);
        vm.IgdbClientId = "id";
        Assert.False(vm.IgdbSecretSaved);

        vm.SaveIgdbClientSecret("synthetic-secret-value-for-tests");

        Assert.True(vm.IgdbSecretSaved);
        Assert.True(vm.CredentialStoreForTest.HasSecret());
        Assert.Equal("synthetic-secret-value-for-tests", vm.CredentialStoreForTest.LoadSecret());
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "igdb-client-secret.protected")
                continue;
            Assert.DoesNotContain("synthetic-secret-value-for-tests", File.ReadAllText(file));
        }

        Assert.DoesNotContain("synthetic-secret-value-for-tests", File.ReadAllText(Path.Combine(dir, "igdb-client-secret.protected")));
        Assert.True(new LibraryViewModel(new SettingsService(dir), new PendingUpdateNotesService(dir)).IgdbSecretSaved); // survives a restart
    }

    [Fact]
    public void ABlankSecret_RemovesTheSavedOne()
    {
        var vm = FreshViewModel(out _);
        vm.SaveIgdbClientSecret("synthetic");

        vm.SaveIgdbClientSecret("   ");

        Assert.False(vm.IgdbSecretSaved);
        Assert.Null(vm.CredentialStoreForTest.LoadSecret());
    }

    [Fact]
    public void ACredentialStoreOverAnEmptyDirectory_HasNoSecret_WithoutCreatingAnything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdSettings-" + Guid.NewGuid());
        _dirs.Add(dir);

        Assert.False(new IgdbCredentialStore(dir).HasSecret());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TheProvidersOffered_FollowTheCredentialsEntered_IgdbFirstBecauseItIsPrimary()
    {
        var vm = FreshViewModel(out _);

        // No IGDB credentials: IGDB is simply not offered (the build may or may not embed a default SteamGridDB key, so nothing
        // is asserted about the fallback here).
        Assert.DoesNotContain(vm.CreateCatalogProvidersForPicker(), p => p.Namespace == IdentifierNamespace.IgdbGame);

        vm.IgdbClientId = "id";
        Assert.DoesNotContain(vm.CreateCatalogProvidersForPicker(), p => p.Namespace == IdentifierNamespace.IgdbGame); // an id alone is not enough

        vm.SaveIgdbClientSecret("synthetic");
        vm.SteamGridDbApiKey = "user-entered-key";
        var providers = vm.CreateCatalogProvidersForPicker().Select(p => p.Namespace).ToList();
        Assert.Equal(new[] { IdentifierNamespace.IgdbGame, IdentifierNamespace.SteamGridDbGame }, providers); // IGDB primary, SteamGridDB fallback

        vm.SaveIgdbClientSecret(null);
        Assert.DoesNotContain(vm.CreateCatalogProvidersForPicker(), p => p.Namespace == IdentifierNamespace.IgdbGame);
    }

    [Fact]
    public void WithTheProjectsRelayInTheBuild_IgdbIsOfferedFirst_WithNothingEntered_AndAHalfEnteredPairChangesNothing()
    {
        var vm = FreshViewModel(out _);
        vm.RelayOverrideForTest = () => new GameLauncher.Services.RelayEndpoint(new Uri("https://relay.example.test"), ["sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="]);
        vm.SteamGridDbApiKey = "user-entered-key";

        Assert.Equal(new[] { IdentifierNamespace.IgdbGame, IdentifierNamespace.SteamGridDbGame },
            vm.CreateCatalogProvidersForPicker().Select(p => p.Namespace));                 // zero setup: IGDB primary, SteamGridDB fallback

        vm.IgdbClientId = "id";                                                              // a lone id is ignored, not combined with anything
        Assert.Equal(new[] { IdentifierNamespace.IgdbGame, IdentifierNamespace.SteamGridDbGame },
            vm.CreateCatalogProvidersForPicker().Select(p => p.Namespace));
    }
}
