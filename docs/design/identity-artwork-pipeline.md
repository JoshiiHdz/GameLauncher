# Game identity and artwork pipeline — design (revision 7, design only)

Status: **for audit. No implementation, staging, commit, push or release accompanies this document.**
Scope: item 1 of the agreed order (identity/artwork data model, transactions, migration, cache binding,
acceptance scenarios). Provider parity, the picker UI, credential onboarding and gaming-PC validation are
later, separately gated items (section 13).

Claims about the current code cite `file:line` as read on this checkout. Claims about third-party APIs I have
not verified are marked **[UNVERIFIED]** and collected in section 14.

## Changes from revision 6 (audit round 3) — narrow amendment, no redesign

| # | Finding | What changed | Where |
|---|---------|--------------|-------|
| 1 | User decisions had authorization bypasses: a rejected candidate's artwork-source cover; a same-title different-id competing entry after a confirmation; an unverified legacy cover after a confirmation | One shared clause **`UserDecisionConsistent`** is now required in *every* automatic-artwork branch of the predicate (not re-derived per branch): a rejected `(ns,id)` is never authorized; **any other id in the confirmed identity's namespace is excluded regardless of title**; unverified legacy continuity **ends when the user confirms**. Reject and Confirm now also remove/adopt `ArtworkSources` and `LegacyEvidence`. Regressions cover fresh publication, cache reuse, carried-forward pixels and restart for each case. | 3.1, 3.3, 3.3.1, 3.4, 6.3, 7, S44–S46 |
| 2 | Quarantine was tested before staleness, so a stale quarantined unit could take the launcher-art exception | Order fixed: **ownership, cancellation, revision and fingerprint first**; only a still-current unit may become `NotApplicable(Quarantined)` and use the exception. The exception also requires that the raw subtree show no user-confirmed identity. | 6.5, 6.6, S35, S47 |
| 3 | Legacy discovery used `CoverArtDecoder` alone (a downsampled decode with no dimension checks) | Legacy continuity now requires **bounded reads and `ArtworkImageValidator.ValidateBytes`** (20 MB / 8000 px / 64 MP), off the UI thread; the validated frozen bitmap is what is displayed. | 9.1, S48 |
| D7 | Launcher artwork after a confirmation | Adopted the auditor's rule: with no confirmed identity a verified launcher id may authorize launcher art; **once the user confirms a catalog identity, launcher art must be consistent with it**, proven by provider-supplied equivalence evidence recorded on the confirmed identity — otherwise it is removed (icon or the confirmed identity's own art). No pin required. | 3.1, 3.3.1, 14, S49 |
| R8 | Search-name mismatch as a verdict | Reframed: a mismatch is **stale lookup evidence**. Continuity is withheld and fresh resolution scheduled; **no rejection is created** and the association is not declared wrong. | 9.1, 8.1, S41, S50 |

## Changes from revision 5 (audit round 2) — narrow revision

| # | Finding | What changed | Where |
|---|---------|--------------|-------|
| 1 | `identityCurrent` was computed but never checked; ID equality does not imply identity validation; `ClearAutomatic` had fewer checks; Reset on a quarantined record let artwork bypass the identity safeguard | Step 4 now begins with an **explicit gate G1: the identity half must have validated** (`Committed` or `ValidatedNoChange`) before *any* artwork publish, `Set` or `ClearAutomatic`. The one exception (launcher-derived `Set` on a quarantined record) is defined separately and narrowly. Catalog artwork can no longer bypass a quarantined identity. | 6.5, 6.6, I9 |
| 2 | An identity dialog could apply over a newer user decision when the active set did not change | New persisted **`DecisionRevision`**: advances on every meaningful Confirm/Reject/Clear change even if the active identity is unchanged; never on automatic writes. Identity dialogs check it **and** `IdentityRevision`. | 6.1, 6.3, 6.7 |
| 3 | An artwork-ineligible entry still passed publication; "lower-priority" ambiguous; fallback matches stored as identity | `SelectActive` now separates **identity entries** from **artwork-source associations** (separate stored list), carries `ArtworkEligible`, and orders authority as **user-confirmed first**. One **`ArtworkAuthorized` predicate** governs publication, cache reuse, carried-forward pixels, restart display and invalidation. | 3.1, 3.3, 3.3.1, 7.1, I11 |
| 4 | Legacy continuity named the wrong cache file | Provider-specific **legacy cache discovery** using the real formats (SteamGridDB `{gameId}-v12.png` + `.png.meta.json`; Steam CDN `steam-{appid}.jpg`), exact supported versions only, full sidecar/hash/id cross-checks, read-only. | 1.13, 9.1 |

## Changes from revision 4 (audit round 1)

| # | Finding | What changed | Where |
|---|---------|--------------|-------|
| 1 | Automatic identity changes had no concurrency protection | Revision now advances whenever the **active identity set** changes, from any origin. Added a session-only per-game write generation for metadata-only races. Automatic commit is a compare-and-swap. Artwork publishes only if its `DerivedFrom` matches the identity active **at publication**. Half-commit rules made explicit both ways. | 6.1–6.4, I9 |
| 2 | Migration trusted old wrong covers | Old automatic artwork is now **unverified legacy evidence**, stored apart from identity, never effective, never sticky, never used for id-based fetch. It is revalidated against current detected inputs; a failed revalidation invalidates it with no replacement required. | 3.4, 8.1, I10 |
| 3 | Publisher agreement is not enough to resolve abbreviations | Publisher/developer agreement is **ranking only**. Non-exact acceptance needs a product-specific connection (namespaced external-id mapping, or an exhaustive exact alternative-title query). Otherwise unresolved. | 4.5 |
| 4 | Derived state and action semantics incomplete | One **effective-identity selection function** filters rejected, stale, untrusted and user-decision-dependent evidence before choosing. Reject/Clear/Change define which entries survive. Reset's unresolved-state behaviour defined without the artwork transaction touching identity. | 3.3, 6.6, 7 |
| 5 | Cross-provider merge conflicts missing | Conflicts are detected across namespaces; both user decisions are always preserved; equivalence only on explicit evidence. Revision exhaustion and confirmed-vs-rejected added. | 8.2 |
| 6 | Unknown enum names would not load | The claim was wrong (.NET 8 `JsonStringEnumConverter` throws on an unknown name). Replaced with open string-valued types plus per-record tolerant parsing and quarantine; user decisions round-trip verbatim. | 3.1, 5.2 |
| D3, D5, D6 | Auditor recommendations | D3 adopted (stale-selection revalidation for Change Cover, no identity dependency). D5 reconciled with S5. D6 stays with the owner. | 6.5, 7, 11, 14 |

---

## 0. The correction this design is built around

Artwork selection and game identity are **two separate decisions**, each with its own state, its own revision
counter and its own commit rules. There is no single precedence ladder.

- A custom cover answers "what image do we show?". It says nothing about which game is installed.
- A confirmed identity answers "which game is this?". It must never replace, re-select or re-derive an
  explicit cover.

Eleven invariants. Each is testable and section 11 turns them into a test matrix.

| # | Invariant |
|---|-----------|
| I1 | No identity operation (confirm, reject, clear, automatic resolve, merge) ever changes a user-selected artwork record or its asset. |
| I2 | No artwork operation (change cover, reset cover, automatic fetch) ever changes identity data. A follow-up automatic resolution triggered by Reset is a **separate, ordinary automatic transaction** (6.6), never part of the artwork commit. |
| I3 | What is displayed is a pure function of two inputs: if a cover is pinned, that asset; otherwise automatic artwork for which the **`ArtworkAuthorized` predicate (3.3.1)** holds (including the explicitly marked legacy-continuity case, 3.4). There is no third path. |
| I4 | User decisions are **durable before visible**: written and saved first, displayed only after the save succeeds, fully rolled back if it fails. Automatic results may be shown first and saved best-effort, because they are re-derivable. |
| I5 | An automatic result never overwrites a user decision, and a provider outage (`Unavailable`) never clears or downgrades existing identity or artwork. |
| I6 | Provider identifiers never cross namespaces. An IGDB id is never sent to SteamGridDB, a Steam appid is never sent to IGDB as a game id. Titles (text) may generate *candidates* in another provider; they never prove equivalence. |
| I7 | A user's display name (`CustomName`) can never reach a provider. Enforced by types, not convention (section 2). |
| I8 | Changing or clearing identity never deletes a user-selected asset. Automatic artwork is invalidated by cache-key change, not deletion. |
| I9 | Automatic artwork is published only if (a) the unit's **identity half validated** — an explicit gate, never inferred from id equality — and (b) the artwork is authorized against the identity **actually active at publication** (I11). An identity half that failed validation never lets its artwork be set or cleared, even when the artwork's `DerivedFrom` id is still active. |
| I10 | Legacy (pre-migration) matches are unverified evidence. They never act as identity, never enable id-based fetch and are never sticky until revalidated against current detected inputs. |
| I11 | **One** artwork-authorization predicate (3.3.1) governs every path by which automatic pixels can reach the screen or be reused: publication, cache reuse, carried-forward pixels, restart display, and the removal pass on identity change. An entry that is ineligible for artwork, or an artwork-source association whose basis no longer stands, can pass none of them. |

---

## 1. What the current code does that shapes this design

Read from source, not assumed.

1. **`CustomName` already leaks into provider searches.** `GameScannerService.cs:179-180` overwrites
   `game.Name` with `CustomName` *before* `SafeApplyCoverArt` runs (`:186`). Every provider then searches
   `game.CatalogName ?? game.Name` (`CoverArtService.cs:74`, `IgdbCoverArtProvider.cs:346,458`,
   `SteamGridDbCoverArtProvider.cs:191,306`). Reset's scratch copy also copies `game.Name`
   (`LibraryViewModel.cs:1529`). Two further paths reach the same place: `ApplyScanResultAsync` re-applies
   `CustomName` at publish (`:811`), and `MigrateMergedOverrides` adopts a loser's `CustomName` (`:564`), so a
   rename can appear *mid-scan*. No UI writes `CustomName` today (no rename feature exists in `src/`), so this is
   latent until one ships; I have not reproduced a wrong cover from it.
2. **Identity and artwork are conflated in one record.** `GameOverride.Artwork` holds an automatic result or a
   user selection (`IsUserSelected`); its `ProviderGameId` is evidence about the *image's* catalog entry, not
   about the installed game. There is no identity record.
3. **One revision counter guards artwork only** (`GameOverride.ArtworkRevision`), with an optimistic-concurrency
   pattern: capture at start, compare at a synchronous UI-thread commit (`CommitArtworkChange :1703`,
   `CommitAutomaticArtworkIfCurrent :1774`, `ReconcileArtwork :902`), and an exhaustion rule that rejects before
   mutating (`TryGetNextRevision :625`). **Automatic commits deliberately do not bump it.** That rule is what
   audit finding 1 shows is insufficient once automatic commits can change *identity* (6.1).
4. **Automatic results overwrite unconditionally when current** (`ReconcileArtwork` `over.Artwork =
   automaticResult`, `:946-947`, "replace or clear"). An `Unavailable` outcome and a genuine `NoMatch` are
   indistinguishable at this layer; the shared `CoverLookupStatus` (formerly `IgdbLookupStatus`; IGDB and, since B1, SteamGridDB report it) distinguishes them but the distinction stops at
   `CoverArtService.Apply`.
5. **Merges are field-level and preserve conflicting covers** (`MigrateMergedOverrides :539`,
   `ArtworkConflicts`), but only for artwork.
6. **Persistence is one atomic whole-file save and one whole-document load.** `SettingsService.Save` is temp +
   `File.Replace`; `Load`'s `TryLoad` deserializes the whole `AppSettings`, and any `JsonException` falls back
   to the backup, then to defaults (`SettingsService.cs:55,99-113`). Consequence for section 5.2: one
   unparseable identity value could discard *unrelated* user data unless parsing is made record-tolerant.
7. **Enums persist as integers** (no string converter). `ArtworkProvider` stays append-only. An unknown
   *numeric* value deserializes without throwing; an unknown *string* name into a string-enum converter does
   throw.
8. **Hand-maintained special cases exist and are not the general solution**:
   `EaScanner.KnownAbbreviatedCatalogNames` (`"Apex"` → `"Apex Legends"`, `EaScanner.cs:34`) and
   `IsAmbiguousUmbrellaProduct` (currently `callofduty`).
9. **Launcher metadata captured today**: Steam appid + manifest name (`SteamScanner.cs:117-142`); GOG registry
   game id (`gog-{gameId}`, `GogScanner.cs:48`); Epic `AppName` (an artifact id) + `DisplayName`
   (`EpicScanner.cs:75-100`); Ubisoft install id; Xbox AUMID; EA registry key/folder name; Battle.net,
   Rockstar and Amazon via uninstall-registry `DisplayName`/`Publisher`; manual folders by folder name. **Not
   captured anywhere today**: executable version info, Epic catalog namespace/item id, uninstall
   `InstallLocation` as evidence.
10. **No asset garbage collector** exists (`ArtworkAssetStore.cs:12`); user assets are never reclaimed.
11. **Exact-title uniqueness is computed over a bounded, ranked result list** (`search "..."; limit 20;`,
    `IgdbCoverArtProvider.cs:459`). The provider's own remarks already state that uniqueness within one
    response says nothing about a same-named product elsewhere (`:492`). Section 4.3 uses this.
12. **Shipped data:** v1.18.x persisted SteamGridDB automatic artwork with `ProviderGameId` and no uniqueness
    check on the match. IGDB artwork exists only on the developer's local, uncommitted build. So the "old
    automatic records" of section 8.1 are SteamGridDB (and Steam CDN) matches.
13. **Actual automatic-cache formats** (needed by 9.1; revision 5 named the wrong one):
    - **SteamGridDB:** `CoverArtCache\{game.Id}-v12.png` as **shipped** (`CacheVersion = 12` at the audited checkout,
      `SteamGridDbCoverArtProvider.cs:81`; **B1 bumps it to 13**, see 9.1's B1 note;
      path built at `:194`) with sidecar `<file>.meta.json` (`:195`) whose shape is
      `{Id, Title, SearchedName, ImageSha256}` (`:606`). A read requires `Id > 0`, non-empty `Title` and
      `ImageSha256`, `SearchedName` equal to the *current* search name, and the hash to match the bytes
      (`TryReadMatchedGame :622-645`); any failure **deletes** both files and re-fetches (`:220-225`). The version
      is bumped precisely when earlier matching logic is known to have mis-cached (comments `:16-80`).
    - **Steam CDN:** `CoverArtCache\steam-{appId}.jpg`, unversioned, **no sidecar** (`SteamCoverArtProvider.cs:37`).
      Its identity is the launcher's own appid, not a catalog match.
    - **IGDB:** `CoverArtCache\Igdb\{game.Id}-v1.png` + sidecar (`IgdbCoverArtProvider.cs:70,364`). Exists only on
      the developer's local build; never shipped.

---

## 2. Search inputs: immutable detected data, separate from display

### 2.1 Types

```csharp
// Built once per scan by the scanner. Immutable. Has NO Name/CustomName member (I7).
public sealed record IdentityQuery(
    string DetectedTitle,                            // scanner's raw title: Steam manifest name, Epic DisplayName, folder name...
    GameSource Source,
    IReadOnlyList<LauncherIdentifier> LauncherIds,   // e.g. (SteamApp,"1091500"), (GogProduct,"1207658924")
    string? InstallFolderName,
    ExecutableFacts? Exe,                            // ProductName/CompanyName/OriginalFilename — 2.3, not collected today
    string? CuratedHint);                            // transitional: today's CatalogName; provenance-tagged, see 4.6
```

- `GameEntry.DetectedTitle { get; init; }` is set by the scanner and never reassigned. `GameEntry.Name` stays the
  mutable display name (custom name overlaid), exactly as now.
- Providers accept `IdentityQuery`, not `GameEntry`. The type has no display-name member, so a provider *cannot*
  read `CustomName`. A reflection test pins the member list (I7); behavioural tests are in section 11.
- `Fingerprint(query)` = SHA-256 over normalised fields (collapsed title, sorted launcher ids, source, exe facts,
  hint). It is the "same inputs?" token for stale detection and re-resolution. It contains no display name, so a
  rename never changes it.

### 2.2 No per-game mappings as the general solution

`CuratedHint` (today's `CatalogName`) is kept only as a provenance-tagged evidence source so nothing that works
today regresses. It is transitional: 4.5 and 4.6 describe the general mechanism and its limits, and section 13
gives a measurable removal criterion. No entry is added to any hand list by this design.

### 2.3 Evidence collection is measured before it is relied on

Which extra evidence a launcher exposes on a real machine is an empirical question I cannot answer from here.
The first implementation batch adds a **read-only diagnostic** (no behaviour change): per detected game, log which
evidence fields were obtainable (launcher ids, exe ProductName/CompanyName, uninstall Publisher/InstallLocation,
Epic catalog ids). Later batches rely only on fields the owner's real library shows are present.

---

## 3. Identity model

### 3.1 Stored facts

Facts are persisted; state is derived. Per game, on `GameOverride`:

```csharp
public sealed class GameIdentityRecord
{
    public ProviderIdentity? Confirmed { get; set; }               // user decision, canonical (at most one)
    public List<ProviderIdentity> Rejected { get; set; } = new();  // "not this game", candidate-specific
    public List<ResolvedIdentity> Resolved { get; set; } = new();  // automatic IDENTITY entries, per namespace, compact, derivable
    public List<ArtworkSourceAssociation> ArtworkSources { get; set; } = new();  // 7.1 — art-source matches, NEVER identity
    public List<LegacyAssociation> LegacyEvidence { get; set; } = new();   // 3.4 — NEVER identity
    public ResolutionAttempt? LastAttempt { get; set; }
    public JsonElement? Quarantined { get; set; }                  // 5.2 — raw, opaque, written back verbatim
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }   // forward compatibility
}
public sealed record ProviderIdentity(IdentifierNamespace Namespace, string Id, string Title, DateTime At,
    IReadOnlyList<LauncherIdentifier>? VerifiedLauncherIds = null);
    // VerifiedLauncherIds: launcher ids the PROVIDER's own cross-reference data proved map to this exact catalog
    // entry, captured when the user confirms it (D7, 3.3.1). Null/empty means "not proven" — never "assumed".
    // Its availability depends on U1 (unverified). Used only for launcher-art consistency; never as identity.
public sealed record ResolvedIdentity(IdentifierNamespace Namespace, string Id, string Title,
    IdentityTier Tier, string EvidenceFingerprint, int ResolverVersion, DateTime ResolvedAt);
// A match a fallback provider found by searching the CONFIRMED canonical title (7.1). It is not an identity: it
// never counts toward the active identity key or state, and it exists only while its basis still stands.
public sealed record ArtworkSourceAssociation(IdentifierNamespace Namespace, string Id, string Title,
    int ResolverVersion, DateTime ResolvedAt, ProviderIdentity Basis);   // Basis = the Confirmed identity whose title generated it; required
public sealed record LegacyAssociation(IdentifierNamespace Namespace, string Id, string Title,
    ArtworkProvider SourceProvider, DateTime MigratedAt,
    LegacyStatus Status);   // open string type: Pending | StaleLookup (9.1). NEITHER is a verdict on the candidate.
public sealed record ResolutionAttempt(LookupOutcome Outcome, DateTime At, string? Fingerprint, int ResolverVersion, string? Trigger);
```

`IdentifierNamespace`, `IdentityTier` and `LookupOutcome` are **open, string-valued types** (a `readonly record
struct` around a string with well-known static members), not enums (5.2). An unrecognised value is a valid,
round-trippable value that the resolver treats as opaque: it can be preserved, listed in conflicts and
rejections, but never resolved or fetched through. `IdentifierNamespace` is separate from `ArtworkProvider`: one
names where a *catalog entry* lives, the other where *pixels* came from.

`IdentityTier` (automatic only): `TitleExact`, `TitleExactCorroborated`, `IdMapped`.

### 3.2 Launcher ids are evidence, not stored identity

Launcher ids (`SteamApp`, `GogProduct`, …) come from the scanner into `IdentityQuery` every scan. They are never
copied into `Resolved`. `Resolved` holds only **catalog** namespaces (`IgdbGame`, `SteamGridDbGame`, …).

### 3.3 The effective-identity selection function (single source of truth)

One pure, total, deterministic function decides what is active. Every consumer (state derivation, revision
bumps, artwork validity, Reset, the picker) calls it; nothing reads `Resolved` directly.

```
SelectActive(record, query) -> Active { Entries[], Primary?, Key, AuthKey }
    Entry { Namespace, Id, Title, Role (Identity | ArtworkSource), Authority (User | Automatic),
            ArtworkEligible, IneligibleReason? }

AUTHORITY ORDER (the only meaning of "priority" anywhere in this document):
    Confirmed (User)  >  automatic identity entries ordered by (Tier desc, namespace priority
    [IgdbGame > SteamGridDbGame > ...], ResolvedAt desc).
    An ArtworkSource never has identity authority. A user-confirmed SteamGridDB game therefore outranks an
    automatic IGDB game, even though IGDB ranks above SteamGridDB among AUTOMATIC entries.

 1. IDENTITY ENTRIES.
      a. If Confirmed != null -> Entry(Role=Identity, Authority=User). Never filtered by Rejected, staleness or
         contradiction (a contradiction may be surfaced as a warning only).
      b. Each record.Resolved entry E where ALL hold -> Entry(Role=Identity, Authority=Automatic):
           - E.EvidenceFingerprint == query.Fingerprint            (not stale)
           - E.ResolverVersion >= MinTrustedResolverVersion         (not from a known-defective resolver)
           - (E.Namespace, E.Id) not in record.Rejected             (not rejected)
         (A contradiction found by a later resolution is not a filter here: the resolver REMOVES the entry in
          that commit, so no "disqualified" marker exists to drift from the stored facts.)
      LegacyEvidence is NEVER consulted (I10).
 2. Primary = the Confirmed entry if any, else the highest-authority automatic identity entry, else none.
 3. ELIGIBILITY, always judged RELATIVE TO THE PRIMARY (the authoritative identity). Every identity entry X other
    than Primary is ArtworkEligible=false when EITHER:
      a. SAME NAMESPACE, DIFFERENT ID as Primary — regardless of title (reason SameNamespaceDifferentId). Two
         catalog entries in one namespace are two different products even when their titles are identical
         (edition/duplicate entries). Namespace-and-id, never title, decides within a namespace. Consequently a
         user's confirmation of B in a namespace excludes an automatic A in that same namespace, equal titles or not.
      b. DIFFERENT NAMESPACE, and its collapsed canonical title differs from Primary's, with no explicit
         equivalence evidence (reason Disagreement).
    Primary and evidenced-equivalent entries are eligible. No equivalence is ever inferred. At most ONE automatic
    Resolved entry exists per namespace (the commit enforces it). An ineligible entry stays in Entries (it is
    evidence, and reported) but is excluded by the predicate in 3.3.1 — it is NOT filtered out of the list, so
    it cannot be mistaken for "absent".
 4. ARTWORK-SOURCE ENTRIES: each record.ArtworkSources S is produced only if ALL hold:
      - S.Basis == Confirmed's key                                    (its basis stands)
      - S.ResolverVersion is trusted
      - (S.Namespace, S.Id) not in record.Rejected                    (not rejected — same filter as 1b)
      - NOT (S.Namespace == Confirmed.Namespace and S.Id != Confirmed.Id)   (never a competing id in the confirmed
                                                                              identity's own namespace)
    -> Entry(Role=ArtworkSource, Authority=Automatic, ArtworkEligible=true). Anything else is not produced.
    ArtworkSources never affect Primary, state derivation or Key.
 5. Key     = canonical serialisation of the IDENTITY entries only: {(ns, id, Authority)} plus the Primary marker.
              This is what IdentityRevision tracks and what identity dialogs depend on.
    AuthKey = Key plus every entry's (ns, id, Role, ArtworkEligible), ArtworkSources included.
              This is what artwork publication depends on; it is evaluated live, never cached.
```

`IdFor(ns)` = the entry in namespace `ns` with `ArtworkEligible == true`, or none. An ineligible entry is never
returned. **This**, together with the predicate below, is what "the identity actually active" means throughout.

The four required states are derived from `SelectActive` + facts, never stored:

| State | Derived when |
|-------|--------------|
| **UserConfirmed** | `Confirmed != null` |
| **AutoResolved** | no `Confirmed`; `Primary != null` |
| **Detected** | `Primary == null`; the query carries launcher id(s); no attempt has completed with a definitive outcome |
| **Unresolved(reason)** | `Primary == null`; reason from `LastAttempt`: `NeverAttempted`, `NoMatch`, `Ambiguous`, `Contradicted`, `Unavailable`, `PendingRevalidation` (only legacy evidence exists), `Quarantined` |

A `Resolved` entry that was later rejected or went stale therefore can never make a game "AutoResolved": the
function excludes it before anything is derived (finding 4). An `ArtworkSource` never can, by construction.

### 3.3.1 The artwork-authorization predicate (finding 3; I11)

There is exactly one, evaluated live against `SelectActive` and the live `IdentityQuery`. Pinned (user-selected)
artwork is not subject to it (I1, I3).

```
// ONE shared clause. It is required by every catalog-identified branch below, so a user decision cannot be
// bypassed by taking a branch that forgot to check it (finding 1: rejected artwork source, same-namespace
// competitor, legacy cover after confirmation).
UserDecisionConsistent((ns, id), record) :=
      (ns, id) is NOT in record.Rejected
  AND ( record.Confirmed == null
        OR record.Confirmed.Namespace != ns
        OR record.Confirmed.Id == id )              // any OTHER id in the confirmed identity's namespace is excluded,
                                                    // whatever its title

ArtworkAuthorized(art, active, query, record) :=
  art.DerivedFrom is a CATALOG (ns, id):
        UserDecisionConsistent((ns, id), record)
    AND exists Entry e in active.Entries with e.Namespace == ns and e.Id == id and e.ArtworkEligible
        (Role=Identity: the confirmed identity or an eligible automatic one; Role=ArtworkSource: basis stands)

  art.DerivedFrom is a LAUNCHER (ns, id):                                                       // D7, revised
        (ns, id) is in query.LauncherIds
    AND ( record.Confirmed == null                       // no user-confirmed identity: a verified launcher id may authorize it
          OR record.Confirmed.VerifiedLauncherIds contains (ns, id) )   // else the launcher art must be PROVEN consistent
                                                                        // with the confirmed identity; unproven = not authorized

  art.DerivedFrom == null (migrated legacy artwork):
        record.Confirmed == null                         // continuity ENDS the moment the user confirms an identity (3.4)
    AND a Pending LegacyAssociation L exists with (L.Namespace, L.Id) == art.ProviderGameId    // StaleLookup does not qualify
    AND UserDecisionConsistent((L.Namespace, L.Id), record)
    AND the legacy cache entry passed 9.1's validation (including ArtworkImageValidator)
                                                         // DISPLAY ONLY; never authorizes a fetch or cache reuse
```

On the launcher branch (D7): an app id identifies the *launcher product*; it does not necessarily identify the game
the user picked inside a hub or collection, and missing cross-reference evidence does not prove consistency. So
after a confirmation, launcher art that cannot be proven consistent is **removed** in the confirming transaction
(the removal pass, below): the card shows the confirmed identity's own art if any, else the icon. The user is
**not** required to pin a cover to make an identity correction effective. `VerifiedLauncherIds` is populated only
from provider-supplied cross-reference data at confirmation time (availability: U1, unverified); it is never
assumed, so today it will usually be empty and the safe outcome (no launcher art after a confirmation) applies.

Where it is applied (every place automatic pixels can appear or be reused):

| Path | Rule |
|---|---|
| **Publication** (6.5 step 4) | evaluated on the record *after* the identity half commits |
| **Cache reuse** | before serving a cached image for `(ns, id)`, the predicate must hold for that `(ns, id)`; a cached image for an ineligible or absent identity is not served, however valid its file |
| **Carried-forward pixels** (`ReconcileArtwork` stale branch) | the displayed frame carries its `DerivedFrom`; it is reused only if the predicate holds now |
| **Restart / `Load` display** | persisted automatic artwork is displayed only if the predicate holds |
| **Removal pass** inside Confirm / Reject / Clear / merge | automatic artwork for which it no longer holds is removed in the same transaction (never pinned artwork, I1) |

Consequence for concurrency: because the predicate reads the *whole* `AuthKey` (Primary, every entry's
eligibility, every artwork-source), an eligibility change or an artwork-source change cannot slip past a publish;
and because every such change is an identity-record write, it also advances the generation (6.1), so the older
unit is discarded outright before the predicate is even reached.

### 3.4 Legacy evidence (finding 2)

A migrated automatic match is a **`LegacyAssociation`**: "the old pipeline matched this game to that catalog
entry, and that entry supplied the image currently shown." A valid provider id proves which catalog entry
supplied an image, not that it is the installed game, and the old pipeline is the one that produced the wrong
covers. So a legacy association:

- is stored apart from `Resolved`, so `SelectActive` **cannot** treat it as identity;
- is never sticky, never used for id-based fetch or Reset-by-id, never counted in state derivation;
- permits exactly one thing: **display continuity** of the already-cached image it supplied, so the library does
  not blank on upgrade. That image is displayed only while its association is `Pending` (never `StaleLookup`),
  only while **no identity is user-confirmed**, only if it survives 9.1's validation, and it is flagged unverified
  in the resolution report. **Confirming an identity ends continuity immediately** (an unverified association is
  not independently validated *for the user's choice*): the Confirm transaction removes the continuity artwork and
  every `LegacyEvidence` entry, except that an entry naming the *same* `(namespace, id)` the user confirmed is
  adopted — its validated bytes are re-keyed under the confirmed id and the artwork gains that `DerivedFrom` (the
  user's decision is the corroboration). Rejecting a candidate removes any legacy entry naming it;
- is revalidated (8.1) against the current `IdentityQuery` (detected title and launcher facts, never `CustomName`).

### 3.5 Provenance

Every automatic identity carries tier + fingerprint + resolver version + trigger. Every user decision carries a
timestamp and the exact provider id chosen. **User decisions (Confirmed, Rejected, IdentityConflicts) are always
stored in full and never re-derived**; automatic `Resolved` entries are compact and re-derivable.

---

## 4. Resolution policy (how automatic detection decides)

A pure, unit-testable function `Resolve(query, candidates, record, decisions) → Outcome`.

### 4.1 Precedence within identity only

`UserConfirmed` > candidate-specific `UserRejection` > `IdMapped` > `TitleExactCorroborated` > `TitleExact`. This
ladder is about identity. It has no bearing on which cover is displayed (I3).

### 4.2 ID-based path (preferred when the launcher supplies an id)

If the query carries a launcher id and the provider can map that external id to its own game, the mapping is
authoritative for that namespace and needs no title match. This removes today's dependency on the store title
equalling the catalog title. The auditor notes IGDB documents `external_game_source`, `uid` and a deprecated
`category` for this; I could not fetch the documentation page myself (HTTP 403), so I have **not independently
confirmed** it, and actual catalog coverage per store needs verification (U1).

### 4.3 Title path (fallback; today's behaviour, tightened)

Accept a candidate only if **all** hold:
1. exact collapsed-title match (existing `IsConfidentMatch`), and unique among exact matches;
2. not contradicted by hard evidence (4.4);
3. not in `Rejected`.

A unique exact-title hit that fails 2 or 3 is **not accepted**: outcome `Contradicted`, recorded with what
contradicted it. Uniqueness today is computed over a bounded, ranked list (finding 1.11), so it is a *weak*
uniqueness claim. The design strengthens it where the provider allows it: an exhaustive exact-name query
(`where name = "<title>"`) in addition to ranked search, so a second exact match beyond the ranked window is
seen. Whether the provider supports that as assumed is **[UNVERIFIED]** (U5). Until then the existing behaviour
stands and the weakness stays a disclosed limit.

### 4.4 Hard contradictions (few and concrete)

- The candidate is in `Rejected`.
- Launcher-store id mismatch: the launcher says Steam appid N; the candidate's external Steam id (when the
  provider reports one) is M ≠ N.
- Platform mismatch: the candidate's platform list is non-empty and excludes PC while the install is a Windows
  install. Unknown/empty platform data is **not** a contradiction.
- The candidate is a franchise/collection entity rather than a game (4.7).

A unique exact title with no contradiction is still accepted as `TitleExact`, so the five confirmed titles do not
regress. We do **not** start rejecting on `parent_game`/`category` (the Minecraft fixture already pins that).

### 4.5 Non-exact titles (the abbreviation class) — revised for finding 3

Publisher or developer agreement is **not sufficient** to accept a non-exact candidate. Several unrelated games
share every publisher, and a ranked search may simply omit the right one (the FC/UFC error class: both EA
Sports). So:

- **Ranking only.** Publisher/developer/platform agreement may order candidates and may raise an already
  product-linked candidate from `TitleExact` to `TitleExactCorroborated`. It never turns "no product-specific
  link" into an acceptance.
- **Acceptance of a non-exact title needs a product-specific connection**, one of:
  1. a correctly namespaced external-id mapping between a launcher id the game actually carries and the
     candidate (4.2); or
  2. **verified alternative-title evidence**: the provider's own alternative-name data says this exact collapsed
     title is a known name of the candidate. Because search results can omit the right game, this is checked by an
     **exhaustive exact query on the alternative-name entity** (all games having that name), and accepted only if
     it returns exactly one game that passes 4.4. Zero → `NoMatch`; two or more → `Ambiguous`.
- **Otherwise the game stays unresolved.** No similarity score, prefix match or publisher match ever accepts.

Whether IGDB's alternative-name data contains the abbreviations that occur in practice ("Apex") is **[UNVERIFIED]**
(U3). I do not claim the general mechanism resolves Apex. Until measurement shows it does, `CuratedHint` stays and
Apex keeps resolving through it (4.6).

### 4.6 The curated hint, honestly

`CuratedHint` is an explicit, hand-verified, provenance-tagged input. It is not evidence a general rule may
extend. Its removal criterion (section 13) is a resolution report on the owner's library showing 4.2 or 4.5
resolving the same titles.

### 4.7 Ambiguous launcher hubs

An entry whose name is a series/hub rather than a game (`Call of Duty`, a launcher shell) must yield
`Unresolved(Ambiguous)`, never a guess. General signal: the name exact-matches a provider collection/franchise
entity and does not uniquely match one game (U4, **[UNVERIFIED]**). The existing `IsAmbiguousUmbrellaProduct`
list stays as a seed. `Ambiguous` blocks every title-search provider (as today), leaves Steam CDN unaffected (it
never guesses by name), and is the primary reason the picker exists.

### 4.8 Stickiness, retries, breakers

- A **fresh** `Resolved` entry (current fingerprint, trusted resolver version) is sticky: replaced only by a
  fingerprint change (it becomes stale and is excluded by 3.3), a higher tier, or a contradiction (it is
  removed in that commit). Legacy associations are never sticky (I10), so this rule cannot preserve an old mistake.
- `MinTrustedResolverVersion` lets a discovered resolver defect retire every entry it produced without a data
  migration.
- `Unresolved` is retried when the fingerprint changes; a provider is newly configured; `ResolverVersion` bumps;
  after a cooldown for `NoMatch`/`Ambiguous`; every scan for `Unavailable`, subject to the breaker; and on an
  explicit Reset (which bypasses the cooldown, never the rejection/contradiction filters).
- Per-provider **circuit breaker per scan**: after N consecutive `Unavailable` outcomes the provider is skipped for
  the rest of that scan and the skip is recorded (the disclosed "one failing token request per game per scan"
  gap).
- **Outage never downgrades (I5).** `Unavailable` from a higher-priority provider keeps the prior fresh identity
  and its artwork; a lower-priority provider's result is used only when nothing prior exists.

---

## 5. Persisted schema (additive) and tolerant loading

### 5.1 Shape

```jsonc
"Overrides": { "<gameId>": {
   "CustomName": ..., "Hidden": ..., "Favorite": ..., "DateAdded": ...,        // unchanged
   "Artwork": { ..., "DerivedFrom": {"Namespace":"IgdbGame","Id":"348220"} },   // Artwork/Revision unchanged; DerivedFrom new
   "ArtworkRevision": 7,
   "Identity": { "Confirmed": {...}|null, "Rejected": [...], "Resolved": [...], "ArtworkSources": [...],
                 "LegacyEvidence": [...], "LastAttempt": {...} },
   "IdentityRevision": 3,                                                       // active identity set (3.3 Key)
   "DecisionRevision": 5                                                        // user decisions (6.1); both: same long + exhaustion + clamp rules
}},
"ArtworkConflicts": [ ... ],       // unchanged
"IdentityConflicts": [ ... ],      // new (8.2)
"QuarantineArchive": [ ... ]       // new (5.2 item 5): raw identity subtrees a user explicitly replaced; written back verbatim
```

**Single file, single save.** Identity and artwork stay in `settings.json` so one atomic `Save` is the commit
point for any transaction touching both (D2, agreed).

`ArtworkSelection.DerivedFrom` names the identity an *automatic* artwork was fetched for: a catalog namespace+id,
or a launcher id for launcher-derived art (Steam CDN, validated against the live query's `LauncherIds`). Pinned
selections keep `ProviderGameId` as **provenance of the image only**, never as identity. A migrated legacy
artwork has `DerivedFrom == null`, which is exactly the marker for the 3.4 continuity case.

### 5.2 Tolerant parsing (finding 6)

I previously claimed unknown enum names would load "as unresolved". That was wrong: .NET 8's
`JsonStringEnumConverter` throws `JsonException` for an unknown name, and because `Load` deserializes the whole
document, one bad identity value would send the app to the backup or to defaults, losing favorites, watched
folders and covers. Corrected design:

1. **No enums in identity data.** `IdentifierNamespace`, `IdentityTier`, `LookupOutcome` are open string-valued
   types (3.1). There is no name→enum step that can fail, and an unknown value is preserved.
2. **Per-record tolerant converters** on `GameOverride.Identity`, the `IdentityConflicts` list and each element
   inside them. A converter reads the element, tries to understand it, and on *any* failure (wrong JSON type,
   missing/extra fields, unparseable date, non-numeric revision) **quarantines the raw element** instead of
   throwing: it is kept as a raw `JsonElement`, written back **verbatim** on every `Save`, never edited, never
   interpreted, never deleted. The game then derives `Unresolved(Quarantined)` and the application performs no
   identity writes for it until the record is understood again.
3. **Unknown properties** are captured by `[JsonExtensionData]` on `AppSettings`, `GameOverride` and the identity
   types and written back, so a *future* field added by a later version survives an intermediate version's save.
   (A v1.18.x binary has no such capture and will drop `Identity` on its next save; that downgrade risk, R3,
   is unchanged and is not solved here.)
4. **`IdentityRevision` and `DecisionRevision`** with a wrong type load as 0 and are clamped exactly like
   `ArtworkRevision` (`SettingsService.cs:90-94`).
5. **A quarantined record is only ever replaced by an explicit user action** ("Clear identity" on a
   `Unresolved(Quarantined)` game). That action moves the raw subtree, untouched, into `QuarantineArchive` (also
   written back verbatim, never deleted) and starts a fresh record; it advances `DecisionRevision`. Nothing
   automatic replaces, edits or drops a quarantined record.
6. **Guarantee, tested:** for each of — unknown namespace string; unknown outcome/tier; `Identity` as a string,
   number, array or `null`; a bad element in `IdentityConflicts`; a bad date — the file loads, favorites,
   watched folders, custom covers and every other game's identity are intact, and the quarantined subtree
   round-trips Load→Save structurally unchanged.

---

## 6. Transactions

### 6.1 What is versioned, and what bumps when (finding 1)

Three persisted revisions plus one session-only generation, per game:

| Counter | Persisted | Advances when |
|---|---|---|
| `ArtworkRevision` | yes | any **user** artwork change, Reset, or a merge that changes artwork — **not** on automatic artwork writes (unchanged rule) |
| `IdentityRevision` | yes | the **active identity set** changes (`SelectActive(...).Key`: identity entries + Primary), **regardless of origin**: a user Confirm/Reject/Clear, a merge, *or an automatic commit*. Artwork-source changes and metadata-only writes do **not** advance it. |
| **`DecisionRevision`** | yes | any **meaningful change to the user's own decisions**, **whether or not the active identity changed**: `Confirmed` set / changed / cleared; any add or remove in `Rejected`; a merge that adopts, unions or records user decisions; a quarantine archive (5.2). **Never advanced by an automatic write**, so a timestamp, `LastAttempt`, tier restamp or artwork-source write cannot invalidate an open dialog. A no-op (re-confirming the same identity, rejecting an already-rejected candidate, clearing when nothing is set) returns `NoChange` and advances nothing. |
| `IdentityGeneration` | **no** (session) | **every write to the game's identity record of any kind** — user, merge or automatic, including metadata-only ones and `ArtworkSources` — which is how a late *older automatic unit* is detected |

Why a separate `DecisionRevision` (finding 2): `IdentityRevision` tracks what is *active*, so a user decision that
does not change the active set (rejecting a candidate that is not currently active) would leave it unchanged.
Counterexample this closes: a Clear-identity dialog opens; another operation rejects a non-active candidate; the
active set and `IdentityRevision` stay put; the old dialog applies Clear and silently removes the newer rejection.
With `DecisionRevision` the old dialog's Apply returns `StaleSelection` and the rejection is preserved. Automatic
units do not check `DecisionRevision` (they cannot write user decisions), and any user decision write advances the
generation, so an in-flight automatic unit is discarded by the existing generation check.

All three persisted counters use `TryGetNextRevision` before mutating; an exhausted `DecisionRevision` rejects the
user operation before it changes anything (`RevisionExhausted`).

The distinction requested in the audit: an identity-**changing** automatic commit advances `IdentityRevision`
(after validation and after `TryGetNextRevision` succeeds); a **metadata-only** refresh leaves the revision alone
but still advances the generation, which is how a late *older* writer is detected without inventing a persisted
counter for every timestamp.

The old rule "automatic commits never bump" is retired for identity, kept for artwork (an automatic artwork write
that bumped `ArtworkRevision` would invalidate the check that just admitted it, and I9's `DerivedFrom` rule now
provides the identity-side protection that bump was not able to).

### 6.2 Commit shape

Every commit is one synchronous UI-thread method (the existing `CommitArtworkChange` pattern generalised):

1. Resolve the game by id, through the session merge-alias map (6.4). Not found → `GameNoLongerExists`.
2. Verify the operation's required captured state (below).
3. Compute next revisions with `TryGetNextRevision` **before** mutating; exhausted → reject that half with the
   existing `RevisionExhausted` behaviour, mutating nothing.
4. Mutate in memory; advance what this operation owns; bump the generation if the identity record was written.
5. `SettingsService.Save`.
6. **User operation:** on success display prepared/frozen pixels; on failure restore *every* touched record and
   counter exactly (`SaveFailed`), display untouched (I4). **Automatic operation:** display immediately, save
   best-effort (any later successful `Save` persists the whole document, so memory and disk converge).

### 6.3 Operation matrix (this table is the spec; section 11 tests it)

| Operation | Origin | Captured state it requires | Advances | Touches identity | Touches artwork |
|---|---|---|---|---|---|
| Change Cover (file / catalog image) | user | `expectedArtworkRevision` from preview open (D3) | Artwork | never | sets pinned |
| Reset cover | user | none | Artwork | **never** | pinned → automatic (record removed) |
| Confirm / change identity | user | **`expectedDecisionRevision` and `expectedIdentityRevision`** from dialog open (6.7) | **Decision** (always, if meaningful) + Identity (iff `Key` changes) + generation | sets `Confirmed` (recording `VerifiedLauncherIds` only if proven); removes `ArtworkSources` whose basis was the old `Confirmed` **or that fail `UserDecisionConsistent`**; removes every `LegacyEvidence` entry except one naming the confirmed id, which is adopted (3.4) | removes automatic artwork for which `ArtworkAuthorized` no longer holds — **including unproven launcher art and legacy continuity**; pinned untouched |
| Reject candidate | user | both expected revisions | **Decision** (always, if meaningful) + Identity (iff `Key` changes) + generation | adds to `Rejected`; **removes the matching `Resolved`, `ArtworkSources` and `LegacyEvidence` entries** (any role, any list); a later re-derivation excludes it | removes automatic artwork for which `ArtworkAuthorized` no longer holds; pinned untouched |
| Clear identity | user | both expected revisions | **Decision** (always, if meaningful) + Identity (iff `Key` changes) + generation | removes `Confirmed`, `Rejected`, and every `ArtworkSources` entry | removes automatic artwork for which `ArtworkAuthorized` no longer holds |
| Automatic resolution unit (6.5) | system | Identity revision, **generation**, fingerprint, Artwork revision | Identity (iff `Key` changes) + generation; **never Decision** | writes `Resolved`/`ArtworkSources`/`LegacyEvidence`/`LastAttempt` | writes automatic artwork only through gate G1 and `ArtworkAuthorized` (6.5) |
| Dedup merge | system | n/a (sync) | Identity/Artwork as 8.2 | field-level | existing rules |

**Identity ops no longer bump `ArtworkRevision`.** In revision 4 they did (only when artwork was automatic) to
reuse the artwork guard. With I9 that indirection is unnecessary and slightly wrong: in-flight automatic artwork
for the old identity is discarded because the generation moved and `ArtworkAuthorized` no longer holds for it — not
because an unrelated counter changed. Identity ops still *remove* the now-unauthorized automatic artwork record in
the same transaction, so a stale image is never displayed against a changed identity.

### 6.4 Stale asynchronous results — full rule set

| Cause | Detection | Effect |
|---|---|---|
| Any identity write to this game since the unit started (user op, merge, another automatic unit, even metadata-only) | generation ≠ captured | **whole unit discarded** (superseded); a needed re-resolution is rescheduled, not applied late |
| Identity active set changed by a user op | IdentityRevision ≠ captured (and generation moved) | identity half and artwork half both discarded |
| User pinned/reset a cover mid-unit | ArtworkRevision ≠ captured | artwork half dropped; a still-valid identity half applies |
| Scanner re-ran with a different detected title | live query fingerprint ≠ unit's (no identity write has happened, so the generation may be unchanged) | identity half `Rejected(fingerprint)` → **gate G1 blocks every artwork publish**, `Set` and `ClearAutomatic` alike, *even when the artwork's `DerivedFrom` id is still active* (e.g. a still-confirmed id). Not excepted for launcher-derived art either. Discarding is always safe; the rescan that changed the title schedules its own unit. |
| Rename / custom-name adoption mid-lookup | not part of either revision, the generation or the fingerprint | **result stays valid** (I7): the name was never an input |
| Game merged away mid-operation | session alias map `loser → winner`, populated from `MergedGameIds` in `ApplyScanResultAsync`, chains resolved | automatic: re-target the winner **only if** the winner's generation/revisions still match a fresh capture, else discard; user dialog: 6.7 |
| Scan cancelled | token | no commit at all |

### 6.5 The automatic resolution unit and its commit (finding 1, in full)

Per game, off the UI thread: snapshot `IdentityQuery`, the prior record, credentials, and
`(IdentityRevision, IdentityGeneration, ArtworkRevision)`. Resolve identity first, then fetch artwork for the
resolved identity by id (no second title search when an id is known). Output: an **identity half** (new record
state incl. legacy outcomes and `LastAttempt`), an **artwork half** (`Set(selection, prepared bitmap)` or
`ClearAutomatic`), all value snapshots.

Commit, on the UI thread:

```
1. game = resolve(gameId via alias map);            if none: drop
2. if live.IdentityGeneration != unit.Generation:   discard whole unit (superseded writer)
3. IDENTITY HALF — produces exactly one identityOutcome. The COMMON currency checks run FIRST, in this order,
   for every record including a quarantined one; only a still-current unit can ever reach NotApplicable:
     (a) unit cancelled, or no longer the owner ..... no commit at all (6.4)          [with step 2's generation check]
     (b) live.IdentityRevision != unit.IdentityRevision ..... Rejected(revision)
     (c) liveFingerprint      != unit.Fingerprint ........... Rejected(fingerprint)
     (d) only now: record is Quarantined .................... NotApplicable   (cannot be validated or written, 5.2)
     (e) otherwise:
       keyBefore = SelectActive(record).Key
       apply the unit's identity state to a scratch copy (incl. legacy corroborated/failed, ArtworkSources)
       keyAfter  = SelectActive(scratch).Key
       if keyAfter != keyBefore: TryGetNextRevision(IdentityRevision) FIRST;
                                 exhausted ........ Rejected(exhausted), change nothing
       if the scratch differs from the record in any way:
                                 commit it, advance IdentityRevision iff Key changed, ++IdentityGeneration
                                 -> Committed
       else                      -> ValidatedNoChange
   Validated := (Committed | ValidatedNoChange).  Nothing else is Validated. Ids matching proves nothing here.
4. ARTWORK HALF. Every publish — Set AND ClearAutomatic — requires, first and explicitly:
     G1. identityOutcome is Validated                                           (the explicit gate, I9)
   then, for Set (all of):
     G2. ArtworkAuthorized(art, SelectActive(record AFTER step 3), live query)  (3.3.1; also I11)
     G3. live.ArtworkRevision == unit.ArtworkRevision
     G4. artwork mode is Automatic (nothing pinned)
   for ClearAutomatic (all of): G1, G3, G4. When it clears artwork derived from a legacy association, that
   association's removal must be part of the step-3 Committed delta.
   THE ONE DEFINED EXCEPTION (narrow, stated so nothing else relies on it):
     a launcher-derived Set (Steam CDN) when identityOutcome == NotApplicable(Quarantined) — which, by the ordering
     in step 3, means the unit is STILL CURRENT: it passed ownership, generation, revision and fingerprint — may
     publish if G2 (its launcher id is in the live query), G3 and G4 hold AND a shallow, read-only inspection of
     the quarantined raw subtree finds no non-null `Confirmed` (if that cannot be determined, the launcher art is
     NOT authorized: an opaque record may hold a user decision, and D7 forbids launcher art that contradicts one).
     It depends on launcher facts that G2 validates, not on catalog identity. Catalog-derived artwork, every
     ClearAutomatic, and every Rejected(...) outcome are NEVER excepted; a Rejected identity half blocks
     launcher-derived art too — a quarantined record with a moved fingerprint is Rejected(fingerprint), not
     NotApplicable, and publishes nothing.
5. Save best-effort; display prepared pixels only for an artwork half that passed 4.
```

`identityOutcome` is a value the algorithm carries; step 4 tests it. It is **not** derived from any id comparison.
Revision 5 computed a boolean and never read it, and asserted that a failed identity half necessarily made
`DerivedFrom` fail. That is false: with a changed fingerprint and a still-confirmed id, the identity half fails
validation while `DerivedFrom` still matches the active id. G1 is what stops it.

Explicit consequences, both directions:

- **Identity succeeds, pinned cover blocks the artwork half:** identity `Committed`, G4 fails, the pinned cover is
  not touched, nothing new is displayed. Knowing which game this is has value even when the display decision was
  taken away.
- **Artwork ready but the identity half did not validate** (revision moved, fingerprint moved, exhausted,
  quarantined, superseded): G1 fails, the bitmap is dropped and **never displayed**, and a `ClearAutomatic` does not
  clear. This holds even if G2 would have passed.
- **The audit's example:** a Reset lookup starts for identity A; a scan then upgrades the active identity to B.
  The scan's commit advances `IdentityRevision` and the generation. The Reset lookup's late commit fails step 2
  (generation moved) and, independently, G1 (`Rejected(revision)`) and G2 (`DerivedFrom = A`, A no longer
  authorized). A's artwork is never displayed.
- **Two automatic units racing on metadata only** (an older one would restamp a tier downward): the generation
  makes the older one lose without any persisted counter.
- **Ineligible identity:** if `DerivedFrom` names an entry with `ArtworkEligible == false`, G2 fails even though the
  id is present in `Entries` (3.3, 3.3.1).

The scan is a long-lived unit per game; it captures the generation map at start (a snapshot dictionary, like the
existing `overridesSnapshot`) and its per-game commit runs this algorithm at publish. `ReconcileArtwork`'s
existing "stale result → carry forward the last known-good display" branch is extended: a result is *current* only
if `(ArtworkRevision, IdentityRevision, IdentityGeneration)` all match, and carried-forward pixels are reused only
if `ArtworkAuthorized` holds for the frame's `DerivedFrom` now (3.3.1).

### 6.6 Reset when there is no identity (finding 4)

Reset is an **artwork** transaction: `Artwork` pinned→removed, `ArtworkRevision` advanced, identity untouched (I2).
What happens next depends only on the *derived* state, and the follow-up is a separate automatic transaction:

- **Active identity exists** (`IdFor(ns)` for the top provider): the follow-up automatic unit fetches by that id
  (zero primary-namespace title-search calls).
- **No active identity** (`Detected`, `Unresolved(*)`, `PendingRevalidation`): the follow-up is the
  **ordinary full resolution** (identity then artwork), flagged `Trigger=Reset` and `ForceReattempt` (bypassing
  the cooldown only). Its identity half commits under exactly the 6.5 rules like any scan's would, and its artwork
  publishes only through G1 and `ArtworkAuthorized`. If it resolves, that is an automatic identity write with its
  own provenance; if not, the state stays unresolved with a fresh `LastAttempt`.
- **`Quarantined` record:** the identity half is `NotApplicable`, so it is neither validated nor written (5.2), and
  **newly resolved catalog artwork is not published** — it cannot bypass the safeguard the identity half exists
  to provide. The only artwork that may publish is a launcher-derived one under 6.5's single defined exception;
  otherwise the card shows the icon. The state stays `Unresolved(Quarantined)` and is surfaced to the user, whose
  explicit "Clear identity" archives the raw subtree and starts a fresh record (5.2 item 5). Revision 5 said
  "only the artwork result applies", which let catalog artwork through; that is withdrawn.
- Reset therefore **never** requires an id and never makes the artwork commit "secretly mutate identity": the
  identity write, when there is one, is a distinct, separately-validated, separately-attributed transaction.

### 6.7 Dialog transactions (design constraint for the later UI; D3 adopted)

A dialog captures `(gameId, IdentityRevision, DecisionRevision, ArtworkRevision)` at open.

- **Identity operations** (Confirm / Reject / Clear) carry **both** `expectedDecisionRevision` and
  `expectedIdentityRevision`; a mismatch on either returns `StaleSelection`. They guard different things:
  `DecisionRevision` protects the user's own decisions from being overwritten by a stale dialog even when the
  active identity did not change (finding 2's Clear-vs-non-active-Reject case); `IdentityRevision` protects the
  *displayed premise* ("current identity: X") — an automatic resolution that lands while the picker is open
  changes it, and the right behaviour is to tell the user ("this game was just identified as X — replace it?").
  Harmless automatic writes (timestamps, `LastAttempt`, tier restamps of an already-active entry, artwork-source
  changes) advance neither and do **not** invalidate a dialog. (A legacy association being corroborated *does*
  add an identity entry, so it changes the `Key` and does advance `IdentityRevision`; that is a real change to the
  premise, not a harmless write.)
- **Change Cover (local file / catalog image)** carries `expectedArtworkRevision` only. It has **no identity-
  revision dependency** (a cover does not depend on identity). If a newer cover/Reset, or a merge (which advances
  the winner's `ArtworkRevision`), landed during the preview, Apply returns `StaleSelection` and the UI
  revalidates ("this cover changed while you were choosing — replace anyway?") rather than silently overwriting.
  Automatic artwork landing mid-preview does not stale it (automatic writes do not advance `ArtworkRevision`).
- A merged-away `gameId` is retargeted through the alias map; the retarget itself is a revalidation trigger.
- Candidate search inside a dialog uses a per-dialog **search-generation token** plus a linked
  `CancellationTokenSource`, so results from a superseded query are discarded and their HTTP work cancelled.

---

## 7. Reset, change, clear, reject: exact semantics

| User action | Identity record | Artwork | Notes |
|---|---|---|---|
| **Reset cover** | untouched | pinned → automatic; follow-up per 6.6 | Reset never clears identity. |
| **Change identity** (confirm B while A confirmed, or while nothing was confirmed) | `Confirmed=B`; `ArtworkSources` whose `Basis` was A, or that fail `UserDecisionConsistent`, removed; B removed from `Rejected`; **`LegacyEvidence` removed, except an entry naming B, which is adopted**; automatic `Resolved` entries are re-judged by 3.3 (eligibility recomputed *relative to B*, so any other id in B's namespace is excluded whatever its title) | pinned kept; automatic artwork for which `ArtworkAuthorized` no longer holds removed (**launcher art unless proven consistent with B; legacy continuity; a same-namespace competitor's cover**), then B's art fetched | Dialog says "your custom cover was kept" when pinned. No pin is needed for the correction to take effect. Previous `Confirmed` retained nowhere except logs (D4). |
| **Reject** a candidate X (active, an artwork source, or a legacy association) | `X` added to `Rejected`; **the `Resolved`, `ArtworkSources` and `LegacyEvidence` entries equal to X are removed in the same transaction**; state re-derives (`SelectActive` cannot return X again, and `ArtworkSources` refuses it) | pinned kept; automatic artwork no longer authorized removed, re-resolution and fallback searches excluding X | Rejecting the *confirmed* identity is not a Reject: it is Clear or Change (`InvalidOperation` if attempted). |
| **Clear identity** | `Confirmed` and `Rejected` removed; **every `ArtworkSources` entry removed** (their basis is gone). **Surviving:** fresh, non-rejected `Resolved` entries that still pass 3.3 (they never depended on the user's decisions; eligibility is recomputed with no confirmed primary). **Not surviving:** anything the user's decisions caused. | pinned kept; automatic artwork re-judged by `ArtworkAuthorized`, unauthorized removed | Cleared rejections mean a previously rejected candidate may be picked again by ordinary automatic rules; that is what "back to automatic" means. |
| **Reset both** | Clear + Reset as one transaction, one `Save` | | Never the default. |

Confirmed identity survives rescans, exe repicks, renames, scanner title changes, provider outages and cache
version bumps. It is provider-scoped (I6): a confirmed IGDB id is never sent to SteamGridDB.

### 7.1 What fallback providers may receive (D5, reconciled with S5)

- A fallback provider (e.g. SteamGridDB when IGDB has no usable art for the active identity) may be given the
  confirmed **canonical title** (text) to *generate candidates*, and applies its own exact-title and uniqueness
  rules, and never accepts a candidate in `Rejected` or another id in the confirmed identity's own namespace
  (3.3.1's `UserDecisionConsistent`). A hit is recorded in **`ArtworkSources`** (a list separate from `Resolved`, 3.1), in that provider's
  namespace, with its `Basis` (the confirmed identity whose title generated it). It is an *artwork-source*
  association: it never proves the two catalogs describe the same product, never counts as identity, never
  contributes to `Key`, state derivation or `IdentityRevision`, never carries a tier, and is produced by
  `SelectActive` only while its `Basis` still equals the current `Confirmed`. Revision 5 stored it in `Resolved`
  and so let it feed the active identity key; that inconsistency is removed.
- An active identity that is only auto-resolved uses the `DetectedTitle` for fallbacks, exactly as today. Such a
  fallback match is the fallback provider's *own* independent resolution against the detected inputs, so it is an
  ordinary `Resolved` identity entry in its namespace and is judged for eligibility relative to the Primary (3.3);
  it is **not** an `ArtworkSource`.
- Where the launcher supplies an id in the fallback provider's own namespace (a Steam appid for a Steam-keyed
  provider), that id is used instead of a title.
- **S5 is restated accordingly:** after Reset with a confirmed identity whose own provider can supply art, there are
  **zero title-search calls to any provider**. If that provider has no usable art, the fallback chain runs and
  *fallback* title searches (canonical title, once) are expected and counted separately. The earlier unconditional
  "zero title-search calls" was too strong.

---

## 8. Migration and merges

### 8.1 Migration from v1.18.x settings (finding 2)

Lazy and idempotent, in memory on `Load`, persisted by the next ordinary `Save`. **No destructive step; no field
is rewritten in place.**

- **User-selected covers:** untouched, byte-for-byte (asset ids, extensions, `SelectedAt`, revision). A user cover
  implies **no** identity and is never used to infer one.
- **Explicit identity choices:** none exist in shipped data (Identify Game never shipped), so there is nothing to
  migrate for them. Stated so it is not mistaken for an omission.
- **Existing automatic artwork with a provider id** (shipped data: SteamGridDB; finding 1.12): becomes a
  **`LegacyAssociation` (`Pending`)** in `LegacyEvidence`, **not** a `Resolved` entry. The artwork record is kept
  with `DerivedFrom = null` (the continuity marker). Nothing is inferred; no network call is made at migration.
  Whether the cached image can be shown meanwhile is decided by the format-specific, validated discovery in 9.1.
  Steam CDN needs no seed (the appid comes from the launcher each scan; its cached `steam-{appId}.jpg` is the
  launcher's own game, not a catalog match, so it is not legacy evidence).
- **Revalidation** runs as part of the ordinary automatic unit (6.5), bounded per scan by a budget so a large
  library cannot burst the 4 req/s limit. It re-runs *that association's namespace's* own resolution against the
  current `IdentityQuery` (DetectedTitle, launcher ids, never `CustomName`) and compares within the namespace:

  | Fresh result in the same namespace | Effect on the legacy association |
  |---|---|
  | same id | **Corroborated:** becomes a fresh `Resolved` entry (`Tier` per the fresh evidence); pixels unchanged; artwork gains `DerivedFrom`. |
  | a different id | **Failed:** association removed; the fresh candidate is used if it passes the ordinary rules; artwork derived from the legacy association is replaced. |
  | `NoMatch` / `Ambiguous` / `Contradicted` | **Failed:** association removed **even with no replacement**; its continuity artwork is cleared, and the display falls to the next provider's fresh result or the icon. |
  | `Unavailable` / provider not configured | **Preserve the existing status.** Continuity remains available only for a previously validated `Pending` association; a `StaleLookup` association stays `StaleLookup` and its artwork stays withheld — an outage must not restore withheld artwork. Retried next scan under the breaker. |

  This is deliberate: a legacy match created from a searched `CustomName`, or from the old un-uniqueness-checked
  SteamGridDB path, can fail revalidation and be removed instead of being carried forward by stickiness.

  A `StaleLookup` association (9.1: its recorded search name no longer matches the current inputs) is revalidated
  by exactly this table like a `Pending` one. The name mismatch itself is **not** a verdict and **never creates a
  rejection** (rejections are user decisions only): it withholds the continuity image and makes fresh resolution
  due. The verdict comes from the fresh result in the table above.
- **`ArtworkConflicts`:** preserved as is. `IdentityConflicts` starts empty.
- **`CustomName`:** preserved, display-only.
- **Version skew:** an *older* app reading a newer file drops `Identity` on its next `Save`. Velopack updates are
  forward-only, so this is an accepted, documented risk (R3). Automatic data is re-derivable; only `Confirmed`/
  `Rejected` would be lost.
- **Honest consequence:** existing games do **not** get a trusted identity at upgrade time. They keep their cover
  (continuity) and gain a verified identity only as revalidation succeeds. Until then their state derives as
  `Unresolved(PendingRevalidation)`.

### 8.2 Dedup merges (extends `MigrateMergedOverrides`; finding 5)

Same discipline as artwork: compute the next revisions first; if exhausted, skip that migration but **still record**
every user decision that would otherwise be dropped. **Namespace never decides.** Two `Confirmed` values are
"the same decision" only if they are the same `(namespace, id)`; different ids in the same namespace **and**
values in different namespaces are both conflicts, unless there is *explicit equivalence evidence* (a provider-
supplied cross-reference proving both catalog entries map to the same product, e.g. the same store id in both
providers' external-id data — availability **[UNVERIFIED]**, U1). Absent that evidence, neither side is dropped.

| Winner | Loser | Result |
|---|---|---|
| none / automatic | `Confirmed` | loser's `Confirmed` adopted (a user decision beats an automatic one); `ArtworkSources` re-derived from the new basis |
| `Confirmed` A | none / automatic | winner kept |
| `Confirmed` A | `Confirmed` A (same ns, same id) | winner kept; no conflict |
| `Confirmed` A (IGDB) | `Confirmed` B, **same namespace, different id** | winner active; loser in `IdentityConflicts` (`SameNamespaceDifferentId`) |
| `Confirmed` A (IGDB) | `Confirmed` B (SteamGridDB) — **different namespace** | winner active; loser in `IdentityConflicts` (`CrossNamespaceUnproven`). **Not** merged into `Confirmed`, not discarded, not treated as equivalent. |
| `Confirmed` A | loser `Rejected` contains A (or winner `Rejected` contains the loser's `Confirmed`) | `IdentityConflicts` (`ConfirmedVsRejected`); winner active; the offending rejection is kept in the conflict record, not in the live `Rejected` list, so `Confirmed ∉ Rejected` always holds |
| any | `Rejected` lists | unioned, except entries equal to the surviving `Confirmed` (→ conflict above) |
| `Resolved` / `ArtworkSources` / `LegacyEvidence` | | dropped and re-derived (a merged install is a new evidence fingerprint); `ArtworkSources` are re-derived only under the surviving `Confirmed`'s basis |
| either side's identity counter exhausted | | identity migration skipped, revisions untouched, but the loser's `Confirmed` and `Rejected` are recorded as `IdentityConflicts` (`MergeRevisionExhausted`) exactly as `MigrateMergedOverrides` already does for artwork |

Conflict records are `{Kind, WinnerGameId, LoserGameId, LoserConfirmed?, LoserRejected[], DetectedAt}`, deduplicated
by (winner, loser, loser decision). `IdentityRevision`, `DecisionRevision` and `ArtworkRevision` each advance
strictly past either side's prior value where that merge changed the corresponding thing. A merge that adopts,
unions or records user decisions advances `DecisionRevision`, so a dialog open across a merge is stale (6.7). As with
`ArtworkConflicts` today, this list is a durable, referenced home rather than a resolution UI; resolution UI is
later work, but **preservation is not deferred (D4)**.

---

## 9. Cache binding

Today automatic art is cached as `{gameId}-v{N}.png` with a sidecar whose `SearchedName` must match the current
search name, which ties a cached image to a *name*.

- **Key by resolved identity:** `CoverArtCache\{Provider}\{providerGameId}-v{N}.png` + sidecar
  `{Id, Title, ImageSha256}` (validated on read as now). `SearchedName` is kept for audit only. An identity change
  changes the key: an old entry can never be served for a new identity and **nothing needs deleting** (I8).
- **Restart / offline:** an active identity plus a cache hit displays with no network and no title search. A cache
  miss refetches by *id*. Offline, identity is retained and the icon shows until a later scan (I5).
- **Cache reuse is authorized, not just valid.** Before any id-keyed entry is served, `ArtworkAuthorized` (3.3.1)
  must hold for that `(namespace, id)`. A perfectly valid cached file for an artwork-ineligible or absent identity
  is not served (e.g. an automatic IGDB game A's cached cover after the user confirms a different SteamGridDB
  game B).
- **Legacy continuity:** see 9.1. In short: a legacy association's image may be displayed **only** while `Pending`
  and only after the validated, provider-specific discovery below succeeds; it is never adopted into the id-keyed
  cache and never used to satisfy an id-based fetch, because the association is unverified (I10). On
  corroboration the validated bytes may be re-keyed under the verified id; on failure the image is cleared.
- **User assets** live only in `ArtworkAssetStore`, referenced by `Artwork.AssetId` and
  `ArtworkConflicts.LoserSelection.AssetId`. Identity records reference no assets and no identity operation deletes
  one. `IdentityConflicts` reference none either. (No asset GC exists today; none is added.)
- `CacheVersion` bumps invalidate automatic pixels only; identity persists, so refetch is by id.
- GC of orphaned *automatic* entries is out of scope for the first batches.

### 9.1 Legacy cache discovery (finding 4)

Revision 5 pointed continuity at `{gameId}-v1.png`. The real SteamGridDB provider writes
`{gameId}-v12.png` (`CacheVersion = 12`, `SteamGridDbCoverArtProvider.cs:81,194`), so that would have missed the
cache it promised to preserve (finding 1.13). Discovery is now a **per-provider registry of exact, supported
formats**, never a pattern, range or glob:

| Provider | Location | File | Sidecar | Supported | Notes |
|---|---|---|---|---|---|
| **SteamGridDB** | `CoverArtCache\` | `{gameId}-v{N}.png` | `<file>.meta.json` = `{Id, Title, SearchedName, ImageSha256}` | **exactly the provider's current `CacheVersion`: `N = 13`** once B1 lands (see the B1 note below) | the only shipped catalog cache format |
| **Steam CDN** | `CoverArtCache\` | `steam-{appId}.jpg` | none | unversioned | **not legacy evidence**: keyed by the launcher's own appid. Continues to display as today (decode-validated); no `LegacyAssociation` is created |
| **IGDB** | `CoverArtCache\Igdb\` | `{gameId}-v1.png` | sidecar | **none** | developer-local build only, never shipped; not migrated (Reset re-fetches it) |

Why exactly one version: `CacheVersion` is bumped *because* earlier matching logic is known to have mis-cached
(comments `:16-80`), so older-version files are the ones most likely to hold wrong covers and are **ignored, not
read**. Each future bump makes an explicit, changelogged decision on whether the previous version's entries are
still trustworthy and edits the registry; nothing is accepted by default.

**B1 note (implemented locally, uncommitted, awaiting audit).** B1 provider parity bumps SteamGridDB's
`CacheVersion` from 12 to **13**, because v12 entries were produced without the uniqueness check and without
original-size validation. That is exactly the decision this rule asks for: **v12, the last shipped version, is
superseded and ignored** (its entries may belong to the wrong one of several same-titled products), and the
registry's supported version becomes 13, whose entries were produced under uniqueness and validation. The
consequence for migration: **do not assume every installation ran a v13-producing release before identity
integration ships.** An installation upgrading straight from v12 has no v13 entry, and if it is offline (or
SteamGridDB is `Unavailable`) it has no automatic SteamGridDB cover for that game until a fresh lookup succeeds —
"no v13 entry" is therefore an ordinary state for such installs, not an anomaly, and the cascade (Steam CDN for
Steam games, else the exe icon) is what the card shows meanwhile. v12 files are left untouched on disk, never
deleted by the provider. "Supported" still means only "displayable while unverified" (I10), never trusted as
identity. Revision 6 and 7 wrote `12` because that was the only shipped version when they were written.

**B1 correction note — what `Unavailable` covers.** A provider response it cannot *read* is `Unavailable`, never a
catalog conclusion. Concretely, for both IGDB and SteamGridDB: a search response that is valid JSON but the wrong
shape, a candidate that is not an object or has no usable (non-blank string) name, a candidate that matches by
name but has no usable positive integer id, and a cover/grid listing whose first entry has no usable image
reference are all `Unavailable`. A candidate is skippable only when its *readable name proves it is a different
title*; a missing name is never replaced by a placeholder. A **valid empty** search stays `NoMatch` and a **valid
empty** listing stays `IdentifiedWithoutUsableArt`. The identity resolver may therefore rely on this: `NoMatch`
and `IdentifiedWithoutUsableArt` are statements about the catalog; an unreadable response is only ever the
outage-shaped `Unavailable` (I5).

**B2 note — transport bounds and what they add to `Unavailable`.** All three providers now share one HTTP
exchange (`ProviderHttp`): each request has its own 8 s deadline and takes the **scan's cancellation**; JSON bodies
are capped at 2 MiB (OAuth token 64 KiB), cache sidecars at 16 KiB and images at 20 MB, all enforced against the
bytes actually read. The rule the resolver may rely on: **the caller's cancellation is the only thing that escapes
as an exception** — it is never a lookup outcome, so it must never be recorded, retried-as-failure or turned into
a write (the unit's ownership/revision checks already discard it). A deadline overrun, a stall, an over-cap or
declared-over-cap **JSON or token** body, and a malformed response are all `Unavailable` (I5) — outage-shaped,
saying nothing about the catalog. An **image** is different, deliberately: an over-cap, oversized-dimension or
undecodable image is a fact about that cover, not about the service, so the game is identified and the result is
`IdentifiedWithoutUsableArt`; only a stall, a non-404 HTTP failure or a deadline overrun while fetching the image
is `Unavailable`. The two must not be described as one outcome. WebP covers decode only where Windows' WebP codec is installed; where it is absent a good WebP is
unusable art for that machine (`IdentifiedWithoutUsableArt`, logged as an unsupported codec rather than a damaged
file) and never a rejection or an identity signal. Validation of provider images also requires the decode to be
faithful to the declared header (a truncated WebP decoded to 1x1 without any error before this check).

A discovered SteamGridDB entry is offered for continuity only if **all** of these pass; otherwise there is no
continuity image (the association stays for revalidation, and the card shows the next provider's result or the
icon):

1. exact filename for the supported version, resolved inside the cache directory (no path traversal);
2. **bounded read, then the shared validator — not `CoverArtDecoder` alone.** The image file is read with a hard
   cap enforced against the bytes actually read (the `ReadBoundedAsync` discipline, `ArtworkImageValidator.cs:
   156-167`, not a `FileInfo.Length` check a lying or changing file could defeat) at
   `ArtworkImageValidator.MaxFileBytes` (20 MB), and the bytes go through
   **`ArtworkImageValidator.ValidateProviderBytes`** (`ValidateBytes` with only the container allowlist relaxed,
   because a provider cache entry is never staged as a user asset and a v13 SteamGridDB entry may hold WebP
   under its `.png` name), which enforces the file-size cap, `MaxDimensionPixels` (8000) and `MaxTotalPixels`
   (64 MP) against the *original* image, **requires the decode to be faithful to the header** (B2: a truncated
   WebP decoded to 1x1 without error), and returns the frozen `DecodedImage`. `CoverArtDecoder.Decode` alone is a **downsampled** decode (`DecodePixelWidth`) with no size or
   dimension checks, so a valid hash plus a successful downsampled decode establishes neither. Routing legacy
   continuity around the validator would recreate, through a migration path, the dimension-validation bypass B1
   exists to close. The frozen bitmap returned by the validator **is** the continuity image (no second decode);
   validation runs off the UI thread in the same PREPARE phase as `DecodePendingCoverRestoresAsync`, once per
   legacy game. (The SteamGridDB provider's own cache-hit path now goes through the same validator — B1 — so the
   two agree on what a displayable cached image is.);
3. the **sidecar is also read with a hard bound** — reuse `ProviderImageIo.ReadBoundedSidecarText` (B2: 16 KiB,
   `ProviderHttp.MaxSidecarBytes`; an over-cap sidecar is unverifiable evidence, exactly like a corrupt one) — then
   must parse tolerantly;
   `Id > 0`, `Title` non-empty, `ImageSha256` non-empty and **equal to the SHA-256 of the bounded image bytes**
   (the same three checks as `TryReadMatchedGame :630-637`);
4. **cross-check with settings:** the sidecar `Id` equals the `ArtworkSelection.ProviderGameId` recorded for that
   game in `settings.json`, so the file is the image the settings record actually describes;
5. **`SearchedName` is stale lookup evidence, not a verdict.** The provider compares it to the *current search
   name* and deletes the entry on mismatch (`:196-225`). Discovery compares it to the current query's search
   title (curated hint if present, else `DetectedTitle`, ordinal, as the provider does). A **mismatch means the
   lookup was made under inputs that are no longer this game's inputs** (a searched `CustomName` is the known
   case). It does **not** prove the catalog candidate is wrong, so: the continuity image is **withheld**, the
   association is marked **`StaleLookup`** (kept, not removed), fresh resolution is scheduled, and **no rejection
   is created** — rejections are user decisions only. The fresh result (8.1's table) is the verdict. A match means
   only "looked up under the current inputs"; the association stays `Pending`, because the old pipeline's
   judgement itself is the unverified part.

Discovery is **read-only**: it never deletes, rewrites or re-keys a legacy file. (The provider's own delete-on-
invalid behaviour belongs to that provider's path and is unchanged.) Only after *corroboration* (or the user
confirming the same id, 3.4) may the validated bytes and sidecar be copied to the id-keyed entry. Steam CDN's
unversioned file is untouched by this discovery.

---

## 10. How this improves automatic detection (existing and new games)

The picker is a backstop for genuinely unresolved cases. The improvements are what make it a backstop.

1. **Renames stop mattering** (1.1 fixed structurally by 2.1). Applies to every existing game once `DetectedTitle`
   ships; no user action.
2. **Existing games get *re-verified*, not blindly promoted** (8.1). Matches produced by the old, less strict
   pipeline are checked against current detected inputs; wrong ones are removed and replaced or fall back
   honestly, instead of being frozen in by "identity". Right ones are confirmed and become id-fetchable.
3. **Identity survives what today's name-keyed cache cannot** (renames, merges, cache-version bumps) and is reused
   for artwork by id.
4. **ID-based resolution** for launcher ids we already capture (Steam appid, GOG id; Epic once catalog ids are
   collected) removes today's reliance on the store title equalling the catalog title, subject to U1.
5. **Unique-exact-title no longer accepted blindly:** contradictions (store-id mismatch, platform mismatch,
   rejection) turn a would-be wrong cover into `Contradicted` → honest fallback → picker, without regressing the
   five confirmed titles.
6. **Outage resilience:** no clearing/downgrading on `Unavailable`; per-scan breaker; recorded retry reasons.
7. **Safer general mechanisms in place of two hand lists** (abbreviations, hubs), each gated on measured evidence
   and each keeping today's list as a seed until measurement justifies removal. Publisher agreement is never
   enough (4.5).
8. **Every game ends a scan in a named state** and a diagnostic **resolution report** lists them. That report is
   how "automatic detection improved" is *measured* on the owner's library instead of asserted.

Not promised: that Apex, A Way Out, FC27, BO7 or Minecraft resolve correctly on the gaming PC (gate G2), or that
abbreviations like "Apex" resolve without `CuratedHint` (U3).

---

## 11. Acceptance scenarios and test plan

### 11.1 Scenarios (each becomes named tests)

| # | Scenario | Expected | Layer |
|---|---|---|---|
| S1 | Rename mid-lookup (UI rename, or merge adopting `CustomName`) | result applies; provider saw only `DetectedTitle`; cache key unchanged; card shows custom name + correct cover | resolver + VM |
| S2 | Detected title changes mid-lookup (rescan) | identity half `Rejected(fingerprint)`; **gate G1 blocks all artwork publishing** (see S34); next unit uses the new title | VM |
| S3 | Identity confirmed while an automatic download is in flight | download discarded (generation + `DerivedFrom`); new fetch for the new id; **zero** commits from the old unit; nothing displayed for the old id | VM |
| S4 | Confirm identity while a custom cover is pinned | pinned asset id/bytes/revision unchanged; identity stored; no pixel change | VM |
| S5 | Reset after identity confirmed, provider can supply art | identity untouched; **zero title-search calls to any provider**; confirmed game's cover shown by id | VM + provider fake |
| S5b | Same, but the confirmed identity's provider has no usable art | fallback chain runs; exactly one canonical-title fallback search; result is an **`ArtworkSources`** entry (with `Basis` = the confirmed identity), not a `Resolved` identity; `Key`, state and `IdentityRevision` unchanged by it | VM + provider fake |
| S6 | Clear identity | `Confirmed`, `Rejected` and **all `ArtworkSources`** gone; fresh independent `Resolved` entries survive; pinned unchanged | VM |
| S7 | Change Cover while an automatic unit runs | artwork half dropped; valid identity half applied | VM |
| S8 | Refresh/merge while a Change Cover preview is open | merged-away id retargets via alias map; `ArtworkRevision` moved → `StaleSelection`; nothing silently overwritten | VM |
| S8b | Identity dialog open while an automatic unit changes the active identity | `IdentityRevision` moved → `StaleSelection`; the UI tells the user what the game was just identified as | VM |
| S9 | Merge: two different confirmed ids, same namespace | winner active; loser in `IdentityConflicts(SameNamespaceDifferentId)` | VM |
| S9b | Merge: winner confirmed IGDB A, loser confirmed SteamGridDB B | winner active; loser kept as `CrossNamespaceUnproven`; never merged into `Confirmed`; never dropped | VM |
| S9c | Merge with `IdentityRevision` exhausted | identity migration skipped, revisions untouched, loser's decisions recorded (`MergeRevisionExhausted`) | VM |
| S9d | Merge: loser confirmed X, winner rejected X | `ConfirmedVsRejected` conflict; `Confirmed ∉ Rejected` holds | VM |
| S10 | Merge: loser confirmed, winner automatic | loser's confirmed adopted; pinned artwork rules unchanged | VM |
| S11 | Save failure on a user op / on an automatic commit | user: every touched record and counter rolled back, display unchanged, `SaveFailed`. automatic: shown, memory ahead, persisted by next successful save | VM |
| S12 | Restart after (a) pinned+confirmed (b) automatic | identity persists; pinned decodes from asset; automatic served from id-keyed cache with **no network**; offline shows cached art, no downgrade | VM + round-trip |
| S13 | Ambiguous hub entry | `Unresolved(Ambiguous)`; no title-search provider called; no cover guessed; confirming a title makes art follow | resolver + VM |
| S14 | Outage matrix: (a) prior fresh identity + IGDB `Unavailable` (b) none + IGDB down + SGDB up (c) IGDB recovers | (a) retained (b) SGDB art, identity `Unresolved(Unavailable)` for IGDB (c) upgraded once, no flip-flop; breaker stops repeat calls within a scan | resolver + coordinator |
| S15 | Unique exact title but launcher store id maps to a different game | rejected → `Contradicted`; fallbacks not seeded from the rejected candidate | resolver |
| S16 | Rejected candidate | never auto-picked again across rescans/restart; its `Resolved`/legacy entries removed at reject time; cleared only by Clear | resolver + VM |
| S17 | Scan cancelled mid-unit | no identity or artwork write of any kind | VM |
| S18 | v1.18.1 settings fixture | pinned covers byte-identical; `ArtworkConflicts` preserved; automatic artwork becomes `LegacyEvidence(Pending)`, **not** `Resolved`; state `Unresolved(PendingRevalidation)`; second load a no-op | migration |
| S19 | `IdentityRevision` exhausted (user op / automatic commit) | rejected **before** any mutation; automatic artwork for the un-committed identity is never displayed | VM |
| S20 | Unknown namespace/outcome/tier, `Identity` of the wrong JSON type, bad `IdentityConflicts` element | file loads; favorites, watched folders, covers and other games' identity intact; quarantined subtree round-trips unchanged | settings |
| **S21** | **Reset lookup for A in flight; scan upgrades active identity to B** (audit example) | scan advances `IdentityRevision`+generation; Reset's late commit fails on generation **and** `DerivedFrom`; A's art never displayed; B's shown | VM |
| **S22** | Identity half succeeds while a newer pinned cover blocks the artwork half | identity recorded; pinned untouched; no new pixels | VM |
| **S23** | Artwork ready, identity half rejected (revision exhausted / fingerprint moved / superseded) | bitmap dropped, never displayed | VM |
| **S24** | Two automatic units race; the older would restamp a tier downward | older loses on generation; tier not downgraded | VM |
| **S25** | Legacy SteamGridDB match that came from a searched `CustomName` / wrong title | revalidation with `DetectedTitle` fails → association removed, continuity art cleared, no replacement required | migration + resolver |
| **S26** | Legacy association corroborated | becomes fresh `Resolved`; pixels unchanged; artwork gains `DerivedFrom` | migration |
| **S27** | Legacy + provider `Unavailable` | stays `Pending`; continuity image stays; never used for id fetch or Reset-by-id; never sticky | migration |
| **S28** | Reject a `Resolved` candidate | entry removed in the same transaction; `SelectActive` cannot return it; state re-derives | VM |
| **S29** | Two providers' entries disagree on canonical title, no equivalence evidence | the entry that disagrees **with the Primary** (authority order: a user-confirmed entry first) gets `ArtworkEligible=false`; `IdFor` never returns it; no cross-game art shown; reported | resolver |
| **S30** | Reset with no active identity (`Unresolved`/`Detected`) | artwork transaction alone changes only artwork; follow-up is an ordinary automatic unit with `Trigger=Reset` and cooldown bypass; identity write (if any) validated by 6.5 | VM |
| **S31** | Non-exact title; one candidate matches only by publisher/developer | **not accepted**; stays unresolved (FC/UFC-class fixture) | resolver |
| **S32** | Non-exact title; exhaustive alternative-name query returns exactly one / zero / two games | accepted (if 4.4 passes) / `NoMatch` / `Ambiguous` | resolver |
| **S33** | **User confirms SteamGridDB game B while a conflicting automatic IGDB game A exists** (audit acceptance test, finding 3). Four variants: (a) A's download is in flight when Confirm commits; (b) A's image is already in the id-keyed cache; (c) A's pixels are the previous frame carried forward across a scan; (d) restart | A is `ArtworkEligible=false` relative to the confirmed Primary B. **A's downloaded or cached pixels are never displayed in any variant**: (a) dropped by generation and G2; (b) not served (cache reuse is authorized); (c) not reused (predicate); (d) not shown at `Load`. B's art (or a canonical-title `ArtworkSource`) displays instead | VM + provider fake |
| **S34** | **Identity validation fails while `DerivedFrom` still matches an existing active id** (audit acceptance test, finding 1): fingerprint moved (rescan changed the detected title) but the same user-confirmed id stays active | test first asserts `ArtworkAuthorized` alone **would** be true (so it is not vacuous); identity half is `Rejected(fingerprint)`; **neither `Set` nor `ClearAutomatic` publishes**; displayed pixels and `Artwork` unchanged. Same test for `Rejected(exhausted)` and for a catalog-derived `Set` on a `NotApplicable` record | VM |
| **S35** | Launcher-derived (Steam CDN) artwork with identity outcome `NotApplicable(Quarantined)` vs `Rejected(...)` | publishes only in the `NotApplicable` case, only if the unit is still current, its launcher id is in the live query, and the quarantined raw shows no non-null `Confirmed`; never for `Rejected`; a catalog-derived `Set` and every `ClearAutomatic` never publish on a quarantined record | VM |
| **S36** | Reset on a **quarantined** game (finding 1, 6.6) | artwork transaction changes only artwork; raw identity subtree byte-identical after Load→Save; no newly resolved catalog artwork is published; explicit "Clear identity" moves the raw subtree to `QuarantineArchive` verbatim and advances `DecisionRevision` | VM + settings |
| **S37** | **Clear-identity dialog open; another operation rejects a non-active candidate** (finding 2 counterexample) | active set and `IdentityRevision` unchanged, `DecisionRevision` advanced → the old dialog's Apply returns `StaleSelection`; the newer rejection is **preserved** | VM |
| **S38** | Harmless automatic writes while a dialog is open: `LastAttempt`, tier restamp of an active entry, `ArtworkSources` add/remove, timestamp | neither `DecisionRevision` nor `IdentityRevision` advances; the dialog is **not** invalidated | VM |
| **S39** | No-op user operations: re-confirm the same identity; reject an already-rejected candidate; clear when nothing is set | `NoChange`; no counter advances; no `Save` required | VM |
| **S40** | Reject/Clear/Confirm that changes the active set vs one that does not | `DecisionRevision` advances in both; `IdentityRevision` advances only when `Key` changes; `DecisionRevision` exhausted → user op rejected before any mutation | VM |
| **S41** | **Legacy cache discovery** (finding 4), SteamGridDB: (a) valid `{gameId}-v13.png` (the current version, per the 9.1 B1 note) + matching sidecar + sidecar `Id` = settings `ProviderGameId`; (b) `-v12` (the superseded shipped version), `-v11`, `-v14`, `-v1`, or an unversioned name present instead; (c) sidecar missing / unparseable / `ImageSha256` mismatch / `Id` ≤ 0 / `Id` ≠ settings id; (d) `SearchedName` ≠ current query search title | (a) continuity image displayed (validated frozen bitmap), association `Pending`; (b) **ignored**, no continuity; (c) no continuity, association `Pending`; (d) continuity **withheld with no network call**, association kept as **`StaleLookup`**, fresh resolution scheduled, **no rejection created** (S50). In every case **no legacy file is deleted, rewritten or re-keyed** | migration + fs fixture |
| **S42** | Steam CDN `steam-{appId}.jpg` and the developer-local IGDB `Igdb\{id}-v1.png` present | Steam CDN file creates **no** `LegacyAssociation` and continues to display as today; the IGDB local cache is not migrated | migration |
| **S43** | Cache reuse for an ineligible or unauthorized identity | a valid, hash-correct cache file for `(ns, id)` is **not served** when `ArtworkAuthorized` is false for that pair (e.g. after Confirm/Reject); served again if authorization is restored | resolver + fs fixture |
| **S44** | **Rejected artwork source** (finding 1a): user confirmed IGDB A; SteamGridDB fallback matched C by A's title and C's cover is displayed/cached as an `ArtworkSources` entry; the user then rejects C | C is added to `Rejected`; the `ArtworkSources` entry is **removed in the same transaction**; C's cover is removed. Variants, each asserting C's pixels **never** display: (a) a C download in flight when Reject commits (dropped by generation + predicate); (b) C's image in the id-keyed cache (not served); (c) C's frame carried forward across a scan (not reused); (d) **restart** with a hand-preserved `ArtworkSources` entry for C still present in the file (`SelectActive` refuses it by the `Rejected` filter, so nothing is displayed); (e) fallback re-derivation refuses C | VM + fs fixture + settings round-trip |
| **S45** | **Same title, different id, same namespace** (finding 1b): automatic IGDB A and user-confirmed IGDB B have identical titles | A is `ArtworkEligible=false` (`SameNamespaceDifferentId`) although titles agree; `UserDecisionConsistent` also refuses it. Variants, none may show A's pixels: (a) A's download in flight at Confirm; (b) A's cached image; (c) A's carried-forward frame; (d) restart. An `ArtworkSource` naming a different id in B's namespace is not produced | resolver + VM + fs fixture |
| **S46** | **Legacy cover after confirmation** (finding 1c): a `Pending` legacy association for A with a valid cached continuity image; the user confirms B ≠ A | continuity ends **in the Confirm transaction**: the continuity artwork is removed and A's `LegacyEvidence` is removed; nothing pins. Variants: (a) a revalidation unit for A in flight (dropped: generation + predicate); (b) the legacy cache file still valid on disk (not served: legacy branch requires `Confirmed == null`); (c) carried-forward (not reused); (d) **restart** with a `LegacyEvidence` entry for A still present in a hand-preserved file (not authorized while `Confirmed` exists). If B **equals** A's id, the entry is *adopted* instead: bytes re-keyed under B, artwork gains `DerivedFrom = B` | VM + fs fixture + settings round-trip |
| **S47** | **Quarantined record with a moved fingerprint** (finding 2) | outcome is `Rejected(fingerprint)`, **not** `NotApplicable`; launcher-derived art does **not** publish; same test with the revision moved. Then a still-current quarantined unit with a launcher id in the query and no non-null `Confirmed` in the raw does publish (the S35 case) | VM |
| **S48** | **Legacy continuity through the shared validator** (finding 3): valid hash and a decodable image, but (a) 9000×100 px (width over `MaxDimensionPixels`); (b) 100×9000 px (height over it) — note the 8000 px per-side cap already bounds the total at exactly `MaxTotalPixels`, so the total-pixel check cannot fire independently and is not given its own case; (c) file larger than 20 MB, including a file whose reported `Length` is small but which streams more; (d) truncated/corrupt body; (e) oversized sidecar | none is offered as continuity; nothing is decoded past the bounds (bounded read); the association stays for revalidation; no file deleted. A valid in-bounds image is displayed from the validator's frozen bitmap with no second decode | fs fixture |
| **S49** | **Launcher art after confirmation** (D7): Steam game, launcher art displayed; the user confirms catalog identity B | (a) `VerifiedLauncherIds` empty/unproven → launcher art **removed in the Confirm transaction**; B's art fetched, else the icon; **no pin needed**; (b) `VerifiedLauncherIds` contains that appid → launcher art stays authorized; (c) no confirmed identity → verified launcher id authorizes it (unchanged); (d) variants for (a): in-flight Steam CDN fetch dropped; cached `steam-{appid}.jpg` not reused for display; carried-forward not reused; restart shows B's art or icon | VM + fs fixture |
| **S50** | **Search-name mismatch is stale evidence** (R8) | continuity withheld; association `StaleLookup`; fresh resolution due; **`Rejected` unchanged** (no rejection created); if the fresh result names the same id, the association is corroborated normally; if the provider is unavailable it stays `StaleLookup` | migration + resolver |

### 11.2 Invariant matrix

One parametrised test: every **operation** in 6.3 × every **starting state** (Pinned | Automatic | None) ×
(Confirmed | AutoResolved | Legacy | Unresolved | Quarantined) asserts the declared post-state and that I1, I2, I8,
I9, I10 and I11 hold (pinned asset id and bytes unchanged by identity ops; identity record unchanged by artwork
ops; no asset file removed; automatic artwork displayed only when `ArtworkAuthorized` holds; every artwork publish
preceded by a validated identity half except the single defined exception; legacy never in `SelectActive`;
`ArtworkSources` never in `Key`). The table *is* the oracle.

### 11.3 Mutation targets (each must turn a named test red, then be reverted)

Automatic commit that changes the active set does not advance `IdentityRevision`; generation check removed;
**gate G1 removed, or replaced by the `DerivedFrom`-id check alone (S34 must fail)**; `ClearAutomatic` allowed
without G1; the launcher-derived exception widened to catalog artwork, to `Rejected`, or to `ClearAutomatic`;
`IdFor` returning an ineligible entry (S33 must fail); eligibility judged against the lower-authority entry instead
of the Primary (a confirmed SteamGridDB B losing to an automatic IGDB A); cache served without `ArtworkAuthorized`;
carried-forward pixels reused without the predicate; an `ArtworkSource` stored in `Resolved` or counted in `Key`;
`DecisionRevision` advanced only when the active set changes (S37 must fail); an automatic write advancing
`DecisionRevision` (S38 must fail); identity dialog checking only one of the two revisions; identity op rewrites
pinned artwork; Reset clears identity or requires an id; `SelectActive` reads `Resolved` without filtering
rejected/stale/version; legacy consulted by `SelectActive`; Reject leaves the `Resolved` entry; Clear keeps
`ArtworkSources`; failed legacy revalidation kept "because no replacement"; **legacy discovery accepting `-v11`/
`-v13`/any glob, skipping the hash check, or skipping the sidecar-`Id`-vs-settings cross-check (S41 must fail);
legacy discovery deleting or rewriting a file**; `SearchedName` mismatch treated as a verdict (association removed, or a rejection created) instead of `StaleLookup`; **`UserDecisionConsistent` dropped from any single branch of the predicate (S44/S45/S46 must each fail); rejected `ArtworkSources` entry still authorized; a same-namespace different-id entry judged by title instead of id; legacy continuity surviving a confirmation; Reject removing only `Resolved`/`LegacyEvidence` and not `ArtworkSources`; launcher art exempt from a confirmed identity (S49 must fail); `NotApplicable` decided before the common revision/fingerprint checks (S47 must fail); the launcher-art exception ignoring the quarantined raw `Confirmed`; legacy discovery using `CoverArtDecoder` alone or an unbounded read (S48 must fail)**; publisher-only agreement
accepted; cross-namespace merge conflict dropped; exhausted merge drops the loser's decision; unknown
enum/wrong-type identity throws or discards the whole document; quarantined record not written back verbatim, or
replaced automatically; `Unavailable` treated as `NoMatch`; a provider reading a display-name member; cache keyed
by name again.

### 11.4 Discipline carried over

Per-test temp dirs and isolated stores only (no static overrides), the shared static-state xUnit collection for
provider statics, real AppData verified untouched, real fixtures preserved.

---

## 12. Picker scope (later, for boundary only)

Serves `Unresolved(NoMatch|Ambiguous|Contradicted|PendingRevalidation)` and explicit correction of `AutoResolved`,
entered from a badge on those cards and a context menu. "Identify game" and "Choose cover" are **two commands**
composing two independent transactions (6.3). Picking an *image* records provenance only and does not confirm
identity. Search-generation tokens and `StaleSelection` (6.7) apply. Success metric: the picker is needed only for
cases the resolution report lists as `Unresolved`.

---

## 13. Sequencing and gates

Design review → **B1 provider parity** (before any picker UI, as instructed) → **B2** identity data model +
`DetectedTitle` + `IdentityQuery` + tolerant loading + `SelectActive` + migration to legacy evidence + the
transaction generalisation (revisions, generation, `DerivedFrom`), with behaviour change limited to the
rename-leak fix → **B3** resolver policy (ID path, alternative-title path, contradictions, stickiness, breaker,
id-keyed cache, legacy revalidation) + the 2.3 evidence diagnostic and resolution report → **B4** picker UI.

Explicit later gates (nothing above starts them):
- **G1 credential onboarding** (Settings UI/import path for IGDB credentials; private/per-installation until a
  shared-service deployment is separately approved).
- **G2 gaming-PC validation** of Apex, A Way Out, FC27, COD BO7, Minecraft: full-size correct art, retained
  after restart. Cannot be done from the development machine.
- **G3 staged rollout** for the Tier-3 pullback.
- Removal of `CuratedHint`/umbrella seeds only after the resolution report on the owner's library shows the
  general mechanism covers them.

B1's shape: SteamGridDB gains IGDB's uniqueness check and routes its images through the shared validator (closing
the pre-existing dimension-validation gap), and both providers are exercised by one shared conformance suite.
B1 is separable and does not depend on any of the above.

---

## 14. Risks, unverified assumptions, decisions

**Unverified assumptions (each needs evidence before it is relied on)**
- **U1.** IGDB external-id mapping: the auditor reports public docs describing `external_game_source`, `uid` and a
  deprecated `category`; **I could not fetch that page (HTTP 403) and have not confirmed it.** Which stores are
  covered, and how completely, is unknown. Only a read-only probe answers it, which needs the owner's approval.
- **U2.** Executable version info / uninstall `InstallLocation` / Epic catalog ids being present and useful on the
  owner's library. Measured by the 2.3 diagnostic, not assumed.
- **U3.** IGDB alternative-name data containing the abbreviations that occur in practice ("Apex").
- **U4.** Collection/franchise matching as a reliable "hub, not a game" signal.
- **U5.** An exhaustive exact-name (`where name = ...`) and exact alternative-name query being supported and cheap
  enough as assumed in 4.3/4.5.
- **U6.** API cost: id mapping + cover fetch per game against the 4 req/s limit; the per-scan revalidation budget
  (8.1) exists because of this.

**Risks**
- R1. `settings.json` growth: bounded by compact, re-derivable automatic entries (a few hundred bytes/game).
- R2. Identity coupled to the settings file (deliberate; D2 agreed).
- R3. Downgrade to a v1.18.x binary drops identity data.
- R4. `IdentityConflicts` are preserved but have no resolution UI yet (same status as `ArtworkConflicts`).
- R5. The hand lists remain until measurement says otherwise; Apex-class titles rely on `CuratedHint` and I do not
  claim otherwise.
- R6. Legacy revalidation can *remove* a cover that was right but is not currently re-derivable (a title the
  provider no longer matches). That is the intended trade (wrong-but-trusted is worse), and the pinned-cover and
  picker paths are the recourse. It is worth calling out to the owner before it ships.
- R7. Exact-title uniqueness over a ranked list is weak (1.11) until U5 is confirmed.
- R8. *(Reframed in revision 7 per audit.)* A `SearchedName` mismatch (9.1) is **stale lookup evidence**, not a
  verdict: it withholds the continuity image and schedules fresh resolution, keeps the association as
  `StaleLookup`, and creates no rejection. Residual risk: if the current query's normalisation of the *same* title
  ever drifts from the old provider's, covers that were right lose continuity until fresh resolution runs (which
  needs the provider to be available); recovery is the ordinary automatic path or a pinned cover. The design
  compares exactly as the provider did (ordinal, hint-else-detected) to keep that drift to zero today.
- R9. *(Reversed in revision 7 per audit ruling on D7.)* After a user confirms a catalog identity, launcher-derived
  artwork is **not** exempt: it stays only if provider cross-reference data proved consistency
  (`VerifiedLauncherIds`, U1). Because that evidence is unverified and usually absent today, a Steam game whose
  user confirms a catalog identity will typically lose its Steam CDN cover in favour of the confirmed identity's
  own art, or the icon if that has none. That is the intended safe outcome; the owner should be told before the
  picker ships.

**Decisions**
- D1. *Auditor:* two revisions appropriate, revise automatic-commit rules. Done in revision 5 (6.1–6.5) and
  **tightened in revision 6** (explicit G1, `DecisionRevision`, artwork authorization). Please re-audit.
- D2. Identity in `settings.json`. **Agreed.**
- D3. Local-file picks have no identity-revision dependency; a newer cover/Reset or merge during preview triggers
  revalidation. **Adopted (6.7).**
- D4. Undo history deferred; **merge-conflict preservation is not deferred (8.2).**
- D5. Canonical titles generate fallback *candidates*, never prove equivalence; reconciled with S5 (7.1). **Adopted.**
- D6. Credential use for the U1 probe is the **owner's** decision and is **not** approved by anything in this
  document. Open until the owner says otherwise.
- D7. **Ruled by the auditor; adopted (3.3.1).** With no confirmed identity a verified launcher id may authorize
  launcher artwork. Once the user explicitly confirms a catalog identity, launcher artwork must be consistent with
  that choice, not exempt from it: an app id identifies the launcher product, not necessarily the game selected
  inside a hub or collection, and missing cross-reference evidence does not prove consistency. If equivalence
  cannot be established, keep an already-authorized cover or show the icon; the user is **not** made to pin a
  cover to make the correction effective. My earlier proposal (launcher art stays authorized) is withdrawn.

---

## 15. Implementation notes (batch C: the design implemented end to end)

Written after implementation, for the consolidated audit. Nothing here changes a ruling above; it records where each
part lives, where the implementation is narrower than the design, and what is still unproven. **All of it is local and
uncommitted.** Synthetic tests are evidence about the logic, not proof that any real game is now correct.

### 15.1 Where things live

| Design | Implementation |
|---|---|
| 3.1-3.2 model, open string types, `DetectedTitle` | `Models/IdentityTypes.cs`, `Models/GameOverride.cs`, `Models/AppSettings.cs`, `Models/GameEntry.cs` |
| 3.3 `SelectActive`, `UserDecisionConsistent` | `Services/Identity/IdentitySelection.cs` |
| 3.3.1 the one authorization predicate (I11) | `Services/Identity/ArtworkAuthorization.cs` |
| 4 resolution policy, cooldown, breaker, budget | `Services/Identity/AutomaticResolver.cs`, `CatalogProviders.cs`, `CatalogTypes.cs` |
| Provider identity halves (title path, id path, id-keyed covers, picker) | `Services/CoverArt/IgdbCoverArtProvider.Identity.cs`, `SteamGridDbCoverArtProvider.Identity.cs` |
| 5 schema and tolerant loading, quarantine | `Serialization/TolerantIdentityConverters.cs`, `Services/SettingsService.cs` |
| 6.5 the automatic unit and its commit | `GameScannerService.ResolveGameUnit` (worker half), `LibraryViewModel.Identity.cs` `CommitAutomaticUnit` (UI-thread half) |
| 6.3 / 6.7 user operations and their transactions | `LibraryViewModel.Identity.cs` `ConfirmIdentityAsync`, `RejectIdentityCandidate`, `ClearIdentityAsync`, `CommitIdentityChange`, `ApplyCatalogCoverAsync` |
| 8.1 migration, 9.1 legacy discovery | `Services/Identity/IdentityMigration.cs`, `LegacyCacheDiscovery.cs` |
| 8.2 merges | `LibraryViewModel.Identity.cs` `MigrateMergedIdentity` |
| 9 identity-bound cache | `Services/CoverArt/IdKeyedCoverCache.cs` (`id-{id}-v{N}.png` + sidecar) |
| 12 picker | `IdentifyGameWindow` + `IdentifyGameViewModel`; card menu items and the "?" badge in `Resources/GameCardTemplate.xaml` |

Added beyond the document: an **IGDB Client ID / secret section in Settings** (the user's own Twitch application; the secret
is DPAPI-protected outside `settings.json`; nothing is embedded in the launcher). Without it IGDB-primary cannot work at
all, and the design had left credential onboarding as a later gate. The launcher still ships no IGDB credential.

### 15.2 Where the implementation is narrower than the design

1. **Store-id mapping is unverified and Steam-only.** *(Revised in batch D.)* The id path now follows the schema the auditor
   cites for IGDB: it finds the external-game **source by name** through `external_game_sources` (no numeric source id is
   assumed), then queries `external_games` by `external_game_source` + `uid`, and verifies both echo the request; the
   deprecated `category` field is neither queried nor trusted. I could not fetch the documentation page myself (HTTP 403),
   so this rests on the auditor's citation, and neither the response shapes nor a live authenticated call have been
   observed (U1/U5). GOG and Epic ids are deliberately not mapped. Where the id path is unavailable the title path runs
   unchanged.
2. **4.5 (alternative-name path).** *(Implemented in batch D.)* When the primary-title lookup says `NoMatch`, IGDB's
   `alternative_names` are queried exhaustively (`where name ~ "<title>"`, 50 rows, every row's name re-checked with the
   shared collapsed-title rule). Exactly one game -> accepted at its own tier (`AlternativeName`, level with an exact title
   so namespace priority breaks ties) and then held to the same contradiction/rejection rules; none -> `NoMatch`; several,
   or a response that fills the limit -> `Ambiguous`; anything unreadable that could hide a second game -> `Unavailable`.
   SteamGridDB has no such data and says `NoMatch`. Whether IGDB's alternative names contain the abbreviations that occur
   in practice (U3) is still unmeasured; the curated hint stays.
3. **Legacy bytes are not re-keyed.** On corroboration or adoption the legacy cover is not moved into the id-keyed cache;
   its artwork gains `DerivedFrom` and the pixels are refetched by id when next needed.
4. **`VerifiedLauncherIds` is populated only when the catalog's own cross-reference proves it.** *(Revised in batch D.)* At
   Confirm the chosen catalog is asked which of the game's launcher ids belong to that exact entry; only `Consistent`
   counts. For Steam through IGDB that depends on the unverified `external_games` data (U1), so until it is observed
   working a Steam game's CDN cover is still removed when the user confirms a catalog identity (D7/R9) and the confirmed
   identity's own art (or the icon) replaces it. That is the intended safe outcome, not a defect.
5. **A pinned cover still gets identity resolution (S22) but no cover download.** The resolver records the identity and
   never fetches art for it.
6. `CoverArtService.Apply` and `SafeApplyCoverArt` remain in the tree for the existing tests; a scan no longer calls them.
7. **The UI has never been seen.** Tests load the real XAML, resolve its bindings, drive the real commands through
   automation peers and check the card menu targets the clicked game; they cannot show layout, spacing, theming or real
   mouse behaviour.

### 15.3 Defects the new tests found (and fixed) while writing them

- a null record and an empty record produced different active keys, so the first rejection on a never-resolved game
  advanced `IdentityRevision` for no change in the active identity;
- an ambiguous name found by one provider was still guessed by the next provider;
- `ClearAutomatic` applied in place left the stale pixels on the card;

Assertion mistakes in the tests themselves (not product defects) are not listed.

### 15.4 Test map

`tests/GameLauncher.Tests/Identity/`: `IdentityModelTests`, `IdentitySelectionTests`, `AutomaticResolverTests`,
`LibraryViewModelIdentityCommitTests`, `LibraryViewModelIdentityOperationsTests`, `IdentifyGameViewModelTests`,
`IdentifyGameWindowTests` (real window and card template on the STA dispatcher), `IdentityProviderPartsTests` (parsers,
id-keyed cache, legacy discovery), `IdentityProviderHttpTests` (both providers through the real HttpClient pipeline over fake
transports), `LibraryViewModelIdentityBadgeAndSettingsTests`. The production path is used throughout: a unit is produced by
the same worker method a scan uses and published through `ApplyScanResultAsync`.

Coverage is by behaviour, not one-to-one by scenario tag (S32, the alternative-name outcomes, is covered since batch D). **Scenarios without a dedicated test:** S31 (the resolver has no publisher/developer signal at all, so there is nothing to accept - only the
exact-title path is tested); S43 as a filesystem fixture toggling authorization on a real cache file (the predicate and the
cache read are each tested, and S33b/S45b cover the cache case through a scan); S48's oversize/dimension variants for legacy
discovery (those bounds belong to the shared validator and are tested with it; legacy discovery itself is tested for
hash, id, version, path-escape, unreadable image and sidecar); S14b/c (IGDB down with SteamGridDB up, then recovery) are
covered only in their parts (S14a, breaker, cooldown, outage-clears-nothing).

### 15.5 Mutation results (11.3, extended)

72 single-line mutations of the identity pipeline were applied one at a time - the automatic commit's gates G1-G4, the
generation/revision/fingerprint checks, the user-operation transactions and their rollback, `SelectActive` eligibility and
filters, every branch of the authorization predicate (D7, quarantine, legacy, pinned), the resolver (ambiguity blocking,
cooldown, contradiction, rejection, breaker), the dialog, the card/window markup, both providers' parsers and host checks,
the id-keyed cache and legacy discovery. Each was built, the full suite run, and the file restored (the whole source tree was
re-hashed against a pre-run snapshot afterwards and matched byte for byte).

First pass (71 mutations): **61 killed, 10 survived.** Every survivor was investigated (the 72nd, the SteamGridDB twin of the
cancel-before-cache-write mutation, was added afterwards):

| Surviving mutation | What it was | Disposition |
|---|---|---|
| user change does not advance the generation | **Real test gap.** A rejection that lands while a unit that resolved the same candidate is in flight leaves the active set unchanged; only the generation tells the unit it lost, and without it the unit overwrote the user's rejection. | test added; killed |
| Confirm keeps the candidate in `Rejected` | **Real test gap** (`Confirmed` and `Rejected` overlapping). | test added; killed |
| id mapping `Ambiguous` ignored | **Real test gap:** the title path then guessed one of the mapped games. | test added; killed |
| `IdentityRevision` check at the unit commit | Redundant with the generation (every real revision change also advances it). | pinned with a test that moves the revision alone; killed |
| G3 (artwork revision) | Redundant with the generation and G4 in real paths (any follow-up unit also restamps `LastAttempt`). | pinned with a test that moves the artwork revision alone; killed |
| G2 (predicate at publication) | Redundant with G1 + generation + the removal pass; the difference is only what is *reported* and briefly written. | pinned with a hand-built unit asserting the report; killed |
| `UserDecisionConsistent` in the catalog branch | Redundant with `SelectActive`'s own filters. | pinned with direct predicate tests over an active set that lies; killed |
| cancelled lookup still caches (IGDB, and its SteamGridDB twin) | A window of one line: cancellation arriving as the last byte does. | test with a body that cancels at end-of-stream; both killed |
| carry-forward without the predicate | **Equivalent mutant:** the removal pass re-evaluates the same predicate immediately afterwards in the same synchronous method, so the final state is identical. | left; not testable without a seam that observes a transient |
| quarantine branch: catalog art not refused by namespace | **Equivalent mutant:** `HasLauncherId` is never true for a catalog namespace, so the next clause refuses it anyway. | left |

Final: 70 of 72 killed, 2 equivalent mutants documented. No product defect was found by this pass - the code was right in
every case; the suite was missing tests for three behaviours and the independent guards. A mutation pass proves the tests
notice *these* mutations, not that no other defect exists.

### 15.6 Audit corrections (batch D) and the physical run-through

The auditor's review of the batch above found four material problems; all are fixed, each with the regression that would have
caught it.

1. **An outage replaced a working cover.** With an existing IGDB cover and IGDB unavailable, SteamGridDB or Steam CDN could
   supply a replacement. Design 4.8/I5 already said an outage never downgrades; the resolver now keeps an existing,
   still-authorized automatic cover from a *different* source while a higher-priority source has been unavailable, and uses
   the fallback only when there is no usable prior cover (or the same source refreshes its own cover). Tested at the resolver
   and through the real commit (displayed pixels *and* attribution unchanged, then IGDB recovering).
2. **Closing the dialog was not safe across all its asynchronous work.** Provider work now runs without a cancellation token
   on the scheduling side and observes a shared lifetime token itself (a task cancelled before it starts used to bypass its
   own handler and escape the command), cancellation is handled around every awaited task, busy state is restored in
   `finally`, Apply passes the token to its download and re-checks it before committing (closing during a download saves
   nothing), and a confirmation's follow-up lookup is cancelled quietly. Tested with a scheduler the test controls, so the
   window closes at the exact moment work is queued but has not started.
3. **The cooldown overwrote its own evidence.** A unit that only honoured the cooldown replaced `LastAttempt` with
   `NotConfigured` and a new timestamp, so a second scan lost the explanation and a third searched inside the six hours. A
   unit that asked nobody now leaves the record alone. Tested with three consecutive scans, each fed the previous scan's
   record, and through the real commit (the second and third scans write nothing). Also: every provider skipped by the
   breaker is now reported `Unavailable`, not `NotConfigured`.
4. **Dynamic identity was unfinished.** See 15.2 items 1 and 2. In addition, verified launcher mappings are now connected: at
   Confirm, the chosen catalog is asked (`CheckLauncherConsistency`) which of the game's launcher ids it can prove belong to
   that exact entry, and those are stored as `VerifiedLauncherIds`. Where IGDB proves a Steam app id, Steam CDN art therefore
   stays authorized after the confirmation instead of being dropped (R9 now applies only when nothing proves it).

**Found by the run-through, not by the auditor:** on a game with a confirmed identity *and* a pinned cover, every scan still
sent SteamGridDB a title search for the confirmed game's name, only to record an art source for a cover that is never
shown (a wasted request per pinned game per scan; offline, a visible failed attempt). Such a game now asks no catalog
anything.

**The run-through** (`IdentifyConfirmChooseCoverRestartTests`) drives the real Identify Game window with automation clicks
over the real view model, the real IGDB and SteamGridDB provider classes on fake HTTP transports, the real id-keyed cache
and `settings.json` in a temp directory, across four simulated processes: (P1, online) the automatic scan finds nothing,
the "?" badge appears, the window opens on the detected title, the user picks a candidate and confirms, and the cover is
fetched by id; (P2, offline restart) the identity persisted and the cover comes from the id-keyed cache with **zero**
network attempts; (P3, online) Choose Cover pins a different catalog cover and identity/decision revisions do not move;
(P4, offline restart) the pinned cover shows, an offline scan neither replaces nor clears it, and Reset returns to the cached
identity cover, again with zero network attempts. It is a test of the wiring, not of the appearance, and not the gaming PC.

**Deployment fact:** IGDB becomes primary only when a usable Client ID and secret are configured *on the machine that runs
the launcher*. Adding the Settings fields configures nothing by itself; until the owner enters their own credentials on the
gaming PC, that machine runs SteamGridDB-only exactly as before (and the picker offers only the catalogs that are
configured).

**Mutation follow-up:** 28 further mutations covered these corrections (outage preservation and its exceptions, the
cooldown record, every branch of the alternative-name path, the source-echo/cache/duplicate rules, verified-launcher-id
proof, the confirmed+pinned skip, and the dialog's lifetime). 25 of the first 27 were killed at once; the two survivors
were real test gaps (a cancellation that never actually propagated in my test, and a cover listing that finishes after
close) and were closed; the run-through's finding added the 28th. All 28 are killed.

### 15.7 Second audit corrections (batch E)

The re-audit of 15.6 found two gaps, both real; no design change was needed.

1. **The outage guard protected a record of a cover, not a cover.** `PreservesCurrent` checked that the saved artwork *metadata*
   was still authorized, not that an image existed behind it. After a restart with IGDB metadata recorded, its cache file missing
   or corrupt, and IGDB unavailable, SteamGridDB could have supplied a cover but the rule suppressed it - and with no displayed
   pixels to preserve, the card stayed an icon. A cover now counts as **usable** only if its pixels are on the card
   (`UnitInput.CurrentArtworkDisplayed`: a scan gets a UI-thread snapshot, `LibraryViewModel.SnapshotDisplayedCovers()`, because it
   builds brand-new entries; an in-place Reset/Confirm/Clear follow-up reads the live card) **or** are validated in the source's
   own id-keyed cache (`ICatalogProvider.TryReadCachedCover`: cache only, no network, never a search; Steam CDN through its
   cache-first fetch, asked at most once per unit). Cache-only pixels are put back on the card (published as the *current* selection,
   `RetrievedFrom = LocalCache`) rather than replaced; with neither, the fallback runs. This also closes a neighbouring hole: a
   provider skipped by the per-scan breaker had never had its cache consulted at all.
2. **Cancellation did not reach the final save.** Choose Cover's Apply now hands the dialog's lifetime token to
   `ApplyCatalogCoverAsync` (it had been called without one); `CommitArtworkChange` and `CommitIdentityChange` check the token as the
   first statement of the synchronous commit - there is no await between that check and the mutation/save - and
   `RunAutomaticUnitAsync` checks it immediately after its worker returns, before anything is committed. A cancellation that arrived
   during *any* earlier await (validation, download, asset staging, launcher-id verification, the worker itself) therefore commits
   nothing and surfaces as `OperationCanceledException`, which the dialog already treats as "closed". A staged asset file may stay on
   disk under the accepted deferred-cleanup policy; that is never a reason to save a cancelled selection.

**Regressions:** the restart scenario through the real commit (persisted IGDB metadata, empty cache, IGDB down, SteamGridDB up: the
published card receives the fallback); the breaker-open variants (a valid cache is restored, an unreadable one falls back); Steam CDN
both ways; the in-place path; the existing working-cover preservation tests now say the cover is displayed. For cancellation: the window
closing after the asset is written but before the commit (dialog level and library level), a Confirm cancelled after launcher
verification finishes, a Clear cancelled before its commit, and a worker that finishes after cancellation - each with the same scenario
uncancelled as its control.

**Mutation follow-up:** 20 further mutations (the usability rule and both of its terms, restore/publish, the cache reads of both
providers and both catalog wrappers, where "displayed" comes from, and each cancellation check and hand-off of the token). All 20 were
killed; every file was restored and verified by hash.

**Not covered:** the two lines that carry the displayed-covers snapshot from `RefreshAsync` through `GameScannerService.ScanAllAsync`
into `ResolveGameUnit` cannot be exercised hermetically (`ScanAllAsync` runs every real launcher scanner and has no seam), so they
rest on the type system and review; everything on either side of them is tested. A second, equivalent-by-design guard (the numeric-id check in
`ReadCachedCoverForId`) is defence in depth behind the cache's own file check and was not mutation-tested.

### 15.8 IGDB access without a secret in the launcher: the project's relay

**Decision (owner, 2026-09-21):** users must get IGDB with zero setup, and the Twitch Client Secret must stay off every distributed
copy. An embedded secret (briefly considered the same day) is recoverable from any copy of the exe and cannot be protected, so it was
replaced by a small relay on the owner's existing server: the launcher sends the same `/v4/{endpoint}` apicalypse queries to the relay's
address, and the relay holds the credentials, caches and rate-limits. Scope is a small friends-only launcher, not a service: no signup,
no per-user keys, no new paid resources.

- **Launcher:** `DefaultIgdbRelay` (address embedded from a gitignored one-line `default-igdb-relay.txt`; not a secret), `IgdbAccess.Resolve`
  (a COMPLETE user pair from Settings is used directly; a half-entered pair is ignored; otherwise the relay; otherwise no IGDB, i.e.
  SteamGridDB only), and a relay mode in `IgdbCoverArtProvider` that sends no Client-ID/token and treats a relay 401 as an outage.
  Images still come straight from IGDB's CDN, never through the relay. Everything above the transport is unchanged.
- **Relay:** `docs/deploy/igdb-relay/` (nginx config, token-refresh script, tests). Only the five endpoints the launcher uses, POST only, 2 KB
  body cap, 2-day cache with stale-on-error, per-address and upstream (3 req/s) limits, credentials attached server-side.
- **Not protected, by design of "no keys":** anyone who learns the relay's address can query it within the limits. That protects the
  secret and the shared IGDB quota, not the endpoint's privacy. The address itself is public in every build.
- **Transport:** HTTPS, authenticated by a PINNED KEY (audit finding: plain HTTP would let anyone on the path substitute game identities,
  cross-references and cover ids, and HTTPS image downloads do not authenticate the lookup that chose them). The relay uses a self-signed
  certificate; the build embeds the SHA-256 of its public key (`RelayEndpoint`, `RelayTransport`) and the relay is trusted by that key alone - not by a
  CA or a DNS name - so no domain, Cloudflare or account is involved. A wrong pin, or an impostor with a different key, fails the handshake before any
  request is sent (tested against a real local TLS server; also verified live against the real relay, where the pin computed by OpenSSL on the server
  is accepted by the .NET client). Plain http is never accepted, and the launcher refuses an address without a pin.
- **Releases cannot silently lose it:** `release.yml` fails unless `IGDB_RELAY_URL` and `IGDB_RELAY_PIN` are set and valid, and re-checks the
  configuration actually embedded in the finished package (`installer/Check-RelayConfig.ps1`). Only development builds may omit it.
- **Terms:** Twitch's developer terms on client secrets are not verifiable from the dev machine (the documentation is not fetchable); with
  the secret only on a server, the launcher no longer distributes it, which is the case those terms are about. The owner should still read
  the current terms.
- **Verified:** relay behaviour by tests on the real server's nginx build; the real launcher pipeline through the live relay (Steam-id
  mapping returns `Consistent` for real apps, so the previously unobserved external-game shapes are now observed; alternative-name search
  found `Apex` but not the abbreviations `FC27`/`BO7`/`AWayOut`); and the real built app, isolated, matching 6 of 7 dummy titles with covers
  (the 7th, "Hades", is correctly left ambiguous). **Not verified:** the owner's actual games on the gaming PC, other people's networks.
