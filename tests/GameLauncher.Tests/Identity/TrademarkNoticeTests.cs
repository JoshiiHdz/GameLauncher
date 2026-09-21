using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>Found on the gaming PC: Call of Duty (a Game Pass install) was detected as "Call of Duty(R)" - the Xbox Start Menu writes the notice as TEXT.
/// The symbol forms already vanished under the letters-and-digits comparison, but "(R)" / "(TM)" left a stray letter behind, so the title was never
/// recognised as the known umbrella name, was searched with the notice still in it, and no exact-title comparison against IGDB could succeed.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class TrademarkNoticeTests
{
    // ---- comparison ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Call of Duty(R)")]
    [InlineData("Call of Duty(TM)")]
    [InlineData("Call of Duty (R)")]
    [InlineData("CALL OF DUTY (tm)")]
    [InlineData("Call of Duty®")]
    [InlineData("Call of Duty™")]
    [InlineData("Call of Duty (C)")]
    [InlineData("Call of Duty(SM)")]
    public void AnyTrademarkNotice_DoesNotChangeWhichTitleItIs(string name) =>
        Assert.Equal(SteamGridDbCoverArtProvider.CollapsedTitle("Call of Duty"), SteamGridDbCoverArtProvider.CollapsedTitle(name));

    [Theory]
    [InlineData("Call of Duty(R)")]
    [InlineData("Call of Duty(TM)")]
    [InlineData("Call of Duty®")]
    public void TheUmbrellaName_IsRecognised_WhateverNoticeRidesAlong(string name) => Assert.True(SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(name));

    [Theory]
    [InlineData("Call of Duty: Black Ops 7(R)")]                   // a specific title stays specific
    [InlineData("Call of Duty: Black Ops 7")]
    [InlineData("Call of Duty(R) Modern Warfare")]
    public void ASpecificTitle_IsNeverTakenForTheUmbrella(string name) => Assert.False(SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(name));

    [Fact]
    public void ANoticeAlone_DoesNotBlankOutATitle_AndRealParenthesesSurvive()
    {
        Assert.Equal("(R)", SteamGridDbCoverArtProvider.StripTrademarks("(R)"));                      // never an empty search
        Assert.Equal("Dune (Part Two)", SteamGridDbCoverArtProvider.StripTrademarks("Dune (Part Two)")); // only notices are removed, not any parentheses
        Assert.Equal("Wolfenstein (Old Blood)", SteamGridDbCoverArtProvider.StripTrademarks("Wolfenstein (Old Blood)"));
    }

    // ---- what is searched, and what is shown --------------------------------------------------------------------------------

    [Theory]
    [InlineData("Call of Duty(R)", "Call of Duty")]
    [InlineData("Call of Duty(TM)", "Call of Duty")]
    [InlineData("Assassin's Creed® Valhalla", "Assassin's Creed Valhalla")]
    [InlineData("DOOM™ Eternal", "DOOM Eternal")]
    [InlineData("Forza Horizon 5 (C)", "Forza Horizon 5")]
    [InlineData("Apex Legends", "Apex Legends")]
    public void TheSearchTitle_HasNoNotice_ButTheDetectedTitleIsExactlyWhatWasDetected(string detected, string searched)
    {
        var query = IdentityQuery.From(Games.Manual("m", detected));

        Assert.Equal(searched, query.SearchTitle);
        Assert.Equal(detected, query.DetectedTitle);                                                   // evidence stays as the launcher reported it
    }

    [Fact]
    public void ACuratedHint_StillWinsOverTheDetectedTitle()
    {
        var game = Games.Ea("ea-apex", "Apex(R)", "Apex Legends");

        Assert.Equal("Apex Legends", IdentityQuery.From(game).SearchTitle);
    }

    [Fact]
    public void TheSameGame_WithOrWithoutTheNotice_HasTheSameFingerprint()
    {
        Assert.Equal(IdentityQuery.From(Games.Manual("a", "Call of Duty")).Fingerprint, IdentityQuery.From(Games.Manual("a", "Call of Duty(R)")).Fingerprint);
        Assert.NotEqual(IdentityQuery.From(Games.Manual("a", "Call of Duty")).Fingerprint, IdentityQuery.From(Games.Manual("a", "Call of Duty 2")).Fingerprint);
    }

    // ---- the whole path -----------------------------------------------------------------------------------------------------

    [Fact]
    public void IgdbTitle_TheUmbrellaWithAnNoticeAttached_IsAmbiguous_WithoutAnyRequest()
    {
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => FakeIgdbHandler.Json("[]") };
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };

        Assert.Equal(CatalogStatus.Ambiguous, provider.SearchByTitleForIdentity("Call of Duty(R)", CancellationToken.None).Status);
        Assert.Equal(0, handler.ApiCalls);
    }

    [Fact]
    public void TheResolver_SearchesTheCleanTitle_NotTheOneWithTheNoticeInIt()
    {
        var igdb = new FakeCatalog(Cat.Igdb, "IGDB") { Title = _ => CatalogSearchResult.Found("7", "Some Game") };
        var game = Games.Manual("m", "Some Game(TM)");

        var output = AutomaticResolver.Run(Cat.Input(game), Cat.Ctx([igdb]), CancellationToken.None);

        Assert.Equal(new[] { "Some Game" }, igdb.SearchedTitles);
        Assert.Contains(output.NewRecord!.Resolved, r => r.Id == "7");                                  // and, being an exact match now, it resolves
    }

    [Fact]
    public async Task ThePickerOpens_OnTheCleanTitle_WhileTheDialogStillSaysWhatWasDetected()
    {
        using var h = new IdentityHarness();
        var game = await h.Add(Games.Manual("manual-cod", "Call of Duty(R)"));

        var dialog = new IdentifyGameViewModel(h.Vm, [h.Igdb], game.Id, game.Name);

        Assert.Equal("Call of Duty", dialog.SearchText);                                               // not "Call of Duty(R)"
        Assert.Equal("Call of Duty(R)", dialog.DetectedTitle);
        dialog.Cancel();
    }
}
