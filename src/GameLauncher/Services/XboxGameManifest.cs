using System.IO;
using System.Xml.Linq;

namespace GameLauncher.Services;

/// <summary>One executable a package's own metadata names as a launch target, before this app decides
/// anything about it. ExeRelativePath is exactly what the manifest says (never resolved/verified against
/// disk here - that's the caller's job, since "the manifest says X" and "X exists and is readable" are
/// two different facts, and the caller is also the one that validates the path is actually contained
/// within the install root before ever combining it). ApplicationId, when present, is what an AUMID is
/// built from (PackageFamilyName!ApplicationId) - more reliable than fuzzy-matching a display name
/// against Get-StartApps, since it's the identity the manifest itself declares.</summary>
public sealed record XboxLaunchTarget(string ExeRelativePath, string? ApplicationId, string? DisplayName);

/// <summary>What reading a package's own metadata produced: at most one target with any confidence -
/// several equally-plausible ones is reported as Ambiguous, not resolved by guessing (design requirement:
/// "preserve the ambiguity rather than selecting the wrong game"). Candidates carries the full list for
/// an Ambiguous result, so a caller with INDEPENDENT evidence (a package identity/application id already
/// confirmed some other way) can still resolve it - not by guessing, by recognizing which of these
/// specific candidates that evidence already points to.
///
/// None and IncompatibleTarget both mean "no PC executable came out of this file", but for genuinely
/// different reasons a caller MUST tell apart: None is "this file said nothing at all" (missing,
/// unreadable, or declared zero executables of any kind) - a real absence of information, which is
/// exactly the case a caller's own folder-search fallback exists for. IncompatibleTarget is "this file DID
/// declare executables, and EVERY one of them explicitly targets a non-PC device family" - an affirmative
/// declaration that no PC build exists, which a folder search must never second-guess: if it did, and one
/// of those console-only executables happens to physically sit in the folder, a search-based fallback
/// would launch it anyway, exactly undoing what the compatibility declaration was for.</summary>
public enum XboxManifestOutcome { None, Single, Ambiguous, IncompatibleTarget, NonPlayableContent }

public readonly record struct XboxManifestReadResult(
    XboxManifestOutcome Outcome, XboxLaunchTarget? Target, IReadOnlyList<XboxLaunchTarget>? Candidates = null)
{
    public static readonly XboxManifestReadResult NoInfo = new(XboxManifestOutcome.None, null);
    public static readonly XboxManifestReadResult IncompatibleTarget = new(XboxManifestOutcome.IncompatibleTarget, null);
    public static XboxManifestReadResult Single(XboxLaunchTarget target) => new(XboxManifestOutcome.Single, target);
    public static XboxManifestReadResult Ambiguous(IReadOnlyList<XboxLaunchTarget> candidates) =>
        new(XboxManifestOutcome.Ambiguous, null, candidates);
}

/// <summary>
/// Reads a Windows/Xbox game's OWN declared launch target, straight from the metadata Microsoft
/// documents for exactly this purpose - MicrosoftGame.config (GDK titles: learn.microsoft.com documents
/// its ExecutableList/Executable/Name/TargetDeviceFamily shape) and AppxManifest.xml (every packaged
/// app has one; its Applications/Application/@Executable is the same field Windows itself uses to launch
/// the app). This is the "no manifest to read" comment XboxScanner used to have being wrong: there IS
/// one, this reads it, and a scanner that trusts it never has to guess which exe in a folder is the real
/// game.
///
/// Both formats can legitimately list MORE than one executable (MicrosoftGame.config: one per target
/// device family - PC, Xbox, Scarlett, ...; AppxManifest.xml: one per Application element, rare but
/// possible). MicrosoftGame.config entries are filtered to PC-appropriate ones (TargetDeviceFamily
/// missing, or containing "pc"/"desktop") FIRST - an entry that explicitly declares a non-PC family
/// (TargetDeviceFamily="Xbox", "Scarlett", ...) is never a candidate, full stop, even if it's the only
/// executable the file lists: Microsoft's own schema (learn.microsoft.com/.../microsoftgameconfig-
/// element-executable) documents TargetDeviceFamily as an explicit compatibility declaration, and
/// reintroducing an explicitly-incompatible executable just because nothing else was left would mean
/// selecting a console build to run on PC. Zero PC-appropriate candidates is None (this file named
/// nothing usable), not "fall back to whatever's there". More than one PC-appropriate candidate is
/// Ambiguous - never an arbitrary "just take the first one".
/// </summary>
public static class XboxGameManifest
{
    private const string GameConfigFileName = "MicrosoftGame.config";
    private static readonly string[] AppxManifestFileNames = { "AppxManifest.xml", "appxmanifest.xml" };

    // XboxGames uses a content directory while Get-AppxPackage can expose a separate WindowsApps
    // registration path. The manifest's identity links those views without matching display names.
    internal static string FindMetadataRoot(string installRoot)
    {
        static bool HasManifest(string directory) => File.Exists(Path.Combine(directory, GameConfigFileName))
            || File.Exists(Path.Combine(directory, "AppxManifest.xml"));
        if (HasManifest(installRoot)) return installRoot;
        var content = Path.Combine(installRoot, "Content");
        // A directory named Content alone is not evidence: retain the whole-root executable fallback.
        return HasManifest(content) ? content : installRoot;
    }

    internal static string? ReadIdentityName(string installRoot)
    {
        foreach (var fileName in new[] { GameConfigFileName, "AppxManifest.xml" })
        {
            var path = Path.Combine(installRoot, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                var name = XDocument.Load(path).Root?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                Logger.Warn($"Xbox: couldn't read package identity from '{path}'.", ex);
            }
        }
        return null;
    }

    /// <summary>Tries MicrosoftGame.config first (GDK titles declare their launch target there
    /// explicitly, including which build is the PC one), then AppxManifest.xml (every packaged app has
    /// one, including non-GDK titles). Returns NoInfo, not an exception, for a missing/malformed file -
    /// a manifest that can't be read is exactly the case the caller's own fallback search exists for.</summary>
    public static XboxManifestReadResult ReadLaunchTarget(string installRoot)
    {
        var gameConfigPath = Path.Combine(installRoot, GameConfigFileName);
        if (File.Exists(gameConfigPath))
        {
            var fromGameConfig = ReadMicrosoftGameConfig(gameConfigPath);
            if (fromGameConfig.Outcome != XboxManifestOutcome.None)
                return fromGameConfig;
        }

        foreach (var name in AppxManifestFileNames)
        {
            var manifestPath = Path.Combine(installRoot, name);
            if (File.Exists(manifestPath))
            {
                var fromManifest = ReadAppxManifest(manifestPath);
                if (fromManifest.Outcome != XboxManifestOutcome.None)
                    return fromManifest;
            }
        }

        return XboxManifestReadResult.NoInfo;
    }

    internal static XboxManifestReadResult ReadMicrosoftGameConfig(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Logger.Warn($"Xbox: couldn't read '{path}'.", ex);
            return XboxManifestReadResult.NoInfo;
        }

        // Product relationships alone do not prove this is non-playable content: a base game can
        // declare them too. Respect its own launch declarations; exclude content-only manifests
        // before Appx/folder fallback can turn an undeclared payload into a game.
        var executables = doc.Descendants().Where(e => e.Name.LocalName == "Executable").ToList();
        if (executables.Count == 0 && doc.Root?.Elements().Any(e =>
                e.Name.LocalName == "TargetDeviceFamilyForDLC"
                || (e.Name.LocalName is "AllowedProducts" or "RelatedProducts" && e.Elements().Any())) == true)
            return new XboxManifestReadResult(XboxManifestOutcome.NonPlayableContent, null);

        // LocalName-based, not a hardcoded namespace URI: MicrosoftGame.config's schema has carried a
        // couple of different configVersion/namespace values across GDK releases, and every field this
        // reads is unambiguous by local name alone within the file.
        if (executables.Count == 0)
            return XboxManifestReadResult.NoInfo;

        // PC-appropriate only - NEVER falls back to "every executable" when this is empty. An explicit
        // non-PC TargetDeviceFamily is a compatibility declaration this app is not entitled to override
        // just because nothing else is available (see the class remarks for the real case that motivated
        // this: a config listing only Xbox/Scarlett executables must resolve to "no PC target", not to
        // whichever one happens to be there). IncompatibleTarget, not NoInfo: the file DID name
        // executables, it just named none this app can use - a caller must not treat that the same as
        // "this file said nothing", or a folder search could still pick up and launch the console-only
        // exe this declaration explicitly excluded.
        var pcCandidates = executables.Where(IsPcTargeted).ToList();
        if (pcCandidates.Count == 0)
            return XboxManifestReadResult.IncompatibleTarget;

        if (pcCandidates.Count > 1)
        {
            Logger.Warn($"Xbox: '{path}' lists {pcCandidates.Count} equally plausible PC executables - not guessing which one is the real build.");
            return XboxManifestReadResult.Ambiguous(pcCandidates.Select(ToLaunchTarget).Where(t => t is not null).Select(t => t!).ToList());
        }

        var target = ToLaunchTarget(pcCandidates[0]);
        return target is null ? XboxManifestReadResult.NoInfo : XboxManifestReadResult.Single(target);

        static XboxLaunchTarget? ToLaunchTarget(XElement exe)
        {
            var name = (string?)exe.Attribute("Name");
            return string.IsNullOrWhiteSpace(name) ? null : new XboxLaunchTarget(name, (string?)exe.Attribute("Id"), DisplayName: null);
        }

        static bool IsPcTargeted(XElement exe)
        {
            var family = (string?)exe.Attribute("TargetDeviceFamily");
            return string.IsNullOrWhiteSpace(family)
                || family.Contains("pc", StringComparison.OrdinalIgnoreCase)
                || family.Contains("desktop", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static XboxManifestReadResult ReadAppxManifest(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Logger.Warn($"Xbox: couldn't read '{path}'.", ex);
            return XboxManifestReadResult.NoInfo;
        }

        var applications = doc.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
        if (applications.Count == 0)
            return XboxManifestReadResult.NoInfo;

        if (applications.Count > 1)
        {
            Logger.Warn($"Xbox: '{path}' declares {applications.Count} Application entries - not guessing which one is the real game.");
            var candidates = applications.Select(ToLaunchTarget).Where(t => t is not null).Select(t => t!).ToList();
            return candidates.Count > 0 ? XboxManifestReadResult.Ambiguous(candidates) : XboxManifestReadResult.NoInfo;
        }

        var single = ToLaunchTarget(applications[0]);
        return single is null ? XboxManifestReadResult.NoInfo : XboxManifestReadResult.Single(single);

        static XboxLaunchTarget? ToLaunchTarget(XElement app)
        {
            var executable = (string?)app.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(executable))
                return null; // e.g. a pure-UWP app with no Win32 executable at all

            var id = (string?)app.Attribute("Id");
            var displayName = app.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")
                ?.Attribute("DisplayName")?.Value;

            return new XboxLaunchTarget(executable, id, displayName);
        }
    }
}
