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
    public void SelectGameId_NoStorefrontMatch_FallsBackToTopResult()
    {
        var json = """{ "data": [{ "id": 111, "name": "Apex", "types": ["steam"] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(111, id);
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
    public void SelectGameId_TaggedCandidateHasMalformedId_FallsThroughToTopResult()
    {
        // A candidate that matches the storefront tag but has a malformed "id" must not abort the
        // whole selection - it's simply skipped in favor of whatever's next (here, the top result).
        var json = """
            { "data": [
                { "id": 111, "name": "Apex", "types": ["steam"] },
                { "id": "not a number", "name": "Apex Legends", "types": ["origin"] }
            ] }
            """;
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Apex Legends", storefrontTag: "origin");
        Assert.Equal(111, id); // fell through to the top result rather than throwing
    }

    // ---- Confidence rejection: the two real, confirmed false positives -------------------------------

    [Fact]
    public void SelectGameId_DerivativeModProduct_IsRejected_MinecraftModFoundryCase()
    {
        // The EXACT string from the real log - "ModFoundry" alone (a prior version's shortened, wrong
        // test double) shares no words with "Minecraft for Windows" and would trivially fail a bare
        // word-overlap check without actually proving the real bug is fixed. The real candidate name
        // shares the word "Minecraft" and would PASS a bare word-overlap rule - it's rejected here
        // specifically because it marks itself as a mod/maker product, not because of a lack of overlap.
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
    public void SelectGameId_NoMeaningfulWordOverlapAtAll_IsRejected()
    {
        // A genuinely unrelated result with zero shared words and no derivative-product marker either -
        // the plain word-overlap rule alone (not the derivative-product rule) is what rejects this.
        var json = """{ "data": [{ "id": 999, "name": "Some Totally Unrelated Software", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Minecraft for Windows", storefrontTag: null);
        Assert.Null(id);
    }

    [Fact]
    public void SelectGameId_QueryItselfMentionsMod_DerivativeMarkerNoLongerDisqualifies()
    {
        // If the USER is actually searching for a mod/addon (their detected game name itself contains
        // that word), a candidate sharing it must not be penalized - the rejection only fires when the
        // marker appears ONLY on the candidate side.
        var json = """{ "data": [{ "id": 999, "name": "Minecraft Mod Maker", "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, "Minecraft Mod Maker", storefrontTag: null);
        Assert.Equal(999, id);
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
    [InlineData("Overwatch 2", "Overwatch II")] // no digits in the candidate - number check doesn't apply
    [InlineData("Half-Life 2", "Half Life 2: Episode One")] // subtitle added - still shares real words + matching number
    public void SelectGameId_PlausibleRealWorldVariants_AreAccepted_NotOverRejected(string query, string candidateName)
    {
        var json = $$"""{ "data": [{ "id": 999, "name": {{System.Text.Json.JsonSerializer.Serialize(candidateName)}}, "types": [] }] }""";
        var id = SteamGridDbCoverArtProvider.SelectGameId(json, query, storefrontTag: null);
        Assert.Equal(999, id);
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
}
