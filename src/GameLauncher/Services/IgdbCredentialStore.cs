using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameLauncher.Services;

/// <summary>
/// Stores the IGDB Client Secret OS-protected (Windows DPAPI, CurrentUser scope) in its OWN file,
/// entirely OUTSIDE AppSettings/settings.json - a real, confirmed gap this closes: AppSettings.
/// IgdbClientSecret used to be a plain serializable string, which SettingsService.Save wrote into
/// settings.json in plaintext, and from there into its own temp file and backup copy too (see
/// SettingsService's own atomic-write/backup remarks) - .gitignore protects nothing at runtime; it only
/// keeps a file out of source control. DPAPI ties the encrypted bytes to the CURRENT WINDOWS USER ACCOUNT
/// on THIS MACHINE - copying the protected file to another machine or user account makes it
/// undecryptable there, which is exactly the property "protected credential storage" needs and a plain
/// settings field never had.
///
/// An INSTANCE with an immutable directory, deliberately not a static class with a settable override: an
/// earlier version exposed a static DataDirOverrideForTest that two test classes both set and reset to
/// null, and xUnit runs test classes in parallel by default - if one class reset it between the other's
/// set and its SaveSecret/LoadSecret call, that call would silently target the REAL credential file
/// (including deleting it, via SaveSecret(null)). There is intentionally NO parameterless constructor and
/// no fallback to the production directory here: every caller must state which directory it means, and
/// LibraryViewModel derives it from the SettingsService it was handed (the same lesson its own
/// constructor's remarks record for PendingUpdateNotesService), so a test that isolates settings gets an
/// isolated credential store automatically instead of relying on a global switch it has to remember.
///
/// AppSettings.IgdbClientId remains a normal settings field - a Client ID is not the sensitive half of
/// this credential pair (Twitch's own client_credentials flow treats only the secret as a password-
/// equivalent - see IgdbCoverArtProvider's own remarks).
///
/// The Settings window writes here (SaveSecret) when a user enters their OWN Twitch application's secret; that
/// pair is then used directly and bypasses the project's relay (IgdbAccess.Resolve).
/// </summary>
public sealed class IgdbCredentialStore
{
    private readonly string _dataDir;
    private readonly string _secretPath;

    public IgdbCredentialStore(string dataDir)
    {
        _dataDir = dataDir;
        _secretPath = Path.Combine(dataDir, "igdb-client-secret.protected");
    }

    public void SaveSecret(string? secret)
    {
        try
        {
            if (string.IsNullOrEmpty(secret))
            {
                if (File.Exists(_secretPath))
                    File.Delete(_secretPath);
                return;
            }

            Directory.CreateDirectory(_dataDir);
            var plainBytes = Encoding.UTF8.GetBytes(secret);
            var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_secretPath, protectedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Logger.Warn("Couldn't save the IGDB client secret to protected storage.", ex);
        }
    }

    /// <summary>Whether a secret has been saved - a file-existence check only, so showing "credentials saved" in Settings never
    /// decrypts (or even reads) the secret.</summary>
    public bool HasSecret()
    {
        try
        {
            return File.Exists(_secretPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Null for "nothing saved" (never configured, or a previous save failed) as well as for a
    /// genuine decryption failure (e.g. the file was copied from a different machine/user account, where
    /// DPAPI can never recover the original bytes) - both mean the same thing to every caller: IGDB has no
    /// usable secret right now, fall back to SteamGridDB exactly as if it were never configured.</summary>
    public string? LoadSecret()
    {
        try
        {
            if (!File.Exists(_secretPath))
                return null;

            var protectedBytes = File.ReadAllBytes(_secretPath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            Logger.Warn("Couldn't load the IGDB client secret from protected storage.", ex);
            return null;
        }
    }
}
