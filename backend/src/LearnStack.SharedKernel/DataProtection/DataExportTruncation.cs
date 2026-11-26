using System.Collections.Immutable;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// One collection of an export bundle that stopped at its section cap: how many records the bundle
/// holds and how many there were (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3</see>).
/// </summary>
/// <remarks>
/// The bundle states it under <c>truncated</c> and the artifact's receipt keeps the same statement, so a
/// capped audit section is disclosed both to the person who reads the bundle and to anyone who reads the
/// receipt. It names a collection and two counts, never a value or an identifier.
/// </remarks>
public sealed record DataExportTruncation
{
    /// <exception cref="ArgumentException">The key is not <c>participant.collection</c> or the counts are inconsistent.</exception>
    public DataExportTruncation(string collection, long included, long total)
    {
        if (!DataRequestLimits.IsTruncationKey(collection))
        {
            throw new ArgumentException("A truncation names one section collection.", nameof(collection));
        }

        if (included < 0 || total < 0 || included > total || total > DataRequestLimits.MaximumTruncationCount)
        {
            throw new ArgumentException("A truncation's included records are at most its total.", nameof(included));
        }

        Collection = collection;
        Included = included;
        Total = total;
    }

    /// <summary><c>participant.collection</c>.</summary>
    public string Collection { get; }

    public long Included { get; }

    public long Total { get; }

    /// <summary>
    /// Validates and orders a set of truncations ordinally by collection, refusing a repeated
    /// collection or more than <see cref="DataRequestLimits.MaximumTruncatedCollections"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The set is malformed.</exception>
    public static ImmutableArray<DataExportTruncation> Normalize(IEnumerable<DataExportTruncation>? truncations)
    {
        var items = (truncations ?? []).ToImmutableArray();
        if (items.Any(item => item is null) || items.Length > DataRequestLimits.MaximumTruncatedCollections
            || items.Select(item => item.Collection).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("A bundle states each truncated collection once, within the bound.", nameof(truncations));
        }

        return [.. items.OrderBy(item => item.Collection, StringComparer.Ordinal)];
    }
}
