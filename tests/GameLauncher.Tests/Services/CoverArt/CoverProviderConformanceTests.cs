using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>One observable outcome of a provider lookup, in a provider-neutral shape so the same assertions
/// run against every name-searching provider.</summary>
public sealed record ProviderRun(
    BitmapImage? Image, CoverLookupStatus Status, (int Id, string Title)? Matched, bool FromCache,
    int SearchCalls, int ImageCalls, string? LastQuery);

/// <summary>B1 provider parity: ONE contract, asserted against BOTH IgdbCoverArtProvider and
/// SteamGridDbCoverArtProvider through their own transport-level seams (search response + image bytes). Any
/// behaviour asserted here that one provider stops honouring turns that provider's copy of the test red - so
/// the two cannot silently drift apart, which is exactly how SteamGridDB came to lack IGDB's uniqueness check
/// and its image validation.
///
/// What this suite deliberately does NOT cover (each has its own provider-specific tests): token/auth,
/// rate limiting and 429 handling (IGDB only); the response SHAPES (IGDB returns a bare array, SteamGridDB
/// wraps it in {"data": ...} - hidden behind SearchJson); storefront tags and the compacted-words retry
/// (SteamGridDB only).
///
/// Every test uses its own temp cache directory and per-instance seams - no static state is touched.</summary>
public abstract class CoverProviderConformanceTests : IDisposable
{
    private readonly List<string> _dirs = new();

    protected abstract string SearchJson(params (int Id, string Name)[] candidates);
    protected abstract string CacheImagePath(GameEntry game, string cacheDir);
    protected abstract ProviderRun Run(GameEntry game, string cacheDir,
        Func<string, string> search, Func<int, byte[]?> image);

    /// <summary>Wraps raw candidate JSON objects in this provider's search-response shape (IGDB: a bare array;
    /// SteamGridDB: {"data": [...]}), so a test can hand the provider a candidate that no well-formed serializer
    /// would ever produce.</summary>
    protected abstract string WrapCandidates(params string[] rawCandidates);

    /// <summary>A well-formed search response holding no candidates at all.</summary>
    protected abstract string EmptySearchBody { get; }

    /// <summary>Bodies that are valid JSON but not this provider's search-response shape.</summary>
    protected abstract IReadOnlyList<string> WrongShapeSearchBodies { get; }

    /// <summary>The real request pipeline: the search AND the cover/grid LISTING AND the image are all served
    /// through one HttpMessageHandler, so the listing's parsing is exercised (the `image` seam above bypasses
    /// it). `listing` is the raw body of the listing endpoint. ImageCalls counts image requests.</summary>
    protected abstract ProviderRun RunWire(GameEntry game, string cacheDir, string searchBody, string listing);

    protected abstract string ValidListing { get; }
    protected abstract string EmptyListing { get; }

    /// <summary>Listings that are valid JSON but not this provider's listing shape, or whose first entry has no
    /// usable image reference.</summary>
    protected abstract IReadOnlyList<string> MalformedListings { get; }

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    protected string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Conformance-" + Guid.NewGuid());
        _dirs.Add(dir);
        return dir;
    }

    protected static GameEntry Game(string name = "Test Game", string? catalogName = null, string id = "manual-conformance") => new()
    {
        Id = id, Name = name, CatalogName = catalogName, ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Writes a cache entry exactly as a provider would have (image + sidecar), so a test can put a
    /// specific, possibly hostile, entry in place before the lookup.</summary>
    private void WriteCacheEntry(GameEntry game, string cacheDir, byte[] imageBytes,
        int id = 11, string title = "Test Game", string? searchedName = null, string? sha = null)
    {
        var path = CacheImagePath(game, cacheDir);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(path, imageBytes);
        File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(new
        {
            Id = id, Title = title, SearchedName = searchedName ?? game.CatalogName ?? game.Name, ImageSha256 = sha ?? Sha(imageBytes),
        }));
    }

    // ---- The happy path and the cache ---------------------------------------------------------------

    [Fact]
    public void UniqueExactMatch_WithValidImage_IsResolved_AndCached()
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.NotNull(run.Image);
        Assert.Equal((11, "Test Game"), run.Matched);
        Assert.False(run.FromCache);
        Assert.True(File.Exists(CacheImagePath(game, dir)));
        Assert.True(File.Exists(CacheImagePath(game, dir) + ".meta.json"));
    }

    [Fact]
    public void SecondLookup_IsServedFromCache_WithoutSearchingOrDownloading()
    {
        var dir = NewCacheDir();
        var game = Game();
        Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        var second = Run(game, dir, _ => throw new InvalidOperationException("must not search"), _ => throw new InvalidOperationException("must not download"));

        Assert.Equal(CoverLookupStatus.Resolved, second.Status);
        Assert.True(second.FromCache);
        Assert.Equal((11, "Test Game"), second.Matched);
        Assert.Equal(0, second.SearchCalls);
        Assert.Equal(0, second.ImageCalls);
    }

    // ---- Matching discipline: exact, and UNIQUE ------------------------------------------------------

    [Fact]
    public void TwoDistinctExactMatches_AreAmbiguous_AndNothingIsDownloadedOrCached()
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => SearchJson((11, "Test Game"), (12, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Ambiguous, run.Status);
        Assert.Null(run.Image);
        Assert.Null(run.Matched);
        Assert.Equal(0, run.ImageCalls);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    [Fact]
    public void ARepeatedId_IsOneCandidate_NotTwo()
    {
        // The same catalog entry listed twice is redundancy, not a second product.
        var run = Run(Game(), NewCacheDir(), _ => SearchJson((11, "Test Game"), (11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Equal((11, "Test Game"), run.Matched);
    }

    [Fact]
    public void NoExactMatch_IsNoMatch_NotAFuzzyPick()
    {
        var run = Run(Game(), NewCacheDir(), _ => SearchJson((11, "Test Game: Deluxe Edition")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.NoMatch, run.Status);
        Assert.Null(run.Image);
        Assert.Equal(0, run.ImageCalls);
    }

    [Fact]
    public void ExactMatchAmongEditionsAndDlc_ResolvesTheBaseGameOnly()
    {
        // The real EA Sports FC 27 shape: the base game plus separately catalogued editions.
        var run = Run(Game("EA Sports FC 27"), NewCacheDir(),
            _ => SearchJson((408819, "EA Sports FC 27"), (410902, "EA Sports FC 27: Ultimate Edition"), (411107, "EA Sports FC 27: Ultimate Plus Edition")),
            _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Equal((408819, "EA Sports FC 27"), run.Matched);
    }

    [Fact]
    public void KnownUmbrellaName_IsAmbiguous_AndNeverSearches()
    {
        var run = Run(Game("Call of Duty"), NewCacheDir(), _ => SearchJson((1, "Call of Duty")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Ambiguous, run.Status);
        Assert.Equal(0, run.SearchCalls);
        Assert.Null(run.Image);
    }

    [Fact]
    public void ACatalogName_IsWhatIsSearched_NotTheRawDetectedName()
    {
        var run = Run(Game("Apex", catalogName: "Apex Legends"), NewCacheDir(),
            _ => SearchJson((114795, "Apex Legends")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Contains("Apex Legends", run.LastQuery);
    }

    // ---- Image validation on the DOWNLOAD path ---------------------------------------------------------

    [Fact]
    public void IdentifiedButNoArt_IsIdentifiedWithoutUsableArt_AndNothingIsCached()
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => null);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, run.Status);
        Assert.Null(run.Image);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    public static IEnumerable<object[]> UnusableImages()
    {
        yield return new object[] { "over the width limit (9000x100)", TestImages.Png(9000, 100) };
        yield return new object[] { "over the height limit (100x9000)", TestImages.Png(100, 9000) };
        yield return new object[] { "not an image at all", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };
        yield return new object[] { "empty body", Array.Empty<byte>() };
    }

    [Theory]
    [MemberData(nameof(UnusableImages))]
    public void AnImageThatFailsValidation_IsIdentifiedWithoutUsableArt_AndIsNeverCached(string why, byte[] bytes)
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => bytes);

        Assert.True(run.Status == CoverLookupStatus.IdentifiedWithoutUsableArt, $"{why}: got {run.Status}");
        Assert.Null(run.Image);
        Assert.False(File.Exists(CacheImagePath(game, dir)), $"{why}: an invalid image must never reach the cache");
    }

    [Fact]
    public void ADecodableContainerOutsideTheAssetStoreFormats_IsAccepted()
    {
        // Provider art is never staged into ArtworkAssetStore, so its five-format allowlist must not narrow
        // what automatic covers can be (SteamGridDB documents WebP grids). Only the bounds apply.
        var run = Run(Game(), NewCacheDir(), _ => SearchJson((11, "Test Game")), _ => TestImages.Icon());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.NotNull(run.Image);
    }

    // ---- Image validation on the CACHE-HIT path (the point of B1's "including cache hits") ----------------

    [Fact]
    public void ACacheHit_WithAnOversizedImage_AndAFullyValidSidecar_IsNotServed_AndIsRefetched()
    {
        // Hash matches, sidecar id/title/searched-name all valid - only the ORIGINAL dimensions are out of
        // bounds. Before B1 a cache hit decoded a downsampled copy and served this.
        var dir = NewCacheDir();
        var game = Game();
        var oversized = TestImages.Png(9000, 100);
        WriteCacheEntry(game, dir, oversized);
        var fresh = TestImages.Png(60, 90);

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => fresh);

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.Equal(1, run.SearchCalls);
        Assert.Equal(fresh, File.ReadAllBytes(CacheImagePath(game, dir))); // the oversized entry was replaced
    }

    [Fact]
    public void ACacheHit_WithAnOversizedImage_AndNothingToReplaceItWith_IsNeverServed_AndIsDeleted()
    {
        var dir = NewCacheDir();
        var game = Game();
        WriteCacheEntry(game, dir, TestImages.Png(9000, 100));

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => null);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, run.Status);
        Assert.Null(run.Image);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
        Assert.False(File.Exists(CacheImagePath(game, dir) + ".meta.json"));
    }

    [Fact]
    public void ACacheHit_WithACorruptImage_IsRefetched()
    {
        var dir = NewCacheDir();
        var game = Game();
        WriteCacheEntry(game, dir, new byte[] { 9, 9, 9, 9 });

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
    }

    [Fact]
    public void ACacheHit_WhoseSidecarWasWrittenForADifferentSearchName_IsStale_AndRefetched()
    {
        var dir = NewCacheDir();
        var game = Game();
        WriteCacheEntry(game, dir, TestImages.Png(), searchedName: "Some Other Title");

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.Equal(1, run.SearchCalls);
    }

    [Fact]
    public void ACacheHit_WhoseSidecarHashDoesNotMatchTheImage_IsRefetched()
    {
        var dir = NewCacheDir();
        var game = Game();
        WriteCacheEntry(game, dir, TestImages.Png(), sha: new string('0', 64));

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
    }

    [Fact]
    public void ACacheFile_OverTheByteCap_IsNotLoadedInFull_AndIsRefetched()
    {
        // ArtworkImageValidator.MaxFileBytes + 1 zero bytes: never a valid image, and must be rejected by the
        // bounded read rather than loaded into memory whole first.
        var dir = NewCacheDir();
        var game = Game();
        Directory.CreateDirectory(dir);
        using (var f = File.Create(CacheImagePath(game, dir)))
            f.SetLength(ArtworkImageValidator.MaxFileBytes + 1L);

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.True(new FileInfo(CacheImagePath(game, dir)).Length < ArtworkImageValidator.MaxFileBytes);
    }

    [Fact]
    public void ACacheFile_TooLargeToEverLoadIntoMemory_IsAnInvalidEntry_NotAnOutage()
    {
        // The wiring of the bounded read, which the 20MB+1 case above cannot prove (a full read followed by
        // validation rejects that file too, with the same outcome). File.ReadAllBytes cannot read a file over
        // 2GB at all - it throws - which the provider boundary would report as Unavailable; the bounded read
        // never gets that far and treats the file as what it is: an invalid cache entry to delete and re-fetch.
        // Created with SetLength, so no data is written (NTFS extends the file without touching the disk).
        var dir = NewCacheDir();
        var game = Game();
        Directory.CreateDirectory(dir);
        using (var f = File.Create(CacheImagePath(game, dir)))
            f.SetLength(int.MaxValue + 1L);

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.True(new FileInfo(CacheImagePath(game, dir)).Length < ArtworkImageValidator.MaxFileBytes);
    }

    // ---- Failure is Unavailable, never a non-match ---------------------------------------------------------

    [Fact]
    public void ASearchThatThrows_IsUnavailable_NotNoMatch()
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => throw new HttpRequestException("boom"), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
        Assert.Null(run.Image);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    [Fact]
    public void AMalformedSearchBody_IsUnavailable_AndNeverEscapes()
    {
        var run = Run(Game(), NewCacheDir(), _ => "this is not json", _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
    }

    // ---- Malformed candidates fail closed: an unreadable candidate never establishes uniqueness ------------

    private const string ValidExact = """{"id": 111, "name": "Test Game"}""";

    public static IEnumerable<object[]> UnreadableExactMatches()
    {
        var unreadable = new (string Why, string Json)[]
        {
            ("id is a string (the audited counterexample)", """{"id": "invalid", "name": "Test Game"}"""),
            ("id is null", """{"id": null, "name": "Test Game"}"""),
            ("id is missing", """{"name": "Test Game"}"""),
            ("id is zero", """{"id": 0, "name": "Test Game"}"""),
            ("id is negative", """{"id": -3, "name": "Test Game"}"""),
            ("id is not an integer", """{"id": 1.5, "name": "Test Game"}"""),
            ("id overflows an int", """{"id": 99999999999, "name": "Test Game"}"""),
        };
        foreach (var (why, json) in unreadable)
        {
            yield return new object[] { why, json, false };
            yield return new object[] { why, json, true };
        }
    }

    [Theory]
    [MemberData(nameof(UnreadableExactMatches))]
    public void AnExactMatchWithAnUnreadableId_BesideAReadableOne_IsUnavailable_NotAUniqueMatch(string why, string unreadable, bool unreadableFirst)
    {
        // The unreadable entry may be a second product, or the only real one; the response does not say.
        // Uniqueness was never established, so the readable one must NOT be returned as a confident match -
        // whichever order the two arrive in - and nothing may be downloaded or cached.
        var dir = NewCacheDir();
        var game = Game();
        var body = unreadableFirst ? WrapCandidates(unreadable, ValidExact) : WrapCandidates(ValidExact, unreadable);

        var run = Run(game, dir, _ => body, _ => TestImages.Png());

        Assert.True(run.Status == CoverLookupStatus.Unavailable, $"{why}: expected Unavailable, got {run.Status}");
        Assert.Null(run.Image);
        Assert.Null(run.Matched);
        Assert.Equal(0, run.ImageCalls);
        Assert.False(File.Exists(CacheImagePath(game, dir)), $"{why}: nothing may be cached");
    }

    [Theory]
    [InlineData("""{"id": "invalid", "name": "Test Game"}""")]
    [InlineData("""{"name": "Test Game"}""")]
    [InlineData("""{"id": 0, "name": "Test Game"}""")]
    public void AnUnreadableIdOnTheOnlyMatch_IsUnavailable_NotNoMatch(string onlyCandidate)
    {
        // Alone it cannot be a "confident match" (no id to fetch art for), and it cannot be "no match" either:
        // its name says it IS the game searched for.
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => WrapCandidates(onlyCandidate), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
        Assert.Equal(0, run.ImageCalls);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    [Theory]
    [InlineData("""{"id": 5}""")] // no name
    [InlineData("""{"id": 5, "name": null}""")]
    [InlineData("""{"id": 5, "name": 42}""")]
    [InlineData("""{"id": 5, "name": ""}""")]
    [InlineData("""{"id": 5, "name": "   "}""")]
    public void ACandidateWithNoUsableName_BesideAnExactMatch_IsUnavailable(string nameless)
    {
        // It could be anything, including a second exact match, so uniqueness is unproven.
        var dir = NewCacheDir();
        var game = Game();

        var run = Run(game, dir, _ => WrapCandidates(ValidExact, nameless), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
        Assert.Null(run.Matched);
        Assert.Equal(0, run.ImageCalls);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    [Theory]
    [InlineData("""{"id": 5}""")]
    [InlineData("""{"id": 5, "name": null}""")]
    [InlineData("""{"id": 5, "name": 42}""")]
    public void AMissingName_IsNeverReplacedByAPlaceholderThatCanMatch(string nameless)
    {
        // The selectors used to substitute the literal "unknown" for an unreadable name - so a game genuinely
        // called "unknown" matched every nameless candidate, on no evidence at all.
        var dir = NewCacheDir();
        var game = Game("unknown");

        var run = Run(game, dir, _ => WrapCandidates(nameless), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
        Assert.Null(run.Matched);
        Assert.Equal(0, run.ImageCalls);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("[]")]
    public void ACandidateThatIsNotAnObject_BesideAnExactMatch_IsUnavailable(string notAnObject)
    {
        var run = Run(Game(), NewCacheDir(), _ => WrapCandidates(ValidExact, notAnObject), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Unavailable, run.Status);
        Assert.Null(run.Matched);
        Assert.Equal(0, run.ImageCalls);
    }

    [Theory]
    [InlineData("""{"id": "invalid", "name": "Something Else"}""")]
    [InlineData("""{"id": -1, "name": "Something Else"}""")]
    [InlineData("""{"name": "Something Else"}""")]
    public void AnUnreadableIdOnAProvablyDifferentTitle_DoesNotBlockAUniqueMatch(string differentTitle)
    {
        // The readable name proves it is not a match, so it cannot create a second one: only a candidate that
        // COULD be a match has to be readable. (A ruling to confirm - stricter would be "any malformed => Unavailable".)
        var run = Run(Game(), NewCacheDir(), _ => WrapCandidates(differentTitle, ValidExact), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Equal((111, "Test Game"), run.Matched);
    }

    [Fact]
    public void ARepeatedValidId_IsStillOneCandidate_EvenBesideAnUnreadableDifferentTitle()
    {
        var run = Run(Game(), NewCacheDir(),
            _ => WrapCandidates(ValidExact, """{"id": "x", "name": "Other"}""", ValidExact), _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Equal((111, "Test Game"), run.Matched);
    }

    // ---- Response SHAPES: malformed is Unavailable, a valid empty result is a real NoMatch -----------------

    [Fact]
    public void AValidEmptySearch_IsNoMatch_NotUnavailable()
    {
        var run = Run(Game(), NewCacheDir(), _ => EmptySearchBody, _ => TestImages.Png());

        Assert.Equal(CoverLookupStatus.NoMatch, run.Status);
        Assert.Equal(0, run.ImageCalls);
    }

    [Fact]
    public void ASearchResponseOfTheWrongShape_IsUnavailable_NotNoMatch()
    {
        // Valid JSON, wrong shape - e.g. {} - used to return null and read as a catalog non-match.
        foreach (var body in WrongShapeSearchBodies)
        {
            var dir = NewCacheDir();
            var game = Game();

            var run = Run(game, dir, _ => body, _ => TestImages.Png());

            Assert.True(run.Status == CoverLookupStatus.Unavailable, $"body {body}: expected Unavailable, got {run.Status}");
            Assert.Equal(0, run.ImageCalls);
            Assert.False(File.Exists(CacheImagePath(game, dir)));
        }
    }

    [Fact]
    public void TheListingSeam_Baseline_AValidListingResolves_ThroughTheRealPipeline()
    {
        // Guards the wire harness itself: if this fails, the listing tests below prove nothing.
        var dir = NewCacheDir();
        var game = Game();

        var run = RunWire(game, dir, WrapCandidates(ValidExact), ValidListing);

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.Equal((111, "Test Game"), run.Matched);
        Assert.Equal(1, run.ImageCalls);
    }

    [Fact]
    public void AValidEmptyListing_IsIdentifiedWithoutUsableArt_NotUnavailable()
    {
        var dir = NewCacheDir();
        var game = Game();

        var run = RunWire(game, dir, WrapCandidates(ValidExact), EmptyListing);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, run.Status);
        Assert.Equal(0, run.ImageCalls);
        Assert.False(File.Exists(CacheImagePath(game, dir)));
    }

    [Fact]
    public void AMalformedListing_IsUnavailable_NotIdentifiedWithoutUsableArt()
    {
        // The game WAS identified, but a listing the provider cannot read says nothing about whether it has
        // art - so this is an outage-shaped failure, not a definite "no art".
        foreach (var listing in MalformedListings)
        {
            var dir = NewCacheDir();
            var game = Game();

            var run = RunWire(game, dir, WrapCandidates(ValidExact), listing);

            Assert.True(run.Status == CoverLookupStatus.Unavailable, $"listing {listing}: expected Unavailable, got {run.Status}");
            Assert.Null(run.Image);
            Assert.Equal(0, run.ImageCalls);
            Assert.False(File.Exists(CacheImagePath(game, dir)));
        }
    }

    // ---- The cache SIDECAR is read under a byte cap --------------------------------------------------------------

    private string SidecarPath(GameEntry game, string dir) => CacheImagePath(game, dir) + ".meta.json";

    [Fact]
    public void ASidecarExactlyAtTheByteCap_IsStillTrusted_TheBoundIsNotOffByOne()
    {
        var dir = NewCacheDir();
        var game = Game();
        var png = TestImages.Png();
        WriteCacheEntry(game, dir, png);
        var valid = File.ReadAllText(SidecarPath(game, dir));
        File.WriteAllText(SidecarPath(game, dir), valid + new string(' ', ProviderHttp.MaxSidecarBytes - valid.Length));
        Assert.Equal(ProviderHttp.MaxSidecarBytes, new FileInfo(SidecarPath(game, dir)).Length);

        var run = Run(game, dir, _ => throw new InvalidOperationException("must not search"), _ => throw new InvalidOperationException("must not download"));

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.True(run.FromCache);
    }

    [Fact]
    public void ASidecarOneByteOverTheCap_IsUnverifiableEvidence_AndTheEntryIsRefetched()
    {
        // Padding with whitespace keeps it VALID JSON describing exactly these cached bytes - so only the size
        // bound can be what rejects it (File.ReadAllText + Deserialize would have trusted it).
        var dir = NewCacheDir();
        var game = Game();
        var png = TestImages.Png();
        WriteCacheEntry(game, dir, png);
        var valid = File.ReadAllText(SidecarPath(game, dir));
        File.WriteAllText(SidecarPath(game, dir), valid + new string(' ', ProviderHttp.MaxSidecarBytes + 1 - valid.Length));

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => png);

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.Equal(1, run.SearchCalls);
        Assert.Equal(1, run.ImageCalls);
        Assert.True(new FileInfo(SidecarPath(game, dir)).Length < ProviderHttp.MaxSidecarBytes); // rewritten to its real size
    }

    [Fact]
    public void ASidecarTooLargeToEverLoadIntoMemory_IsUnverifiableEvidence_NotAFailure()
    {
        // File.ReadAllText cannot read this (it throws something the sidecar reader does not catch, which then
        // surfaces as an outage); the bounded read treats it as an invalid entry to re-fetch. SetLength writes no data.
        var dir = NewCacheDir();
        var game = Game();
        var png = TestImages.Png();
        WriteCacheEntry(game, dir, png);
        using (var f = new FileStream(SidecarPath(game, dir), FileMode.Truncate))
            f.SetLength(int.MaxValue + 1L);

        var run = Run(game, dir, _ => SearchJson((11, "Test Game")), _ => png);

        Assert.Equal(CoverLookupStatus.Resolved, run.Status);
        Assert.False(run.FromCache);
        Assert.Equal(1, run.ImageCalls);
    }
}

/// <summary>The conformance contract against IgdbCoverArtProvider. In the IGDB static-state collection out of
/// caution, although with both transport seams set nothing shared (token cache, rate-limit clock) is used.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public sealed class IgdbCoverProviderConformanceTests : CoverProviderConformanceTests
{
    protected override string SearchJson(params (int Id, string Name)[] candidates) =>
        JsonSerializer.Serialize(candidates.Select(c => new { id = c.Id, name = c.Name }));

    protected override string CacheImagePath(GameEntry game, string cacheDir) =>
        Path.Combine(cacheDir, $"{game.Id}-v{IgdbCoverArtProvider.CacheVersionForTest}.png");

    protected override string WrapCandidates(params string[] rawCandidates) => "[" + string.Join(",", rawCandidates) + "]";
    protected override string EmptySearchBody => "[]";
    protected override IReadOnlyList<string> WrongShapeSearchBodies { get; } =
        new[] { "{}", """{"data": []}""", "\"a string\"", "5", "null", """{"message": "oops"}""" };

    protected override string ValidListing => """[{"id": 1, "image_id": "co1abc"}]""";
    protected override string EmptyListing => "[]";
    protected override IReadOnlyList<string> MalformedListings { get; } = new[]
    {
        "{}", """{"data": []}""", "\"a string\"", "null", "5",
        "[123]", "[null]", "[[]]", """["co1abc"]""",
        """[{"id": 1}]""", """[{"id": 1, "image_id": null}]""", """[{"id": 1, "image_id": 7}]""",
        """[{"id": 1, "image_id": ""}]""", """[{"id": 1, "image_id": "   "}]""",
    };

    protected override ProviderRun RunWire(GameEntry game, string cacheDir, string searchBody, string listing)
    {
        int searches = 0;
        var handler = new FakeIgdbHandler
        {
            OnApi = (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/covers", StringComparison.Ordinal))
                    return FakeIgdbHandler.Json(listing);
                searches++;
                return FakeIgdbHandler.Json(searchBody);
            },
            OnImage = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) },
        };
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };

        var bitmap = provider.GetCoverArt(game, out var fromCache, out var matched, out var status, cacheDir);
        return new ProviderRun(bitmap, status, matched is { } m ? (m.Id, m.Title) : null, fromCache, searches, handler.ImageCalls, null);
    }

    protected override ProviderRun Run(GameEntry game, string cacheDir, Func<string, string> search, Func<int, byte[]?> image)
    {
        int searches = 0, images = 0;
        string? last = null;
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = q => { searches++; last = q; return search(q); },
            FetchCoverImageBytesOverride = id => { images++; return image(id); },
        };

        var bitmap = provider.GetCoverArt(game, out var fromCache, out var matched, out var status, cacheDir);
        return new ProviderRun(bitmap, status, matched is { } m ? (m.Id, m.Title) : null, fromCache, searches, images, last);
    }
}

/// <summary>The conformance contract against SteamGridDbCoverArtProvider.</summary>
public sealed class SteamGridDbCoverProviderConformanceTests : CoverProviderConformanceTests
{
    protected override string SearchJson(params (int Id, string Name)[] candidates) =>
        JsonSerializer.Serialize(new { data = candidates.Select(c => new { id = c.Id, name = c.Name, types = Array.Empty<string>() }) });

    protected override string CacheImagePath(GameEntry game, string cacheDir) =>
        Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");

    protected override string WrapCandidates(params string[] rawCandidates) => """{"data": [""" + string.Join(",", rawCandidates) + "]}";
    protected override string EmptySearchBody => """{"data": []}""";
    protected override IReadOnlyList<string> WrongShapeSearchBodies { get; } =
        new[] { "{}", "[]", "\"a string\"", "5", "null", """{"data": "x"}""", """{"data": null}""", """{"data": {}}""", """{"success": false}""" };

    protected override string ValidListing => """{"data": [{"url": "https://cdn2.steamgriddb.com/grid/cover.png"}]}""";
    protected override string EmptyListing => """{"data": []}""";
    protected override IReadOnlyList<string> MalformedListings { get; } = new[]
    {
        "{}", "[]", "\"a string\"", "null", "5", """{"data": "x"}""", """{"data": null}""", """{"data": {}}""",
        """{"data": [123]}""", """{"data": [null]}""", """{"data": [{}]}""",
        """{"data": [{"url": null}]}""", """{"data": [{"url": 7}]}""", """{"data": [{"url": ""}]}""",
        """{"data": [{"url": "   "}]}""", """{"data": [{"url": "not a url"}]}""", """{"data": [{"url": "/relative.png"}]}""",
    };

    protected override ProviderRun RunWire(GameEntry game, string cacheDir, string searchBody, string listing)
    {
        int searches = 0, images = 0;
        var handler = new FuncHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/search/autocomplete/", StringComparison.Ordinal))
            {
                searches++;
                return FakeIgdbHandler.Json(searchBody);
            }

            if (path.Contains("/grids/game/", StringComparison.Ordinal))
                return FakeIgdbHandler.Json(listing);

            images++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) };
        });
        var provider = new SteamGridDbCoverArtProvider("key") { HttpHandlerOverrideForTest = handler };

        var bitmap = provider.GetCoverArt(game, out var fromCache, out var matched, out var status, cacheDir);
        return new ProviderRun(bitmap, status, matched is { } m ? (m.Id, m.Title) : null, fromCache, searches, images, null);
    }

    protected override ProviderRun Run(GameEntry game, string cacheDir, Func<string, string> search, Func<int, byte[]?> image)
    {
        int searches = 0, images = 0;
        string? last = null;
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = q => { searches++; last = q; return search(q); },
            FetchGridImageBytesOverride = id => { images++; return image(id); },
        };

        var bitmap = provider.GetCoverArt(game, out var fromCache, out var matched, out var status, cacheDir);
        return new ProviderRun(bitmap, status, matched is { } m ? (m.Id, m.Title) : null, fromCache, searches, images, last);
    }
}
