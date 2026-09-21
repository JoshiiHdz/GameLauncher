using System.IO;
using System.Text.Json;

namespace GameLauncher.Services.CoverArt;

/// <summary>One catalog search candidate that passed IsConfidentMatch, with the raw element kept only so a
/// provider can read its own extra (diagnostic) fields.</summary>
internal readonly record struct ConfidentCandidate(int Id, string Name, JsonElement Element);

/// <summary>How the automatic providers (IGDB, SteamGridDB) read a catalog response - one strict reader, so the
/// two cannot drift on what counts as evidence.
///
/// The rule it exists to enforce: a candidate that cannot be READ never counts as "not a match". A response that
/// is valid JSON but the wrong shape, or that holds a candidate whose fields cannot be trusted, says nothing
/// about the catalog - so it throws InvalidDataException, which each provider's boundary reports as Unavailable
/// (never NoMatch, never a confident match). The counterexample that made this a rule: two exact-title
/// candidates, one with id 111 and one with an unreadable id, used to resolve to 111 - but the unreadable one
/// might be a second product, so uniqueness was never established. Likewise a missing name used to be replaced
/// by the literal "unknown", which is a string the matcher would happily treat as evidence.
///
/// A valid EMPTY result (an empty candidate list, an empty cover listing) is a real, different fact and is NOT
/// malformed: it stays NoMatch / IdentifiedWithoutUsableArt.</summary>
internal static class CatalogResponseReader
{
    /// <summary>Reads every candidate in `candidates` (a JSON array) and returns the DISTINCT ones that pass
    /// IsConfidentMatch(query, name), in response order. A repeated id is one catalog entry (the first wins).
    ///
    /// A candidate is skippable only when its readable name PROVES it is not a match. So this throws when a
    /// candidate is not an object, has no usable name (absent, not a string, or blank), or matches the query by
    /// name but has no usable id (absent, not an integer, or not positive) - in each case it could be a second
    /// product, or the only one, and guessing either way would be evidence the response does not give.</summary>
    internal static List<ConfidentCandidate> ReadConfidentCandidates(JsonElement candidates, string query, string providerLabel)
    {
        if (candidates.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{providerLabel} search response: the candidate list is not an array.");

        var confident = new List<ConfidentCandidate>();
        var index = -1;
        foreach (var candidate in candidates.EnumerateArray())
        {
            index++;
            if (candidate.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{providerLabel} search response: candidate #{index} is not an object.");

            if (!TryReadName(candidate, out var name))
                throw new InvalidDataException($"{providerLabel} search response: candidate #{index} has no usable name.");

            if (!SteamGridDbCoverArtProvider.IsConfidentMatch(query, name))
                continue; // a readable, different title: provably not a match, whatever its other fields hold

            if (!TryReadId(candidate, out var id))
                throw new InvalidDataException($"{providerLabel} search response: candidate #{index} '{name}' matches by name but has no usable id.");

            if (confident.Any(c => c.Id == id))
                continue; // the same catalog entry repeated, not a second product

            confident.Add(new ConfidentCandidate(id, name, candidate));
        }

        return confident;
    }

    /// <summary>A candidate's name: a JSON string that is not empty or whitespace. Anything else is not
    /// evidence and is never substituted with a placeholder.</summary>
    internal static bool TryReadName(JsonElement item, out string name)
    {
        name = string.Empty;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = nameProp.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        name = value;
        return true;
    }

    /// <summary>A candidate's id: a JSON number that is an integer and positive. A non-positive id is not a
    /// catalog id at all.</summary>
    internal static bool TryReadId(JsonElement item, out int id)
    {
        id = 0;
        return item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
            && idProp.TryGetInt32(out id) && id > 0;
    }
}
