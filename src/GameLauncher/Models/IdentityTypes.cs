using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Models;

// Identity data deliberately uses OPEN, string-valued types instead of enums (design 3.1, 5.2): .NET's
// JsonStringEnumConverter throws on an unknown name, and settings.json is deserialized as one document, so one
// unrecognised value would send the whole file to the backup or to defaults and lose favorites, watched folders and
// covers. An open string type has no name->enum step that can fail: an unknown value is a valid, round-trippable
// value that the resolver treats as opaque (preserved and listed, never resolved through).

public interface IOpenString<TSelf> where TSelf : struct, IOpenString<TSelf>
{
    string Value { get; }
    static abstract TSelf Create(string value);
}

/// <summary>Reads any non-blank JSON string; anything else is a JsonException, which the tolerant record converters
/// turn into quarantine of the whole record (never a load failure).</summary>
public sealed class OpenStringConverter<T> : JsonConverter<T> where T : struct, IOpenString<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeof(T).Name} must be a JSON string.");

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException($"{typeof(T).Name} must not be blank.");

        return T.Create(value);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>Where a CATALOG entry (or a launcher product) lives - separate from ArtworkProvider, which says where
/// PIXELS came from. An id is only ever meaningful inside its own namespace (I6).</summary>
[JsonConverter(typeof(OpenStringConverter<IdentifierNamespace>))]
public readonly record struct IdentifierNamespace(string Value) : IOpenString<IdentifierNamespace>
{
    public static IdentifierNamespace Create(string value) => new(value);

    // Catalog namespaces: hold RESOLVED identities and artwork sources.
    public static readonly IdentifierNamespace IgdbGame = new("IgdbGame");
    public static readonly IdentifierNamespace SteamGridDbGame = new("SteamGridDbGame");

    // Launcher namespaces: EVIDENCE the scanner supplies every scan. Never stored as a resolved identity (3.2).
    public static readonly IdentifierNamespace SteamApp = new("SteamApp");
    public static readonly IdentifierNamespace GogProduct = new("GogProduct");
    public static readonly IdentifierNamespace EpicApp = new("EpicApp");
    public static readonly IdentifierNamespace UbisoftGame = new("UbisoftGame");

    public bool IsLauncher => Value is "SteamApp" or "GogProduct" or "EpicApp" or "UbisoftGame";
    public bool IsKnownCatalog => this == IgdbGame || this == SteamGridDbGame;

    /// <summary>Among AUTOMATIC entries only (a user-confirmed entry outranks all of them regardless): lower wins.</summary>
    public int AutomaticPriority => this == IgdbGame ? 0 : this == SteamGridDbGame ? 1 : 2;

    public override string ToString() => Value;
}

[JsonConverter(typeof(OpenStringConverter<IdentityTier>))]
public readonly record struct IdentityTier(string Value) : IOpenString<IdentityTier>
{
    public static IdentityTier Create(string value) => new(value);
    public static readonly IdentityTier TitleExact = new("TitleExact");
    public static readonly IdentityTier TitleExactCorroborated = new("TitleExactCorroborated");
    public static readonly IdentityTier IdMapped = new("IdMapped");

    /// <summary>The provider's own alternative-name data says this exact title is a known name of the game, and exactly one game
    /// has it (design 4.5). It stands level with an exact primary-name match, so a tie between catalogs goes to namespace priority
    /// (IGDB first) rather than to whichever catalog happened to match a name literally.</summary>
    public static readonly IdentityTier AlternativeName = new("AlternativeName");

    /// <summary>Higher is stronger; an unrecognised tier ranks lowest (it can be kept, never preferred).</summary>
    public int Rank => Value switch { "IdMapped" => 4, "TitleExactCorroborated" => 3, "TitleExact" => 2, "AlternativeName" => 2, _ => 0 };

    public override string ToString() => Value;
}

[JsonConverter(typeof(OpenStringConverter<LookupOutcome>))]
public readonly record struct LookupOutcome(string Value) : IOpenString<LookupOutcome>
{
    public static LookupOutcome Create(string value) => new(value);
    public static readonly LookupOutcome Resolved = new("Resolved");
    public static readonly LookupOutcome NoMatch = new("NoMatch");
    public static readonly LookupOutcome Ambiguous = new("Ambiguous");
    public static readonly LookupOutcome Contradicted = new("Contradicted");
    public static readonly LookupOutcome Unavailable = new("Unavailable");
    public static readonly LookupOutcome NotConfigured = new("NotConfigured");

    public override string ToString() => Value;
}

[JsonConverter(typeof(OpenStringConverter<LegacyStatus>))]
public readonly record struct LegacyStatus(string Value) : IOpenString<LegacyStatus>
{
    public static LegacyStatus Create(string value) => new(value);

    /// <summary>Validated continuity: the old pipeline's image may be DISPLAYED while nothing better exists.</summary>
    public static readonly LegacyStatus Pending = new("Pending");

    /// <summary>Its recorded search name no longer matches the current inputs: NOT a verdict on the candidate (R8) -
    /// the continuity image is withheld and fresh resolution is scheduled. Never creates a rejection.</summary>
    public static readonly LegacyStatus StaleLookup = new("StaleLookup");

    public override string ToString() => Value;
}

/// <summary>A (namespace, id) pair - the unit of identity comparison. Never compare ids across namespaces.</summary>
public sealed record IdentityKey(IdentifierNamespace Namespace, string Id)
{
    public override string ToString() => $"{Namespace}:{Id}";
}

public sealed record LauncherIdentifier(IdentifierNamespace Namespace, string Id)
{
    public override string ToString() => $"{Namespace}:{Id}";
}

/// <summary>A catalog identity the USER chose or rejected. Stored in full and never re-derived (3.5).
/// VerifiedLauncherIds: launcher ids the PROVIDER's own cross-reference data proved map to this exact catalog
/// entry (D7). Null/empty means "not proven", never "assumed".</summary>
public sealed record ProviderIdentity
{
    public IdentifierNamespace Namespace { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime At { get; init; }
    public List<LauncherIdentifier>? VerifiedLauncherIds { get; init; }

    [JsonIgnore] public IdentityKey Key => new(Namespace, Id);
}

/// <summary>An AUTOMATIC identity: compact and re-derivable, one per catalog namespace at most.</summary>
public sealed record ResolvedIdentity
{
    public IdentifierNamespace Namespace { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public IdentityTier Tier { get; init; }
    public string EvidenceFingerprint { get; init; } = "";
    public int ResolverVersion { get; init; }
    public DateTime ResolvedAt { get; init; }

    [JsonIgnore] public IdentityKey Key => new(Namespace, Id);
}

/// <summary>A match a fallback provider found by searching the CONFIRMED canonical title (7.1). Not an identity: it
/// never counts toward the active identity key or state and exists only while its Basis still stands.</summary>
public sealed record ArtworkSourceAssociation
{
    public IdentifierNamespace Namespace { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int ResolverVersion { get; init; }
    public DateTime ResolvedAt { get; init; }
    public IdentityKey? Basis { get; init; }

    [JsonIgnore] public IdentityKey Key => new(Namespace, Id);
}

/// <summary>What the pre-identity pipeline matched (3.4). Unverified evidence: it can permit DISPLAY CONTINUITY of the
/// image it supplied and nothing else - never identity, never an id-based fetch, never sticky.</summary>
public sealed record LegacyAssociation
{
    public IdentifierNamespace Namespace { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public ArtworkProvider SourceProvider { get; init; }
    public DateTime MigratedAt { get; init; }
    public LegacyStatus Status { get; init; }

    [JsonIgnore] public IdentityKey Key => new(Namespace, Id);
}

public sealed record ResolutionAttempt
{
    public LookupOutcome Outcome { get; init; }
    public DateTime At { get; init; }
    public string? Fingerprint { get; init; }
    public int ResolverVersion { get; init; }
    public string? Trigger { get; init; }
}

/// <summary>Everything the launcher knows (and the user has decided) about WHICH GAME an entry is - separate from
/// which IMAGE it shows (GameOverride.Artwork). Facts are persisted; state is derived by IdentitySelection.
///
/// A record the app cannot understand is QUARANTINED: kept as a raw JsonElement, written back verbatim on every Save,
/// never edited, interpreted or deleted (5.2). Nothing automatic replaces it; only an explicit user "Clear identity"
/// moves it to AppSettings.QuarantineArchive.</summary>
public sealed class GameIdentityRecord
{
    public ProviderIdentity? Confirmed { get; set; }
    public List<ProviderIdentity> Rejected { get; set; } = new();
    public List<ResolvedIdentity> Resolved { get; set; } = new();
    public List<ArtworkSourceAssociation> ArtworkSources { get; set; } = new();
    public List<LegacyAssociation> LegacyEvidence { get; set; } = new();
    public ResolutionAttempt? LastAttempt { get; set; }

    /// <summary>Set only by the tolerant converter, when the persisted subtree could not be understood.</summary>
    [JsonIgnore] public JsonElement? Quarantined { get; set; }

    /// <summary>Forward compatibility: a field a later version added survives an intermediate version's save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore] public bool IsQuarantined => Quarantined is not null;

    /// <summary>Deep validity: a deserialized record whose entries are missing ids/namespaces is not understood
    /// (a missing field must not silently become an empty id that could match something).</summary>
    public bool IsWellFormed()
    {
        bool Ok(IdentifierNamespace ns, string id) => !string.IsNullOrWhiteSpace(ns.Value) && !string.IsNullOrWhiteSpace(id);

        return (Confirmed is null || Ok(Confirmed.Namespace, Confirmed.Id))
            && Rejected is not null && Rejected.All(r => r is not null && Ok(r.Namespace, r.Id))
            && Resolved is not null && Resolved.All(r => r is not null && Ok(r.Namespace, r.Id))
            && ArtworkSources is not null && ArtworkSources.All(r => r is not null && Ok(r.Namespace, r.Id))
            && LegacyEvidence is not null && LegacyEvidence.All(r => r is not null && Ok(r.Namespace, r.Id));
    }

    /// <summary>A structural copy (the lists are copied; the immutable entries are shared). Used for the scratch
    /// records the automatic commit validates before it writes anything.</summary>
    public GameIdentityRecord Clone() => new()
    {
        Confirmed = Confirmed,
        Rejected = Rejected.ToList(),
        Resolved = Resolved.ToList(),
        ArtworkSources = ArtworkSources.ToList(),
        LegacyEvidence = LegacyEvidence.ToList(),
        LastAttempt = LastAttempt,
        Quarantined = Quarantined,
        Unknown = Unknown is null ? null : new Dictionary<string, JsonElement>(Unknown),
    };
}

public enum IdentityConflictKind
{
    SameNamespaceDifferentId,
    CrossNamespaceUnproven,
    ConfirmedVsRejected,
    MergeRevisionExhausted,
}

/// <summary>A dedup-merge where two user identity decisions could not both stand (8.2). Recorded rather than
/// dropped - preservation is not deferred; a resolution UI is later work, exactly as for ArtworkConflicts.</summary>
public sealed class IdentityConflict
{
    public string Kind { get; set; } = "";
    public string WinnerGameId { get; set; } = "";
    public string LoserGameId { get; set; } = "";
    public ProviderIdentity? LoserConfirmed { get; set; }
    public List<ProviderIdentity> LoserRejected { get; set; } = new();
    public DateTime DetectedAt { get; set; }

    /// <summary>Set only by the tolerant list converter for an element it could not understand.</summary>
    [JsonIgnore] public JsonElement? Raw { get; set; }
}
