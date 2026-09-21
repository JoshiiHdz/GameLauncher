using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>Each test builds its OWN IgdbCredentialStore over its OWN immutable temp directory - there is
/// no shared or settable state anywhere in this class or in the store, so no interleaving of xUnit's
/// parallel test classes can redirect a Save/Load/Delete at another test's (or the real) credential file.
/// An earlier version used a static DataDirOverrideForTest that this class and SettingsServiceTests both
/// set and reset to null: a reset landing between the other class's set and its SaveSecret call would have
/// silently pointed that call - including SaveSecret(null), which deletes - at the REAL file.</summary>
public class IgdbCredentialStoreTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private (IgdbCredentialStore Store, string Dir) NewIsolatedStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IgdbCreds-" + Guid.NewGuid());
        _dirs.Add(dir);
        return (new IgdbCredentialStore(dir), dir);
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheSecretExactly()
    {
        var (store, _) = NewIsolatedStore();

        store.SaveSecret("sk_test_sentinel_secret_value");

        Assert.Equal("sk_test_sentinel_secret_value", store.LoadSecret());
    }

    [Fact]
    public void LoadSecret_NothingSaved_ReturnsNull()
    {
        var (store, _) = NewIsolatedStore();

        Assert.Null(store.LoadSecret());
    }

    [Fact]
    public void SaveSecret_Null_DeletesAnyExistingSavedSecret()
    {
        var (store, _) = NewIsolatedStore();
        store.SaveSecret("something");
        Assert.NotNull(store.LoadSecret());

        store.SaveSecret(null);

        Assert.Null(store.LoadSecret());
    }

    [Fact]
    public void TwoStoresOverDifferentDirectories_NeverAffectEachOther_IncludingDeletion()
    {
        // The property the old static override could not provide: state is per-instance and immutable, so
        // one store saving, overwriting, or DELETING can never touch another store's file - there is no
        // shared switch for an interleaved test to flip.
        var (storeA, _) = NewIsolatedStore();
        var (storeB, _) = NewIsolatedStore();
        storeA.SaveSecret("secret-A");
        storeB.SaveSecret("secret-B");

        storeA.SaveSecret(null);

        Assert.Null(storeA.LoadSecret());
        Assert.Equal("secret-B", storeB.LoadSecret());
    }

    [Fact]
    public void StoreOnlyEverTouchesItsOwnDirectory()
    {
        var (store, dir) = NewIsolatedStore();

        store.SaveSecret("anything");

        var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Single(written);
        Assert.StartsWith(Path.GetFullPath(dir), Path.GetFullPath(written[0]));
    }

    [Fact]
    public void SavedFile_NeverContainsThePlaintextSecret_OnDisk()
    {
        // The whole point of this store: the bytes actually written to disk must not contain the
        // plaintext secret anywhere in them - DPAPI-encrypted output, not a base64/obfuscated wrapper
        // around the same bytes.
        const string sentinel = "sk_test_sentinel_secret_value_that_must_never_appear_on_disk";
        var (store, dir) = NewIsolatedStore();
        store.SaveSecret(sentinel);

        var protectedFile = Directory.GetFiles(dir, "*.protected").Single();
        var rawBytes = File.ReadAllBytes(protectedFile);

        // A real substring search over bytes, not a fragile UTF-8-decode-then-string-contains check that
        // DPAPI's invalid-UTF-8 output could itself defeat.
        var sentinelBytes = System.Text.Encoding.UTF8.GetBytes(sentinel);
        Assert.False(ContainsSubsequence(rawBytes, sentinelBytes), "The protected file's raw bytes contain the plaintext secret.");
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public void LoadSecret_CorruptedProtectedFile_ReturnsNull_DoesNotThrow()
    {
        var (store, dir) = NewIsolatedStore();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "igdb-client-secret.protected"), [1, 2, 3, 4, 5]); // not real DPAPI output

        var exception = Record.Exception(() => store.LoadSecret());

        Assert.Null(exception);
        Assert.Null(store.LoadSecret());
    }
}
