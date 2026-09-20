using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>
/// Exercises SteamGridDbCoverArtProvider's response-parsing logic (SelectGameId/SelectGridImageUrl)
/// directly against JSON strings - split out from SearchGameId/GetGridImageUrl specifically so
/// malformed-response handling can be tested without a real HTTP round-trip to SteamGridDB.
/// </summary>
public class SteamGridDbCoverArtProviderTests
{
    // ---- SelectGameId ----------------------------------------------------------------------------

    [Fact]
    public void SelectGameId_WellFormedResponse_NoStorefrontTag_ReturnsTopResult()
    {
        var json = """{ "data": [{ "id": 111, "name": "Apex Legends", "types": ["steam"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: null);
        Assert.Equal(111, id);
    }

    [Fact]
    public void SelectGameId_StorefrontTagMatches_PrefersTaggedResultOverTopResult()
    {
        var json = """
            { "data": [
                { "id": 111, "name": "Apex", "types": ["steam"] },
                { "id": 222, "name": "Apex Legends", "types": ["origin"] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(222, id);
    }

    [Fact]
    public void SelectGameId_NoStorefrontMatch_ThinTopResult_IsRejected_NotBlindlyTrusted()
    {
        // Previously fell back to whatever ranked first regardless of fit - "Apex" is a real, different,
        // obscure game (see SearchGameId's own remarks), not "Apex Legends" abbreviated. A query naming
        // "Legends" must not settle for a candidate that doesn't have it, even as a last resort.
        var json = """{ "data": [{ "id": 111, "name": "Apex", "types": ["steam"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_EmptyDataArray_ReturnsNull()
    {
        var id = SteamGridDbCoverArtProvider.SelectGameId("""{ "data": [] }""", "Anything", null);
        Assert.Null(id);
    }

    [Theory]
    [InlineData("[]")] // root is an array, not an object
    [InlineData("\"just a string\"")] // root is a bare string
    [InlineData("{}")] // missing "data" entirely
    [InlineData("""{ "data": "not an array" }""")] // "data" has the wrong type
    [InlineData("""{ "data": [{ "id": "not a number", "name": "Apex" }] }""")] // "id" has the wrong type
    [InlineData("""{ "data": [{ "name": "Apex" }] }""")] // "id" missing entirely
    [InlineData("""{ "data": [123] }""")] // array element isn't even an object
    public void SelectGameId_UnexpectedShape_ReturnsNullWithoutThrowing(string malformedJson)
    {
        // Every one of these is syntactically valid JSON - only the SHAPE is wrong. The original code
        // called GetProperty/GetString/GetInt32 straight off search results with no shape validation
        // at all, so each of these used to throw InvalidOperationException (GetProperty's own
        // documented behavior for an absent property, or one of the wrong kind) - not a JsonException,
        // and so not caught by GetCoverArt's old catch clause, which could crash the whole scan over a
        // single unexpected API response.
        var id = SteamGridDbCoverArtProvider.SelectGameId(malformedJson, "Some Game", storefrontTag: "steam");
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_TaggedCandidateHasMalformedId_FallsThroughToNextConfidentResult()
    {
        // A candidate that matches the storefront tag but has a malformed "id" must not abort the
        // whole selection - it's simply skipped in favor of whatever's next. Using a genuinely
        // confident fallback ("Apex Legends" itself, not a bare "Apex") so this test still proves its
        // original point (malformed data doesn't crash/abort selection) under the stricter matching
        // rules - a thin "Apex" fallback would now correctly be rejected regardless of the malformed id.
        var json = """
            { "data": [
                { "id": 111, "name": "Apex Legends", "types": ["steam"] },
                { "id": "not a number", "name": "Apex Legends", "types": ["origin"] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(111, id); // fell through to the valid result rather than throwing
    }

    // ---- Confidence rejection: the two real, confirmed false positives -------------------------------

    [Fact]
    public void SelectGameId_DerivativeModProduct_IsRejected_MinecraftModFoundryCase()
    {
        // The EXACT string from the real log. Not textually equal to the query once normalized, so
        // exact-match rejects it regardless of the "Minecraft" word the two names happen to share.
        var json = """{ "data": [{ "id": 999, "name": "ModFoundry - Mod Maker for Minecraft", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Minecraft for Windows", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_DerivativeModProduct_StillRejected_EvenWithStorefrontTag()
    {
        var json = """{ "data": [{ "id": 999, "name": "ModFoundry - Mod Maker for Minecraft", "types": ["steam"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Minecraft for Windows", storefrontTag: "steam");
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_NoMeaningfulOverlapAtAll_IsRejected()
    {
        var json = """{ "data": [{ "id": 999, "name": "Some Totally Unrelated Software", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Minecraft for Windows", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_NormalizedEquivalentTitle_AWayOutCase_IsAccepted()
    {
        // "AWayOut" (the detected, un-prettified game name - GameLauncher does not rewrite display
        // names) and "A Way Out" (SteamGridDB's actual catalog title) are the SAME title, differing only
        // in spacing - recognized via a precise collapsed-text comparison, not a fuzzy word-splitting
        // heuristic. This fixes ARTWORK MATCHING specifically; it says nothing about A Way Out's
        // separate, still-open LAUNCHING problem.
        var json = """{ "data": [{ "id": 999, "name": "A Way Out", "types": ["origin"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "AWayOut", storefrontTag: "origin");
        Assert.Equal(999, id);
    }

    [Fact]
    public void SelectGameId_MismatchedEditionNumber_IsRejected_EvenWithStorefrontTag_FC27Fc24Case()
    {
        var json = """{ "data": [{ "id": 999, "name": "EA Sports FC 24", "types": ["origin"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "EA SPORTS FC 27", storefrontTag: "origin");
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_MismatchedEditionNumber_SkipsToALaterConfidentResult()
    {
        var json = """
            { "data": [
                { "id": 111, "name": "EA Sports FC 24", "types": [] },
                { "id": 222, "name": "EA Sports FC 27", "types": [] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "EA SPORTS FC 27", storefrontTag: null);
        Assert.Equal(222, id); // the wrong-edition top result is skipped, not blindly trusted
    }

    [Fact]
    public void SelectGameId_MatchingEditionNumber_IsAccepted()
    {
        var json = """{ "data": [{ "id": 999, "name": "EA Sports FC 27", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "EA SPORTS FC 27", storefrontTag: null);
        Assert.Equal(999, id);
    }

    [Theory]
    [InlineData("Overwatch 2", "Overwatch 2")]
    [InlineData("Overwatch 2", "Overwatch II")] // roman numeral normalized to "2" before comparison
    [InlineData("Half-Life 2", "Half Life 2")] // hyphen/spacing difference only - genuinely the same title
    public void SelectGameId_PlausibleRealWorldVariants_AreAccepted_NotOverRejected(string query, string candidateName)
    {
        var json = $$"""{ "data": [{ "id": 999, "name": {{System.Text.Json.JsonSerializer.Serialize(candidateName)}}, "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, query, storefrontTag: null);
        Assert.Equal(999, id);
    }

    // ---- Real, confirmed false positive: "EA SPORTS FC 27" shown a UFC cover ----------------------

    [Fact]
    public void SelectGameId_SharedGenericWordOnly_IsRejected_Fc27UfcCase()
    {
        // Real, confirmed case: a UFC cover was shown for "EA SPORTS FC 27". "EA Sports UFC" is not the
        // same title, full stop - sharing the word "Sports" is not evidence of identity.
        var json = """{ "data": [{ "id": 999, "name": "EA Sports UFC", "types": ["origin"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "EA SPORTS FC 27", storefrontTag: "origin");
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_SharedGenericWordOnly_NoStorefrontTag_IsStillRejected()
    {
        var json = """{ "data": [{ "id": 999, "name": "EA Sports UFC", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "EA SPORTS FC 27", storefrontTag: null);
        Assert.Null(id);
    }

    // ---- Real category of false positive: a query matching a titularly-related but distinct product ---

    [Fact]
    public void SelectGameId_GenericFranchisePage_DoesNotMatchASpecificSubtitledQuery_BlackOps7Case()
    {
        // "Call of Duty: Black Ops 7" must not settle for a plain "Call of Duty" result - they are not
        // the same title, regardless of the shared "Call of Duty" prefix.
        var json = """{ "data": [{ "id": 999, "name": "Call of Duty", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Call of Duty: Black Ops 7", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_GenericFranchisePage_SkipsToTheActualExactMatchFurtherDown()
    {
        var json = """
            { "data": [
                { "id": 111, "name": "Call of Duty", "types": [] },
                { "id": 222, "name": "Call of Duty: Black Ops 7", "types": [] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Call of Duty: Black Ops 7", storefrontTag: null);
        Assert.Equal(222, id); // the generic franchise page is skipped, not blindly trusted
    }

    [Fact]
    public void SelectGameId_BareFranchiseQuery_DoesNotMatchASpecificSubtitledCandidate_ModernWarfareCase()
    {
        // Neither name contains a number, so this is NOT caught by any number-based rule - it's rejected
        // purely because "Call of Duty" and "Call of Duty: Modern Warfare" are not the same exact title.
        // There is no way to tell which of a franchise's many entries a bare, edition-less query means.
        var json = """{ "data": [{ "id": 999, "name": "Call of Duty: Modern Warfare", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Call of Duty", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_BareFranchiseQueryWithNoEdition_DoesNotGuessASpecificNumberedCandidate()
    {
        var json = """{ "data": [{ "id": 999, "name": "Call of Duty: Black Ops 7", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Call of Duty", storefrontTag: null);
        Assert.Null(id);
    }

    // ---- Real category of false positive: a genuinely different, separately catalogued sequel --------

    [Fact]
    public void SelectGameId_SeparatelyCataloguedSequel_IsRejected_HalfLife2EpisodeOneCase()
    {
        // "Half-Life 2: Episode One" is its own separately catalogued product, not a formatting variant
        // of "Half-Life 2" - an earlier version of this test wrongly accepted this substitution by
        // tolerating a candidate with an unrelated extra subtitle whenever the query itself had no
        // number to contradict it.
        var json = """{ "data": [{ "id": 999, "name": "Half Life 2: Episode One", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Half-Life 2", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_SeparatelyCataloguedSequel_IsRejected_HalfLife2Episode2Case()
    {
        // A candidate carrying a DIFFERENT number ("2", from "Episode 2") that happens to coincide with
        // the query's own number ("2", from "Half-Life 2") is not evidence of identity - a set-based
        // number comparison would wrongly treat this coincidence as a match.
        var json = """{ "data": [{ "id": 999, "name": "Half Life 2: Episode 2", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Half-Life 2", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_ExactSequelMatch_SkipsThePriorGamesEntry_HalfLifeOrderingCase()
    {
        // The broader/related candidate ("Half-Life") ranks first but is NOT the query - the exact
        // match ranked second must still win, proving every candidate is evaluated rather than stopping
        // at the first one that merely looks plausible.
        var json = """
            { "data": [
                { "id": 111, "name": "Half-Life", "types": [] },
                { "id": 222, "name": "Half-Life 2", "types": [] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Half-Life 2", storefrontTag: null);
        Assert.Equal(222, id);
    }

    // ---- Real category of false positive: a remaster/edition suffix treated as the base game --------

    [Fact]
    public void SelectGameId_DifferentlyCataloguedRemaster_IsRejected_DarkSoulsRemasteredCase()
    {
        // "Dark Souls Remastered" is its own separately catalogued listing on SteamGridDB, not
        // interchangeable with "Dark Souls" - a generic-word filter that discarded "Remastered" as mere
        // SKU-descriptor noise would wrongly equate the two.
        var json = """{ "data": [{ "id": 999, "name": "Dark Souls Remastered", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Dark Souls", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_NoOverlapAtAll_IsRejected()
    {
        var json = """{ "data": [{ "id": 999, "name": "Some Unrelated Game", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "The", storefrontTag: null);
        Assert.Null(id);
    }

    // ---- Known ambiguous umbrella product names: skipped before ever searching ---------------------

    private static GameEntry MakeGameEntry(string name) => new()
    {
        Id = "battlenet-test",
        Name = name,
        ExecutablePath = @"C:\Games\CallOfDuty\cod.exe",
        InstallDir = @"C:\Games\CallOfDuty",
        Source = GameSource.BattleNet,
    };

    [Fact]
    public void GetCoverArt_KnownAmbiguousUmbrellaProductName_NeverReachesSearch()
    {
        // "Call of Duty" is Activision's shared Battle.net installer name for every modern yearly
        // release - GameEntry.Name carries that generic name verbatim regardless of which specific title
        // is actually installed (see BattleNetScanner/PublisherUninstallScanner). No name-matching rule
        // can recover that, so this must be skipped before ever reaching the network.
        //
        // Asserting only Assert.Null(result) here would NOT prove the guard actually ran - a real search
        // with the fake API key below would fail authentication and also return null, leaving a broken/
        // removed guard undetected. SearchGameIdOverride proves it directly: if the guard were removed,
        // execution would reach this override and searchCalls would become 1, failing the assertion
        // below without ever touching a real network connection.
        var provider = new SteamGridDbCoverArtProvider("fake-api-key");
        var searchCalls = 0;
        provider.SearchGameIdOverride = _ => { searchCalls++; return null; };

        var result = provider.GetCoverArt(MakeGameEntry("Call of Duty"));

        Assert.Null(result);
        Assert.Equal(0, searchCalls);
    }

    [Fact]
    public void GetCoverArt_OrdinaryGameName_DoesReachSearch_SeamIsWiredCorrectly()
    {
        // Companion to the test above: proves SearchGameIdOverride is actually wired into the normal
        // path (not just silently unused), so a search-call count of zero above is meaningful evidence
        // of a skip, not an artifact of a seam that never fires for any input.
        var provider = new SteamGridDbCoverArtProvider("fake-api-key");
        var searchCalls = 0;
        provider.SearchGameIdOverride = _ => { searchCalls++; return null; };

        var result = provider.GetCoverArt(MakeGameEntry("Some Ordinary Game"));

        Assert.Null(result); // override itself returns null -> no match -> GetCoverArt returns null
        Assert.Equal(1, searchCalls);
    }

    [Theory]
    [InlineData("Call of Duty®")] // trademark symbol
    [InlineData("Call of Duty ")] // trailing whitespace
    [InlineData("Call  of   Duty")] // repeated internal spacing
    [InlineData("call of duty")] // case difference
    public void IsAmbiguousUmbrellaProduct_NormalizedFormattingVariants_AreAllCaught(string variantName)
    {
        // The bug this fixes: GetCoverArt's guard used to compare game.Name RAW against the umbrella set
        // while IsConfidentMatch compares NORMALIZED text - a variant like these would miss the raw
        // check, then go on to exact-match the real "Call of Duty" catalog entry anyway once
        // IsConfidentMatch normalized it, defeating the guard entirely for exactly the formatting
        // variance it exists to survive.
        Assert.True(SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(variantName));
    }

    [Fact]
    public void IsAmbiguousUmbrellaProduct_MoreSpecificSubtitledName_IsNotBlocked()
    {
        // A genuinely specific title built on the umbrella name must NOT be caught by this - only the
        // bare umbrella name itself (after normalization) is ambiguous.
        Assert.False(SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct("Call of Duty: Black Ops 7"));
    }

    [Fact]
    public void GetCoverArt_UmbrellaNameFormattingVariant_StillNeverReachesSearch()
    {
        var provider = new SteamGridDbCoverArtProvider("fake-api-key");
        var searchCalls = 0;
        provider.SearchGameIdOverride = _ => { searchCalls++; return null; };

        var result = provider.GetCoverArt(MakeGameEntry("Call of Duty®")); // trademark symbol variant

        Assert.Null(result);
        Assert.Equal(0, searchCalls);
    }

    // ---- Empty normalized titles: no evidence must never count as a match --------------------------

    [Fact]
    public void IsConfidentMatch_BothNamesCollapseToEmpty_IsRejected()
    {
        // "!!!" and "???" both strip down to an empty string under CollapseForComparison - two empty
        // strings being textually "equal" is not identity evidence, it's the absence of any evidence.
        Assert.False(SteamGridDbCoverArtProvider.IsConfidentMatch("!!!", "???"));
    }

    [Fact]
    public void SelectGameId_QueryCollapsesToEmpty_IsRejected_EvenAgainstAnEquallyEmptyCandidate()
    {
        var json = """{ "data": [{ "id": 999, "name": "???", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "!!!", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_MalformedItemAlongsideValidOnes_StillMatchesAValidOne()
    {
        var json = """
            { "data": [
                123,
                { "id": 222, "name": "Apex Legends", "types": ["origin"] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(222, id);
    }

    // ---- SelectGridImageUrl -----------------------------------------------------------------------

    [Fact]
    public void SelectGridImageUrl_WellFormedResponse_ReturnsUrl()
    {
        var json = """{ "data": [{ "url": "https://example.com/cover.png" }] }""";
        Assert.Equal("https://example.com/cover.png", SteamGridDbCoverArtProvider.SelectGridImageUrl(json));
    }

    [Fact]
    public void SelectGridImageUrl_EmptyDataArray_ReturnsNull()
    {
        Assert.Null(SteamGridDbCoverArtProvider.SelectGridImageUrl("""{ "data": [] }"""));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "data": "not an array" }""")]
    [InlineData("""{ "data": [{ "url": 123 }] }""")]
    [InlineData("""{ "data": [{}] }""")]
    [InlineData("""{ "data": [123] }""")]
    public void SelectGridImageUrl_UnexpectedShape_ReturnsNullWithoutThrowing(string malformedJson)
    {
        Assert.Null(SteamGridDbCoverArtProvider.SelectGridImageUrl(malformedJson));
    }

    // ---- Retrieval evidence: a cache hit must never be reported as a fresh network download --------

    [Fact]
    public void GetCoverArt_ValidPreExistingCacheFile_ServedFromCache_NeverReachesSearch()
    {
        // CoverArtService.Apply needs to know a result came from THIS provider's own on-disk cache
        // rather than a fresh network fetch just now, so it can record accurate ArtworkRetrievalMethod
        // evidence instead of manufacturing "NetworkDownload" for what was actually a cache hit.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var provider = new SteamGridDbCoverArtProvider("fake-api-key");
            var game = new GameEntry
            {
                Id = "battlenet-cache-hit-test",
                Name = "Some Ordinary Game",
                ExecutablePath = @"C:\Games\CallOfDuty\cod.exe",
                InstallDir = @"C:\Games\CallOfDuty",
                Source = GameSource.BattleNet,
            };

            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
            var pngBytes = MakeValidPngBytes();
            File.WriteAllBytes(cachePath, pngBytes);
            // A cache hit now also requires valid, identity-matching sidecar evidence (see
            // TryReadMatchedGame's own remarks) - without one, this would be treated as unverifiable and
            // fall through to a fresh search, defeating the very thing this test exists to prove.
            File.WriteAllText(cachePath + ".meta.json", MakeSidecarJson(111, "Some Ordinary Game", game.Name, pngBytes));

            var searchCalls = 0;
            provider.SearchGameIdOverride = _ => { searchCalls++; return null; };

            var result = provider.GetCoverArt(game, out var servedFromCache, cacheDir);

            Assert.NotNull(result);
            Assert.True(servedFromCache);
            Assert.Equal(0, searchCalls); // never even reached the search step, let alone the network
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_NoCacheFile_SearchFindsNothing_ServedFromCacheIsFalse()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var provider = new SteamGridDbCoverArtProvider("fake-api-key") { SearchGameIdOverride = _ => null };

            var result = provider.GetCoverArt(MakeGameEntry("Some Ordinary Game"), out var servedFromCache, cacheDir);

            Assert.Null(result);
            Assert.False(servedFromCache);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static byte[] MakeValidPngBytes(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, width);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- SelectMatchedGame: same selection as SelectGameId, but also returns the matched title -------

    [Fact]
    public void SelectMatchedGame_ReturnsTheMatchedCandidatesOwnTitle_NotJustItsId()
    {
        var json = """{ "data": [{ "id": 111, "name": "Apex Legends", "types": ["steam"] }] }""";
        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Apex Legends", storefrontTag: null);
        Assert.Equal(111, matched?.Id);
        Assert.Equal("Apex Legends", matched?.Title);
    }

    [Fact]
    public void SelectMatchedGame_AgreesWithSelectGameId_OnTheSameInput()
    {
        var json = """
            { "data": [
                { "id": 111, "name": "Apex", "types": ["steam"] },
                { "id": 222, "name": "Apex Legends", "types": ["origin"] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(id, matched?.Id);
    }

    // ---- SplitCompactedWords: query-generation fix for a spacing-glued name --------------------------

    [Theory]
    [InlineData("AWayOut", "A Way Out")]
    [InlineData("ModFoundry", "Mod Foundry")]
    public void SplitCompactedWords_InsertsWordBoundarySpaces(string compacted, string expected)
    {
        Assert.Equal(expected, SteamGridDbCoverArtProvider.SplitCompactedWords(compacted));
    }

    [Theory]
    [InlineData("Apex")] // a genuine abbreviation, not a spacing problem - must be left alone
    [InlineData("A Way Out")] // already spaced
    [InlineData("Minecraft")]
    [InlineData("Half-Life")] // hyphen breaks the adjacency the rule needs - must not split here
    public void SplitCompactedWords_NoWordBoundaryToInsert_ReturnsUnchanged(string name)
    {
        Assert.Equal(name, SteamGridDbCoverArtProvider.SplitCompactedWords(name));
    }

    // ---- The real, confirmed Apex identity scenario: EA + CatalogName --------------------------------

    private static GameEntry MakeEaGameEntry(string name, string? catalogName) => new()
    {
        Id = "ea-apex-test",
        Name = name,
        CatalogName = catalogName,
        ExecutablePath = @"C:\Games\Apex\r5apex.exe",
        InstallDir = @"C:\Games\Apex",
        Source = GameSource.Ea,
    };

    /// <summary>Every GetCoverArt call in this section runs against an isolated cache directory, never
    /// the real %AppData%\GameLauncher\CoverArtCache - a successful match+image fetch really does write
    /// a .png and a .meta.json to disk (see WriteMatchedGame), and these tests exist specifically to
    /// drive that path, so skipping the override here (unlike the pre-existing "never reaches search"
    /// tests above, which return before any write) would leave real files behind on whatever machine
    /// runs the suite.</summary>
    private static string CreateIsolatedCacheDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid())).FullName;

    [Fact]
    public void GetCoverArt_EaApexScenario_WithoutCatalogName_MatchesTheWrongUnrelatedGame()
    {
        // Reproduces the real, confirmed bug exactly: the raw detected name "Apex" is an exact match for
        // an unrelated, differently-catalogued game also named "Apex" - and IsConfidentMatch correctly
        // REJECTS the true "Apex Legends" storefront-tagged candidate, because "Apex" != "Apex Legends".
        // This test pins that this is a genuine identity gap, not a bug already fixed by CatalogName
        // alone existing as a field - it's still reachable whenever CatalogName isn't populated.
        var cacheDir = CreateIsolatedCacheDir();
        try
        {
            var json = """
                { "data": [
                    { "id": 999, "name": "Apex", "types": ["steam"] },
                    { "id": 222, "name": "Apex Legends", "types": ["origin"] }
                ] }
                """;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchRequestOverride = _ => json,
                FetchGridImageBytesOverride = _ => MakeValidPngBytes(),
            };

            var result = provider.GetCoverArt(MakeEaGameEntry("Apex", catalogName: null), out _, out var matched, cacheDir);

            Assert.Equal(999, matched?.Id); // the wrong, unrelated "Apex" - confirms the bug is real without the fix
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_EaApexScenario_WithVerifiedCatalogName_MatchesTheRealGame()
    {
        var cacheDir = CreateIsolatedCacheDir();
        try
        {
            var json = """
                { "data": [
                    { "id": 999, "name": "Apex", "types": ["steam"] },
                    { "id": 222, "name": "Apex Legends", "types": ["origin"] }
                ] }
                """;
            var seenQueries = new List<string>();
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchRequestOverride = query => { seenQueries.Add(query); return json; },
                FetchGridImageBytesOverride = _ => MakeValidPngBytes(),
            };

            var result = provider.GetCoverArt(MakeEaGameEntry("Apex", catalogName: "Apex Legends"), out _, out var matched, cacheDir);

            Assert.Equal(222, matched?.Id);
            Assert.Equal("Apex Legends", matched?.Title);
            Assert.Equal(["Apex Legends"], seenQueries); // searched the VERIFIED title, never the raw "Apex"
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ---- Query-generation retry: a compacted name that returns zero results on its own ---------------

    [Fact]
    public void GetCoverArt_AWayOutScenario_CompactedQueryFindsNothing_RetriesWithSpacedQuery()
    {
        // Reproduces the real, confirmed second gap: "AWayOut" (an EA folder name) returns zero results
        // from SteamGridDB's own search - not merely "no confident match among some candidates", NO
        // candidates at all - so no amount of comparison leniency downstream could ever recover it. This
        // is a transport-level test specifically because a fake response keyed by exact query text is
        // the only way to prove the SECOND query was actually sent with different (expanded) text -
        // asserting only the final `matched` result could pass even if the retry silently resent the
        // exact same failing query text twice.
        var cacheDir = CreateIsolatedCacheDir();
        try
        {
            const string emptyResponse = """{ "data": [] }""";
            const string realGameResponse = """{ "data": [{ "id": 555, "name": "A Way Out", "types": ["origin"] }] }""";

            var seenQueries = new List<string>();
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchRequestOverride = query =>
                {
                    seenQueries.Add(query);
                    return query == "AWayOut" ? emptyResponse : realGameResponse;
                },
                FetchGridImageBytesOverride = _ => MakeValidPngBytes(),
            };

            var game = new GameEntry
            {
                Id = "ea-awayout-test",
                Name = "AWayOut",
                ExecutablePath = @"C:\Games\AWayOut\AWayOut.exe",
                InstallDir = @"C:\Games\AWayOut",
                Source = GameSource.Ea,
            };

            var result = provider.GetCoverArt(game, out _, out var matched, cacheDir);

            Assert.Equal(555, matched?.Id);
            Assert.Equal(["AWayOut", "A Way Out"], seenQueries); // both attempts, in order, with the right text
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_CompactedQueryFindsNothing_RetryAlsoFindsNothing_ReturnsNullWithoutLooping()
    {
        var cacheDir = CreateIsolatedCacheDir();
        try
        {
            const string emptyResponse = """{ "data": [] }""";
            var attempts = 0;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchRequestOverride = _ => { attempts++; return emptyResponse; },
            };

            var game = new GameEntry
            {
                Id = "ea-nomatch-test",
                Name = "SomeCompactedName",
                ExecutablePath = @"C:\Games\Foo\Foo.exe",
                InstallDir = @"C:\Games\Foo",
                Source = GameSource.Ea,
            };

            var result = provider.GetCoverArt(game, out _, out var matched, cacheDir);

            Assert.Null(result);
            Assert.Null(matched);
            Assert.Equal(2, attempts); // primary + exactly one retry, never an unbounded loop
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ---- Matching evidence survives a cache hit (see GetCoverArt's own remarks on the sidecar file) ---

    /// <summary>Builds a sidecar in the exact on-disk shape WriteMatchedGame produces
    /// (CachedMatchEvidence: Id, Title, SearchedName, ImageSha256) - computing the same SHA-256 over
    /// `imageBytes` a real write would, so a test-seeded sidecar validates exactly the way a genuinely
    /// fetched one would. Duplicated here rather than reusing ComputeImageHash (private) so this file
    /// exercises the sidecar CONTRACT (its JSON shape/field names) rather than reaching into the
    /// provider's own implementation detail.</summary>
    private static string MakeSidecarJson(int id, string title, string searchedName, byte[] imageBytes)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes));
        return JsonSerializer.Serialize(new { Id = id, Title = title, SearchedName = searchedName, ImageSha256 = hash });
    }

    [Fact]
    public void GetCoverArt_CacheHit_WithValidSidecar_ReportsTheOriginallyMatchedGame()
    {
        // The real, confirmed gap this closes: SafeApplyCoverArt re-runs the automatic matcher on EVERY
        // scan for any non-user-selected selection, and every scan after the first is a cache hit - so
        // if a cache hit couldn't report matched-game evidence, CommitAutomaticArtworkIfCurrent's
        // unconditional metadata sync would silently null out ProviderGameId/ProviderTitle the very next
        // scan after they were first recorded. The sidecar is pre-seeded directly here (as
        // GetCoverArt_ValidPreExistingCacheFile_ServedFromCache_NeverReachesSearch above also does for
        // the image itself) rather than via a genuine fresh fetch first, since GetGridImageUrl's own
        // image-download HTTP call has no test seam to intercept.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var provider = new SteamGridDbCoverArtProvider("fake-api-key");
            var game = MakeEaGameEntry("Apex", catalogName: "Apex Legends");

            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
            var metaPath = cachePath + ".meta.json";
            var pngBytes = MakeValidPngBytes();
            File.WriteAllBytes(cachePath, pngBytes);
            File.WriteAllText(metaPath, MakeSidecarJson(222, "Apex Legends", "Apex Legends", pngBytes));

            var result = provider.GetCoverArt(game, out var servedFromCache, out var matched, cacheDir);

            Assert.NotNull(result);
            Assert.True(servedFromCache);
            Assert.Equal(222, matched?.Id);
            Assert.Equal("Apex Legends", matched?.Title);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    // {HASH} is substituted with the ACTUAL cached image's correct hash at run time (a [Theory] can only
    // hold compile-time constants, so it can't be computed here). This matters: an EARLIER version of
    // this test used a fixed placeholder like "deadbeef" for every case, including the non-positive-id
    // and empty-title cases - which is always wrong for whatever bytes MakeValidPngBytes() produces, so
    // those two cases would reject on the hash check alone regardless of whether the id/title validation
    // in TryReadMatchedGame existed at all, proving nothing about it specifically. Giving them the
    // CORRECT hash isolates each case to the ONE field it's meant to exercise.
    [Theory]
    [InlineData("""{}""")] // every field defaults to 0/null - not evidence, must not be trusted
    [InlineData("""{"Id":0,"Title":"Apex Legends","SearchedName":"Apex Legends","ImageSha256":"{HASH}"}""")] // non-positive id
    [InlineData("""{"Id":222,"Title":"","SearchedName":"Apex Legends","ImageSha256":"{HASH}"}""")] // empty title
    [InlineData("""{"Id":222,"Title":"Apex Legends","SearchedName":"Apex Legends","ImageSha256":""}""")] // empty hash
    [InlineData("""{"Id":222,"Title":"Apex Legends","SearchedName":"Apex Legends","ImageSha256":"0000000000000000000000000000000000000000000000000000000000000000"}""")] // hash present but WRONG - doesn't describe this image
    [InlineData("not even json")]
    public void GetCoverArt_CacheHit_MalformedOrMismatchedSidecar_TreatedAsUnverified_CacheIsInvalidated(string sidecarJsonTemplate)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var game = MakeEaGameEntry("Apex", catalogName: "Apex Legends");
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
            var metaPath = cachePath + ".meta.json";

            Directory.CreateDirectory(cacheDir);
            var pngBytes = MakeValidPngBytes();
            var correctHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pngBytes));
            var sidecarJson = sidecarJsonTemplate.Replace("{HASH}", correctHash);
            File.WriteAllBytes(cachePath, pngBytes);
            File.WriteAllText(metaPath, sidecarJson);

            var searchCalls = 0;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchGameIdOverride = _ => { searchCalls++; return null; },
            };

            var result = provider.GetCoverArt(game, out var servedFromCache, out var matched, cacheDir);

            Assert.False(servedFromCache);
            Assert.Null(matched);
            Assert.Equal(1, searchCalls); // unverifiable evidence forced a real re-fetch attempt, not a silent cache hit
            Assert.False(File.Exists(cachePath), "The stale, unverifiable cache file must be deleted, not left to fail identically forever.");
            Assert.False(File.Exists(metaPath));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_CacheHit_SidecarResolvedForADifferentIdentity_TreatedAsStale_CacheIsInvalidated()
    {
        // The real, confirmed scenario this closes: the SAME local game id/cache directory, but the
        // RESOLVED identity used to produce the cached entry differs from the one being searched for
        // now (e.g. Reset supplying a CatalogName a much earlier scan never had - see LibraryViewModel's
        // own ResetCoverToAutomaticAsync fix). A CacheVersion bump alone cannot catch this: it only
        // clears mistakes once, at release time, not ones introduced afterward by an identity that
        // changes between two calls against the exact same cache entry.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var game = MakeEaGameEntry("Apex", catalogName: "Apex Legends");
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
            var metaPath = cachePath + ".meta.json";
            var pngBytes = MakeValidPngBytes();

            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(cachePath, pngBytes);
            // Cached under the RAW abbreviated name, before CatalogName ever existed for this game.
            File.WriteAllText(metaPath, MakeSidecarJson(999, "Apex", "Apex", pngBytes));

            var searchCalls = 0;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchGameIdOverride = _ => { searchCalls++; return null; },
            };

            // The SAME cache directory/local game id as above - only the resolved identity (via
            // CatalogName, already "Apex Legends" on `game`) differs from what the sidecar recorded.
            var result = provider.GetCoverArt(game, out var servedFromCache, out var matched, cacheDir);

            Assert.False(servedFromCache);
            Assert.Null(matched);
            Assert.Equal(1, searchCalls); // the stale-identity entry was rejected, forcing a fresh lookup
            Assert.False(File.Exists(cachePath));
        }
        finally {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_ImageOverwritten_ButSidecarWriteFailedOrWasSkipped_StaleSidecarIsRejected()
    {
        // The real, confirmed scenario this closes: the image and its sidecar are two SEPARATE files,
        // written one after the other (see GetCoverArt) - a fresh download can succeed and overwrite
        // cachePath while the sidecar write that should follow it fails (disk full, permissions) or is
        // otherwise skipped, leaving an OLD sidecar (describing a PREVIOUS image, e.g. from before an
        // identity correction) sitting next to a cache file it no longer actually describes. Simulated
        // directly here by writing image A + its real sidecar, then overwriting JUST the image file with
        // DIFFERENT bytes (image B) without touching the sidecar - exactly what an interrupted second
        // fetch would leave behind. The hash binding must catch this: reporting image A's identity for
        // image B's actual bytes would be presenting mismatched evidence as verified.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var game = MakeEaGameEntry("Apex", catalogName: "Apex Legends");
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
            var metaPath = cachePath + ".meta.json";

            Directory.CreateDirectory(cacheDir);
            var imageA = MakeValidPngBytes(width: 8, height: 8);
            var imageB = MakeValidPngBytes(width: 16, height: 16); // genuinely different bytes, not a copy
            File.WriteAllBytes(cachePath, imageA);
            File.WriteAllText(metaPath, MakeSidecarJson(222, "Apex Legends", "Apex Legends", imageA));

            // Simulates the interrupted second fetch: the image half of a fresh write landed, the
            // metadata half didn't (or was never reached) - the old sidecar is left describing bytes
            // that are no longer what's actually at cachePath.
            File.WriteAllBytes(cachePath, imageB);

            var searchCalls = 0;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchGameIdOverride = _ => { searchCalls++; return null; },
            };

            var result = provider.GetCoverArt(game, out var servedFromCache, out var matched, cacheDir);

            Assert.False(servedFromCache);
            Assert.Null(matched); // must NOT report image A's identity for image B's bytes
            Assert.Equal(1, searchCalls); // forced a fresh lookup rather than trusting the stale sidecar
            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_GenuineFetchThenSecondCall_IsARealCacheHit_ReportingTheSameEvidence()
    {
        // End-to-end round trip through the real write path (WriteMatchedGame) AND the real read path
        // (TryReadMatchedGame) - every other cache-hit test above pre-seeds the sidecar by hand to test
        // one specific validation rule in isolation; this one proves the two sides actually agree with
        // each other in the ordinary, successful case.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var json = """{ "data": [{ "id": 222, "name": "Apex Legends", "types": ["origin"] }] }""";
            var searchCalls = 0;
            var provider = new SteamGridDbCoverArtProvider("fake-api-key")
            {
                SearchRequestOverride = _ => { searchCalls++; return json; },
                FetchGridImageBytesOverride = _ => MakeValidPngBytes(),
            };
            var game = MakeEaGameEntry("Apex", catalogName: "Apex Legends");

            var firstResult = provider.GetCoverArt(game, out var firstServedFromCache, out var firstMatched, cacheDir);
            Assert.NotNull(firstResult);
            Assert.False(firstServedFromCache);
            Assert.Equal(222, firstMatched?.Id);
            Assert.Equal(1, searchCalls);

            var secondResult = provider.GetCoverArt(game, out var secondServedFromCache, out var secondMatched, cacheDir);

            Assert.NotNull(secondResult);
            Assert.True(secondServedFromCache);
            Assert.Equal(222, secondMatched?.Id);
            Assert.Equal("Apex Legends", secondMatched?.Title);
            Assert.Equal(1, searchCalls); // the second call never searched again - a genuine cache hit
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
