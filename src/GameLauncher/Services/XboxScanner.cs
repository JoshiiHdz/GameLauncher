using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

/// <summary>
/// Finds Xbox / Game Pass PC games, metadata-first:
///
///   1. PRIMARY: Get-AppxPackage (XboxPackageDiscovery) - the documented, public source for what's
///      actually installed and WHERE, including a custom install drive/folder the Xbox app was
///      configured to use, not just its default "XboxGames" convention. A candidate is only treated as
///      a game if it has real evidence of being one: it sits in a "XboxGames" folder on some ready
///      drive (the established convention), OR it has its own MicrosoftGame.config (the GDK's own,
///      documented, game-specific manifest) - never just "it's an installed package", which would add
///      every ordinary Store app (Calculator, Mail, ...) as a "game".
///
///   2. Its own launch target is read from that evidence: MicrosoftGame.config or AppxManifest.xml
///      (XboxGameManifest) names the real executable and application id directly - no guessing which
///      exe in a folder is the game. Several equally plausible targets are reported as ambiguous and
///      resolved ONLY by independent, corroborating identity evidence (a Start Menu entry Windows itself
///      already confirms belongs to this exact package) - never by falling back to "pick the biggest
///      file" or "take the first one listed".
///
///   3. FALLBACK, only for whatever step 1 didn't find at all (Get-AppxPackage unavailable/incomplete,
///      or a title packaged in a form it doesn't surface): "XboxGames" folders on every ready drive,
///      same as before - kept, not replaced, per the ask to keep XboxGames as A fallback, not THE
///      location. A package's InstallLocation and its containing XboxGames folder entry can both name
///      the SAME real install (e.g. InstallLocation is "...\Game\Content", the umbrella folder is
///      "...\Game"). Containment handles that case; package/activation identity reconciles the separate
///      WindowsApps and XboxGames paths that have no textual containment relationship at all.
///
///   4. Whenever metadata doesn't name a confident executable, the search for one (XboxExeSearch) is
///      bounded by directory-visit/time budgets and cancellation (checked during enumeration too, not
///      just once per directory), not by folder depth - real installs nest at unpredictable depths. A
///      search that runs out of budget is never confused with "no executable exists" - see
///      XboxExeSearch's own remarks. A verified package identity (AUMID) is enough to add the game even
///      when no executable could be found at all - shell:appsFolder is a valid activation target on its
///      own.
///
///   5. A game's Id is anchored to its PackageFamilyName whenever one is known (BuildCandidateEntry's own
///      remarks) - Windows' own permanent, unique-per-package identity, never shared by two different
///      real packages and never affected by which subfolder a package's InstallLocation happens to point
///      at, or by what else does or doesn't also live under the same umbrella folder on any given scan.
///      GameEntry.LegacyId carries the pre-registration, folder-hashed Id the SAME install would have
///      gotten from the old, package-registration-free scanner, so GameScannerService can still find an
///      already-installed game's saved favorites/hidden-state/artwork under it the first time this build
///      recomputes a different Id for the very same game.
///
/// Known open limitation of step 1's "is this a game" gate: a title packaged in a way that is NEITHER
/// under a "XboxGames" folder NOR ships a readable MicrosoftGame.config (a pure UWP/WindowsApps-only
/// title whose manifest this app either can't reach at all due to ACLs, or that predates the GDK's
/// MicrosoftGame.config convention entirely) cannot currently be told apart from an arbitrary non-game
/// Store app using the signals available here, and is not added. Loosening the gate to include it would
/// reopen the exact "adds every registered Windows app" problem this gate exists to prevent - there is no
/// principled middle ground without a genuinely reliable "is this specific locked-down package a game"
/// signal, which this app does not have.
/// </summary>
public static class XboxScanner
{
    private const string XboxFolderName = "XboxGames";

    /// <summary>Game Pass installs drop DLC/tracker stub folders next to real games; they carry no
    /// playable executable, but they're skipped by name too so they never surface as blank cards.</summary>
    private static readonly string[] StubNamePatterns =
        { " dlc", "launch tracker", "game stub", "game pass pack", "pre-order", "preorder" };

    public static List<GameEntry> Scan(CancellationToken ct = default) => Scan(GetReadyDrives().ToList(), ct);

    internal static List<GameEntry> Scan(IReadOnlyList<string> readyDrives, CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var handledXboxGamesDirs = new List<string>();
        // A top-level folder ends up here when a package under it was read AND its own metadata
        // explicitly rejected a launch target (an Ambiguous manifest no identity evidence resolved, or
        // one that named only incompatible/non-PC executables) - never for a package that simply found
        // nothing at all (a genuinely empty/stale registration), which must still leave the fallback
        // walk free to search normally. See ONE consolidated remark on why this exists: without it, the
        // fallback walk reads ITS OWN (different, usually empty) manifest at the top-level folder, sees
        // nothing, and falls back to a full-tree search (or a free-text AUMID guess) that can pick up and
        // launch, or activate, the exact target the deeper manifest already, explicitly, rejected.
        var rejectedManifestTopLevelDirs = new List<string>();
        ct.ThrowIfCancellationRequested();
        var packagedApps = StartAppsResolver.GetPackagedApps(ct);
        var packages = XboxPackageDiscovery.GetInstalledPackages(ct);
        var installedPackageNames = packages.Where(p => Directory.Exists(p.InstallLocation)).Select(p => p.Name).ToList();

        // ---- 1: package registration (authoritative install locations + identity) --------------------
        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(package.InstallLocation))
                continue;

            // The TOP-LEVEL "XboxGames\<Game>" folder, when this package's InstallLocation sits under
            // one - which can be a nested subfolder of it ("...\Game\Content"), not that folder itself.
            // Used for the folder name (stub-detection, the free-text AUMID fallback, the last-resort
            // display name - InstallLocation's OWN leaf, "Content", is meaningless for any of those) and
            // for LegacyId (see BuildCandidateEntry's remarks) - NOT for Id itself, which is anchored to
            // PackageFamilyName whenever one is known, exactly because a folder can be shared by more
            // than one real package and a folder-based Id cannot.
            var canonicalTopLevelFolder = TryGetTopLevelXboxGamesFolder(package.InstallLocation, readyDrives);
            var underXboxGames = canonicalTopLevelFolder is not null;

            if (!underXboxGames && !HasMicrosoftGameConfig(package.InstallLocation))
                continue; // no evidence this package is a game at all - never add "every registered app"

            var folderName = Path.GetFileName((canonicalTopLevelFolder ?? package.InstallLocation).TrimEnd(Path.DirectorySeparatorChar));
            if (IsStub(folderName) || IsStub(package.Name))
            {
                Logger.Info($"  Xbox: skipping DLC/stub package '{folderName}'.");
                continue;
            }

            var entry = BuildCandidateEntry(package.InstallLocation, folderName, package.PackageFamilyName, packagedApps, ct,
                canonicalIdRoot: canonicalTopLevelFolder, metadataRejectedSearch: out var metadataRejectedSearch,
                siblingPackageNames: SamePublisher(installedPackageNames, package.Name));

            if (entry is not null)
            {
                games.Add(entry);
                // Marked handled ONLY on an actual, usable entry - not merely "this package sits under
                // XboxGames". A package registration that turns out empty/unlaunchable (wrong subfolder,
                // stale entry, whatever) must NOT suppress the fallback walk's chance to find the real
                // game content sitting elsewhere under the SAME top-level folder; that would erase a
                // valid installation the old, package-registration-free scanner would have found fine.
                if (underXboxGames)
                    handledXboxGamesDirs.Add(NormalizeDir(canonicalTopLevelFolder!));
            }
            else if (underXboxGames && metadataRejectedSearch)
            {
                rejectedManifestTopLevelDirs.Add(NormalizeDir(canonicalTopLevelFolder!));
            }
        }

        var registeredEntries = games.ToArray();
        // ---- 2: XboxGames folders on every ready drive, for anything step 1 didn't already cover ------
        var anyFolderFound = false;
        foreach (var drive in readyDrives)
        {
            var xboxDir = Path.Combine(drive, XboxFolderName);
            if (!Directory.Exists(xboxDir))
                continue;

            anyFolderFound = true;

            try
            {
                foreach (var gameDir in Directory.EnumerateDirectories(xboxDir))
                {
                    ct.ThrowIfCancellationRequested();

                    if (IsAlreadyHandled(gameDir, handledXboxGamesDirs))
                        continue; // package registration (step 1) already covers this exact folder, or a content root nested inside it

                    var name = Path.GetFileName(gameDir);
                    if (IsStub(name))
                    {
                        Logger.Info($"  Xbox: skipping DLC/stub folder '{name}'.");
                        continue;
                    }

                    // A package under this SAME top-level folder already read a manifest deeper in the
                    // tree and explicitly rejected it (incompatible target, or unresolved ambiguity) -
                    // this fallback attempt must not undo that rejection just because ITS OWN (shallower)
                    // manifest read here finds nothing: not via a search (which could still pick up and
                    // launch the exact exe already rejected), and not via an AUMID either (a free-text
                    // guess against the top-level folder's own generic name is not tied to the specific
                    // package that was actually rejected, and would activate the wrong identity just as
                    // confidently as launching the wrong exe would).
                    var normalizedGameDir = NormalizeDir(gameDir);
                    var forceRejected = rejectedManifestTopLevelDirs.Any(d => string.Equals(d, normalizedGameDir, StringComparison.OrdinalIgnoreCase));

                    var metadataRoot = XboxGameManifest.FindMetadataRoot(gameDir);
                    var identityName = XboxGameManifest.ReadIdentityName(metadataRoot);
                    var families = packages.Where(p => identityName is not null
                            && string.Equals(p.Name, identityName, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.PackageFamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var family = families.Length == 1 ? families[0] : null;
                    var entry = BuildCandidateEntry(metadataRoot, name, family, packagedApps, ct,
                        canonicalIdRoot: gameDir, forceRejected: forceRejected, metadataRejectedSearch: out _);
                    if (entry is not null)
                    {
                        // WindowsApps registration and XboxGames content are often unrelated paths for
                        // the same activation target. Match identity, never titles or folder similarity.
                        var matches = registeredEntries.Where(g => g.Id == entry.Id
                            || (entry.LaunchUri is not null && g.LaunchUri is not null
                                && string.Equals(g.LaunchUri, entry.LaunchUri, StringComparison.OrdinalIgnoreCase)))
                            .ToArray();
                        if (matches.Length == 1)
                        {
                            var registered = matches[0];
                            games[games.FindIndex(g => g.Id == registered.Id)] = new GameEntry
                            {
                                Id = registered.Id,
                                LegacyId = entry.LegacyId ?? (entry.Id != registered.Id ? entry.Id : registered.LegacyId),
                                Name = registered.Name,
                                CatalogName = registered.CatalogName,
                                Source = GameSource.Xbox,
                                InstallDir = entry.InstallDir,
                                ExecutablePath = entry.ExecutablePath,
                                LaunchUri = registered.LaunchUri ?? entry.LaunchUri,
                            };
                            Logger.Info($"  Xbox: reconciled package registration and content folder for '{registered.Name}' ({gameDir}).");
                        }
                        else games.Add(entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed reading Xbox games from '{xboxDir}'.", ex);
            }
        }

        if (!anyFolderFound && games.Count == 0)
            Logger.Info("Xbox: no XboxGames folder found on any drive, and no package registration matched either.");

        return games;
    }

    /// <summary>A gameDir from the XboxGames-folder fallback walk is already covered by package
    /// registration when a handled install location IS that folder, or is NESTED inside it (a package's
    /// InstallLocation can be the umbrella folder's own "Content" subfolder rather than the folder
    /// itself - both name the same real install). Deliberately NOT the reverse (a handled dir being an
    /// ANCESTOR of gameDir): gameDir here is always a direct child of "XboxGames", so a handled dir can
    /// only ever be it or something inside it, never something that contains it - which also means this
    /// can't accidentally suppress an unrelated SIBLING game folder.</summary>
    private static bool IsAlreadyHandled(string gameDir, IReadOnlyList<string> handledDirs)
    {
        var normalizedGameDir = NormalizeDir(gameDir);
        return handledDirs.Any(h => string.Equals(h, normalizedGameDir, StringComparison.OrdinalIgnoreCase)
            || h.StartsWith(normalizedGameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds one GameEntry from an install root that's already been confirmed to be a real
    /// game (by whichever caller's own evidence) - reads its launch metadata, resolves an executable
    /// (from metadata first, only falling back to XboxExeSearch when metadata named NOTHING at all -
    /// neither an Ambiguous manifest that independent identity evidence couldn't resolve, NOR one that
    /// explicitly named only incompatible (non-PC) executables, is ever handed to the search as if it
    /// were the same as "no information"), resolves an AUMID (from metadata's own application id first,
    /// then package-identity-scoped Start Menu matching - REQUIRING a unique match, never a best-guess
    /// tie-break, since a name-similarity guess is exactly the kind of unsupported pick this whole design
    /// exists to avoid - only falling back to free-text name matching when there's no package identity at
    /// all), and skips only when NEITHER a usable executable NOR a usable activation target came out of
    /// any of that. A search that hit its budget before confirming a result is never published as if it
    /// were one - see the ProvisionalExePath handling below. An unresolved ambiguity blocks BOTH the
    /// executable search and AUMID resolution together, as one "rejected" fact - see `rejected` below.
    ///
    /// Id is anchored to packageFamilyName whenever one is known - Windows' own permanent, unique-per-
    /// package identity, stable regardless of which subfolder a package's InstallLocation happens to
    /// point at, and regardless of what else does or doesn't ALSO happen to be registered under the same
    /// top-level folder on any given scan (a sibling companion app appearing or disappearing can never
    /// change this install's own Id, unlike a scheme that decided the Id from how many candidates shared
    /// a folder). canonicalIdRoot, when given (the top-level "XboxGames\Game" folder a package's
    /// InstallLocation may be nested under), is used only as the Id fallback for the no-package-identity
    /// case, and to compute LegacyId: the OLD, pre-registration Id this same install would have hashed to
    /// (installRoot/InstallDir/ExecutablePath are unaffected either way and always reflect the real,
    /// possibly-nested install location) - GameScannerService falls back to an override recorded under
    /// LegacyId when none exists yet under the new Id, so an already-installed game's favorites/hidden-
    /// state/artwork survive the transition to package-identity-based Ids.
    ///
    /// forceRejected lets a caller (the XboxGames-folder fallback loop) carry a REJECTION already
    /// established deeper in the same top-level folder's tree into a shallower call that would otherwise
    /// read no manifest at all here and fall back to a blind search or a free-text AUMID guess - see
    /// Scan()'s own remarks on rejectedManifestTopLevelDirs for the concrete scenario this exists for.
    ///
    /// metadataRejectedSearch reports back whether THIS call's own metadata explicitly rejected a launch
    /// target (as opposed to naming nothing at all) - regardless of whether that produced an entry -
    /// so the caller can record the SAME rejection against a fallback attempt at a shallower level.</summary>
    internal static GameEntry? BuildCandidateEntry(string installRoot, string folderOrPackageName,
        string? packageFamilyName, IReadOnlyList<(string Name, string Aumid)> packagedApps, CancellationToken ct,
        out bool metadataRejectedSearch, int searchMaxDirectoriesToVisit = 20_000, TimeSpan? searchTimeBudget = null,
        string? canonicalIdRoot = null, bool forceRejected = false, IReadOnlyList<string>? siblingPackageNames = null)
    {
        var manifest = XboxGameManifest.ReadLaunchTarget(installRoot);

        if (manifest.Outcome == XboxManifestOutcome.NonPlayableContent)
        {
            metadataRejectedSearch = true;
            Logger.Info($"  Xbox: '{folderOrPackageName}' declares DLC content, not a standalone game - skipping.");
            return null;
        }

        var resolvedTarget = manifest.Outcome == XboxManifestOutcome.Single ? manifest.Target : null;
        var unresolvedAmbiguity = false;

        if (manifest.Outcome == XboxManifestOutcome.Ambiguous)
        {
            resolvedTarget = TryDisambiguateViaConfirmedIdentity(manifest.Candidates!, packageFamilyName, packagedApps);
            unresolvedAmbiguity = resolvedTarget is null;
            Logger.Warn(resolvedTarget is not null
                ? $"Xbox: '{installRoot}' named {manifest.Candidates!.Count} plausible executables - resolved to "
                    + $"'{resolvedTarget.ExeRelativePath}' via its independently-confirmed package identity."
                : $"Xbox: '{installRoot}' names {manifest.Candidates!.Count} equally plausible executables and no "
                    + "independent evidence resolves which one is real - not guessing.");
        }
        else if (manifest.Outcome == XboxManifestOutcome.IncompatibleTarget)
        {
            Logger.Info($"Xbox: '{installRoot}' declares only non-PC executables - not launching any of them, "
                + "and not searching the folder for a substitute either.");
        }

        // Reported to the caller regardless of forceRejected (which only ever ADDS to this call's own
        // reasons, it never masks one this call found for itself).
        metadataRejectedSearch = manifest.Outcome == XboxManifestOutcome.IncompatibleTarget || unresolvedAmbiguity;

        // Two DIFFERENT gates, not one combined flag - deliberately, because IncompatibleTarget and
        // unresolved ambiguity are not equally strong reasons to distrust AUMID resolution:
        //   - skipSearch: IncompatibleTarget, unresolved ambiguity, OR a rejection forced in from a
        //     deeper package - a size-based file guess is never entitled to override any of these.
        //   - skipAumid: unresolved ambiguity or a forced rejection ONLY - NOT IncompatibleTarget alone.
        //     A console-only declaration says nothing about whether this package's own CONFIRMED identity
        //     (tier 1/2 - the manifest's own application id, or a uniquely-confirmed family match) is
        //     real; blocking that too would make a legitimately-installed, console-declared title
        //     unlaunchable via its own real activation target for no reason tied to the actual ambiguity
        //     this design exists to prevent. A FORCED rejection is different: it's specifically about a
        //     SHALLOWER fallback call, with no package identity of its own, guessing an AUMID via
        //     free-text name matching against an entirely different (and possibly wrong) Start Menu
        //     entry - not the original, confirmed package's own identity - so it's blocked regardless of
        //     which reason the deeper package was rejected for.
        var skipSearch = metadataRejectedSearch || forceRejected;
        var skipAumid = unresolvedAmbiguity || forceRejected;

        var exePath = resolvedTarget is not null ? TryResolveExecutableFromManifest(installRoot, resolvedTarget) : null;

        // Every other null-exePath case still searches: None (the manifest named nothing at all), a
        // Single target whose declared file turned out missing/unreadable (a stale manifest - the
        // ORIGINAL reason this fallback exists), and an Ambiguous target that WAS resolved by identity
        // but whose file then turned out missing too.
        if (exePath is null && !skipSearch)
        {
            var searchResult = XboxExeSearch.FindLargestExe(installRoot, ct, searchMaxDirectoriesToVisit, searchTimeBudget);
            if (searchResult.Outcome == XboxExeSearchOutcome.Found)
            {
                exePath = searchResult.ExePath;
            }
            else if (searchResult.Outcome == XboxExeSearchOutcome.Incomplete)
            {
                // ProvisionalExePath is deliberately NEVER assigned to exePath/ExecutablePath below - an
                // incomplete search cannot confirm this is the right (or only, or largest) candidate, and
                // publishing it as a resolved launch target would let the one piece of state that matters
                // (uncertainty) evaporate the moment this method returns, leaving only a log line no part
                // of the running app ever reads. The entry falls through exactly as if nothing were found
                // (AUMID-only if available, otherwise skipped) - not launched off an unconfirmed guess.
                Logger.Warn($"Xbox: the executable search for '{folderOrPackageName}' hit its scan budget before finishing - "
                    + (searchResult.ProvisionalExePath is null
                        ? "this does not mean the game has no executable, only that none was found in time."
                        : $"found '{searchResult.ProvisionalExePath}' but NOT using it - an incomplete search cannot "
                            + "confirm it is the right (or only) candidate. Falling back to identity-based launching if available."));
            }
        }

        string? aumid;
        string? resolvedDisplayName = null;
        if (skipAumid)
        {
            // Neither ResolveAumid's tier 2 (a family-wide match that doesn't verify correspondence to
            // either of the manifest's OWN candidates at all: a same-family "Tools" companion app,
            // matching neither declared candidate, would otherwise be accepted as if it were the game)
            // nor its tier 3 free-text guess (which could just as easily match an unrelated Start Menu
            // entry sharing the top-level folder's generic name) is allowed to bypass a rejection either -
            // both would sidestep the exact conflict this is meant to preserve, using an entirely
            // unrelated signal to do it.
            aumid = null;
        }
        else
        {
            var manifestApplicationId = resolvedTarget?.ApplicationId;
            aumid = ResolveAumid(packageFamilyName, manifestApplicationId, folderOrPackageName, packagedApps, out resolvedDisplayName);
        }

        if (exePath is null && aumid is null)
        {
            Logger.Info($"  Xbox: no launchable exe and no registered activation target for '{folderOrPackageName}' - skipping this scan.");
            return null;
        }

        var displayName = resolvedDisplayName ?? resolvedTarget?.DisplayName ?? folderOrPackageName;

        if (aumid is null)
        {
            Logger.Warn($"  Xbox: no Start Menu/package entry matched '{folderOrPackageName}' - launching the exe "
                + "directly (can error for packaged titles) and using the detected name as-is.");
        }
        else if (!string.Equals(displayName, folderOrPackageName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Info($"  Xbox: '{folderOrPackageName}' is actually '{displayName}'.");
        }

        var catalogName = siblingPackageNames is null ? null : ResolveHubCatalogName(displayName, siblingPackageNames);
        if (catalogName is not null)
        {
            Logger.Info($"  Xbox: '{displayName}' is a multi-title hub package; the publisher's installed sibling packages name the title as "
                + $"'{catalogName}' - searching cover art for that (the card keeps the name '{displayName}').");
        }
        else if (siblingPackageNames is not null && SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(displayName))
        {
            Logger.Warn($"  Xbox: '{displayName}' is a multi-title hub package and no installed sibling package names a known title "
                + "- cover art is not searched automatically for it (Identify Game can pick the title).");
        }

        return new GameEntry
        {
            Id = $"xbox-{StableId(packageFamilyName ?? canonicalIdRoot ?? installRoot)}",
            // The pre-registration Id this SAME install would have hashed to, only when it's actually
            // possible for one to have existed: a known package identity (so Id above is family-based,
            // not folder-based) that ALSO sits under a recognized XboxGames top-level folder (so the OLD
            // scanner could plausibly have found and hashed it before this package-registration path
            // existed at all). Null whenever Id is already folder-based itself - nothing to reconcile.
            LegacyId = packageFamilyName is not null && canonicalIdRoot is not null ? $"xbox-{StableId(canonicalIdRoot)}" : null,
            Name = displayName,
            CatalogName = catalogName,
            // A directory (not a file) when no CONFIRMED exe was found - IconService already treats a
            // directory ExecutablePath as "look for an icon inside it", the same handling every other
            // exe-less packaged entry already relies on; not a special case invented for this. Never the
            // unconfirmed ProvisionalExePath from an incomplete search - see its handling above.
            ExecutablePath = exePath ?? installRoot,
            InstallDir = installRoot,
            Source = GameSource.Xbox,
            // Launching the exe directly skips the activation context Windows sets up for packaged
            // apps - shell:appsFolder is how a real shortcut actually launches one.
            LaunchUri = aumid is null ? null : $"shell:appsFolder\\{aumid}",
        };
    }

    /// <summary>Title codes Activision's Game Pass packages carry in their sibling DLC/stub package names
    /// ("38985CA0.BO7DLC01GameStub01" -> BO7), mapped to the real catalog title. Hand-verified entries only - one
    /// seen in a real install's package list; extend it the same way (a code is added when a real install shows
    /// it), never from a pattern guess.</summary>
    private static readonly Dictionary<string, string> KnownHubTitleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BO7"] = "Call of Duty: Black Ops 7",
    };

    private static readonly Regex HubTitleCodePattern = new(
        @"^[^.]+\.(?<code>[A-Za-z]{2,4}\d{1,2})DLC", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Other installed packages from the same publisher as `packageName` (the text before its first dot).</summary>
    internal static IReadOnlyList<string> SamePublisher(IReadOnlyList<string> installedPackageNames, string packageName)
    {
        var dot = packageName.IndexOf('.');
        if (dot <= 0)
            return Array.Empty<string>();

        var prefix = packageName[..(dot + 1)];
        return installedPackageNames.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The real catalog title for a multi-title "hub" install, or null. Xbox registers Call of Duty as ONE
    /// package named just "Call of Duty" whichever yearly title is installed; covers are never guessed for that bare name
    /// (SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct), so such a game never got one. What does name the title is
    /// the publisher's own sibling DLC/stub packages ("...BO7DLC01GameStub01"). It counts only when the display name IS the
    /// bare hub name and those siblings name exactly ONE known title; two different known titles, or none, give no hint.
    /// Name is untouched - this only feeds automatic cover matching, like EaScanner's CatalogName.</summary>
    internal static string? ResolveHubCatalogName(string displayName, IEnumerable<string> siblingPackageNames)
    {
        if (!SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(displayName))
            return null;

        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in siblingPackageNames)
        {
            var match = HubTitleCodePattern.Match(name);
            if (match.Success && KnownHubTitleCodes.TryGetValue(match.Groups["code"].Value, out var title))
                titles.Add(title);
        }

        return titles.Count == 1 ? titles.First() : null;
    }

    /// <summary>Resolves a manifest's ambiguity using ONLY independent, corroborating evidence - never a
    /// guess. A candidate's own ApplicationId is trusted specifically when Get-StartApps ALSO has an
    /// entry confirmed to belong to this exact package (AUMID starts with "PackageFamilyName!") whose
    /// trailing app id matches it: that is Windows' own registration telling us which application id is
    /// real for this package, not this app inferring anything. Still returns null (stays ambiguous) when
    /// zero or MORE THAN ONE candidate is independently confirmed this way.</summary>
    private static XboxLaunchTarget? TryDisambiguateViaConfirmedIdentity(IReadOnlyList<XboxLaunchTarget> candidates,
        string? packageFamilyName, IReadOnlyList<(string Name, string Aumid)> packagedApps)
    {
        if (packageFamilyName is null)
            return null; // nothing independent to corroborate against at all

        var prefix = packageFamilyName + "!";
        var confirmedAppIds = packagedApps
            .Where(a => a.Aumid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Aumid[prefix.Length..])
            .ToList();

        var matches = candidates
            .Where(c => c.ApplicationId is not null
                && confirmedAppIds.Any(id => string.Equals(id, c.ApplicationId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Only trusts a manifest-declared executable that actually exists on disk AND resolves to
    /// somewhere inside installRoot - a stale or wrong manifest is exactly what the caller's own
    /// fallback search exists to recover from, and Microsoft's own schema specifies a game-root-RELATIVE
    /// path (learn.microsoft.com/.../microsoftgameconfig-element-executable), so an absolute path or a
    /// "..\" escape is never a legitimate declaration - both are refused outright, not combined and
    /// trusted. Returns null (not a fabricated guess) for any of that, INCLUDING when the file exists but
    /// isn't readable (an ACL-locked packaged path) - the caller decides what to do about that, not this
    /// method.</summary>
    private static string? TryResolveExecutableFromManifest(string installRoot, XboxLaunchTarget target)
    {
        var relative = target.ExeRelativePath;

        if (Path.IsPathRooted(relative) || relative.Split('\\', '/').Any(segment => segment == ".."))
        {
            Logger.Warn($"Xbox: '{installRoot}' declares an executable path outside its own install root ('{relative}') - refusing to use it.");
            return null;
        }

        string candidate;
        string normalizedRoot;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(installRoot, relative));
            normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null; // a malformed path in the manifest - not this app's to fix
        }

        // Defense in depth even after the textual check above - catches drive-relative oddities (e.g.
        // "C:foo", which Path.IsPathRooted alone does not consider rooted) the pre-check doesn't flag:
        // the fully resolved path must still actually land inside installRoot.
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"Xbox: '{installRoot}' declares an executable path that resolves outside its own install root ('{relative}') - refusing to use it.");
            return null;
        }

        if (File.Exists(candidate))
            return candidate;

        Logger.Info($"Xbox: '{installRoot}' declares '{relative}' but that file isn't "
            + "readable here (locked package content, or a stale manifest) - falling back to a search.");
        return null;
    }

    /// <summary>Three confidence tiers, in order - never a lower one when a higher one is available, and
    /// the first two NEVER fall back to a name-similarity guess when they don't produce a unique answer -
    /// that guess is exactly the kind of unsupported pick "preserve ambiguity, don't select the wrong
    /// game" rules out, for the launch target as much as for the executable:
    ///   1. The manifest itself named an application id: AUMID = PackageFamilyName!ApplicationId directly,
    ///      no matching/guessing needed at all - this is the whole point of reading the manifest.
    ///   2. Package identity confirmed but no application id from the manifest: narrow Get-StartApps to
    ///      entries that are ALREADY confirmed to belong to this exact package (AUMID starts with
    ///      "PackageFamilyName!"). Used ONLY when that narrows to EXACTLY one - having confirmed package
    ///      identity is supposed to be strong evidence; if there's genuine multiplicity even within it (a
    ///      companion-app scenario), ranking the candidates by which name looks closest is a guess with
    ///      extra steps, not resolution, and must return no AUMID rather than pick one.
    ///   3. No confirmed package identity at all (the XboxGames-folder-only fallback, when package
    ///      registration never surfaced this install): the old free-text folder-name-vs-Start-Menu-title
    ///      match, WITH its name-similarity tie-break - kept only because this tier has no package
    ///      identity to be confident about in the first place, so a best-effort guess is the most this
    ///      tier could ever have offered, exactly as before this whole redesign.</summary>
    private static string? ResolveAumid(string? packageFamilyName, string? manifestApplicationId,
        string fallbackSearchName, IReadOnlyList<(string Name, string Aumid)> packagedApps, out string? displayName)
    {
        displayName = null;

        if (packageFamilyName is not null && manifestApplicationId is not null)
        {
            var directAumid = $"{packageFamilyName}!{manifestApplicationId}";
            var matched = packagedApps.FirstOrDefault(a => string.Equals(a.Aumid, directAumid, StringComparison.OrdinalIgnoreCase));
            displayName = matched.Aumid is null ? null : matched.Name;
            return directAumid;
        }

        if (packageFamilyName is not null)
        {
            var familyMatches = packagedApps
                .Where(a => a.Aumid.StartsWith(packageFamilyName + "!", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (familyMatches.Count != 1)
                return null; // zero, or genuinely ambiguous even within the confirmed family - never guessed

            displayName = familyMatches[0].Name;
            return familyMatches[0].Aumid;
        }

        var guess = RankByNameSimilarity(fallbackSearchName, packagedApps);
        displayName = guess?.Name;
        return guess?.Aumid;
    }

    /// <summary>Matches an Xbox game folder/package name against Get-StartApps entries, returning the
    /// real display name alongside the AUMID. Ranks every candidate (exact match or contains-match) by
    /// name length and takes the longest, most specific one - not just whichever counts as "exact" first.
    /// This matters because modern Call of Duty installs both a generic "Call of Duty" hub entry (Call
    /// of Duty HQ) and the actual specific title ("Call of Duty: Black Ops 7") in the Start Menu at the
    /// same time; an umbrella folder literally named "Call of Duty" exact-matches the generic hub, which
    /// is real but the wrong one - the specific title is what should win, and only loses on length if it
    /// doesn't exist. Still best-effort, not a guarantee, if a franchise has two equally-specific entries
    /// installed at once - which is exactly why this is only ever the LAST-resort tier in ResolveAumid,
    /// not the primary identification mechanism it used to be.</summary>
    private static (string Name, string Aumid)? RankByNameSimilarity(
        string name, IReadOnlyList<(string Name, string Aumid)> packagedApps)
    {
        var normalizedTarget = Normalize(name);
        if (normalizedTarget.Length == 0)
            return null;

        var candidates = packagedApps
            .Where(a =>
            {
                var normalizedName = Normalize(a.Name);
                return normalizedName.Length > 0
                       && (normalizedName == normalizedTarget
                           || normalizedName.Contains(normalizedTarget)
                           || normalizedTarget.Contains(normalizedName));
            })
            .OrderByDescending(a => a.Name.Length)
            .ToList();

        return candidates.Count == 0 ? null : candidates[0];
    }

    private static string Normalize(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsStub(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        var compact = Normalize(folderName);
        return StubNamePatterns.Any(lower.Contains)
            || new[] { "launchtracker", "gamestub", "gamepasspack", "preorder" }.Any(compact.Contains)
            || Regex.IsMatch(folderName, @"(?:^|[\s._-]|(?<=\d))dlc(?=\d|[\s._-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasMicrosoftGameConfig(string installLocation)
    {
        try
        {
            return File.Exists(Path.Combine(installLocation, "MicrosoftGame.config"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The top-level "&lt;drive&gt;\XboxGames\&lt;Game&gt;" folder that `installLocation` sits
    /// under, on whichever ready drive that is - null when it isn't under any of them at all.
    /// installLocation itself may BE that folder, or nested arbitrarily deep inside it
    /// ("...\Game\Content", "...\Game\Content\bin", ...); either way, this returns the same, single
    /// canonical top-level path, which is what the game's Id and display name are anchored to (see the
    /// call site's own remarks for why that specific anchor matters).</summary>
    private static string? TryGetTopLevelXboxGamesFolder(string installLocation, IReadOnlyList<string> readyDrives)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(installLocation);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        foreach (var drive in readyDrives)
        {
            var xboxGamesRoot = Path.Combine(drive, XboxFolderName) + Path.DirectorySeparatorChar;
            if (!normalized.StartsWith(xboxGamesRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = normalized[xboxGamesRoot.Length..];
            var firstSegment = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is null)
                return null; // installLocation IS the XboxGames folder itself - not a game folder under it

            return xboxGamesRoot + firstSegment;
        }

        return null;
    }

    private static string NormalizeDir(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static IEnumerable<string> GetReadyDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var ready = false;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
            }

            if (ready)
                yield return drive.RootDirectory.FullName;
        }
    }

    private static string StableId(string path)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
}
